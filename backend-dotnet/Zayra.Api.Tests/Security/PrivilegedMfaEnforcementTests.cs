using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;
using Zayra.Api.Tests.Platform;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Mandatory TOTP for platform operators and for tenant Admin / HR Manager / HR Director / Payroll /
/// Finance / Finance Approver, rolled out by DATE so nobody is locked out on deploy day:
/// before the date sign-in works and the user is prompted; from the date sign-in yields an enrolment
/// challenge and grace-period refresh tokens stop rotating. Break-glass: a platform-config window,
/// a per-tenant date, and owner/platform factor resets.
/// </summary>
public sealed class PrivilegedMfaEnforcementTests
{
    private const string Password = "Correct-Horse-Battery-9!";
    private static readonly DateTime Now = DateTime.UtcNow;
    private static readonly DateTime Past = Now.AddDays(-1);
    private static readonly DateTime Future = Now.AddDays(14);

    // ── Pure decision ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_CoversEveryBranch()
    {
        PrivilegedMfaPolicy.Evaluate(false, false, Past, null, Now).Status.Should().Be(PrivilegedMfaStatus.NotRequired);
        PrivilegedMfaPolicy.Evaluate(true, true, Past, null, Now).Status.Should().Be(PrivilegedMfaStatus.Satisfied);
        PrivilegedMfaPolicy.Evaluate(true, false, Future, null, Now).Status.Should().Be(PrivilegedMfaStatus.GracePeriod);
        PrivilegedMfaPolicy.Evaluate(true, false, null, null, Now).Status.Should().Be(PrivilegedMfaStatus.GracePeriod,
            "with no date configured the rule can only prompt — it must never lock anyone out by default");
        PrivilegedMfaPolicy.Evaluate(true, false, Past, null, Now).Status.Should().Be(PrivilegedMfaStatus.Enforced);
        PrivilegedMfaPolicy.Evaluate(true, false, Now, null, Now).Status.Should().Be(PrivilegedMfaStatus.Enforced,
            "enforcement starts AT the instant, not after it");

        var glass = PrivilegedMfaPolicy.Evaluate(true, false, Past, Now.AddHours(2), Now);
        glass.Status.Should().Be(PrivilegedMfaStatus.GracePeriod);
        glass.BreakGlassActive.Should().BeTrue();
    }

    [Fact]
    public void BreakGlass_IsHonouredOnlyInsideItsCap()
    {
        IConfiguration Cfg(DateTime until) => new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [PrivilegedMfaPolicy.BreakGlassConfigKey] = PrivilegedMfaPolicy.FormatDate(until) }).Build();

        PrivilegedMfaPolicy.ActiveBreakGlassUntil(Cfg(Now.AddDays(2)), Now).Should().NotBeNull();
        PrivilegedMfaPolicy.ActiveBreakGlassUntil(Cfg(Now.AddMinutes(-1)), Now).Should().BeNull("an expired window is over");
        PrivilegedMfaPolicy.ActiveBreakGlassUntil(Cfg(Now.AddDays(30)), Now).Should().BeNull(
            "a window further out than the cap is ignored, so a forgotten variable is not a permanent off switch");
        PrivilegedMfaPolicy.ActiveBreakGlassUntil(new ConfigurationBuilder().Build(), Now).Should().BeNull();
    }

    [Fact]
    public void PrivilegedRoles_AreTheAgreedSet_AndCompanyGrantsCount()
    {
        PrivilegedMfaPolicy.TenantRoles.Should().BeEquivalentTo(
            "Admin", "HR Manager", "HR Director", "Payroll Manager", "Payroll Officer", "Finance", "Finance Approver");

        User With(string role, bool active = true) => new()
        {
            UserRoles = { new UserRole { Role = new Role { Name = role, IsActive = active } } },
        };
        PrivilegedMfaPolicy.HoldsPrivilegedTenantRole(With("Admin")).Should().BeTrue();
        PrivilegedMfaPolicy.HoldsPrivilegedTenantRole(With("Employee")).Should().BeFalse();
        PrivilegedMfaPolicy.HoldsPrivilegedTenantRole(With("Admin", active: false)).Should().BeFalse();
        PrivilegedMfaPolicy.HoldsPrivilegedTenantRole(new User
        {
            EntityAccesses = { new UserEntityAccess { Role = "Finance", IsActive = true } },
        }).Should().BeTrue("a company-scoped Finance grant is still a Finance user");
    }

    // ── Tenant sign-in ────────────────────────────────────────────────────────────────────────

    private static async Task<AuthLoginResult> LoginAsync(AuthHardeningTestKit kit, string email, IConfiguration? config = null)
    {
        await using var db = kit.NewDb();
        return await kit.Auth(db, config: config).LoginAsync(
            new LoginRequest(email, Password, AuthHardeningTestKit.TenantSlug), AuthHardeningTestKit.Ctx, CancellationToken.None);
    }

    [Fact]
    public async Task BeforeTheDate_PrivilegedUserStillSignsIn_AndIsPromptedNotBlocked()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await kit.SetPlatformEnforcementDateAsync(Future);
        await kit.SeedUserAsync("admin@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), "Admin");

        var result = await LoginAsync(kit, "admin@hardening.local");

        result.Tokens.Should().NotBeNull("deploy day must not lock anyone out");
        result.RequiresMfaEnrollment.Should().BeFalse();
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("HR Manager")]
    [InlineData("HR Director")]
    [InlineData("Payroll Manager")]
    [InlineData("Payroll Officer")]
    [InlineData("Finance")]
    [InlineData("Finance Approver")]
    public async Task FromTheDate_PrivilegedUserGetsAnEnrolmentChallengeInsteadOfASession(string role)
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await kit.SetPlatformEnforcementDateAsync(Past);
        var userId = await kit.SeedUserAsync("p@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), role);

        var result = await LoginAsync(kit, "p@hardening.local");

        result.Tokens.Should().BeNull();
        result.RequiresMfaEnrollment.Should().BeTrue();
        result.EnrollmentChallenge!.ChallengeToken.Should().StartWith(AuthChallengeTokenCodec.TenantEnrollmentPurpose + ".");
        await using var db = kit.NewDb();
        (await db.AuditLogs.IgnoreQueryFilters().AnyAsync(a => a.Action == "auth.mfa_enrollment_required"
            && a.EntityId == userId.ToString() && a.Metadata!.Contains("privileged_role"))).Should().BeTrue();
        (await db.RefreshTokens.AnyAsync(r => r.UserId == userId)).Should().BeFalse("no session may be issued");
    }

    [Fact]
    public async Task FromTheDate_NonPrivilegedUsersAreUnaffected()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await kit.SetPlatformEnforcementDateAsync(Past);
        await kit.SeedUserAsync("staff@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), "Employee");

        (await LoginAsync(kit, "staff@hardening.local")).Tokens.Should().NotBeNull();
    }

    [Fact]
    public async Task TenantDate_OverridesThePlatformDate_InBothDirections()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await kit.SeedUserAsync("admin@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), "Admin");

        // Platform already enforcing, tenant pushed out (tenant-level break-glass): grace.
        await kit.SetPlatformEnforcementDateAsync(Past);
        await kit.SetTenantEnforcementDateAsync(Future);
        (await LoginAsync(kit, "admin@hardening.local")).Tokens.Should().NotBeNull();

        // Platform not yet enforcing, tenant opted in early: enforced.
        await kit.SetPlatformEnforcementDateAsync(Future);
        await kit.SetTenantEnforcementDateAsync(Past);
        (await LoginAsync(kit, "admin@hardening.local")).RequiresMfaEnrollment.Should().BeTrue();
    }

    [Fact]
    public async Task BreakGlassWindow_LetsAPrivilegedUserSignInAfterTheDate()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await kit.SetPlatformEnforcementDateAsync(Past);
        await kit.SeedUserAsync("admin@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), "Admin");
        var glass = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [PrivilegedMfaPolicy.BreakGlassConfigKey] = PrivilegedMfaPolicy.FormatDate(Now.AddHours(6)),
        }).Build();

        (await LoginAsync(kit, "admin@hardening.local", glass)).Tokens.Should().NotBeNull();
        (await LoginAsync(kit, "admin@hardening.local")).RequiresMfaEnrollment.Should().BeTrue(
            "without the window the same user is enforced again");
    }

    [Fact]
    public async Task GracePeriodRefreshTokens_StopRotatingOnceTheDatePasses()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await kit.SetPlatformEnforcementDateAsync(Future);
        await kit.SeedUserAsync("admin@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), "Admin");
        var refresh = (await LoginAsync(kit, "admin@hardening.local")).Tokens!.RefreshToken;

        await kit.SetPlatformEnforcementDateAsync(Past);

        await using var db = kit.NewDb();
        var act = () => kit.Auth(db).RefreshAsync(new RefreshTokenRequest(refresh), AuthHardeningTestKit.Ctx, CancellationToken.None);
        await act.Should().ThrowAsync<UnauthorizedAccessException>(
            "a session begun in the grace period must not outlive enforcement by more than one access token");
    }

    [Fact]
    public async Task Enrolling_ThroughTheChallenge_TurnsTheNextSignInIntoATotpChallenge()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await kit.SetPlatformEnforcementDateAsync(Past);
        var userId = await kit.SeedUserAsync("admin@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), "Admin");
        var enrollment = (await LoginAsync(kit, "admin@hardening.local")).EnrollmentChallenge!.ChallengeToken;

        await using (var db = kit.NewDb())
        {
            var mfa = kit.Mfa(db);
            var setup = await mfa.InitiateEnrollmentSetupAsync(enrollment, CancellationToken.None);
            setup.Should().NotBeNull();
            (await mfa.VerifyEnrollmentSetupAsync(enrollment,
                new MfaVerifySetupRequest(setup!.TempSecret, Totp.Now(setup.TempSecret)), CancellationToken.None))
                .Should().BeTrue();
        }

        var next = await LoginAsync(kit, "admin@hardening.local");
        next.RequiresMfa.Should().BeTrue("an enrolled user is challenged for a code");
        next.RequiresMfaEnrollment.Should().BeFalse();
        await using (var db = kit.NewDb())
            (await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId)).MFAEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task LostDevice_PlatformResetOfATenantFactor_LeadsToReEnrolment_NotLockout()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await kit.SetPlatformEnforcementDateAsync(Past);
        var userId = await kit.SeedUserAsync("admin@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), "Admin");
        await using (var db = kit.NewDb())
        {
            var user = await db.Users.SingleAsync(u => u.Id == userId);
            user.MFAEnabled = true;
            user.MfaSecretEncrypted = kit.Totp.EncryptSecret(kit.Totp.GenerateBase32Secret());
            await db.SaveChangesAsync();
        }
        (await LoginAsync(kit, "admin@hardening.local")).RequiresMfa.Should().BeTrue();

        await using (var db = kit.NewDb())
            (await kit.Mfa(db).AdminDisableAsync(userId, kit.TenantId, AuthHardeningTestKit.Ctx, CancellationToken.None))
                .Should().BeTrue();

        var after = await LoginAsync(kit, "admin@hardening.local");
        after.RequiresMfaEnrollment.Should().BeTrue("the user re-enrols a new device at the next sign-in");
        after.EnrollmentChallenge.Should().NotBeNull();
    }

    // ── Platform operators ────────────────────────────────────────────────────────────────────

    private static async Task<Guid> SeedOperatorAsync(AuthHardeningTestKit kit, string email, string role = PlatformRoles.Owner)
    {
        await using var db = kit.NewDb();
        var pu = new PlatformUser
        {
            Email = email, FullName = email, Role = role, IsActive = true,
            PasswordHash = new Pbkdf2PasswordHasher().Hash(Password),
        };
        db.PlatformUsers.Add(pu);
        await db.SaveChangesAsync();
        return pu.Id;
    }

    private static PlatformController Platform(AuthHardeningTestKit kit, Zayra.Api.Data.ZayraDbContext db, Guid? actingAs = null)
    {
        var jwt = AuthHardeningTestKit.Jwt;
        var hasher = new Pbkdf2PasswordHasher();
        var tokens = new JwtTokenService(jwt);
        var config = new ConfigurationBuilder().Build();
        var controller = new PlatformController(
            db, jwt, hasher, new FakeAuthSeeder(db, hasher), tokens, new FakePlatformEmailService(), config,
            kit.Mfa(db),
            new AccessManagementService(db, hasher, new AuditService(db), tokens, config),
            NullLogger<PlatformController>.Instance,
            new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
        var claims = new List<Claim>
        {
            new("is_platform_admin", "true"), new("platform_role", PlatformRoles.Owner),
        };
        if (actingAs is { } id) claims.Add(new Claim(ClaimTypes.NameIdentifier, id.ToString()));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
        };
        return controller;
    }

    private static JsonElement Body(IActionResult result)
        => JsonSerializer.SerializeToElement(((ObjectResult)result).Value);

    [Fact]
    public async Task PlatformOperator_BeforeTheDate_GetsASession()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await kit.SetPlatformEnforcementDateAsync(Future);
        await SeedOperatorAsync(kit, "owner@platform.test");

        await using var db = kit.NewDb();
        var body = Body(await Platform(kit, db).Login(new PlatformLoginRequest("owner@platform.test", Password), CancellationToken.None));
        body.TryGetProperty("token", out _).Should().BeTrue();
    }

    [Fact]
    public async Task PlatformOperator_FromTheDate_MustEnrol_ThenIsChallenged()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await kit.SetPlatformEnforcementDateAsync(Past);
        var id = await SeedOperatorAsync(kit, "owner@platform.test", PlatformRoles.Marketing);

        string enrollmentToken;
        await using (var db = kit.NewDb())
        {
            var body = Body(await Platform(kit, db).Login(new PlatformLoginRequest("owner@platform.test", Password), CancellationToken.None));
            body.TryGetProperty("token", out _).Should().BeFalse("no session before a factor exists");
            body.GetProperty("mfaEnrollmentRequired").GetBoolean().Should().BeTrue();
            enrollmentToken = body.GetProperty("enrollmentToken").GetString()!;
            enrollmentToken.Should().StartWith(AuthChallengeTokenCodec.PlatformEnrollmentPurpose + ".");
        }

        await using (var db = kit.NewDb())
        {
            var mfa = kit.Mfa(db);
            (await mfa.CompletePlatformChallengeAsync(enrollmentToken, "000000", AuthHardeningTestKit.Ctx, CancellationToken.None))
                .Should().BeNull("an enrolment token is never accepted as a sign-in challenge");
            var setup = await mfa.InitiatePlatformEnrollmentSetupAsync(enrollmentToken, CancellationToken.None);
            setup.Should().NotBeNull();
            (await mfa.VerifyPlatformEnrollmentSetupAsync(enrollmentToken, new MfaVerifySetupRequest(setup!.TempSecret, "000000"), CancellationToken.None))
                .Should().BeFalse("a wrong code does not enable the factor");
            (await mfa.VerifyPlatformEnrollmentSetupAsync(enrollmentToken,
                new MfaVerifySetupRequest(setup.TempSecret, Totp.Now(setup.TempSecret)), CancellationToken.None))
                .Should().BeTrue();
            (await mfa.InitiatePlatformEnrollmentSetupAsync(enrollmentToken, CancellationToken.None))
                .Should().BeNull("the enrolment token is single-use");
        }

        await using (var db = kit.NewDb())
        {
            (await db.PlatformUsers.AsNoTracking().SingleAsync(p => p.Id == id)).MfaEnabled.Should().BeTrue();
            var body = Body(await Platform(kit, db).Login(new PlatformLoginRequest("owner@platform.test", Password), CancellationToken.None));
            body.GetProperty("mfaRequired").GetBoolean().Should().BeTrue();
        }
    }

    [Fact]
    public async Task OwnerReset_ClearsAnotherOperatorsFactor_ButNeverTheirOwn()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await kit.SetPlatformEnforcementDateAsync(Past);
        var owner = await SeedOperatorAsync(kit, "owner@platform.test");
        var lost = await SeedOperatorAsync(kit, "lost@platform.test", PlatformRoles.Support);
        await using (var db = kit.NewDb())
        {
            foreach (var pu in await db.PlatformUsers.ToListAsync())
            {
                pu.MfaEnabled = true;
                pu.MfaSecretEncrypted = kit.Totp.EncryptSecret(kit.Totp.GenerateBase32Secret());
            }
            await db.SaveChangesAsync();
        }

        await using (var db = kit.NewDb())
            (await Platform(kit, db, actingAs: owner).ResetTeamMemberMfa(owner, CancellationToken.None))
                .Should().BeOfType<BadRequestObjectResult>();
        await using (var db = kit.NewDb())
            (await Platform(kit, db, actingAs: owner).ResetTeamMemberMfa(lost, CancellationToken.None))
                .Should().BeOfType<OkObjectResult>();

        await using (var db = kit.NewDb())
        {
            (await db.PlatformUsers.AsNoTracking().SingleAsync(p => p.Id == owner)).MfaEnabled.Should().BeTrue();
            (await db.PlatformUsers.AsNoTracking().SingleAsync(p => p.Id == lost)).MfaEnabled.Should().BeFalse();
            var body = Body(await Platform(kit, db).Login(new PlatformLoginRequest("lost@platform.test", Password), CancellationToken.None));
            body.GetProperty("mfaEnrollmentRequired").GetBoolean().Should().BeTrue("the operator re-enrols; they are not locked out");
        }
    }

    // ── TOTP for tests ────────────────────────────────────────────────────────────────────────

    private static class Totp
    {
        public static string Now(string base32Secret)
        {
            var key = FromBase32(base32Secret);
            var msg = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
            if (BitConverter.IsLittleEndian) Array.Reverse(msg);
            using var hmac = new System.Security.Cryptography.HMACSHA1(key);
            var hash = hmac.ComputeHash(msg);
            var offset = hash[^1] & 0x0F;
            var bin = ((hash[offset] & 0x7F) << 24) | ((hash[offset + 1] & 0xFF) << 16)
                      | ((hash[offset + 2] & 0xFF) << 8) | (hash[offset + 3] & 0xFF);
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
