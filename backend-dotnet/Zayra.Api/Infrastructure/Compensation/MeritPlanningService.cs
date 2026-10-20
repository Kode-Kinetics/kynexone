using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Compensation;

/// <summary>The CMP permission keys (seeded in AuthSeeder).</summary>
public static class CompPermissions
{
    public const string Read = "compensation.read";
    /// <summary>Line managers: build and submit worksheets for their own direct reports.</summary>
    public const string Plan = "compensation.plan";
    /// <summary>HR: cycles, eligibility, budgets, guidelines, FX, close/apply/cancel/reverse.</summary>
    public const string Manage = "compensation.manage";
    /// <summary>Decide worksheet approvals (with approvals.decide) and approve the cycle.</summary>
    public const string Approve = "compensation.approve";
    public const string BenchmarksManage = "compensation.benchmarks.manage";
    public const string EquityRead = "compensation.equity.read";

    /// <summary>Every compensation key. Each role is granted these by explicit list only (CompensationRoleGrantTests).</summary>
    public static readonly IReadOnlyList<string> All = [Read, Plan, Manage, Approve, BenchmarksManage, EquityRead];
}

/// <summary>Stable notification event codes (outbox dedupe identity).</summary>
public static class CompEventCodes
{
    public const string WorksheetOpened = "compensation.worksheet.opened";
    public const string WorksheetSubmitted = "compensation.worksheet.submitted";
    public const string CycleApplied = "compensation.cycle.applied";
}

/// <summary>Who is calling, resolved once by the controller.</summary>
public sealed record CompCaller(Guid TenantId, Guid? UserId, string UserName, IReadOnlySet<string> Permissions, int? EmployeeId, RequestContext Context)
{
    public bool Can(string permission) => Permissions.Contains(permission);
    public bool CanManage => Can(CompPermissions.Manage);
    public bool CanReadAll => Can(CompPermissions.Manage) || Can(CompPermissions.Read) || Can(CompPermissions.Approve);
}

/// <summary>
/// Merit planning: cycle configuration and lifecycle, manager worksheets and scenarios, submission into the shared
/// approval engine, and the apply step that hands approved awards to the governed salary path.
///
/// <para><b>The write path (CMP_DESIGN.md §Payroll integration).</b> This service never writes
/// <c>employee_salary_structures</c>. Apply creates ONE <see cref="EmployeeChangeRequest"/> (<c>salaryBreakdown</c>)
/// per award, already approved (status <c>ApprovedPendingEffectiveDate</c>, approver = the cycle approver), with the
/// same sealed "what the approver saw" baseline the Approval Center records for a future-dated change. The existing
/// <see cref="Employees.EffectiveChangeJobHandler"/> applies it on the effective date — exactly once, row-locked,
/// drift-checked — through <see cref="Employees.EmployeeSalaryBreakdownWriter"/>. Apply is idempotent: the cycle row is
/// locked, an awarded line is skipped, and the database allows one award per (cycle, employee).</para>
/// </summary>
public sealed partial class MeritPlanningService(
    ZayraDbContext db,
    IAuditService audit,
    IApprovalWorkflowService approvals,
    INotificationService? notifications = null,
    IDataProtectionProvider? dataProtection = null,
    ILogger<MeritPlanningService>? log = null)
{
    public const string WorksheetWorkflowCode = "COMP-WORKSHEET-DEFAULT";

    private readonly MeritBasisLoader _basis = new(db);
    private readonly IDataProtector? _baselineProtector = dataProtection is null ? null : EmployeeChangeBaseline.CreateProtector(dataProtection);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ───────────────────────────── cycles: read ─────────────────────────────

    public async Task<IReadOnlyList<CompCycleSummaryDto>> ListCyclesAsync(CompCaller caller, CancellationToken ct)
    {
        var cycles = await db.CompCycles.AsNoTracking().Where(c => c.TenantId == caller.TenantId)
            .OrderByDescending(c => c.EffectiveDate).ThenBy(c => c.Name).ToListAsync(ct);
        if (!caller.CanReadAll)
        {
            // A planner sees the cycles they have a worksheet in.
            var mine = caller.EmployeeId is int me
                ? await db.CompWorksheets.AsNoTracking().Where(w => w.ManagerEmployeeId == me).Select(w => w.CycleId).Distinct().ToListAsync(ct)
                : [];
            cycles = cycles.Where(c => mine.Contains(c.Id)).ToList();
        }
        return await SummariesAsync(cycles, ct);
    }

    /// <param name="tenantWideFor">Set only for a manager's own worksheet list (cycles they own a worksheet in): the company
    /// names and counts of those cycles are read past the company filter, pinned to the tenant.</param>
    private async Task<IReadOnlyList<CompCycleSummaryDto>> SummariesAsync(IReadOnlyList<CompCycle> cycles, CancellationToken ct, Guid? tenantWideFor = null)
    {
        var ids = cycles.Select(c => c.Id).ToList();
        var companyIds = cycles.Select(c => c.CompanyId).Distinct().ToList();
        const string why = "merit planning: names and counts of the cycles a manager owns a worksheet in";
        var companySet = tenantWideFor is Guid t1 ? Zayra.Api.Infrastructure.Data.ScopedBypass.TenantWide(db.Companies, t1, why) : db.Companies;
        var sheetSet = tenantWideFor is Guid t2 ? Zayra.Api.Infrastructure.Data.ScopedBypass.TenantWide(db.CompWorksheets, t2, why) : db.CompWorksheets;
        var companies = await companySet.AsNoTracking().Where(c => companyIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.LegalNameEn, ct);
        var sheets = await sheetSet.AsNoTracking().Where(w => ids.Contains(w.CycleId) && w.IsPrimary)
            .Select(w => new { w.CycleId, w.Status }).ToListAsync(ct);
        return cycles.Select(c => new CompCycleSummaryDto(c.Id, c.CompanyId, c.CompanyId is Guid id ? companies.GetValueOrDefault(id) ?? "" : "",
            c.Name, c.Status, c.EffectiveDate, c.Currency, c.PerformancePeriodStart, c.PerformancePeriodEnd,
            sheets.Count(s => s.CycleId == c.Id && s.Status != CompWorksheetStatuses.Cancelled),
            sheets.Count(s => s.CycleId == c.Id && s.Status == CompWorksheetStatuses.Approved), c.Version)).ToList();
    }

    public async Task<CompCycleDto> GetCycleAsync(CompCaller caller, Guid cycleId, CancellationToken ct)
    {
        if (!caller.CanReadAll) throw new CompForbiddenException("forbidden", "Only HR, Finance or a compensation approver can open the whole cycle. Open your worksheet instead.");
        var cycle = await FindCycleAsync(caller, cycleId, track: false, ct);
        return await ProjectCycleAsync(cycle, ct);
    }

    // ───────────────────────────── cycles: configure ─────────────────────────────

    public async Task<CompCycleDto> CreateCycleAsync(CompCaller caller, CreateCompCycleRequest request, CancellationToken ct)
    {
        RequireManage(caller);
        var company = await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == caller.TenantId && c.Id == request.CompanyId, ct)
            ?? throw new CompNotFoundException("That company was not found in your scope.");
        var currency = (company.DefaultCurrency ?? string.Empty).Trim().ToUpperInvariant();
        if (currency.Length != 3) throw new CompRuleException("company_currency_missing", $"{company.LegalNameEn} has no default currency. Set it on the company first; the cycle is planned in it.");
        var cycle = new CompCycle { TenantId = caller.TenantId, CompanyId = company.Id, Currency = currency, CreatedByUserId = caller.UserId };
        ApplyConfig(cycle, request.Name, request.PerformancePeriodStart, request.PerformancePeriodEnd, request.EffectiveDate, request.Eligibility, request.EquityIncludesNationality);
        db.CompCycles.Add(cycle);
        await db.SaveChangesAsync(ct);
        await AuditAsync(caller, "compensation.cycle.created", nameof(CompCycle), cycle.Id, new { cycle.Name, cycle.CompanyId, cycle.Currency, effectiveDate = cycle.EffectiveDate }, ct);
        return await ProjectCycleAsync(cycle, ct);
    }

    public async Task<CompCycleDto> UpdateCycleAsync(CompCaller caller, Guid cycleId, UpdateCompCycleRequest request, CancellationToken ct)
    {
        RequireManage(caller);
        var cycle = await FindCycleAsync(caller, cycleId, track: true, ct);
        RequireVersion(cycle, request.Version);
        if (cycle.Status != CompCycleStatuses.Draft) throw new CompRuleException("cycle_not_draft", "Eligibility, dates and proration can only be changed while the cycle is a draft.");
        ApplyConfig(cycle, request.Name, request.PerformancePeriodStart, request.PerformancePeriodEnd, request.EffectiveDate, request.Eligibility, request.EquityIncludesNationality);
        Touch(cycle, caller);
        await SaveAsync(ct);
        await AuditAsync(caller, "compensation.cycle.updated", nameof(CompCycle), cycle.Id, new { cycle.Name, effectiveDate = cycle.EffectiveDate, cycle.ProrationRule, cycle.MinimumEligibleDays }, ct);
        return await ProjectCycleAsync(cycle, ct);
    }

    private static void ApplyConfig(CompCycle cycle, string name, DateOnly start, DateOnly end, DateOnly effective, CompEligibilityInput eligibility, bool nationality)
    {
        var clean = (name ?? string.Empty).Trim();
        if (clean.Length is 0 or > 160) throw new CompRuleException("invalid_name", "Give the cycle a name of 1 to 160 characters.");
        if (end < start) throw new CompRuleException("invalid_period", "The performance period must end on or after it starts.");
        if (effective.Day != 1) throw new CompRuleException("effective_not_first", $"The effective date must be the 1st of a month (payroll pays a whole month at one rate). Did you mean {new DateOnly(effective.Year, effective.Month, 1).AddMonths(1):yyyy-MM-dd}?");
        if (effective <= start) throw new CompRuleException("effective_before_period", "The effective date must fall after the performance period starts.");
        if (!CompProrationRules.All.Contains(eligibility.ProrationRule)) throw new CompRuleException("invalid_proration", "Choose a proration rule: None or EligibleDays.");
        if (eligibility.MinimumEligibleDays is < 0 or > 3660) throw new CompRuleException("invalid_min_days", "Minimum eligible days must be between 0 and 3660.");
        var statuses = (eligibility.EligibleStatuses ?? ["Active"]).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (statuses.Count == 0) throw new CompRuleException("invalid_statuses", "Choose at least one employee status that may be eligible.");
        if (statuses.Any(s => MeritBasisLoader.SeparatedStatuses.Contains(s))) throw new CompRuleException("invalid_statuses", "A separated status can never be eligible for a merit increase.");
        cycle.Name = clean;
        cycle.PerformancePeriodStart = start;
        cycle.PerformancePeriodEnd = end;
        cycle.EffectiveDate = effective;
        cycle.HireDateCutoff = eligibility.HireDateCutoff;
        cycle.EligibleStatuses = string.Join(',', statuses);
        cycle.EligibilityFiltersJson = new CompEligibilityFilters(
            (eligibility.IncludeGradeIds ?? []).Distinct().ToList(), (eligibility.ExcludeGradeIds ?? []).Distinct().ToList(),
            (eligibility.IncludeDepartmentIds ?? []).Distinct().ToList(), (eligibility.ExcludeDepartmentIds ?? []).Distinct().ToList()).ToJson();
        cycle.MinimumEligibleDays = eligibility.MinimumEligibleDays;
        cycle.ProrationRule = eligibility.ProrationRule;
        cycle.EquityIncludesNationality = nationality;
    }

    public async Task<CompCycleDto> SaveBudgetsAsync(CompCaller caller, Guid cycleId, SaveCompBudgetsRequest request, CancellationToken ct)
    {
        RequireManage(caller);
        var cycle = await FindCycleAsync(caller, cycleId, track: true, ct);
        if (cycle.Status is not (CompCycleStatuses.Draft or CompCycleStatuses.Planning))
            throw new CompRuleException("budgets_locked", "Budgets can be changed while the cycle is a draft or in planning.");
        await RequireNothingSubmittedAsync(cycle, "Budgets", ct);
        var inputs = request.Budgets ?? [];
        if (inputs.GroupBy(b => b.DepartmentId).Any(g => g.Count() > 1)) throw new CompRuleException("duplicate_department", "Each department can have one budget.");
        var departmentIds = inputs.Select(b => b.DepartmentId).ToList();
        var known = await db.Departments.AsNoTracking().Where(d => d.TenantId == caller.TenantId && departmentIds.Contains(d.Id) && !d.IsDeleted).Select(d => d.Id).ToListAsync(ct);
        if (departmentIds.Except(known).Any()) throw new CompRuleException("unknown_department", "A department in the budget list was not found.");
        foreach (var b in inputs)
        {
            if (b.PoolType == CompBudgetPoolTypes.PercentOfEligibleBasic && (b.PoolPercent is not decimal p || p < 0 || p > 100 || b.FixedAmount is not null))
                throw new CompRuleException("invalid_budget", "A percentage budget needs a percent between 0 and 100 and no fixed amount.");
            if (b.PoolType == CompBudgetPoolTypes.FixedAmount && (b.FixedAmount is not decimal f || f < 0 || MeritMath.Money(f) != f || b.PoolPercent is not null))
                throw new CompRuleException("invalid_budget", "A fixed budget needs a non-negative amount with at most two decimals and no percent.");
            if (!CompBudgetPoolTypes.All.Contains(b.PoolType)) throw new CompRuleException("invalid_budget", "Budget type must be PercentOfEligibleBasic or FixedAmount.");
        }
        var existing = await db.CompCycleBudgets.Where(b => b.CycleId == cycle.Id).ToListAsync(ct);
        db.CompCycleBudgets.RemoveRange(existing.Where(e => !departmentIds.Contains(e.DepartmentId)));
        foreach (var input in inputs)
        {
            var row = existing.FirstOrDefault(e => e.DepartmentId == input.DepartmentId);
            if (row is null) { row = new CompCycleBudget { TenantId = caller.TenantId, CompanyId = cycle.CompanyId, CycleId = cycle.Id, DepartmentId = input.DepartmentId, CreatedByUserId = caller.UserId }; db.CompCycleBudgets.Add(row); }
            else { row.Version++; row.UpdatedAtUtc = DateTime.UtcNow; }
            row.PoolType = input.PoolType;
            row.PoolPercent = input.PoolType == CompBudgetPoolTypes.PercentOfEligibleBasic ? input.PoolPercent : null;
            row.FixedAmount = input.PoolType == CompBudgetPoolTypes.FixedAmount ? input.FixedAmount : null;
        }
        Touch(cycle, caller);
        await SaveAsync(ct);
        await AuditAsync(caller, "compensation.budgets.saved", nameof(CompCycle), cycle.Id, new { budgets = inputs }, ct);
        return await ProjectCycleAsync(cycle, ct);
    }

    public async Task<CompCycleDto> SaveGuidelinesAsync(CompCaller caller, Guid cycleId, SaveCompGuidelinesRequest request, CancellationToken ct)
    {
        RequireManage(caller);
        var cycle = await FindCycleAsync(caller, cycleId, track: true, ct);
        if (cycle.Status != CompCycleStatuses.Draft) throw new CompRuleException("guidelines_locked", "The guideline matrix is fixed once planning opens, so every manager plans against the same rules.");
        var inputs = request.Guidelines ?? [];
        foreach (var g in inputs)
        {
            if (string.IsNullOrWhiteSpace(g.Rating) || g.Rating.Trim().Length > 80) throw new CompRuleException("invalid_guideline", "Each guideline needs a rating label (up to 80 characters).");
            if (g.MinPercent < 0 || g.MaxPercent > 100 || g.MinPercent > g.TargetPercent || g.TargetPercent > g.MaxPercent)
                throw new CompRuleException("invalid_guideline", $"{g.Rating}: minimum ≤ target ≤ maximum, all between 0 % and 100 %.");
            if (g.CompaRatioMin < 0 || (g.CompaRatioMax is decimal max && max <= g.CompaRatioMin))
                throw new CompRuleException("invalid_guideline", $"{g.Rating}: the compa-ratio band must start at 0 or above and end after it starts.");
        }
        foreach (var rating in inputs.GroupBy(g => g.Rating.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var bands = rating.OrderBy(g => g.CompaRatioMin).ToList();
            for (var i = 1; i < bands.Count; i++)
                if (bands[i - 1].CompaRatioMax is not decimal prevMax || prevMax > bands[i].CompaRatioMin)
                    throw new CompRuleException("overlapping_bands", $"{rating.Key}: compa-ratio bands overlap. Each band ends where the next begins.");
        }
        db.CompGuidelines.RemoveRange(await db.CompGuidelines.Where(g => g.CycleId == cycle.Id).ToListAsync(ct));
        foreach (var g in inputs)
            db.CompGuidelines.Add(new CompGuideline
            {
                TenantId = caller.TenantId, CompanyId = cycle.CompanyId, CycleId = cycle.Id, Rating = g.Rating.Trim(),
                CompaRatioMin = g.CompaRatioMin, CompaRatioMax = g.CompaRatioMax, MinPercent = g.MinPercent, TargetPercent = g.TargetPercent, MaxPercent = g.MaxPercent,
            });
        Touch(cycle, caller);
        await SaveAsync(ct);
        await AuditAsync(caller, "compensation.guidelines.saved", nameof(CompCycle), cycle.Id, new { cells = inputs }, ct);
        return await ProjectCycleAsync(cycle, ct);
    }

    public async Task<CompCycleDto> SaveFxAsync(CompCaller caller, Guid cycleId, SaveCompFxRequest request, CancellationToken ct)
    {
        RequireManage(caller);
        var cycle = await FindCycleAsync(caller, cycleId, track: true, ct);
        if (cycle.Status is not (CompCycleStatuses.Draft or CompCycleStatuses.Planning))
            throw new CompRuleException("fx_locked", "FX rates can be entered while the cycle is a draft or in planning.");
        await RequireNothingSubmittedAsync(cycle, "Exchange rates", ct);
        var inputs = (request.Rates ?? []).Select(r => r with { FromCurrency = (r.FromCurrency ?? "").Trim().ToUpperInvariant(), Source = (r.Source ?? "").Trim() }).ToList();
        foreach (var r in inputs)
        {
            if (r.FromCurrency.Length != 3 || !r.FromCurrency.All(char.IsAsciiLetterUpper)) throw new CompRuleException("invalid_fx", "Currency must be a three-letter code such as AED.");
            if (r.FromCurrency == cycle.Currency) throw new CompRuleException("invalid_fx", $"{cycle.Currency} is the cycle currency; it needs no rate.");
            if (r.Rate <= 0 || decimal.Round(r.Rate, 8) != r.Rate) throw new CompRuleException("invalid_fx", $"{r.FromCurrency}: the rate must be positive with at most 8 decimals.");
            if (r.Source.Length is 0 or > 200) throw new CompRuleException("invalid_fx", $"{r.FromCurrency}: say where the rate comes from (for example the central bank reference rate).");
            if (r.RateDate > DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1)) throw new CompRuleException("invalid_fx", $"{r.FromCurrency}: the rate date cannot be in the future.");
        }
        if (inputs.GroupBy(r => r.FromCurrency).Any(g => g.Count() > 1)) throw new CompRuleException("duplicate_fx", "Each currency can have one rate.");
        var existing = await db.CompFxRates.Where(f => f.CycleId == cycle.Id).ToListAsync(ct);
        db.CompFxRates.RemoveRange(existing.Where(e => inputs.All(i => i.FromCurrency != e.FromCurrency)));
        foreach (var input in inputs)
        {
            var row = existing.FirstOrDefault(e => e.FromCurrency == input.FromCurrency);
            if (row is not null && row.Rate == input.Rate && row.Source == input.Source && row.RateDate == input.RateDate) continue;
            if (row is null) { row = new CompFxRate { TenantId = caller.TenantId, CompanyId = cycle.CompanyId, CycleId = cycle.Id, FromCurrency = input.FromCurrency }; db.CompFxRates.Add(row); }
            row.Rate = input.Rate; row.Source = input.Source; row.RateDate = input.RateDate;
            row.EnteredByUserId = caller.UserId; row.EnteredByName = caller.UserName; row.EnteredAtUtc = DateTime.UtcNow;
        }
        Touch(cycle, caller);
        await SaveAsync(ct);
        await AuditAsync(caller, "compensation.fx.saved", nameof(CompCycle), cycle.Id, new { rates = inputs }, ct);
        return await ProjectCycleAsync(cycle, ct);
    }

    // ───────────────────────────── cycles: lifecycle ─────────────────────────────

    /// <summary>Draft → Planning. Opens one primary worksheet per manager with eligible direct reports.</summary>
    public async Task<CompCycleDto> OpenPlanningAsync(CompCaller caller, Guid cycleId, CompCycleTransitionRequest request, CancellationToken ct)
    {
        RequireManage(caller);
        var opened = new List<(Guid WorksheetId, int ManagerId)>();
        var cycle = await InTransactionAsync(async () =>
        {
            var c = await LockCycleAsync(caller, cycleId, ct);
            RequireVersion(c, request.Version);
            if (c.Status != CompCycleStatuses.Draft) throw new CompRuleException("cycle_not_draft", $"Planning can be opened from Draft; this cycle is {c.Status}.");
            var ctx = await _basis.LoadAsync(c, ct);
            var blockers = PlanningBlockers(ctx);
            if (blockers.Count > 0) throw new CompRuleException("planning_blocked", "Planning cannot open yet: " + string.Join(" ", blockers.Select(b => b.Message)), blockers);
            await EnsureWorksheetWorkflowAsync(caller, ct);

            var managers = await ManagersAsync(ctx, ct);
            foreach (var managerId in ctx.Employees.Values.Where(b => b.Eligible && b.Employee.ManagerEmployeeId is int m && managers.ContainsKey(m))
                         .Select(b => b.Employee.ManagerEmployeeId!.Value).Distinct())
            {
                var sheet = new CompWorksheet
                {
                    TenantId = caller.TenantId, CompanyId = c.CompanyId, CycleId = c.Id, ManagerEmployeeId = managerId,
                    ScenarioName = "Main", IsPrimary = true, CreatedByUserId = caller.UserId,
                };
                db.CompWorksheets.Add(sheet);
                foreach (var b in TeamOf(ctx, managerId))
                {
                    var line = NewLine(c, sheet);
                    MeritLineCalculator.Compute(line, b, ctx, 0m, null, null);
                    db.CompWorksheetLines.Add(line);
                }
                opened.Add((sheet.Id, managerId));
            }
            c.Status = CompCycleStatuses.Planning;
            c.PlanningOpenedAtUtc = DateTime.UtcNow;
            c.PlanningOpenedByUserId = caller.UserId;
            Touch(c, caller);
            await SaveAsync(ct);
            return c;
        }, ct);
        await AuditAsync(caller, "compensation.cycle.planning_opened", nameof(CompCycle), cycle.Id, new { worksheets = opened.Count }, ct);
        foreach (var (worksheetId, managerId) in opened)
            await NotifyEmployeeAsync(caller.TenantId, managerId, CompEventCodes.WorksheetOpened, nameof(CompWorksheet), worksheetId,
                $"Merit planning is open: {cycle.Name}",
                $"Your merit worksheet for {cycle.Name} is ready. Propose increases for your team before the cycle closes. | ورقة الزيادات السنوية لفريقك جاهزة: {cycle.Name}.", ct);
        return await ProjectCycleAsync(cycle, ct);
    }

    /// <summary>Planning → Review: every primary worksheet is approved.</summary>
    public async Task<CompCycleDto> StartReviewAsync(CompCaller caller, Guid cycleId, CompCycleTransitionRequest request, CancellationToken ct)
    {
        RequireManage(caller);
        var cycle = await InTransactionAsync(async () =>
        {
            var c = await LockCycleAsync(caller, cycleId, ct);
            RequireVersion(c, request.Version);
            if (c.Status != CompCycleStatuses.Planning) throw new CompRuleException("cycle_not_planning", $"Review starts from Planning; this cycle is {c.Status}.");
            var sheets = await db.CompWorksheets.AsNoTracking().Where(w => w.CycleId == c.Id && w.IsPrimary && w.Status != CompWorksheetStatuses.Cancelled).ToListAsync(ct);
            if (sheets.Count == 0) throw new CompRuleException("no_worksheets", "There are no worksheets in this cycle.");
            var open = sheets.Where(w => w.Status != CompWorksheetStatuses.Approved).ToList();
            if (open.Count > 0) throw new CompRuleException("worksheets_open", $"{open.Count} worksheet(s) are not approved yet. Every manager's worksheet must be approved before the cycle goes to review.");
            var inconsistent = await CycleConsistencyAsync(c, ct);
            if (inconsistent.Count > 0) throw new CompRuleException("cycle_inconsistent", "The approved worksheets no longer match the cycle's budgets or rates.", inconsistent);
            c.Status = CompCycleStatuses.Review;
            c.ReviewStartedAtUtc = DateTime.UtcNow;
            c.ReviewStartedByUserId = caller.UserId;
            Touch(c, caller);
            await SaveAsync(ct);
            return c;
        }, ct);
        await AuditAsync(caller, "compensation.cycle.review_started", nameof(CompCycle), cycle.Id, null, ct);
        return await ProjectCycleAsync(cycle, ct);
    }

    /// <summary>
    /// Back to editable planning, with a reason: from Review (to Planning), or within Planning to unlock budgets and FX.
    /// Every submitted or approved worksheet is returned to its owner (revision +1, the reason as the comment, its pending
    /// approval closed), so nothing approved against the old budget or rate stays approved. Audited per worksheet.
    /// </summary>
    public async Task<CompCycleDto> ReopenPlanningAsync(CompCaller caller, Guid cycleId, CompCycleTransitionRequest request, CancellationToken ct)
    {
        RequireManage(caller);
        var reason = RequireReason(request.Reason);
        var returned = new List<Guid>();
        var cycle = await InTransactionAsync(async () =>
        {
            returned.Clear();
            var c = await LockCycleAsync(caller, cycleId, ct);
            RequireVersion(c, request.Version);
            if (c.Status is not (CompCycleStatuses.Review or CompCycleStatuses.Planning))
                throw new CompRuleException("cycle_not_reopenable", $"Planning can be reopened from Review or Planning; this cycle is {c.Status}.");
            var sheets = await db.CompWorksheets.Where(w => w.CycleId == c.Id && w.IsPrimary
                && (w.Status == CompWorksheetStatuses.Submitted || w.Status == CompWorksheetStatuses.Approved)).ToListAsync(ct);
            var now = DateTime.UtcNow;
            foreach (var sheet in sheets)
            {
                if (sheet.Status == CompWorksheetStatuses.Submitted && sheet.ApprovalRequestId is Guid approvalId) await CloseApprovalAsync(c.TenantId, approvalId, ct);
                sheet.Status = CompWorksheetStatuses.Returned;
                sheet.StartRevision();
                sheet.DecisionComments = Truncate($"Planning was reopened by HR: {reason}", 1000);
                sheet.DecidedAtUtc = now;
                sheet.Version++;
                returned.Add(sheet.Id);
            }
            c.Status = CompCycleStatuses.Planning;
            c.ReviewStartedAtUtc = null; c.ReviewStartedByUserId = null;
            Touch(c, caller);
            await SaveAsync(ct);
            return c;
        }, ct);
        foreach (var id in returned)
            await AuditAsync(caller, "compensation.worksheet.returned_by_reopen", nameof(CompWorksheet), id, new { cycleId, reason }, ct);
        await AuditAsync(caller, "compensation.cycle.reopened", nameof(CompCycle), cycle.Id, new { reason, returnedWorksheets = returned.Count }, ct);
        return await ProjectCycleAsync(cycle, ct);
    }

    private async Task RequireNothingSubmittedAsync(CompCycle cycle, string what, CancellationToken ct)
    {
        if (cycle.Status != CompCycleStatuses.Planning) return;
        var locked = await db.CompWorksheets.AsNoTracking().CountAsync(w => w.CycleId == cycle.Id && w.IsPrimary
            && (w.Status == CompWorksheetStatuses.Submitted || w.Status == CompWorksheetStatuses.Approved), ct);
        if (locked > 0)
            throw new CompRuleException("planning_locked",
                $"{what} are locked because {locked} worksheet(s) are submitted or approved against them. Reopen planning (with a reason) to return those worksheets, then change it.");
    }

    /// <summary>Budgets and FX re-checked against what was approved, before review and before cycle approval.</summary>
    private async Task<List<CompBlockerDto>> CycleConsistencyAsync(CompCycle c, CancellationToken ct)
    {
        var blockers = new List<CompBlockerDto>();
        var sheets = await db.CompWorksheets.AsNoTracking().Where(w => w.CycleId == c.Id && w.IsPrimary && w.Status != CompWorksheetStatuses.Cancelled).ToListAsync(ct);
        var ids = sheets.Select(w => w.Id).ToList();
        var allLines = await db.CompWorksheetLines.AsNoTracking().Where(l => ids.Contains(l.WorksheetId)).ToListAsync(ct);
        var ctx = await _basis.LoadAsync(c, ct, allLines.Where(l => l.ChangeRequestId != null).Select(l => l.ChangeRequestId!.Value).ToList());
        var lines = CurrentLines(allLines, sheets.ToDictionary(x => x.Id), ctx);
        foreach (var l in lines.Where(l => l.ProratedIncreasePay > 0))
        {
            var rate = ctx.RateFor(l.PayCurrency);
            if (rate is null || l.FxRate != rate)
                blockers.Add(new("fx_changed", $"Employee {l.EmployeeId}: the {l.PayCurrency} rate is not the one the award was approved at. Reopen planning so the worksheet is recomputed and approved again."));
        }
        var touchedBy = lines.Where(l => l.ProratedIncrease > 0).GroupBy(l => l.DepartmentId ?? Guid.Empty).ToDictionary(g => g.Key, g => g.Select(l => l.WorksheetId).ToHashSet());
        var frozenCtx = new MeritBasisLoader.CycleContext
        {
            Cycle = new CompCycle { Status = CompCycleStatuses.Review, Currency = c.Currency }, Company = ctx.Company, Employees = ctx.Employees, Fx = ctx.Fx,
            Guidelines = ctx.Guidelines, Budgets = ctx.Budgets, DepartmentNames = ctx.DepartmentNames,
        };
        foreach (var b in BudgetSummary(frozenCtx, lines).Where(b => b.Overspent))
            foreach (var sheet in sheets.Where(s => touchedBy.GetValueOrDefault(b.DepartmentId)?.Contains(s.Id) == true && string.IsNullOrWhiteSpace(s.OverspendJustification)))
                blockers.Add(new("overspend_unjustified", $"{b.DepartmentName} is over budget and a worksheet that spends from it has no overspend justification. Reopen planning."));
        return blockers;
    }

    /// <summary>Review → Approved. Maker-checker: the person who sent it to review cannot approve it.</summary>
    public async Task<CompCycleDto> ApproveCycleAsync(CompCaller caller, Guid cycleId, CompCycleTransitionRequest request, CancellationToken ct)
    {
        if (!caller.Can(CompPermissions.Approve)) throw new CompForbiddenException("forbidden", "Approving a merit cycle needs the compensation approver permission.");
        CompRetroDto? retro = null;
        var cycle = await InTransactionAsync(async () =>
        {
            var c = await LockCycleAsync(caller, cycleId, ct);
            RequireVersion(c, request.Version);
            if (c.Status != CompCycleStatuses.Review) throw new CompRuleException("cycle_not_review", $"A cycle is approved from Review; this cycle is {c.Status}.");
            if (caller.UserId is null || c.ReviewStartedByUserId == caller.UserId)
                throw new CompForbiddenException("maker_checker", "Segregation of duties: you sent this cycle to review, so a different approver must approve it.");
            if (caller.EmployeeId is int me && await db.CompWorksheetLines.AnyAsync(l => l.CycleId == c.Id && l.EmployeeId == me, ct))
                throw new CompForbiddenException("subject", "Segregation of duties: this cycle includes your own pay, so you cannot approve it.");
            var inconsistent = await CycleConsistencyAsync(c, ct);
            if (inconsistent.Count > 0) throw new CompRuleException("cycle_inconsistent", "The approved worksheets no longer match the cycle's budgets or rates.", inconsistent);
            var proposed = await (from l in db.CompWorksheetLines join w in db.CompWorksheets on l.WorksheetId equals w.Id
                                  where l.CycleId == c.Id && w.IsPrimary && w.Status == CompWorksheetStatuses.Approved select l.ProratedIncrease).SumAsync(ct);
            retro = await RetroAsync(c, proposed, ct);
            if (retro is { AcknowledgementRequired: true } && !request.AcknowledgeRetroCost)
                throw new CompRuleException("retro_acknowledgement_required",
                    FormattableString.Invariant($"The effective date is in the past: payroll for {string.Join(", ", retro.PaidPeriods)} already ran at the old rate, so these awards owe about {retro.RetroCostCycle:#,##0.00} {c.Currency} in arrears ({retro.PaidPeriods.Count} month(s) × {retro.MonthlyDeltaCycle:#,##0.00}). Acknowledge the retro cost to approve."));
            c.RetroAcknowledgedPeriods = retro is { AcknowledgementRequired: true } ? string.Join(",", retro.PaidPeriods) : null;
            c.Status = CompCycleStatuses.Approved;
            c.ApprovedAtUtc = DateTime.UtcNow;
            c.ApprovedByUserId = caller.UserId;
            Touch(c, caller);
            await SaveAsync(ct);
            return c;
        }, ct);
        await AuditAsync(caller, "compensation.cycle.approved", nameof(CompCycle), cycle.Id,
            retro is null ? null : new { retroAcknowledged = request.AcknowledgeRetroCost, retro.PaidPeriods, retro.MonthlyDeltaCycle, retro.RetroCostCycle }, ct);
        return await ProjectCycleAsync(cycle, ct);
    }

    /// <summary>
    /// Approved → Applied. Hands every approved award to the governed salary path as an already-approved, future-dated
    /// <see cref="EmployeeChangeRequest"/>. Safe to run again: it only picks up lines without a change request, so a
    /// line that was blocked (and since fixed) is scheduled on the next run and nothing is scheduled twice.
    /// </summary>
    public async Task<CompCycleDto> ApplyAsync(CompCaller caller, Guid cycleId, CancellationToken ct, bool acknowledgeRetroCost = false)
    {
        RequireManage(caller);
        if (_baselineProtector is null)
            throw new CompRuleException("no_key_ring", "The server cannot seal the approval baseline (no data-protection key ring), so awards cannot be scheduled safely. Nothing was changed.");
        var scheduled = new List<(Guid ChangeId, int EmployeeId, decimal NewBasic)>();
        var blocked = new List<(Guid LineId, int EmployeeId, string Reason)>();
        (List<string> Periods, decimal Cost)? retroAcknowledged = null;
        var cycle = await InTransactionAsync(async () =>
        {
            scheduled.Clear(); blocked.Clear(); retroAcknowledged = null;
            var c = await LockCycleAsync(caller, cycleId, ct);
            if (c.Status is not (CompCycleStatuses.Approved or CompCycleStatuses.Applied))
                throw new CompRuleException("cycle_not_approved", $"Awards are applied once the cycle is approved; this cycle is {c.Status}.");
            var liveSheets = await db.CompWorksheets.AsNoTracking()
                .Where(w => w.CycleId == c.Id && w.IsPrimary && w.Status != CompWorksheetStatuses.Cancelled).ToDictionaryAsync(w => w.Id, ct);
            var approvedSheets = liveSheets.Values.Where(w => w.Status == CompWorksheetStatuses.Approved).Select(w => w.Id).ToHashSet();
            var candidateLines = await db.CompWorksheetLines.Where(l => l.CycleId == c.Id && liveSheets.Keys.Contains(l.WorksheetId)).ToListAsync(ct);
            var ownChanges = candidateLines.Where(l => l.ChangeRequestId != null).Select(l => l.ChangeRequestId!.Value).ToList();
            var ctx = await _basis.LoadAsync(c, ct, ownChanges);
            // One line per employee: a superseded line (the employee moved to another worksheet owner) is never scheduled.
            var lines = CurrentLines(candidateLines, liveSheets, ctx).Where(l => approvedSheets.Contains(l.WorksheetId)).ToList();
            // A payroll month run between approval and apply was paid at the old rate: scheduling now owes arrears the
            // approver never saw. It needs an explicit acknowledgement by someone with the approver permission.
            var toSchedule = lines.Where(l => l.ChangeRequestId == null && l.ProratedIncreasePay > 0).ToList();
            if (toSchedule.Count > 0)
            {
                var acknowledged = (c.RetroAcknowledgedPeriods ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                var unacknowledged = (await RunPeriodsFromAsync(c.TenantId, c.CompanyId, c.EffectiveDate, PaidRunStatuses, ct)).Where(p => !acknowledged.Contains(p)).ToList();
                if (unacknowledged.Count > 0)
                {
                    var cost = MeritMath.Money(unacknowledged.Count * toSchedule.Sum(l => l.ProratedIncrease));
                    if (!acknowledgeRetroCost)
                        throw new CompRuleException("retro_acknowledgement_required",
                            FormattableString.Invariant($"Payroll for {string.Join(", ", unacknowledged)} ran after this cycle was approved, at the old pay. Scheduling the awards now adds about {cost:#,##0.00} {c.Currency} of arrears for those months. An approver must acknowledge it to schedule the awards."));
                    if (!caller.Can(CompPermissions.Approve))
                        throw new CompForbiddenException("approver_required", "Arrears for payroll months run after approval must be acknowledged by someone with the compensation approver permission.");
                    c.RetroAcknowledgedPeriods = string.Join(",", acknowledged.Concat(unacknowledged).Order());
                    retroAcknowledged = (unacknowledged, cost);
                }
            }
            var awardedEmployees = candidateLines.Where(l => l.ChangeRequestId != null).Select(l => l.EmployeeId).ToHashSet();
            var now = DateTime.UtcNow;
            foreach (var line in lines.Where(l => l.ChangeRequestId == null && l.ProratedIncreasePay > 0).OrderBy(l => l.EmployeeId))
            {
                var reason = await ApplyBlockAsync(line, ctx, awardedEmployees, ct);
                if (reason is not null)
                {
                    line.ApplyBlockedReason = reason;
                    blocked.Add((line.Id, line.EmployeeId, reason));
                    continue;
                }
                var b = ctx.Employees[line.EmployeeId];
                var package = MeritLineCalculator.Package(b.SalaryRow!, b.SalaryStructureCode, line.NewBasic, c.EffectiveDate, line.PayCurrency);
                var change = new EmployeeChangeRequest
                {
                    TenantId = c.TenantId,
                    EmployeeId = line.EmployeeId,
                    RequestedByUserId = caller.UserId,
                    Status = EmployeeChangeStatuses.ApprovedPendingEffectiveDate,
                    RequiresApproval = true,
                    EffectiveDate = c.EffectiveDate,
                    SensitiveFields = EmployeeSalaryBreakdownChanges.Key,
                    ProposedChangesJson = JsonSerializer.Serialize(package),
                    // The authority is the cycle's approval (worksheet approvals + the cycle approver), recorded on the
                    // line and the history row. ApprovalRequestId stays null on purpose: it would point at a
                    // CompensationWorksheet request, and a re-review raised by the effective-date job must route on the
                    // EmployeeChangeRequest workflow, not on the worksheet's.
                    ApprovalRequestId = null,
                    ApprovedByUserId = c.ApprovedByUserId,
                    ApprovedAtUtc = now,
                };
                db.EmployeeChangeRequests.Add(change);
                var profile = await db.EmployeePayrollProfiles.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.TenantId == c.TenantId && p.EmployeeId == line.EmployeeId && !p.IsDeleted, ct);
                var baseline = await EmployeeChangeBaseline.CaptureAsync(db, b.Employee, profile, package.Keys, ct);
                db.EmployeeHistories.Add(new EmployeeHistory
                {
                    TenantId = c.TenantId,
                    EmployeeId = line.EmployeeId,
                    EventType = EmployeeChangeBaseline.ScheduledEventType,
                    FieldName = EmployeeSalaryBreakdownChanges.Key,
                    EffectiveDate = c.EffectiveDate,
                    Reason = $"Merit award from cycle '{c.Name}' (approved {c.ApprovedAtUtc:yyyy-MM-dd}), worksheet line {line.Id}.",
                    ApprovedByUserId = c.ApprovedByUserId,
                    SnapshotJson = EmployeeChangeBaseline.SnapshotJson(change.Id, baseline is null ? null : EmployeeChangeBaseline.Protect(_baselineProtector, change.Id, baseline)),
                    CreatedByUserId = caller.UserId,
                });
                line.ChangeRequestId = change.Id;
                line.ChangeRequestedAtUtc = now;
                line.ApplyBlockedReason = null;
                awardedEmployees.Add(line.EmployeeId);
                scheduled.Add((change.Id, line.EmployeeId, line.NewBasic));
            }
            if (c.Status == CompCycleStatuses.Approved)
            {
                c.Status = CompCycleStatuses.Applied;
                c.AppliedAtUtc = now;
                c.AppliedByUserId = caller.UserId;
            }
            Touch(c, caller);
            await SaveAsync(ct);
            return c;
        }, ct);
        foreach (var (changeId, employeeId, newBasic) in scheduled)
            await AuditAsync(caller, "compensation.award.scheduled", nameof(EmployeeChangeRequest), changeId,
                new { cycleId = cycle.Id, employeeId, effectiveDate = cycle.EffectiveDate, newBasic }, ct);
        foreach (var (lineId, employeeId, reason) in blocked)
            await AuditAsync(caller, "compensation.award.apply_blocked", nameof(CompWorksheetLine), lineId, new { cycleId = cycle.Id, employeeId, reason }, ct);
        if (retroAcknowledged is { } ack)
            await AuditAsync(caller, "compensation.cycle.retro_acknowledged_at_apply", nameof(CompCycle), cycle.Id, new { periods = ack.Periods, retroCostCycle = ack.Cost }, ct);
        await AuditAsync(caller, "compensation.cycle.applied", nameof(CompCycle), cycle.Id, new { scheduled = scheduled.Count, blocked = blocked.Count }, ct);
        if (cycle.CreatedByUserId is Guid owner && owner != caller.UserId)
            await NotifyUserAsync(caller.TenantId, owner, CompEventCodes.CycleApplied, nameof(CompCycle), cycle.Id,
                $"Merit awards scheduled: {cycle.Name}",
                $"{scheduled.Count} award(s) take effect on {cycle.EffectiveDate:yyyy-MM-dd}; {blocked.Count} need attention.", ct);
        return await ProjectCycleAsync(cycle, ct);
    }

    /// <summary>Why an approved award cannot be scheduled now, or null. Never re-bases an award on new pay.</summary>
    private async Task<string?> ApplyBlockAsync(CompWorksheetLine line, MeritBasisLoader.CycleContext ctx, HashSet<int> awarded, CancellationToken ct)
    {
        if (awarded.Contains(line.EmployeeId)) return "This employee already has an award from this cycle on another worksheet.";
        if (!ctx.Employees.TryGetValue(line.EmployeeId, out var b)) return "The employee is no longer in this company.";
        if (MeritBasisLoader.SeparatedStatuses.Contains(b.Employee.Status)) return $"The employee's status is now {b.Employee.Status}.";
        // Eligibility is evaluated again at apply: a resignation, termination or offboarding opened after approval, or a
        // move out of an eligible grade or department, refuses the award instead of paying it.
        if (!b.Eligible) return $"No longer eligible for this cycle: {b.IneligibleReason}";
        if (b.Employee.GradeId != line.GradeId) return "The employee's grade changed after the award was approved, so the guideline and range it was approved against no longer apply. Plan it again in a new cycle or as a salary change.";
        if (b.Employee.DepartmentId != line.DepartmentId) return "The employee moved department after the award was approved, so it is no longer funded from the budget it was approved against.";
        if (b.SalaryRow is null || b.SalaryRow.Id != line.SalaryRowId || b.Basic != line.CurrentBasic || b.Gross != line.CurrentGross || b.PayCurrency != line.PayCurrency)
            return "Pay of record changed after this award was approved. The award was not re-based on the new pay; correct it through a separate salary change.";
        if (b.HasPendingSalaryChange) return "Another salary change for this employee is waiting for approval or for its effective date. Resolve it, then apply again.";
        if (b.HasLaterSalaryRow) return $"A salary change dated after {ctx.Cycle.EffectiveDate:yyyy-MM-dd} exists and would override this award.";
        if (b.ContractBound) return "Pay is bound to a contract renewal or an allowance basis; change it through Contract renewals.";
        try
        {
            await EmployeeSalaryBreakdownChanges.ValidateAsync(db, b.Employee,
                MeritLineCalculator.Package(b.SalaryRow, b.SalaryStructureCode, line.NewBasic, ctx.Cycle.EffectiveDate, line.PayCurrency), ct);
        }
        catch (InvalidOperationException ex) { return ex.Message; }
        return null;
    }

    /// <summary>
    /// Cancels the cycle. Before apply: closes pending worksheet approvals. After apply: withdraws every award that has
    /// not yet taken effect (its change request is cancelled under the same row lock the effective-date job takes);
    /// refused when any award already took effect — those are reversed one by one with a correcting change.
    /// </summary>
    public async Task<CompCycleDto> CancelAsync(CompCaller caller, Guid cycleId, CompCycleTransitionRequest request, CancellationToken ct)
    {
        RequireManage(caller);
        var reason = RequireReason(request.Reason);
        var withdrawn = new List<Guid>();
        var cycle = await InTransactionAsync(async () =>
        {
            withdrawn.Clear();
            var c = await LockCycleAsync(caller, cycleId, ct);
            RequireVersion(c, request.Version);
            if (c.Status == CompCycleStatuses.Cancelled) throw new CompRuleException("already_cancelled", "This cycle is already cancelled.");
            var sheets = await db.CompWorksheets.Where(w => w.CycleId == c.Id && w.Status != CompWorksheetStatuses.Cancelled).ToListAsync(ct);
            if (c.Status == CompCycleStatuses.Applied)
            {
                var changeIds = await db.CompWorksheetLines.Where(l => l.CycleId == c.Id && l.ChangeRequestId != null).Select(l => l.ChangeRequestId!.Value).ToListAsync(ct);
                var changes = await db.EmployeeChangeRequests.TagWith(RowLockingInterceptor.ForUpdateTag)
                    .Where(x => x.TenantId == c.TenantId && changeIds.Contains(x.Id)).ToListAsync(ct);
                var applied = changes.Count(x => x.Status == EmployeeChangeStatuses.ApprovedApplied);
                if (applied > 0)
                    throw new CompRuleException("awards_applied", $"{applied} award(s) already took effect, so the cycle cannot be cancelled. Reverse those awards one by one (a correcting salary change), and cancel only what has not taken effect.");
                foreach (var change in changes.Where(x => x.Status is EmployeeChangeStatuses.ApprovedPendingEffectiveDate or EmployeeChangeStatuses.PendingApproval))
                {
                    if (change.Status == EmployeeChangeStatuses.PendingApproval && change.ApprovalRequestId is Guid reviewId)
                        await CloseApprovalAsync(c.TenantId, reviewId, ct);
                    change.Status = EmployeeChangeStatuses.Cancelled;
                    change.RejectionReason = Truncate($"Merit cycle '{c.Name}' was cancelled before the award took effect: {reason}", 1000);
                    db.EmployeeHistories.Add(new EmployeeHistory
                    {
                        TenantId = c.TenantId, EmployeeId = change.EmployeeId, EventType = "SensitiveChangeCancelled",
                        FieldName = change.SensitiveFields, EffectiveDate = change.EffectiveDate, Reason = change.RejectionReason,
                        SnapshotJson = EmployeeChangeBaseline.SnapshotJson(change.Id, null), CreatedByUserId = caller.UserId,
                    });
                    withdrawn.Add(change.Id);
                }
            }
            foreach (var sheet in sheets)
            {
                if (sheet.Status == CompWorksheetStatuses.Submitted && sheet.ApprovalRequestId is Guid approvalId) await CloseApprovalAsync(c.TenantId, approvalId, ct);
                sheet.Status = CompWorksheetStatuses.Cancelled;
                sheet.Version++;
            }
            c.Status = CompCycleStatuses.Cancelled;
            c.CancelledAtUtc = DateTime.UtcNow;
            c.CancelledByUserId = caller.UserId;
            c.CancelReason = reason;
            Touch(c, caller);
            await SaveAsync(ct);
            return c;
        }, ct);
        foreach (var changeId in withdrawn)
            await AuditAsync(caller, "compensation.award.withdrawn", nameof(EmployeeChangeRequest), changeId, new { cycleId = cycle.Id, reason }, ct);
        await AuditAsync(caller, "compensation.cycle.cancelled", nameof(CompCycle), cycle.Id, new { reason, withdrawnAwards = withdrawn.Count }, ct);
        return await ProjectCycleAsync(cycle, ct);
    }

    /// <summary>A pending shared-approval row this module raised is closed with the same compare-and-swap the engine
    /// uses (DecisionVersion); a decision racing it loses cleanly.</summary>
    private async Task CloseApprovalAsync(Guid tenantId, Guid approvalId, CancellationToken ct)
    {
        var approval = await db.ApprovalRequests.FirstOrDefaultAsync(a => a.TenantId == tenantId && a.Id == approvalId, ct);
        if (approval is null || approval.Status != "Pending") return;
        approval.Status = "Cancelled";
        approval.CompletedAtUtc = DateTime.UtcNow;
        approval.DecisionVersion++;
    }

    /// <summary>
    /// Withdraws ONE scheduled award before it takes effect, without touching the rest of the cycle: the change is
    /// cancelled under its row lock (the effective-date job takes the same lock, so exactly one of them wins), any
    /// re-review it was waiting on is closed, and the line records why. An award already in pay is reversed instead.
    /// </summary>
    public async Task<CompApplyLineDto> WithdrawAwardAsync(CompCaller caller, Guid lineId, WithdrawCompAwardRequest request, CancellationToken ct)
    {
        RequireManage(caller);
        var reason = RequireReason(request.Reason);
        Guid changeId = Guid.Empty;
        var line = await InTransactionAsync(async () =>
        {
            var l = await db.CompWorksheetLines.FirstOrDefaultAsync(x => x.Id == lineId, ct) ?? throw new CompNotFoundException("Award not found.");
            var c = await LockCycleAsync(caller, l.CycleId, ct);
            if (l.ChangeRequestId is not Guid awardId) throw new CompRuleException("not_applied", "This line has no scheduled award to withdraw.");
            var change = await db.EmployeeChangeRequests.TagWith(RowLockingInterceptor.ForUpdateTag)
                .FirstAsync(x => x.TenantId == c.TenantId && x.Id == awardId, ct);
            if (change.Status == EmployeeChangeStatuses.ApprovedApplied || change.AppliedAtUtc is not null)
                throw new CompRuleException("award_effective", "This award already took effect. Reverse it instead (a correcting salary change).");
            if (change.Status is not (EmployeeChangeStatuses.ApprovedPendingEffectiveDate or EmployeeChangeStatuses.PendingApproval))
                throw new CompRuleException("award_closed", $"This award is '{change.Status}', so there is nothing to withdraw.");
            if (change.Status == EmployeeChangeStatuses.PendingApproval && change.ApprovalRequestId is Guid reviewId)
                await CloseApprovalAsync(c.TenantId, reviewId, ct);
            change.Status = EmployeeChangeStatuses.Cancelled;
            change.RejectionReason = Truncate($"Merit award from cycle '{c.Name}' withdrawn before it took effect: {reason}", 1000);
            db.EmployeeHistories.Add(new EmployeeHistory
            {
                TenantId = c.TenantId, EmployeeId = change.EmployeeId, EventType = "SensitiveChangeCancelled",
                FieldName = change.SensitiveFields, EffectiveDate = change.EffectiveDate, Reason = change.RejectionReason,
                SnapshotJson = EmployeeChangeBaseline.SnapshotJson(change.Id, null), CreatedByUserId = caller.UserId,
            });
            l.ApplyBlockedReason = Truncate($"Withdrawn by HR: {reason}", 1000);
            Touch(c, caller);
            await SaveAsync(ct);
            changeId = change.Id;
            return l;
        }, ct);
        await AuditAsync(caller, "compensation.award.withdrawn", nameof(EmployeeChangeRequest), changeId,
            new { lineId, cycleId = line.CycleId, employeeId = line.EmployeeId, reason, single = true }, ct);
        var name = await db.Employees.AsNoTracking().Where(e => e.Id == line.EmployeeId).Select(e => e.FullName).FirstOrDefaultAsync(ct) ?? "";
        return new CompApplyLineDto(line.Id, line.EmployeeId, name, line.ChangeRequestId, EmployeeChangeStatuses.Cancelled, line.ApplyBlockedReason, line.NewBasic, line.PayCurrency, null, null);
    }

    /// <summary>
    /// Reverses an award that already took effect: a correcting salaryBreakdown change (basic minus the award, allowances
    /// as they are now) raised on the ordinary EmployeeChangeRequest approval route — never a row deletion. Payroll and
    /// arrears consume it like any other approved change.
    /// </summary>
    public async Task<CompApplyLineDto> ReverseAwardAsync(CompCaller caller, Guid lineId, ReverseCompAwardRequest request, CancellationToken ct)
    {
        RequireManage(caller);
        var reason = RequireReason(request.Reason);
        if (request.EffectiveDate.Day != 1) throw new CompRuleException("effective_not_first", "A reversal takes effect on the 1st of a month.");
        Guid changeId = Guid.Empty;
        var lockedPeriods = new List<string>();
        var line = await InTransactionAsync(async () =>
        {
            var l = await db.CompWorksheetLines.FirstOrDefaultAsync(x => x.Id == lineId, ct) ?? throw new CompNotFoundException("Award not found.");
            var c = await LockCycleAsync(caller, l.CycleId, ct);
            if (l.ChangeRequestId is not Guid awardId) throw new CompRuleException("not_applied", "This line has no scheduled award to reverse.");
            var award = await db.EmployeeChangeRequests.AsNoTracking().FirstAsync(x => x.Id == awardId, ct);
            if (award.Status != EmployeeChangeStatuses.ApprovedApplied)
                throw new CompRuleException("award_not_effective", award.Status == EmployeeChangeStatuses.ApprovedPendingEffectiveDate
                    ? "This award has not taken effect yet. Cancel the cycle (or the pending change) instead of reversing it."
                    : $"This award is '{award.Status}', so there is nothing to reverse.");
            if (request.EffectiveDate < award.EffectiveDate) throw new CompRuleException("reversal_before_award", $"A reversal cannot take effect before the award did ({award.EffectiveDate:yyyy-MM-dd}).");
            lockedPeriods = await RunPeriodsFromAsync(c.TenantId, c.CompanyId, request.EffectiveDate, LockedRunStatuses, ct);
            if (lockedPeriods.Count > 0)
            {
                if (!request.AcknowledgeLockedPeriods)
                    throw new CompRuleException("locked_periods",
                        $"Payroll for {string.Join(", ", lockedPeriods)} is already approved or locked. A reversal dated {request.EffectiveDate:yyyy-MM-dd} recovers pay from those months; an approver must acknowledge it explicitly.");
                if (!caller.Can(CompPermissions.Approve))
                    throw new CompForbiddenException("approver_required", "A reversal into approved or locked payroll months must be acknowledged by someone with the compensation approver permission.");
            }
            if (l.ReversalChangeRequestId is Guid prior && await db.EmployeeChangeRequests.AnyAsync(x => x.Id == prior
                    && x.Status != EmployeeChangeStatuses.Rejected && x.Status != EmployeeChangeStatuses.Cancelled && x.Status != EmployeeChangeStatuses.Expired, ct))
                throw new CompRuleException("reversal_exists", "A reversal for this award is already in progress or applied.");
            var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.TenantId == c.TenantId && e.Id == l.EmployeeId && !e.IsDeleted, ct)
                ?? throw new CompNotFoundException("The employee is not available.");
            var row = await db.EmployeeSalaryStructures.AsNoTracking()
                .Where(s => s.TenantId == c.TenantId && s.EmployeeId == l.EmployeeId && s.IsActive && s.EffectiveDate <= request.EffectiveDate)
                .OrderByDescending(s => s.EffectiveDate).ThenByDescending(s => s.CreatedAtUtc).FirstOrDefaultAsync(ct)
                ?? throw new CompRuleException("no_salary_row", "There is no salary record to correct.");
            if (row.BasicSalary != l.NewBasic)
                throw new CompRuleException("pay_moved", $"Basic is now {row.BasicSalary:0.00}, not the awarded {l.NewBasic:0.00}; another change followed the award. Correct pay through the employee's salary instead.");
            var code = await db.SalaryStructures.AsNoTracking().Where(s => s.Id == row.SalaryStructureId).Select(s => s.Code).FirstOrDefaultAsync(ct);
            var package = MeritLineCalculator.Package(row, code, row.BasicSalary - l.ProratedIncreasePay, request.EffectiveDate, row.Currency);
            await ValidatePackageAsync(employee, package, ct);
            var reversalTitle = Truncate($"Merit award reversal - {employee.EmployeeCode} {employee.FullName} ({c.Name}): {reason}", 240);
            // Routed BEFORE the change row is written: a reversal nobody could decide is refused with nothing saved,
            // not left as a PendingApproval change with no approval behind it.
            try
            {
                await approvals.EnsureRoutableAsync(c.TenantId, new CreateApprovalRequest(null, nameof(EmployeeChangeRequest), Guid.Empty.ToString(),
                    reversalTitle, employee.Id, employee.CompanyId, "High"), caller.Context, ct);
            }
            catch (ApprovalRouteNotConfiguredException)
            {
                throw new CompRuleException("no_approval_route", "No approval workflow is configured for employee changes, so a reversal cannot be routed. Configure one under Approvals.");
            }
            catch (ApprovalRoutingException ex) { throw new CompRuleException(ex.Code, ex.Message); }
            var change = new EmployeeChangeRequest
            {
                TenantId = c.TenantId, EmployeeId = l.EmployeeId, RequestedByUserId = caller.UserId, EffectiveDate = request.EffectiveDate,
                SensitiveFields = EmployeeSalaryBreakdownChanges.Key, ProposedChangesJson = JsonSerializer.Serialize(package),
            };
            db.EmployeeChangeRequests.Add(change);
            await SaveAsync(ct);
            ApprovalRequestDto approval;
            try
            {
                approval = await approvals.CreateRequestAsync(c.TenantId, new CreateApprovalRequest(null, nameof(EmployeeChangeRequest), change.Id.ToString(),
                    reversalTitle, employee.Id, employee.CompanyId, "High"),
                    caller.Context, ct);
            }
            catch (ApprovalRouteNotConfiguredException)
            {
                throw new CompRuleException("no_approval_route", "No approval workflow is configured for employee changes, so a reversal cannot be routed. Configure one under Approvals.");
            }
            catch (ApprovalRoutingException ex) { throw new CompRuleException(ex.Code, ex.Message); }
            change.ApprovalRequestId = approval.Id;
            l.ReversalChangeRequestId = change.Id;
            await SaveAsync(ct);
            changeId = change.Id;
            return l;
        }, ct);
        await AuditAsync(caller, "compensation.award.reversal_requested", nameof(EmployeeChangeRequest), changeId,
            new { lineId, cycleId = line.CycleId, employeeId = line.EmployeeId, effectiveDate = request.EffectiveDate, reason,
                lockedPeriods, lockedPeriodsAcknowledged = lockedPeriods.Count > 0 && request.AcknowledgeLockedPeriods }, ct);
        var name = await db.Employees.AsNoTracking().Where(e => e.Id == line.EmployeeId).Select(e => e.FullName).FirstOrDefaultAsync(ct) ?? "";
        return new CompApplyLineDto(line.Id, line.EmployeeId, name, line.ChangeRequestId, EmployeeChangeStatuses.ApprovedApplied, null, line.NewBasic, line.PayCurrency,
            changeId, EmployeeChangeStatuses.PendingApproval);
    }

    private async Task ValidatePackageAsync(Employee employee, IReadOnlyDictionary<string, JsonElement> package, CancellationToken ct)
    {
        try { await EmployeeSalaryBreakdownChanges.ValidateAsync(db, employee, package, ct); }
        catch (InvalidOperationException ex) { throw new CompRuleException("invalid_package", ex.Message); }
    }

    // ───────────────────────────── projection ─────────────────────────────

    private async Task<CompCycleDto> ProjectCycleAsync(CompCycle cycle, CancellationToken ct)
    {
        var ctx = await _basis.LoadAsync(cycle, ct, await db.CompWorksheetLines.AsNoTracking()
            .Where(l => l.CycleId == cycle.Id && l.ChangeRequestId != null).Select(l => l.ChangeRequestId!.Value).ToListAsync(ct));
        var sheets = await db.CompWorksheets.AsNoTracking().Where(w => w.CycleId == cycle.Id).OrderBy(w => w.ManagerEmployeeId).ThenByDescending(w => w.IsPrimary).ThenBy(w => w.ScenarioName).ToListAsync(ct);
        var sheetIds = sheets.Select(s => s.Id).ToList();
        var lines = await db.CompWorksheetLines.AsNoTracking().Where(l => sheetIds.Contains(l.WorksheetId)).ToListAsync(ct);
        var primaryIds = sheets.Where(s => s.IsPrimary && s.Status != CompWorksheetStatuses.Cancelled).Select(s => s.Id).ToHashSet();
        var primaryLines = CurrentLines(lines.Where(l => primaryIds.Contains(l.WorksheetId)), sheets.ToDictionary(x => x.Id), ctx);
        var current = primaryLines.Select(l => l.Id).ToHashSet();
        var budgets = BudgetSummary(ctx, primaryLines);
        var managerNames = await NamesAsync(cycle.TenantId, sheets.Select(s => s.ManagerEmployeeId), ct);
        var overspentDepts = budgets.Where(b => b.Overspent).Select(b => b.DepartmentId).ToHashSet();

        var worksheets = sheets.Select(s =>
        {
            var mine = lines.Where(l => l.WorksheetId == s.Id).ToList();
            return new CompWorksheetSummaryDto(s.Id, s.ManagerEmployeeId, managerNames.GetValueOrDefault(s.ManagerEmployeeId) ?? $"#{s.ManagerEmployeeId}",
                s.ScenarioName, s.IsPrimary, s.Status, s.Revision, mine.Count, mine.Sum(l => l.ProratedIncrease),
                s.IsPrimary && mine.Any(l => l.ProratedIncrease > 0 && overspentDepts.Contains(l.DepartmentId ?? Guid.Empty)),
                s.ApprovalRequestId, s.DecisionComments, s.Version, s.CycleId);
        }).ToList();

        var managers = await ManagersAsync(ctx, ct);
        var liveOwners = sheets.Where(s => s.IsPrimary && s.Status != CompWorksheetStatuses.Cancelled).Select(s => s.ManagerEmployeeId).ToHashSet();
        var planningOpen = cycle.Status != CompCycleStatuses.Draft;
        var uncovered = ctx.Employees.Values.Where(b => b.Eligible).Select(b =>
            {
                var owner = ctx.CoveringOwner(b);
                string? reason = owner is not int o ? "No manager on record, so no worksheet covers this employee."
                    : o == b.Employee.Id ? "The employee is recorded as their own manager."
                    : !managers.ContainsKey(o) && !ctx.Assignments.ContainsKey(b.Employee.Id) ? "The manager on record is not an active employee."
                    : planningOpen && !liveOwners.Contains(o) ? $"{managers.GetValueOrDefault(o) ?? "The manager"} has no worksheet in this cycle (for example, the reporting line changed after planning opened)."
                    : null;
                return reason is null ? null : new CompExcludedDto(b.Employee.Id, b.Employee.EmployeeCode, b.Employee.FullName, reason,
                    owner, owner is int ow ? managers.GetValueOrDefault(ow) : null,
                    OwnerCanBeOpened: planningOpen && owner is int oo && oo != b.Employee.Id && managers.ContainsKey(oo) && !liveOwners.Contains(oo));
            })
            .Where(x => x is not null).Select(x => x!).OrderBy(x => x.Name).ToList();
        var ineligible = ctx.Employees.Values.Where(b => !b.Eligible)
            .Select(b => new CompExcludedDto(b.Employee.Id, b.Employee.EmployeeCode, b.Employee.FullName, b.IneligibleReason ?? "Not eligible."))
            .OrderBy(x => x.Name).ToList();

        var filters = CompEligibilityFilters.Parse(cycle.EligibilityFiltersJson);
        var payCurrencies = ctx.Employees.Values.Where(b => b.Eligible).Select(b => b.PayCurrency).Distinct().OrderBy(x => x).ToList();
        var totals = new CompTotalsDto(budgets.Sum(b => b.EligibleCount), budgets.Sum(b => b.EligibleBasicCycle), budgets.Sum(b => b.Pool),
            budgets.Sum(b => b.Proposed), budgets.Sum(b => b.Remaining), MeritMath.PercentUsed(budgets.Sum(b => b.Proposed), budgets.Sum(b => b.Pool)),
            primaryLines.Count(l => l.ProratedIncreasePay > 0));

        CompApplySummaryDto? apply = null;
        if (cycle.Status is CompCycleStatuses.Approved or CompCycleStatuses.Applied or CompCycleStatuses.Cancelled)
        {
            var approvedIds = sheets.Where(s => s.IsPrimary).Select(s => s.Id).ToHashSet();
            var awardLines = lines.Where(l => approvedIds.Contains(l.WorksheetId) && l.ProratedIncreasePay > 0 && (current.Contains(l.Id) || l.ChangeRequestId != null)).ToList();
            var changeIds = awardLines.SelectMany(l => new[] { l.ChangeRequestId, l.ReversalChangeRequestId }).Where(x => x != null).Select(x => x!.Value).ToList();
            var statuses = await db.EmployeeChangeRequests.AsNoTracking().Where(x => changeIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Status, ct);
            var names = await NamesAsync(cycle.TenantId, awardLines.Select(l => l.EmployeeId), ct);
            var applyLines = awardLines.OrderBy(l => names.GetValueOrDefault(l.EmployeeId)).Select(l => new CompApplyLineDto(l.Id, l.EmployeeId, names.GetValueOrDefault(l.EmployeeId) ?? "",
                l.ChangeRequestId, l.ChangeRequestId is Guid id ? statuses.GetValueOrDefault(id) : null, l.ApplyBlockedReason, l.NewBasic, l.PayCurrency,
                l.ReversalChangeRequestId, l.ReversalChangeRequestId is Guid rid ? statuses.GetValueOrDefault(rid) : null)).ToList();
            apply = new CompApplySummaryDto(awardLines.Count,
                applyLines.Count(l => l.ChangeStatus == EmployeeChangeStatuses.ApprovedPendingEffectiveDate),
                applyLines.Count(l => l.ChangeStatus == EmployeeChangeStatuses.ApprovedApplied),
                applyLines.Count(l => l.ChangeStatus == EmployeeChangeStatuses.PendingApproval),
                applyLines.Count(l => l.ChangeStatus is EmployeeChangeStatuses.Cancelled or EmployeeChangeStatuses.Expired or EmployeeChangeStatuses.Rejected),
                applyLines.Count(l => l.ChangeRequestId is null), applyLines);
        }

        var blockers = cycle.Status == CompCycleStatuses.Draft ? PlanningBlockers(ctx)
            : cycle.Status == CompCycleStatuses.Planning ? await OwnersWithoutCompanyAccessAsync(cycle, sheets.Where(x => x.IsPrimary && CompWorksheetStatuses.IsEditable(x.Status)).Select(x => x.ManagerEmployeeId).Distinct().ToList(), ct)
            : [];
        var retro = cycle.Status is CompCycleStatuses.Cancelled ? null : await RetroAsync(cycle, totals.Proposed, ct);
        return new CompCycleDto(cycle.Id, cycle.CompanyId, ctx.Company.LegalNameEn, cycle.Name, cycle.Status, cycle.PerformancePeriodStart, cycle.PerformancePeriodEnd,
            cycle.EffectiveDate, cycle.Currency, cycle.PayComponent, cycle.HireDateCutoff,
            cycle.EligibleStatuses.Split(',', StringSplitOptions.RemoveEmptyEntries), filters.IncludeGradeIds, filters.ExcludeGradeIds,
            filters.IncludeDepartmentIds, filters.ExcludeDepartmentIds, cycle.MinimumEligibleDays, cycle.ProrationRule, cycle.EquityIncludesNationality, cycle.Version,
            budgets, ctx.Guidelines.Select(g => new CompGuidelineDto(g.Id, g.Rating, g.CompaRatioMin, g.CompaRatioMax, g.MinPercent, g.TargetPercent, g.MaxPercent)).ToList(),
            ctx.Fx.Select(f => new CompFxDto(f.Id, f.FromCurrency, f.Rate, f.Source, f.RateDate, f.EnteredByName, f.EnteredAtUtc)).ToList(),
            payCurrencies, worksheets, uncovered, ineligible, blockers, totals, AllowedActions(cycle), apply, cycle.ApprovedAtUtc, cycle.AppliedAtUtc, cycle.CancelReason,
            retro);
    }

    /// <summary>
    /// Worksheet owners whose login is limited to other companies cannot open this cycle's worksheet (the company boundary
    /// is not crossed for them). HR sees who and what to do. A login with no company grants recorded is not flagged.
    /// </summary>
    private async Task<List<CompBlockerDto>> OwnersWithoutCompanyAccessAsync(CompCycle cycle, IReadOnlyCollection<int> ownerIds, CancellationToken ct)
    {
        if (ownerIds.Count == 0 || cycle.CompanyId is not Guid companyId) return [];
        const string why = "merit planning: whether a worksheet owner's login can open the cycle's company";
        var owners = await Zayra.Api.Infrastructure.Data.ScopedBypass.NullableTenantWide(db.Employees, cycle.TenantId, why).AsNoTracking()
            .Where(e => ownerIds.Contains(e.Id) && e.UserAccountId != null).Select(e => new { e.Id, e.FullName, UserId = e.UserAccountId!.Value }).ToListAsync(ct);
        if (owners.Count == 0) return [];
        var userIds = owners.Select(o => o.UserId).ToList();
        var groupUsers = (await db.Users.AsNoTracking().Where(u => u.TenantId == cycle.TenantId && userIds.Contains(u.Id) && u.IsGroupScope).Select(u => u.Id).ToListAsync(ct)).ToHashSet();
        var grants = await db.UserEntityAccesses.AsNoTracking().Where(g => g.TenantId == cycle.TenantId && userIds.Contains(g.UserId) && g.IsActive)
            .Select(g => new { g.UserId, g.CompanyId, g.GrantMode }).ToListAsync(ct);
        var companyName = await db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.LegalNameEn).FirstOrDefaultAsync(ct) ?? "this company";
        return owners.Where(o => !groupUsers.Contains(o.UserId)
                                 && grants.Any(g => g.UserId == o.UserId)
                                 && !grants.Any(g => g.UserId == o.UserId && (g.GrantMode != EntityGrantModes.SelectedCompanies || g.CompanyId == companyId)))
            .Select(o => new CompBlockerDto("owner_company_access",
                $"{o.FullName} owns a worksheet here but their login cannot open {companyName}. Give them access to {companyName} in User Management, complete the worksheet for them, or assign the team to another worksheet owner."))
            .ToList();
    }

    private static readonly string[] PaidRunStatuses = ["Processed", "PendingFinanceReview", "Approved", "Locked", "Paid"];
    private static readonly string[] LockedRunStatuses = ["Approved", "Locked", "Paid"];

    /// <summary>Payroll periods (yyyy-MM) of a company already run at or after a month, in the given run states.</summary>
    private async Task<List<string>> RunPeriodsFromAsync(Guid tenantId, Guid? companyId, DateOnly from, string[] statuses, CancellationToken ct)
    {
        var key = from.Year * 12 + from.Month - 1;
        var runs = await db.PayrollRuns.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.CompanyId == companyId && statuses.Contains(r.Status) && r.Year * 12 + (r.Month - 1) >= key)
            .Select(r => new { r.Year, r.Month }).Distinct().ToListAsync(ct);
        return runs.OrderBy(r => r.Year).ThenBy(r => r.Month).Select(r => $"{r.Year:0000}-{r.Month:00}").ToList();
    }

    /// <summary>
    /// A past effective date costs arrears: every payroll month already run from the effective month at the old rate is
    /// owed the monthly increase. Shown on the cycle (create, review, approve); approving needs an explicit acknowledgement.
    /// </summary>
    private async Task<CompRetroDto?> RetroAsync(CompCycle c, decimal monthlyDeltaCycle, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var monthsInPast = Math.Max(0, (today.Year * 12 + today.Month) - (c.EffectiveDate.Year * 12 + c.EffectiveDate.Month));
        var paid = await RunPeriodsFromAsync(c.TenantId, c.CompanyId, c.EffectiveDate, PaidRunStatuses, ct);
        if (monthsInPast == 0 && paid.Count == 0) return null;
        return new CompRetroDto(monthsInPast, paid, monthlyDeltaCycle, MeritMath.Money(paid.Count * monthlyDeltaCycle), paid.Count > 0);
    }

    private static IReadOnlyList<string> AllowedActions(CompCycle c) => c.Status switch
    {
        CompCycleStatuses.Draft => ["edit", "budgets", "guidelines", "fx", "open_planning", "cancel"],
        CompCycleStatuses.Planning => ["budgets", "fx", "start_review", "reopen", "cancel"],
        CompCycleStatuses.Review => ["approve", "reopen", "cancel"],
        CompCycleStatuses.Approved => ["apply", "cancel"],
        CompCycleStatuses.Applied => ["apply", "cancel", "reverse"],
        _ => [],
    };

    private static List<CompBlockerDto> PlanningBlockers(MeritBasisLoader.CycleContext ctx)
    {
        var blockers = new List<CompBlockerDto>();
        var eligible = ctx.Employees.Values.Where(b => b.Eligible).ToList();
        if (eligible.Count == 0) blockers.Add(new("no_eligible", "No employee is eligible under these rules."));
        if (ctx.Guidelines.Count == 0) blockers.Add(new("no_guidelines", "Add the guideline matrix (at least one rating row)."));
        if (ctx.Budgets.Count == 0) blockers.Add(new("no_budgets", "Set a budget for at least one department."));
        var missingFx = eligible.Where(b => b.FxRate is null).Select(b => b.PayCurrency).Distinct().OrderBy(x => x).ToList();
        if (missingFx.Count > 0) blockers.Add(new("fx_missing", $"Enter an FX rate for {string.Join(", ", missingFx)}: some eligible employees are paid in it. A missing rate is never guessed."));
        return blockers;
    }

    /// <summary>
    /// Per-department pool and utilisation. Lines = the persisted lines of every primary, live worksheet.
    /// <para><b>Frozen after planning.</b> While the cycle is Draft or Planning the eligible basic is read live from pay of
    /// record. From Review onward it is the basis each line was approved on (its snapshot), so the pool an approver saw
    /// does not move when the awards themselves — or anything else — later change pay of record.</para>
    /// </summary>
    /// <summary>
    /// ONE line per employee per cycle, chosen deterministically from the live primary worksheets. An employee can sit on
    /// two of them — e.g. they moved manager after the first worksheet was approved and HR opened one for the new manager.
    /// Budgets, review checks and apply must all count the same line: an award already scheduled, else the worksheet of the
    /// manager who covers the employee now, else the approved one, else the most recently changed.
    /// </summary>
    internal static List<CompWorksheetLine> CurrentLines(IEnumerable<CompWorksheetLine> lines, IReadOnlyDictionary<Guid, CompWorksheet> sheets,
        MeritBasisLoader.CycleContext ctx)
    {
        bool Live(CompWorksheetLine l) => sheets.TryGetValue(l.WorksheetId, out var w) && w.IsPrimary && w.Status != CompWorksheetStatuses.Cancelled;
        bool Covering(CompWorksheetLine l) => ctx.Employees.TryGetValue(l.EmployeeId, out var b) && ctx.CoveringOwner(b) == sheets[l.WorksheetId].ManagerEmployeeId;
        return lines.Where(Live).GroupBy(l => l.EmployeeId)
            .Select(g => g.OrderByDescending(l => l.ChangeRequestId != null)
                .ThenByDescending(Covering)
                .ThenByDescending(l => sheets[l.WorksheetId].Status == CompWorksheetStatuses.Approved)
                .ThenByDescending(l => sheets[l.WorksheetId].UpdatedAtUtc ?? sheets[l.WorksheetId].CreatedAtUtc)
                .ThenBy(l => l.WorksheetId)
                .First())
            .ToList();
    }

    internal static List<CompBudgetDto> BudgetSummary(MeritBasisLoader.CycleContext ctx, IReadOnlyList<CompWorksheetLine> lines)
    {
        var frozen = ctx.Cycle.Status is not (CompCycleStatuses.Draft or CompCycleStatuses.Planning);
        if (frozen)
        {
            // The approved basis: one line per employee (primary sheets only reach here), department as snapshotted.
            var snapshot = lines.GroupBy(l => l.EmployeeId).Select(g => g.First()).ToList();
            var depts = snapshot.Select(l => l.DepartmentId ?? Guid.Empty).Concat(ctx.Budgets.Select(b => b.DepartmentId)).Distinct().ToList();
            return depts.Select(dept =>
            {
                var members = snapshot.Where(l => (l.DepartmentId ?? Guid.Empty) == dept).ToList();
                var eligibleBasic = members.Where(l => l.CurrentBasicCycle != null).Sum(l => l.CurrentBasicCycle!.Value);
                var budget = ctx.Budgets.FirstOrDefault(b => b.DepartmentId == dept);
                var pool = budget is null ? 0m : budget.PoolType == CompBudgetPoolTypes.FixedAmount ? budget.FixedAmount ?? 0m : MeritMath.Pool(eligibleBasic, budget.PoolPercent ?? 0m);
                var proposed = members.Sum(l => l.ProratedIncrease);
                return new CompBudgetDto(dept, dept == Guid.Empty ? "No department" : ctx.DepartmentNames.GetValueOrDefault(dept) ?? "Department",
                    budget is not null, budget?.PoolType, budget?.PoolPercent, budget?.FixedAmount, members.Count, eligibleBasic, pool, proposed, pool - proposed,
                    MeritMath.PercentUsed(proposed, pool), proposed > pool);
            }).OrderBy(b => b.DepartmentName).ToList();
        }
        var eligible = ctx.Employees.Values.Where(b => b.Eligible).ToList();
        var departments = eligible.Select(b => b.Employee.DepartmentId ?? Guid.Empty)
            .Concat(ctx.Budgets.Select(b => b.DepartmentId)).Concat(lines.Select(l => l.DepartmentId ?? Guid.Empty)).Distinct().ToList();
        var result = new List<CompBudgetDto>();
        foreach (var dept in departments)
        {
            var members = eligible.Where(b => (b.Employee.DepartmentId ?? Guid.Empty) == dept).ToList();
            var eligibleBasic = members.Where(b => b.BasicCycle != null).Sum(b => b.BasicCycle!.Value);
            var budget = ctx.Budgets.FirstOrDefault(b => b.DepartmentId == dept);
            var pool = budget is null ? 0m
                : budget.PoolType == CompBudgetPoolTypes.FixedAmount ? budget.FixedAmount ?? 0m
                : MeritMath.Pool(eligibleBasic, budget.PoolPercent ?? 0m);
            var proposed = lines.Where(l => (l.DepartmentId ?? Guid.Empty) == dept).Sum(l => l.ProratedIncrease);
            result.Add(new CompBudgetDto(dept, dept == Guid.Empty ? "No department" : ctx.DepartmentNames.GetValueOrDefault(dept) ?? "Department",
                budget is not null, budget?.PoolType, budget?.PoolPercent, budget?.FixedAmount, members.Count, eligibleBasic, pool, proposed, pool - proposed,
                MeritMath.PercentUsed(proposed, pool), proposed > pool));
        }
        return result.OrderBy(b => b.DepartmentName).ToList();
    }

    // ───────────────────────────── shared helpers ─────────────────────────────

    /// <summary>Installs the default two-step worksheet workflow (HR Manager → Finance Approver) when the tenant has no
    /// active one for CompensationWorksheet. Insert-if-absent; the tenant can edit it under Approvals.</summary>
    private async Task EnsureWorksheetWorkflowAsync(CompCaller caller, CancellationToken ct)
    {
        var tenantId = caller.TenantId;
        if (await db.ApprovalWorkflows.AnyAsync(w => w.TenantId == tenantId && w.IsActive && w.EntityName == CompensationApprovalSync.EntityName, ct)) return;
        if (await db.ApprovalWorkflows.AnyAsync(w => w.TenantId == tenantId && w.Code == WorksheetWorkflowCode, ct)) return;
        var workflow = new ApprovalWorkflow
        {
            TenantId = tenantId, Code = WorksheetWorkflowCode, Name = "Default merit worksheet approval",
            EntityName = CompensationApprovalSync.EntityName, IsDefault = true, IsActive = true,
        };
        workflow.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantId, WorkflowId = workflow.Id, StepOrder = 1, StepName = "HR review", ApproverType = "Role", ApproverRole = "HR Manager", IsFinalStep = false });
        workflow.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantId, WorkflowId = workflow.Id, StepOrder = 2, StepName = "Finance approval", ApproverType = "Role", ApproverRole = "Finance Approver", IsFinalStep = true });
        db.ApprovalWorkflows.Add(workflow);
        await AuditAsync(caller, "compensation.workflow.installed", nameof(ApprovalWorkflow), workflow.Id, new { workflow.Code }, ct, staged: true);
    }

    /// <summary>Managers referenced by the company's eligible employees, read tenant-wide (a manager may sit in another company).</summary>
    private async Task<Dictionary<int, string>> ManagersAsync(MeritBasisLoader.CycleContext ctx, CancellationToken ct)
    {
        var ids = ctx.Employees.Values.Where(b => b.Employee.ManagerEmployeeId != null).Select(b => b.Employee.ManagerEmployeeId!.Value).Distinct().ToList();
        return (await Zayra.Api.Infrastructure.Data.ScopedBypass.NullableTenantWide(db.Employees, ctx.Cycle.TenantId,
                    "Merit planning: a worksheet is owned by the employee's manager, who may be employed by another company of the same tenant. Tenant re-applied; names and ids only.")
                .AsNoTracking().Where(e => ids.Contains(e.Id) && !e.IsDeleted && !MeritBasisLoader.SeparatedStatuses.Contains(e.Status))
                .Select(e => new { e.Id, e.FullName }).ToListAsync(ct))
            .ToDictionary(e => e.Id, e => e.FullName);
    }

    /// <summary>A worksheet owner's team: eligible employees covered by them (explicit HR assignment first, else the
    /// reporting line), never the owner themself.</summary>
    private static IEnumerable<EmployeeBasis> TeamOf(MeritBasisLoader.CycleContext ctx, int managerId) =>
        ctx.Employees.Values.Where(b => b.Eligible && b.Employee.Id != managerId && ctx.CoveringOwner(b) == managerId).OrderBy(b => b.Employee.FullName);

    private static void StampAssignment(CompWorksheetLine line, MeritBasisLoader.CycleContext ctx, int ownerId) =>
        line.AssignedByUserId = ctx.Assignments.TryGetValue(line.EmployeeId, out var a) && a.Owner == ownerId ? a.AssignedBy : null;

    private async Task<Dictionary<int, string>> NamesAsync(Guid tenantId, IEnumerable<int> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return (await Zayra.Api.Infrastructure.Data.ScopedBypass.NullableTenantWide(db.Employees, tenantId,
                    "Merit planning: display names of worksheet owners and award holders across the tenant's companies. Tenant re-applied; names only.")
                .AsNoTracking().Where(e => list.Contains(e.Id)).Select(e => new { e.Id, e.FullName }).ToListAsync(ct))
            .ToDictionary(e => e.Id, e => e.FullName);
    }

    private static CompWorksheetLine NewLine(CompCycle cycle, CompWorksheet sheet) => new()
    {
        TenantId = cycle.TenantId, CompanyId = cycle.CompanyId, CycleId = cycle.Id, WorksheetId = sheet.Id,
    };

    private async Task<CompCycle> FindCycleAsync(CompCaller caller, Guid id, bool track, CancellationToken ct)
    {
        var q = db.CompCycles.Where(c => c.TenantId == caller.TenantId && c.Id == id);
        return await (track ? q : q.AsNoTracking()).FirstOrDefaultAsync(ct) ?? throw new CompNotFoundException("Merit cycle not found.");
    }

    private async Task<CompCycle> LockCycleAsync(CompCaller caller, Guid id, CancellationToken ct) =>
        await db.CompCycles.TagWith(RowLockingInterceptor.ForUpdateTag).Where(c => c.TenantId == caller.TenantId && c.Id == id).FirstOrDefaultAsync(ct)
        ?? throw new CompNotFoundException("Merit cycle not found.");

    private static void RequireManage(CompCaller caller)
    {
        if (!caller.CanManage) throw new CompForbiddenException("forbidden", "Managing merit cycles needs the compensation manager permission.");
    }

    private static void RequireVersion(CompCycle cycle, int version)
    {
        if (cycle.Version != version) throw new CompConflictException("This cycle was changed by someone else. Reload it and try again.");
    }

    private static string RequireReason(string? reason)
    {
        var clean = (reason ?? string.Empty).Trim();
        if (clean.Length is < 3 or > 1000) throw new CompRuleException("reason_required", "Give a reason (3 to 1000 characters).");
        return clean;
    }

    private static void Touch(CompCycle cycle, CompCaller caller)
    {
        cycle.Version++;
        cycle.UpdatedAtUtc = DateTime.UtcNow;
        cycle.UpdatedByUserId = caller.UserId;
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new CompConflictException("Someone else changed this record at the same time. Reload it and try again."); }
    }

    /// <summary>One unit of work under the retrying execution strategy (ExecutionStrategyLintTests).</summary>
    private async Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        var attempt = 0;
        return await strategy.ExecuteAsync(async () =>
        {
            if (attempt++ > 0) db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var result = await work();
            await tx.CommitAsync(ct);
            return result;
        });
    }

    /// <summary>Audit after the business commit (IAuditService), or staged into the current unit of work.</summary>
    private async Task AuditAsync(CompCaller caller, string action, string entity, Guid id, object? metadata, CancellationToken ct, bool staged = false)
    {
        var json = metadata is null ? null : JsonSerializer.Serialize(metadata, Json);
        if (staged)
        {
            db.AuditLogs.Add(new Zayra.Api.Domain.Entities.AuditLog
            {
                TenantId = caller.TenantId, UserId = caller.UserId, Action = action, EntityName = entity, EntityId = id.ToString(),
                Metadata = json, CreatedAtUtc = DateTime.UtcNow,
            });
            return;
        }
        await audit.WriteAsync(action, entity, id.ToString(), caller.Context, json, ct);
    }

    private async Task NotifyEmployeeAsync(Guid tenantId, int employeeId, string eventCode, string entity, Guid entityId, string title, string message, CancellationToken ct)
    {
        var userId = await Zayra.Api.Infrastructure.Data.ScopedBypass.NullableTenantWide(db.Employees, tenantId,
                "Merit planning notification: the worksheet owner's login, whatever company they are employed by. Tenant re-applied.")
            .AsNoTracking().Where(e => e.Id == employeeId).Select(e => e.UserAccountId).FirstOrDefaultAsync(ct);
        if (userId is Guid u) await NotifyUserAsync(tenantId, u, eventCode, entity, entityId, title, message, ct);
    }

    private async Task NotifyUserAsync(Guid tenantId, Guid userId, string eventCode, string entity, Guid entityId, string title, string message, CancellationToken ct)
    {
        if (notifications is null) return;
        try
        {
            await notifications.EnqueueAsync(new NotificationRequest
            {
                TenantId = tenantId, UserId = userId, EventCode = eventCode, EntityName = entity, EntityId = entityId.ToString(), Title = title, Message = message,
                Variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Subject"] = title, ["Body"] = message },
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Post-commit and best-effort, like every other notification: the audit row is the record.
            log?.LogWarning("Compensation notification {EventCode} for {Entity} {EntityId} failed ({ErrorType}).", eventCode, entity, entityId, ex.GetType().Name);
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
