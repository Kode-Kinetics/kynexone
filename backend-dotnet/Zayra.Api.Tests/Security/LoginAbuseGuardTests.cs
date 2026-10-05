using System.Net;
using FluentAssertions;
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
/// Login refusals that happen BEFORE any password hashing: 10 attempts per account per 15 minutes,
/// 20 failures per client address per 10 minutes, and a client address that honours the
/// proxy-asserted header only when the shared secret is configured and presented.
/// </summary>
public sealed class LoginAbuseGuardTests
{
    private const string Password = "Correct-Horse-Battery-9!";
    private static readonly DateTime T0 = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AccountBudget_Is10Per15Minutes_AndARefusalDoesNotExtendIt()
    {
        using var guard = new LoginAbuseGuard();
        for (var i = 0; i < 10; i++)
            guard.TryBeginAccountAttempt("tenant", "acme", "a@acme.test", T0.AddSeconds(i)).Should().BeTrue();
        guard.TryBeginAccountAttempt("tenant", "acme", "a@acme.test", T0.AddMinutes(1)).Should().BeFalse();
        guard.TryBeginAccountAttempt("tenant", "ACME", " A@ACME.TEST ", T0.AddMinutes(1)).Should().BeFalse("tenant and email are normalised");
        guard.TryBeginAccountAttempt("tenant", "other", "a@acme.test", T0.AddMinutes(1)).Should().BeTrue("the key includes the tenant");
        guard.TryBeginAccountAttempt("platform", "platform", "a@acme.test", T0.AddMinutes(1)).Should().BeTrue("tenant and platform are separate scopes");
        guard.TryBeginAccountAttempt("tenant", "acme", "a@acme.test", T0.AddMinutes(15).AddSeconds(1)).Should().BeTrue("the window slides");
    }

    [Fact]
    public void IpBudget_Is20FailuresPer10Minutes()
    {
        using var guard = new LoginAbuseGuard();
        for (var i = 0; i < 19; i++) guard.RecordFailure("203.0.113.9", T0.AddSeconds(i));
        guard.IsIpBlocked("203.0.113.9", T0.AddMinutes(1)).Should().BeFalse();
        guard.RecordFailure("203.0.113.9", T0.AddMinutes(1));
        guard.IsIpBlocked("203.0.113.9", T0.AddMinutes(1)).Should().BeTrue();
        guard.IsIpBlocked("203.0.113.10", T0.AddMinutes(1)).Should().BeFalse();
        guard.IsIpBlocked("203.0.113.9", T0.AddMinutes(11)).Should().BeFalse("old failures drain out of the window");
    }

    [Fact]
    public void RetryAfter_IsJitteredBetween2And6Seconds()
    {
        var values = Enumerable.Range(0, 400).Select(_ => int.Parse(LoginAbuseGuard.JitteredRetryAfterSeconds())).ToList();
        values.Should().OnlyContain(v => v >= 2 && v <= 6);
        values.Distinct().Count().Should().BeGreaterThan(1);
    }

    private static DefaultHttpContext Http(string remote, string? assertedIp = null, string? secret = null)
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        if (assertedIp is not null) http.Request.Headers[ClientIpResolver.ClientIpHeader] = assertedIp;
        if (secret is not null) http.Request.Headers[ClientIpResolver.SecretHeader] = secret;
        return http;
    }

    [Fact]
    public void ClientIp_TrustsTheProxyHeaderOnlyWithTheConfiguredSecret()
    {
        const string proxy = "76.76.21.21";
        ClientIpResolver.Resolve(Http(proxy, "198.51.100.7", "s3cret"), null).Should().Be(proxy, "off by default");
        ClientIpResolver.Resolve(Http(proxy, "198.51.100.7"), "s3cret").Should().Be(proxy, "no secret presented");
        ClientIpResolver.Resolve(Http(proxy, "198.51.100.7", "wrong"), "s3cret").Should().Be(proxy, "wrong secret");
        ClientIpResolver.Resolve(Http(proxy, "not-an-ip", "s3cret"), "s3cret").Should().Be(proxy, "garbage IP");
        ClientIpResolver.Resolve(Http(proxy, "198.51.100.7", "s3cret"), "s3cret").Should().Be("198.51.100.7");
    }

    private sealed class CountingHasher : IPasswordHasher
    {
        private readonly Pbkdf2PasswordHasher _inner = new();
        public int Calls { get; private set; }
        public string Hash(string password) { Calls++; return _inner.Hash(password); }
        public bool Verify(string password, string passwordHash) { Calls++; return _inner.Verify(password, passwordHash); }
        public bool NeedsRehash(string passwordHash) => _inner.NeedsRehash(passwordHash);
    }

    [Fact]
    public async Task TenantLogin_RefusesAnExhaustedAccountOrAddress_BeforeAnyHashing()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await kit.SeedUserAsync("victim@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), roleName: null);
        using var guard = new LoginAbuseGuard(accountLimit: 2, ipFailureLimit: 3);

        async Task<(IActionResult Result, int Hashing, HttpResponse Response)> Attempt(string remote, string password)
        {
            var hasher = new CountingHasher();
            await using var db = kit.NewDb();
            var controller = new AuthController(kit.Auth(db, hasher), guard)
            {
                ControllerContext = new ControllerContext { HttpContext = Http(remote) },
            };
            var result = await controller.Login(
                new LoginRequest("victim@hardening.local", password, AuthHardeningTestKit.TenantSlug), CancellationToken.None);
            return (result, hasher.Calls, controller.Response);
        }

        (await Attempt("198.51.100.1", "wrong")).Result.Should().BeOfType<UnauthorizedObjectResult>();
        (await Attempt("198.51.100.2", "wrong")).Result.Should().BeOfType<UnauthorizedObjectResult>();
        var refused = await Attempt("198.51.100.3", Password);
        refused.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(429, "the account is over its attempt budget");
        refused.Hashing.Should().Be(0, "the refusal must come before any PBKDF2 work");
        int.Parse(refused.Response.Headers.RetryAfter.ToString()).Should().BeInRange(2, 6);

        // One address, many accounts: the failure budget (3 here) blocks it before hashing.
        for (var i = 0; i < 3; i++)
        {
            var hasher = new CountingHasher();
            await using var db = kit.NewDb();
            var c = new AuthController(kit.Auth(db, hasher), guard) { ControllerContext = new ControllerContext { HttpContext = Http("192.0.2.50") } };
            await c.Login(new LoginRequest($"nobody{i}@hardening.local", "x", AuthHardeningTestKit.TenantSlug), CancellationToken.None);
        }
        await using (var db = kit.NewDb())
        {
            var hasher = new CountingHasher();
            var c = new AuthController(kit.Auth(db, hasher), guard) { ControllerContext = new ControllerContext { HttpContext = Http("192.0.2.50") } };
            var blocked = await c.Login(new LoginRequest("someone@hardening.local", "x", AuthHardeningTestKit.TenantSlug), CancellationToken.None);
            blocked.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(429);
            hasher.Calls.Should().Be(0);
        }
    }

    [Fact]
    public async Task PlatformLogin_RefusesAnExhaustedAccount_BeforeAnyHashing()
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
        using var guard = new LoginAbuseGuard(accountLimit: 1);

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
        refused.Hashing.Should().Be(0);
    }
}
