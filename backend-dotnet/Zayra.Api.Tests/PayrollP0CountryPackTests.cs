using Zayra.Api.Application.CountryPack;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.CountryPack.Uae;
using Zayra.Api.Infrastructure.CountryPack.Qatar;
using Zayra.Api.Infrastructure.Payroll;

namespace Zayra.Api.Tests;

// Correctness tests for the Payroll P0 country-pack wiring.
// These are math-level assertions — they verify that the new pack-based
// calculation path produces the correct statutory amounts for each jurisdiction.
// Reference: GosiCalculationService is the prior implementation and is kept
// for parity comparison on the KSA/Saudi case.

public class PayrollP0CountryPackTests
{
    private static readonly StubRuleReader KsaDefaultRules = new StubRuleReader()
        .Set("gosi.saudi_employee_rate",            0.09m)
        .Set("gosi.saudi_employer_rate",            0.09m)
        .Set("gosi.saned_rate",                     0.0075m)
        .Set("gosi.expat_occupational_hazard_rate", 0.02m)
        .Set("gosi.covered_wage_ceiling_sar",       45_000m);

    private static readonly StubRuleReader UaeDefaultRules = new StubRuleReader()
        .Set("gpssa.national_employee_rate", 0.05m)
        .Set("gpssa.national_employer_rate", 0.125m);

    private static readonly StubRuleReader QatarDefaultRules = new StubRuleReader()
        .Set("grsia.national_employee_rate", 0.07m)
        .Set("grsia.national_employer_rate", 0.14m);

    private static readonly SalaryBreakdown Salary10k = new(10_000m, 0m, 0m, 0m);

    // ── 1. KSA parity — new pack EE total matches expected GOSI math ────────
    // Legacy GosiCalculationService for SAU national, SAR 10,000 covered wage:
    //   Annuities EE 9% = 900 + SANED EE 0.75% = 75 → total EE = 975
    // The pack path must produce the same number so payroll totals are unchanged.

    [Fact]
    public void LegacyGosi_SaudiNational_BaselineIs975()
    {
        // Sanity-check the legacy calculation we're matching.
        var legacyRules = GosiTests_DefaultRules();
        var legacyResult = GosiCalculationService.Calculate("SAU", 10_000m, legacyRules, new DateOnly(2026, 1, 1), Guid.Empty, new Zayra.Api.Infrastructure.CountryPack.Ksa.GosiWageBounds(null, Zayra.Api.Infrastructure.CountryPack.Ksa.KsaGosiWageBounds.DefaultMonthlyCeilingSar));
        Assert.Equal(975m, legacyResult.EmployeeTotal);
    }

    [Fact]
    public async Task KsaPack_SaudiNational_EmployeeDeductionIs975_MatchesLegacy()
    {
        // Pack result must equal the legacy GOSI baseline (975m).
        var calc = new KsaDeductionCalculator(KsaDefaultRules);
        var input = new StatutoryDeductionInput(
            Guid.Empty, Guid.Empty, Salary10k, "SAU", "Unlimited", 2026, 1);
        var packResult = await calc.CalculateAsync(input);
        Assert.Equal(975m, packResult.TotalEmployeeDeduction);   // parity with legacy path
    }

    [Fact]
    public async Task KsaPack_SaudiNational_ProducesAnnuityAndSanedLines()
    {
        var calc = new KsaDeductionCalculator(KsaDefaultRules);
        var input = new StatutoryDeductionInput(
            Guid.Empty, Guid.Empty, Salary10k, "SAU", "Unlimited", 2026, 1);
        var result = await calc.CalculateAsync(input);

        // EE: Annuities 9% = 900, SANED 0.75% = 75
        Assert.Equal(975m, result.TotalEmployeeDeduction);

        Assert.Contains(result.Lines, l => l.Code == "GOSI-ANN-EE" && l.EmployeeAmount == 900m);
        Assert.Contains(result.Lines, l => l.Code == "GOSI-SANED-EE" && l.EmployeeAmount == 75m);
    }

    // ── 2. UAE/GPSSA — UAE national gets GPSSA lines, not GOSI ───────────────

    [Fact]
    public async Task UaePack_UaeNational_ProducesGpssaLinesNotGosi()
    {
        var calc = new UaeDeductionCalculator(UaeDefaultRules);
        var input = new StatutoryDeductionInput(
            Guid.Empty, Guid.Empty, Salary10k, "AE", "Unlimited", 2026, 1);
        var result = await calc.CalculateAsync(input);

        // GPSSA EE 5% = 500, GPSSA ER 12.5% = 1,250
        Assert.Equal(500m, result.TotalEmployeeDeduction);
        Assert.Equal(1_250m, result.TotalEmployerContribution);

        Assert.Contains(result.Lines, l => l.Code == "GPSSA-EE");
        Assert.Contains(result.Lines, l => l.Code == "GPSSA-ER");
        Assert.DoesNotContain(result.Lines, l => l.Code.StartsWith("GOSI"));
    }

    [Fact]
    public async Task UaePack_Expatriate_ZeroDeduction()
    {
        var calc = new UaeDeductionCalculator(UaeDefaultRules);
        var input = new StatutoryDeductionInput(
            Guid.Empty, Guid.Empty, Salary10k, "India", "Unlimited", 2026, 1);
        var result = await calc.CalculateAsync(input);

        // Expats have no UAE statutory contribution
        Assert.Equal(0m, result.TotalEmployeeDeduction);
        Assert.Equal(0m, result.TotalEmployerContribution);
        Assert.Empty(result.Lines);
    }

    // ── 3. Qatar/GRSIA — Qatar national gets GRSIA lines ────────────────────

    [Fact]
    public async Task QatarPack_QatariNational_ProducesGrsiaLines()
    {
        var calc = new QatarDeductionCalculator(QatarDefaultRules);
        var input = new StatutoryDeductionInput(
            Guid.Empty, Guid.Empty, Salary10k, "QA", "Unlimited", 2026, 1);
        var result = await calc.CalculateAsync(input);

        // GRSIA EE 7% = 700, GRSIA ER 14% = 1,400 (on basic only)
        Assert.Equal(700m, result.TotalEmployeeDeduction);
        Assert.Equal(1_400m, result.TotalEmployerContribution);

        Assert.Contains(result.Lines, l => l.Code == "GRSIA-EE");
        Assert.Contains(result.Lines, l => l.Code == "GRSIA-ER");
        Assert.DoesNotContain(result.Lines, l => l.Code.StartsWith("GOSI"));
    }

    // ── 4. WPS format assertion — KSA → mudad-xml, UAE → mohre-sif ───────────

    [Fact]
    public async Task KsaWpsExporter_ProducesMudadXmlFormat()
    {
        var exporter = new KsaWageProtectionExporter();
        var input = WpsTestInput("SAU-EST-001");
        var result = await exporter.ExportAsync(input);

        Assert.Equal("mudad-xml", result.Format);
        Assert.True(result.FileBytes.Length > 0, "KSA exporter must produce non-empty file bytes");
        Assert.Equal(1, result.RecordCount);
    }

    [Fact]
    public async Task UaeWpsExporter_ProducesMohreSifFormat()
    {
        var exporter = new UaeWageProtectionExporter();
        var input = WpsTestInput("UAE-EST-001");
        var result = await exporter.ExportAsync(input);

        Assert.Equal("mohre-sif", result.Format);
        Assert.True(result.FileBytes.Length > 0, "UAE exporter must produce non-empty file bytes");
        Assert.Equal(1, result.RecordCount);
    }

    // ── 5. 422 guard sentinel — DefaultWageProtectionExporter signals no-pack ─

    [Fact]
    public async Task DefaultWpsExporter_ReturnsNoneFormat_Triggers422Guard()
    {
        // When CountryPackResolver cannot find a jurisdiction-specific exporter it falls
        // back to DefaultWageProtectionExporter. PayrollController checks for this sentinel
        // and returns HTTP 422 before calling ExportAsync.
        // This test verifies the sentinel contract: Format=="none", empty bytes.
        var exporter = new DefaultWageProtectionExporter();
        var input = WpsTestInput("UNKNOWN-001");
        var result = await exporter.ExportAsync(input);

        Assert.Equal("none", result.Format);
        Assert.Empty(result.FileBytes);
        Assert.Equal(0, result.RecordCount);
    }

    // ── 6. Tenant override changes KSA GOSI result ──────────────────────────
    // KsaDeductionCalculator reads rates from IStatutoryRuleReader.
    // When a tenant configures a different rate (e.g. 12% instead of 9%),
    // the calculator must return a proportionally different deduction.
    // This verifies the calculator is rate-driven, not hardcoded.

    [Fact]
    public async Task KsaPack_ElevatedTenantRate_ProducesHigherDeduction()
    {
        var standardRules = new StubRuleReader()
            .Set("gosi.saudi_employee_rate", 0.09m)   // 9%
            .Set("gosi.saudi_employer_rate", 0.09m)
            .Set("gosi.saned_rate",          0.0075m)
            .Set("gosi.covered_wage_ceiling_sar", 45_000m);

        var overrideRules = new StubRuleReader()
            .Set("gosi.saudi_employee_rate", 0.12m)   // tenant configured 12% (e.g. different bracket)
            .Set("gosi.saudi_employer_rate", 0.12m)
            .Set("gosi.saned_rate",          0.0075m)
            .Set("gosi.covered_wage_ceiling_sar", 45_000m);

        var input = new StatutoryDeductionInput(
            Guid.Empty, Guid.Empty, Salary10k, "SAU", "Unlimited", 2026, 1);

        var standardResult  = await new KsaDeductionCalculator(standardRules).CalculateAsync(input);
        var overrideResult  = await new KsaDeductionCalculator(overrideRules).CalculateAsync(input);

        // Standard: 9% + 0.75% = 975; Override: 12% + 0.75% = 1,275
        Assert.Equal(975m, standardResult.TotalEmployeeDeduction);
        Assert.Equal(1_275m, overrideResult.TotalEmployeeDeduction);
        Assert.True(overrideResult.TotalEmployeeDeduction > standardResult.TotalEmployeeDeduction,
            "Elevated rate must produce a higher employee deduction");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static WageProtectionExportInput WpsTestInput(string establishmentId) =>
        new(
            TenantId:        Guid.NewGuid(),
            CompanyId:       Guid.NewGuid(),
            PayrollRunId:    Guid.NewGuid(),
            PeriodYear:      2026,
            PeriodMonth:     1,
            EstablishmentId: establishmentId,
            EmployerIban:    "SA0000000000000000000000",
            CompanyNameEn:   "Test Company",
            CompanyNameAr:   "شركة اختبار",
            Employees: new[]
            {
                new WpsEmployee(
                    EmployeeId:   1,
                    EmployeeCode: "EMP001",
                    FullNameEn:   "Test Employee",
                    FullNameAr:   "موظف اختبار",
                    Nationality:  "SAU",
                    NationalId:   "1234567890",
                    IbanOrAccount:"SA0000000000000000000001",
                    BankCode:     "1060",
                    Salary:       new SalaryBreakdown(10_000m, 3_000m, 1_000m, 0m),
                    NetPay:       12_000m)
            });

    // Mirror the GosiTests DefaultRules factory — verified against GosiRuleSeeder.
    // UNIT: rates are decimal FRACTIONS of the contributory wage (0.09 = 9%), the same unit the
    // StubRuleReader rows above use for gosi.saudi_employee_rate. That the two engines agree on
    // 975m is only meaningful because they now agree on the unit too.
    private static List<Zayra.Api.Models.GosiContributionRule> GosiTests_DefaultRules()
    {
        var effective = new DateOnly(2016, 6, 1);
        return new List<Zayra.Api.Models.GosiContributionRule>
        {
            GRule("Saudi",    "Annuities",           "Employee", 0.09m,  effective),
            GRule("Saudi",    "Annuities",           "Employer", 0.09m,  effective),
            GRule("Saudi",    "SANED",               "Employee", 0.0075m,  effective),
            GRule("Saudi",    "SANED",               "Employer", 0.0075m,  effective),
            GRule("Saudi",    "OccupationalHazards", "Employer", 0.02m,  effective),
            GRule("GCC",      "Annuities",           "Employee", 0.09m,  effective),
            GRule("GCC",      "Annuities",           "Employer", 0.09m,  effective),
            GRule("GCC",      "SANED",               "Employee", 0.0075m,  effective),
            GRule("GCC",      "SANED",               "Employer", 0.0075m,  effective),
            GRule("GCC",      "OccupationalHazards", "Employer", 0.02m,  effective),
            GRule("NonSaudi", "OccupationalHazards", "Employer", 0.02m,  effective),
        };
    }

    private static Zayra.Api.Models.GosiContributionRule GRule(
        string classification, string branch, string payer, decimal rate, DateOnly from) =>
        new()
        {
            Id             = Guid.NewGuid(),
            TenantId       = Guid.Empty,
            CountryCode    = "SA",
            Classification = classification,
            Branch         = branch,
            Payer          = payer,
            Rate           = rate,
            EffectiveFrom  = from,
            IsActive       = true,
        };
}
