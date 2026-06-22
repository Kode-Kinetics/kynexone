using System.Text;
using System.Xml;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;

namespace Zayra.Api.Tests;

// Unit tests for the KSA country pack.
// A StubRuleReader returns the same directional defaults as the seeder
// so tests exercise the actual rate-based calculation path.

public class KsaPackTests
{
    private static readonly StubRuleReader StubRules = new StubRuleReader()
        .Set("gosi.saudi_employee_rate", 0.09m)
        .Set("gosi.saudi_employer_rate", 0.09m)
        .Set("gosi.saned_rate", 0.0075m)
        .Set("gosi.expat_occupational_hazard_rate", 0.02m)
        .Set("gosi.covered_wage_ceiling_sar", 45_000m)
        .Set("nitaqat.default_target_ratio", 0.35m);

    // ── GOSI — Saudi national ─────────────────────────────────────────────────

    [Fact]
    public async Task Gosi_SaudiNational_CalculatesAnnuitiesAndSanedAndOh()
    {
        // Basic SAR 8,000 + Housing SAR 3,000 → covered wage SAR 11,000
        // Annuities EE: 11,000 × 9%    = SAR 990
        // Annuities ER: 11,000 × 9%    = SAR 990
        // SANED EE:     11,000 × 0.75% = SAR 82.50
        // SANED ER:     11,000 × 0.75% = SAR 82.50
        // OH ER:        11,000 × 2%    = SAR 220   (employer pays OH for all employees)
        // EE total: 990 + 82.50 = 1,072.50 (9.75%)
        // ER total: 990 + 82.50 + 220 = 1,292.50 (11.75%)
        var calc = new KsaDeductionCalculator(StubRules);
        var input = new StatutoryDeductionInput(
            Guid.NewGuid(), Guid.NewGuid(),
            new SalaryBreakdown(8_000m, 3_000m, 0m, 0m),
            "SAU", "Unlimited", 2026, 1);

        var result = await calc.CalculateAsync(input);

        Assert.Equal(990m + 82.50m, result.TotalEmployeeDeduction);          // 9.75%
        Assert.Equal(990m + 82.50m + 220m, result.TotalEmployerContribution); // 11.75%
        Assert.Equal(5, result.Lines.Count);
        Assert.Contains(result.Lines, l => l.Code == "GOSI-ANN-EE" && l.EmployeeAmount == 990m);
        Assert.Contains(result.Lines, l => l.Code == "GOSI-ANN-ER" && l.EmployerAmount == 990m);
        Assert.Contains(result.Lines, l => l.Code == "GOSI-SANED-EE" && l.EmployeeAmount == 82.50m);
        Assert.Contains(result.Lines, l => l.Code == "GOSI-SANED-ER" && l.EmployerAmount == 82.50m);
        Assert.Contains(result.Lines, l => l.Code == "GOSI-OH-ER" && l.EmployerAmount == 220m);
    }

    [Fact]
    public async Task Gosi_SaudiNational_CoveredWageCappedAtCeiling()
    {
        // Covered wage = basic 40,000 + housing 10,000 = 50,000 > 45,000 ceiling
        var calc = new KsaDeductionCalculator(StubRules);
        var input = new StatutoryDeductionInput(
            Guid.NewGuid(), Guid.NewGuid(),
            new SalaryBreakdown(40_000m, 10_000m, 0m, 0m),
            "SAU", "Unlimited", 2026, 1);

        var result = await calc.CalculateAsync(input);

        // Employee annuity should be on 45,000 ceiling × 9% = 4,050
        decimal annuityLine = result.Lines.First(l => l.Code == "GOSI-ANN-EE").EmployeeAmount;
        Assert.Equal(45_000m * 0.09m, annuityLine);
    }

    // ── GOSI — Non-Saudi (expat) ──────────────────────────────────────────────

    [Fact]
    public async Task Gosi_NonSaudi_EmployerOnlyOccupationalHazard()
    {
        // Expat: only OH 2% employer on covered wage
        // Basic 10,000 + Housing 4,000 = 14,000 covered wage → 14,000 × 2% = SAR 280
        var calc = new KsaDeductionCalculator(StubRules);
        var input = new StatutoryDeductionInput(
            Guid.NewGuid(), Guid.NewGuid(),
            new SalaryBreakdown(10_000m, 4_000m, 0m, 0m),
            "EGY", "Unlimited", 2026, 1);

        var result = await calc.CalculateAsync(input);

        Assert.Equal(0m, result.TotalEmployeeDeduction);
        Assert.Equal(280m, result.TotalEmployerContribution);
        Assert.Single(result.Lines);
        Assert.Equal("GOSI-OH-ER", result.Lines[0].Code);
    }

    // ── EOSB — Termination (no discount) ────────────────────────────────────

    [Fact]
    public async Task Eosb_Termination_ThreeYears_TierOne()
    {
        // 3 years exactly, terminated (no resignation discount)
        // Tier 1: 3 × 0.5 × 8,000 = SAR 12,000
        var calc = new KsaEndOfServiceCalculator(StubRules);
        var input = new EndOfServiceInput(
            Guid.NewGuid(), Guid.NewGuid(),
            new SalaryBreakdown(8_000m, 0m, 0m, 0m),
            new DateOnly(2020, 1, 1), new DateOnly(2023, 1, 1),
            "Termination", "Unlimited", "SAU");

        var result = await calc.CalculateAsync(input);

        Assert.Equal("KSA-LaborLaw-Art84", result.ApplicableRule);
        Assert.Equal(12_000m, result.TotalGratuity);
    }

    [Fact]
    public async Task Eosb_Termination_SevenAndHalfYears_TwoTiers()
    {
        // 7.5 years, terminated
        // Tier 1: 5 × 0.5 × 10,000 = 25,000
        // Tier 2: 2.5 × 1.0 × 10,000 = 25,000
        // Total: SAR 50,000
        var calc = new KsaEndOfServiceCalculator(StubRules);
        var input = new EndOfServiceInput(
            Guid.NewGuid(), Guid.NewGuid(),
            new SalaryBreakdown(10_000m, 0m, 0m, 0m),
            new DateOnly(2018, 1, 1), new DateOnly(2025, 7, 1),
            "Termination", "Unlimited", "SAU");

        var result = await calc.CalculateAsync(input);

        Assert.True(result.TotalGratuity > 48_000m && result.TotalGratuity <= 50_000m,
            $"Expected ~50,000 for 7.5 yr termination, got {result.TotalGratuity}");
    }

    // ── EOSB — Resignation discount ─────────────────────────────────────────

    [Fact]
    public async Task Eosb_Resignation_UnderTwoYears_ReturnsZero()
    {
        var calc = new KsaEndOfServiceCalculator(StubRules);
        var input = new EndOfServiceInput(
            Guid.NewGuid(), Guid.NewGuid(),
            new SalaryBreakdown(10_000m, 0m, 0m, 0m),
            new DateOnly(2024, 1, 1), new DateOnly(2025, 6, 1),
            "Resignation", "Unlimited", "SAU");

        var result = await calc.CalculateAsync(input);
        Assert.Equal(0m, result.TotalGratuity);
    }

    [Fact]
    public async Task Eosb_Resignation_TwoToFiveYears_OneThirdDiscount()
    {
        // 3 years, resignation → ⅓ of entitlement
        // Full entitlement = 3 × 0.5 × 8,000 = 12,000
        // After ⅓: 4,000
        var calc = new KsaEndOfServiceCalculator(StubRules);
        var input = new EndOfServiceInput(
            Guid.NewGuid(), Guid.NewGuid(),
            new SalaryBreakdown(8_000m, 0m, 0m, 0m),
            new DateOnly(2020, 1, 1), new DateOnly(2023, 1, 1),
            "Resignation", "Unlimited", "SAU");

        var result = await calc.CalculateAsync(input);
        Assert.Equal(4_000m, result.TotalGratuity);
    }

    [Fact]
    public async Task Eosb_Resignation_FiveToTenYears_TwoThirdsDiscount()
    {
        // 7.5 years, resignation → ⅔ of entitlement
        // Full = ~50,000 → ⅔ ≈ 33,333.33
        var calc = new KsaEndOfServiceCalculator(StubRules);
        var input = new EndOfServiceInput(
            Guid.NewGuid(), Guid.NewGuid(),
            new SalaryBreakdown(10_000m, 0m, 0m, 0m),
            new DateOnly(2018, 1, 1), new DateOnly(2025, 7, 1),
            "Resignation", "Unlimited", "SAU");

        var result = await calc.CalculateAsync(input);
        Assert.True(result.TotalGratuity >= 33_000m && result.TotalGratuity <= 34_000m,
            $"Expected ~33,333 for 7.5 yr resignation ⅔ discount, got {result.TotalGratuity}");
    }

    // ── Nitaqat nationalization ───────────────────────────────────────────────

    [Fact]
    public async Task Nitaqat_AboveTarget_ReturnsCompliant()
    {
        var tracker = new KsaNationalizationTracker(StubRules);
        var input = new NationalizationInput(Guid.NewGuid(), Guid.NewGuid(), 100, 40);
        var result = await tracker.GetStatusAsync(input);

        Assert.Equal(NationalizationComplianceStatus.Compliant, result.Status);
        Assert.Equal("Nitaqat", result.SchemeLabel);
        Assert.Equal(0.40d, result.CurrentRatio, precision: 2);
    }

    [Fact]
    public async Task Nitaqat_BelowTarget_ReturnsNonCompliant()
    {
        var tracker = new KsaNationalizationTracker(StubRules);
        var input = new NationalizationInput(Guid.NewGuid(), Guid.NewGuid(), 100, 10);
        var result = await tracker.GetStatusAsync(input);

        Assert.Equal(NationalizationComplianceStatus.NonCompliant, result.Status);
    }

    // ── WPS Mudad XML golden-file test ────────────────────────────────────────

    [Fact]
    public async Task KsaWps_MudadXml_StructureIsValid()
    {
        var exporter = new KsaWageProtectionExporter();
        var employee = new WpsEmployee(
            1, "E001", "Ahmed Al-Rashid", "أحمد الراشد",
            "SAU", "1234567890", "SA0380000000608010167519", "RIBL",
            new SalaryBreakdown(10_000m, 4_000m, 1_000m, 0m), 14_910m);

        var input = new WageProtectionExportInput(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 2026, 6,
            "MUD-001", "SA0380000000608010167100",
            "Acme Corp", "شركة أكمي", new[] { employee });

        var result = await exporter.ExportAsync(input);

        Assert.Equal("mudad-xml", result.Format);
        Assert.Equal(1, result.RecordCount);
        Assert.True(result.FileBytes.Length > 0);
        Assert.EndsWith(".xml", result.FileName);

        // Parse and assert XML structure
        var xml = Encoding.UTF8.GetString(result.FileBytes);
        var doc = new XmlDocument();
        doc.LoadXml(xml);

        Assert.NotNull(doc.DocumentElement);
        Assert.Equal("MudadWPS", doc.DocumentElement!.Name);
        Assert.Equal("MUD-001",  doc.SelectSingleNode("//Header/EmployerID")!.InnerText);
        Assert.Equal("2026-06",  doc.SelectSingleNode("//Header/Period")!.InnerText);
        Assert.Equal("1",        doc.SelectSingleNode("//Header/RecordCount")!.InnerText);
        Assert.NotNull(doc.SelectSingleNode("//Employees/Employee"));
        Assert.Equal("E001",     doc.SelectSingleNode("//Employees/Employee/EmpCode")!.InnerText);
        Assert.Equal("10000.00", doc.SelectSingleNode("//Employees/Employee/BasicSalary")!.InnerText);
        Assert.Equal("14910.00", doc.SelectSingleNode("//Employees/Employee/NetPay")!.InnerText);
    }

    // ── Localization ──────────────────────────────────────────────────────────

    [Fact]
    public void KsaLocalization_ReturnsSaudiProfile()
    {
        var profile = new KsaLocalizationProfile().GetProfile();
        Assert.Equal("SAR", profile.CurrencyCode);
        Assert.True(profile.IsRtl);
        Assert.Equal("ar-SA", profile.LocaleCode);
        Assert.Equal("UmAlQura", profile.CalendarSystem);
    }
}
