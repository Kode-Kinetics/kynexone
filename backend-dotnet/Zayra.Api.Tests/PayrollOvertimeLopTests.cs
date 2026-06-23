using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Integration tests for Overtime earnings and LOP (Loss of Pay) deductions wired into payroll.
///
/// Tests run against a real Postgres container (shared PostgresFixture).
///
/// KSA defaults (FLAG-COMPLIANCE — requires Saudi labour-law sign-off before production filing):
///   OT:  hourly = basic / 240 (standard monthly hours); OT pay = hours × hourly × 1.5
///   LOP: day-rate = basic / 30; deduction = absent-days × day-rate
///   GOSI covered wage = basic + housing (OT excluded; LOP effect on GOSI base flagged separately)
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class PayrollOvertimeLopTests
{
    private readonly PostgresFixture _fx;
    public PayrollOvertimeLopTests(PostgresFixture fx) => _fx = fx;

    // ── (1) 10 OT hours → amount = hours × (basic/240) × 1.5 ──────────────────

    [Fact]
    public async Task OvertimeOnly_TenHours_Correct1Point5xAmount()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var (emp, run) = await SeedPayrollContext(db, tenantId, basic: 12_000m, housing: 3_000m);

        // 10 OT hours in June 2026
        await SeedOvertimeImpact(db, tenantId, emp.Id, new DateOnly(2026, 6, 10), hours: 10m);
        await db.SaveChangesAsync();

        var result = await BuildCtrl(db, tenantId).Process(run.Id, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);

        var slip = await db.PayrollSlips.AsNoTracking()
            .FirstAsync(s => s.TenantId == tenantId && s.EmployeeId == emp.Id);

        // hourly = 12,000 / 240 = 50; OT = 10 × 50 × 1.5 = 750
        var expectedOt = Math.Round(10m * (12_000m / 240m) * 1.5m, 2);
        var otLine = await db.PayrollEarnings.AsNoTracking()
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.EmployeeId == emp.Id && e.ComponentCode == "OVERTIME");
        Assert.NotNull(otLine);
        Assert.Equal(expectedOt, otLine.Amount);

        // Gross includes OT
        var expectedGross = 12_000m + 3_000m + expectedOt; // basic + housing + OT
        Assert.Equal(expectedGross, slip.GrossSalary, precision: 2);
    }

    // ── (2) 3 absent days → LOP = days × (basic/30) ────────────────────────────

    [Fact]
    public async Task LopOnly_ThreeAbsentDays_CorrectDayRateDeduction()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var (emp, run) = await SeedPayrollContext(db, tenantId, basic: 9_000m, housing: 2_000m);

        // 3 absent days = 3 × 480 minutes
        SeedAttendanceImpact(db, tenantId, emp.Id, new DateOnly(2026, 6, 5), "Absence", 480);
        SeedAttendanceImpact(db, tenantId, emp.Id, new DateOnly(2026, 6, 6), "Absence", 480);
        SeedAttendanceImpact(db, tenantId, emp.Id, new DateOnly(2026, 6, 7), "Absence", 480);
        SeedAttendanceDailyRecord(db, tenantId, emp.Id, new DateOnly(2026, 6, 5));
        await db.SaveChangesAsync();

        var result = await BuildCtrl(db, tenantId).Process(run.Id, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);

        // LOP = 3 days × (9,000 / 30) = 3 × 300 = 900
        var expectedLop = Math.Round(3m * (9_000m / 30m), 2);
        var lopLine = await db.PayrollDeductions.AsNoTracking()
            .FirstOrDefaultAsync(d => d.TenantId == tenantId && d.EmployeeId == emp.Id && d.ComponentCode == "LOP_DEDUCTION");
        Assert.NotNull(lopLine);
        Assert.Equal(expectedLop, lopLine.Amount);

        // Source must be "Attendance" for GL routing to account 2104
        Assert.Equal("Attendance", lopLine.Source);
    }

    // ── (3) OT + LOP combined: gross = base + OT − LOP − GOSI ─────────────────

    [Fact]
    public async Task OtAndLop_Combined_GrossAndNetCorrect()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var (emp, run) = await SeedPayrollContext(db, tenantId, basic: 10_000m, housing: 3_000m, nationality: "Indian");

        await SeedOvertimeImpact(db, tenantId, emp.Id, new DateOnly(2026, 6, 15), hours: 5m);
        SeedAttendanceImpact(db, tenantId, emp.Id, new DateOnly(2026, 6, 3), "Absence", 480);
        SeedAttendanceDailyRecord(db, tenantId, emp.Id, new DateOnly(2026, 6, 3));
        await db.SaveChangesAsync();

        var result = await BuildCtrl(db, tenantId).Process(run.Id, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);

        // OT = 5 × (10,000/240) × 1.5 = 5 × 41.6667 × 1.5 ≈ 312.50
        var hourlyRate = 10_000m / 240m;
        var expectedOt  = Math.Round(5m * hourlyRate * 1.5m, 2);
        // LOP = 1 × (10,000/30) = 333.33
        var expectedLop = Math.Round(1m * (10_000m / 30m), 2);

        var slip = await db.PayrollSlips.AsNoTracking()
            .FirstAsync(s => s.TenantId == tenantId && s.EmployeeId == emp.Id);

        // Expat: no EE GOSI, so net = base + housing + OT − LOP
        var expectedNet = 10_000m + 3_000m + expectedOt - expectedLop;
        Assert.Equal(expectedNet, slip.NetSalary, precision: 2);
    }

    // ── (4) No attendance/OT data → WARN_NO_ATTENDANCE ─────────────────────────

    [Fact]
    public async Task NoAttendanceData_ActiveEmployee_ProducesWarnNoAttendance()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var (emp, run) = await SeedPayrollContext(db, tenantId, basic: 8_000m, housing: 2_000m);

        // No attendance impacts, no OT, no daily records seeded.
        var result = await BuildCtrl(db, tenantId).Process(run.Id, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);

        var warnings = await db.PayrollValidationResults.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.PayrollRunId == run.Id && r.Code == "WARN_NO_ATTENDANCE")
            .ToListAsync();
        Assert.NotEmpty(warnings);
        Assert.All(warnings, w => Assert.Equal("Warning", w.Severity));
    }

    // ── (5) LOP exceeds full salary → net clamped to 0, ZERO_NET_WITH_GROSS error blocks ─

    [Fact]
    public async Task LopExceedsSalary_NetClampedToZero_ZeroNetWithGrossErrorBlocks()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        // Expat so GOSI won't reduce net further and muddy the assertion
        var (emp, run) = await SeedPayrollContext(db, tenantId, basic: 1_000m, housing: 0m, nationality: "Indian");

        // 40 absent-day-equivalents: 20 records × 960 min (2 × 480) → lopDays = 40
        // lopDeduction = 40 × (1000/30) ≈ 1333.33 > gross 1000 → net = max(0, -333.33) = 0
        for (int d = 1; d <= 20; d++)
            SeedAttendanceImpact(db, tenantId, emp.Id, new DateOnly(2026, 6, d), "Absence", 960);
        await db.SaveChangesAsync();

        var result = await BuildCtrl(db, tenantId).Process(run.Id, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);

        // Net is clamped to 0 (never negative), so validation fires ZERO_NET_WITH_GROSS
        var errors = await db.PayrollValidationResults.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.PayrollRunId == run.Id && r.Code == "ZERO_NET_WITH_GROSS")
            .ToListAsync();
        Assert.NotEmpty(errors);
        Assert.All(errors, e => Assert.Equal("Error", e.Severity));

        // Approve is blocked (422) while this error exists
        var approveResult = await BuildCtrl(db, tenantId)
            .Approve(run.Id, new PayrollDecisionRequest(null), CancellationToken.None);
        var objResult = Assert.IsAssignableFrom<ObjectResult>(approveResult);
        Assert.Equal(422, objResult.StatusCode);
    }

    // ── (6) GOSI base = basic + housing, OT excluded ──────────────────────────────

    [Fact]
    public async Task Gosi_CoveredWage_ExcludesOvertimePay()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        // Saudi employee so GOSI applies
        var (emp, run) = await SeedPayrollContext(db, tenantId, basic: 10_000m, housing: 3_000m, nationality: "Saudi");

        // 20 OT hours — should NOT affect GOSI covered wage
        await SeedOvertimeImpact(db, tenantId, emp.Id, new DateOnly(2026, 6, 20), hours: 20m);
        await db.SaveChangesAsync();

        var result = await BuildCtrl(db, tenantId).Process(run.Id, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);

        var slip = await db.PayrollSlips.AsNoTracking()
            .FirstAsync(s => s.TenantId == tenantId && s.EmployeeId == emp.Id);

        // GOSI EE = (10,000 + 3,000) × 9.75% = 1,267.50  (not affected by OT)
        var expectedGosiEe = Math.Round(13_000m * 0.0975m, 2);
        Assert.Equal(expectedGosiEe, slip.EmployeeStatutoryTotal, precision: 2);

        // OT pay must be present in earnings
        var otLine = await db.PayrollEarnings.AsNoTracking()
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.EmployeeId == emp.Id && e.ComponentCode == "OVERTIME");
        Assert.NotNull(otLine);
        Assert.True(otLine.Amount > 0);
    }

    // ── (7) Rasalmanar baseline regression: OT/LOP=0 → existing computation unchanged ──

    [Fact]
    public async Task NoOtNoLop_SaudiEmployee_GosiAndNetUnchanged()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var (emp, run) = await SeedPayrollContext(db, tenantId, basic: 15_000m, housing: 5_000m, nationality: "Saudi");

        // No OT, no attendance — baseline run
        var result = await BuildCtrl(db, tenantId).Process(run.Id, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);

        var slip = await db.PayrollSlips.AsNoTracking()
            .FirstAsync(s => s.TenantId == tenantId && s.EmployeeId == emp.Id);

        // Gross = basic + housing = 20,000
        Assert.Equal(20_000m, slip.GrossSalary, precision: 2);

        // GOSI EE = 20,000 × 9.75% = 1,950
        Assert.Equal(Math.Round(20_000m * 0.0975m, 2), slip.EmployeeStatutoryTotal, precision: 2);

        // Net = gross − GOSI EE
        Assert.Equal(slip.GrossSalary - slip.EmployeeStatutoryTotal, slip.NetSalary, precision: 2);

        // No OT or LOP lines
        var hasOt  = await db.PayrollEarnings.AnyAsync(e => e.TenantId == tenantId && e.EmployeeId == emp.Id && e.ComponentCode == "OVERTIME");
        var hasLop = await db.PayrollDeductions.AnyAsync(d => d.TenantId == tenantId && d.EmployeeId == emp.Id && d.ComponentCode == "LOP_DEDUCTION");
        Assert.False(hasOt,  "No OT earnings should exist when no overtime was recorded");
        Assert.False(hasLop, "No LOP deduction should exist when no absences were recorded");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>Seeds company + employee + salary + payroll profile + run in Draft status.</summary>
    private static async Task<(Zayra.Api.Models.Employee emp, PayrollRun run)> SeedPayrollContext(
        ZayraDbContext db, Guid tenantId, decimal basic, decimal housing, string nationality = "Saudi")
    {
        var company = new Company
        {
            TenantId           = tenantId,
            LegalNameEn        = $"OT-LOP Test Co {Guid.NewGuid():N}",
            CountryCode        = "SAU",
            Jurisdiction       = "KSA-mainland",
            RegistrationNumber = $"OT-REG-{Guid.NewGuid():N}",
            DefaultCurrency    = "SAR",
            IsActive           = true,
            CreatedAtUtc       = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Companies.Add(company);

        var emp = new Zayra.Api.Models.Employee
        {
            TenantId     = tenantId,
            EmployeeCode = $"OT-{Guid.NewGuid():N}",
            FullName     = "OT LOP Test Employee",
            Nationality  = nationality,
            Status       = "Active",
            JoiningDate  = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();

        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId          = tenantId,
            EmployeeId        = emp.Id,
            SalaryStructureId = Guid.NewGuid(),
            BasicSalary       = basic,
            HousingAllowance  = housing,
            EffectiveDate     = new DateOnly(2024, 1, 1),
            IsActive          = true,
        });
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
        {
            TenantId        = tenantId,
            EmployeeId      = emp.Id,
            Iban            = "SA4420000001234567891234",
            MolId           = $"MOL-OT-{Guid.NewGuid():N}",
            SalaryCurrency  = "SAR",
        });

        var run = new PayrollRun
        {
            TenantId     = tenantId,
            CompanyId    = company.Id,
            Year         = 2026,
            Month        = 6,
            Status       = "Draft",
            CreatedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        return (emp, run);
    }

    private static async Task SeedOvertimeImpact(
        ZayraDbContext db, Guid tenantId, int empId, DateOnly workDate, decimal hours)
    {
        var startHour = 17;
        var endDateTime = new DateTime(workDate.Year, workDate.Month, workDate.Day, startHour, 0, 0, DateTimeKind.Utc)
            .AddHours((double)hours);
        var request = new OvertimeRequest
        {
            TenantId          = tenantId,
            EmployeeId        = empId,
            EmployeeName      = "OT LOP Test Employee",
            WorkDate          = workDate,
            StartTimeUtc      = new DateTime(workDate.Year, workDate.Month, workDate.Day, startHour, 0, 0, DateTimeKind.Utc),
            EndTimeUtc        = endDateTime,
            RequestedMinutes  = (int)(hours * 60),
            ApprovedMinutes   = (int)(hours * 60),
            Status            = "Approved",
        };
        db.OvertimeRequests.Add(request);
        await db.SaveChangesAsync();

        db.OvertimePayrollImpacts.Add(new OvertimePayrollImpact
        {
            TenantId         = tenantId,
            OvertimeRequestId = request.Id,
            EmployeeId       = empId,
            Hours            = hours,
            Amount           = 0m, // amount is recomputed from hours × rate × multiplier; stored 0 here
            Status           = "PendingPayroll",
        });
    }

    private static void SeedAttendanceImpact(
        ZayraDbContext db, Guid tenantId, int empId, DateOnly workDate, string impactType, int minutes)
    {
        db.AttendancePayrollImpacts.Add(new AttendancePayrollImpact
        {
            TenantId   = tenantId,
            EmployeeId = empId,
            WorkDate   = workDate,
            ImpactType = impactType,
            Minutes    = minutes,
            Status     = "PendingPayroll",
        });
    }

    private static void SeedAttendanceDailyRecord(
        ZayraDbContext db, Guid tenantId, int empId, DateOnly workDate)
    {
        db.AttendanceDailyRecords.Add(new AttendanceDailyRecord
        {
            TenantId         = tenantId,
            EmployeeId       = empId,
            WorkDate         = workDate,
            Status           = "Absent",
            ProcessedAtUtc   = new DateTime(workDate.Year, workDate.Month, workDate.Day, 0, 0, 0, DateTimeKind.Utc),
            CreatedAtUtc     = new DateTime(workDate.Year, workDate.Month, workDate.Day, 0, 0, 0, DateTimeKind.Utc),
        });
    }

    private static PayrollController BuildCtrl(ZayraDbContext db, Guid tenantId, string role = "Admin")
    {
        var rules = new StubRuleReader()
            .Set("gosi.saudi_employee_rate",            0.09m)
            .Set("gosi.saudi_employer_rate",            0.09m)
            .Set("gosi.saned_rate",                     0.0075m)
            .Set("gosi.expat_occupational_hazard_rate", 0.02m)
            .Set("gosi.covered_wage_ceiling_sar",       45_000m)
            .Set("ot.standard_multiplier",              1.5m)
            .Set("ot.standard_monthly_hours",           240m)
            .Set("lop.monthly_day_divisor",             30m)
            .Set("lop.standard_work_minutes_per_day",   480m);

        var ctrl = new PayrollController(
            db,
            new DataScopeService(db),
            new HttpContextAccessor(),
            new _OtLopNullNotifications(),
            new _OtLopKsaPackResolver(rules),
            rules,
            new _OtLopNullLetterService(),
            new NullDocumentStorage(),
            new PdfRenderGate(8));

        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id",               tenantId.ToString()),
                    new Claim(System.Security.Claims.ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(System.Security.Claims.ClaimTypes.Role, role),
                }, "test")),
            },
        };
        return ctrl;
    }
}

// ── File-scoped stubs ─────────────────────────────────────────────────────────

file sealed class _OtLopNullNotifications : INotificationService
{
    public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string en, string? eid, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _OtLopKsaPackResolver : ICountryPackResolver
{
    private readonly StubRuleReader _rules;
    public _OtLopKsaPackResolver(StubRuleReader rules) => _rules = rules;

    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => new KsaDeductionCalculator(_rules);
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j)
        => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j)
        => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j)
        => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j)
        => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j)
        => new DefaultCountryPackDescriptor();
}

file sealed class _OtLopNullLetterService : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}
