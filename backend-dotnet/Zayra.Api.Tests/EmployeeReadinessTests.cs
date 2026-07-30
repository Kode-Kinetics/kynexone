using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Readiness POLICY RESOLVER (statutory-floor UNION), EVALUATOR (fail-closed presence), field REGISTRY,
/// and the write-side VALIDATOR — the core of invariants (1) fail-closed registry, (2) UNION
/// strictest-wins, (4) split primitive. Pure/InMemory, no HTTP.
/// </summary>
public class EmployeeReadinessTests
{
    private static ZayraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static EmployeeReadinessPolicyResolver Resolver(ZayraDbContext db) => new(db);
    private static EmployeeReadinessEvaluator Evaluator(ZayraDbContext db) => new(db);

    private static EmployeeReadinessSnapshot Snap(string country, string nationality, Action<EmployeeReadinessSnapshotBuilder>? cfg = null)
    {
        var b = new EmployeeReadinessSnapshotBuilder { CountryCode = country, Nationality = nationality };
        cfg?.Invoke(b);
        return b.Build();
    }

    // ── UNION floor resolver ─────────────────────────────────────────────────

    [Fact]
    public async Task FreshTenant_NoProfile_StillGatesGccCountry()
    {
        await using var db = NewDb();
        var policy = await Resolver(db).ResolveAsync(Guid.NewGuid(), null, "SA", "Indian");
        // Code floor is the guarantee even with no profile + no GCC setting row.
        policy.Items.Should().Contain(i => i.Key == "GosiReference" && i.FailClosed);
        policy.Items.Should().Contain(i => i.Key == "IqamaNumber" && i.FailClosed, "an expat in KSA needs an Iqama");
        policy.Sources.Should().Contain("floor");
    }

    [Fact]
    public async Task NonGccCountry_HasEmptyFloor()
    {
        await using var db = NewDb();
        var policy = await Resolver(db).ResolveAsync(Guid.NewGuid(), null, "US", "American");
        policy.Items.Should().BeEmpty("non-GCC countries carry no code floor; only config profiles apply");
    }

    [Fact]
    public async Task Config_OmitsFloorKey_FloorStillEnforced()
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        // A KSA company profile that lists ONLY EmiratesId (omits GOSI/Iqama entirely).
        db.CompanyComplianceProfiles.Add(new CompanyComplianceProfile
        {
            TenantId = tenantId, CompanyId = null, CountryCode = "SA", Status = "Active",
            EffectiveFrom = new DateOnly(2020, 1, 1),
            RequiredFieldsJson = """[{"key":"EmiratesId","failClosed":true}]""",
        });
        await db.SaveChangesAsync();

        var policy = await Resolver(db).ResolveAsync(tenantId, null, "SA", "Indian");
        policy.Items.Should().Contain(i => i.Key == "GosiReference" && i.FailClosed,
            "the floor's GOSI requirement survives even though the profile omitted it (never weaker)");
        policy.Items.Should().Contain(i => i.Key == "EmiratesId", "config may ADD keys");
    }

    [Fact]
    public async Task Config_DowngradesFloorKey_FloorWins_AndContradictionRecorded()
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        db.CompanyComplianceProfiles.Add(new CompanyComplianceProfile
        {
            TenantId = tenantId, CompanyId = null, CountryCode = "SA", Status = "Active",
            EffectiveFrom = new DateOnly(2020, 1, 1),
            RequiredFieldsJson = """[{"key":"GosiReference","failClosed":false}]""",
        });
        await db.SaveChangesAsync();

        var policy = await Resolver(db).ResolveAsync(tenantId, null, "SA", "Saudi");
        policy.Items.Single(i => i.Key == "GosiReference").FailClosed.Should().BeTrue("floor wins over a config downgrade");
        policy.FloorContradictions.Should().Contain(c => c.StartsWith("GosiReference"));
    }

    [Fact]
    public async Task AppliesWhen_SaudiNational_NotRequiredToHaveIqama()
    {
        await using var db = NewDb();
        var policy = await Resolver(db).ResolveAsync(Guid.NewGuid(), null, "SA", "Saudi");
        policy.Items.Should().NotContain(i => i.Key == "IqamaNumber", "an Iqama is an expat requirement, wrong for a Saudi national");
        policy.Items.Should().Contain(i => i.Key == "GosiReference", "GOSI applies to all");
        policy.Items.Should().Contain(i => i.Key == "IdNumber", "a Saudi national needs a national ID");
    }

    [Fact]
    public async Task Resolver_SeesTenantDefaultRow_ViaIgnoreQueryFilters()
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        db.CompanyComplianceProfiles.Add(new CompanyComplianceProfile
        {
            TenantId = tenantId, CompanyId = null, CountryCode = "US", Status = "Active",
            EffectiveFrom = new DateOnly(2020, 1, 1),
            RequiredFieldsJson = """[{"key":"WorkEmail","failClosed":true}]""",
        });
        await db.SaveChangesAsync();
        // Resolving FOR a company still unions in the CompanyId==null tenant-default row.
        var policy = await Resolver(db).ResolveAsync(tenantId, companyId, "US", "American");
        policy.Items.Should().Contain(i => i.Key == "WorkEmail" && i.FailClosed);
    }

    // ── Evaluator: fail-closed + IBAN + expiry ───────────────────────────────

    private static ResolvedReadinessPolicy Policy(params ReadinessRequirement[] items) =>
        new("SA", "SA", "certified", items, new[] { "floor" }, System.Array.Empty<string>(), GccReadinessFloor.Disclaimer);

    [Fact]
    public void Evaluator_UnknownKey_FailsClosed()
    {
        var policy = Policy(new ReadinessRequirement("NotARealField", "identity", true, "activate", "company"));
        var readiness = Evaluator(NewDb()).Evaluate(Snap("SA", "SA"), policy);
        readiness.Blocking.Should().Contain(i => i.Key == "NotARealField", "an unresolvable required key blocks (never silently passes)");
        readiness.IsBlocked.Should().BeTrue();
    }

    [Fact]
    public void Evaluator_IbanPresentButInvalid_IsBlocker()
    {
        var policy = Policy(new ReadinessRequirement("BankIban", "payroll", true, "activate", "company"));
        var readiness = Evaluator(NewDb()).Evaluate(Snap("SA", "SA", b => b.BankIban = "SA00INVALID"), policy);
        readiness.Blocking.Should().Contain(i => i.Key == "BankIban" && i.Reason == "invalid");
    }

    [Fact]
    public void Evaluator_IbanAbsent_ActivateGate_IsActivateBlocker()
    {
        var policy = Policy(new ReadinessRequirement("BankIban", "payroll", true, "activate", "company"));
        var readiness = Evaluator(NewDb()).Evaluate(Snap("SA", "SA"), policy);
        readiness.Blocking.Should().Contain(i => i.Key == "BankIban");
    }

    [Fact]
    public void Evaluator_MissingId_ActivateBlock_ExpiredId_PayBlock_ExpiringSoon_Amber()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        // Missing Iqama (activate) blocks activation.
        Evaluator(NewDb()).Evaluate(Snap("SA", "IN"),
            Policy(new ReadinessRequirement("IqamaNumber", "identity", true, "activate", "floor")))
            .Blocking.Should().Contain(i => i.Key == "IqamaNumber");

        // Present-but-expired visa (pay) is a PAY blocker, not an activation blocker.
        var expired = Evaluator(NewDb()).Evaluate(
            Snap("AE", "IN", b => { b.VisaNumber = "V1"; b.VisaExpiryDate = today.AddDays(-1); }),
            Policy(new ReadinessRequirement("VisaExpiryDate", "identity", true, "pay", "floor")));
        expired.PayBlocking.Should().Contain(i => i.Key == "VisaExpiryDate" && i.Reason == "expired");
        expired.Blocking.Should().BeEmpty();

        // Within the alert window → amber only, never blocks.
        var amber = Evaluator(NewDb()).Evaluate(
            Snap("AE", "IN", b => b.VisaExpiryDate = today.AddDays(10)),
            Policy(new ReadinessRequirement("VisaExpiryDate", "identity", true, "pay", "floor")));
        amber.ExpiringSoon.Should().Contain(i => i.Key == "VisaExpiryDate");
        amber.PayBlocking.Should().BeEmpty();
    }

    [Fact]
    public void Evaluator_DocContract_RequireVerified()
    {
        var policy = Policy(new ReadinessRequirement("doc:Contract", "document", true, "activate", "company", RequireVerified: true));
        // Present but unverified ⇒ still missing when requireVerified.
        Evaluator(NewDb()).Evaluate(Snap("SA", "SA", b => b.Documents = new[] { new DocumentPresence("Contract", false, null) }), policy)
            .Blocking.Should().Contain(i => i.Key == "doc:Contract");
        // Verified ⇒ satisfied.
        Evaluator(NewDb()).Evaluate(Snap("SA", "SA", b => b.Documents = new[] { new DocumentPresence("Contract", true, null) }), policy)
            .Blocking.Should().BeEmpty();
    }

    // ── Write-side validator hardening ───────────────────────────────────────

    [Fact]
    public void Validator_RejectsUnknownKey_AcceptsKnown_AndFieldAlias()
    {
        CompanyComplianceProfilesController.ValidateRequiredFieldsJson("""[{"key":"NotARealField","failClosed":true}]""")
            .Should().NotBeNull("unknown keys are rejected at write — closes the fail-open hole");
        CompanyComplianceProfilesController.ValidateRequiredFieldsJson("""[{"key":"IqamaNumber","failClosed":true}]""")
            .Should().BeNull();
        CompanyComplianceProfilesController.ValidateRequiredFieldsJson("""[{"field":"GosiReference","failClosed":true}]""")
            .Should().BeNull("'field' is a back-compat alias for 'key'");
        CompanyComplianceProfilesController.ValidateRequiredFieldsJson("""[{"key":"IqamaNumber","category":"bogus"}]""")
            .Should().NotBeNull("unknown category rejected");
        CompanyComplianceProfilesController.ValidateRequiredFieldsJson("""[{"key":"IqamaNumber","gate":"bogus"}]""")
            .Should().NotBeNull("unknown gate rejected");
        CompanyComplianceProfilesController.ValidateRequiredFieldsJson("{not json")
            .Should().NotBeNull("malformed JSON still 400s");
        CompanyComplianceProfilesController.ValidateRequiredFieldsJson("""[{"key":"doc:Contract","failClosed":true}]""")
            .Should().BeNull("doc:{Type} keys are resolvable");
    }
}

/// <summary>Mutable builder so tests can spell out only the fields they care about.</summary>
public sealed class EmployeeReadinessSnapshotBuilder
{
    public string CountryCode = "";
    public string Nationality = "";
    public string BankIban = "";
    public string VisaNumber = "";
    public DateOnly? VisaExpiryDate;
    public IReadOnlyList<DocumentPresence> Documents = System.Array.Empty<DocumentPresence>();

    public EmployeeReadinessSnapshot Build() => new()
    {
        CountryCode = CountryCode,
        Nationality = Nationality,
        BankIban = BankIban,
        VisaNumber = VisaNumber,
        VisaExpiryDate = VisaExpiryDate,
        Documents = Documents,
    };
}
