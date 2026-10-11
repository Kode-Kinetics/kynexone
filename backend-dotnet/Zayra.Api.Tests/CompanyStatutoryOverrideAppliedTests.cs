using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// "Approved but never applied" — the proof. Every key the company statutory-override endpoint
/// accepts (<see cref="StatutoryOverridePolicy.CompanyAppliedKeys"/>) is approved for company A
/// through the real maker-checker API and then a REAL payroll run is processed for A and for a
/// sibling company B of the same tenant: A's payslip moves by exactly the override, B's does not.
///
/// Each test uses its own jurisdiction so the platform rows it seeds cannot touch any other test
/// sharing the Postgres fixture. Arithmetic: basic 12,000 / 240 h = 50 an hour; hourly base "basic".
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class CompanyStatutoryOverrideAppliedTests
{
    private readonly PostgresFixture _fx;
    public CompanyStatutoryOverrideAppliedTests(PostgresFixture fx) => _fx = fx;

    private const decimal Basic = 12_000m;

    [Theory]
    // key, override, OT work date, expected OT pay for A, expected OT pay for B (platform 1.5)
    [InlineData("ot.standard_multiplier", "2", 10, 1_000.00, 750.00)]   // Wednesday: 10 h × 50 × 2
    [InlineData("ot.restday_multiplier", "2", 5, 1_000.00, 750.00)]     // Friday (GCC rest day)
    [InlineData("ot.holiday_multiplier", "2.5", 3, 1_250.00, 750.00)]   // public holiday seeded on the 3rd
    [InlineData("ot.standard_monthly_hours", "200", 10, 900.00, 750.00)] // 12,000 / 200 = 60 an hour × 1.5
    public async Task OvertimeOverride_ChangesOvertimePay_ForThatCompanyOnly(string key, string value, int day, double expectedA, double expectedB)
    {
        await using var db = _fx.CreateDb();
        var f = await SeedAsync(db);
        var workDate = new DateOnly(2026, 6, day);
        if (key == "ot.holiday_multiplier")
            db.PublicHolidays.Add(new PublicHoliday { TenantId = f.TenantId, CalendarId = Guid.NewGuid(), NameEn = "Holiday", Date = workDate });
        await SeedOvertime(db, f.TenantId, f.EmpA, f.CompanyA, workDate, 10m);
        await SeedOvertime(db, f.TenantId, f.EmpB, f.CompanyB, workDate, 10m);
        await db.SaveChangesAsync();

        await ApproveOverrideAsync(db, f, key, value);

        AssertOk(await Payroll(db, f.TenantId).Process(f.RunA, default));
        AssertOk(await Payroll(db, f.TenantId).Process(f.RunB, default));

        Assert.Equal((decimal)expectedA, await LineAmount(db, f.TenantId, f.EmpA, "OVERTIME", earning: true));
        Assert.Equal((decimal)expectedB, await LineAmount(db, f.TenantId, f.EmpB, "OVERTIME", earning: true));
    }

    [Theory]
    // key, override, expected LOP for A, expected LOP for B — 3 absent days of 480 minutes, basic ÷ 30 = 400 a day
    [InlineData("lop.monthly_day_divisor", "31", 1_161.29, 1_200.00)]            // 3 × 12,000 / 31
    [InlineData("lop.standard_work_minutes_per_day", "600", 960.00, 1_200.00)]   // 1,440 / 600 = 2.4 days × 400
    public async Task AbsenceOverride_ChangesLossOfPay_ForThatCompanyOnly(string key, string value, double expectedA, double expectedB)
    {
        await using var db = _fx.CreateDb();
        var f = await SeedAsync(db);
        foreach (var emp in new[] { f.EmpA, f.EmpB })
            for (var d = 15; d <= 17; d++)
            {
                db.AttendancePayrollImpacts.Add(new AttendancePayrollImpact
                {
                    TenantId = f.TenantId, EmployeeId = emp, WorkDate = new DateOnly(2026, 6, d),
                    ImpactType = "Absence", Minutes = 480, Status = "PendingPayroll",
                });
                db.AttendanceDailyRecords.Add(new AttendanceDailyRecord
                {
                    TenantId = f.TenantId, EmployeeId = emp, WorkDate = new DateOnly(2026, 6, d), Status = "Absent",
                    ProcessedAtUtc = new DateTime(2026, 6, d, 0, 0, 0, DateTimeKind.Utc),
                    CreatedAtUtc = new DateTime(2026, 6, d, 0, 0, 0, DateTimeKind.Utc),
                });
            }
        await db.SaveChangesAsync();

        await ApproveOverrideAsync(db, f, key, value);

        AssertOk(await Payroll(db, f.TenantId).Process(f.RunA, default));
        AssertOk(await Payroll(db, f.TenantId).Process(f.RunB, default));

        Assert.Equal((decimal)expectedA, await LineAmount(db, f.TenantId, f.EmpA, "LOP_DEDUCTION", earning: false));
        Assert.Equal((decimal)expectedB, await LineAmount(db, f.TenantId, f.EmpB, "LOP_DEDUCTION", earning: false));
    }

    [Fact]
    public void EveryCompanyOverridableKey_HasAPayrollProofAbove()
    {
        var proven = new[]
        {
            "ot.standard_multiplier", "ot.restday_multiplier", "ot.holiday_multiplier", "ot.standard_monthly_hours",
            "lop.monthly_day_divisor", "lop.standard_work_minutes_per_day",
        };
        Assert.Equal(proven.OrderBy(k => k), StatutoryOverridePolicy.CompanyAppliedKeys.OrderBy(k => k));
    }

    [Fact]
    public async Task TwoCheckersApprovingTogether_ExactlyOneDecides()
    {
        Fixture f;
        Guid requestId;
        await using (var db = _fx.CreateDb())
        {
            f = await SeedAsync(db);
            var created = (OkObjectResult)await Rates(db, f.TenantId, Guid.NewGuid()).CreateStatutoryOverride(new StatutoryOverrideRequest(
                f.CompanyA, "SAU", f.Jurisdiction, "ot.standard_multiplier", "1.75", new DateOnly(2026, 6, 1), "CBA", new DateOnly(2027, 6, 1)), default);
            requestId = (Guid)created.Value!.GetType().GetProperty("Id")!.GetValue(created.Value)!;
        }

        async Task<IActionResult> Approve()
        {
            await using var db = _fx.CreateDb();
            return await Rates(db, f.TenantId, Guid.NewGuid()).ApproveStatutoryOverride(requestId, default);
        }
        var results = await Task.WhenAll(Approve(), Approve(), Approve());
        Assert.Single(results, r => r is OkObjectResult);
        Assert.Equal(2, results.Count(r => r is ConflictObjectResult));

        await using var verify = _fx.CreateDb();
        Assert.Equal(1, await verify.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.TenantId == f.TenantId && a.Action == "payroll.rate.statutory_override.approved"));
    }

    private static void AssertOk(IActionResult result)
    {
        if (result is OkObjectResult) return;
        var body = result is ObjectResult o ? System.Text.Json.JsonSerializer.Serialize(o.Value) : result.GetType().Name;
        Assert.Fail($"Payroll did not process: {result.GetType().Name} {body}");
    }

    // ── Fixture ─────────────────────────────────────────────────────────────────

    private sealed record Fixture(Guid TenantId, string Jurisdiction, Guid CompanyA, Guid CompanyB, int EmpA, int EmpB, Guid RunA, Guid RunB);

    private static async Task<Fixture> SeedAsync(ZayraDbContext db)
    {
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var jur = $"OVR-{Guid.NewGuid():N}"[..20]; // jurisdiction columns are varchar(30)
        var since = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var (key, value) in new[]
        {
            ("ot.standard_multiplier", "1.5"), ("ot.restday_multiplier", "1.5"), ("ot.holiday_multiplier", "1.5"),
            ("ot.standard_monthly_hours", "240"), ("ot.hourly_base", "basic"),
            ("lop.monthly_day_divisor", "30"), ("lop.standard_work_minutes_per_day", "480"),
        })
            db.StatutoryRules.Add(new StatutoryRule
            {
                TenantId = null, CountryCode = "SAU", Jurisdiction = jur, RuleKey = key, RuleValue = value,
                DataType = key == "ot.hourly_base" ? "string" : "decimal", Description = "test platform value", EffectiveFrom = since,
            });

        Company NewCompany(string name) => new()
        {
            TenantId = tenantId, LegalNameEn = $"{name} {Guid.NewGuid():N}", CountryCode = "SAU", Jurisdiction = jur,
            RegistrationNumber = $"OVR-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true,
            CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var a = NewCompany("Override A");
        var b = NewCompany("Sibling B");
        db.Companies.AddRange(a, b);
        await db.SaveChangesAsync();

        var empA = await SeedEmployee(db, tenantId, a.Id);
        var empB = await SeedEmployee(db, tenantId, b.Id);
        var runA = new PayrollRun { TenantId = tenantId, CompanyId = a.Id, Year = 2026, Month = 6, Status = "Draft", CreatedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc) };
        var runB = new PayrollRun { TenantId = tenantId, CompanyId = b.Id, Year = 2026, Month = 6, Status = "Draft", CreatedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc) };
        db.PayrollRuns.AddRange(runA, runB);
        await db.SaveChangesAsync();
        return new Fixture(tenantId, jur, a.Id, b.Id, empA, empB, runA.Id, runB.Id);
    }

    private static async Task<int> SeedEmployee(ZayraDbContext db, Guid tenantId, Guid companyId)
    {
        var emp = new Zayra.Api.Models.Employee
        {
            TenantId = tenantId, CompanyId = companyId, EmployeeCode = $"OVR-{Guid.NewGuid():N}", FullName = "Override Test Employee",
            Nationality = "Indian", Status = "Active", JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = emp.Id, SalaryStructureId = Guid.NewGuid(),
            BasicSalary = Basic, HousingAllowance = 3_000m, EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
        });
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
        {
            TenantId = tenantId, EmployeeId = emp.Id, Iban = "SA4420000001234567891234",
            MolId = $"MOL-OVR-{Guid.NewGuid():N}", SalaryCurrency = "SAR",
        });
        await db.SaveChangesAsync();
        return emp.Id;
    }

    private static async Task SeedOvertime(ZayraDbContext db, Guid tenantId, int empId, Guid companyId, DateOnly workDate, decimal hours)
    {
        var start = new DateTime(workDate.Year, workDate.Month, workDate.Day, 17, 0, 0, DateTimeKind.Utc);
        var request = new OvertimeRequest
        {
            TenantId = tenantId, CompanyId = companyId, EmployeeId = empId, EmployeeName = "Override Test Employee", WorkDate = workDate,
            StartTimeUtc = start, EndTimeUtc = start.AddHours((double)hours),
            RequestedMinutes = (int)(hours * 60), ApprovedMinutes = (int)(hours * 60), Status = "Approved",
        };
        db.OvertimeRequests.Add(request);
        await db.SaveChangesAsync();
        db.OvertimePayrollImpacts.Add(new OvertimePayrollImpact
        {
            TenantId = tenantId, OvertimeRequestId = request.Id, EmployeeId = empId,
            Minutes = (int)Math.Round(hours * 60m), Amount = 0m, Status = "PendingPayroll",
        });
    }

    /// <summary>Requested by one person and approved by another — the real maker-checker path.</summary>
    private static async Task ApproveOverrideAsync(ZayraDbContext db, Fixture f, string key, string value)
    {
        var created = await Rates(db, f.TenantId, Guid.NewGuid()).CreateStatutoryOverride(new StatutoryOverrideRequest(
            f.CompanyA, "SAU", f.Jurisdiction, key, value, new DateOnly(2026, 6, 1), "Collective agreement clause 7", new DateOnly(2027, 6, 1)), default);
        var ok = Assert.IsType<OkObjectResult>(created);
        var id = (Guid)ok.Value!.GetType().GetProperty("Id")!.GetValue(ok.Value)!;
        Assert.IsType<OkObjectResult>(await Rates(db, f.TenantId, Guid.NewGuid()).ApproveStatutoryOverride(id, default));
    }

    private static async Task<decimal> LineAmount(ZayraDbContext db, Guid tenantId, int empId, string code, bool earning)
        => earning
            ? (await db.PayrollEarnings.AsNoTracking().Where(e => e.TenantId == tenantId && e.EmployeeId == empId && e.ComponentCode == code).SumAsync(e => (decimal?)e.Amount)) ?? 0m
            : (await db.PayrollDeductions.AsNoTracking().Where(d => d.TenantId == tenantId && d.EmployeeId == empId && d.ComponentCode == code).SumAsync(d => (decimal?)d.Amount)) ?? 0m;

    private static RatesController Rates(ZayraDbContext db, Guid tenantId, Guid userId)
    {
        var reader = new StatutoryRuleReader(db);
        return new RatesController(db, reader, new StatutoryRateResolver(db, reader))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Principal(tenantId, userId, "payroll.rates.statutory_override") } },
        };
    }

    private static PayrollController Payroll(ZayraDbContext db, Guid tenantId)
    {
        // The REAL reader: the company override must reach the payslip through the same reads payroll uses.
        var rules = new StatutoryRuleReader(db);
        return new PayrollController(
            db, new DataScopeService(db), new HttpContextAccessor(), new _OvrNullNotifications(),
            new _OvrDefaultPackResolver(), rules, new _OvrNullLetters(), new NullDocumentStorage(), new PdfRenderGate(8))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Principal(tenantId, Guid.NewGuid(), null, role: "Admin") } },
        };
    }

    private static ClaimsPrincipal Principal(Guid tenantId, Guid userId, string? permission, string? role = null)
    {
        var claims = new List<Claim> { new("tenant_id", tenantId.ToString()), new(ClaimTypes.NameIdentifier, userId.ToString()) };
        if (permission is not null) claims.Add(new Claim("permission", permission));
        if (role is not null) claims.Add(new Claim(ClaimTypes.Role, role));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }
}

// ── File-scoped stubs ─────────────────────────────────────────────────────────

file sealed class _OvrNullNotifications : INotificationService
{
    public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string en, string? eid, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
}

// The KSA deduction pack (payroll refuses to run without a registered pack) on its own stub rates, so
// GOSI stays fixed and only the overtime / absence lines under test can move.
file sealed class _OvrDefaultPackResolver : ICountryPackResolver
{
    private static readonly StubRuleReader GosiRates = new StubRuleReader()
        .Set("gosi.saudi_employee_rate", 0.09m).Set("gosi.saudi_employer_rate", 0.09m)
        .Set("gosi.saned_rate", 0.0075m).Set("gosi.expat_occupational_hazard_rate", 0.02m)
        .Set("gosi.covered_wage_ceiling_sar", 45_000m);
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) => new Zayra.Api.Infrastructure.CountryPack.Ksa.KsaDeductionCalculator(GosiRates);
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class _OvrNullLetters : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}
