using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Auth;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Http;
using Zayra.Api.Models;
using Zayra.Api.Tests.Platform;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Sign-in refusals that happen BEFORE any password hashing:
/// (account, client IP) 10 per 15 min; account overall 50 per 15 min unless the browser is a known
/// device for that account; and a per-address budget of 150 failures against UNKNOWN accounts per
/// 10 min, applied only when the address identifies one client.
/// </summary>
public sealed class LoginAbuseGuardTests
{
    private const string Password = "Correct-Horse-Battery-9!";
    private static readonly DateTime T0 = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly ClientAddress DirectA = new("198.51.100.1", ClientIpSource.Direct);
    private static readonly ClientAddress DirectB = new("198.51.100.2", ClientIpSource.Direct);
    private static readonly IDataProtectionProvider Protection = DataProtectionProvider.Create("login-abuse-tests");

    private static LoginAbuseGuard Guard(int accountIp = 10, int account = 50, int ipFailures = 150)
        => new(accountIp, account, ipFailureLimit: ipFailures, dataProtection: Protection);

    // ── Account limits ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void PerAccountAndAddress_Is10Per15Minutes_AndDoesNotLockOtherAddresses()
    {
        using var guard = Guard();
        for (var i = 0; i < 10; i++)
            guard.TryBegin("tenant", "acme", "victim@acme.test", DirectA, false, T0.AddSeconds(i)).Should().BeNull();
        guard.TryBegin("tenant", "acme", "victim@acme.test", DirectA, false, T0.AddMinutes(1)).Should().Be(LoginRefusal.AccountLimit);
        guard.TryBegin("tenant", "ACME", " VICTIM@ACME.TEST ", DirectA, false, T0.AddMinutes(1))
            .Should().Be(LoginRefusal.AccountLimit, "tenant and email are normalised");
        guard.TryBegin("tenant", "acme", "victim@acme.test", DirectB, false, T0.AddMinutes(1))
            .Should().BeNull("an attacker at one address cannot lock the victim out everywhere");
        guard.TryBegin("tenant", "acme", "victim@acme.test", DirectA, false, T0.AddMinutes(15).AddSeconds(10))
            .Should().BeNull("the window slides, and refused attempts were not recorded");
    }

    [Fact]
    public void AccountOverall_Is50Per15Minutes_UnlessTheBrowserIsAKnownDevice()
    {
        using var guard = Guard();
        for (var i = 0; i < 50; i++)
            guard.TryBegin("tenant", "acme", "victim@acme.test", new ClientAddress($"203.0.113.{i}", ClientIpSource.Direct), false, T0)
                .Should().BeNull();
        var fresh = new ClientAddress("192.0.2.200", ClientIpSource.Direct);
        guard.TryBegin("tenant", "acme", "victim@acme.test", fresh, false, T0.AddSeconds(1))
            .Should().Be(LoginRefusal.AccountLimit, "a distributed guess hits the account-wide cap");
        guard.TryBegin("tenant", "acme", "victim@acme.test", fresh, knownDevice: true, T0.AddSeconds(1))
            .Should().BeNull("the account owner's known device is exempt from the account-wide cap");
    }

    [Fact]
    public void KnownDevice_DoesNotBypassThePerAddressLimit()
    {
        using var guard = Guard();
        for (var i = 0; i < 10; i++) guard.TryBegin("tenant", "acme", "u@acme.test", DirectA, true, T0).Should().BeNull();
        guard.TryBegin("tenant", "acme", "u@acme.test", DirectA, knownDevice: true, T0).Should().Be(LoginRefusal.AccountLimit);
    }

    // ── IP failure budget ───────────────────────────────────────────────────────────────────

    [Fact]
    public void IpBudget_Is150UnknownAccountFailures_AndOnlyForAddressesThatIdentifyOneClient()
    {
        using var guard = Guard();
        var proxied = new ClientAddress("76.76.21.21", ClientIpSource.UnverifiedProxy);
        var asserted = new ClientAddress("198.51.100.77", ClientIpSource.AuthenticatedProxy);
        for (var i = 0; i < 150; i++)
        {
            guard.RecordUnknownAccountFailure(DirectA, T0);
            guard.RecordUnknownAccountFailure(proxied, T0);
            guard.RecordUnknownAccountFailure(asserted, T0);
        }

        guard.TryBegin("tenant", "acme", "anyone@acme.test", DirectA, false, T0.AddMinutes(1)).Should().Be(LoginRefusal.IpFailureBudget);
        guard.TryBegin("tenant", "acme", "anyone@acme.test", asserted, false, T0.AddMinutes(1)).Should().Be(LoginRefusal.IpFailureBudget);
        guard.TryBegin("tenant", "acme", "anyone@acme.test", proxied, false, T0.AddMinutes(1))
            .Should().BeNull("without the proxy secret every web user shares that address; a budget there would lock everyone out");
        guard.TryBegin("tenant", "acme", "anyone@acme.test", DirectA, false, T0.AddMinutes(11)).Should().BeNull("old failures drain");

        using var smaller = Guard();
        for (var i = 0; i < 149; i++) smaller.RecordUnknownAccountFailure(DirectB, T0);
        smaller.TryBegin("tenant", "acme", "x@acme.test", DirectB, false, T0).Should().BeNull("149 is under the default budget of 150");
    }

    [Fact]
    public void ClientAddress_SourceIsAuthenticatedDirectOrUnverified()
    {
        const string proxy = "76.76.21.21";
        static DefaultHttpContext Ctx(string remote, params (string, string)[] headers)
        {
            var http = new DefaultHttpContext();
            http.Connection.RemoteIpAddress = IPAddress.Parse(remote);
            foreach (var (k, v) in headers) http.Request.Headers[k] = v;
            return http;
        }

        ClientIpResolver.ResolveAddress(Ctx(proxy), null).Should().Be(new ClientAddress(proxy, ClientIpSource.Direct));
        ClientIpResolver.ResolveAddress(Ctx(proxy, (ClientIpResolver.ViaProxyHeader, "1")), null)
            .Should().Be(new ClientAddress(proxy, ClientIpSource.UnverifiedProxy));
        ClientIpResolver.ResolveAddress(Ctx(proxy, ("x-vercel-id", "fra1::abc")), null).Source.Should().Be(ClientIpSource.UnverifiedProxy);
        ClientIpResolver.ResolveAddress(Ctx(proxy, (ClientIpResolver.ViaProxyHeader, "1"),
                (ClientIpResolver.ClientIpHeader, "198.51.100.7"), (ClientIpResolver.SecretHeader, "wrong")), "s3cret")
            .Should().Be(new ClientAddress(proxy, ClientIpSource.Direct),
                "with a secret configured, a proxy claim without it is a direct caller and keeps the per-IP budget");
        foreach (var marker in new[] { ClientIpResolver.ViaProxyHeader, "x-vercel-id", "x-vercel-forwarded-for" })
            ClientIpResolver.ResolveAddress(Ctx(proxy, (marker, "1")), "s3cret").Source
                .Should().Be(ClientIpSource.Direct, $"{marker} without the secret proves nothing once a secret exists");
        ClientIpResolver.ResolveAddress(Ctx(proxy, (ClientIpResolver.ViaProxyHeader, "1"),
                (ClientIpResolver.ClientIpHeader, "198.51.100.7"), (ClientIpResolver.SecretHeader, "s3cret")), "s3cret")
            .Should().Be(new ClientAddress("198.51.100.7", ClientIpSource.AuthenticatedProxy));
        ClientIpResolver.ResolveAddress(Ctx(proxy, (ClientIpResolver.ClientIpHeader, "198.51.100.7"),
                (ClientIpResolver.SecretHeader, "s3cret")), null)
            .Ip.Should().Be(proxy, "with no secret configured the asserted IP is never read");
    }

    // ── Known device ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void KnownDeviceToken_IsBoundToTheAccount_ThePrincipalAndTheCredentials_AndExpires()
    {
        using var guard = Guard();
        var id = Guid.NewGuid();
        var version = LoginAbuseGuard.CredentialVersion("PBKDF2$600000$salt$key", false, null);
        var device = Guid.NewGuid();
        var token = guard.IssueKnownDeviceToken("tenant", "acme", "u@acme.test", id, version, device, DateTime.UtcNow)!;
        guard.TryReadKnownDevice(token, "tenant", "acme", "u@acme.test", id, version, out var read).Should().BeTrue();
        read.Should().Be(device, "the token carries a stable per-device id");

        guard.IsKnownDevice(token, "tenant", "ACME", "U@acme.test", id, version).Should().BeTrue();
        guard.IsKnownDevice(token, "tenant", "acme", "other@acme.test", id, version).Should().BeFalse("bound to one account");
        guard.IsKnownDevice(token, "platform", "platform", "u@acme.test", id, version).Should().BeFalse("bound to one scope");
        guard.IsKnownDevice(token, "tenant", "acme", "u@acme.test", Guid.NewGuid(), version)
            .Should().BeFalse("a re-created user has a new id");
        guard.IsKnownDevice(token, "tenant", "acme", "u@acme.test", id,
            LoginAbuseGuard.CredentialVersion("PBKDF2$600000$salt$NEWKEY", false, null)).Should().BeFalse("password changed");
        guard.IsKnownDevice(token, "tenant", "acme", "u@acme.test", id,
            LoginAbuseGuard.CredentialVersion("PBKDF2$600000$salt$key", true, T0)).Should().BeFalse("MFA enrolled since");
        LoginAbuseGuard.CredentialVersion("h", true, T0).Should().NotBe(LoginAbuseGuard.CredentialVersion("h", true, T0.AddTicks(10)),
            "re-enrolling MFA (new configured time) is a new credential");
        guard.IsKnownDevice(token[..^4] + "AAAA", "tenant", "acme", "u@acme.test", id, version).Should().BeFalse("tampered");
        guard.IsKnownDevice(guard.IssueKnownDeviceToken("tenant", "acme", "u@acme.test", id, version, device, DateTime.UtcNow.AddDays(-91)),
            "tenant", "acme", "u@acme.test", id, version).Should().BeFalse("older than 90 days");
        using var otherRing = new LoginAbuseGuard(dataProtection: DataProtectionProvider.Create("another-key-ring"));
        otherRing.IsKnownDevice(token, "tenant", "acme", "u@acme.test", id, version).Should().BeFalse();
    }

    [Fact]
    public void AKnownDeviceThatKeepsGuessing_StopsBeingTrusted_WithoutTouchingTheOwnersOtherDevices()
    {
        LoginAbuseGuard.KnownDeviceFailureLimit.Should().Be(5);
        using var guard = Guard();
        var stolen = Guid.NewGuid();
        var other = Guid.NewGuid();
        for (var i = 0; i < LoginAbuseGuard.KnownDeviceFailureLimit - 1; i++)
            guard.RecordKnownDeviceFailure("tenant", "acme", "u@acme.test", stolen, T0).Should().BeFalse();
        guard.KnownDeviceStillTrusted("tenant", "acme", "u@acme.test", stolen, T0).Should().BeTrue();
        guard.RecordKnownDeviceFailure("tenant", "acme", "u@acme.test", stolen, T0)
            .Should().BeTrue("reaching the limit is reported once, so the owner can be warned");
        guard.KnownDeviceStillTrusted("tenant", "acme", "u@acme.test", stolen, T0).Should().BeFalse(
            "a stolen cookie is not an unlimited guessing licence");
        guard.KnownDeviceStillTrusted("tenant", "acme", "u@acme.test", other, T0).Should().BeTrue(
            "misses are counted per device");
        guard.RecordKnownDeviceFailure("tenant", "acme", "u@acme.test", stolen, T0).Should().BeFalse("only the crossing is reported");
    }

    [Fact]
    public void RetryAfterSeconds_IsTheTimeUntilTheBlockingWindowFrees_AndTheWordsMatchIt()
    {
        using var guard = Guard(accountIp: 2);
        guard.TryBegin("tenant", "acme", "u@acme.test", DirectA, false, T0).Should().BeNull();
        guard.TryBegin("tenant", "acme", "u@acme.test", DirectA, false, T0.AddMinutes(5)).Should().BeNull();
        var refusal = guard.TryBegin("tenant", "acme", "u@acme.test", DirectA, false, T0.AddMinutes(6));
        refusal.Should().Be(LoginRefusal.AccountLimit);
        guard.RetryAfterSeconds(refusal!.Value, "tenant", "acme", "u@acme.test", DirectA, T0.AddMinutes(6))
            .Should().Be(9 * 60, "the oldest attempt (T0) leaves the 15-minute window at T0+15");

        LoginAbuseGuard.WaitPhrase(10).Should().Be("in a few seconds");
        LoginAbuseGuard.WaitPhrase(11).Should().Be("in about a minute");
        LoginAbuseGuard.WaitPhrase(90).Should().Be("in about a minute");
        LoginAbuseGuard.WaitPhrase(91).Should().Be("in a few minutes");
        LoginAbuseGuard.WaitPhrase(300).Should().Be("in a few minutes");
        LoginAbuseGuard.WaitPhrase(301).Should().Be("in about 6 minutes");
        LoginAbuseGuard.WaitPhrase(15 * 60).Should().Be("in about 15 minutes");
        LoginAbuseGuard.Describe(LoginRefusal.IpFailureBudget, 5).Message.Should().EndWith("in a few seconds.");
        new PasswordVerificationBusyException().Message.Should().Contain("in a few seconds",
            "the gate's Retry-After is 2-6 s, so its words say so");
    }

    [Fact]
    public void RetryAfter_IsJitteredBetween2And6Seconds()
    {
        var values = Enumerable.Range(0, 400).Select(_ => int.Parse(LoginAbuseGuard.JitteredRetryAfterSeconds())).ToList();
        values.Should().OnlyContain(v => v >= 2 && v <= 6);
        values.Distinct().Count().Should().BeGreaterThan(1);
    }

    // ── Through the controllers ─────────────────────────────────────────────────────────────

    private sealed class CountingHasher : IPasswordHasher
    {
        private readonly Pbkdf2PasswordHasher _inner = new();
        public int Calls { get; private set; }
        public string Hash(string password) { Calls++; return _inner.Hash(password); }
        public bool Verify(string password, string passwordHash) { Calls++; return _inner.Verify(password, passwordHash); }
        public bool NeedsRehash(string passwordHash) => _inner.NeedsRehash(passwordHash);
    }

    private static DefaultHttpContext Http(string remote, string? cookie = null, bool viaProxy = false)
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        if (cookie is not null) http.Request.Headers.Cookie = $"{LoginAbuseGuard.TenantKnownDeviceCookie}={cookie}";
        if (viaProxy) http.Request.Headers[ClientIpResolver.ViaProxyHeader] = "1";
        return http;
    }

    private static async Task<(IActionResult Result, int Hashing, HttpResponse Response)> TenantLogin(
        AuthHardeningTestKit kit, LoginAbuseGuard guard, string email, string password, DefaultHttpContext http)
    {
        var hasher = new CountingHasher();
        await using var db = kit.NewDb();
        var controller = new AuthController(kit.Auth(db, hasher, abuse: guard), guard) { ControllerContext = new ControllerContext { HttpContext = http } };
        var result = await controller.Login(new LoginRequest(email, password, AuthHardeningTestKit.TenantSlug), CancellationToken.None);
        return (result, hasher.Calls, controller.Response);
    }

    private static string? ErrorCode(IActionResult result)
        => result is ObjectResult { Value: { } value } ? value.GetType().GetProperty("error")?.GetValue(value) as string : null;

    [Fact]
    public async Task TenantLogin_AccountLimit_RefusesBeforeHashing_WithItsOwnErrorCode()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await kit.SeedUserAsync("victim@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), roleName: null);
        using var guard = Guard(accountIp: 2);

        (await TenantLogin(kit, guard, "victim@hardening.local", "wrong", Http("198.51.100.1"))).Result.Should().BeOfType<UnauthorizedObjectResult>();
        (await TenantLogin(kit, guard, "victim@hardening.local", "wrong", Http("198.51.100.1"))).Result.Should().BeOfType<UnauthorizedObjectResult>();
        var refused = await TenantLogin(kit, guard, "victim@hardening.local", Password, Http("198.51.100.1"));
        refused.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(429);
        ErrorCode(refused.Result).Should().Be("account_rate_limited");
        refused.Hashing.Should().Be(0, "the refusal comes before any PBKDF2 work");
        var retry = int.Parse(refused.Response.Headers.RetryAfter.ToString());
        retry.Should().BeInRange(14 * 60, 15 * 60, "Retry-After is when the account's window actually frees up");
        ((ObjectResult)refused.Result).Value!.GetType().GetProperty("message")!.GetValue(((ObjectResult)refused.Result).Value)
            .Should().Be("Too many sign-in attempts for this account. Please try again in about 15 minutes.");

        (await TenantLogin(kit, guard, "victim@hardening.local", Password, Http("198.51.100.9"))).Result
            .Should().BeOfType<OkObjectResult>("the victim signs in from their own address");
    }

    [Fact]
    public async Task TenantLogin_OnlyUnknownAccountFailuresSpendTheIpBudget_AndNeverBehindAnUnverifiedProxy()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await kit.SeedUserAsync("real@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), roleName: null);
        using var guard = Guard(accountIp: 100, account: 100, ipFailures: 3);

        // Wrong passwords on a REAL account do not count towards the address budget (4 > budget of 3,
        // and under the account's own lockout of 5).
        for (var i = 0; i < 4; i++)
            await TenantLogin(kit, guard, "real@hardening.local", "wrong", Http("192.0.2.10"));
        (await TenantLogin(kit, guard, "real@hardening.local", Password, Http("192.0.2.10"))).Result.Should().BeOfType<OkObjectResult>();

        // Unknown accounts from a direct address do.
        for (var i = 0; i < 3; i++)
            await TenantLogin(kit, guard, $"ghost{i}@hardening.local", "x", Http("192.0.2.20"));
        var blocked = await TenantLogin(kit, guard, "someone@hardening.local", "x", Http("192.0.2.20"));
        ErrorCode(blocked.Result).Should().Be("ip_failure_budget");
        blocked.Hashing.Should().Be(0);

        // Behind the proxy without the secret, the shared address is never blocked.
        for (var i = 0; i < 6; i++)
            await TenantLogin(kit, guard, $"spray{i}@hardening.local", "x", Http("76.76.21.21", viaProxy: true));
        (await TenantLogin(kit, guard, "real@hardening.local", Password, Http("76.76.21.21", viaProxy: true))).Result
            .Should().BeOfType<OkObjectResult>("one sprayer must not lock every web user out");
    }

    [Fact]
    public async Task TenantLogin_SuccessSetsAHardenedKnownDeviceCookie_ThatLiftsTheAccountWideCap()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await kit.SeedUserAsync("owner@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), roleName: null);
        using var guard = Guard(accountIp: 10, account: 3);

        var ok = await TenantLogin(kit, guard, "owner@hardening.local", Password, Http("198.51.100.50"));
        ok.Result.Should().BeOfType<OkObjectResult>();
        var setCookie = ok.Response.Headers.SetCookie.ToString();
        setCookie.Should().StartWith(LoginAbuseGuard.TenantKnownDeviceCookie + "=");
        setCookie.ToLowerInvariant().Should().Contain("httponly").And.Contain("secure")
            .And.Contain("samesite=strict").And.Contain("path=/api/auth");
        var cookie = setCookie.Split(';')[0].Split('=', 2)[1];

        // An attacker exhausts the account-wide cap from other addresses…
        for (var i = 0; i < 2; i++)
            await TenantLogin(kit, guard, "owner@hardening.local", "wrong", Http($"203.0.113.{i}"));
        ErrorCode((await TenantLogin(kit, guard, "owner@hardening.local", Password, Http("198.51.100.60"))).Result)
            .Should().Be("account_rate_limited");
        // …but the owner's known browser still gets in.
        (await TenantLogin(kit, guard, "owner@hardening.local", Password, Http("198.51.100.60", cookie))).Result
            .Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task PlatformLogin_AccountLimit_RefusesBeforeHashing()
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
        using var guard = Guard(accountIp: 1);

        async Task<(IActionResult Result, int Hashing)> Attempt()
        {
            var hasher = new CountingHasher();
            await using var db = kit.NewDb();
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
                ControllerContext = new ControllerContext { HttpContext = Http("198.51.100.20") },
            };
            var result = await controller.Login(new PlatformLoginRequest("owner@platform.test", "wrong"), CancellationToken.None);
            return (result, hasher.Calls);
        }

        (await Attempt()).Result.Should().BeOfType<UnauthorizedObjectResult>();
        var refused = await Attempt();
        refused.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(429);
        ErrorCode(refused.Result).Should().Be("account_rate_limited");
        refused.Hashing.Should().Be(0);
    }
}
