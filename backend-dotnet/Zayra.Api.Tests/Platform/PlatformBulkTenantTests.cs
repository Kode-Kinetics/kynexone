using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Platform;

/// <summary>
/// Covers the bulk tenant operations (suspend / reactivate / delete / feature apply).
/// These mirror the single-tenant endpoints but operate on a selection or platform-wide.
/// </summary>
public class PlatformBulkTenantTests : PlatformTestBase
{
    private static async Task<Tenant> SeedTenant(Zayra.Api.Data.ZayraDbContext db, string slug,
        string status = "Active", bool active = true)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = slug, Slug = slug, IsActive = active };
        db.Tenants.Add(tenant);
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = tenant.Id, Status = status });
        await db.SaveChangesAsync();
        return tenant;
    }

    private static string Json(IActionResult r) =>
        JsonSerializer.Serialize(((OkObjectResult)r).Value);

    // ── Suspend ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BulkSuspend_SuspendsAllSelected_AndPersists()
    {
        await using var db = CreateDb();
        var a = await SeedTenant(db, "alpha");
        var b = await SeedTenant(db, "beta");
        var controller = CreateController(db);

        var result = await controller.BulkSuspendTenants(
            new BulkTenantActionRequest(new List<Guid> { a.Id, b.Id }, "audit cleanup"), CancellationToken.None);

        var json = Json(result);
        json.Should().Contain("\"succeeded\":2");
        (await db.Tenants.FindAsync(a.Id))!.IsActive.Should().BeFalse();
        (await db.TenantSubscriptions.FirstAsync(s => s.TenantId == b.Id)).Status.Should().Be("Suspended");
    }

    [Fact]
    public async Task BulkSuspend_SkipsAlreadySuspended_AndMissing()
    {
        await using var db = CreateDb();
        var a = await SeedTenant(db, "alpha", status: "Suspended", active: false);
        var controller = CreateController(db);

        var result = await controller.BulkSuspendTenants(
            new BulkTenantActionRequest(new List<Guid> { a.Id, Guid.NewGuid() }, null), CancellationToken.None);

        var json = Json(result);
        json.Should().Contain("\"succeeded\":0");
        json.Should().Contain("\"skipped\":2");
    }

    [Fact]
    public async Task BulkSuspend_WithEmptySelection_Returns400()
    {
        await using var db = CreateDb();
        var controller = CreateController(db);

        var result = await controller.BulkSuspendTenants(
            new BulkTenantActionRequest(new List<Guid>(), null), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ── Reactivate ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task BulkReactivate_RestoresSelected()
    {
        await using var db = CreateDb();
        var a = await SeedTenant(db, "alpha", status: "Suspended", active: false);
        var controller = CreateController(db);

        var result = await controller.BulkReactivateTenants(
            new BulkTenantActionRequest(new List<Guid> { a.Id }, null), CancellationToken.None);

        Json(result).Should().Contain("\"succeeded\":1");
        (await db.Tenants.FindAsync(a.Id))!.IsActive.Should().BeTrue();
        (await db.TenantSubscriptions.FirstAsync(s => s.TenantId == a.Id)).Status.Should().Be("Active");
    }

    // ── Delete ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BulkDelete_WithoutConfirm_Returns400()
    {
        await using var db = CreateDb();
        var a = await SeedTenant(db, "alpha");
        var controller = CreateController(db);

        var result = await controller.BulkDeleteTenants(
            new BulkDeleteTenantsRequest(new List<Guid> { a.Id }, ""), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task BulkDelete_SoftDeletes_FreesSlug_DeactivatesUsers_RevokesTokens()
    {
        await using var db = CreateDb();
        var a = await SeedTenant(db, "alpha");
        var user = new User { Id = Guid.NewGuid(), TenantId = a.Id, Email = "u@alpha.com", IsActive = true, Status = "Active" };
        db.Users.Add(user);
        db.RefreshTokens.Add(new RefreshToken { Id = Guid.NewGuid(), UserId = user.Id, TokenHash = "h", ExpiresAtUtc = DateTime.UtcNow.AddDays(1) });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var result = await controller.BulkDeleteTenants(
            new BulkDeleteTenantsRequest(new List<Guid> { a.Id }, "DELETE"), CancellationToken.None);

        Json(result).Should().Contain("\"succeeded\":1");
        var tenant = await db.Tenants.FindAsync(a.Id);
        tenant!.IsActive.Should().BeFalse();
        tenant.Slug.Should().Contain("__deleted_");
        (await db.Users.FindAsync(user.Id))!.IsActive.Should().BeFalse();
        (await db.RefreshTokens.FirstAsync(t => t.UserId == user.Id)).RevokedAtUtc.Should().NotBeNull();
        (await db.TenantSubscriptions.FirstAsync(s => s.TenantId == a.Id)).Status.Should().Be("Cancelled");
    }

    [Fact]
    public async Task BulkDelete_SkipsAlreadyDeleted()
    {
        await using var db = CreateDb();
        var a = await SeedTenant(db, "alpha");
        var controller = CreateController(db);

        await controller.BulkDeleteTenants(new BulkDeleteTenantsRequest(new List<Guid> { a.Id }, "DELETE"), CancellationToken.None);
        var second = await controller.BulkDeleteTenants(new BulkDeleteTenantsRequest(new List<Guid> { a.Id }, "DELETE"), CancellationToken.None);

        Json(second).Should().Contain("\"skipped\":1");
    }

    // ── Bulk feature flags ──────────────────────────────────────────────────────

    [Fact]
    public async Task BulkFeature_AppliesToSelectedTenants()
    {
        await using var db = CreateDb();
        var a = await SeedTenant(db, "alpha");
        var b = await SeedTenant(db, "beta");
        var controller = CreateController(db);

        var result = await controller.BulkSetFeatureFlag(
            new BulkFeatureFlagRequest(new List<Guid> { a.Id, b.Id }, ApplyToAll: false,
                FeatureKey: FeatureKeys.AiAssistant, IsEnabled: true, ConfigJson: null), CancellationToken.None);

        Json(result).Should().Contain("\"succeeded\":2");
        (await db.TenantFeatureFlags.CountAsync(f => f.FeatureKey == FeatureKeys.AiAssistant && f.IsEnabled)).Should().Be(2);
    }

    [Fact]
    public async Task BulkFeature_ApplyToAll_TargetsEveryActiveTenant_AndSkipsInactive()
    {
        await using var db = CreateDb();
        await SeedTenant(db, "alpha");
        await SeedTenant(db, "beta");
        await SeedTenant(db, "gamma", status: "Cancelled", active: false); // inactive — excluded
        var controller = CreateController(db);

        var result = await controller.BulkSetFeatureFlag(
            new BulkFeatureFlagRequest(TenantIds: null, ApplyToAll: true,
                FeatureKey: FeatureKeys.Payroll, IsEnabled: true, ConfigJson: null), CancellationToken.None);

        var json = Json(result);
        json.Should().Contain("\"succeeded\":2");
        json.Should().Contain("\"appliedToAll\":true");
        (await db.TenantFeatureFlags.CountAsync(f => f.FeatureKey == FeatureKeys.Payroll)).Should().Be(2);
    }

    [Fact]
    public async Task BulkFeature_WithNoFeatureKey_Returns400()
    {
        await using var db = CreateDb();
        await SeedTenant(db, "alpha");
        var controller = CreateController(db);

        var result = await controller.BulkSetFeatureFlag(
            new BulkFeatureFlagRequest(new List<Guid>(), ApplyToAll: true, FeatureKey: "", IsEnabled: true, ConfigJson: null),
            CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task BulkFeature_ApplyToAll_WithNoTenants_Returns400()
    {
        await using var db = CreateDb();
        var controller = CreateController(db);

        var result = await controller.BulkSetFeatureFlag(
            new BulkFeatureFlagRequest(null, ApplyToAll: true, FeatureKey: FeatureKeys.Payroll, IsEnabled: true, ConfigJson: null),
            CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ── Authorization: delete requires Owner ────────────────────────────────────

    [Fact]
    public async Task BulkDelete_RequiresOwnerRole_AttributePresent()
    {
        var method = typeof(PlatformController).GetMethod(nameof(PlatformController.BulkDeleteTenants))!;
        var attr = (RequirePlatformRoleAttribute?)Attribute.GetCustomAttribute(method, typeof(RequirePlatformRoleAttribute));
        attr.Should().NotBeNull();
        attr!.Roles.Should().Contain(PlatformRoles.Owner);
        attr.Roles.Should().NotContain(PlatformRoles.Admin);
    }
}
