using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>A fixed tenant clock: tests decide what "today" is.</summary>
public sealed class FixedTenantClock(DateOnly today) : ITenantClock
{
    public DateOnly Today { get; set; } = today;
    public Task<DateOnly> TodayAsync(Guid tenantId, CancellationToken ct) => Task.FromResult(Today);
}

/// <summary>
/// The storyline's Mohammed (plan §5): Egyptian G3 at Masar Facility Services, basic SAR 8,000, housing 25% = SAR 2,000,
/// term 1 Feb 2026 – 31 Jan 2027, wife and two children. Grade cells as the demo matrix sets them for G3.
/// </summary>
public sealed class PackageSeed
{
    public static readonly DateOnly TermStart = new(2026, 2, 1);
    public static readonly DateOnly TermEnd = new(2027, 1, 31);
    public static readonly DateOnly CellsFrom = new(2026, 1, 1);
    /// <summary>A day before the term starts: a direct freeze (activation) is from the grade table only before a term runs.</summary>
    public static readonly DateOnly FreezeDay = new(2026, 1, 20);

    public Tenant Tenant { get; } = new() { Id = Guid.NewGuid(), Name = "Masar", Slug = $"masar-{Guid.NewGuid():N}" };
    public Company Company { get; }
    public Company OtherCompany { get; }
    public Grade Grade { get; }
    public Employee Mohammed { get; }
    public Employee Colleague { get; }
    public EmployeeContract Term { get; }
    public EmployeeContract ColleagueTerm { get; }
    public EmployeeSalaryStructure Salary { get; }
    public LoanType HousingAdvance { get; }
    /// <summary>The company's housing-advance policy: the loan form (and so the package) refuses a grade-limited type without one.</summary>
    public LoanPolicy HousingAdvancePolicy { get; }
    public Guid UserId { get; } = Guid.NewGuid();
    public Guid ColleagueUserId { get; } = Guid.NewGuid();
    public Dictionary<string, GradeEntitlement> Cells { get; } = new();

    public Guid TenantId => Tenant.Id;

    public PackageSeed()
    {
        Company = new Company { TenantId = Tenant.Id, LegalNameEn = "Masar Facility Services", LegalNameAr = "مسار للخدمات", CountryCode = "SAU",
            Jurisdiction = "KSA-mainland", RegistrationNumber = $"CR-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true };
        OtherCompany = new Company { TenantId = Tenant.Id, LegalNameEn = "Masar Logistics", LegalNameAr = "مسار للخدمات اللوجستية", CountryCode = "SAU",
            Jurisdiction = "KSA-mainland", RegistrationNumber = $"CR-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true };
        Grade = new Grade { TenantId = Tenant.Id, Code = "G3", Name = "Supervisor", NameAr = "مشرف", Level = 30 };
        Mohammed = Person("E-1", Company.Id, UserId);
        Colleague = Person("E-2", OtherCompany.Id, ColleagueUserId);
        Term = new EmployeeContract { TenantId = Tenant.Id, CompanyId = Company.Id, EmployeeId = Mohammed.PublicId, EmployeeName = "Mohammed",
            ContractNumber = "CON-1", Status = "Active", StartDate = TermStart, EndDate = TermEnd, BasicSalary = 8000m, CurrencyCode = "SAR",
            WorkerNationalityClass = WorkerNationalityClasses.NonSaudi };
        ColleagueTerm = new EmployeeContract { TenantId = Tenant.Id, CompanyId = OtherCompany.Id, EmployeeId = Colleague.PublicId, EmployeeName = "Colleague",
            ContractNumber = "CON-2", Status = "Active", StartDate = TermStart, EndDate = TermEnd, BasicSalary = 6000m, CurrencyCode = "SAR",
            WorkerNationalityClass = WorkerNationalityClasses.NonSaudi };
        Salary = new EmployeeSalaryStructure { TenantId = Tenant.Id, EmployeeId = 0, SalaryStructureId = Guid.NewGuid(), BasicSalary = 8000m,
            HousingBasis = AllowanceBases.PercentOfBasic, HousingRate = 0.25m, HousingAllowance = 2000m, TransportAllowance = 800m,
            Currency = "SAR", EffectiveDate = TermStart };
        HousingAdvance = new LoanType { TenantId = Tenant.Id, Code = "HOUSING_ADVANCE", NameEn = "Housing advance", NameAr = "سلفة السكن",
            MaxAmount = 50_000m, GradeLimited = true, EntitlementComponentCode = "LOAN_HOUSING_ADVANCE", IsActive = true };

        HousingAdvancePolicy = new LoanPolicy { TenantId = Tenant.Id, LoanTypeId = HousingAdvance.Id, CompanyId = Company.Id, PolicyName = "Housing advance",
            MaxAmount = 50_000m, MaxInstallments = 12, MaxConcurrentLoans = 1, IsActive = true, IsOffered = true };
        Cell("HOUSING", PayEntitlementClasses.QiwaWage, GradeEntitlementValueTypes.PercentOfBasic, rate: 0.25m, period: EntitlementLimitPeriods.Monthly);
        Cell("TRANSPORT", PayEntitlementClasses.QiwaWage, GradeEntitlementValueTypes.PercentOfBasic, rate: 0.10m, period: EntitlementLimitPeriods.Monthly);
        Cell("MEDICAL", PayEntitlementClasses.Contractual, GradeEntitlementValueTypes.CoverageTier, tier: CoverageTiers.B,
            scope: DependantScopes.Family, period: EntitlementLimitPeriods.PerTerm);
        var ticket = Cell("AIR_TICKET", PayEntitlementClasses.Contractual, GradeEntitlementValueTypes.Quantity, tier: CoverageTiers.Economy,
            quantity: 1, period: EntitlementLimitPeriods.Annual);
        ticket.NationalityScope = NationalityScopes.NonSaudi;
        ticket.NationalityBasis = "Home-leave ticket per contract";
        Cell("EDUCATION", PayEntitlementClasses.Contractual, GradeEntitlementValueTypes.EligibilityOnly, eligible: false);
        Cell("PER_DIEM", PayEntitlementClasses.Facility, GradeEntitlementValueTypes.Amount, amount: 250m, period: EntitlementLimitPeriods.PerDay);
        Cell("LOAN_HOUSING_ADVANCE", PayEntitlementClasses.Facility, GradeEntitlementValueTypes.MultipleOfHousing, rate: 3m);
    }

    private Employee Person(string code, Guid companyId, Guid userId) => new()
    {
        TenantId = Tenant.Id, CompanyId = companyId, EmployeeCode = code, FullName = $"Employee {code}", Nationality = "Egyptian",
        SaudiOrNonSaudi = "Non-Saudi", ContractType = "Fixed", Status = "Active", JoiningDate = new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc),
        ProbationEndDate = new DateOnly(2025, 5, 1), GradeId = Grade.Id, UserAccountId = userId,
    };

    public GradeEntitlement Cell(string code, string cls, string valueType, decimal? amount = null, decimal? rate = null, string? tier = null,
        short? quantity = null, string scope = DependantScopes.None, short? max = null, string? period = null, bool eligible = true,
        Guid? companyId = null, DateOnly? from = null)
    {
        var cell = new GradeEntitlement
        {
            TenantId = Tenant.Id, CompanyId = companyId, GradeId = Grade.Id, PayComponentCode = code, EntitlementClass = cls, Eligible = eligible,
            ValueType = valueType, Amount = amount, Rate = rate, CoverageTier = tier, Quantity = quantity, DependantScope = scope,
            MaxDependants = max, LimitPeriod = period, EffectiveFrom = from ?? CellsFrom,
        };
        Cells[companyId is null ? code : $"{code}@{companyId}"] = cell;
        return cell;
    }

    /// <summary>Writes the tenant, people, terms, salary, dependants, loan type and cells.</summary>
    public async Task SaveAsync(ZayraDbContext db)
    {
        db.AddRange(Tenant, Company, OtherCompany, Grade, Mohammed, Colleague);
        await db.SaveChangesAsync();
        Salary.EmployeeId = Mohammed.Id;
        db.AddRange(Term, ColleagueTerm, Salary, HousingAdvance, HousingAdvancePolicy);
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure { TenantId = Tenant.Id, EmployeeId = Colleague.Id, SalaryStructureId = Guid.NewGuid(),
            BasicSalary = 6000m, HousingAllowance = 1500m, TransportAllowance = 600m, Currency = "SAR", EffectiveDate = TermStart });
        db.EmployeeDependents.AddRange(
            new EmployeeDependent { TenantId = Tenant.Id, EmployeeId = Mohammed.Id, FullName = "Wife", Relationship = "Spouse", DateOfBirth = new DateOnly(1990, 1, 1) },
            new EmployeeDependent { TenantId = Tenant.Id, EmployeeId = Mohammed.Id, FullName = "Son", Relationship = "Child", DateOfBirth = new DateOnly(2015, 1, 1) },
            new EmployeeDependent { TenantId = Tenant.Id, EmployeeId = Mohammed.Id, FullName = "Daughter", Relationship = "Daughter", DateOfBirth = new DateOnly(2018, 1, 1) },
            new EmployeeDependent { TenantId = Tenant.Id, EmployeeId = Mohammed.Id, FullName = "Father", Relationship = "Father" });
        db.GradeEntitlements.AddRange(Cells.Values);
        await db.SaveChangesAsync();
    }
}
