using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The workspace owner (User.IsWorkspaceOwner) and "Confirm it's you" (StepUpService): no other Admin changes the
/// owner's account; any action against an Admin needs the caller's password again (and two-step code when it is on);
/// only the owner hands ownership on, and KynexOne support sets it when the owner is gone.
/// </summary>
public sealed class OwnerAndStepUpTests
{
    private const string Password = "Correct-Horse-9!";

    // ── The owner is protected ────────────────────────────────────────────────

    [Fact]
    public async Task AnotherAdmin_CannotLockSuspendResetDemoteOrDisableTheOwner()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);
        var asAdmin = Controller(db, w, w.AdminId, StepUpTestKit.AlwaysConfirmed(db));

        AssertOwnerProtected(await asAdmin.LockUser(w.OwnerId, new ReasonRequest("x"), default));
        AssertOwnerProtected(await asAdmin.SuspendUser(w.OwnerId, new ReasonRequest("x"), default));
        AssertOwnerProtected(await asAdmin.IssuePasswordResetLink(w.OwnerId, default));
        AssertOwnerProtected(await asAdmin.AdminResetPassword(w.OwnerId, new AdminResetPasswordRequest("Temporary-Pass1!", "asked"), default));
        AssertOwnerProtected((await asAdmin.SetAccessMode(w.OwnerId, new AccessModeRequest(AccessModes.NoLogin, "x"), default)).Result);
        AssertOwnerProtected((await asAdmin.AssignRoles(w.OwnerId, new AssignRolesRequest(["Employee"]), default)).Result);

        var owner = await db.Users.AsNoTracking().Include(x => x.UserRoles).SingleAsync(x => x.Id == w.OwnerId);
        owner.Status.Should().Be("Active");
        owner.IsLocked.Should().BeFalse();
        owner.MustChangePassword.Should().BeFalse();
        owner.UserRoles.Should().ContainSingle(x => x.RoleId == w.AdminRoleId);
        (await db.PasswordResetTokens.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AnotherAdmin_CannotOverrideTheOwnersPermissions()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);
        db.Permissions.Add(new Permission { Key = "security.manage", Module = "Security", Description = "Manage security" });
        await db.SaveChangesAsync();

        var result = await Controller(db, w, w.AdminId, StepUpTestKit.AlwaysConfirmed(db))
            .SetPermissionOverride(w.OwnerId, new PermissionOverrideRequest("security.manage", "Deny", "x", null), default);

        AssertOwnerProtected(result.Result);
        (await db.UserPermissionOverrides.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task MakingSomeoneAnAdmin_TellsEveryAdmin()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);
        var notices = new CapturingNotices();

        var result = await Controller(db, w, w.OwnerId, StepUpTestKit.AlwaysConfirmed(db), notices)
            .AssignRoles(w.EmployeeId, new AssignRolesRequest(["Admin"]), default);

        result.Result.Should().BeOfType<OkObjectResult>();
        notices.Requests.Select(x => x.UserId).Should().BeEquivalentTo(new Guid?[] { w.AdminId, w.EmployeeId },
            "the new Admin and every other Admin are told; the one who did it is not");
    }

    [Fact]
    public async Task TheOwner_StillManagesOtherAdmins()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);

        var result = await Controller(db, w, w.OwnerId, StepUpTestKit.AlwaysConfirmed(db)).LockUser(w.AdminId, new ReasonRequest("left"), default);

        result.Should().BeOfType<NoContentResult>();
        (await db.Users.AsNoTracking().SingleAsync(x => x.Id == w.AdminId)).Status.Should().Be("Locked");
    }

    // ── Confirm it's you ──────────────────────────────────────────────────────

    [Fact]
    public async Task ActingOnAnAdmin_NeedsConfirmation_AndWorksOnceConfirmed()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);
        var stepUp = StepUpTestKit.Real(db);

        var refused = await Controller(db, w, w.OwnerId, stepUp).LockUser(w.AdminId, new ReasonRequest("x"), default);
        Code(refused).Should().Be(StepUpService.RequiredCode);
        (await db.Users.AsNoTracking().SingleAsync(x => x.Id == w.AdminId)).Status.Should().Be("Active");

        var confirmed = await stepUp.ConfirmAsync(w.OwnerId, w.TenantId, Password, null, Ctx, default);
        confirmed.Outcome.Should().Be(StepUpOutcome.Confirmed);
        var controller = Controller(db, w, w.OwnerId, stepUp);
        controller.Request.Headers[StepUpService.HeaderName] = confirmed.Token;

        (await controller.LockUser(w.AdminId, new ReasonRequest("x"), default)).Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task ActingOnAnOrdinaryUser_NeedsNoConfirmation()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);

        var result = await Controller(db, w, w.OwnerId, StepUpTestKit.Real(db)).LockUser(w.EmployeeId, new ReasonRequest("x"), default);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task CreatingAnAdmin_NeedsConfirmation()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);

        var result = await Controller(db, w, w.OwnerId, StepUpTestKit.Real(db)).CreateUser(
            new CreateUserRequest("new.admin@example.test", "New Admin", "Strong-Pass-123!", ["Admin"], IsGroupScope: true), default);

        Code(result.Result).Should().Be(StepUpService.RequiredCode);
        (await db.Users.AnyAsync(x => x.Email == "new.admin@example.test")).Should().BeFalse();
    }

    [Fact]
    public async Task WrongPasswords_CountTowardTheLockout()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);
        var stepUp = StepUpTestKit.Real(db);

        (await stepUp.ConfirmAsync(w.OwnerId, w.TenantId, "wrong", null, Ctx, default)).Outcome.Should().Be(StepUpOutcome.WrongPassword);
        for (var i = 0; i < 3; i++) await stepUp.ConfirmAsync(w.OwnerId, w.TenantId, "wrong", null, Ctx, default);
        (await stepUp.ConfirmAsync(w.OwnerId, w.TenantId, "wrong", null, Ctx, default)).Outcome.Should().Be(StepUpOutcome.LockedOut);

        (await db.Users.AsNoTracking().SingleAsync(x => x.Id == w.OwnerId)).IsLocked.Should().BeTrue();
        (await stepUp.ConfirmAsync(w.OwnerId, w.TenantId, Password, null, Ctx, default)).Outcome.Should().Be(StepUpOutcome.LockedOut);
    }

    [Fact]
    public async Task WithTwoStepOn_TheCodeIsAskedFor()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);
        var owner = await db.Users.SingleAsync(x => x.Id == w.OwnerId);
        owner.MFAEnabled = true;
        owner.MfaSecretEncrypted = new TotpService(StepUpTestKit.Protection).EncryptSecret("JBSWY3DPEHPK3PXP");
        await db.SaveChangesAsync();
        var stepUp = StepUpTestKit.Real(db);

        (await stepUp.ConfirmAsync(w.OwnerId, w.TenantId, Password, null, Ctx, default)).Outcome.Should().Be(StepUpOutcome.CodeRequired);
        (await stepUp.ConfirmAsync(w.OwnerId, w.TenantId, Password, "000000", Ctx, default)).Outcome.Should().Be(StepUpOutcome.WrongCode);
    }

    [Fact]
    public async Task AToken_IsTheCallersOwn_AndDiesWithTheSession()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);
        var stepUp = StepUpTestKit.Real(db);
        var token = (await stepUp.ConfirmAsync(w.OwnerId, w.TenantId, Password, null, Ctx, default)).Token;

        (await stepUp.IsConfirmedAsync(w.OwnerId, w.TenantId, token, default)).Should().BeTrue();
        (await stepUp.IsConfirmedAsync(w.AdminId, w.TenantId, token, default)).Should().BeFalse("another user cannot use it");
        (await stepUp.IsConfirmedAsync(w.OwnerId, w.TenantId, token + "x", default)).Should().BeFalse();

        // A password change (or anything that rotates the session stamp) voids it.
        var owner = await db.Users.SingleAsync(x => x.Id == w.OwnerId);
        TenantSessionSecurity.RotateStamp(owner, DateTime.UtcNow.AddSeconds(5));
        await db.SaveChangesAsync();
        (await stepUp.IsConfirmedAsync(w.OwnerId, w.TenantId, token, default)).Should().BeFalse();
    }

    // ── Handing ownership on ──────────────────────────────────────────────────

    [Fact]
    public async Task TheOwner_HandsOwnershipToAnotherActiveAdmin()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);

        var result = await Controller(db, w, w.OwnerId, StepUpTestKit.AlwaysConfirmed(db)).MakeOwner(w.AdminId, default);

        result.Should().BeOfType<NoContentResult>();
        (await db.Users.AsNoTracking().SingleAsync(x => x.Id == w.AdminId)).IsWorkspaceOwner.Should().BeTrue();
        (await db.Users.AsNoTracking().SingleAsync(x => x.Id == w.OwnerId)).IsWorkspaceOwner.Should().BeFalse();
        (await db.AuditLogs.AsNoTracking().CountAsync(x => x.Action == "access.ownership_transferred")).Should().Be(1);
    }

    [Fact]
    public async Task OnlyTheOwner_HandsItOn_AndOnlyToAnActiveAdmin()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);

        (await Controller(db, w, w.AdminId, StepUpTestKit.AlwaysConfirmed(db)).MakeOwner(w.AdminId, default))
            .Should().BeOfType<BadRequestObjectResult>("the caller is not the owner");
        (await Controller(db, w, w.OwnerId, StepUpTestKit.AlwaysConfirmed(db)).MakeOwner(w.EmployeeId, default))
            .Should().BeOfType<BadRequestObjectResult>("the new owner must be an Admin");
        (await db.Users.AsNoTracking().SingleAsync(x => x.Id == w.OwnerId)).IsWorkspaceOwner.Should().BeTrue();
    }

    [Fact]
    public async Task MakingSomeoneOwner_NeedsConfirmation()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);

        var result = await Controller(db, w, w.OwnerId, StepUpTestKit.Real(db)).MakeOwner(w.AdminId, default);

        Code(result).Should().Be(StepUpService.RequiredCode);
        (await db.Users.AsNoTracking().SingleAsync(x => x.Id == w.OwnerId)).IsWorkspaceOwner.Should().BeTrue();
    }

    [Fact]
    public async Task Support_SetsTheOwner_WhenTheOwnerIsGone()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);
        var owner = await db.Users.SingleAsync(x => x.Id == w.OwnerId);
        owner.IsDeleted = true;
        owner.IsWorkspaceOwner = false;
        await db.SaveChangesAsync();

        await Service(db).SetOwnerByPlatformAsync(w.TenantId, w.AdminId, "Customer CEO verified by phone", Ctx, default);

        (await db.Users.AsNoTracking().SingleAsync(x => x.Id == w.AdminId)).IsWorkspaceOwner.Should().BeTrue();
        (await db.AuditLogs.AsNoTracking().SingleAsync(x => x.Action == "platform.workspace_owner_set")).Metadata
            .Should().Contain("Customer CEO verified by phone");
    }

    [Fact]
    public async Task TheUserList_SaysWhoTheOwnerIs()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);

        var owner = await Service(db).GetUserAsync(w.TenantId, w.OwnerId, EntityScopeContext.GroupLevel, default);
        var admin = await Service(db).GetUserAsync(w.TenantId, w.AdminId, EntityScopeContext.GroupLevel, default);

        owner!.IsOwner.Should().BeTrue();
        admin!.IsOwner.Should().BeFalse();
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static readonly RequestContext Ctx = new("127.0.0.1", "tests");

    private static readonly IOptions<JwtOptions> Jwt = Options.Create(new JwtOptions
    {
        Issuer = "Zayra.Tests",
        TenantAudience = "kynexone-tenant-test",
        PlatformAudience = "kynexone-platform-test",
        SigningKey = "TEST_SIGNING_KEY_WITH_MORE_THAN_64_CHARACTERS_FOR_OWNER_AND_STEP_UP_TESTS",
        AccessTokenMinutes = 30,
        RefreshTokenDays = 7,
    });

    private sealed record World(Guid TenantId, Guid OwnerId, Guid AdminId, Guid EmployeeId, Guid AdminRoleId);

    private static ZayraDbContext CreateDb() => new(
        new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<World> SeedAsync(ZayraDbContext db)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Kynex HQ", Slug = "kynex-hq", IsActive = true };
        var hash = new Pbkdf2PasswordHasher().Hash(Password);
        User NewUser(string email, bool owner = false) => new()
        {
            Id = Guid.NewGuid(), TenantId = tenant.Id, Tenant = tenant, Email = email, NormalizedEmail = email.ToUpperInvariant(),
            FullName = email, PasswordHash = hash, Status = "Active", AccessMode = AccessModes.FullPortal, IsActive = true,
            IsWorkspaceOwner = owner,
        };
        var owner = NewUser("owner@example.test", owner: true);
        var admin = NewUser("admin@example.test");
        var employee = NewUser("employee@example.test");
        var adminRole = new Role { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Admin", NormalizedName = "ADMIN", Description = "Admin", IsActive = true };
        var employeeRole = new Role { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Employee", NormalizedName = "EMPLOYEE", Description = "Employee", IsActive = true };
        db.Tenants.Add(tenant);
        db.SecuritySettings.Add(new SecuritySetting { Id = Guid.NewGuid(), TenantId = tenant.Id });
        db.Users.AddRange(owner, admin, employee);
        db.Roles.AddRange(adminRole, employeeRole);
        db.UserRoles.AddRange(
            new UserRole { UserId = owner.Id, RoleId = adminRole.Id },
            new UserRole { UserId = admin.Id, RoleId = adminRole.Id },
            new UserRole { UserId = employee.Id, RoleId = employeeRole.Id });
        await db.SaveChangesAsync();
        return new World(tenant.Id, owner.Id, admin.Id, employee.Id, adminRole.Id);
    }

    private static AccessManagementService Service(ZayraDbContext db) =>
        new(db, new Pbkdf2PasswordHasher(), new AuditService(db), new JwtTokenService(Jwt),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["APP_URL"] = "https://app.kynexone.test" }).Build());

    private static AccessController Controller(ZayraDbContext db, World w, Guid callerId, StepUpService stepUp,
        Zayra.Api.Infrastructure.Notifications.INotificationService? notices = null) =>
        new(Service(db), db, new NoEmail(), Auth(db), stepUp: stepUp, adminNotices: notices)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", w.TenantId.ToString()),
                        new Claim(ClaimTypes.NameIdentifier, callerId.ToString()),
                        new Claim("is_group_scope", "true"),
                    }, "test")),
                },
            },
        };

    private static AuthService Auth(ZayraDbContext db)
    {
        var tokens = new JwtTokenService(Jwt);
        var audit = new AuditService(db);
        var totp = new TotpService(StepUpTestKit.Protection);
        return new AuthService(db, new Pbkdf2PasswordHasher(), tokens, audit, new NoEmail(), Jwt, new MfaService(db, totp, tokens, audit), totp,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthService>.Instance);
    }

    private static string? Code(IActionResult? result)
    {
        var body = result.Should().BeAssignableTo<ObjectResult>().Subject;
        body.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        var json = JsonDocument.Parse(JsonSerializer.Serialize(body.Value)).RootElement;
        return json.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static void AssertOwnerProtected(IActionResult? result) => Code(result).Should().Be(PrivilegeCeiling.Codes.OwnerProtected);

    private sealed class CapturingNotices : Zayra.Api.Infrastructure.Notifications.INotificationService
    {
        public List<Zayra.Api.Infrastructure.Notifications.NotificationRequest> Requests { get; } = [];
        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<NotificationDelivery>> EnqueueAsync(Zayra.Api.Infrastructure.Notifications.NotificationRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult<IReadOnlyList<NotificationDelivery>>([]);
        }
    }

    private sealed class NoEmail : IEmailService
    {
        public Task SendAsync(string toAddress, string toName, string subject, string htmlBody,
            IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
