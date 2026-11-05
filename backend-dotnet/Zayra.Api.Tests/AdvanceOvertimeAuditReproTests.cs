using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
/// Reproductions written during the 2026-11-05 advance / overtime end-to-end audit. Each test asserts the
/// CORRECT behaviour, so a failure is the defect. Local audit evidence; not part of any release train.
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

/// <summary>Postgres-backed reproductions (the statutory rule reader's NULL-tenant default lookup needs real SQL).</summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class AdvanceOvertimeAuditReproPostgresTests
{
    private readonly PostgresFixture _fx;
    public AdvanceOvertimeAuditReproPostgresTests(PostgresFixture fx) => _fx = fx;

    private const decimal Basic = 18_000m, Housing = 8_000m, Transport = 4_000m;

    // ── Defect B: the overtime module reads holidays from a different source than attendance/payroll ─
    // OvertimeController.IsPublicHoliday looks at PublicHolidays alone; attendance and payroll go through
    // CompanyPayrollRules.HolidayDatesAsync (ACTIVE calendars, company-wide or this company's). A holiday on
    // an INACTIVE calendar is therefore a 2.0× day for the module and an ordinary 1.5× day for payroll — and
    // because payroll honours ApprovedMultiplier at or above the floor, the 2.0 is what gets PAID.
    [Fact]
    public async Task OvertimeApproval_IgnoresAnInactiveCalendarsHoliday_AsPayrollAndAttendanceDo()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, employee, policy) = await SeedKsaAsync(db);
        var workDate = NextWeekday(DayOfWeek.Wednesday);

        var inactive = new PublicHolidayCalendar { TenantId = tenantId, Name = "Retired 2024 calendar", CountryCode = "SAU", CalendarYear = workDate.Year, IsActive = false };
        db.PublicHolidayCalendars.Add(inactive);
        db.PublicHolidays.Add(new PublicHoliday { TenantId = tenantId, CalendarId = inactive.Id, NameEn = "Stale", Date = workDate });
        await db.SaveChangesAsync();

        // What attendance and payroll consider a holiday for this company on that date: nothing.
        var payrollHolidays = await CompanyPayrollRules.HolidayDatesAsync(db, tenantId, employee.CompanyId, workDate, workDate, CancellationToken.None);
        payrollHolidays.Should().BeEmpty("an inactive calendar is not a holiday for payroll");

        var request = await SeedPendingRequestAsync(db, tenantId, employee, policy, workDate);
        var result = await CreateController(db, tenantId).Approve(request.Id, new OvertimeDecisionRequest(60, "ok"), CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();
        var calc = (OvertimeCalculation)((OkObjectResult)result).Value!;

        calc.Multiplier.Should().Be(1.5m,
            "the module must categorise the day exactly as payroll does; a 2.0 here is persisted as ApprovedMultiplier " +
            "and payroll pays it (EffectiveMultiplier = max(approved, floor)), so the employee is overpaid " +
            $"{OvertimeStatutoryCalculator.EffectiveMultiplier(calc.Multiplier, 1.5m)}× for an ordinary day");
    }

    // ── Defect C: an impact approved after its period's run locked is never paid and nobody is told ─
    [Fact]
    public async Task AnApprovedOvertimeImpact_FromAnEarlierPeriod_IsEitherPaidOrFlagged_ByTheNextRun()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var (emp, run) = await SeedPayrollContext(db, tenantId); // a June 2026 run
        // Two overtime hours worked on 28 May 2026, approved after May's run had locked.
        await SeedOvertimeImpact(db, tenantId, emp.Id, new DateOnly(2026, 5, 28), hours: 2m);
        await db.SaveChangesAsync();

        var result = await BuildPayroll(db, tenantId).Process(run.Id, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();

        var otLine = await db.PayrollEarnings.AsNoTracking()
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.EmployeeId == emp.Id && e.ComponentCode == "OVERTIME");
        var impact = await db.OvertimePayrollImpacts.AsNoTracking().SingleAsync(x => x.TenantId == tenantId && x.EmployeeId == emp.Id);
        var warnings = await db.PayrollValidationResults.AsNoTracking()
            .Where(v => v.TenantId == tenantId && v.PayrollRunId == run.Id).ToListAsync();

        var paid = otLine is not null && impact.Status == "Processed";
        var flagged = warnings.Any(w => w.Message.Contains("overtime", StringComparison.OrdinalIgnoreCase)
                                     && w.Message.Contains("2026-05", StringComparison.OrdinalIgnoreCase)
                                   || w.Code.Contains("OVERTIME", StringComparison.OrdinalIgnoreCase) && w.Code.Contains("PRIOR", StringComparison.OrdinalIgnoreCase));
        (paid || flagged).Should().BeTrue(
            $"approved overtime must reach a payslip (as arrears) or at least block/warn the run; " +
            $"actual: otLine={(otLine is null ? "none" : otLine.Amount.ToString())}, impactStatus={impact.Status}, " +
            $"validation codes=[{string.Join(", ", warnings.Select(w => w.Code))}]");
    }

    // ── helpers (mirrors of the private ones in OvertimeControllerStatutoryParityTests / PayrollOvertimeLopTests) ──

    private async Task<(Guid TenantId, Employee Employee, OvertimePolicy Policy)> SeedKsaAsync(ZayraDbContext db)
    {
        await StatutoryRuleSeeder.SeedAsync(db, NullLogger.Instance);
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company { TenantId = tenantId, LegalNameEn = "KSA Co", TradeName = "KSA Co", CountryCode = CountryCodes.Saudi, Jurisdiction = Jurisdictions.KsaMainland, DefaultCurrency = "SAR", IsActive = true };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var employee = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = $"OT-{Guid.NewGuid():N}"[..12], FullName = "Ahmed Al-Rashidi",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-3), CountryCode = CountryCodes.Saudi, Salary = Basic,
        };
        var policy = new OvertimePolicy
        {
            TenantId = tenantId, Code = $"OT-{Guid.NewGuid():N}"[..10], Name = "Standard OT", HourlyRateBasis = "BasicSalary",
            StandardMonthlyHours = 240, MinimumMinutes = 30, MaximumMinutesPerDay = 240, MonthlyCapMinutes = 3600, RequiresApproval = true, IsActive = true,
        };
        db.Employees.Add(employee);
        db.OvertimePolicies.Add(policy);
        await db.SaveChangesAsync();
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = employee.Id, BasicSalary = Basic, HousingAllowance = Housing, TransportAllowance = Transport,
            Currency = "SAR", EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-1)), IsActive = true,
        });
        db.OvertimeMultipliers.AddRange(
            new OvertimeMultiplier { TenantId = tenantId, OvertimePolicyId = policy.Id, DayCategory = "RegularDay", Multiplier = 1.5m },
            new OvertimeMultiplier { TenantId = tenantId, OvertimePolicyId = policy.Id, DayCategory = "Weekend", Multiplier = 2.0m },
            new OvertimeMultiplier { TenantId = tenantId, OvertimePolicyId = policy.Id, DayCategory = "PublicHoliday", Multiplier = 2.0m });
        await db.SaveChangesAsync();
        return (tenantId, employee, policy);
    }

    private static async Task<OvertimeRequest> SeedPendingRequestAsync(ZayraDbContext db, Guid tenantId, Employee employee, OvertimePolicy policy, DateOnly workDate)
    {
        var request = new OvertimeRequest
        {
            TenantId = tenantId, CompanyId = employee.CompanyId, EmployeeId = employee.Id, EmployeeName = employee.FullName,
            OvertimePolicyId = policy.Id, WorkDate = workDate, StartTimeUtc = DateTime.UtcNow.Date.AddHours(18), EndTimeUtc = DateTime.UtcNow.Date.AddHours(20),
            RequestedMinutes = 120, Reason = "Quarterly close", Status = "PendingManager",
        };
        db.OvertimeRequests.Add(request);
        await db.SaveChangesAsync();
        return request;
    }

    private static OvertimeController CreateController(ZayraDbContext db, Guid tenantId)
    {
        var ctrl = new OvertimeController(db, new OrgScope(), new HrmHierarchyService(db, new AuditService(db)));
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role, "Admin"),
                }, "Test")),
            },
        };
        return ctrl;
    }

    private static DateOnly NextWeekday(DayOfWeek day)
    {
        var d = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        while (d.DayOfWeek != day) d = d.AddDays(1);
        return d;
    }

    private static async Task<(Employee emp, PayrollRun run)> SeedPayrollContext(ZayraDbContext db, Guid tenantId)
    {
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = $"Stale OT Co {Guid.NewGuid():N}", CountryCode = "SAU", Jurisdiction = "KSA-mainland",
            RegistrationNumber = $"REG-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true, CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Companies.Add(company);
        var emp = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"OT-{Guid.NewGuid():N}", FullName = "Stale OT Employee", Nationality = "Saudi",
            Status = "Active", JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = emp.Id, SalaryStructureId = Guid.NewGuid(), BasicSalary = 12_000m, HousingAllowance = 3_000m,
            EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
        });
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
        {
            TenantId = tenantId, EmployeeId = emp.Id, Iban = "SA4420000001234567891234", MolId = $"MOL-{Guid.NewGuid():N}", SalaryCurrency = "SAR",
        });
        var run = new PayrollRun { TenantId = tenantId, CompanyId = company.Id, Year = 2026, Month = 6, Status = "Draft", CreatedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc) };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        return (emp, run);
    }

    private static async Task SeedOvertimeImpact(ZayraDbContext db, Guid tenantId, int empId, DateOnly workDate, decimal hours)
    {
        var start = new DateTime(workDate.Year, workDate.Month, workDate.Day, 17, 0, 0, DateTimeKind.Utc);
        var request = new OvertimeRequest
        {
            TenantId = tenantId, EmployeeId = empId, EmployeeName = "Stale OT Employee", WorkDate = workDate,
            StartTimeUtc = start, EndTimeUtc = start.AddHours((double)hours), RequestedMinutes = (int)(hours * 60), ApprovedMinutes = (int)(hours * 60), Status = "Approved",
        };
        db.OvertimeRequests.Add(request);
        await db.SaveChangesAsync();
        db.OvertimePayrollImpacts.Add(new OvertimePayrollImpact
        {
            TenantId = tenantId, OvertimeRequestId = request.Id, EmployeeId = empId, Minutes = (int)Math.Round(hours * 60m), Amount = 0m, Status = "PendingPayroll", ApprovedMultiplier = 1.5m,
        });
    }

    private static PayrollController BuildPayroll(ZayraDbContext db, Guid tenantId)
    {
        var rules = new StubRuleReader()
            .Set("gosi.saudi_employee_rate", 0.09m).Set("gosi.saudi_employer_rate", 0.09m).Set("gosi.saned_rate", 0.0075m)
            .Set("gosi.expat_occupational_hazard_rate", 0.02m).Set("gosi.covered_wage_ceiling_sar", 45_000m)
            .Set("ot.standard_multiplier", 1.5m).Set("ot.standard_monthly_hours", 240m)
            .Set("lop.monthly_day_divisor", 30m).Set("lop.standard_work_minutes_per_day", 480m);
        var ctrl = new PayrollController(db, new DataScopeService(db), new HttpContextAccessor(), new _AuditNullNotifications(),
            new _AuditKsaPackResolver(rules), rules, new _AuditNullLetterService(), new NullDocumentStorage(), new PdfRenderGate(8));
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role, "Admin"),
                }, "test")),
            },
        };
        return ctrl;
    }

    private sealed class OrgScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
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
