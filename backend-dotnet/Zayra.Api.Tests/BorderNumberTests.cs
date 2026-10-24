using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// KSA Border Number (رقم الحدود) — the number Jawazat issues when an expat first enters on a work visa,
/// BEFORE the Iqama exists. A new arrival works on it, so:
///   * the Iqama activation requirement for a non-GCC expat in KSA is "Iqama OR border number" — a held
///     border number satisfies it PROVISIONALLY (activation proceeds, the checklist says the Iqama is due);
///   * the field is SA-only, expat-only, sensitive, masked like every other national ID;
///   * the documents issued after hire (Iqama, GOSI, Qiwa, Muqeem, sponsor, work permit, visa file) are
///     flagged so the create form never presents them as needed up front.
/// Nothing here may loosen another country's rule.
/// Duplicate detection / create / import mapping live in <see cref="EmployeeDuplicateDetectionTests"/>.
/// </summary>
public class BorderNumberTests
{
    private static ZayraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<ResolvedReadinessPolicy> SaPolicyAsync(ZayraDbContext db, Guid tenantId, string nationality)
        => await new EmployeeReadinessPolicyResolver(db).ResolveAsync(tenantId, companyId: null, "SA", nationality);

    private static readonly DateTime Today = DateTime.UtcNow.Date;

    /// <summary>A KSA employee. <paramref name="complete"/> fills every PAY-stage document so a test can knock
    /// out exactly one; the Iqama + expiry are controlled separately.</summary>
    private static EmployeeReadinessSnapshot SaEmployee(
        string iqama = "", string border = "", string nationality = "Indian", int joinedDaysAgo = 10,
        bool complete = false, DateOnly? iqamaExpiry = null, string? without = null)
    {
        string V(string key, string value) => complete && key != without ? value : string.Empty;
        return new EmployeeReadinessSnapshot
        {
            EmployeeId = 1,
            CountryCode = "SA",
            Nationality = nationality,
            EnglishName = "New Arrival",
            FullName = "New Arrival",
            JoiningDate = Today.AddDays(-joinedDaysAgo),
            IqamaNumber = iqama,
            IqamaExpiryDate = iqamaExpiry,
            BorderNumber = border,
            IdNumber = nationality == "Saudi" ? "1012345678" : string.Empty,
            GosiReference = V("GosiReference", "GOSI-1"),
            QiwaContractNumber = V("QiwaContractNumber", "QIWA-1"),
            MuqeemNumber = V("MuqeemNumber", "MUQ-1"),
            SponsorName = V("SponsorName", "Employer Co"),
            WorkPermitNumber = V("WorkPermitNumber", "WP-1"),
            VisaNumber = V("VisaNumber", "VISA-1"),
            VisaFileNumber = V("VisaFileNumber", "VF-1"),
        };
    }

    private static EmployeeReadinessSnapshot SaExpat(string iqama = "", string border = "", string nationality = "Indian")
        => SaEmployee(iqama, border, nationality);

    private static EmployeeReadiness Evaluate(ZayraDbContext db, EmployeeReadinessSnapshot s, ResolvedReadinessPolicy p)
        => new EmployeeReadinessEvaluator(db).Evaluate(s, p);

    /// <summary>The documents issued after hire that the owner made mandatory before payroll (KSA).</summary>
    public static IEnumerable<object[]> PayStageDocuments() => new[]
    {
        new object[] { "GosiReference" }, new object[] { "QiwaContractNumber" }, new object[] { "MuqeemNumber" },
        new object[] { "SponsorName" }, new object[] { "WorkPermitNumber" }, new object[] { "VisaNumber" },
        new object[] { "VisaFileNumber" },
    };

    // ══════════════════════════ READINESS: activation (Iqama OR border number) ══════════════════════════

    [Fact]
    public async Task SaExpat_WithNeitherIqamaNorBorderNumber_IsStillBlockedAtActivation()
    {
        await using var db = NewDb();
        var r = Evaluate(db, SaExpat(), await SaPolicyAsync(db, Guid.NewGuid(), "Indian"));

        r.State.Should().Be("Blocked");
        r.Blocking.Should().ContainSingle(i => i.Key == "IqamaNumber")
            .Which.Label.Should().Be("Iqama or border number", "HR must learn that a border number would also do");
    }

    [Fact]
    public async Task SaExpat_OnBorderNumberOnly_IsActivatable_AndTheIqamaStaysOnTheChecklistAsDue()
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var policy = await SaPolicyAsync(db, tenantId, "Indian");
        var r = Evaluate(db, SaExpat(border: "3012345678"), policy);

        r.IsBlocked.Should().BeFalse("a new arrival works on the border number until the Iqama is issued");
        r.Blocking.Should().BeEmpty("none of the documents issued after hire may block activation");
        var due = r.Recommended.Should().ContainSingle(i => i.Key == "IqamaNumber").Subject;
        due.Label.Should().Be("Iqama due — employee is on border number");
        due.Reason.Should().Be("provisional");
        r.Present.Should().NotContain(i => i.Key == "IqamaNumber", "the Iqama itself is still not held");
        r.State.Should().Be("NeedsAttention");

        // The real activation gate agrees.
        var guard = new EmployeeReadinessEvaluator(db);
        await FluentActions.Awaiting(() => guard.EnsureActivatableAsync(SaExpat(border: "3012345678"), policy,
                new Zayra.Api.Application.Auth.RequestContext("127.0.0.1", "tests", Guid.NewGuid(), tenantId, ["HR Manager"], []),
                CancellationToken.None))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task SaExpat_WithIqama_IsSatisfied_WhetherOrNotABorderNumberIsAlsoHeld()
    {
        await using var db = NewDb();
        var policy = await SaPolicyAsync(db, Guid.NewGuid(), "Indian");

        foreach (var border in new[] { "", "3012345678" })
        {
            var r = Evaluate(db, SaExpat(iqama: "2123456789", border: border), policy);
            r.Present.Should().Contain(i => i.Key == "IqamaNumber");
            r.Recommended.Should().NotContain(i => i.Key == "IqamaNumber");
            r.Blocking.Should().NotContain(i => i.Key == "IqamaNumber");
        }
    }

    /// <summary>The OR lives in the registry getter, so it holds for EVERY layer that names IqamaNumber — the
    /// code floor, the seeded tenant-default profile (failClosed, no gate) and the GCC-setting IqamaRequired
    /// toggle — and strictest-wins merging cannot re-harden it.</summary>
    [Fact]
    public async Task BorderNumber_SatisfiesIqama_UnderTheProvisionedProfile_AndTheIqamaRequiredToggle()
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        foreach (var (country, json) in TenantProvisioningBundle.ComplianceSeeds)
            db.CompanyComplianceProfiles.Add(new CompanyComplianceProfile
            {
                TenantId = tenantId, CompanyId = null, CountryCode = country, Status = CompanyPolicyStatuses.Active,
                EffectiveFrom = new DateOnly(2020, 1, 1), RequiredFieldsJson = json,
            });
        db.GCCComplianceSettings.Add(new GCCComplianceSetting { TenantId = tenantId, CompanyId = null, CountryCode = "SA", IqamaRequired = true });
        await db.SaveChangesAsync();
        var policy = await SaPolicyAsync(db, tenantId, "Indian");
        policy.Items.Should().Contain(i => i.Key == "IqamaNumber" && i.FailClosed && i.Gate == "activate");

        Evaluate(db, SaExpat(border: "3012345678"), policy).IsBlocked.Should().BeFalse();
        Evaluate(db, SaExpat(), policy).Blocking.Should().Contain(i => i.Key == "IqamaNumber");
    }

    // ══════════════════════════ READINESS: pay stage ══════════════════════════

    [Fact]
    public async Task SaExpat_PayStage_RequiresEveryDocumentIssuedAfterHire_AndBlocksNoneOfThemAtActivation()
    {
        await using var db = NewDb();
        var policy = await SaPolicyAsync(db, Guid.NewGuid(), "Indian");
        foreach (var key in PayStageDocuments().Select(x => (string)x[0]))
            policy.Items.Should().Contain(i => i.Key == key && i.FailClosed && i.Gate == "pay", $"{key} is required before payroll");

        var r = Evaluate(db, SaEmployee(border: "3012345678"), policy);
        r.Blocking.Should().BeEmpty();
        r.PayBlocking.Select(i => i.Key).Should().BeEquivalentTo(PayStageDocuments().Select(x => (string)x[0]),
            "with nothing recorded, every document issued after hire is a pay blocker — and only those (the border number covers the Iqama)");
    }

    [Theory]
    [MemberData(nameof(PayStageDocuments))]
    public async Task SaExpat_PayStage_FailsForEachMissingDocument(string key)
    {
        await using var db = NewDb();
        var policy = await SaPolicyAsync(db, Guid.NewGuid(), "Indian");
        var future = DateOnly.FromDateTime(Today).AddYears(1);

        Evaluate(db, SaEmployee(iqama: "2123456789", iqamaExpiry: future, complete: true), policy)
            .PayBlocking.Should().BeEmpty("a fully documented expat is payroll-ready");

        var r = Evaluate(db, SaEmployee(iqama: "2123456789", iqamaExpiry: future, complete: true, without: key), policy);
        r.PayBlocking.Should().ContainSingle().Which.Key.Should().Be(key);
        r.IsBlocked.Should().BeFalse("a document issued after hire never blocks activation");
    }

    [Fact]
    public async Task SaudiNational_PayStage_RequiresGosiAndQiwaOnly_AndBorderNumberDoesNotStandInForTheNationalId()
    {
        await using var db = NewDb();
        var policy = await SaPolicyAsync(db, Guid.NewGuid(), "Saudi");
        policy.Items.Should().NotContain(i => i.Key == "IqamaNumber");

        var r = Evaluate(db, SaEmployee(nationality: "Saudi"), policy);
        r.PayBlocking.Select(i => i.Key).Should().BeEquivalentTo(new[] { "GosiReference", "QiwaContractNumber" },
            "Muqeem, sponsor, work permit and visa are expat-only");

        var noId = SaEmployee(border: "3012345678", nationality: "Saudi") with { IdNumber = string.Empty };
        Evaluate(db, noId, policy).Blocking.Should().Contain(i => i.Key == "IdNumber");
    }

    // ── The Iqama at pay: a border number is accepted for 90 days after joining, then the Iqama is required ──

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(90)]   // the last day of the window
    public async Task BorderNumber_StandsInForTheIqamaAtPay_WithinNinetyDaysOfJoining(int joinedDaysAgo)
    {
        await using var db = NewDb();
        var r = Evaluate(db, SaEmployee(border: "3012345678", joinedDaysAgo: joinedDaysAgo, complete: true),
            await SaPolicyAsync(db, Guid.NewGuid(), "Indian"));

        r.PayBlocking.Should().BeEmpty("inside the issuing window the border number covers the Iqama and its expiry");
        r.Recommended.Should().Contain(i => i.Key == "IqamaNumber" && i.Label == "Iqama due — employee is on border number");
        r.Recommended.Should().Contain(i => i.Key == "IqamaExpiry" && i.Reason == "provisional");
        r.IsBlocked.Should().BeFalse();
    }

    [Theory]
    [InlineData(91)]   // the first day after the window
    [InlineData(200)]
    public async Task AfterNinetyDays_TheIqamaIsRequiredAtPay_ButActivationStillAcceptsTheBorderNumber(int joinedDaysAgo)
    {
        await using var db = NewDb();
        var r = Evaluate(db, SaEmployee(border: "3012345678", joinedDaysAgo: joinedDaysAgo, complete: true),
            await SaPolicyAsync(db, Guid.NewGuid(), "Indian"));

        r.PayBlocking.Should().Contain(i => i.Key == "IqamaNumber"
            && i.Label == "Iqama required before payroll — the border-number period (90 days from joining) has ended");
        r.PayBlocking.Should().Contain(i => i.Key == "IqamaExpiry");
        r.IsBlocked.Should().BeFalse("the border number still identifies the person — activation is not reversed");
        r.Recommended.Should().NotContain(i => i.Key == "IqamaNumber");
    }

    [Fact]
    public async Task OnceTheIqamaIsRecorded_TheGraceNoLongerMatters()
    {
        await using var db = NewDb();
        var r = Evaluate(db,
            SaEmployee(iqama: "2123456789", border: "3012345678", joinedDaysAgo: 400, complete: true,
                iqamaExpiry: DateOnly.FromDateTime(Today).AddYears(1)),
            await SaPolicyAsync(db, Guid.NewGuid(), "Indian"));
        r.PayBlocking.Should().BeEmpty();
        r.State.Should().Be("NeedsAttention", "the universal recommended items (contract, salary, IBAN) are still open");
    }

    // ══════════════════════════ Other countries are unchanged ══════════════════════════

    [Fact]
    public async Task OtherCountries_AreNotLoosened_ByABorderNumber()
    {
        await using var db = NewDb();
        var policy = await new EmployeeReadinessPolicyResolver(db).ResolveAsync(Guid.NewGuid(), null, "AE", "Indian");
        var snap = new EmployeeReadinessSnapshot
        {
            EmployeeId = 2, CountryCode = "AE", Nationality = "Indian", FullName = "UAE Hire", EnglishName = "UAE Hire",
            JoiningDate = new DateTime(2026, 10, 1), BorderNumber = "3012345678",
        };

        var r = new EmployeeReadinessEvaluator(db).Evaluate(snap, policy);
        r.Blocking.Should().Contain(i => i.Key == "EmiratesId");
        r.Blocking.Should().Contain(i => i.Key == "WorkPermitNumber", "the UAE work permit is still an ACTIVATION gate");
        r.Recommended.Should().NotContain(i => i.Reason == "provisional");
    }

    [Theory]
    [InlineData("AE")] [InlineData("QA")] [InlineData("KW")] [InlineData("OM")] [InlineData("BH")]
    public void OtherGccFloors_GainNoneOfTheKsaPayStageDocuments(string country)
    {
        var floor = GccReadinessFloor.Resolve(country);
        floor.Should().NotContain(i => i.Key == "QiwaContractNumber" || i.Key == "MuqeemNumber"
            || i.Key == "SponsorName" || i.Key == "VisaFileNumber" || i.Key == "VisaNumber" || i.Key == "IqamaNumber");
        if (country == "AE")
            floor.Should().ContainSingle(i => i.Key == "WorkPermitNumber").Which.Gate.Should().Be("activate");
        else
            floor.Should().NotContain(i => i.Key == "WorkPermitNumber");
    }

    [Fact]
    public void BuildFromEmployee_CarriesTheBorderNumberIntoTheSnapshot()
    {
        var e = new Employee { Id = 7, CountryCode = "SA", Nationality = "Indian", FullName = "X", BorderNumber = "3012345678" };
        EmployeeReadinessEvaluator.BuildFromEmployee(e, null, [], new Dictionary<string, DateOnly?>(), hasSalary: false)
            .BorderNumber.Should().Be("3012345678");
    }

    // ══════════════════════════ CATALOG: visibility, sensitivity, issued-after-hire ══════════════════════════

    [Fact]
    public void Catalog_OffersBorderNumber_ToANonGccExpatInKsaOnly()
    {
        static bool Has(string country, string nat) =>
            EmployeeFieldRegistry.CatalogFor(country, nat).Any(d => d.Key == "BorderNumber");

        Has("SA", "Indian").Should().BeTrue();
        Has("SA", "Saudi").Should().BeFalse("a Saudi national never holds a border number");
        Has("SA", "AE").Should().BeFalse("a GCC national enters KSA on their national ID");
        Has("AE", "Indian").Should().BeFalse("the border number is a KSA (Jawazat) identifier");

        var d = EmployeeFieldRegistry.Catalog.Single(x => x.Key == "BorderNumber");
        d.Sensitive.Should().BeTrue();
        d.ComplianceFieldKey.Should().Be("border_number");
        d.CsvHeader.Should().Be("BorderNumber");
        d.ExpiryKey.Should().BeNull("a border number has no expiry");
        EmployeesController.EmployeeEditKey(d).Should().Be("borderNumber");
        EmployeesController.EditableEmployeeFields.Should().Contain("borderNumber");
        EmployeeFieldRegistry.CsvHeaders.Should().Contain("BorderNumber");
    }

    [Theory]
    [InlineData("IqamaNumber")]
    [InlineData("IqamaExpiry")]
    [InlineData("GosiReference")]
    [InlineData("QiwaContractNumber")]
    [InlineData("MuqeemNumber")]
    [InlineData("SponsorName")]
    [InlineData("WorkPermitNumber")]
    [InlineData("VisaFileNumber")]
    [InlineData("VisaNumber")]
    public void KsaDocumentsIssuedAfterHire_AreFlagged(string key)
    {
        var d = EmployeeFieldRegistry.Catalog.Single(x => x.Key == key);
        EmployeeFieldRegistry.IsIssuedAfterHire(d, "SA").Should().BeTrue();
    }

    [Fact]
    public void BorderNumberPassportAndOtherCountries_AreNotFlaggedIssuedAfterHire()
    {
        foreach (var key in new[] { "BorderNumber", "PassportNumber", "IdNumber" })
            EmployeeFieldRegistry.IsIssuedAfterHire(EmployeeFieldRegistry.Catalog.Single(x => x.Key == key), "SA")
                .Should().BeFalse($"{key} is held before (or at) hire");
        // Scoped to KSA: the UAE work permit is still presented exactly as before.
        EmployeeFieldRegistry.IsIssuedAfterHire(EmployeeFieldRegistry.Catalog.Single(x => x.Key == "WorkPermitNumber"), "AE")
            .Should().BeFalse();
    }

    [Fact]
    public void CatalogIntegrityGuards_StillHold()
        => FluentActions.Invoking(EmployeeFieldRegistry.AssertCatalogIntegrity).Should().NotThrow();

    [Fact]
    public void KsaFormat_ForBorderNumber_IsTheUsualTenDigitsStartingWith3()
    {
        var (pattern, hint) = new KsaIdentityDocumentFormat().GetFormat("BorderNumber");
        pattern.Should().NotBeNull();
        System.Text.RegularExpressions.Regex.IsMatch("3012345678", pattern!).Should().BeTrue();
        System.Text.RegularExpressions.Regex.IsMatch("2012345678", pattern!).Should().BeFalse();
        hint.Should().Contain("3");
    }

    // ══════════════════════════ UPDATE + MASKING ══════════════════════════

    [Fact]
    public void ChangeApplier_WritesTheBorderNumber()
    {
        var e = new Employee();
        var unknown = EmployeeChangeApplier.Apply(e, new Dictionary<string, JsonElement>
        {
            ["borderNumber"] = JsonSerializer.SerializeToElement("3012345678"),
        });
        unknown.Should().BeEmpty();
        e.BorderNumber.Should().Be("3012345678");
    }

    [Fact]
    public void DetailDto_MasksTheBorderNumber_WithoutSensitiveAccess()
    {
        var e = new Employee { FullName = "X", BorderNumber = "3012345678" };
        EmployeeDetailDto.Project(e, includeSensitive: true).BorderNumber.Should().Be("3012345678");
        EmployeeDetailDto.Project(e, includeSensitive: false).BorderNumber.Should().BeEmpty();
    }

    [Fact]
    public void HistorySnapshot_MasksTheBorderNumber()
    {
        var json = EmployeeSafeSnapshot.Serialize(new Employee { FullName = "X", BorderNumber = "3012345678" });
        json.Should().NotContain("3012345678");
        EmployeeSafeSnapshot.SanitizeFieldValue("BorderNumber", "3012345678").Should().NotBe("3012345678");
    }
}
