using Zayra.Api.Application.CountryPack;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.CountryPack.Qatar;
using Zayra.Api.Infrastructure.CountryPack.Uae;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// S1 — statutory correctness of the end-of-service wage base and service period.
///
/// <para>These are LEGAL assertions, not consistency assertions. Every one of them fails against the
/// pre-S1 code, which computed KSA gratuity on basic salary alone (A1), ignored unpaid leave entirely
/// (A8), and returned a DIFC cash figure the employer must not pay (A9).</para>
/// </summary>
public class StatutoryEosbWageBaseTests
{
    private static readonly DateOnly Start2014 = new(2014, 1, 1);
    private static readonly DateOnly End2026   = new(2026, 1, 1);

    private static EndOfServiceInput Eosb(
        SalaryBreakdown salary, DateOnly start, DateOnly end,
        string reason = "Termination", decimal configured = 0m, int unpaidLeaveDays = 0) =>
        new(Guid.NewGuid(), Guid.NewGuid(), salary, start, end, reason, "Indefinite", "SAU")
        {
            ConfiguredEosbWage = configured,
            UnpaidLeaveDays    = unpaidLeaveDays,
        };

    // ── A1 — KSA Art. 84 is measured on the LAST WAGE, not on basic ──────────────────────────────

    [Fact]
    public async Task Ksa_Eosb_IsComputedOnLastWage_IncludingHousing_NotBasicAlone()
    {
        // The SME's worked example: a Saudi national, 12 years' service, SAR 30,000 total package on
        // the canonical 60/25/15 split — basic 18,000, housing 7,500, transport 4,500.
        //
        // Art. 84 M/51 awards on the LAST WAGE; Art. 2 defines wage as "the basic wage plus all other
        // due increments". The correct base is the full 30,000. The pre-S1 product awarded on 18,000 —
        // 60% of what it owed, on every Saudi leaver, for the life of the contract.
        var calc = new KsaEndOfServiceCalculator(new StubRuleReader());
        var input = Eosb(new SalaryBreakdown(18_000m, 7_500m, 4_500m, 0m), Start2014, End2026);

        var result = await calc.CalculateAsync(input);

        // 12 yrs: 5 × ½ month + 7 × 1 month = 9.5 months of the LAST WAGE.
        Assert.Equal(30_000m, result.AppliedWageBase);
        Assert.Equal(9.5m * 30_000m, result.TotalGratuity);
        // The pre-S1 answer, stated so the regression is unmistakable:
        Assert.NotEqual(9.5m * 18_000m, result.TotalGratuity);
    }

    [Fact]
    public async Task Ksa_Eosb_HousingFloor_CannotBeConfiguredAway()
    {
        // A1's real sting was that the base was "configurable" and the shipped default was wrong, under
        // a comment telling the implementer the default was right. Configuration must therefore only
        // ever be able to RAISE the base: basic + housing is statute and is not a setting.
        var calc = new KsaEndOfServiceCalculator(new StubRuleReader()
            .Set("eosb.include_transport", "false"));
        var input = Eosb(new SalaryBreakdown(18_000m, 7_500m, 4_500m, 0m), Start2014, End2026,
            configured: 18_000m);   // a catalog that flags BASIC only

        var result = await calc.CalculateAsync(input);

        Assert.Equal(25_500m, result.AppliedWageBase);   // basic + housing, despite the configuration
    }

    [Fact]
    public async Task Ksa_Eosb_ConfiguredCatalogCanRaiseTheBaseAboveStatute()
    {
        // A tenant whose catalog flags an extra EOSB component (a Fixed car allowance, say) gets the
        // higher base. Generosity is allowed; going under statute is not.
        var calc = new KsaEndOfServiceCalculator(new StubRuleReader());
        var input = Eosb(new SalaryBreakdown(18_000m, 7_500m, 0m, 0m), Start2014, End2026,
            configured: 32_000m);

        var result = await calc.CalculateAsync(input);

        Assert.Equal(32_000m, result.AppliedWageBase);
    }

    [Fact]
    public async Task Ksa_Eosb_TransportIsCounsel_IncludedByDefault_AndSaysSo()
    {
        var calc = new KsaEndOfServiceCalculator(new StubRuleReader());
        var input = Eosb(new SalaryBreakdown(10_000m, 4_000m, 2_000m, 0m), Start2014, End2026);

        var result = await calc.CalculateAsync(input);

        Assert.Equal(16_000m, result.AppliedWageBase);
        Assert.Contains(result.Notices, n => n.Contains("[COUNSEL-KSA]") && n.Contains("transport"));
    }

    [Fact]
    public async Task Ksa_Eosb_TransportExclusion_IsARuleNotALiteral()
    {
        var calc = new KsaEndOfServiceCalculator(new StubRuleReader()
            .Set("eosb.include_transport", "false"));
        var input = Eosb(new SalaryBreakdown(10_000m, 4_000m, 2_000m, 0m), Start2014, End2026);

        var result = await calc.CalculateAsync(input);

        Assert.Equal(14_000m, result.AppliedWageBase);
    }

    [Fact]
    public void ShippedPayComponentCatalog_FlagsHousingIntoTheEosbBase()
    {
        // The catalog comment used to assert housing "is not EOSB base". That was wrong as a statement
        // of Saudi law and it is what a tenant provisioning run copied into every new company.
        var housing = PayComponentCatalog.SystemComponentSeeds(Guid.NewGuid())
            .Single(c => c.Code == "HOUSING");

        Assert.True(housing.EosbIncluded);
    }

    // ── A1 — and the same fix must NOT leak into UAE or Qatar, which are basic-only ──────────────

    [Fact]
    public async Task Uae_Eosb_StaysBasicOnly_EvenWithHousingInThePackage()
    {
        // UAE Decree-Law 33/2021 Art. 51 is explicit that gratuity is on BASIC. A client who insists
        // otherwise is wrong, and the KSA fix must not quietly make them right.
        var calc = new UaeMainlandEndOfServiceCalculator(new StubRuleReader());
        var input = Eosb(new SalaryBreakdown(10_000m, 5_000m, 2_000m, 1_000m), new(2020, 1, 1), new(2023, 1, 1));

        var result = await calc.CalculateAsync(input);

        Assert.Equal(10_000m, result.AppliedWageBase);
    }

    [Fact]
    public async Task Qatar_Eosb_StaysBasicOnly_EvenWithHousingInThePackage()
    {
        var calc = new QatarEndOfServiceCalculator(new StubRuleReader());
        var input = Eosb(new SalaryBreakdown(10_000m, 5_000m, 0m, 0m), new(2020, 1, 1), new(2023, 1, 1));

        var result = await calc.CalculateAsync(input);

        Assert.Equal(10_000m, result.AppliedWageBase);
    }

    // ── A8 — unpaid leave and the service period ─────────────────────────────────────────────────

    [Fact]
    public async Task Uae_Eosb_ExcludesUnpaidLeaveFromTheServicePeriod()
    {
        // Decree-Law 33/2021 Art. 51 excludes unpaid leave from the service period EXPRESSLY. [CERT]
        var calc = new UaeMainlandEndOfServiceCalculator(new StubRuleReader());
        var start = new DateOnly(2020, 1, 1);
        var end   = new DateOnly(2024, 1, 1);

        var withLeave = await calc.CalculateAsync(
            Eosb(new SalaryBreakdown(12_000m, 0m, 0m, 0m), start, end, unpaidLeaveDays: 90));
        var without = await calc.CalculateAsync(
            Eosb(new SalaryBreakdown(12_000m, 0m, 0m, 0m), start, end));

        Assert.True(withLeave.TotalGratuity < without.TotalGratuity,
            "90 days of unpaid leave must shorten the UAE service period (Art. 51).");
        Assert.Contains(withLeave.Notices, n => n.Contains("[CERT-UAE]") && n.Contains("EXCLUDED"));
    }

    [Fact]
    public async Task Ksa_Eosb_KeepsUnpaidLeaveInService_ByDefault_ButSaysSo()
    {
        // [CONF] rather than [CERT]: KSA has no express exclusion, so the default must not silently
        // shorten anyone's service — but it must not be silent either.
        var calc = new KsaEndOfServiceCalculator(new StubRuleReader());
        var withLeave = await calc.CalculateAsync(
            Eosb(new SalaryBreakdown(10_000m, 0m, 0m, 0m), Start2014, End2026, unpaidLeaveDays: 90));
        var without = await calc.CalculateAsync(
            Eosb(new SalaryBreakdown(10_000m, 0m, 0m, 0m), Start2014, End2026));

        Assert.Equal(without.TotalGratuity, withLeave.TotalGratuity);
        Assert.Contains(withLeave.Notices, n => n.Contains("[CONF-KSA]") && n.Contains("INCLUDED"));
    }

    [Fact]
    public async Task Ksa_Eosb_ExcludesUnpaidLeave_WhenCounselHasRuled()
    {
        var calc = new KsaEndOfServiceCalculator(new StubRuleReader()
            .Set("eosb.exclude_unpaid_leave", "true"));
        var withLeave = await calc.CalculateAsync(
            Eosb(new SalaryBreakdown(10_000m, 0m, 0m, 0m), Start2014, End2026, unpaidLeaveDays: 365));
        var without = await calc.CalculateAsync(
            Eosb(new SalaryBreakdown(10_000m, 0m, 0m, 0m), Start2014, End2026));

        Assert.True(withLeave.TotalGratuity < without.TotalGratuity);
    }

    // ── A9 — DIFC/DEWS: the employer must not pay the post-2020 portion ──────────────────────────

    [Fact]
    public async Task Difc_Eosb_PaysOnlyTheGrandfatheredPreFeb2020Service()
    {
        // Hired 2015, leaving 2026. DEWS took effect 1 Feb 2020. The pre-S1 product applied DEWS rates
        // across all 11 years and returned the total as a payable lump sum — a figure the employer must
        // NOT pay, because the trustee pays the DEWS portion. Correct: old-law 21/30-day gratuity on
        // service to 31 Jan 2020, and nothing payable by the employer after that.
        var calc = new UaeDifcEndOfServiceCalculator(new StubRuleReader());
        var input = Eosb(new SalaryBreakdown(20_000m, 8_000m, 0m, 0m), new(2015, 1, 1), new(2026, 1, 1));

        var result = await calc.CalculateAsync(input);

        // ~5.087 yrs to the cut-over: 5 × 21 days + 0.087 × 30 days, on basic/30.
        var preYears = (new DateOnly(2020, 2, 1).DayNumber - new DateOnly(2015, 1, 1).DayNumber) / 365m;
        var daily = 20_000m / 30m;
        var expected = Math.Round(Math.Round(Math.Min(preYears, 5m) * 21m * daily, 2)
                                + Math.Round(Math.Max(0m, preYears - 5m) * 30m * daily, 2), 2);
        Assert.Equal(expected, result.TotalGratuity);
        Assert.Equal("DIFC-EmploymentLaw-DEWS-2020-cutover", result.ApplicableRule);
        Assert.Contains(result.Notices, n => n.Contains("DO NOT PAY the DEWS portion"));
        // The post-cut-over line is present and carried at ZERO, so it is visible but not payable.
        Assert.Contains(result.Breakdown, b => b.Label.Contains("PAID BY THE TRUSTEE") && b.Amount == 0m);
    }

    [Fact]
    public async Task Difc_Eosb_WhollyPostCutOverEmployee_IsNotPayableByTheEmployer()
    {
        var calc = new UaeDifcEndOfServiceCalculator(new StubRuleReader());
        var input = Eosb(new SalaryBreakdown(20_000m, 0m, 0m, 0m), new(2021, 6, 1), new(2026, 1, 1));

        var result = await calc.CalculateAsync(input);

        Assert.Equal(0m, result.TotalGratuity);
        Assert.Contains(result.Notices, n => n.Contains("DO NOT PAY"));
        Assert.Contains(result.Notices, n => n.Contains("[GAP-DIFC]") && n.Contains("MONTHLY DEWS remittance"));
    }

    // ── §4 — the Default pack's zero is the absence of an answer, and must say so ────────────────

    [Fact]
    public async Task DefaultPack_Eosb_ZeroCarriesANoPackNotice()
    {
        // A Kuwaiti tenant could run a settlement showing KWD 0.00 indemnity and act on it. The WPS
        // path already refuses; this makes the EOSB path capable of the same refusal.
        var calc = new DefaultEndOfServiceCalculator();
        var result = await calc.CalculateAsync(
            Eosb(new SalaryBreakdown(1_000m, 0m, 0m, 0m), Start2014, End2026));

        Assert.Equal(0m, result.TotalGratuity);
        Assert.Contains(result.Notices, n => n.Contains("[NO-PACK]"));
    }

    // ── Dead configuration: the EOSB rates and the minimum-service gate ─────────────────────────
    // The Saudi compliance-config screen has always let a customer edit three fields that nothing
    // read, next to an EosbEnabled toggle that worked — so the panel looked live.

    [Fact]
    public async Task Ksa_Eosb_ConfiguredRatesAboveTheFloor_AreHonouredAsAnEnhancement()
    {
        var calc = new KsaEndOfServiceCalculator(new StubRuleReader());
        var salary = new SalaryBreakdown(12_000m, 0m, 0m, 0m);
        var enhanced = await calc.CalculateAsync(
            Eosb(salary, new(2020, 1, 1), new(2023, 1, 1)) with
            { Policy = new EosbPolicyOverride(21m, 30m, 1) });
        var statutory = await calc.CalculateAsync(Eosb(salary, new(2020, 1, 1), new(2023, 1, 1)));

        // 3 yrs, all tier 1: statutory 15 days/yr vs configured 21 days/yr.
        Assert.Equal(Math.Round(statutory.TotalGratuity * 21m / 15m, 2), enhanced.TotalGratuity);
        Assert.Contains(enhanced.Notices, n => n.Contains("[POLICY-KSA]") && n.Contains("ENHANCEMENT"));
    }

    [Fact]
    public async Task Ksa_Eosb_ConfiguredRatesBelowTheFloor_AreRefusedAndNamed()
    {
        var calc = new KsaEndOfServiceCalculator(new StubRuleReader());
        var salary = new SalaryBreakdown(12_000m, 0m, 0m, 0m);
        var underCut = await calc.CalculateAsync(
            Eosb(salary, new(2020, 1, 1), new(2023, 1, 1)) with
            { Policy = new EosbPolicyOverride(7m, 10m, 1) });
        var statutory = await calc.CalculateAsync(Eosb(salary, new(2020, 1, 1), new(2023, 1, 1)));

        Assert.Equal(statutory.TotalGratuity, underCut.TotalGratuity);
        Assert.Contains(underCut.Notices, n => n.Contains("[CERT-KSA]") && n.Contains("BELOW the"));
    }

    [Fact]
    public async Task Ksa_Eosb_UnconfiguredRates_LeaveTheAwardExactlyWhereItWas()
    {
        // The rate wiring must be a no-op for every tenant that never touched the panel: a zero on
        // GCCComplianceSetting means "not configured", not "configured to zero".
        var calc = new KsaEndOfServiceCalculator(new StubRuleReader());
        var salary = new SalaryBreakdown(20_000m, 0m, 0m, 0m);
        var result = await calc.CalculateAsync(
            Eosb(salary, Start2014, End2026) with { Policy = new EosbPolicyOverride(0m, 0m, 1) });

        Assert.Equal(9.5m * 20_000m, result.TotalGratuity);   // 5 × ½ month + 7 × 1 month
    }

    [Fact]
    public async Task Ksa_Eosb_MinimumServiceGate_DoesNotWithholdAnArt84Award()
    {
        // Art. 84 grants the award from the FIRST DAY on an employer-initiated termination. A
        // configured 1-year minimum cannot lawfully withhold it, so the award stands and the
        // misconfiguration is named instead of being silently obeyed.
        var calc = new KsaEndOfServiceCalculator(new StubRuleReader());
        var result = await calc.CalculateAsync(
            Eosb(new SalaryBreakdown(12_000m, 0m, 0m, 0m), new(2025, 6, 1), new(2025, 12, 1)) with
            { Policy = new EosbPolicyOverride(0m, 0m, 1) });

        Assert.True(result.TotalGratuity > 0m, "Art. 84 has no minimum-service gate for a termination.");
        Assert.Contains(result.Notices, n => n.Contains("[CERT-KSA]") && n.Contains("minimum-service"));
    }

    [Fact]
    public async Task Ksa_MinimumServiceGate_IsNotConflatedWithTheArt85ResignationScale()
    {
        // PM/SME point: a resignation below the Art. 85 thresholds is a REDUCTION, not a service gate.
        // With NO minimum-service configured at all, an 18-month resignation must still forfeit under
        // Art. 85 — and that forfeiture must come from Art. 85, not from a borrowed gate.
        var calc = new KsaEndOfServiceCalculator(new StubRuleReader());
        var resigned = await calc.CalculateAsync(Eosb(
            new SalaryBreakdown(12_000m, 0m, 0m, 0m), new(2024, 1, 1), new(2025, 7, 1),
            reason: "Resignation") with { Policy = new EosbPolicyOverride(0m, 0m, 0) });
        var terminated = await calc.CalculateAsync(Eosb(
            new SalaryBreakdown(12_000m, 0m, 0m, 0m), new(2024, 1, 1), new(2025, 7, 1),
            reason: "Termination") with { Policy = new EosbPolicyOverride(0m, 0m, 0) });

        Assert.Equal(0m, resigned.TotalGratuity);       // Art. 85: under 2 years → nil
        Assert.True(terminated.TotalGratuity > 0m);     // Art. 84: same tenure, employer-initiated → paid
    }

    [Fact]
    public async Task Uae_Eosb_ConfiguredMinimumAboveOneYear_CannotDenyTheArt51Entitlement()
    {
        var calc = new UaeMainlandEndOfServiceCalculator(new StubRuleReader());
        var result = await calc.CalculateAsync(
            Eosb(new SalaryBreakdown(12_000m, 0m, 0m, 0m), new(2023, 1, 1), new(2024, 7, 1)) with
            { Policy = new EosbPolicyOverride(0m, 0m, 3) });

        Assert.True(result.TotalGratuity > 0m);
        Assert.Contains(result.Notices, n => n.Contains("[CERT-UAE]") && n.Contains("minimum-service"));
    }

    // ── B4 — the Art. 85 boundaries were already RIGHT. Pin them so a refactor cannot "simplify" them.

    [Theory]
    [InlineData(2.0, 1.0 / 3.0)]      // exactly 2 years → ⅓
    [InlineData(5.0, 1.0 / 3.0)]      // exactly 5 years → still ⅓ (inclusive upper bound)
    [InlineData(5.5, 2.0 / 3.0)]      // above 5, below 10 → ⅔
    [InlineData(10.0, 1.0)]           // 10 or more → full
    public async Task Ksa_Art85_ResignationScale_BoundariesAreExact(double years, double expectedFraction)
    {
        var calc = new KsaEndOfServiceCalculator(new StubRuleReader());
        var start = new DateOnly(2010, 1, 1);
        var end   = start.AddDays((int)Math.Round(years * 365.2425));
        var salary = new SalaryBreakdown(10_000m, 0m, 0m, 0m);

        var resigned = await calc.CalculateAsync(Eosb(salary, start, end, reason: "Resignation"));
        var terminated = await calc.CalculateAsync(Eosb(salary, start, end, reason: "Termination"));

        var actual = terminated.TotalGratuity == 0m ? 0m : resigned.TotalGratuity / terminated.TotalGratuity;
        Assert.Equal((decimal)expectedFraction, actual, 2);
    }

    [Fact]
    public async Task Ksa_Art85_UnderTwoYearsResignation_ForfeitsEntirely()
    {
        var calc = new KsaEndOfServiceCalculator(new StubRuleReader());
        var result = await calc.CalculateAsync(Eosb(
            new SalaryBreakdown(10_000m, 0m, 0m, 0m), new(2024, 1, 1), new(2025, 6, 1), reason: "Resignation"));

        Assert.Equal(0m, result.TotalGratuity);
    }

    [Fact]
    public async Task Ksa_Art80_DismissalForCause_ForfeitsEntirely()
    {
        var calc = new KsaEndOfServiceCalculator(new StubRuleReader());
        var result = await calc.CalculateAsync(Eosb(
            new SalaryBreakdown(10_000m, 5_000m, 0m, 0m), Start2014, End2026, reason: "Article80"));

        Assert.Equal(0m, result.TotalGratuity);
    }
}
