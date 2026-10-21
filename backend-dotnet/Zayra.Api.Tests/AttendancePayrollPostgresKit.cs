using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// A Saudi company, one salaried employee and a Draft August-2026 run on real Postgres, processed through the real
/// <c>PayrollController</c> — for the attendance-to-payroll checks (stale impacts, held deductions, the mark count).
/// Attendance days are seeded as rows, exactly as a given version of the processor wrote them.
/// </summary>
internal sealed class AttendancePayrollPostgresKit
{
    public static readonly DateOnly Day = new(2026, 8, 18); // a Tuesday: a working day under the Fri/Sat week

    public Guid TenantId { get; private init; }
    public Company Company { get; private init; } = null!;
    public Employee Employee { get; private init; } = null!;
    public PayrollRun Run { get; private init; } = null!;
    private Func<ZayraDbContext> Factory { get; init; } = null!;

    public static async Task<AttendancePayrollPostgresKit> CreateAsync(Func<ZayraDbContext> factory, string? storedTimeZone = null)
    {
        await using var db = factory();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        if (storedTimeZone is not null)
            db.TenantLocalizationSettings.Add(new TenantLocalizationSetting { TenantId = tenantId, CountryCode = "SAU", DefaultTimezone = storedTimeZone });
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = $"Attendance Co {Guid.NewGuid():N}", TradeName = "Attendance Co",
            CountryCode = CountryCodes.Saudi, Jurisdiction = Jurisdictions.KsaMainland, RegistrationNumber = $"ATT-{Guid.NewGuid():N}",
            DefaultCurrency = "SAR", IsActive = true, GosiEmployerId = "GOSI-ER-1",
            CreatedAtUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var employee = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = $"ATT-{Guid.NewGuid():N}"[..16], FullName = "Attendance Employee",
            Nationality = "Egypt", Status = "Active", ContractType = "Indefinite", CountryCode = CountryCodes.Saudi, Salary = 9_000m,
            JoiningDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), GosiReference = "GOSI-1", GosiFirstRegisteredOn = new DateOnly(2015, 1, 1),
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = employee.Id, SalaryStructureId = Guid.NewGuid(), BasicSalary = 9_000m,
            HousingAllowance = 0m, TransportAllowance = 0m, Currency = "SAR", EffectiveDate = new DateOnly(2020, 1, 1), IsActive = true,
        });
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
        {
            TenantId = tenantId, EmployeeId = employee.Id, Iban = "SA4420000001234567891234", MolId = $"MOL-{Guid.NewGuid():N}", SalaryCurrency = "SAR",
        });
        var run = new PayrollRun
        {
            TenantId = tenantId, CompanyId = company.Id, Year = Day.Year, Month = Day.Month, Status = "Draft", IncludesRecurringPay = true,
            CreatedAtUtc = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        return new AttendancePayrollPostgresKit { TenantId = tenantId, Company = company, Employee = employee, Run = run, Factory = factory };
    }

    public static DateTime Utc(DateOnly day, int hour, int minute) => day.ToDateTime(new TimeOnly(hour, minute), DateTimeKind.Utc);

    /// <summary>A raw punch, as a punch path records it.</summary>
    public async Task<AttendanceRawEvent> PunchAsync(DateTime atUtc, string direction, bool processed = true)
    {
        await using var db = Factory();
        var raw = new AttendanceRawEvent
        {
            TenantId = TenantId, EmployeeId = Employee.Id, EmployeeCode = Employee.EmployeeCode, PunchDirection = direction,
            PunchTimestampUtc = atUtc, IsProcessed = processed,
        };
        db.AttendanceRawEvents.Add(raw);
        await db.SaveChangesAsync();
        return raw;
    }

    /// <summary>A daily record and its pending impacts exactly as given — what some earlier processor wrote.</summary>
    public async Task SeedDayAsync(DateOnly day, DateTime? firstIn, DateTime? lastOut, string status, bool missingPunch,
        int lateMinutes = 0, int earlyExitMinutes = 0, params (string Type, int Minutes)[] pendingImpacts)
    {
        await using var db = Factory();
        var daily = new AttendanceDailyRecord
        {
            TenantId = TenantId, EmployeeId = Employee.Id, EmployeeName = Employee.FullName, WorkDate = day,
            FirstInUtc = firstIn, LastOutUtc = lastOut, Status = status, MissingPunch = missingPunch,
            LateMinutes = lateMinutes, EarlyExitMinutes = earlyExitMinutes, ProcessedAtUtc = DateTime.UtcNow,
        };
        db.AttendanceDailyRecords.Add(daily);
        foreach (var (type, minutes) in pendingImpacts)
            db.AttendancePayrollImpacts.Add(new AttendancePayrollImpact
            {
                TenantId = TenantId, EmployeeId = Employee.Id, WorkDate = day, ImpactType = type, Minutes = minutes, DailyRecordId = daily.Id,
            });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Marks the kit's own run as a processed recurring-pay run with a slip for the employee, so its month counts as paid.
    /// The kit's run, not a second one: a second run for the same company and month breaks the period's unique index the
    /// moment anything (the company backfill) gives it the company.
    /// </summary>
    public async Task MarkRunPaidAsync()
    {
        await using var db = Factory();
        await db.PayrollRuns.IgnoreQueryFilters().Where(r => r.Id == Run.Id).ExecuteUpdateAsync(x => x.SetProperty(r => r.Status, "Processed"));
        db.PayrollSlips.Add(new PayrollSlip { TenantId = TenantId, CompanyId = Company.Id, RunId = Run.Id, EmployeeId = Employee.Id, Status = "Processed" });
        await db.SaveChangesAsync();
    }

    public ZayraDbContext NewContext() => Factory();

    public PayrollController Payroll(ZayraDbContext db)
    {
        var rules = KsaRules();
        var ctrl = new PayrollController(db, new _AttScope(), new HttpContextAccessor(), new _AttNotifications(),
            new _AttKsaResolver(rules), rules, new _AttLetters(), new NullDocumentStorage(), new PdfRenderGate(8));
        ctrl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Principal(TenantId) } };
        return ctrl;
    }

    public async Task<List<AttendancePayrollImpact>> ImpactsAsync()
    {
        await using var db = Factory();
        return await db.AttendancePayrollImpacts.IgnoreQueryFilters().AsNoTracking().Where(x => x.TenantId == TenantId).ToListAsync();
    }

    public static T Prop<T>(object value, string name) => (T)value.GetType().GetProperty(name)!.GetValue(value)!;

    private static StubRuleReader KsaRules() => new StubRuleReader()
        .Set("ot.hourly_base", "wage").Set("ot.standard_multiplier", 1.5m).Set("ot.restday_multiplier", 2.0m)
        .Set("ot.holiday_multiplier", 2.0m).Set("ot.standard_monthly_hours", 240m)
        .Set("lop.monthly_day_divisor", 30m).Set("lop.standard_work_minutes_per_day", 480m)
        .Set("gosi.saudi_employee_rate", 0.09m).Set("gosi.saudi_employer_rate", 0.09m).Set("gosi.saned_rate", 0.0075m)
        .Set("gosi.expat_occupational_hazard_rate", 0.02m).Set("gosi.covered_wage_ceiling_sar", 45_000m);

    private static ClaimsPrincipal Principal(Guid tenantId) => new(new ClaimsIdentity(new[]
    {
        new Claim("tenant_id", tenantId.ToString()),
        new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        new Claim(ClaimTypes.Name, "Attendance Tester"),
        new Claim(ClaimTypes.Role, "Admin"),
        new Claim("permission", "payroll.read"),
        new Claim("permission", "payroll.write"),
    }, "Test"));
}

file sealed class _AttScope : IDataScopeService
{
    public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
}

file sealed class _AttNotifications : INotificationService
{
    public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string en, string? eid, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _AttKsaResolver(IStatutoryRuleReader rules) : ICountryPackResolver
{
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) => new KsaDeductionCalculator(rules);
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new KsaEndOfServiceCalculator(rules);
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class _AttLetters : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}
