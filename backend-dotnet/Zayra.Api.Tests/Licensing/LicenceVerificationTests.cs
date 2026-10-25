using System.Security.Cryptography;
using FluentAssertions;
using Zayra.Api.Infrastructure.Licensing;
using Zayra.Api.Infrastructure.Modules;

namespace Zayra.Api.Tests.Licensing;

/// <summary>WP3.2: a licence counts only when the vendor signed it, for this installation, and it is well formed.</summary>
public class LicenceVerificationTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private static LicenceVerification Verify(LicenceTestKit kit, LicenceFile file, Guid? installationId = null) =>
        kit.Verifier().Verify(file, installationId, LicenceService.KnownModules, LicenceService.KnownOptIns, Now);

    [Fact]
    public void AValidSignature_IsAccepted_WithItsFingerprint()
    {
        using var kit = new LicenceTestKit();
        var payload = LicenceTestKit.Payload();

        var result = Verify(kit, kit.Sign(payload));

        result.IsValid.Should().BeTrue(string.Join(" ", result.Problems));
        result.Licence!.Payload.LicenceId.Should().Be(payload.LicenceId);
        result.Licence.Fingerprint.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void ATamperedPayload_IsRejected()
    {
        using var kit = new LicenceTestKit();
        var signed = kit.Sign(LicenceTestKit.Payload());
        var raised = LicenceTestKit.Payload(b => b.With(p => p with { MaxEmployees = 100_000 }));

        var result = Verify(kit, signed with { Payload = Base64Url.Encode(raised.ToUtf8Bytes()) });

        result.IsValid.Should().BeFalse();
        result.Problems.Should().ContainMatch("*signature is not valid*");
    }

    [Fact]
    public void ASignatureFromAnotherKey_IsRejected()
    {
        using var vendor = new LicenceTestKit();
        using var forger = new LicenceTestKit();

        var result = Verify(vendor, forger.Sign(LicenceTestKit.Payload()));

        result.IsValid.Should().BeFalse("only the vendor's key may sign a licence");
    }

    [Fact]
    public void AnUnknownKid_IsRejected()
    {
        using var kit = new LicenceTestKit();

        var result = Verify(kit, kit.Sign(LicenceTestKit.Payload(), kid: "2099-01"));

        result.Problems.Should().ContainMatch("*Unknown signing key*");
    }

    [Fact]
    public void ALicenceBoundToAnotherInstallation_IsRejected_AndOneBoundToThisInstallation_IsAccepted()
    {
        using var kit = new LicenceTestKit();
        var here = Guid.NewGuid();

        Verify(kit, kit.Sign(LicenceTestKit.Payload(b => b.With(p => p with { InstallationId = Guid.NewGuid() }))), here)
            .Problems.Should().ContainMatch("*bound to installation*");
        Verify(kit, kit.Sign(LicenceTestKit.Payload(b => b.With(p => p with { InstallationId = here }))), here)
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void ALicenceNotYetValid_IsRejected()
    {
        using var kit = new LicenceTestKit();

        var result = Verify(kit, kit.Sign(LicenceTestKit.Payload(b => b.With(p => p with { NotBeforeUtc = Now.AddDays(3) }))));

        result.Problems.Should().ContainMatch("*not valid before*");
    }

    [Theory]
    [InlineData("module")]
    [InlineData("optin")]
    [InlineData("negative")]
    [InlineData("format")]
    [InlineData("accountType")]
    public void AMalformedPayload_IsRejected_NamingTheProblem(string defect)
    {
        using var kit = new LicenceTestKit();
        var payload = LicenceTestKit.Payload(b => b.With(p => defect switch
        {
            "module" => p with { Modules = ["no_such_module"] },
            "optin" => p with { OptInFeatures = ["no_such_feature"] },
            "negative" => p with { MaxUsers = -1 },
            "format" => p with { FormatVersion = 2 },
            _ => p with { AccountType = "Enterprise" },
        }));

        Verify(kit, kit.Sign(payload)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void TheFileFormat_RoundTrips()
    {
        using var kit = new LicenceTestKit();
        var file = kit.Sign(LicenceTestKit.Payload());

        LicenceFile.Parse(file.ToJson()).Should().Be(file);
        LicenceFile.Parse("{ \"not\": \"a licence\" }").Should().BeNull();
        LicenceFile.Parse("not json").Should().BeNull();
    }

    [Fact]
    public void TheEmbeddedVendorKey_IsLoaded_UnderItsKid()
    {
        new LicenceVerifier().KnownKids.Should().Contain("2026-01");
        LicenceVerifier.KidFromResource("Licensing.Keys.2026-01.pub.pem").Should().Be("2026-01");
    }

    [Fact]
    public void TheIssuersModuleKeys_AreExactlyTheCatalogsKeys()
    {
        // tools/LicenceIssuer validates modules against ModuleKeys; the product against ModuleCatalog. They must agree,
        // or the issuer could sign a licence the product rejects.
        var moduleKeys = typeof(ModuleKeys).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet();

        moduleKeys.Should().BeEquivalentTo(LicenceService.KnownModules);
    }
}

/// <summary>WP3.3: the licence's state follows its dates.</summary>
public class LicenceStateTests
{
    private static readonly DateTime Now = new(2027, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private static LicenceState StateOf(LicencePayload? payload) =>
        new LicenceSnapshot(payload is null ? null : new VerifiedLicence(payload, "k", "", "", "f"), null, Now).StateAt(Now);

    [Fact]
    public void NoLicence_IsMissing() => StateOf(null).Should().Be(LicenceState.Missing);

    [Fact]
    public void APerpetualLicence_IsValid() =>
        StateOf(LicenceTestKit.Payload(b => b.With(p => p with { ExpiresAtUtc = null }))).Should().Be(LicenceState.Valid);

    [Fact]
    public void BeforeExpiry_IsValid() =>
        StateOf(LicenceTestKit.Payload(b => b.With(p => p with { NotBeforeUtc = Now.AddYears(-1), ExpiresAtUtc = Now.AddDays(1) })))
            .Should().Be(LicenceState.Valid);

    [Fact]
    public void InsideTheGraceDays_IsGrace() =>
        StateOf(LicenceTestKit.Payload(b => b.With(p => p with { NotBeforeUtc = Now.AddYears(-1), ExpiresAtUtc = Now.AddDays(-3), GraceDays = 7 })))
            .Should().Be(LicenceState.Grace);

    [Fact]
    public void PastTheGraceDays_IsExpired() =>
        StateOf(LicenceTestKit.Payload(b => b.With(p => p with { NotBeforeUtc = Now.AddYears(-1), ExpiresAtUtc = Now.AddDays(-8), GraceDays = 7 })))
            .Should().Be(LicenceState.Expired);

    [Fact]
    public void WithoutALicence_TheLimitsAreOneOfEach_NeverUnlimited()
    {
        var none = LicenceSnapshot.None(null);
        none.MaxEmployees.Should().Be(1);
        none.MaxUsers.Should().Be(1);
        none.MaxCompanies.Should().Be(1);
        none.AiMonthlyTokenLimit.Should().Be(0, "AI is not licensed without a licence");
        none.Modules.Should().BeEmpty();
        none.OptInFeatures.Should().BeEmpty();
    }

    [Fact]
    public void CoreAndStatutoryModules_AreAlwaysLicensed_EvenWithoutALicence()
    {
        var licence = new FixedLicenceService(null);
        var core = ModuleCatalog.All.First(m => m.Lock == ModuleLock.Core);
        var statutory = ModuleCatalog.All.First(m => m.Lock == ModuleLock.Statutory);
        var optional = ModuleCatalog.All.First(m => m.Lock == ModuleLock.Optional);

        licence.IsModuleLicensed(core.Key).Should().BeTrue();
        licence.IsModuleLicensed(statutory.Key).Should().BeTrue("a licence must never switch off a legal obligation");
        licence.IsModuleLicensed(optional.Key).Should().BeFalse();
    }

    [Fact]
    public void TheAiAllowance_FollowsTheLicence()
    {
        AiAllowance.For(null).Licensed.Should().BeTrue("a test-built service imposes no limit");
        AiAllowance.For(new FixedLicenceService(LicenceTestKit.Payload(b => b.With(p => p with { AiMonthlyTokenLimit = 0 }))))
            .Licensed.Should().BeFalse("0 = AI disabled");
        var allowance = AiAllowance.For(new FixedLicenceService(LicenceTestKit.Payload(b => b.With(p => p with { AiMonthlyTokenLimit = 5000 }))));
        allowance.Licensed.Should().BeTrue();
        allowance.MonthlyTokenLimit.Should().Be(5000);
    }
}
