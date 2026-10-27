using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll;
using Xunit;

namespace Zayra.Api.Tests;

/// <summary>
/// Per-company payroll rules. A value below a statutory minimum is allowed, but only with an explicit
/// acknowledgement stored with who and when; a meaningless value is refused.
/// </summary>
public class CompanyPayrollRulesTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static readonly DateTime Now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task NoRows_MeansPlatformDefaults_SoExistingCompaniesAreUnchanged()
    {
        await using var db = CreateDb();
        var set = await CompanyPayrollRules.ResolveAsync(db, Guid.NewGuid(), Guid.NewGuid(), default);
        set.Should().Be(CompanyPayrollRuleSet.Defaults);
        set.RestDayWorkMode.Should().Be(RestDayWorkModes.PaidPremium);
        set.LeaveCashOutOnExit.Should().BeTrue();
        set.ProrationBasis.Should().BeNull("unset falls through to the existing rate-policy chain (Calendar30)");
        set.AbsenceDeductionBase.Should().Be(CompanyPayrollRules.AbsenceBasic);
        set.RestDayMultiplier.Should().BeNull("unset keeps the rule value that already applies");
        set.MaxProbationDays.Should().BeNull();
    }

    [Theory]
    [InlineData(PayrollRuleKeys.RestDayMultiplier, "1.25", CompanyPayrollRules.WarnBelowOvertimeFloor)]
    [InlineData(PayrollRuleKeys.HolidayMultiplier, "1", CompanyPayrollRules.WarnBelowOvertimeFloor)]
    [InlineData(PayrollRuleKeys.LeaveCashOutOnExit, "false", CompanyPayrollRules.WarnLeaveCashOutOff)]
    [InlineData(PayrollRuleKeys.MaxProbationDays, "120", CompanyPayrollRules.WarnProbationOver90)]
    [InlineData(PayrollRuleKeys.MaxProbationDays, "200", CompanyPayrollRules.WarnProbationOver180)]
    public async Task BelowFloorValue_IsRefusedWithoutAcknowledgement_AndSavesNothing(string key, string value, string code)
    {
        await using var db = CreateDb();
        var tenant = Guid.NewGuid(); var company = Guid.NewGuid();
        var result = await CompanyPayrollRules.SaveAsync(db, tenant, company, Guid.NewGuid(),
            new[] { new PayrollRuleChange(key, value) }, acknowledge: false, note: null, Now, default);

        result.Saved.Should().BeFalse();
        result.UnacknowledgedWarnings.Should().ContainSingle().Which.Code.Should().Be(code);
        (await db.CompanyPayrollRules.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task BelowFloorValue_WithAcknowledgement_IsSavedWithWhoAndWhen()
    {
        await using var db = CreateDb();
        var tenant = Guid.NewGuid(); var company = Guid.NewGuid(); var user = Guid.NewGuid();
        var result = await CompanyPayrollRules.SaveAsync(db, tenant, company, user,
            new[] { new PayrollRuleChange(PayrollRuleKeys.RestDayMultiplier, "1.25") },
            acknowledge: true, note: "Contract terms", Now, default,
            acknowledgedWarnings: new[] { "rest_day_multiplier:BELOW_OVERTIME_FLOOR" });

        result.Saved.Should().BeTrue();
        var row = await db.CompanyPayrollRules.SingleAsync();
        row.AcknowledgedBy.Should().Be(user);
        row.AcknowledgedAtUtc.Should().Be(Now);
        row.AcknowledgementNote.Should().Be("Contract terms");
        (await CompanyPayrollRules.ResolveAsync(db, tenant, company, default)).RestDayMultiplier.Should().Be(1.25m);
    }

    [Fact]
    public async Task MovingBackAboveTheFloor_ClearsTheAcknowledgement()
    {
        await using var db = CreateDb();
        var tenant = Guid.NewGuid(); var company = Guid.NewGuid(); var user = Guid.NewGuid();
        await CompanyPayrollRules.SaveAsync(db, tenant, company, user,
            new[] { new PayrollRuleChange(PayrollRuleKeys.RestDayMultiplier, "1.25") }, true, "Signed contract terms", Now, default,
            acknowledgedWarnings: new[] { "rest_day_multiplier:BELOW_OVERTIME_FLOOR" });
        await CompanyPayrollRules.SaveAsync(db, tenant, company, user,
            new[] { new PayrollRuleChange(PayrollRuleKeys.RestDayMultiplier, "1.5") }, false, null, Now.AddDays(1), default);

        // History is kept: the older, below-floor row stays dated; the newer row is the one in force.
        var row = await db.CompanyPayrollRules.OrderByDescending(r => r.EffectiveFrom).FirstAsync();
        row.RuleValue.Should().Be("1.5");
        row.AcknowledgedBy.Should().BeNull();
        row.AcknowledgedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task AcknowledgingWithoutAReason_IsRefused_AndSavesNothing()
    {
        await using var db = CreateDb();
        var result = await CompanyPayrollRules.SaveAsync(db, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new[] { new PayrollRuleChange(PayrollRuleKeys.LeaveCashOutOnExit, "false") }, true, " ", Now, default);
        result.Saved.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("reason");
        (await db.CompanyPayrollRules.CountAsync()).Should().Be(0);
    }

    [Fact]
    public void AnOverlongValue_IsRefusedWithAReason_NotAColumnError()
        => CompanyPayrollRules.Evaluate(PayrollRuleKeys.RestDayMultiplier, "1." + new string('0', 70) + "1").Error.Should().NotBeNull();

    [Fact]
    public async Task NullValue_ClearsTheRuleBackToDefault()
    {
        await using var db = CreateDb();
        var tenant = Guid.NewGuid(); var company = Guid.NewGuid();
        await CompanyPayrollRules.SaveAsync(db, tenant, company, null,
            new[] { new PayrollRuleChange(PayrollRuleKeys.ProrationBasis, "CalendarActual") }, false, null, Now, default);
        await CompanyPayrollRules.SaveAsync(db, tenant, company, null,
            new[] { new PayrollRuleChange(PayrollRuleKeys.ProrationBasis, null) }, false, null, Now, default);

        (await CompanyPayrollRules.ResolveAsync(db, tenant, company, default)).ProrationBasis
            .Should().BeNull();
    }

    // ── Rest-day / holiday work (audit P0-2) ────────────────────────────────────────────────────────
    // A full 8h (480 min) Friday: before the change the 480-minute baseline swallowed all of it.
    [Fact]
    public void RestDayWork_PaidPremium_EveryWorkedMinuteIsOvertime()
    {
        var r = CompanyPayrollRules.DayMinutes(480, 480, restDayOrHoliday: true, RestDayWorkModes.PaidPremium);
        r.Overtime.Should().Be(480, "was 0 before: 480 - 480 baseline");
        r.Undertime.Should().Be(0);
        r.EarnsCompensatoryDay.Should().BeFalse();
    }

    [Fact]
    public void RestDayWork_CompensatoryDay_PaysNoOvertimeAndEarnsADayOff()
    {
        var r = CompanyPayrollRules.DayMinutes(480, 480, true, RestDayWorkModes.CompensatoryDay);
        (r.Overtime, r.Undertime, r.EarnsCompensatoryDay).Should().Be((0, 0, true));
    }

    [Fact]
    public void RestDayWithNoWork_EarnsNothing()
    {
        CompanyPayrollRules.DayMinutes(0, 480, true, RestDayWorkModes.CompensatoryDay).EarnsCompensatoryDay.Should().BeFalse();
        CompanyPayrollRules.DayMinutes(0, 480, true, RestDayWorkModes.PaidPremium).Overtime.Should().Be(0);
    }

    [Theory]
    [InlineData(540, 60, 0)]   // 9h on an ordinary day: 1h overtime
    [InlineData(420, 0, 60)]   // 7h on an ordinary day: 1h short
    [InlineData(480, 0, 0)]
    public void OrdinaryDay_IsUnchanged(int worked, int ot, int under)
    {
        var r = CompanyPayrollRules.DayMinutes(worked, 480, false, RestDayWorkModes.CompensatoryDay);
        (r.Overtime, r.Undertime, r.EarnsCompensatoryDay).Should().Be((ot, under, false));
    }

    // ── Absence base (audit P2-2) ───────────────────────────────────────────────────────────────────
    [Fact]
    public void AbsenceBase_BasicByDefault_GrossWhenChosen()
    {
        // basic 10,000 + housing 2,500 + transport 1,000 + other 500 = 14,000 gross. One absent day, divisor 30.
        CompanyPayrollRules.AbsenceDeductionBaseAmount("Basic", 10_000m, 2_500m, 1_000m, 500m).Should().Be(10_000m);
        CompanyPayrollRules.AbsenceDeductionBaseAmount("Gross", 10_000m, 2_500m, 1_000m, 500m).Should().Be(14_000m);
        Math.Round(10_000m / 30m, 2).Should().Be(333.33m);
        Math.Round(14_000m / 30m, 2).Should().Be(466.67m);
    }

    [Theory]
    [InlineData(PayrollRuleKeys.RestDayMultiplier, "0.5")]
    [InlineData(PayrollRuleKeys.RestDayMultiplier, "9")]
    [InlineData(PayrollRuleKeys.MaxProbationDays, "-1")]
    [InlineData(PayrollRuleKeys.MaxProbationDays, "400")]
    [InlineData(PayrollRuleKeys.ProrationBasis, "Weekly")]
    [InlineData("not_a_rule", "x")]
    public async Task MeaninglessValue_IsRefused_EvenWithAcknowledgement(string key, string value)
    {
        await using var db = CreateDb();
        var result = await CompanyPayrollRules.SaveAsync(db, Guid.NewGuid(), Guid.NewGuid(), null,
            new[] { new PayrollRuleChange(key, value) }, acknowledge: true, null, Now, default);
        result.Saved.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public async Task AtOrAboveTheFloor_NeedsNoAcknowledgement()
    {
        await using var db = CreateDb();
        var result = await CompanyPayrollRules.SaveAsync(db, Guid.NewGuid(), Guid.NewGuid(), null, new[]
        {
            new PayrollRuleChange(PayrollRuleKeys.RestDayMultiplier, "1.5"),
            new PayrollRuleChange(PayrollRuleKeys.MaxProbationDays, "90"),
            new PayrollRuleChange(PayrollRuleKeys.LeaveCashOutOnExit, "true"),
        }, false, null, Now, default);
        result.Saved.Should().BeTrue();
    }

    [Fact]
    public async Task Rules_AreScopedToTheirCompany()
    {
        await using var db = CreateDb();
        var tenant = Guid.NewGuid(); var a = Guid.NewGuid(); var b = Guid.NewGuid();
        await CompanyPayrollRules.SaveAsync(db, tenant, a, null,
            new[] { new PayrollRuleChange(PayrollRuleKeys.AbsenceDeductionBase, "Gross") }, false, null, Now, default);
        (await CompanyPayrollRules.ResolveAsync(db, tenant, a, default)).AbsenceDeductionBase.Should().Be("Gross");
        (await CompanyPayrollRules.ResolveAsync(db, tenant, b, default)).AbsenceDeductionBase.Should().Be("Basic");
    }

    // ── One absence base for every absence-type deduction (audit M6) ────────────────────────────────
    // Basic 6,000 + housing 1,500 + transport 500 = 8,000 gross. 30-day divisor, 240 standard hours a month.

    [Theory]
    [InlineData("Basic", 400.00)]    // 2 absent days: 2 x 6,000 / 30
    [InlineData("Gross", 533.33)]    // 2 x 8,000 / 30 = 533.333...
    public void AbsentDays_ArePricedOnTheChosenBase(string choice, double expected)
    {
        var dayRate = CompanyPayrollRules.AbsenceDeductionBaseAmount(choice, 6_000m, 1_500m, 500m, 0m) / 30m;
        Math.Round(2m * dayRate, 2).Should().Be((decimal)expected);
    }

    [Theory]
    [InlineData("Basic", 37.50)]     // 90 minutes late = 1.5 h x (6,000 / 240 = 25.00)
    [InlineData("Gross", 50.00)]     // 1.5 h x (8,000 / 240 = 33.3333)
    public void LateMinutes_ArePricedOnTheSameBaseAsAbsentDays(string choice, double expected)
    {
        var hourly = CompanyPayrollRules.ShortHoursHourlyRate(choice, 6_000m, 1_500m, 500m, 0m, 240m);
        Math.Round(90m / 60m * hourly, 2).Should().Be((decimal)expected);
    }

    [Theory]
    [InlineData("Basic", 600.00)]    // 3 unpaid days stored at approval: 3 x 6,000 / 30
    [InlineData("Gross", 800.00)]    // 3 x 8,000 / 30
    public void UnpaidLeave_FollowsTheAbsenceBase(string choice, double expected)
    {
        var dayRate = CompanyPayrollRules.AbsenceDeductionBaseAmount(choice, 6_000m, 1_500m, 500m, 0m) / 30m;
        CompanyPayrollRules.UnpaidLeaveDeduction(choice, 600m, 3m, dayRate, CompanyPayrollRules.UnpaidLeaveImpactType)
            .Should().Be((decimal)expected);
    }

    [Fact]
    public void TheSickLeaveBands_KeepTheirOwnWageBaseRule_UnderTheGrossAbsenceBase()
        => CompanyPayrollRules.UnpaidLeaveDeduction("Gross", 123.45m, 2m, 266.67m, "Leave Deduction (KSA Art.117 sick-leave scale: 30d@100%)")
            .Should().Be(123.45m);
}
