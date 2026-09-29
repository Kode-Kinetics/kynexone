using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Filters;
using Zayra.Api.Infrastructure.Modules;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Unit-tests for <see cref="FeatureFlagGuardFilter"/>.
/// Verifies that:
///   1. Disabled features return HTTP 403 with error = "feature_not_enabled".
///   2. Enabled features pass through.
///   3. Absent flags (no row in DB) default to ALLOWED (backwards compat).
///   4. Always-allowed prefixes (/api/auth, /api/employees, etc.) bypass the guard.
///   5. Platform admin requests (no tenant_id claim) bypass the guard.
///   6. Cross-tenant flag isolation is enforced.
///   7. New routes: /api/saudi-compliance, /api/gosi, /api/wps are correctly gated.
/// </summary>
public class FeatureFlagGuardTests
{
    private static ZayraDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ZayraDbContext(opts);
    }

    private static FeatureFlagGuardFilter MakeFilter(ZayraDbContext db)
        => new(new TenantModuleService(db, new MemoryCache(new MemoryCacheOptions())),
               NullLogger<FeatureFlagGuardFilter>.Instance);

    private static async Task<IActionResult?> RunFilter(
        ZayraDbContext db, string path, Guid tenantId, bool featureEnabled, string featureKey)
    {
        db.TenantFeatureFlags.Add(new TenantFeatureFlag
        {
            TenantId   = tenantId,
            FeatureKey = featureKey,
            IsEnabled  = featureEnabled,
        });
        await db.SaveChangesAsync();

        var httpCtx = new DefaultHttpContext();
        httpCtx.Request.Path = path;
        var claims = new[] { new Claim("tenant_id", tenantId.ToString()) };
        httpCtx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));

        IActionResult? result = null;
        var actionCtx = new ActionContext(httpCtx, new RouteData(), new ActionDescriptor());
        var ctx = new ActionExecutingContext(actionCtx, [], new Dictionary<string, object?>(), null!);

        var filter = MakeFilter(db);
        await filter.OnActionExecutionAsync(ctx, () =>
        {
            result = null;
            return Task.FromResult(new ActionExecutedContext(actionCtx, [], null!));
        });

        return ctx.Result ?? result;
    }

    // ── Core scenarios ────────────────────────────────────────────────────────

    [Fact]
    public async Task AiAssistant_DisabledForTenant_Returns403()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();

        var result = await RunFilter(db, "/api/ai-assistant/chat", tenantId, false, FeatureKeys.AiAssistant);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(403);

        var body = (result as ObjectResult)!.Value;
        body.Should().NotBeNull();
        body!.ToString().Should().Contain("feature_not_enabled");
    }

    [Fact]
    public async Task AiAssistant_EnabledForTenant_PassesThrough()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();

        var result = await RunFilter(db, "/api/ai-assistant/chat", tenantId, true, FeatureKeys.AiAssistant);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Recruitment_DisabledForTenant_Returns403()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();

        var result = await RunFilter(db, "/api/recruitment/jobs", tenantId, false, FeatureKeys.Recruitment);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task Performance_EnabledForTenant_PassesThrough()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();

        var result = await RunFilter(db, "/api/performance/reviews", tenantId, true, FeatureKeys.Performance);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Payroll_DisabledForTenant_Returns403()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();

        var result = await RunFilter(db, "/api/payroll/runs", tenantId, false, FeatureKeys.Payroll);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(403);
    }

    // ── Newly-mapped routes ───────────────────────────────────────────────────

    // ── Statutory routes: a flag cannot switch off a legal obligation ────────
    //
    // These four tests previously asserted the opposite, and the opposite was a defect rather
    // than a decision:
    //
    //   * `/api/saudi-compliance` and `/api/gosi` were both gated by `qiwa_integration`, so a KSA
    //     tenant who switched off the Qiwa portal integration — a preference — also switched off
    //     Saudization reporting and GOSI contribution filing, which are compulsory.
    //   * `/api/wps` was gated by `wps_export`, but no controller has ever served `/api/wps`
    //     (WPS files are produced under `/api/payroll/payment-batches/...`). The test passed
    //     because the guard blocked a path that did not exist; nothing was ever protected.
    //
    // Saudization and GOSI are now `ModuleLock.Statutory` in ModuleCatalog and cannot be
    // disabled by a tenant to whom the obligation applies. A tenant with no localisation row has
    // an unknown country, which the catalog treats as "the obligation applies" (fail-closed).

    [Fact]
    public async Task SaudiCompliance_StoredDisableFlag_IsNotHonoured_BecauseSaudizationIsStatutory()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();

        var result = await RunFilter(db, "/api/saudi-compliance/reports", tenantId, false, ModuleKeys.Saudization);

        result.Should().BeNull("Saudization reporting is compulsory and a stored `false` must be ignored");
    }

    [Fact]
    public async Task Gosi_StoredDisableFlag_IsNotHonoured_BecauseGosiIsStatutory()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();

        // The historical spelling: disabling Qiwa used to disable GOSI as a side effect.
        var result = await RunFilter(db, "/api/gosi/contributions", tenantId, false, FeatureKeys.QiwaIntegration);

        result.Should().BeNull("GOSI filing must not switch off as a side effect of a Qiwa preference");
    }

    [Fact]
    public async Task Gosi_OwnKeyDisabled_IsStillNotHonoured_WhilePayrollRunsHere()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();

        var result = await RunFilter(db, "/api/gosi/contributions", tenantId, false, ModuleKeys.Gosi);

        result.Should().BeNull("the GOSI obligation stands while payroll is run in KynexOne");
    }

    [Fact]
    public async Task Gosi_Enabled_PassesThrough()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();

        var result = await RunFilter(db, "/api/gosi/contributions", tenantId, true, ModuleKeys.Gosi);

        result.Should().BeNull();
    }

    // ── New feature key constants compile and are usable ─────────────────────

    [Theory]
    [InlineData(FeatureKeys.EosbCalc,            "eosb_calc")]
    [InlineData(FeatureKeys.ResumeScreening,     "resume_screening")]
    [InlineData(FeatureKeys.PayrollAiValidation, "payroll_ai_validation")]
    [InlineData(FeatureKeys.RiskScores,          "risk_scores")]
    [InlineData(FeatureKeys.HijriCalendar,       "hijri_calendar")]
    public void NewFeatureKeyConstants_HaveCorrectValues(string constant, string expected)
    {
        constant.Should().Be(expected);
    }

    // ── Absent flag = allowed ─────────────────────────────────────────────────

    [Fact]
    public async Task AbsentFlag_DefaultsToAllowed()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        // Do NOT add any flag row

        var httpCtx = new DefaultHttpContext();
        httpCtx.Request.Path = "/api/ai-assistant/ask";
        var claims = new[] { new Claim("tenant_id", tenantId.ToString()) };
        httpCtx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));

        var actionCtx = new ActionContext(httpCtx, new RouteData(), new ActionDescriptor());
        var ctx = new ActionExecutingContext(actionCtx, [], new Dictionary<string, object?>(), null!);

        var filter = MakeFilter(db);
        await filter.OnActionExecutionAsync(ctx, () =>
            Task.FromResult(new ActionExecutedContext(actionCtx, [], null!)));

        ctx.Result.Should().BeNull("absent flag must default to allowed");
    }

    // ── Always-allowed prefixes ───────────────────────────────────────────────

    [Theory]
    [InlineData("/api/auth/login")]
    [InlineData("/api/employees")]
    [InlineData("/api/leave/requests")]
    [InlineData("/api/attendance/records")]
    [InlineData("/api/dashboard/summary")]
    [InlineData("/api/features")]
    [InlineData("/api/approvals")]
    public async Task AlwaysAllowedRoute_BypassesGuard(string path)
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();

        db.TenantFeatureFlags.Add(new TenantFeatureFlag
        {
            TenantId = tenantId, FeatureKey = FeatureKeys.AiAssistant, IsEnabled = false
        });
        await db.SaveChangesAsync();

        var httpCtx = new DefaultHttpContext();
        httpCtx.Request.Path = path;
        var claims = new[] { new Claim("tenant_id", tenantId.ToString()) };
        httpCtx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));

        var actionCtx = new ActionContext(httpCtx, new RouteData(), new ActionDescriptor());
        var ctx = new ActionExecutingContext(actionCtx, [], new Dictionary<string, object?>(), null!);

        var filter = MakeFilter(db);
        await filter.OnActionExecutionAsync(ctx, () =>
            Task.FromResult(new ActionExecutedContext(actionCtx, [], null!)));

        ctx.Result.Should().BeNull($"'{path}' is always-allowed and must not be blocked");
    }

    // ── Platform admin (no tenant_id claim) bypasses guard ───────────────────

    [Fact]
    public async Task PlatformAdminRequest_WithoutTenantId_BypassesGuard()
    {
        await using var db = CreateDb();

        var httpCtx = new DefaultHttpContext();
        httpCtx.Request.Path = "/api/ai-assistant/chat";
        httpCtx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("is_platform_admin", "true"),
        }, "Test"));

        var actionCtx = new ActionContext(httpCtx, new RouteData(), new ActionDescriptor());
        var ctx = new ActionExecutingContext(actionCtx, [], new Dictionary<string, object?>(), null!);

        var filter = MakeFilter(db);
        await filter.OnActionExecutionAsync(ctx, () =>
            Task.FromResult(new ActionExecutedContext(actionCtx, [], null!)));

        ctx.Result.Should().BeNull("platform admin has no tenant_id claim and must bypass the guard");
    }

    // ── Cross-tenant flag isolation ───────────────────────────────────────────

    [Fact]
    public async Task FeatureFlag_TenantA_DoesNotAffectTenantB()
    {
        await using var db = CreateDb();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        db.TenantFeatureFlags.Add(new TenantFeatureFlag
        {
            TenantId = tenantA, FeatureKey = FeatureKeys.AiAssistant, IsEnabled = false
        });
        db.TenantFeatureFlags.Add(new TenantFeatureFlag
        {
            TenantId = tenantB, FeatureKey = FeatureKeys.AiAssistant, IsEnabled = true
        });
        await db.SaveChangesAsync();

        var httpCtx = new DefaultHttpContext();
        httpCtx.Request.Path = "/api/ai-assistant/ask";
        var claims = new[] { new Claim("tenant_id", tenantB.ToString()) };
        httpCtx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));

        var actionCtx = new ActionContext(httpCtx, new RouteData(), new ActionDescriptor());
        var ctx = new ActionExecutingContext(actionCtx, [], new Dictionary<string, object?>(), null!);

        var filter = MakeFilter(db);
        await filter.OnActionExecutionAsync(ctx, () =>
            Task.FromResult(new ActionExecutedContext(actionCtx, [], null!)));

        ctx.Result.Should().BeNull("TenantB has AI enabled; TenantA's disabled flag must not affect it");
    }

    [Fact]
    public async Task CrossTenantBypass_NotPossible_TenantABlockedEvenIfBEnabled()
    {
        await using var db = CreateDb();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        db.TenantFeatureFlags.Add(new TenantFeatureFlag
        {
            TenantId = tenantA, FeatureKey = FeatureKeys.Payroll, IsEnabled = false
        });
        db.TenantFeatureFlags.Add(new TenantFeatureFlag
        {
            TenantId = tenantB, FeatureKey = FeatureKeys.Payroll, IsEnabled = true
        });
        await db.SaveChangesAsync();

        // TenantA request — must still be blocked even though TenantB has it enabled
        var httpCtx = new DefaultHttpContext();
        httpCtx.Request.Path = "/api/payroll/runs";
        var claims = new[] { new Claim("tenant_id", tenantA.ToString()) };
        httpCtx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));

        var actionCtx = new ActionContext(httpCtx, new RouteData(), new ActionDescriptor());
        var ctx = new ActionExecutingContext(actionCtx, [], new Dictionary<string, object?>(), null!);

        var filter = MakeFilter(db);
        await filter.OnActionExecutionAsync(ctx, () =>
            Task.FromResult(new ActionExecutedContext(actionCtx, [], null!)));

        ctx.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(403, "TenantA's payroll is disabled; TenantB's flag must not grant access to TenantA");
    }
}
