using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Auth;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// POST /api/access/users/{id}/admin-reset-password: an administrator sets a TEMPORARY password for someone who
/// cannot receive a reset email. The administrator knows it, so it may only ever be spent on choosing a new one:
/// sign-in proves it, opens no session, and hands back a single-use token for POST /api/auth/reset-password.
/// </summary>
public sealed class AdminSetTemporaryPasswordTests
{
    private const string Temporary = "Temporary-Pass1!";
    private const string Reason = "Locked out, verified in person";

    [Fact]
    public async Task SettingATemporaryPassword_ForcesAChange_EndsSessionsAndLinks_AndIsAudited()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);
        db.RefreshTokens.Add(new RefreshToken { Id = Guid.NewGuid(), UserId = w.TargetId, TokenHash = "live", ExpiresAtUtc = DateTime.UtcNow.AddDays(1), CreatedAtUtc = DateTime.UtcNow });
        db.PasswordResetTokens.Add(new PasswordResetToken { UserId = w.TargetId, TokenHash = "old-link", ExpiresAtUtc = DateTime.UtcNow.AddHours(1), CreatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var result = await Controller(db, w, w.AdminId).AdminResetPassword(w.TargetId, new AdminResetPasswordRequest(Temporary, Reason), default);

        var body = Body(result.Should().BeOfType<OkObjectResult>().Subject);
        body.GetProperty("mustChangePassword").GetBoolean().Should().BeTrue();
        body.GetRawText().Should().NotContain(Temporary, "the password is never echoed back");

        var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == w.TargetId);
        user.MustChangePassword.Should().BeTrue();
        new Pbkdf2PasswordHasher().Verify(Temporary, user.PasswordHash).Should().BeTrue();
        (await db.RefreshTokens.AsNoTracking().SingleAsync()).RevokedAtUtc.Should().NotBeNull();
        (await db.PasswordResetTokens.AsNoTracking().SingleAsync()).UsedAtUtc.Should().NotBeNull();

        var audit = await db.AuditLogs.AsNoTracking().SingleAsync(x => x.Action == "access.admin_password_reset");
        audit.EntityId.Should().Be(w.TargetId.ToString());
        audit.UserId.Should().Be(w.AdminId, "the administrator becomes a credential handler of this login");
        audit.Metadata.Should().Contain(Reason).And.NotContain(Temporary);
    }

    [Fact]
    public async Task SigningInWithTheTemporaryPassword_OpensNoSession_AndOnlyLetsTheUserChooseTheirOwn()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);
        await Controller(db, w, w.AdminId).AdminResetPassword(w.TargetId, new AdminResetPasswordRequest(Temporary, Reason), default);

        var first = await Auth(db).LoginAsync(new LoginRequest(TargetEmail, Temporary, w.Slug), Ctx, default);
        first.Tokens.Should().BeNull();
        first.PasswordChange.Should().NotBeNull();
        (await db.RefreshTokens.CountAsync()).Should().Be(0);

        // The temporary password itself is refused as the new one — the administrator would still know it.
        var reuse = () => Auth(db).ResetPasswordAsync(new ResetPasswordRequest(first.PasswordChange!.ResetToken, Temporary, w.Slug), Ctx, default);
        await reuse.Should().ThrowAsync<InvalidOperationException>();

        await Auth(db).ResetPasswordAsync(new ResetPasswordRequest(first.PasswordChange!.ResetToken, "MyOwnPassword9!", w.Slug), Ctx, default);
        (await db.Users.AsNoTracking().SingleAsync(x => x.Id == w.TargetId)).MustChangePassword.Should().BeFalse();

        var temporaryAgain = () => Auth(db).LoginAsync(new LoginRequest(TargetEmail, Temporary, w.Slug), Ctx, default);
        await temporaryAgain.Should().ThrowAsync<UnauthorizedAccessException>();
        var own = await Auth(db).LoginAsync(new LoginRequest(TargetEmail, "MyOwnPassword9!", w.Slug), Ctx, default);
        own.Tokens.Should().NotBeNull();
        own.PasswordChange.Should().BeNull();
    }

    [Fact]
    public async Task AWrongPassword_ForAUserWhoMustChange_IsStillJustAWrongPassword()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);
        await Controller(db, w, w.AdminId).AdminResetPassword(w.TargetId, new AdminResetPasswordRequest(Temporary, Reason), default);

        var wrong = () => Auth(db).LoginAsync(new LoginRequest(TargetEmail, "Not-The-Temp1!", w.Slug), Ctx, default);
        await wrong.Should().ThrowAsync<UnauthorizedAccessException>();
        (await db.PasswordResetTokens.CountAsync(x => x.UsedAtUtc == null)).Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("ok")]
    public async Task AReasonIsRequired(string? reason)
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);

        var result = await Controller(db, w, w.AdminId).AdminResetPassword(w.TargetId, new AdminResetPasswordRequest(Temporary, reason), default);

        result.Should().BeOfType<BadRequestObjectResult>();
        await AssertUntouchedAsync(db, w.TargetId);
    }

    [Fact]
    public async Task NeverYourOwnPassword()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);

        var result = await Controller(db, w, w.AdminId).AdminResetPassword(w.AdminId, new AdminResetPasswordRequest(Temporary, Reason), default);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task APasswordTheWorkspacePolicyRejects_IsRefused()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);

        var result = await Controller(db, w, w.AdminId).AdminResetPassword(w.TargetId, new AdminResetPasswordRequest("alllowercase-no-digits", Reason), default);

        result.Should().BeOfType<BadRequestObjectResult>();
        await AssertUntouchedAsync(db, w.TargetId);
    }

    [Theory]
    [InlineData("Suspended", true, AccessModes.FullPortal)]
    [InlineData("Deactivated", false, AccessModes.FullPortal)]
    [InlineData("Invited", true, AccessModes.FullPortal)]
    [InlineData("PendingPasswordSetup", true, AccessModes.FullPortal)]
    [InlineData("Active", false, AccessModes.FullPortal)]
    [InlineData("Active", true, AccessModes.NoLogin)]
    public async Task AnAccountThatCannotSignIn_IsRefused_AndNeverRevived(string status, bool isActive, string accessMode)
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);
        var target = await db.Users.SingleAsync(x => x.Id == w.TargetId);
        target.Status = status;
        target.IsActive = isActive;
        target.AccessMode = accessMode;
        await db.SaveChangesAsync();

        var result = await Controller(db, w, w.AdminId).AdminResetPassword(w.TargetId, new AdminResetPasswordRequest(Temporary, Reason), default);

        result.Should().BeOfType<BadRequestObjectResult>();
        var after = await db.Users.AsNoTracking().SingleAsync(x => x.Id == w.TargetId);
        after.Status.Should().Be(status);
        after.IsActive.Should().Be(isActive);
        after.MustChangePassword.Should().BeFalse();
    }

    [Fact]
    public async Task AUserOfAnotherWorkspace_IsNotFound()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);
        var other = await SeedAsync(db, slug: "other-co");

        var result = await Controller(db, w, w.AdminId).AdminResetPassword(other.TargetId, new AdminResetPasswordRequest(Temporary, Reason), default);

        result.Should().BeOfType<BadRequestObjectResult>();
        await AssertUntouchedAsync(db, other.TargetId);
    }

    [Fact]
    public async Task NobodySetsThePasswordOfSomeoneAboveThem()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);

        // The plain employee (no roles) tries it on the Admin.
        var result = await Controller(db, w, w.TargetId).AdminResetPassword(w.AdminId, new AdminResetPasswordRequest(Temporary, Reason), default);

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        await AssertUntouchedAsync(db, w.AdminId);
    }

    [Fact]
    public async Task AWrongPasswordLockout_EndsWithTheOldPassword_ButAnAdministratorsLockStays()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);
        var target = await db.Users.SingleAsync(x => x.Id == w.TargetId);
        target.FailedLoginCount = 5;
        target.IsLocked = true;
        target.LockoutEnd = DateTime.UtcNow.AddMinutes(15);
        await db.SaveChangesAsync();

        await Controller(db, w, w.AdminId).AdminResetPassword(w.TargetId, new AdminResetPasswordRequest(Temporary, Reason), default);

        var after = await db.Users.AsNoTracking().SingleAsync(x => x.Id == w.TargetId);
        after.IsLocked.Should().BeFalse();
        after.LockoutEnd.Should().BeNull();
        after.FailedLoginCount.Should().Be(0);

        // An administrator's deliberate lock (Status "Locked") is not lifted by a password change.
        var locked = await db.Users.SingleAsync(x => x.Id == w.TargetId);
        locked.Status = "Locked";
        locked.IsLocked = true;
        locked.LockoutEnd = DateTime.UtcNow.AddDays(1);
        await db.SaveChangesAsync();
        await Controller(db, w, w.AdminId).AdminResetPassword(w.TargetId, new AdminResetPasswordRequest("Second-Temp-Pass2!", Reason), default);
        var stillLocked = await db.Users.AsNoTracking().SingleAsync(x => x.Id == w.TargetId);
        stillLocked.Status.Should().Be("Locked");
        stillLocked.IsLocked.Should().BeTrue();
    }

    [Fact]
    public async Task AResetThatLandsWhileTheTemporaryPasswordIsBeingChecked_GetsNoTokenIssuedAfterIt()
    {
        var name = Guid.NewGuid().ToString();
        await using var db = CreateDb(name);
        var w = await SeedAsync(db);
        await Controller(db, w, w.AdminId).AdminResetPassword(w.TargetId, new AdminResetPasswordRequest(Temporary, Reason), default);

        // The owner's own reset commits between the temporary password being proven and the token being issued.
        var hasher = new RacingHasher(() =>
        {
            using var other = CreateDb(name);
            var owner = other.Users.Single(x => x.Id == w.TargetId);
            owner.PasswordHash = new Pbkdf2PasswordHasher().Hash("OwnersOwnChoice9!");
            owner.MustChangePassword = false;
            other.SaveChanges();
        });
        var auth = Auth(db, hasher);

        var signIn = () => auth.LoginAsync(new LoginRequest(TargetEmail, Temporary, w.Slug), Ctx, default);

        await signIn.Should().ThrowAsync<UnauthorizedAccessException>();
        (await db.PasswordResetTokens.AsNoTracking().CountAsync(x => x.UsedAtUtc == null)).Should().Be(0);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private const string TargetEmail = "temp.target@example.test";
    private static readonly RequestContext Ctx = new("127.0.0.1", "tests");

    private static readonly IOptions<JwtOptions> Jwt = Options.Create(new JwtOptions
    {
        Issuer = "Zayra.Tests",
        TenantAudience = "kynexone-tenant-test",
        PlatformAudience = "kynexone-platform-test",
        SigningKey = "TEST_SIGNING_KEY_WITH_MORE_THAN_64_CHARACTERS_FOR_ADMIN_SET_PASSWORD_TESTS",
        AccessTokenMinutes = 30,
        RefreshTokenDays = 7,
    });

    private static readonly IConfiguration Configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["APP_URL"] = "https://app.kynexone.test" })
        .Build();

    private sealed record World(Guid TenantId, string Slug, Guid AdminId, Guid TargetId);

    private static ZayraDbContext CreateDb(string? name = null) => new(
        new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(name ?? Guid.NewGuid().ToString())
            .Options);

    /// <summary>A real hasher that runs <paramref name="race"/> once, the first time a password is verified.</summary>
    private sealed class RacingHasher(Action race) : IPasswordHasher
    {
        private readonly Pbkdf2PasswordHasher _inner = new();
        private Action? _race = race;
        public string Hash(string password) => _inner.Hash(password);
        public bool NeedsRehash(string hash) => _inner.NeedsRehash(hash);
        public bool Verify(string password, string hash)
        {
            var ok = _inner.Verify(password, hash);
            Interlocked.Exchange(ref _race, null)?.Invoke();
            return ok;
        }
    }

    private static async Task<World> SeedAsync(ZayraDbContext db, string slug = "kynex-hq")
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = slug, Slug = slug, IsActive = true };
        var hasher = new Pbkdf2PasswordHasher();
        var target = new User
        {
            Id = Guid.NewGuid(), TenantId = tenant.Id, Tenant = tenant,
            Email = TargetEmail, NormalizedEmail = TargetEmail.ToUpperInvariant(), FullName = "Temp Target",
            PasswordHash = hasher.Hash("OriginalPassword1!"), Status = "Active", AccessMode = AccessModes.FullPortal, IsActive = true,
        };
        var adminEmail = $"admin@{slug}.test";
        var admin = new User
        {
            Id = Guid.NewGuid(), TenantId = tenant.Id, Tenant = tenant,
            Email = adminEmail, NormalizedEmail = adminEmail.ToUpperInvariant(), FullName = "Acting Admin",
            PasswordHash = hasher.Hash("AdminPassword1!"), Status = "Active", AccessMode = AccessModes.FullPortal, IsActive = true,
        };
        var adminRole = new Role { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Admin", NormalizedName = "ADMIN", Description = "Admin", IsActive = true };
        db.Tenants.Add(tenant);
        db.SecuritySettings.Add(new SecuritySetting { Id = Guid.NewGuid(), TenantId = tenant.Id });
        db.Users.AddRange(target, admin);
        db.Roles.Add(adminRole);
        db.UserRoles.Add(new UserRole { UserId = admin.Id, RoleId = adminRole.Id });
        await db.SaveChangesAsync();
        return new World(tenant.Id, slug, admin.Id, target.Id);
    }

    private static AccessController Controller(ZayraDbContext db, World w, Guid callerId) =>
        new(new AccessManagementService(db, new Pbkdf2PasswordHasher(), new AuditService(db), new JwtTokenService(Jwt), Configuration),
            db,
            new NoEmail(),
            Auth(db))
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

    private static AuthService Auth(ZayraDbContext db, IPasswordHasher? hasher = null)
    {
        var tokenService = new JwtTokenService(Jwt);
        var audit = new AuditService(db);
        var totp = new TotpService(DataProtectionProvider.Create("ZayraAdminSetPasswordTests"));
        return new AuthService(db, hasher ?? new Pbkdf2PasswordHasher(), tokenService, audit, new NoEmail(), Jwt,
            new MfaService(db, totp, tokenService, audit), totp, NullLogger<AuthService>.Instance, Configuration);
    }

    private static async Task AssertUntouchedAsync(ZayraDbContext db, Guid userId)
    {
        var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == userId);
        user.MustChangePassword.Should().BeFalse();
        new Pbkdf2PasswordHasher().Verify(Temporary, user.PasswordHash).Should().BeFalse();
    }

    private static JsonElement Body(ObjectResult result) =>
        JsonDocument.Parse(JsonSerializer.Serialize(result.Value)).RootElement;

    private sealed class NoEmail : IEmailService
    {
        public Task SendAsync(string toAddress, string toName, string subject, string htmlBody,
            IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
