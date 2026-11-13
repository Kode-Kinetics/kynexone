using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The GOSI half-up rounding rule takes effect from its go-live month, not from 2016. A month already processed under banker's rounding must
/// not be recomputed differently if it is reopened or previewed, so the seeded rule is HALF_EVEN from 2016 and HALF_UP from
/// <see cref="GosiRounding.HalfUpEffectiveFrom"/> (the first month after the release that introduced it).
///
/// Covered wage 6,182 x SANED 0.75% = 46.365: 46.36 before go-live, 46.37 from it. 12,500.50 x 9% = 1,125.045: 1,125.04 before, 1,125.05 from it.
/// </summary>
public class GosiRoundingGoLiveTests
{
    private static async Task<(ZayraDbContext Db, SqliteConnection Conn)> SeededDbAsync()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        await StatutoryRuleSeeder.SeedAsync(db, NullLogger.Instance);
        return (db, conn);
    }

    private static Task<StatutoryDeductionResult> Calculate(ZayraDbContext db, decimal covered, int year, int month) =>
        new KsaDeductionCalculator(new StatutoryRuleReader(db)).CalculateAsync(
            new StatutoryDeductionInput(Guid.Empty, Guid.Empty, new SalaryBreakdown(covered, 0m, 0m, 0m), "Saudi", "Indefinite", year, month)
            { SocialInsuranceFirstRegisteredOn = new DateOnly(2016, 5, 10) });

    private static decimal Line(StatutoryDeductionResult r, string code) =>
        code.EndsWith("-EE", StringComparison.Ordinal) ? r.Lines.Single(x => x.Code == code).EmployeeAmount : r.Lines.Single(x => x.Code == code).EmployerAmount;

    [Fact]
    public void GoLive_IsTheFirstDayOfTheMonthAfterTheRelease()
    {
        GosiRounding.HalfUpEffectiveFrom.Should().Be(new DateOnly(2026, 12, 1));
    }

    [Fact]
    public void TheSeededRule_IsHalfEvenFrom2016_AndHalfUpFromGoLive()
    {
        var rows = StatutoryRuleSeeder.BuildRules().Where(r => r.RuleKey == GosiRounding.RuleKey).OrderBy(r => r.EffectiveFrom).ToList();
        rows.Should().HaveCount(2);
        rows[0].RuleValue.Should().Be("HALF_EVEN");
        DateOnly.FromDateTime(rows[0].EffectiveFrom).Should().Be(new DateOnly(2016, 6, 1));
        rows[1].RuleValue.Should().Be("HALF_UP");
        DateOnly.FromDateTime(rows[1].EffectiveFrom).Should().Be(GosiRounding.HalfUpEffectiveFrom);
        rows.Should().OnlyContain(r => r.TenantId == null && r.CountryCode == CountryCodes.Saudi);
    }

    [Fact]
    public async Task SANED_6182_RoundsHalfEvenBeforeGoLive_AndHalfUpFromIt()
    {
        var (db, conn) = await SeededDbAsync();
        await using var _ = conn; await using var __ = db;

        Line(await Calculate(db, 6_182m, 2026, 10), "GOSI-SANED-EE").Should().Be(46.36m, "before go-live: banker's, as the month was always processed");
        Line(await Calculate(db, 6_182m, 2026, 11), "GOSI-SANED-EE").Should().Be(46.36m);
        Line(await Calculate(db, 6_182m, 2026, 12), "GOSI-SANED-EE").Should().Be(46.37m, "from go-live: half-up");
        Line(await Calculate(db, 6_182m, 2026, 12), "GOSI-SANED-ER").Should().Be(46.37m);
    }

    [Fact]
    public async Task Annuities_12500_50_RoundHalfEvenBeforeGoLive_AndHalfUpFromIt()
    {
        var (db, conn) = await SeededDbAsync();
        await using var _ = conn; await using var __ = db;

        Line(await Calculate(db, 12_500.50m, 2026, 10), "GOSI-ANN-EE").Should().Be(1_125.04m);
        Line(await Calculate(db, 12_500.50m, 2026, 12), "GOSI-ANN-EE").Should().Be(1_125.05m);
        Line(await Calculate(db, 12_500.50m, 2027, 3), "GOSI-ANN-ER").Should().Be(1_125.05m);
    }

    [Fact]
    public void ThePreviewArithmetic_ReadsTheRuleToo_NotAFixedMode()
    {
        // The explicit-rule-list overload used to round AwayFromZero whatever the period. Without a supplied mode it now follows the same
        // go-live date; a caller that resolved the rule passes it.
        var from = new DateOnly(2016, 6, 1);
        GosiContributionRule R(string branch, string payer, decimal rate) => new()
        {
            Id = Guid.NewGuid(), TenantId = Guid.Empty, CountryCode = "SA", Classification = GosiClassifications.Saudi, Branch = branch,
            Payer = payer, Rate = rate, EffectiveFrom = from, IsActive = true,
        };
        var rules = new List<GosiContributionRule>
        {
            R(GosiBranches.SANED, GosiPayers.Employee, 0.0075m), R(GosiBranches.SANED, GosiPayers.Employer, 0.0075m),
        };
        var bounds = new GosiWageBounds(null, KsaGosiWageBounds.DefaultMonthlyCeilingSar);

        var before = GosiCalculationService.Calculate("Saudi", 6_182m, rules, new DateOnly(2026, 10, 1), Guid.Empty, bounds);
        var after = GosiCalculationService.Calculate("Saudi", 6_182m, rules, new DateOnly(2026, 12, 1), Guid.Empty, bounds);
        before.Lines.Single(l => l.Branch == GosiBranches.SANED && l.Payer == GosiPayers.Employee).Amount.Should().Be(46.36m);
        after.Lines.Single(l => l.Branch == GosiBranches.SANED && l.Payer == GosiPayers.Employee).Amount.Should().Be(46.37m);

        var explicitMode = GosiCalculationService.Calculate("Saudi", 6_182m, rules, new DateOnly(2026, 10, 1), Guid.Empty, bounds, MidpointRounding.AwayFromZero);
        explicitMode.Lines.Single(l => l.Branch == GosiBranches.SANED && l.Payer == GosiPayers.Employee).Amount.Should().Be(46.37m);
    }
}
