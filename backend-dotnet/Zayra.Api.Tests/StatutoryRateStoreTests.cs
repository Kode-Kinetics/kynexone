using System.Text.RegularExpressions;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Qatar;
using Zayra.Api.Infrastructure.CountryPack.Uae;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// S1 — A2 (three disagreeing GOSI rate stores), A10 (no GPSSA bounds), A11 (Qatar contribution base).
///
/// <para>A2 is the finding internal-consistency testing structurally cannot reach, because each store
/// is self-consistent. The tests below compare the stores against EACH OTHER and against the unit
/// each one is documented to use.</para>
/// </summary>
public class StatutoryRateStoreTests
{
    // ── A2(a) — the tenant rate rows, and the unit confusion under them ─────────────────────────

    /// <summary>
    /// <c>GosiContributionRule.Rate</c> is a PERCENT: <c>GosiCalculationService</c> computes
    /// <c>wage × Rate / 100</c>. <c>KsaDemoTenantSeeder</c> wrote FRACTIONS (0.10m for "10%"), which
    /// resolved to 0.10% — roughly ninety times under — on TENANT rows, which beat the platform
    /// defaults. Nothing in the suite noticed, because the store was consistent with itself.
    /// </summary>
    [Fact]
    public void EverySeededGosiRate_IsExpressedInPercentNotAsAFraction()
    {
        var seedDir = ResolveSeedDirectory();
        if (seedDir is null) return;

        // Rate = <number>m inside a GosiContributionRule initializer.
        var rx = new Regex(@"new\s+GosiContributionRule\s*\{[^}]*?\bRate\s*=\s*([0-9]*\.?[0-9]+)m",
            RegexOptions.Singleline);
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(seedDir, "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            foreach (Match m in rx.Matches(source))
            {
                var rate = decimal.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                // A genuine GCC social-insurance branch rate in percent is never below 0.1% and never
                // above 30%. A value under 0.1 is a fraction that someone meant as a percentage.
                if (rate > 0m && rate < 0.1m)
                {
                    var line = source.Take(m.Index).Count(c => c == '\n') + 1;
                    offenders.Add($"{Path.GetFileName(file)}:{line} (Rate = {rate}m)");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "GosiContributionRule.Rate is a PERCENT — GosiCalculationService computes wage × Rate / 100 " +
            "and GosiRuleSeeder writes 9.00m / 0.75m / 2.00m. A row written as a FRACTION (0.09m for 9%) " +
            "silently contributes ~1% of what is owed, and because these are tenant rows they beat the " +
            "platform defaults for every tenant the seeder touches. Offenders: " + string.Join(", ", offenders));
    }

    [Fact]
    public void KsaTenantSeeder_AndPlatformDefaults_AgreeOnTheSaudiSchedule()
    {
        var seedDir = ResolveSeedDirectory();
        if (seedDir is null) return;
        var source = File.ReadAllText(Path.Combine(seedDir, "KsaDemoTenantSeeder.cs"));

        // 9% / 9% annuities, 0.75% / 0.75% SANED, 2% occupational hazard — the same schedule
        // GosiRuleSeeder ships as the platform default. The old rows were 0.10 / 0.12 / 0.01 / 0.02.
        Assert.Contains("Branch = GosiBranches.Annuities, Payer = GosiPayers.Employee, Rate = 9.00m", source);
        Assert.Contains("Branch = GosiBranches.Annuities, Payer = GosiPayers.Employer, Rate = 9.00m", source);
        Assert.Contains("Branch = GosiBranches.SANED,      Payer = GosiPayers.Employee, Rate = 0.75m", source);
        Assert.Contains("Branch = GosiBranches.SANED,      Payer = GosiPayers.Employer, Rate = 0.75m", source);
        Assert.DoesNotContain("Rate = 0.10m", source);
        Assert.DoesNotContain("Rate = 0.12m", source);
    }

    /// <summary>
    /// The tenant seeder also wrote a THIRD store — StatutoryRule rows whose keys never matched the
    /// keys the country pack reads, so they carried the same invented figures and influenced nothing.
    /// Dead rows with wrong values are a trap: the next person to read them believes them.
    /// </summary>
    [Fact]
    public void KsaTenantSeeder_StatutoryRules_UseTheKeysThePackActuallyReads()
    {
        var seedDir = ResolveSeedDirectory();
        if (seedDir is null) return;
        var source = File.ReadAllText(Path.Combine(seedDir, "KsaDemoTenantSeeder.cs"));

        Assert.Contains("\"gosi.saudi_employee_rate\"", source);
        Assert.Contains("\"gosi.saudi_employer_rate\"", source);
        Assert.Contains("\"gosi.saned_rate\"", source);
        Assert.DoesNotContain("\"gosi.employee_rate_saudi\"", source);   // the dead key
        Assert.DoesNotContain("\"gosi.saned_employee_rate\"", source);   // the dead key
    }

    // ── A2(b) — the two engines must agree on the contributory wage ─────────────────────────────

    [Fact]
    public void BothGosiEngines_ComputeOnBasicPlusHousing()
    {
        // SalaryBreakdown.GosiCoveredWage (the pack) has always been basic + housing.
        // GosiCalculationService took "basicSalary" and every caller passed basic, so the GOSI module,
        // the readiness report and the compliance dashboard all under-stated what payroll deducted —
        // and the dashboard is the number a finance team reconciles against the GOSI portal.
        var breakdown = new SalaryBreakdown(10_000m, 4_000m, 2_000m, 500m);
        Assert.Equal(14_000m, breakdown.GosiCoveredWage);

        var rules = new List<GosiContributionRule>
        {
            new() { TenantId = Guid.Empty, Classification = GosiClassifications.Saudi,
                    Branch = GosiBranches.Annuities, Payer = GosiPayers.Employee, Rate = 9.00m,
                    EffectiveFrom = new DateOnly(2016, 6, 1), IsActive = true },
        };

        var viaModule = GosiCalculationService.Calculate(
            "Saudi", breakdown.GosiCoveredWage, rules, new DateOnly(2026, 1, 1), Guid.Empty, new Zayra.Api.Infrastructure.CountryPack.Ksa.GosiWageBounds(null, Zayra.Api.Infrastructure.CountryPack.Ksa.KsaGosiWageBounds.DefaultMonthlyCeilingSar));

        Assert.Equal(Math.Round(14_000m * 0.09m, 2), viaModule.EmployeeTotal);
    }

    [Fact]
    public void GosiReadinessAndPreview_PassTheCoveredWageNotBasicAlone()
    {
        // Source lint: the two production callers must add the housing allowance. A unit test on the
        // pure function cannot see which number a caller hands it, and that was the whole defect.
        foreach (var (file, needle) in new[]
                 {
                     ("Infrastructure/Compliance/GosiReadinessReportService.cs", "salary!.BasicSalary + salary.HousingAllowance"),
                     ("Controllers/GosiController.cs",                           "salary.BasicSalary + salary.HousingAllowance"),
                 })
        {
            var path = ResolveApiFile(file);
            if (path is null) continue;
            Assert.Contains(needle, File.ReadAllText(path), StringComparison.Ordinal);
        }
    }

    // ── A2(c) — one ceiling, not a compiled third copy ──────────────────────────────────────────

    [Fact]
    public void ValidationEngine_ReadsTheCeilingFromTheContext()
    {
        var path = ResolveApiFile("Infrastructure/Payroll/PayrollValidationEngine.cs");
        if (path is null) return;
        var source = File.ReadAllText(path);

        Assert.Contains("ctx.GosiCoveredWageCeiling", source, StringComparison.Ordinal);
        // The old compiled constant must no longer be the thing the comparison uses.
        Assert.DoesNotMatch(new Regex(@"coveredWage\s*>\s*GosiCoveredWageCeiling"), source);
    }

    // ── A10 — GPSSA floor and ceiling ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Gpssa_CapsTheContributionSalary_SoTheEmployeeIsNotOverDeducted()
    {
        // An Emirati executive on AED 100,000 basic + 20,000 housing had 5% taken from the whole
        // 120,000 — AED 6,000 a month, against AED 2,500 at the 50,000 cap. Over-deducting without
        // statutory basis is an unlawful deduction under Art. 25 of Decree-Law 33/2021, and unlike an
        // under-contribution it costs the employee cash this month.
        var calc = new UaeDeductionCalculator(new StubRuleReader()
            .Set("gpssa.national_employee_rate", 0.05m)
            .Set("gpssa.national_employer_rate", 0.125m)
            .Set("gpssa.contribution_salary_min", 1_000m)
            .Set("gpssa.contribution_salary_max", 50_000m));

        var result = await calc.CalculateAsync(new StatutoryDeductionInput(
            Guid.NewGuid(), Guid.NewGuid(), new SalaryBreakdown(100_000m, 20_000m, 0m, 0m),
            "Emirati", "Indefinite", 2026, 1));

        Assert.Equal(2_500m, result.TotalEmployeeDeduction);      // 50,000 × 5%, not 120,000 × 5%
        Assert.Equal(6_250m, result.TotalEmployerContribution);   // 50,000 × 12.5%
    }

    [Fact]
    public async Task Gpssa_AppliesTheFloor()
    {
        var calc = new UaeDeductionCalculator(new StubRuleReader()
            .Set("gpssa.national_employee_rate", 0.05m)
            .Set("gpssa.national_employer_rate", 0.125m)
            .Set("gpssa.contribution_salary_min", 1_000m)
            .Set("gpssa.contribution_salary_max", 50_000m));

        var result = await calc.CalculateAsync(new StatutoryDeductionInput(
            Guid.NewGuid(), Guid.NewGuid(), new SalaryBreakdown(400m, 0m, 0m, 0m),
            "Emirati", "Indefinite", 2026, 1));

        Assert.Equal(50m, result.TotalEmployeeDeduction);   // 1,000 × 5%
    }

    [Fact]
    public async Task Gpssa_WithNoBoundsConfigured_BehavesExactlyAsBefore()
    {
        // A tenant the seeder has not reached must not have its numbers move.
        var calc = new UaeDeductionCalculator(new StubRuleReader()
            .Set("gpssa.national_employee_rate", 0.05m)
            .Set("gpssa.national_employer_rate", 0.125m));

        var result = await calc.CalculateAsync(new StatutoryDeductionInput(
            Guid.NewGuid(), Guid.NewGuid(), new SalaryBreakdown(100_000m, 20_000m, 0m, 0m),
            "Emirati", "Indefinite", 2026, 1));

        Assert.Equal(6_000m, result.TotalEmployeeDeduction);
    }

    // ── A11 — the Qatar contribution salary under Law 1/2022 ────────────────────────────────────

    [Fact]
    public async Task Qatar_ContributionSalary_IncludesHousingFromJanuary2023()
    {
        var calc = new QatarDeductionCalculator(new StubRuleReader()
            .Set("grsia.national_employee_rate", 0.07m)
            .Set("grsia.national_employer_rate", 0.14m));

        var result = await calc.CalculateAsync(new StatutoryDeductionInput(
            Guid.NewGuid(), Guid.NewGuid(), new SalaryBreakdown(15_000m, 5_000m, 2_000m, 0m),
            "Qatari", "Indefinite", 2026, 1));

        Assert.Equal(Math.Round(20_000m * 0.07m, 2), result.TotalEmployeeDeduction);
        Assert.Equal(Math.Round(20_000m * 0.14m, 2), result.TotalEmployerContribution);
        // Transport stays out: Law 1/2022 names basic + social + housing.
        Assert.NotEqual(Math.Round(22_000m * 0.07m, 2), result.TotalEmployeeDeduction);
    }

    [Fact]
    public async Task Qatar_PrePeriod2023_StillReproducesTheLaw24Of2002BasicOnlyBase()
    {
        // Re-running a 2022 period must reproduce what was actually filed at the time, not restate it
        // under a law that had not commenced.
        var calc = new QatarDeductionCalculator(new StubRuleReader()
            .Set("grsia.national_employee_rate", 0.07m)
            .Set("grsia.national_employer_rate", 0.14m));

        var result = await calc.CalculateAsync(new StatutoryDeductionInput(
            Guid.NewGuid(), Guid.NewGuid(), new SalaryBreakdown(15_000m, 5_000m, 0m, 0m),
            "Qatari", "Indefinite", 2022, 6));

        Assert.Equal(Math.Round(15_000m * 0.07m, 2), result.TotalEmployeeDeduction);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static string? ResolveSeedDirectory() => ResolveApiPath(Path.Combine("Infrastructure", "Seed"), isDir: true);
    private static string? ResolveApiFile(string relative) => ResolveApiPath(relative.Replace('/', Path.DirectorySeparatorChar), isDir: false);

    private static string? ResolveApiPath(string relative, bool isDir)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6; i++)
        {
            if (dir?.Parent is null) return null;
            dir = dir.Parent;
            var candidate = Path.Combine(dir.FullName, "Zayra.Api", relative);
            if (isDir ? Directory.Exists(candidate) : File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
