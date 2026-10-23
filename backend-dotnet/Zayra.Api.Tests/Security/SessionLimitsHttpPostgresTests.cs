using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// The hard session limit (security_settings.session_max_hours) and the session epoch (users.session_epoch), through
/// the real Program.cs pipeline over a real PostgreSQL built by every EF migration. Every session is started by a real
/// sign-in and ended (or kept) through the real endpoints; only the clock and changes no endpoint can make (a session
/// that started hours ago, a reactivation) are written to the database directly.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SessionLimitsHttpPostgresTests : IClassFixture<SessionLimitsHostFixture>
{
    private const string Password = SessionLimitsHostFixture.Password;
    private const string NewPassword = "Another-Horse-Battery-7!";
    private readonly SessionLimitsHostFixture _fx;
    public SessionLimitsHttpPostgresTests(SessionLimitsHostFixture fx) => _fx = fx;

    // ── A. Hard session limit ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Refresh_PastTheMaximumLength_Is401SessionExpired_RevokesTheFamily_AndIsAudited()
    {
        var (slug, _) = await _fx.SeedTenantAsync("cap");
        var (email, userId) = await _fx.SeedUserAsync(slug, "cap");
        var session = await LoginAsync(email, slug);
        await StartSessionHoursAgoAsync(session.Refresh, 13);

        var refreshed = await RefreshAsync(session.Refresh);

        Assert.Equal(HttpStatusCode.Unauthorized, refreshed.StatusCode);
        var body = await Json(refreshed);
        Assert.Equal("ERR_SESSION_EXPIRED", body.GetProperty("code").GetString());
        Assert.Equal("Your session has ended. Sign in again.", body.GetProperty("message").GetString());
        await AssertFamilyRevokedAsync(userId, session.Refresh);
        await using var db = _fx.CreateDb();
        var audit = await db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(a => a.Action == "auth.session_expired" && a.UserId == userId);
        Assert.Contains("\"sessionMaxHours\":12", audit.Metadata);
        // A second presentation of the same (now revoked) token is the generic refusal, not a second expiry.
        var again = await RefreshAsync(session.Refresh);
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
        Assert.False((await Json(again)).TryGetProperty("code", out _));
    }

    [Fact]
    public async Task Refresh_InsideTheWindow_Rotates_AndKeepsTheSessionStartAndEpoch()
    {
        var (slug, _) = await _fx.SeedTenantAsync("inside");
        var (email, userId) = await _fx.SeedUserAsync(slug, "inside");
        var session = await LoginAsync(email, slug);
        await StartSessionHoursAgoAsync(session.Refresh, 11);
        var parent = await TokenAsync(session.Refresh);

        var first = await RefreshAsync(session.Refresh);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var r2 = (await Json(first)).GetProperty("refreshToken").GetString()!;
        var child = await TokenAsync(r2);
        Assert.Equal(parent.FamilyId, child.FamilyId);
        Assert.Equal(parent.SessionStartedAtUtc, child.SessionStartedAtUtc);
        Assert.Equal(parent.SessionEpoch, child.SessionEpoch);
        Assert.True(child.CreatedAtUtc > parent.CreatedAtUtc, "the idle clock restarts at each rotation");

        var second = await RefreshAsync(r2);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var grandchild = await TokenAsync((await Json(second)).GetProperty("refreshToken").GetString()!);
        Assert.Equal(parent.SessionStartedAtUtc, grandchild.SessionStartedAtUtc);
        _ = userId;
    }

    [Fact]
    public async Task LoweringTheMaximum_AppliesToARunningSessionAtItsNextRefresh()
    {
        var (slug, tenantId) = await _fx.SeedTenantAsync("lower");
        var (email, _) = await _fx.SeedUserAsync(slug, "lower");
        var admin = await AdminAsync(slug, tenantId);
        var session = await LoginAsync(email, slug);
        await StartSessionHoursAgoAsync(session.Refresh, 5);

        var stillInside = await RefreshAsync(session.Refresh);
        Assert.Equal(HttpStatusCode.OK, stillInside.StatusCode);
        var r2 = (await Json(stillInside)).GetProperty("refreshToken").GetString()!;

        var put = await SendAsync(HttpMethod.Put, "/api/access/security-settings", new { sessionMaxHours = 4 }, admin);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(4, (await Json(put)).GetProperty("sessionMaxHours").GetInt32());

        var refused = await RefreshAsync(r2);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal("ERR_SESSION_EXPIRED", (await Json(refused)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task SessionMaxHours_IsExposed_AndOutsideOneTo168_Is400()
    {
        var (slug, tenantId) = await _fx.SeedTenantAsync("range");
        var admin = await AdminAsync(slug, tenantId);

        var get = await SendAsync(HttpMethod.Get, "/api/access/security-settings", null, admin);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(12, (await Json(get)).GetProperty("sessionMaxHours").GetInt32());

        foreach (var bad in new[] { 0, -1, 169, 10_000 })
        {
            var refused = await SendAsync(HttpMethod.Put, "/api/access/security-settings", new { sessionMaxHours = bad }, admin);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("ERR_SESSION_MAX_HOURS_RANGE", (await Json(refused)).GetProperty("code").GetString());
        }
        foreach (var good in new[] { 1, 24, 168 })
        {
            var ok = await SendAsync(HttpMethod.Put, "/api/access/security-settings", new { sessionMaxHours = good }, admin);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Equal(good, (await Json(ok)).GetProperty("sessionMaxHours").GetInt32());
        }
        // The database refuses an out-of-range value whatever path writes it.
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            _fx.ExecAsync($"UPDATE security_settings SET session_max_hours = 169 WHERE tenant_id = '{tenantId}'"));
        Assert.Equal("23514", ex.SqlState);
    }

    // ── B. Session epoch: every change that must end the user's sessions ─────────────────────

    [Fact]
    public async Task PasswordChange_EndsEarlierSessions()
    {
        var (slug, _) = await _fx.SeedTenantAsync("pwchange");
        var (email, userId) = await _fx.SeedUserAsync(slug, "pwchange");
        var other = await LoginAsync(email, slug);
        var self = await LoginAsync(email, slug);
        var before = await StateAsync(userId);

        var change = await SendAsync(HttpMethod.Post, "/api/auth/change-password",
            new { currentPassword = Password, newPassword = NewPassword }, self.Access);
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        await AssertSessionsEndedAsync(userId, other.Refresh, before, "password_changed");
    }

    [Fact]
    public async Task PasswordReset_EndsEarlierSessions()
    {
        var (slug, _) = await _fx.SeedTenantAsync("pwreset");
        var (email, userId) = await _fx.SeedUserAsync(slug, "pwreset");
        var session = await LoginAsync(email, slug);
        var before = await StateAsync(userId);
        const string rawReset = "reset-token-for-the-session-epoch-test-0001";
        await using (var db = _fx.CreateDb())
        {
            db.PasswordResetTokens.Add(new PasswordResetToken
            {
                UserId = userId, TokenHash = _fx.Hash(rawReset), ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            });
            await db.SaveChangesAsync();
        }

        var reset = await SendAsync(HttpMethod.Post, "/api/auth/reset-password",
            new { resetToken = rawReset, newPassword = NewPassword, tenantSlug = slug }, null);
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        await AssertSessionsEndedAsync(userId, session.Refresh, before, null);
    }

    [Fact]
    public async Task MfaDisable_EndsEarlierSessions()
    {
        var (slug, _) = await _fx.SeedTenantAsync("mfaoff");
        var (email, userId) = await _fx.SeedUserAsync(slug, "mfaoff");
        var enrolled = await EnrollAsync(await LoginAsync(email, slug));
        var before = await StateAsync(userId);

        // A later code than setup used (replay protection). Turning two-step off ends every session, the caller's too.
        var disable = await SendAsync(HttpMethod.Post, "/api/auth/mfa/disable",
            new { totpCode = Totp(enrolled.Secret, 1) }, enrolled.Session.Access);
        Assert.Equal(HttpStatusCode.NoContent, disable.StatusCode);

        await AssertSessionsEndedAsync(userId, enrolled.Session.Refresh, before, "mfa_disabled");
    }

    [Fact]
    public async Task AdminMfaReset_EndsTheUsersSessions()
    {
        var (slug, tenantId) = await _fx.SeedTenantAsync("mfareset");
        var (email, userId) = await _fx.SeedUserAsync(slug, "mfareset");
        var admin = await AdminAsync(slug, tenantId);
        var enrolled = await EnrollAsync(await LoginAsync(email, slug));
        var before = await StateAsync(userId);

        var reset = await SendAsync(HttpMethod.Post, $"/api/access/users/{userId}/mfa/reset", new { reason = "Lost phone" }, admin);
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        await AssertSessionsEndedAsync(userId, enrolled.Session.Refresh, before, null);
    }

    [Fact]
    public async Task Deactivate_EndsTheUsersSessions()
    {
        var (slug, tenantId) = await _fx.SeedTenantAsync("deact");
        var (email, userId) = await _fx.SeedUserAsync(slug, "deact");
        var admin = await AdminAsync(slug, tenantId);
        var session = await LoginAsync(email, slug);
        var before = await StateAsync(userId);

        var delete = await SendAsync(HttpMethod.Delete, $"/api/access/users/{userId}", null, admin);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        await AssertSessionsEndedAsync(userId, session.Refresh, before, null);
    }

    [Fact]
    public async Task SuspendThenReactivate_DoesNotBringTheEarlierSessionBack()
    {
        var (slug, tenantId) = await _fx.SeedTenantAsync("suspend");
        var (email, userId) = await _fx.SeedUserAsync(slug, "suspend");
        var admin = await AdminAsync(slug, tenantId);
        var session = await LoginAsync(email, slug);
        var before = await StateAsync(userId);

        var suspend = await SendAsync(HttpMethod.Patch, $"/api/access/users/{userId}/suspend", new { reason = "Investigation" }, admin);
        Assert.Equal(HttpStatusCode.NoContent, suspend.StatusCode);
        // The product reactivates a suspended login only through a fresh invitation; put it straight back to Active
        // here so the test asks the sharper question: does the OLD session come back with the account?
        await using (var db = _fx.CreateDb())
        {
            var user = await db.Users.SingleAsync(u => u.Id == userId);
            user.Status = "Active";
            user.IsActive = true;
            await db.SaveChangesAsync();
        }

        await AssertSessionsEndedAsync(userId, session.Refresh, before, "user_deactivated");
        Assert.Equal(HttpStatusCode.OK, (await LoginResponseAsync(email, slug)).StatusCode);
    }

    [Fact]
    public async Task ScimDeprovision_EndsTheUsersSessions()
    {
        var (slug, tenantId) = await _fx.SeedTenantAsync("scim");
        var (email, userId) = await _fx.SeedUserAsync(slug, "scim", scimOwned: true);
        const string scimToken = "scim-token-for-the-session-epoch-test-0001";
        await using (var db = _fx.CreateDb())
        {
            db.TenantIdentityProviderSettings.Add(new TenantIdentityProviderSetting
            {
                TenantId = tenantId, ScimEnabled = true, ScimDryRun = false, ScimTokenHash = _fx.Hash(scimToken),
            });
            await db.SaveChangesAsync();
        }
        var session = await LoginAsync(email, slug);
        var before = await StateAsync(userId);

        var deprovision = await SendAsync(HttpMethod.Delete, $"/api/enterprise-identity/scim/v2/Users/{userId}", null, scimToken);
        Assert.Equal(HttpStatusCode.NoContent, deprovision.StatusCode);

        await AssertSessionsEndedAsync(userId, session.Refresh, before, "user_deactivated");
    }

    // ── B. Negative cases: changes that must NOT end the session ──────────────────────────────

    [Fact]
    public async Task ProfileEdit_KeepsTheSession()
    {
        var (slug, tenantId) = await _fx.SeedTenantAsync("profile");
        var (email, userId) = await _fx.SeedUserAsync(slug, "profile");
        var admin = await AdminAsync(slug, tenantId);
        var session = await LoginAsync(email, slug);
        var before = await StateAsync(userId);

        var edit = await SendAsync(HttpMethod.Put, $"/api/access/users/{userId}", new { fullName = "Renamed Person" }, admin);
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(session.Refresh)).StatusCode);
        await AssertEpochUnchangedAsync(userId, before);
    }

    [Fact]
    public async Task MfaSignInOnASecondDevice_KeepsTheFirstSession()
    {
        var (slug, _) = await _fx.SeedTenantAsync("second");
        var (email, userId) = await _fx.SeedUserAsync(slug, "second");
        var enrolled = await EnrollAsync(await LoginAsync(email, slug));
        var before = await StateAsync(userId);

        await CompleteMfaLoginAsync(email, slug, enrolled.Secret, stepOffset: 1);

        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(enrolled.Session.Refresh)).StatusCode);
        await AssertEpochUnchangedAsync(userId, before);
    }

    [Fact]
    public async Task RoleChange_DoesNotRaiseTheEpoch()
    {
        var (slug, tenantId) = await _fx.SeedTenantAsync("roles");
        var (email, userId) = await _fx.SeedUserAsync(slug, "roles");
        var admin = await AdminAsync(slug, tenantId);
        var session = await LoginAsync(email, slug);
        var before = await StateAsync(userId);

        var assign = await SendAsync(HttpMethod.Put, $"/api/access/users/{userId}/roles", new { roles = new[] { "Employee" } }, admin);
        Assert.Equal(HttpStatusCode.OK, assign.StatusCode);

        await AssertEpochUnchangedAsync(userId, before);
        // A role change keeps its own, older revocation (AccessManagementService.InvalidateAuthorizationSessionsAsync
        // revokes the user's refresh tokens so the next session carries the new permissions). That is unchanged here:
        // the epoch is not the reason, and nothing in this change weakens it.
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(session.Refresh)).StatusCode);
    }

    [Fact]
    public async Task FailedPasswordLockout_AndLoginTelemetry_DoNotRaiseTheEpoch()
    {
        var (slug, _) = await _fx.SeedTenantAsync("lockout");
        var (email, userId) = await _fx.SeedUserAsync(slug, "lockout");
        var session = await LoginAsync(email, slug);
        var before = await StateAsync(userId);

        await using (var db = _fx.CreateDb())
        {
            var user = await db.Users.SingleAsync(u => u.Id == userId);
            user.FailedLoginCount = 5;
            user.IsLocked = true;
            user.LockoutEnd = DateTime.UtcNow.AddMinutes(30);
            user.LastLoginAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
            user.FailedLoginCount = 0;
            user.IsLocked = false;
            user.LockoutEnd = null;
            await db.SaveChangesAsync();
        }

        await AssertEpochUnchangedAsync(userId, before);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(session.Refresh)).StatusCode);
    }

    [Fact]
    public async Task SignInPasswordRehash_KeepsTheOtherSessions()
    {
        var (slug, _) = await _fx.SeedTenantAsync("rehash");
        var (email, userId) = await _fx.SeedUserAsync(slug, "rehash");
        var first = await LoginAsync(email, slug);
        // Then the same password at fewer iterations than the host's hasher (as an old release stored it): the next
        // successful sign-in re-hashes it. Written straight to the row, as the earlier release's own write would be.
        await _fx.ExecAsync($"UPDATE users SET password_hash = '{new Pbkdf2PasswordHasher(500).Hash(Password)}' WHERE id = '{userId}'");
        var before = await StateAsync(userId);
        Assert.StartsWith("PBKDF2$500$", before.PasswordHash);

        await LoginAsync(email, slug);

        var after = await StateAsync(userId);
        Assert.DoesNotContain("PBKDF2$500$", after.PasswordHash);
        await AssertEpochUnchangedAsync(userId, before);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(first.Refresh)).StatusCode);
    }

    // ── B. The epoch itself ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EpochMismatchAlone_Is401_AndRevokesTheFamily()
    {
        var (slug, _) = await _fx.SeedTenantAsync("mismatch");
        var (email, userId) = await _fx.SeedUserAsync(slug, "mismatch");
        var session = await LoginAsync(email, slug);
        // A raise that revokes nothing itself (raw SQL skips the save hook): only the refresh check stands in the way.
        await _fx.ExecAsync($"UPDATE users SET session_epoch = session_epoch + 1 WHERE id = '{userId}'");
        Assert.Null((await TokenAsync(session.Refresh)).RevokedAtUtc);

        var refreshed = await RefreshAsync(session.Refresh);

        Assert.Equal(HttpStatusCode.Unauthorized, refreshed.StatusCode);
        Assert.False((await Json(refreshed)).TryGetProperty("code", out _), "an ended session is the generic refusal");
        await AssertFamilyRevokedAsync(userId, session.Refresh);
    }

    [Fact]
    public async Task ACredentialChangeThroughAnyPath_LeavesNoLiveRefreshTokenOrOpenChallenge()
    {
        var (slug, _) = await _fx.SeedTenantAsync("tidy");
        var (email, userId) = await _fx.SeedUserAsync(slug, "tidy");
        var enrolled = await EnrollAsync(await LoginAsync(email, slug));
        var challenge = await LoginResponseAsync(email, slug);
        var challengeToken = (await Json(challenge)).GetProperty("challengeToken").GetString()!;
        long epochBefore;
        await using (var db = _fx.CreateDb())
        {
            var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
            epochBefore = user.SessionEpoch;
            db.RefreshTokens.Add(new RefreshToken
            {
                UserId = userId, TokenHash = new string('E', 64), ExpiresAtUtc = DateTime.UtcNow.AddDays(1),
                SessionEpoch = user.SessionEpoch,
            });
            await db.SaveChangesAsync();
        }
        Assert.Equal(2, await LiveRefreshCountAsync(userId));
        Assert.Equal(1, await OpenChallengeCountAsync(userId));

        // A path that knows nothing about sessions: one plain save of a new password hash.
        await using (var db = _fx.CreateDb())
        {
            var user = await db.Users.SingleAsync(u => u.Id == userId);
            user.PasswordHash = new Pbkdf2PasswordHasher().Hash(NewPassword);
            await db.SaveChangesAsync();
        }

        Assert.Equal(0, await LiveRefreshCountAsync(userId));
        Assert.Equal(0, await OpenChallengeCountAsync(userId));
        await using (var db = _fx.CreateDb())
        {
            Assert.Equal(epochBefore + 1, (await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId)).SessionEpoch);
            var audit = (await db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                    .Where(a => a.Action == "auth.sessions_ended" && a.EntityId == userId.ToString())
                    .Select(a => a.Metadata!).ToListAsync())
                .Single(m => m.Contains("password_changed"));
            Assert.Contains("\"refreshTokensRevoked\":2", audit);
            Assert.Contains("\"challengesConsumed\":1", audit);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(enrolled.Session.Refresh)).StatusCode);
        var verify = await SendAsync(HttpMethod.Post, "/api/auth/mfa/challenge/verify",
            new { challengeToken, totpCode = Totp(enrolled.Secret, 1) }, null);
        Assert.Equal(HttpStatusCode.Unauthorized, verify.StatusCode);
    }

    [Fact]
    public async Task EndAllSessions_RaisesOnce_EvenWithACredentialChangeInTheSameSave_AndASecondSaveIsANoOp()
    {
        var (slug, _) = await _fx.SeedTenantAsync("endall");
        var (email, userId) = await _fx.SeedUserAsync(slug, "endall");
        var session = await LoginAsync(email, slug);

        await using (var db = _fx.CreateDb())
        {
            var user = await db.Users.SingleAsync(u => u.Id == userId);
            TenantSessionSecurity.EndAllSessions(user);
            await db.SaveChangesAsync();
            await db.SaveChangesAsync(); // nothing changed since: no second raise, no second audit row
        }
        await AssertFamilyRevokedAsync(userId, session.Refresh);
        Assert.Equal(1L, (await StateAsync(userId)).SessionEpoch);

        var second = await LoginAsync(email, slug);
        await using (var db = _fx.CreateDb())
        {
            var user = await db.Users.SingleAsync(u => u.Id == userId);
            TenantSessionSecurity.EndAllSessions(user);
            user.PasswordHash = new Pbkdf2PasswordHasher().Hash(NewPassword);
            await db.SaveChangesAsync();
        }
        Assert.Equal(2L, (await StateAsync(userId)).SessionEpoch);
        await using (var db = _fx.CreateDb())
        {
            var triggers = await db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .Where(a => a.Action == "auth.sessions_ended" && a.EntityId == userId.ToString())
                .OrderBy(a => a.CreatedAtUtc).Select(a => a.Metadata!).ToListAsync();
            Assert.Equal(2, triggers.Count);
            Assert.Contains("\"trigger\":\"end_all_sessions\"", triggers[0]);
            Assert.Contains("\"trigger\":\"password_changed\"", triggers[1]);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(second.Refresh)).StatusCode);
    }

    [Fact]
    public async Task TwoConcurrentRaisesFromTheSameRead_LandTwoApart()
    {
        var (slug, _) = await _fx.SeedTenantAsync("twice");
        var (_, userId) = await _fx.SeedUserAsync(slug, "twice");
        await using var a = _fx.CreateDb();
        await using var b = _fx.CreateDb();
        var ua = await a.Users.SingleAsync(u => u.Id == userId);
        var ub = await b.Users.SingleAsync(u => u.Id == userId);
        Assert.Equal(0L, ua.SessionEpoch);
        Assert.Equal(0L, ub.SessionEpoch);

        ua.PasswordHash = new Pbkdf2PasswordHasher().Hash(NewPassword);
        await a.SaveChangesAsync();
        ub.Status = "Suspended";
        ub.IsActive = false;
        await b.SaveChangesAsync(); // wrote 0 + 1 from its stale read; the trigger lands it at 2

        Assert.Equal(2L, (await StateAsync(userId)).SessionEpoch);
    }

    // ── B. The race: a sign-in verified with the old password, concurrent with a password change ──

    [Fact]
    public async Task SignInWaitingOnTheUserRow_WhileThePasswordChanges_IsRefused()
    {
        var (slug, _) = await _fx.SeedTenantAsync("racewait");
        var (email, userId) = await _fx.SeedUserAsync(slug, "racewait");

        await using var holder = _fx.CreateDb(retrying: false);
        await using var tx = await holder.Database.BeginTransactionAsync();
        var user = await holder.Users.TagWith(RowLockingInterceptor.ForUpdateTag).SingleAsync(u => u.Id == userId);
        // The sign-in verifies the (still current) password without a lock, then waits on this row.
        var login = LoginResponseAsync(email, slug);
        await _fx.WaitForALockWaiterAsync();
        user.PasswordHash = new Pbkdf2PasswordHasher().Hash(NewPassword);
        await holder.SaveChangesAsync();
        await tx.CommitAsync();

        var response = await login;

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await LiveRefreshCountAsync(userId));
        Assert.Equal(1L, (await StateAsync(userId)).SessionEpoch);
    }

    [Fact]
    public async Task SignInCommittedBeforeAPasswordChangeStagedOnAnOlderRead_EndsWithADeniedToken()
    {
        var (slug, _) = await _fx.SeedTenantAsync("racestale");
        var (email, userId) = await _fx.SeedUserAsync(slug, "racestale");
        await using var stale = _fx.CreateDb();
        var user = await stale.Users.SingleAsync(u => u.Id == userId); // read before the sign-in: epoch 0, old hash

        var session = await LoginAsync(email, slug);
        Assert.Equal(0L, (await TokenAsync(session.Refresh)).SessionEpoch);
        user.PasswordHash = new Pbkdf2PasswordHasher().Hash(NewPassword);
        await stale.SaveChangesAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(session.Refresh)).StatusCode);
        await AssertFamilyRevokedAsync(userId, session.Refresh);
        Assert.Equal(0, await LiveRefreshCountAsync(userId));
    }

    [Fact]
    public async Task ParallelSignInsAndAPasswordChange_LeaveNoLiveSessionBelowTheEpoch()
    {
        var (slug, _) = await _fx.SeedTenantAsync("raceall");
        var (email, userId) = await _fx.SeedUserAsync(slug, "raceall");
        var logins = Enumerable.Range(0, 6).Select(_ => LoginResponseAsync(email, slug)).ToList();
        var change = Task.Run(async () =>
        {
            await using var db = _fx.CreateDb(retrying: false);
            await using var tx = await db.Database.BeginTransactionAsync();
            var user = await db.Users.TagWith(RowLockingInterceptor.ForUpdateTag).SingleAsync(u => u.Id == userId);
            user.PasswordHash = new Pbkdf2PasswordHasher().Hash(NewPassword);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        });
        await Task.WhenAll(logins.Cast<Task>().Append(change));

        var epoch = (await StateAsync(userId)).SessionEpoch;
        Assert.Equal(1L, epoch);
        await using var check = _fx.CreateDb();
        var live = await check.RefreshTokens.AsNoTracking().Where(r => r.UserId == userId && r.RevokedAtUtc == null).ToListAsync();
        Assert.All(live, r => Assert.Equal(epoch, r.SessionEpoch));
        foreach (var response in logins.Select(t => t.Result).Where(r => r.StatusCode == HttpStatusCode.OK))
        {
            var refresh = (await Json(response)).GetProperty("refreshToken").GetString()!;
            var token = await TokenAsync(refresh);
            var refreshed = await RefreshAsync(refresh);
            Assert.Equal(token.SessionEpoch == epoch ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, refreshed.StatusCode);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private sealed record Session(string Access, string Refresh);
    private sealed record Enrolled(string Secret, Session Session);
    private sealed record UserState(long SessionEpoch, string PasswordHash, int SessionsEndedAudits);

    private async Task<UserState> StateAsync(Guid userId)
    {
        await using var db = _fx.CreateDb();
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(u => u.Id == userId);
        var audits = await db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(a => a.Action == "auth.sessions_ended" && a.EntityId == userId.ToString());
        return new UserState(user.SessionEpoch, user.PasswordHash, audits);
    }

    /// <summary>R is refused (generic 401), its whole family is revoked, the epoch moved exactly one step, nothing of
    /// the user is left live, and exactly one <c>auth.sessions_ended</c> row names the trigger.</summary>
    private async Task AssertSessionsEndedAsync(Guid userId, string refresh, UserState before, string? trigger)
    {
        var refreshed = await RefreshAsync(refresh);
        Assert.Equal(HttpStatusCode.Unauthorized, refreshed.StatusCode);
        await AssertFamilyRevokedAsync(userId, refresh);
        var after = await StateAsync(userId);
        Assert.Equal(before.SessionEpoch + 1, after.SessionEpoch);
        Assert.Equal(before.SessionsEndedAudits + 1, after.SessionsEndedAudits);
        Assert.Equal(0, await LiveRefreshCountAsync(userId));
        Assert.Equal(0, await OpenChallengeCountAsync(userId));
        await using var db = _fx.CreateDb();
        var latest = await db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.Action == "auth.sessions_ended" && a.EntityId == userId.ToString())
            .OrderByDescending(a => a.CreatedAtUtc).Select(a => a.Metadata!).FirstAsync();
        Assert.Contains("\"trigger\":\"" + (trigger ?? string.Empty), latest);
        Assert.Contains($"\"sessionEpoch\":{after.SessionEpoch}", latest);
    }

    private async Task AssertEpochUnchangedAsync(Guid userId, UserState before)
    {
        var after = await StateAsync(userId);
        Assert.Equal(before.SessionEpoch, after.SessionEpoch);
        Assert.Equal(before.SessionsEndedAudits, after.SessionsEndedAudits);
    }

    private async Task AssertFamilyRevokedAsync(Guid userId, string rawRefreshToken)
    {
        var token = await TokenAsync(rawRefreshToken);
        Assert.Equal(userId, token.UserId);
        await using var db = _fx.CreateDb();
        var family = await db.RefreshTokens.AsNoTracking().Where(r => r.FamilyId == token.FamilyId).ToListAsync();
        Assert.NotEmpty(family);
        Assert.All(family, r => Assert.NotNull(r.RevokedAtUtc));
    }

    private async Task<RefreshToken> TokenAsync(string rawRefreshToken)
    {
        await using var db = _fx.CreateDb();
        var hash = _fx.Hash(rawRefreshToken);
        return await db.RefreshTokens.AsNoTracking().SingleAsync(r => r.TokenHash == hash);
    }

    private async Task<int> LiveRefreshCountAsync(Guid userId)
    {
        await using var db = _fx.CreateDb();
        return await db.RefreshTokens.AsNoTracking().CountAsync(r => r.UserId == userId && r.RevokedAtUtc == null);
    }

    private async Task<int> OpenChallengeCountAsync(Guid userId)
    {
        await using var db = _fx.CreateDb();
        return await db.MfaChallengeTokens.IgnoreQueryFilters().AsNoTracking().CountAsync(c => c.UserId == userId && c.UsedAtUtc == null);
    }

    /// <summary>Moves a live session's sign-in <paramref name="hours"/> into the past (only the clock is faked).</summary>
    private Task StartSessionHoursAgoAsync(string rawRefreshToken, int hours) =>
        _fx.ExecAsync($"UPDATE refresh_tokens SET session_started_at_utc = now() - interval '{hours} hours' WHERE token_hash = '{_fx.Hash(rawRefreshToken)}'");

    private Task<HttpResponseMessage> LoginResponseAsync(string email, string slug) =>
        SendAsync(HttpMethod.Post, "/api/auth/login", new { email, password = Password, tenantSlug = slug }, null);

    private async Task<Session> LoginAsync(string email, string slug)
    {
        var response = await LoginResponseAsync(email, slug);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        return new Session(body.GetProperty("accessToken").GetString()!, body.GetProperty("refreshToken").GetString()!);
    }

    private async Task<string> AdminAsync(string slug, Guid tenantId)
    {
        var email = await _fx.SeedAdminAsync(slug, tenantId);
        return (await LoginAsync(email, slug)).Access;
    }

    /// <summary>Signed-in two-step setup; returns the secret and the session re-issued by verify-setup.</summary>
    private async Task<Enrolled> EnrollAsync(Session session)
    {
        var setup = await SendAsync(HttpMethod.Post, "/api/auth/mfa/setup", new { }, session.Access);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var secret = SecretOf((await Json(setup)).GetProperty("provisioningUri").GetString()!);
        var verify = await SendAsync(HttpMethod.Post, "/api/auth/mfa/verify-setup", new { tempSecret = secret, totpCode = Totp(secret, 0) }, session.Access);
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        var body = await Json(verify);
        return new Enrolled(secret, new Session(body.GetProperty("accessToken").GetString()!, body.GetProperty("refreshToken").GetString()!));
    }

    private async Task<Session> CompleteMfaLoginAsync(string email, string slug, string secret, int stepOffset)
    {
        var login = await LoginResponseAsync(email, slug);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var challengeToken = (await Json(login)).GetProperty("challengeToken").GetString()!;
        var verify = await SendAsync(HttpMethod.Post, "/api/auth/mfa/challenge/verify",
            new { challengeToken, totpCode = Totp(secret, stepOffset) }, null);
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        var body = await Json(verify);
        return new Session(body.GetProperty("accessToken").GetString()!, body.GetProperty("refreshToken").GetString()!);
    }

    private Task<HttpResponseMessage> RefreshAsync(string refresh) =>
        SendAsync(HttpMethod.Post, "/api/auth/refresh", new { refreshToken = refresh }, null);

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, string? bearer)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return _fx.Client.SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static string SecretOf(string provisioningUri)
    {
        var query = new Uri(provisioningUri).Query.TrimStart('?').Split('&');
        return Uri.UnescapeDataString(query.Single(p => p.StartsWith("secret=", StringComparison.OrdinalIgnoreCase))["secret=".Length..]);
    }

    /// <summary>The RFC 6238 code for the current 30-second step plus <paramref name="stepOffset"/> (the server accepts ±1).</summary>
    private static string Totp(string secret, int stepOffset)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var input = secret.TrimEnd('=').ToUpperInvariant();
        var key = new byte[input.Length * 5 / 8];
        int buffer = 0, bits = 0, index = 0;
        foreach (var c in input)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8) { bits -= 8; key[index++] = (byte)((buffer >> bits) & 0xFF); }
        }
        var msg = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 + stepOffset);
        if (BitConverter.IsLittleEndian) Array.Reverse(msg);
        using var hmac = new System.Security.Cryptography.HMACSHA1(key);
        var hash = hmac.ComputeHash(msg);
        var offset = hash[^1] & 0x0F;
        var bin = ((hash[offset] & 0x7F) << 24) | ((hash[offset + 1] & 0xFF) << 16) | ((hash[offset + 2] & 0xFF) << 8) | (hash[offset + 3] & 0xFF);
        return (bin % 1_000_000).ToString("D6");
    }
}

/// <summary>Its own PostgreSQL 16, migrated by every EF migration, behind the real Program.cs pipeline.</summary>
public sealed class SessionLimitsHostFixture : IAsyncLifetime
{
    public const string Password = "Correct-Horse-Battery-9!";
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
    private SessionLimitsHost _host = null!;
    public string ConnectionString { get; private set; } = string.Empty;
    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        ConnectionString = _pg.GetConnectionString();
        await using (var migrate = CreateDb())
            await migrate.Database.MigrateAsync();
        _host = new SessionLimitsHost(ConnectionString);
        Client = _host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public Task DisposeAsync()
    {
        Client?.Dispose();
        _host?.Dispose();
        return _pg.DisposeAsync().AsTask();
    }

    /// <summary>The production provider options (a retrying strategy); <paramref name="retrying"/> false for a context
    /// that opens its own transaction, which a retrying strategy refuses.</summary>
    public ZayraDbContext CreateDb(bool retrying = true)
    {
        var builder = new DbContextOptionsBuilder<ZayraDbContext>();
        if (retrying) builder.UseNpgsql(ConnectionString, PostgresFixture.ProductionProviderOptions);
        else builder.UseNpgsql(ConnectionString);
        return new ZayraDbContext(builder.AddInterceptors(RowLockingInterceptor.Instance).Options);
    }

    public string Hash(string raw) => new JwtTokenService(AuthHardeningTestKit.Jwt).HashToken(raw);

    public async Task ExecAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Returns once another backend of this database is waiting on a row lock (10 s at most).</summary>
    public async Task WaitForALockWaiterAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'", conn);
            if ((long)(await cmd.ExecuteScalarAsync())! > 0) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("No backend started waiting on the held user row.");
    }

    public async Task<(string Slug, Guid TenantId)> SeedTenantAsync(string label)
    {
        var tenantId = Guid.NewGuid();
        var slug = $"ses-{label}-{tenantId:N}"[..24];
        await using var db = CreateDb();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = $"Sessions {label}", Slug = slug, IsActive = true });
        await db.SaveChangesAsync();
        await new Zayra.Api.Infrastructure.Seed.AuthSeeder(db).EnsureTenantRolesAsync(tenantId);
        return (slug, tenantId);
    }

    /// <summary>An active tenant user with a password and no factor (optionally owned by SCIM).</summary>
    public async Task<(string Email, Guid UserId)> SeedUserAsync(string slug, string label, string? passwordHash = null, bool scimOwned = false)
    {
        await using var db = CreateDb();
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(t => t.Slug == slug);
        var email = $"{label}@sessions.test";
        var user = new User
        {
            Id = Guid.NewGuid(), TenantId = tenant.Id, Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = label,
            PasswordHash = passwordHash ?? new Pbkdf2PasswordHasher().Hash(Password), Status = "Active", IsActive = true,
            IsEmailConfirmed = true, IsGroupScope = true,
            IdentityProvider = scimOwned ? Zayra.Api.Models.EnterpriseIdentityProtocols.Scim : "Local",
            ProvisioningSource = scimOwned ? Zayra.Api.Models.EnterpriseIdentityProtocols.Scim : "Local",
            ExternalId = scimOwned ? $"ext-{label}" : string.Empty,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return (email, user.Id);
    }

    /// <summary>An administrator of the tenant (the seeded Admin role holds security.manage).</summary>
    public async Task<string> SeedAdminAsync(string slug, Guid tenantId)
    {
        var (email, adminId) = await SeedUserAsync(slug, $"admin-{Guid.NewGuid():N}"[..14]);
        await using var db = CreateDb();
        var adminRole = await db.Roles.SingleAsync(r => r.TenantId == tenantId && r.Name == "Admin");
        db.UserRoles.Add(new UserRole { UserId = adminId, RoleId = adminRole.Id });
        await db.SaveChangesAsync();
        return email;
    }
}

/// <summary>The real Program.cs host over the fixture's database: background workers removed, auth limits raised.</summary>
internal sealed class SessionLimitsHost : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    public SessionLimitsHost(string connectionString) => _connectionString = connectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("ConnectionStrings:Default", _connectionString);
        builder.UseSetting("Database:RunMigrationsOnStartup", "false");
        builder.UseSetting("RateLimit:LoginPermitLimit", "10000");
        builder.UseSetting("RateLimit:RefreshPermitLimit", "10000");
        builder.ConfigureTestServices(services =>
        {
            var apiAssembly = typeof(ZayraDbContext).Assembly;
            foreach (var worker in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType?.Assembly == apiAssembly).ToList())
                services.Remove(worker);
        });
    }
}
