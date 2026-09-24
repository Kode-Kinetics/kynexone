using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
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
/// M1-G2 real-PostgreSQL acceptance evidence for the one-time invitation and MFA credentials.
///
/// Every race in this class runs 20 independent cycles with independently connected DbContexts and
/// an async barrier that releases only after every client has opened and warmed its PostgreSQL
/// connection. The standard races use five clients; PG06 deliberately uses two clients, one per
/// distinct live credential. There are no timing sleeps or retries-to-green in the test harness.
///
/// Proof IDs:
/// - AUTH-C01-PG01 — invitation exactly-once plus transaction rollback on injected audit failure.
/// - AUTH-C02-PG01 — tenant valid-code race has exactly one session winner.
/// - AUTH-C02-PG02 — tenant wrong-code race reaches the exact cap and permanently consumes.
/// - AUTH-C02-PG03 — platform valid-code race has exactly one completion winner.
/// - AUTH-C02-PG04 — platform wrong-code race reaches the exact cap and permanently consumes.
/// - AUTH-C02-PG05 — enrollment has one factor winner; wrong attempts count exactly; no session.
/// - AUTH-C02-PG06 — two distinct enrollment credentials cannot deadlock or enable two factors.
/// - AUTH-C02-PG07 — corrupt cross-tenant auth children fail both factor-enrollment paths closed.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class AuthCredentialConcurrencyPostgresTests
{
    private const int AcceptanceCycles = 20;
    private const int ConcurrentClients = 5;
    private const string InjectedAuditFailureMarker = "AUTH_G2_INJECTED_AUDIT_FAILURE";

    private static readonly RequestContext Request = new("203.0.113.91", "g2-pg-race-tests");
    private static readonly IOptions<JwtOptions> Jwt = Options.Create(new JwtOptions
    {
        Issuer = "Zayra.G2.Postgres.Tests",
        TenantAudience = "kynexone-tenant-g2-tests",
        PlatformAudience = "kynexone-platform-g2-tests",
        SigningKey = "G2_TEST_SIGNING_KEY_WITH_MORE_THAN_64_CHARACTERS_FOR_AUTH_TESTS_2026",
        AccessTokenMinutes = 30,
        RefreshTokenDays = 7
    });
    private static readonly IConfiguration Configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["APP_URL"] = "https://app.example.test"
        })
        .Build();

    private readonly PostgresFixture _fixture;
    private readonly TotpService _totp = new(new EphemeralDataProtectionProvider());

    public AuthCredentialConcurrencyPostgresTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AuthC01Pg01_SameInvitation_FiveClients_HasOneWinnerAndOneAtomicEffect_ForTwentyCycles()
    {
        for (var cycle = 0; cycle < AcceptanceCycles; cycle++)
        {
            var seed = await SeedInvitationAsync(cycle);

            var outcomes = await RaceFiveAsync(async db =>
            {
                await BuildAuthService(db).AcceptInvitationAsync(
                    new AcceptInvitationRequest(seed.RawInvitation, seed.NewPassword, seed.TenantSlug),
                    Request,
                    CancellationToken.None);
                return true;
            });

            Assert.Single(outcomes, x => x.Succeeded);
            Assert.Equal(ConcurrentClients - 1, outcomes.Count(x => x.Error is UnauthorizedAccessException));
            Assert.DoesNotContain(outcomes, x => !x.Succeeded && x.Error is not UnauthorizedAccessException);

            await using var verify = _fixture.CreateRetryingDb();
            var user = await verify.Users.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(x => x.Id == seed.UserId);
            var link = await verify.EmployeeUserAccounts.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(x => x.Id == seed.LinkId);
            var refreshes = await verify.RefreshTokens.AsNoTracking()
                .Where(x => x.UserId == seed.UserId).ToListAsync();
            var reset = await verify.PasswordResetTokens.AsNoTracking()
                .SingleAsync(x => x.Id == seed.ResetId);
            var challenge = await verify.MfaChallengeTokens.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(x => x.Id == seed.ChallengeId);

            Assert.True(new Pbkdf2PasswordHasher().Verify(seed.NewPassword, user.PasswordHash));
            Assert.True(user.IsActive);
            Assert.True(user.IsEmailConfirmed);
            Assert.Equal("Active", user.Status);
            Assert.Equal(AccessModes.EssOnly, user.AccessMode);
            Assert.False(user.MustChangePassword);
            Assert.False(link.RequiresPasswordSetup);
            Assert.Equal("Active", link.Status);
            Assert.Equal(string.Empty, link.InvitationTokenHash);
            Assert.Null(link.InvitationExpiresAtUtc);
            Assert.NotNull(link.InvitationAcceptedAtUtc);
            Assert.Single(refreshes);
            Assert.NotNull(refreshes[0].RevokedAtUtc);
            Assert.NotNull(reset.UsedAtUtc);
            Assert.NotNull(challenge.UsedAtUtc);
            Assert.Empty(await verify.LoginActivities.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.UserId == seed.UserId).ToListAsync());
            Assert.Single(await verify.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.TenantId == seed.TenantId
                    && x.Action == "auth.invitation_accepted"
                    && x.EntityId == seed.UserId.ToString()).ToListAsync());
        }
    }

    [Fact]
    public async Task AuthC01Pg01_InjectedAuditFailure_RollsBackPasswordConsumeAndInvalidation()
    {
        var seed = await SeedInvitationAsync(cycle: -1);
        await InstallInvitationAuditFailureTriggerAsync(seed.UserId);
        Exception? failure;
        try
        {
            await using var actionDb = _fixture.CreateRetryingDb();
            failure = await Record.ExceptionAsync(() => BuildAuthService(actionDb).AcceptInvitationAsync(
                new AcceptInvitationRequest(seed.RawInvitation, seed.NewPassword, seed.TenantSlug),
                Request,
                CancellationToken.None));
        }
        finally
        {
            await DropInvitationAuditFailureTriggerAsync();
        }

        Assert.NotNull(failure);
        Assert.Contains(InjectedAuditFailureMarker, failure!.ToString(), StringComparison.Ordinal);

        await using var verify = _fixture.CreateRetryingDb();
        var user = await verify.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == seed.UserId);
        var link = await verify.EmployeeUserAccounts.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == seed.LinkId);
        var refresh = await verify.RefreshTokens.AsNoTracking().SingleAsync(x => x.Id == seed.RefreshId);
        var reset = await verify.PasswordResetTokens.AsNoTracking().SingleAsync(x => x.Id == seed.ResetId);
        var challenge = await verify.MfaChallengeTokens.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.Id == seed.ChallengeId);

        Assert.Equal(seed.OriginalPasswordHash, user.PasswordHash);
        Assert.Equal(seed.OriginalUserUpdatedAtUtc, user.UpdatedAtUtc);
        Assert.False(user.IsActive);
        Assert.False(user.IsEmailConfirmed);
        Assert.Equal("PendingPasswordSetup", user.Status);
        Assert.Equal(AccessModes.NoLogin, user.AccessMode);
        Assert.Equal(Hash(seed.RawInvitation), link.InvitationTokenHash);
        Assert.True(link.RequiresPasswordSetup);
        Assert.Equal("Invited", link.Status);
        Assert.Null(link.InvitationAcceptedAtUtc);
        Assert.Null(refresh.RevokedAtUtc);
        Assert.Null(reset.UsedAtUtc);
        Assert.Null(challenge.UsedAtUtc);
        Assert.Empty(await verify.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == seed.TenantId
                && x.Action == "auth.invitation_accepted"
                && x.EntityId == seed.UserId.ToString()).ToListAsync());
    }

    [Fact]
    public async Task AuthC02Pg01_TenantValidCode_FiveClients_HasOneSessionWinner_ForTwentyCycles()
    {
        for (var cycle = 0; cycle < AcceptanceCycles; cycle++)
        {
            var seed = await SeedTenantMfaAsync(cycle, enrollment: false);
            var code = CurrentCode(seed.Secret);

            var outcomes = await RaceFiveAsync(db => BuildAuthService(db).CompleteMfaLoginAsync(
                seed.RawChallenge,
                code,
                Request,
                CancellationToken.None));

            Assert.Single(outcomes, x => x.Succeeded
                && !string.IsNullOrWhiteSpace(x.Value?.AccessToken)
                && !string.IsNullOrWhiteSpace(x.Value.RefreshToken));
            Assert.Equal(ConcurrentClients - 1, outcomes.Count(x => x.Error is UnauthorizedAccessException));
            Assert.DoesNotContain(outcomes, x => !x.Succeeded && x.Error is not UnauthorizedAccessException);

            await using var verify = _fixture.CreateRetryingDb();
            var challenge = await verify.MfaChallengeTokens.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(x => x.Id == seed.ChallengeId);
            Assert.NotNull(challenge.UsedAtUtc);
            Assert.Equal(0, challenge.FailedAttempts);
            Assert.Single(await verify.RefreshTokens.AsNoTracking()
                .Where(x => x.UserId == seed.UserId).ToListAsync());
            Assert.Single(await verify.LoginActivities.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.UserId == seed.UserId && x.EventType == LoginEventTypes.LoginSuccess).ToListAsync());
            Assert.Single(await verify.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.TenantId == seed.TenantId
                    && x.UserId == seed.UserId
                    && x.Action == "auth.login").ToListAsync());
        }
    }

    [Fact]
    public async Task AuthC02Pg02_TenantWrongCodes_FiveClients_ReachExactCapAndKillChallenge_ForTwentyCycles()
    {
        for (var cycle = 0; cycle < AcceptanceCycles; cycle++)
        {
            var seed = await SeedTenantMfaAsync(cycle, enrollment: false);
            var wrongCode = DefinitelyWrongCode(seed.Secret);

            var outcomes = await RaceFiveAsync(db => BuildAuthService(db).CompleteMfaLoginAsync(
                seed.RawChallenge,
                wrongCode,
                Request,
                CancellationToken.None));

            Assert.DoesNotContain(outcomes, x => x.Succeeded);
            var unexpectedErrors = outcomes
                .Select((outcome, clientIndex) => (outcome, clientIndex))
                .Where(x => x.outcome.Error is not UnauthorizedAccessException)
                .Select(x => $"client[{x.clientIndex}] {DescribeException(x.outcome.Error)}")
                .ToArray();
            Assert.True(
                unexpectedErrors.Length == 0,
                "Every wrong-code client must receive the stable unauthorized rejection. "
                + string.Join(Environment.NewLine, unexpectedErrors));
            Assert.Equal(ConcurrentClients, outcomes.Count(x => x.Error is UnauthorizedAccessException));

            await using (var replayDb = _fixture.CreateRetryingDb())
            {
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => BuildAuthService(replayDb).CompleteMfaLoginAsync(
                    seed.RawChallenge,
                    CurrentCode(seed.Secret),
                    Request,
                    CancellationToken.None));
            }

            await using var verify = _fixture.CreateRetryingDb();
            var challenge = await verify.MfaChallengeTokens.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(x => x.Id == seed.ChallengeId);
            var user = await verify.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == seed.UserId);
            Assert.Equal(MfaChallengeToken.MaxAttempts, challenge.FailedAttempts);
            Assert.NotNull(challenge.UsedAtUtc);
            Assert.Equal(MfaChallengeToken.MaxAttempts, user.MfaFailedCount);
            Assert.Empty(await verify.RefreshTokens.AsNoTracking()
                .Where(x => x.UserId == seed.UserId).ToListAsync());
            Assert.Equal(MfaChallengeToken.MaxAttempts, await verify.LoginActivities.IgnoreQueryFilters().AsNoTracking()
                .CountAsync(x => x.UserId == seed.UserId
                    && x.EventType == LoginEventTypes.LoginFailed
                    && x.FailureReason == "mfa_code_mismatch"));
            Assert.Equal(MfaChallengeToken.MaxAttempts, await verify.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .CountAsync(x => x.TenantId == seed.TenantId
                    && x.UserId == seed.UserId
                    && x.Action == "auth.mfa_failed"));
        }
    }

    [Fact]
    public async Task AuthC02Pg03_PlatformValidCode_FiveClients_HasOneWinner_ForTwentyCycles()
    {
        for (var cycle = 0; cycle < AcceptanceCycles; cycle++)
        {
            var seed = await SeedPlatformMfaAsync(cycle);

            var outcomes = await RaceFiveAsync(db => BuildMfaService(db).CompletePlatformChallengeAsync(
                seed.RawChallenge,
                CurrentCode(seed.Secret),
                Request,
                CancellationToken.None));

            Assert.Single(outcomes, x => x.Succeeded && x.Value is not null);
            Assert.Equal(ConcurrentClients - 1, outcomes.Count(x => x.Succeeded && x.Value is null));
            Assert.DoesNotContain(outcomes, x => !x.Succeeded);

            await using var verify = _fixture.CreateRetryingDb();
            var challenge = await verify.MfaChallengeTokens.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(x => x.Id == seed.ChallengeId);
            Assert.NotNull(challenge.UsedAtUtc);
            Assert.Equal(0, challenge.FailedAttempts);
            Assert.Single(await verify.LoginActivities.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.UserId == seed.PlatformUserId
                    && x.EventType == LoginEventTypes.PlatformLoginSuccess).ToListAsync());
            Assert.Single(await verify.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.TenantId == null
                    && x.Action == "platform.auth.mfa_login"
                    && x.EntityId == seed.PlatformUserId.ToString()).ToListAsync());
        }
    }

    [Fact]
    public async Task AuthC02Pg04_PlatformWrongCodes_FiveClients_ReachExactCapAndKillChallenge_ForTwentyCycles()
    {
        for (var cycle = 0; cycle < AcceptanceCycles; cycle++)
        {
            var seed = await SeedPlatformMfaAsync(cycle);
            var wrongCode = DefinitelyWrongCode(seed.Secret);

            var outcomes = await RaceFiveAsync(db => BuildMfaService(db).CompletePlatformChallengeAsync(
                seed.RawChallenge,
                wrongCode,
                Request,
                CancellationToken.None));

            Assert.Equal(ConcurrentClients, outcomes.Count(x => x.Succeeded && x.Value is null));
            Assert.DoesNotContain(outcomes, x => !x.Succeeded);

            await using (var replayDb = _fixture.CreateRetryingDb())
            {
                var replay = await BuildMfaService(replayDb).CompletePlatformChallengeAsync(
                    seed.RawChallenge,
                    CurrentCode(seed.Secret),
                    Request,
                    CancellationToken.None);
                Assert.Null(replay);
            }

            await using var verify = _fixture.CreateRetryingDb();
            var challenge = await verify.MfaChallengeTokens.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(x => x.Id == seed.ChallengeId);
            Assert.Equal(MfaChallengeToken.MaxAttempts, challenge.FailedAttempts);
            Assert.NotNull(challenge.UsedAtUtc);
            Assert.Equal(MfaChallengeToken.MaxAttempts, await verify.LoginActivities.IgnoreQueryFilters().AsNoTracking()
                .CountAsync(x => x.UserId == seed.PlatformUserId
                    && x.EventType == LoginEventTypes.PlatformLoginFailed
                    && x.FailureReason == "mfa_code_mismatch"));
            Assert.Equal(MfaChallengeToken.MaxAttempts, await verify.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .CountAsync(x => x.TenantId == null
                    && x.Action == "platform.auth.mfa_failed"
                    && x.EntityName == "MfaChallengeToken"
                    && x.EntityId == seed.ChallengeId.ToString()));
        }
    }

    [Fact]
    public async Task AuthC02Pg05_EnrollmentValidCode_FiveClients_HasOneFactorWinnerAndNoSession_ForTwentyCycles()
    {
        for (var cycle = 0; cycle < AcceptanceCycles; cycle++)
        {
            var seed = await SeedTenantMfaAsync(cycle, enrollment: true);
            var competingSecrets = Enumerable.Range(0, ConcurrentClients)
                .Select(index => index == 0 ? seed.Secret : _totp.GenerateBase32Secret())
                .ToArray();

            var outcomes = await RaceFiveAsync(async (clientIndex, db) =>
            {
                var secret = competingSecrets[clientIndex];
                var accepted = await BuildMfaService(db).VerifyEnrollmentSetupAsync(
                    seed.RawChallenge,
                    new MfaVerifySetupRequest(secret, CurrentCode(secret)),
                    CancellationToken.None);
                return new EnrollmentAttempt(secret, accepted);
            });

            var winner = Assert.Single(outcomes, x => x.Succeeded && x.Value?.Accepted == true);
            Assert.Equal(ConcurrentClients - 1, outcomes.Count(x => x.Succeeded && x.Value?.Accepted == false));
            Assert.DoesNotContain(outcomes, x => !x.Succeeded);

            await using var verify = _fixture.CreateRetryingDb();
            var user = await verify.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == seed.UserId);
            var challenge = await verify.MfaChallengeTokens.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(x => x.Id == seed.ChallengeId);
            Assert.True(user.MFAEnabled);
            Assert.False(string.IsNullOrWhiteSpace(user.MfaSecretEncrypted));
            Assert.Equal(winner.Value!.Secret, _totp.DecryptSecret(user.MfaSecretEncrypted));
            Assert.NotNull(challenge.UsedAtUtc);
            Assert.Equal(0, challenge.FailedAttempts);
            Assert.Empty(await verify.RefreshTokens.AsNoTracking()
                .Where(x => x.UserId == seed.UserId).ToListAsync());
            Assert.Empty(await verify.LoginActivities.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.UserId == seed.UserId && x.EventType == LoginEventTypes.LoginSuccess).ToListAsync());
            Assert.Single(await verify.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.TenantId == seed.TenantId
                    && x.UserId == seed.UserId
                    && x.Action == "auth.mfa.enabled").ToListAsync());
        }
    }

    [Fact]
    public async Task AuthC02Pg05_EnrollmentWrongCodes_FiveClients_CountExactlyAndCannotOverwrite_ForTwentyCycles()
    {
        for (var cycle = 0; cycle < AcceptanceCycles; cycle++)
        {
            var seed = await SeedTenantMfaAsync(cycle, enrollment: true);
            var wrongRequest = new MfaVerifySetupRequest(seed.Secret, DefinitelyWrongCode(seed.Secret));

            var outcomes = await RaceFiveAsync(db => BuildMfaService(db).VerifyEnrollmentSetupAsync(
                seed.RawChallenge,
                wrongRequest,
                CancellationToken.None));

            Assert.Equal(ConcurrentClients, outcomes.Count(x => x.Succeeded && !x.Value));
            Assert.DoesNotContain(outcomes, x => !x.Succeeded);

            await using (var replayDb = _fixture.CreateRetryingDb())
            {
                var laterCorrect = await BuildMfaService(replayDb).VerifyEnrollmentSetupAsync(
                    seed.RawChallenge,
                    new MfaVerifySetupRequest(seed.Secret, CurrentCode(seed.Secret)),
                    CancellationToken.None);
                Assert.False(laterCorrect);
            }

            await using var verify = _fixture.CreateRetryingDb();
            var user = await verify.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == seed.UserId);
            var challenge = await verify.MfaChallengeTokens.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(x => x.Id == seed.ChallengeId);
            Assert.False(user.MFAEnabled);
            Assert.Null(user.MfaSecretEncrypted);
            Assert.Equal(MfaChallengeToken.MaxAttempts, user.MfaFailedCount);
            Assert.Equal(MfaChallengeToken.MaxAttempts, challenge.FailedAttempts);
            Assert.NotNull(challenge.UsedAtUtc);
            Assert.Empty(await verify.RefreshTokens.AsNoTracking()
                .Where(x => x.UserId == seed.UserId).ToListAsync());
            Assert.Equal(MfaChallengeToken.MaxAttempts, await verify.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .CountAsync(x => x.TenantId == seed.TenantId
                    && x.UserId == seed.UserId
                    && x.Action == "auth.mfa_enrollment_failed"));
        }
    }

    [Fact]
    public async Task AuthC02Pg06_TwoDistinctEnrollmentChallenges_HaveOneFactorWinner_NoDeadlockAndNoSession_ForTwentyCycles()
    {
        for (var cycle = 0; cycle < AcceptanceCycles; cycle++)
        {
            var seed = await SeedDistinctEnrollmentChallengesAsync(cycle);
            var competingSecrets = new[]
            {
                _totp.GenerateBase32Secret(),
                _totp.GenerateBase32Secret()
            };

            var outcomes = await RaceTwoDistinctEnrollmentChallengesWithoutRetryAsync(
                seed,
                competingSecrets);

            Assert.DoesNotContain(outcomes, x => ContainsPostgresSqlState(x.Error, "40P01"));
            Assert.DoesNotContain(outcomes, x => !x.Succeeded);
            var winner = Assert.Single(outcomes, x => x.Value?.Accepted == true);
            Assert.Single(outcomes, x => x.Value?.Accepted == false);

            // A completed winner must make both independently issued credentials permanently inert.
            for (var challengeIndex = 0; challengeIndex < seed.RawChallenges.Count; challengeIndex++)
            {
                await using var replayDb = CreateNonRetryingDb();
                var replayAccepted = await BuildMfaService(replayDb).VerifyEnrollmentSetupAsync(
                    seed.RawChallenges[challengeIndex],
                    new MfaVerifySetupRequest(
                        competingSecrets[challengeIndex],
                        CurrentCode(competingSecrets[challengeIndex])),
                    CancellationToken.None);
                Assert.False(replayAccepted);
            }

            await using var verify = _fixture.CreateRetryingDb();
            var user = await verify.Users.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(x => x.Id == seed.UserId);
            var challenges = await verify.MfaChallengeTokens.IgnoreQueryFilters().AsNoTracking()
                .Where(x => seed.ChallengeIds.Contains(x.Id))
                .OrderBy(x => x.Id)
                .ToListAsync();

            Assert.True(user.MFAEnabled);
            Assert.False(string.IsNullOrWhiteSpace(user.MfaSecretEncrypted));
            Assert.Equal(winner.Value!.Secret, _totp.DecryptSecret(user.MfaSecretEncrypted));
            Assert.Equal(2, challenges.Count);
            Assert.All(challenges, challenge =>
            {
                Assert.NotNull(challenge.UsedAtUtc);
                Assert.Equal(0, challenge.FailedAttempts);
            });
            Assert.Empty(await verify.RefreshTokens.AsNoTracking()
                .Where(x => x.UserId == seed.UserId).ToListAsync());
            Assert.Empty(await verify.LoginActivities.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.UserId == seed.UserId && x.EventType == LoginEventTypes.LoginSuccess)
                .ToListAsync());
            Assert.Empty(await verify.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.TenantId == seed.TenantId
                    && x.UserId == seed.UserId
                    && x.Action == "auth.login")
                .ToListAsync());
            Assert.Single(await verify.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.TenantId == seed.TenantId
                    && x.UserId == seed.UserId
                    && x.Action == "auth.mfa.enabled")
                .ToListAsync());
        }
    }

    [Fact]
    public async Task AuthC02Pg07_CrossTenantPermissionOverride_CannotEnableFactorThroughEitherSetupPath()
    {
        var seed = await SeedTenantMfaAsync(cycle: -700, enrollment: true);
        string originalStamp;
        DateTime? originalUpdatedAtUtc;

        await using (var corruptDb = _fixture.CreateRetryingDb())
        {
            var foreignTenant = new Tenant
            {
                Name = $"G2 Foreign MFA {Guid.NewGuid():N}",
                Slug = $"g2-foreign-mfa-{Guid.NewGuid():N}"
            };
            corruptDb.Tenants.Add(foreignTenant);
            corruptDb.UserPermissionOverrides.Add(new UserPermissionOverride
            {
                TenantId = foreignTenant.Id,
                UserId = seed.UserId,
                PermissionKey = "security.manage",
                Effect = "Allow",
                Reason = "deliberate cross-tenant corruption regression"
            });
            await corruptDb.SaveChangesAsync();

            var originalUser = await corruptDb.Users.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(x => x.Id == seed.UserId);
            originalStamp = TenantSessionSecurity.StampValue(originalUser);
            originalUpdatedAtUtc = originalUser.UpdatedAtUtc;
        }

        var authenticatedSecret = _totp.GenerateBase32Secret();
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, seed.UserId.ToString()),
                new Claim("tenant_id", seed.TenantId.ToString())
            }, authenticationType: "g2-mfa-graph-integrity"))
        };
        await using (var authenticatedDb = _fixture.CreateDbWithAccessor(
                         new HttpContextAccessor { HttpContext = httpContext }))
        {
            var accepted = await BuildMfaService(authenticatedDb).VerifySetupAsync(
                seed.UserId,
                seed.TenantId,
                new MfaVerifySetupRequest(authenticatedSecret, CurrentCode(authenticatedSecret)),
                CancellationToken.None);
            Assert.False(accepted);
        }

        await using (var enrollmentDb = _fixture.CreateRetryingDb())
        {
            var accepted = await BuildMfaService(enrollmentDb).VerifyEnrollmentSetupAsync(
                seed.RawChallenge,
                new MfaVerifySetupRequest(seed.Secret, CurrentCode(seed.Secret)),
                CancellationToken.None);
            Assert.False(accepted);
        }

        await using var verify = _fixture.CreateRetryingDb();
        var user = await verify.Users.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.Id == seed.UserId);
        var challenge = await verify.MfaChallengeTokens.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.Id == seed.ChallengeId);
        Assert.False(user.MFAEnabled);
        Assert.Null(user.MfaSecretEncrypted);
        Assert.Null(user.MfaConfiguredAtUtc);
        Assert.Equal(0, user.MfaFailedCount);
        Assert.Equal(originalUpdatedAtUtc, user.UpdatedAtUtc);
        Assert.Equal(originalStamp, TenantSessionSecurity.StampValue(user));
        Assert.Null(challenge.UsedAtUtc);
        Assert.Equal(0, challenge.FailedAttempts);
        Assert.Empty(await verify.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == seed.TenantId
                && x.UserId == seed.UserId
                && x.Action == "auth.mfa.enabled")
            .ToListAsync());
    }

    private async Task<InvitationSeed> SeedInvitationAsync(int cycle)
    {
        await using var db = _fixture.CreateRetryingDb();
        var suffix = $"{cycle}-{Guid.NewGuid():N}";
        var tenant = new Tenant
        {
            Name = $"G2 Invitation {suffix}",
            Slug = $"g2-invite-{suffix}"
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var hasher = new Pbkdf2PasswordHasher();
        var user = new User
        {
            TenantId = tenant.Id,
            Email = $"invite-{suffix}@example.test",
            NormalizedEmail = AuthService.Normalize($"invite-{suffix}@example.test"),
            FullName = "G2 Invitation User",
            PasswordHash = hasher.Hash("UnreachableOriginal1!"),
            Status = "PendingPasswordSetup",
            AccessMode = AccessModes.NoLogin,
            IsActive = false,
            IsEmailConfirmed = false
        };
        var employee = new Employee
        {
            TenantId = tenant.Id,
            UserAccountId = user.Id,
            EmployeeCode = $"G2-I-{Guid.NewGuid():N}",
            FullName = user.FullName,
            WorkEmail = user.Email,
            Status = EmployeeStatuses.Invited,
            JoiningDate = DateTime.UtcNow.AddDays(-1)
        };
        db.AddRange(user, employee);
        await db.SaveChangesAsync();

        var rawInvitation = $"g2-invitation-{Guid.NewGuid():N}-{Guid.NewGuid():N}";
        var link = new EmployeeUserAccount
        {
            TenantId = tenant.Id,
            EmployeeId = employee.Id,
            UserId = user.Id,
            AccessMode = AccessModes.EssOnly,
            IsPrimary = true,
            Status = "Invited",
            RequiresPasswordSetup = true,
            InvitationTokenHash = Hash(rawInvitation),
            InvitationExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            InvitedAtUtc = DateTime.UtcNow
        };
        var refresh = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = Hash($"old-refresh-{suffix}"),
            ExpiresAtUtc = DateTime.UtcNow.AddDays(1),
            CreatedByIp = "203.0.113.90"
        };
        var reset = new PasswordResetToken
        {
            UserId = user.Id,
            TokenHash = Hash($"old-reset-{suffix}"),
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            CreatedByIp = "203.0.113.90"
        };
        var challenge = new MfaChallengeToken
        {
            UserId = user.Id,
            TenantId = tenant.Id,
            TokenHash = Hash($"old-mfa-{suffix}"),
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5),
            CreatedByIp = "203.0.113.90"
        };
        db.AddRange(link, refresh, reset, challenge);
        await db.SaveChangesAsync();

        return new InvitationSeed(
            tenant.Id,
            tenant.Slug,
            user.Id,
            link.Id,
            refresh.Id,
            reset.Id,
            challenge.Id,
            rawInvitation,
            $"G2NewPassword-{Guid.NewGuid():N}!aA1",
            user.PasswordHash,
            // The STORED stamp, not the tracked entity's. PostgreSQL holds microseconds while a
            // DateTime carries 100ns ticks, so on a Linux clock a "nothing was touched" assertion
            // fails by a fraction of a microsecond; a macOS clock, microsecond-granular, never
            // shows it. The rollback assertion still fails for real if the row is written.
            await StoredUpdatedAtUtcAsync(user.Id));
    }

    private async Task<DateTime?> StoredUpdatedAtUtcAsync(Guid userId)
    {
        await using var read = _fixture.CreateRetryingDb();
        return await read.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Id == userId).Select(x => x.UpdatedAtUtc).SingleAsync();
    }

    private async Task<TenantMfaSeed> SeedTenantMfaAsync(int cycle, bool enrollment)
    {
        await using var db = _fixture.CreateRetryingDb();
        var suffix = $"{(enrollment ? "enroll" : "login")}-{cycle}-{Guid.NewGuid():N}";
        var tenant = new Tenant
        {
            Name = $"G2 MFA {suffix}",
            Slug = $"g2-mfa-{suffix}"
        };
        db.Tenants.Add(tenant);
        db.SecuritySettings.Add(new SecuritySetting
        {
            TenantId = tenant.Id,
            MfaRequired = true,
            AllowMultipleSessions = true
        });
        await db.SaveChangesAsync();

        var secret = _totp.GenerateBase32Secret();
        var user = new User
        {
            TenantId = tenant.Id,
            Email = $"{suffix}@example.test",
            NormalizedEmail = AuthService.Normalize($"{suffix}@example.test"),
            FullName = "G2 MFA User",
            PasswordHash = new Pbkdf2PasswordHasher().Hash("MfaPassword1!"),
            Status = "Active",
            AccessMode = AccessModes.FullPortal,
            IsActive = true,
            IsEmailConfirmed = true,
            MFAEnabled = !enrollment,
            MfaSecretEncrypted = enrollment ? null : _totp.EncryptSecret(secret),
            MfaConfiguredAtUtc = enrollment ? null : DateTime.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var mfa = BuildMfaService(db);
        var rawChallenge = enrollment
            ? await mfa.CreateEnrollmentChallengeAsync(user.Id, tenant.Id, Request.IpAddress!, CancellationToken.None)
            : await mfa.CreateChallengeAsync(user.Id, tenant.Id, Request.IpAddress!, CancellationToken.None);
        var challengeId = await db.MfaChallengeTokens.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.UserId == user.Id)
            .Select(x => x.Id)
            .SingleAsync();
        return new TenantMfaSeed(tenant.Id, user.Id, challengeId, rawChallenge, secret);
    }

    private async Task<DistinctEnrollmentSeed> SeedDistinctEnrollmentChallengesAsync(int cycle)
    {
        var first = await SeedTenantMfaAsync(cycle, enrollment: true);
        await using var db = _fixture.CreateRetryingDb();
        var secondRawChallenge = await BuildMfaService(db).CreateEnrollmentChallengeAsync(
            first.UserId,
            first.TenantId,
            Request.IpAddress!,
            CancellationToken.None);
        if (!AuthChallengeTokenCodec.TryParse(
                secondRawChallenge,
                AuthChallengeTokenCodec.TenantEnrollmentPurpose,
                out var secondEnvelope))
            throw new InvalidOperationException("The second enrollment challenge was not parseable.");

        var challengeIds = new[] { first.ChallengeId, secondEnvelope.ChallengeId };
        var liveChallenges = await db.MfaChallengeTokens.IgnoreQueryFilters().AsNoTracking()
            .Where(x => challengeIds.Contains(x.Id))
            .ToListAsync();
        if (liveChallenges.Count != 2
            || liveChallenges.Any(x => x.UsedAtUtc is not null || x.ExpiresAtUtc <= DateTime.UtcNow)
            || liveChallenges.Select(x => x.TokenHash).Distinct(StringComparer.Ordinal).Count() != 2)
            throw new InvalidOperationException("The distinct enrollment race requires two different live credentials.");

        return new DistinctEnrollmentSeed(
            first.TenantId,
            first.UserId,
            challengeIds,
            new[] { first.RawChallenge, secondRawChallenge });
    }

    private async Task<PlatformMfaSeed> SeedPlatformMfaAsync(int cycle)
    {
        await using var db = _fixture.CreateRetryingDb();
        var suffix = $"{cycle}-{Guid.NewGuid():N}";
        var secret = _totp.GenerateBase32Secret();
        var user = new PlatformUser
        {
            Email = $"g2-platform-{suffix}@example.test",
            FullName = "G2 Platform MFA User",
            PasswordHash = new Pbkdf2PasswordHasher().Hash("PlatformPassword1!"),
            Role = PlatformRoles.Admin,
            IsActive = true,
            MfaEnabled = true,
            MfaSecretEncrypted = _totp.EncryptSecret(secret),
            MfaConfiguredAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        db.PlatformUsers.Add(user);
        await db.SaveChangesAsync();

        var rawChallenge = await BuildMfaService(db).CreatePlatformChallengeAsync(
            user.Id,
            Request.IpAddress!,
            CancellationToken.None);
        var challengeId = await db.MfaChallengeTokens.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.PlatformUserId == user.Id)
            .Select(x => x.Id)
            .SingleAsync();
        return new PlatformMfaSeed(user.Id, challengeId, rawChallenge, secret);
    }

    private Task<IReadOnlyList<RaceOutcome<T>>> RaceFiveAsync<T>(
        Func<ZayraDbContext, Task<T>> operation) =>
        RaceFiveAsync((_, db) => operation(db));

    private async Task<IReadOnlyList<RaceOutcome<T>>> RaceFiveAsync<T>(
        Func<int, ZayraDbContext, Task<T>> operation)
    {
        var barrier = new AsyncStartBarrier(ConcurrentClients);
        var clients = Enumerable.Range(0, ConcurrentClients).Select(async clientIndex =>
        {
            await using var db = _fixture.CreateRetryingDb();
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("SELECT 1");
            await barrier.SignalAndWaitAsync();
            try
            {
                return RaceOutcome<T>.Success(await operation(clientIndex, db));
            }
            catch (Exception error)
            {
                return RaceOutcome<T>.Failure(error);
            }
        }).ToArray();
        return await Task.WhenAll(clients);
    }

    private async Task<IReadOnlyList<RaceOutcome<EnrollmentAttempt>>> RaceTwoDistinctEnrollmentChallengesWithoutRetryAsync(
        DistinctEnrollmentSeed seed,
        IReadOnlyList<string> competingSecrets)
    {
        const int participants = 2;
        if (seed.RawChallenges.Count != participants || competingSecrets.Count != participants)
            throw new ArgumentException("The distinct challenge race requires exactly two clients.");

        var barrier = new AsyncStartBarrier(participants);
        var clients = Enumerable.Range(0, participants).Select(async clientIndex =>
        {
            // Deliberately omit EnableRetryOnFailure. A PostgreSQL 40P01 must reach the assertion;
            // the regression is not allowed to pass because an execution strategy retried it.
            await using var db = CreateNonRetryingDb();
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("SELECT 1");
            await barrier.SignalAndWaitAsync();
            try
            {
                var secret = competingSecrets[clientIndex];
                var accepted = await BuildMfaService(db).VerifyEnrollmentSetupAsync(
                    seed.RawChallenges[clientIndex],
                    new MfaVerifySetupRequest(secret, CurrentCode(secret)),
                    CancellationToken.None);
                return RaceOutcome<EnrollmentAttempt>.Success(new EnrollmentAttempt(secret, accepted));
            }
            catch (Exception error)
            {
                return RaceOutcome<EnrollmentAttempt>.Failure(error);
            }
        }).ToArray();
        return await Task.WhenAll(clients);
    }

    private ZayraDbContext CreateNonRetryingDb() => new(
        new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance)
            .Options);

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

    private MfaService BuildMfaService(ZayraDbContext db)
    {
        var tokens = new JwtTokenService(Jwt);
        return new MfaService(db, _totp, tokens, new AuditService(db));
    }

    private async Task InstallInvitationAuditFailureTriggerAsync(Guid userId)
    {
        await using var db = _fixture.CreateRetryingDb();
        await db.Database.ExecuteSqlRawAsync(@"
DROP TRIGGER IF EXISTS trg_auth_g2_fail_invitation_audit ON audit_logs;
DROP FUNCTION IF EXISTS auth_g2_fail_invitation_audit();
CREATE FUNCTION auth_g2_fail_invitation_audit() RETURNS trigger AS $$
BEGIN
    IF NEW.action = 'auth.invitation_accepted'
       AND NEW.entity_id = '" + userId + @"' THEN
        RAISE EXCEPTION '" + InjectedAuditFailureMarker + @"';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
CREATE TRIGGER trg_auth_g2_fail_invitation_audit
    BEFORE INSERT ON audit_logs
    FOR EACH ROW EXECUTE FUNCTION auth_g2_fail_invitation_audit();");
    }

    private async Task DropInvitationAuditFailureTriggerAsync()
    {
        await using var db = _fixture.CreateRetryingDb();
        await db.Database.ExecuteSqlRawAsync(@"
DROP TRIGGER IF EXISTS trg_auth_g2_fail_invitation_audit ON audit_logs;
DROP FUNCTION IF EXISTS auth_g2_fail_invitation_audit();");
    }

    private static string CurrentCode(string secret) =>
        ComputeTotp(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30).ToString("D6");

    private static string DefinitelyWrongCode(string secret)
    {
        var currentStep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var acceptedWindow = new HashSet<int>
        {
            ComputeTotp(secret, currentStep - 1),
            ComputeTotp(secret, currentStep),
            ComputeTotp(secret, currentStep + 1)
        };
        for (var candidate = 0; candidate < 1_000_000; candidate++)
            if (!acceptedWindow.Contains(candidate))
                return candidate.ToString("D6");
        throw new InvalidOperationException("Unable to select a code outside the TOTP acceptance window.");
    }

    private static int ComputeTotp(string base32Secret, long timeStep)
    {
        var key = FromBase32(base32Secret);
        var counter = BitConverter.GetBytes(timeStep);
        if (BitConverter.IsLittleEndian) Array.Reverse(counter);
        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(counter);
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24)
                   | ((hash[offset + 1] & 0xff) << 16)
                   | ((hash[offset + 2] & 0xff) << 8)
                   | (hash[offset + 3] & 0xff);
        return binary % 1_000_000;
    }

    private static byte[] FromBase32(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var normalized = input.TrimEnd('=').ToUpperInvariant();
        var output = new byte[normalized.Length * 5 / 8];
        var buffer = 0;
        var bitsLeft = 0;
        var index = 0;
        foreach (var character in normalized)
        {
            var value = alphabet.IndexOf(character);
            if (value < 0) throw new FormatException($"Invalid base32 character '{character}'.");
            buffer = (buffer << 5) | value;
            bitsLeft += 5;
            if (bitsLeft < 8) continue;
            bitsLeft -= 8;
            output[index++] = (byte)((buffer >> bitsLeft) & 0xff);
        }
        return output;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool ContainsPostgresSqlState(Exception? error, string sqlState)
    {
        while (error is not null)
        {
            if (error is Npgsql.PostgresException postgres && postgres.SqlState == sqlState)
                return true;
            error = error.InnerException;
        }
        return false;
    }

    private static string DescribeException(Exception? error)
    {
        if (error is null) return "<no exception>";
        var parts = new List<string>();
        while (error is not null)
        {
            var sqlState = error is Npgsql.PostgresException postgres
                ? $" SQLSTATE={postgres.SqlState}"
                : string.Empty;
            parts.Add($"{error.GetType().FullName}{sqlState}: {error.Message}");
            error = error.InnerException;
        }
        return string.Join(" --> ", parts);
    }

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

    private sealed record RaceOutcome<T>(bool Succeeded, T? Value, Exception? Error)
    {
        public static RaceOutcome<T> Success(T value) => new(true, value, null);
        public static RaceOutcome<T> Failure(Exception error) => new(false, default, error);
    }

    private sealed record InvitationSeed(
        Guid TenantId,
        string TenantSlug,
        Guid UserId,
        Guid LinkId,
        Guid RefreshId,
        Guid ResetId,
        Guid ChallengeId,
        string RawInvitation,
        string NewPassword,
        string OriginalPasswordHash,
        DateTime? OriginalUserUpdatedAtUtc);

    private sealed record TenantMfaSeed(
        Guid TenantId,
        Guid UserId,
        Guid ChallengeId,
        string RawChallenge,
        string Secret);

    private sealed record PlatformMfaSeed(
        Guid PlatformUserId,
        Guid ChallengeId,
        string RawChallenge,
        string Secret);

    private sealed record DistinctEnrollmentSeed(
        Guid TenantId,
        Guid UserId,
        IReadOnlyList<Guid> ChallengeIds,
        IReadOnlyList<string> RawChallenges);

    private sealed record EnrollmentAttempt(string Secret, bool Accepted);

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
