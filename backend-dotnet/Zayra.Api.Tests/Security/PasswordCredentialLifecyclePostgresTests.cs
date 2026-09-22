using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Models;
using Zayra.Api.Tests;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Real-PostgreSQL evidence for the password credential lifecycle.
/// - AUTH-C04-PG01: change-password kills every access token (stamp) and every refresh family.
/// - AUTH-C05-PG01: five concurrent resets with one link have exactly one winner and one effect.
/// - AUTH-C04/C05-PG02: a failure injected at the audit insert rolls the whole credential change back.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class PasswordCredentialLifecyclePostgresTests
{
    private const int ResetRaceCycles = 10;
    private const int ConcurrentClients = 5;
    private const string InjectedFailureMarker = "AUTH_C04_INJECTED_AUDIT_FAILURE";
    private const string OriginalPassword = "OriginalPassword1!";

    private static readonly RequestContext Request = new("203.0.113.94", "c04-pg-credential-tests");
    private static readonly IOptions<JwtOptions> Jwt = Options.Create(new JwtOptions
    {
        Issuer = "Zayra.C04.Postgres.Tests",
        TenantAudience = "kynexone-tenant-c04-tests",
        PlatformAudience = "kynexone-platform-c04-tests",
        SigningKey = "C04_TEST_SIGNING_KEY_WITH_MORE_THAN_64_CHARACTERS_FOR_AUTH_TESTS_2026",
        AccessTokenMinutes = 30,
        RefreshTokenDays = 7
    });
    private static readonly IConfiguration Configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["APP_URL"] = "https://app.example.test" })
        .Build();

    private readonly PostgresFixture _fixture;
    private readonly TotpService _totp = new(new EphemeralDataProtectionProvider());

    public PasswordCredentialLifecyclePostgresTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AuthC04Pg01_ChangePassword_KillsOldAccessTokensAndEveryRefreshFamily_NewLoginWorks()
    {
        var seed = await SeedUserAsync("change");
        var first = await LoginAsync(seed, OriginalPassword);
        var second = await LoginAsync(seed, OriginalPassword);
        Assert.True(await IsCurrentAsync(first.AccessToken), "Control: the pre-change access token must be current.");
        Assert.True(await IsCurrentAsync(second.AccessToken), "Control: the pre-change access token must be current.");

        const string newPassword = "ChangedPassword2@b";
        await using (var actionDb = _fixture.CreateRetryingDb())
        {
            await BuildAuthService(actionDb).ChangePasswordAsync(
                seed.UserId,
                new ChangePasswordRequest(OriginalPassword, newPassword),
                Request with { UserId = seed.UserId, TenantId = seed.TenantId },
                CancellationToken.None);
        }

        Assert.False(await IsCurrentAsync(first.AccessToken));
        Assert.False(await IsCurrentAsync(second.AccessToken));
        foreach (var oldRefresh in new[] { first.RefreshToken, second.RefreshToken })
        {
            await using var refreshDb = _fixture.CreateRetryingDb();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => BuildAuthService(refreshDb).RefreshAsync(
                new RefreshTokenRequest(oldRefresh), Request, CancellationToken.None));
        }

        await using (var oldPasswordDb = _fixture.CreateRetryingDb())
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => BuildAuthService(oldPasswordDb).LoginAsync(
                new LoginRequest(seed.Email, OriginalPassword, seed.TenantSlug), Request, CancellationToken.None));
        }
        var fresh = await LoginAsync(seed, newPassword);
        Assert.True(await IsCurrentAsync(fresh.AccessToken));

        await using var verify = _fixture.CreateRetryingDb();
        Assert.Equal(1, await verify.RefreshTokens.AsNoTracking()
            .CountAsync(x => x.UserId == seed.UserId && x.RevokedAtUtc == null));
        Assert.Single(await verify.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == seed.TenantId
                && x.Action == "auth.password_changed"
                && x.EntityId == seed.UserId.ToString()).ToListAsync());
        Assert.Null(await verify.PasswordResetTokens.AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == seed.UserId && x.UsedAtUtc == null));
    }

    [Fact]
    public async Task AuthC05Pg01_SameResetLink_FiveClients_HasOneWinnerSiblingsDeadSessionsRevoked()
    {
        for (var cycle = 0; cycle < ResetRaceCycles; cycle++)
        {
            var seed = await SeedUserAsync($"reset-{cycle}");
            var session = await LoginAsync(seed, OriginalPassword);
            Assert.True(await IsCurrentAsync(session.AccessToken));
            var newPassword = $"ResetPassword-{Guid.NewGuid():N}!aA1";

            var barrier = new AsyncStartBarrier(ConcurrentClients);
            var outcomes = await Task.WhenAll(Enumerable.Range(0, ConcurrentClients).Select(async _ =>
            {
                await using var db = _fixture.CreateRetryingDb();
                await db.Database.OpenConnectionAsync();
                await db.Database.ExecuteSqlRawAsync("SELECT 1");
                await barrier.SignalAndWaitAsync();
                try
                {
                    await BuildAuthService(db).ResetPasswordAsync(
                        new ResetPasswordRequest(seed.RawReset, newPassword, seed.TenantSlug),
                        Request,
                        CancellationToken.None);
                    return (Exception?)null;
                }
                catch (Exception error)
                {
                    return error;
                }
            }));

            Assert.Single(outcomes, x => x is null);
            Assert.Equal(ConcurrentClients - 1, outcomes.Count(x => x is UnauthorizedAccessException));

            // A sibling link issued before the reset must be dead too.
            await using (var siblingDb = _fixture.CreateRetryingDb())
            {
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => BuildAuthService(siblingDb).ResetPasswordAsync(
                    new ResetPasswordRequest(seed.RawSiblingReset, "SiblingPassword3#c", seed.TenantSlug),
                    Request,
                    CancellationToken.None));
            }

            Assert.False(await IsCurrentAsync(session.AccessToken));
            await using (var refreshDb = _fixture.CreateRetryingDb())
            {
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => BuildAuthService(refreshDb).RefreshAsync(
                    new RefreshTokenRequest(session.RefreshToken), Request, CancellationToken.None));
            }

            await using var verify = _fixture.CreateRetryingDb();
            var user = await verify.Users.AsNoTracking().SingleAsync(x => x.Id == seed.UserId);
            Assert.True(new Pbkdf2PasswordHasher().Verify(newPassword, user.PasswordHash));
            Assert.Empty(await verify.PasswordResetTokens.AsNoTracking()
                .Where(x => x.UserId == seed.UserId && x.UsedAtUtc == null).ToListAsync());
            Assert.Empty(await verify.RefreshTokens.AsNoTracking()
                .Where(x => x.UserId == seed.UserId && x.RevokedAtUtc == null).ToListAsync());
            Assert.Empty(await verify.MfaChallengeTokens.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.UserId == seed.UserId && x.UsedAtUtc == null).ToListAsync());
            Assert.Single(await verify.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.TenantId == seed.TenantId
                    && x.Action == "auth.password_reset"
                    && x.EntityId == seed.UserId.ToString()).ToListAsync());
            Assert.Single(await verify.LoginActivities.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.UserId == seed.UserId
                    && x.EventType == LoginEventTypes.PasswordResetCompleted).ToListAsync());
        }
    }

    [Theory]
    [InlineData("auth.password_changed")]
    [InlineData("auth.password_reset")]
    public async Task AuthC04C05Pg02_InjectedAuditFailure_LeavesPasswordStampAndSessionsUntouched(string action)
    {
        var seed = await SeedUserAsync("rollback");
        var session = await LoginAsync(seed, OriginalPassword);
        await using (var snapshotDb = _fixture.CreateRetryingDb())
        {
            var before = await snapshotDb.Users.AsNoTracking().SingleAsync(x => x.Id == seed.UserId);
            seed = seed with { PasswordHash = before.PasswordHash, UpdatedAtUtc = before.UpdatedAtUtc };
        }

        await InstallAuditFailureTriggerAsync(seed.UserId);
        Exception? failure;
        try
        {
            await using var actionDb = _fixture.CreateRetryingDb();
            var service = BuildAuthService(actionDb);
            failure = await Record.ExceptionAsync(() => action == "auth.password_changed"
                ? service.ChangePasswordAsync(
                    seed.UserId,
                    new ChangePasswordRequest(OriginalPassword, "RolledBackPassword4$d"),
                    Request with { UserId = seed.UserId, TenantId = seed.TenantId },
                    CancellationToken.None)
                : service.ResetPasswordAsync(
                    new ResetPasswordRequest(seed.RawReset, "RolledBackPassword4$d", seed.TenantSlug),
                    Request,
                    CancellationToken.None));
        }
        finally
        {
            await DropAuditFailureTriggerAsync();
        }

        Assert.NotNull(failure);
        Assert.Contains(InjectedFailureMarker, failure!.ToString(), StringComparison.Ordinal);

        await using var verify = _fixture.CreateRetryingDb();
        var user = await verify.Users.AsNoTracking().SingleAsync(x => x.Id == seed.UserId);
        Assert.Equal(seed.PasswordHash, user.PasswordHash);
        Assert.Equal(seed.UpdatedAtUtc, user.UpdatedAtUtc);
        Assert.Equal(2, await verify.PasswordResetTokens.AsNoTracking()
            .CountAsync(x => x.UserId == seed.UserId && x.UsedAtUtc == null));
        Assert.Equal(1, await verify.MfaChallengeTokens.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(x => x.UserId == seed.UserId && x.UsedAtUtc == null));
        Assert.Equal(1, await verify.RefreshTokens.AsNoTracking()
            .CountAsync(x => x.UserId == seed.UserId && x.RevokedAtUtc == null));
        Assert.Empty(await verify.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.EntityId == seed.UserId.ToString() && x.Action == action).ToListAsync());
        Assert.Empty(await verify.LoginActivities.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.UserId == seed.UserId
                && x.EventType == LoginEventTypes.PasswordResetCompleted).ToListAsync());
        Assert.True(await IsCurrentAsync(session.AccessToken));
    }

    private async Task<UserSeed> SeedUserAsync(string label)
    {
        await using var db = _fixture.CreateRetryingDb();
        var suffix = $"{label}-{Guid.NewGuid():N}";
        var tenant = new Tenant { Name = $"C04 {suffix}", Slug = $"c04-{suffix}" };
        db.Tenants.Add(tenant);
        db.SecuritySettings.Add(new SecuritySetting { TenantId = tenant.Id });
        await db.SaveChangesAsync();

        var email = $"c04-{suffix}@example.test";
        var user = new User
        {
            TenantId = tenant.Id,
            Email = email,
            NormalizedEmail = AuthService.Normalize(email),
            FullName = "C04 Credential User",
            PasswordHash = new Pbkdf2PasswordHasher().Hash(OriginalPassword)
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var rawReset = $"c04-reset-{Guid.NewGuid():N}";
        var rawSibling = $"c04-sibling-{Guid.NewGuid():N}";
        db.PasswordResetTokens.AddRange(
            new PasswordResetToken { UserId = user.Id, TokenHash = Hash(rawReset), ExpiresAtUtc = DateTime.UtcNow.AddHours(1) },
            new PasswordResetToken { UserId = user.Id, TokenHash = Hash(rawSibling), ExpiresAtUtc = DateTime.UtcNow.AddHours(1) });
        db.MfaChallengeTokens.Add(new MfaChallengeToken
        {
            UserId = user.Id,
            TenantId = tenant.Id,
            TokenHash = Hash($"c04-mfa-{Guid.NewGuid():N}"),
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5)
        });
        await db.SaveChangesAsync();
        return new UserSeed(tenant.Id, tenant.Slug, user.Id, email, rawReset, rawSibling, user.PasswordHash, user.UpdatedAtUtc);
    }

    private async Task<AuthResponse> LoginAsync(UserSeed seed, string password)
    {
        await using var db = _fixture.CreateRetryingDb();
        var result = await BuildAuthService(db).LoginAsync(
            new LoginRequest(seed.Email, password, seed.TenantSlug), Request, CancellationToken.None);
        return result.Tokens ?? throw new InvalidOperationException("Login did not issue tokens.");
    }

    private async Task<bool> IsCurrentAsync(string accessToken)
    {
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(jwt.Claims, "jwt"));
        await using var db = _fixture.CreateRetryingDb();
        return await TenantSessionSecurity.IsCurrentAsync(principal, db, CancellationToken.None);
    }

    private AuthService BuildAuthService(ZayraDbContext db)
    {
        var tokens = new JwtTokenService(Jwt);
        var audit = new AuditService(db);
        return new AuthService(
            db,
            new Pbkdf2PasswordHasher(),
            tokens,
            audit,
            new NoopEmailService(),
            Jwt,
            new MfaService(db, _totp, tokens, audit),
            _totp,
            NullLogger<AuthService>.Instance,
            Configuration);
    }

    private async Task InstallAuditFailureTriggerAsync(Guid userId)
    {
        await using var db = _fixture.CreateRetryingDb();
        await db.Database.ExecuteSqlRawAsync(@"
DROP TRIGGER IF EXISTS trg_auth_c04_fail_credential_audit ON audit_logs;
DROP FUNCTION IF EXISTS auth_c04_fail_credential_audit();
CREATE FUNCTION auth_c04_fail_credential_audit() RETURNS trigger AS $$
BEGIN
    IF NEW.action IN ('auth.password_changed', 'auth.password_reset')
       AND NEW.entity_id = '" + userId + @"' THEN
        RAISE EXCEPTION '" + InjectedFailureMarker + @"';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
CREATE TRIGGER trg_auth_c04_fail_credential_audit
    BEFORE INSERT ON audit_logs
    FOR EACH ROW EXECUTE FUNCTION auth_c04_fail_credential_audit();");
    }

    private async Task DropAuditFailureTriggerAsync()
    {
        await using var db = _fixture.CreateRetryingDb();
        await db.Database.ExecuteSqlRawAsync(@"
DROP TRIGGER IF EXISTS trg_auth_c04_fail_credential_audit ON audit_logs;
DROP FUNCTION IF EXISTS auth_c04_fail_credential_audit();");
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record UserSeed(
        Guid TenantId,
        string TenantSlug,
        Guid UserId,
        string Email,
        string RawReset,
        string RawSiblingReset,
        string PasswordHash,
        DateTime? UpdatedAtUtc);

    private sealed class AsyncStartBarrier
    {
        private readonly int _participants;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public AsyncStartBarrier(int participants) => _participants = participants;

        public Task SignalAndWaitAsync()
        {
            if (Interlocked.Increment(ref _arrived) == _participants)
                _release.TrySetResult();
            return _release.Task;
        }
    }

    private sealed class NoopEmailService : IEmailService
    {
        public Task SendAsync(
            string toAddress,
            string toName,
            string subject,
            string htmlBody,
            IReadOnlyList<EmailAttachment>? attachments = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
