using FluentAssertions;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;

namespace Zayra.Api.Tests;

/// <summary>
/// F02 — the KSA pack looks the Saudi annuities rates up by COHORT and period, and every result says which
/// cohort and rate basis produced it. These tests run on a reader WITHOUT the new-entrant rows: no rate is
/// invented in code, so a new entrant with no row for the period is computed on the pre-3-July-2024 schedule
/// and reported as NOT MODELLED (fail closed). The seeded new-entrant schedule is pinned in
/// <see cref="GosiNewEntrantScheduleTests"/>.
/// </summary>
public class GosiEntrantCohortCalculatorTests
{
    private static StubRuleReader Rules() => new StubRuleReader()
        .Set("gosi.saudi_employee_rate", 0.09m)
        .Set("gosi.saudi_employer_rate", 0.09m)
        .Set("gosi.saned_rate", 0.0075m)
        .Set("gosi.expat_occupational_hazard_rate", 0.02m)
        .Set("gosi.covered_wage_ceiling_sar", 45_000m);

    private static StatutoryDeductionInput Saudi(DateOnly? firstRegisteredOn, string nationality = "Saudi") =>
        new(Guid.Empty, Guid.Empty, new SalaryBreakdown(10_000m, 3_000m, 0m, 0m), nationality, "Indefinite", 2026, 9)
        {
            SocialInsuranceFirstRegisteredOn = firstRegisteredOn,
        };

    [Fact]
    public async Task UnknownCohort_IsComputedOnThePreReformSchedule_AndSaysItIsUnverified()
    {
        var r = await new KsaDeductionCalculator(Rules()).CalculateAsync(Saudi(null));

        r.SocialInsuranceCohort.Should().Be(GosiCohorts.Unknown, "null is unknown — never a new entrant, never confirmed");
        r.Basis.Should().Contain("unverified").And.Contain("ASSUMED").And.Contain("first-registration date");
        r.TotalEmployeeDeduction.Should().Be(1_267.50m); // 13,000 × 9.75% — the figure the pack has always produced
    }

    [Fact]
    public async Task ExistingSubscriber_IsComputedOnTheirOwnSchedule_AndTheBasisNamesTheRates()
    {
        var r = await new KsaDeductionCalculator(Rules()).CalculateAsync(Saudi(new DateOnly(2016, 5, 10)));

        r.SocialInsuranceCohort.Should().Be(GosiCohorts.PreJuly2024);
        r.Basis.Should().Be(
            "GOSI cohort: existing subscriber (first registered 2016-05-10, before 3 July 2024). Rate basis: " +
            "pre-3-July-2024 schedule — annuities 9% employee / 9% employer, SANED 0.75% each side, " +
            "occupational hazards 2% employer, for 2026-09.");
    }

    [Fact]
    public async Task NewEntrant_WithNoRateOnRecord_IsReportedNotModelled_NeverSilentlyPricedAsAnExistingSubscriber()
    {
        var r = await new KsaDeductionCalculator(Rules()).CalculateAsync(Saudi(new DateOnly(2025, 2, 1)));

        r.SocialInsuranceCohort.Should().Be(GosiCohorts.NewEntrant);
        r.Basis.Should().Contain("new entrant (first registered 2025-02-01, on or after 3 July 2024)")
            .And.Contain("NOT MODELLED").And.Contain("wrong for this person");
    }

    [Fact]
    public async Task TheBasisIsGregorianIso_WhateverTheServerCulture()
    {
        // ar-SA formats dates on the Umm al-Qura calendar by default; a frozen payslip explanation must not.
        var prior = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");
            var r = await new KsaDeductionCalculator(Rules()).CalculateAsync(Saudi(new DateOnly(2016, 5, 10)));
            r.Basis.Should().Contain("first registered 2016-05-10").And.Contain("for 2026-09").And.Contain("9% employee");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = prior;
        }
    }

    [Fact]
    public async Task NoNewEntrantRateIsInvented_WhenNoStatutoryRowExists()
    {
        // The schedule lives ONLY in effective-dated statutory rows. With no row on record, nothing in code
        // supplies a rate, and the status says so (the validator blocks on it).
        (await KsaGosiCohortSchedule.NewEntrantAnnuitiesAsync(Rules(), new DateOnly(2026, 9, 1), CancellationToken.None))
            .Should().BeNull();
        (await KsaGosiCohortSchedule.ResolveAnnuitiesAsync(Rules(), GosiCohorts.NewEntrant, new DateOnly(2026, 9, 1)))
            .Status.Should().Be(GosiCohortRateStatus.NewEntrantRateNotFound);
    }

    [Fact]
    public async Task WithoutNewEntrantRows_AmountsFallBackToThePreReformFigure_AndAreFlagged()
    {
        // Without the new-entrant rows the figure is the pre-reform one for everyone (and the new entrant's
        // status is NewEntrantRateNotFound, which blocks). With the seeded rows it differs from July 2025.
        var calc = new KsaDeductionCalculator(Rules());
        var unknown = await calc.CalculateAsync(Saudi(null));
        var existing = await calc.CalculateAsync(Saudi(new DateOnly(2016, 5, 10)));
        var entrant = await calc.CalculateAsync(Saudi(new DateOnly(2025, 2, 1)));

        foreach (var r in new[] { existing, entrant })
        {
            r.TotalEmployeeDeduction.Should().Be(unknown.TotalEmployeeDeduction);
            r.TotalEmployerContribution.Should().Be(unknown.TotalEmployerContribution);
            r.Lines.Should().BeEquivalentTo(unknown.Lines);
        }
    }

    [Fact]
    public async Task AReconstruction_UsesTheFrozenCohort_NotTheDate()
    {
        // GOSI reconciliation recomputes a slip on the cohort it was PROCESSED on.
        var r = await new KsaDeductionCalculator(Rules()).CalculateAsync(
            Saudi(new DateOnly(2016, 5, 10)) with { SocialInsuranceCohort = GosiCohorts.Unknown });

        r.SocialInsuranceCohort.Should().Be(GosiCohorts.Unknown);
    }

    [Theory]
    [InlineData("Indian", "Non-Saudi")]
    [InlineData("Bahraini", "GCC national (BH)")]
    public async Task NoSaudiCohortForExpatriatesOrGccNationals_ButTheBasisIsStillExplained(string nationality, string basisStart)
    {
        var r = await new KsaDeductionCalculator(Rules()).CalculateAsync(Saudi(new DateOnly(2025, 2, 1), nationality));

        r.SocialInsuranceCohort.Should().BeNull();
        r.Basis.Should().StartWith(basisStart);
    }
}
