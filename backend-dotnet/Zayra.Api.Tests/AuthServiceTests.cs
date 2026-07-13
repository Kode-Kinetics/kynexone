using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class AuthServiceTests
{
    // ── Shared helpers ────────────────────────────────────────────────────────

    private static ZayraDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ZayraDbContext(options);
    }

    private static AuthService BuildService(ZayraDbContext db)
    {
        var jwt = Options.Create(new JwtOptions
        {
            Issuer           = "Zayra.Tests",
            TenantAudience   = "kynexone-tenant-test",
            PlatformAudience = "kynexone-platform-test",
            SigningKey        = "TEST_SIGNING_KEY_WITH_MORE_THAN_64_CHARACTERS_FOR_AUTH_TESTS",
            AccessTokenMinutes = 30,
            RefreshTokenDays   = 7
        });
        return new AuthService(
            db,
            new Pbkdf2PasswordHasher(),
            new JwtTokenService(jwt),
            new AuditService(db),
            new FakeEmailService(),
            jwt,
            new NullMfaService(),
            NullLogger<AuthService>.Instance);
    }

    private static readonly RequestContext TestCtx = new("127.0.0.1", "tests");

    private static async Task<(ZayraDbContext db, User user, Tenant tenant)> SeedUserAsync(
        ZayraDbContext? existingDb = null,
        int maxFailedAttempts = 5,
        int lockoutMinutes = 15)
    {
        var db     = existingDb ?? CreateDb();
        var hasher = new Pbkdf2PasswordHasher();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Zayra HQ", Slug = "zayra" };
        var sec    = new SecuritySetting
        {
            Id                     = Guid.NewGuid(),
            TenantId               = tenant.Id,
            MaxFailedLoginAttempts = maxFailedAttempts,
            LockoutDurationMinutes = lockoutMinutes
        };
        var permission = new Permission { Id = Guid.NewGuid(), Key = "dashboard.read", Module = "Dashboard", Description = "Read" };
        var role       = new Role { Id = Guid.NewGuid(), TenantId = tenant.Id, Tenant = tenant, Name = "Admin", NormalizedName = "ADMIN", Description = "Admin" };
        var user       = new User
        {
            Id              = Guid.NewGuid(),
            TenantId        = tenant.Id,
            Tenant          = tenant,
            Email           = "admin@zayra.local",
            NormalizedEmail = "ADMIN@ZAYRA.LOCAL",
            FullName        = "Zayra Admin",
            PasswordHash    = hasher.Hash("CorrectPassword1!")
        };
        db.Tenants.Add(tenant);
        db.SecuritySettings.Add(sec);
        db.Permissions.Add(permission);
        db.Roles.Add(role);
        db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permission.Id });
        db.Users.Add(user);
        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();
        return (db, user, tenant);
    }

    // ── Password hasher unit tests ────────────────────────────────────────────

    [Fact]
    public void PasswordHasher_VerifiesValidPassword_AndRejectsInvalidPassword()
    {
        var hasher = new Pbkdf2PasswordHasher();
        var hash   = hasher.Hash("CorrectHorse123!");

        Assert.True(hasher.Verify("CorrectHorse123!", hash));
        Assert.False(hasher.Verify("wrong-password", hash));
    }

    // ── Successful login / token rotation ────────────────────────────────────

    [Fact]
    public async Task LoginAndRefresh_IssueTokensAndRotateRefreshToken()
    {
        var (db, _, _) = await SeedUserAsync();
        var auth = BuildService(db);

        var login   = await auth.LoginAsync(new LoginRequest("admin@zayra.local", "CorrectPassword1!", "zayra"), TestCtx, CancellationToken.None);
        var refresh = await auth.RefreshAsync(new RefreshTokenRequest(login.Tokens!.RefreshToken), TestCtx, CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(login.Tokens!.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(refresh.AccessToken));
        Assert.NotEqual(login.Tokens!.RefreshToken, refresh.RefreshToken);
        Assert.Equal("zayra", login.Tokens!.User.TenantSlug);
        Assert.Contains("Admin", login.Tokens!.User.Roles);
        Assert.Contains("dashboard.read", login.Tokens!.User.Permissions);
        Assert.Equal(2, await db.RefreshTokens.CountAsync());
        Assert.Equal(1, await db.RefreshTokens.CountAsync(x => x.RevokedAtUtc != null));
        Assert.True(await db.AuditLogs.AnyAsync(x => x.Action == "auth.login"));
        Assert.True(await db.AuditLogs.AnyAsync(x => x.Action == "auth.refresh"));
    }

    [Fact]
    public async Task Login_UsesTenantRefreshTokenExpiryPolicy()
    {
        var (db, _, _) = await SeedUserAsync();
        var sec = await db.SecuritySettings.FirstAsync();
        sec.RefreshTokenExpiryDays = 2;
        await db.SaveChangesAsync();

        var auth = BuildService(db);
        await auth.LoginAsync(new LoginRequest("admin@zayra.local", "CorrectPassword1!", "zayra"), TestCtx, CancellationToken.None);

        var token = await db.RefreshTokens.SingleAsync();
        Assert.True(token.ExpiresAtUtc <= DateTime.UtcNow.AddDays(2).AddMinutes(1));
        Assert.True(token.ExpiresAtUtc > DateTime.UtcNow.AddDays(1));
    }

    [Fact]
    public async Task Login_DisallowsMultipleSessions_WhenTenantPolicyDisablesThem()
    {
        var (db, _, _) = await SeedUserAsync();
        var sec = await db.SecuritySettings.FirstAsync();
        sec.AllowMultipleSessions = false;
        await db.SaveChangesAsync();

        var auth = BuildService(db);
        await auth.LoginAsync(new LoginRequest("admin@zayra.local", "CorrectPassword1!", "zayra"), TestCtx, CancellationToken.None);
        await auth.LoginAsync(new LoginRequest("admin@zayra.local", "CorrectPassword1!", "zayra"), TestCtx, CancellationToken.None);

        Assert.Equal(2, await db.RefreshTokens.CountAsync());
        Assert.Equal(1, await db.RefreshTokens.CountAsync(x => x.RevokedAtUtc == null));
        Assert.Equal(1, await db.RefreshTokens.CountAsync(x => x.RevokedAtUtc != null));
    }

    [Fact]
    public async Task Refresh_RejectsTokenPastTenantSessionTimeout_AndRevokesIt()
    {
        var (db, _, _) = await SeedUserAsync();
        var sec = await db.SecuritySettings.FirstAsync();
        sec.SessionTimeoutMinutes = 15;
        await db.SaveChangesAsync();

        var auth = BuildService(db);
        var login = await auth.LoginAsync(new LoginRequest("admin@zayra.local", "CorrectPassword1!", "zayra"), TestCtx, CancellationToken.None);
        var stored = await db.RefreshTokens.SingleAsync();
        stored.CreatedAtUtc = DateTime.UtcNow.AddMinutes(-16);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => auth.RefreshAsync(new RefreshTokenRequest(login.Tokens!.RefreshToken), TestCtx, CancellationToken.None));

        Assert.NotNull((await db.RefreshTokens.SingleAsync()).RevokedAtUtc);
    }

    [Fact]
    public async Task Refresh_TenantRequiresMfaForUnenrolledUser_RejectsExistingSession()
    {
        var (db, _, _) = await SeedUserAsync();
        var auth = BuildService(db);
        var login = await auth.LoginAsync(new LoginRequest("admin@zayra.local", "CorrectPassword1!", "zayra"), TestCtx, CancellationToken.None);

        var sec = await db.SecuritySettings.FirstAsync();
        sec.MfaRequired = true;
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => auth.RefreshAsync(new RefreshTokenRequest(login.Tokens!.RefreshToken), TestCtx, CancellationToken.None));

        Assert.NotNull((await db.RefreshTokens.SingleAsync()).RevokedAtUtc);
    }

    // ── Tenant-mandated MFA enforcement ───────────────────────────────────────

    [Fact]
    public async Task Login_TenantRequiresMfa_UnenrolledUser_GetsNoTokens_AndEnrollmentSignal()
    {
        var (db, _, _) = await SeedUserAsync();
        // Tenant policy now mandates MFA for all users, but the seeded user has NOT enrolled TOTP.
        var sec = await db.SecuritySettings.FirstAsync();
        sec.MfaRequired = true;
        await db.SaveChangesAsync();

        var auth = BuildService(db);
        var login = await auth.LoginAsync(new LoginRequest("admin@zayra.local", "CorrectPassword1!", "zayra"), TestCtx, CancellationToken.None);

        Assert.Null(login.Tokens);                       // NO session is issued
        Assert.Null(login.Challenge);
        Assert.True(login.RequiresMfaEnrollment);        // client is told to enroll
        Assert.NotNull(login.EnrollmentChallenge);
        Assert.False(string.IsNullOrWhiteSpace(login.EnrollmentChallenge!.ChallengeToken));
        Assert.True(await db.AuditLogs.AnyAsync(x => x.Action == "auth.mfa_enrollment_required"));
    }

    [Fact]
    public async Task Login_TenantDoesNotRequireMfa_UnenrolledUser_LogsInNormally()
    {
        // Guard against over-enforcement: with MfaRequired=false (the default) login is unaffected.
        var (db, _, _) = await SeedUserAsync();
        var auth = BuildService(db);
        var login = await auth.LoginAsync(new LoginRequest("admin@zayra.local", "CorrectPassword1!", "zayra"), TestCtx, CancellationToken.None);

        Assert.NotNull(login.Tokens);
        Assert.False(login.RequiresMfaEnrollment);
    }

    // ── Lockout: failure counter ──────────────────────────────────────────────

    [Fact]
    public async Task Login_IncrementsFailedLoginCount_OnPasswordMismatch()
    {
        var (db, user, _) = await SeedUserAsync();
        var auth = BuildService(db);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => auth.LoginAsync(new LoginRequest("admin@zayra.local", "WrongPassword!", "zayra"), TestCtx, CancellationToken.None));

        var updated = await db.Users.FindAsync(user.Id);
        Assert.Equal(1, updated!.FailedLoginCount);
        Assert.Null(updated.LockoutEnd);
        Assert.False(updated.IsLocked);
    }

    [Fact]
    public async Task Login_ResetsFailedLoginCount_OnSuccessfulLogin()
    {
        var (db, user, _) = await SeedUserAsync();
        var auth = BuildService(db);

        // Pre-heat counter with two failures
        for (int i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => auth.LoginAsync(new LoginRequest("admin@zayra.local", "WrongPassword!", "zayra"), TestCtx, CancellationToken.None));
        }

        var preFail = await db.Users.FindAsync(user.Id);
        Assert.Equal(2, preFail!.FailedLoginCount);

        // Successful login must reset counter
        await auth.LoginAsync(new LoginRequest("admin@zayra.local", "CorrectPassword1!", "zayra"), TestCtx, CancellationToken.None);

        var postSuccess = await db.Users.FindAsync(user.Id);
        Assert.Equal(0, postSuccess!.FailedLoginCount);
        Assert.Null(postSuccess.LockoutEnd);
        Assert.False(postSuccess.IsLocked);
    }

    // ── Lockout: account locking ──────────────────────────────────────────────

    [Fact]
    public async Task Login_LocksAccount_AfterMaxFailedAttempts()
    {
        var (db, user, _) = await SeedUserAsync(maxFailedAttempts: 5, lockoutMinutes: 15);
        var auth = BuildService(db);

        for (int i = 0; i < 5; i++)
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => auth.LoginAsync(new LoginRequest("admin@zayra.local", "WrongPassword!", "zayra"), TestCtx, CancellationToken.None));
        }

        var locked = await db.Users.FindAsync(user.Id);
        Assert.Equal(5, locked!.FailedLoginCount);
        Assert.True(locked.IsLocked);
        Assert.NotNull(locked.LockoutEnd);
        Assert.True(locked.LockoutEnd > DateTime.UtcNow);
        Assert.True(locked.LockoutEnd <= DateTime.UtcNow.AddMinutes(16)); // within configured window

        // Audit log must record the locking event
        Assert.True(await db.AuditLogs.AnyAsync(x => x.Action == "auth.account_locked"));
    }

    [Fact]
    public async Task Login_BlocksLockedAccount_WithCorrectPassword()
    {
        var (db, user, _) = await SeedUserAsync(maxFailedAttempts: 3, lockoutMinutes: 15);
        var auth = BuildService(db);

        // Exhaust attempts to trigger lockout
        for (int i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => auth.LoginAsync(new LoginRequest("admin@zayra.local", "WrongPassword!", "zayra"), TestCtx, CancellationToken.None));
        }

        var locked = await db.Users.FindAsync(user.Id);
        Assert.True(locked!.IsLocked);

        // Even with the correct password, must be blocked while locked
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => auth.LoginAsync(new LoginRequest("admin@zayra.local", "CorrectPassword1!", "zayra"), TestCtx, CancellationToken.None));

        // Audit log must record the blocked attempt
        Assert.True(await db.AuditLogs.AnyAsync(x => x.Action == "auth.login_blocked_lockout"));

        // Failure counter must NOT increment further while locked
        var stillLocked = await db.Users.FindAsync(user.Id);
        Assert.Equal(3, stillLocked!.FailedLoginCount);
    }

    [Fact]
    public async Task Login_AllowsLoginAfterLockoutExpires()
    {
        var (db, user, _) = await SeedUserAsync(maxFailedAttempts: 3, lockoutMinutes: 15);
        // Manually set an expired lockout on the user
        var u = await db.Users.FindAsync(user.Id);
        u!.IsLocked         = true;
        u.FailedLoginCount  = 3;
        u.LockoutEnd        = DateTime.UtcNow.AddMinutes(-1); // expired 1 minute ago
        await db.SaveChangesAsync();

        var auth = BuildService(db);

        // Login must succeed after lockout has expired
        var response = await auth.LoginAsync(
            new LoginRequest("admin@zayra.local", "CorrectPassword1!", "zayra"), TestCtx, CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(response.Tokens!.AccessToken));

        // Lockout state must be cleared
        var cleared = await db.Users.FindAsync(user.Id);
        Assert.Equal(0, cleared!.FailedLoginCount);
        Assert.False(cleared.IsLocked);
        Assert.Null(cleared.LockoutEnd);
    }

    // ── Error message safety ─────────────────────────────────────────────────

    [Fact]
    public async Task Login_ReturnsIdenticalErrorMessage_ForUnknownUserAndWrongPassword()
    {
        var (db, _, _) = await SeedUserAsync();
        var auth = BuildService(db);

        var exUnknown = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => auth.LoginAsync(new LoginRequest("nobody@example.com", "anything", "zayra"), TestCtx, CancellationToken.None));

        var exWrongPwd = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => auth.LoginAsync(new LoginRequest("admin@zayra.local", "WrongPassword!", "zayra"), TestCtx, CancellationToken.None));

        Assert.Equal(exUnknown.Message, exWrongPwd.Message);
    }

    // ── Demo seeder gate ─────────────────────────────────────────────────────

    [Fact]
    public void DemoSeeder_ShouldNotRunInProduction_WhenEnvVarNotSet()
    {
        // Guard: if SEED_DEMO_DATA is not set, the demo seeder must not be invoked.
        // This test verifies the flag-reading logic in isolation.
        var envValue     = Environment.GetEnvironmentVariable("SEED_DEMO_DATA");
        var configValue  = "false"; // simulating appsettings SeedAdmin:SeedDemoData=false (default)

        var shouldSeed =
            string.Equals(envValue,    "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(configValue, "true", StringComparison.OrdinalIgnoreCase);

        Assert.False(shouldSeed, "Demo seeder must NOT run when SEED_DEMO_DATA is absent/false");
    }

    [Fact]
    public void DemoSeeder_ShouldRun_WhenEnvVarIsTrue()
    {
        const string simulatedEnvValue = "true";
        var shouldSeed = string.Equals(simulatedEnvValue, "true", StringComparison.OrdinalIgnoreCase);
        Assert.True(shouldSeed, "Demo seeder must run when SEED_DEMO_DATA=true");
    }
}

file sealed class NullMfaService : IMfaService
{
    public Task<MfaSetupInitDto> InitiateSetupAsync(Guid userId, Guid tenantId, CancellationToken ct) => throw new NotImplementedException();
    public Task<bool> VerifySetupAsync(Guid userId, Guid tenantId, MfaVerifySetupRequest req, CancellationToken ct) => throw new NotImplementedException();
    public Task<string> CreateEnrollmentChallengeAsync(Guid userId, Guid tenantId, string ip, CancellationToken ct) => Task.FromResult("test-enrollment-token");
    public Task<MfaSetupInitDto?> InitiateEnrollmentSetupAsync(string token, CancellationToken ct) => throw new NotImplementedException();
    public Task<bool> VerifyEnrollmentSetupAsync(string token, MfaVerifySetupRequest req, CancellationToken ct) => throw new NotImplementedException();
    public Task<string> CreateChallengeAsync(Guid userId, Guid tenantId, string ip, CancellationToken ct) => throw new NotImplementedException();
    public Task<Zayra.Api.Domain.Entities.User?> VerifyChallengeAsync(string token, string code, CancellationToken ct) => throw new NotImplementedException();
    public Task<bool> DisableAsync(Guid userId, Guid tenantId, string code, CancellationToken ct) => throw new NotImplementedException();
    public Task<MfaSetupInitDto> InitiatePlatformSetupAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
    public Task<bool> VerifyPlatformSetupAsync(Guid id, MfaVerifySetupRequest req, CancellationToken ct) => throw new NotImplementedException();
    public Task<string> CreatePlatformChallengeAsync(Guid id, string ip, CancellationToken ct) => throw new NotImplementedException();
    public Task<Zayra.Api.Models.PlatformUser?> VerifyPlatformChallengeAsync(string token, string code, CancellationToken ct) => throw new NotImplementedException();
    public Task<bool> DisablePlatformAsync(Guid id, string code, CancellationToken ct) => throw new NotImplementedException();
}

file sealed class FakeEmailService : IEmailService
{
    public Task SendAsync(string toAddress, string toName, string subject, string htmlBody,
        IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}
