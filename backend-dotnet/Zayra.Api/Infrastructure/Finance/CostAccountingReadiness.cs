using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Finance;

/// <summary>One entry of a readiness check's capped sample (AM-13): an id and a code, plus a kind where the id alone is ambiguous.</summary>
public sealed record ReadinessSample(object Id, string Code, string? Kind = null);

/// <summary>One readiness check (AM-13). <see cref="Sample"/> is capped (25; employees 10) and never carries names, pay or foreign rows.</summary>
public sealed record ReadinessCheck(string Key, string Severity, int Count, IReadOnlyList<object> Sample);

/// <summary>AM-1 blocker detail: one entry per employee whose cost centre cannot be resolved. Ids and codes only.</summary>
public sealed record CostCentreBlocker(
    int EmployeeId, string EmployeeCode, string Level, Guid? DepartmentId, Guid? PositionId, Guid? ParentDepartmentId,
    string Reason, string ErrorCode, string? AccountCode = null);

public sealed record ReadinessResult(
    Guid CompanyId, bool Enabled, Guid? DefaultCostCenterId, short SmallCellMin, bool AwaitingRun,
    IReadOnlyList<ReadinessCheck> Checks, IReadOnlyList<CostCentreBlocker> Blockers, int SmallCellExposure)
{
    public IEnumerable<ReadinessCheck> HardChecks => Checks.Where(c => c.Severity == "blocker" && c.Count > 0);
}

/// <summary>
/// The cost-accounting readiness dry-run (AM-1, AM-2, AM-13). Read-only: it never writes, and it reads only the company's
/// own rows plus aggregate counts of tenant-wide data (cost centres with no company), so it can never name another company's
/// cost centre, account or employee (S-033).
/// <para>The per-employee resolution here mirrors the contract's order (employee override, position, department, parent
/// department, company default) for the DRY RUN only. Posting uses WP-A's resolver; the two must agree, and the readiness
/// tests pin the order.</para>
/// </summary>
public static class CostAccountingReadinessService
{
    public const int SampleCap = 25;
    public const int EmployeeSampleCap = 10;
    public const int SmallCellThreshold = 3;
    public static readonly string[] CheckKeys =
    {
        "employees-unresolved", "invalid-reference", "accounts-no-rule", "no-company", "rounding-default", "mapping-invisible",
        "small-cell", "rules-not-enforced", "currency-scale", "employees-no-company",
    };

    private sealed record CcRow(Guid Id, Guid? CompanyId, string Code, bool IsActive, bool IsDeleted);
    private sealed record DeptRow(Guid Id, string Code, Guid? ParentId, Guid? CostCenterId);
    private sealed record PosRow(Guid Id, string Code, Guid? CostCenterId);
    private sealed record EmpRow(int Id, string Code, Guid? DepartmentId, Guid? PositionId, Guid? CostCenterId, bool IsOverride);
    private sealed record Resolution(Guid? CostCenterId, string? Reason, string Level);

    /// <param name="defaultCostCenterId">The default cost centre to evaluate with (the proposed one when a change is being previewed).</param>
    /// <param name="smallCellMin">K to evaluate with.</param>
    public static async Task<ReadinessResult> ComputeAsync(
        ZayraDbContext db, Company company, Guid? defaultCostCenterId, short smallCellMin, CancellationToken ct)
    {
        var tid = company.TenantId;
        var cid = company.Id;

        var ccs = (await ScopedBypass.TenantWide(db.CostCenters, tid, "Readiness validates references against the tenant's cost centres; foreign rows are only ever counted, never returned.")
                .AsNoTracking().Select(c => new CcRow(c.Id, c.CompanyId, c.Code, c.IsActive, c.IsDeleted)).ToListAsync(ct))
            .ToDictionary(c => c.Id);
        bool Valid(Guid? id) => id is { } v && ccs.TryGetValue(v, out var c) && !c.IsDeleted && c.IsActive && c.CompanyId == cid;

        var depts = (await ScopedBypass.TenantWide(db.Departments, tid, "Readiness reads the company's own departments.")
                .AsNoTracking().Where(d => d.CompanyId == cid && !d.IsDeleted)
                .Select(d => new DeptRow(d.Id, d.Code, d.ParentDepartmentId, d.CostCenterId)).ToListAsync(ct))
            .ToDictionary(d => d.Id);
        var positions = (await ScopedBypass.TenantWide(db.Positions, tid, "Readiness reads the company's own positions.")
                .AsNoTracking().Where(p => p.CompanyId == cid && !p.IsDeleted)
                .Select(p => new PosRow(p.Id, p.Code, p.CostCenterId)).ToListAsync(ct))
            .ToDictionary(p => p.Id);
        var employees = await ScopedBypass.NullableTenantWide(db.Employees, tid, "Readiness reads the company's own active employees (ids and codes only leave this method).")
            .AsNoTracking().Where(e => e.CompanyId == cid && !e.IsDeleted && e.Status == "Active")
            .OrderBy(e => e.EmployeeCode)
            .Select(e => new EmpRow(e.Id, e.EmployeeCode, e.DepartmentId, e.PositionId, e.CostCenterId, e.CostCenterIsOverride)).ToListAsync(ct);
        var accounts = await ScopedBypass.TenantWide(db.GlAccounts, tid, "Readiness reads the company's chart plus the tenant-default chart.")
            .AsNoTracking().Where(a => a.CompanyId == cid || a.CompanyId == null)
            .Select(a => new { a.Id, a.Code, a.AccountType, a.CostCenterRule, a.IsActive, a.CompanyId }).ToListAsync(ct);
        var mappings = await ScopedBypass.TenantWide(db.GlAccountMappings, tid, "Readiness reads the company's mappings plus the tenant-default ones that apply to it.")
            .AsNoTracking().Where(m => m.IsActive && m.SegmentCostCenterId != null && (m.CompanyId == cid || m.CompanyId == null))
            .Select(m => new { m.Id, m.DriverKey, m.SegmentCostCenterId }).ToListAsync(ct);
        var awaiting = await CostAccountingGuards.HasRunAwaitingFinanceOrLockAsync(db, tid, cid, ct);

        // ── per-employee dry-run resolution ──
        Resolution Check(Guid? id, string level) =>
            Valid(id) ? new Resolution(id, null, level) : new Resolution(null, $"{level}_cost_centre_invalid", level);

        Resolution Resolve(EmpRow e)
        {
            if (e.IsOverride && e.CostCenterId != null) return Check(e.CostCenterId, "employee");
            if (e.PositionId is { } pid && positions.TryGetValue(pid, out var pos) && pos.CostCenterId != null) return Check(pos.CostCenterId, "position");
            var seen = new HashSet<Guid>();
            var depth = 0;
            var d = e.DepartmentId is { } did && depts.TryGetValue(did, out var d0) ? d0 : null;
            while (d is not null && seen.Add(d.Id) && depth < 20)
            {
                if (d.CostCenterId != null) return Check(d.CostCenterId, depth == 0 ? "department" : "parent_department");
                d = d.ParentId is { } par && depts.TryGetValue(par, out var next) ? next : null;
                depth++;
            }
            if (defaultCostCenterId != null) return Check(defaultCostCenterId, "company_default");
            return new Resolution(null, "none", "company");
        }

        static string LevelOf(Resolution r) => r.Level switch
        {
            "parent_department" => "parentDepartment",
            "company_default" => "company",
            _ => r.Level,
        };

        var requiredPnl = accounts.Any(a => a.IsActive && a.CostCenterRule == "Required" && GlAccountRules.IsPnl(a.AccountType));
        var resolved = new List<(EmpRow Emp, Resolution Res)>(employees.Count);
        foreach (var e in employees) resolved.Add((e, Resolve(e)));

        CostCentreBlocker Blocker(EmpRow e, Resolution r)
        {
            var parent = e.DepartmentId is { } did && depts.TryGetValue(did, out var dd) ? dd.ParentId : null;
            var requiredCode = accounts.Where(a => a.IsActive && a.CostCenterRule == "Required" && GlAccountRules.IsPnl(a.AccountType)).OrderBy(a => a.Code, StringComparer.Ordinal).Select(a => a.Code).FirstOrDefault();
            return new CostCentreBlocker(e.Id, e.Code, LevelOf(r), e.DepartmentId, e.PositionId, parent, r.Reason!,
                r.Reason == "none" ? "cost_centre_required" : "cost_centre_invalid", r.Reason == "none" ? requiredCode : null);
        }

        var unresolved = resolved.Where(x => x.Res.Reason == "none").ToList();
        var invalid = resolved.Where(x => x.Res.Reason != null && x.Res.Reason != "none").ToList();
        var blockers = unresolved.Take(EmployeeSampleCap).Select(x => Blocker(x.Emp, x.Res))
            .Concat(invalid.Take(EmployeeSampleCap).Select(x => Blocker(x.Emp, x.Res))).ToList();

        object EmployeeSample(EmpRow e, Resolution r)
        {
            var b = Blocker(e, r);
            return new { id = b.EmployeeId, code = b.EmployeeCode, employeeId = b.EmployeeId, employeeCode = b.EmployeeCode, level = b.Level,
                         departmentId = b.DepartmentId, positionId = b.PositionId, parentDepartmentId = b.ParentDepartmentId, reason = b.Reason };
        }

        var checks = new List<ReadinessCheck>();

        // 1. employees with no cost centre at any level
        checks.Add(new ReadinessCheck("employees-unresolved",
            unresolved.Count == 0 ? "ok" : requiredPnl ? "blocker" : "warning", unresolved.Count,
            unresolved.Take(EmployeeSampleCap).Select(x => EmployeeSample(x.Emp, x.Res)).ToList()));

        // 2. invalid references, listed by the row that holds them (own company's rows only)
        var holders = new List<ReadinessSample>();
        holders.AddRange(depts.Values.Where(d => d.CostCenterId != null && !Valid(d.CostCenterId)).OrderBy(d => d.Code, StringComparer.Ordinal).Select(d => new ReadinessSample(d.Id, d.Code, "department")));
        holders.AddRange(positions.Values.Where(p => p.CostCenterId != null && !Valid(p.CostCenterId)).OrderBy(p => p.Code, StringComparer.Ordinal).Select(p => new ReadinessSample(p.Id, p.Code, "position")));
        holders.AddRange(employees.Where(e => e.IsOverride && e.CostCenterId != null && !Valid(e.CostCenterId)).Select(e => new ReadinessSample(e.Id, e.Code, "employee")));
        if (defaultCostCenterId != null && !Valid(defaultCostCenterId)) holders.Add(new ReadinessSample(cid, "default", "company"));
        checks.Add(new ReadinessCheck("invalid-reference", holders.Count == 0 ? "ok" : "blocker", holders.Count,
            holders.Take(SampleCap).Select(h => (object)new { id = h.Id, code = h.Code, kind = h.Kind }).ToList()));

        // 3. profit-and-loss accounts that still sit on the default rule
        var noRule = accounts.Where(a => a.IsActive && a.CostCenterRule == "Optional" && GlAccountRules.IsPnl(a.AccountType)).OrderBy(a => a.Code, StringComparer.Ordinal).ToList();
        checks.Add(new ReadinessCheck("accounts-no-rule", noRule.Count == 0 ? "ok" : "warning", noRule.Count,
            noRule.Take(SampleCap).Select(a => (object)new { id = a.Id, code = a.Code }).ToList()));

        // 4. cost centres with no company: an aggregate count only (they are visible to every company by design)
        var noCompany = ccs.Values.Count(c => c.CompanyId == null && !c.IsDeleted);
        checks.Add(new ReadinessCheck("no-company", noCompany == 0 ? "ok" : "blocker", noCompany, Array.Empty<object>()));

        // 5. the rounding account requires a cost centre but the company has no usable default
        var gl = await GlAccountResolver.LoadAsync(db, tid, cid, ct);
        var (roundingCode, _) = GlAccountResolver.Resolve("ROUNDING", gl.Overrides, gl.DriverDefaults);
        var roundingRequired = gl.AccountRules.TryGetValue(roundingCode, out var rr) && rr.Rule == "Required";
        var roundingBad = roundingRequired && !Valid(defaultCostCenterId);
        checks.Add(new ReadinessCheck("rounding-default", roundingBad ? "blocker" : "ok", roundingBad ? 1 : 0, Array.Empty<object>()));

        // 6. a posting rule fixed to a cost centre this company cannot use (never names the foreign row)
        var invisible = mappings.Where(m => !Valid(m.SegmentCostCenterId)).OrderBy(m => m.DriverKey, StringComparer.Ordinal).ToList();
        checks.Add(new ReadinessCheck("mapping-invisible", invisible.Count == 0 ? "ok" : "blocker", invisible.Count,
            invisible.Take(SampleCap).Select(m => (object)new { id = m.Id, code = m.DriverKey }).ToList()));

        // 7. small cells (AM-15): cost centres whose headcount is below the privacy threshold and not rolled up
        var cells = resolved.Where(x => x.Res.CostCenterId != null).GroupBy(x => x.Res.CostCenterId!.Value)
            .Select(g => (Id: g.Key, N: g.Count())).Where(c => c.N >= Math.Max((int)smallCellMin, 1) && c.N < SmallCellThreshold)
            .OrderBy(c => ccs[c.Id].Code, StringComparer.Ordinal).ToList();
        var exposure = smallCellMin >= SmallCellThreshold ? 0 : cells.Count;
        checks.Add(new ReadinessCheck("small-cell", exposure == 0 ? "ok" : "warning", exposure,
            cells.Take(exposure == 0 ? 0 : SampleCap).Select(c => (object)new { id = c.Id, code = ccs[c.Id].Code }).ToList()));

        // 8. Required rules that nothing enforces because the switch is off
        var notEnforced = accounts.Where(a => !company.CostAccountingEnabled && a.IsActive && a.CostCenterRule == "Required" && GlAccountRules.IsPnl(a.AccountType))
            .OrderBy(a => a.Code, StringComparer.Ordinal).ToList();
        checks.Add(new ReadinessCheck("rules-not-enforced", notEnforced.Count == 0 ? "ok" : "warning", notEnforced.Count,
            notEnforced.Take(SampleCap).Select(a => (object)new { id = a.Id, code = a.Code }).ToList()));

        // 9. 3-decimal currencies are not carried end to end by payroll storage (S-053): a hard refusal, not a warning
        var scale3 = CurrencyMinorUnits.Scale(company.DefaultCurrency) == 3;
        checks.Add(new ReadinessCheck("currency-scale", scale3 ? "blocker" : "ok", scale3 ? 1 : 0, Array.Empty<object>()));

        // 10. active employees with no company: their ledger lines would be unattributed, which a costing tenant refuses.
        // The pool is tenant-wide by nature (it belongs to no company), so only the COUNT is returned, never who they are.
        var noCompanyEmployees = await ScopedBypass.NullableTenantWide(db.Employees, tid, "Counts the tenant's active employees that belong to no company; aggregate only.")
            .AsNoTracking().CountAsync(e => e.CompanyId == null && !e.IsDeleted && e.Status == "Active", ct);
        checks.Add(new ReadinessCheck("employees-no-company", noCompanyEmployees == 0 ? "ok" : "blocker", noCompanyEmployees, Array.Empty<object>()));

        return new ReadinessResult(cid, company.CostAccountingEnabled, defaultCostCenterId, smallCellMin, awaiting, checks, blockers, exposure);
    }
}

/// <summary>Account-type predicate shared by the readiness checks (kept local so this file has no controller dependency).</summary>
internal static class GlAccountRules
{
    public static bool IsPnl(string? type) =>
        string.Equals(type, "Expense", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "Income", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "Revenue", StringComparison.OrdinalIgnoreCase);
}
