using System.Text;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Uae;

namespace Zayra.Api.Tests;

public class UaePackTests
{
    private static readonly StubRuleReader StubRules = new StubRuleReader()
        .Set("gpssa.national_employee_rate", 0.05m)
        .Set("gpssa.national_employer_rate", 0.125m)
        .Set("emiratisation.target_ratio",   0.10m)
        .Set("dews.tier1_monthly_rate",      0.0583m)
        .Set("dews.tier2_monthly_rate",      0.0833m);

    // ── GPSSA — UAE national ──────────────────────────────────────────────────

    [Fact]
    public async Task Gpssa_UaeNational_CalculatesEmployeeAndEmployer()
    {
        // Basic AED 12,000 + Housing AED 5,000 → GPSSA base AED 17,000
        // Employee 5%: AED 850
        // Employer 12.5%: AED 2,125
        var calc = new UaeDeductionCalculator(StubRules);
        var input = new StatutoryDeductionInput(
            Guid.NewGuid(), Guid.NewGuid(),
            new SalaryBreakdown(12_000m, 5_000m, 0m, 0m),
            "ARE", "Unlimited", 2026, 1);

        var result = await calc.CalculateAsync(input);

        Assert.Equal(850m, result.TotalEmployeeDeduction);
        Assert.Equal(2_125m, result.TotalEmployerContribution);
        Assert.Contains(result.Lines, l => l.Code == "GPSSA-EE" && l.EmployeeAmount == 850m);
        Assert.Contains(result.Lines, l => l.Code == "GPSSA-ER" && l.EmployerAmount == 2_125m);
    }

    [Fact]
    public async Task Gpssa_Expatriate_ReturnsZero()
    {
        var calc = new UaeDeductionCalculator(StubRules);
        var input = new StatutoryDeductionInput(
            Guid.NewGuid(), Guid.NewGuid(),
            new SalaryBreakdown(15_000m, 5_000m, 0m, 0m),
            "IND", "Unlimited", 2026, 1);

        var result = await calc.CalculateAsync(input);

        Assert.Equal(0m, result.TotalEmployeeDeduction);
        Assert.Equal(0m, result.TotalEmployerContribution);
        Assert.Empty(result.Lines);
    }

    // ── UAE Mainland EOSB ────────────────────────────────────────────────────

    [Fact]
    public async Task UaeMainlandEos_SixYears_TwoTierCalc()
    {
        // 6 years service, basic AED 15,000
        // Daily rate = 15,000 / 30 = AED 500
        // Tier 1: 5 × 21 days = 105 days → 105 × 500 = AED 52,500
        // Tier 2: 1 × 30 days = 30  days → 30  × 500 = AED 15,000
        // Total: AED 67,500
        var calc = new UaeMainlandEndOfServiceCalculator(StubRules);
        var input = new EndOfServiceInput(
            Guid.NewGuid(), Guid.NewGuid(),
            new SalaryBreakdown(15_000m, 0m, 0m, 0m),
            new DateOnly(2018, 1, 1), new DateOnly(2024, 1, 1),
            "Termination", "Unlimited", "IND");

        var result = await calc.CalculateAsync(input);

        Assert.Equal("UAE-LaborLaw-Art51", result.ApplicableRule);
        // 2018-01-01 to 2024-01-01 spans leap year 2020 → 2191 days / 365 ≈ 6.0027 yrs;
        // tier2 = 1.0027 × 30 × 500 ≈ 15,041 → total ≈ 67,541
        Assert.True(result.TotalGratuity >= 67_400m && result.TotalGratuity <= 67_600m,
            $"Expected ~67,500 for 6yr UAE mainland EOS, got {result.TotalGratuity}");
    }

    [Fact]
    public async Task UaeMainlandEos_TwoYears_TierOneOnly()
    {
        // 2 years, basic AED 10,000
        // Daily rate = 333.33
        // 2 × 21 × 333.33 = 14,000
        var calc = new UaeMainlandEndOfServiceCalculator(StubRules);
        var input = new EndOfServiceInput(
            Guid.NewGuid(), Guid.NewGuid(),
            new SalaryBreakdown(10_000m, 0m, 0m, 0m),
            new DateOnly(2022, 1, 1), new DateOnly(2024, 1, 1),
            "Termination", "Unlimited", "ARE");

        var result = await calc.CalculateAsync(input);

        Assert.True(result.TotalGratuity > 13_900m && result.TotalGratuity < 14_100m,
            $"Expected ~14,000 for 2yr UAE mainland EOS, got {result.TotalGratuity}");
    }

    // ── UAE DIFC DEWS ────────────────────────────────────────────────────────

    [Fact]
    public async Task UaeDifcEos_WhollyPostCutOver_IsNotAnEmployerPayable()
    {
        // S1/A9 — REWRITTEN. This test previously asserted that DIFC returns the DEWS accrual
        // (36 × 20,000 × 5.83% = 41,976) as TotalGratuity, i.e. as a payable cash lump sum. That is
        // the defect, not the specification: since 1 February 2020 the DEWS trustee pays the employee
        // their vested account value and the employer pays nothing on termination for that service.
        // An employer who paid the old number paid roughly AED 42,000 twice.
        var calc = new UaeDifcEndOfServiceCalculator(StubRules);
        var input = new EndOfServiceInput(
            Guid.NewGuid(), Guid.NewGuid(),
            new SalaryBreakdown(20_000m, 0m, 0m, 0m),
            new DateOnly(2021, 1, 1), new DateOnly(2024, 1, 1),
            "Termination", "Unlimited", "GBR");

        var result = await calc.CalculateAsync(input);

        Assert.Equal("DIFC-EmploymentLaw-DEWS-2020-cutover", result.ApplicableRule);
        Assert.Equal(0m, result.TotalGratuity);
        Assert.Contains(result.Notices, n => n.Contains("DO NOT PAY the DEWS portion"));
        // The accrual is still REPORTED — at zero — so the approver can reconcile it against the
        // trustee's statement rather than being told nothing happened.
        Assert.Contains(result.Breakdown, b => b.Label.Contains("PAID BY THE TRUSTEE") && b.Amount == 0m);
    }

    [Fact]
    public async Task UaeDifcEos_StraddlingTheCutOver_PaysTheGrandfatheredPortionOnly()
    {
        // S1/A9 — REWRITTEN. Hired 2016, left 2023: 4.0877 years fall BEFORE the 1 Feb 2020 cut-over
        // and are an employer-paid lump sum under the pre-DEWS DIFC law (21 days basic per year for
        // the first five years); the remaining ~3 years are the trustee's.
        //   4.0877 yrs × 21 days × (15,000 / 30) = AED 42,920.55
        // The old assertion (52,470 + 29,988 = 82,458) applied DEWS rates across ALL seven years and
        // returned them as cash — both the wrong model and, for the pre-2020 service, the wrong scale.
        var calc = new UaeDifcEndOfServiceCalculator(StubRules);
        var input = new EndOfServiceInput(
            Guid.NewGuid(), Guid.NewGuid(),
            new SalaryBreakdown(15_000m, 0m, 0m, 0m),
            new DateOnly(2016, 1, 1), new DateOnly(2023, 1, 1),
            "Termination", "Unlimited", "USA");

        var result = await calc.CalculateAsync(input);

        Assert.Equal("DIFC-EmploymentLaw-DEWS-2020-cutover", result.ApplicableRule);
        Assert.Equal(42_920.55m, result.TotalGratuity);
        Assert.Equal(2, result.Breakdown.Count);   // grandfathered (payable) + DEWS (zero, trustee's)
        Assert.Contains(result.Notices, n => n.Contains("[CERT-DIFC]") && n.Contains("grandfathered"));
    }

    // ── Jurisdiction resolver: DIFC vs Mainland ─────────────────────────────
    // This is the key test proving the jurisdiction fallback works correctly.

    [Fact]
    public void Resolver_UaeDifc_ResolvesToDewsCalculator_NotMainlandCalculator()
    {
        // Build a minimal resolver using the KeyedService<T> pattern (no DI container)
        var mainlandCalc = new UaeMainlandEndOfServiceCalculator(StubRules);
        var difcCalc     = new UaeDifcEndOfServiceCalculator(StubRules);
        var defaultCalc  = new Zayra.Api.Infrastructure.CountryPack.DefaultEndOfServiceCalculator();

        var registry = new Dictionary<string, IEndOfServiceCalculator>
        {
            [$"{CountryCodes.UAE}"] = mainlandCalc,
            [$"{CountryCodes.UAE}:{Jurisdictions.Difc}"] = difcCalc,
        };

        IEndOfServiceCalculator Resolve(string cc, string j)
            => registry.TryGetValue($"{cc}:{j}", out var exact) ? exact
             : registry.TryGetValue(cc, out var wide)           ? wide
             : defaultCalc;

        // DIFC jurisdiction → DEWS calculator
        var forDifc     = Resolve(CountryCodes.UAE, Jurisdictions.Difc);
        Assert.IsType<UaeDifcEndOfServiceCalculator>(forDifc);

        // Mainland jurisdiction → standard UAE calculator
        var forMainland = Resolve(CountryCodes.UAE, Jurisdictions.UAEMainland);
        Assert.IsType<UaeMainlandEndOfServiceCalculator>(forMainland);

        // ADGM (no specific override) → falls to country-wide = mainland calc
        var forAdgm     = Resolve(CountryCodes.UAE, Jurisdictions.Adgm);
        Assert.IsType<UaeMainlandEndOfServiceCalculator>(forAdgm);
    }

    // ── Emiratisation nationalization ────────────────────────────────────────

    [Fact]
    public async Task Emiratisation_MeetsTarget_ReturnsCompliant()
    {
        var tracker = new UaeNationalizationTracker(StubRules);
        var input = new NationalizationInput(Guid.NewGuid(), Guid.NewGuid(), 100, 15);
        var result = await tracker.GetStatusAsync(input);

        Assert.Equal(NationalizationComplianceStatus.Compliant, result.Status);
        Assert.Equal("Emiratisation+Nafis", result.SchemeLabel);
    }

    [Fact]
    public async Task Emiratisation_ZeroHeadcount_ReturnsNotApplicable()
    {
        var tracker = new UaeNationalizationTracker(StubRules);
        var input = new NationalizationInput(Guid.NewGuid(), Guid.NewGuid(), 0, 0);
        var result = await tracker.GetStatusAsync(input);

        Assert.Equal(NationalizationComplianceStatus.NotApplicable, result.Status);
    }

    // ── WPS MOHRE SIF golden-file test ────────────────────────────────────────

    [Fact]
    public async Task UaeWps_MohreSif_StructureIsValid()
    {
        var exporter = new UaeWageProtectionExporter();
        var employee = new WpsEmployee(
            1, "E001", "Mohammed Al-Mansoori", "محمد المنصوري",
            "ARE", "784199012345678", "AE060000000000000000123", "ADCB",
            new SalaryBreakdown(15_000m, 5_000m, 1_500m, 0m), 20_575m);

        var input = new WageProtectionExportInput(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 2026, 6,
            "MOHRE-EST-001", "AE070331234567890123456",
            "Gulf Corp", "شركة الخليج", new[] { employee });

        var result = await exporter.ExportAsync(input);

        Assert.Equal("mohre-sif", result.Format);
        Assert.Equal(1, result.RecordCount);
        Assert.True(result.FileBytes.Length > 0);
        Assert.EndsWith(".sif", result.FileName);

        var lines = Encoding.UTF8.GetString(result.FileBytes).Split('\n');

        // Header line
        Assert.StartsWith("H|MOHRESIF|1.0|MOHRE-EST-001|2026|06|AED|1", lines[0]);
        // Employer line
        Assert.StartsWith("E|Gulf Corp|MOHRE-EST-001", lines[1]);
        // Data line
        Assert.StartsWith("D|E001|784199012345678|ARE|AE060000000000000000123", lines[2]);
        Assert.Contains("15000.00", lines[2]);
        Assert.Contains("20575.00", lines[2]);
        // Trailer
        Assert.StartsWith("T|1|", lines[^1]);
        Assert.Contains("20575.00", lines[^1]);
    }

    // ── Localization ──────────────────────────────────────────────────────────

    [Fact]
    public void UaeLocalization_ReturnsArabicAedProfile()
    {
        var profile = new UaeLocalizationProfile().GetProfile();
        Assert.Equal("AED", profile.CurrencyCode);
        Assert.True(profile.IsRtl);
        Assert.Equal("ar-AE", profile.LocaleCode);
        Assert.Equal("Gregorian", profile.CalendarSystem);
    }
}
