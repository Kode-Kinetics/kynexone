using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Tests.Security;
using static Zayra.Api.Tests.Platform.PlatformOperatorKit;

namespace Zayra.Api.Tests.Platform;

/// <summary>The platform-wide mandatory-MFA start date: Security Center read, and the Owner-only change.</summary>
public sealed class PlatformPrivilegedMfaEnforcementEndpointTests
{
    private const string Reason = "rollout schedule agreed";

    private static PrivilegedMfaEnforcementRequest Req(string? date, string reason = Reason) => new(date, reason);

    private static async Task<int> ConfigRowCountAsync(AuthHardeningTestKit kit)
    {
        await using var db = kit.NewDb();
        return await db.PlatformConfigEntries.CountAsync(e => e.Key == PrivilegedMfaPolicy.PlatformConfigKey);
    }

    [Fact]
    public async Task RefusedRequests_Change_Nothing()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var owner = await SeedOperatorAsync(kit, "owner@platform.test");
        var future = DateTime.UtcNow.AddDays(30);

        await using (var db = kit.NewDb())
        {
            var c = As(kit, db, owner);
            Status(await c.SetPlatformPrivilegedMfaEnforcement(Req(null), default)).Should().Be(400, "the platform date cannot be removed");
            Status(await c.SetPlatformPrivilegedMfaEnforcement(Req(PrivilegedMfaPolicy.FormatDate(future), "too short"), default)).Should().Be(400);
            Status(await c.SetPlatformPrivilegedMfaEnforcement(Req(future.ToString("yyyy-MM-ddTHH:mm:ss")), default)).Should().Be(400, "offset-less");
            Status(await c.SetPlatformPrivilegedMfaEnforcement(Req(PrivilegedMfaPolicy.FormatDate(DateTime.UtcNow.AddDays(91))), default)).Should().Be(400, "beyond 90 days");
        }

        (await ConfigRowCountAsync(kit)).Should().Be(0);
        (await AuditRowsAsync(kit, "PrivilegedMfaEnforcementChanged")).Should().BeEmpty();
    }

    [Fact]
    public async Task AnOwnersChange_IsStored_ReadBack_AndAuditedWithReasonAndPreviousValue()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var owner = await SeedOperatorAsync(kit, "owner@platform.test");
        var first = DateTime.UtcNow.AddDays(30);
        var second = DateTime.UtcNow.AddDays(45);

        await using (var db = kit.NewDb())
        {
            var ok = await As(kit, db, owner).SetPlatformPrivilegedMfaEnforcement(Req(PrivilegedMfaPolicy.FormatDate(first)), default);
            ok.Should().BeOfType<OkObjectResult>();
        }

        await using (var db = kit.NewDb())
        {
            var entry = await db.PlatformConfigEntries.AsNoTracking().SingleAsync(e => e.Key == PrivilegedMfaPolicy.PlatformConfigKey);
            PrivilegedMfaPolicy.ParseDate(entry.Value).Should().BeCloseTo(first, TimeSpan.FromSeconds(1));
            entry.UpdatedByPlatformUserId.Should().Be(owner.Id);

            var read = Body(await As(kit, db, owner).GetPlatformPrivilegedMfaEnforcement(default))
                .GetProperty("platformPrivilegedMfaEnforceFromUtc").GetDateTime();
            read.Should().BeCloseTo(first, TimeSpan.FromSeconds(1));
        }

        var firstAudit = (await AuditRowsAsync(kit, "PrivilegedMfaEnforcementChanged")).Should().ContainSingle().Subject;
        JsonDocument.Parse(firstAudit.NewValuesJson).RootElement.GetProperty("reason").GetString().Should().Be(Reason);
        JsonDocument.Parse(firstAudit.OldValuesJson).RootElement.GetProperty("enforceFromUtc").ValueKind.Should().Be(JsonValueKind.Null);

        await using (var db = kit.NewDb())
            (await As(kit, db, owner).SetPlatformPrivilegedMfaEnforcement(Req(PrivilegedMfaPolicy.FormatDate(second), "moved after a customer escalation"), default))
                .Should().BeOfType<OkObjectResult>();

        var audits = await AuditRowsAsync(kit, "PrivilegedMfaEnforcementChanged");
        audits.Should().HaveCount(2);
        var previousValues = audits
            .Select(a => JsonDocument.Parse(a.OldValuesJson).RootElement.GetProperty("enforceFromUtc"))
            .Where(p => p.ValueKind == JsonValueKind.String)
            .Select(p => PrivilegedMfaPolicy.ParseDate(p.GetString())!.Value)
            .ToList();
        previousValues.Should().ContainSingle().Which.Should().BeCloseTo(first, TimeSpan.FromSeconds(1));
        (await ConfigRowCountAsync(kit)).Should().Be(1, "the second change updates the row rather than adding one");
    }

    [Fact]
    public async Task Get_ReportsNull_WhenNoPlatformDateIsSet()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var owner = await SeedOperatorAsync(kit, "owner@platform.test");

        await using var db = kit.NewDb();
        Body(await As(kit, db, owner).GetPlatformPrivilegedMfaEnforcement(default))
            .GetProperty("platformPrivilegedMfaEnforceFromUtc").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
