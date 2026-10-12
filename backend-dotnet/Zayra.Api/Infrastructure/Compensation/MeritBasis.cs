using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Compensation;

/// <summary>Grade/department include-exclude lists stored on <see cref="CompCycle.EligibilityFiltersJson"/>.</summary>
public sealed record CompEligibilityFilters(
    IReadOnlyList<Guid> IncludeGradeIds, IReadOnlyList<Guid> ExcludeGradeIds,
    IReadOnlyList<Guid> IncludeDepartmentIds, IReadOnlyList<Guid> ExcludeDepartmentIds)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static readonly CompEligibilityFilters Empty = new([], [], [], []);

    public static CompEligibilityFilters Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty;
        try
        {
            var raw = JsonSerializer.Deserialize<Raw>(json, Json);
            return raw is null ? Empty : new(raw.IncludeGradeIds ?? [], raw.ExcludeGradeIds ?? [], raw.IncludeDepartmentIds ?? [], raw.ExcludeDepartmentIds ?? []);
        }
        catch (JsonException) { return Empty; }
    }

    public string ToJson() => JsonSerializer.Serialize(new Raw(IncludeGradeIds.ToList(), ExcludeGradeIds.ToList(), IncludeDepartmentIds.ToList(), ExcludeDepartmentIds.ToList()), Json);

    private sealed record Raw(List<Guid>? IncludeGradeIds, List<Guid>? ExcludeGradeIds, List<Guid>? IncludeDepartmentIds, List<Guid>? ExcludeDepartmentIds);
}

/// <summary>Everything a cycle needs to know about one employee, read once from pay of record and the org model.</summary>
public sealed class EmployeeBasis
{
    public required Employee Employee { get; init; }
    public bool Eligible { get; init; }
    public string? IneligibleReason { get; init; }
    public int EligibleDays { get; init; }
    public decimal ProrationFactor { get; init; }
    public EmployeeSalaryStructure? SalaryRow { get; init; }
    public string? SalaryStructureCode { get; init; }
    public bool HasLaterSalaryRow { get; init; }
    public bool ContractBound { get; init; }
    public string PayCurrency { get; init; } = string.Empty;
    /// <summary>Cycle units per 1 pay unit; null = no rate entered (blocks).</summary>
    public decimal? FxRate { get; init; }
    public decimal Basic { get; init; }
    public decimal Gross { get; init; }
    public decimal? BasicCycle { get; init; }
    public decimal? GrossCycle { get; init; }
    public Grade? Grade { get; init; }
    public decimal? CompaRatio { get; init; }
    public decimal? RangePosition { get; init; }
    public string Rating { get; init; } = "Unrated";
    public bool HasPendingSalaryChange { get; init; }
    public string DepartmentName { get; init; } = string.Empty;
    public string DesignationName { get; init; } = string.Empty;
    public CompBenchmarkPositionDto Benchmark { get; init; } = new("missing", null, null, false, null, null, null, null, null);
}

/// <summary>
/// Reads the facts a cycle is planned on — in bulk, once per request — and evaluates eligibility, the pay basis,
/// grade position, rating and market position per employee. Read-only: nothing here writes.
/// </summary>
public sealed class MeritBasisLoader(ZayraDbContext db)
{
    /// <summary>Statuses after which an employee is off the books (same list as the effective-change job).</summary>
    public static readonly HashSet<string> SeparatedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Terminated", "Resigned", "Inactive", "Exited", "Offboarded", "Archived", "Retired", "Deceased",
    };

    public const int StaleBenchmarkMonths = 18;

    public sealed class CycleContext
    {
        public required CompCycle Cycle { get; init; }
        public required Company Company { get; init; }
        public required IReadOnlyDictionary<int, EmployeeBasis> Employees { get; init; }
        public required IReadOnlyList<CompFxRate> Fx { get; init; }
        public required IReadOnlyList<CompGuideline> Guidelines { get; init; }
        public required IReadOnlyList<CompCycleBudget> Budgets { get; init; }
        public required IReadOnlyDictionary<Guid, string> DepartmentNames { get; init; }
        /// <summary>Employees HR assigned explicitly to a worksheet owner (employee → owner manager, assigned by). Overrides
        /// the reporting line for worksheet coverage.</summary>
        public IReadOnlyDictionary<int, (int Owner, Guid AssignedBy)> Assignments { get; init; } = new Dictionary<int, (int, Guid)>();

        /// <summary>Whose worksheet covers this employee: an explicit assignment, else their manager on record.</summary>
        public int? CoveringOwner(EmployeeBasis b) =>
            Assignments.TryGetValue(b.Employee.Id, out var a) ? a.Owner : b.Employee.ManagerEmployeeId;

        public decimal? RateFor(string currency) =>
            string.Equals(currency, Cycle.Currency, StringComparison.Ordinal) ? 1m
            : Fx.FirstOrDefault(f => string.Equals(f.FromCurrency, currency, StringComparison.Ordinal))?.Rate;

        public CompGuideline? GuidelineFor(string rating, decimal? compaRatio)
        {
            var cells = Guidelines.Where(g => string.Equals(g.Rating, rating, StringComparison.OrdinalIgnoreCase)).ToList();
            if (compaRatio is not decimal compa)
                return cells.FirstOrDefault(g => g.CompaRatioMin == 0m && g.CompaRatioMax is null);
            return cells.Where(g => compa >= g.CompaRatioMin && (g.CompaRatioMax is null || compa < g.CompaRatioMax))
                .OrderByDescending(g => g.CompaRatioMin).FirstOrDefault();
        }
    }

    /// <summary>
    /// The cycle's eligibility rules for one employee: why they are not eligible (null when they are) and their eligible
    /// days. One definition, used at planning, at apply, and on the effective date (MeritEffectiveChangeGate).
    /// </summary>
    public static (string? Reason, int EligibleDays) Eligibility(CompCycle cycle, Employee e, DateOnly? separationLastWorkingDay)
    {
        var filters = CompEligibilityFilters.Parse(cycle.EligibilityFiltersJson);
        var statuses = cycle.EligibleStatuses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? reason = null;
        var joined = DateOnly.FromDateTime(e.JoiningDate == default ? DateTime.MinValue : e.JoiningDate);
        if (!statuses.Contains(e.Status) || SeparatedStatuses.Contains(e.Status)) reason = $"Status is {e.Status}.";
        else if (e.JoiningDate == default) reason = "No joining date on record.";
        else if (cycle.HireDateCutoff is DateOnly cutoff && joined > cutoff) reason = $"Joined {joined:yyyy-MM-dd}, after the hire-date cutoff {cutoff:yyyy-MM-dd}.";
        else if (filters.IncludeGradeIds.Count > 0 && (e.GradeId is null || !filters.IncludeGradeIds.Contains(e.GradeId.Value))) reason = "Grade is not included in this cycle.";
        else if (e.GradeId is Guid g && filters.ExcludeGradeIds.Contains(g)) reason = "Grade is excluded from this cycle.";
        else if (filters.IncludeDepartmentIds.Count > 0 && (e.DepartmentId is null || !filters.IncludeDepartmentIds.Contains(e.DepartmentId.Value))) reason = "Department is not included in this cycle.";
        else if (e.DepartmentId is Guid d && filters.ExcludeDepartmentIds.Contains(d)) reason = "Department is excluded from this cycle.";
        else if (separationLastWorkingDay is DateOnly lwd) reason = $"On notice or separating (last working day {lwd:yyyy-MM-dd}).";

        var windowStart = joined > cycle.PerformancePeriodStart ? joined : cycle.PerformancePeriodStart;
        var eligibleDays = reason is null ? MeritMath.Days(windowStart, cycle.PerformancePeriodEnd) : 0;
        if (reason is null && eligibleDays < Math.Max(1, cycle.MinimumEligibleDays))
            reason = $"{eligibleDays} eligible day(s) in the performance period; the cycle requires at least {Math.Max(1, cycle.MinimumEligibleDays)}.";
        return (reason, eligibleDays);
    }

    /// <summary>Last working day of the earliest offboarding that is not cancelled (notice, offboarding or separation).</summary>
    public static async Task<DateOnly?> SeparationAsync(ZayraDbContext db, Guid tenantId, int employeeId, CancellationToken ct) =>
        await db.EmployeeOffboardings.AsNoTracking()
            .Where(o => o.TenantId == tenantId && o.EmployeeId == employeeId && o.Status != "Cancelled")
            .OrderBy(o => o.LastWorkingDay).Select(o => (DateOnly?)o.LastWorkingDay).FirstOrDefaultAsync(ct);

    public async Task<CycleContext> LoadAsync(CompCycle cycle, CancellationToken ct, IReadOnlyCollection<Guid>? excludeChangeIds = null)
    {
        var tenantId = cycle.TenantId;
        var company = await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == cycle.CompanyId, ct)
            ?? throw new CompNotFoundException("The cycle's company is not available to you.");
        var fx = await db.CompFxRates.AsNoTracking().Where(f => f.CycleId == cycle.Id).OrderBy(f => f.FromCurrency).ToListAsync(ct);
        var guidelines = await db.CompGuidelines.AsNoTracking().Where(g => g.CycleId == cycle.Id)
            .OrderBy(g => g.Rating).ThenBy(g => g.CompaRatioMin).ToListAsync(ct);
        var budgets = await db.CompCycleBudgets.AsNoTracking().Where(b => b.CycleId == cycle.Id).ToListAsync(ct);

        var employees = await db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.CompanyId == cycle.CompanyId && !e.IsDeleted)
            .ToListAsync(ct);
        var ids = employees.Select(e => e.Id).ToList();

        var rows = await db.EmployeeSalaryStructures.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.IsActive && ids.Contains(s.EmployeeId))
            .ToListAsync(ct);
        var structureIds = rows.Select(r => r.SalaryStructureId).Distinct().ToList();
        var structureCodes = await db.SalaryStructures.AsNoTracking()
            .Where(s => s.TenantId == tenantId && structureIds.Contains(s.Id) && !s.IsDeleted)
            .Select(s => new { s.Id, s.Code }).ToDictionaryAsync(s => s.Id, s => s.Code, ct);

        var gradeIds = employees.Where(e => e.GradeId != null).Select(e => e.GradeId!.Value).Distinct().ToList();
        var grades = await db.Grades.AsNoTracking().Where(g => g.TenantId == tenantId && gradeIds.Contains(g.Id) && !g.IsDeleted)
            .ToDictionaryAsync(g => g.Id, ct);
        var departmentNames = await db.Departments.AsNoTracking().Where(d => d.TenantId == tenantId)
            .Select(d => new { d.Id, d.NameEn }).ToDictionaryAsync(d => d.Id, d => d.NameEn, ct);
        var designationIds = employees.Where(e => e.DesignationId != null).Select(e => e.DesignationId!.Value).Distinct().ToList();
        var designationNames = await db.Designations.AsNoTracking().Where(d => d.TenantId == tenantId && designationIds.Contains(d.Id))
            .Select(d => new { d.Id, d.TitleEn }).ToDictionaryAsync(d => d.Id, d => d.TitleEn, ct);

        // Last published rating in a review period that ended on or before the cycle's performance period end.
        var ratings = (await (from r in db.AppraisalReviews.AsNoTracking()
                              join pc in db.PerformanceCycles.AsNoTracking() on r.CycleId equals pc.Id
                              where r.TenantId == tenantId && pc.TenantId == tenantId && ids.Contains(r.EmployeeId)
                                    && r.PublishedAt != null && r.FinalRating != ""
                                    && pc.ReviewPeriodEnd <= cycle.PerformancePeriodEnd
                              select new { r.EmployeeId, r.FinalRating, pc.ReviewPeriodEnd, r.PublishedAt }).ToListAsync(ct))
            .GroupBy(r => r.EmployeeId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.ReviewPeriodEnd).ThenByDescending(r => r.PublishedAt).First().FinalRating.Trim());

        // On notice or separated: any offboarding that is not cancelled.
        var separations = (await db.EmployeeOffboardings.AsNoTracking()
                .Where(o => o.TenantId == tenantId && ids.Contains(o.EmployeeId) && o.Status != "Cancelled")
                .Select(o => new { o.EmployeeId, o.LastWorkingDay, o.Status }).ToListAsync(ct))
            .GroupBy(o => o.EmployeeId).ToDictionary(g => g.Key, g => g.OrderBy(o => o.LastWorkingDay).First());

        // Pending salary decisions elsewhere: a change request still waiting to be approved or to take effect.
        var excluded = excludeChangeIds ?? Array.Empty<Guid>();
        var pending = (await db.EmployeeChangeRequests.AsNoTracking()
                .Where(c => c.TenantId == tenantId && ids.Contains(c.EmployeeId)
                            && (c.Status == EmployeeChangeStatuses.PendingApproval || c.Status == EmployeeChangeStatuses.ApprovedPendingEffectiveDate))
                .Select(c => new { c.Id, c.EmployeeId, c.SensitiveFields }).ToListAsync(ct))
            .Where(c => !excluded.Contains(c.Id) && TouchesSalary(c.SensitiveFields))
            .Select(c => c.EmployeeId).ToHashSet();

        var benchmarks = await LoadBenchmarksAsync(tenantId, cycle, designationIds, gradeIds, ct);

        var periodDays = MeritMath.Days(cycle.PerformancePeriodStart, cycle.PerformancePeriodEnd);
        var countryIso2 = CountryCodeStandard.NormalizeToIso2(company.CountryCode);

        var assignments = (await (from l in db.CompWorksheetLines.AsNoTracking()
                                  join w in db.CompWorksheets.AsNoTracking() on l.WorksheetId equals w.Id
                                  where l.CycleId == cycle.Id && l.AssignedByUserId != null && w.Status != CompWorksheetStatuses.Cancelled
                                  select new { l.EmployeeId, w.ManagerEmployeeId, l.AssignedByUserId, w.IsPrimary }).ToListAsync(ct))
            .GroupBy(x => x.EmployeeId)
            .ToDictionary(g => g.Key, g => { var x = g.OrderByDescending(y => y.IsPrimary).First(); return (x.ManagerEmployeeId, x.AssignedByUserId!.Value); });

        var context = new Dictionary<int, EmployeeBasis>();
        var partial = new CycleContext
        {
            Cycle = cycle, Company = company, Employees = context, Fx = fx, Guidelines = guidelines, Budgets = budgets,
            DepartmentNames = departmentNames, Assignments = assignments,
        };

        foreach (var e in employees)
        {
            // ── eligibility ──
            separations.TryGetValue(e.Id, out var separation);
            var (reason, eligibleDays) = Eligibility(cycle, e, separation?.LastWorkingDay);
            var factor = reason is not null ? 0m
                : cycle.ProrationRule == CompProrationRules.None ? 1m
                : MeritMath.ProrationFactor(eligibleDays, periodDays);

            // ── pay of record at the effective date (the row payroll would pay that month without the award) ──
            var mine = rows.Where(r => r.EmployeeId == e.Id).ToList();
            var row = mine.Where(r => r.EffectiveDate <= cycle.EffectiveDate)
                .OrderByDescending(r => r.EffectiveDate).ThenByDescending(r => r.CreatedAtUtc).FirstOrDefault();
            var later = mine.Any(r => r.EffectiveDate > cycle.EffectiveDate);
            var contractBound = row is not null && (row.RenewalCaseId != null || row.QiwaConfirmedOn != null
                || row.HousingBasis != AllowanceBases.Amount || row.TransportBasis != AllowanceBases.Amount);
            var payCurrency = row?.Currency is { Length: 3 } c ? c.ToUpperInvariant() : cycle.Currency;
            var rate = partial.RateFor(payCurrency);
            var basic = row?.BasicSalary ?? 0m;
            var gross = row is null ? 0m : MeritMath.Gross(row.BasicSalary, row.HousingAllowance, row.TransportAllowance, row.FoodAllowance, row.MobileAllowance, row.OtherAllowance);

            var grade = e.GradeId is Guid gid ? grades.GetValueOrDefault(gid) : null;
            var (compa, position) = GradePosition(partial, grade, gross, payCurrency);

            var designationName = e.DesignationId is Guid did ? designationNames.GetValueOrDefault(did) ?? e.Designation : e.Designation;
            context[e.Id] = new EmployeeBasis
            {
                Employee = e,
                Eligible = reason is null,
                IneligibleReason = reason,
                EligibleDays = eligibleDays,
                ProrationFactor = factor,
                SalaryRow = row,
                SalaryStructureCode = row is null ? null : structureCodes.GetValueOrDefault(row.SalaryStructureId),
                HasLaterSalaryRow = later,
                ContractBound = contractBound,
                PayCurrency = payCurrency,
                FxRate = rate,
                Basic = basic,
                Gross = gross,
                BasicCycle = rate is decimal r1 ? MeritMath.ToCycle(basic, r1) : null,
                GrossCycle = rate is decimal r2 ? MeritMath.ToCycle(gross, r2) : null,
                Grade = grade,
                CompaRatio = compa,
                RangePosition = position,
                Rating = ratings.GetValueOrDefault(e.Id) ?? "Unrated",
                HasPendingSalaryChange = pending.Contains(e.Id),
                DepartmentName = e.DepartmentId is Guid depId ? departmentNames.GetValueOrDefault(depId) ?? e.Department : e.Department,
                DesignationName = designationName,
                Benchmark = BenchmarkFor(partial, benchmarks, e, countryIso2, basic, gross, payCurrency),
            };
        }
        return partial;
    }

    public static bool TouchesSalary(string? sensitiveFields) =>
        (sensitiveFields ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(k => k is "salary" or EmployeeSalaryBreakdownChanges.Key);

    /// <summary>Compa-ratio and range position against the grade band. Compared in pay currency when the grade is in
    /// the pay currency; otherwise both sides are normalised to the cycle currency (null when a rate is missing).</summary>
    public static (decimal? Compa, decimal? Position) GradePosition(CycleContext ctx, Grade? grade, decimal gross, string payCurrency)
    {
        if (grade is null || grade.MidSalary <= 0) return (null, null);
        var gradeCurrency = string.IsNullOrWhiteSpace(grade.Currency) ? payCurrency : grade.Currency.ToUpperInvariant();
        if (gradeCurrency == payCurrency)
            return (MeritMath.CompaRatio(gross, grade.MidSalary), MeritMath.RangePosition(gross, grade.MinSalary, grade.MaxSalary));
        if (ctx.RateFor(payCurrency) is not decimal payRate || ctx.RateFor(gradeCurrency) is not decimal gradeRate) return (null, null);
        var g = MeritMath.ToCycle(gross, payRate);
        return (MeritMath.CompaRatio(g, MeritMath.ToCycle(grade.MidSalary, gradeRate)),
            MeritMath.RangePosition(g, MeritMath.ToCycle(grade.MinSalary, gradeRate), MeritMath.ToCycle(grade.MaxSalary, gradeRate)));
    }

    // ── benchmarks ──

    private sealed record BenchmarkCandidate(PayBenchmarkSet Set, PayBenchmarkRow Row);

    private async Task<IReadOnlyList<BenchmarkCandidate>> LoadBenchmarksAsync(Guid tenantId, CompCycle cycle,
        IReadOnlyCollection<Guid> designationIds, IReadOnlyCollection<Guid> gradeIds, CancellationToken ct)
    {
        return (await (from row in db.PayBenchmarkRows.AsNoTracking()
                       join set in db.PayBenchmarkSets.AsNoTracking() on row.SetId equals set.Id
                       where set.TenantId == tenantId && set.CompanyId == cycle.CompanyId && set.IsActive
                             && ((row.DesignationId != null && designationIds.Contains(row.DesignationId.Value))
                                 || (row.GradeId != null && gradeIds.Contains(row.GradeId.Value)))
                       select new { set, row }).ToListAsync(ct))
            .Select(x => new BenchmarkCandidate(x.set, x.row)).ToList();
    }

    private static CompBenchmarkPositionDto BenchmarkFor(CycleContext ctx, IReadOnlyList<BenchmarkCandidate> candidates, Employee e,
        string? countryIso2, decimal basic, decimal gross, string payCurrency)
    {
        // Designation mapping first, then grade. Among matches an in-scope set (same country, a usable rate) beats an
        // out-of-scope one, then the newest survey wins; out-of-scope is reported only when nothing in scope is mapped.
        bool InScope(BenchmarkCandidate c) => (countryIso2 is null || string.Equals(c.Set.Geography, countryIso2, StringComparison.OrdinalIgnoreCase))
                                              && ctx.RateFor(c.Set.Currency) is not null;
        BenchmarkCandidate? Best(IEnumerable<BenchmarkCandidate> list) => list.OrderByDescending(InScope)
            .ThenByDescending(c => c.Set.SurveyDate).ThenByDescending(c => c.Set.ImportedAtUtc).FirstOrDefault();
        var match = Best(candidates.Where(c => e.DesignationId != null && c.Row.DesignationId == e.DesignationId))
                    ?? Best(candidates.Where(c => e.GradeId != null && c.Row.GradeId == e.GradeId));
        if (match is null)
            return new("missing", null, null, false, null, null, null, null, "No benchmark row is mapped to this employee's designation or grade.");
        var set = match.Set;
        if (countryIso2 is not null && !string.Equals(set.Geography, countryIso2, StringComparison.OrdinalIgnoreCase))
            return new("out_of_scope", set.Id, set.Name, set.IsSynthetic, set.SurveyDate, set.PayBasis, null, null,
                $"The survey covers {set.Geography}; this company is in {countryIso2}.");
        if (ctx.RateFor(set.Currency) is not decimal setRate)
            return new("out_of_scope", set.Id, set.Name, set.IsSynthetic, set.SurveyDate, set.PayBasis, null, null,
                $"The survey is in {set.Currency} and the cycle has no {set.Currency} rate.");
        if (ctx.RateFor(payCurrency) is not decimal payRate)
            return new("out_of_scope", set.Id, set.Name, set.IsSynthetic, set.SurveyDate, set.PayBasis, null, null,
                $"No {payCurrency} rate is entered, so pay cannot be compared.");
        var p50 = MeritMath.ToCycle(match.Row.P50, setRate);
        var compare = MeritMath.ToCycle(set.PayBasis == PayBenchmarkBases.MonthlyGross ? gross : basic, payRate);
        var position = p50 > 0 ? MeritMath.Ratio(compare / p50) : (decimal?)null;
        var stale = IsStale(set.SurveyDate, ctx.Cycle.EffectiveDate);
        return new(stale ? "stale" : "current", set.Id, set.Name, set.IsSynthetic, set.SurveyDate, set.PayBasis, p50, position,
            stale ? $"Survey dated {set.SurveyDate:yyyy-MM-dd} is more than {StaleBenchmarkMonths} months before the effective date." : null);
    }

    public static bool IsStale(DateOnly surveyDate, DateOnly asOf) => surveyDate.AddMonths(StaleBenchmarkMonths) < asOf;
}
