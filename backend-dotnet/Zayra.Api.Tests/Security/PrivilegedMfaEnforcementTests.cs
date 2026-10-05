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
    public async Task EveryCataloguePermission_IsDeliberatelyClassified()
    {
        var (db, _) = await SeededRoleBundles.NewTenantAsync("mfa-catalogue");
        await using var _db = db;
        var catalogue = await db.Permissions.AsNoTracking().Select(p => p.Key).ToListAsync();

        PrivilegedMfaPolicy.PrivilegedPermissions.Should().NotIntersectWith(PrivilegedMfaPolicy.NonPrivilegedPermissions);
        var unclassified = catalogue
            .Where(k => !PrivilegedMfaPolicy.PrivilegedPermissions.Contains(k) && !PrivilegedMfaPolicy.NonPrivilegedPermissions.Contains(k))
            .ToList();
        unclassified.Should().BeEmpty(
            "a new permission must be classified on purpose. Runtime already treats it as privileged (MFA required); "
            + "add it to PrivilegedMfaPolicy.PrivilegedPermissions, or to NonPrivilegedPermissions if it is self-service, "
            + "read-only or a line manager's own-team decision");
        catalogue.Should().Contain(PrivilegedMfaPolicy.PrivilegedPermissions.Concat(PrivilegedMfaPolicy.NonPrivilegedPermissions),
            "every classified key must be real — a typo in the non-privileged list would silently exempt a permission");
        PrivilegedMfaPolicy.IsPrivilegedPermission("some.future_permission").Should().BeTrue("unknown keys default to privileged");
    }

    [Theory]
    [InlineData("security.manage")]
    [InlineData("payroll.export")]
    [InlineData("payroll.rates.manage")]
    [InlineData("payroll.rates.statutory_override")]
    [InlineData("payroll.structure_manage")]
    [InlineData("finance.gl.manage")]
    [InlineData("approvals.override")]
    [InlineData("qiwa.configure")]
    [InlineData("employees.bulk_import")]
    [InlineData("users.manage")]
    [InlineData("roles.manage")]
    [InlineData("payroll.write")]
    [InlineData("payroll.approve")]
    [InlineData("payroll.lock")]
    [InlineData("employees.sensitive")]
    [InlineData("loans.approve")]
    public void NamedHighRiskPermissions_ArePrivileged(string key)
        => PrivilegedMfaPolicy.IsPrivilegedPermission(key).Should().BeTrue();

    [Fact]
    public void EveryPermissionGuardingAccessIdentitySetupAndBankExports_IsPrivileged()
    {
        var api = FindApiSource();
        if (api is null)
        {
            if (Environment.GetEnvironmentVariable("CI") is "true" or "1")
                throw new Xunit.Sdk.XunitException("Zayra.Api source not found under CI.");
            return;
        }
        var literal = new System.Text.RegularExpressions.Regex(@"HasPermission\(\s*""([a-z0-9_.]+)""");
        var found = new List<(string File, string Key)>();
        foreach (var file in new[]
                 {
                     "Controllers/AccessController.cs", "Controllers/EnterpriseIdentityController.cs",
                     "Controllers/Admin/SetupSettingsController.cs", "Controllers/SaudiBankExportsController.cs",
                 })
            found.AddRange(literal.Matches(File.ReadAllText(Path.Combine(api, file))).Select(m => (file, m.Groups[1].Value)));

        // The WPS / bank-file endpoints in PayrollController check their permission inline.
        var payroll = File.ReadAllLines(Path.Combine(api, "Controllers/PayrollController.cs"));
        for (var i = 0; i < payroll.Length; i++)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(payroll[i], @"\[Http\w+\(""[^""]*(wps|payment-batches|bank)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                continue;
            var body = string.Join('\n', payroll.Skip(i).Take(25));
            found.AddRange(literal.Matches(body).Select(m => ("PayrollController (WPS/bank)", m.Groups[1].Value)));
        }

        found.Should().NotBeEmpty("the scan must still be matching the guards it is meant to check");
        found.Select(f => f.Key).Should().Contain("security.manage").And.Contain("payroll.export");
        found.Where(f => !PrivilegedMfaPolicy.IsPrivilegedPermission(f.Key))
            .Select(f => $"{f.File}: {f.Key}")
            .Should().BeEmpty("whoever can manage access, identity, setup or bank/WPS files must use MFA");
    }

    private static string? FindApiSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "backend-dotnet", "Zayra.Api");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    [Fact]
    public async Task SeededRoles_PrivilegeFollowsTheirPermissions_AndSelfServiceRolesStayExempt()
    {
        var (db, tenantId) = await SeededRoleBundles.NewTenantAsync("mfa-priv");
        await using var _ = db;
        var roles = await db.Roles.AsNoTracking()
            .Where(r => r.TenantId == tenantId)
            .Include(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .ToListAsync();

        var privileged = roles
            .Where(r => r.RolePermissions.Any(rp => PrivilegedMfaPolicy.IsPrivilegedPermission(rp.Permission!.Key)))
            .Select(r => r.Name)
            .ToList();

        privileged.Should().BeEquivalentTo(new[]
        {
            "Admin", "HR Director", "HR Manager", "Payroll Manager", "Payroll Officer", "Finance", "Finance Approver",
            "HR Officer",          // employees.write, employees.bulk_import, employees.documents, …
            "Compliance Officer",  // compliance.write, employees.documents
            "Supervisor",          // attendance.write (drives overtime and deductions)
            "Recruiter",           // recruitment.write (candidate PII, offers)
        });
        privileged.Should().NotContain(new[] { "Employee", "Manager", "HR Assistant", "Auditor", "Kiosk Operator" },
            "self-service, read-only and own-team roles must not be forced onto MFA");
    }

    [Fact]
    public async Task ARenamedSeededRole_IsStillEnforced()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await kit.SetPlatformEnforcementDateAsync(Past);
        await kit.SeedUserAsync("admin@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), "Admin");
        await using (var db = kit.NewDb())
        {
            var admin = await db.Roles.SingleAsync(r => r.TenantId == kit.TenantId && r.Name == "Admin");
            admin.Name = "Workspace Owner";
            admin.NormalizedName = "WORKSPACE OWNER";
            await db.SaveChangesAsync();
        }

        (await LoginAsync(kit, "admin@hardening.local")).RequiresMfaEnrollment.Should().BeTrue(
            "renaming the role changes nothing about what it can do");
    }

    [Fact]
    public async Task ACustomRoleWithPrivilegedPermissions_IsEnforced_AndOneWithout_IsNot()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await kit.SetPlatformEnforcementDateAsync(Past);
        var hash = new Pbkdf2PasswordHasher().Hash(Password);
        await kit.SeedUserAsync("desk@hardening.local", hash, "Salary Desk", "payroll.read", "payroll.write", "employees.sensitive");
        await kit.SeedUserAsync("viewer@hardening.local", hash, "Roster Viewer", "employees.read", "attendance.read", "payroll.read");

        (await LoginAsync(kit, "desk@hardening.local")).RequiresMfaEnrollment.Should().BeTrue(
            "a custom role that can run payroll and read bank details is a Payroll role whatever it is called");
        (await LoginAsync(kit, "viewer@hardening.local")).Tokens.Should().NotBeNull();
    }

    [Theory]
    [InlineData("users.manage")]
    [InlineData("security.manage")]
    [InlineData("payroll.export")]
    [InlineData("employees.sensitive")]
    [InlineData("approvals.override")]
    [InlineData("a.permission_nobody_has_classified")]
    public async Task EachPrivilegedPermission_OnItsOwn_TriggersEnforcement(string permission)
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await kit.SetPlatformEnforcementDateAsync(Past);
        await kit.SeedUserAsync("one@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), $"Only {permission}", permission);

        (await LoginAsync(kit, "one@hardening.local")).RequiresMfaEnrollment.Should().BeTrue();
    }

    [Fact]
    public async Task ACompanyGrantNamingAPrivilegedRole_IsEnforced()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await kit.SetPlatformEnforcementDateAsync(Past);
        // Make sure the seeded "Finance" role exists, then give a role-less user only a company grant.
        await kit.SeedUserAsync("seed@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), "Finance");
        var userId = await kit.SeedUserAsync("grant@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), roleName: null);
        await using (var db = kit.NewDb())
        {
            db.UserEntityAccesses.Add(new UserEntityAccess
            {
                TenantId = kit.TenantId, UserId = userId, CompanyId = null, Role = "Finance", IsActive = true,
                GrantMode = EntityGrantModes.AllCurrentCompanies,
            });
            await db.SaveChangesAsync();
        }

        (await LoginAsync(kit, "grant@hardening.local")).RequiresMfaEnrollment.Should().BeTrue();
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
                .Should().BeNull("a wrong code does not enable the factor");
            (await mfa.VerifyPlatformEnrollmentSetupAsync(enrollmentToken,
                new MfaVerifySetupRequest(setup.TempSecret, Totp.Now(setup.TempSecret)), CancellationToken.None))
                .Should().HaveCount(10, "enrolment hands out the one-time recovery codes");
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

    // ── Date input and break-glass visibility ─────────────────────────────────────────────────

    [Theory]
    [InlineData("2026-11-01T06:00:00Z", true)]
    [InlineData("2026-11-01T06:00:00+00:00", true)]
    [InlineData("2026-11-01T06:00:00", false)]
    [InlineData("2026-11-01", false)]
    [InlineData("2026-11-01T06:00:00+03:00", false)]
    [InlineData("not a date", false)]
    public void EnforcementDates_MustBeExplicitUtc(string input, bool accepted)
        => (PrivilegedMfaPolicy.ParseExplicitUtc(input, out _) is not null).Should().Be(accepted);

    [Fact]
    public void BootLog_SaysWhetherBreakGlassIsActive_AndWarnsWhenItIsIgnored()
    {
        (bool Warn, string Message) Describe(string? value) => PrivilegedMfaPolicy.DescribeBreakGlass(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PrivilegedMfaPolicy.BreakGlassConfigKey] = value,
            }).Build(), Now);

        Describe(null).Warn.Should().BeFalse();
        Describe(PrivilegedMfaPolicy.FormatDate(Now.AddDays(2))).Message.Should().Contain("ACTIVE");
        foreach (var ignored in new[]
                 {
                     PrivilegedMfaPolicy.FormatDate(Now.AddDays(30)), // beyond the cap
                     PrivilegedMfaPolicy.FormatDate(Now.AddDays(-1)), // past
                     "tomorrow",                                      // unparseable
                     Now.AddDays(2).ToString("yyyy-MM-ddTHH:mm:ss"),  // offset-less
                 })
        {
            var (warn, message) = Describe(ignored);
            warn.Should().BeTrue();
            message.Should().Contain("IGNORED", $"'{ignored}' must be reported as not in effect");
        }
    }

    [Fact]
    public async Task TenantEnforcementDate_RejectsOffsetlessAndFarFutureDates_AndAcceptsUtc()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var owner = await SeedOperatorAsync(kit, "owner@platform.test");
        const string reason = "customer onboarding their HR team";

        async Task<IActionResult> Set(string? date)
        {
            await using var db = kit.NewDb();
            return await Platform(kit, db, actingAs: owner).SetTenantPrivilegedMfaEnforcement(
                kit.TenantId, new PrivilegedMfaEnforcementRequest(date, reason), CancellationToken.None);
        }

        (await Set(Now.AddDays(30).ToString("yyyy-MM-ddTHH:mm:ss"))).Should().BeOfType<BadRequestObjectResult>("offset-less");
        (await Set(PrivilegedMfaPolicy.FormatDate(Now.AddDays(120)))).Should().BeOfType<BadRequestObjectResult>("beyond 90 days");
        (await Set(PrivilegedMfaPolicy.FormatDate(Now.AddDays(30)))).Should().BeOfType<OkObjectResult>();
        await using (var db = kit.NewDb())
            (await db.SecuritySettings.AsNoTracking().SingleAsync(x => x.TenantId == kit.TenantId))
                .PrivilegedMfaEnforceFromUtc.Should().BeCloseTo(Now.AddDays(30), TimeSpan.FromSeconds(5));
        (await Set(null)).Should().BeOfType<OkObjectResult>("null returns the tenant to the platform date");
        await using (var db = kit.NewDb())
            (await db.SecuritySettings.AsNoTracking().SingleAsync(x => x.TenantId == kit.TenantId))
                .PrivilegedMfaEnforceFromUtc.Should().BeNull();
    }

    // ── Enrolment notices ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnrollingThroughAnEnrolmentToken_EmailsTheAccountOwner()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await kit.SetPlatformEnforcementDateAsync(Past);
        await kit.SeedUserAsync("admin@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), "Admin");
        var token = (await LoginAsync(kit, "admin@hardening.local")).EnrollmentChallenge!.ChallengeToken;
        var email = new RecordingEmail();

        await using (var db = kit.NewDb())
        {
            var mfa = kit.Mfa(db, email);
            var setup = await mfa.InitiateEnrollmentSetupAsync(token, CancellationToken.None);
            (await mfa.VerifyEnrollmentSetupAsync(token, new MfaVerifySetupRequest(setup!.TempSecret, "000000"), CancellationToken.None))
                .Should().BeFalse();
            email.Sent.Should().BeEmpty("a failed attempt enrols nothing");
            (await mfa.VerifyEnrollmentSetupAsync(token,
                new MfaVerifySetupRequest(setup.TempSecret, Totp.Now(setup.TempSecret)), CancellationToken.None)).Should().BeTrue();
        }

        email.Sent.Should().ContainSingle();
        email.Sent[0].To.Should().Be("admin@hardening.local");
        email.Sent[0].TenantId.Should().Be(kit.TenantId, "tenant mail goes through the tenant-explicit relay");
        email.Sent[0].Subject.Should().Contain("Two-step sign-in was turned on");
    }

    [Fact]
    public async Task PlatformEnrolmentThroughAToken_EmailsTheOperator()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var id = await SeedOperatorAsync(kit, "owner@platform.test");
        var email = new RecordingEmail();
        await using var db = kit.NewDb();
        var mfa = kit.Mfa(db, email);
        var token = await mfa.CreatePlatformEnrollmentChallengeAsync(id, "127.0.0.1", CancellationToken.None);
        var setup = await mfa.InitiatePlatformEnrollmentSetupAsync(token, CancellationToken.None);
        (await mfa.VerifyPlatformEnrollmentSetupAsync(token,
            new MfaVerifySetupRequest(setup!.TempSecret, Totp.Now(setup.TempSecret)), CancellationToken.None)).Should().NotBeNull();

        email.Sent.Should().ContainSingle().Which.To.Should().Be("owner@platform.test");
    }

    // ── Platform recovery codes ───────────────────────────────────────────────────────────────

    /// <summary>Enrols an operator through the real enrolment flow; returns the TOTP secret and codes.</summary>
    private static async Task<(string Secret, IReadOnlyList<string> Codes)> EnrolOperatorAsync(AuthHardeningTestKit kit, Guid id)
    {
        await using var db = kit.NewDb();
        var mfa = kit.Mfa(db);
        var token = await mfa.CreatePlatformEnrollmentChallengeAsync(id, "127.0.0.1", CancellationToken.None);
        var setup = await mfa.InitiatePlatformEnrollmentSetupAsync(token, CancellationToken.None);
        var codes = await mfa.VerifyPlatformEnrollmentSetupAsync(token,
            new MfaVerifySetupRequest(setup!.TempSecret, Totp.Now(setup.TempSecret)), CancellationToken.None);
        return (setup.TempSecret, codes!);
    }

    private static async Task<string> PasswordStepChallengeAsync(AuthHardeningTestKit kit, string email)
    {
        await using var db = kit.NewDb();
        var body = Body(await Platform(kit, db).Login(new PlatformLoginRequest(email, Password), CancellationToken.None));
        body.GetProperty("mfaRequired").GetBoolean().Should().BeTrue();
        return body.GetProperty("challengeToken").GetString()!;
    }

    [Fact]
    public async Task RecoveryCodes_AreIssuedOnce_StoredOnlyAsHashes()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var id = await SeedOperatorAsync(kit, "owner@platform.test");
        var (_, codes) = await EnrolOperatorAsync(kit, id);

        codes.Should().HaveCount(10).And.OnlyHaveUniqueItems();
        codes.Should().AllSatisfy(c => c.Should().MatchRegex("^[A-HJ-NP-Z2-9]{4}(-[A-HJ-NP-Z2-9]{4}){4}$",
            "20 symbols from the 32-letter alphabet (100 bits), grouped 4-4-4-4-4"));
        await using var db = kit.NewDb();
        var stored = (await db.PlatformUsers.AsNoTracking().SingleAsync(p => p.Id == id)).MfaRecoveryCodeHashes!;
        foreach (var code in codes)
        {
            stored.Should().NotContain(code);
            stored.Should().NotContain(code.Replace("-", ""));
        }
    }

    [Fact]
    public async Task RecoveryCode_SignsInOnce_ThenIsSpent_AndTheUseIsAudited()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var id = await SeedOperatorAsync(kit, "owner@platform.test");
        var (_, codes) = await EnrolOperatorAsync(kit, id);

        var challenge = await PasswordStepChallengeAsync(kit, "owner@platform.test");
        await using (var db = kit.NewDb())
        {
            var result = await Platform(kit, db).PlatformMfaRecoveryVerify(
                new PlatformRecoveryCodeRequest(challenge, " " + codes[3].ToLowerInvariant().Replace("-", " ") + " "), CancellationToken.None);
            Body(result).TryGetProperty("token", out _).Should().BeTrue("a recovery code replaces the authenticator code");
        }

        var again = await PasswordStepChallengeAsync(kit, "owner@platform.test");
        await using (var db = kit.NewDb())
        {
            (await Platform(kit, db).PlatformMfaRecoveryVerify(new PlatformRecoveryCodeRequest(again, codes[3]), CancellationToken.None))
                .Should().BeOfType<UnauthorizedObjectResult>("each code works once");
            (await Platform(kit, db).PlatformMfaRecoveryVerify(new PlatformRecoveryCodeRequest(again, codes[4]), CancellationToken.None))
                .Should().BeOfType<OkObjectResult>("the remaining codes still work, and a miss only costs an attempt");
        }

        await using (var db = kit.NewDb())
        {
            (await db.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.Action == "platform.auth.mfa_recovery_code_used")).Should().Be(2);
            (await db.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.Action == "platform.auth.mfa_recovery_code_failed")).Should().Be(1);
            MfaService.RecoveryCodesRemaining(await db.PlatformUsers.AsNoTracking().SingleAsync(p => p.Id == id)).Should().Be(8);
        }
    }

    [Fact]
    public async Task RecoveryCode_IsRejectedWithoutTheSignInChallenge_AndWithTheEnrolmentToken()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var id = await SeedOperatorAsync(kit, "owner@platform.test");
        var (_, codes) = await EnrolOperatorAsync(kit, id);
        await using var db = kit.NewDb();
        var mfa = kit.Mfa(db);
        var enrolment = await mfa.CreatePlatformEnrollmentChallengeAsync(id, "127.0.0.1", CancellationToken.None);

        (await mfa.CompletePlatformChallengeWithRecoveryCodeAsync("pm1.forged", codes[0], AuthHardeningTestKit.Ctx, CancellationToken.None)).Should().BeNull();
        (await mfa.CompletePlatformChallengeWithRecoveryCodeAsync(enrolment, codes[0], AuthHardeningTestKit.Ctx, CancellationToken.None)).Should().BeNull(
            "a recovery code is a second factor; it never replaces the password step");
    }

    [Fact]
    public async Task RegeneratingRecoveryCodes_RequiresTotp_AndRetiresTheOldCodes()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var id = await SeedOperatorAsync(kit, "owner@platform.test");
        var (secret, oldCodes) = await EnrolOperatorAsync(kit, id);

        IReadOnlyList<string>? fresh;
        await using (var db = kit.NewDb())
        {
            var mfa = kit.Mfa(db);
            (await mfa.RegeneratePlatformRecoveryCodesAsync(id, "000000", CancellationToken.None)).Should().BeNull();
            fresh = await mfa.RegeneratePlatformRecoveryCodesAsync(id, Totp.Now(secret), CancellationToken.None);
        }
        fresh.Should().HaveCount(10).And.NotIntersectWith(oldCodes);

        var challenge = await PasswordStepChallengeAsync(kit, "owner@platform.test");
        await using (var db = kit.NewDb())
        {
            var mfa = kit.Mfa(db);
            (await mfa.CompletePlatformChallengeWithRecoveryCodeAsync(challenge, oldCodes[0], AuthHardeningTestKit.Ctx, CancellationToken.None))
                .Should().BeNull("regeneration retires every earlier code");
            (await mfa.CompletePlatformChallengeWithRecoveryCodeAsync(challenge, fresh![0], AuthHardeningTestKit.Ctx, CancellationToken.None))
                .Should().NotBeNull();
        }
    }

    [Fact]
    public async Task OwnerReset_AlsoClearsRecoveryCodes()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var owner = await SeedOperatorAsync(kit, "owner@platform.test");
        var lost = await SeedOperatorAsync(kit, "lost@platform.test", PlatformRoles.Support);
        await EnrolOperatorAsync(kit, lost);

        await using (var db = kit.NewDb())
            (await kit.Mfa(db).AdminResetPlatformFactorAsync(lost, owner, AuthHardeningTestKit.Ctx, CancellationToken.None)).Should().BeTrue();
        await using (var db = kit.NewDb())
            (await db.PlatformUsers.AsNoTracking().SingleAsync(p => p.Id == lost)).MfaRecoveryCodeHashes.Should().BeNull();
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
