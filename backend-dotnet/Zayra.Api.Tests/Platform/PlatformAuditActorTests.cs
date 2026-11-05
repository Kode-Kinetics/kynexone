using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;
using Zayra.Api.Tests.Security;
using static Zayra.Api.Tests.Platform.PlatformOperatorKit;

namespace Zayra.Api.Tests.Platform;

/// <summary>
/// Every platform console write names the operator who made it, and the platform audit view (no tenant chosen)
/// lists the platform's own changes: team, platform configuration, and per-tenant security settings.
/// </summary>
public sealed class PlatformAuditActorTests
{
    private const string OperatorEmail = "ops.owner@platform.test";

    [Fact]
    public void NoAuditWriteInTheController_UsesTheGenericPlaceholderAsTheActor()
    {
        var path = Path.Combine(SourceScan.ResolveApiRoot(), "Controllers", "PlatformController.cs");
        var (_, code) = SourceScan.Files(Path.GetDirectoryName(path)!, "PlatformController.cs").Single();

        var offenders = Regex.Matches(code, "PerformedByName\\s*=[^;\\n]*\"platform_admin\"")
            .Select(m => code[..m.Index].Count(c => c == '\n') + 1)
            .ToList();

        offenders.Should().BeEmpty("audit rows must name the real operator (PlatformActorEmail()); offending lines: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public async Task PlatformMfaDateChange_IsAuditedUnderTheRealOperator()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var owner = await SeedOperatorAsync(kit, OperatorEmail);

        await using (var db = kit.NewDb())
            (await As(kit, db, owner).SetPlatformPrivilegedMfaEnforcement(
                new PrivilegedMfaEnforcementRequest(PrivilegedMfaPolicy.FormatDate(DateTime.UtcNow.AddDays(30)), "rollout schedule agreed"), default))
                .Should().BeOfType<OkObjectResult>();

        (await AuditRowsAsync(kit, "PrivilegedMfaEnforcementChanged")).Should().ContainSingle()
            .Which.PerformedByName.Should().Be(OperatorEmail);
    }

    [Fact]
    public async Task DefaultAuditView_ListsPlatformLevelChanges_AndStillHidesUnrelatedTenantRows()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var owner = await SeedOperatorAsync(kit, OperatorEmail);
        var support = await SeedOperatorAsync(kit, "support@platform.test", PlatformRoles.Support);
        var finance = await SeedOperatorAsync(kit, "finance@platform.test", PlatformRoles.Finance);
        await using (var seed = kit.NewDb())
        {
            seed.TenantSubscriptions.Add(new TenantSubscription { TenantId = kit.TenantId, Status = "Active" });
            // A tenant-scoped row of an unrelated kind must stay out of the platform view.
            seed.AdminAuditLogs.Add(new AdminAuditLog
            {
                TenantId = kit.TenantId, EntityType = "Invoice", EntityId = "x", Action = "InvoiceNoteEdited",
                PerformedByName = "someone", IpAddress = "",
            });
            await seed.SaveChangesAsync();
        }

        await using (var db = kit.NewDb())
        {
            var c = As(kit, db, owner);
            (await c.CreateTeamMember(new CreatePlatformUserRequest("new@platform.test", "New", "LongEnough-Password-1!", PlatformRoles.Auditor), default))
                .Should().BeOfType<OkObjectResult>();
            (await c.UpdateTeamMember(support.Id, new UpdatePlatformUserRequest(PlatformRoles.Admin, null, null), default))
                .Should().BeOfType<OkObjectResult>();
            (await c.DeactivateTeamMember(finance.Id, default)).Should().BeOfType<OkObjectResult>();
            (await c.SetPlatformPrivilegedMfaEnforcement(
                new PrivilegedMfaEnforcementRequest(PrivilegedMfaPolicy.FormatDate(DateTime.UtcNow.AddDays(30)), "rollout schedule agreed"), default))
                .Should().BeOfType<OkObjectResult>();
            (await c.UpdateTenantInvitationHandover(kit.TenantId, new UpdateTenantInvitationHandoverRequest(true, "email never arrived"), default))
                .Should().BeOfType<OkObjectResult>();
            (await c.UpdateTenantTwoStepPolicy(kit.TenantId, new UpdateTenantTwoStepPolicyRequest(null, 60, "longer trust for this customer"), default))
                .Should().BeOfType<OkObjectResult>();
            (await c.SetTenantPrivilegedMfaEnforcement(kit.TenantId,
                new PrivilegedMfaEnforcementRequest(PrivilegedMfaPolicy.FormatDate(DateTime.UtcNow.AddDays(20)), "customer needs more time"), default))
                .Should().BeOfType<OkObjectResult>();
        }

        await using var read = kit.NewDb();
        var view = await As(kit, read, owner).GetAuditLogs(tenantId: null, page: 1, pageSize: 100, ct: default);
        var logs = JsonSerializer.SerializeToElement(((OkObjectResult)view).Value).GetProperty("logs").EnumerateArray().ToList();
        var actions = logs.Select(l => l.GetProperty("Action").GetString()).ToList();

        var expectedActions = new[]
        {
            "PlatformUserCreated", "PlatformUserUpdated", "PlatformUserDeactivated", "PrivilegedMfaEnforcementChanged",
            "InvitationHandoverUpdated", "TwoStepPolicyUpdated", "TenantPrivilegedMfaEnforcementChanged",
        };
        foreach (var expected in expectedActions)
            actions.Count(a => a == expected).Should().Be(1, $"{expected} must be listed exactly once");
        actions.Should().NotContain("InvoiceNoteEdited");
        logs.Where(l => expectedActions.Contains(l.GetProperty("Action").GetString()))
            .Should().AllSatisfy(l => l.GetProperty("PerformedByName").GetString().Should().Be(OperatorEmail));
    }

    [Fact]
    public async Task TenantMfaPostponement_IsWrittenToThePlatformAuditTrail_WithReasonAndBothDates()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var owner = await SeedOperatorAsync(kit, OperatorEmail);
        var until = DateTime.UtcNow.AddDays(20);

        await using (var db = kit.NewDb())
            (await As(kit, db, owner).SetTenantPrivilegedMfaEnforcement(kit.TenantId,
                new PrivilegedMfaEnforcementRequest(PrivilegedMfaPolicy.FormatDate(until), "customer needs more time"), default))
                .Should().BeOfType<OkObjectResult>();

        var row = (await AuditRowsAsync(kit, "TenantPrivilegedMfaEnforcementChanged")).Should().ContainSingle().Subject;
        row.TenantId.Should().Be(kit.TenantId);
        row.PerformedByName.Should().Be(OperatorEmail);
        row.NewValuesJson.Should().Contain("customer needs more time").And.Contain("previous").And.Contain("next");
    }
}
