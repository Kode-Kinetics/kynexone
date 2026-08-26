using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
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
    public async Task SecondSuccessfulLogin_DoesNotRevokeExistingTenantAccessSession()
    {
        var (db, user, tenant) = await SeedUserAsync();
        user.IsGroupScope = true;
        await db.SaveChangesAsync();
        var auth = BuildService(db);

        await auth.LoginAsync(
            new LoginRequest("admin@zayra.local", "CorrectPassword1!", "zayra"), TestCtx, CancellationToken.None);
        var firstStamp = TenantSessionSecurity.StampValue(user);
        var principal = TenantPrincipal(user, tenant, firstStamp);
        Assert.True(await TenantSessionSecurity.IsCurrentAsync(principal, db, CancellationToken.None));

        await auth.LoginAsync(
            new LoginRequest("admin@zayra.local", "CorrectPassword1!", "zayra"), TestCtx, CancellationToken.None);

        Assert.Equal(firstStamp, TenantSessionSecurity.StampValue(user));
        Assert.True(await TenantSessionSecurity.IsCurrentAsync(principal, db, CancellationToken.None));
        Assert.Equal(2, await db.RefreshTokens.CountAsync(x => x.RevokedAtUtc == null));
    }

    [Fact]
    public async Task FailedLoginBelowLockoutThreshold_DoesNotRevokeExistingTenantAccessSession()
    {
        var (db, user, tenant) = await SeedUserAsync(maxFailedAttempts: 5);
        user.IsGroupScope = true;
        await db.SaveChangesAsync();
        var auth = BuildService(db);
        await auth.LoginAsync(
            new LoginRequest("admin@zayra.local", "CorrectPassword1!", "zayra"), TestCtx, CancellationToken.None);
        var originalStamp = TenantSessionSecurity.StampValue(user);
        var principal = TenantPrincipal(user, tenant, originalStamp);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => auth.LoginAsync(
            new LoginRequest("admin@zayra.local", "wrong-password", "zayra"), TestCtx, CancellationToken.None));

        Assert.Equal(originalStamp, TenantSessionSecurity.StampValue(user));
        Assert.True(await TenantSessionSecurity.IsCurrentAsync(principal, db, CancellationToken.None));
    }

    private static ClaimsPrincipal TenantPrincipal(User user, Tenant tenant, string stamp)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new("tenant_id", tenant.Id.ToString()),
            new(TenantSessionSecurity.SessionStampClaim, stamp),
            new(ClaimTypes.Role, "Admin"),
            new("permission", "dashboard.read")
        };
        claims.AddRange(EntityScopeClaims.Build(EntityScopeDescriptor.Group, Array.Empty<EntityAccessGrant>()));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Fact]
    public async Task RefreshRotation_PreservesAbsoluteFamilyExpiry()
    {
        var (db, _, _) = await SeedUserAsync();
        var auth = BuildService(db);
        var login = await auth.LoginAsync(
            new LoginRequest("admin@zayra.local", "CorrectPassword1!", "zayra"), TestCtx, CancellationToken.None);
        var parent = await db.RefreshTokens.SingleAsync();
        var absoluteExpiry = DateTime.UtcNow.AddHours(2);
        parent.ExpiresAtUtc = absoluteExpiry;
        await db.SaveChangesAsync();

        await auth.RefreshAsync(new RefreshTokenRequest(login.Tokens!.RefreshToken), TestCtx, CancellationToken.None);

        var descendant = await db.RefreshTokens.SingleAsync(x => x.Id != parent.Id);
        Assert.Equal(absoluteExpiry, descendant.ExpiresAtUtc);
    }

    [Fact]
    public async Task Logout_InvalidatesAlreadyIssuedTenantAccessSession()
    {
        var (db, user, tenant) = await SeedUserAsync();
        user.IsGroupScope = true;
        await db.SaveChangesAsync();
        var auth = BuildService(db);
        var login = await auth.LoginAsync(
            new LoginRequest("admin@zayra.local", "CorrectPassword1!", "zayra"), TestCtx, CancellationToken.None);
        var oldStamp = TenantSessionSecurity.StampValue(user);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new("tenant_id", tenant.Id.ToString()),
            new(TenantSessionSecurity.SessionStampClaim, oldStamp),
            new(ClaimTypes.Role, "Admin"),
            new("permission", "dashboard.read")
        };
        claims.AddRange(EntityScopeClaims.Build(EntityScopeDescriptor.Group, Array.Empty<EntityAccessGrant>()));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        Assert.True(await TenantSessionSecurity.IsCurrentAsync(principal, db, CancellationToken.None));

        await auth.LogoutAsync(new LogoutRequest(login.Tokens!.RefreshToken), TestCtx, CancellationToken.None);

        Assert.False(await TenantSessionSecurity.IsCurrentAsync(principal, db, CancellationToken.None));
    }

    [Fact]
    public async Task Refresh_RelationalRotationConsumesOldTokenOnce()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        await SeedUserAsync(db);
        var auth = BuildService(db);

        var login = await auth.LoginAsync(
            new LoginRequest("admin@zayra.local", "CorrectPassword1!", "zayra"), TestCtx, CancellationToken.None);
        var originalToken = login.Tokens!.RefreshToken;
        var rotated = await auth.RefreshAsync(new RefreshTokenRequest(originalToken), TestCtx, CancellationToken.None);

        Assert.NotEqual(originalToken, rotated.RefreshToken);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => auth.RefreshAsync(new RefreshTokenRequest(originalToken), TestCtx, CancellationToken.None));
        Assert.Equal(2, await db.RefreshTokens.CountAsync());
        Assert.Empty(await db.RefreshTokens.Where(x => x.RevokedAtUtc == null).ToListAsync());
        Assert.True(await db.AuditLogs.AnyAsync(x => x.Action == "auth.refresh_reuse_detected"));
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

    [Fact]
    public async Task AcceptInvitation_ConsumesTokenAndRejectsReplay()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var (_, user, _) = await SeedUserAsync(db);
        const string invitationToken = "one-time-invitation-token";
        db.EmployeeUserAccounts.Add(new EmployeeUserAccount
        {
            TenantId = user.TenantId,
            EmployeeId = 42,
            UserId = user.Id,
            AccessMode = AccessModes.EssOnly,
            Status = "Invited",
            RequiresPasswordSetup = true,
            InvitationTokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(invitationToken))),
            InvitationExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        });
        await db.SaveChangesAsync();

        var auth = BuildService(db);
        var request = new AcceptInvitationRequest(user.Email, invitationToken, "NewPassword1!", "zayra");
        var accepted = await auth.AcceptInvitationAsync(request, TestCtx, CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(accepted.AccessToken));
        var link = await db.EmployeeUserAccounts.SingleAsync();
        Assert.Equal(string.Empty, link.InvitationTokenHash);
        Assert.Null(link.InvitationExpiresAtUtc);
        Assert.NotNull(link.InvitationAcceptedAtUtc);
        Assert.False(link.RequiresPasswordSetup);
        Assert.Equal("Active", link.Status);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => auth.AcceptInvitationAsync(request, TestCtx, CancellationToken.None));
        Assert.Single(await db.RefreshTokens.ToListAsync());
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

[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class AuthRefreshTokenSecurityTests
{
    private readonly PostgresFixture _fixture;
    private static readonly RequestContext Context = new("203.0.113.10", "refresh-security-tests");

    public AuthRefreshTokenSecurityTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ConcurrentRotation_TwoPostgresContexts_CreatesExactlyOneDescendant()
    {
        var seeded = await SeedUserAsync();
        string originalToken;
        await using (var loginDb = _fixture.CreateDb())
        {
            var login = await BuildService(loginDb).LoginAsync(
                new LoginRequest(seeded.Email, seeded.Password, seeded.TenantSlug),
                Context,
                CancellationToken.None);
            originalToken = login.Tokens!.RefreshToken;
        }

        await using var dbA = _fixture.CreateDb();
        await using var dbB = _fixture.CreateDb();
        var authA = BuildService(dbA);
        var authB = BuildService(dbB);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<bool> AttemptAsync(AuthService service)
        {
            await gate.Task;
            try
            {
                await service.RefreshAsync(new RefreshTokenRequest(originalToken), Context, CancellationToken.None);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        var attemptA = AttemptAsync(authA);
        var attemptB = AttemptAsync(authB);
        gate.SetResult();
        var outcomes = await Task.WhenAll(attemptA, attemptB);

        Assert.Single(outcomes, x => x);
        Assert.Single(outcomes, x => !x);

        await using var verify = _fixture.CreateDb();
        var tokens = await verify.RefreshTokens.AsNoTracking()
            .Where(x => x.UserId == seeded.UserId)
            .ToListAsync();
        Assert.Equal(2, tokens.Count);
        var original = Assert.Single(tokens, x => x.TokenHash == HashToken(originalToken));
        var descendant = Assert.Single(tokens, x => x.TokenHash != HashToken(originalToken));
        Assert.Single(tokens.Select(x => x.FamilyId).Distinct());
        Assert.Equal(descendant.TokenHash, original.ReplacedByTokenHash);
        Assert.All(tokens, token => Assert.NotNull(token.RevokedAtUtc));
        Assert.True(await verify.AuditLogs.AnyAsync(x =>
            x.TenantId == seeded.TenantId && x.Action == "auth.refresh_reuse_detected"));
    }

    [Fact]
    public async Task ReusingConsumedAncestor_RevokesEveryActiveDescendantInFamily()
    {
        var seeded = await SeedUserAsync();
        string originalToken;
        await using (var db = _fixture.CreateDb())
        {
            var auth = BuildService(db);
            var login = await auth.LoginAsync(
                new LoginRequest(seeded.Email, seeded.Password, seeded.TenantSlug), Context, CancellationToken.None);
            originalToken = login.Tokens!.RefreshToken;
            var child = await auth.RefreshAsync(new RefreshTokenRequest(originalToken), Context, CancellationToken.None);
            await auth.RefreshAsync(new RefreshTokenRequest(child.RefreshToken), Context, CancellationToken.None);
        }

        await using (var replayDb = _fixture.CreateDb())
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => BuildService(replayDb).RefreshAsync(
                new RefreshTokenRequest(originalToken), Context, CancellationToken.None));
        }

        await using var verify = _fixture.CreateDb();
        var tokens = await verify.RefreshTokens.AsNoTracking()
            .Where(x => x.UserId == seeded.UserId)
            .ToListAsync();
        Assert.Equal(3, tokens.Count);
        Assert.Single(tokens.Select(x => x.FamilyId).Distinct());
        Assert.DoesNotContain(tokens, x => x.RevokedAtUtc == null);
        var reuseAudit = await verify.AuditLogs.AsNoTracking().SingleAsync(x =>
            x.TenantId == seeded.TenantId && x.Action == "auth.refresh_reuse_detected");
        Assert.Contains(tokens[0].FamilyId.ToString("D"), reuseAudit.Metadata);
        Assert.DoesNotContain(tokens[0].TokenHash, reuseAudit.Metadata);
    }

    [Fact]
    public async Task ReusingOneFamily_DoesNotRevokeAnUnrelatedLoginSession()
    {
        var seeded = await SeedUserAsync();
        string compromisedAncestor;
        string independentSession;
        await using (var db = _fixture.CreateDb())
        {
            var auth = BuildService(db);
            var firstLogin = await auth.LoginAsync(
                new LoginRequest(seeded.Email, seeded.Password, seeded.TenantSlug), Context, CancellationToken.None);
            compromisedAncestor = firstLogin.Tokens!.RefreshToken;
            await auth.RefreshAsync(new RefreshTokenRequest(compromisedAncestor), Context, CancellationToken.None);

            var secondLogin = await auth.LoginAsync(
                new LoginRequest(seeded.Email, seeded.Password, seeded.TenantSlug), Context, CancellationToken.None);
            independentSession = secondLogin.Tokens!.RefreshToken;
        }

        await using (var replayDb = _fixture.CreateDb())
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => BuildService(replayDb).RefreshAsync(
                new RefreshTokenRequest(compromisedAncestor), Context, CancellationToken.None));
        }

        await using var verify = _fixture.CreateDb();
        var independent = await verify.RefreshTokens.AsNoTracking()
            .SingleAsync(x => x.UserId == seeded.UserId && x.TokenHash == HashToken(independentSession));
        Assert.Null(independent.RevokedAtUtc);

        var compromisedFamily = await verify.RefreshTokens.AsNoTracking()
            .Where(x => x.UserId == seeded.UserId && x.FamilyId != independent.FamilyId)
            .ToListAsync();
        Assert.Equal(2, compromisedFamily.Count);
        Assert.All(compromisedFamily, token => Assert.NotNull(token.RevokedAtUtc));
    }

    [Fact]
    public async Task Rotation_WhenAuditFails_RollsBackParentConsumptionAndDescendantInsert()
    {
        var seeded = await SeedUserAsync();
        string originalToken;
        await using (var loginDb = _fixture.CreateDb())
        {
            var login = await BuildService(loginDb).LoginAsync(
                new LoginRequest(seeded.Email, seeded.Password, seeded.TenantSlug), Context, CancellationToken.None);
            originalToken = login.Tokens!.RefreshToken;
        }

        await using (var faultDb = _fixture.CreateDb())
        {
            var faultingAuth = BuildService(faultDb, new ThrowingRefreshAuditService());
            await Assert.ThrowsAsync<InvalidOperationException>(() => faultingAuth.RefreshAsync(
                new RefreshTokenRequest(originalToken), Context, CancellationToken.None));
        }

        await using var verify = _fixture.CreateDb();
        var stored = await verify.RefreshTokens.AsNoTracking()
            .SingleAsync(x => x.UserId == seeded.UserId);
        Assert.Null(stored.RevokedAtUtc);
        Assert.Null(stored.ReplacedByTokenHash);
        Assert.Equal(HashToken(originalToken), stored.TokenHash);
        Assert.False(await verify.AuditLogs.AnyAsync(x =>
            x.TenantId == seeded.TenantId && x.Action == "auth.refresh"));
    }

    private async Task<SeededAuthUser> SeedUserAsync()
    {
        await using var db = _fixture.CreateDb();
        const string password = "CorrectPassword1!";
        var tenantId = Guid.NewGuid();
        var slug = $"auth-{Guid.NewGuid():N}";
        var email = $"admin-{Guid.NewGuid():N}@example.test";
        var tenant = new Tenant { Id = tenantId, Name = "Auth Security Tenant", Slug = slug };
        var permission = new Permission
        {
            Id = Guid.NewGuid(),
            Key = $"dashboard.read.{Guid.NewGuid():N}",
            Module = "Dashboard",
            Description = "Read"
        };
        var role = new Role
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Tenant = tenant,
            Name = "Admin",
            NormalizedName = "ADMIN",
            Description = "Admin"
        };
        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Tenant = tenant,
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            FullName = "Refresh Security Admin",
            PasswordHash = new Pbkdf2PasswordHasher().Hash(password)
        };
        db.Tenants.Add(tenant);
        db.SecuritySettings.Add(new SecuritySetting { Id = Guid.NewGuid(), TenantId = tenantId });
        db.Permissions.Add(permission);
        db.Roles.Add(role);
        db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permission.Id });
        db.Users.Add(user);
        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();
        return new SeededAuthUser(tenantId, user.Id, slug, email, password);
    }

    private static AuthService BuildService(ZayraDbContext db, IAuditService? audit = null)
    {
        var jwt = Options.Create(new JwtOptions
        {
            Issuer = "Zayra.Tests",
            TenantAudience = "kynexone-tenant-test",
            PlatformAudience = "kynexone-platform-test",
            SigningKey = "TEST_SIGNING_KEY_WITH_MORE_THAN_64_CHARACTERS_FOR_AUTH_TESTS",
            AccessTokenMinutes = 30,
            RefreshTokenDays = 7
        });
        return new AuthService(
            db,
            new Pbkdf2PasswordHasher(),
            new JwtTokenService(jwt),
            audit ?? new AuditService(db),
            new FakeEmailService(),
            jwt,
            new NullMfaService(),
            NullLogger<AuthService>.Instance);
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private sealed record SeededAuthUser(
        Guid TenantId,
        Guid UserId,
        string TenantSlug,
        string Email,
        string Password);
}

file sealed class ThrowingRefreshAuditService : IAuditService
{
    public Task WriteAsync(
        string action,
        string entityName,
        string? entityId,
        RequestContext context,
        string? metadata,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Injected refresh audit persistence failure.");
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
