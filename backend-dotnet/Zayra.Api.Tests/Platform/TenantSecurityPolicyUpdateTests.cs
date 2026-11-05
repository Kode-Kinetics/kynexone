using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Models;
using Zayra.Api.Tests.Security;
using static Zayra.Api.Tests.Platform.PlatformOperatorKit;

namespace Zayra.Api.Tests.Platform;

/// <summary>
/// Changing a tenant's password/lockout/session policy from the platform console needs a reason, is bounded by the same
/// limits the tenant's own settings screen uses, and is recorded with before/after values under the real operator.
/// </summary>
public sealed class TenantSecurityPolicyUpdateTests
{
    private const string OperatorEmail = "ops.owner@platform.test";
    private const string Reason = "Customer audit requirement";

    private static UpdateSecurityPolicyRequest Req(string? reason = Reason, int? minLength = null, int? expiryDays = null,
        int? lockoutMinutes = null, int? sessionMinutes = null, int? refreshDays = null)
        => new(minLength, null, null, null, null, expiryDays, null, null, lockoutMinutes, sessionMinutes, refreshDays, null, reason!);

    private static async Task<(AuthHardeningTestKit Kit, PlatformUser Owner)> StartAsync()
    {
        var kit = await AuthHardeningTestKit.CreateAsync();
        return (kit, await SeedOperatorAsync(kit, OperatorEmail));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("         ")]
    [InlineData("too short")] // 9 characters
    public async Task AReasonOfAtLeastTenCharacters_IsRequired_AndNothingIsStoredWithout(string? reason)
    {
        var (kit, owner) = await StartAsync();
        await using var _ = kit;

        await using (var db = kit.NewDb())
            Status(await As(kit, db, owner).UpdateTenantSecurityPolicy(kit.TenantId, Req(reason, sessionMinutes: 60), default)).Should().Be(400);

        await using var verify = kit.NewDb();
        (await verify.SecuritySettings.CountAsync(s => s.TenantId == kit.TenantId)).Should().Be(0);
        (await verify.AdminAuditLogs.IgnoreQueryFilters().CountAsync(l => l.Action == "SecurityPolicyUpdated")).Should().Be(0);
    }

    [Fact]
    public async Task ATenIsEnough_AndAnUnknownTenantIs404()
    {
        var (kit, owner) = await StartAsync();
        await using var _ = kit;

        await using var db = kit.NewDb();
        var c = As(kit, db, owner);
        (await c.UpdateTenantSecurityPolicy(kit.TenantId, Req("ten chars!", sessionMinutes: 60), default)).Should().BeOfType<NoContentResult>();
        (await c.UpdateTenantSecurityPolicy(Guid.NewGuid(), Req(sessionMinutes: 60), default)).Should().BeOfType<NotFoundResult>();
    }

    [Theory]
    [InlineData("minLength", 3, 6)]
    [InlineData("minLength", 500, 64)]
    [InlineData("expiryDays", 9999, 365)]
    [InlineData("expiryDays", -5, 0)]
    [InlineData("lockoutMinutes", 1, 5)]
    [InlineData("lockoutMinutes", 99999, 1440)]
    [InlineData("sessionMinutes", 5, 15)]
    [InlineData("sessionMinutes", 99999, 1440)]
    [InlineData("refreshDays", 0, 1)]
    [InlineData("refreshDays", 500, 90)]
    public async Task Values_AreClampedToTheLimitsTheTenantsOwnSettingsUse(string field, int sent, int stored)
    {
        var (kit, owner) = await StartAsync();
        await using var _ = kit;
        var req = field switch
        {
            "minLength" => Req(minLength: sent),
            "expiryDays" => Req(expiryDays: sent),
            "lockoutMinutes" => Req(lockoutMinutes: sent),
            "sessionMinutes" => Req(sessionMinutes: sent),
            _ => Req(refreshDays: sent),
        };

        await using (var db = kit.NewDb())
            (await As(kit, db, owner).UpdateTenantSecurityPolicy(kit.TenantId, req, default)).Should().BeOfType<NoContentResult>();

        await using var verify = kit.NewDb();
        var sec = await verify.SecuritySettings.AsNoTracking().SingleAsync(s => s.TenantId == kit.TenantId);
        var actual = field switch
        {
            "minLength" => sec.PasswordMinLength,
            "expiryDays" => sec.PasswordExpiryDays,
            "lockoutMinutes" => sec.LockoutDurationMinutes,
            "sessionMinutes" => sec.SessionTimeoutMinutes,
            _ => sec.RefreshTokenExpiryDays,
        };
        actual.Should().Be(stored);
    }

    [Fact]
    public async Task TheChange_IsAuditedOnce_WithReasonBeforeAfterAndTheRealOperator()
    {
        var (kit, owner) = await StartAsync();
        await using var _ = kit;

        await using (var db = kit.NewDb())
            (await As(kit, db, owner).UpdateTenantSecurityPolicy(kit.TenantId, Req(sessionMinutes: 60, refreshDays: 7), default))
                .Should().BeOfType<NoContentResult>();

        var row = (await AuditRowsAsync(kit, "SecurityPolicyUpdated")).Should().ContainSingle().Subject;
        row.TenantId.Should().Be(kit.TenantId);
        row.EntityType.Should().Be("SecuritySetting");
        row.PerformedByName.Should().Be(OperatorEmail);
        var details = JsonDocument.Parse(row.NewValuesJson).RootElement;
        details.GetProperty("reason").GetString().Should().Be(Reason);
        details.GetProperty("before").GetProperty("sessionTimeoutMinutes").GetInt32().Should().Be(480);
        details.GetProperty("before").GetProperty("refreshTokenExpiryDays").GetInt32().Should().Be(30);
        details.GetProperty("after").GetProperty("sessionTimeoutMinutes").GetInt32().Should().Be(60);
        details.GetProperty("after").GetProperty("refreshTokenExpiryDays").GetInt32().Should().Be(7);
    }

    [Fact]
    public async Task ASecondChange_RecordsTheFirstOnesValuesAsItsBefore()
    {
        var (kit, owner) = await StartAsync();
        await using var _ = kit;

        await using (var db = kit.NewDb())
        {
            var c = As(kit, db, owner);
            (await c.UpdateTenantSecurityPolicy(kit.TenantId, Req(sessionMinutes: 60), default)).Should().BeOfType<NoContentResult>();
        }
        await using (var db = kit.NewDb())
            (await As(kit, db, owner).UpdateTenantSecurityPolicy(kit.TenantId, Req(sessionMinutes: 120), default)).Should().BeOfType<NoContentResult>();

        var rows = await AuditRowsAsync(kit, "SecurityPolicyUpdated");
        rows.Should().HaveCount(2);
        rows.Select(r => JsonDocument.Parse(r.NewValuesJson).RootElement.GetProperty("before").GetProperty("sessionTimeoutMinutes").GetInt32())
            .Should().BeEquivalentTo(new[] { 480, 60 });
    }
}
