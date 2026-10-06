using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
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
/// 600k-iteration PBKDF2 is about half a CPU-second per call. Login CPU is bounded by a process-wide
/// gate (short wait, then 429), and every failed sign-in does the same PBKDF2 work as a real one so
/// response time does not reveal whether an email exists.
/// </summary>
public sealed class PasswordVerificationGateTests
{
    private const string Password = "Correct-Horse-Battery-9!";

    [Fact]
    public async Task Gate_RejectsWithBusyWhenNoSlotFreesUpInTime()
    {
        using var gate = new PasswordVerificationGate(maxConcurrency: 1, maxWait: TimeSpan.FromMilliseconds(50));
        using var release = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        var holder = Task.Run(() => gate.RunAsync(() => { entered.Set(); release.Wait(); return true; }, CancellationToken.None));
        entered.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue("the holder must own the only slot before the probe");

        var act = () => gate.RunAsync(() => true, CancellationToken.None);
        await act.Should().ThrowAsync<PasswordVerificationBusyException>();

        release.Set();
        (await holder).Should().BeTrue();
        (await gate.RunAsync(() => 42, CancellationToken.None)).Should().Be(42, "the slot is released after use");
    }

    [Fact]
    public async Task TenantLogin_UnderASaturatedGate_Answers429()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await kit.SeedUserAsync("busy@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), roleName: null);
        using var gate = new PasswordVerificationGate(maxConcurrency: 1, maxWait: TimeSpan.FromMilliseconds(20));
        using var release = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        var holder = Task.Run(() => gate.RunAsync(() => { entered.Set(); release.Wait(); return true; }, CancellationToken.None));
        entered.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();

        await using var db = kit.NewDb();
        var controller = new AuthController(kit.Auth(db, gate: gate))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        var result = await controller.Login(
            new LoginRequest("busy@hardening.local", Password, AuthHardeningTestKit.TenantSlug), CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        controller.Response.Headers.RetryAfter.ToString().Should().NotBeEmpty();
        release.Set();
        await holder;
    }

    [Fact]
    public async Task TenantLogin_UnknownEmailDoesTheSamePbkdf2WorkAsAWrongPassword()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await kit.SeedUserAsync("known@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), roleName: null);

        async Task<int> VerifiesFor(string email)
        {
            var counting = new CountingHasher();
            await using var db = kit.NewDb();
            var act = () => kit.Auth(db, counting).LoginAsync(
                new LoginRequest(email, "wrong-password", AuthHardeningTestKit.TenantSlug),
                AuthHardeningTestKit.Ctx, CancellationToken.None);
            await act.Should().ThrowAsync<UnauthorizedAccessException>();
            return counting.Verifies;
        }

        var unknown = await VerifiesFor("nobody@hardening.local");
        var wrong = await VerifiesFor("known@hardening.local");
        unknown.Should().Be(1).And.Be(wrong, "an unknown email must cost exactly what a wrong password costs");
    }

    [Fact]
    public async Task PlatformLogin_UnknownEmailDoesTheSamePbkdf2WorkAsAWrongPassword()
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

        async Task<(int Verifies, IActionResult Result)> Attempt(string email)
        {
            var counting = new CountingHasher();
            await using var db = kit.NewDb();
            var jwt = AuthHardeningTestKit.Jwt;
            var tokens = new JwtTokenService(jwt);
            var config = new ConfigurationBuilder().Build();
            var controller = new PlatformController(
                db, jwt, counting, new FakeAuthSeeder(db, counting), tokens, new FakePlatformEmailService(), config,
                kit.Mfa(db), new AccessManagementService(db, counting, new AuditService(db), tokens, config),
                NullLogger<PlatformController>.Instance,
                new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()))
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            };
            var result = await controller.Login(new PlatformLoginRequest(email, "wrong-password"), CancellationToken.None);
            return (counting.Verifies, result);
        }

        var unknown = await Attempt("nobody@platform.test");
        var wrong = await Attempt("owner@platform.test");
        unknown.Result.Should().BeOfType<UnauthorizedObjectResult>();
        wrong.Result.Should().BeOfType<UnauthorizedObjectResult>();
        unknown.Verifies.Should().Be(1).And.Be(wrong.Verifies);
    }

    private sealed class CountingHasher : IPasswordHasher
    {
        private readonly Pbkdf2PasswordHasher _inner = new();
        public int Verifies { get; private set; }
        public string Hash(string password) => _inner.Hash(password);
        public bool Verify(string password, string passwordHash) { Verifies++; return _inner.Verify(password, passwordHash); }
        public bool NeedsRehash(string passwordHash) => _inner.NeedsRehash(passwordHash);
    }
}
