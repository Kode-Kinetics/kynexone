using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Auth;
using Zayra.Api.Controllers;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Platform;

/// <summary>
/// Tests for LoginActivity audit trail:
///   - successful login writes a login_success row
///   - failed login writes login_failed without leaking passwords/tokens
///   - lockout writes account_locked
///   - password reset events write corresponding rows
///   - platform admin can filter login activity by tenant
///   - cross-tenant: tenant A cannot see tenant B's records
/// </summary>
public class LoginActivityTests : PlatformTestBase
{
    // ── AuthService helpers ───────────────────────────────────────────────────

    private static AuthService BuildAuthService(Zayra.Api.Data.ZayraDbContext db)
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

    private static readonly RequestContext TestCtx = new("127.0.0.1", "Mozilla/5.0 tests");

    private static async Task<(Zayra.Api.Data.ZayraDbContext db, User user)> SeedUserAsync(
        int maxAttempts = 5, bool locked = false)
    {
        var db     = CreateDb();
        var hasher = new Pbkdf2PasswordHasher();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", Slug = "acme" };
        var sec = new SecuritySetting
        {
            Id                     = Guid.NewGuid(),
            TenantId               = tenant.Id,
            MaxFailedLoginAttempts = maxAttempts,
            LockoutDurationMinutes = 15,
        };
        var user = new User
        {
            Id              = Guid.NewGuid(),
            TenantId        = tenant.Id,
            FullName        = "Alice",
            Email           = "alice@acme.com",
            NormalizedEmail = "ALICE@ACME.COM",
            PasswordHash    = hasher.Hash("Correct123!"),
            IsActive        = true,
            IsLocked        = locked,
            LockoutEnd      = locked ? DateTime.UtcNow.AddMinutes(10) : null,
            Tenant          = tenant,
        };
        db.Tenants.Add(tenant);
        db.SecuritySettings.Add(sec);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return (db, user);
    }

    // ── Successful login ──────────────────────────────────────────────────────

    [Fact]
    public async Task SuccessfulLogin_WritesLoginSuccessActivity()
    {
        var (db, user) = await SeedUserAsync();
        var svc = BuildAuthService(db);

        await svc.LoginAsync(new LoginRequest("alice@acme.com", "Correct123!", "acme"), TestCtx, CancellationToken.None);

        var activity = await db.LoginActivities
            .FirstOrDefaultAsync(a => a.UserId == user.Id && a.EventType == LoginEventTypes.LoginSuccess);

        activity.Should().NotBeNull("successful login must create a login_success LoginActivity row");
        activity!.EmailAttempted.Should().Be("alice@acme.com");
        activity.IpAddress.Should().Be("127.0.0.1");
        activity.FailureReason.Should().BeNull();
    }

    [Fact]
    public async Task SuccessfulLogin_DoesNotExposePasswordOrToken()
    {
        var (db, user) = await SeedUserAsync();
        var svc = BuildAuthService(db);

        await svc.LoginAsync(new LoginRequest("alice@acme.com", "Correct123!", "acme"), TestCtx, CancellationToken.None);

        var activities = await db.LoginActivities.Where(a => a.UserId == user.Id).ToListAsync();
        foreach (var a in activities)
        {
            a.EmailAttempted.Should().NotContain("123!", "password must never appear in EmailAttempted");
            a.FailureReason.Should().NotContain("Correct", "password must not appear in FailureReason");
        }
    }

    // ── Failed login ──────────────────────────────────────────────────────────

    [Fact]
    public async Task FailedLogin_WritesLoginFailedActivity()
    {
        var (db, user) = await SeedUserAsync();
        var svc = BuildAuthService(db);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            svc.LoginAsync(new LoginRequest("alice@acme.com", "WrongPass!", "acme"), TestCtx, CancellationToken.None));

        var activity = await db.LoginActivities
            .FirstOrDefaultAsync(a => a.UserId == user.Id && a.EventType == LoginEventTypes.LoginFailed);

        activity.Should().NotBeNull("wrong password must create a login_failed LoginActivity row");
        activity!.FailureReason.Should().Be("password_mismatch");
        activity.IpAddress.Should().Be("127.0.0.1");
    }

    // ── Account lockout ───────────────────────────────────────────────────────

    [Fact]
    public async Task RepeatedFailures_WriteAccountLockedActivity()
    {
        var (db, _) = await SeedUserAsync(maxAttempts: 3);
        var svc = BuildAuthService(db);

        // 3 bad attempts → should lock on 3rd
        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                svc.LoginAsync(new LoginRequest("alice@acme.com", "Bad!", "acme"), TestCtx, CancellationToken.None));
        }

        var lockActivity = await db.LoginActivities
            .FirstOrDefaultAsync(a => a.EventType == LoginEventTypes.AccountLocked);

        lockActivity.Should().NotBeNull("after max failures the account_locked event must be recorded");
    }

    [Fact]
    public async Task LoginOnLockedAccount_WritesBlockedActivity()
    {
        var (db, user) = await SeedUserAsync(locked: true);
        var svc = BuildAuthService(db);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            svc.LoginAsync(new LoginRequest("alice@acme.com", "Correct123!", "acme"), TestCtx, CancellationToken.None));

        var activity = await db.LoginActivities
            .FirstOrDefaultAsync(a => a.UserId == user.Id && a.EventType == LoginEventTypes.LoginBlockedLockout);

        activity.Should().NotBeNull("locked account attempt must create login_blocked_lockout activity");
    }

    // ── Password reset events ─────────────────────────────────────────────────

    [Fact]
    public async Task ForgotPassword_WritesPasswordResetRequestedActivity()
    {
        var (db, user) = await SeedUserAsync();
        var svc = BuildAuthService(db);

        await svc.ForgotPasswordAsync(new ForgotPasswordRequest("alice@acme.com", "acme"), TestCtx, CancellationToken.None);

        var activity = await db.LoginActivities
            .FirstOrDefaultAsync(a => a.UserId == user.Id && a.EventType == LoginEventTypes.PasswordResetRequested);

        activity.Should().NotBeNull("forgot-password must create a password_reset_requested activity");
    }

    // ── Platform admin filter by tenant ──────────────────────────────────────

    [Fact]
    public async Task PlatformAdmin_CanFilterLoginActivity_ByTenant()
    {
        await using var db = CreateDb();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        db.LoginActivities.AddRange(
            new LoginActivity { TenantId = tenantA, EventType = LoginEventTypes.LoginSuccess,  OccurredAtUtc = DateTime.UtcNow },
            new LoginActivity { TenantId = tenantA, EventType = LoginEventTypes.LoginFailed,   OccurredAtUtc = DateTime.UtcNow },
            new LoginActivity { TenantId = tenantB, EventType = LoginEventTypes.LoginSuccess,  OccurredAtUtc = DateTime.UtcNow }
        );
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var result = await controller.ListLoginActivity(tenantA, null, null, null, null, 1, 50, CancellationToken.None);

        var ok   = result.Should().BeOfType<OkObjectResult>().Subject;
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        var doc  = System.Text.Json.JsonDocument.Parse(json);
        var total = doc.RootElement.GetProperty("total").GetInt32();

        total.Should().Be(2, "filtering by tenantA must return only tenantA rows");
    }

    // ── Cross-tenant isolation ────────────────────────────────────────────────

    [Fact]
    public async Task LoginActivity_TenantA_NotReturnedForTenantBFilter()
    {
        await using var db = CreateDb();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        db.LoginActivities.Add(new LoginActivity { TenantId = tenantA, EventType = LoginEventTypes.LoginSuccess });
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var result = await controller.ListLoginActivity(tenantB, null, null, null, null, 1, 50, CancellationToken.None);

        var ok   = result.Should().BeOfType<OkObjectResult>().Subject;
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        var doc  = System.Text.Json.JsonDocument.Parse(json);
        doc.RootElement.GetProperty("total").GetInt32().Should().Be(0, "tenantB filter must not expose tenantA data");
    }

    // ── Filter by eventType ───────────────────────────────────────────────────

    [Fact]
    public async Task LoginActivity_FilterByEventType_ReturnsOnlyMatchingRows()
    {
        await using var db = CreateDb();
        var tenant = Guid.NewGuid();

        db.LoginActivities.AddRange(
            new LoginActivity { TenantId = tenant, EventType = LoginEventTypes.LoginSuccess },
            new LoginActivity { TenantId = tenant, EventType = LoginEventTypes.LoginFailed  },
            new LoginActivity { TenantId = tenant, EventType = LoginEventTypes.LoginFailed  }
        );
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var result = await controller.ListLoginActivity(tenant, null, LoginEventTypes.LoginFailed, null, null, 1, 50, CancellationToken.None);

        var ok   = result.Should().BeOfType<OkObjectResult>().Subject;
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        var doc  = System.Text.Json.JsonDocument.Parse(json);
        doc.RootElement.GetProperty("total").GetInt32().Should().Be(2);
    }
}

// ── Fakes needed by AuthService tests ──────────────────────────────────────────

internal sealed class FakeEmailService : IEmailService
{
    public Task SendAsync(string to, string name, string subject, string html,
        IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task<bool> IsConfiguredAsync(CancellationToken ct = default) => Task.FromResult(false);
}

internal sealed class NullMfaService : IMfaService
{
    public Task<MfaSetupInitDto> InitiateSetupAsync(Guid u, Guid t, CancellationToken c) => throw new NotImplementedException();
    public Task<bool> VerifySetupAsync(Guid u, Guid t, MfaVerifySetupRequest r, CancellationToken c) => throw new NotImplementedException();
    public Task<string> CreateEnrollmentChallengeAsync(Guid u, Guid t, string ip, CancellationToken c) => throw new NotImplementedException();
    public Task<MfaSetupInitDto?> InitiateEnrollmentSetupAsync(string token, CancellationToken c) => throw new NotImplementedException();
    public Task<bool> VerifyEnrollmentSetupAsync(string token, MfaVerifySetupRequest r, CancellationToken c) => throw new NotImplementedException();
    public Task<string> CreateChallengeAsync(Guid u, Guid t, string ip, CancellationToken c) => throw new NotImplementedException();
    public Task<User?> VerifyChallengeAsync(string token, string code, CancellationToken c) => throw new NotImplementedException();
    public Task<bool> DisableAsync(Guid u, Guid t, string code, CancellationToken c) => throw new NotImplementedException();
    public Task<MfaSetupInitDto> InitiatePlatformSetupAsync(Guid id, CancellationToken c) => throw new NotImplementedException();
    public Task<bool> VerifyPlatformSetupAsync(Guid id, MfaVerifySetupRequest r, CancellationToken c) => throw new NotImplementedException();
    public Task<string> CreatePlatformChallengeAsync(Guid id, string ip, CancellationToken c) => throw new NotImplementedException();
    public Task<PlatformUser?> VerifyPlatformChallengeAsync(string token, string code, CancellationToken c) => throw new NotImplementedException();
    public Task<bool> DisablePlatformAsync(Guid id, string code, CancellationToken c) => throw new NotImplementedException();
}
