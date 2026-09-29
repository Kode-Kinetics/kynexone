// PayslipEmployeeNameTests.cs
// ─────────────────────────────────────────────────────────────────────────────
// REGRESSION: the Payslips tab listed "Emp #1" … "Emp #14" instead of employee names.
//
// The Payslip entity is header-only and carries no name, and GET runs/{id}/payslips returned that
// raw entity, so the only identity the UI ever received was the numeric EmployeeId — while
// GET slips/{id}/pdf printed slip.EmployeeName ("Aisha Al-Harbi") on the document itself. Screen
// and document disagreed about who a payslip belonged to.
//
// These tests assert the API CONTRACT — a payslip row carries the employee's name — against the
// same denormalised source the PDF prints from, so the two can never drift apart again.
// ─────────────────────────────────────────────────────────────────────────────

using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Application.Finance;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class PayslipEmployeeNameTests
{
    private static (ZayraDbContext db, SqliteConnection conn) CreateDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static PayrollController MakeCtrl(ZayraDbContext db, Guid tenantId)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Name, "Payroll Officer"),
            new Claim("permission", "payroll.write"),
        }, "test"));
        var httpCtx = new DefaultHttpContext { User = principal };
        var ctrl = new PayrollController(
            db,
            new _PsnUnrestrictedScope(),
            new _PsnHttpAccessor(httpCtx),
            new _PsnNullNotifications(),
            new _PsnNullPackResolver(),
            new StubRuleReader(),
            new _PsnNullLetterService(),
            new NullDocumentStorage(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(1));
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }

    /// <summary>
    /// Seeds one locked October-2026 run with three employees, each with the PayrollSlip row the
    /// payslip PDF reads its name from. Payslip headers are NOT created here — GeneratePayslips
    /// creates them, exactly as the operator's "Generate Payslips" button does.
    /// </summary>
    private static Guid SeedRun(ZayraDbContext db, Guid tenantId)
    {
        var run = new PayrollRun
        {
            TenantId = tenantId, Year = 2026, Month = 10,
            Status = "Locked", RunType = "Regular",
        };
        db.PayrollRuns.Add(run);

        var people = new[]
        {
            (Id: 1, Code: "INTELLIFLOW-KSA-E1", Name: "Liu Wei"),
            (Id: 2, Code: "INTELLIFLOW-KSA-E2", Name: "Aisha Al-Harbi"),
            (Id: 3, Code: "INTELLIFLOW-KSA-E3", Name: "Grace Mwangi"),
        };
        foreach (var p in people)
        {
            db.Employees.Add(new Employee
            {
                Id = p.Id, TenantId = tenantId, EmployeeCode = p.Code, FullName = p.Name, Status = "Active",
            });
            db.PayrollSlips.Add(new PayrollSlip
            {
                TenantId = tenantId, RunId = run.Id, EmployeeId = p.Id,
                EmployeeCode = p.Code, EmployeeName = p.Name, Department = "Operations",
                BasicSalary = 10_000m, GrossSalary = 10_000m, NetSalary = 9_000m, Status = "Final",
            });
        }
        db.SaveChanges();
        return run.Id;
    }

    private static IReadOnlyList<PayslipListItemDto> Rows(IActionResult result) =>
        ((PagedResult<PayslipListItemDto>)((OkObjectResult)result).Value!).Items.ToList();

    /// <summary>
    /// THE defect: every listed payslip must name its employee. Before the fix the response carried
    /// no name at all and the operator's table fell back to "Emp #1".
    /// </summary>
    [Fact]
    public async Task ListPayslips_NamesEveryEmployee()
    {
        var (db, conn) = CreateDb();
        using var _ = conn;
        var tenantId = Guid.NewGuid();
        var runId = SeedRun(db, tenantId);
        var ctrl = MakeCtrl(db, tenantId);

        (await ctrl.GeneratePayslips(runId, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        var rows = Rows(await ctrl.ListPayslips(runId, 1, 50, CancellationToken.None));

        rows.Should().HaveCount(3);
        rows.Select(r => r.EmployeeName).Should()
            .BeEquivalentTo(new[] { "Liu Wei", "Aisha Al-Harbi", "Grace Mwangi" },
                "the payslip list must identify people by name, not by a placeholder code");
        rows.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.EmployeeName));
        rows.Should().NotContain(r => r.EmployeeName == $"Emp #{r.EmployeeId}");
        rows.Select(r => r.EmployeeCode).Should()
            .BeEquivalentTo(new[] { "INTELLIFLOW-KSA-E1", "INTELLIFLOW-KSA-E2", "INTELLIFLOW-KSA-E3" });
    }

    /// <summary>
    /// The list and the payslip PDF must name the same person. The PDF builds PayslipData from
    /// PayrollSlip.EmployeeName; the list must read that exact value, not the live Employees row —
    /// otherwise renaming an employee makes the screen disagree with an already-issued document.
    /// </summary>
    [Fact]
    public async Task ListPayslips_UsesTheSameNameThePdfPrints()
    {
        var (db, conn) = CreateDb();
        using var _ = conn;
        var tenantId = Guid.NewGuid();
        var runId = SeedRun(db, tenantId);
        var ctrl = MakeCtrl(db, tenantId);
        (await ctrl.GeneratePayslips(runId, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        // The employee marries and the HR record is updated. The October payslip was already issued
        // under the old name and the PDF still prints it, so the list must still print it too.
        var emp = db.Employees.Single(e => e.Id == 2);
        emp.FullName = "Aisha Al-Otaibi";
        await db.SaveChangesAsync();

        var row = Rows(await ctrl.ListPayslips(runId, 1, 50, CancellationToken.None)).Single(r => r.EmployeeId == 2);
        var pdfName = db.PayrollSlips.Single(s => s.RunId == runId && s.EmployeeId == 2).EmployeeName;

        row.EmployeeName.Should().Be(pdfName,
            "the list name is the payslip document's name — a screen that disagrees with the issued PDF "
            + "is worse than one that shows a code");
        row.EmployeeName.Should().Be("Aisha Al-Harbi");
    }

    /// <summary>
    /// Generate returns the same shape the list does — the UI must not receive a named row from one
    /// endpoint and an anonymous one from the other.
    /// </summary>
    [Fact]
    public async Task GeneratePayslips_ReturnsNamedRows()
    {
        var (db, conn) = CreateDb();
        using var _ = conn;
        var tenantId = Guid.NewGuid();
        var runId = SeedRun(db, tenantId);
        var ctrl = MakeCtrl(db, tenantId);

        var generated = (List<PayslipListItemDto>)((OkObjectResult)
            await ctrl.GeneratePayslips(runId, CancellationToken.None)).Value!;

        generated.Should().HaveCount(3);
        generated.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.EmployeeName));
        generated.Select(r => r.EmployeeName).Should().Contain("Aisha Al-Harbi");
    }

    /// <summary>
    /// A payslip whose PayrollSlip row is gone (a reopened run wipes slips but keeps payslips) still
    /// names its employee, from the live Employee record, rather than falling back to a code.
    /// </summary>
    [Fact]
    public async Task ListPayslips_FallsBackToTheEmployeeRecord_WhenTheSlipRowIsGone()
    {
        var (db, conn) = CreateDb();
        using var _ = conn;
        var tenantId = Guid.NewGuid();
        var runId = SeedRun(db, tenantId);
        var ctrl = MakeCtrl(db, tenantId);
        (await ctrl.GeneratePayslips(runId, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        db.PayrollSlips.RemoveRange(db.PayrollSlips.Where(s => s.RunId == runId));
        await db.SaveChangesAsync();

        var rows = Rows(await ctrl.ListPayslips(runId, 1, 50, CancellationToken.None));

        rows.Should().HaveCount(3, "losing the computed slips must not drop payslips off the page");
        rows.Select(r => r.EmployeeName).Should()
            .BeEquivalentTo(new[] { "Liu Wei", "Aisha Al-Harbi", "Grace Mwangi" });
    }
}

// ── File-scoped stubs ─────────────────────────────────────────────────────────

file sealed class _PsnUnrestrictedScope : IDataScopeService
{
    public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
}

file sealed class _PsnHttpAccessor : IHttpContextAccessor
{
    public _PsnHttpAccessor(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _PsnNullNotifications : INotificationService
{
    public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string entity, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _PsnNullLetterService : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class _PsnNullPackResolver : ICountryPackResolver
{
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) => new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}
