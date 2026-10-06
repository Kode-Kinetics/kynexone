using System.Net;
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
using Zayra.Api.Models;
using Zayra.Api.Tests.Platform;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// The database lockout (5 wrong passwords → 15 minutes) let anyone lock a named person out from
/// anywhere. It still applies to unknown devices exactly as before, but the owner's known device —
/// a cookie issued to that account at a previous successful sign-in — gets through it with the right
/// password and resets the counter, and its own wrong passwords never trip it.
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

    // ── Tenant ──────────────────────────────────────────────────────────────────────────────

    private static async Task<(IActionResult Result, HttpResponse Response)> Tenant(
        AuthHardeningTestKit kit, LoginAbuseGuard guard, string password, string remote, string? cookie = null)
    {
        await using var db = kit.NewDb();
        var controller = new AuthController(kit.Auth(db, abuse: guard), guard)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = Http(remote, LoginAbuseGuard.TenantKnownDeviceCookie, cookie),
            },
        };
        var result = await controller.Login(new LoginRequest("victim@hardening.local", password, AuthHardeningTestKit.TenantSlug), CancellationToken.None);
        return (result, controller.Response);
    }

    private static async Task<(int Failed, bool Locked)> TenantState(AuthHardeningTestKit kit)
    {
        await using var db = kit.NewDb();
        var u = await db.Users.AsNoTracking().SingleAsync(x => x.Email == "victim@hardening.local");
        return (u.FailedLoginCount, u.IsLocked || u.LockoutEnd > DateTime.UtcNow);
    }

    [Fact]
    public async Task AnAttackerLocksTheAccountFromAnUnknownDevice_TheOwnerStillSignsInOnTheirKnownDevice()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await kit.SeedUserAsync("victim@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), roleName: null);
        using var guard = Guard();
        var first = await Tenant(kit, guard, Password, "198.51.100.10");
        first.Result.Should().BeOfType<OkObjectResult>();
        var cookie = CookieValue(first.Response, LoginAbuseGuard.TenantKnownDeviceCookie);

        for (var i = 0; i < 5; i++) await Tenant(kit, guard, "attacker-guess", "203.0.113.66");
        (await TenantState(kit)).Locked.Should().BeTrue("five misses from an unknown device lock the account, exactly as before");
        (await Tenant(kit, guard, Password, "203.0.113.66")).Result.Should().BeOfType<UnauthorizedObjectResult>(
            "an unknown device stays locked out even with the right password");

        (await Tenant(kit, guard, Password, "198.51.100.10", cookie)).Result.Should().BeOfType<OkObjectResult>(
            "the owner's known device with the right password gets through the lockout");
        (await TenantState(kit)).Should().Be((0, false), "and the counter and lockout are reset");
    }

    [Fact]
    public async Task WrongPasswordsFromAKnownDevice_DoNotLockOutUnknownDevices()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await kit.SeedUserAsync("victim@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), roleName: null);
        using var guard = Guard();
        var cookie = CookieValue((await Tenant(kit, guard, Password, "198.51.100.10")).Response, LoginAbuseGuard.TenantKnownDeviceCookie);

        for (var i = 0; i < 6; i++)
            (await Tenant(kit, guard, "typo", "198.51.100.10", cookie)).Result.Should().BeOfType<UnauthorizedObjectResult>();
        (await TenantState(kit)).Should().Be((0, false), "known-device misses are counted elsewhere, never toward the lockout");
        (await Tenant(kit, guard, Password, "192.0.2.44")).Result.Should().BeOfType<OkObjectResult>(
            "an unknown device was not locked out by them");
        await using var db = kit.NewDb();
        (await db.LoginActivities.CountAsync(a => a.FailureReason == "password_mismatch_known_device")).Should().Be(6, "they still count");
    }

    [Fact]
    public async Task TheCounterResetsAfterASuccess()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await kit.SeedUserAsync("victim@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), roleName: null);
        using var guard = Guard();
        for (var i = 0; i < 3; i++) await Tenant(kit, guard, "typo", "198.51.100.10");
        (await TenantState(kit)).Failed.Should().Be(3);
        (await Tenant(kit, guard, Password, "198.51.100.10")).Result.Should().BeOfType<OkObjectResult>();
        (await TenantState(kit)).Should().Be((0, false));
    }

    [Fact]
    public async Task AChangedPasswordRevokesTheKnownDevice()
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
        AuthHardeningTestKit kit, LoginAbuseGuard guard, string password, string remote, string? cookie = null)
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
            passwordGate: null, loginAbuse: guard)
        {
            ControllerContext = new ControllerContext { HttpContext = Http(remote, LoginAbuseGuard.PlatformKnownDeviceCookie, cookie) },
        };
        var result = await controller.Login(new PlatformLoginRequest("owner@platform.test", password), CancellationToken.None);
        return (result, controller.Response);
    }

    private static async Task<(int Failed, bool Locked)> PlatformState(AuthHardeningTestKit kit)
    {
        await using var db = kit.NewDb();
        var p = await db.PlatformUsers.AsNoTracking().SingleAsync(x => x.Email == "owner@platform.test");
        return (p.FailedLoginCount, p.LockoutEndUtc > DateTime.UtcNow);
    }

    [Fact]
    public async Task Platform_KnownDeviceGetsThroughAnAttackersLockout_AndItsOwnMissesNeverLock()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await using (var seed = kit.NewDb())
        {
            seed.PlatformUsers.Add(new PlatformUser
            {
                Email = "owner@platform.test", FullName = "Owner", Role = PlatformRoles.Owner, IsActive = true,
                PasswordHash = new Pbkdf2PasswordHasher().Hash(Password),
            });
            await seed.SaveChangesAsync();
        }
        using var guard = Guard();
        var first = await Platform(kit, guard, Password, "198.51.100.10");
        first.Result.Should().BeOfType<OkObjectResult>();
        var cookie = CookieValue(first.Response, LoginAbuseGuard.PlatformKnownDeviceCookie);

        for (var i = 0; i < PlatformUser.MaxFailedLogins; i++) await Platform(kit, guard, "attacker-guess", "203.0.113.66");
        (await PlatformState(kit)).Locked.Should().BeTrue();
        (await Platform(kit, guard, Password, "203.0.113.66")).Result.Should().BeOfType<UnauthorizedObjectResult>();
        (await Platform(kit, guard, Password, "198.51.100.10", cookie)).Result.Should().BeOfType<OkObjectResult>();
        (await PlatformState(kit)).Should().Be((0, false));

        for (var i = 0; i < 6; i++) await Platform(kit, guard, "typo", "198.51.100.10", cookie);
        (await PlatformState(kit)).Should().Be((0, false), "known-device misses never trip the unknown-device lockout");
        (await Platform(kit, guard, Password, "192.0.2.44")).Result.Should().BeOfType<OkObjectResult>();
    }
}
