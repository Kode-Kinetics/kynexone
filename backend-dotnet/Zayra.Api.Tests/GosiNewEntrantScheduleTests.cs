using FluentAssertions;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// GOSI new-entrant annuities schedule — New Social Insurance Law (Royal Decree M/273; Council of Ministers
/// Resolution 1022, 26/12/1445H), in force 3 July 2024, for a Saudi first registered with GOSI on or after
/// that date: 9% → 9.5% (Jul 2025) → 10% (Jul 2026) → 10.5% (Jul 2027) → 11% (Jul 2028), EACH of employee
/// and employer, applied from the July contribution month. Existing subscribers stay on 9% / 9%.
///
/// <para>Every figure here is computed from the rows <see cref="StatutoryRuleSeeder"/> actually seeds, read
/// with the same effective-date rule as <c>StatutoryRuleReader</c> (newest platform row whose EffectiveFrom
/// is on or before the period and whose EffectiveTo is after it).</para>
/// </summary>
public class GosiNewEntrantScheduleTests
{
    private static readonly DateOnly RegisteredAug2024 = new(2024, 8, 1);

    private static StatutoryDeductionInput Saudi(int year, int month, DateOnly? firstRegisteredOn,
        decimal basic = 10_000m, decimal housing = 3_000m, string nationality = "Saudi") =>
        new(Guid.Empty, Guid.Empty, new SalaryBreakdown(basic, housing, 0m, 0m), nationality, "Indefinite", year, month)
        {
            SocialInsuranceFirstRegisteredOn = firstRegisteredOn,
        };

    private static StatutoryDeductionLine Line(StatutoryDeductionResult r, string code) => r.Lines.Single(l => l.Code == code);

    // ── The ladder, on the seeded rows ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(2024, 7, "0.09")]
    [InlineData(2025, 6, "0.09")]
    [InlineData(2025, 7, "0.095")]
    [InlineData(2026, 6, "0.095")]
    [InlineData(2026, 7, "0.10")]
    [InlineData(2027, 7, "0.105")]
    [InlineData(2028, 6, "0.105")]
    [InlineData(2028, 7, "0.11")]
    [InlineData(2031, 1, "0.11")]
    public async Task NewEntrant_RegisteredAug2024_PaysTheStepInForceForThePayrollMonth_EachSide(int year, int month, string rateText)
    {
        var rate = decimal.Parse(rateText, System.Globalization.CultureInfo.InvariantCulture);
        var r = await new KsaDeductionCalculator(SeededRules.Instance).CalculateAsync(Saudi(year, month, RegisteredAug2024));

        r.SocialInsuranceCohort.Should().Be(GosiCohorts.NewEntrant);
        Line(r, "GOSI-ANN-EE").Rate.Should().Be(rate);
        Line(r, "GOSI-ANN-ER").Rate.Should().Be(rate, "the employer share steps exactly as the employee share");
        Line(r, "GOSI-ANN-EE").EmployeeAmount.Should().Be(Math.Round(13_000m * rate, 2));
        Line(r, "GOSI-ANN-ER").EmployerAmount.Should().Be(Math.Round(13_000m * rate, 2));

        // SANED, occupational hazards and the basic + housing base are unchanged by the law.
        Line(r, "GOSI-SANED-EE").EmployeeAmount.Should().Be(97.50m);
        Line(r, "GOSI-SANED-ER").EmployerAmount.Should().Be(97.50m);
        Line(r, "GOSI-OH-ER").EmployerAmount.Should().Be(260.00m);
        r.TotalEmployeeDeduction.Should().Be(Math.Round(13_000m * rate, 2) + 97.50m);

        r.Basis.Should().Contain(KsaGosiCohortSchedule.NewEntrantScheduleAppliedMarker)
            .And.Contain("Royal Decree M/273")
            .And.Contain("first registered 2024-08-01");
    }

    [Theory]
    [InlineData(2025, 7)]
    [InlineData(2028, 7)]
    public async Task ExistingSubscriber_StaysOnNinePercent_WhateverTheMonth(int year, int month)
    {
        var r = await new KsaDeductionCalculator(SeededRules.Instance).CalculateAsync(Saudi(year, month, new DateOnly(2016, 5, 10)));

        r.SocialInsuranceCohort.Should().Be(GosiCohorts.PreJuly2024);
        Line(r, "GOSI-ANN-EE").Rate.Should().Be(0.09m);
        Line(r, "GOSI-ANN-ER").Rate.Should().Be(0.09m);
        r.TotalEmployeeDeduction.Should().Be(1_267.50m);
    }

    [Fact]
    public async Task RegisteredTheDayBefore3July2024_IsAnExistingSubscriber_OnTheDayIsANewEntrant()
    {
        var before = await new KsaDeductionCalculator(SeededRules.Instance).CalculateAsync(Saudi(2026, 7, new DateOnly(2024, 7, 2)));
        var onDay = await new KsaDeductionCalculator(SeededRules.Instance).CalculateAsync(Saudi(2026, 7, new DateOnly(2024, 7, 3)));

        Line(before, "GOSI-ANN-EE").Rate.Should().Be(0.09m);
        Line(onDay, "GOSI-ANN-EE").Rate.Should().Be(0.10m);
    }

    [Fact]
    public async Task UnknownRegistrationDate_KeepsTheLegacyNinePercent_WithTheEnterTheDateWarning()
    {
        var r = await new KsaDeductionCalculator(SeededRules.Instance).CalculateAsync(Saudi(2028, 7, null));

        r.SocialInsuranceCohort.Should().Be(GosiCohorts.Unknown);
        Line(r, "GOSI-ANN-EE").Rate.Should().Be(0.09m, "a missing date is treated as the old scheme, as Oracle HCM does");
        r.Basis.Should().Contain("unverified")
            .And.Contain("Enter the GOSI registration date; if it is on or after 3 July 2024 the new-entrant rate applies");
    }

    [Fact]
    public async Task TheSar45000Ceiling_StillCapsTheNewEntrantBase()
    {
        var r = await new KsaDeductionCalculator(SeededRules.Instance).CalculateAsync(
            Saudi(2026, 7, RegisteredAug2024, basic: 50_000m, housing: 10_000m));

        Line(r, "GOSI-ANN-EE").ContributoryWage.Should().Be(45_000m);
        Line(r, "GOSI-ANN-EE").EmployeeAmount.Should().Be(4_500m, "10% of the 45,000 ceiling, not of 60,000");
        Line(r, "GOSI-ANN-ER").EmployerAmount.Should().Be(4_500m);
    }

    [Fact]
    public async Task ThePreviewAndReadinessEngine_IsThePayslipEngine_ForANewEntrant()
    {
        var payslip = await new KsaDeductionCalculator(SeededRules.Instance).CalculateAsync(Saudi(2025, 7, RegisteredAug2024));
        var preview = await GosiCalculationService.CalculateAsync(
            SeededRules.Instance, "Saudi", 10_000m, 3_000m, new DateOnly(2025, 7, 1), RegisteredAug2024);

        preview.EmployeeTotal.Should().Be(payslip.TotalEmployeeDeduction);
        preview.EmployerTotal.Should().Be(payslip.TotalEmployerContribution);
        preview.Cohort.Should().Be(GosiCohorts.NewEntrant);
        preview.Basis.Should().Be(payslip.Basis);

        var emp = new Employee { Id = 1, EmployeeCode = "E1", Nationality = "Saudi", GosiReference = "123", GosiFirstRegisteredOn = RegisteredAug2024 };
        var report = GosiReadinessValidator.Validate(emp, 10_000m, preview);
        report.IsReady.Should().BeTrue("the new-entrant schedule is modelled: readiness no longer blocks a new entrant");
        report.Warnings.Should().ContainSingle(w => w.Code == PayrollValidationEngine.GosiNewEntrantScheduleApplied)
            .Which.Message.Should().Contain("9.5% employee");
    }

    // ── The seeded rows themselves ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(RuleKeys.GosiNewEntrantEmployeeRate)]
    [InlineData(RuleKeys.GosiNewEntrantEmployerRate)]
    public void TheLadder_IsFiveEffectiveDatedPlatformRows_EachCitingTheLawAndBothSources_AndVerified(string key)
    {
        var rows = StatutoryRuleSeeder.BuildRules()
            .Where(r => r.CountryCode == CountryCodes.Saudi && r.Jurisdiction == Jurisdictions.KsaMainland && r.RuleKey == key)
            .OrderBy(r => r.EffectiveFrom)
            .ToList();

        rows.Select(r => (DateOnly.FromDateTime(r.EffectiveFrom), r.RuleValue)).Should().Equal(
            (new DateOnly(2024, 7, 1), "0.09"),
            (new DateOnly(2025, 7, 1), "0.095"),
            (new DateOnly(2026, 7, 1), "0.10"),
            (new DateOnly(2027, 7, 1), "0.105"),
            (new DateOnly(2028, 7, 1), "0.11"));
        rows.Should().OnlyContain(r => r.TenantId == null, "a GOSI rate is a platform row");
        for (var i = 0; i < rows.Count - 1; i++)
            rows[i].EffectiveTo.Should().Be(rows[i + 1].EffectiveFrom, "each step ends where the next begins");
        rows[^1].EffectiveTo.Should().BeNull();

        rows.Should().OnlyContain(r => r.Description.Contains("Royal Decree M/273")
            && r.Description.Contains("Resolution 1022")
            && r.Description.Contains("docs.oracle.com/en/cloud/saas/readiness/hcm/25b/payr-25b/25B-payroll-wn-f37393.htm")
            && r.Description.Contains("argaam.com/en/article/articledetail/id/1824657"));
        rows.Should().OnlyContain(r => StatutoryOverridePolicy.IsVerified(r.Description),
            "the Statutory Rates screen must show the new-entrant rows as verified");
        rows.Should().OnlyContain(r => StatutoryValueUnits.Validate(r.RuleKey, r.DataType, r.RuleValue) == null);
    }

    [Fact]
    public void TheNewEntrantRates_AreStatutory_SoATenantCannotOverrideThem()
    {
        GosiStatutoryValues.IsStatutory(RuleKeys.GosiNewEntrantEmployeeRate).Should().BeTrue();
        GosiStatutoryValues.IsStatutory(RuleKeys.GosiNewEntrantEmployerRate).Should().BeTrue();
        GosiStatutoryValues.IsStatutory(RuleKeys.GosiMinContributoryWageAnnuitiesSar).Should().BeTrue();
        GosiStatutoryValues.IsStatutory(RuleKeys.GosiMinContributoryWageOccupationalHazardsSar).Should().BeTrue();
    }

    [Fact]
    public void TheMinimumContributoryWageRows_AreSeeded_MarkedSecondary_AndShownUnverified()
    {
        var rows = StatutoryRuleSeeder.BuildRules()
            .Where(r => r.RuleKey is RuleKeys.GosiMinContributoryWageAnnuitiesSar or RuleKeys.GosiMinContributoryWageOccupationalHazardsSar)
            .ToDictionary(r => r.RuleKey);

        rows[RuleKeys.GosiMinContributoryWageAnnuitiesSar].RuleValue.Should().Be("1500");
        rows[RuleKeys.GosiMinContributoryWageOccupationalHazardsSar].RuleValue.Should().Be("400");
        rows.Values.Should().OnlyContain(r => r.Description.Contains("SECONDARY SOURCE") && !StatutoryOverridePolicy.IsVerified(r.Description));
        StatutoryRuleSeeder.BuildRules().Should().NotContain(r => r.RuleKey == KsaGosiWageBounds.FloorRuleKey,
            "the minimum is a warning threshold, never the clamping floor that would raise a deduction");
    }

    // ── The validator ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARunWithANewEntrantComputedOnTheSchedule_IsNoLongerBlocked_AndCarriesTheRateNotice()
    {
        var calc = await new KsaDeductionCalculator(SeededRules.Instance).CalculateAsync(Saudi(2025, 7, RegisteredAug2024));
        var w = new ValidationWorld(2025, 7);
        var e = w.Saudi(1, RegisteredAug2024, calc.Basis, calc.TotalEmployeeDeduction);

        var results = PayrollValidationEngine.Run(w.Context());

        results.Should().NotContain(r => r.Severity == "Error");
        results.Should().NotContain(r => r.Code == PayrollValidationEngine.GosiNewEntrantScheduleNotModelled);
        var notice = results.Should().ContainSingle(r => r.Code == PayrollValidationEngine.GosiNewEntrantScheduleApplied).Subject;
        notice.Severity.Should().Be("Warning");
        notice.EmployeeId.Should().Be(e.Id);
        notice.Message.Should().Contain("Royal Decree M/273").And.Contain("9.5% employee / 9.5% employer").And.Contain("2024-08-01");
    }

    [Fact]
    public void ASlipProcessedBeforeTheScheduleExisted_StillBlocks_AndSaysReprocess()
    {
        // The exact basis the previous code froze on a new entrant's slip: 9%, "NOT MODELLED".
        const string staleBasis = "GOSI cohort: new entrant (first registered 2024-08-01, on or after 3 July 2024). Rate basis: " +
            "NOT MODELLED — the new-entrant schedule is not in this product, so the pre-3-July-2024 schedule was applied " +
            "(annuities 9% employee / 9% employer, SANED 0.75% each side, occupational hazards 2% employer, for 2025-07) and " +
            "is wrong for this person. Payroll validation blocks approval of a run that computes GOSI for them.";
        var w = new ValidationWorld(2025, 7);
        var e = w.Saudi(1, RegisteredAug2024, staleBasis, 1_267.50m);

        var block = PayrollValidationEngine.Run(w.Context())
            .Should().ContainSingle(r => r.Code == PayrollValidationEngine.GosiNewEntrantScheduleNotModelled).Subject;
        block.Severity.Should().Be("Error");
        block.EmployeeId.Should().Be(e.Id);
        block.Message.Should().Contain("re-process");
        PayrollValidationOverridePolicy.IsOverridable(PayrollValidationEngine.GosiNewEntrantScheduleNotModelled).Should().BeFalse();
    }

    [Fact]
    public void BelowTheMinimumContributoryWage_IsAWarning_NeverAnError()
    {
        var w = new ValidationWorld(2026, 7) { MinAnnuities = 1_500m, MinOh = 400m };
        var saudi = w.Saudi(1, new DateOnly(2016, 5, 10), basis: null, gosiEe: 117m, basic: 1_000m, housing: 200m);
        var expat = w.Expat(2, basic: 300m, housing: 50m);
        w.Saudi(3, new DateOnly(2016, 5, 10), basis: null, gosiEe: 195m, basic: 1_600m, housing: 400m);

        var results = PayrollValidationEngine.Run(w.Context());
        var mins = results.Where(r => r.Code == PayrollValidationEngine.GosiBelowMinimumContributoryWage).ToList();

        mins.Select(r => r.EmployeeId).Should().BeEquivalentTo(new int?[] { saudi.Id, expat.Id }, "2,000 SAR clears both minimums");
        mins.Should().OnlyContain(r => r.Severity == "Warning" && r.Message.Contains("GOSI may assess on the minimum"));
        mins.Single(r => r.EmployeeId == saudi.Id).Message.Should().Contain("SAR 1,500 for annuities");
        mins.Single(r => r.EmployeeId == expat.Id).Message.Should().Contain("SAR 400 for occupational hazards")
            .And.NotContain("annuities", "an expatriate pays no annuities");
        results.Should().NotContain(r => r.Severity == "Error" && (r.EmployeeId == saudi.Id || r.EmployeeId == expat.Id)
            && r.Code == PayrollValidationEngine.GosiBelowMinimumContributoryWage);
        PayrollValidationOverridePolicy.NonOverridable.Should().NotContain(PayrollValidationEngine.GosiBelowMinimumContributoryWage);
    }

    [Fact]
    public void WithNoMinimumOnRecord_TheMinimumCheckIsOff()
    {
        var w = new ValidationWorld(2026, 7);
        w.Saudi(1, new DateOnly(2016, 5, 10), basis: null, gosiEe: 117m, basic: 1_000m, housing: 200m);

        PayrollValidationEngine.Run(w.Context())
            .Should().NotContain(r => r.Code == PayrollValidationEngine.GosiBelowMinimumContributoryWage);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The seeded platform rows, resolved the way StatutoryRuleReader resolves platform rows.</summary>
    internal sealed class SeededRules : IStatutoryRuleReader
    {
        public static readonly SeededRules Instance = new();
        private readonly List<StatutoryRule> _rows = StatutoryRuleSeeder.BuildRules();

        public Task<decimal?> GetDecimalAsync(string countryCode, string jurisdiction, string ruleKey,
            DateOnly effectiveDate, Guid? tenantId = null, CancellationToken ct = default)
        {
            var raw = Find(countryCode, jurisdiction, ruleKey, effectiveDate);
            return Task.FromResult(raw is not null && decimal.TryParse(raw, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? (decimal?)v : null);
        }

        public Task<string?> GetStringAsync(string countryCode, string jurisdiction, string ruleKey,
            DateOnly effectiveDate, Guid? tenantId = null, CancellationToken ct = default)
            => Task.FromResult(Find(countryCode, jurisdiction, ruleKey, effectiveDate));

        private string? Find(string cc, string jur, string key, DateOnly on)
        {
            var cutoff = on.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            return _rows
                .Where(r => r.TenantId == null && r.CountryCode == cc && r.Jurisdiction == jur && r.RuleKey == key
                         && r.EffectiveFrom <= cutoff && (r.EffectiveTo == null || r.EffectiveTo > cutoff))
                .OrderByDescending(r => r.EffectiveFrom)
                .FirstOrDefault()?.RuleValue;
        }
    }

    private sealed class ValidationWorld
    {
        private readonly Guid _tenantId = Guid.NewGuid();
        private readonly PayrollRun _run;
        private readonly Company _company;
        private readonly List<PayrollSlip> _slips = new();
        private readonly List<Employee> _employees = new();
        private readonly List<EmployeeSalaryStructure> _salaries = new();
        private readonly List<EmployeePayrollProfile> _profiles = new();
        private readonly List<PayrollDeduction> _deductions = new();
        public decimal MinAnnuities { get; init; }
        public decimal MinOh { get; init; }

        public ValidationWorld(int year, int month)
        {
            _company = new Company { Id = Guid.NewGuid(), TenantId = _tenantId, LegalNameEn = "Entrant Co", CountryCode = "SAU", DefaultCurrency = "SAR", IsActive = true };
            _run = new PayrollRun { Id = Guid.NewGuid(), TenantId = _tenantId, Year = year, Month = month, Status = "Processed", CompanyId = _company.Id, IncludesRecurringPay = true };
        }

        public Employee Saudi(int id, DateOnly? firstRegisteredOn, string? basis, decimal gosiEe, decimal basic = 10_000m, decimal housing = 3_000m)
        {
            var e = Add(id, "Saudi", gosiEe, basic, housing);
            e.GosiFirstRegisteredOn = firstRegisteredOn;
            _slips[^1].GosiCohort = GosiCohorts.Resolve(firstRegisteredOn);
            _slips[^1].StatutoryBasis = basis;
            return e;
        }

        public Employee Expat(int id, decimal basic, decimal housing) => Add(id, "Indian", 0m, basic, housing);

        private Employee Add(int id, string nationality, decimal gosiEe, decimal basic, decimal housing)
        {
            var e = new Employee { Id = id, TenantId = _tenantId, EmployeeCode = $"EMP{id:D3}", FullName = $"Employee {id}", Nationality = nationality, Status = "Active" };
            _employees.Add(e);
            var gross = basic + housing;
            _slips.Add(new PayrollSlip
            {
                Id = Guid.NewGuid(), TenantId = _tenantId, RunId = _run.Id, EmployeeId = id, EmployeeCode = e.EmployeeCode, EmployeeName = e.FullName,
                BasicSalary = basic, HousingAllowance = housing, GrossSalary = gross, Deductions = gosiEe, NetSalary = gross - gosiEe,
            });
            _salaries.Add(new EmployeeSalaryStructure { Id = Guid.NewGuid(), TenantId = _tenantId, EmployeeId = id, SalaryStructureId = Guid.NewGuid(), BasicSalary = basic, IsActive = true });
            _profiles.Add(new EmployeePayrollProfile { Id = Guid.NewGuid(), TenantId = _tenantId, EmployeeId = id, Iban = "SA4420000001234567891234", MolId = "MOL123456", SalaryCurrency = "SAR" });
            if (gosiEe > 0m)
                _deductions.Add(new PayrollDeduction
                {
                    Id = Guid.NewGuid(), TenantId = _tenantId, PayrollRunId = _run.Id, EmployeeId = id,
                    ComponentCode = "GOSI-ANN-EE", ComponentName = "GOSI Annuities (Employee)", Amount = gosiEe, Source = "Statutory",
                });
            return e;
        }

        public PayrollValidationContext Context()
        {
            _run.TotalGrossSalary = _slips.Sum(s => s.GrossSalary);
            _run.TotalDeductions = _slips.Sum(s => s.Deductions);
            _run.TotalNetSalary = _slips.Sum(s => s.NetSalary);
            return new PayrollValidationContext(_run, _slips, _employees, _salaries, _profiles, _deductions, Array.Empty<PayrollEarning>(), _company)
            {
                GosiMinContributoryWageAnnuities = MinAnnuities,
                GosiMinContributoryWageOccupationalHazards = MinOh,
            };
        }
    }
}
