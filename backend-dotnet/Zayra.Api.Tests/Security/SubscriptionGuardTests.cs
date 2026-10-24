using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Filters;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Unit-tests for <see cref="SubscriptionGuardFilter"/>.
/// Verifies:
///   1. Suspended → 402
///   2. Cancelled → 402
///   3. Expired Trial/Active → 402
///   4. PastDue (not yet expired) → passes through + X-Subscription-Status header
///   5. Active with no expiry → passes through
///   6. ManualContract → always passes through (no expiry enforcement)
///   7. /api/auth/*, /api/health, /api/version bypass the guard
/// </summary>
public class SubscriptionGuardTests
{
    private static ZayraDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ZayraDbContext(opts);
    }

    private static async Task<(IActionResult? Result, HttpContext Http)> RunGuard(
        ZayraDbContext db, string path, Guid tenantId)
    {
        var httpCtx = new DefaultHttpContext();
        httpCtx.Request.Path = path;
        httpCtx.Response.Body = new System.IO.MemoryStream();
        var claims = new[] { new Claim("tenant_id", tenantId.ToString()) };
        httpCtx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));

        var actionCtx = new ActionContext(httpCtx, new RouteData(), new ActionDescriptor());
        var ctx = new ResourceExecutingContext(actionCtx, [], []);

        var filter = new SubscriptionGuardFilter(db, NullLogger<SubscriptionGuardFilter>.Instance);
        await filter.OnResourceExecutionAsync(ctx, () =>
            Task.FromResult(new ResourceExecutedContext(actionCtx, [])));

        return (ctx.Result, httpCtx);
    }

    // ── Suspended ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SuspendedSubscription_Returns402()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.TenantSubscriptions.Add(new TenantSubscription
        {
            TenantId = tenantId, Status = SubscriptionStatuses.Suspended, Plan = "Starter"
        });
        await db.SaveChangesAsync();

        var (result, _) = await RunGuard(db, "/api/employees", tenantId);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(402);
        (result as ObjectResult)!.Value!.ToString().Should().Contain("subscription_inactive");
    }

    // ── Cancelled ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task CancelledSubscription_Returns402()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.TenantSubscriptions.Add(new TenantSubscription
        {
            TenantId = tenantId, Status = SubscriptionStatuses.Cancelled, Plan = "Starter"
        });
        await db.SaveChangesAsync();

        var (result, _) = await RunGuard(db, "/api/employees", tenantId);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(402);
        (result as ObjectResult)!.Value!.ToString().Should().Contain("subscription_inactive");
    }

    // ── Expired ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExpiredTrial_Returns402()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.TenantSubscriptions.Add(new TenantSubscription
        {
            TenantId     = tenantId,
            Status       = SubscriptionStatuses.Trial,
            Plan         = "Trial",
            ExpiresAtUtc = DateTime.UtcNow.AddDays(-1), // already expired
        });
        await db.SaveChangesAsync();

        var (result, _) = await RunGuard(db, "/api/employees", tenantId);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(402);
        (result as ObjectResult)!.Value!.ToString().Should().Contain("subscription_expired");
    }

    [Fact]
    public async Task ExpiredActive_Returns402()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.TenantSubscriptions.Add(new TenantSubscription
        {
            TenantId     = tenantId,
            Status       = SubscriptionStatuses.Active,
            Plan         = "Starter",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(-1),
        });
        await db.SaveChangesAsync();

        var (result, _) = await RunGuard(db, "/api/payroll/runs", tenantId);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(402);
    }

    // ── PastDue (not expired) ─────────────────────────────────────────────────

    [Fact]
    public async Task PastDueNotExpired_PassesThroughWithHeader()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.TenantSubscriptions.Add(new TenantSubscription
        {
            TenantId     = tenantId,
            Status       = SubscriptionStatuses.PastDue,
            Plan         = "Starter",
            ExpiresAtUtc = DateTime.UtcNow.AddDays(7), // still valid
        });
        await db.SaveChangesAsync();

        var (result, http) = await RunGuard(db, "/api/employees", tenantId);

        result.Should().BeNull("PastDue with unexpired subscription must pass through");
        http.Response.Headers["X-Subscription-Status"].ToString().Should().Be("PastDue");
    }

    // ── Active ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ActiveSubscription_NoExpiry_PassesThrough()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.TenantSubscriptions.Add(new TenantSubscription
        {
            TenantId     = tenantId,
            Status       = SubscriptionStatuses.Active,
            Plan         = "Enterprise",
            ExpiresAtUtc = null, // no expiry
        });
        await db.SaveChangesAsync();

        var (result, _) = await RunGuard(db, "/api/payroll/runs", tenantId);

        result.Should().BeNull();
    }

    [Fact]
    public async Task TrialSubscription_NotExpired_PassesThrough()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.TenantSubscriptions.Add(new TenantSubscription
        {
            TenantId     = tenantId,
            Status       = SubscriptionStatuses.Trial,
            Plan         = "Trial",
            ExpiresAtUtc = DateTime.UtcNow.AddDays(14),
        });
        await db.SaveChangesAsync();

        var (result, _) = await RunGuard(db, "/api/employees", tenantId);

        result.Should().BeNull("active trial subscription must not be blocked");
    }

    // ── ManualContract bypasses expiry ────────────────────────────────────────

    [Fact]
    public async Task ManualContract_PastExpiry_StillPassesThrough()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.TenantSubscriptions.Add(new TenantSubscription
        {
            TenantId     = tenantId,
            Status       = SubscriptionStatuses.ManualContract,
            Plan         = "Enterprise",
            ExpiresAtUtc = DateTime.UtcNow.AddDays(-30), // expired date — must be ignored
        });
        await db.SaveChangesAsync();

        var (result, _) = await RunGuard(db, "/api/employees", tenantId);

        result.Should().BeNull("ManualContract must bypass expiry enforcement");
    }

    // ── Skipped prefixes ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("/api/auth/login")]
    [InlineData("/api/tenant-admin/localization")]
    [InlineData("/api/health")]
    [InlineData("/api/version")]
    public async Task SkippedPrefixes_BypassGuardEvenWhenSuspended(string path)
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.TenantSubscriptions.Add(new TenantSubscription
        {
            TenantId = tenantId, Status = SubscriptionStatuses.Suspended, Plan = "Starter"
        });
        await db.SaveChangesAsync();

        var (result, _) = await RunGuard(db, path, tenantId);

        result.Should().BeNull($"'{path}' is in the skip list and must bypass the subscription guard");
    }

    // ── Suspended tenant cannot reach HR APIs ────────────────────────────────

    [Theory]
    [InlineData("/api/payroll/runs")]
    [InlineData("/api/recruitment/jobs")]
    [InlineData("/api/performance/reviews")]
    public async Task SuspendedTenant_CannotReachBusinessApis(string path)
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.TenantSubscriptions.Add(new TenantSubscription
        {
            TenantId = tenantId, Status = SubscriptionStatuses.Suspended, Plan = "Starter"
        });
        await db.SaveChangesAsync();

        var (result, _) = await RunGuard(db, path, tenantId);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(402);
    }

    [Theory]
    [InlineData("/api/payroll/runs")]
    [InlineData("/api/employees")]
    public async Task CancelledTenant_CannotReachBusinessApis(string path)
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.TenantSubscriptions.Add(new TenantSubscription
        {
            TenantId = tenantId, Status = SubscriptionStatuses.Cancelled, Plan = "Growth"
        });
        await db.SaveChangesAsync();

        var (result, _) = await RunGuard(db, path, tenantId);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(402);
    }

    // ── No subscription record ────────────────────────────────────────────────

    [Fact]
    public async Task NoSubscriptionRecord_PassesThrough()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        // No subscription row seeded

        var (result, _) = await RunGuard(db, "/api/employees", tenantId);

        result.Should().BeNull("missing subscription row must not block (fail open)");
    }

    // ── No tenant claim ─────────────────────────────────────

    [Fact]
    public async Task NoTenantIdClaim_BypassesGuard()
    {
        await using var db = CreateDb();
        var httpCtx = new DefaultHttpContext();
        httpCtx.Request.Path = "/api/employees";
        httpCtx.User = new ClaimsPrincipal(new ClaimsIdentity(Array.Empty<Claim>(), "Test"));

        var actionCtx = new ActionContext(httpCtx, new RouteData(), new ActionDescriptor());
        var ctx = new ResourceExecutingContext(actionCtx, [], []);

        var filter = new SubscriptionGuardFilter(db, NullLogger<SubscriptionGuardFilter>.Instance);
        await filter.OnResourceExecutionAsync(ctx, () =>
            Task.FromResult(new ResourceExecutedContext(actionCtx, [])));

        ctx.Result.Should().BeNull("with no tenant_id claim there is no subscription to enforce");
    }
}
