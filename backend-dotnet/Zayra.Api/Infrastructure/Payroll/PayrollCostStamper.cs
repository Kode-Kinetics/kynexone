using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// One reason Lock cannot assign a cost centre (AM-1). <see cref="Reason"/> is the stable error code of the refusal
/// (<c>cost_centre_required</c> M-1, <c>cost_centre_invalid</c> M-2, <c>cost_centre_functional_class_reserved</c> M-5).
/// <see cref="Amount"/> is internal: the response carries amounts only as aggregates.
/// </summary>
public sealed record CostBlocker(
    int EmployeeId, string EmployeeCode, string Level, Guid? DepartmentId, Guid? PositionId, Guid? ParentDepartmentId,
    string Reason, string? AccountCode, string? AccountName, decimal Amount);

/// <summary>The cost-centre decisions for one payroll run, computed in ONE pass from ONE consistent read (AM-8).</summary>
public sealed class PayrollCostPlan
{
    public static readonly PayrollCostPlan Off = new() { Enabled = false };

    public bool Enabled { get; init; }
    /// <summary>The final (after small-cell roll-up) cost centre per employee; null id = uncosted.</summary>
    public IReadOnlyDictionary<int, (Guid? CostCenterId, string? Source)> Employees { get; init; } = new Dictionary<int, (Guid?, string?)>();
    public IReadOnlyList<CostBlocker> Blockers { get; init; } = Array.Empty<CostBlocker>();
    /// <summary>W-2: employees costed to the company default, and the earnings they carry.</summary>
    public int FallbackEmployees { get; init; }
    public decimal FallbackAmount { get; init; }
    /// <summary>W-1: employees with a costable line left uncosted (an Optional account), and that amount.</summary>
    public int UncostedEmployees { get; init; }
    public decimal UncostedAmount { get; init; }
    /// <summary>Per-row decisions keyed by row id (earnings and deductions), applied to the entities by <see cref="PayrollCostStamper.Apply"/>.</summary>
    internal IReadOnlyDictionary<Guid, (Guid? CostCenterId, string? Source)> Rows { get; init; } = new Dictionary<Guid, (Guid?, string?)>();
    public IReadOnlyDictionary<int, string> EmployeeCodes { get; init; } = new Dictionary<int, string>();
    /// <summary>Rolled-up-from map for the audit record (employee id to original cost centre), only when the small-cell roll-up changed something.</summary>
    public IReadOnlyDictionary<int, Guid> RolledUpFrom { get; init; } = new Dictionary<int, Guid>();
    /// <summary>Set by the journal builder: M-4 (the ROUNDING account is Required and nothing can cost the difference).</summary>
    public CostBlocker? RoundingBlocker { get; set; }
}

/// <summary>
/// Cost-accounting v1, WP-C2 (contract 2.3, 2.5, AM-8, AM-18): decides the cost centre of every costable payroll row at
/// Lock, writes it onto the rows, and fingerprints everything that changes what posts.
/// <para>Costing OFF returns <see cref="PayrollCostPlan.Off"/> and touches nothing, so every journal is byte-identical.</para>
/// <para>Payroll component amounts are computed upstream and are NOT re-rounded here (AM-18): the stamper only chooses where
/// each stored row lands. A CC line is therefore an exact sum of 2-dp rows and no halala can be created or lost.</para>
/// </summary>
public static class PayrollCostStamper
{
    public const string RequiredCode = "cost_centre_required";
    public const string InvalidCode = "cost_centre_invalid";
    public const string ReservedClassCode = "cost_centre_functional_class_reserved";
    public const string RoundingCode = "cost_centre_rounding_required";
    public const string ChangedAfterApprovalCode = "cost_centre_changed_after_approval";
    public const string ReservedClass = "CAPITALISABLE";

    private sealed record Emp(int Id, string Code, Guid? DepartmentId, Guid? PositionId, Guid? CostCenterId, bool IsOverride);

    // ── account facts ────────────────────────────────────────────────────────

    /// <summary>The account type of a posting driver: the chart row of the code it resolves to, else the built-in catalog.</summary>
    public static string AccountTypeOf(string driverKey, GlResolutionContext ctx)
    {
        var (code, _) = GlAccountResolver.Resolve(driverKey, ctx.Overrides, ctx.DriverDefaults);
        if (ctx.AccountRules.TryGetValue(code, out var a)) return a.AccountType;
        var catalog = PayrollGlCatalog.Drivers.FirstOrDefault(d => d.Key == driverKey);
        if (catalog is not null) return catalog.AccountType;
        var row = ctx.Drivers.FirstOrDefault(d => d.Key == driverKey);
        return row is not null && string.Equals(row.Category, GlDriverCategories.Earning, StringComparison.Ordinal) ? "Expense" : "Liability";
    }

    /// <summary>True when lines on this driver's account are profit-and-loss lines, so they carry a cost centre.</summary>
    public static bool IsCostableDriver(string driverKey, GlResolutionContext ctx) =>
        AccountTypeOf(driverKey, ctx) is "Expense" or "Revenue";

    public static string RuleOf(string code, GlResolutionContext ctx) =>
        ctx.AccountRules.TryGetValue(code, out var a) ? a.Rule : "Optional";

    public static string? ClassOf(Guid? costCentreId, GlResolutionContext ctx) =>
        costCentreId is Guid id && ctx.CostCentres.TryGetValue(id, out var cc) ? cc.FunctionalClass : null;

    // ── planning ─────────────────────────────────────────────────────────────

    public static async Task<PayrollCostPlan> PlanAsync(
        ZayraDbContext db, Guid tenantId, Guid companyId,
        IReadOnlyList<PayrollEarning> earnings, IReadOnlyList<PayrollDeduction> deductions,
        GlResolutionContext ctx, CancellationToken ct)
    {
        if (!ctx.CostAccountingEnabled) return PayrollCostPlan.Off;

        var ids = earnings.Select(e => e.EmployeeId).Concat(deductions.Select(d => d.EmployeeId)).Where(i => i > 0).Distinct().ToList();
        var employees = (await ScopedBypass.NullableTenantWide(db.Employees, tenantId,
                    "Cost resolution reads the run's employees tenant-pinned and read-only, whatever the caller's company claims.")
                .AsNoTracking().Where(e => ids.Contains(e.Id))
                .Select(e => new Emp(e.Id, e.EmployeeCode, e.DepartmentId, e.PositionId, e.CostCenterId, e.CostCenterIsOverride))
                .ToListAsync(ct))
            .ToDictionary(e => e.Id);
        var snapshot = await CostCentreResolver.LoadSnapshotAsync(db, tenantId, companyId, ct);

        // 1. Resolve each distinct employee once.
        var resolved = new Dictionary<int, CostCentreResolution>();
        foreach (var id in ids)
            resolved[id] = employees.TryGetValue(id, out var e)
                ? CostCentreResolver.Resolve(new ResolverEmployee(e.DepartmentId, e.PositionId, e.CostCenterId, e.IsOverride), snapshot)
                : CostCentreResolution.Unresolved("none", "company");

        // 2. Small-cell roll-up (AM-15), before rows are decided so every line, slip and stamp carries the rolled-up centre.
        var final = resolved.ToDictionary(kv => kv.Key, kv => (CostCenterId: kv.Value.CostCenterId, Source: kv.Value.Source));
        var rolledFrom = new Dictionary<int, Guid>();
        if (ctx.CostCentreSmallCellMin > 0)
            RollUp(final, rolledFrom, ctx, snapshot);

        // 3. Decide every costable row.
        var rows = new Dictionary<Guid, (Guid?, string?)>();
        var blockers = new Dictionary<(int Emp, string Reason, string Account), CostBlocker>();
        var uncosted = new Dictionary<int, decimal>();
        var fallback = new Dictionary<int, decimal>();

        void Decide(Guid rowId, int employeeId, string driverKey, decimal amount, bool isEarning)
        {
            if (!IsCostableDriver(driverKey, ctx)) { rows[rowId] = (null, null); return; }

            Guid? ccId; string? source; CostCentreResolution? failure = null;
            if (ctx.FixedCostCentres.TryGetValue(driverKey, out var fixedId))
            {
                if (ctx.CostCentres.TryGetValue(fixedId, out var fixedCc) && fixedCc.IsActive) { ccId = fixedId; source = "MappingFixed"; }
                else { ccId = null; source = null; failure = CostCentreResolution.Unresolved("mapping_cost_centre_invalid", "mapping"); }
            }
            else
            {
                var (cc, src) = final.TryGetValue(employeeId, out var f) ? f : (null, null);
                ccId = cc; source = src;
                if (cc is null) failure = resolved.TryGetValue(employeeId, out var r) ? r : CostCentreResolution.Unresolved("none", "company");
            }

            var account = GlAccountResolver.AccountFor(driverKey, ClassOf(ccId, ctx), ctx);
            var rule = RuleOf(account.Code, ctx);
            if (rule == "NotAllowed") { rows[rowId] = (null, null); return; }

            if (ccId is Guid id && ClassOf(id, ctx) == ReservedClass)
            {
                AddBlocker(employeeId, ReservedClassCode, "costCentre", account.Code, account.Name, amount, ctx.CostCentres[id].Code);
                rows[rowId] = (null, null);
                return;
            }

            if (ccId is not null)
            {
                rows[rowId] = (ccId, source);
                if (source == "CompanyDefault" && isEarning) fallback[employeeId] = fallback.GetValueOrDefault(employeeId) + amount;
                return;
            }

            // unresolved
            rows[rowId] = (null, null);
            if (rule == "Required")
            {
                var invalid = failure!.UnresolvedReason != "none";
                AddBlocker(employeeId, invalid ? InvalidCode : RequiredCode, failure.Level ?? "company", account.Code, account.Name, amount, null);
            }
            else uncosted[employeeId] = uncosted.GetValueOrDefault(employeeId) + amount;
        }

        void AddBlocker(int employeeId, string reason, string level, string accountCode, string accountName, decimal amount, string? detail)
        {
            var key = (employeeId, reason + "|" + level, accountCode);
            var emp = employees.GetValueOrDefault(employeeId);
            // The department chain for the blocker detail (AM-1): the department itself and its parent.
            Guid? parentDept = emp?.DepartmentId is Guid d && snapshot.Departments.TryGetValue(d, out var dept) ? dept.ParentDepartmentId : null;
            blockers[key] = blockers.TryGetValue(key, out var existing)
                ? existing with { Amount = existing.Amount + amount }
                : new CostBlocker(employeeId, emp?.Code ?? string.Empty, level, emp?.DepartmentId, emp?.PositionId, parentDept, reason, accountCode, accountName, amount);
        }

        foreach (var e in earnings)
        {
            var driver = PayrollController.PinnedGlDriver(new[] { e.GlDriverKey })
                         ?? PayrollController.EarningDriverKeyFor(ctx.Drivers, e.ComponentCode, e.Source);
            Decide(e.Id, e.EmployeeId, driver, e.Amount, true);
        }
        foreach (var d in deductions)
        {
            var (driver, employerSide, pairKey) = PayrollController.ResolveDeductionRouting(
                ctx.Drivers, d.ComponentCode, d.Source, PayrollController.PinnedGlDriver(new[] { d.GlDriverKey }));
            // An employer contribution costs the PAIRED EXPENSE; its liability credit is never costed.
            Decide(d.Id, d.EmployeeId, employerSide ? pairKey : driver, d.Amount, false);
        }

        return new PayrollCostPlan
        {
            Enabled = true,
            Employees = final,
            Rows = rows,
            Blockers = blockers.Values.OrderBy(b => b.Reason, StringComparer.Ordinal).ThenBy(b => b.AccountCode, StringComparer.Ordinal)
                .ThenBy(b => b.EmployeeCode, StringComparer.Ordinal).ToList(),
            FallbackEmployees = fallback.Count,
            FallbackAmount = fallback.Values.Sum(),
            UncostedEmployees = uncosted.Count,
            UncostedAmount = uncosted.Values.Sum(),
            EmployeeCodes = employees.ToDictionary(kv => kv.Key, kv => kv.Value.Code),
            RolledUpFrom = rolledFrom,
        };
    }

    /// <summary>
    /// Resolves the cost centre of a set of employees of one company (contract 2.2) for the writers that post outside a payroll run
    /// (bonus accrual, final-settlement accrual): one snapshot, one resolution per employee. The caller applies the account rules.
    /// </summary>
    public static async Task<Dictionary<int, CostCentreResolution>> ResolveEmployeesAsync(
        ZayraDbContext db, Guid tenantId, Guid companyId, IEnumerable<int> employeeIds, CancellationToken ct)
    {
        var ids = employeeIds.Where(i => i > 0).Distinct().ToList();
        var employees = (await ScopedBypass.NullableTenantWide(db.Employees, tenantId,
                    "Cost resolution reads the employees tenant-pinned and read-only, whatever the caller's company claims.")
                .AsNoTracking().Where(e => ids.Contains(e.Id))
                .Select(e => new Emp(e.Id, e.EmployeeCode, e.DepartmentId, e.PositionId, e.CostCenterId, e.CostCenterIsOverride))
                .ToListAsync(ct)).ToDictionary(e => e.Id);
        var snapshot = await CostCentreResolver.LoadSnapshotAsync(db, tenantId, companyId, ct);
        return ids.ToDictionary(id => id, id => employees.TryGetValue(id, out var e)
            ? CostCentreResolver.Resolve(new ResolverEmployee(e.DepartmentId, e.PositionId, e.CostCenterId, e.IsOverride), snapshot)
            : CostCentreResolution.Unresolved("none", "company"));
    }

    /// <summary>Contract 2.8: a cost centre with fewer than K employees is replaced by the nearest ancestor whose subtree holds at least K,
    /// else by the company default; the row's source becomes RolledUp.</summary>
    private static void RollUp(Dictionary<int, (Guid? CostCenterId, string? Source)> final, Dictionary<int, Guid> rolledFrom,
        GlResolutionContext ctx, CostCentreResolverSnapshot snapshot)
    {
        var k = (int)ctx.CostCentreSmallCellMin;
        var counts = final.Values.Where(v => v.CostCenterId != null).GroupBy(v => v.CostCenterId!.Value).ToDictionary(g => g.Key, g => g.Count());

        int Subtree(Guid root)
        {
            var total = 0;
            var stack = new Stack<Guid>();
            stack.Push(root);
            var seen = new HashSet<Guid>();
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (!seen.Add(id)) continue;
                total += counts.GetValueOrDefault(id);
                foreach (var child in ctx.CostCentres.Where(c => c.Value.ParentId == id)) stack.Push(child.Key);
            }
            return total;
        }

        foreach (var (employeeId, value) in final.ToList())
        {
            if (value.CostCenterId is not Guid own || counts.GetValueOrDefault(own) >= k) continue;
            Guid? target = null;
            var visited = new HashSet<Guid> { own };
            var cursor = ctx.CostCentres.TryGetValue(own, out var info) ? info.ParentId : null;
            while (cursor is Guid p && visited.Add(p))
            {
                if (ctx.CostCentres.TryGetValue(p, out var parent) && parent.IsActive && Subtree(p) >= k) { target = p; break; }
                cursor = ctx.CostCentres.TryGetValue(p, out var up) ? up.ParentId : null;
            }
            target ??= ctx.CompanyDefaultCostCentreId is Guid d && snapshot.CostCentres.TryGetValue(d, out var dcc) && dcc.IsActive && !dcc.IsDeleted ? d : null;
            if (target is null) continue;   // nothing to roll into: stays as it was (a Required account still passes with the real centre)
            rolledFrom[employeeId] = own;
            final[employeeId] = (target, "RolledUp");
        }
    }

    /// <summary>Writes the plan's decisions onto the rows (and the employee-level cost centre onto the slips). Costing off: no-op.</summary>
    public static void Apply(PayrollCostPlan plan, IEnumerable<PayrollEarning> earnings, IEnumerable<PayrollDeduction> deductions, IEnumerable<PayrollSlip>? slips = null)
    {
        if (!plan.Enabled) return;
        // These are the PAYROLL STAMP columns on the run's own rows, written at Lock behind payroll.lock and the approval hash; they are
        // not the employee / department / position attribution inputs the organization.costcenter.manage ratchet watches.
        foreach (var earning in earnings)
            if (plan.Rows.TryGetValue(earning.Id, out var s)) { earning.CostCenterId = s.CostCenterId; earning.CostCenterSource = s.Source; }
        foreach (var deduction in deductions)
            if (plan.Rows.TryGetValue(deduction.Id, out var s)) { deduction.CostCenterId = s.CostCenterId; deduction.CostCenterSource = s.Source; }
        if (slips is not null)
            foreach (var slip in slips)
                if (plan.Employees.TryGetValue(slip.EmployeeId, out var s)) slip.CostCenterId = s.CostCenterId;
    }

    // ── refusals (contract 2.7) ──────────────────────────────────────────────

    private static string LevelLabel(string level) => level switch
    {
        "employee" => "Employee", "position" => "Position", "department" => "Department",
        "parentDepartment" => "Parent department", "company" => "Company", "mapping" => "Mapping", "costCentre" => "Cost centre",
        _ => level,
    };

    private static string LevelLabelAr(string level) => level switch
    {
        "employee" => "الموظف", "position" => "المنصب", "department" => "القسم",
        "parentDepartment" => "القسم الأعلى", "company" => "الشركة", "mapping" => "قاعدة الترحيل", _ => level,
    };

    /// <summary>The 422 body (M-1, M-2, M-5): AM-1 blockers (no amounts) plus per-account / per-level aggregate groups.</summary>
    public static (string Error, object Body, IReadOnlyList<object> Groups) BuildRefusal(IReadOnlyList<CostBlocker> blockers)
    {
        var groups = new List<(string Error, string Message, string MessageAr, object Group)>();

        foreach (var g in blockers.Where(b => b.Reason == InvalidCode).GroupBy(b => b.Level).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var employees = g.GroupBy(b => b.EmployeeId).OrderBy(x => x.First().EmployeeCode, StringComparer.Ordinal).ToList();
            var message = $"The cost centre set at the {LevelLabel(g.Key)} level is inactive, deleted or belongs to another company, so cost cannot be assigned. Fix it in Setup, then lock again.";
            var messageAr = $"مركز التكلفة المحدد على مستوى {LevelLabelAr(g.Key)} غير نشط أو محذوف أو يتبع شركة أخرى، لذا لا يمكن توزيع التكلفة. صحّحه من الإعدادات ثم أعد القفل.";
            groups.Add((InvalidCode, message, messageAr, new
            {
                error = InvalidCode, level = LevelLabel(g.Key), employeeCount = employees.Count,
                employees = employees.Take(10).Select(x => new { id = x.Key, code = x.First().EmployeeCode }).ToList(),
                amount = Math.Round(g.Sum(b => b.Amount), 4), message, messageAr,
            }));
        }
        foreach (var g in blockers.Where(b => b.Reason == RequiredCode).GroupBy(b => (b.AccountCode, b.AccountName)).OrderBy(g => g.Key.AccountCode, StringComparer.Ordinal))
        {
            var employees = g.GroupBy(b => b.EmployeeId).OrderBy(x => x.First().EmployeeCode, StringComparer.Ordinal).ToList();
            var message = $"{employees.Count} employee(s) have no cost centre, and account {g.Key.AccountCode} {g.Key.AccountName} requires one. Assign a cost centre to each employee, their position, their department, or set a company default cost centre, then lock again.";
            var messageAr = $"لدى {employees.Count} موظف/موظفين لا يوجد مركز تكلفة، والحساب {g.Key.AccountCode} {g.Key.AccountName} يتطلب مركز تكلفة. عيّن مركز تكلفة للموظف أو لمنصبه أو لقسمه، أو حدّد مركز تكلفة افتراضيًا للشركة، ثم أعد القفل.";
            groups.Add((RequiredCode, message, messageAr, new
            {
                error = RequiredCode, accountCode = g.Key.AccountCode, accountName = g.Key.AccountName, employeeCount = employees.Count,
                employees = employees.Take(10).Select(x => new { id = x.Key, code = x.First().EmployeeCode }).ToList(),
                amount = Math.Round(g.Sum(b => b.Amount), 4), message, messageAr,
            }));
        }
        foreach (var g in blockers.Where(b => b.Reason == ReservedClassCode).GroupBy(b => b.AccountCode).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            const string message = "A cost centre uses a functional class that is reserved and cannot be used for posting yet.";
            const string messageAr = "مركز تكلفة يستخدم تصنيفًا وظيفيًا محجوزًا لا يمكن استخدامه في الترحيل بعد.";
            groups.Add((ReservedClassCode, message, messageAr, new
            {
                error = ReservedClassCode, accountCode = g.Key, employeeCount = g.Select(b => b.EmployeeId).Distinct().Count(),
                employees = g.GroupBy(b => b.EmployeeId).Take(10).Select(x => new { id = x.Key, code = x.First().EmployeeCode }).ToList(),
                amount = Math.Round(g.Sum(b => b.Amount), 4), message, messageAr,
            }));
        }

        var first = groups[0];
        var error = first.Error;
        var body = new
        {
            error,
            message = first.Message,
            messageAr = first.MessageAr,
            blockers = blockers.Select(b => new
            {
                employeeId = b.EmployeeId, employeeCode = b.EmployeeCode, level = b.Level, departmentId = b.DepartmentId,
                positionId = b.PositionId, parentDepartmentId = b.ParentDepartmentId, reason = b.Reason, accountCode = b.AccountCode,
            }).ToList(),
            groups = groups.Select(g => g.Group).ToList(),
        };
        return (error, body, groups.Select(g => g.Group).ToList());
    }

    public static object RoundingRefusal(string accountCode, string accountName, decimal amount, string currency) => new
    {
        error = RoundingCode,
        message = $"A rounding difference of {amount.ToString("0.00", CultureInfo.InvariantCulture)} {currency} must be posted to account {accountCode}, which requires a cost centre. Set a default cost centre for the company, then lock again.",
        messageAr = $"يجب ترحيل فرق تقريب مقداره {amount.ToString("0.00", CultureInfo.InvariantCulture)} {currency} إلى الحساب {accountCode} الذي يتطلب مركز تكلفة. حدّد مركز تكلفة افتراضيًا للشركة ثم أعد القفل.",
        accountCode, accountName, amount, currency,
    };

    public static object ChangedAfterApprovalBody(string status) => new
    {
        error = ChangedAfterApprovalCode,
        message = "Cost-centre assignments or posting rules changed after this run was approved, so it was sent back for approval. Process and approve it again.",
        messageAr = "تغيّرت تعيينات مراكز التكلفة أو قواعد الترحيل بعد اعتماد هذه المسيرة، لذا أُعيدت للاعتماد. أعد المعالجة والاعتماد.",
        status,
    };

    // ── the approval fingerprint (AM-8) ──────────────────────────────────────

    /// <summary>Account master facts that decide where a line lands (name, type, active, external code, rule).</summary>
    public sealed record AccountMaster(string Code, string Name, string Type, bool IsActive, string? ExternalCode, string Rule, Guid? CompanyId);

    public static async Task<List<AccountMaster>> LoadAccountMastersAsync(ZayraDbContext db, Guid tenantId, Guid companyId, CancellationToken ct) =>
        await ScopedBypass.TenantWide(db.GlAccounts, tenantId, "The approval fingerprint reads the company's chart plus the tenant-default chart, read-only.")
            .AsNoTracking().Where(a => a.CompanyId == companyId || a.CompanyId == null)
            .Select(a => new AccountMaster(a.Code, a.Name, a.AccountType, a.IsActive, a.ExternalCode, a.CostCenterRule, a.CompanyId))
            .ToListAsync(ct);

    /// <summary>
    /// SHA-256 over everything that decides what Lock posts: the journal Lock would write (accounts, sides, amounts, cost-centre id and
    /// snapshots, posting keys), the company's costing switch / K / default, each employee's resolution, the cost-centre master facts in
    /// play, the account master, the mappings, class overrides and fixed cost centres, and the refusals. Bonus, settlement and EOSB inputs
    /// are inside the journal itself. Null when costing is off.
    /// </summary>
    public static string? ComputeHash(PayrollCostPlan plan, IReadOnlyCollection<FinanceGlEntry> lines, GlResolutionContext ctx,
        IEnumerable<AccountMaster> accounts, IEnumerable<string> extra)
    {
        if (!plan.Enabled) return null;
        var sb = new StringBuilder();
        string N(decimal d) => d.ToString("0.####", CultureInfo.InvariantCulture);
        sb.Append("v1|enabled=").Append(ctx.CostAccountingEnabled).Append("|k=").Append(ctx.CostCentreSmallCellMin)
          .Append("|default=").Append(ctx.CompanyDefaultCostCentreId?.ToString("N") ?? "-").Append('\n');

        foreach (var (id, v) in plan.Employees.OrderBy(kv => kv.Key))
            sb.Append("emp|").Append(id).Append('|').Append(v.CostCenterId?.ToString("N") ?? "unresolved").Append('|').Append(v.Source ?? "-").Append('\n');

        var inPlay = new HashSet<Guid>(plan.Employees.Values.Where(v => v.CostCenterId != null).Select(v => v.CostCenterId!.Value));
        foreach (var l in lines) if (l.CostCenterId is Guid c) inPlay.Add(c);
        foreach (var f in ctx.FixedCostCentres.Values) inPlay.Add(f);
        if (ctx.CompanyDefaultCostCentreId is Guid dc) inPlay.Add(dc);
        foreach (var id in inPlay.OrderBy(i => i))
            if (ctx.CostCentres.TryGetValue(id, out var cc))
                sb.Append("cc|").Append(id.ToString("N")).Append('|').Append(cc.Code).Append('|').Append(cc.Name).Append('|').Append(cc.GlSegment ?? "-")
                  .Append('|').Append(cc.FunctionalClass ?? "-").Append('|').Append(cc.IsActive).Append('|').Append(cc.ParentId?.ToString("N") ?? "-").Append('\n');
            else sb.Append("cc|").Append(id.ToString("N")).Append("|missing\n");

        foreach (var a in accounts.OrderBy(a => a.Code, StringComparer.Ordinal).ThenBy(a => a.CompanyId))
            sb.Append("acct|").Append(a.Code).Append('|').Append(a.Name).Append('|').Append(a.Type).Append('|').Append(a.IsActive)
              .Append('|').Append(a.ExternalCode ?? "-").Append('|').Append(a.Rule).Append('|').Append(a.CompanyId?.ToString("N") ?? "-").Append('\n');
        foreach (var kv in ctx.Overrides.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.Append("map|").Append(kv.Key).Append('|').Append(kv.Value.Code).Append('|').Append(kv.Value.Name).Append('\n');
        foreach (var kv in ctx.ClassOverrides.OrderBy(kv => kv.Key.DriverKey, StringComparer.Ordinal).ThenBy(kv => kv.Key.Class, StringComparer.Ordinal))
            sb.Append("cmap|").Append(kv.Key.DriverKey).Append('|').Append(kv.Key.Class).Append('|').Append(kv.Value.Code).Append('|').Append(kv.Value.Name).Append('\n');
        foreach (var kv in ctx.FixedCostCentres.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.Append("fix|").Append(kv.Key).Append('|').Append(kv.Value.ToString("N")).Append('\n');

        foreach (var l in lines.OrderBy(l => l.PostingKey ?? string.Empty, StringComparer.Ordinal).ThenBy(l => l.Description, StringComparer.Ordinal))
            sb.Append("line|").Append(l.EventType).Append('|').Append(l.SourceEntityRef).Append('|').Append(l.DebitAccount).Append('|').Append(l.CreditAccount)
              .Append('|').Append(N(l.Amount)).Append('|').Append(l.CompanyId?.ToString("N") ?? "-")
              .Append('|').Append(l.CostCenterId?.ToString("N") ?? "-").Append('|').Append(l.CostCenterCode ?? "-").Append('|').Append(l.CostCenterName ?? "-")
              .Append('|').Append(l.CostCenterGlSegment ?? "-").Append('|').Append(l.Description).Append('|').Append(l.PostingKey ?? "-").Append('\n');

        foreach (var b in plan.Blockers)
            sb.Append("blk|").Append(b.EmployeeId).Append('|').Append(b.Reason).Append('|').Append(b.Level).Append('|').Append(b.AccountCode).Append('\n');
        foreach (var e in extra.OrderBy(e => e, StringComparer.Ordinal)) sb.Append("x|").Append(e).Append('\n');

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    // ── posting keys (contract 1.8) ──────────────────────────────────────────

    /// <summary>sha256(runId | eventType | sourceEntityRef | side | driverKey | componentCode | source | costCenterId-or-'-' | ordinal), hex.</summary>
    public static string PostingKey(Guid runId, string eventType, string sourceEntityRef, string side, string driverKey,
        string componentCode, string source, Guid? costCenterId, int ordinal)
    {
        var text = string.Join('|', runId.ToString("N"), eventType, sourceEntityRef, side, driverKey, componentCode, source,
            costCenterId?.ToString("N") ?? "-", ordinal.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    /// <summary>The reversal key: sha256('REV' | originalEntryId). "Void twice" fails at the unique index.</summary>
    public static string ReversalKey(Guid originalEntryId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("REV|" + originalEntryId.ToString("N")))).ToLowerInvariant();
}
