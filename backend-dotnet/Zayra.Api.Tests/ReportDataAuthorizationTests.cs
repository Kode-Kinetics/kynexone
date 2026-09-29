using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Reports;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// F10 — a report is authorized against the data it shows, not only against <c>reports.read</c>.
///
/// <para>Before this, any role holding <c>reports.read</c> — the Compliance Officer, say — could run
/// the payroll register, the loan book and the passport list of the whole workforce, because the
/// report catalog checked the reporting capability and nothing else. Each test below failed on main by
/// being served the data (a 200) rather than refused.</para>
/// </summary>
public sealed class ReportDataAuthorizationTests
{
    private static readonly GovernanceOverrideRequest Override =
        new("GRC-2026-0101", "Scheduled pack approved by the reporting lead.", true);

    // ── Data permissions ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PayrollAndLoanReports_WithoutTheirDataPermission_AreRefusedWithAReason()
    {
        await using var db = Db();
        var tid = await SeedPayrollAsync(db, (2026, 9, "Locked"));
        // A Compliance Officer's shape: reports.read/export, employee and compliance rights, no payroll.
        var ctrl = Reports(db, tid, "reports.read", "reports.export", "employees.read", "compliance.read");

        foreach (var key in new[] { "payroll.register", "payroll.summary", "payroll.slips", "finance.bonus-payout" })
            AssertForbiddenWithReason(await ctrl.RunReport(new RunReportRequest(key, null), default), "payroll.read");
        AssertForbiddenWithReason(
            await ctrl.RunReport(new RunReportRequest("finance.loan-balance", null), default), "loans.read");
        AssertForbiddenWithReason(
            await ctrl.ExportReport(new ExportReportRequest("payroll.register", null, "csv"), default), "payroll.read");

        // Refused before execution: nothing ran, so nothing was logged as a successful run.
        Assert.Equal(0, await db.ReportExecutionLogs.CountAsync());
    }

    [Fact]
    public async Task PayrollRegister_WithPayrollRead_IsStillServed()
    {
        await using var db = Db();
        var tid = await SeedPayrollAsync(db, (2026, 9, "Locked"));
        var ctrl = Reports(db, tid, "reports.read", "employees.read", "payroll.read");

        var ok = Assert.IsType<OkObjectResult>(await ctrl.RunReport(new RunReportRequest("payroll.register", null), default));

        Assert.Contains("SLIP-2026-9", JsonSerializer.Serialize(ok.Value));
    }

    [Fact]
    public void Catalog_ListsOnlyReportsWhoseDataTheCallerMayRead()
    {
        using var db = Db();
        var ctrl = Reports(db, Guid.NewGuid(), "reports.read", "employees.read", "compliance.read");

        var keys = CatalogKeys(ctrl);

        Assert.DoesNotContain("payroll.register", keys);
        Assert.DoesNotContain("payroll.summary", keys);
        Assert.DoesNotContain("finance.loan-balance", keys);
        Assert.DoesNotContain("finance.bonus-payout", keys);
        Assert.DoesNotContain("attendance.daily", keys);
        Assert.DoesNotContain("recruitment.pipeline", keys);
        Assert.Contains("hr.headcount", keys);
        Assert.Contains("compliance.passport-expiry", keys);
        Assert.Contains("qiwa.readiness", keys);
    }

    [Fact]
    public async Task SavingOrSchedulingAReport_ForDataTheCallerCannotRead_IsRefused()
    {
        await using var db = Db();
        var tid = await SeedTenantAsync(db);
        var ctrl = Reports(db, tid, "reports.read", "reports.schedule", "employees.read");

        AssertForbiddenWithReason(await ctrl.SaveReport(
            new SaveReportRequest("payroll.register", "Payroll", "Payroll", null, null, false), default), "payroll.read");
        // A governance override authorises the schedule as a governance act; it does not grant payroll data.
        AssertForbiddenWithReason(await ctrl.CreateSchedule(
            new CreateScheduleRequest("payroll.summary", "Payroll Summary", "Payroll", null, "Monthly", "Email",
                "finance@example.test", "csv", Override), default), "payroll.read");

        Assert.Equal(0, await db.SavedReports.CountAsync());
        Assert.Equal(0, await db.ReportSchedules.CountAsync());
    }

    [Fact]
    public async Task SavedAndScheduledReports_ForDataTheCallerCannotRead_AreNotListed()
    {
        await using var db = Db();
        var tid = await SeedTenantAsync(db);
        db.SavedReports.AddRange(
            new SavedReport { TenantId = tid, ReportKey = "payroll.register", Name = "Shared payroll", Category = "Payroll", IsShared = true, CreatedBy = Guid.NewGuid() },
            new SavedReport { TenantId = tid, ReportKey = "hr.headcount", Name = "Shared headcount", Category = "HR", IsShared = true, CreatedBy = Guid.NewGuid() });
        db.ReportSchedules.AddRange(
            new ReportSchedule { TenantId = tid, ReportKey = "payroll.summary", ReportName = "Monthly payroll", Category = "Payroll", Frequency = "Monthly", DeliveryMethod = "Email" },
            new ReportSchedule { TenantId = tid, ReportKey = "hr.headcount", ReportName = "Monthly headcount", Category = "HR", Frequency = "Monthly", DeliveryMethod = "Email" });
        await db.SaveChangesAsync();
        var ctrl = Reports(db, tid, "reports.read", "reports.schedule", "employees.read");

        var saved = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await ctrl.ListSavedReports(default)).Value);
        var schedules = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await ctrl.ListSchedules(default)).Value);

        Assert.Contains("Shared headcount", saved);
        Assert.DoesNotContain("Shared payroll", saved);
        Assert.Contains("Monthly headcount", schedules);
        Assert.DoesNotContain("Monthly payroll", schedules);
    }

    // ── Sensitive fields ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PassportExpiry_MasksPassportNumbers_UnlessTheCallerHoldsEmployeesSensitive()
    {
        await using var db = Db();
        var tid = await SeedIdentityDocumentsAsync(db);

        var withoutSensitive = Reports(db, tid, "reports.read", "employees.read", "compliance.read");
        var masked = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(
            await withoutSensitive.RunReport(new RunReportRequest("compliance.passport-expiry", null), default)).Value);
        Assert.DoesNotContain("P1234567", masked);
        Assert.Contains("Restricted", masked);
        // The expiry itself is the compliance fact, so the row is kept, and the response says what was held back.
        Assert.Contains("Passport Holder", masked);
        Assert.Contains("\"restrictedFields\":[\"PassportNumber\"]", masked);

        var withSensitive = Reports(db, tid, "reports.read", "employees.read", "compliance.read", "employees.sensitive");
        var clear = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(
            await withSensitive.RunReport(new RunReportRequest("compliance.passport-expiry", null), default)).Value);
        Assert.Contains("P1234567", clear);
    }

    [Fact]
    public async Task VisaExpiryExport_MasksVisaNumbers_UnlessTheCallerHoldsEmployeesSensitive()
    {
        await using var db = Db();
        var tid = await SeedIdentityDocumentsAsync(db);
        var ctrl = Reports(db, tid, "reports.read", "reports.export", "employees.read", "compliance.read");

        var file = Assert.IsType<FileContentResult>(
            await ctrl.ExportReport(new ExportReportRequest("compliance.visa-expiry", null, "csv"), default));
        var csv = Encoding.UTF8.GetString(file.FileContents);

        Assert.DoesNotContain("V-998877", csv);
        Assert.Contains("Restricted", csv);
        Assert.Contains("Passport Holder", csv);
    }

    // ── Period and run selection ──────────────────────────────────────────────────────────

    [Fact]
    public async Task PayrollRegisterAndSummary_HonourTheRequestedPeriod()
    {
        await using var db = Db();
        var tid = await SeedPayrollAsync(db, (2026, 8, "Locked"), (2026, 9, "Locked"));
        var ctrl = Reports(db, tid, "reports.read", "employees.read", "payroll.read");
        var august = new ReportFilters { Period = "2026-08" };

        var register = Data(await ctrl.RunReport(new RunReportRequest("payroll.register", august), default));
        Assert.Equal(["SLIP-2026-8"], register.EnumerateArray().Select(r => r.GetProperty("EmployeeCode").GetString()));

        var summary = Data(await ctrl.RunReport(new RunReportRequest("payroll.summary", august), default));
        Assert.Equal(1000m, Assert.Single(summary.EnumerateArray()).GetProperty("TotalNet").GetDecimal());
    }

    [Fact]
    public async Task PayrollRegister_WithoutAPeriod_SkipsAVoidedRun()
    {
        await using var db = Db();
        var tid = await SeedPayrollAsync(db, (2026, 8, "Locked"), (2026, 9, "Voided"));
        var ctrl = Reports(db, tid, "reports.read", "employees.read", "payroll.read");

        var register = Data(await ctrl.RunReport(new RunReportRequest("payroll.register", null), default));

        Assert.Equal(["SLIP-2026-8"], register.EnumerateArray().Select(r => r.GetProperty("EmployeeCode").GetString()));
    }

    [Fact]
    public async Task AMalformedPeriod_IsRejected_NotQuietlyReplacedByTheLatestRun()
    {
        await using var db = Db();
        var tid = await SeedPayrollAsync(db, (2026, 9, "Locked"));
        var ctrl = Reports(db, tid, "reports.read", "employees.read", "payroll.read");

        var result = await ctrl.RunReport(
            new RunReportRequest("payroll.register", new ReportFilters { Period = "Sept 2026" }), default);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("YYYY-MM", JsonSerializer.Serialize(bad.Value));
    }

    // ── Scope ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnOrganisationWideReport_ForATeamScopedCaller_IsRefused_NotServedWhole()
    {
        await using var db = Db();
        var tid = await SeedTenantAsync(db);
        db.JobApplications.Add(new JobApplication { TenantId = tid, JobTitle = "Engineer", CandidateName = "Cand", Stage = "Screening", Status = "Active" });
        await db.SaveChangesAsync();
        // manager.read narrows employee data to the caller's team; recruitment rows are not employees,
        // so the report cannot be narrowed and must not be served organisation-wide instead.
        var ctrl = Reports(db, tid, "reports.read", "employees.read", "manager.read", "recruitment.read");

        AssertForbiddenWithReason(
            await ctrl.RunReport(new RunReportRequest("recruitment.pipeline", null), default), "organisation");
    }

    [Fact]
    public async Task BonusPayout_ForACompanyScopedCaller_IsRefused_BecauseBatchesSpanTheGroup()
    {
        await using var db = Db();
        var tid = await SeedTenantAsync(db);
        db.BonusBatches.Add(new BonusBatch { TenantId = tid, BatchNumber = "BON-1", BatchName = "Group bonus", PaymentPeriod = "2026-09", TotalAmount = 50000, EmployeeCount = 10 });
        await db.SaveChangesAsync();
        var ctrl = Reports(db, tid, CompanyClaim(Guid.NewGuid()), "reports.read", "employees.read", "payroll.read");

        AssertForbiddenWithReason(
            await ctrl.RunReport(new RunReportRequest("finance.bonus-payout", null), default), "every company");
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────

    internal static void AssertForbiddenWithReason(IActionResult result, string expectedFragment)
    {
        // ObjectResult, not ForbidResult: the refusal carries a reason the UI can show. And 403, never
        // a 200 with an empty list that reads as "there is no data".
        var refused = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, refused.StatusCode);
        Assert.Contains(expectedFragment, JsonSerializer.Serialize(refused.Value), StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement Data(IActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value).GetProperty("data");

    private static string?[] CatalogKeys(ReportsController ctrl) =>
        JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(ctrl.GetCatalog()).Value)
            .EnumerateArray().Select(x => x.GetProperty("key").GetString()).ToArray();

    private static ZayraDbContext Db() => new(
        new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static Claim CompanyClaim(Guid companyId) =>
        new(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { companyId } }));

    private static ReportsController Reports(ZayraDbContext db, Guid tid, params string[] permissions) =>
        Reports(db, tid, null, permissions);

    private static ReportsController Reports(ZayraDbContext db, Guid tid, Claim? scopeClaim, params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tid.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "Report User"),
        };
        if (scopeClaim is not null) claims.Add(scopeClaim);
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        return new ReportsController(db, new DataScopeService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
            },
        };
    }

    private static async Task<Guid> SeedTenantAsync(ZayraDbContext db)
    {
        var tid = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tid, Name = "Reports", Slug = $"reports-{tid:N}" });
        await db.SaveChangesAsync();
        return tid;
    }

    /// <summary>One run per (year, month, status), each with one slip whose code names its period.</summary>
    private static async Task<Guid> SeedPayrollAsync(ZayraDbContext db, params (int Year, int Month, string Status)[] runs)
    {
        var tid = await SeedTenantAsync(db);
        var employeeId = 1;
        foreach (var (year, month, status) in runs)
        {
            var run = new PayrollRun { TenantId = tid, Year = year, Month = month, Status = status };
            db.PayrollRuns.Add(run);
            db.PayrollSlips.Add(new PayrollSlip
            {
                TenantId = tid, RunId = run.Id, EmployeeId = employeeId++, EmployeeCode = $"SLIP-{year}-{month}",
                EmployeeName = $"Person {month}", Department = "Finance",
                // August nets 1,000 and September 2,000, so a summary total names the run it came from.
                GrossSalary = (month - 7) * 1000 + 200, Deductions = 200, NetSalary = (month - 7) * 1000,
                Status = "Paid",
            });
        }
        await db.SaveChangesAsync();
        return tid;
    }

    private static async Task<Guid> SeedIdentityDocumentsAsync(ZayraDbContext db)
    {
        var tid = await SeedTenantAsync(db);
        var employee = new Employee
        {
            TenantId = tid, EmployeeCode = "E-PP", FullName = "Passport Holder", Status = "Active",
            JoiningDate = DateTime.UtcNow.AddYears(-2),
        };
        db.Employees.Add(employee);
        var expiry = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(20));
        db.PassportRecords.Add(new PassportRecord
        {
            TenantId = tid, EmployeeId = employee.PublicId, EmployeeName = employee.FullName,
            PassportNumber = "P1234567", Nationality = "Indian", ExpiryDate = expiry, Status = "Active",
        });
        db.VisaRecords.Add(new VisaRecord
        {
            TenantId = tid, EmployeeId = employee.PublicId, EmployeeName = employee.FullName,
            VisaType = "Residence", VisaNumber = "V-998877", ExpiryDate = expiry, Status = "Active",
        });
        await db.SaveChangesAsync();
        return tid;
    }
}
