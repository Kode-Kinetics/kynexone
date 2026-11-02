using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Controllers;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Platform;

/// <summary>
/// The platform console sets, per tenant, who must use two-step sign-in and how long a trusted browser skips the code.
/// It runs the tenant's own settings command, so the rules (a reason to relax, trust shortening, sign-out on Everyone)
/// cannot differ between the two doors.
/// </summary>
public class TenantTwoStepPolicyTests : PlatformTestBase
{
    private static async Task<Tenant> SeedTenant(Zayra.Api.Data.ZayraDbContext db)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Alpha", Slug = "alpha", IsActive = true };
        db.Tenants.Add(tenant);
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = tenant.Id, Status = "Active" });
        await db.SaveChangesAsync();
        return tenant;
    }

    [Fact]
    public async Task Get_ReportsThePolicyAndTrustDays_WithDefaultsWhenNoRowExists()
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db);

        var ok = (OkObjectResult)await CreateController(db).GetTenantSecurityPolicy(t.Id, default);

        var json = JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("\"mfaPolicy\":\"PrivilegedRoles\"").And.Contain("\"mfaTrustedDeviceDays\":30");
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(90)]
    [InlineData(0)]
    public async Task SetTrustDays_StoresTheChoice(int days)
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db);

        var result = await CreateController(db).UpdateTenantTwoStepPolicy(t.Id, new UpdateTenantTwoStepPolicyRequest(null, days, null), default);

        result.Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await db.SecuritySettings.SingleAsync(s => s.TenantId == t.Id)).MfaTrustedDeviceDays.Should().Be(days);
        (await db.AdminAuditLogs.CountAsync(l => l.TenantId == t.Id && l.Action == "TwoStepPolicyUpdated")).Should().Be(1);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(91)]
    public async Task TrustDaysOutsideZeroToNinety_AreRefused(int days)
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db);

        var result = await CreateController(db).UpdateTenantTwoStepPolicy(t.Id, new UpdateTenantTwoStepPolicyRequest(null, days, null), default);

        JsonSerializer.Serialize(result.Should().BeOfType<BadRequestObjectResult>().Subject.Value).Should().Contain(MfaErrorCodes.TrustDaysRange);
        (await db.SecuritySettings.AnyAsync(s => s.TenantId == t.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task LettingEachPersonChoose_NeedsAReason_AndIsStoredWithOne()
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db);
        var controller = CreateController(db);

        var refused = await controller.UpdateTenantTwoStepPolicy(t.Id, new UpdateTenantTwoStepPolicyRequest(MfaPolicies.Optional, null, null), default);
        JsonSerializer.Serialize(refused.Should().BeOfType<BadRequestObjectResult>().Subject.Value).Should().Contain(MfaErrorCodes.PolicyReasonRequired);

        var ok = await controller.UpdateTenantTwoStepPolicy(t.Id, new UpdateTenantTwoStepPolicyRequest(MfaPolicies.Optional, null, "Pilot tenant, testing"), default);
        ok.Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await db.SecuritySettings.SingleAsync(s => s.TenantId == t.Id)).EffectiveMfaPolicy.Should().Be(MfaPolicies.Optional);
    }

    [Fact]
    public async Task AnUnknownPolicy_IsRefused_AndRequiringEveryoneStoresIt()
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db);
        var controller = CreateController(db);

        (await controller.UpdateTenantTwoStepPolicy(t.Id, new UpdateTenantTwoStepPolicyRequest("Nobody", null, null), default))
            .Should().BeOfType<BadRequestObjectResult>();

        (await controller.UpdateTenantTwoStepPolicy(t.Id, new UpdateTenantTwoStepPolicyRequest(MfaPolicies.Everyone, null, null), default))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await db.SecuritySettings.SingleAsync(s => s.TenantId == t.Id)).MfaRequired.Should().BeTrue();
    }

    [Fact]
    public async Task NothingToChange_IsRefused_AndAnUnknownTenantIs404()
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db);
        var controller = CreateController(db);

        (await controller.UpdateTenantTwoStepPolicy(t.Id, new UpdateTenantTwoStepPolicyRequest(null, null, null), default))
            .Should().BeOfType<BadRequestObjectResult>();
        (await controller.UpdateTenantTwoStepPolicy(Guid.NewGuid(), new UpdateTenantTwoStepPolicyRequest(null, 30, null), default))
            .Should().BeOfType<NotFoundObjectResult>();
    }
}
