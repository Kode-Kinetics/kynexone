using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Employees.Onboarding;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Employees.Onboarding;
using Zayra.Api.Infrastructure.Modules;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;
using Xunit;

namespace Zayra.Api.Tests.Onboarding;

/// <summary>Consultant-audit backend fixes CD26-CD31 and CD36: contract end date, name-only advisory duplicates,
/// identity rows, draft-to-active re-validation, IBAN by country, eligibility defaults, probation limit, flag registration.</summary>
public class HireFunctionalHardeningTests
{
    private static readonly DateOnly Today = new(2026, 10, 26);
    private static readonly DateTime Joining = new(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc);

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static (Guid Tenant, Company Company) Seed(ZayraDbContext db, string country = "SA")
    {
        var tenant = Guid.NewGuid();
        var c = new Company { TenantId = tenant, LegalNameEn = "Main", TradeName = "Main", CountryCode = country, Jurisdiction = "t",
            RegistrationNumber = "RC-" + Guid.NewGuid().ToString("N"), DefaultCurrency = "SAR", IsActive = true, CreatedAtUtc = DateTime.UtcNow };
        db.Companies.Add(c); db.SaveChanges();
        return (tenant, c);
    }

    private static RequestContext Ctx(Guid tenant, params string[] perms) =>
        new("127.0.0.1", "test", Guid.NewGuid(), tenant, ["HR Manager"], perms);

    private static EmployeeCreateRequest Req(string contract = "Unlimited", DateTime? joining = null, DateOnly? contractEnd = null,
        DateOnly? probStart = null, DateOnly? probEnd = null, EmployeePayrollProfileRequest? payroll = null,
        IReadOnlyCollection<EmployeeComplianceRecordRequest>? records = null) =>
        EmployeeInputValidatorTests.Req(joining: joining ?? Joining, probStart: probStart, probEnd: probEnd, payroll: payroll, records: records)
            with { ContractType = contract, ContractEndDate = contractEnd };

    private static EmployeeInputValidationResult Run(EmployeeCreateRequest r, EmployeeGuardMode mode = EmployeeGuardMode.Create, string country = "SA") =>
        EmployeeInputValidator.Validate(r, new EmployeeInputValidationContext(country, Today, mode, TimeZoneInfo.Utc));

    // ── CD26(b): contract end date ──────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ContractEnd_AtOrBeforeStart_FixedTerm_IsHard(int offsetDays)
    {
        var r = Run(Req("Fixed-Term", contractEnd: DateOnly.FromDateTime(Joining).AddDays(offsetDays)));
        r.Errors["contractEndDate"].Should().Contain(EmployeeFieldErrors.ContractEndBeforeStart);
    }

    [Fact]
    public void ContractEnd_WithoutFixedTerm_IsHard()
    {
        Run(Req("Unlimited", contractEnd: Today.AddYears(1))).Errors["contractEndDate"]
            .Should().Contain(EmployeeFieldErrors.ContractEndWithoutFixedTerm);
    }

    [Fact]
    public void ContractEnd_ValidFixedTerm_NoError_AndNoEndIsFine()
    {
        Run(Req("Fixed-Term", contractEnd: Today.AddYears(1))).Errors.Should().NotContainKey("contractEndDate");
        Run(Req("Unlimited")).Errors.Should().NotContainKey("contractEndDate");
    }

    [Fact]
    public void ContractEnd_DraftAutosave_IsNotEnforced_Cd28()
    {
        Run(Req("Unlimited", contractEnd: Today.AddDays(-9)), EmployeeGuardMode.Draft).Errors.Should().NotContainKey("contractEndDate");
    }

    [Fact]
    public async Task Guard_Create_And_Update_RefuseBadContractEnd_Draft_Accepts()
    {
        await using var db = CreateDb(); var (t, c) = Seed(db);
        var bad = Req("Fixed-Term", contractEnd: Today.AddDays(-1)) with { CompanyId = c.Id };
        var ex = await Assert.ThrowsAsync<EmployeeValidationException>(() => new EmployeeInputGuard(db).EnforceCreateAsync(t, bad, Ctx(t), default));
        ex.Errors["contractEndDate"].Should().Contain("contract_end_before_start");
        await Assert.ThrowsAsync<EmployeeValidationException>(() => new EmployeeInputGuard(db).EnforceUpdateAsync(t, 1, bad with { CompanyId = c.Id }, Ctx(t), default));
        var kept = await new EmployeeInputGuard(db).EnforceDraftAsync(t, null, bad, Ctx(t), default);
        kept.ContractEndDate.Should().Be(Today.AddDays(-1), "autosave never fights typing");
    }

    // ── CD26(c): name-only advisory duplicate ───────────────────────────────────────────────────
    private static Employee Emp(Guid tenant, string english, string? arabic = null, DateOnly? dob = null, Guid? company = null) => new()
    {
        TenantId = tenant, CompanyId = company, EmployeeCode = "E-" + Guid.NewGuid().ToString("N")[..8], FullName = english, EnglishName = english,
        ArabicName = arabic ?? string.Empty, DateOfBirth = dob, Status = "Active", JoiningDate = DateTime.UtcNow.Date,
    };

    private static DuplicateProbe Probe(string? en, string? ar = null, DateOnly? dob = null) =>
        new(en, ar, dob, null, null, new Dictionary<string, string>(), null);

    [Fact]
    public async Task NameOnly_English_IsPossible_WithNameSignal_NormalisedForCaseAndSpacing()
    {
        await using var db = CreateDb(); var (t, _) = Seed(db);
        db.Employees.Add(Emp(t, "Khalid Al Otaibi")); await db.SaveChangesAsync();
        var hits = await new EmployeeDuplicateDetector(db).FindAsync(t, Probe("  khalid   AL otaibi "), default);
        var hit = hits.Should().ContainSingle().Subject;
        hit.MatchType.Should().Be("possible");
        hit.Signals.Should().Contain("name");
    }

    [Fact]
    public async Task NameOnly_Arabic_IsPossible()
    {
        await using var db = CreateDb(); var (t, _) = Seed(db);
        db.Employees.Add(Emp(t, "Someone Else", "خالد بن سعد العتيبي")); await db.SaveChangesAsync();
        var hits = await new EmployeeDuplicateDetector(db).FindAsync(t, Probe("Unrelated Name", "خالد بن سعد العتيبي"), default);
        hits.Should().ContainSingle(h => h.MatchType == "possible" && h.Signals.Contains("name"));
    }

    [Fact]
    public async Task NameOnly_SentinelDob_StaysPossible_NotProbable()
    {
        await using var db = CreateDb(); var (t, _) = Seed(db);
        var jan1 = new DateOnly(1990, 1, 1);
        db.Employees.Add(Emp(t, "Omar Farouk Hassan", dob: jan1)); await db.SaveChangesAsync();
        var hits = await new EmployeeDuplicateDetector(db).FindAsync(t, Probe("Omar Farouk Hassan", dob: jan1), default);
        hits.Should().ContainSingle().Which.MatchType.Should().Be("possible");
    }

    [Fact]
    public async Task NameAndRealDob_StaysProbable_AndIsNotReportedTwice()
    {
        await using var db = CreateDb(); var (t, _) = Seed(db);
        var dob = new DateOnly(1988, 5, 17);
        db.Employees.Add(Emp(t, "Omar Farouk Hassan", dob: dob)); await db.SaveChangesAsync();
        var hits = await new EmployeeDuplicateDetector(db).FindAsync(t, Probe("Omar Farouk Hassan", dob: dob), default);
        hits.Should().ContainSingle().Which.MatchType.Should().Be("probable");
    }

    [Fact]
    public async Task NameOnly_SingleWord_OtherTenant_AndExcludedSelf_DoNotMatch()
    {
        await using var db = CreateDb(); var (t, _) = Seed(db);
        var other = Guid.NewGuid();
        var self = Emp(t, "Layla Mansour"); var elsewhere = Emp(other, "Layla Mansour"); var single = Emp(t, "Layla");
        db.Employees.AddRange(self, elsewhere, single); await db.SaveChangesAsync();
        var det = new EmployeeDuplicateDetector(db);
        (await det.FindAsync(t, Probe("Layla"), default)).Should().BeEmpty("one word is not a name match");
        (await det.FindAsync(t, Probe("Layla Mansour") with { ExcludeEmployeeId = self.Id }, default)).Should().BeEmpty();
        (await det.FindAsync(t, Probe("Layla Mansour"), default)).Should().ContainSingle(h => h.EmployeeId == self.Id);
    }

    // ── CD27: Border Number + passport as a second identity row ─────────────────────────────────
    [Fact]
    public void FieldCatalog_ExposesBorderNumberIqamaAndPassport_ForExpats_NotForSaudis()
    {
        var expat = EmployeeFieldRegistry.CatalogFor("SA", "Indian").Select(d => d.ComplianceFieldKey).ToList();
        expat.Should().Contain(["border_number", "iqama_number", "passport_number"]);
        var saudi = EmployeeFieldRegistry.CatalogFor("SA", "Saudi").Select(d => d.ComplianceFieldKey).ToList();
        saudi.Should().NotContain(["border_number", "iqama_number"]);
    }

    [Fact]
    public async Task Guard_AcceptsIqamaAndPassportRows_Together_AndBorderNumberAlone()
    {
        await using var db = CreateDb(); var (t, c) = Seed(db);
        var both = new[]
        {
            new EmployeeComplianceRecordRequest("SA", "iqama_number", "Iqama", "2000000004", null, Today.AddYears(1), true, false),
            new EmployeeComplianceRecordRequest("SA", "passport_number", "Passport", "N1234567", null, Today.AddYears(3), true, false),
        };
        var req = (Req(records: both) with { CompanyId = c.Id, Nationality = "Indian", NationalityCode = "IN" });
        var o = await new EmployeeInputGuard(db).EnforceCreateAsync(t, req, Ctx(t), default);
        o.ComplianceRecords!.Select(r => r.FieldKey).Should().BeEquivalentTo("iqama_number", "passport_number");
        o.ComplianceRecords!.Single(r => r.FieldKey == "passport_number").ExpiryDate.Should().Be(Today.AddYears(3));

        var border = new[] { new EmployeeComplianceRecordRequest("SA", "border_number", "Border", "3000000007", null, null, true, false) };
        var o2 = await new EmployeeInputGuard(db).EnforceCreateAsync(t, Req(records: border) with { CompanyId = c.Id, Nationality = "Indian", NationalityCode = "IN" }, Ctx(t), default);
        o2.ComplianceRecords!.Single().FieldValue.Should().Be("3000000007");
    }

    // ── CD28: invalid draft values block activation ─────────────────────────────────────────────
    private static async Task<(Guid T, Company C, Employee E)> SeedDraftWithIban(ZayraDbContext db, string iban)
    {
        var (t, c) = Seed(db);
        var e = Emp(t, "Draft Person", company: c.Id); e.Status = "Draft"; e.CountryCode = "SA"; e.Nationality = "Saudi";
        db.Employees.Add(e); await db.SaveChangesAsync();
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = t, EmployeeId = e.Id, Iban = iban }); await db.SaveChangesAsync();
        return (t, c, e);
    }

    [Fact]
    public async Task Activation_InvalidIbanSavedInDraft_IsRefused_WithTheFieldCode()
    {
        await using var db = CreateDb(); var (t, _, e) = await SeedDraftWithIban(db, "SA0000000000000000000001");
        var ex = await Assert.ThrowsAsync<EmployeeValidationException>(() => new EmployeeInputGuard(db).EnforceActivationAsync(t, e.Id, Ctx(t), default));
        ex.Status.Should().Be(400);
        ex.Errors["payrollProfile.iban"].Should().Contain(EmployeeFieldErrors.IbanInvalid);
    }

    [Fact]
    public async Task Activation_ValidStoredData_Passes()
    {
        await using var db = CreateDb(); var (t, _, e) = await SeedDraftWithIban(db, IbanValidator.WithValidCheckDigits("SA0380000000608010167519"));
        await new EmployeeInputGuard(db).EnforceActivationAsync(t, e.Id, Ctx(t), default);
    }

    [Fact]
    public async Task Readiness_InvalidStoredIban_BecomesABlocker()
    {
        await using var db = CreateDb(); var (t, _, e) = await SeedDraftWithIban(db, "SA0000000000000000000001");
        var tracked = await db.Employees.SingleAsync(x => x.Id == e.Id);
        await new EmployeeActivationGuard(db).StampReadinessAsync(tracked, default);
        tracked.ReadinessState.Should().Be("Blocked");
        tracked.ActivationBlockersCount.Should().BeGreaterThan(0);
        var detail = await new EmployeeActivationGuard(db).EvaluateEmployeeAsync(t, e.Id, default);
        detail!.Value.Readiness.Blocking.Should().Contain(i => i.Key == "invalid:payrollProfile.iban");
    }

    // ── CD29: IBAN by company country, SA bank names ────────────────────────────────────────────
    [Fact]
    public void Iban_NonSaCompany_UsesGenericRule_WithCountryLength()
    {
        var ae = IbanValidator.WithValidCheckDigits("AE000330000019100000001");           // 23 chars, AE length
        IbanValidator.IsValid(ae).Should().BeTrue();
        var aeShort = IbanValidator.WithValidCheckDigits("AE0003300000191000001");        // mod-97 valid, 21 chars: wrong for AE
        IbanValidator.IsValid(aeShort).Should().BeFalse("UAE IBANs are exactly 23 characters");
        Run(Req(payroll: new("B", ae, null, null, null, null, null, null, null, null, null, null)), country: "AE").Errors.Should().NotContainKey("payrollProfile.iban");
        Run(Req(payroll: new("B", ae, null, null, null, null, null, null, null, null, null, null)), country: "SA").Errors["payrollProfile.iban"]
            .Should().Contain(EmployeeFieldErrors.IbanInvalid, "a Saudi company needs a Saudi IBAN");
    }

    [Fact]
    public void SaudiBanks_ResolveKnownCodes_AndNeverGuessUnknownOnes()
    {
        SaudiBanks.NameEn("80").Should().Be("Al Rajhi Bank");
        SaudiBanks.NameEn("10").Should().Contain("National Bank");
        SaudiBanks.NameEn("99").Should().BeNull();
        SaudiBanks.FromIban("SA0380000000608010167519")!.Code.Should().Be("80");
        SaudiBanks.FromIban("AE000330000019100000001").Should().BeNull();
        SaudiBanks.All.Should().HaveCount(9).And.OnlyContain(b => b.Code.Length == 2 && b.NameEn.Length > 0 && b.NameAr.Length > 0);
    }

    // ── CD30: wps/eosb defaults from the company country, not from typing ───────────────────────
    [Theory]
    [InlineData("SA", true)]
    [InlineData("US", false)]
    public async Task Eligibility_DefaultsFromCompanyCountry_WhenNoPayrollProfileTyped(string country, bool expected)
    {
        await using var db = CreateDb(); var (t, c) = Seed(db, country);
        var o = await new EmployeeInputGuard(db).EnforceCreateAsync(t, Req() with { CompanyId = c.Id, Nationality = "Saudi" }, Ctx(t), default);
        o.PayrollProfile.Should().NotBeNull();
        o.PayrollProfile!.WpsEligible.Should().Be(expected);
        o.PayrollProfile.EosbEligible.Should().Be(expected);
    }

    [Fact]
    public async Task Eligibility_TypedIbanOrMolId_DoesNotChangeTheDefault_AndExplicitFalseIsKept()
    {
        await using var db = CreateDb(); var (t, c) = Seed(db);
        var typed = new EmployeePayrollProfileRequest(null, null, null, null, null, null, null, null, null, null, "MOL-1", null);
        var o = await new EmployeeInputGuard(db).EnforceCreateAsync(t, Req(payroll: typed) with { CompanyId = c.Id }, Ctx(t), default);
        o.PayrollProfile!.WpsEligible.Should().BeTrue();
        var off = typed with { WpsEligible = false, EosbEligible = false };
        var o2 = await new EmployeeInputGuard(db).EnforceCreateAsync(t, Req(payroll: off) with { CompanyId = c.Id }, Ctx(t), default);
        o2.PayrollProfile!.WpsEligible.Should().BeFalse();
        o2.PayrollProfile.EosbEligible.Should().BeFalse();
    }

    // ── CD31: probation over the limit ──────────────────────────────────────────────────────────
    [Fact]
    public void Probation_Over180Days_EmitsProbationOverLimit_AnchoredOnStartOrJoining()
    {
        var start = DateOnly.FromDateTime(Joining);
        var withStart = Run(Req(probStart: start, probEnd: start.AddDays(181)));
        withStart.Warnings.Should().ContainSingle(w => w.Code == EmployeeWarningCodes.ProbationOverLimit && w.Field == "probationEndDate");
        var joiningOnly = Run(Req(probEnd: start.AddDays(200)));
        joiningOnly.Warnings.Select(w => w.Code).Should().Contain(EmployeeWarningCodes.ProbationOverLimit);
        Run(Req(probStart: start, probEnd: start.AddDays(180))).Warnings.Select(w => w.Code).Should().NotContain(EmployeeWarningCodes.ProbationOverLimit);
    }

    [Fact]
    public void WageFloorConstant_IsRemoved_NotDeadCode()
    {
        typeof(EmployeeWarningCodes).GetField("SalaryBelowWageFloor").Should().BeNull();
    }

    // ── CD36: hire_wizard_v2 is registered and console-switchable ───────────────────────────────
    [Fact]
    public void HireWizardV2_IsInTheCatalogs()
    {
        PlatformController.FeatureFlagCatalog.Should().Contain(f => f.Key == FeatureKeys.HireWizardV2 && f.Category == "Rollout (opt-in)" && !f.DefaultEnabled);
        ModuleCatalog.NonModuleKeys.Should().ContainKey(FeatureKeys.HireWizardV2);
    }
}
