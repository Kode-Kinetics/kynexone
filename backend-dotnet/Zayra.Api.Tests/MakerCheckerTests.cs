using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Maker-checker enforcement: the user who processes a payroll run (calls /process)
/// cannot approve it. Enforced server-side at the Approve API — no bypass via UI
/// or direct API call.
///
/// Coverage:
///   (a) Processor attempts self-approval → 403
///   (b) Distinct approver → 200 (approved)
///   (c) API bypass: processor forges approve call directly → still 403
/// </summary>
public class MakerCheckerTests
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static PayrollController MakeCtrl(
        ZayraDbContext db, Guid tenantId, Guid userId, string role = "Admin")
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Role, role),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var httpCtx   = new DefaultHttpContext { User = principal };

        var ctrl = new PayrollController(
            db,
            new _MakerUnrestrictedScope(),
            new _MakerHttpAccessor(httpCtx),
            new _MakerNullNotifications(),
            new _MakerNullPackResolver(),
            new _MakerNullLetterService());
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }

    private static async Task<PayrollRun> SeedProcessedRun(
        ZayraDbContext db, Guid tenantId, Guid processorId)
    {
        var run = new PayrollRun
        {
            TenantId        = tenantId,
            Year            = 2026,
            Month           = 6,
            Status          = "Processed",
            ProcessedByUserId = processorId,
            TotalNetSalary  = 0m,
            CreatedByUserId = processorId,
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    // ── (a) Processor cannot self-approve ─────────────────────────────────────

    [Fact]
    public async Task SelfApprove_WhenProcessorCallsApprove_Returns403()
    {
        var db          = CreateDb();
        var tenantId    = Guid.NewGuid();
        var processorId = Guid.NewGuid();

        var run = await SeedProcessedRun(db, tenantId, processorId);

        // Processor calls /approve with the same user ID that processed the run.
        var ctrl   = MakeCtrl(db, tenantId, processorId, role: "Admin");
        var result = await ctrl.Approve(run.Id, new PayrollDecisionRequest(null), CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, status.StatusCode);

        var body = System.Text.Json.JsonSerializer.Serialize(status.Value);
        Assert.Contains("maker_checker_violation", body);
    }

    // ── (b) Distinct approver succeeds ────────────────────────────────────────

    [Fact]
    public async Task DistinctApprover_WhenDifferentUser_Returns200()
    {
        var db          = CreateDb();
        var tenantId    = Guid.NewGuid();
        var processorId = Guid.NewGuid();
        var approverId  = Guid.NewGuid(); // different user

        var run = await SeedProcessedRun(db, tenantId, processorId);

        // Seed a PayrollApprovals table row doesn't exist yet — nothing to block.
        // No validation errors seeded, so the engine check passes.
        var ctrl   = MakeCtrl(db, tenantId, approverId, role: "Admin");
        var result = await ctrl.Approve(run.Id, new PayrollDecisionRequest("Approved by Finance"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(200, ok.StatusCode);

        // Run should now be Approved in DB.
        var updated = await db.PayrollRuns.FindAsync(run.Id);
        Assert.Equal("Approved", updated!.Status);
    }

    // ── (c) API bypass: processor forges approve call directly → 403 ──────────

    [Fact]
    public async Task ApiBypass_ProcessorForgesApproveCallDirectly_Returns403()
    {
        // Scenario: a user who processed the run has an elevated role (Admin) and
        // attempts to call the Approve endpoint directly (no UI, raw HTTP) to bypass
        // the maker-checker control. The server-side check still rejects them.

        var db          = CreateDb();
        var tenantId    = Guid.NewGuid();
        var processorId = Guid.NewGuid();

        var run = await SeedProcessedRun(db, tenantId, processorId);

        // Even with Admin role + direct controller call, processor is blocked.
        var ctrl   = MakeCtrl(db, tenantId, processorId, role: "Admin");
        var result = await ctrl.Approve(run.Id, new PayrollDecisionRequest("I am admin, let me approve"), CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, status.StatusCode);

        var body = System.Text.Json.JsonSerializer.Serialize(status.Value);
        Assert.Contains("maker_checker_violation", body);

        // Run must still be in Processed state — not advanced.
        var unchanged = await db.PayrollRuns.FindAsync(run.Id);
        Assert.Equal("Processed", unchanged!.Status);
    }

    // ── Edge: null ProcessedByUserId (legacy run) allows approval ────────────

    [Fact]
    public async Task LegacyRun_WithNullProcessedBy_AllowsSelfApprove()
    {
        // Runs created before this feature (ProcessedByUserId == null) must not be
        // blocked — we have no evidence of who processed them.
        var db         = CreateDb();
        var tenantId   = Guid.NewGuid();
        var userId     = Guid.NewGuid();

        var run = new PayrollRun
        {
            TenantId          = tenantId,
            Year              = 2026,
            Month             = 5,
            Status            = "Processed",
            ProcessedByUserId = null, // legacy: no processor recorded
            TotalNetSalary    = 0m,
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();

        var ctrl   = MakeCtrl(db, tenantId, userId, role: "Admin");
        var result = await ctrl.Approve(run.Id, new PayrollDecisionRequest(null), CancellationToken.None);

        // Not blocked — 200 OK.
        Assert.IsType<OkObjectResult>(result);
    }
}

// ── File-scoped stubs (identical pattern to PayrollModuleTests stubs) ─────────

file sealed class _MakerUnrestrictedScope : IDataScopeService
{
    public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
}

file sealed class _MakerHttpAccessor : IHttpContextAccessor
{
    public _MakerHttpAccessor(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _MakerNullNotifications : INotificationService
{
    public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string en, string? eid, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _MakerNullPackResolver : ICountryPackResolver
{
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => new DefaultStatutoryDeductionCalculator();
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

file sealed class _MakerNullLetterService : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}
