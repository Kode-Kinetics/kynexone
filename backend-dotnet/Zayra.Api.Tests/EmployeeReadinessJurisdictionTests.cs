using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// DEFECT (P0): a Saudi employee could not be activated without an Emirates ID and a Qatar ID.
///
/// <see cref="CompanyComplianceProfile"/> and <see cref="GCCComplianceSetting"/> are both keyed by
/// (tenant, company, COUNTRY), and <see cref="TenantProvisioningBundle"/> seeds ONE TENANT-DEFAULT
/// PROFILE PER GCC STATE on every new tenant. The readiness policy resolver selected its config
/// layers on tenant + company + effective date ONLY — never on country — so an employee of a Saudi
/// company was handed another jurisdiction's identity documents (EmiratesId, WorkPermitNumber, Qid)
/// as fail-closed ACTIVATE blockers that they can never satisfy.
///
/// Every test here fails against the pre-fix resolver and passes after it. The final two are the
/// fail-closed ratchet: narrowing the policy to the employing country must NOT make the gate softer
/// for the documents that country really does require.
/// </summary>
public class EmployeeReadinessJurisdictionTests
{
    private static ZayraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static EmployeeReadinessPolicyResolver Resolver(ZayraDbContext db) => new(db);

    /// <summary>Writes the REAL provisioning seed — the six per-country tenant-default profiles.
    /// <paramref name="editedCountry"/> gets a later EffectiveFrom, which is what a tenant that has
    /// since edited one entity's profile looks like; it also makes the pre-fix "arbitrary row wins"
    /// selection deterministic, so the without-fix failure is reproducible rather than order-luck.</summary>
    private static void SeedProvisionedTenantDefaults(ZayraDbContext db, Guid tenantId, string editedCountry)
    {
        foreach (var (country, json) in TenantProvisioningBundle.ComplianceSeeds)
        {
            db.CompanyComplianceProfiles.Add(new CompanyComplianceProfile
            {
                TenantId = tenantId,
                CompanyId = null,
                CountryCode = country,
                Status = CompanyPolicyStatuses.Active,
                EffectiveFrom = country == editedCountry ? new DateOnly(2024, 1, 1) : new DateOnly(2020, 1, 1),
                RequiredFieldsJson = json,
            });
        }
        db.SaveChanges();
    }

    [Fact]
    public async Task ProvisionedTenant_SaudiEmployee_IsNotAskedForAnotherCountrysIdentityDocuments()
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        SeedProvisionedTenantDefaults(db, tenantId, editedCountry: "AE");

        var policy = await Resolver(db).ResolveAsync(tenantId, companyId: null, "SA", "Indian");

        policy.Items.Should().NotContain(i => i.Key == "EmiratesId",
            "an Emirates ID is a UAE identity card — the UAE tenant-default profile must not reach a Saudi employee");
        policy.Items.Should().NotContain(i => i.Key == "WorkPermitNumber",
            "a MOHRE work permit is UAE work authorization");
        policy.Items.Should().NotContain(i => i.Key == "Qid" || i.Key == "CivilId",
            "Qatari/Kuwaiti/Omani/Bahraini identity cards belong to their own jurisdictions");
    }

    [Fact]
    public async Task CompanyProfileForAnotherCountry_IsNotLayeredOntoTheEmployingCountry()
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var saudiCompanyId = Guid.NewGuid();
        SeedProvisionedTenantDefaults(db, tenantId, editedCountry: "SA");
        // The same legal entity also carries a Qatar profile row (a multi-jurisdiction tenant, or a
        // row created against the wrong country). It must not gate the Saudi employee.
        db.CompanyComplianceProfiles.Add(new CompanyComplianceProfile
        {
            TenantId = tenantId,
            CompanyId = saudiCompanyId,
            CountryCode = "QA",
            Status = CompanyPolicyStatuses.Active,
            EffectiveFrom = new DateOnly(2024, 6, 1),
            RequiredFieldsJson = """[{"key":"Qid","category":"identity","failClosed":true}]""",
        });
        await db.SaveChangesAsync();

        var policy = await Resolver(db).ResolveAsync(tenantId, saudiCompanyId, "SA", "Indian");

        policy.Items.Should().NotContain(i => i.Key == "Qid",
            "a Qatar ID requirement configured for another jurisdiction must never block a Saudi activation");
        policy.Sources.Should().NotContain("company",
            "no company profile for the employing country exists, so that layer contributes nothing");
    }

    [Fact]
    public async Task GccSettingForAnotherCountry_DoesNotAddItsIdentityToggle()
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        // A tenant that runs a UAE entity has EmiratesIdRequired on its UAE settings row.
        db.GCCComplianceSettings.Add(new GCCComplianceSetting
        {
            TenantId = tenantId, CompanyId = null, CountryCode = "AE",
            EmiratesIdRequired = true, VisaTrackingEnabled = false,
        });
        await db.SaveChangesAsync();

        var policy = await Resolver(db).ResolveAsync(tenantId, companyId: null, "SA", "Indian");

        policy.Items.Should().NotContain(i => i.Key == "EmiratesId",
            "the UAE settings row is not the Saudi entity's configuration");
    }

    [Fact]
    public async Task GccSettingForAnotherCountry_DoesNotLeakItsWpsFlag()
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        db.GCCComplianceSettings.Add(new GCCComplianceSetting
        {
            TenantId = tenantId, CompanyId = null, CountryCode = "AE",
            WpsEnabled = true, VisaTrackingEnabled = false,
        });
        await db.SaveChangesAsync();

        var policy = await Resolver(db).ResolveAsync(tenantId, companyId: null, "SA", "Indian");

        // The floor's IBAN is pay-gated; only the EMPLOYING country's WPS flag may upgrade it to activate.
        policy.Items.Should().Contain(i => i.Key == "BankIban" && i.Gate == "pay",
            "UAE WPS must not turn an IBAN into a Saudi activation blocker");
    }

    // ── Fail-closed ratchet: the narrowing must not weaken the employing country's own gate ──

    [Fact]
    public async Task EmployingCountrysOwnProfileAndSettingStillApply()
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        SeedProvisionedTenantDefaults(db, tenantId, editedCountry: "AE");
        db.GCCComplianceSettings.Add(new GCCComplianceSetting
        {
            TenantId = tenantId, CompanyId = null, CountryCode = "SA",
            IqamaRequired = true, WpsEnabled = true, VisaTrackingEnabled = false,
        });
        await db.SaveChangesAsync();

        var policy = await Resolver(db).ResolveAsync(tenantId, companyId: null, "SA", "Indian");

        policy.Sources.Should().Contain("tenant", "the SA tenant-default profile is still a layer");
        policy.Sources.Should().Contain("gcc-setting", "the SA settings row is still a layer");
        policy.Items.Should().Contain(i => i.Key == "GosiReference" && i.FailClosed && i.Gate == "activate");
        policy.Items.Should().Contain(i => i.Key == "IqamaNumber" && i.FailClosed && i.Gate == "activate",
            "an expat in KSA still needs an Iqama");
        policy.Items.Should().Contain(i => i.Key == "BankIban" && i.Gate == "activate",
            "KSA's own WPS flag still upgrades the IBAN to an activation blocker");
    }

    [Fact]
    public async Task UaeEmployee_StillRequiresTheEmiratesId()
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        SeedProvisionedTenantDefaults(db, tenantId, editedCountry: "SA");

        var policy = await Resolver(db).ResolveAsync(tenantId, companyId: null, "AE", "Indian");

        policy.Items.Should().Contain(i => i.Key == "EmiratesId" && i.FailClosed && i.Gate == "activate",
            "narrowing to the employing country must not lose the UAE's own identity requirement");
        policy.Items.Should().Contain(i => i.Key == "WorkPermitNumber" && i.FailClosed);
        policy.Items.Should().NotContain(i => i.Key == "GosiReference", "GOSI is a Saudi scheme");
    }

    [Fact]
    public async Task SaudiEmployee_MissingGosi_IsStillBlocked()
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        SeedProvisionedTenantDefaults(db, tenantId, editedCountry: "AE");
        var policy = await Resolver(db).ResolveAsync(tenantId, companyId: null, "SA", "Saudi");

        var snapshot = new EmployeeReadinessSnapshot { CountryCode = "SA", Nationality = "SA", IdNumber = "1234567890" };
        var readiness = new EmployeeReadinessEvaluator(db).Evaluate(snapshot, policy);

        readiness.IsBlocked.Should().BeTrue("a missing RELEVANT identifier must still fail closed");
        readiness.Blocking.Should().Contain(i => i.Key == "GosiReference");
    }

    [Fact]
    public async Task SaudiNational_WithSaudiIdentifiers_IsReadyToActivate()
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        SeedProvisionedTenantDefaults(db, tenantId, editedCountry: "AE");
        var policy = await Resolver(db).ResolveAsync(tenantId, companyId: null, "SA", "Saudi");

        var snapshot = new EmployeeReadinessSnapshot
        {
            CountryCode = "SA",
            Nationality = "SA",
            IdNumber = "1234567890",          // Hawiyya — the host-national identity card
            GosiReference = "GOSI-99887766",
        };
        var readiness = new EmployeeReadinessEvaluator(db).Evaluate(snapshot, policy);

        readiness.Blocking.Should().BeEmpty(
            "a Saudi national in a Saudi company holding a national ID and a GOSI reference is activatable; "
            + "before the fix they were blocked on EmiratesId/Qid/CivilId they can never hold");
        readiness.IsBlocked.Should().BeFalse();
    }
}
