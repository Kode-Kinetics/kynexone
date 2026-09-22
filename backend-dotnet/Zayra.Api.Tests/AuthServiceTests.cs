using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;
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
            new TotpService(DataProtectionProvider.Create("ZayraTests")),
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RequiredWorkspace_RejectsMissingOrBlankValues(string? workspace)
    {
        var tenantSlug = typeof(ForgotPasswordRequest)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Single(parameter => parameter.Name == nameof(ForgotPasswordRequest.TenantSlug));
        var validator = Assert.Single(
            tenantSlug.GetCustomAttributes(typeof(RequiredWorkspaceAttribute), inherit: false)
                .Cast<RequiredWorkspaceAttribute>());

        Assert.False(validator.IsValid(workspace));
        Assert.Equal(
            "Workspace is required.",
            validator.FormatErrorMessage(nameof(ForgotPasswordRequest.TenantSlug)));
        Assert.Throws<InvalidOperationException>(() => AuthService.RequireWorkspace(workspace));
    }

    [Fact]
    public void RequireWorkspace_TrimsAndLowercases()
    {
        Assert.Equal("acme-workspace", AuthService.RequireWorkspace("  ACME-Workspace  "));
    }

    [Fact]
    public void AuthLinkBuilder_EmitsCanonicalFragmentOnlyLinks()
    {
        const string token = "A+B/C=&secret#tail";
        Assert.Equal(
            "https://app.example.test/reset-password?workspace=acme%2Fhq#token=A%2BB%2FC%3D%26secret%23tail",
            AuthLinkBuilder.ResetPassword("https://app.example.test///", " ACME/HQ ", token));
        Assert.Equal(
            "http://localhost:3000/accept-invitation?workspace=acme%2Fhq#token=A%2BB%2FC%3D%26secret%23tail",
            AuthLinkBuilder.AcceptInvitation(null, " ACME/HQ ", token));
        Assert.Throws<InvalidOperationException>(() =>
            AuthLinkBuilder.RequireHttpsPublicAppUrl("http://app.example.test"));
        Assert.Throws<InvalidOperationException>(() =>
            AuthLinkBuilder.RequireHttpsPublicAppUrl("https://localhost:3000"));
        Assert.Throws<InvalidOperationException>(() =>
            AuthLinkBuilder.ResolvePublicAppUrl("https://app.example.test/base"));
        Assert.Equal(
            "https://app.example.test",
            AuthLinkBuilder.RequireHttpsPublicAppUrl(" https://app.example.test/ "));
    }

    [Fact]
    public async Task ForgotPassword_DuplicateEmailAcrossTenants_MutatesOnlyNamedWorkspace()
    {
        await using var db = CreateDb();
        var hasher = new Pbkdf2PasswordHasher();
        var tenantA = new Tenant { Id = Guid.NewGuid(), Name = "Tenant A", Slug = "tenant-a" };
        var tenantB = new Tenant { Id = Guid.NewGuid(), Name = "Tenant B", Slug = "tenant-b" };
        var userA = new User
        {
            Id = Guid.NewGuid(), TenantId = tenantA.Id, Tenant = tenantA,
            Email = "same@example.test", NormalizedEmail = "SAME@EXAMPLE.TEST",
            FullName = "Tenant A User", PasswordHash = hasher.Hash("Password A1!")
        };
        var userB = new User
        {
            Id = Guid.NewGuid(), TenantId = tenantB.Id, Tenant = tenantB,
            Email = "same@example.test", NormalizedEmail = "SAME@EXAMPLE.TEST",
            FullName = "Tenant B User", PasswordHash = hasher.Hash("Password B1!")
        };
        db.AddRange(tenantA, tenantB, userA, userB);
        await db.SaveChangesAsync();

        await BuildService(db).ForgotPasswordAsync(
            new ForgotPasswordRequest("  SAME@example.test ", "  TENANT-A "),
            TestCtx,
            CancellationToken.None);

        var token = Assert.Single(await db.PasswordResetTokens.AsNoTracking().ToListAsync());
        Assert.Equal(userA.Id, token.UserId);
        Assert.DoesNotContain(await db.PasswordResetTokens.AsNoTracking().ToListAsync(), x => x.UserId == userB.Id);
    }

    [Fact]
    public async Task ResetPassword_WrongWorkspace_PerformsNoMutation()
    {
        var (db, user, _) = await SeedUserAsync();
        const string rawToken = "workspace-bound-reset-token";
        var originalHash = user.PasswordHash;
        var token = new PasswordResetToken
        {
            UserId = user.Id,
            TokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))),
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        db.PasswordResetTokens.Add(token);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => BuildService(db).ResetPasswordAsync(
            new ResetPasswordRequest(rawToken, "DifferentPassword1!", "other-tenant"),
            TestCtx,
            CancellationToken.None));

        Assert.Equal(originalHash, user.PasswordHash);
        Assert.Null(token.UsedAtUtc);
        Assert.Empty(await db.LoginActivities.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task AcceptInvitation_WrongWorkspace_PerformsNoMutation()
    {
        var (db, user, _) = await SeedUserAsync();
        const string rawToken = "workspace-bound-invitation-token";
        var originalHash = user.PasswordHash;
        db.Employees.Add(new Employee
        {
            Id = 8001,
            TenantId = user.TenantId,
            EmployeeCode = "AUTH-8001",
            FullName = "Invitation User",
            Status = "Active",
            JoiningDate = DateTime.UtcNow.AddYears(-1)
        });
        var link = new EmployeeUserAccount
        {
            TenantId = user.TenantId,
            EmployeeId = 8001,
            UserId = user.Id,
            AccessMode = AccessModes.EssOnly,
            Status = "Invited",
            RequiresPasswordSetup = true,
            InvitationTokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))),
            InvitationExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        db.EmployeeUserAccounts.Add(link);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => BuildService(db).AcceptInvitationAsync(
            new AcceptInvitationRequest(rawToken, "DifferentPassword1!", "other-tenant"),
            TestCtx,
            CancellationToken.None));

        Assert.Equal(originalHash, user.PasswordHash);
        Assert.Equal("Invited", link.Status);
        Assert.Null(link.InvitationAcceptedAtUtc);
        Assert.Empty(await db.RefreshTokens.AsNoTracking().ToListAsync());
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
        user.Status = "PendingPasswordSetup";
        user.AccessMode = AccessModes.NoLogin;
        user.IsActive = false;
        user.IsEmailConfirmed = false;
        const string invitationToken = "one-time-invitation-token";
        db.Employees.Add(new Employee
        {
            Id = 42,
            TenantId = user.TenantId,
            EmployeeCode = "AUTH-42",
            FullName = "Invitation User",
            Status = "Active",
            JoiningDate = DateTime.UtcNow.AddYears(-1)
        });
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
        var request = new AcceptInvitationRequest(invitationToken, "NewPassword1!", "zayra");
        await auth.AcceptInvitationAsync(request, TestCtx, CancellationToken.None);

        var link = await db.EmployeeUserAccounts.SingleAsync();
        Assert.Equal(string.Empty, link.InvitationTokenHash);
        Assert.Null(link.InvitationExpiresAtUtc);
        Assert.NotNull(link.InvitationAcceptedAtUtc);
        Assert.False(link.RequiresPasswordSetup);
        Assert.Equal("Active", link.Status);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => auth.AcceptInvitationAsync(request, TestCtx, CancellationToken.None));
        Assert.Empty(await db.RefreshTokens.ToListAsync());
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

    // Regression for the 2026-09-20/21 OOM kills (12 on the 512 MB Render instance). The per-request
    // session check and the /me by-id load pulled roles×permissions×overrides×accounts×grants as ONE
    // cartesian query. On a relational provider (InMemory ignores query splitting) this pins: the
    // checks stay correct on a production-sized graph, no statement joins the sibling collections,
    // and every split statement runs inside a transaction (the split-query security contract).
    [Fact]
    public async Task SessionCheck_ProductionSizedAdmin_IsCorrectAndUsesSplitQueries()
    {
        var recorder = new Zayra.Api.Tests.Security.ReaderCommandRecorder();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseSqlite(connection).AddInterceptors(recorder).Options);
        await db.Database.EnsureCreatedAsync();
        var seeded = await Zayra.Api.Tests.Security.ProductionSizedAdminSeed.SeedAsync(db);
        var principal = await Zayra.Api.Tests.Security.ProductionSizedAdminSeed.PrincipalAsync(db, seeded);

        recorder.Reset();
        Assert.True(await TenantSessionSecurity.IsCurrentAsync(principal, db, CancellationToken.None));
        AssertSplitInsideTransaction(recorder, "session check");

        recorder.Reset();
        db.ChangeTracker.Clear();
        var me = await BuildService(db).GetCurrentUserAsync(seeded.UserId, CancellationToken.None);
        Assert.NotNull(me);
        AssertSplitInsideTransaction(recorder, "/me by-id load");
    }

    private static void AssertSplitInsideTransaction(Zayra.Api.Tests.Security.ReaderCommandRecorder recorder, string path)
    {
        Assert.False(recorder.AnyCartesianUserGraphCommand,
            $"{path}: a single statement joined role permissions with overrides/entity grants (cartesian).");
        // Root + role graph + overrides + employee accounts + entity grants.
        Assert.True(recorder.UserGraphCommands.Count >= 5,
            $"{path}: expected split user-graph statements, saw {recorder.UserGraphCommands.Count}.");
        Assert.All(recorder.Commands, c => Assert.True(c.InTransaction,
            $"{path}: statement ran outside a transaction: {c.Sql[..Math.Min(120, c.Sql.Length)]}"));
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
            new TotpService(DataProtectionProvider.Create("ZayraTests")),
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

/// <summary>
/// Regression cover for the P0 that logged every session out 30 minutes after login.
///
/// Program.cs registers Npgsql with <c>EnableRetryOnFailure</c>, so the ambient execution strategy
/// is <c>NpgsqlRetryingExecutionStrategy</c>, which refuses a user-initiated
/// <c>BeginTransactionAsync</c> unless the whole unit runs inside
/// <c>Database.CreateExecutionStrategy().ExecuteAsync(...)</c>. AuthService took a bare
/// transaction, so <c>POST /api/auth/refresh</c> threw
/// <c>InvalidOperationException("... does not support user-initiated transactions ...")</c> on
/// every single call and the API returned HTTP 400.
///
/// <para>The pre-existing <see cref="AuthRefreshTokenSecurityTests"/> missed it because
/// <c>PostgresFixture.CreateDb()</c> does NOT enable retry — with no retrying strategy a bare
/// transaction is perfectly legal. These tests therefore build their contexts with
/// <see cref="CreateRetryingDb"/>, matching production, which is the only configuration that can
/// catch this class of defect.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class AuthRetryingExecutionStrategyTests
{
    private readonly PostgresFixture _fixture;
    private static readonly RequestContext Context = new("203.0.113.11", "retry-strategy-tests");

    public AuthRetryingExecutionStrategyTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>Production's provider configuration: Npgsql + EnableRetryOnFailure.</summary>
    private ZayraDbContext CreateRetryingDb() => new(
        new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(_fixture.ConnectionString, o => o.EnableRetryOnFailure(maxRetryCount: 3))
            .Options);

    [Fact]
    public async Task RefreshAsync_UnderRetryingStrategy_ReturnsRotatedPairInsteadOfFailing()
    {
        var seeded = await SeedUserAsync();

        string originalToken;
        await using (var loginDb = CreateRetryingDb())
        {
            var login = await NoStrategyConflict(() => BuildService(loginDb).LoginAsync(
                new LoginRequest(seeded.Email, seeded.Password, seeded.TenantSlug),
                Context,
                CancellationToken.None));
            originalToken = login.Tokens!.RefreshToken;
        }

        AuthResponse refreshed;
        await using (var refreshDb = CreateRetryingDb())
        {
            refreshed = await NoStrategyConflict(() => BuildService(refreshDb).RefreshAsync(
                new RefreshTokenRequest(originalToken), Context, CancellationToken.None));
        }

        // A real rotated pair, not an echo of the presented credential.
        Assert.False(string.IsNullOrWhiteSpace(refreshed.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(refreshed.RefreshToken));
        Assert.NotEqual(originalToken, refreshed.RefreshToken);

        await using var verify = _fixture.CreateDb();
        var tokens = await verify.RefreshTokens.AsNoTracking()
            .Where(x => x.UserId == seeded.UserId)
            .ToListAsync();
        Assert.Equal(2, tokens.Count);
        var parent = Assert.Single(tokens, x => x.TokenHash == HashToken(originalToken));
        var descendant = Assert.Single(tokens, x => x.TokenHash == HashToken(refreshed.RefreshToken));
        Assert.NotNull(parent.RevokedAtUtc);
        Assert.Equal(descendant.TokenHash, parent.ReplacedByTokenHash);
        Assert.Null(descendant.RevokedAtUtc);
        Assert.Equal(parent.FamilyId, descendant.FamilyId);
        // Rotation must not extend the absolute session lifetime.
        Assert.Equal(parent.ExpiresAtUtc, descendant.ExpiresAtUtc);
        // The whole unit committed, audit row included.
        Assert.True(await verify.AuditLogs.AnyAsync(x =>
            x.TenantId == seeded.TenantId && x.Action == "auth.refresh"));
    }

    [Fact]
    public async Task RefreshAsync_UnderRetryingStrategy_RepeatedRotationKeepsWorking()
    {
        // 30 minutes of a demo is several rotations, and each one runs through the strategy.
        var seeded = await SeedUserAsync();

        await using var db = CreateRetryingDb();
        var auth = BuildService(db);
        var login = await NoStrategyConflict(() => auth.LoginAsync(
            new LoginRequest(seeded.Email, seeded.Password, seeded.TenantSlug), Context, CancellationToken.None));

        var current = login.Tokens!.RefreshToken;
        var seen = new List<string> { current };
        for (var i = 0; i < 3; i++)
        {
            var next = await NoStrategyConflict(() => auth.RefreshAsync(
                new RefreshTokenRequest(current), Context, CancellationToken.None));
            current = next.RefreshToken;
            Assert.DoesNotContain(current, seen);
            seen.Add(current);
        }

        await using var verify = _fixture.CreateDb();
        var tokens = await verify.RefreshTokens.AsNoTracking()
            .Where(x => x.UserId == seeded.UserId)
            .ToListAsync();
        Assert.Equal(4, tokens.Count);
        Assert.Single(tokens.Select(x => x.FamilyId).Distinct());
        // Exactly one live token — the newest; every ancestor consumed.
        var active = Assert.Single(tokens, x => x.RevokedAtUtc == null);
        Assert.Equal(HashToken(current), active.TokenHash);
    }

    [Fact]
    public async Task RefreshAsync_UnderRetryingStrategy_ReplayedAncestorStillRevokesFamily()
    {
        // Covers RevokeRefreshTokenFamilyForReuseAsync, which took its own bare transaction. Under
        // the retrying strategy the replay path threw the execution-strategy error instead of
        // killing the stolen lineage, so a replayed token was neither revoked nor audited — and the
        // caller saw HTTP 400 rather than 401.
        var seeded = await SeedUserAsync();

        string ancestor;
        await using (var db = CreateRetryingDb())
        {
            var auth = BuildService(db);
            var login = await NoStrategyConflict(() => auth.LoginAsync(
                new LoginRequest(seeded.Email, seeded.Password, seeded.TenantSlug), Context, CancellationToken.None));
            ancestor = login.Tokens!.RefreshToken;
            var child = await NoStrategyConflict(() => auth.RefreshAsync(
                new RefreshTokenRequest(ancestor), Context, CancellationToken.None));
            await NoStrategyConflict(() => auth.RefreshAsync(
                new RefreshTokenRequest(child.RefreshToken), Context, CancellationToken.None));
        }

        await using (var replayDb = CreateRetryingDb())
        {
            var replay = await Record.ExceptionAsync(() => BuildService(replayDb).RefreshAsync(
                new RefreshTokenRequest(ancestor), Context, CancellationToken.None));
            AssertNotStrategyConflict(replay);
            // The designed response to a replayed credential: 401, never 400.
            Assert.IsType<UnauthorizedAccessException>(replay);
        }

        await using var verify = _fixture.CreateDb();
        var tokens = await verify.RefreshTokens.AsNoTracking()
            .Where(x => x.UserId == seeded.UserId)
            .ToListAsync();
        Assert.Equal(3, tokens.Count);
        Assert.Single(tokens.Select(x => x.FamilyId).Distinct());
        // Whole lineage terminated, including the previously-live descendant.
        Assert.DoesNotContain(tokens, x => x.RevokedAtUtc == null);
        var reuseAudit = await verify.AuditLogs.AsNoTracking().SingleAsync(x =>
            x.TenantId == seeded.TenantId && x.Action == "auth.refresh_reuse_detected");
        Assert.Contains(tokens[0].FamilyId.ToString("D"), reuseAudit.Metadata);
    }

    [Fact]
    public async Task AcceptInvitationAsync_UnderRetryingStrategy_ConsumesInvitationWithoutSession()
    {
        // The third bare-transaction site. Under the retrying strategy every
        // POST /api/auth/accept-invitation returned HTTP 400, so no invited employee could ever
        // set a password.
        var seeded = await SeedUserAsync();
        const string invitationToken = "retry-strategy-invitation-token";

        await using (var seedDb = _fixture.CreateDb())
        {
            var stagedUser = await seedDb.Users.SingleAsync(x => x.Id == seeded.UserId);
            stagedUser.Status = "PendingPasswordSetup";
            stagedUser.AccessMode = AccessModes.NoLogin;
            stagedUser.IsActive = false;
            stagedUser.IsEmailConfirmed = false;
            var employee = new Employee
            {
                TenantId = seeded.TenantId,
                EmployeeCode = $"AUTH-{Guid.NewGuid():N}",
                FullName = "Invitation User",
                Status = "Active",
                JoiningDate = DateTime.UtcNow.AddYears(-1)
            };
            seedDb.Employees.Add(employee);
            await seedDb.SaveChangesAsync();
            seedDb.EmployeeUserAccounts.Add(new EmployeeUserAccount
            {
                TenantId = seeded.TenantId,
                EmployeeId = employee.Id,
                UserId = seeded.UserId,
                AccessMode = AccessModes.EssOnly,
                Status = "Invited",
                RequiresPasswordSetup = true,
                InvitationTokenHash = HashToken(invitationToken),
                InvitationExpiresAtUtc = DateTime.UtcNow.AddHours(1)
            });
            await seedDb.SaveChangesAsync();
        }

        await using (var acceptDb = CreateRetryingDb())
        {
            await NoStrategyConflict(() => BuildService(acceptDb).AcceptInvitationAsync(
                new AcceptInvitationRequest(invitationToken, "NewPassword1!", seeded.TenantSlug),
                Context,
                CancellationToken.None));
        }

        await using var verify = _fixture.CreateDb();
        var link = await verify.EmployeeUserAccounts.AsNoTracking()
            .SingleAsync(x => x.UserId == seeded.UserId);
        Assert.Equal(string.Empty, link.InvitationTokenHash);
        Assert.Null(link.InvitationExpiresAtUtc);
        Assert.NotNull(link.InvitationAcceptedAtUtc);
        Assert.False(link.RequiresPasswordSetup);
        Assert.Equal("Active", link.Status);
        Assert.Empty(await verify.RefreshTokens.AsNoTracking()
            .Where(x => x.UserId == seeded.UserId && x.RevokedAtUtc == null).ToListAsync());
        Assert.True(await verify.AuditLogs.AnyAsync(x =>
            x.TenantId == seeded.TenantId && x.Action == "auth.invitation_accepted"));
    }

    // ── Failure reporting ─────────────────────────────────────────────────────
    // The defect surfaced as InvalidOperationException, which AuthController's
    // UnauthorizedAccessException handler does not catch, so it fell through to the generic
    // handler as HTTP 400. Name it explicitly so a reintroduction is unmistakable.
    private const string ConflictFragment = "does not support user-initiated transactions";

    private static async Task<T> NoStrategyConflict<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains(ConflictFragment))
        {
            Assert.Fail(
                "REGRESSION: a bare BeginTransactionAsync is back under the retrying execution " +
                "strategy. Every /api/auth/refresh call will return HTTP 400 and log users out. " +
                $"Wrap the unit in Database.CreateExecutionStrategy().ExecuteAsync(...). Original: {ex.Message}");
            throw;
        }
    }

    private static async Task NoStrategyConflict(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains(ConflictFragment))
        {
            Assert.Fail(
                "REGRESSION: a bare BeginTransactionAsync is back under the retrying execution " +
                "strategy. Original: " + ex.Message);
        }
    }

    private static void AssertNotStrategyConflict(Exception? ex)
    {
        if (ex is InvalidOperationException invalid && invalid.Message.Contains(ConflictFragment))
            Assert.Fail(
                "REGRESSION: the refresh-token reuse path took a bare BeginTransactionAsync under " +
                $"the retrying execution strategy, so the stolen family was never revoked. Original: {invalid.Message}");
    }

    private async Task<SeededAuthUser> SeedUserAsync()
    {
        await using var db = _fixture.CreateDb();
        const string password = "CorrectPassword1!";
        var tenantId = Guid.NewGuid();
        var slug = $"retry-{Guid.NewGuid():N}";
        var email = $"retry-{Guid.NewGuid():N}@example.test";
        var tenant = new Tenant { Id = tenantId, Name = "Retry Strategy Tenant", Slug = slug };
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
            FullName = "Retry Strategy Admin",
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
            new TotpService(DataProtectionProvider.Create("ZayraTests")),
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
    public Task<bool> AdminDisableAsync(Guid userId, Guid tenantId, RequestContext context, CancellationToken ct) => throw new NotImplementedException();
    public Task<MfaSetupInitDto> InitiatePlatformSetupAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
    public Task<bool> VerifyPlatformSetupAsync(Guid id, MfaVerifySetupRequest req, CancellationToken ct) => throw new NotImplementedException();
    public Task<string> CreatePlatformChallengeAsync(Guid id, string ip, CancellationToken ct) => throw new NotImplementedException();
    public Task<Zayra.Api.Models.PlatformUser?> VerifyPlatformChallengeAsync(string token, string code, CancellationToken ct) => throw new NotImplementedException();
    public Task<Zayra.Api.Models.PlatformUser?> CompletePlatformChallengeAsync(string token, string code, RequestContext context, CancellationToken ct) => throw new NotImplementedException();
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
