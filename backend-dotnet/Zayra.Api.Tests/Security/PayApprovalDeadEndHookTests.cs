using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Employees.Onboarding;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// The dead-end monitor is wired into every place that changes who may review pay, and every place pay is submitted.
/// Each hook category: a stranded package opens exactly one notice per recipient, re-adding a reviewer closes it.
/// </summary>
public sealed partial class EmployeeAccessTests
{
    // ── harness ────────────────────────────────────────────────────────────────────────────────────────

    private sealed class HookTokens : ITokenService
    {
        public string CreateAccessToken(User user, IReadOnlyCollection<string> roles, IReadOnlyCollection<string> permissions, Tenant tenant,
            IReadOnlyCollection<EntityAccessGrant> entityAccess, EntityScopeDescriptor entityScope, out DateTime expiresAtUtc)
        { expiresAtUtc = DateTime.UtcNow.AddHours(1); return $"hook-access-{user.Id}"; }
        public string CreateSecureToken() => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        public string HashToken(string token) => Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    }

    private sealed class ThrowingMonitor : IPayApprovalDeadEndMonitor
    {
        public int Calls;
        public Task SyncCompanyAsync(Guid tenantId, Guid companyId, string trigger, CancellationToken ct)
        { Calls++; throw new InvalidOperationException("monitor unavailable"); }
    }

    private static IPayApprovalDeadEndMonitor MonitorFor(ZayraDbContext db) =>
        new PayApprovalDeadEndMonitor(db, new HttpContextAccessor(), null, NullLogger<PayApprovalDeadEndMonitor>.Instance);

    private static AccessManagementService AccessWith(ZayraDbContext db, IPayApprovalDeadEndMonitor? monitor = null) =>
        new(db, new Pbkdf2PasswordHasher(), new AuditService(db), new HookTokens(), null, null, monitor ?? MonitorFor(db));

    private static EmployeeManagementService EmployeesWith(ZayraDbContext db, IPayApprovalDeadEndMonitor? monitor = null) =>
        new(db, new AuditService(db), new NoStorage(), TestNotifications.For(db), deadEndMonitor: monitor ?? MonitorFor(db));

    private static AccessController AccessCtl(ZayraDbContext db, World w, Guid callerId)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", w.TenantId.ToString()), new(ClaimTypes.NameIdentifier, callerId.ToString()), new("permission", "security.manage"),
            new(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
        };
        return new AccessController(AccessWith(db), db, null!, stepUp: StepUpTestKit.AlwaysConfirmed(db))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } },
        };
    }

    private async Task<List<Notification>> OpenNoticesAsync(World w, Guid? companyId = null) =>
        (await DeadEndNoticesAsync(w, companyId)).Where(n => n.Status != PayApprovalDeadEnds.ResolvedStatus).ToList();

    /// <summary>A second recipient: holds security.manage but not payroll.approve, so it never counts as a reviewer.</summary>
    private async Task<Guid> AddSecurityOfficerAsync(World w)
    {
        await using (var db = _fx.CreateDb())
        {
            var perm = await db.Permissions.SingleAsync(p => p.Key == "security.manage");
            var role = new Role
            {
                Id = Guid.NewGuid(), TenantId = w.TenantId, Name = "Security Officer", NormalizedName = AuthService.Normalize("Security Officer"),
                Description = "Security Officer", IsSystem = true, IsEditable = true, IsActive = true, AuthorityLevel = 10,
            };
            db.Roles.Add(role);
            db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = perm.Id });
            await db.SaveChangesAsync();
        }
        return await AddUserAsync(w, $"secoff.{Guid.NewGuid():N}@{w.Domain}", ["Security Officer"], true, staff: true, name: "Security Officer");
    }

    private Task<int> HireMonitoredAsync(World w, Guid creator, int seed, IPayApprovalDeadEndMonitor? monitor = null) =>
        WithTransientApproverAsync(w, () => HireMonitoredCoreAsync(w, creator, seed, monitor));

    private async Task<int> HireMonitoredCoreAsync(World w, Guid creator, int seed, IPayApprovalDeadEndMonitor? monitor)
    {
        await using var db = _fx.CreateDb();
        var request = Hire($"Hook Person {seed}", $"hook.{seed}.{Guid.NewGuid():N}@{w.Domain}", w.CompanyId) with {
            Nationality = "Indian",
            ComplianceRecords = [new EmployeeComplianceRecordRequest("SA", "iqama_number", "Iqama", TestIds.ValidIqama(seed),
                null, DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)), true, true)],
            SalaryBreakdown = new EmployeeSalaryBreakdownRequest(9300m + seed, 0, 0, 0, 0, 0, 0, null, null, "SAR")
        };
        return (await EmployeesWith(db, monitor).CreateAsync(w.TenantId, request, Ctx(creator, w.TenantId), default)).Id;
    }

    private async Task AssertOneNoticePerRecipientAsync(World w, params Guid[] recipients)
    {
        var open = await OpenNoticesAsync(w);
        open.Select(n => n.UserId!.Value).Should().BeEquivalentTo(recipients, "exactly one open notice per recipient");
        (await CompanyAuditAsync(w)).Count(a => a == PayApprovalDeadEnds.OpenedAction).Should().Be(1);
    }

    private async Task AssertAllResolvedAsync(World w)
    {
        (await OpenNoticesAsync(w)).Should().BeEmpty();
        (await DeadEndNoticesAsync(w)).Should().NotBeEmpty().And.OnlyContain(n => n.Status == PayApprovalDeadEnds.ResolvedStatus);
        (await CompanyAuditAsync(w)).Should().Contain(PayApprovalDeadEnds.ResolvedAction);
    }

    private async Task<Guid> AddReviewerAsync(World w) =>
        await AddUserAsync(w, $"fin.{Guid.NewGuid():N}@{w.Domain}", ["Payroll Approver"], true, staff: true, name: "Finance Reviewer");

    private async Task AssignRolesHookAsync(World w, Guid userId, params string[] roles)
    {
        await using var db = _fx.CreateRetryingDb();
        await AccessWith(db).AssignRolesAsync(w.TenantId, userId, new AssignRolesRequest(roles), EntityScopeContext.GroupLevel, Ctx(w.AdminId, w.TenantId), default);
    }

    // ── hook categories ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Hook_LastReviewerDeactivated_OpensOneNoticePerRecipient_AndAddingAReviewerResolvesIt()
    {
        var w = await SeedAsync();
        var officer = await AddSecurityOfficerAsync(w);
        var reviewer = await AddReviewerAsync(w);
        await HireWithPayAsync(w, w.AdminId, 11);
        (await DeadEndNoticesAsync(w)).Should().BeEmpty("a reviewer exists");

        await using (var db = _fx.CreateRetryingDb())
            await AccessWith(db).SuspendUserAsync(w.TenantId, reviewer, "left the team", EntityScopeContext.GroupLevel, Ctx(w.AdminId, w.TenantId), default);
        await AssertOneNoticePerRecipientAsync(w, w.AdminId, officer);

        await AssignRolesHookAsync(w, w.HrOfficer2Id, "HR Officer", "Payroll Approver");
        await AssertAllResolvedAsync(w);
    }

    [Fact]
    public async Task Hook_RoleRemoved_OpensOneNotice_AndRestoringTheRoleResolvesIt()
    {
        var w = await SeedAsync();
        var reviewer = await AddReviewerAsync(w);
        await HireWithPayAsync(w, w.AdminId, 12);

        await AssignRolesHookAsync(w, reviewer, "Employee");
        await AssertOneNoticePerRecipientAsync(w, w.AdminId);

        await AssignRolesHookAsync(w, reviewer, "Payroll Approver");
        await AssertAllResolvedAsync(w);
    }

    [Fact]
    public async Task Hook_RolePermissionsChanged_ReachesEveryHolderOfTheRole()
    {
        var w = await SeedAsync();
        await AddReviewerAsync(w);
        await HireWithPayAsync(w, w.AdminId, 13);
        Guid roleId;
        await using (var db = _fx.CreateDb())
            roleId = (await db.Roles.IgnoreQueryFilters().SingleAsync(r => r.TenantId == w.TenantId && r.NormalizedName == AuthService.Normalize("Payroll Approver"))).Id;

        await using (var db = _fx.CreateRetryingDb())
            await AccessWith(db).SetRolePermissionsAsync(w.TenantId, roleId, new BulkRolePermissionsRequest(["profile.read"]), Ctx(w.AdminId, w.TenantId), default);
        await AssertOneNoticePerRecipientAsync(w, w.AdminId);

        await using (var db = _fx.CreateRetryingDb())
            await AccessWith(db).SetRolePermissionsAsync(w.TenantId, roleId, new BulkRolePermissionsRequest(["payroll.approve"]), Ctx(w.AdminId, w.TenantId), default);
        await AssertAllResolvedAsync(w);
    }

    [Fact]
    public async Task Hook_CompanyGrantRemoved_OpensOneNotice_AndGrantingItAgainResolvesIt()
    {
        var w = await SeedAsync();
        var reviewer = await AddReviewerAsync(w);
        Guid grantId;
        await using (var db = _fx.CreateDb())
        {
            var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == reviewer);
            user.IsGroupScope = false;
            var grant = new UserEntityAccess { TenantId = w.TenantId, UserId = reviewer, CompanyId = w.CompanyId, GrantMode = EntityGrantModes.SelectedCompanies, Role = "Company User", IsActive = true };
            db.UserEntityAccesses.Add(grant);
            await db.SaveChangesAsync();
            grantId = grant.Id;
        }
        await HireWithPayAsync(w, w.AdminId, 14);
        (await DeadEndNoticesAsync(w)).Should().BeEmpty("the reviewer holds a grant for the company");

        await using (var db = _fx.CreateRetryingDb())
            (await AccessCtl(db, w, w.AdminId).DeleteEntityGrant(grantId, default)).Should().BeOfType<NoContentResult>();
        await AssertOneNoticePerRecipientAsync(w, w.AdminId);

        await using (var db = _fx.CreateRetryingDb())
            await AccessWith(db).CreateEntityGrantAsync(w.TenantId, reviewer, w.CompanyId, "Company Reviewer", EntityGrantModes.SelectedCompanies, Ctx(w.AdminId, w.TenantId), default);
        await AssertAllResolvedAsync(w);
    }

    [Fact]
    public async Task Hook_GroupScopeNarrowed_OpensOneNotice_AndWideningItResolvesIt()
    {
        var w = await SeedAsync();
        var reviewer = await AddReviewerAsync(w);
        await HireWithPayAsync(w, w.AdminId, 15);

        await using (var db = _fx.CreateRetryingDb())
            (await AccessCtl(db, w, w.AdminId).SetGroupScope(reviewer, new SetGroupScopeRequest(false), default)).Should().BeOfType<NoContentResult>();
        await AssertOneNoticePerRecipientAsync(w, w.AdminId);

        await using (var db = _fx.CreateRetryingDb())
            (await AccessCtl(db, w, w.AdminId).SetGroupScope(reviewer, new SetGroupScopeRequest(true), default)).Should().BeOfType<NoContentResult>();
        await AssertAllResolvedAsync(w);
    }

    [Fact]
    public async Task Hook_PermissionOverrideDeniesApproval_OpensOneNotice_AndRemovingTheDenyResolvesIt()
    {
        var w = await SeedAsync();
        var reviewer = await AddReviewerAsync(w);
        await HireWithPayAsync(w, w.AdminId, 16);

        await using (var db = _fx.CreateRetryingDb())
            await AccessWith(db).SetPermissionOverrideAsync(w.TenantId, reviewer, new PermissionOverrideRequest("payroll.approve", "Deny", "test", null),
                EntityScopeContext.GroupLevel, Ctx(w.AdminId, w.TenantId), default);
        await AssertOneNoticePerRecipientAsync(w, w.AdminId);

        Guid overrideId;
        await using (var db = _fx.CreateDb())
            overrideId = (await db.UserPermissionOverrides.IgnoreQueryFilters().SingleAsync(o => o.UserId == reviewer && o.PermissionKey == "payroll.approve")).Id;
        await using (var db = _fx.CreateRetryingDb())
            (await AccessWith(db).DeletePermissionOverrideAsync(w.TenantId, reviewer, overrideId, EntityScopeContext.GroupLevel, Ctx(w.AdminId, w.TenantId), default)).Should().BeTrue();
        await AssertAllResolvedAsync(w);
    }

    [Fact]
    public async Task Hook_MfaPolicyTightened_MakesTheReviewerIneligible_AndLoosenOpensNothingNew()
    {
        var w = await SeedAsync();
        var reviewer = await AddReviewerAsync(w);
        await using (var db = _fx.CreateDb())
        {
            db.SecuritySettings.Add(new SecuritySetting { TenantId = w.TenantId, MfaPolicy = MfaPolicies.Optional, MfaRequired = false, PrivilegedMfaEnforceFromUtc = DateTime.UtcNow.AddDays(-1) });
            await db.SaveChangesAsync();
        }
        await HireWithPayAsync(w, w.AdminId, 17);
        (await DeadEndNoticesAsync(w)).Should().BeEmpty("each person chooses, so the reviewer is usable");

        UpdateSecuritySettingRequest Policy(string policy, string? reason = null) =>
            new(null, null, null, null, null, null, null, null, null, null, null, null, MfaPolicy: policy, MfaPolicyReason: reason);
        await using (var db = _fx.CreateRetryingDb())
            await AccessWith(db).UpdateSecuritySettingsAsync(w.TenantId, Policy(MfaPolicies.PrivilegedRoles), Ctx(w.AdminId, w.TenantId), default);
        await AssertOneNoticePerRecipientAsync(w, w.AdminId);

        await using (var db = _fx.CreateRetryingDb())
            await AccessWith(db).UpdateSecuritySettingsAsync(w.TenantId, Policy(MfaPolicies.Optional, "pilot exception"), Ctx(w.AdminId, w.TenantId), default);
        await AssertAllResolvedAsync(w);
        reviewer.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Hook_MfaReset_OfAnEnrolledReviewer_UnderEnforcedPolicy_OpensOneNotice()
    {
        var w = await SeedAsync();
        var reviewer = await AddReviewerAsync(w);
        await using (var db = _fx.CreateDb())
        {
            db.SecuritySettings.Add(new SecuritySetting { TenantId = w.TenantId, MfaPolicy = MfaPolicies.PrivilegedRoles, MfaRequired = false, PrivilegedMfaEnforceFromUtc = DateTime.UtcNow.AddDays(-1) });
            var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == reviewer);
            user.MFAEnabled = true;
            user.MfaSecretEncrypted = "enrolled-secret";
            await db.SaveChangesAsync();
        }
        await HireWithPayAsync(w, w.AdminId, 18);
        (await DeadEndNoticesAsync(w)).Should().BeEmpty("the reviewer is enrolled");

        await using (var db = _fx.CreateRetryingDb())
            (await AccessWith(db).ResetUserMfaAsync(w.TenantId, reviewer, "lost the device", EntityScopeContext.GroupLevel, Ctx(w.AdminId, w.TenantId), default))
                .Outcome.Should().Be(MfaAdminResetOutcome.Reset);
        await AssertOneNoticePerRecipientAsync(w, w.AdminId);
    }

    [Fact]
    public async Task Hook_LinkedEmployeeTerminated_OpensOneNotice_AndAddingAReviewerResolvesIt()
    {
        var w = await SeedAsync();
        var reviewer = await AddReviewerAsync(w);
        var employeeId = await AddEmployeeAsync(w, $"fin.person.{Guid.NewGuid():N}@{w.Domain}");
        await using (var db = _fx.CreateDb())
        {
            db.EmployeeUserAccounts.Add(new EmployeeUserAccount
            {
                TenantId = w.TenantId, EmployeeId = employeeId, UserId = reviewer, AccessMode = AccessModes.FullPortal, Status = "Active",
                RequiresPasswordSetup = false, IsPrimary = true,
            });
            (await db.Employees.IgnoreQueryFilters().SingleAsync(e => e.Id == employeeId)).UserAccountId = reviewer;
            await db.SaveChangesAsync();
        }
        await HireWithPayAsync(w, w.AdminId, 19);
        (await DeadEndNoticesAsync(w)).Should().BeEmpty();

        await using (var db = _fx.CreateDb())
            await EmployeesWith(db).ChangeStatusAsync(w.TenantId, employeeId,
                new EmployeeStatusChangeRequest("Terminated", DateOnly.FromDateTime(DateTime.UtcNow), "Left", "Resignation"), Ctx(w.AdminId, w.TenantId), default);
        await AssertOneNoticePerRecipientAsync(w, w.AdminId);

        await AssignRolesHookAsync(w, w.HrOfficer2Id, "HR Officer", "Payroll Approver");
        await AssertAllResolvedAsync(w);
    }

    // ── submit time ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Submit_Create_IsRefusedWithoutAReviewer_SoNoDeadEndOpens_AndOpensNothingWhenOneExists()
    {
        // The second-approver guard stops a salaried hire when nobody else could approve it, so creation cannot strand a package.
        var w = await SeedAsync();
        await using (var db = _fx.CreateDb())
        {
            var request = Hire("Refused Hire", $"refused.{Guid.NewGuid():N}@{w.Domain}", w.CompanyId) with {
                Nationality = "Indian",
                ComplianceRecords = [new EmployeeComplianceRecordRequest("SA", "iqama_number", "Iqama", TestIds.ValidIqama(21),
                    null, DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)), true, true)],
                SalaryBreakdown = new EmployeeSalaryBreakdownRequest(9321m, 0, 0, 0, 0, 0, 0, null, null, "SAR")
            };
            var act = async () => await EmployeesWith(db, null).CreateAsync(w.TenantId, request, Ctx(w.AdminId, w.TenantId), default);
            await act.Should().ThrowAsync<Zayra.Api.Infrastructure.Approvals.SecondApproverCheck.NoEligibleApproverException>();
        }
        (await DeadEndNoticesAsync(w)).Should().BeEmpty();

        var other = await SeedAsync();
        await AddReviewerAsync(other);
        await HireMonitoredAsync(other, other.AdminId, 22);
        (await DeadEndNoticesAsync(other)).Should().BeEmpty();
    }

    [Fact]
    public async Task Submit_Update_OpensTheNotice_WhenNoReviewerExists_AndNotWhenOneDoes()
    {
        var w = await SeedAsync();
        var id = await HireWithPayAsync(w, w.AdminId, 23);   // created without the monitor: nothing opened yet
        (await DeadEndNoticesAsync(w)).Should().BeEmpty();
        await UpdateWithPayAsync(w, id, 23, 9900m);
        await AssertOneNoticePerRecipientAsync(w, w.AdminId);

        var other = await SeedAsync();
        await AddReviewerAsync(other);
        var otherId = await HireWithPayAsync(other, other.AdminId, 24);
        await UpdateWithPayAsync(other, otherId, 24, 9900m);
        (await DeadEndNoticesAsync(other)).Should().BeEmpty();
    }

    private async Task UpdateWithPayAsync(World w, int employeeId, int seed, decimal basic)
    {
        await using var db = _fx.CreateDb();
        var email = (await db.Employees.IgnoreQueryFilters().SingleAsync(e => e.Id == employeeId)).WorkEmail;
        var request = Hire($"Dead End Person {seed}", email, w.CompanyId) with {
            Nationality = "Indian",
            ComplianceRecords = [new EmployeeComplianceRecordRequest("SA", "iqama_number", "Iqama", TestIds.ValidIqama(seed),
                null, DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)), true, true)],
            SalaryBreakdown = new EmployeeSalaryBreakdownRequest(basic, 0, 0, 0, 0, 0, 0, null, null, "SAR")
        };
        (await EmployeesWith(db).UpdateAsync(w.TenantId, employeeId, request, Ctx(w.AdminId, w.TenantId), default)).Should().NotBeNull();
    }

    // ── backstop ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sweep_CatchesAChangeMadeWithNoHook_AndIsIdempotent()
    {
        var w = await SeedAsync();
        var reviewer = await AddReviewerAsync(w);
        await HireWithPayAsync(w, w.AdminId, 31);
        (await DeadEndNoticesAsync(w)).Should().BeEmpty();

        // A direct edit: nothing in the application saw it.
        await using (var db = _fx.CreateDb())
        {
            var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == reviewer);
            user.IsActive = false; user.Status = "Deactivated";
            await db.SaveChangesAsync();
        }
        (await DeadEndNoticesAsync(w)).Should().BeEmpty("no hook ran");

        var options = Options.Create(new PayApprovalDeadEndSweepOptions { MaxTenantsPerRun = 1000 });
        await using (var db = _fx.CreateDb())
        {
            var sweep = new PayApprovalDeadEndSweepService(db, MonitorFor(db), options, NullLogger<PayApprovalDeadEndSweepService>.Instance);
            (await sweep.TenantsWithPendingWorkAsync(default)).Should().Contain(w.TenantId);
            (await sweep.RunTenantAsync(w.TenantId, default)).Companies.Should().Be(1);
        }
        await AssertOneNoticePerRecipientAsync(w, w.AdminId);

        await using (var db = _fx.CreateDb())
            await new PayApprovalDeadEndSweepService(db, MonitorFor(db), options, NullLogger<PayApprovalDeadEndSweepService>.Instance).RunTenantAsync(w.TenantId, default);
        await AssertOneNoticePerRecipientAsync(w, w.AdminId);   // still one notice, still one audit row

        // And it closes the notice once access is back, again with no hook.
        await using (var db = _fx.CreateDb())
        {
            var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == w.HrOfficer2Id);
            var role = await db.Roles.IgnoreQueryFilters().SingleAsync(r => r.TenantId == w.TenantId && r.NormalizedName == AuthService.Normalize("Payroll Approver"));
            db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
            await db.SaveChangesAsync();
        }
        await using (var db = _fx.CreateDb())
            await new PayApprovalDeadEndSweepService(db, MonitorFor(db), options, NullLogger<PayApprovalDeadEndSweepService>.Instance).RunTenantAsync(w.TenantId, default);
        await AssertAllResolvedAsync(w);
    }

    // ── a failing sync never fails the change ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Hook_TheMonitorFailing_NeverFailsTheCaller()
    {
        var w = await SeedAsync();
        var reviewer = await AddReviewerAsync(w);
        await HireWithPayAsync(w, w.AdminId, 41);
        var thrower = new ThrowingMonitor();

        await using (var db = _fx.CreateRetryingDb())
            await AccessWith(db, thrower).AssignRolesAsync(w.TenantId, reviewer, new AssignRolesRequest(["Employee"]), EntityScopeContext.GroupLevel, Ctx(w.AdminId, w.TenantId), default);
        await using (var db = _fx.CreateDb())
        {
            var roles = await db.UserRoles.Where(r => r.UserId == reviewer).Select(r => r.Role!.Name).ToListAsync();
            roles.Should().Equal(new[] { "Employee" }, "the change was saved even though the sync failed");
        }
        thrower.Calls.Should().BeGreaterThan(0, "the hook did run");

        await AddReviewerAsync(w);   // main's guard needs a second approver to create a salaried hire
        await using (var db = _fx.CreateDb())
            (await EmployeesWith(db, thrower).CreateAsync(w.TenantId,
                Hire("Throwing Hook", $"throwing.{Guid.NewGuid():N}@{w.Domain}", w.CompanyId) with {
                    Nationality = "Indian",
                    ComplianceRecords = [new EmployeeComplianceRecordRequest("SA", "iqama_number", "Iqama", TestIds.ValidIqama(42),
                        null, DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)), true, true)],
                    SalaryBreakdown = new EmployeeSalaryBreakdownRequest(9000m, 0, 0, 0, 0, 0, 0, null, null, "SAR") },
                Ctx(w.AdminId, w.TenantId), default)).Id.Should().BeGreaterThan(0);
    }
}
