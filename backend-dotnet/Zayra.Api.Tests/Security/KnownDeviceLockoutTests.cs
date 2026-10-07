using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Auth;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Models;
using Zayra.Api.Tests.Platform;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// The database lockout (5 wrong passwords → 15 minutes) let anyone lock a named person out. It still
/// applies to unknown devices exactly as before — and is never cleared by the owner. The owner's known
/// device BYPASSES an active lockout with the right password, leaving the lockout and the shared
/// failure counter alone, so an attacker on an unknown device stays locked out until it expires. A
/// known device's own wrong passwords never count toward that lockout; they count against that device
/// alone, which stops being trusted after five (and the owner is emailed).
/// </summary>
public sealed class KnownDeviceLockoutTests
{
    private const string Password = "Correct-Horse-Battery-9!";
    private static readonly IDataProtectionProvider Protection = DataProtectionProvider.Create("known-device-lockout-tests");

    private static LoginAbuseGuard Guard() => new(accountIpLimit: 100, accountLimit: 100, dataProtection: Protection);

    private static DefaultHttpContext Http(string remote, string? cookieName = null, string? cookie = null)
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        if (cookie is not null) http.Request.Headers.Cookie = $"{cookieName}={cookie}";
        return http;
    }

    private static string CookieValue(HttpResponse response, string name)
    {
        var header = response.Headers.SetCookie.ToString();
        header.Should().StartWith(name + "=");
        return header.Split(';')[0].Split('=', 2)[1];
    }

    private static ClaimsPrincipal Principal(string jwt)
        => new(new ClaimsIdentity(new JwtSecurityTokenHandler().ReadJwtToken(jwt).Claims, "test"));

    // ── Tenant ──────────────────────────────────────────────────────────────────────────────

    private static async Task<(IActionResult Result, HttpResponse Response)> Tenant(
        AuthHardeningTestKit kit, LoginAbuseGuard guard, string password, string remote, string? cookie = null,
        RecordingNotifications? outbox = null)
    {
        await using var db = kit.NewDb();
        var controller = new AuthController(kit.Auth(db, abuse: guard, notifications: outbox), guard)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = Http(remote, LoginAbuseGuard.TenantKnownDeviceCookie, cookie),
            },
        };
        var result = await controller.Login(new LoginRequest("victim@hardening.local", password, AuthHardeningTestKit.TenantSlug), CancellationToken.None);
        return (result, controller.Response);
    }

    private static async Task<(int Failed, bool Locked, DateTime? Until)> TenantState(AuthHardeningTestKit kit)
    {
        await using var db = kit.NewDb();
        var u = await db.Users.AsNoTracking().SingleAsync(x => x.Email == "victim@hardening.local");
        return (u.FailedLoginCount, u.IsLocked || u.LockoutEnd > DateTime.UtcNow, u.LockoutEnd);
    }

    private static async Task<bool> TenantSessionIsCurrent(AuthHardeningTestKit kit, IActionResult loginResult)
    {
        var token = ((AuthResponse)((OkObjectResult)loginResult).Value!).AccessToken;
        await using var db = kit.NewDb();
        return await TenantSessionSecurity.IsCurrentAsync(Principal(token), db, CancellationToken.None);
    }

    [Fact]
    public async Task Tenant_TheOwnersKnownDeviceBypassesTheLockout_WithoutClearingItForAnyoneElse()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await kit.SeedUserAsync("victim@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), roleName: null);
        using var guard = Guard();
        var outbox = new RecordingNotifications();
        var first = await Tenant(kit, guard, Password, "198.51.100.10");
        var cookie = CookieValue(first.Response, LoginAbuseGuard.TenantKnownDeviceCookie);
        (await TenantSessionIsCurrent(kit, first.Result)).Should().BeTrue();

        for (var i = 0; i < 5; i++) await Tenant(kit, guard, "attacker-guess", "203.0.113.66");
        var locked = await TenantState(kit);
        locked.Locked.Should().BeTrue("five misses from an unknown device lock the account, exactly as before");
        (await TenantSessionIsCurrent(kit, first.Result)).Should().BeFalse("the lockout still ends sessions issued before it");

        var owner = await Tenant(kit, guard, Password, "198.51.100.10", cookie, outbox);
        owner.Result.Should().BeOfType<OkObjectResult>("the owner's known device gets through the lockout");
        (await TenantSessionIsCurrent(kit, owner.Result)).Should().BeTrue("and the session it got works during the lockout");
        (await TenantState(kit)).Should().Be(locked, "the lockout and the shared counter are NOT cleared");

        (await Tenant(kit, guard, Password, "203.0.113.66")).Result.Should().BeOfType<UnauthorizedObjectResult>(
            "a correct password from an unknown device is still refused until the lockout expires");

        await using (var db = kit.NewDb())
            (await db.AuditLogs.IgnoreQueryFilters().AnyAsync(a => a.Action == "auth.lockout_bypassed_known_device")).Should().BeTrue();
        (await Tenant(kit, guard, Password, "198.51.100.10", cookie, outbox)).Result.Should().BeOfType<OkObjectResult>();
        outbox.Requests.Should().ContainSingle("one notice per lockout, however often the owner signs in during it")
            .Which.EventCode.Should().Be("security.lockout_bypassed_known_device");
        outbox.Requests[0].UserId.Should().NotBeNull("it goes to the account owner through the tenant outbox");

        // Once the lockout expires, a normal success resets the counter as before.
        await using (var db = kit.NewDb())
        {
            var u = await db.Users.SingleAsync(x => x.Email == "victim@hardening.local");
            u.LockoutEnd = DateTime.UtcNow.AddMinutes(-1);
            u.IsLocked = false;
            await db.SaveChangesAsync();
        }
        (await Tenant(kit, guard, Password, "203.0.113.66")).Result.Should().BeOfType<OkObjectResult>();
        (await TenantState(kit)).Failed.Should().Be(0);
    }

    [Fact]
    public async Task Tenant_KnownDeviceMissesNeverLock_CountPerDevice_AndWarnTheOwnerAtTheLimit()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await kit.SeedUserAsync("victim@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), roleName: null);
        using var guard = Guard();
        var outbox = new RecordingNotifications();
        var stolen = CookieValue((await Tenant(kit, guard, Password, "198.51.100.10")).Response, LoginAbuseGuard.TenantKnownDeviceCookie);
        var ownersOtherDevice = CookieValue((await Tenant(kit, guard, Password, "198.51.100.11")).Response, LoginAbuseGuard.TenantKnownDeviceCookie);

        for (var i = 0; i < LoginAbuseGuard.KnownDeviceFailureLimit; i++)
            (await Tenant(kit, guard, "thief-guess", "198.51.100.10", stolen, outbox)).Result.Should().BeOfType<UnauthorizedObjectResult>();
        (await TenantState(kit)).Failed.Should().Be(0, "known-device misses never count toward the account lockout");
        outbox.Requests.Should().ContainSingle(r => r.EventCode == "security.known_device_distrusted", "the owner is warned once, at the limit");

        // Lock the account from an unknown device; the stolen cookie no longer bypasses it…
        for (var i = 0; i < 5; i++) await Tenant(kit, guard, "attacker-guess", "203.0.113.66");
        (await Tenant(kit, guard, Password, "198.51.100.10", stolen)).Result.Should().BeOfType<UnauthorizedObjectResult>(
            "a device past its miss limit is no longer trusted");
        // …but the owner's OTHER device is untouched by the thief's misses.
        (await Tenant(kit, guard, Password, "198.51.100.11", ownersOtherDevice)).Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Tenant_AChangedPasswordRevokesTheKnownDevice()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await kit.SeedUserAsync("victim@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), roleName: null);
        using var guard = Guard();
        var cookie = CookieValue((await Tenant(kit, guard, Password, "198.51.100.10")).Response, LoginAbuseGuard.TenantKnownDeviceCookie);
        await using (var db = kit.NewDb())
        {
            var u = await db.Users.SingleAsync(x => x.Email == "victim@hardening.local");
            u.PasswordHash = new Pbkdf2PasswordHasher().Hash("A-whole-new-password-77!");
            await db.SaveChangesAsync();
        }

        for (var i = 0; i < 5; i++) await Tenant(kit, guard, "attacker-guess", "203.0.113.66");
        (await Tenant(kit, guard, "A-whole-new-password-77!", "198.51.100.10", cookie)).Result
            .Should().BeOfType<UnauthorizedObjectResult>("the old cookie was issued for the old password and no longer counts");
    }

    // ── Platform ────────────────────────────────────────────────────────────────────────────

    private static async Task<(IActionResult Result, HttpResponse Response)> Platform(
        AuthHardeningTestKit kit, LoginAbuseGuard guard, string password, string remote, string? cookie = null,
        PlatformSecurityNoticeQueue? notices = null, string address = "owner@platform.test")
    {
        await using var db = kit.NewDb();
        var hasher = new Pbkdf2PasswordHasher();
        var jwt = AuthHardeningTestKit.Jwt;
        var tokens = new JwtTokenService(jwt);
        var config = new ConfigurationBuilder().Build();
        var controller = new PlatformController(
            db, jwt, hasher, new FakeAuthSeeder(db, hasher), tokens, new FakePlatformEmailService(), config,
            kit.Mfa(db), new AccessManagementService(db, hasher, new AuditService(db), tokens, config),
            NullLogger<PlatformController>.Instance,
            new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
            passwordGate: null, loginAbuse: guard, securityNotices: notices)
        {
            ControllerContext = new ControllerContext { HttpContext = Http(remote, LoginAbuseGuard.PlatformKnownDeviceCookie, cookie) },
        };
        var result = await controller.Login(new PlatformLoginRequest(address, password), CancellationToken.None);
        return (result, controller.Response);
    }

    private static async Task<(int Failed, DateTime? Until)> PlatformState(AuthHardeningTestKit kit)
    {
        await using var db = kit.NewDb();
        var p = await db.PlatformUsers.AsNoTracking().SingleAsync(x => x.Email == "owner@platform.test");
        return (p.FailedLoginCount, p.LockoutEndUtc);
    }

    private static async Task SeedOwnerAsync(AuthHardeningTestKit kit, bool active = true)
    {
        await using var seed = kit.NewDb();
        seed.PlatformUsers.Add(new PlatformUser
        {
            Email = "owner@platform.test", FullName = "Owner", Role = PlatformRoles.Owner, IsActive = active,
            PasswordHash = new Pbkdf2PasswordHasher().Hash(Password),
        });
        await seed.SaveChangesAsync();
    }

    private static async Task<bool> PlatformSessionIsCurrent(AuthHardeningTestKit kit, IActionResult loginResult)
    {
        var body = JsonSerializer.SerializeToElement(((OkObjectResult)loginResult).Value);
        await using var db = kit.NewDb();
        return await PlatformSessionSecurity.IsCurrentAsync(Principal(body.GetProperty("token").GetString()!), db, CancellationToken.None);
    }

    [Fact]
    public async Task Platform_TheOwnersKnownDeviceBypassesTheLockout_WithoutClearingItForAnyoneElse()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await SeedOwnerAsync(kit);
        using var guard = Guard();
        using var notices = new PlatformSecurityNoticeQueue();
        var first = await Platform(kit, guard, Password, "198.51.100.10");
        var cookie = CookieValue(first.Response, LoginAbuseGuard.PlatformKnownDeviceCookie);

        for (var i = 0; i < PlatformUser.MaxFailedLogins; i++) await Platform(kit, guard, "attacker-guess", "203.0.113.66");
        var locked = await PlatformState(kit);
        locked.Until.Should().BeAfter(DateTime.UtcNow);
        (await PlatformSessionIsCurrent(kit, first.Result)).Should().BeFalse("the lockout still ends sessions issued before it");

        var owner = await Platform(kit, guard, Password, "198.51.100.10", cookie, notices);
        owner.Result.Should().BeOfType<OkObjectResult>();
        (await PlatformSessionIsCurrent(kit, owner.Result)).Should().BeTrue("the bypass session works during the lockout");
        (await PlatformState(kit)).Should().Be(locked, "the lockout and the shared counter are NOT cleared");
        (await Platform(kit, guard, Password, "203.0.113.66")).Result.Should().BeOfType<UnauthorizedObjectResult>(
            "a correct password from an unknown device is still refused until expiry");
        (await Platform(kit, guard, Password, "198.51.100.10", cookie, notices)).Result.Should().BeOfType<OkObjectResult>();
        var queued = Drain(notices);
        queued.Should().ContainSingle("one notice per lockout, queued off the sign-in path")
            .Which.Should().Match<PlatformSecurityNotice>(n => n.Kind == "lockout-bypassed" && n.Email == "owner@platform.test");
        await using var db = kit.NewDb();
        (await db.AuditLogs.IgnoreQueryFilters().AnyAsync(a => a.Action == "platform.auth.lockout_bypassed_known_device")).Should().BeTrue();
    }

    [Fact]
    public async Task Platform_KnownDeviceMisses_RecordTheClientIpAndAudit_NeverLock_AndWarnAtTheLimit()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await SeedOwnerAsync(kit);
        using var guard = Guard();
        using var notices = new PlatformSecurityNoticeQueue();
        var cookie = CookieValue((await Platform(kit, guard, Password, "198.51.100.10")).Response, LoginAbuseGuard.PlatformKnownDeviceCookie);

        for (var i = 0; i < LoginAbuseGuard.KnownDeviceFailureLimit; i++)
            await Platform(kit, guard, "typo", "198.51.100.10", cookie, notices);
        (await PlatformState(kit)).Failed.Should().Be(0);
        Drain(notices).Should().ContainSingle(n => n.Kind == "known-device-distrusted");

        await using var db = kit.NewDb();
        (await db.LoginActivities.AsNoTracking().Where(a => a.FailureReason == "password_mismatch_known_device")
            .Select(a => a.IpAddress).ToListAsync()).Should().HaveCount(5).And.OnlyContain(ip => ip == "198.51.100.10");
        (await db.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.Action == "platform.auth.login_failed"
            && a.Metadata!.Contains("password_mismatch_known_device"))).Should().Be(5, "the same audit row as the tenant path");
    }

    [Fact]
    public void NoticeQueue_RemembersADedupeKeyOnlyOnceTheNoticeIsQueued()
    {
        using var queue = new PlatformSecurityNoticeQueue(capacity: 1);
        static PlatformSecurityNotice N(string kind) => new(Guid.NewGuid(), "o@platform.test", "O", "s", "t", kind);

        queue.TryEnqueue(N("filler"), "k-filler").Should().Be(PlatformSecurityNoticeOutcome.Queued);
        queue.TryEnqueue(N("lockout"), "k-lockout").Should().Be(PlatformSecurityNoticeOutcome.Dropped);
        Drain(queue).Should().ContainSingle(n => n.Kind == "filler");

        queue.TryEnqueue(N("lockout"), "k-lockout").Should().Be(PlatformSecurityNoticeOutcome.Queued,
            "a dropped notice must not burn its dedupe key, or that lockout's email never goes out");
        queue.TryEnqueue(N("lockout"), "k-lockout").Should().Be(PlatformSecurityNoticeOutcome.Duplicate);
        Drain(queue).Should().ContainSingle(n => n.Kind == "lockout");
    }

    [Fact]
    public async Task Platform_ABypassNoticeDroppedOnAFullQueue_IsQueuedOnTheNextBypass()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await SeedOwnerAsync(kit);
        using var guard = Guard();
        using var notices = new PlatformSecurityNoticeQueue(capacity: 1);
        var cookie = CookieValue((await Platform(kit, guard, Password, "198.51.100.10")).Response, LoginAbuseGuard.PlatformKnownDeviceCookie);
        for (var i = 0; i < PlatformUser.MaxFailedLogins; i++) await Platform(kit, guard, "attacker-guess", "203.0.113.66");

        notices.TryEnqueue(new PlatformSecurityNotice(Guid.NewGuid(), "x@platform.test", "X", "s", "t", "filler"))
            .Should().Be(PlatformSecurityNoticeOutcome.Queued);
        (await Platform(kit, guard, Password, "198.51.100.10", cookie, notices)).Result.Should().BeOfType<OkObjectResult>(
            "a full notice queue never blocks the owner's sign-in");
        Drain(notices).Should().OnlyContain(n => n.Kind == "filler", "the bypass notice was dropped");

        (await Platform(kit, guard, Password, "198.51.100.10", cookie, notices)).Result.Should().BeOfType<OkObjectResult>();
        Drain(notices).Should().ContainSingle(n => n.Kind == "lockout-bypassed");
    }

    private static List<PlatformSecurityNotice> Drain(PlatformSecurityNoticeQueue queue)
    {
        var items = new List<PlatformSecurityNotice>();
        while (queue.Reader.TryRead(out var n)) items.Add(n);
        return items;
    }

    [Fact]
    public async Task Platform_TheBypassClaimCoversOnlyTheLockoutItBypassed()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await SeedOwnerAsync(kit);
        using var guard = Guard();
        var cookie = CookieValue((await Platform(kit, guard, Password, "198.51.100.10")).Response, LoginAbuseGuard.PlatformKnownDeviceCookie);
        for (var i = 0; i < PlatformUser.MaxFailedLogins; i++) await Platform(kit, guard, "attacker-guess", "203.0.113.66");
        var bypass = (await Platform(kit, guard, Password, "198.51.100.10", cookie)).Result;
        (await PlatformSessionIsCurrent(kit, bypass)).Should().BeTrue();

        async Task SetLockoutEnd(DateTime? until)
        {
            await using var db = kit.NewDb();
            await db.PlatformUsers.Where(p => p.Email == "owner@platform.test")
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.LockoutEndUtc, until));
        }

        var original = (await PlatformState(kit)).Until!.Value;
        await SetLockoutEnd(original.AddMinutes(-5));
        (await PlatformSessionIsCurrent(kit, bypass)).Should().BeTrue("an earlier end is the same lockout or less");
        await SetLockoutEnd(original.AddMinutes(30));
        (await PlatformSessionIsCurrent(kit, bypass)).Should().BeFalse(
            "a NEW, longer lockout (more attacker failures) is not covered by the old bypass");
        await SetLockoutEnd(null);
        (await PlatformSessionIsCurrent(kit, bypass)).Should().BeTrue("no lockout, no restriction");

        PlatformSessionSecurity.BypassCovers("not-a-number", original).Should().BeFalse();
        PlatformSessionSecurity.BypassCovers(PlatformSessionSecurity.LockoutClaimValue(original), original).Should().BeTrue();
    }

    [Fact]
    public async Task Platform_TheAccountWideCapCoversInactiveAccountsToo()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await SeedOwnerAsync(kit, active: false);
        using var guard = new LoginAbuseGuard(accountIpLimit: 100, accountLimit: 2, dataProtection: Protection);

        (await Platform(kit, guard, "x", "203.0.113.1")).Result.Should().BeOfType<UnauthorizedObjectResult>();
        (await Platform(kit, guard, "x", "203.0.113.2")).Result.Should().BeOfType<UnauthorizedObjectResult>();
        (await Platform(kit, guard, "x", "203.0.113.3")).Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(429);
    }
}
