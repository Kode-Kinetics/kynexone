using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Zayra.Api.Application.Auth;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// The company two-step policy with per-person choice (security_settings.mfa_policy): Optional ("each person chooses"),
/// PrivilegedRoles (the default) and Everyone. Optional means nobody is forced, privileged roles included.
/// </summary>
public sealed class MfaPolicyChoiceTests
{
    private const string Password = "Correct-Horse-Battery-9!";
    private static readonly DateTime Past = DateTime.UtcNow.AddDays(-1);

    private static async Task SetPolicyAsync(AuthHardeningTestKit kit, string policy)
    {
        await using var db = kit.NewDb();
        var sec = await db.SecuritySettings.FirstOrDefaultAsync(s => s.TenantId == kit.TenantId);
        if (sec is null) { sec = new SecuritySetting { TenantId = kit.TenantId }; db.SecuritySettings.Add(sec); }
        sec.MfaPolicy = policy;
        sec.MfaRequired = policy == MfaPolicies.Everyone;
        await db.SaveChangesAsync();
    }

    private static async Task<AuthLoginResult> LoginAsync(AuthHardeningTestKit kit, string email, IConfiguration? config = null)
    {
        await using var db = kit.NewDb();
        return await kit.Auth(db, config: config).LoginAsync(
            new LoginRequest(email, Password, AuthHardeningTestKit.TenantSlug), AuthHardeningTestKit.Ctx, CancellationToken.None);
    }

    private static async Task<string> EnrolWithBackupCodesAsync(AuthHardeningTestKit kit, Guid userId)
    {
        var codes = MfaBackupCodes.Generate();
        await using var db = kit.NewDb();
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        user.MFAEnabled = true;
        user.MfaSecretEncrypted = kit.Totp.EncryptSecret(kit.Totp.GenerateBase32Secret());
        user.MfaRecoveryCodeHashes = codes.Stored;
        await db.SaveChangesAsync();
        return codes.Codes[0];
    }

    private static async Task<(SecuritySetting? Row, PrivilegedMfaState State)> StateAsync(AuthHardeningTestKit kit, Guid userId, IConfiguration? cfg = null)
    {
        await using var db = kit.NewDb();
        var user = await db.Users.AsNoTracking()
            .Include(x => x.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x!.RolePermissions).ThenInclude(x => x.Permission)
            .Include(x => x.PermissionOverrides).Include(x => x.EmployeeUserAccounts).Include(x => x.EntityAccesses)
            .AsSplitQuery().SingleAsync(u => u.Id == userId);
        var policy = await db.SecuritySettings.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == kit.TenantId);
        return (policy, await PrivilegedMfaPolicy.ForTenantUserAsync(db, cfg, user, policy, DateTime.UtcNow, CancellationToken.None));
    }

    [Fact]
    public async Task Optional_PrivilegedUserPastTheDate_SignsInWithNoChallengeOrEnrolment()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await kit.SetPlatformEnforcementDateAsync(Past);
        await kit.SeedUserAsync("admin@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), "Admin");

        (await LoginAsync(kit, "admin@hardening.local")).RequiresMfaEnrollment.Should().BeTrue("PrivilegedRoles: enforced");
        await SetPolicyAsync(kit, MfaPolicies.Optional);

        var result = await LoginAsync(kit, "admin@hardening.local");
        result.Tokens.Should().NotBeNull();
        result.RequiresMfa.Should().BeFalse();
        result.RequiresMfaEnrollment.Should().BeFalse();
    }

    [Fact]
    public async Task Optional_RefreshKeepsRotating_ForAnUnenrolledPrivilegedUser()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await kit.SetPlatformEnforcementDateAsync(DateTime.UtcNow.AddDays(14));
        await kit.SeedUserAsync("admin@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), "Admin");
        var refresh = (await LoginAsync(kit, "admin@hardening.local")).Tokens!.RefreshToken;
        await kit.SetPlatformEnforcementDateAsync(Past);
        await SetPolicyAsync(kit, MfaPolicies.Optional);

        await using var db = kit.NewDb();
        (await kit.Auth(db).RefreshAsync(new RefreshTokenRequest(refresh), AuthHardeningTestKit.Ctx, CancellationToken.None))
            .Should().NotBeNull("under Optional there is no refresh block");
    }

    [Fact]
    public async Task Optional_AnEnrolledUserIsStillChallenged_BecauseTheyChoseIt()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var id = await kit.SeedUserAsync("admin@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), "Admin");
        await EnrolWithBackupCodesAsync(kit, id);
        await SetPolicyAsync(kit, MfaPolicies.Optional);

        (await LoginAsync(kit, "admin@hardening.local")).RequiresMfa.Should().BeTrue();
    }

    [Fact]
    public async Task Optional_StatusIsNotRequired_And_PrivilegedUserCanSelfDisable_WithABackupCode()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await kit.SetPlatformEnforcementDateAsync(Past);
        var id = await kit.SeedUserAsync("admin@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), "Admin");
        var backup = await EnrolWithBackupCodesAsync(kit, id);

        await using (var db = kit.NewDb())
            (await kit.Mfa(db).DisableWithOutcomeAsync(id, kit.TenantId, null, backup, AuthHardeningTestKit.Ctx, CancellationToken.None))
                .Should().Be(MfaDisableOutcome.RequiredForRole, "PrivilegedRoles still refuses");

        await SetPolicyAsync(kit, MfaPolicies.Optional);
        (await StateAsync(kit, id)).State.Status.Should().Be(PrivilegedMfaStatus.NotRequired);

        await using (var db = kit.NewDb())
            (await kit.Mfa(db).DisableWithOutcomeAsync(id, kit.TenantId, null, "ZZZZZ-ZZZZZ", AuthHardeningTestKit.Ctx, CancellationToken.None))
                .Should().Be(MfaDisableOutcome.CodeInvalid);
        await using (var db = kit.NewDb())
            (await kit.Mfa(db).DisableWithOutcomeAsync(id, kit.TenantId, null, backup, AuthHardeningTestKit.Ctx, CancellationToken.None))
                .Should().Be(MfaDisableOutcome.Disabled);

        await using var check = kit.NewDb();
        var user = await check.Users.AsNoTracking().SingleAsync(u => u.Id == id);
        user.MFAEnabled.Should().BeFalse();
        user.MfaRecoveryCodeHashes.Should().BeNull();
        (await LoginAsync(kit, "admin@hardening.local")).Tokens.Should().NotBeNull();
    }

    [Fact]
    public async Task PrivilegedRoles_IsTheDefault_AndUnchanged()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await kit.SetPlatformEnforcementDateAsync(Past);
        var admin = await kit.SeedUserAsync("admin@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), "Admin");
        await kit.SeedUserAsync("staff@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), "Employee");

        MfaPolicies.For(null).Should().Be(MfaPolicies.PrivilegedRoles);
        new SecuritySetting().MfaPolicy.Should().Be(MfaPolicies.PrivilegedRoles);
        (await StateAsync(kit, admin)).State.Status.Should().Be(PrivilegedMfaStatus.Enforced);
        (await LoginAsync(kit, "staff@hardening.local")).Tokens.Should().NotBeNull();
    }

    [Fact]
    public async Task Everyone_RequiresEveryUser_AndALegacyFlagAloneReadsAsEveryone()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await kit.SeedUserAsync("staff@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), "Employee");
        await SetPolicyAsync(kit, MfaPolicies.Everyone);
        (await LoginAsync(kit, "staff@hardening.local")).RequiresMfaEnrollment.Should().BeTrue();

        new SecuritySetting { MfaRequired = true }.EffectiveMfaPolicy.Should().Be(MfaPolicies.Everyone,
            "a row written by older code that only set the flag is still Everyone");
    }

    [Fact]
    public async Task TestingSwitch_StillOverridesEveryPolicy()
    {
        var off = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [PrivilegedMfaPolicy.DisabledForTestingConfigKey] = "true" }).Build();
        foreach (var policy in MfaPolicies.All)
        {
            await using var kit = await AuthHardeningTestKit.CreateAsync();
            await kit.SetPlatformEnforcementDateAsync(Past);
            await kit.SeedUserAsync("admin@hardening.local", new Pbkdf2PasswordHasher().Hash(Password), "Admin");
            await SetPolicyAsync(kit, policy);
            var r = await LoginAsync(kit, "admin@hardening.local", off);
            r.Tokens.Should().NotBeNull($"the testing switch overrides {policy}");
            r.RequiresMfaEnrollment.Should().BeFalse();
        }
    }

    // ── Changing the policy ───────────────────────────────────────────────────────────────────

    private static AccessManagementService Access(AuthHardeningTestKit kit, Zayra.Api.Data.ZayraDbContext db) =>
        new(db, new Pbkdf2PasswordHasher(), new Zayra.Api.Infrastructure.Audit.AuditService(db), new JwtTokenService(AuthHardeningTestKit.Jwt));

    private static UpdateSecuritySettingsRequestBuilder Req => new();
    private sealed class UpdateSecuritySettingsRequestBuilder
    {
        public UpdateSecuritySettingRequest Policy(string? policy, string? reason = null, bool? legacy = null) =>
            new(null, null, null, null, null, null, null, null, null, null, null, null, MfaRequired: legacy, MfaPolicy: policy, MfaPolicyReason: reason);
    }

    private static readonly RequestContext Ctx = new("198.51.100.7", "test", null, null);

    [Fact]
    public async Task ChangingPolicy_IsAudited_WithOldAndNewValues()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await using (var db = kit.NewDb())
        {
            var dto = await Access(kit, db).UpdateSecuritySettingsAsync(kit.TenantId, Req.Policy(MfaPolicies.Everyone), Ctx with { TenantId = kit.TenantId }, CancellationToken.None);
            dto.MfaPolicy.Should().Be(MfaPolicies.Everyone);
            dto.MfaRequired.Should().BeTrue("mfaRequired stays readable and equals Everyone");
        }
        await using var check = kit.NewDb();
        var row = await check.AuditLogs.IgnoreQueryFilters().SingleAsync(a => a.Action == "security.mfa_policy_changed");
        row.Metadata.Should().Contain("\"from\":\"PrivilegedRoles\"").And.Contain("\"to\":\"Everyone\"");
        (await check.SecuritySettings.SingleAsync(s => s.TenantId == kit.TenantId)).MfaRequired.Should().BeTrue();
    }

    [Fact]
    public async Task MovingToOptional_NeedsAReason_IsLoggedAsAWarning_AndClearsTheFlag()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await kit.SetWorkspaceMfaRequiredAsync(true);

        await using (var db = kit.NewDb())
        {
            var act = () => Access(kit, db).UpdateSecuritySettingsAsync(kit.TenantId, Req.Policy(MfaPolicies.Optional, "  "), Ctx with { TenantId = kit.TenantId }, CancellationToken.None);
            (await act.Should().ThrowAsync<MfaPolicyException>()).Which.Code.Should().Be(MfaErrorCodes.PolicyReasonRequired);
        }
        await using (var db = kit.NewDb())
            (await db.SecuritySettings.SingleAsync(s => s.TenantId == kit.TenantId)).MfaRequired.Should().BeTrue("a refused change changes nothing");

        await using (var db = kit.NewDb())
        {
            var dto = await Access(kit, db).UpdateSecuritySettingsAsync(kit.TenantId, Req.Policy(MfaPolicies.Optional, "Pilot with test data"), Ctx with { TenantId = kit.TenantId }, CancellationToken.None);
            dto.MfaPolicy.Should().Be(MfaPolicies.Optional);
            dto.MfaRequired.Should().BeFalse();
        }
        await using var check = kit.NewDb();
        var row = await check.AuditLogs.IgnoreQueryFilters().SingleAsync(a => a.Action == "security.mfa_policy_relaxed");
        row.Metadata.Should().Contain("\"from\":\"Everyone\"").And.Contain("\"to\":\"Optional\"")
            .And.Contain("\"severity\":\"warning\"").And.Contain("Pilot with test data");
    }

    [Fact]
    public async Task AnUnknownPolicy_IsRefused_AndTheLegacyFlagStillWorks()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await using (var db = kit.NewDb())
        {
            var act = () => Access(kit, db).UpdateSecuritySettingsAsync(kit.TenantId, Req.Policy("Sometimes"), Ctx with { TenantId = kit.TenantId }, CancellationToken.None);
            (await act.Should().ThrowAsync<MfaPolicyException>()).Which.Code.Should().Be(MfaErrorCodes.PolicyInvalid);
        }
        await using (var db = kit.NewDb())
            (await Access(kit, db).UpdateSecuritySettingsAsync(kit.TenantId, Req.Policy(null, null, true), Ctx with { TenantId = kit.TenantId }, CancellationToken.None))
                .MfaPolicy.Should().Be(MfaPolicies.Everyone);
        await using (var db = kit.NewDb())
            (await Access(kit, db).UpdateSecuritySettingsAsync(kit.TenantId, Req.Policy(null, null, false), Ctx with { TenantId = kit.TenantId }, CancellationToken.None))
                .MfaPolicy.Should().Be(MfaPolicies.PrivilegedRoles);
    }

    [Fact]
    public async Task UnrelatedSettingsSave_DoesNotRelaxOrAuditThePolicy()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await SetPolicyAsync(kit, MfaPolicies.Optional);
        await using (var db = kit.NewDb())
            await Access(kit, db).UpdateSecuritySettingsAsync(kit.TenantId,
                new UpdateSecuritySettingRequest(null, null, null, null, null, null, null, null, null, null, null, null, MfaTrustedDeviceDays: 7),
                Ctx with { TenantId = kit.TenantId }, CancellationToken.None);
        await using var check = kit.NewDb();
        (await check.SecuritySettings.SingleAsync(s => s.TenantId == kit.TenantId)).MfaPolicy.Should().Be(MfaPolicies.Optional);
        (await check.AuditLogs.IgnoreQueryFilters().AnyAsync(a => a.Action.StartsWith("security.mfa_policy"))).Should().BeFalse();
    }

    [Fact]
    public void MigrationMapping_ExistingRows_RequiredBecomesEveryone_OthersPrivilegedRoles()
    {
        var src = File.ReadAllText(Directory.EnumerateFiles(FindMigrations(), "*AddSecuritySettingsMfaPolicy.cs").Single());
        src.Should().Contain("CASE WHEN mfa_required THEN 'Everyone' ELSE 'PrivilegedRoles' END");
        src.Should().NotContain("'Optional' ELSE", "nobody is moved to Optional by the migration");
        src.IndexOf("migrationBuilder.Sql", StringComparison.Ordinal).Should().BeLessThan(
            src.IndexOf("AddCheckConstraint", StringComparison.Ordinal), "backfill runs before the CHECKs");
    }

    private static string FindMigrations()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Zayra.Api", "Migrations"))) dir = dir.Parent;
        return dir is null ? throw new DirectoryNotFoundException("Migrations") : Path.Combine(dir.FullName, "Zayra.Api", "Migrations");
    }
}
