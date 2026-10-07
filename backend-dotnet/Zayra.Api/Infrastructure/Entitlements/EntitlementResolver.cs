using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Entitlements;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Entitlements;

// Release A slice R2 owns this file. Shared contracts: Application/Entitlements (IEntitlementResolver, EmployeePackage,
// PackageLine, GradeStandardLine, EntitlementRates). Gated per tenant by the release_a feature flag at the API edge.

/// <summary>Relationship values the dependants writer stores (employee_dependents.relationship).</summary>
public static class DependantRelationships
{
    public const string Spouse = "Spouse";
    public const string Child = "Child";
    public const string Parent = "Parent";
    public const string Other = "Other";
    public static readonly string[] All = [Spouse, Child, Parent, Other];
}

/// <summary>The pure rules the resolver and the writer share. Public so tests can pin them without a database.</summary>
public static class PackageRules
{
    // The writer stores the enum values; rows entered before it existed are free text, read with the same meaning.
    private static readonly HashSet<string> SpouseWords = new(StringComparer.OrdinalIgnoreCase)
        { DependantRelationships.Spouse, "wife", "husband", "زوج", "زوجة" };
    private static readonly HashSet<string> ChildWords = new(StringComparer.OrdinalIgnoreCase)
        { DependantRelationships.Child, "son", "daughter", "kid", "ابن", "ابنة", "بنت", "طفل" };

    public static bool IsSpouse(string? relationship) => relationship is not null && SpouseWords.Contains(relationship.Trim());
    public static bool IsChild(string? relationship) => relationship is not null && ChildWords.Contains(relationship.Trim());

    /// <summary>
    /// How many of the employee's recorded dependants a line covers on <paramref name="asOf"/>: those in the scope (spouse,
    /// children, or both), capped at <paramref name="maxDependants"/>. A dependant born after <paramref name="asOf"/> is not
    /// counted yet. Dependants are a coverage basis, never an eligibility criterion.
    /// </summary>
    public static int DependantsCovered(string dependantScope, short? maxDependants, IEnumerable<EmployeeDependent> dependants, DateOnly asOf)
    {
        var born = dependants.Where(d => d.DateOfBirth is null || d.DateOfBirth.Value <= asOf).ToList();
        var count = dependantScope switch
        {
            DependantScopes.Spouse => born.Count(d => IsSpouse(d.Relationship)),
            DependantScopes.Children => born.Count(d => IsChild(d.Relationship)),
            DependantScopes.Family => born.Count(d => IsSpouse(d.Relationship) || IsChild(d.Relationship)),
            _ => 0,
        };
        return maxDependants is short max ? Math.Min(count, max) : count;
    }

    /// <summary>
    /// The nationality criterion, from the contract term's own <c>worker_nationality_class</c> (stamped by R4). R2 never
    /// classifies a nationality itself: a scoped cell with no class on the term is "needs confirmation", never a guess.
    /// NULL when the criterion is met (or the cell is not nationality-scoped).
    /// </summary>
    public static PackageReason? NationalityReason(string nationalityScope, string? workerNationalityClass) =>
        nationalityScope == NationalityScopes.Any ? null
        : workerNationalityClass is null ? new PackageReason(PackageReasons.NationalityUnconfirmed, PackageCriteria.Nationality)
        : nationalityScope == workerNationalityClass ? null
        : new PackageReason(PackageReasons.NotEligibleCriteria, PackageCriteria.Nationality);

    /// <summary>The time-based criteria on <paramref name="asOf"/>, with the date the line becomes eligible. NULL when met.</summary>
    public static (PackageReason Reason, DateOnly? EligibleFrom)? TimeCriteria(short? minServiceMonths, bool afterProbation, Employee employee, DateOnly asOf)
    {
        if (minServiceMonths is short months && months > 0)
        {
            DateOnly? joined = employee.JoiningDate == default ? null : DateOnly.FromDateTime(employee.JoiningDate);
            var from = joined?.AddMonths(months);
            if (from is null || from > asOf) return (new PackageReason(PackageReasons.NotEligibleCriteria, PackageCriteria.ServiceMonths), from);
        }
        if (afterProbation)
        {
            var from = employee.ConfirmationDate ?? employee.ProbationEndDate?.AddDays(1);
            if (from is null || from > asOf) return (new PackageReason(PackageReasons.NotEligibleCriteria, PackageCriteria.AfterProbation), from);
        }
        return null;
    }

    /// <summary>True when a value no longer matches the grade's standard — "reviewed at renewal". Rates at 4dp (round 2).</summary>
    public static bool Differs(GradeStandardLine? standard, string? valueType, decimal? amount, decimal? rate, string? coverageTier,
        short? quantity, string dependantScope, short? maxDependants)
    {
        if (standard is null) return false;
        if (!standard.Eligible || !standard.Offered) return true;
        return standard.ValueType != valueType || standard.Amount != amount || !EntitlementRates.Same(standard.Rate, rate)
            || standard.CoverageTier != coverageTier || standard.Quantity != quantity
            || standard.DependantScope != dependantScope || standard.MaxDependants != maxDependants;
    }
}

/// <summary>The package plus, per component, the reason detail the contract's string <c>ReasonCode</c> cannot carry.</summary>
public sealed record ResolvedPackage(EmployeePackage Package, IReadOnlyDictionary<string, PackageReason> Reasons);

/// <summary>
/// Reads the employee package and the grade standard (<see cref="IEntitlementResolver"/>). Read-only. One source per class:
/// <list type="bullet">
/// <item><b>Salary</b> — QiwaWage cash from the salary row in force (Art. 2). The grade cell sits alongside.</item>
/// <item><b>ContractFrozen</b> — Contractual benefits frozen AND verified for the term in force. Anything not verified is
/// never presented as fixed: it shows as the grade standard, "not yet fixed".</item>
/// <item><b>GradeStandard</b> — a Contractual benefit not frozen for this term yet: the cell, as a preview.</item>
/// <item><b>Facility</b> — per diem and loan/advance limits on the date. A loan limit is the loan form's own preview
/// (<see cref="LoanEligibilityService"/>), so policy caps, notice and overdue blocks and currency all apply and the
/// package and the loan form always agree.</item>
/// </list>
/// Every ineligible or not-offered line carries a reason code (never NULL); <see cref="PackageReasons"/> describes it.
/// </summary>
public sealed class EntitlementResolver : IEntitlementResolver
{
    private readonly ZayraDbContext _db;

    public EntitlementResolver(ZayraDbContext db) => _db = db;

    private static readonly string[] ClassOrder = [PayEntitlementClasses.QiwaWage, PayEntitlementClasses.Contractual, PayEntitlementClasses.Facility];

    public async Task<EmployeePackage> ResolveAsync(Guid tenantId, int employeeId, DateOnly asOf, CancellationToken ct) =>
        (await ResolveDetailedAsync(tenantId, employeeId, asOf, ct)).Package;

    public async Task<ResolvedPackage> ResolveDetailedAsync(Guid tenantId, int employeeId, DateOnly asOf, CancellationToken ct)
    {
        var employee = await ScopedBypass.NullableTenantWide(_db.Employees, tenantId,
                "The package belongs to the employee's own company; the caller has already been authorised for this employee.")
            .AsNoTracking().FirstOrDefaultAsync(x => x.Id == employeeId && !x.IsDeleted, ct)
            ?? throw new KeyNotFoundException($"Employee {employeeId} was not found.");

        var blocks = new List<string>();
        var companyId = employee.CompanyId ?? Guid.Empty;
        var contract = await ContractInForceAsync(tenantId, employee.PublicId, asOf, ct);
        var salary = await SalaryInForce(_db, tenantId, employee.Id, asOf, ct);
        var dependants = await _db.EmployeeDependents.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId == employee.Id && !x.IsDeleted).ToListAsync(ct);

        IReadOnlyList<GradeStandardLine> standard = [];
        if (employee.GradeId is not Guid gradeId) blocks.Add(PackageReasons.GradeMissing);
        else standard = await GradeStandardAsync(tenantId, gradeId, companyId, asOf, ct);
        var byCode = standard.ToDictionary(s => s.ComponentCode, StringComparer.OrdinalIgnoreCase);

        // Only verified rows are the fixed package (an unverified row is a proposal, never "fixed"). Rows are read across the
        // employee's terms for the date — after a back-dated amendment the predecessor still owns the days before the
        // successor's first row (the EXCLUDE guarantees one row per benefit per day) — except a terminated term's rows past
        // its termination day.
        var rowsOnDate = await ScopedBypass.TenantWide(_db.EmployeeEntitlements, tenantId,
                "The employee's own frozen package; the caller has already been authorised for this employee.")
            .AsNoTracking()
            .Where(x => x.EmployeeId == employee.PublicId && x.VerificationState == EntitlementVerificationStates.Verified
                && x.EffectiveFrom <= asOf && (x.EffectiveTo == null || x.EffectiveTo >= asOf))
            .ToListAsync(ct);
        var rowContractIds = rowsOnDate.Select(x => x.ContractId).Distinct().ToList();
        var rowContracts = rowContractIds.Count == 0 ? [] : await ScopedBypass.TenantWide(_db.EmployeeContracts, tenantId, "The terms those rows belong to.")
            .AsNoTracking().Where(x => rowContractIds.Contains(x.Id)).ToListAsync(ct);
        var rowTerminations = await ContractTerminationDates.ForAsync(_db, tenantId, rowContracts, ct);
        var endedOn = rowContracts.ToDictionary(x => x.Id, x => x.Status == "Terminated" ? ContractTerminationDates.LastDay(x, rowTerminations) : null);
        var frozen = rowsOnDate.Where(x => endedOn.GetValueOrDefault(x.ContractId) is not DateOnly last || last >= asOf).ToList();
        var frozenByCode = frozen.GroupBy(x => x.PayComponentCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.EffectiveFrom).First(), StringComparer.OrdinalIgnoreCase);
        var citedIds = frozen.Where(x => x.GradeEntitlementId != null).Select(x => x.GradeEntitlementId!.Value).ToList();
        var cited = citedIds.Count == 0 ? new Dictionary<Guid, GradeEntitlement>() : await ScopedBypass.TenantWide(_db.GradeEntitlements, tenantId,
                "The grade cells this employee's frozen rows were copied from.")
            .AsNoTracking().Where(x => citedIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);

        var codes = new List<string> { EntitlementComponentRules.Housing, EntitlementComponentRules.Transport };
        if ((salary is not null && salary.FoodAllowance + salary.MobileAllowance + salary.OtherAllowance > 0)
            || byCode.ContainsKey(EntitlementComponentRules.OtherAllowances))
            codes.Add(EntitlementComponentRules.OtherAllowances);
        if (employee.GradeId is not null) codes.Add(EntitlementComponentRules.Medical);
        codes.AddRange(standard.Select(s => s.ComponentCode));
        codes.AddRange(frozen.Select(f => f.PayComponentCode));
        codes = codes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var lines = new List<PackageLine>();
        var reasons = new Dictionary<string, PackageReason>(StringComparer.OrdinalIgnoreCase);
        var nationalityClass = contract?.WorkerNationalityClass;
        foreach (var code in codes)
        {
            byCode.TryGetValue(code, out var cell);
            frozenByCode.TryGetValue(code, out var row);
            var rule = EntitlementComponentRules.For(code);
            var cls = rule?.Class ?? cell?.Class ?? row?.EntitlementClass ?? PayEntitlementClasses.Contractual;
            var floor = rule?.Floor ?? cell?.Floor ?? PayStatutoryFloors.None;
            var citedCell = row?.GradeEntitlementId is Guid cid && cited.TryGetValue(cid, out var c) ? c : null;
            var built = cls switch
            {
                PayEntitlementClasses.QiwaWage => SalaryLine(code, cls, floor, salary, cell),
                PayEntitlementClasses.Facility => await FacilityLineAsync(tenantId, employee, code, cls, floor, cell, nationalityClass, asOf, ct),
                _ => ContractualLine(code, cls, floor, row, cell, citedCell, employee, nationalityClass, dependants, asOf),
            };
            if (built is not { } b) continue;
            // The invariant the screens rely on: a line that is not given always says why.
            var reason = b.Reason ?? (!b.Line.Offered ? new PackageReason(PackageReasons.NotOfferedByCompany)
                : !b.Line.Eligible ? new PackageReason(PackageReasons.NotInGrade) : null);
            lines.Add(b.Line with { ReasonCode = reason?.Code });
            if (reason is not null) reasons[code] = reason;
        }

        // A package one person froze as a new hire's must still be one: if the joining date has since moved earlier, say so
        // (coded, on the panel). Nothing is re-frozen or changed automatically — the joining-date edit is audited.
        var directRows = contract is null ? [] : frozen.Where(x => x.ContractId == contract.Id && x.Source == EntitlementSources.GradeDefault
            && x.CarriedFromEntitlementId == null && x.RenewalCaseId == null).ToList();
        if (directRows.Count > 0)
        {
            var basis = await DirectFreezeBasis.ForAsync(_db, tenantId, employee, contract!, ct);
            if (directRows.Any(x => !basis.Allows(x.PayComponentCode))) blocks.Add(PackageReasons.JoiningDateChanged);
        }

        var catalogueOrder = EntitlementComponentRules.Catalogue.Select(r => r.Code).ToList();
        static int Rank(int index) => index < 0 ? 99 : index;
        var ordered = lines
            .OrderBy(l => Rank(Array.IndexOf(ClassOrder, l.Class)))
            .ThenBy(l => Rank(catalogueOrder.IndexOf(l.ComponentCode)))
            .ThenBy(l => l.ComponentCode, StringComparer.Ordinal)
            .ToList();
        blocks.AddRange(ordered.Select(l => l.ReasonCode).OfType<string>()
            .Where(r => r is PackageReasons.CellMissing or PackageReasons.NationalityUnconfirmed or PackageReasons.SalaryMissing
                or Application.Contracts.ReleaseABlockReasons.EntitlementFloorHousing or Application.Contracts.ReleaseABlockReasons.EntitlementFloorTransport));

        return new ResolvedPackage(
            new EmployeePackage(employee.Id, employee.GradeId, contract?.Id, contract?.EndDate, asOf, ordered, blocks.Distinct().ToList()),
            reasons);
    }

    public async Task<IReadOnlyList<GradeStandardLine>> GradeStandardAsync(Guid tenantId, Guid gradeId, Guid companyId, DateOnly asOf, CancellationToken ct)
    {
        // Company filter dropped on purpose: the standard is a property of the EMPLOYEE's company, whoever looks.
        var cells = await ScopedBypass.TenantWide(_db.GradeEntitlements, tenantId,
                "The grade standard for one company: that company's cells and the tenant-wide cells it inherits.")
            .AsNoTracking()
            .Where(x => x.GradeId == gradeId && (x.CompanyId == null || x.CompanyId == companyId)
                && x.EffectiveFrom <= asOf && (x.EffectiveTo == null || x.EffectiveTo >= asOf))
            .ToListAsync(ct);
        var chosen = cells.GroupBy(x => x.PayComponentCode, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.CompanyId.HasValue).ThenByDescending(x => x.EffectiveFrom).First())
            .ToList();
        if (chosen.Count == 0) return [];

        var codes = chosen.Select(x => x.PayComponentCode).ToList();
        var offerings = await ScopedBypass.TenantWide(_db.PayComponents, tenantId,
                "Whether the employee's company offers each component: its own row, else the tenant row.")
            .AsNoTracking()
            .Where(x => codes.Contains(x.Code) && !x.IsDeleted && (x.CompanyId == null || x.CompanyId == companyId))
            .Select(x => new { x.Code, x.CompanyId, x.IsOffered })
            .ToListAsync(ct);
        var loanOffer = await LoanOfferingAsync(tenantId, companyId, codes, ct);

        return chosen.Select(cell =>
        {
            var rule = EntitlementComponentRules.For(cell.PayComponentCode);
            bool offered;
            if (rule?.IsLoanFacility == true)
                offered = !loanOffer.TryGetValue(cell.PayComponentCode, out var o) || o;
            else
            {
                var own = offerings.Where(x => string.Equals(x.Code, cell.PayComponentCode, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.CompanyId.HasValue).FirstOrDefault();
                // A component that can never be skipped (floors and paid wage components) is always offered.
                offered = (rule is not null && !EntitlementComponentRules.CanBeSkipped(rule) && !rule.IsLoanFacility) || own is null || own.IsOffered;
            }
            return new GradeStandardLine(cell.PayComponentCode, rule?.Class ?? cell.EntitlementClass, rule?.Floor ?? PayStatutoryFloors.None,
                offered, cell.Eligible, cell.ValueType, cell.Amount, cell.Rate, cell.MaxOutstandingAmount, cell.CoverageTier, cell.Quantity,
                cell.DependantScope, cell.MaxDependants, cell.LimitPeriod, cell.MinServiceMonths, cell.AfterProbation, cell.NationalityScope,
                cell.Id, cell.CompanyId.HasValue, cell.EffectiveFrom);
        }).ToList();
    }

    // ── Lines ───────────────────────────────────────────────────────────────────────────────────────

    private readonly record struct Built(PackageLine Line, PackageReason? Reason);

    private static PackageLine NewLine(string code, string cls, string floor, string source, bool offered, bool eligible, string? valueType,
        decimal? amount, decimal? rate, decimal? monthlyCash, string? tier, short? quantity, string scope, short? maxDependants, int covered,
        string? period, Guid? cellId, Guid? rowId, bool companyOverride, bool differs, decimal? maxOutstanding, decimal? resolved,
        DateOnly? eligibleFrom, GradeStandardLine? standard) =>
        new(code, cls, floor, source, offered, eligible, valueType, amount, rate, monthlyCash, tier, quantity, scope, maxDependants, covered,
            period, cellId, rowId, companyOverride, differs, null, maxOutstanding, resolved, eligibleFrom, standard);

    private static Built SalaryLine(string code, string cls, string floor, EmployeeSalaryStructure? salary, GradeStandardLine? cell)
    {
        string? valueType = null;
        decimal? amount = null, rate = null, cash = null;
        if (salary is not null)
        {
            (valueType, cash, rate) = code.ToUpperInvariant() switch
            {
                EntitlementComponentRules.Housing => (salary.HousingBasis, (decimal?)salary.HousingAllowance, salary.HousingRate),
                EntitlementComponentRules.Transport => (salary.TransportBasis, salary.TransportAllowance, salary.TransportRate),
                _ => (GradeEntitlementValueTypes.Amount, salary.FoodAllowance + salary.MobileAllowance + salary.OtherAllowance, null),
            };
            amount = valueType == GradeEntitlementValueTypes.Amount ? cash : null;
            if (valueType == GradeEntitlementValueTypes.InKind) cash = 0m;
        }
        // Art. 61: housing and transport are given in cash or in kind, never neither. A salary row that gives neither is a
        // floor breach HR must fix (the line stays eligible — the right exists; the row does not honour it).
        PackageReason? reason = null;
        if (salary is null) reason = new PackageReason(PackageReasons.SalaryMissing);
        else if ((floor == PayStatutoryFloors.Housing || floor == PayStatutoryFloors.Transport)
                 && valueType != GradeEntitlementValueTypes.InKind && (cash ?? 0) <= 0)
            reason = new PackageReason(floor == PayStatutoryFloors.Housing
                ? Application.Contracts.ReleaseABlockReasons.EntitlementFloorHousing
                : Application.Contracts.ReleaseABlockReasons.EntitlementFloorTransport);
        // "Reviewed at renewal" compares what the employee GETS, not how it is expressed: 25% of 8,000 and a fixed 2,000 are the
        // same cash. In kind differs from any cash, and cash from in kind.
        var differs = salary is not null && cell is not null && (!cell.Eligible
            || (cell.ValueType == GradeEntitlementValueTypes.InKind) != (valueType == GradeEntitlementValueTypes.InKind)
            || (valueType != GradeEntitlementValueTypes.InKind && StandardCash(cell, salary.BasicSalary) is decimal standardCash && standardCash != cash));
        return new(NewLine(code, cls, floor, PackageLineSources.Salary, true, true, valueType, amount, rate, cash, null, null,
            DependantScopes.None, null, 0, EntitlementLimitPeriods.Monthly, cell?.GradeEntitlementId, null, cell?.IsCompanyOverride ?? false,
            differs, null, cash, null, cell), reason);
    }

    /// <summary>The monthly cash a wage cell stands for on this basic salary; NULL when it is not a cash figure.</summary>
    private static decimal? StandardCash(GradeStandardLine cell, decimal basic) => cell.ValueType switch
    {
        GradeEntitlementValueTypes.Amount => cell.Amount,
        GradeEntitlementValueTypes.PercentOfBasic when cell.Rate is decimal r => EntitlementMoney.PercentOf(basic, r),
        _ => null,
    };

    private static Built ContractualLine(string code, string cls, string floor, EmployeeEntitlement? row, GradeStandardLine? cell,
        GradeEntitlement? citedCell, Employee employee, string? nationalityClass, IReadOnlyList<EmployeeDependent> dependants, DateOnly asOf)
    {
        if (row is not null)
        {
            // Frozen for this term: the row is the truth. Time criteria come from the cell it was copied from (close-only,
            // so a stable witness); the grade's current cell only raises "differs".
            var time = citedCell is null ? null : PackageRules.TimeCriteria(citedCell.MinServiceMonths, citedCell.AfterProbation, employee, asOf);
            return new(NewLine(code, cls, floor, PackageLineSources.ContractFrozen, true, time is null, row.ValueType,
                row.Amount, row.Rate, null, row.CoverageTier, row.Quantity, row.DependantScope, row.MaxDependants,
                PackageRules.DependantsCovered(row.DependantScope, row.MaxDependants, dependants, asOf), row.LimitPeriod,
                row.GradeEntitlementId, row.Id, citedCell?.CompanyId != null,
                PackageRules.Differs(cell, row.ValueType, row.Amount, row.Rate, row.CoverageTier, row.Quantity, row.DependantScope, row.MaxDependants),
                row.MaxOutstandingAmount, row.ResolvedAmount, time?.EligibleFrom, cell), time?.Reason);
        }
        if (cell is null)
            return new(NewLine(code, cls, floor, PackageLineSources.GradeStandard, true, false, null, null, null, null, null, null,
                DependantScopes.None, null, 0, null, null, null, false, false, null, null, null, null), new PackageReason(PackageReasons.CellMissing));

        var (reason, eligibleFrom) = CellReason(cell, employee, nationalityClass, asOf);
        return new(NewLine(code, cls, floor, PackageLineSources.GradeStandard, cell.Offered, reason is null, cell.ValueType,
            cell.Amount, cell.Rate, null, cell.CoverageTier, cell.Quantity, cell.DependantScope, cell.MaxDependants,
            reason is null ? PackageRules.DependantsCovered(cell.DependantScope, cell.MaxDependants, dependants, asOf) : 0,
            cell.LimitPeriod, cell.GradeEntitlementId, null, cell.IsCompanyOverride, false, cell.MaxOutstandingAmount,
            cell.ValueType == GradeEntitlementValueTypes.Amount ? cell.Amount : null, eligibleFrom, cell), reason);
    }

    private static (PackageReason? Reason, DateOnly? EligibleFrom) CellReason(GradeStandardLine cell, Employee employee, string? nationalityClass, DateOnly asOf)
    {
        if (!cell.Offered) return (new PackageReason(PackageReasons.NotOfferedByCompany), null);
        if (!cell.Eligible) return (new PackageReason(PackageReasons.NotInGrade), null);
        if (PackageRules.NationalityReason(cell.NationalityScope, nationalityClass) is { } n) return (n, null);
        var time = PackageRules.TimeCriteria(cell.MinServiceMonths, cell.AfterProbation, employee, asOf);
        return (time?.Reason, time?.EligibleFrom);
    }

    private async Task<Built?> FacilityLineAsync(Guid tenantId, Employee employee, string code, string cls, string floor,
        GradeStandardLine? cell, string? nationalityClass, DateOnly asOf, CancellationToken ct)
    {
        if (cell is null) return null; // a facility the grade has no cell for is simply not part of this package
        var (reason, eligibleFrom) = CellReason(cell, employee, nationalityClass, asOf);
        var offered = cell.Offered;
        decimal? amount = cell.Amount, resolved = cell.ValueType == GradeEntitlementValueTypes.Amount ? cell.Amount : null;
        if (reason is null && EntitlementComponentRules.For(code)?.IsLoanFacility == true)
        {
            var loan = await LoanPreviewAsync(tenantId, employee, code, asOf, ct);
            if (loan is not null)
            {
                (reason, offered) = (loan.Value.Reason, loan.Value.Offered);
                amount = cell.ValueType == GradeEntitlementValueTypes.Amount ? cell.Amount : null;
                resolved = reason is null ? loan.Value.Available : null;
            }
        }
        return new(NewLine(code, cls, floor, PackageLineSources.Facility, offered, reason is null, cell.ValueType,
            amount, cell.Rate, null, cell.CoverageTier, cell.Quantity, cell.DependantScope, cell.MaxDependants, 0,
            cell.LimitPeriod, cell.GradeEntitlementId, null, cell.IsCompanyOverride, false, cell.MaxOutstandingAmount, resolved, eligibleFrom, cell),
            reason);
    }

    /// <summary>
    /// The loan form's own preview (<see cref="LoanEligibilityService"/>, preview mode, the form's default repayment method),
    /// so "up to SAR 6,000" here is exactly what the employee would be offered: grade cell, policy caps, notice and overdue
    /// blocks, concurrent loans, currency and anything already outstanding. NULL when no grade-limited loan type uses the code.
    /// </summary>
    private async Task<(PackageReason? Reason, bool Offered, decimal? Available)?> LoanPreviewAsync(Guid tenantId, Employee employee, string code,
        DateOnly asOf, CancellationToken ct)
    {
        var loanType = await _db.LoanTypes.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && !x.IsDeleted && x.IsActive && x.GradeLimited && x.EntitlementComponentCode == code, ct);
        if (loanType is null) return null;
        var result = await new LoanEligibilityService(_db).EvaluateAsync(tenantId, employee, loanType, 0m, 0, LoanPreviewRepaymentMethod, ct: ct, preview: true, asOf: asOf);
        if (result.Eligible) return (null, true, result.Available ?? result.MaxAvailableAmount);
        var codes = result.Codes;
        if (codes.Contains(LoanEligibilityCodes.TypeNotOffered)) return (new PackageReason(PackageReasons.NotOfferedByCompany), false, null);
        PackageReason reason = result.GradeLimit?.ReasonCode switch
        {
            GradeLimitCodes.HousingInKind => new(PackageReasons.HousingInKind),
            GradeLimitCodes.SalaryMissing => new(PackageReasons.SalaryMissing),
            GradeLimitCodes.NotEligible => new(PackageReasons.NotInGrade),
            GradeLimitCodes.Missing => new(PackageReasons.GradeMissing),
            GradeLimitCodes.NotConfigured => new(PackageReasons.CellMissing),
            _ when codes.Contains("MinService") => new(PackageReasons.NotEligibleCriteria, PackageCriteria.ServiceMonths),
            _ when codes.Contains("Probation") => new(PackageReasons.NotEligibleCriteria, PackageCriteria.AfterProbation),
            _ => new(PackageReasons.LoanPolicyBlocks),
        };
        return (reason, true, null);
    }

    /// <summary>The loan form's default repayment method (LoansController eligibility endpoint), so both previews agree.</summary>
    public const string LoanPreviewRepaymentMethod = "BankTransfer";

    private async Task<Dictionary<string, bool>> LoanOfferingAsync(Guid tenantId, Guid companyId, IReadOnlyList<string> codes, CancellationToken ct)
    {
        var loanCodes = codes.Where(c => c.StartsWith(EntitlementComponentRules.LoanPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
        if (loanCodes.Count == 0) return [];
        var types = await _db.LoanTypes.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.EntitlementComponentCode != null && loanCodes.Contains(x.EntitlementComponentCode))
            .Select(x => new { x.Id, Code = x.EntitlementComponentCode! }).ToListAsync(ct);
        var typeIds = types.Select(t => t.Id).ToList();
        var policies = await ScopedBypass.TenantWide(_db.LoanPolicies, tenantId, "Whether the employee's own company offers each loan type.")
            .AsNoTracking()
            .Where(x => typeIds.Contains(x.LoanTypeId) && x.CompanyId == companyId && x.IsActive)
            .Select(x => new { x.LoanTypeId, x.IsOffered, x.Version, x.CreatedAtUtc }).ToListAsync(ct);
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in types)
            result[type.Code] = policies.Where(p => p.LoanTypeId == type.Id)
                .OrderByDescending(p => p.Version).ThenByDescending(p => p.CreatedAtUtc)
                .Select(p => (bool?)p.IsOffered).FirstOrDefault() ?? true;
        return result;
    }

    // ── Reads ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The term in force on the date. A Terminated term ends on the day it was terminated (its termination audit row, the
    /// same rule as R4's chain census), not on its original end date: after that day it carries no package.
    /// </summary>
    private async Task<EmployeeContract?> ContractInForceAsync(Guid tenantId, Guid employeePublicId, DateOnly asOf, CancellationToken ct)
    {
        var candidates = await ScopedBypass.TenantWide(_db.EmployeeContracts, tenantId, "The employee's own contract term in force on the date.")
            .AsNoTracking()
            .Where(x => x.EmployeeId == employeePublicId && !x.IsDeleted
                && (x.Status == "Active" || x.Status == "Expired" || x.Status == "Terminated")
                && x.StartDate <= asOf && (x.EndDate == null || x.EndDate >= asOf))
            .OrderByDescending(x => x.StartDate).ThenByDescending(x => x.Version)
            .Take(5).ToListAsync(ct);
        var terminated = await ContractTerminationDates.ForAsync(_db, tenantId, candidates, ct);
        return candidates.FirstOrDefault(x => ContractTerminationDates.LastDay(x, terminated) is not DateOnly last || last >= asOf);
    }

    /// <summary>The salary row in force on <paramref name="asOf"/> (the rule GradeLoanLimitResolver applies).</summary>
    internal static Task<EmployeeSalaryStructure?> SalaryInForce(ZayraDbContext db, Guid tenantId, int employeeId, DateOnly asOf, CancellationToken ct) =>
        db.EmployeeSalaryStructures.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId && x.IsActive && x.EffectiveDate <= asOf)
            .OrderByDescending(x => x.EffectiveDate).ThenByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
}
