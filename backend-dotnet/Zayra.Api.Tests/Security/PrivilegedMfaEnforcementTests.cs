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
    [InlineData("payroll.read")]
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
            "Auditor",             // payroll.read (every employee's pay and bank lines)
        });
        privileged.Should().NotContain(new[] { "Employee", "Manager", "HR Assistant", "Kiosk Operator" },
            "self-service, read-only and own-team roles must not be forced onto MFA");
    }

    [Fact]
    public void AccessModeBundles_CountOnlyTheirPrivilegedKeys()
    {
        var modes = typeof(AccessModes).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
        modes.Should().Contain(AccessModes.Mobile).And.Contain(AccessModes.ManagerPortal);
        foreach (var mode in modes)
            PrivilegedMfaPolicy.PrivilegedBundleKeys(mode, AuthService.AccessModePermissions(mode))
                .Should().BeEmpty($"today's {mode} bundle is self-service or exempted on purpose");

        PrivilegedMfaPolicy.PrivilegedBundleKeys(AccessModes.ManagerPortal, new[] { "approvals.decide", "payroll.export" })
            .Should().Equal(new[] { "payroll.export" }, "a privileged key added to a bundle later must count");
        PrivilegedMfaPolicy.PrivilegedBundleKeys(AccessModes.EssOnly, new[] { "attendance.write" })
            .Should().Equal(new[] { "attendance.write" }, "the own-punch exemption is Mobile's alone");

        var mobileEmployee = new User
        {
            EmployeeUserAccounts = { new EmployeeUserAccount { AccessMode = AccessModes.Mobile, IsPrimary = true } },
        };
        PrivilegedMfaPolicy.HoldsPrivilegedPermission(mobileEmployee).Should().BeFalse(
            "Mobile's attendance.write is the employee's own clock-in");
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
        await kit.SeedUserAsync("viewer@hardening.local", hash, "Roster Viewer", "employees.read", "attendance.read", "leave.read");

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
    public async Task LostDevice_AdminResetOfAFactor_LeadsToReEnrolment_NotLockout()
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

    // ── TOTP replay, notices and no-store ─────────────────────────────────────────────────────

    [Fact]
    public async Task TenantTotp_AReplayedCodeIsRefused()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await kit.SetPlatformEnforcementDateAsync(Past);
        await kit.SeedUserAsync("admin@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), "Admin");
        var token = (await LoginAsync(kit, "admin@hardening.local")).EnrollmentChallenge!.ChallengeToken;
        string secret;
        await using (var db = kit.NewDb())
        {
            var mfa = kit.Mfa(db);
            var setup = await mfa.InitiateEnrollmentSetupAsync(token, CancellationToken.None);
            secret = setup!.TempSecret;
            (await mfa.VerifyEnrollmentSetupAsync(token, new MfaVerifySetupRequest(secret, Totp.Now(secret)), CancellationToken.None))
                .Should().BeTrue();
        }

        async Task<bool> Complete(string code)
        {
            var challenge = (await LoginAsync(kit, "admin@hardening.local")).Challenge!.ChallengeToken;
            await using var db = kit.NewDb();
            try
            {
                await kit.Auth(db).CompleteMfaLoginAsync(challenge, code, AuthHardeningTestKit.Ctx, CancellationToken.None);
                return true;
            }
            catch (UnauthorizedAccessException) { return false; }
        }

        long enrolled;
        await using (var db = kit.NewDb())
            enrolled = (await db.Users.AsNoTracking().SingleAsync(u => u.Email == "admin@hardening.local")).MfaLastTotpStep!.Value;

        (await Complete(Totp.ForStep(secret, enrolled))).Should().BeFalse("the enrolment code's step is spent");
        (await Complete(Totp.ForStep(secret, enrolled + 1))).Should().BeTrue();
        (await Complete(Totp.ForStep(secret, enrolled + 1))).Should().BeFalse("replay");
    }

    [Theory]
    [InlineData(typeof(MfaController), nameof(MfaController.InitiateSetup))]
    [InlineData(typeof(MfaController), nameof(MfaController.InitiateEnrollmentSetup))]
    [InlineData(typeof(MfaController), nameof(MfaController.StartEnrollment))]
    [InlineData(typeof(MfaController), nameof(MfaController.VerifyChallenge))]
    [InlineData(typeof(AuthController), nameof(AuthController.Login))]
    [InlineData(typeof(AuthController), nameof(AuthController.Refresh))]
    public void ResponsesCarryingSecretsOrCodes_AreNeverCached(Type controller, string action)
    {
        var method = controller.GetMethods().Single(m => m.Name == action && m.DeclaringType == controller);
        method.GetCustomAttributes(typeof(Zayra.Api.Infrastructure.Http.NoStoreAttribute), inherit: true)
            .Should().NotBeEmpty($"{controller.Name}.{action} returns a token, secret or recovery codes");

        var http = new DefaultHttpContext();
        var ctx = new Microsoft.AspNetCore.Mvc.Filters.ResultExecutingContext(
            new ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()),
            new List<Microsoft.AspNetCore.Mvc.Filters.IFilterMetadata>(), new OkResult(), controller: null!);
        new Zayra.Api.Infrastructure.Http.NoStoreAttribute().OnResultExecuting(ctx);
        http.Response.Headers.CacheControl.ToString().Should().Contain("no-store");
    }

    [Fact]
    public async Task AuthenticatorApps_ShowTheProductName()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var id = await kit.SeedUserAsync("admin@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), "Admin");
        await using var db = kit.NewDb();
        var setup = await kit.Mfa(db).InitiateSetupAsync(id, kit.TenantId, CancellationToken.None);

        setup.ProvisioningUri.Should().Contain("issuer=KynexOne").And.NotContain("Zayra");
    }

    // ── Grace-period sessions ────────────────────────────────────────────────────────

    [Fact]
    public void GraceSessions_EndNoLaterThanTheEnforcementDate_OrTheBreakGlassWindow()
    {
        var eight = TimeSpan.FromHours(8);
        PrivilegedMfaPolicy.SessionExpiry(PrivilegedMfaPolicy.Evaluate(true, false, Now.AddHours(2), null, Now), null, Now, eight)
            .Should().Be(Now.AddHours(2) - PrivilegedMfaPolicy.JwtClockSkew,
                "validation tolerates the skew past exp, so the token is issued that much earlier");
        PrivilegedMfaPolicy.SessionExpiry(PrivilegedMfaPolicy.Evaluate(true, false, Now.AddDays(3), null, Now), null, Now, eight)
            .Should().Be(Now + eight);
        PrivilegedMfaPolicy.SessionExpiry(PrivilegedMfaPolicy.Evaluate(true, true, Now.AddHours(2), null, Now), null, Now, eight)
            .Should().Be(Now + eight, "an enrolled operator is not in a grace period");

        var glassEnds = DateTime.SpecifyKind(Now.AddHours(1), DateTimeKind.Utc);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [PrivilegedMfaPolicy.BreakGlassConfigKey] = PrivilegedMfaPolicy.FormatDate(glassEnds),
        }).Build();
        var glass = PrivilegedMfaPolicy.Evaluate(true, false, Past, PrivilegedMfaPolicy.ActiveBreakGlassUntil(config, Now), Now);
        PrivilegedMfaPolicy.SessionExpiry(glass, config, Now, eight)
            .Should().BeCloseTo(glassEnds - PrivilegedMfaPolicy.JwtClockSkew, TimeSpan.FromSeconds(1));
        PrivilegedMfaPolicy.SessionExpiry(PrivilegedMfaPolicy.Evaluate(true, false, Now.AddSeconds(30), null, Now), null, Now, eight)
            .Should().Be(Now, "a cap closer than the skew issues an already-expired token, never one that outlives it");
    }

    // ── TOTP for tests ────────────────────────────────────────────────────────────────────────

    private static class Totp
    {
        public static string Now(string base32Secret) => At(base32Secret, 0);

        /// <summary>The code for the current time-step plus <paramref name="offsetSteps"/> (still inside the ±1 window).</summary>
        public static string At(string base32Secret, int offsetSteps)
            => ForStep(base32Secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 + offsetSteps);

        /// <summary>The code for an absolute time-step (tests anchor on the stored step, not the clock).</summary>
        public static string ForStep(string base32Secret, long step)
        {
            var key = FromBase32(base32Secret);
            var msg = BitConverter.GetBytes(step);
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
