using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The unit of a statutory rate, and the three things that have to stay true about it.
///
/// <para><b>1. A mistyped percentage is refused at the API.</b> The dangerous input is not an
/// absurd one — it is "9" for a rate whose stored form is "0.09". It is what an operator who has
/// read "9%" anywhere naturally types, it is inside every numeric range a naive check would use,
/// and it multiplies a GOSI deduction by a hundred. It cannot be disambiguated from the value
/// alone, so it must be refused, not interpreted.</para>
///
/// <para><b>2. The two stores agree.</b> <c>gosi_contribution_rules</c> (GOSI preview, readiness
/// report) and <c>statutory_rules</c> (payslip, GOSI filing) both hold the KSA GOSI rates. They
/// now use one unit, but they are still two rows. This walks the bridge between them.</para>
///
/// <para><b>3. A canonical package computes a number a human recognises.</b> The unit slip this
/// file exists to prevent does not produce a subtly wrong figure; it produces one that is off by
/// exactly 100×. Pinning the arithmetic for one ordinary Saudi salary is what makes that visible
/// in a failure message rather than in a remittance.</para>
/// </summary>
public class StatutoryRateUnitTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly DateOnly Period = new(2026, 1, 31);

    // ── 1. The mistyped percentage, refused at every write path ──────────────────────────────

    [Fact]
    public async Task StatutoryRulesApi_RefusesARateTypedAsAPercentage()
    {
        using var db = MakeDb();
        Seed(db, "gosi.saudi_employee_rate", "0.09");
        await db.SaveChangesAsync();

        var ctrl = StatutoryRulesControllerFor(db);

        // "9" meaning 9%. The stored platform default beside it is "0.09".
        var result = await ctrl.Create(new CreateStatutoryRuleRequest(
            CountryCodes.Saudi, Jurisdictions.KsaMainland, "gosi.saudi_employee_rate",
            "9", "decimal", "Annual GOSI circular update", new DateTime(2026, 1, 1), null),
            CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        var message = Assert.IsType<string>(bad.Value);

        // The refusal has to name the FORM, not merely say "invalid".
        Assert.Contains("FRACTION", message, StringComparison.Ordinal);
        Assert.Contains("0.09", message, StringComparison.Ordinal);
        Assert.Contains("Nothing has been saved", message, StringComparison.Ordinal);

        // And nothing may have been written.
        Assert.Empty(await db.StatutoryRules.IgnoreQueryFilters()
            .Where(r => r.TenantId == TenantId).ToListAsync());
    }

    [Fact]
    public async Task StatutoryRulesApi_RefusesAPercentageOnSupersede()
    {
        using var db = MakeDb();
        var prior = Seed(db, "gosi.saudi_employee_rate", "0.09", tenantId: TenantId);
        await db.SaveChangesAsync();

        var ctrl = StatutoryRulesControllerFor(db);
        var result = await ctrl.Update(prior.Id, new UpdateStatutoryRuleRequest(
            "9.75", "2026 circular", new DateTime(2026, 7, 1), null), CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("0.0975", Assert.IsType<string>(bad.Value), StringComparison.Ordinal);

        // The prior row must be untouched — a refused supersede closes nothing.
        Assert.Null((await db.StatutoryRules.FindAsync(prior.Id))!.EffectiveTo);
    }

    [Fact]
    public async Task StatutoryRulesApi_AcceptsTheSameRateWrittenAsAFraction()
    {
        using var db = MakeDb();
        Seed(db, "gosi.saudi_employee_rate", "0.09");
        await db.SaveChangesAsync();

        var result = await StatutoryRulesControllerFor(db).Create(new CreateStatutoryRuleRequest(
            CountryCodes.Saudi, Jurisdictions.KsaMainland, "gosi.saudi_employee_rate",
            "0.0975", "decimal", "2026 circular", new DateTime(2026, 1, 1), null),
            CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(result.Result);
        var stored = Assert.Single(await db.StatutoryRules.IgnoreQueryFilters()
            .Where(r => r.TenantId == TenantId).ToListAsync());
        Assert.Equal("0.0975", stored.RuleValue);
    }

    [Theory]
    // The percent spelling of each seeded KSA GOSI rate. Every one of these is a 100× error.
    [InlineData("gosi.saudi_employee_rate", "9")]
    [InlineData("gosi.saudi_employer_rate", "9.75")]
    [InlineData("gosi.saned_rate", "0.75")]
    [InlineData("gosi.expat_occupational_hazard_rate", "2")]
    [InlineData("gpssa.national_employee_rate", "5")]
    [InlineData("grsia.national_employer_rate", "14")]
    public void UnitRegistry_RefusesEveryContributionRateWrittenAsAPercentage(string key, string percent)
    {
        var error = StatutoryValueUnits.Validate(key, "decimal", percent);
        Assert.NotNull(error);
        Assert.Contains("FRACTION", error, StringComparison.Ordinal);
    }

    [Fact]
    public void UnitRegistry_RefusesAValueItCannotType()
    {
        // "9%" is the operator telling us they do not know which unit the store uses.
        Assert.NotNull(StatutoryValueUnits.Validate("gosi.saudi_employee_rate", "decimal", "9%"));
        // A thousands separator is not a decimal in the invariant grammar this store parses with.
        Assert.NotNull(StatutoryValueUnits.Validate("gosi.covered_wage_ceiling_sar", "decimal", "45,000"));
        // Beyond numeric(9,6).
        Assert.NotNull(StatutoryValueUnits.Validate("gosi.saned_rate", "decimal", "0.00750001"));
        // Negative.
        Assert.NotNull(StatutoryValueUnits.Validate("gosi.saned_rate", "decimal", "-0.0075"));
    }

    [Fact]
    public void UnitRegistry_RefusalNamesTheValueThatWasProbablyMeant_AndStillRefuses()
    {
        var error = StatutoryValueUnits.Validate("gosi.saudi_employee_rate", "decimal", "9");
        Assert.NotNull(error);
        // It says what 9% would be…
        Assert.Contains("0.09", error, StringComparison.Ordinal);
        // …and that it did not apply it. Naming the guess and taking it are different things.
        Assert.Contains("Nothing has been saved", error, StringComparison.Ordinal);
    }

    [Fact]
    public void UnitRegistry_AcceptsEverySeededStatutoryValue()
    {
        // The registry must not be stricter than the product's own reference data. If a seeded row
        // would be refused, the bounds are wrong, not the seeder.
        var refused = StatutoryRuleSeeder.BuildRules()
            .Select(r => (r.RuleKey, r.RuleValue, Error: StatutoryValueUnits.Validate(r.RuleKey, r.DataType, r.RuleValue)))
            .Where(x => x.Error is not null)
            .Select(x => $"{x.RuleKey} = '{x.RuleValue}': {x.Error}")
            .ToList();

        Assert.True(refused.Count == 0,
            "StatutoryValueUnits refuses values StatutoryRuleSeeder writes: " + string.Join(" | ", refused));
    }

    [Fact]
    public async Task GosiContributionRuleApi_RefusesARateTypedAsAPercentage()
    {
        using var db = MakeDb();
        var ctrl = GosiControllerFor(db);

        var result = await ctrl.CreateContributionRule(new CreateGosiRuleRequest(
            Classification: GosiClassifications.Saudi,
            Branch: GosiBranches.Annuities,
            Payer: GosiPayers.Employee,
            Rate: 9m,
            EffectiveFrom: new DateOnly(2026, 1, 1),
            SourceReference: "2026 GOSI circular"), CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var message = bad.Value!.GetType().GetProperty("error")!.GetValue(bad.Value) as string;
        Assert.NotNull(message);
        Assert.Contains("FRACTION", message, StringComparison.Ordinal);
        Assert.Contains("0.09", message, StringComparison.Ordinal);
        Assert.Empty(await db.GosiContributionRules.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task GosiContributionRuleApi_AcceptsTheFraction()
    {
        using var db = MakeDb();
        var result = await GosiControllerFor(db).CreateContributionRule(new CreateGosiRuleRequest(
            GosiClassifications.Saudi, GosiBranches.Annuities, GosiPayers.Employee,
            0.0975m, new DateOnly(2026, 1, 1), SourceReference: "2026 GOSI circular"),
            CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(result);
        Assert.Equal(0.0975m, (await db.GosiContributionRules.IgnoreQueryFilters().SingleAsync()).Rate);
    }

    [Fact]
    public void GosiBranchRateGuard_RefusesTheOldPercentSpellingOfSaned()
    {
        // 0.75 is inside a naive 0-to-1 "fraction" check and is the exact value the column held
        // before the conversion. Read as a fraction it deducts three quarters of the covered wage.
        var error = StatutoryValueUnits.ValidateGosiBranchRate(0.75m, GosiBranches.SANED, GosiPayers.Employee);
        Assert.NotNull(error);
        Assert.Contains("0.0075", error, StringComparison.Ordinal);
    }

    // ── 2. The two stores hold one fact ──────────────────────────────────────────────────────

    [Fact]
    public void BothStores_AgreeOnEverySeededKsaGosiRate()
    {
        // gosi_contribution_rules is read by the GOSI preview and the readiness report;
        // statutory_rules is read by the payslip and the GOSI filing. One statutory fact, two
        // rows. This is what fails when someone updates one seeder for a new circular.
        var problems = GosiRuleSeeder.VerifyStoresAgree();

        Assert.True(problems.Count == 0,
            "The two GOSI rate stores disagree: " + string.Join(" | ", problems));
    }

    [Fact]
    public void BothStores_ExpressTheirRatesInTheSameUnit()
    {
        // Stated as a property rather than as three literals, so a legitimate rate change does not
        // have to be made in this file too: every seeded contribution rate, in EITHER store, must
        // be admissible as a fraction.
        foreach (var rule in DefaultGosiRules())
            Assert.Null(StatutoryValueUnits.ValidateGosiBranchRate(rule.Rate, rule.Branch, rule.Payer));

        foreach (var key in GosiRuleSeeder.StatutoryRuleKeyFor.Values.Distinct())
        {
            var row = StatutoryRuleSeeder.BuildRules().First(r =>
                r.CountryCode == CountryCodes.Saudi && r.Jurisdiction == Jurisdictions.KsaMainland && r.RuleKey == key);
            Assert.Null(StatutoryValueUnits.Validate(row.RuleKey, row.DataType, row.RuleValue));
        }
    }

    // ── 3. The canonical package ─────────────────────────────────────────────────────────────

    // An ordinary Saudi salary: SAR 10,000 basic + SAR 2,500 housing. The GOSI contributory wage
    // is basic + housing = SAR 12,500, below the SAR 45,000 ceiling, so nothing is clamped.
    //
    //   Employee  annuities 9%     of 12,500 = 1,125.00
    //             SANED     0.75%  of 12,500 =    93.75   → 1,218.75
    //   Employer  annuities 9%     of 12,500 = 1,125.00
    //             SANED     0.75%  of 12,500 =    93.75
    //             hazards   2%     of 12,500 =   250.00   → 1,468.75
    //
    // A unit slip does not move these a little. Reading the fraction as a percent gives SAR 12.19;
    // reading a percent as a fraction gives SAR 121,875 — more than the salary. Either way the
    // failure message carries a number a payroll officer recognises on sight.
    private const decimal CanonicalBasic = 10_000m;
    private const decimal CanonicalHousing = 2_500m;
    private const decimal CanonicalCoveredWage = 12_500m;
    private const decimal CanonicalEmployeeGosi = 1_218.75m;
    private const decimal CanonicalEmployerGosi = 1_468.75m;

    [Fact]
    public void CanonicalKsaPackage_GosiPreviewStore_ComputesTheKnownContribution()
    {
        var result = GosiCalculationService.Calculate(
            "Saudi", CanonicalCoveredWage, DefaultGosiRules(), Period, TenantId,
            new GosiWageBounds(null, KsaGosiWageBounds.DefaultMonthlyCeilingSar));

        Assert.Equal(CanonicalEmployeeGosi, result.EmployeeTotal);
        Assert.Equal(CanonicalEmployerGosi, result.EmployerTotal);

        // And the individual branch a human would check first.
        var annuities = result.Lines.Single(l => l.Branch == GosiBranches.Annuities && l.Payer == GosiPayers.Employee);
        Assert.Equal(1_125.00m, annuities.Amount);
        Assert.Equal(0.09m, annuities.Rate);   // a FRACTION on the line as well as in the column
    }

    [Fact]
    public async Task CanonicalKsaPackage_PayslipStore_ComputesTheSameContribution()
    {
        // The other store, through the calculator the payroll run actually uses. The two engines
        // reaching the same figure from different tables is the whole point of one unit.
        var calc = new KsaDeductionCalculator(new StubRuleReader()
            .Set("gosi.saudi_employee_rate", 0.09m)
            .Set("gosi.saudi_employer_rate", 0.09m)
            .Set("gosi.saned_rate", 0.0075m)
            .Set("gosi.expat_occupational_hazard_rate", 0.02m)
            .Set(KsaGosiWageBounds.CeilingRuleKey, 45_000m));

        var result = await calc.CalculateAsync(new StatutoryDeductionInput(
            Guid.NewGuid(), Guid.NewGuid(),
            new SalaryBreakdown(CanonicalBasic, CanonicalHousing, 1_000m, 0m),
            "Saudi", "Indefinite", 2026, 1));

        Assert.Equal(CanonicalEmployeeGosi, result.TotalEmployeeDeduction);
        Assert.Equal(CanonicalEmployerGosi, result.TotalEmployerContribution);
    }

    [Fact]
    public async Task CanonicalKsaPackage_BothEnginesReachTheSameNumberFromTheSeededData()
    {
        // Neither side's figures are hand-fed here: one comes from GosiRuleSeeder, the other from
        // StatutoryRuleSeeder, exactly as a fresh deployment would hold them.
        using var db = MakeDb();
        await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);
        var seededRules = await db.GosiContributionRules.IgnoreQueryFilters().AsNoTracking().ToListAsync();

        var viaPreviewStore = GosiCalculationService.Calculate(
            "Saudi", CanonicalCoveredWage, seededRules, Period, TenantId,
            new GosiWageBounds(null, KsaGosiWageBounds.DefaultMonthlyCeilingSar));

        var statutory = new StubRuleReader();
        foreach (var r in StatutoryRuleSeeder.BuildRules()
                     .Where(r => r.CountryCode == CountryCodes.Saudi
                              && r.Jurisdiction == Jurisdictions.KsaMainland
                              && r.DataType == "decimal"
                              && decimal.TryParse(r.RuleValue, System.Globalization.NumberStyles.Any,
                                     System.Globalization.CultureInfo.InvariantCulture, out _)))
            statutory.Set(r.RuleKey, decimal.Parse(r.RuleValue, System.Globalization.CultureInfo.InvariantCulture));

        var viaPayslipStore = await new KsaDeductionCalculator(statutory).CalculateAsync(
            new StatutoryDeductionInput(Guid.NewGuid(), Guid.NewGuid(),
                new SalaryBreakdown(CanonicalBasic, CanonicalHousing, 0m, 0m),
                "Saudi", "Indefinite", 2026, 1));

        Assert.Equal(CanonicalEmployeeGosi, viaPreviewStore.EmployeeTotal);
        Assert.Equal(CanonicalEmployeeGosi, viaPayslipStore.TotalEmployeeDeduction);
        Assert.Equal(CanonicalEmployerGosi, viaPreviewStore.EmployerTotal);
        Assert.Equal(CanonicalEmployerGosi, viaPayslipStore.TotalEmployerContribution);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    private static ZayraDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static List<GosiContributionRule> DefaultGosiRules()
    {
        using var db = MakeDb();
        GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance).GetAwaiter().GetResult();
        return db.GosiContributionRules.IgnoreQueryFilters().AsNoTracking().ToList();
    }

    private static StatutoryRule Seed(ZayraDbContext db, string ruleKey, string value, Guid? tenantId = null)
    {
        var rule = new StatutoryRule
        {
            TenantId = tenantId,
            CountryCode = CountryCodes.Saudi,
            Jurisdiction = Jurisdictions.KsaMainland,
            RuleKey = ruleKey,
            RuleValue = value,
            DataType = "decimal",
            Description = "seeded",
            EffectiveFrom = new DateTime(2016, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.StatutoryRules.Add(rule);
        return rule;
    }

    private static ClaimsPrincipal Principal() => new(new ClaimsIdentity(new[]
    {
        new Claim("tenant_id", TenantId.ToString()),
        new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        new Claim(ClaimTypes.Role, "Admin"),
        new Claim("permission", "payroll.rates.statutory_override"),
        new Claim("permission", "payroll.manage"),
    }, "Test"));

    private static StatutoryRulesController StatutoryRulesControllerFor(ZayraDbContext db) =>
        new(db) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Principal() } } };

    private static GosiController GosiControllerFor(ZayraDbContext db) =>
        new(db, TestReconciliation.For(db), TestReconciliation.KsaRuleReader())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Principal() } },
        };
}
