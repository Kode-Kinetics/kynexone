using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;

namespace Zayra.Api.Tests;

/// <summary>
/// Track A PR-3 — GOSI Readiness + Payroll Deduction Governance tests.
///
/// Coverage:
///   - GosiCalculationService: classification derivation, rule selection, amount calculation
///   - GosiReadinessValidator: blocking issues, warnings, classification pass-through
///   - GosiRuleSeeder: default rule structure and counts
///   - Component code / name generation
///   - Effective-date rule filtering and tenant-override priority
///   - Non-Saudi employees: only OccHazards employer contribution
///   - Wage cap enforcement
///   - GCC classification warning issued
/// </summary>
public class GosiTests
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static readonly DateOnly Period = new(2026, 6, 30);


    // The MONTHLY contributory-wage bounds, as KsaGosiWageBounds resolves them from the statutory

    // rules engine on a default installation: SAR 45,000 ceiling, no floor. These are now the ONLY

    // source of the cap — GosiContributionRule.Max/MinContributoryWage are no longer read.

    private static readonly Zayra.Api.Infrastructure.CountryPack.Ksa.GosiWageBounds StatutoryBounds =

        new(null, Zayra.Api.Infrastructure.CountryPack.Ksa.KsaGosiWageBounds.DefaultMonthlyCeilingSar);
    private static readonly Guid TenantA = Guid.NewGuid();

    private static List<GosiContributionRule> DefaultRules()
    {
        // Mirrors GosiRuleSeeder.BuildDefaultRules (public surface for tests)
        var effective = new DateOnly(2016, 6, 1);
        return new List<GosiContributionRule>
        {
            Rule(GosiClassifications.Saudi,    GosiBranches.Annuities,           GosiPayers.Employee, 9.00m,  effective),
            Rule(GosiClassifications.Saudi,    GosiBranches.Annuities,           GosiPayers.Employer, 9.00m,  effective),
            Rule(GosiClassifications.Saudi,    GosiBranches.SANED,               GosiPayers.Employee, 0.75m, effective),
            Rule(GosiClassifications.Saudi,    GosiBranches.SANED,               GosiPayers.Employer, 0.75m, effective),
            Rule(GosiClassifications.Saudi,    GosiBranches.OccupationalHazards, GosiPayers.Employer, 2.00m,  effective),
            Rule(GosiClassifications.GCC,      GosiBranches.Annuities,           GosiPayers.Employee, 9.00m,  effective),
            Rule(GosiClassifications.GCC,      GosiBranches.Annuities,           GosiPayers.Employer, 9.00m,  effective),
            Rule(GosiClassifications.GCC,      GosiBranches.SANED,               GosiPayers.Employee, 0.75m, effective),
            Rule(GosiClassifications.GCC,      GosiBranches.SANED,               GosiPayers.Employer, 0.75m, effective),
            Rule(GosiClassifications.GCC,      GosiBranches.OccupationalHazards, GosiPayers.Employer, 2.00m,  effective),
            Rule(GosiClassifications.NonSaudi, GosiBranches.OccupationalHazards, GosiPayers.Employer, 2.00m,  effective),
        };
    }

    private static GosiContributionRule Rule(
        string  classification,
        string  branch,
        string  payer,
        decimal rate,
        DateOnly from,
        DateOnly? to      = null,
        Guid?   tenantId  = null) =>
        new()
        {
            Id             = Guid.NewGuid(),
            TenantId       = tenantId ?? Guid.Empty,
            CountryCode    = "SA",
            Classification = classification,
            Branch         = branch,
            Payer          = payer,
            Rate           = rate,
            EffectiveFrom  = from,
            EffectiveTo    = to,
            IsActive       = true,
        };

    // ── DeriveClassification ──────────────────────────────────────────────────

    [Fact]
    public void DeriveClassification_Saudi_ISO_Code()
    {
        Assert.Equal(GosiClassifications.Saudi, GosiCalculationService.DeriveClassification("SA"));
        Assert.Equal(GosiClassifications.Saudi, GosiCalculationService.DeriveClassification("SAU"));
        Assert.Equal(GosiClassifications.Saudi, GosiCalculationService.DeriveClassification("Saudi"));
        Assert.Equal(GosiClassifications.Saudi, GosiCalculationService.DeriveClassification("Saudi Arabia"));
        Assert.Equal(GosiClassifications.Saudi, GosiCalculationService.DeriveClassification("saudi arabia")); // case-insensitive
    }

    [Fact]
    public void DeriveClassification_GCC_Countries()
    {
        Assert.Equal(GosiClassifications.GCC, GosiCalculationService.DeriveClassification("UAE"));
        Assert.Equal(GosiClassifications.GCC, GosiCalculationService.DeriveClassification("Bahrain"));
        Assert.Equal(GosiClassifications.GCC, GosiCalculationService.DeriveClassification("KW"));
        Assert.Equal(GosiClassifications.GCC, GosiCalculationService.DeriveClassification("Emirati"));
        Assert.Equal(GosiClassifications.GCC, GosiCalculationService.DeriveClassification("Kuwaiti"));
        Assert.Equal(GosiClassifications.GCC, GosiCalculationService.DeriveClassification("Qatari"));
    }

    [Fact]
    public void DeriveClassification_NonSaudi_Default()
    {
        Assert.Equal(GosiClassifications.NonSaudi, GosiCalculationService.DeriveClassification("India"));
        Assert.Equal(GosiClassifications.NonSaudi, GosiCalculationService.DeriveClassification("Egypt"));
        Assert.Equal(GosiClassifications.NonSaudi, GosiCalculationService.DeriveClassification("US"));
        Assert.Equal(GosiClassifications.NonSaudi, GosiCalculationService.DeriveClassification(null));
        Assert.Equal(GosiClassifications.NonSaudi, GosiCalculationService.DeriveClassification("   "));
    }

    // ── SelectActiveRules — effective-date filtering ───────────────────────────

    [Fact]
    public void SelectActiveRules_ReturnsOnlyEffectiveOnPeriodDate()
    {
        var rules = new List<GosiContributionRule>
        {
            Rule(GosiClassifications.Saudi, GosiBranches.Annuities, GosiPayers.Employee, 9.0m,
                new DateOnly(2016, 1, 1), new DateOnly(2023, 12, 31)), // expired
            Rule(GosiClassifications.Saudi, GosiBranches.Annuities, GosiPayers.Employee, 9.5m,
                new DateOnly(2024, 1, 1)), // current
        };

        var active = GosiCalculationService.SelectActiveRules(
            GosiClassifications.Saudi, rules, new DateOnly(2026, 1, 1), Guid.Empty);

        var annuityEmp = active.Single(r => r.Branch == GosiBranches.Annuities && r.Payer == GosiPayers.Employee);
        Assert.Equal(9.5m, annuityEmp.Rate);
    }

    [Fact]
    public void SelectActiveRules_TenantOverrideTakesPrecedenceOverDefault()
    {
        var tenant = Guid.NewGuid();
        var rules = new List<GosiContributionRule>
        {
            Rule(GosiClassifications.Saudi, GosiBranches.Annuities, GosiPayers.Employee, 9.0m,
                new DateOnly(2016, 1, 1), tenantId: Guid.Empty),
            Rule(GosiClassifications.Saudi, GosiBranches.Annuities, GosiPayers.Employee, 8.5m,
                new DateOnly(2025, 1, 1), tenantId: tenant), // tenant override with lower rate
        };

        var active = GosiCalculationService.SelectActiveRules(
            GosiClassifications.Saudi, rules, Period, tenant);

        var rule = active.Single(r => r.Branch == GosiBranches.Annuities && r.Payer == GosiPayers.Employee);
        Assert.Equal(8.5m, rule.Rate); // tenant override wins
    }

    [Fact]
    public void SelectActiveRules_ExcludesOtherClassifications()
    {
        var rules = DefaultRules();
        var selected = GosiCalculationService.SelectActiveRules(
            GosiClassifications.NonSaudi, rules, Period, Guid.Empty);

        // NonSaudi only gets OccHazards employer — no Annuities or SANED
        Assert.DoesNotContain(selected, r => r.Branch == GosiBranches.Annuities);
        Assert.DoesNotContain(selected, r => r.Branch == GosiBranches.SANED);
        Assert.Contains(selected, r => r.Branch == GosiBranches.OccupationalHazards);
    }

    // ── Calculate — Saudi employee ────────────────────────────────────────────

    [Fact]
    public void Calculate_Saudi_CorrectBranchAmounts()
    {
        var result = GosiCalculationService.Calculate("Saudi Arabia", 10_000m, DefaultRules(), Period, Guid.Empty, StatutoryBounds);

        Assert.Equal(GosiClassifications.Saudi, result.Classification);

        // Employee: Annuities 9% + SANED 0.75% = 9.75%
        Assert.Equal(975m, result.EmployeeTotal);

        // Employer: Annuities 9% + SANED 0.75% + OccHazards 2% = 11.75%
        Assert.Equal(1175m, result.EmployerTotal);
    }

    [Fact]
    public void Calculate_Saudi_SevenBranchLines()
    {
        var result = GosiCalculationService.Calculate("SA", 10_000m, DefaultRules(), Period, Guid.Empty, StatutoryBounds);

        // 2 employee lines (Annuities + SANED) + 3 employer lines (Annuities + SANED + OccHazards)
        Assert.Equal(5, result.Lines.Count);
        Assert.Equal(2, result.Lines.Count(l => l.Payer == GosiPayers.Employee));
        Assert.Equal(3, result.Lines.Count(l => l.Payer == GosiPayers.Employer));
    }

    [Fact]
    public void Calculate_NonSaudi_OnlyOccHazardsEmployer()
    {
        var result = GosiCalculationService.Calculate("India", 10_000m, DefaultRules(), Period, Guid.Empty, StatutoryBounds);

        Assert.Equal(GosiClassifications.NonSaudi, result.Classification);
        Assert.Equal(0m, result.EmployeeTotal);       // no employee deduction
        Assert.Equal(200m, result.EmployerTotal);     // 2% of 10,000
        Assert.Single(result.Lines);
        Assert.Equal(GosiBranches.OccupationalHazards, result.Lines[0].Branch);
        Assert.Equal(GosiPayers.Employer, result.Lines[0].Payer);
    }

    [Fact]
    public void Calculate_GCC_SameRatesAsSaudi()
    {
        var saudi  = GosiCalculationService.Calculate("Saudi Arabia", 10_000m, DefaultRules(), Period, Guid.Empty, StatutoryBounds);
        var gcc    = GosiCalculationService.Calculate("UAE",           10_000m, DefaultRules(), Period, Guid.Empty, StatutoryBounds);

        Assert.Equal(saudi.EmployeeTotal, gcc.EmployeeTotal);
        Assert.Equal(saudi.EmployerTotal, gcc.EmployerTotal);
    }

    [Fact]
    public void Calculate_ZeroBasic_ProducesNoLines()
    {
        var result = GosiCalculationService.Calculate("Saudi Arabia", 0m, DefaultRules(), Period, Guid.Empty, StatutoryBounds);

        Assert.Equal(0m, result.EmployeeTotal);
        Assert.Equal(0m, result.EmployerTotal);
        Assert.Empty(result.Lines);
    }

    [Fact]
    public void Calculate_WageCap_Applied()
    {
        var rule  = Rule(GosiClassifications.Saudi, GosiBranches.Annuities, GosiPayers.Employee, 9.0m,
            new DateOnly(2016, 1, 1));
        var rules = new List<GosiContributionRule> { rule };

        // Wage above the ceiling: contribution is computed on the ceiling, not on the wage.
        var result = GosiCalculationService.Calculate("SA", 60_000m, rules, Period, Guid.Empty, StatutoryBounds);
        var line   = result.Lines.Single();
        Assert.Equal(45_000m, line.ContributoryWage);
        Assert.Equal(Math.Round(45_000m * 9m / 100m, 2), line.Amount);
    }

    /// <summary>
    /// THE DEFECT. GosiRuleSeeder never populates MinContributoryWage / MaxContributoryWage, so on
    /// platform defaults those columns are NULL. While the calculator read them, the GOSI preview and
    /// the GOSI readiness report computed UNCAPPED off exactly those rows while the payslip capped at
    /// SAR 45,000 from the statutory rules engine: SAR 5,850 shown against SAR 4,387.50 deducted, on
    /// a 60,000 covered wage, in the number a finance team reconciles against the GOSI portal.
    ///
    /// The cap now comes from one place. A platform-default rule — caps NULL, exactly as the seeder
    /// writes it — must still produce the capped figure.
    /// </summary>
    [Fact]
    public void Calculate_PlatformDefaultRuleWithNoCapColumns_StillCapsAtTheStatutoryCeiling()
    {
        var platformDefault = Rule(GosiClassifications.Saudi, GosiBranches.Annuities, GosiPayers.Employee, 9.0m,
            new DateOnly(2016, 1, 1));
        platformDefault.TenantId = Guid.Empty;

        // Precisely what GosiRuleSeeder.Rule() produces: both bound columns unset.
        Assert.Null(platformDefault.MinContributoryWage);
        Assert.Null(platformDefault.MaxContributoryWage);

        var result = GosiCalculationService.Calculate(
            "SA", 60_000m, new List<GosiContributionRule> { platformDefault }, Period, Guid.Empty, StatutoryBounds);

        var line = result.Lines.Single();
        Assert.Equal(45_000m, line.ContributoryWage);
        Assert.Equal(4_050m, line.Amount);                 // 45,000 x 9%, not 60,000 x 9% = 5,400
    }

    /// <summary>
    /// The full employee side of the same case: 9% Annuities + 0.75% SANED on a SAR 60,000 covered
    /// wage is SAR 4,387.50 against the ceiling, and SAR 5,850 without it. This pins the exact pair
    /// of figures from the defect report.
    /// </summary>
    [Fact]
    public void Calculate_SixtyThousandWage_EmployeeTotalIsCeilingBased()
    {
        var capped   = GosiCalculationService.Calculate("SA", 60_000m, DefaultRules(), Period, Guid.Empty, StatutoryBounds);
        var uncapped = GosiCalculationService.Calculate("SA", 60_000m, DefaultRules(), Period, Guid.Empty,
            Zayra.Api.Infrastructure.CountryPack.Ksa.GosiWageBounds.Unbounded);

        Assert.Equal(4_387.50m, capped.EmployeeTotal);     // what the payslip deducts
        Assert.Equal(5_850.00m, uncapped.EmployeeTotal);   // what the report used to show
    }

    /// <summary>
    /// RATCHET — the second store stays dead. A rule carrying its own MaxContributoryWage must not
    /// change the answer: re-introducing a per-row cap re-introduces the divergence, because the
    /// payroll run's country pack does not read that column and never has.
    /// </summary>
    [Fact]
    public void Calculate_PerRuleCapColumns_AreNotRead()
    {
        var withOwnCap = Rule(GosiClassifications.Saudi, GosiBranches.Annuities, GosiPayers.Employee, 9.0m,
            new DateOnly(2016, 1, 1));
        withOwnCap.MaxContributoryWage = 10_000m;
        withOwnCap.MinContributoryWage = 9_000m;

        var result = GosiCalculationService.Calculate(
            "SA", 60_000m, new List<GosiContributionRule> { withOwnCap }, Period, Guid.Empty, StatutoryBounds);

        // 45,000 (the statutory ceiling), NOT 10,000 (the row's own column).
        Assert.Equal(45_000m, result.Lines.Single().ContributoryWage);
    }

    // ── ComponentCode / ComponentName ─────────────────────────────────────────

    [Fact]
    public void ToComponentCode_CorrectCodes()
    {
        Assert.Equal("GOSI_ANNUITIES_EMP",  GosiCalculationService.ToComponentCode(GosiBranches.Annuities,           GosiPayers.Employee));
        Assert.Equal("GOSI_SANED_EMP",      GosiCalculationService.ToComponentCode(GosiBranches.SANED,               GosiPayers.Employee));
        Assert.Equal("GOSI_ANNUITIES_ER",   GosiCalculationService.ToComponentCode(GosiBranches.Annuities,           GosiPayers.Employer));
        Assert.Equal("GOSI_SANED_ER",       GosiCalculationService.ToComponentCode(GosiBranches.SANED,               GosiPayers.Employer));
        Assert.Equal("GOSI_OCHAZARDS_ER",   GosiCalculationService.ToComponentCode(GosiBranches.OccupationalHazards, GosiPayers.Employer));
    }

    [Fact]
    public void ToComponentName_ContainsRateAndPayer()
    {
        var name = GosiCalculationService.ToComponentName(GosiBranches.Annuities, GosiPayers.Employee, 9.00m);
        Assert.Contains("9", name);
        Assert.Contains("employee", name, StringComparison.OrdinalIgnoreCase);
    }

    // ── GosiReadinessValidator ────────────────────────────────────────────────

    private static Employee ReadyEmployee(Guid tenantId = default) => new()
    {
        Id           = 1,
        TenantId     = tenantId,
        EmployeeCode = "EMP-001",
        FullName     = "Ali Al-Ghamdi",
        Status       = "Active",
        Nationality  = "Saudi Arabia",
        GosiReference = "GOSI-123456",
        SaudiOrNonSaudi = "Saudi",
        IdType         = "NationalId",
        OccupationCode = "2421",
        EstablishmentId = "7000123456",
        WorkLocationId  = "WL-1",
        ContractReference = "CONTRACT-1",
    };

    [Fact]
    public void Validate_ReadyEmployee_NoIssues()
    {
        var rules  = GosiCalculationService.SelectActiveRules(GosiClassifications.Saudi, DefaultRules(), Period, Guid.Empty);
        var report = GosiReadinessValidator.Validate(ReadyEmployee(), 10_000m, rules);

        Assert.True(report.IsReady);
        Assert.Empty(report.BlockingIssues);
    }

    [Fact]
    public void Validate_MissingGosiReference_BlockingIssue()
    {
        var emp = ReadyEmployee();
        emp.GosiReference = "";
        var rules  = GosiCalculationService.SelectActiveRules(GosiClassifications.Saudi, DefaultRules(), Period, Guid.Empty);
        var report = GosiReadinessValidator.Validate(emp, 10_000m, rules);

        Assert.False(report.IsReady);
        Assert.Contains(report.BlockingIssues, i => i.Code == "MISSING_GOSI_REFERENCE");
    }

    [Fact]
    public void Validate_MissingBasicSalary_BlockingIssue()
    {
        var rules  = GosiCalculationService.SelectActiveRules(GosiClassifications.Saudi, DefaultRules(), Period, Guid.Empty);
        var report = GosiReadinessValidator.Validate(ReadyEmployee(), 0m, rules);

        Assert.False(report.IsReady);
        Assert.Contains(report.BlockingIssues, i => i.Code == "MISSING_BASIC_SALARY");
    }

    [Fact]
    public void Validate_NullSalary_BlockingIssue()
    {
        var rules  = GosiCalculationService.SelectActiveRules(GosiClassifications.Saudi, DefaultRules(), Period, Guid.Empty);
        var report = GosiReadinessValidator.Validate(ReadyEmployee(), null, rules);

        Assert.Contains(report.BlockingIssues, i => i.Code == "MISSING_BASIC_SALARY");
    }

    [Fact]
    public void Validate_MissingNationality_Warning_NotBlocking()
    {
        var emp = ReadyEmployee();
        emp.Nationality = "";
        var rules  = GosiCalculationService.SelectActiveRules(GosiClassifications.NonSaudi, DefaultRules(), Period, Guid.Empty);
        var report = GosiReadinessValidator.Validate(emp, 10_000m, rules);

        // Missing nationality is a warning, not a blocker
        Assert.True(report.IsReady);
        Assert.Contains(report.Warnings, w => w.Code == "MISSING_NATIONALITY");
    }

    [Fact]
    public void Validate_GCCEmployee_HasConfirmationWarning()
    {
        var emp = ReadyEmployee();
        emp.Nationality = "UAE";
        var rules  = GosiCalculationService.SelectActiveRules(GosiClassifications.GCC, DefaultRules(), Period, Guid.Empty);
        var report = GosiReadinessValidator.Validate(emp, 10_000m, rules);

        Assert.True(report.IsReady);
        Assert.Contains(report.Warnings, w => w.Code == "GCC_RULES_PENDING_CONFIRMATION");
    }

    [Fact]
    public void Validate_NoApplicableRules_Warning()
    {
        var rules  = new List<GosiContributionRule>(); // empty — no rules
        var report = GosiReadinessValidator.Validate(ReadyEmployee(), 10_000m, rules);

        Assert.True(report.IsReady); // not blocking
        Assert.Contains(report.Warnings, w => w.Code == "NO_APPLICABLE_RULES");
    }

    // ── GosiRuleSeeder defaults ────────────────────────────────────────────────

    [Fact]
    public async Task GosiRuleSeeder_SeedsExpectedRuleCount()
    {
        var opts = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using var db = new ZayraDbContext(opts);

        var logger = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance.CreateLogger("test");
        await GosiRuleSeeder.SeedDefaultsAsync(db, logger);

        // Global defaults: 5 Saudi + 5 GCC + 1 NonSaudi = 11 rules
        var count = await db.GosiContributionRules.IgnoreQueryFilters().CountAsync(r => r.TenantId == Guid.Empty);
        Assert.Equal(11, count);
    }

    [Fact]
    public async Task GosiRuleSeeder_IsIdempotent()
    {
        var opts = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using var db = new ZayraDbContext(opts);

        var logger = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance.CreateLogger("test");
        await GosiRuleSeeder.SeedDefaultsAsync(db, logger);
        await GosiRuleSeeder.SeedDefaultsAsync(db, logger); // second call — should be no-op

        var count = await db.GosiContributionRules.IgnoreQueryFilters().CountAsync(r => r.TenantId == Guid.Empty);
        Assert.Equal(11, count);
    }

    [Fact]
    public async Task GosiRuleSeeder_SaudiRulesHaveCorrectBaselineRates()
    {
        var opts = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using var db = new ZayraDbContext(opts);

        var logger = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance.CreateLogger("test");
        await GosiRuleSeeder.SeedDefaultsAsync(db, logger);

        var rules = await db.GosiContributionRules.IgnoreQueryFilters()
            .Where(r => r.TenantId == Guid.Empty && r.Classification == GosiClassifications.Saudi)
            .ToListAsync();

        Assert.Equal(9.00m, rules.Single(r => r.Branch == GosiBranches.Annuities && r.Payer == GosiPayers.Employee).Rate);
        Assert.Equal(9.00m, rules.Single(r => r.Branch == GosiBranches.Annuities && r.Payer == GosiPayers.Employer).Rate);
        Assert.Equal(0.75m, rules.Single(r => r.Branch == GosiBranches.SANED     && r.Payer == GosiPayers.Employee).Rate);
        Assert.Equal(0.75m, rules.Single(r => r.Branch == GosiBranches.SANED     && r.Payer == GosiPayers.Employer).Rate);
        Assert.Equal(2.00m, rules.Single(r => r.Branch == GosiBranches.OccupationalHazards && r.Payer == GosiPayers.Employer).Rate);
    }

    // ── Tenant isolation ──────────────────────────────────────────────────────

    [Fact]
    public void SelectActiveRules_DoesNotReturnOtherTenantOverrides()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var rules = new List<GosiContributionRule>
        {
            Rule(GosiClassifications.Saudi, GosiBranches.Annuities, GosiPayers.Employee, 9.0m,
                new DateOnly(2016, 1, 1), tenantId: Guid.Empty),
            Rule(GosiClassifications.Saudi, GosiBranches.Annuities, GosiPayers.Employee, 7.5m,
                new DateOnly(2025, 1, 1), tenantId: tenantB), // tenantB override
        };

        var selected = GosiCalculationService.SelectActiveRules(
            GosiClassifications.Saudi, rules, Period, tenantA); // querying for tenantA

        var annuity = selected.Single(r => r.Branch == GosiBranches.Annuities && r.Payer == GosiPayers.Employee);
        Assert.Equal(9.0m, annuity.Rate); // tenantB override not visible to tenantA
    }

    // ── Rounding ─────────────────────────────────────────────────────────────

    [Fact]
    public void Calculate_AmountsRoundedToTwoDecimals()
    {
        // 8333 * 9% = 749.97, 8333 * 0.75% = 62.4975 → 62.50
        var result = GosiCalculationService.Calculate("SA", 8_333m, DefaultRules(), Period, Guid.Empty, StatutoryBounds);

        foreach (var line in result.Lines)
            Assert.Equal(Math.Round(line.ContributoryWage * line.Rate / 100m, 2), line.Amount);
    }

    [Fact]
    public void Calculate_EmployeeTotalEqualsSumOfEmployeeLines()
    {
        var result = GosiCalculationService.Calculate("Saudi", 12_500m, DefaultRules(), Period, Guid.Empty, StatutoryBounds);
        var expectedTotal = result.Lines.Where(l => l.Payer == GosiPayers.Employee).Sum(l => l.Amount);
        Assert.Equal(expectedTotal, result.EmployeeTotal);
    }

    [Fact]
    public void Calculate_EmployerTotalEqualsSumOfEmployerLines()
    {
        var result = GosiCalculationService.Calculate("Saudi", 12_500m, DefaultRules(), Period, Guid.Empty, StatutoryBounds);
        var expectedTotal = result.Lines.Where(l => l.Payer == GosiPayers.Employer).Sum(l => l.Amount);
        Assert.Equal(expectedTotal, result.EmployerTotal);
    }
}
