using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Contracts;
using Zayra.Api.Application.Entitlements;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Compliance;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Entitlements;

// Release A slice R2 owns this file. Shared contracts: Application/Entitlements (IEntitlementResolver, EmployeePackage,
// PackageLine, GradeStandardLine). Gated per tenant by the release_a feature flag at the API edge.

/// <summary>
/// Why a package line is not eligible or not offered, when no <see cref="ReleaseABlockReasons"/> code fits. These explain
/// a line; they are not refusals. The R0 block catalogue covers refusals (a missing grade, a missing cell, a floor
/// breach) and R2 uses those codes where they apply. The frontend renders every code as a sentence
/// (frontend/src/i18n/releaseA/package.ts); none is ever shown raw.
/// </summary>
public static class PackageReasonCodes
{
    /// <summary>The employee's company has chosen not to offer this benefit (pay_components.is_offered = false).</summary>
    public const string NotOfferedByCompany = "PACKAGE_NOT_OFFERED_BY_COMPANY";
    /// <summary>The grade cell says this grade does not get the benefit.</summary>
    public const string NotInGrade = "PACKAGE_NOT_IN_GRADE";
    /// <summary>The cell needs more months of service than the employee has completed.</summary>
    public const string ServiceMonths = "PACKAGE_SERVICE_MONTHS";
    /// <summary>The cell applies only once probation has ended.</summary>
    public const string AfterProbation = "PACKAGE_AFTER_PROBATION";
    /// <summary>The cell is limited to another nationality group (with a recorded legal basis).</summary>
    public const string Nationality = "PACKAGE_NATIONALITY";
    /// <summary>The housing advance is a multiple of the housing allowance, and housing is provided in kind.</summary>
    public const string HousingInKind = "PACKAGE_HOUSING_IN_KIND";
    /// <summary>No salary row is in force, so the cash lines (and salary-based limits) cannot be shown.</summary>
    public const string SalaryMissing = "PACKAGE_SALARY_MISSING";

    public static readonly string[] All = [NotOfferedByCompany, NotInGrade, ServiceMonths, AfterProbation, Nationality, HousingInKind, SalaryMissing];
}

/// <summary>
/// The pure rules the resolver and the writer share: criteria, dependants, and "does the grade standard differ from
/// what the employee has". Public so tests can pin them without a database.
/// </summary>
public static class PackageRules
{
    private static readonly HashSet<string> SpouseWords = new(StringComparer.OrdinalIgnoreCase)
        { "spouse", "wife", "husband", "زوج", "زوجة" };
    private static readonly HashSet<string> ChildWords = new(StringComparer.OrdinalIgnoreCase)
        { "child", "son", "daughter", "kid", "ابن", "ابنة", "بنت", "طفل" };

    public static bool IsSpouse(string? relationship) => relationship is not null && SpouseWords.Contains(relationship.Trim());
    public static bool IsChild(string? relationship) => relationship is not null && ChildWords.Contains(relationship.Trim());

    /// <summary>
    /// How many of the employee's recorded dependants a line covers on <paramref name="asOf"/>: the dependants in the
    /// scope (spouse, children, or both), capped at <paramref name="maxDependants"/>. A dependant born after
    /// <paramref name="asOf"/> is not counted yet. Dependants are a coverage basis, never an eligibility criterion.
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

    /// <summary>Saudi or NonSaudi for the nationality criterion, from the one Saudi-spelling list.</summary>
    public static string NationalityClass(Employee employee) =>
        SaudiNationality.IsSaudi(employee.SaudiOrNonSaudi) || SaudiNationality.IsSaudi(employee.Nationality)
            ? WorkerNationalityClasses.Saudi : WorkerNationalityClasses.NonSaudi;

    /// <summary>The static criterion (nationality). NULL when it is met.</summary>
    public static string? NationalityReason(string nationalityScope, Employee employee) =>
        nationalityScope == NationalityScopes.Any || nationalityScope == NationalityClass(employee) ? null : PackageReasonCodes.Nationality;

    /// <summary>The time-based criteria (service months, after probation) on <paramref name="asOf"/>. NULL when met.</summary>
    public static string? TimeCriteriaReason(short? minServiceMonths, bool afterProbation, Employee employee, DateOnly asOf)
    {
        if (minServiceMonths is short months && months > 0)
        {
            var joined = employee.JoiningDate == default ? (DateOnly?)null : DateOnly.FromDateTime(employee.JoiningDate);
            if (joined is null || joined.Value.AddMonths(months) > asOf) return PackageReasonCodes.ServiceMonths;
        }
        if (afterProbation && !(employee.ConfirmationDate <= asOf
                                || (employee.ProbationEndDate.HasValue && employee.ProbationEndDate.Value < asOf)))
            return PackageReasonCodes.AfterProbation;
        return null;
    }

    /// <summary>Rates (a fraction or a multiple) compare at 4 decimal places — the precision the grade cell stores.</summary>
    public static bool SameRate(decimal? a, decimal? b) =>
        a is null || b is null ? a == b : Math.Round(a.Value, 4) == Math.Round(b.Value, 4);

    /// <summary>True when the frozen value no longer matches the grade's standard — shown as "reviewed at renewal".</summary>
    public static bool Differs(GradeStandardLine? standard, string? valueType, decimal? amount, decimal? rate, string? coverageTier,
        short? quantity, string dependantScope, short? maxDependants)
    {
        if (standard is null) return false;
        if (!standard.Eligible || !standard.Offered) return true;
        return standard.ValueType != valueType || standard.Amount != amount || !SameRate(standard.Rate, rate)
            || standard.CoverageTier != coverageTier || standard.Quantity != quantity
            || standard.DependantScope != dependantScope || standard.MaxDependants != maxDependants;
    }
}

/// <summary>
/// Reads the employee package and the grade standard (<see cref="IEntitlementResolver"/>). Read-only. One source per
/// class of benefit:
/// <list type="bullet">
/// <item><b>Salary</b> — QiwaWage cash (housing, transport, other allowances) from the salary row in force (Art. 2:
/// the actual wage lives only there). The grade cell sits alongside for "Why?" and <c>GradeStandardDiffers</c>.</item>
/// <item><b>ContractFrozen</b> — Contractual benefits frozen for the term in force (<c>employee_entitlements</c>).</item>
/// <item><b>GradeStandard</b> — a Contractual benefit not frozen for this term yet: the cell, as a preview.</item>
/// <item><b>Facility</b> — per diem and loan/advance limits, read from the cell as of the date (policy, not contract);
/// loan limits through L1's <see cref="GradeLoanLimitResolver"/>, so the package and the loan form agree.</item>
/// </list>
/// A change to the grade table never rewrites a frozen line: it only raises <c>GradeStandardDiffers</c>.
/// </summary>
public sealed class EntitlementResolver : IEntitlementResolver
{
    private readonly ZayraDbContext _db;

    public EntitlementResolver(ZayraDbContext db) => _db = db;

    private static readonly string[] ClassOrder = [PayEntitlementClasses.QiwaWage, PayEntitlementClasses.Contractual, PayEntitlementClasses.Facility];

    public async Task<EmployeePackage> ResolveAsync(Guid tenantId, int employeeId, DateOnly asOf, CancellationToken ct)
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
            .Where(x => x.TenantId == tenantId && x.EmployeeId == employee.Id).ToListAsync(ct);

        IReadOnlyList<GradeStandardLine> standard = [];
        if (employee.GradeId is not Guid gradeId) blocks.Add(ReleaseABlockReasons.GradeMissing);
        else standard = await GradeStandardAsync(tenantId, gradeId, companyId, asOf, ct);
        var byCode = standard.ToDictionary(s => s.ComponentCode, StringComparer.OrdinalIgnoreCase);

        var frozen = contract is null ? [] : await ScopedBypass.TenantWide(_db.EmployeeEntitlements, tenantId,
                "The employee's own frozen package; the caller has already been authorised for this employee.")
            .AsNoTracking()
            .Where(x => x.EmployeeId == employee.PublicId && x.ContractId == contract.Id
                && x.EffectiveFrom <= asOf && (x.EffectiveTo == null || x.EffectiveTo >= asOf))
            .ToListAsync(ct);
        var frozenByCode = frozen.GroupBy(x => x.PayComponentCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.EffectiveFrom).First(), StringComparer.OrdinalIgnoreCase);
        // The cells frozen rows cite (possibly closed since): their criteria and company-override flag explain the line.
        var citedIds = frozen.Where(x => x.GradeEntitlementId != null).Select(x => x.GradeEntitlementId!.Value).ToList();
        var cited = citedIds.Count == 0 ? new Dictionary<Guid, GradeEntitlement>() : await ScopedBypass.TenantWide(_db.GradeEntitlements, tenantId,
                "The grade cells this employee's frozen rows were copied from.")
            .AsNoTracking().Where(x => citedIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);

        // Which codes appear: the wage floors always (they are on the salary row), every cell the grade has, every frozen
        // row, and the Medical floor (a missing medical cell is a gap HR must see, not a silently absent line).
        var codes = new List<string> { EntitlementComponentRules.Housing, EntitlementComponentRules.Transport };
        if ((salary is not null && salary.FoodAllowance + salary.MobileAllowance + salary.OtherAllowance > 0)
            || byCode.ContainsKey(EntitlementComponentRules.OtherAllowances))
            codes.Add(EntitlementComponentRules.OtherAllowances);
        if (employee.GradeId is not null) codes.Add(EntitlementComponentRules.Medical);
        codes.AddRange(standard.Select(s => s.ComponentCode));
        codes.AddRange(frozen.Select(f => f.PayComponentCode));
        codes = codes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var lines = new List<PackageLine>();
        foreach (var code in codes)
        {
            byCode.TryGetValue(code, out var cell);
            frozenByCode.TryGetValue(code, out var row);
            var rule = EntitlementComponentRules.For(code);
            var cls = rule?.Class ?? cell?.Class ?? row?.EntitlementClass ?? PayEntitlementClasses.Contractual;
            var floor = rule?.Floor ?? cell?.Floor ?? PayStatutoryFloors.None;
            var citedCell = row?.GradeEntitlementId is Guid cid && cited.TryGetValue(cid, out var c) ? c : null;
            var line = cls switch
            {
                PayEntitlementClasses.QiwaWage => SalaryLine(code, cls, floor, salary, cell),
                PayEntitlementClasses.Facility => await FacilityLineAsync(tenantId, employee, code, cls, floor, cell, asOf, ct),
                _ => ContractualLine(code, cls, floor, row, cell, citedCell, employee, dependants, asOf),
            };
            if (line is not null) lines.Add(line);
        }

        var catalogueOrder = EntitlementComponentRules.Catalogue.Select(r => r.Code).ToList();
        static int Rank(int index) => index < 0 ? 99 : index;
        var ordered = lines
            .OrderBy(l => Rank(Array.IndexOf(ClassOrder, l.Class)))
            .ThenBy(l => Rank(catalogueOrder.IndexOf(l.ComponentCode)))
            .ThenBy(l => l.ComponentCode, StringComparer.Ordinal)
            .ToList();
        if (ordered.Any(l => l.ReasonCode == ReleaseABlockReasons.EntitlementCellMissing))
            blocks.Add(ReleaseABlockReasons.EntitlementCellMissing);
        blocks.AddRange(ordered.Select(l => l.ReasonCode)
            .Where(r => r is ReleaseABlockReasons.EntitlementFloorHousing or ReleaseABlockReasons.EntitlementFloorTransport)!);

        return new EmployeePackage(employee.Id, employee.GradeId, contract?.Id, contract?.EndDate, asOf, ordered,
            blocks.Distinct().ToList());
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
                // A floor component is always offered (also a DB CHECK); anything else follows the company row.
                offered = rule?.IsFloor == true || own is null || own.IsOffered;
            }
            return new GradeStandardLine(cell.PayComponentCode, rule?.Class ?? cell.EntitlementClass, rule?.Floor ?? PayStatutoryFloors.None,
                offered, cell.Eligible, cell.ValueType, cell.Amount, cell.Rate, cell.MaxOutstandingAmount, cell.CoverageTier, cell.Quantity,
                cell.DependantScope, cell.MaxDependants, cell.LimitPeriod, cell.MinServiceMonths, cell.AfterProbation, cell.NationalityScope,
                cell.Id, cell.CompanyId.HasValue, cell.EffectiveFrom);
        }).ToList();
    }

    // ── Lines ───────────────────────────────────────────────────────────────────────────────────────

    private static PackageLine SalaryLine(string code, string cls, string floor, EmployeeSalaryStructure? salary, GradeStandardLine? cell)
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
        string? reason = null;
        if (salary is null) reason = PackageReasonCodes.SalaryMissing;
        else if ((floor == PayStatutoryFloors.Housing || floor == PayStatutoryFloors.Transport)
                 && valueType != GradeEntitlementValueTypes.InKind && (cash ?? 0) <= 0)
            reason = floor == PayStatutoryFloors.Housing ? ReleaseABlockReasons.EntitlementFloorHousing : ReleaseABlockReasons.EntitlementFloorTransport;
        var differs = salary is not null && cell is not null && (!cell.Eligible || cell.ValueType != valueType
            || (valueType == GradeEntitlementValueTypes.PercentOfBasic && !PackageRules.SameRate(cell.Rate, rate))
            || (valueType == GradeEntitlementValueTypes.Amount && cell.Amount != amount));
        return new PackageLine(code, cls, floor, PackageLineSources.Salary, cell?.Offered ?? true, true, valueType, amount, rate, cash,
            null, null, DependantScopes.None, null, 0, EntitlementLimitPeriods.Monthly, cell?.GradeEntitlementId, null,
            cell?.IsCompanyOverride ?? false, differs, reason);
    }

    private static PackageLine ContractualLine(string code, string cls, string floor, EmployeeEntitlement? row, GradeStandardLine? cell,
        GradeEntitlement? citedCell, Employee employee, IReadOnlyList<EmployeeDependent> dependants, DateOnly asOf)
    {
        if (row is not null)
        {
            // Frozen for this term: the row is the truth. Time criteria come from the cell it was copied from (a cell is
            // close-only, so the citation is a stable witness); the grade's current cell only raises "differs".
            var timeReason = citedCell is null ? null
                : PackageRules.TimeCriteriaReason(citedCell.MinServiceMonths, citedCell.AfterProbation, employee, asOf);
            return new PackageLine(code, cls, floor, PackageLineSources.ContractFrozen, true, timeReason is null, row.ValueType,
                row.Amount, row.Rate, null, row.CoverageTier, row.Quantity, row.DependantScope, row.MaxDependants,
                PackageRules.DependantsCovered(row.DependantScope, row.MaxDependants, dependants, asOf), row.LimitPeriod,
                row.GradeEntitlementId, row.Id, citedCell?.CompanyId != null,
                PackageRules.Differs(cell, row.ValueType, row.Amount, row.Rate, row.CoverageTier, row.Quantity, row.DependantScope, row.MaxDependants),
                timeReason);
        }
        if (cell is null)
            return new PackageLine(code, cls, floor, PackageLineSources.GradeStandard, true, false, null, null, null, null, null, null,
                DependantScopes.None, null, 0, null, null, null, false, false, ReleaseABlockReasons.EntitlementCellMissing);

        var reason = !cell.Offered ? PackageReasonCodes.NotOfferedByCompany
            : !cell.Eligible ? PackageReasonCodes.NotInGrade
            : PackageRules.NationalityReason(cell.NationalityScope, employee)
              ?? PackageRules.TimeCriteriaReason(cell.MinServiceMonths, cell.AfterProbation, employee, asOf);
        return new PackageLine(code, cls, floor, PackageLineSources.GradeStandard, cell.Offered, reason is null, cell.ValueType,
            cell.Amount, cell.Rate, null, cell.CoverageTier, cell.Quantity, cell.DependantScope, cell.MaxDependants,
            reason is null ? PackageRules.DependantsCovered(cell.DependantScope, cell.MaxDependants, dependants, asOf) : 0,
            cell.LimitPeriod, cell.GradeEntitlementId, null, cell.IsCompanyOverride, false, reason);
    }

    private async Task<PackageLine?> FacilityLineAsync(Guid tenantId, Employee employee, string code, string cls, string floor,
        GradeStandardLine? cell, DateOnly asOf, CancellationToken ct)
    {
        if (cell is null) return null; // a facility the grade has no cell for is simply not part of this package
        var reason = !cell.Offered ? PackageReasonCodes.NotOfferedByCompany
            : !cell.Eligible ? PackageReasonCodes.NotInGrade
            : PackageRules.NationalityReason(cell.NationalityScope, employee)
              ?? PackageRules.TimeCriteriaReason(cell.MinServiceMonths, cell.AfterProbation, employee, asOf);
        var amount = cell.Amount;
        if (reason is null && EntitlementComponentRules.For(code)?.IsLoanFacility == true)
        {
            // The same resolver the loan form, submission and approval use — so "up to SAR 6,000" here is what the
            // employee can actually apply for today, after anything already outstanding on that loan type.
            var limit = await LoanLimitAsync(tenantId, employee, code, asOf, ct);
            if (limit is not null)
            {
                if (limit.ReasonCode is not null)
                    reason = limit.ReasonCode switch
                    {
                        GradeLimitCodes.HousingInKind => PackageReasonCodes.HousingInKind,
                        GradeLimitCodes.SalaryMissing => PackageReasonCodes.SalaryMissing,
                        GradeLimitCodes.NotEligible => PackageReasonCodes.NotInGrade,
                        GradeLimitCodes.Missing => ReleaseABlockReasons.GradeMissing,
                        _ => ReleaseABlockReasons.EntitlementCellMissing,
                    };
                amount = reason is null ? limit.Available ?? limit.PerLoanCap : null;
            }
        }
        return new PackageLine(code, cls, floor, PackageLineSources.Facility, cell.Offered, reason is null, cell.ValueType,
            amount, cell.Rate, null, cell.CoverageTier, cell.Quantity, cell.DependantScope, cell.MaxDependants, 0,
            cell.LimitPeriod, cell.GradeEntitlementId, null, cell.IsCompanyOverride, false, reason);
    }

    private async Task<GradeLoanLimitResult?> LoanLimitAsync(Guid tenantId, Employee employee, string code, DateOnly asOf, CancellationToken ct)
    {
        var loanType = await _db.LoanTypes.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && !x.IsDeleted && x.IsActive && x.GradeLimited && x.EntitlementComponentCode == code, ct);
        if (loanType is null) return null;
        var currency = employee.CompanyId is Guid cid
            ? await ScopedBypass.TenantWide(_db.Companies, tenantId, "The employee's own company currency for the loan limit.")
                .AsNoTracking().Where(x => x.Id == cid).Select(x => x.DefaultCurrency).FirstOrDefaultAsync(ct)
            : null;
        // Outstanding on this loan type, counted the way LoanEligibilityService counts it (pending and approved count).
        var commitments = await ScopedBypass.TenantWide(_db.EmployeeLoans, tenantId,
                "The employee's own debts on this loan type, across legal entities after a transfer.")
            .AsNoTracking()
            .Where(x => x.EmployeeIntId == employee.Id && x.LoanTypeId == loanType.Id && !x.IsDeleted
                && (x.Status == "Pending" || x.Status == "Approved" || x.OutstandingBalance > 0))
            .Select(x => new { x.DisbursementDate, x.OutstandingBalance, x.Status, x.ApprovedAmount, x.RequestedAmount })
            .ToListAsync(ct);
        var outstanding = commitments.Sum(x => x.DisbursementDate.HasValue || x.OutstandingBalance > 0
            ? x.OutstandingBalance : x.Status == "Approved" ? x.ApprovedAmount : x.RequestedAmount);
        return await new GradeLoanLimitResolver(_db).ResolveAsync(tenantId, employee, loanType, asOf, outstanding, null, currency ?? "SAR", ct);
    }

    private async Task<Dictionary<string, bool>> LoanOfferingAsync(Guid tenantId, Guid companyId, IReadOnlyList<string> codes, CancellationToken ct)
    {
        var loanCodes = codes.Where(c => c.StartsWith(EntitlementComponentRules.LoanPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
        if (loanCodes.Count == 0) return [];
        var types = await _db.LoanTypes.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.EntitlementComponentCode != null && loanCodes.Contains(x.EntitlementComponentCode))
            .Select(x => new { x.Id, Code = x.EntitlementComponentCode! }).ToListAsync(ct);
        var typeIds = types.Select(t => t.Id).ToList();
        // The company's newest active policy decides (LoanEligibilityService reads the same switch).
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

    private Task<EmployeeContract?> ContractInForceAsync(Guid tenantId, Guid employeePublicId, DateOnly asOf, CancellationToken ct) =>
        ScopedBypass.TenantWide(_db.EmployeeContracts, tenantId, "The employee's own contract term in force on the date.")
            .AsNoTracking()
            .Where(x => x.EmployeeId == employeePublicId && !x.IsDeleted
                && (x.Status == "Active" || x.Status == "Expired" || x.Status == "Terminated")
                && x.StartDate <= asOf && (x.EndDate == null || x.EndDate >= asOf))
            .OrderByDescending(x => x.StartDate).ThenByDescending(x => x.Version)
            .FirstOrDefaultAsync(ct);

    /// <summary>The salary row in force on <paramref name="asOf"/> (the rule GradeLoanLimitResolver applies).</summary>
    internal static Task<EmployeeSalaryStructure?> SalaryInForce(ZayraDbContext db, Guid tenantId, int employeeId, DateOnly asOf, CancellationToken ct) =>
        db.EmployeeSalaryStructures.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId && x.IsActive && x.EffectiveDate <= asOf)
            .OrderByDescending(x => x.EffectiveDate).ThenByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
}
