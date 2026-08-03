using System.Text;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Qatar;

namespace Zayra.Api.Tests;

public class QatarPackTests
{
    private static readonly StubRuleReader StubRules = new StubRuleReader()
        .Set("grsia.national_employee_rate", 0.07m)
        .Set("grsia.national_employer_rate", 0.14m)
        .Set("qatarization.target_ratio",    0.20m);

    // ── GRSIA — Qatari national ───────────────────────────────────────────────

    [Fact]
    public async Task Grsia_QatariNational_CalculatesContributions()
    {
        // Basic QAR 10,000 — GRSIA base = basic only
        // Employee 7%: QAR 700
        // Employer 14%: QAR 1,400
        var calc = new QatarDeductionCalculator(StubRules);
        var input = new StatutoryDeductionInput(
            Guid.NewGuid(), Guid.NewGuid(),
            new SalaryBreakdown(10_000m, 2_000m, 1_000m, 0m),
            "QAT", "Unlimited", 2026, 3);

        var result = await calc.CalculateAsync(input);

        Assert.Equal(700m, result.TotalEmployeeDeduction);
        Assert.Equal(1_400m, result.TotalEmployerContribution);
        Assert.Contains(result.Lines, l => l.Code == "GRSIA-EE" && l.EmployeeAmount == 700m);
        Assert.Contains(result.Lines, l => l.Code == "GRSIA-ER" && l.EmployerAmount == 1_400m);
    }

    [Fact]
    public async Task Grsia_Expatriate_ReturnsZero()
    {
        var calc = new QatarDeductionCalculator(StubRules);
        var input = new StatutoryDeductionInput(
            Guid.NewGuid(), Guid.NewGuid(),
            new SalaryBreakdown(12_000m, 3_000m, 0m, 0m),
            "IND", "Unlimited", 2026, 3);

        var result = await calc.CalculateAsync(input);

        Assert.Equal(0m, result.TotalEmployeeDeduction);
        Assert.Equal(0m, result.TotalEmployerContribution);
        Assert.Empty(result.Lines);
    }

    // ── Qatar EOSB — 3 weeks per year ────────────────────────────────────────

    [Fact]
    public async Task QatarEos_ThreeYears_MinimumThreeWeeksPerYear()
    {
        // 3 years, basic QAR 5,000
        // Daily rate = 5,000 / 30 = QAR 166.67
        // Annual: 21 days × 166.67 = QAR 3,500
        // 3 years: QAR 10,500
        var calc = new QatarEndOfServiceCalculator(StubRules);
        var input = new EndOfServiceInput(
            Guid.NewGuid(), Guid.NewGuid(),
            new SalaryBreakdown(5_000m, 0m, 0m, 0m),
            new DateOnly(2020, 1, 1), new DateOnly(2023, 1, 1),
            "Termination", "Unlimited", "QAT");

        var result = await calc.CalculateAsync(input);

        Assert.Equal("Qatar-LaborLaw-14-2004-Art54", result.ApplicableRule);
        // 3 calendar years span a leap day → 1096 days / 365 × 21 × (5000/30) ≈ 10,509
        Assert.True(result.TotalGratuity >= 10_490m && result.TotalGratuity <= 10_520m,
            $"Expected ~10,500 for 3yr Qatar EOS, got {result.TotalGratuity}");
    }

    [Fact]
    public async Task QatarEos_UnderOneYear_ReturnsZero()
    {
        // 6 months (0.5 years), basic QAR 6,000.
        // Qatar Law 14/2004 Art.54 requires at least ONE completed year of service, so a
        // sub-1yr separation earns NO gratuity. This eligibility floor lives in the pack
        // (single engine) now that the PayrollController minYears pre-gate has been removed.
        var calc = new QatarEndOfServiceCalculator(StubRules);
        var input = new EndOfServiceInput(
            Guid.NewGuid(), Guid.NewGuid(),
            new SalaryBreakdown(6_000m, 0m, 0m, 0m),
            new DateOnly(2024, 1, 1), new DateOnly(2024, 7, 4), // ~6 months (184 days)
            "Termination", "Unlimited", "QAT");

        var result = await calc.CalculateAsync(input);

        Assert.Equal(0m, result.TotalGratuity);
        Assert.Equal("Qatar-LaborLaw-14-2004-Art54", result.ApplicableRule);
    }

    // ── Qatarization nationalization ─────────────────────────────────────────

    [Fact]
    public async Task Qatarization_AboveTarget_ReturnsCompliant()
    {
        var tracker = new QatarNationalizationTracker(StubRules);
        var input = new NationalizationInput(Guid.NewGuid(), Guid.NewGuid(), 100, 25);
        var result = await tracker.GetStatusAsync(input);

        Assert.Equal(NationalizationComplianceStatus.Compliant, result.Status);
        Assert.Equal("Qatarization", result.SchemeLabel);
    }

    [Fact]
    public async Task Qatarization_BelowTarget_ReturnsNonCompliant()
    {
        var tracker = new QatarNationalizationTracker(StubRules);
        var input = new NationalizationInput(Guid.NewGuid(), Guid.NewGuid(), 100, 5);
        var result = await tracker.GetStatusAsync(input);

        Assert.Equal(NationalizationComplianceStatus.NonCompliant, result.Status);
    }

    [Fact]
    public async Task Qatarization_AtRiskBand_ReturnsAtRisk()
    {
        // 20% target; 75-100% of target = AtRisk. 17% is 85% of 20%.
        var tracker = new QatarNationalizationTracker(StubRules);
        var input = new NationalizationInput(Guid.NewGuid(), Guid.NewGuid(), 100, 17);
        var result = await tracker.GetStatusAsync(input);

        Assert.Equal(NationalizationComplianceStatus.AtRisk, result.Status);
    }

    // ── WPS QCB SIF golden-file test ─────────────────────────────────────────

    [Fact]
    public async Task QatarWps_QcbSif_StructureIsValid()
    {
        var exporter = new QatarWageProtectionExporter();
        var employee = new WpsEmployee(
            1, "E001", "Khalid Al-Thani", "خالد آل ثاني",
            "QAT", "29012345678901", "QA58DOHB00001234567890ABCDEFG", "QNBA",
            new SalaryBreakdown(8_000m, 3_000m, 1_000m, 0m), 11_300m);

        var input = new WageProtectionExportInput(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 2026, 6,
            "CR-12345", "QA04BRES000000000000003123488",
            "Doha Partners", "شركاء الدوحة", new[] { employee });

        var result = await exporter.ExportAsync(input);

        Assert.Equal("qcb-sif", result.Format);
        Assert.Equal(1, result.RecordCount);
        Assert.True(result.FileBytes.Length > 0);
        Assert.EndsWith(".sif", result.FileName);

        var lines = Encoding.UTF8.GetString(result.FileBytes).Split('\n');

        // Header
        Assert.StartsWith("H|QCBSIF|1.0|CR-12345|2026|06|QAR|1", lines[0]);
        // Employer
        Assert.StartsWith("E|Doha Partners|", lines[1]);
        Assert.Contains("CR-12345", lines[1]);
        // Data
        Assert.StartsWith("D|E001|29012345678901|QAT|", lines[2]);
        Assert.Contains("8000.00", lines[2]);
        Assert.Contains("11300.00", lines[2]);
        // Trailer
        Assert.StartsWith("T|1|", lines[^1]);
        Assert.Contains("11300.00", lines[^1]);
    }

    // ── Localization ──────────────────────────────────────────────────────────

    [Fact]
    public void QatarLocalization_ReturnsQarProfile()
    {
        var profile = new QatarLocalizationProfile().GetProfile();
        Assert.Equal("QAR", profile.CurrencyCode);
        Assert.True(profile.IsRtl);
        Assert.Equal("ar-QA", profile.LocaleCode);
    }
}
