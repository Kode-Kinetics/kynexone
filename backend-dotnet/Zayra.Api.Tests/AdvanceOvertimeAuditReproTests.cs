using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Application.Finance;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Regression tests for the 2026-11-05 advance / overtime end-to-end audit (annual advance quota, overtime holiday
/// source, overtime left behind by an earlier period). Each test asserts the CORRECT behaviour.
/// </summary>
public class AdvanceOvertimeAuditReproTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    // ── Defect A: a REJECTED advance request still consumes the employee's annual quota ───────────────
    [Fact]
    public async Task AdvanceQuota_ARejectedRequest_DoesNotConsumeTheAnnualQuota()
    {
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenantId = Guid.NewGuid();
        var company = new Company { TenantId = tenantId, LegalNameEn = "Quota Co", CountryCode = "SAU", DefaultCurrency = "SAR", IsActive = true };
        var employee = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "Q-001", FullName = "Quota Tester", EnglishName = "Quota Tester",
            Status = EmployeeStatuses.Active, JoiningDate = DateTime.UtcNow.AddYears(-2), UserAccountId = Guid.NewGuid(),
        };
        db.AddRange(company, employee);
        db.AdvancePolicies.Add(new AdvancePolicy
        {
            TenantId = tenantId, PolicyName = "One a year", MaxPercentageOfSalary = 50, MaxAdvancesPerYear = 1,
            MinServiceMonths = 0, AllowInstallments = true, MaxInstallments = 3, CooldownMonths = 0, IsActive = true,
        });
        await db.SaveChangesAsync();
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = employee.Id, SalaryStructureId = Guid.NewGuid(),
            BasicSalary = 8_000m, HousingAllowance = 2_000m, Currency = "SAR",
            EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-1)), IsActive = true,
        });
        await db.SaveChangesAsync();

        var hr = Advances(db, tenantId, Guid.NewGuid(), "HR Manager", "loans.write");
        var finance = Advances(db, tenantId, Guid.NewGuid(), "Finance", "loans.write");
        var request = new CreateAdvanceRequest(employee.PublicId, employee.FullName, 1_000m, "Installments", 2, null, "rent", employee.Id);

        var first = await hr.Create(request, Ct);
        first.Should().BeOfType<OkObjectResult>("the first request of the year is inside the quota");
        var firstId = ((SalaryAdvanceDto)((OkObjectResult)first).Value!).Id;

        (await finance.Reject(firstId, new RejectRequest("typo in amount"), Ct)).Should().BeOfType<OkObjectResult>();

        var second = await hr.Create(request, Ct);
        second.Should().BeOfType<OkObjectResult>(
            "a rejected request never paid anything, so it must not count against MaxAdvancesPerYear; " +
            "actual: " + ((second as ObjectResult)?.Value ?? second.GetType().Name));
    }

    [Fact]
    public async Task AdvanceQuota_APendingRequest_StillBlocksASecondRequest_AtQuotaOne()
    {
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenantId = Guid.NewGuid();
        var company = new Company { TenantId = tenantId, LegalNameEn = "Quota Co", CountryCode = "SAU", DefaultCurrency = "SAR", IsActive = true };
        var employee = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "Q-002", FullName = "Pending Tester", EnglishName = "Pending Tester",
            Status = EmployeeStatuses.Active, JoiningDate = DateTime.UtcNow.AddYears(-2), UserAccountId = Guid.NewGuid(),
        };
        db.AddRange(company, employee);
        db.AdvancePolicies.Add(new AdvancePolicy
        {
            TenantId = tenantId, PolicyName = "One a year", MaxPercentageOfSalary = 50, MaxAdvancesPerYear = 1,
            MinServiceMonths = 0, AllowInstallments = true, MaxInstallments = 3, CooldownMonths = 0, IsActive = true,
        });
        await db.SaveChangesAsync();
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = employee.Id, SalaryStructureId = Guid.NewGuid(),
            BasicSalary = 8_000m, HousingAllowance = 2_000m, Currency = "SAR",
            EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-1)), IsActive = true,
        });
        await db.SaveChangesAsync();

        var hr = Advances(db, tenantId, Guid.NewGuid(), "HR Manager", "loans.write");
        var request = new CreateAdvanceRequest(employee.PublicId, employee.FullName, 1_000m, "Installments", 2, null, "rent", employee.Id);

        (await hr.Create(request, Ct)).Should().BeOfType<OkObjectResult>("the first request is inside the quota and stays Pending");

        var second = await hr.Create(request, Ct);
        second.Should().BeOfType<BadRequestObjectResult>("a Pending request still reserves the single slot");
        ((BadRequestObjectResult)second).Value!.ToString().Should().Contain("already used 1 advance(s)");
    }

    private static AdvancesController Advances(ZayraDbContext db, Guid tenantId, Guid userId, string role, string perm)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, "Audit"),
            new(ClaimTypes.Role, role),
            new("permission", perm),
        };
        return new AdvancesController(db, new OrgScope())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
            },
        };
    }

    private sealed class OrgScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }
}

/// <summary>
/// The overtime module must categorise a day exactly as attendance and payroll do. All three read holidays through
/// <see cref="CompanyPayrollRules.HolidayDatesAsync"/>; payroll pays <c>max(ApprovedMultiplier, floor)</c>, so a day the
/// module calls a 2.0× holiday that payroll calls ordinary is paid at 2.0, and the reverse underpays.
/// In-memory with a stub rule reader: the question is the day category, not statutory arithmetic.
/// </summary>
public class AdvanceOvertimeAuditReproHolidayTests
{
    private static readonly DateOnly Wednesday = new(2026, 3, 4); // a plain working day in a Fri/Sat-weekend country

    private sealed record World(ZayraDbContext Db, Guid TenantId, Company Company, Employee Employee, OvertimePolicy Policy);

    private static async Task<World> SeedAsync(bool legacyEmployeeWithoutCompany = false)
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenantId = Guid.NewGuid();
        var company = new Company { TenantId = tenantId, LegalNameEn = "KSA Co", TradeName = "KSA Co", CountryCode = CountryCodes.Saudi, Jurisdiction = Jurisdictions.KsaMainland, DefaultCurrency = "SAR", IsActive = true };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var employee = new Employee
        {
            TenantId = tenantId, CompanyId = legacyEmployeeWithoutCompany ? null : company.Id, EmployeeCode = "OT-001", FullName = "Ahmed Al-Rashidi",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-3), CountryCode = CountryCodes.Saudi, Salary = 18_000m,
        };
        var policy = new OvertimePolicy
        {
            TenantId = tenantId, Code = "STD", Name = "Standard OT", HourlyRateBasis = "BasicSalary",
            StandardMonthlyHours = 240, MinimumMinutes = 30, MaximumMinutesPerDay = 240, MonthlyCapMinutes = 3600, RequiresApproval = true, IsActive = true,
        };
        db.Employees.Add(employee);
        db.OvertimePolicies.Add(policy);
        await db.SaveChangesAsync();
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = employee.Id, BasicSalary = 18_000m, HousingAllowance = 8_000m, TransportAllowance = 4_000m,
            Currency = "SAR", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
        });
        db.OvertimeMultipliers.AddRange(
            new OvertimeMultiplier { TenantId = tenantId, OvertimePolicyId = policy.Id, DayCategory = "RegularDay", Multiplier = 1.5m },
            new OvertimeMultiplier { TenantId = tenantId, OvertimePolicyId = policy.Id, DayCategory = "Weekend", Multiplier = 2.0m },
            new OvertimeMultiplier { TenantId = tenantId, OvertimePolicyId = policy.Id, DayCategory = "PublicHoliday", Multiplier = 2.0m });
        await db.SaveChangesAsync();
        return new World(db, tenantId, company, employee, policy);
    }

    private static async Task AddHolidayAsync(World w, Guid? calendarCompanyId, bool active, string name)
    {
        var calendar = new PublicHolidayCalendar
        {
            TenantId = w.TenantId, Name = name, CountryCode = "SAU", CalendarYear = Wednesday.Year, IsActive = active, CompanyId = calendarCompanyId,
        };
        w.Db.PublicHolidayCalendars.Add(calendar);
        w.Db.PublicHolidays.Add(new PublicHoliday { TenantId = w.TenantId, CalendarId = calendar.Id, NameEn = name, Date = Wednesday });
        await w.Db.SaveChangesAsync();
    }

    private static async Task<decimal> ApprovedMultiplierAsync(World w)
    {
        var request = new OvertimeRequest
        {
            TenantId = w.TenantId, CompanyId = w.Employee.CompanyId, EmployeeId = w.Employee.Id, EmployeeName = w.Employee.FullName,
            OvertimePolicyId = w.Policy.Id, WorkDate = Wednesday, StartTimeUtc = Wednesday.ToDateTime(new TimeOnly(18, 0), DateTimeKind.Utc),
            EndTimeUtc = Wednesday.ToDateTime(new TimeOnly(20, 0), DateTimeKind.Utc), RequestedMinutes = 120, Reason = "Quarterly close", Status = "PendingManager",
        };
        w.Db.OvertimeRequests.Add(request);
        await w.Db.SaveChangesAsync();

        var ctrl = new OvertimeController(w.Db, new HolidayOrgScope(), new HrmHierarchyService(w.Db, new AuditService(w.Db)), ruleReader: new StubRuleReader());
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", w.TenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role, "Admin"),
                }, "Test")),
            },
        };
        var result = await ctrl.Approve(request.Id, new OvertimeDecisionRequest(60, "ok"), CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();
        return ((OvertimeCalculation)((OkObjectResult)result).Value!).Multiplier;
    }

    [Fact]
    public async Task AnActiveCompanyHoliday_IsAHolidayRateDay_PositiveControl()
    {
        var w = await SeedAsync();
        await AddHolidayAsync(w, w.Company.Id, active: true, "Founding Day");
        (await CompanyPayrollRules.HolidayDatesAsync(w.Db, w.TenantId, w.Employee.CompanyId, Wednesday, Wednesday, CancellationToken.None))
            .Should().Contain(Wednesday, "payroll sees this holiday");

        (await ApprovedMultiplierAsync(w)).Should().Be(2.0m);
    }

    [Fact]
    public async Task AnInactiveCalendarsHoliday_IsAnOrdinaryDay_AsPayrollAndAttendanceTreatIt()
    {
        var w = await SeedAsync();
        await AddHolidayAsync(w, null, active: false, "Retired calendar");
        (await CompanyPayrollRules.HolidayDatesAsync(w.Db, w.TenantId, w.Employee.CompanyId, Wednesday, Wednesday, CancellationToken.None))
            .Should().BeEmpty("an inactive calendar is not a holiday for payroll");

        (await ApprovedMultiplierAsync(w)).Should().Be(1.5m,
            "a 2.0 here is persisted as ApprovedMultiplier and payroll pays it, so the employee would be overpaid on an ordinary day");
    }

    [Fact]
    public async Task ASisterCompanysCalendar_IsAnOrdinaryDay_ForThisCompanysEmployee()
    {
        var w = await SeedAsync();
        var sister = new Company { TenantId = w.TenantId, LegalNameEn = "Sister Co", CountryCode = CountryCodes.Saudi, Jurisdiction = Jurisdictions.KsaMainland, DefaultCurrency = "SAR", IsActive = true };
        w.Db.Companies.Add(sister);
        await w.Db.SaveChangesAsync();
        await AddHolidayAsync(w, sister.Id, active: true, "Sister anniversary");

        (await ApprovedMultiplierAsync(w)).Should().Be(1.5m);
    }

    // C1 — a legacy employee has no company stamp. Payroll resolves a null company to the tenant's single active
    // company before reading holidays, so it pays the company's holiday at 2.0; the module must classify the day the same way.
    [Fact]
    public async Task ALegacyEmployeeWithoutACompany_InASingleCompanyTenant_GetsTheCompanysHolidayRate()
    {
        var w = await SeedAsync(legacyEmployeeWithoutCompany: true);
        await AddHolidayAsync(w, w.Company.Id, active: true, "Founding Day");
        w.Employee.CompanyId.Should().BeNull();

        (await CompanyPayrollRules.HolidayDatesAsync(w.Db, w.TenantId, w.Company.Id, Wednesday, Wednesday, CancellationToken.None))
            .Should().Contain(Wednesday, "payroll resolves the single active company, so it sees this holiday");

        (await ApprovedMultiplierAsync(w)).Should().Be(2.0m,
            "approving at 1.5 while payroll pays 2.0 means the approval screen understates what the employee is paid");
    }

    [Fact]
    public async Task ALegacyEmployeeWithoutACompany_InAMultiCompanyTenant_IsNotGivenAnArbitraryCompanysHoliday()
    {
        var w = await SeedAsync(legacyEmployeeWithoutCompany: true);
        w.Db.Companies.Add(new Company { TenantId = w.TenantId, LegalNameEn = "Second Co", CountryCode = CountryCodes.Saudi, Jurisdiction = Jurisdictions.KsaMainland, DefaultCurrency = "SAR", IsActive = true });
        await w.Db.SaveChangesAsync();
        await AddHolidayAsync(w, w.Company.Id, active: true, "Founding Day");

        (await ApprovedMultiplierAsync(w)).Should().Be(1.5m,
            "with two active companies payroll refuses to guess (company_not_resolved), so the module must not guess either");
    }

    private sealed class HolidayOrgScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }
}

/// <summary>
/// Rule 11b's source: approved overtime still PendingPayroll, dated before the run's period, whose own period
/// has ALREADY been paid. Overtime whose period has no live run is that run's to pay, not this one's.
/// </summary>
public class AdvanceOvertimeAuditReproPriorPeriodFinderTests
{
    private static readonly DateOnly PeriodStart = new(2026, 6, 1);
    private static readonly DateOnly MayDay = new(2026, 5, 28);

    private sealed class World
    {
        public ZayraDbContext Db { get; } = new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Guid TenantId { get; } = Guid.NewGuid();
        public Guid CompanyId { get; } = Guid.NewGuid();
        public int EmployeeId { get; } = 7;
    }

    private static async Task<OvertimeRequest> AddImpactAsync(World w, DateOnly workDate, string impactStatus = "PendingPayroll", Guid? runId = null,
        Guid? requestCompany = null, bool nullCompany = false, string requestStatus = "Approved", Guid? tenantId = null)
    {
        var tid = tenantId ?? w.TenantId;
        var request = new OvertimeRequest
        {
            TenantId = tid, CompanyId = nullCompany ? null : requestCompany ?? w.CompanyId, EmployeeId = w.EmployeeId, EmployeeName = "E",
            WorkDate = workDate, StartTimeUtc = workDate.ToDateTime(new TimeOnly(17, 0), DateTimeKind.Utc),
            EndTimeUtc = workDate.ToDateTime(new TimeOnly(19, 0), DateTimeKind.Utc), RequestedMinutes = 120, ApprovedMinutes = 120, Status = requestStatus,
        };
        w.Db.OvertimeRequests.Add(request);
        await w.Db.SaveChangesAsync();
        w.Db.OvertimePayrollImpacts.Add(new OvertimePayrollImpact
        {
            TenantId = tid, OvertimeRequestId = request.Id, EmployeeId = w.EmployeeId, Minutes = 120, Amount = 0m,
            Status = impactStatus, ApprovedMultiplier = 1.5m, PayrollRunId = runId,
        });
        await w.Db.SaveChangesAsync();
        return request;
    }

    private static async Task AddRunAsync(World w, int year, int month, string status, string runType = "Regular", Guid? companyId = null, bool noCompany = false, Guid? tenantId = null)
    {
        w.Db.PayrollRuns.Add(new PayrollRun
        {
            TenantId = tenantId ?? w.TenantId, CompanyId = noCompany ? null : companyId ?? w.CompanyId, Year = year, Month = month, Status = status, RunType = runType,
        });
        await w.Db.SaveChangesAsync();
    }

    private static Task<IReadOnlyList<PriorPeriodOvertime>> FindAsync(World w, Guid? companyId = null) =>
        PriorPeriodOvertimeFinder.FindAsync(w.Db, w.TenantId, companyId ?? w.CompanyId, new[] { w.EmployeeId }, PeriodStart, CancellationToken.None);

    [Theory]
    [InlineData("Locked")]
    [InlineData("Processed")]
    [InlineData("PendingFinanceReview")]
    [InlineData("Approved")]
    [InlineData("Paid")]
    public async Task OvertimeFromAnEarlierPeriod_WhoseRunHasConsumedItsImpacts_IsFlagged(string status)
    {
        var w = new World();
        var request = await AddImpactAsync(w, MayDay);
        await AddRunAsync(w, 2026, 5, status);

        var found = await FindAsync(w);
        found.Should().ContainSingle().Which.Should().BeEquivalentTo(new PriorPeriodOvertime(w.EmployeeId, request.Id, MayDay, 120),
            o => o.Including(x => x.EmployeeId).Including(x => x.OvertimeRequestId).Including(x => x.WorkDate).Including(x => x.Minutes));
    }

    [Fact]
    public async Task OvertimeFromAnEarlierPeriod_WhoseReplacementRunIsLocked_IsFlagged()
    {
        var w = new World();
        await AddImpactAsync(w, MayDay);
        await AddRunAsync(w, 2026, 5, "Voided");
        await AddRunAsync(w, 2026, 5, "Locked", runType: "Replacement");

        (await FindAsync(w)).Should().ContainSingle();
    }

    [Fact]
    public async Task OvertimeFromAnEarlierPeriod_WithNoRunAtAll_IsNotFlagged_ItsOwnPeriodWillPayIt()
    {
        var w = new World();
        await AddImpactAsync(w, MayDay);

        (await FindAsync(w)).Should().BeEmpty("May has not been run yet (e.g. May is processed after June); May's run pays it");
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("Voided")]
    public async Task OvertimeFromAnEarlierPeriod_WhoseOnlyRunIsDraftOrVoided_IsNotFlagged(string status)
    {
        var w = new World();
        await AddImpactAsync(w, MayDay);
        await AddRunAsync(w, 2026, 5, status);

        (await FindAsync(w)).Should().BeEmpty(
            "a voided run returned its overtime to PendingPayroll for the replacement run to pay; a draft run has paid nothing yet");
    }

    [Fact]
    public async Task OvertimeFromAnEarlierPeriod_WhoseRunBelongsToAnotherCompany_IsNotFlagged()
    {
        var w = new World();
        await AddImpactAsync(w, MayDay);
        await AddRunAsync(w, 2026, 5, "Locked", companyId: Guid.NewGuid());

        (await FindAsync(w)).Should().BeEmpty("the other company's run never paid, or could pay, this company's overtime");
    }

    [Fact]
    public async Task OvertimeFromAnEarlierPeriod_WhoseOnlyLockedRunIsADifferentMonth_IsNotFlagged()
    {
        var w = new World();
        await AddImpactAsync(w, MayDay);
        await AddRunAsync(w, 2026, 4, "Locked");

        (await FindAsync(w)).Should().BeEmpty();
    }

    [Fact]
    public async Task ALegacyRequestWithNoCompanyStamp_IsFlaggedOnceItsPeriodHasALockedRunForThisCompany()
    {
        var w = new World();
        await AddImpactAsync(w, MayDay, nullCompany: true);
        await AddRunAsync(w, 2026, 5, "Locked");

        (await FindAsync(w)).Should().ContainSingle();
    }

    [Fact]
    public async Task AnotherCompanysOvertime_IsNotFlagged()
    {
        var w = new World();
        await AddImpactAsync(w, MayDay, requestCompany: Guid.NewGuid());
        await AddRunAsync(w, 2026, 5, "Locked");

        (await FindAsync(w)).Should().BeEmpty();
    }

    [Fact]
    public async Task AnotherTenantsOvertimeAndRuns_AreNeverRead()
    {
        var w = new World();
        var otherTenant = Guid.NewGuid();
        await AddImpactAsync(w, MayDay, tenantId: otherTenant);
        await AddRunAsync(w, 2026, 5, "Locked", tenantId: otherTenant);

        (await FindAsync(w)).Should().BeEmpty();

        // …and another tenant's Locked run does not make this tenant's overtime flaggable.
        await AddImpactAsync(w, MayDay);
        (await FindAsync(w)).Should().BeEmpty("this tenant has no May run");
    }

    [Fact]
    public async Task OvertimeInThePeriod_AlreadyPaid_OrNotApproved_IsNotFlagged()
    {
        var w = new World();
        await AddRunAsync(w, 2026, 5, "Locked");
        await AddRunAsync(w, 2026, 6, "Draft");
        await AddImpactAsync(w, new DateOnly(2026, 6, 10));                                   // in-period: this run pays it
        await AddImpactAsync(w, MayDay, impactStatus: "Processed", runId: Guid.NewGuid());    // already paid
        await AddImpactAsync(w, MayDay, impactStatus: "Processed");                           // already paid (status only)
        await AddImpactAsync(w, MayDay, requestStatus: "Rejected");                           // never approved

        (await FindAsync(w)).Should().BeEmpty();
    }
}

/// <summary>
/// Rule 11b through the real Process / Validate / Approve pipeline (SQLite, no container).
/// </summary>
public class AdvanceOvertimeAuditReproPriorPeriodRunTests
{
    private static readonly Guid Preparer = Guid.NewGuid();
    private static readonly Guid Approver = Guid.NewGuid();
    private const string Code = "OVERTIME_PRIOR_PERIOD_UNPAID";

    private static (ZayraDbContext Db, SqliteConnection Conn) NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static PayrollController Payroll(ZayraDbContext db, Guid tenantId, Guid userId)
    {
        var rules = new StubRuleReader()
            .Set("gosi.saudi_employee_rate", 0.09m).Set("gosi.saudi_employer_rate", 0.09m).Set("gosi.saned_rate", 0.0075m)
            .Set("gosi.expat_occupational_hazard_rate", 0.02m).Set("gosi.covered_wage_ceiling_sar", 45_000m)
            .Set("ot.standard_multiplier", 1.5m).Set("ot.standard_monthly_hours", 240m)
            .Set("lop.monthly_day_divisor", 30m).Set("lop.standard_work_minutes_per_day", 480m);
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, userId == Preparer ? "preparer" : "approver"),
            new(ClaimTypes.Role, userId == Preparer ? "Payroll Officer" : "Finance Controller"),
            new("permission", "payroll.export"), new("permission", "payroll.lock"),
            new("permission", "payroll.write"), new("permission", "payroll.approve"),
        };
        var httpCtx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        var ctrl = new PayrollController(db, new RunScope(), new RunHttp(httpCtx), new _AuditNullNotifications(),
            new _AuditKsaPackResolver(rules), rules, new _AuditNullLetterService(), new NullDocumentStorage(), new PdfRenderGate(4));
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }

    private static async Task<(Guid CompanyId, Employee Emp, PayrollRun June)> SeedAsync(ZayraDbContext db, Guid tenantId)
    {
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "Prior OT Co", CountryCode = "SAU", Jurisdiction = "KSA-mainland",
            RegistrationNumber = $"REG-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true,
        };
        db.Companies.Add(company);
        var structure = new SalaryStructure { TenantId = tenantId, Code = "STR", Name = "Base", Currency = "SAR", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true };
        db.SalaryStructures.Add(structure);
        await db.SaveChangesAsync();
        var emp = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "E-OT-1", FullName = "Prior OT Employee", Nationality = "SAU",
            Status = "Active", JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), ContractType = "Indefinite",
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = emp.Id, SalaryStructureId = structure.Id, BasicSalary = 12_000m, HousingAllowance = 3_000m,
            EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
        });
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
        {
            TenantId = tenantId, EmployeeId = emp.Id, Iban = "SA4420000001234567891234", MolId = "MOL-OT-1", SalaryCurrency = "SAR",
        });
        var june = new PayrollRun { TenantId = tenantId, CompanyId = company.Id, Year = 2026, Month = 6, Status = "Draft", CreatedByUserId = Preparer };
        db.PayrollRuns.Add(june);
        await db.SaveChangesAsync();
        return (company.Id, emp, june);
    }

    private static async Task SeedImpactAsync(ZayraDbContext db, Guid tenantId, Guid companyId, int empId, DateOnly workDate)
    {
        var start = workDate.ToDateTime(new TimeOnly(17, 0), DateTimeKind.Utc);
        var request = new OvertimeRequest
        {
            TenantId = tenantId, CompanyId = companyId, EmployeeId = empId, EmployeeName = "Prior OT Employee", WorkDate = workDate,
            StartTimeUtc = start, EndTimeUtc = start.AddHours(2), RequestedMinutes = 120, ApprovedMinutes = 120, Status = "Approved",
        };
        db.OvertimeRequests.Add(request);
        await db.SaveChangesAsync();
        db.OvertimePayrollImpacts.Add(new OvertimePayrollImpact
        {
            TenantId = tenantId, OvertimeRequestId = request.Id, EmployeeId = empId, Minutes = 120, Amount = 0m, Status = "PendingPayroll", ApprovedMultiplier = 1.5m,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedPriorRunAsync(ZayraDbContext db, Guid tenantId, Guid companyId, string status)
    {
        db.PayrollRuns.Add(new PayrollRun { TenantId = tenantId, CompanyId = companyId, Year = 2026, Month = 5, Status = status });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task OvertimeFromAnEarlierLockedPeriod_IsABlockingError_NamesTheEmployee_RefusesApproval_AndSurvivesValidate()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, emp, june) = await SeedAsync(db, tenantId);
        await SeedPriorRunAsync(db, tenantId, companyId, "Locked");
        await SeedImpactAsync(db, tenantId, companyId, emp.Id, new DateOnly(2026, 5, 28));

        (await Payroll(db, tenantId, Preparer).Process(june.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var finding = await db.PayrollValidationResults.AsNoTracking().SingleAsync(v => v.PayrollRunId == june.Id && v.Code == Code);
        finding.Severity.Should().Be("Error");
        finding.EmployeeId.Should().Be(emp.Id);
        finding.IsResolved.Should().BeFalse();
        finding.Message.Should().Contain(emp.EmployeeCode).And.Contain("2026-05-28").And.Contain("2.00");
        finding.Message.Should().Contain("own period", "the message says this run does not pay it")
            .And.Contain("overtime record", "the settlement path is the overtime record")
            .And.Contain("override", "settled-outside-payroll is the only reason to clear it");
        finding.Message.ToLowerInvariant().Should().NotContain("adjustment").And.NotContain("arrears",
            "no payroll adjustment moves the overtime impact out of PendingPayroll, so advising one invites a second payment");

        await ReconTestSupport.SignOffAsync(db, june.Id);
        db.ChangeTracker.Clear();
        (await Payroll(db, tenantId, Approver).Approve(june.Id, new PayrollDecisionRequest("ok"), CancellationToken.None))
            .Should().BeOfType<UnprocessableEntityObjectResult>("an unresolved Error blocks approval");
        db.ChangeTracker.Clear();

        (await Payroll(db, tenantId, Preparer).Validate(june.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        var rebuilt = await db.PayrollValidationResults.AsNoTracking().SingleAsync(v => v.PayrollRunId == june.Id && v.Code == Code);
        rebuilt.Severity.Should().Be("Error");
        rebuilt.EmployeeId.Should().Be(emp.Id);
        rebuilt.Message.Should().Be(finding.Message, "Process and /validate share one source");

        var impact = await db.OvertimePayrollImpacts.AsNoTracking().SingleAsync(i => i.TenantId == tenantId);
        impact.Status.Should().Be("PendingPayroll", "this run does not pay an earlier period's overtime");
        impact.PayrollRunId.Should().BeNull();
    }

    [Fact]
    public async Task OvertimeFromAnEarlierPeriodThatHasNoRun_IsNotFlagged_ByProcessOrValidate()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, emp, june) = await SeedAsync(db, tenantId);
        await SeedImpactAsync(db, tenantId, companyId, emp.Id, new DateOnly(2026, 5, 28)); // May has not been run yet

        (await Payroll(db, tenantId, Preparer).Process(june.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        (await Payroll(db, tenantId, Preparer).Validate(june.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        (await db.PayrollValidationResults.AsNoTracking().AnyAsync(v => v.PayrollRunId == june.Id && v.Code == Code))
            .Should().BeFalse("May's own run will pay it; flagging it here would invite paying it twice");
    }

    [Fact]
    public async Task OvertimeInTheRunsOwnPeriod_IsPaid_AndNotFlagged()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, emp, june) = await SeedAsync(db, tenantId);
        await SeedPriorRunAsync(db, tenantId, companyId, "Locked");
        await SeedImpactAsync(db, tenantId, companyId, emp.Id, new DateOnly(2026, 6, 10));

        (await Payroll(db, tenantId, Preparer).Process(june.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        (await db.PayrollValidationResults.AsNoTracking().AnyAsync(v => v.PayrollRunId == june.Id && v.Code == Code)).Should().BeFalse();
        (await db.OvertimePayrollImpacts.AsNoTracking().SingleAsync(i => i.TenantId == tenantId)).Status.Should().Be("Processed");
    }

    private sealed class RunScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
    }

    private sealed class RunHttp(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = ctx;
    }
}

file sealed class _AuditNullNotifications : INotificationService
{
    public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string en, string? eid, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _AuditKsaPackResolver(StubRuleReader rules) : ICountryPackResolver
{
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) => new KsaDeductionCalculator(rules);
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class _AuditNullLetterService : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}
