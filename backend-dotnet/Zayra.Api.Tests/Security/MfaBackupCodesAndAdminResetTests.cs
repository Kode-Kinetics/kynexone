using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Two-step wave 2 (mfa2-contract.md): tenant users' backup codes and the administrator's two-step reset.
/// Backup codes: ten Crockford codes shown once at setup, stored only as one salted set, single use, normalised,
/// never trusting the browser, replaced by regeneration (which needs a current app code), deleted with the factor.
/// Reset: security.manage plus the account ceiling, never yourself, a reason, 404 across tenants, 409 when off, and one
/// transaction that ends the factor, sessions, challenges, trusted browsers and backup codes, audited.
/// The concurrency and migration halves run on PostgreSQL in MfaBackupCodesPostgresTests.
/// </summary>
public sealed class MfaBackupCodesAndAdminResetTests
{
    private const string Password = "Correct-Horse-Battery-9!";
    private const string StaffEmail = "staff@hardening.local";
    private const string AdminEmail = "admin@hardening.local";
    private static readonly Regex CodeShape = new("^[0-9A-HJKMNP-TV-Z]{5}-[0-9A-HJKMNP-TV-Z]{5}$");

    // ── Fixture helpers ──────────────────────────────────────────────────────────────────────

    private sealed record Enrolled(Guid UserId, string Secret, IReadOnlyList<string> Codes);

    private static DefaultHttpContext Http(ClaimsPrincipal? user = null)
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.7");
        http.Request.Headers.UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_5) Gecko/20100101 Firefox/131.0";
        if (user is not null) http.User = user;
        return http;
    }

    private static ClaimsPrincipal Principal(Guid userId, Guid tenantId) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim("tenant_id", tenantId.ToString())], "test"));

    private static MfaController SignedIn(AuthHardeningTestKit kit, ZayraDbContext db, Guid userId) =>
        new(kit.Mfa(db), kit.Auth(db)) { ControllerContext = new ControllerContext { HttpContext = Http(Principal(userId, kit.TenantId)) } };

    private static MfaController Anonymous(AuthHardeningTestKit kit, ZayraDbContext db) =>
        new(kit.Mfa(db), kit.Auth(db)) { ControllerContext = new ControllerContext { HttpContext = Http() } };

    private static JsonElement Body(IActionResult result) =>
        JsonSerializer.SerializeToElement(((ObjectResult)result).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static void ShouldBeCoded(IActionResult result, int status, string code)
    {
        var obj = result.Should().BeAssignableTo<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(status);
        Body(result).GetProperty("code").GetString().Should().Be(code);
        Body(result).GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
    }

    private static IReadOnlyList<string> CodesOf(IActionResult result)
    {
        result.Should().BeOfType<OkObjectResult>();
        return Body(result).GetProperty("backupCodes").EnumerateArray().Select(x => x.GetString()!).ToList();
    }

    /// <summary>Turns two-step on through the real signed-in setup endpoint and returns the codes it showed.</summary>
    private static async Task<Enrolled> EnrolAsync(AuthHardeningTestKit kit, Guid userId)
    {
        var secret = kit.Totp.GenerateBase32Secret();
        await using var db = kit.NewDb();
        var codes = CodesOf(await SignedIn(kit, db, userId).VerifySetup(new MfaVerifySetupRequest(secret, Totp.Now(secret)), CancellationToken.None));
        return new Enrolled(userId, secret, codes);
    }

    private static async Task<Guid> SeedStaffAsync(AuthHardeningTestKit kit, string email = StaffEmail) =>
        await kit.SeedUserAsync(email, new Pbkdf2PasswordHasher().Hash(Password), null);

    /// <summary>Forgets the last accepted TOTP step so the same window's code can be used again.</summary>
    private static async Task ForgetStepAsync(AuthHardeningTestKit kit, Guid userId)
    {
        await using var db = kit.NewDb();
        (await db.Users.SingleAsync(u => u.Id == userId)).MfaLastTotpStep = null;
        await db.SaveChangesAsync();
    }

    private static async Task<string> ChallengeAsync(AuthHardeningTestKit kit, string email = StaffEmail)
    {
        await using var db = kit.NewDb();
        var controller = new AuthController(kit.Auth(db)) { ControllerContext = new ControllerContext { HttpContext = Http() } };
        var body = Body(await controller.Login(new LoginRequest(email, Password, AuthHardeningTestKit.TenantSlug), CancellationToken.None));
        body.GetProperty("mfaRequired").GetBoolean().Should().BeTrue("a code challenge was expected");
        return body.GetProperty("challengeToken").GetString()!;
    }

    private static async Task<(IActionResult Result, HttpResponse Response)> VerifyAsync(
        AuthHardeningTestKit kit, string challenge, string? totp = null, string? backup = null, bool? remember = null)
    {
        await using var db = kit.NewDb();
        var controller = Anonymous(kit, db);
        var result = await controller.VerifyChallenge(new MfaChallengeVerifyRequest(challenge, totp, remember, backup), CancellationToken.None);
        return (result, controller.Response);
    }

    private static async Task<JsonElement> StatusAsync(AuthHardeningTestKit kit, Guid userId)
    {
        await using var db = kit.NewDb();
        return Body(await SignedIn(kit, db, userId).Status(db, new ConfigurationBuilder().Build(), CancellationToken.None));
    }

    private static async Task<User> UserAsync(AuthHardeningTestKit kit, Guid userId)
    {
        await using var db = kit.NewDb();
        return await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
    }

    private static async Task<List<AuditLog>> AuditsAsync(AuthHardeningTestKit kit, string action)
    {
        await using var db = kit.NewDb();
        return await db.AuditLogs.IgnoreQueryFilters().AsNoTracking().Where(a => a.Action == action).ToListAsync();
    }

    private sealed class CapturingNotifications : INotificationService
    {
        public List<NotificationRequest> Requests { get; } = [];
        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<NotificationDelivery>> EnqueueAsync(NotificationRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult<IReadOnlyList<NotificationDelivery>>([]);
        }
    }

    private static AccessController Access(AuthHardeningTestKit kit, ZayraDbContext db, Guid callerId, Guid? tenantId = null)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", (tenantId ?? kit.TenantId).ToString()),
            new(ClaimTypes.NameIdentifier, callerId.ToString()),
            new("permission", "security.manage"),
            new(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
        };
        var service = new AccessManagementService(db, new Pbkdf2PasswordHasher(), new AuditService(db), new JwtTokenService(AuthHardeningTestKit.Jwt));
        return new AccessController(service, db, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = Http(new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))) },
        };
    }

    private static async Task<IActionResult> ResetAsync(AuthHardeningTestKit kit, Guid callerId, Guid targetId, string? reason,
        CapturingNotifications? notifications = null)
    {
        await using var db = kit.NewDb();
        return await Access(kit, db, callerId).ResetUserMfa(targetId, new MfaAdminResetRequest(reason), notifications, CancellationToken.None);
    }

    // ── Codes: shape and normalisation ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("ABCDE-FGH12", "ABCDEFGH12")]
    [InlineData("abcde fgh12", "ABCDEFGH12")]
    [InlineData(" abcde-fgh12 ", "ABCDEFGH12")]
    [InlineData("ABCDEFGH12", "ABCDEFGH12")]
    [InlineData("oO1il-IL0ab", "00111110AB")]
    public void Normalize_IgnoresCaseSpacesAndHyphens_AndReadsCrockfordLookAlikes(string input, string expected) =>
        MfaBackupCodes.Normalize(input).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ABC")]
    [InlineData("ABCDE-FGH123")]
    [InlineData("ABCDE-FGHU1")]
    [InlineData("ABCDE_FGH12")]
    [InlineData("123456")]
    public void Normalize_RejectsAnythingWeCouldNotHaveIssued(string? input) =>
        MfaBackupCodes.Normalize(input).Should().BeNull();

    [Fact]
    public void Generate_GivesTenDistinctCodes_StoredOnlyAsOneSaltedSet()
    {
        var a = MfaBackupCodes.Generate();
        var b = MfaBackupCodes.Generate();
        a.Codes.Should().HaveCount(10).And.OnlyHaveUniqueItems().And.AllSatisfy(c => CodeShape.IsMatch(c).Should().BeTrue(c));
        MfaBackupCodes.Remaining(a.Stored).Should().Be(10);
        foreach (var code in a.Codes)
        {
            a.Stored.Should().NotContain(code).And.NotContain(MfaBackupCodes.Normalize(code)!);
            a.Stored.Should().NotContain(Convert.ToHexString(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(MfaBackupCodes.Normalize(code)!))),
                "a 50-bit code is never stored as a fast unsalted hash");
        }
        a.Stored.Split('$')[2].Should().NotBe(b.Stored.Split('$')[2], "every set has its own salt");
        MfaBackupCodes.TryConsume(a.Stored, b.Codes[0], out _, out _).Should().BeFalse("a code of another set never matches");
        MfaBackupCodes.TryConsume(a.Stored, a.Codes[3], out var left, out var remaining).Should().BeTrue();
        remaining.Should().Be(9);
        MfaBackupCodes.TryConsume(left, a.Codes[3], out _, out _).Should().BeFalse("single use");
    }

    // ── Codes: setup, status, sign-in ────────────────────────────────────────────────────────

    [Fact]
    public async Task Setup_ReturnsTenCodesOnce_AuditsThem_AndStatusCountsThem()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var userId = await SeedStaffAsync(kit);
        (await StatusAsync(kit, userId)).GetProperty("backupCodesRemaining").GetInt32().Should().Be(0, "two-step is off");

        var who = await EnrolAsync(kit, userId);

        who.Codes.Should().HaveCount(10).And.OnlyHaveUniqueItems().And.AllSatisfy(c => CodeShape.IsMatch(c).Should().BeTrue(c));
        var stored = (await UserAsync(kit, userId)).MfaRecoveryCodeHashes!;
        foreach (var code in who.Codes) stored.Should().NotContain(code.Replace("-", ""));
        (await StatusAsync(kit, userId)).GetProperty("backupCodesRemaining").GetInt32().Should().Be(10);
        var audit = (await AuditsAsync(kit, "auth.mfa.backup_codes_generated")).Should().ContainSingle().Subject;
        audit.EntityId.Should().Be(userId.ToString());
        audit.Metadata.Should().Contain("\"via\":\"setup\"").And.Contain("\"count\":10");
    }

    private static async Task<AuthResponse> PasswordSessionAsync(AuthHardeningTestKit kit)
    {
        await using var db = kit.NewDb();
        var controller = new AuthController(kit.Auth(db)) { ControllerContext = new ControllerContext { HttpContext = Http() } };
        var result = await controller.Login(new LoginRequest(StaffEmail, Password, AuthHardeningTestKit.TenantSlug), CancellationToken.None);
        return result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<AuthResponse>().Subject;
    }

    private static async Task<bool> AccessTokenIsCurrentAsync(AuthHardeningTestKit kit, string accessToken)
    {
        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(jwt.Claims, "test"));
        await using var db = kit.NewDb();
        return await TenantSessionSecurity.IsCurrentAsync(principal, db, CancellationToken.None);
    }

    private static async Task<bool> RefreshWorksAsync(AuthHardeningTestKit kit, string refreshToken)
    {
        await using var db = kit.NewDb();
        try
        {
            await kit.Auth(db).RefreshAsync(new RefreshTokenRequest(refreshToken), AuthHardeningTestKit.Ctx, CancellationToken.None);
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Amendment 1: turning two-step on ends every other session but re-issues the caller's, login-shaped.</summary>
    [Fact]
    public async Task SignedInSetup_EndsOtherSessions_AndReissuesThisOne_InTheLoginShape()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var userId = await SeedStaffAsync(kit);
        var mine = await PasswordSessionAsync(kit);
        var other = await PasswordSessionAsync(kit);
        (await AccessTokenIsCurrentAsync(kit, other.AccessToken)).Should().BeTrue("precondition");

        var secret = kit.Totp.GenerateBase32Secret();
        IActionResult result;
        HttpResponse response;
        await using (var db = kit.NewDb())
        {
            var controller = SignedIn(kit, db, userId);
            result = await controller.VerifySetup(new MfaVerifySetupRequest(secret, Totp.Now(secret)), CancellationToken.None);
            response = controller.Response;
        }

        var body = Body(result);
        body.GetProperty("backupCodes").GetArrayLength().Should().Be(10);
        var newAccess = body.GetProperty("accessToken").GetString()!;
        var newRefresh = body.GetProperty("refreshToken").GetString()!;
        body.GetProperty("expiresAtUtc").GetDateTime().Should().BeAfter(DateTime.UtcNow);
        body.GetProperty("user").GetProperty("id").GetGuid().Should().Be(userId, "the same user object login returns");
        response.Headers.SetCookie.Should().Contain(h => h!.StartsWith(MfaTrustedDevices.CookieName + "=", StringComparison.Ordinal),
            "the old trust cookie is cleared");

        // The caller's new session works...
        (await AccessTokenIsCurrentAsync(kit, newAccess)).Should().BeTrue();
        (await RefreshWorksAsync(kit, newRefresh)).Should().BeTrue();
        // ...and every session from before is refused, the caller's old one included.
        (await AccessTokenIsCurrentAsync(kit, mine.AccessToken)).Should().BeFalse();
        (await AccessTokenIsCurrentAsync(kit, other.AccessToken)).Should().BeFalse();
        (await RefreshWorksAsync(kit, other.RefreshToken)).Should().BeFalse("the rotated stamp alone would have let it refresh");
        (await RefreshWorksAsync(kit, mine.RefreshToken)).Should().BeFalse();
        (await AuditsAsync(kit, "auth.session_reissued")).Should().ContainSingle().Which.Metadata.Should().Contain("\"via\":\"mfa_setup\"");

        // Single use, and bound to this factor: it cannot mint a second session.
        var configuredAt = (await UserAsync(kit, userId)).MfaConfiguredAtUtc!.Value;
        await using (var db = kit.NewDb())
        {
            (await kit.Auth(db).IssueSessionAfterMfaSetupAsync(userId, kit.TenantId, configuredAt, AuthHardeningTestKit.Ctx, CancellationToken.None))
                .Should().BeNull();
            (await kit.Auth(db).IssueSessionAfterMfaSetupAsync(userId, kit.TenantId, configuredAt.AddSeconds(-1), AuthHardeningTestKit.Ctx, CancellationToken.None))
                .Should().BeNull();
        }
    }

    [Fact]
    public async Task EnrollmentSetup_ReturnsTenCodes()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var userId = await SeedStaffAsync(kit);
        string token;
        MfaSetupInitDto init;
        await using (var db = kit.NewDb())
        {
            token = await kit.Mfa(db).CreateEnrollmentChallengeAsync(userId, kit.TenantId, "198.51.100.7", CancellationToken.None);
            init = (await kit.Mfa(db).InitiateEnrollmentSetupAsync(token, CancellationToken.None))!;
        }
        IActionResult result;
        await using (var db = kit.NewDb())
            result = await Anonymous(kit, db).VerifyEnrollmentSetup(
                new MfaEnrollmentVerifySetupRequest(token, init.TempSecret, Totp.Now(init.TempSecret)), CancellationToken.None);

        CodesOf(result).Should().HaveCount(10).And.AllSatisfy(c => CodeShape.IsMatch(c).Should().BeTrue(c));
        MfaBackupCodes.Remaining((await UserAsync(kit, userId)).MfaRecoveryCodeHashes).Should().Be(10);
        (await AuditsAsync(kit, "auth.mfa.backup_codes_generated")).Should().ContainSingle()
            .Which.Metadata.Should().Contain("\"via\":\"enrollment\"");
    }

    [Fact]
    public async Task ABackupCode_SignsInOnce_Normalised_NeverTrustsTheBrowser_AndIsAudited()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var who = await EnrolAsync(kit, await SeedStaffAsync(kit));
        var code = who.Codes[4];
        var typed = " " + code.ToLowerInvariant().Replace("-", " ") + " ";

        var (result, response) = await VerifyAsync(kit, await ChallengeAsync(kit), backup: typed, remember: true);

        var auth = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<AuthResponse>().Subject;
        auth.AccessToken.Should().NotBeNullOrWhiteSpace();
        auth.MfaTrustToken.Should().BeNull("a backup code never trusts the browser, even with rememberDevice");
        response.Headers.SetCookie.Should().NotContain(h => h!.StartsWith(MfaTrustedDevices.CookieName + "=", StringComparison.Ordinal));
        await using (var db = kit.NewDb()) (await db.MfaTrustedDevices.CountAsync()).Should().Be(0);
        (await StatusAsync(kit, who.UserId)).GetProperty("backupCodesRemaining").GetInt32().Should().Be(9);
        (await AuditsAsync(kit, "auth.mfa.backup_code_used")).Should().ContainSingle()
            .Which.Metadata.Should().Contain("\"remaining\":9");
        (await AuditsAsync(kit, "auth.login")).Should().ContainSingle().Which.Metadata.Should().Contain("\"factor\":\"backup_code\"");

        // Used once: the same code is now a wrong code (400), and costs an attempt on the new challenge.
        var again = await VerifyAsync(kit, await ChallengeAsync(kit), backup: code);
        ShouldBeCoded(again.Result, 400, MfaErrorCodes.CodeInvalid);
        (await AuditsAsync(kit, "auth.mfa.backup_code_failed")).Should().ContainSingle();
        // Another unused code still works.
        (await VerifyAsync(kit, await ChallengeAsync(kit), backup: who.Codes[5])).Result.Should().BeOfType<OkObjectResult>();
        MfaBackupCodes.Remaining((await UserAsync(kit, who.UserId)).MfaRecoveryCodeHashes).Should().Be(8);
    }

    [Fact]
    public async Task UsingABackupCode_DoesNotSpendTheAppCodeStep()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var who = await EnrolAsync(kit, await SeedStaffAsync(kit));
        await ForgetStepAsync(kit, who.UserId);
        (await VerifyAsync(kit, await ChallengeAsync(kit), backup: who.Codes[0])).Result.Should().BeOfType<OkObjectResult>();
        (await UserAsync(kit, who.UserId)).MfaLastTotpStep.Should().BeNull();
        (await VerifyAsync(kit, await ChallengeAsync(kit), totp: Totp.Now(who.Secret))).Result.Should().BeOfType<OkObjectResult>();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "  ")]
    [InlineData("123456", "ABCDE-FGH12")]
    public async Task ChallengeVerify_WithNeitherOrBothCodes_Is400CodeRequired_AndCostsNoAttempt(string? totp, string? backup)
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var who = await EnrolAsync(kit, await SeedStaffAsync(kit));
        var challenge = await ChallengeAsync(kit);

        ShouldBeCoded((await VerifyAsync(kit, challenge, totp, backup)).Result, 400, MfaErrorCodes.CodeRequired);

        await using (var db = kit.NewDb())
            (await db.MfaChallengeTokens.AsNoTracking().SingleAsync(c => c.UserId == who.UserId && c.UsedAtUtc == null)).FailedAttempts.Should().Be(0);
        (await VerifyAsync(kit, challenge, backup: who.Codes[0])).Result.Should().BeOfType<OkObjectResult>("the challenge is still good");
    }

    [Theory]
    [InlineData("ABC")]
    [InlineData("ABCDE-FGHU1")]
    [InlineData("not a code at all")]
    [InlineData("00000-00000")]
    public async Task ChallengeVerify_WithAMalformedOrWrongBackupCode_Is400CodeInvalid(string backup)
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var who = await EnrolAsync(kit, await SeedStaffAsync(kit));
        var challenge = await ChallengeAsync(kit);

        ShouldBeCoded((await VerifyAsync(kit, challenge, backup: backup)).Result, 400, MfaErrorCodes.CodeInvalid);
        MfaBackupCodes.Remaining((await UserAsync(kit, who.UserId)).MfaRecoveryCodeHashes).Should().Be(10);
    }

    [Fact]
    public async Task ChallengeVerify_WithAWrongAppCode_Is400CodeInvalid_AndAnInvalidChallengeIsStill401()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var who = await EnrolAsync(kit, await SeedStaffAsync(kit));
        ShouldBeCoded((await VerifyAsync(kit, await ChallengeAsync(kit), totp: Totp.At(who.Secret, 10))).Result, 400, MfaErrorCodes.CodeInvalid);
        (await VerifyAsync(kit, "not-a-challenge", backup: who.Codes[0])).Result.Should().BeOfType<UnauthorizedObjectResult>();
        MfaBackupCodes.Remaining((await UserAsync(kit, who.UserId)).MfaRecoveryCodeHashes).Should().Be(10);
    }

    [Fact]
    public async Task ABackupCodeSignIn_QueuesASecurityNoticeForTheOwner()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var who = await EnrolAsync(kit, await SeedStaffAsync(kit));
        var notifications = new CapturingNotifications();
        var challenge = await ChallengeAsync(kit);
        await using (var db = kit.NewDb())
            await kit.Auth(db, notifications: notifications).CompleteMfaLoginWithBackupCodeAsync(challenge, who.Codes[1], AuthHardeningTestKit.Ctx, CancellationToken.None);
        var notice = notifications.Requests.Should().ContainSingle().Subject;
        notice.EventCode.Should().Be("security.mfa_backup_code_used");
        notice.UserId.Should().Be(who.UserId);
        notice.Message.Should().Contain("9 left");
    }

    // ── Codes: regenerate ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Regenerate_NeedsACurrentCode_ReplacesTheWholeSet_AndKeepsTheSession()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var who = await EnrolAsync(kit, await SeedStaffAsync(kit));
        await ForgetStepAsync(kit, who.UserId);
        var stampBefore = (await UserAsync(kit, who.UserId)).UpdatedAtUtc;

        await using (var db = kit.NewDb())
        {
            ShouldBeCoded(await SignedIn(kit, db, who.UserId).RegenerateBackupCodes(new MfaRegenerateBackupCodesRequest(Totp.At(who.Secret, 10)), CancellationToken.None),
                400, MfaErrorCodes.CodeInvalid);
        }
        await using (var db = kit.NewDb())
        {
            ShouldBeCoded(await SignedIn(kit, db, who.UserId).RegenerateBackupCodes(new MfaRegenerateBackupCodesRequest(null), CancellationToken.None),
                400, MfaErrorCodes.CodeInvalid);
        }
        IReadOnlyList<string> fresh;
        await using (var db = kit.NewDb())
            fresh = CodesOf(await SignedIn(kit, db, who.UserId).RegenerateBackupCodes(new MfaRegenerateBackupCodesRequest(Totp.Now(who.Secret)), CancellationToken.None));

        fresh.Should().HaveCount(10).And.NotIntersectWith(who.Codes);
        var after = await UserAsync(kit, who.UserId);
        after.UpdatedAtUtc.Should().Be(stampBefore, "regenerating does not sign the user out of the page showing the codes");
        after.MfaLastTotpStep.Should().NotBeNull("the code that proved it is spent");
        (await AuditsAsync(kit, "auth.mfa.backup_codes_generated")).Should().Contain(a => a.Metadata!.Contains("\"via\":\"regenerate\""));

        // The same app code cannot regenerate twice (replay).
        await using (var db = kit.NewDb())
            ShouldBeCoded(await SignedIn(kit, db, who.UserId).RegenerateBackupCodes(new MfaRegenerateBackupCodesRequest(Totp.Now(who.Secret)), CancellationToken.None),
                400, MfaErrorCodes.CodeInvalid);

        ShouldBeCoded((await VerifyAsync(kit, await ChallengeAsync(kit), backup: who.Codes[0])).Result, 400, MfaErrorCodes.CodeInvalid);
        (await VerifyAsync(kit, await ChallengeAsync(kit), backup: fresh[0])).Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Regenerate_WhenTwoStepIsOff_Is409NotEnabled()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var userId = await SeedStaffAsync(kit);
        await using var db = kit.NewDb();
        ShouldBeCoded(await SignedIn(kit, db, userId).RegenerateBackupCodes(new MfaRegenerateBackupCodesRequest("123456"), CancellationToken.None),
            409, MfaErrorCodes.NotEnabled);
    }

    // ── Codes: deleted with the factor ───────────────────────────────────────────────────────

    [Fact]
    public async Task Disable_DeletesTheCodes_AndReSetupIssuesANewSetTheOldOnesDoNotJoin()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var who = await EnrolAsync(kit, await SeedStaffAsync(kit));
        await ForgetStepAsync(kit, who.UserId);
        await using (var db = kit.NewDb())
            (await SignedIn(kit, db, who.UserId).Disable(new MfaSelfDisableRequest(Totp.Now(who.Secret)), CancellationToken.None)).Should().BeOfType<NoContentResult>();
        (await UserAsync(kit, who.UserId)).MfaRecoveryCodeHashes.Should().BeNull();
        (await StatusAsync(kit, who.UserId)).GetProperty("backupCodesRemaining").GetInt32().Should().Be(0);

        var again = await EnrolAsync(kit, who.UserId);
        again.Codes.Should().NotIntersectWith(who.Codes);
        ShouldBeCoded((await VerifyAsync(kit, await ChallengeAsync(kit), backup: who.Codes[0])).Result, 400, MfaErrorCodes.CodeInvalid);
        (await VerifyAsync(kit, await ChallengeAsync(kit), backup: again.Codes[0])).Result.Should().BeOfType<OkObjectResult>();
    }

    [Theory]
    [InlineData("secret")]
    [InlineData("flag")]
    public async Task AnyOtherPathThatEndsTheFactor_DropsTheCodes_ThroughTheSaveHook(string change)
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var who = await EnrolAsync(kit, await SeedStaffAsync(kit));
        await using (var db = kit.NewDb())
        {
            // As offboarding, welcome codes and access-mode changes do: they know nothing about backup codes.
            var user = await db.Users.SingleAsync(u => u.Id == who.UserId);
            if (change == "secret") { user.MFAEnabled = false; user.MfaSecretEncrypted = null; }
            else user.MFAEnabled = false;
            await db.SaveChangesAsync();
        }
        (await UserAsync(kit, who.UserId)).MfaRecoveryCodeHashes.Should().BeNull();
    }

    [Fact]
    public async Task TheDatabaseRefusesCodesWithoutAFactor()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var userId = await SeedStaffAsync(kit);
        await using var db = kit.NewDb();
        (await db.Users.SingleAsync(u => u.Id == userId)).MfaRecoveryCodeHashes = MfaBackupCodes.Generate().Stored;
        var save = async () => await db.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateException>();
    }

    // ── Admin reset ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reset_EndsEverything_InOneGo_AndIsAudited()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var adminId = await kit.SeedUserAsync(AdminEmail, new Pbkdf2PasswordHasher().Hash(Password), "Admin");
        var who = await EnrolAsync(kit, await SeedStaffAsync(kit));
        // A live session with a trusted browser, plus an open challenge waiting for a code.
        await ForgetStepAsync(kit, who.UserId);
        var signedIn = await VerifyAsync(kit, await ChallengeAsync(kit), totp: Totp.Now(who.Secret), remember: true);
        signedIn.Result.Should().BeOfType<OkObjectResult>();
        await ChallengeAsync(kit);
        var stampBefore = (await UserAsync(kit, who.UserId)).UpdatedAtUtc;
        await using (var db = kit.NewDb())
            (await Access(kit, db, adminId).GetUser(who.UserId, CancellationToken.None)).Result.As<OkObjectResult>().Value
                .As<UserListDto>().MfaEnabled.Should().BeTrue();
        var notifications = new CapturingNotifications();

        (await ResetAsync(kit, adminId, who.UserId, "  Lost phone on payroll day  ", notifications)).Should().BeOfType<NoContentResult>();

        var user = await UserAsync(kit, who.UserId);
        user.MFAEnabled.Should().BeFalse();
        user.MfaSecretEncrypted.Should().BeNull();
        user.MfaConfiguredAtUtc.Should().BeNull();
        user.MfaLastTotpStep.Should().BeNull();
        user.MfaRecoveryCodeHashes.Should().BeNull("backup codes are deleted");
        user.UpdatedAtUtc.Should().BeAfter(stampBefore!.Value, "the stamp rotates: signed out everywhere");
        await using (var db = kit.NewDb())
        {
            (await db.RefreshTokens.AsNoTracking().Where(r => r.UserId == who.UserId).ToListAsync())
                .Should().NotBeEmpty().And.OnlyContain(r => r.RevokedAtUtc != null);
            (await db.MfaChallengeTokens.AsNoTracking().Where(c => c.UserId == who.UserId).ToListAsync())
                .Should().NotBeEmpty().And.OnlyContain(c => c.UsedAtUtc != null);
            (await db.MfaTrustedDevices.AsNoTracking().Where(t => t.UserId == who.UserId).ToListAsync())
                .Should().ContainSingle().Which.RevokedReason.Should().Be(MfaTrustRevokeReasons.MfaAdminReset);
            (await Access(kit, db, adminId).GetUser(who.UserId, CancellationToken.None)).Result.As<OkObjectResult>().Value
                .As<UserListDto>().MfaEnabled.Should().BeFalse();
        }
        var audit = (await AuditsAsync(kit, "auth.mfa.admin_reset")).Should().ContainSingle().Subject;
        audit.UserId.Should().Be(adminId, "the actor");
        audit.EntityId.Should().Be(who.UserId.ToString(), "the subject");
        audit.TenantId.Should().Be(kit.TenantId);
        var meta = JsonDocument.Parse(audit.Metadata!).RootElement;
        meta.GetProperty("reason").GetString().Should().Be("Lost phone on payroll day");
        meta.GetProperty("subjectUserId").GetGuid().Should().Be(who.UserId);
        meta.GetProperty("sessionsRevoked").GetInt32().Should().BeGreaterThan(0);
        meta.GetProperty("trustedBrowsersRevoked").GetInt32().Should().Be(1);
        meta.GetProperty("backupCodesDeleted").GetInt32().Should().Be(10);
        var notice = notifications.Requests.Should().ContainSingle().Subject;
        notice.EventCode.Should().Be("security.mfa_admin_reset");
        notice.UserId.Should().Be(who.UserId);

        // Not required for this user, so the next sign-in is simply without a code; the old backup codes are gone.
        await using (var db = kit.NewDb())
        {
            var controller = new AuthController(kit.Auth(db)) { ControllerContext = new ControllerContext { HttpContext = Http() } };
            var login = Body(await controller.Login(new LoginRequest(StaffEmail, Password, AuthHardeningTestKit.TenantSlug), CancellationToken.None));
            login.TryGetProperty("challengeToken", out _).Should().BeFalse("no code is asked for");
            login.GetProperty("accessToken").GetString().Should().NotBeNullOrWhiteSpace();
        }
        // Reset again: it is already off.
        ShouldBeCoded(await ResetAsync(kit, adminId, who.UserId, "again"), 409, MfaErrorCodes.NotEnabled);
    }

    [Fact]
    public async Task Reset_OfYourself_Is403_AndChangesNothing()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var adminId = await kit.SeedUserAsync(AdminEmail, new Pbkdf2PasswordHasher().Hash(Password), "Admin");
        await EnrolAsync(kit, adminId);
        ShouldBeCoded(await ResetAsync(kit, adminId, adminId, "I lost my phone"), 403, MfaErrorCodes.ResetSelf);
        (await UserAsync(kit, adminId)).MFAEnabled.Should().BeTrue();
        (await AuditsAsync(kit, "auth.mfa.admin_reset")).Should().BeEmpty();
    }

    [Fact]
    public async Task Reset_OfSomeoneAboveYou_IsRefusedByTheCeiling_AndTheAttemptIsAudited()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var consoleId = await kit.SeedUserAsync("console@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), "Console Admin", "security.manage");
        var adminId = await kit.SeedUserAsync(AdminEmail, new Pbkdf2PasswordHasher().Hash(Password), "Admin");
        var admin = await EnrolAsync(kit, adminId);

        var result = await ResetAsync(kit, consoleId, admin.UserId, "Lost phone");

        var obj = result.Should().BeAssignableTo<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(403);
        Body(result).GetProperty("code").GetString().Should().NotBeNullOrWhiteSpace().And.NotBe(MfaErrorCodes.ResetSelf);
        var user = await UserAsync(kit, admin.UserId);
        user.MFAEnabled.Should().BeTrue();
        MfaBackupCodes.Remaining(user.MfaRecoveryCodeHashes).Should().Be(10);
        (await AuditsAsync(kit, "access.change_refused")).Should().ContainSingle()
            .Which.Metadata.Should().Contain("auth.mfa.admin_reset");
        (await AuditsAsync(kit, "auth.mfa.admin_reset")).Should().BeEmpty();
    }

    [Fact]
    public async Task Reset_OfAnotherTenantsUser_OrAnUnknownId_Is404()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var adminId = await kit.SeedUserAsync(AdminEmail, new Pbkdf2PasswordHasher().Hash(Password), "Admin");
        var otherTenant = new Tenant { Id = Guid.NewGuid(), Name = "Other", Slug = "other", IsActive = true };
        var stranger = new User
        {
            Id = Guid.NewGuid(), TenantId = otherTenant.Id, Email = "x@other.local", NormalizedEmail = "X@OTHER.LOCAL", FullName = "X",
            PasswordHash = new Pbkdf2PasswordHasher().Hash(Password), Status = "Active", IsActive = true, IsEmailConfirmed = true,
            MFAEnabled = true, MfaSecretEncrypted = kit.Totp.EncryptSecret(kit.Totp.GenerateBase32Secret()),
        };
        await using (var db = kit.NewDb())
        {
            db.Tenants.Add(otherTenant);
            db.Users.Add(stranger);
            await db.SaveChangesAsync();
        }

        (await ResetAsync(kit, adminId, stranger.Id, "Lost phone")).Should().BeOfType<NotFoundResult>();
        (await ResetAsync(kit, adminId, Guid.NewGuid(), "Lost phone")).Should().BeOfType<NotFoundResult>();
        (await UserAsync(kit, stranger.Id)).MFAEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Reset_OfSomeoneWithoutTwoStep_Is409()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var adminId = await kit.SeedUserAsync(AdminEmail, new Pbkdf2PasswordHasher().Hash(Password), "Admin");
        var staffId = await SeedStaffAsync(kit);
        ShouldBeCoded(await ResetAsync(kit, adminId, staffId, "Lost phone"), 409, MfaErrorCodes.NotEnabled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  x ")]
    [InlineData("ab")]
    public async Task Reset_WithoutAProperReason_Is400(string? reason)
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var adminId = await kit.SeedUserAsync(AdminEmail, new Pbkdf2PasswordHasher().Hash(Password), "Admin");
        var who = await EnrolAsync(kit, await SeedStaffAsync(kit));
        ShouldBeCoded(await ResetAsync(kit, adminId, who.UserId, reason), 400, MfaErrorCodes.ResetReason);
        ShouldBeCoded(await ResetAsync(kit, adminId, who.UserId, new string('r', 501)), 400, MfaErrorCodes.ResetReason);
        (await ResetAsync(kit, adminId, who.UserId, new string('r', 500))).Should().BeOfType<NoContentResult>();
    }

    // ── TOTP for tests ───────────────────────────────────────────────────────────────────────

    private static class Totp
    {
        public static string Now(string secret) => At(secret, 0);
        public static string At(string secret, int offsetSteps) => ForStep(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 + offsetSteps);

        private static string ForStep(string secret, long step)
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
