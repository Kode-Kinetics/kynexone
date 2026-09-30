using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Three changes to the readiness gate, pinned behaviourally.
///
/// <para><b>(1) Social-insurance references gate PAY, not activation.</b> GOSI registration happens AFTER
/// hire in Saudi practice — the authority issues the reference once the contract exists; GPSSA/GRSIA/PIFSS/
/// SPF/SIO likewise. Requiring it to activate made a clean import produce permanently unactivatable
/// employees. It still fails CLOSED, at the gate where contributions are computed. Existing tenants' stored
/// seed rules (no "gate" property) now resolve to the pay gate too, with no data migration.</para>
///
/// <para><b>(2) A zero-requirement policy is no longer a fabricated Ready/100.</b> A blank or non-GCC country
/// with no configured profile has nothing to evaluate. It now reads <b>NeedsAttention</b>, score 0, with one
/// recommended item naming the missing policy — and deliberately does NOT block: blocking would refuse the
/// next activation of LIVE employees whose company simply has no policy, and turn every such Active employee
/// into a payroll drift warning, although nothing about their data changed.</para>
///
/// <para><b>(3) An unknown joining date blocks activation under every policy, including none.</b></para>
/// </summary>
public class ReadinessGateRegressionTests
{
    private static readonly DateTime Joined = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ZayraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static EmployeeReadinessEvaluator Evaluator(ZayraDbContext db) => new(db);
    private static EmployeeReadinessPolicyResolver Resolver(ZayraDbContext db) => new(db);
    private static RequestContext Ctx(Guid tenantId) => new(null, "tests", Guid.NewGuid(), tenantId);

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  (1) GOSI / social insurance is a PAY blocker, not an ACTIVATION blocker
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    // A Saudi national satisfies the KSA identity gate with the National ID (Hawiyya).
    [InlineData("Saudi", "1234567890", "")]
    // A non-GCC expat satisfies it with the Iqama (residence permit).
    [InlineData("Indian", "", "2000000001")]
    public async Task SaudiEmployee_WithIdentity_ButNoGosi_IsActivatable_AndGosiIsAPayBlocker(
        string nationality, string idNumber, string iqamaNumber)
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var policy = await Resolver(db).ResolveAsync(tenantId, companyId: null, "SA", nationality);

        var snapshot = new EmployeeReadinessSnapshot
        {
            EmployeeId = 1,
            CountryCode = "SA",
            Nationality = nationality,
            JoiningDate = Joined,
            IdNumber = idNumber,
            IqamaNumber = iqamaNumber,
            IqamaExpiryDate = iqamaNumber.Length > 0 ? new DateOnly(2031, 1, 1) : null,
            GosiReference = string.Empty,   // the point of the test
        };

        var readiness = Evaluator(db).Evaluate(snapshot, policy);

        readiness.Blocking.Should().NotContain(i => i.Key == "GosiReference",
            "GOSI registration happens after hire — it can never be a precondition of hiring");
        readiness.IsBlocked.Should().BeFalse();
        var gosi = readiness.PayBlocking.Should().ContainSingle(i => i.Key == "GosiReference").Subject;
        gosi.Gate.Should().Be("pay");
        gosi.Reason.Should().Be("statutory", "it is a hard requirement, just at a later gate");

        var allowed = await new EmployeeActivationGuard(db).EnsureActivatableAsync(tenantId, null, snapshot, Ctx(tenantId), default);
        allowed.Blocking.Should().BeEmpty();
    }

    [Fact]
    public async Task TheKsaFloorStillBlocksActivationOnTheIdentityItGenuinelyNeeds()
    {
        // Moving GOSI must not make the gate soft: a non-GCC expat with NO Iqama is still refused.
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var policy = await Resolver(db).ResolveAsync(tenantId, companyId: null, "SA", "Indian");
        var snapshot = new EmployeeReadinessSnapshot { EmployeeId = 2, CountryCode = "SA", Nationality = "Indian", JoiningDate = Joined };

        var readiness = Evaluator(db).Evaluate(snapshot, policy);

        readiness.State.Should().Be("Blocked");
        readiness.Blocking.Should().ContainSingle(i => i.Key == "IqamaNumber" && i.Gate == "activate");
        var act = () => new EmployeeActivationGuard(db).EnsureActivatableAsync(tenantId, null, snapshot, Ctx(tenantId), default);
        await act.Should().ThrowAsync<EmployeeActivationBlockedException>();
    }

    [Fact]
    public void EveryGccHostSocialInsuranceItemIsPayGated_AndNoneOfThemGatesActivation()
    {
        foreach (var iso2 in new[] { "SA", "AE", "QA", "KW", "OM", "BH" })
        {
            var social = GccReadinessFloor.Resolve(iso2)
                .Where(r => r.Key is "GosiReference" or "SocialInsuranceReference")
                .ToList();
            social.Should().NotBeEmpty($"{iso2} has a host social-insurance scheme in the floor");
            social.Should().OnlyContain(r => r.Gate == "pay" && r.FailClosed,
                $"{iso2}'s social-insurance reference must gate PAY, and must still fail closed");
        }
    }

    [Fact]
    public void TheSeededTenantDefaultProfileDeclaresSocialInsuranceAtThePayGate()
    {
        var sa = TenantProvisioningBundle.ComplianceSeeds.Single(s => s.Country == "SA").RequiredFieldsJson;
        EmployeeReadinessPolicyResolver.ParseProfile(sa, "tenant")
            .Single(r => r.Key == "GosiReference").Gate.Should().Be("pay");
        var bh = TenantProvisioningBundle.ComplianceSeeds.Single(s => s.Country == "BH").RequiredFieldsJson;
        EmployeeReadinessPolicyResolver.ParseProfile(bh, "tenant")
            .Single(r => r.Key == "SocialInsuranceReference").Gate.Should().Be("pay");
    }

    /// <summary>
    /// EXISTING tenants: every tenant provisioned before this change holds the OLD seed row, which states no
    /// "gate" at all. ParseProfile used to read that as "activate", and the strictest-wins merge re-upgraded
    /// the floor — so moving the floor alone would have changed nothing for any live tenant. The omitted gate
    /// now resolves to "pay" for the social-insurance keys, with no migration or backfill.
    /// </summary>
    [Theory]
    [InlineData("SA", "Saudi", "GosiReference",
        """[{"key":"GosiReference","category":"identity","failClosed":true},{"key":"IqamaNumber","category":"identity","failClosed":true,"appliesWhen":{"nationalityNot":"SA"}},{"key":"doc:Contract","category":"contract","failClosed":false}]""")]
    [InlineData("BH", "Bahraini", "SocialInsuranceReference",
        """[{"key":"CivilId","category":"identity","failClosed":true,"appliesWhen":{"nationalityNot":"BH"}},{"key":"SocialInsuranceReference","category":"identity","failClosed":true},{"key":"doc:Contract","category":"contract","failClosed":false}]""")]
    public async Task AnExistingTenantsStoredSeedRow_WithNoGate_ResolvesSocialInsuranceToThePayGate(
        string country, string nationality, string key, string legacySeedJson)
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        db.CompanyComplianceProfiles.Add(new CompanyComplianceProfile
        {
            TenantId = tenantId, CompanyId = null, CountryCode = country,
            Jurisdiction = string.Empty, CompliancePack = string.Empty,
            EffectiveFrom = new DateOnly(2020, 1, 1), Status = CompanyPolicyStatuses.Active,
            RequiredFieldsJson = legacySeedJson,
        });
        await db.SaveChangesAsync();

        var policy = await Resolver(db).ResolveAsync(tenantId, companyId: null, country, nationality);

        policy.Sources.Should().Contain("tenant", "the stored row is still a layer");
        var item = policy.Items.Single(i => i.Key == key);
        item.Gate.Should().Be("pay", "an omitted gate on a social-insurance key means the pay gate");
        item.FailClosed.Should().BeTrue("it still fails closed");
    }

    [Fact]
    public async Task AConfigRowThatExplicitlySaysActivate_IsStillHonoured()
    {
        // Config may tighten, never loosen: a tenant that deliberately wrote "gate":"activate" keeps it.
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        db.CompanyComplianceProfiles.Add(new CompanyComplianceProfile
        {
            TenantId = tenantId, CompanyId = null, CountryCode = "SA",
            Jurisdiction = string.Empty, CompliancePack = string.Empty,
            EffectiveFrom = new DateOnly(2020, 1, 1), Status = CompanyPolicyStatuses.Active,
            RequiredFieldsJson = """[{"key":"GosiReference","category":"identity","failClosed":true,"gate":"activate"}]""",
        });
        await db.SaveChangesAsync();

        var policy = await Resolver(db).ResolveAsync(tenantId, companyId: null, "SA", "Saudi");
        policy.Items.Single(i => i.Key == "GosiReference").Gate.Should().Be("activate");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  (2) A zero-requirement policy: honest, not Ready/100, and not a new block on live data
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    // "UAE" looks like a country but is neither ISO-2 nor ISO-3, so it normalises to nothing.
    [InlineData("", "No readiness policy applies: the employee's country is not set")]
    [InlineData("   ", "No readiness policy applies: the employee's country is not set")]
    [InlineData("UAE", "No readiness policy applies: country 'UAE' is not recognised")]
    [InlineData("IN", "No readiness policy configured for IN")]
    public async Task NoPolicy_IsNeedsAttentionAtScore0_WithANamedRecommendedItem_AndDoesNotBlock(
        string countryCode, string expectedLabel)
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var policy = await Resolver(db).ResolveAsync(tenantId, companyId: null, countryCode, "Indian");
        policy.Items.Should().BeEmpty("this is precisely the zero-requirement case");

        var readiness = Evaluator(db).Evaluate(
            new EmployeeReadinessSnapshot { EmployeeId = 3, CountryCode = countryCode, Nationality = "Indian", JoiningDate = Joined },
            policy);

        readiness.State.Should().Be("NeedsAttention", "never a fabricated Ready");
        readiness.Score.Should().Be(0m, "nothing was checked, so nothing is proven");
        var item = readiness.Recommended.Should().ContainSingle().Subject;
        item.Key.Should().Be(EmployeeReadinessEvaluator.NoPolicyKey);
        item.Label.Should().Be(expectedLabel, "the item must name the actual cause");
        item.Reason.Should().Be("not_evaluable");
        // NOT a new gate on live data: no activation blocker and no pay blocker, so activation still works
        // and an Active employee does not become a payroll drift warning (the pay interlock keys on these).
        readiness.IsBlocked.Should().BeFalse();
        readiness.Blocking.Should().BeEmpty();
        readiness.PayBlocking.Should().BeEmpty();
    }

    [Fact]
    public async Task NoPolicy_StillActivates_BecauseNothingAboutTheRecordChanged()
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var snapshot = new EmployeeReadinessSnapshot { EmployeeId = 4, CountryCode = string.Empty, Nationality = "Indian", JoiningDate = Joined };

        var readiness = await new EmployeeActivationGuard(db).EnsureActivatableAsync(tenantId, null, snapshot, Ctx(tenantId), default);

        readiness.State.Should().Be("NeedsAttention");
        readiness.Blocking.Should().BeEmpty();
    }

    [Fact]
    public async Task AKnownCountryWithAConfiguredPolicyItSatisfies_IsGenuinelyReadyAtScore100()
    {
        // The other direction: the no-policy outcome fires on the ABSENCE of a policy, never on a satisfied one.
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        db.CompanyComplianceProfiles.Add(new CompanyComplianceProfile
        {
            TenantId = tenantId, CompanyId = null, CountryCode = "IN",
            Jurisdiction = string.Empty, CompliancePack = string.Empty,
            EffectiveFrom = new DateOnly(2020, 1, 1), Status = CompanyPolicyStatuses.Active,
            RequiredFieldsJson = """[{"key":"FullName","category":"personal","failClosed":true}]""",
        });
        await db.SaveChangesAsync();

        var policy = await Resolver(db).ResolveAsync(tenantId, companyId: null, "IN", "Indian");
        var readiness = Evaluator(db).Evaluate(new EmployeeReadinessSnapshot
        {
            EmployeeId = 6, CountryCode = "IN", Nationality = "Indian", FullName = "Priya Nair", JoiningDate = Joined,
        }, policy);

        readiness.State.Should().Be("Ready");
        readiness.Score.Should().Be(100m);
        readiness.Blocking.Should().BeEmpty();
        readiness.Present.Should().ContainSingle(i => i.Key == "FullName");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  (3) An unknown joining date blocks activation — with a policy and without one
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("SA", "Saudi", "1234567890")]   // a GCC floor policy the record otherwise satisfies
    [InlineData("", "Indian", "")]              // no policy at all
    public async Task UnknownJoiningDate_BlocksActivation_OnTheJoiningDate(string country, string nationality, string idNumber)
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var snapshot = new EmployeeReadinessSnapshot
        {
            EmployeeId = 9, CountryCode = country, Nationality = nationality, IdNumber = idNumber,
            JoiningDate = default,   // the CSV cell could not be read, so the import left it unknown
        };
        var policy = await Resolver(db).ResolveAsync(tenantId, null, country, nationality);

        var readiness = Evaluator(db).Evaluate(snapshot, policy);

        readiness.State.Should().Be("Blocked");
        var blocker = readiness.Blocking.Should().ContainSingle(i => i.Key == "JoiningDate").Subject;
        blocker.Gate.Should().Be("activate");
        blocker.FixTarget.Should().Be("joiningDate", "the checklist offers the joining-date field as the fix");
        var act = () => new EmployeeActivationGuard(db).EnsureActivatableAsync(tenantId, null, snapshot, Ctx(tenantId), default);
        await act.Should().ThrowAsync<EmployeeActivationBlockedException>();
    }

    [Fact]
    public async Task AKnownJoiningDate_AddsNothing_SoExistingScoresDoNotMove()
    {
        await using var db = NewDb();
        var policy = await Resolver(db).ResolveAsync(Guid.NewGuid(), null, "SA", "Saudi");
        var withDate = Evaluator(db).Evaluate(new EmployeeReadinessSnapshot
        {
            EmployeeId = 10, CountryCode = "SA", Nationality = "Saudi", IdNumber = "1234567890", JoiningDate = Joined,
        }, policy);

        withDate.Blocking.Should().NotContain(i => i.Key == "JoiningDate");
        withDate.Present.Should().NotContain(i => i.Key == "JoiningDate",
            "the integrity check is only ever emitted when violated, so it cannot shift a satisfied record's score");
    }
}
