using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Models;
using Zayra.Api.Tests.Security;
using static Zayra.Api.Tests.Platform.PlatformOperatorKit;

namespace Zayra.Api.Tests.Platform;

/// <summary>
/// Who may assign, change or retire the Owner role on the platform team. Only an Owner may touch an Owner or hand
/// out the Owner role; nobody changes their own role or active flag; the last active Owner always stays.
/// </summary>
public sealed class PlatformTeamRoleGuardTests
{
    private const string Pw = "LongEnough-Password-1!";

    private static CreatePlatformUserRequest NewMember(string email, string role)
        => new(Email: email, FullName: "New Member", Password: Pw, Role: role);

    private static UpdatePlatformUserRequest Patch(string? role = null, bool? active = null, string? name = null)
        => new(Role: role, IsActive: active, FullName: name);

    private static async Task<PlatformUser> ReadAsync(AuthHardeningTestKit kit, Guid id)
    {
        await using var db = kit.NewDb();
        return await db.PlatformUsers.AsNoTracking().SingleAsync(u => u.Id == id);
    }

    // ── Create ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Admin_CannotCreateAnOwner_AndNothingIsStored()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var admin = await SeedOperatorAsync(kit, "admin@platform.test", PlatformRoles.Admin);

        await using (var db = kit.NewDb())
        {
            var result = await As(kit, db, admin).CreateTeamMember(NewMember("new.owner@platform.test", PlatformRoles.Owner), default);
            Status(result).Should().Be(403);
            Body(result).GetProperty("message").GetString().Should().Contain("Owner");
        }

        await using var verify = kit.NewDb();
        (await verify.PlatformUsers.CountAsync(u => u.Email == "new.owner@platform.test")).Should().Be(0);
        (await verify.AdminAuditLogs.IgnoreQueryFilters().CountAsync(l => l.Action == "PlatformUserCreated")).Should().Be(0);
    }

    [Fact]
    public async Task Admin_CanStillCreateANonOwner()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var admin = await SeedOperatorAsync(kit, "admin@platform.test", PlatformRoles.Admin);

        await using var db = kit.NewDb();
        (await As(kit, db, admin).CreateTeamMember(NewMember("support@platform.test", PlatformRoles.Support), default))
            .Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Owner_CanCreateAnOwner()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var owner = await SeedOperatorAsync(kit, "owner@platform.test");

        await using (var db = kit.NewDb())
            (await As(kit, db, owner).CreateTeamMember(NewMember("second.owner@platform.test", PlatformRoles.Owner), default))
                .Should().BeOfType<OkObjectResult>();

        await using var verify = kit.NewDb();
        (await verify.PlatformUsers.SingleAsync(u => u.Email == "second.owner@platform.test")).Role.Should().Be(PlatformRoles.Owner);
    }

    // ── Update ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Admin_CannotSetTheOwnerRole_OnAnotherMember()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var admin = await SeedOperatorAsync(kit, "admin@platform.test", PlatformRoles.Admin);
        var support = await SeedOperatorAsync(kit, "support@platform.test", PlatformRoles.Support);

        await using (var db = kit.NewDb())
            Status(await As(kit, db, admin).UpdateTeamMember(support.Id, Patch(role: PlatformRoles.Owner), default)).Should().Be(403);

        var after = await ReadAsync(kit, support.Id);
        after.Role.Should().Be(PlatformRoles.Support);
        after.UpdatedAtUtc.Should().Be(support.UpdatedAtUtc, "a refused change must not rotate the member's sessions");
        (await AuditRowsAsync(kit, "PlatformUserUpdated")).Should().BeEmpty();
    }

    [Fact]
    public async Task Admin_CannotChangeAnOwner_InAnyWay()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var admin = await SeedOperatorAsync(kit, "admin@platform.test", PlatformRoles.Admin);
        var owner = await SeedOperatorAsync(kit, "owner@platform.test");

        await using (var db = kit.NewDb())
        {
            var c = As(kit, db, admin);
            Status(await c.UpdateTeamMember(owner.Id, Patch(role: PlatformRoles.Support), default)).Should().Be(403);
            Status(await c.UpdateTeamMember(owner.Id, Patch(active: false), default)).Should().Be(403);
            Status(await c.UpdateTeamMember(owner.Id, Patch(name: "Renamed"), default)).Should().Be(403);
        }

        var after = await ReadAsync(kit, owner.Id);
        after.Role.Should().Be(PlatformRoles.Owner);
        after.IsActive.Should().BeTrue();
        after.FullName.Should().Be("owner@platform.test");
        (await AuditRowsAsync(kit, "PlatformUserUpdated")).Should().BeEmpty();
    }

    [Fact]
    public async Task Admin_CanStillChangeANonOwner_AndTheMembersSessionStampRotates()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var admin = await SeedOperatorAsync(kit, "admin@platform.test", PlatformRoles.Admin);
        var support = await SeedOperatorAsync(kit, "support@platform.test", PlatformRoles.Support);

        await using (var db = kit.NewDb())
            (await As(kit, db, admin).UpdateTeamMember(support.Id, Patch(role: PlatformRoles.Finance), default))
                .Should().BeOfType<OkObjectResult>();

        var after = await ReadAsync(kit, support.Id);
        after.Role.Should().Be(PlatformRoles.Finance);
        after.UpdatedAtUtc.Should().BeAfter(support.UpdatedAtUtc ?? DateTime.MinValue, "the session stamp rotates so old tokens stop working");
    }

    [Fact]
    public async Task NobodyChangesTheirOwnRoleOrActiveFlag()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var admin = await SeedOperatorAsync(kit, "admin@platform.test", PlatformRoles.Admin);
        var owner = await SeedOperatorAsync(kit, "owner@platform.test");
        await SeedOperatorAsync(kit, "owner2@platform.test");

        await using (var db = kit.NewDb())
        {
            Status(await As(kit, db, admin).UpdateTeamMember(admin.Id, Patch(role: PlatformRoles.Support), default)).Should().Be(403);
            Status(await As(kit, db, admin).UpdateTeamMember(admin.Id, Patch(active: false), default)).Should().Be(403);
            Status(await As(kit, db, owner).UpdateTeamMember(owner.Id, Patch(role: PlatformRoles.Admin), default)).Should().Be(403);
            Status(await As(kit, db, owner).UpdateTeamMember(owner.Id, Patch(active: false), default)).Should().Be(403);
            Status(await As(kit, db, owner).DeactivateTeamMember(owner.Id, default)).Should().Be(403);
        }

        (await ReadAsync(kit, admin.Id)).Role.Should().Be(PlatformRoles.Admin);
        var ownerAfter = await ReadAsync(kit, owner.Id);
        ownerAfter.Role.Should().Be(PlatformRoles.Owner);
        ownerAfter.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task RestatingYourOwnRoleOrRenamingYourself_IsAllowed()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var owner = await SeedOperatorAsync(kit, "owner@platform.test");

        await using (var db = kit.NewDb())
            (await As(kit, db, owner).UpdateTeamMember(owner.Id, Patch(role: PlatformRoles.Owner, active: true, name: "Renamed Owner"), default))
                .Should().BeOfType<OkObjectResult>();

        (await ReadAsync(kit, owner.Id)).FullName.Should().Be("Renamed Owner");
    }

    // ── Last Owner ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheLastActiveOwner_CannotBeDemotedOrDeactivated()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var onlyOwner = await SeedOperatorAsync(kit, "owner@platform.test");
        // An inactive Owner does not count as a way back in.
        await SeedOperatorAsync(kit, "retired.owner@platform.test", PlatformRoles.Owner, active: false);

        await using (var db = kit.NewDb())
        {
            // The caller carries an Owner claim but is not that row (for example a token minted before a demotion).
            var c = Create(kit, db, Guid.NewGuid(), PlatformRoles.Owner, "stale.owner@platform.test");
            Status(await c.UpdateTeamMember(onlyOwner.Id, Patch(role: PlatformRoles.Admin), default)).Should().Be(409);
            Status(await c.UpdateTeamMember(onlyOwner.Id, Patch(active: false), default)).Should().Be(409);
            Status(await c.DeactivateTeamMember(onlyOwner.Id, default)).Should().Be(409);
        }

        var after = await ReadAsync(kit, onlyOwner.Id);
        after.Role.Should().Be(PlatformRoles.Owner);
        after.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Owner_CanDemoteAndDeactivateAnotherOwner_WhileAnotherActiveOwnerRemains()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var owner = await SeedOperatorAsync(kit, "owner@platform.test");
        var second = await SeedOperatorAsync(kit, "second@platform.test");
        var third = await SeedOperatorAsync(kit, "third@platform.test");

        await using (var db = kit.NewDb())
        {
            var c = As(kit, db, owner);
            (await c.UpdateTeamMember(second.Id, Patch(role: PlatformRoles.Admin), default)).Should().BeOfType<OkObjectResult>();
            (await c.DeactivateTeamMember(third.Id, default)).Should().BeOfType<OkObjectResult>();
        }

        var secondAfter = await ReadAsync(kit, second.Id);
        secondAfter.Role.Should().Be(PlatformRoles.Admin);
        secondAfter.UpdatedAtUtc.Should().BeAfter(second.UpdatedAtUtc ?? DateTime.MinValue);
        var thirdAfter = await ReadAsync(kit, third.Id);
        thirdAfter.IsActive.Should().BeFalse();
        thirdAfter.UpdatedAtUtc.Should().BeAfter(third.UpdatedAtUtc ?? DateTime.MinValue);
    }

    [Fact]
    public async Task Owner_CanPromoteAMemberToOwner()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var owner = await SeedOperatorAsync(kit, "owner@platform.test");
        var support = await SeedOperatorAsync(kit, "support@platform.test", PlatformRoles.Support);

        await using (var db = kit.NewDb())
            (await As(kit, db, owner).UpdateTeamMember(support.Id, Patch(role: PlatformRoles.Owner), default))
                .Should().BeOfType<OkObjectResult>();

        (await ReadAsync(kit, support.Id)).Role.Should().Be(PlatformRoles.Owner);
    }

    // ── Actor in the audit trail ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task TeamChanges_AreAuditedUnderTheRealOperator()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var owner = await SeedOperatorAsync(kit, "ops.owner@platform.test");
        var support = await SeedOperatorAsync(kit, "support@platform.test", PlatformRoles.Support);
        var finance = await SeedOperatorAsync(kit, "finance@platform.test", PlatformRoles.Finance);

        await using (var db = kit.NewDb())
        {
            var c = As(kit, db, owner);
            (await c.CreateTeamMember(NewMember("auditor@platform.test", PlatformRoles.Auditor), default)).Should().BeOfType<OkObjectResult>();
            (await c.UpdateTeamMember(support.Id, Patch(role: PlatformRoles.Admin), default)).Should().BeOfType<OkObjectResult>();
            (await c.DeactivateTeamMember(finance.Id, default)).Should().BeOfType<OkObjectResult>();
        }

        foreach (var action in new[] { "PlatformUserCreated", "PlatformUserUpdated", "PlatformUserDeactivated" })
            (await AuditRowsAsync(kit, action)).Should().ContainSingle(action)
                .Which.PerformedByName.Should().Be("ops.owner@platform.test");
    }
}
