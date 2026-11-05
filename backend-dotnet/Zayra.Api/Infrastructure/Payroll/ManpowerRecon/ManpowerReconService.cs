using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll.ManpowerRecon;

/// <summary>Who is asking, as the service needs to know it. Built by the controller from the request principal.</summary>
public sealed record ReconCaller(
    Guid? UserId, string UserName, bool HoldsApprove, EntityScopeContext EntityScope, DataScope DataScope);

public enum ReconBuildStatus { Ok, NotFound, PriorRunInvalid }

public sealed record ReconEmployeeInfo(
    int EmployeeId, string Code, string Name, decimal PriorGross, decimal CurrentGross, bool InPrior, bool InCurrent,
    bool FinalWageMonth, bool Prorated, DateOnly? JoiningDate);

/// <summary>Everything one reconciliation build produced: the contract DTO and the in-memory detail the drill-down and export read.</summary>
public sealed class ReconBuild
{
    public required ReconDto Dto { get; init; }
    public required PayrollRun Run { get; init; }
    public PayrollRun? Prior { get; init; }
    public int ProcessVersion { get; init; }
    public int? PriorProcessVersion { get; init; }
    public required string SnapshotHash { get; init; }
    public required ReconThresholds Thresholds { get; init; }
    public bool SignoffRequired { get; init; } = true;
    public bool Partial { get; init; }
    public VolumeBridge? Bridge { get; init; }
    public IReadOnlyList<ReconCell> Cells { get; init; } = Array.Empty<ReconCell>();
    public IReadOnlyList<ReconLine> PriorLines { get; init; } = Array.Empty<ReconLine>();
    public IReadOnlyList<ReconLine> CurrentLines { get; init; } = Array.Empty<ReconLine>();
    public IReadOnlyDictionary<int, ReconEmployeeInfo> Employees { get; init; } = new Dictionary<int, ReconEmployeeInfo>();
    public IReadOnlySet<int> FlaggedEmployeeIds { get; init; } = new HashSet<int>();
    public IReadOnlyDictionary<int, string> HoldOutReasons { get; init; } = new Dictionary<int, string>();
    public IReadOnlyList<ReconRequiredExplanationDto> RequiredExplanations { get; init; } = Array.Empty<ReconRequiredExplanationDto>();
    public IReadOnlyList<FsPlacement> FsPlacements { get; init; } = Array.Empty<FsPlacement>();
    public IReadOnlyList<FsSettlementFact> FsSettlements { get; init; } = Array.Empty<FsSettlementFact>();
    public IReadOnlyList<FsLineFact> FsLines { get; init; } = Array.Empty<FsLineFact>();
    public string CurrentPeriod { get; init; } = string.Empty;
    public string? PriorPeriod { get; init; }
    public PayrollRunReconSignoff? Signoff { get; init; }
    public PayrollRunReconSignoff? ActiveSignoff => Signoff is { Status: ReconSignoffStatuses.Active } s ? s : null;
}

public sealed record ReconBuildResult(ReconBuildStatus Status, ReconBuild? Build);

/// <summary>What the drill-down is asked for (the query string of GET recon/employees).</summary>
public sealed record ReconDrillQuery(
    string Section, string? Category, string? Kind, string? ItemCode, string? Bucket, int Page, int PageSize);

/// <summary>
/// Builds the manpower and value reconciliation of one payroll run against the prior month. Read-only: it derives the
/// whole bridge from the run's slips and payslip lines every time, and writes nothing. The caller's company scope is
/// checked here (a run of a company the caller cannot access is simply not found) and a department- or team-scoped caller
/// gets a partial bridge over their own people that cannot be signed.
/// </summary>
public sealed class ManpowerReconService
{
    private readonly ZayraDbContext _db;

    public ManpowerReconService(ZayraDbContext db) => _db = db;

    public async Task<ReconBuildResult> BuildAsync(
        Guid tenantId, Guid runId, ReconCaller caller, Guid? priorRunIdOverride, CancellationToken ct)
    {
        var run = await _db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == runId, ct);
        if (run is null || !caller.EntityScope.CanAccessCompany(run.CompanyId))
            return new ReconBuildResult(ReconBuildStatus.NotFound, null);

        var currency = await ResolveCurrencyAsync(tenantId, run.CompanyId, ct);
        var rules = await CompanyPayrollRules.ResolveReconAsync(_db, tenantId, run.CompanyId, ct);
        var thresholds = ReconThresholds.FromRules(rules.VariancePct, rules.VarianceMinAmount);
        var partial = caller.DataScope.Level != DataScopeLevel.Organization && caller.DataScope.AllowedEmployeeIds is not null;
        var period = $"{run.Year}-{run.Month:D2}";
        var signoff = await LatestSignoffAsync(tenantId, run.Id, ct);

        if (!PayrollRunTypes.IsPeriodOwning(run.RunType))
            return NotApplicable(run, period, currency, thresholds, partial, caller, signoff, rules.SignoffRequired);

        var selection = await PriorRunSelector.SelectAsync(_db, tenantId, run, priorRunIdOverride, ct);
        if (selection.OverrideError is not null) return new ReconBuildResult(ReconBuildStatus.PriorRunInvalid, null);
        var prior = selection.Prior;
        var full = selection.Mode == ReconModes.Full;
        var priorPeriod = prior is null ? null : $"{prior.Year}-{prior.Month:D2}";

        var periodStart = new DateOnly(run.Year, run.Month, 1);
        var periodEnd = periodStart.AddMonths(1).AddDays(-1);

        // ── Slips (the caller's visible employees only) ──────────────────────────────────────────
        var currentSlips = await VisibleSlipsAsync(tenantId, run.Id, caller.DataScope, ct);
        var priorSlips = prior is null ? new List<PayrollSlip>() : await VisibleSlipsAsync(tenantId, prior.Id, caller.DataScope, ct);
        var issues = new List<ReconIssueDto>();
        foreach (var dup in currentSlips.Concat(priorSlips).GroupBy(s => (s.RunId, s.EmployeeId)).Where(g => g.Count() > 1))
            issues.Add(new ReconIssueDto("duplicateSlip", $"Employee {dup.First().EmployeeCode} has {dup.Count()} payslips in one run."));

        var currentByEmp = currentSlips.GroupBy(s => s.EmployeeId).ToDictionary(g => g.Key, g => g.OrderBy(s => s.Id).First());
        var priorByEmp = priorSlips.GroupBy(s => s.EmployeeId).ToDictionary(g => g.Key, g => g.OrderBy(s => s.Id).First());
        var allIds = currentByEmp.Keys.Union(priorByEmp.Keys).ToList();

        // ── Lines ────────────────────────────────────────────────────────────────────────────────
        var visibleCurrent = currentByEmp.Keys.ToHashSet();
        var visiblePrior = priorByEmp.Keys.ToHashSet();
        var currentEarnings = await LoadEarningsAsync(tenantId, run.Id, visibleCurrent, ct);
        var currentDeductions = await LoadDeductionsAsync(tenantId, run.Id, visibleCurrent, ct);
        var priorEarnings = prior is null ? new List<PayrollEarning>() : await LoadEarningsAsync(tenantId, prior.Id, visiblePrior, ct);
        var priorDeductions = prior is null ? new List<PayrollDeduction>() : await LoadDeductionsAsync(tenantId, prior.Id, visiblePrior, ct);
        var currentLines = ToLines(currentEarnings, currentDeductions);
        var priorLines = ToLines(priorEarnings, priorDeductions);

        // I3 / I7: lines against the slip register, per employee, inside the documented tolerance.
        CheckLinesAgainstRegister(currentSlips, currentEarnings, currentDeductions, "current", issues);
        CheckLinesAgainstRegister(priorSlips, priorEarnings, priorDeductions, "prior", issues);
        var registerDifference =
            Math.Abs(priorEarnings.Sum(e => e.Amount) - priorSlips.Sum(s => s.GrossSalary))
            + Math.Abs(priorDeductions.Where(d => !d.IsEmployerContribution).Sum(d => d.Amount) - priorSlips.Sum(s => s.Deductions))
            + Math.Abs(currentEarnings.Sum(e => e.Amount) - currentSlips.Sum(s => s.GrossSalary))
            + Math.Abs(currentDeductions.Where(d => !d.IsEmployerContribution).Sum(d => d.Amount) - currentSlips.Sum(s => s.Deductions));
        var employerLines = currentDeductions.Where(d => d.IsEmployerContribution).Sum(d => d.Amount);
        if (Math.Abs(employerLines - currentSlips.Sum(s => s.EmployerStatutoryTotal)) > 0.01m + 0.005m * currentSlips.Count)
            issues.Add(new ReconIssueDto("employerCost", "The employer cost lines do not add up to the employer cost on the payslips."));

        // ── Employees and the facts the classifier needs ─────────────────────────────────────────
        var employees = await LoadEmployeesAsync(tenantId, allIds, ct);
        var offboardings = await _db.EmployeeOffboardings.AsNoTracking()
            .Where(o => o.TenantId == tenantId && allIds.Contains(o.EmployeeId) && o.Status != "Cancelled")
            .ToListAsync(ct);
        var settlements = await LoadSettlementsAsync(tenantId, run, caller.DataScope, ct);
        var accruedByEmployee = settlements.Where(s => FinalSettlementStatuses.HasAccrued(s.Status))
            .GroupBy(s => s.EmployeeId).ToDictionary(g => g.Key, g => g.Select(s => s.OffboardingId).ToHashSet());
        var newIds = allIds.Where(id => currentByEmp.ContainsKey(id) && !priorByEmp.ContainsKey(id)).ToList();
        var earlierSlipIds = full ? await EarlierWageSlipEmployeesAsync(tenantId, run, newIds, ct) : new HashSet<int>();
        var otherCompanyPriorIds = full ? await PriorPeriodOtherCompanyEmployeesAsync(tenantId, run, newIds, ct) : new HashSet<int>();
        var selections = await _db.PayrollRunEmployeeSelections.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.PayrollRunId == run.Id && s.Outcome == PayrollRunSelectionOutcomes.Excluded)
            .Select(s => new { s.EmployeeId, s.Reason }).ToListAsync(ct);
        var excludedIds = selections.Select(s => s.EmployeeId).ToHashSet();
        var holdOutReasons = selections.GroupBy(s => s.EmployeeId).ToDictionary(g => g.Key, g => g.Select(s => s.Reason).OrderBy(r => r, StringComparer.Ordinal).First());

        var facts = new List<ReconEmployeeFacts>();
        foreach (var id in allIds.OrderBy(i => i))
        {
            currentByEmp.TryGetValue(id, out var cur);
            priorByEmp.TryGetValue(id, out var pri);
            employees.TryGetValue(id, out var emp);
            var joining = emp is null ? (DateOnly?)null : DateOnly.FromDateTime(emp.JoiningDate);
            var empOffboardings = offboardings.Where(o => o.EmployeeId == id).ToList();
            facts.Add(new ReconEmployeeFacts
            {
                EmployeeId = id, InPrior = pri is not null, InCurrent = cur is not null, EmployeeKnown = emp is not null,
                Status = emp?.Status, JoiningDate = joining, OriginalJoiningDate = emp?.OriginalJoiningDate,
                CurrentFinalWageMonth = cur?.IsFinalWageMonth ?? false,
                CurrentProrated = cur?.ProrationFactor is { } cf && cf < 1m,
                PriorFinalWageMonth = pri?.IsFinalWageMonth ?? false,
                EmployeeInOtherCompanyNow = emp?.CompanyId is Guid ec && run.CompanyId is Guid rc && ec != rc,
                InPriorRunOfOtherCompany = otherCompanyPriorIds.Contains(id),
                OffboardedBeforeJoining = joining is { } j && empOffboardings.Any(o => o.LastWorkingDay < j),
                OffboardedBeforePeriod = empOffboardings.Any(o => o.LastWorkingDay < periodStart),
                EarlierWageSlipInTenant = earlierSlipIds.Contains(id),
                ExcludedBySelection = excludedIds.Contains(id),
                HasAccruedSettlement = accruedByEmployee.TryGetValue(id, out var accruedOffboardings)
                    && empOffboardings.Any(o => accruedOffboardings.Contains(o.Id)),
            });
        }

        // ── Volume ───────────────────────────────────────────────────────────────────────────────
        VolumeBridge? bridge = null;
        ReconVolumeDto volume;
        var memoOffCycle = await OffCycleMemoAsync(tenantId, run, prior, caller.DataScope, ct);
        if (full)
        {
            bridge = ManpowerReconClassifier.Bridge(facts, periodStart, periodEnd);
            volume = new ReconVolumeDto(bridge.Opening, bridge.Closing, VolumeLines(bridge), bridge.Ties,
                new ReconVolumeMemoDto(bridge.FinalWageMonthInCurrent, memoOffCycle));
            if (!bridge.Ties) issues.Add(new ReconIssueDto("headcountBridge", "Opening plus adds minus exits does not equal closing."));
        }
        else
        {
            volume = new ReconVolumeDto(0, visibleCurrent.Count, Array.Empty<ReconVolumeLineDto>(), true,
                new ReconVolumeMemoDto(facts.Count(f => f.InCurrent && f.CurrentFinalWageMonth), memoOffCycle));
        }

        // ── Value ────────────────────────────────────────────────────────────────────────────────
        var activeSignoff = signoff is { Status: ReconSignoffStatuses.Active } a ? a : null;
        var itemExplanations = activeSignoff?.Explanations.Where(e => e.Scope == ReconExplanationScopes.Item)
            .ToDictionary(e => e.Key, e => e.Text, StringComparer.Ordinal);
        ValueBridgeResult value;
        if (full)
        {
            var categories = bridge!.Assignments.ToDictionary(kv => kv.Key, kv => kv.Value.Category);
            value = ValueBridgeMath.Build(new ValueBridgeInput
            {
                PriorLines = priorLines, CurrentLines = currentLines, Categories = categories,
                PriorWitness = priorByEmp.ToDictionary(kv => kv.Key, kv => Witness(kv.Value)),
                CurrentWitness = currentByEmp.ToDictionary(kv => kv.Key, kv => Witness(kv.Value)),
                Hints = await LoadHintsAsync(tenantId, run.CompanyId, ct),
                Thresholds = thresholds, ItemExplanations = itemExplanations,
            });
            foreach (var v in value.Violations) issues.Add(new ReconIssueDto("valueBridge", v));
        }
        else
        {
            value = ValueBridgeMath.Composition(currentLines);
        }

        // ── Final settlement ─────────────────────────────────────────────────────────────────────
        var lastWorkingDays = offboardings.GroupBy(o => o.EmployeeId).ToDictionary(g => g.Key, g => g.Max(o => o.LastWorkingDay));
        var leavers = bridge is null ? new List<FsLeaverFact>() : facts
            .Where(f => bridge.Assignments.TryGetValue(f.EmployeeId, out var c) && c.Category == ReconCategory.Leaver)
            .Select(f =>
            {
                var slip = priorByEmp[f.EmployeeId];
                return new FsLeaverFact(f.EmployeeId, slip.EmployeeCode, slip.EmployeeName, bridge.Assignments[f.EmployeeId].FsMissing,
                    lastWorkingDays.TryGetValue(f.EmployeeId, out var lwd) ? lwd : null);
            }).OrderBy(l => l.EmployeeCode, StringComparer.Ordinal).ToList();
        var fsLines = await LoadSettlementLinesAsync(tenantId, settlements.Select(s => s.SettlementId).ToList(), ct);
        var fs = FinalSettlementBlock.Build(new FinalSettlementBlockInput
        {
            Settlements = settlements,
            Lines = fsLines,
            CurrentPeriod = period,
            PriorPeriod = priorPeriod,
            PeriodStart = periodStart,
            Categories = bridge is null ? new Dictionary<int, ReconCategory>() : bridge.Assignments.ToDictionary(kv => kv.Key, kv => kv.Value.Category),
            CurrentFinalWageEmployees = currentByEmp.Where(kv => kv.Value.IsFinalWageMonth).Select(kv => kv.Key).ToHashSet(),
            Leavers = leavers,
        });
        issues.AddRange(fs.Issues);

        // ── Employees (drill-down, flags) ────────────────────────────────────────────────────────
        var info = new Dictionary<int, ReconEmployeeInfo>();
        foreach (var id in allIds)
        {
            currentByEmp.TryGetValue(id, out var cur);
            priorByEmp.TryGetValue(id, out var pri);
            var any = cur ?? pri!;
            employees.TryGetValue(id, out var emp);
            info[id] = new ReconEmployeeInfo(id, any.EmployeeCode, any.EmployeeName, pri?.GrossSalary ?? 0m, cur?.GrossSalary ?? 0m,
                pri is not null, cur is not null, cur?.IsFinalWageMonth ?? false, cur?.ProrationFactor is { } f && f < 1m,
                emp is null ? null : DateOnly.FromDateTime(emp.JoiningDate));
        }
        // An employee is flagged when their own gross moved by more than the threshold. This is the count a signer restates.
        var flaggedEmployees = full
            ? info.Values.Where(i => i.InPrior && i.InCurrent && thresholds.IsFlagged(i.PriorGross, i.CurrentGross - i.PriorGross))
                .Select(i => i.EmployeeId).ToHashSet()
            : new HashSet<int>();

        var required = new List<ReconRequiredExplanationDto>();
        if (full)
        {
            foreach (var (kind, item) in value.Sections.SelectMany(s => s.Items.Select(i => (s.Kind, Item: i))).Where(x => x.Item.Flagged))
                required.Add(new ReconRequiredExplanationDto(ReconExplanationScopes.Item, ReconItemDto.ExplanationKey(kind, item.Code), item.Label));
            foreach (var (id, c) in bridge!.Assignments.Where(kv => kv.Value.Category is ReconCategory.NotPaid or ReconCategory.Unexplained)
                         .OrderBy(kv => info[kv.Key].Code, StringComparer.Ordinal))
                required.Add(new ReconRequiredExplanationDto(ReconExplanationScopes.Employee, id.ToString(CultureInfo.InvariantCulture),
                    $"{info[id].Code} {info[id].Name} ({ReconCategories.Name(c.Category)})"));
        }

        // A fixed order (code, then text): the findings are read from rows the database returns in no particular order, and the
        // reviewed figures (and their fingerprint) must not change when the same findings come back in another order.
        var integrity = new ReconIntegrityDto(issues.Count == 0,
            issues.OrderBy(i => i.Code, StringComparer.Ordinal).ThenBy(i => i.Message, StringComparer.Ordinal).ToList());

        // ── Scope and the sign-off ───────────────────────────────────────────────────────────────
        // canSignOff speaks for SCOPE only. Permission, run status, maker-checker and integrity are separate blockers the screen
        // and the sign-off endpoint already name.
        var scope = new ReconScopeDto(caller.DataScope.Level.ToString(), partial, !partial,
            partial ? new[] { ReconScopeReasons.RequiresOrganizationScope } : Array.Empty<string>());

        var dto = new ReconDto(
            run.Id, period, selection.Mode, prior?.Id, priorPeriod, full ? null : selection.LastRunPeriod,
            selection.PriorNotFinal, selection.PriorVoided, currency, run.ProcessVersion, string.Empty, rules.SignoffRequired, scope, thresholds,
            volume, new ReconValueDto(value.Sections, value.Headline, value.Ties.Ok, registerDifference), fs.Block, integrity, null);
        var hash = ReconSnapshotHash.Compute(dto, run.ProcessVersion, prior?.ProcessVersion);
        dto = dto with { SnapshotHash = hash, Signoff = SignoffDto(signoff) };

        return Ok(new ReconBuild
        {
            Dto = dto, Run = run, Prior = prior, ProcessVersion = run.ProcessVersion, PriorProcessVersion = prior?.ProcessVersion,
            SnapshotHash = hash, Thresholds = thresholds, SignoffRequired = rules.SignoffRequired, Partial = partial,
            Bridge = bridge, Cells = value.Cells, PriorLines = priorLines, CurrentLines = currentLines, Employees = info,
            FlaggedEmployeeIds = flaggedEmployees, HoldOutReasons = holdOutReasons, RequiredExplanations = required,
            FsPlacements = fs.Placements, FsSettlements = settlements, FsLines = fsLines, CurrentPeriod = period, PriorPeriod = priorPeriod,
            Signoff = signoff,
        });
    }

    private static ReconBuildResult Ok(ReconBuild build) => new(ReconBuildStatus.Ok, build);

    // ── NotApplicable (off-cycle) ────────────────────────────────────────────────────────────────

    private static ReconBuildResult NotApplicable(
        PayrollRun run, string period, string currency, ReconThresholds thresholds, bool partial, ReconCaller caller,
        PayrollRunReconSignoff? signoff, bool signoffRequired)
    {
        var zero = new ReconBridgeRowDto(0m, new ReconAddsDto(0, 0, 0, 0, 0), new ReconExitsDto(0, 0, 0, 0),
            new ReconContinuingDto(0, 0, 0, 0, 0, 0, 0, 0), 0m, 0m, null);
        var empty = new ReconOffCycleTotalsDto(0, 0m);
        var unhashed = new ReconDto(run.Id, period, ReconModes.NotApplicable, null, null, null, false, false, currency,
            run.ProcessVersion, string.Empty, signoffRequired,
            new ReconScopeDto(caller.DataScope.Level.ToString(), partial, false, Array.Empty<string>()), thresholds,
            new ReconVolumeDto(0, 0, Array.Empty<ReconVolumeLineDto>(), true,
                new ReconVolumeMemoDto(0, new ReconOffCycleMemoDto(empty, empty))),
            new ReconValueDto(
                new[]
                {
                    new ReconValueSectionDto(ReconSectionKinds.Earning, Array.Empty<ReconItemDto>()),
                    new ReconValueSectionDto(ReconSectionKinds.Deduction, Array.Empty<ReconItemDto>()),
                    new ReconValueSectionDto(ReconSectionKinds.Employer, Array.Empty<ReconItemDto>()),
                },
                new ReconHeadlineDto(zero, zero, zero, zero), true, 0m),
            new ReconFinalSettlementDto(Array.Empty<ReconFsCellDto>(), Array.Empty<ReconFsComponentDto>(),
                Array.Empty<ReconLeaverWithoutFsDto>(), true),
            new ReconIntegrityDto(true, Array.Empty<ReconIssueDto>()), null);
        var hash = ReconSnapshotHash.Compute(unhashed, run.ProcessVersion, null);
        return Ok(new ReconBuild
        {
            Dto = unhashed with { SnapshotHash = hash, Signoff = SignoffDto(signoff) }, Run = run, ProcessVersion = run.ProcessVersion,
            SnapshotHash = hash, Thresholds = thresholds, SignoffRequired = signoffRequired, Partial = partial, Signoff = signoff,
            CurrentPeriod = period,
        });
    }

    // ── Loading ──────────────────────────────────────────────────────────────────────────────────

    private async Task<string> ResolveCurrencyAsync(Guid tenantId, Guid? companyId, CancellationToken ct)
    {
        if (companyId.HasValue)
        {
            var companyCurrency = await _db.Companies.AsNoTracking()
                .Where(c => c.TenantId == tenantId && c.Id == companyId.Value && !c.IsDeleted)
                .Select(c => c.DefaultCurrency).FirstOrDefaultAsync(ct);
            if (!string.IsNullOrWhiteSpace(companyCurrency)) return companyCurrency.Trim().ToUpperInvariant();
        }
        return (await _db.ResolveTenantCurrencyAsync(tenantId, ct)).Trim().ToUpperInvariant();
    }

    private async Task<PayrollRunReconSignoff?> LatestSignoffAsync(Guid tenantId, Guid runId, CancellationToken ct)
    {
        var rows = await _db.PayrollRunReconSignoffs.AsNoTracking().Include(s => s.Explanations)
            .Where(s => s.TenantId == tenantId && s.PayrollRunId == runId).ToListAsync(ct);
        return rows.OrderByDescending(s => s.Status == ReconSignoffStatuses.Active).ThenByDescending(s => s.SignedOffAtUtc).FirstOrDefault();
    }

    public static ReconSignoffDto? SignoffDto(PayrollRunReconSignoff? s) =>
        s is null ? null : new ReconSignoffDto(s.Id.ToString(), s.Status, s.SignedOffByName, DateTime.SpecifyKind(s.SignedOffAtUtc, DateTimeKind.Utc), s.SnapshotHash, s.SupersededReason);

    /// <summary>A run's slips limited to the employees the caller may see - the same rule as the payroll workspace's slip list.</summary>
    private async Task<List<PayrollSlip>> VisibleSlipsAsync(Guid tenantId, Guid runId, DataScope scope, CancellationToken ct)
    {
        var query = _db.PayrollSlips.AsNoTracking().Where(x => x.TenantId == tenantId && x.RunId == runId);
        if (scope.Level != DataScopeLevel.Organization && scope.AllowedEmployeeIds is { } allowed)
            query = query.Where(x => allowed.Contains(x.EmployeeId));
        return await query.ToListAsync(ct);
    }

    private Task<List<PayrollEarning>> LoadEarningsAsync(Guid tenantId, Guid runId, HashSet<int> employees, CancellationToken ct) =>
        _db.PayrollEarnings.AsNoTracking().Where(e => e.TenantId == tenantId && e.PayrollRunId == runId && employees.Contains(e.EmployeeId)).ToListAsync(ct);

    private Task<List<PayrollDeduction>> LoadDeductionsAsync(Guid tenantId, Guid runId, HashSet<int> employees, CancellationToken ct) =>
        _db.PayrollDeductions.AsNoTracking().Where(d => d.TenantId == tenantId && d.PayrollRunId == runId && employees.Contains(d.EmployeeId)).ToListAsync(ct);

    private static List<ReconLine> ToLines(List<PayrollEarning> earnings, List<PayrollDeduction> deductions) =>
        earnings.Select(e => new ReconLine(e.EmployeeId, ReconSectionKinds.Earning, e.ComponentCode, e.ComponentName, e.Source, e.Amount))
            .Concat(deductions.Select(d => new ReconLine(d.EmployeeId,
                d.IsEmployerContribution ? ReconSectionKinds.Employer : ReconSectionKinds.Deduction, d.ComponentCode, d.ComponentName, d.Source, d.Amount)))
            .ToList();

    private static void CheckLinesAgainstRegister(
        List<PayrollSlip> slips, List<PayrollEarning> earnings, List<PayrollDeduction> deductions, string which, List<ReconIssueDto> issues)
    {
        foreach (var slip in slips.GroupBy(s => s.EmployeeId).Select(g => g.OrderBy(s => s.Id).First()))
        {
            var mismatch = PayslipLineInvariant.Check(slip,
                earnings.Where(e => e.EmployeeId == slip.EmployeeId), deductions.Where(d => d.EmployeeId == slip.EmployeeId));
            if (mismatch is not null)
                issues.Add(new ReconIssueDto("lineRegister",
                    $"The {which} payslip lines of {slip.EmployeeCode} do not add up to the payslip (lines {mismatch.EarningLines:0.00} / {mismatch.DeductionLines:0.00}, payslip {mismatch.Gross:0.00} / {mismatch.Deductions:0.00})."));
        }
    }

    /// <summary>The few employee facts the classification reads; names, contact details and pay stay in the table.</summary>
    private sealed record EmployeeFacts(int Id, string Status, DateTime JoiningDate, DateOnly? OriginalJoiningDate, Guid? CompanyId);

    private async Task<Dictionary<int, EmployeeFacts>> LoadEmployeesAsync(Guid tenantId, List<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new Dictionary<int, EmployeeFacts>();
        // Tenant-wide on purpose: an employee who has since moved to another company is hidden from a company-scoped caller by the
        // company filter, which would turn a transfer-out into an unexplained exit. Only the ids already on this run's visible slips are read.
        var rows = await ScopedBypass.NullableTenantWide(_db.Employees, tenantId,
                "Reconciliation classifies employees on this run's slips, including ones who have since moved company.")
            .AsNoTracking().Where(e => ids.Contains(e.Id))
            .Select(e => new EmployeeFacts(e.Id, e.Status, e.JoiningDate, e.OriginalJoiningDate, e.CompanyId)).ToListAsync(ct);
        return rows.ToDictionary(e => e.Id);
    }

    private async Task<HashSet<int>> EarlierWageSlipEmployeesAsync(Guid tenantId, PayrollRun run, List<int> candidates, CancellationToken ct)
    {
        if (candidates.Count == 0) return new HashSet<int>();
        // Any company of the tenant: "has this person ever been paid before" is a question about the person. Ids only; no pay is read.
        var earlierRuns = ScopedBypass.TenantWide(_db.PayrollRuns, tenantId,
                "Reconciliation asks whether an employee was ever paid by any company of the tenant; only ids are read.")
            .Where(r => (r.RunType == PayrollRunTypes.Regular || r.RunType == PayrollRunTypes.Replacement)
                     && r.Status != "Voided" && r.Status != "Draft" && r.Id != run.Id
                     && (r.Year < run.Year || (r.Year == run.Year && r.Month < run.Month)))
            .Select(r => r.Id);
        var ids = await ScopedBypass.TenantWide(_db.PayrollSlips, tenantId,
                "Reconciliation asks whether an employee was ever paid by any company of the tenant; only ids are read.")
            .Where(s => candidates.Contains(s.EmployeeId) && earlierRuns.Contains(s.RunId))
            .Select(s => s.EmployeeId).Distinct().ToListAsync(ct);
        return ids.ToHashSet();
    }

    private async Task<HashSet<int>> PriorPeriodOtherCompanyEmployeesAsync(Guid tenantId, PayrollRun run, List<int> candidates, CancellationToken ct)
    {
        if (candidates.Count == 0 || run.CompanyId is null) return new HashSet<int>();
        var (year, month) = run.Month == 1 ? (run.Year - 1, 12) : (run.Year, run.Month - 1);
        var otherRuns = ScopedBypass.TenantWide(_db.PayrollRuns, tenantId,
                "A transfer-in is an employee paid last month by another company of the tenant; only ids are read.")
            .Where(r => r.CompanyId != run.CompanyId && r.Year == year && r.Month == month
                     && (r.RunType == PayrollRunTypes.Regular || r.RunType == PayrollRunTypes.Replacement)
                     && r.Status != "Voided" && r.Status != "Draft")
            .Select(r => r.Id);
        var ids = await ScopedBypass.TenantWide(_db.PayrollSlips, tenantId,
                "A transfer-in is an employee paid last month by another company of the tenant; only ids are read.")
            .Where(s => candidates.Contains(s.EmployeeId) && otherRuns.Contains(s.RunId))
            .Select(s => s.EmployeeId).Distinct().ToListAsync(ct);
        return ids.ToHashSet();
    }

    private async Task<List<FsSettlementFact>> LoadSettlementsAsync(Guid tenantId, PayrollRun run, DataScope scope, CancellationToken ct)
    {
        var (py, pm) = run.Month == 1 ? (run.Year - 1, 12) : (run.Year, run.Month - 1);
        var periods = new[] { $"{run.Year}-{run.Month:D2}", $"{py}-{pm:D2}" };
        var query = _db.EmployeeFinalSettlements.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.CompanyId == run.CompanyId && s.GlPeriod != null && periods.Contains(s.GlPeriod));
        if (scope.Level != DataScopeLevel.Organization && scope.AllowedEmployeeIds is { } allowed)
            query = query.Where(s => allowed.Contains(s.EmployeeId));
        var rows = await query.ToListAsync(ct);
        return rows.Select(s => new FsSettlementFact(s.Id, s.EmployeeId, s.EmployeeCode, s.EmployeeName, s.GlPeriod, s.Status,
            s.LastWorkingDay, s.GrossPayable, s.TotalDeductions) { OffboardingId = s.OffboardingId }).ToList();
    }

    private async Task<List<FsLineFact>> LoadSettlementLinesAsync(Guid tenantId, List<Guid> settlementIds, CancellationToken ct)
    {
        if (settlementIds.Count == 0) return new List<FsLineFact>();
        var rows = await _db.FinalSettlementLines.AsNoTracking()
            .Where(l => l.TenantId == tenantId && settlementIds.Contains(l.SettlementId)).ToListAsync(ct);
        return rows.Select(l => new FsLineFact(l.SettlementId, l.ComponentCode, l.LineType, l.Amount, l.Source)).ToList();
    }

    private async Task<Dictionary<string, ComponentHint>> LoadHintsAsync(Guid tenantId, Guid? companyId, CancellationToken ct)
    {
        var rows = await _db.PayComponents.AsNoTracking()
            .Where(c => c.TenantId == tenantId && !c.IsDeleted && (c.CompanyId == null || c.CompanyId == companyId))
            .ToListAsync(ct);
        return rows.GroupBy(c => c.Code)
            .ToDictionary(g => g.Key, g =>
            {
                var best = g.OrderByDescending(c => c.CompanyId != null).ThenBy(c => c.Id).First();
                return new ComponentHint(best.Code, best.ProviderKey, best.CalcMethod);
            });
    }

    private async Task<ReconOffCycleMemoDto> OffCycleMemoAsync(Guid tenantId, PayrollRun run, PayrollRun? prior, DataScope scope, CancellationToken ct)
    {
        async Task<ReconOffCycleTotalsDto> For(int year, int month)
        {
            var runs = await _db.PayrollRuns.AsNoTracking()
                .Where(r => r.TenantId == tenantId && r.CompanyId == run.CompanyId && r.Year == year && r.Month == month
                         && r.RunType != PayrollRunTypes.Regular && r.RunType != PayrollRunTypes.Replacement
                         && r.Status != "Voided" && r.Status != "Draft")
                .Select(r => r.Id).ToListAsync(ct);
            if (runs.Count == 0) return new ReconOffCycleTotalsDto(0, 0m);
            var slips = _db.PayrollSlips.AsNoTracking().Where(s => s.TenantId == tenantId && runs.Contains(s.RunId));
            if (scope.Level != DataScopeLevel.Organization && scope.AllowedEmployeeIds is { } allowed)
                slips = slips.Where(s => allowed.Contains(s.EmployeeId));
            var gross = (await slips.Select(s => s.GrossSalary).ToListAsync(ct)).Sum();
            return new ReconOffCycleTotalsDto(runs.Count, gross);
        }
        var (py, pm) = run.Month == 1 ? (run.Year - 1, 12) : (run.Year, run.Month - 1);
        return new ReconOffCycleMemoDto(await For(prior?.Year ?? py, prior?.Month ?? pm), await For(run.Year, run.Month));
    }

    private static ReconSlipWitness Witness(PayrollSlip s) =>
        new(s.EmployeeId, s.FullBasicSalary, s.FullHousingAllowance, s.FullTransportAllowance, s.ProrationFactor);

    private static IReadOnlyList<ReconVolumeLineDto> VolumeLines(VolumeBridge bridge) =>
        ReconCategories.Adds.Concat(ReconCategories.Exits).Select(c => new ReconVolumeLineDto(
            ReconCategories.Name(c), ReconCategories.Kind(c), bridge.Count(c),
            c == ReconCategory.Leaver ? bridge.FsApproved : null,
            c == ReconCategory.Leaver ? bridge.FsMissing : null)).ToList();

    // ── Drill-down ───────────────────────────────────────────────────────────────────────────────

    public const int MaxPageSize = 200;

    /// <summary>
    /// The employees behind a number, paged (at most <see cref="MaxPageSize"/>), ordered by employee code. Returns null for a
    /// query that names nothing real (the endpoint answers 400).
    /// <list type="bullet">
    /// <item><c>volume</c>: <c>category</c> is a movement category, CONTINUING, OPENING (everyone in the prior run) or CLOSING (everyone in this one).</item>
    /// <item><c>value</c>: with <c>kind</c> and <c>itemCode</c>, the employees behind one item: <c>bucket</c> prior or current (who holds an amount),
    /// a continuing driver (package ... other), or a movement <c>category</c> (NEW_JOINER ... UNEXPLAINED). With <c>bucket=flagged</c> alone, the employees
    /// whose own gross moved more than the threshold - the number a signer restates.</item>
    /// <item><c>finalSettlement</c>: <c>category</c> is a cell, or <c>itemCode</c> a settlement component code.</item>
    /// </list>
    /// </summary>
    public static ReconEmployeePageDto? DrillDown(ReconBuild build, ReconDrillQuery q)
    {
        var page = Math.Max(1, q.Page);
        var pageSize = Math.Clamp(q.PageSize <= 0 ? 50 : q.PageSize, 1, MaxPageSize);
        List<ReconEmployeeRowDto>? rows = (q.Section?.Trim().ToLowerInvariant()) switch
        {
            "volume" => VolumeRows(build, q.Category),
            "value" => ValueRows(build, q),
            "finalsettlement" => SettlementRows(build, q),
            _ => null,
        };
        if (rows is null) return null;
        rows = rows.OrderBy(r => r.EmployeeCode, StringComparer.Ordinal).ThenBy(r => r.EmployeeId).ToList();
        return new ReconEmployeePageDto(rows.Skip((page - 1) * pageSize).Take(pageSize).ToList(), rows.Count, page, pageSize);
    }

    private static string CategoryOf(ReconBuild build, int id) =>
        build.Bridge is not null && build.Bridge.Assignments.TryGetValue(id, out var c) ? ReconCategories.Name(c.Category) : string.Empty;

    private static List<ReconEmployeeRowDto>? VolumeRows(ReconBuild build, string? category)
    {
        var name = category?.Trim().ToUpperInvariant();
        IEnumerable<ReconEmployeeInfo> pick;
        switch (name)
        {
            case "OPENING":
                pick = build.Employees.Values.Where(i => i.InPrior);
                break;
            case "CLOSING":
                pick = build.Employees.Values.Where(i => i.InCurrent);
                break;
            case "CONTINUING":
                pick = build.Employees.Values.Where(i => i.InPrior && i.InCurrent);
                break;
            default:
            {
                var wanted = ReconCategories.Parse(name);
                if (wanted is null) return null;
                if (build.Bridge is null) return new List<ReconEmployeeRowDto>();
                pick = build.Bridge.Assignments.Where(kv => kv.Value.Category == wanted).Select(kv => build.Employees[kv.Key]);
                break;
            }
        }
        return pick.Select(i =>
        {
            var cat = CategoryOf(build, i.EmployeeId);
            return new ReconEmployeeRowDto(i.EmployeeId, i.Code, i.Name, cat.Length == 0 ? null : cat, NoteFor(build, i),
                i.InPrior ? i.PriorGross : null, i.InCurrent ? i.CurrentGross : null,
                i.InPrior && i.InCurrent ? i.CurrentGross - i.PriorGross : null);
        }).ToList();
    }

    private static string? NoteFor(ReconBuild build, ReconEmployeeInfo i)
    {
        var notes = new List<string>();
        if (build.Bridge is not null && build.Bridge.Assignments.TryGetValue(i.EmployeeId, out var c))
        {
            switch (c.Category)
            {
                case ReconCategory.Leaver:
                    notes.Add(c.FsApproved ? "Final settlement accrued" : "No final settlement yet");
                    break;
                case ReconCategory.NotPaid:
                    notes.Add(build.HoldOutReasons.TryGetValue(i.EmployeeId, out var why) ? $"Held out of this run: {why}" : "Not paid this month");
                    break;
                case ReconCategory.NewJoiner or ReconCategory.LateJoiner or ReconCategory.Rehire when i.JoiningDate is { } joined:
                    notes.Add($"Joined {joined:yyyy-MM-dd}");
                    break;
            }
        }
        if (i.FinalWageMonth) notes.Add("Final wage month");
        if (i.Prorated) notes.Add("Prorated");
        return notes.Count == 0 ? null : string.Join("; ", notes);
    }

    private static List<ReconEmployeeRowDto>? ValueRows(ReconBuild build, ReconDrillQuery q)
    {
        var bucket = q.Bucket?.Trim();
        if (string.IsNullOrWhiteSpace(q.ItemCode))
        {
            if (!string.Equals(bucket, "flagged", StringComparison.OrdinalIgnoreCase)) return null;
            return build.FlaggedEmployeeIds.Select(id =>
            {
                var i = build.Employees[id];
                var delta = i.CurrentGross - i.PriorGross;
                var pct = ReconThresholds.DeltaPct(i.PriorGross, delta);
                return new ReconEmployeeRowDto(id, i.Code, i.Name, CategoryOf(build, id), pct is { } p ? $"{p:+0.##;-0.##}% on gross" : null,
                    i.PriorGross, i.CurrentGross, delta);
            }).ToList();
        }

        bool KindMatches(string kind) => q.Kind is null || string.Equals(kind, q.Kind, StringComparison.OrdinalIgnoreCase);
        var itemCode = q.ItemCode!;
        decimal Amount(IReadOnlyList<ReconLine> lines, int id) =>
            lines.Where(l => l.EmployeeId == id && l.Code == itemCode && KindMatches(l.Kind)).Sum(l => l.Amount);
        bool Holds(IReadOnlyList<ReconLine> lines, int id) => lines.Any(l => l.EmployeeId == id && l.Code == itemCode && KindMatches(l.Kind));
        ReconEmployeeRowDto Row(int id, decimal? delta)
        {
            var i = build.Employees[id];
            return new ReconEmployeeRowDto(id, i.Code, i.Name, CategoryOf(build, id), NoteFor(build, i),
                Holds(build.PriorLines, id) ? Amount(build.PriorLines, id) : null,
                Holds(build.CurrentLines, id) ? Amount(build.CurrentLines, id) : null, delta);
        }

        if (string.Equals(bucket, "prior", StringComparison.OrdinalIgnoreCase))
            return build.PriorLines.Where(l => l.Code == itemCode && KindMatches(l.Kind)).Select(l => l.EmployeeId).Distinct()
                .Where(build.Employees.ContainsKey).Select(id => Row(id, null)).ToList();
        if (string.Equals(bucket, "current", StringComparison.OrdinalIgnoreCase))
            return build.CurrentLines.Where(l => l.Code == itemCode && KindMatches(l.Kind)).Select(l => l.EmployeeId).Distinct()
                .Where(build.Employees.ContainsKey).Select(id => Row(id, null)).ToList();

        var cells = build.Cells.Where(c => c.Code == itemCode && KindMatches(c.Kind));
        if (!string.IsNullOrWhiteSpace(bucket))
        {
            var driver = bucket.ToLowerInvariant();
            if (!Enum.GetValues<ReconDriver>().Any(d => ReconDriverMap.BucketName(d) == driver)) return null;
            cells = cells.Where(c => c.Group == "continuing" && c.Bucket == driver);
        }
        else if (!string.IsNullOrWhiteSpace(q.Category))
        {
            var category = ReconCategories.Parse(q.Category);
            if (category is null || !(ReconCategories.IsAdd(category.Value) || ReconCategories.IsExit(category.Value))) return null;
            var group = ReconCategories.IsAdd(category.Value) ? "adds" : "exits";
            var key = ReconCategories.IsAdd(category.Value) ? ValueBridgeMath.AddBucket(category.Value) : ValueBridgeMath.ExitBucket(category.Value);
            cells = cells.Where(c => c.Group == group && c.Bucket == key);
        }
        return cells.GroupBy(c => c.EmployeeId).Select(g => Row(g.Key, g.Sum(c => c.Amount))).ToList();
    }

    private static List<ReconEmployeeRowDto>? SettlementRows(ReconBuild build, ReconDrillQuery q)
    {
        if (!string.IsNullOrWhiteSpace(q.Category))
        {
            var cell = q.Category.Trim().ToUpperInvariant();
            if (!FinalSettlementBlock.CellOrder.Contains(cell)) return null;
            return build.FsPlacements.Where(p => p.Cell == cell).Select(p => new ReconEmployeeRowDto(
                p.Settlement.EmployeeId, p.Settlement.EmployeeCode, p.Settlement.EmployeeName, null,
                $"{p.Settlement.Status}; last working day {p.Settlement.LastWorkingDay:yyyy-MM-dd}", null, p.Settlement.GrossPayable, null)).ToList();
        }
        if (string.IsNullOrWhiteSpace(q.ItemCode)) return null;
        var code = q.ItemCode.Trim();
        var byId = build.FsSettlements.Where(s => FinalSettlementStatuses.HasAccrued(s.Status)).ToDictionary(s => s.SettlementId);
        return build.FsLines.Where(l => l.ComponentCode == code && byId.ContainsKey(l.SettlementId))
            .GroupBy(l => l.SettlementId).Select(g =>
            {
                var s = byId[g.Key];
                var amount = g.Sum(l => l.LineType == FinalSettlementLineTypes.Deduction ? -l.Amount : l.Amount);
                var thisMonth = string.Equals(s.GlPeriod, build.CurrentPeriod, StringComparison.Ordinal);
                return new ReconEmployeeRowDto(s.EmployeeId, s.EmployeeCode, s.EmployeeName, null,
                    thisMonth ? "Accrued this month" : "Accrued last month", thisMonth ? null : amount, thisMonth ? amount : null, null);
            }).ToList();
    }
}
