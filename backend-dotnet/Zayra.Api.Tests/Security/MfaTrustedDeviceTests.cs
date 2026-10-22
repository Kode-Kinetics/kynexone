using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Zayra.Api.Application.Auth;
using Zayra.Api.Controllers;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Infrastructure.Retention.Rules;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Two-step sign-in on/off and trusted browsers (contract mfa-contract.md + Amendment 1). A trusted browser skips the
/// CODE only, only after the password, only for the same tenant and user, only while the row is live — and every
/// password, factor and deactivation change revokes it. Turning two-step off is refused for privileged roles and for a
/// company that requires it for everyone, with coded errors.
/// </summary>
public sealed class MfaTrustedDeviceTests
{
    private const string Password = "Correct-Horse-Battery-9!";
    private const string Email = "staff@hardening.local";

    // ── Fixture helpers ──────────────────────────────────────────────────────────────────────

    private sealed record Enrolled(Guid UserId, string Secret);

    private static async Task<Enrolled> SeedEnrolledAsync(AuthHardeningTestKit kit, string email = Email, string? role = null)
    {
        var userId = await kit.SeedUserAsync(email, new Pbkdf2PasswordHasher().Hash(Password), role);
        var secret = kit.Totp.GenerateBase32Secret();
        await using var db = kit.NewDb();
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        user.MFAEnabled = true;
        user.MfaSecretEncrypted = kit.Totp.EncryptSecret(secret);
        user.MfaConfiguredAtUtc = DateTime.UtcNow.AddMinutes(-5);
        await db.SaveChangesAsync();
        return new Enrolled(userId, secret);
    }

    /// <summary>Forgets the last accepted TOTP step so a test can sign in more than three times inside one window.
    /// Done BEFORE a login (it rotates the stamp, which a pending challenge carries).</summary>
    private static async Task ForgetStepAsync(AuthHardeningTestKit kit, Guid userId)
    {
        await using var db = kit.NewDb();
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        user.MfaLastTotpStep = null;
        await db.SaveChangesAsync();
    }

    private static async Task SetDaysAsync(AuthHardeningTestKit kit, int days, bool mfaRequired = false)
    {
        await using var db = kit.NewDb();
        var sec = await db.SecuritySettings.FirstOrDefaultAsync(s => s.TenantId == kit.TenantId);
        if (sec is null) db.SecuritySettings.Add(sec = new SecuritySetting { TenantId = kit.TenantId });
        sec.MfaTrustedDeviceDays = days;
        sec.MfaRequired = mfaRequired;
        await db.SaveChangesAsync();
    }

    private static DefaultHttpContext Http(string? trustCookie = null, ClaimsPrincipal? user = null, string userAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_5) Gecko/20100101 Firefox/131.0")
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.7");
        http.Request.Headers.UserAgent = userAgent;
        if (trustCookie is not null) http.Request.Headers.Cookie = $"{MfaTrustedDevices.CookieName}={trustCookie}";
        if (user is not null) http.User = user;
        return http;
    }

    private static ClaimsPrincipal Principal(Guid userId, Guid tenantId) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim("tenant_id", tenantId.ToString())], "test"));

    private static async Task<(IActionResult Result, HttpResponse Response)> LoginAsync(
        AuthHardeningTestKit kit, string? trustCookie = null, string email = Email, string? tenantSlug = null)
    {
        await using var db = kit.NewDb();
        var controller = new AuthController(kit.Auth(db))
        {
            ControllerContext = new ControllerContext { HttpContext = Http(trustCookie) },
        };
        var result = await controller.Login(new LoginRequest(email, Password, tenantSlug ?? AuthHardeningTestKit.TenantSlug), CancellationToken.None);
        return (result, controller.Response);
    }

    private static JsonElement Body(IActionResult result) =>
        JsonSerializer.SerializeToElement(((ObjectResult)result).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static string ChallengeOf(IActionResult login)
    {
        var body = Body(login);
        body.GetProperty("mfaRequired").GetBoolean().Should().BeTrue("a code challenge was expected");
        return body.GetProperty("challengeToken").GetString()!;
    }

    private static async Task<(IActionResult Result, HttpResponse Response)> VerifyAsync(
        AuthHardeningTestKit kit, string challenge, string code, bool? remember)
    {
        await using var db = kit.NewDb();
        var controller = new MfaController(kit.Mfa(db), kit.Auth(db))
        {
            ControllerContext = new ControllerContext { HttpContext = Http() },
        };
        var result = await controller.VerifyChallenge(new MfaChallengeVerifyRequest(challenge, code, remember), CancellationToken.None);
        return (result, controller.Response);
    }

    private static MfaController SignedIn(AuthHardeningTestKit kit, Zayra.Api.Data.ZayraDbContext db, Guid userId, Guid tenantId, string? trustCookie = null) =>
        new(kit.Mfa(db), kit.Auth(db))
        {
            ControllerContext = new ControllerContext { HttpContext = Http(trustCookie, Principal(userId, tenantId)) },
        };

    private static string? TrustCookie(HttpResponse response)
    {
        foreach (var header in response.Headers.SetCookie)
            if (header!.StartsWith(MfaTrustedDevices.CookieName + "=", StringComparison.Ordinal))
                return header.Split(';')[0].Split('=', 2)[1];
        return null;
    }

    private static string? TrustCookieHeader(HttpResponse response) =>
        response.Headers.SetCookie.FirstOrDefault(h => h!.StartsWith(MfaTrustedDevices.CookieName + "=", StringComparison.Ordinal));

    /// <summary>Signs in with password + code and asks to trust the browser; returns the cookie value.</summary>
    private static async Task<string> TrustThisBrowserAsync(AuthHardeningTestKit kit, Enrolled who, string email = Email, string? slug = null)
    {
        await ForgetStepAsync(kit, who.UserId);
        var challenge = ChallengeOf((await LoginAsync(kit, email: email, tenantSlug: slug)).Result);
        var verified = await VerifyAsync(kit, challenge, Totp.Now(who.Secret), remember: true);
        verified.Result.Should().BeOfType<OkObjectResult>();
        return TrustCookie(verified.Response) ?? throw new InvalidOperationException("no trust cookie was set");
    }

    private static string Sha256Hex(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(raw)));

    private static async Task<MfaTrustedDevice> RowAsync(AuthHardeningTestKit kit, string cookie)
    {
        await using var db = kit.NewDb();
        var hash = Sha256Hex(cookie);
        return await db.MfaTrustedDevices.AsNoTracking().SingleAsync(x => x.TokenHash == hash);
    }

    private static async Task<int> AuditCountAsync(AuthHardeningTestKit kit, string action)
    {
        await using var db = kit.NewDb();
        return await db.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.Action == action);
    }

    // ── Trust and skip ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task VerifyWithRememberDevice_SetsAStrictHttpOnlyCookie_AndStoresOnlyItsHash()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var who = await SeedEnrolledAsync(kit);

        var login = await LoginAsync(kit);
        Body(login.Result).GetProperty("rememberDeviceDays").GetInt32().Should().Be(30, "no settings row means the 30-day default");
        var verified = await VerifyAsync(kit, ChallengeOf(login.Result), Totp.Now(who.Secret), remember: true);

        verified.Result.Should().BeOfType<OkObjectResult>();
        var header = TrustCookieHeader(verified.Response);
        header.Should().NotBeNull();
        header!.ToLowerInvariant().Should().Contain("httponly").And.Contain("secure").And.Contain("samesite=strict")
            .And.Contain("path=/api/auth").And.Contain("max-age=2592000");
        var cookie = TrustCookie(verified.Response)!;
        cookie.Should().HaveLength(43, "32 random bytes, base64url");
        JsonSerializer.Serialize(((OkObjectResult)verified.Result).Value).Should().NotContain(cookie, "the token is never in the body");

        var row = await RowAsync(kit, cookie);
        row.UserId.Should().Be(who.UserId);
        row.TenantId.Should().Be(kit.TenantId);
        row.TokenHash.Should().NotContain(cookie);
        row.ExpiresAtUtc.Should().BeCloseTo(row.CreatedAtUtc.AddDays(30), TimeSpan.FromSeconds(1));
        row.UserAgent.Should().Contain("Firefox");
        (await AuditCountAsync(kit, MfaTrustedDevices.TrustedAuditAction)).Should().Be(1);
    }

    [Fact]
    public async Task VerifyWithoutRememberDevice_TrustsNothing()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var who = await SeedEnrolledAsync(kit);
        var verified = await VerifyAsync(kit, ChallengeOf((await LoginAsync(kit)).Result), Totp.Now(who.Secret), remember: null);

        verified.Result.Should().BeOfType<OkObjectResult>();
        TrustCookie(verified.Response).Should().BeNull("the checkbox defaults to unchecked");
        await using var db = kit.NewDb();
        (await db.MfaTrustedDevices.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task NextLogin_WithTheCookie_SkipsTheCode_AndIsAudited()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var who = await SeedEnrolledAsync(kit);
        var cookie = await TrustThisBrowserAsync(kit, who);

        var next = await LoginAsync(kit, cookie);

        next.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<AuthResponse>(
            "a trusted browser gets the session straight after the password");
        (await RowAsync(kit, cookie)).LastUsedAtUtc.Should().NotBeNull();
        (await AuditCountAsync(kit, MfaTrustedDevices.UsedAuditAction)).Should().Be(1);
        await using var db = kit.NewDb();
        (await db.AuditLogs.IgnoreQueryFilters().AnyAsync(a => a.Action == "auth.login" && a.Metadata!.Contains("\"via\":\"trusted_device\"")))
            .Should().BeTrue();
    }

    [Fact]
    public async Task TheCookie_NeverSkipsThePassword()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var who = await SeedEnrolledAsync(kit);
        var cookie = await TrustThisBrowserAsync(kit, who);

        await using var db = kit.NewDb();
        var controller = new AuthController(kit.Auth(db)) { ControllerContext = new ControllerContext { HttpContext = Http(cookie) } };
        (await controller.Login(new LoginRequest(Email, "wrong-password-1!A", AuthHardeningTestKit.TenantSlug), CancellationToken.None))
            .Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task AnExpiredCookie_IsChallenged()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var who = await SeedEnrolledAsync(kit);
        var cookie = await TrustThisBrowserAsync(kit, who);
        await using (var db = kit.NewDb())
        {
            var row = await db.MfaTrustedDevices.SingleAsync();
            row.CreatedAtUtc = DateTime.UtcNow.AddDays(-40);
            row.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        ChallengeOf((await LoginAsync(kit, cookie)).Result);
    }

    [Fact]
    public async Task ARevokedCookie_IsChallenged()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var who = await SeedEnrolledAsync(kit);
        var cookie = await TrustThisBrowserAsync(kit, who);
        await using (var db = kit.NewDb())
        {
            var row = await db.MfaTrustedDevices.SingleAsync();
            row.RevokedAtUtc = DateTime.UtcNow;
            row.RevokedReason = MfaTrustRevokeReasons.UserRevoked;
            await db.SaveChangesAsync();
        }
        ChallengeOf((await LoginAsync(kit, cookie)).Result);
    }

    [Fact]
    public async Task AnotherUsersCookie_IsChallenged()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var alice = await SeedEnrolledAsync(kit, "alice@hardening.local");
        var bob = await SeedEnrolledAsync(kit, "bob@hardening.local");
        var alicesCookie = await TrustThisBrowserAsync(kit, alice, "alice@hardening.local");

        ChallengeOf((await LoginAsync(kit, alicesCookie, "bob@hardening.local")).Result);
        (await RowAsync(kit, alicesCookie)).LastUsedAtUtc.Should().BeNull("bob's sign-in did not use alice's trust");
        _ = bob;
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // well-formed, never issued
    [InlineData("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!")] // right length, not base64url
    public async Task AGarbageCookie_IsChallenged(string cookie)
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await SeedEnrolledAsync(kit);
        ChallengeOf((await LoginAsync(kit, cookie)).Result);
    }

    [Fact]
    public async Task AnotherTenantsCookie_IsChallenged_AndItsRowsAreInvisible()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var home = await SeedEnrolledAsync(kit);
        // A second tenant with a user of the same email and the same password, who trusts this very browser.
        Guid otherTenant, otherUser;
        await using (var db = kit.NewDb())
        {
            var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Other", Slug = "other", IsActive = true };
            db.Tenants.Add(tenant);
            var user = new User
            {
                Id = Guid.NewGuid(), TenantId = tenant.Id, Email = Email, NormalizedEmail = Email.ToUpperInvariant(), FullName = Email,
                PasswordHash = new Pbkdf2PasswordHasher().Hash(Password), Status = "Active", IsActive = true, IsEmailConfirmed = true,
                IsGroupScope = true, MFAEnabled = true, MfaSecretEncrypted = kit.Totp.EncryptSecret(home.Secret),
                MfaConfiguredAtUtc = DateTime.UtcNow.AddMinutes(-5),
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            (otherTenant, otherUser) = (tenant.Id, user.Id);
        }
        var foreignCookie = await TrustThisBrowserAsync(kit, new Enrolled(otherUser, home.Secret), Email, "other");
        (await RowAsync(kit, foreignCookie)).TenantId.Should().Be(otherTenant);

        ChallengeOf((await LoginAsync(kit, foreignCookie)).Result);

        // The home user cannot see or revoke the other tenant's row, even by id.
        var foreignId = (await RowAsync(kit, foreignCookie)).Id;
        await using (var db = kit.NewDb())
        {
            var list = (OkObjectResult)await SignedIn(kit, db, home.UserId, kit.TenantId, foreignCookie).TrustedDevices(db, CancellationToken.None);
            ((IEnumerable<MfaTrustedDeviceDto>)list.Value!).Should().BeEmpty();
            (await SignedIn(kit, db, home.UserId, kit.TenantId).RevokeTrustedDevice(foreignId, db, CancellationToken.None))
                .Should().BeOfType<NotFoundResult>();
        }
        (await RowAsync(kit, foreignCookie)).RevokedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task ZeroDays_OffersNothing_TrustsNothing_AndEndsTrustAlreadyGiven()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var who = await SeedEnrolledAsync(kit);
        var earlier = await TrustThisBrowserAsync(kit, who);
        await SetDaysAsync(kit, 0);
        await using (var check = kit.NewDb())
            (await check.SecuritySettings.AsNoTracking().SingleAsync(x => x.TenantId == kit.TenantId)).MfaTrustedDeviceDays
                .Should().Be(0, "an explicit 0 on a NEW settings row is stored as 0, not swapped for the column default");

        await ForgetStepAsync(kit, who.UserId);
        var login = await LoginAsync(kit, earlier);
        login.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().NotBeOfType<AuthResponse>("0 days ends trust already given");
        Body(login.Result).GetProperty("rememberDeviceDays").GetInt32().Should().Be(0, "0 hides the checkbox");
        var verified = await VerifyAsync(kit, ChallengeOf(login.Result), Totp.Now(who.Secret), remember: true);

        verified.Result.Should().BeOfType<OkObjectResult>();
        TrustCookie(verified.Response).Should().BeNull();
        await using var db = kit.NewDb();
        (await db.MfaTrustedDevices.CountAsync()).Should().Be(1, "only the row from before the switch-off");
    }

    [Fact]
    public async Task ShorterDays_ShortenTrustAlreadyGiven()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var who = await SeedEnrolledAsync(kit);
        var cookie = await TrustThisBrowserAsync(kit, who);
        await using (var db = kit.NewDb())
        {
            // Trusted ten days ago, under a factor set up before that.
            var row = await db.MfaTrustedDevices.SingleAsync();
            row.CreatedAtUtc = DateTime.UtcNow.AddDays(-10);
            await db.Users.Where(u => u.Id == who.UserId)
                .ExecuteUpdateAsync(x => x.SetProperty(u => u.MfaConfiguredAtUtc, DateTime.UtcNow.AddDays(-20)));
            await db.SaveChangesAsync();
        }
        (await LoginAsync(kit, cookie)).Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<AuthResponse>();
        await SetDaysAsync(kit, 7);
        ChallengeOf((await LoginAsync(kit, cookie)).Result);
    }

    // ── Enrolment is never skipped ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AnEnrolmentRequiredUser_IsNeverSkipped()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var who = await SeedEnrolledAsync(kit);
        var cookie = await TrustThisBrowserAsync(kit, who);
        // The factor is wiped behind the product's back (no revocation) and the company requires two-step.
        await using (var db = kit.NewDb())
            await db.Users.Where(u => u.Id == who.UserId).ExecuteUpdateAsync(x => x
                .SetProperty(u => u.MFAEnabled, false)
                .SetProperty(u => u.MfaSecretEncrypted, (string?)null));
        await SetDaysAsync(kit, 30, mfaRequired: true);
        int sessionsBefore;
        await using (var db = kit.NewDb()) sessionsBefore = await db.RefreshTokens.CountAsync(r => r.UserId == who.UserId);

        var login = await LoginAsync(kit, cookie);
        Body(login.Result).GetProperty("mfaEnrollmentRequired").GetBoolean().Should().BeTrue("trust only ever skips a code, never enrolment");
        await using var verify = kit.NewDb();
        (await verify.RefreshTokens.CountAsync(r => r.UserId == who.UserId)).Should().Be(sessionsBefore, "no session was issued");
        (await RowAsync(kit, cookie)).LastUsedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task APrivilegedUnenrolledUser_PastTheDate_IsNeverSkipped()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await kit.SetPlatformEnforcementDateAsync(DateTime.UtcNow.AddDays(-1));
        var userId = await kit.SeedUserAsync("admin@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), "Admin");
        var (raw, hash) = MfaTrustedDevices.NewToken();
        await using (var db = kit.NewDb())
        {
            db.MfaTrustedDevices.Add(new MfaTrustedDevice
            {
                TenantId = kit.TenantId, UserId = userId, TokenHash = hash,
                CreatedAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddDays(30),
            });
            await db.SaveChangesAsync();
        }
        Body((await LoginAsync(kit, raw, "admin@hardening.local")).Result).GetProperty("mfaEnrollmentRequired").GetBoolean().Should().BeTrue();
    }

    // ── Revocation ───────────────────────────────────────────────────────────────────────────

    private static async Task<MfaTrustedDevice> AssertRevokedAsync(AuthHardeningTestKit kit, string cookie, string reason)
    {
        var row = await RowAsync(kit, cookie);
        row.RevokedAtUtc.Should().NotBeNull();
        row.RevokedReason.Should().Be(reason);
        await using var db = kit.NewDb();
        (await db.AuditLogs.IgnoreQueryFilters().AnyAsync(a => a.Action == MfaTrustedDevices.RevokedAuditAction
            && a.EntityId == row.UserId.ToString() && a.Metadata!.Contains($"\"reason\":\"{reason}\""))).Should().BeTrue();
        return row;
    }

    [Fact]
    public async Task PasswordChange_RevokesEveryTrustedBrowser()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var who = await SeedEnrolledAsync(kit);
        var cookie = await TrustThisBrowserAsync(kit, who);
        await using (var db = kit.NewDb())
            await kit.Auth(db).ChangePasswordAsync(who.UserId, new ChangePasswordRequest(Password, "A-whole-new-password-77!"),
                AuthHardeningTestKit.Ctx, CancellationToken.None);
        await AssertRevokedAsync(kit, cookie, MfaTrustRevokeReasons.PasswordChanged);
    }

    [Fact]
    public async Task PasswordReset_RevokesEveryTrustedBrowser()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var who = await SeedEnrolledAsync(kit);
        var cookie = await TrustThisBrowserAsync(kit, who);
        var tokens = new JwtTokenService(AuthHardeningTestKit.Jwt);
        var resetRaw = tokens.CreateSecureToken();
        await using (var db = kit.NewDb())
        {
            db.PasswordResetTokens.Add(new PasswordResetToken
            {
                UserId = who.UserId, TokenHash = tokens.HashToken(resetRaw), ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            });
            await db.SaveChangesAsync();
        }
        await using (var db = kit.NewDb())
            await kit.Auth(db).ResetPasswordAsync(new ResetPasswordRequest(resetRaw, "A-whole-new-password-77!", AuthHardeningTestKit.TenantSlug),
                AuthHardeningTestKit.Ctx, CancellationToken.None);
        await AssertRevokedAsync(kit, cookie, MfaTrustRevokeReasons.PasswordReset);
    }

    [Fact]
    public async Task Disable_RevokesEveryTrustedBrowser_AndClearsTheCookie()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var who = await SeedEnrolledAsync(kit);
        var cookie = await TrustThisBrowserAsync(kit, who);
        long step;
        await using (var db = kit.NewDb()) step = (await db.Users.AsNoTracking().SingleAsync(u => u.Id == who.UserId)).MfaLastTotpStep!.Value;

        await using (var db = kit.NewDb())
        {
            var controller = SignedIn(kit, db, who.UserId, kit.TenantId, cookie);
            (await controller.Disable(new MfaDisableRequest(Totp.ForStep(who.Secret, step + 1)), CancellationToken.None))
                .Should().BeOfType<NoContentResult>();
            TrustCookieHeader(controller.Response).Should().NotBeNull().And.Contain("expires=Thu, 01 Jan 1970");
        }
        await AssertRevokedAsync(kit, cookie, MfaTrustRevokeReasons.MfaDisabled);
        (await AuditCountAsync(kit, "auth.mfa.disabled")).Should().Be(1);
        await using (var db = kit.NewDb())
            (await db.Users.AsNoTracking().SingleAsync(u => u.Id == who.UserId)).MFAEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task PlatformRecoveryDisable_RevokesEveryTrustedBrowser()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var who = await SeedEnrolledAsync(kit);
        var cookie = await TrustThisBrowserAsync(kit, who);
        await using (var db = kit.NewDb())
            (await kit.Mfa(db).AdminDisableAsync(who.UserId, kit.TenantId, AuthHardeningTestKit.Ctx, CancellationToken.None)).Should().BeTrue();
        await AssertRevokedAsync(kit, cookie, MfaTrustRevokeReasons.MfaRecoveryDisabled);
    }

    [Fact]
    public async Task ReSetup_WithANewSecret_RevokesAnyTrustLeftBehind()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var userId = await kit.SeedUserAsync(Email, new Pbkdf2PasswordHasher().Hash(Password), null);
        var (raw, hash) = MfaTrustedDevices.NewToken();
        await using (var db = kit.NewDb())
        {
            // A live row from an earlier factor that was removed without revoking it.
            db.MfaTrustedDevices.Add(new MfaTrustedDevice
            {
                TenantId = kit.TenantId, UserId = userId, TokenHash = hash,
                CreatedAtUtc = DateTime.UtcNow.AddDays(-1), ExpiresAtUtc = DateTime.UtcNow.AddDays(29),
            });
            await db.SaveChangesAsync();
        }
        var secret = kit.Totp.GenerateBase32Secret();
        await using (var db = kit.NewDb())
        {
            var controller = SignedIn(kit, db, userId, kit.TenantId);
            (await controller.VerifySetup(new MfaVerifySetupRequest(secret, Totp.Now(secret)), CancellationToken.None))
                .Should().BeOfType<NoContentResult>();
        }
        await AssertRevokedAsync(kit, raw, MfaTrustRevokeReasons.MfaResetup);
        ChallengeOf((await LoginAsync(kit, raw)).Result);
    }

    [Theory]
    [InlineData("deactivate", MfaTrustRevokeReasons.UserDeactivated)]
    [InlineData("suspend", MfaTrustRevokeReasons.UserDeactivated)]
    [InlineData("nologin", MfaTrustRevokeReasons.UserDeactivated)]
    [InlineData("delete", MfaTrustRevokeReasons.UserDeleted)]
    [InlineData("password", MfaTrustRevokeReasons.PasswordChanged)]
    public async Task AnyPathThatChangesTheLogin_RevokesThroughTheSaveHook(string change, string reason)
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var who = await SeedEnrolledAsync(kit);
        var cookie = await TrustThisBrowserAsync(kit, who);
        await using (var db = kit.NewDb())
        {
            var user = await db.Users.SingleAsync(u => u.Id == who.UserId);
            switch (change)
            {
                case "deactivate": user.IsActive = false; user.Status = "Deactivated"; break;
                case "suspend": user.Status = "Suspended"; break;
                case "nologin": user.AccessMode = AccessModes.NoLogin; break;
                case "delete": user.IsDeleted = true; break;
                case "password": user.PasswordHash = new Pbkdf2PasswordHasher().Hash("Another-password-55!"); break;
            }
            await db.SaveChangesAsync();
        }
        await AssertRevokedAsync(kit, cookie, reason);
    }

    [Fact]
    public async Task LoginTelemetryAndLockout_DoNotRevoke()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var who = await SeedEnrolledAsync(kit);
        var cookie = await TrustThisBrowserAsync(kit, who);
        await using (var db = kit.NewDb())
        {
            var user = await db.Users.SingleAsync(u => u.Id == who.UserId);
            user.FailedLoginCount = 2;
            user.LastLoginAtUtc = DateTime.UtcNow;
            user.IsLocked = true;
            user.LockoutEnd = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        (await RowAsync(kit, cookie)).RevokedAtUtc.Should().BeNull();
    }

    // ── Turning two-step off (Amendment 1) ───────────────────────────────────────────────────

    private static async Task<IActionResult> DisableAsync(AuthHardeningTestKit kit, Guid userId, string code)
    {
        await using var db = kit.NewDb();
        return await SignedIn(kit, db, userId, kit.TenantId).Disable(new MfaDisableRequest(code), CancellationToken.None);
    }

    private static void ShouldBeCoded(IActionResult result, int status, string code)
    {
        var obj = result.Should().BeAssignableTo<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(status);
        Body(result).GetProperty("code").GetString().Should().Be(code);
        Body(result).GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Disable_IsRefusedForAPrivilegedRole_WithTheContractMessage()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await kit.SetPlatformEnforcementDateAsync(DateTime.UtcNow.AddDays(14)); // grace period: still "required"
        var admin = await SeedEnrolledAsync(kit, "admin@hardening.local", "Admin");

        var result = await DisableAsync(kit, admin.UserId, Totp.Now(admin.Secret));

        ShouldBeCoded(result, 409, MfaErrorCodes.RequiredForRole);
        Body(result).GetProperty("message").GetString().Should().Be(
            "Your role can see or change payroll, banking or access, so your company requires two-step sign-in.");
        await using var db = kit.NewDb();
        (await db.Users.AsNoTracking().SingleAsync(u => u.Id == admin.UserId)).MFAEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Disable_IsAllowedForANonPrivilegedUser()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var who = await SeedEnrolledAsync(kit, role: "Employee");
        long? stampBefore;
        await using (var db = kit.NewDb()) stampBefore = (await db.Users.AsNoTracking().SingleAsync(u => u.Id == who.UserId)).UpdatedAtUtc?.Ticks;

        (await DisableAsync(kit, who.UserId, Totp.Now(who.Secret))).Should().BeOfType<NoContentResult>();

        await using var verify = kit.NewDb();
        var user = await verify.Users.AsNoTracking().SingleAsync(u => u.Id == who.UserId);
        user.MFAEnabled.Should().BeFalse();
        user.UpdatedAtUtc!.Value.Ticks.Should().BeGreaterThan(stampBefore ?? 0, "turning it off rotates the security stamp (ends sessions)");
    }

    [Fact]
    public async Task Disable_IsRefusedWhenTheCompanyRequiresItForEveryone()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var who = await SeedEnrolledAsync(kit);
        await SetDaysAsync(kit, 30, mfaRequired: true);
        var result = await DisableAsync(kit, who.UserId, Totp.Now(who.Secret));
        ShouldBeCoded(result, 409, MfaErrorCodes.RequiredByWorkspace);
        Body(result).GetProperty("message").GetString().Should().Be("Your company requires two-step sign-in for everyone.");
    }

    [Fact]
    public async Task Disable_WithAWrongOrReusedCode_Is400CodeInvalid_AndWhenOff_Is409NotEnabled()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var who = await SeedEnrolledAsync(kit);
        ShouldBeCoded(await DisableAsync(kit, who.UserId, Totp.At(who.Secret, 10)), 400, MfaErrorCodes.CodeInvalid);

        await using (var db = kit.NewDb())
        {
            var user = await db.Users.SingleAsync(u => u.Id == who.UserId);
            user.MfaLastTotpStep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 + 1;
            await db.SaveChangesAsync();
        }
        ShouldBeCoded(await DisableAsync(kit, who.UserId, Totp.Now(who.Secret)), 400, MfaErrorCodes.CodeInvalid);

        await using (var db = kit.NewDb())
            (await kit.Mfa(db).AdminDisableAsync(who.UserId, kit.TenantId, AuthHardeningTestKit.Ctx, CancellationToken.None)).Should().BeTrue();
        ShouldBeCoded(await DisableAsync(kit, who.UserId, Totp.Now(who.Secret)), 409, MfaErrorCodes.NotEnabled);
    }

    // ── Turning two-step on (Amendment 1) ────────────────────────────────────────────────────

    private static async Task<IActionResult> VerifySetupAsync(AuthHardeningTestKit kit, Guid userId, string secret, string code)
    {
        await using var db = kit.NewDb();
        return await SignedIn(kit, db, userId, kit.TenantId).VerifySetup(new MfaVerifySetupRequest(secret, code), CancellationToken.None);
    }

    [Fact]
    public async Task VerifySetup_ReturnsDistinctCodedErrors()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var userId = await kit.SeedUserAsync(Email, new Pbkdf2PasswordHasher().Hash(Password), null);
        var secret = kit.Totp.GenerateBase32Secret();

        ShouldBeCoded(await VerifySetupAsync(kit, userId, "not-a-secret!", "123456"), 400, MfaErrorCodes.SetupInvalid);
        ShouldBeCoded(await VerifySetupAsync(kit, userId, "ABC", "123456"), 400, MfaErrorCodes.SetupInvalid);
        ShouldBeCoded(await VerifySetupAsync(kit, userId, secret, Totp.At(secret, 10)), 400, MfaErrorCodes.CodeInvalid);

        long? stampBefore;
        await using (var db = kit.NewDb()) stampBefore = (await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId)).UpdatedAtUtc?.Ticks;
        (await VerifySetupAsync(kit, userId, secret, Totp.Now(secret))).Should().BeOfType<NoContentResult>();
        await using (var db = kit.NewDb())
        {
            var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
            user.MFAEnabled.Should().BeTrue();
            user.UpdatedAtUtc!.Value.Ticks.Should().BeGreaterThan(stampBefore ?? 0, "turning it on rotates the security stamp (ends sessions)");
        }

        ShouldBeCoded(await VerifySetupAsync(kit, userId, kit.Totp.GenerateBase32Secret(), "123456"), 409, MfaErrorCodes.AlreadyEnabled);
    }

    // ── Status ───────────────────────────────────────────────────────────────────────────────

    private static async Task<JsonElement> StatusAsync(AuthHardeningTestKit kit, Guid userId)
    {
        await using var db = kit.NewDb();
        return Body(await SignedIn(kit, db, userId, kit.TenantId).Status(db, new ConfigurationBuilder().Build(), CancellationToken.None));
    }

    [Fact]
    public async Task Status_ReportsCanDisable_RememberDeviceDays_AndTrustedDeviceCount()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var who = await SeedEnrolledAsync(kit);
        await TrustThisBrowserAsync(kit, who);
        await TrustThisBrowserAsync(kit, who);
        await SetDaysAsync(kit, 14);

        var status = await StatusAsync(kit, who.UserId);
        status.GetProperty("enabled").GetBoolean().Should().BeTrue();
        status.GetProperty("canDisable").GetBoolean().Should().BeTrue();
        status.GetProperty("rememberDeviceDays").GetInt32().Should().Be(14);
        status.GetProperty("trustedDeviceCount").GetInt32().Should().Be(2);

        await SetDaysAsync(kit, 14, mfaRequired: true);
        (await StatusAsync(kit, who.UserId)).GetProperty("canDisable").GetBoolean().Should().BeFalse("required covers the company-wide rule");

        await kit.SetPlatformEnforcementDateAsync(DateTime.UtcNow.AddDays(14));
        var admin = await SeedEnrolledAsync(kit, "admin@hardening.local", "Admin");
        await SetDaysAsync(kit, 14);
        var adminStatus = await StatusAsync(kit, admin.UserId);
        adminStatus.GetProperty("required").GetBoolean().Should().BeTrue();
        adminStatus.GetProperty("canDisable").GetBoolean().Should().BeFalse();
        adminStatus.GetProperty("trustedDeviceCount").GetInt32().Should().Be(0);

        var off = await kit.SeedUserAsync("off@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), null);
        (await StatusAsync(kit, off)).GetProperty("canDisable").GetBoolean().Should().BeFalse("nothing to turn off");
    }

    // ── List, revoke one, revoke all ─────────────────────────────────────────────────────────

    [Fact]
    public async Task List_MarksThisBrowser_RevokeOneAndAll_AndCrossUserIs404()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var who = await SeedEnrolledAsync(kit);
        var other = await SeedEnrolledAsync(kit, "other@hardening.local");
        var first = await TrustThisBrowserAsync(kit, who);
        var second = await TrustThisBrowserAsync(kit, who);
        var othersCookie = await TrustThisBrowserAsync(kit, other, "other@hardening.local");

        await using (var db = kit.NewDb())
        {
            var list = ((IEnumerable<MfaTrustedDeviceDto>)((OkObjectResult)await SignedIn(kit, db, who.UserId, kit.TenantId, second)
                .TrustedDevices(db, CancellationToken.None)).Value!).ToList();
            list.Should().HaveCount(2, "only this user's rows");
            list.Should().ContainSingle(d => d.Current).Which.Id.Should().Be((await RowAsync(kit, second)).Id);
            list.Should().OnlyContain(d => d.Browser == "Firefox on macOS");
        }

        var othersRow = await RowAsync(kit, othersCookie);
        await using (var db = kit.NewDb())
            (await SignedIn(kit, db, who.UserId, kit.TenantId).RevokeTrustedDevice(othersRow.Id, db, CancellationToken.None))
                .Should().BeOfType<NotFoundResult>("another user's row is not found, not forbidden");
        (await RowAsync(kit, othersCookie)).RevokedAtUtc.Should().BeNull();

        await using (var db = kit.NewDb())
            (await SignedIn(kit, db, who.UserId, kit.TenantId).RevokeTrustedDevice(Guid.NewGuid(), db, CancellationToken.None))
                .Should().BeOfType<NotFoundResult>();

        var firstRow = await RowAsync(kit, first);
        await using (var db = kit.NewDb())
        {
            var controller = SignedIn(kit, db, who.UserId, kit.TenantId, first);
            (await controller.RevokeTrustedDevice(firstRow.Id, db, CancellationToken.None)).Should().BeOfType<NoContentResult>();
            TrustCookieHeader(controller.Response).Should().NotBeNull("revoking this browser clears its cookie");
        }
        await AssertRevokedAsync(kit, first, MfaTrustRevokeReasons.UserRevoked);
        ChallengeOf((await LoginAsync(kit, first)).Result);

        await using (var db = kit.NewDb())
            (await SignedIn(kit, db, who.UserId, kit.TenantId, second).RevokeAllTrustedDevices(db, CancellationToken.None))
                .Should().BeOfType<NoContentResult>();
        await AssertRevokedAsync(kit, second, MfaTrustRevokeReasons.UserRevokedAll);
        var auditsAfterFirstRevokeAll = await AuditCountAsync(kit, MfaTrustedDevices.RevokedAuditAction);

        await using (var db = kit.NewDb())
            (await SignedIn(kit, db, who.UserId, kit.TenantId).RevokeAllTrustedDevices(db, CancellationToken.None))
                .Should().BeOfType<NoContentResult>("revoke all twice is a no-op, not an error");
        (await AuditCountAsync(kit, MfaTrustedDevices.RevokedAuditAction)).Should().Be(auditsAfterFirstRevokeAll);
        (await RowAsync(kit, othersCookie)).RevokedAtUtc.Should().BeNull("revoke all touches only the caller's rows");
    }

    [Fact]
    public async Task TwoBrowsersTrustedInTurn_EachGetTheirOwnRow_AndBothWork()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var who = await SeedEnrolledAsync(kit);
        var first = await TrustThisBrowserAsync(kit, who);
        var second = await TrustThisBrowserAsync(kit, who);
        first.Should().NotBe(second);
        await using (var db = kit.NewDb())
            (await db.MfaTrustedDevices.CountAsync(x => x.UserId == who.UserId && x.RevokedAtUtc == null)).Should().Be(2);
        (await LoginAsync(kit, first)).Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<AuthResponse>();
        (await LoginAsync(kit, second)).Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<AuthResponse>();
    }

    [Fact]
    public async Task List_IsInAStableOrder_CurrentFirst_ThenLastUsed_NullsLast_ThenCreated()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var userId = await kit.SeedUserAsync(Email, new Pbkdf2PasswordHasher().Hash(Password), null);
        var now = DateTime.UtcNow;
        var tokens = Enumerable.Range(0, 5).Select(_ => MfaTrustedDevices.NewToken()).ToList();
        MfaTrustedDevice Row(int i, DateTime created, DateTime? used) => new()
        {
            TenantId = kit.TenantId, UserId = userId, TokenHash = tokens[i].Hash, CreatedAtUtc = created,
            ExpiresAtUtc = created.AddDays(30), LastUsedAtUtc = used, UserAgent = i == 4 ? "curl/8.4.0" : null,
        };
        var neverUsedOld = Row(0, now.AddDays(-9), null);
        var neverUsedNew = Row(1, now.AddDays(-2), null);
        var usedLongAgo = Row(2, now.AddDays(-8), now.AddDays(-7));
        var usedRecently = Row(3, now.AddDays(-6), now.AddHours(-1));
        var current = Row(4, now.AddDays(-5), null);   // this browser: never used since, still first
        await using (var db = kit.NewDb())
        {
            db.MfaTrustedDevices.AddRange(neverUsedOld, usedLongAgo, current, usedRecently, neverUsedNew);
            await db.SaveChangesAsync();
        }

        async Task<List<MfaTrustedDeviceDto>> List(string? cookie)
        {
            await using var db = kit.NewDb();
            return ((IEnumerable<MfaTrustedDeviceDto>)((OkObjectResult)await SignedIn(kit, db, userId, kit.TenantId, cookie)
                .TrustedDevices(db, CancellationToken.None)).Value!).ToList();
        }

        var withCurrent = await List(tokens[4].Raw);
        withCurrent.Select(d => d.Id).Should().Equal(current.Id, usedRecently.Id, usedLongAgo.Id, neverUsedNew.Id, neverUsedOld.Id);
        withCurrent[0].Current.Should().BeTrue();
        withCurrent.Skip(1).Should().OnlyContain(d => !d.Current);
        withCurrent[0].Browser.Should().BeNull("an unrecognised User-Agent is null, not a guess");

        (await List(null)).Select(d => d.Id).Should().Equal(usedRecently.Id, usedLongAgo.Id, neverUsedNew.Id, current.Id, neverUsedOld.Id);
        (await List(tokens[4].Raw)).Select(d => d.Id).Should().Equal(withCurrent.Select(d => d.Id), "the order is stable between calls");
    }

    // ── Settings ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-1)]
    [InlineData(91)]
    public void TheSettingIsBoundedInTheModel(int days)
    {
        MfaTrustedDevices.EffectiveDays(new SecuritySetting { MfaTrustedDeviceDays = days }).Should().BeInRange(0, 90);
        MfaTrustedDevices.EffectiveDays(null).Should().Be(30);
    }

    [Fact]
    public void BrowserSummaries_AreShortAndHuman()
    {
        MfaTrustedDevices.DescribeBrowser("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0 Safari/537.36 Edg/129.0")
            .Should().Be("Edge on Windows");
        MfaTrustedDevices.DescribeBrowser("Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1")
            .Should().Be("Safari on iOS");
        MfaTrustedDevices.DescribeBrowser("Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0 Mobile Safari/537.36")
            .Should().Be("Chrome on Android");
        MfaTrustedDevices.DescribeBrowser(null).Should().BeNull("the UI shows \"Unknown browser\" for null");
        MfaTrustedDevices.DescribeBrowser("curl/8.4.0").Should().BeNull();
    }

    [Fact]
    public async Task Retention_DeletesOnlyRowsDeadForMoreThan30Days()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var userId = await kit.SeedUserAsync(Email, new Pbkdf2PasswordHasher().Hash(Password), null);
        var now = DateTime.UtcNow;
        MfaTrustedDevice Row(DateTime created, DateTime expires, DateTime? revoked) => new()
        {
            TenantId = kit.TenantId, UserId = userId, TokenHash = MfaTrustedDevices.NewToken().Hash,
            CreatedAtUtc = created, ExpiresAtUtc = expires, RevokedAtUtc = revoked,
            RevokedReason = revoked is null ? null : MfaTrustRevokeReasons.UserRevoked,
        };
        var live = Row(now.AddDays(-1), now.AddDays(29), null);
        var longExpired = Row(now.AddDays(-80), now.AddDays(-50), null);
        var recentlyRevoked = Row(now.AddDays(-5), now.AddDays(25), now.AddDays(-2));
        var longRevoked = Row(now.AddDays(-70), now.AddDays(10), now.AddDays(-40));
        await using (var db = kit.NewDb())
        {
            db.MfaTrustedDevices.AddRange(live, longExpired, recentlyRevoked, longRevoked);
            await db.SaveChangesAsync();
        }

        await using var sweepDb = kit.NewDb();
        var ctx = new JobExecutionContext(new(Guid.NewGuid(), kit.TenantId, "data-retention", "{}", 1, 1, Guid.NewGuid(), null),
            sweepDb, Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(new Microsoft.Extensions.DependencyInjection.ServiceCollection()), [], TimeSpan.FromMinutes(2), default, default);
        var rule = new MfaTrustedDeviceRetentionRule();
        var candidates = await rule.EvaluateAsync(ctx, now, 100, CancellationToken.None);
        candidates.Select(c => Guid.Parse(c.EntityId)).Should().BeEquivalentTo([longExpired.Id, longRevoked.Id]);
        foreach (var c in candidates) await rule.ApplyAsync(ctx, c, now, CancellationToken.None);

        await using var verify = kit.NewDb();
        (await verify.MfaTrustedDevices.Select(x => x.Id).ToListAsync()).Should().BeEquivalentTo([live.Id, recentlyRevoked.Id]);
    }

    // ── TOTP for tests ───────────────────────────────────────────────────────────────────────

    private static class Totp
    {
        public static string Now(string secret) => At(secret, 0);
        public static string At(string secret, int offsetSteps) => ForStep(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 + offsetSteps);

        public static string ForStep(string secret, long step)
        {
            var key = FromBase32(secret);
            var msg = BitConverter.GetBytes(step);
            if (BitConverter.IsLittleEndian) Array.Reverse(msg);
            using var hmac = new HMACSHA1(key);
            var hash = hmac.ComputeHash(msg);
            var offset = hash[^1] & 0x0F;
            var bin = ((hash[offset] & 0x7F) << 24) | ((hash[offset + 1] & 0xFF) << 16) | ((hash[offset + 2] & 0xFF) << 8) | (hash[offset + 3] & 0xFF);
            return (bin % 1_000_000).ToString("D6");
        }

        private static byte[] FromBase32(string s)
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            var input = s.TrimEnd('=').ToUpperInvariant();
            var output = new byte[input.Length * 5 / 8];
            int buffer = 0, bits = 0, index = 0;
            foreach (var c in input)
            {
                buffer = (buffer << 5) | alphabet.IndexOf(c);
                bits += 5;
                if (bits >= 8) { bits -= 8; output[index++] = (byte)((buffer >> bits) & 0xFF); }
            }
            return output;
        }
    }
}
