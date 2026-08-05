using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Application.WorkWeek;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// POD-C3 — MID-MONTH PRORATION + RETRO/ARREARS.
///
/// The four things that make this pod safe to ship to ~55 live tenants:
///   • INERTNESS — an employee who neither joined nor left is byte-identical to pre-C3. Every proration
///     path is gated on an employment window strictly narrower than the period.
///   • THE RATE/EARNING SPLIT — overtime and unpaid absence are valued on the FULL monthly rate while the
///     wage is prorated. Conflating them under-pays OT and under-charges absence for every joiner.
///   • ARREARS TOP UP A PAID MONTH AND NEVER RE-PAY AN UNPAID ONE, and cannot double-pay however many
///     times the run is processed.
///   • THE A1 LOCKSTEP — the statutory base the run feeds the pack and the base
///     GosiReconciliationService rebuilds stay identical under BOTH GOSI-base policies.
/// </summary>
public class PayrollProrationArrearsTests
{
    // ══ PURE PRORATION ARITHMETIC (no DB) ══════════════════════════════════════════════════════

    /// <summary>
    /// THE FULL-PERIOD RULE, and why it is not optional. Without it, February (28 days) would pay every
    /// employee in every tenant 28/30 of their salary under the Calendar30 basis. This single assertion
    /// is the inertness guarantee for the whole pod.
    /// </summary>
    [Theory]
    [InlineData(2026, 2, 28)]   // 28-day February
    [InlineData(2024, 2, 29)]   // leap February
    [InlineData(2026, 4, 30)]   // 30-day month
    [InlineData(2026, 1, 31)]   // 31-day month
    public void FullPeriodEmployment_IsNeverProrated_WhateverTheMonthLength(int year, int month, int days)
    {
        var start = new DateOnly(year, month, 1);
        var end = start.AddMonths(1).AddDays(-1);
        end.Day.Should().Be(days);

        var r = ProrationCalculator.Compute(start, end, new DateOnly(2020, 1, 1), null, ProrationBases.Calendar30, null);

        r.Factor.Should().Be(1m, "an employee employed for the whole period is never prorated");
        r.IsProrated.Should().BeFalse();
        r.Narrative.Should().BeEmpty("no narrative means the payslip line is byte-identical to pre-C3");
    }

    [Fact]
    public void MidMonthJoiner_IsProratedOnCalendarDaysOverThirty()
    {
        var r = ProrationCalculator.Compute(
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30),
            new DateOnly(2026, 11, 13), null, ProrationBases.Calendar30, null);

        r.PaidFrom.Should().Be(new DateOnly(2026, 11, 13));
        r.PaidDays.Should().Be(18, "13 Nov → 30 Nov inclusive");
        r.Factor.Should().Be(0.6m);
        r.IsFirstWageMonth.Should().BeTrue();
        r.IsFinalWageMonth.Should().BeFalse();
        r.Narrative.Should().Be("18/30 days · 30-day month · joined 2026-11-13");
    }

    [Fact]
    public void JoinerOnTheLastDay_IsPaidExactlyOneDay()
    {
        var r = ProrationCalculator.Compute(
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30),
            new DateOnly(2026, 11, 30), null, ProrationBases.Calendar30, null);

        r.PaidDays.Should().Be(1);
        r.Factor.Should().Be(Math.Round(1m / 30m, 6));
        r.IsExcluded.Should().BeFalse("one employed day is one day of pay, not an exclusion");
    }

    [Fact]
    public void JoinerAfterThePeriodEnds_IsExcludedEntirely_NotPaidZero()
    {
        var r = ProrationCalculator.Compute(
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30),
            new DateOnly(2026, 12, 3), null, ProrationBases.Calendar30, null);

        r.IsExcluded.Should().BeTrue("a zero payslip and an empty GL group are not a truthful " +
                                     "representation of 'not employed'");
        r.Factor.Should().Be(0m);
    }

    [Fact]
    public void MidMonthLeaver_IsPaidThroughTheLastWorkingDayOnly()
    {
        var r = ProrationCalculator.Compute(
            new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31),
            new DateOnly(2020, 1, 1), new DateOnly(2026, 10, 15), ProrationBases.Calendar30, null);

        r.PaidTo.Should().Be(new DateOnly(2026, 10, 15));
        r.PaidDays.Should().Be(15);
        r.Factor.Should().Be(0.5m);
        r.IsFinalWageMonth.Should().BeTrue("POD-C1 reads this as the handoff that the wage side is settled");
    }

    [Fact]
    public void JoinAndLeaveInTheSameMonth_PaysOnlyTheOverlap()
    {
        var r = ProrationCalculator.Compute(
            new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30),
            new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 19), ProrationBases.Calendar30, null);

        r.PaidDays.Should().Be(10);
        r.IsFirstWageMonth.Should().BeTrue();
        r.IsFinalWageMonth.Should().BeTrue();
    }

    /// <summary>A stale offboarding from a prior year (a RE-HIRE) must never zero a live employee's wage.</summary>
    [Fact]
    public void LastWorkingDayBeforeThePeriod_IsIgnored_SoARehireIsNotZeroed()
    {
        var r = ProrationCalculator.Compute(
            new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30),
            new DateOnly(2026, 1, 1), new DateOnly(2024, 5, 1), ProrationBases.Calendar30, null);

        r.Factor.Should().Be(1m);
        r.IsExcluded.Should().BeFalse();
    }

    [Fact]
    public void CalendarActualBasis_UsesTheRealMonthLength()
    {
        var r = ProrationCalculator.Compute(
            new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28),
            new DateOnly(2026, 2, 15), null, ProrationBases.CalendarActual, null);

        r.PaidDays.Should().Be(14);
        r.DenominatorDays.Should().Be(28);
        r.Factor.Should().Be(0.5m);
    }

    [Fact]
    public void NoneBasis_NeverProrates_TheEscapeHatch()
    {
        var r = ProrationCalculator.Compute(
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30),
            new DateOnly(2026, 11, 25), null, ProrationBases.None, null);

        r.Factor.Should().Be(1m);
        r.IsProrated.Should().BeFalse();
    }

    [Fact]
    public void WorkingDaysBasis_CountsScheduledDaysOnBothSidesOfTheRatio()
    {
        var week = new WorkWeekConfig(new HashSet<DayOfWeek> { DayOfWeek.Friday, DayOfWeek.Saturday }, "gcc:company");
        var start = new DateOnly(2026, 6, 1);
        var end = new DateOnly(2026, 6, 30);

        var r = ProrationCalculator.Compute(start, end, new DateOnly(2026, 6, 16), null, ProrationBases.WorkingDays, week);

        r.DenominatorDays.Should().Be(week.CountWorkingDays(start, end));
        r.PaidDays.Should().Be(week.CountWorkingDays(new DateOnly(2026, 6, 16), end));
        r.Factor.Should().BeLessThan(1m).And.BeGreaterThan(0m);
    }

    /// <summary>
    /// No rounding dust may leak into the GL: Σ prorated components == round(factor × package, 2), to the
    /// halala. A cent of drift here is a dr/cr imbalance at Lock.
    /// </summary>
    [Theory]
    [InlineData(0.6)]
    [InlineData(0.033333)]
    [InlineData(0.5666667)]
    public void ProratedComponents_SumExactlyToTheProratedPackage(double factorRaw)
    {
        var factor = (decimal)factorRaw;
        var pkg = ProrationCalculator.Apply(7_333.33m, 2_444.44m, 1_111.11m, 777.77m, factor);
        var expected = Math.Round((7_333.33m + 2_444.44m + 1_111.11m + 777.77m) * factor, 2, MidpointRounding.AwayFromZero);

        (pkg.Basic + pkg.Housing + pkg.Transport + pkg.OtherAllowances).Should().Be(expected);
        pkg.Gross.Should().Be(expected);
    }

    // ══ POLICY RESOLUTION — FAIL LOUD, NEVER FALL BACK ═════════════════════════════════════════

    [Fact]
    public async Task UnrecognisedBasis_RefusesTheRun_NeverSilentlyDefaults()
    {
        var (db, conn) = CreateDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        db.CompanyRatePolicies.Add(new CompanyRatePolicy
        {
            TenantId = tenantId, CompanyId = companyId, RateKey = ProrationRateKeys.Basis,
            RateCategory = "PayParameter", RateValue = "Calender30", DataType = "string",
            EffectiveFrom = new DateOnly(2020, 1, 1), Status = CompanyPolicyStatuses.Active,
        });
        await db.SaveChangesAsync();

        var (policy, error) = await new ProrationPolicyResolver(new CompanyRatePolicyResolver(db))
            .ResolveAsync(tenantId, companyId, new DateOnly(2026, 6, 30), WorkWeekConfig.GccDefault);

        policy.Should().BeNull();
        error!.Code.Should().Be("proration_basis_invalid",
            "a typo'd basis must refuse the run, not quietly pay everyone on a basis nobody chose");
    }

    [Fact]
    public async Task WorkingDaysBasis_WithNoConfiguredWorkingWeek_Refuses()
    {
        var (db, conn) = CreateDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        db.CompanyRatePolicies.Add(new CompanyRatePolicy
        {
            TenantId = tenantId, CompanyId = null, RateKey = ProrationRateKeys.Basis,
            RateCategory = "PayParameter", RateValue = ProrationBases.WorkingDays, DataType = "string",
            EffectiveFrom = new DateOnly(2020, 1, 1), Status = CompanyPolicyStatuses.Active,
        });
        await db.SaveChangesAsync();

        var (_, error) = await new ProrationPolicyResolver(new CompanyRatePolicyResolver(db))
            .ResolveAsync(tenantId, null, new DateOnly(2026, 6, 30), WorkWeekConfig.GccDefault);

        error!.Code.Should().Be("proration_working_week_not_configured",
            "the platform Fri/Sat fallback exists for LEAVE counting, not to decide how much a joiner is paid");
    }

    [Fact]
    public async Task PeriodEarnedArrearsTreatment_IsRefused_NeverDowngradedToPeriodPaid()
    {
        var (db, conn) = CreateDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        db.CompanyRatePolicies.Add(new CompanyRatePolicy
        {
            TenantId = tenantId, CompanyId = null, RateKey = ProrationRateKeys.ArrearsGosiTreatment,
            RateCategory = "PayParameter", RateValue = ArrearsGosiTreatments.PeriodEarned, DataType = "string",
            EffectiveFrom = new DateOnly(2020, 1, 1), Status = CompanyPolicyStatuses.Active,
        });
        await db.SaveChangesAsync();

        var (_, error) = await new ProrationPolicyResolver(new CompanyRatePolicyResolver(db))
            .ResolveAsync(tenantId, null, new DateOnly(2026, 6, 30), WorkWeekConfig.GccDefault);

        error!.Code.Should().Be("arrears_gosi_period_earned_unsupported");
        error.Message.Should().Contain("amended", "the refusal must say WHY, and point at the persisted number");
    }

    [Fact]
    public async Task DefaultPolicy_IsCalendar30_AndFullMonthGosiBase()
    {
        var (db, conn) = CreateDb();
        await using var _ = conn; await using var __ = db;

        var (policy, error) = await new ProrationPolicyResolver(new CompanyRatePolicyResolver(db))
            .ResolveAsync(Guid.NewGuid(), null, new DateOnly(2026, 6, 30), WorkWeekConfig.GccDefault);

        error.Should().BeNull();
        policy!.Basis.Should().Be(ProrationBases.Calendar30);
        policy.GosiBase.Should().Be(ProrationGosiBases.FullMonth,
            "GOSI assesses a MONTHLY contributory wage; prorating it under-remits in the penalty-bearing direction");
        policy.ArrearsGosiTreatment.Should().Be(ArrearsGosiTreatments.PeriodPaid);
        policy.ArrearsMaxLookbackMonths.Should().Be(24);
        policy.ProratedComponents.Should().BeEquivalentTo(ProratedComponentCodes.All);
    }

    // ══ THE RUN ════════════════════════════════════════════════════════════════════════════════

    /// <summary>INERTNESS. A run with no joiner, no leaver and no retro is bit-for-bit pre-C3.</summary>
    [Fact]
    public async Task RunWithNoJoinerLeaverOrRetro_IsUnchangedByPodC3()
    {
        var (db, conn) = CreateDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, a, _) = await SeedAsync(db, tenantId);
        var run = AddRun(db, tenantId, companyId, 2026, 6);
        await db.SaveChangesAsync();

        (await Ctrl(db, tenantId).Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        var slip = await db.PayrollSlips.AsNoTracking().FirstAsync(s => s.RunId == run.Id && s.EmployeeId == a.Id);
        slip.BasicSalary.Should().Be(10_000m);
        slip.HousingAllowance.Should().Be(2_000m);
        slip.ProrationFactor.Should().Be(1m);
        slip.IsFinalWageMonth.Should().BeFalse();
        slip.ArrearsAmount.Should().Be(0m);
        (await db.PayrollEarnings.AsNoTracking().FirstAsync(e => e.PayrollRunId == run.Id && e.EmployeeId == a.Id && e.ComponentCode == "BASIC"))
            .ComponentName.Should().Be("Basic salary", "no narrative is appended when nothing was prorated");
        (await db.PayrollArrearsLines.AsNoTracking().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task MidMonthJoiner_IsPaidProrated_AndThePayslipSaysWhy()
    {
        var (db, conn) = CreateDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, a, _) = await SeedAsync(db, tenantId);
        (await db.Employees.FirstAsync(e => e.Id == a.Id)).JoiningDate = new DateTime(2026, 6, 16);
        var run = AddRun(db, tenantId, companyId, 2026, 6);
        await db.SaveChangesAsync();

        (await Ctrl(db, tenantId).Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        var slip = await db.PayrollSlips.AsNoTracking().FirstAsync(s => s.RunId == run.Id && s.EmployeeId == a.Id);
        slip.PaidDays.Should().Be(15);
        slip.ProrationFactor.Should().Be(0.5m);
        // 13,000 package × 0.5 = 6,500, split so the components still sum exactly to it.
        (slip.BasicSalary + slip.HousingAllowance + slip.TransportAllowance).Should().Be(6_500m);
        slip.FullBasicSalary.Should().Be(10_000m, "the FULL package is persisted for the A1 rebuild and the rate basis");
        slip.PaidFromDate.Should().Be(new DateOnly(2026, 6, 16));

        var basicLine = await db.PayrollEarnings.AsNoTracking()
            .FirstAsync(e => e.PayrollRunId == run.Id && e.EmployeeId == a.Id && e.ComponentCode == "BASIC");
        basicLine.ComponentName.Should().Contain("15/30 days").And.Contain("30-day month").And.Contain("joined 2026-06-16");
    }

    /// <summary>
    /// THE RATE-BASIS REGRESSION — the highest-value assertion in the pod. A prorated joiner's overtime
    /// must be paid at their FULL hourly rate; deriving it from the prorated basic would silently halve it.
    /// </summary>
    [Fact]
    public async Task ProratedJoiner_IsPaidOvertimeAtTheFullHourlyRate()
    {
        var (db, conn) = CreateDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, a, _) = await SeedAsync(db, tenantId);
        (await db.Employees.FirstAsync(e => e.Id == a.Id)).JoiningDate = new DateTime(2026, 6, 16);
        var run = AddRun(db, tenantId, companyId, 2026, 6);
        var otReq = new OvertimeRequest
        {
            TenantId = tenantId, CompanyId = companyId, EmployeeId = a.Id,
            WorkDate = new DateOnly(2026, 6, 20), ApprovedMinutes = 600, Status = "Approved",
        };
        db.OvertimeRequests.Add(otReq);
        await db.SaveChangesAsync();
        db.OvertimePayrollImpacts.Add(new OvertimePayrollImpact
        {
            TenantId = tenantId, OvertimeRequestId = otReq.Id, EmployeeId = a.Id,
            Hours = 10m, Amount = 0m, ApprovedMultiplier = 1.5m, Status = "Pending",
        });
        await db.SaveChangesAsync();

        (await Ctrl(db, tenantId).Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        // FULL hourly rate = 10,000 / 240 = 41.6667; 10 h × 1.5 = 625.00. Half that would be the bug.
        var ot = await db.PayrollEarnings.AsNoTracking()
            .FirstAsync(e => e.PayrollRunId == run.Id && e.EmployeeId == a.Id && e.ComponentCode == "OVERTIME");
        ot.Amount.Should().Be(625.00m, "overtime is valued on the monthly RATE, never on the prorated wage");
    }

    /// <summary>An absent day costs the same whatever day you joined — and can never exceed days employed.</summary>
    [Fact]
    public async Task ProratedJoiner_IsChargedAbsenceAtTheFullDayRate_AndNeverMoreDaysThanEmployed()
    {
        var (db, conn) = CreateDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, a, _) = await SeedAsync(db, tenantId);
        (await db.Employees.FirstAsync(e => e.Id == a.Id)).JoiningDate = new DateTime(2026, 6, 16);
        var run = AddRun(db, tenantId, companyId, 2026, 6);
        for (var d = 16; d <= 17; d++)
            db.AttendancePayrollImpacts.Add(new AttendancePayrollImpact
            {
                TenantId = tenantId, EmployeeId = a.Id, WorkDate = new DateOnly(2026, 6, d),
                ImpactType = "Absence", Minutes = 480, Status = "Pending",
            });
        await db.SaveChangesAsync();

        (await Ctrl(db, tenantId).Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        // 2 days × (FULL 10,000 / 30) = 666.67. Using the prorated basic would charge 333.33.
        var lop = await db.PayrollDeductions.AsNoTracking()
            .FirstAsync(d => d.PayrollRunId == run.Id && d.EmployeeId == a.Id && d.ComponentCode == "LOP_DEDUCTION");
        lop.Amount.Should().Be(666.67m);
    }

    /// <summary>A debt instalment is not a wage. It is never prorated.</summary>
    [Fact]
    public async Task LoanInstalment_IsNotProrated_ForAJoiner()
    {
        var (db, conn) = CreateDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, a, _) = await SeedAsync(db, tenantId);
        (await db.Employees.FirstAsync(e => e.Id == a.Id)).JoiningDate = new DateTime(2026, 6, 16);
        db.EmployeeLoans.Add(new EmployeeLoan
        {
            TenantId = tenantId, CompanyId = companyId, EmployeeIntId = a.Id, Status = "Active",
            ApprovedAmount = 6_000m, OutstandingBalance = 6_000m, InstallmentAmount = 1_000m,
        });
        var run = AddRun(db, tenantId, companyId, 2026, 6);
        await db.SaveChangesAsync();

        (await Ctrl(db, tenantId).Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        var emi = await db.PayrollDeductions.AsNoTracking()
            .FirstAsync(d => d.PayrollRunId == run.Id && d.EmployeeId == a.Id && d.ComponentCode == "LOAN_EMI");
        emi.Amount.Should().Be(1_000m, "an EMI is a debt instalment, not a wage");
    }

    /// <summary>
    /// MF-2 — proration makes an unfundable instalment reachable for the first time. The instalment is
    /// CAPPED at the net the period can fund and the balance CARRIES FORWARD; it is never written off,
    /// and the loan ledger is decremented by what was actually withheld, not by a recomputed instalment.
    /// </summary>
    [Fact]
    public async Task EmiExceedingAvailableNet_IsCappedAndCarriedForward_NotWrittenOff()
    {
        var (db, conn) = CreateDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, a, _) = await SeedAsync(db, tenantId);
        (await db.Employees.FirstAsync(e => e.Id == a.Id)).JoiningDate = new DateTime(2026, 6, 28);  // 3 days
        db.EmployeeLoans.Add(new EmployeeLoan
        {
            TenantId = tenantId, CompanyId = companyId, EmployeeIntId = a.Id, Status = "Active",
            ApprovedAmount = 20_000m, OutstandingBalance = 20_000m, InstallmentAmount = 5_000m,
        });
        var run = AddRun(db, tenantId, companyId, 2026, 6);
        await db.SaveChangesAsync();

        (await Ctrl(db, tenantId).Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        db.ChangeTracker.Clear();
        var slip = await db.PayrollSlips.AsNoTracking().FirstAsync(s => s.RunId == run.Id && s.EmployeeId == a.Id);
        slip.NetSalary.Should().BeGreaterThanOrEqualTo(0m);
        var loan = await db.EmployeeLoans.AsNoTracking().FirstAsync(l => l.EmployeeIntId == a.Id);
        var withheld = 20_000m - loan.OutstandingBalance;
        withheld.Should().BeLessThan(5_000m, "the period could not fund the whole instalment");
        loan.TotalRepaid.Should().Be(withheld, "the ledger records what was withheld, not a recomputed instalment");
        (await db.PayrollValidationResults.AsNoTracking()
            .AnyAsync(r => r.PayrollRunId == run.Id && r.Code == "WARN_EMI_DEFERRED_PRORATED_PERIOD"))
            .Should().BeTrue("the deferral is named, never silent");
    }

    /// <summary>
    /// THE LIVE DEFECT POD-C3 CLOSES. OffboardingController sets Status="Offboarded" at NOTICE time and
    /// the eligible query filtered Status=="Active", so notice-period staff were paid NOTHING — for the
    /// whole notice period, silently.
    /// </summary>
    [Fact]
    public async Task EmployeeServingNotice_IsPaid_AndTheirFinalMonthIsProrated()
    {
        var (db, conn) = CreateDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, a, _) = await SeedAsync(db, tenantId);
        var emp = await db.Employees.FirstAsync(e => e.Id == a.Id);
        emp.Status = EmployeeStatuses.Offboarded;
        db.EmployeeOffboardings.Add(new EmployeeOffboarding
        {
            TenantId = tenantId, EmployeeId = a.Id, EmployeeCode = a.EmployeeCode, EmployeeName = a.FullName,
            NoticeDate = new DateOnly(2026, 5, 1), LastWorkingDay = new DateOnly(2026, 6, 15), Status = "InProgress",
        });
        var run = AddRun(db, tenantId, companyId, 2026, 6);
        await db.SaveChangesAsync();

        (await Ctrl(db, tenantId).Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        var slip = await db.PayrollSlips.AsNoTracking()
            .FirstOrDefaultAsync(s => s.RunId == run.Id && s.EmployeeId == a.Id);
        slip.Should().NotBeNull("before POD-C3 this employee was silently dropped and paid nothing");
        slip!.PaidToDate.Should().Be(new DateOnly(2026, 6, 15));
        slip.PaidDays.Should().Be(15);
        slip.IsFinalWageMonth.Should().BeTrue("POD-C1 reads this as the wage-side handoff");
        (slip.BasicSalary + slip.HousingAllowance + slip.TransportAllowance).Should().Be(6_500m);
        (await db.PayrollValidationResults.AsNoTracking()
            .AnyAsync(r => r.PayrollRunId == run.Id && r.Code == "WARN_LEAVER_INCLUDED_ON_NOTICE"))
            .Should().BeTrue();
    }

    /// <summary>The STOP CONDITION: a final wage month is paid exactly ONCE, never again.</summary>
    [Fact]
    public async Task AfterTheFinalWageMonth_TheLeaverIsNotPaidAgain()
    {
        var (db, conn) = CreateDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, a, _) = await SeedAsync(db, tenantId);
        (await db.Employees.FirstAsync(e => e.Id == a.Id)).Status = EmployeeStatuses.Offboarded;
        db.EmployeeOffboardings.Add(new EmployeeOffboarding
        {
            TenantId = tenantId, EmployeeId = a.Id, EmployeeCode = a.EmployeeCode, EmployeeName = a.FullName,
            NoticeDate = new DateOnly(2026, 5, 1), LastWorkingDay = new DateOnly(2026, 6, 15), Status = "InProgress",
        });
        var june = AddRun(db, tenantId, companyId, 2026, 6);
        await db.SaveChangesAsync();
        (await Ctrl(db, tenantId).Process(june.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        db.ChangeTracker.Clear();
        var july = AddRun(db, tenantId, companyId, 2026, 7);
        await db.SaveChangesAsync();
        (await Ctrl(db, tenantId).Process(july.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        (await db.PayrollSlips.AsNoTracking().AnyAsync(s => s.RunId == july.Id && s.EmployeeId == a.Id))
            .Should().BeFalse("the final wage month is paid exactly once");
    }

    // ══ ARREARS ════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE CORE RETRO CASE: an increment approved now but effective two months ago, over periods whose
    /// runs are locked. Arrears = Σ(new − paid) across the intervening months, itemised per period.
    /// </summary>
    [Fact]
    public async Task BackdatedIncrement_SettlesArrearsPerCoveredPeriod_AndIsIdempotent()
    {
        var (db, conn) = CreateDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, a, _) = await SeedAsync(db, tenantId);

        // April and May were paid at the original package and locked.
        foreach (var m in new[] { 4, 5 })
        {
            var r = AddRun(db, tenantId, companyId, 2026, m);
            await db.SaveChangesAsync();
            (await Ctrl(db, tenantId).Process(r.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
            db.ChangeTracker.Clear();
            (await db.PayrollRuns.FirstAsync(x => x.Id == r.Id)).Status = "Locked";
            await db.SaveChangesAsync();
        }

        // A backdated increment: basic 10,000 → 12,000 effective 1 April, keyed in June.
        var structureId = (await db.EmployeeSalaryStructures.AsNoTracking().FirstAsync(x => x.EmployeeId == a.Id)).SalaryStructureId;
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = a.Id, SalaryStructureId = structureId,
            BasicSalary = 12_000m, HousingAllowance = 2_000m, TransportAllowance = 1_000m,
            EffectiveDate = new DateOnly(2026, 4, 1), IsActive = true, CreatedAtUtc = new DateTime(2026, 6, 1),
        });
        var june = AddRun(db, tenantId, companyId, 2026, 6);
        await db.SaveChangesAsync();

        (await Ctrl(db, tenantId).Process(june.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var lines = await db.PayrollArrearsLines.AsNoTracking().Where(x => x.PayrollRunId == june.Id).ToListAsync();
        lines.Should().HaveCount(2, "one line per covered period — April and May — so the payslip is itemised");
        lines.Select(l => (l.CoveredYear, l.CoveredMonth)).Should()
            .BeEquivalentTo(new[] { (2026, 4), (2026, 5) });
        lines.Sum(l => l.Amount).Should().Be(4_000m, "2,000 per month × 2 months");
        lines.Should().OnlyContain(l => l.ComponentCode == PayrollArrearsComponents.Basic);
        lines.Should().OnlyContain(l => l.IsGosiBearing, "basic is contributory under the default treatment");

        // Itemised on the payslip, per covered period.
        var earnLines = await db.PayrollEarnings.AsNoTracking()
            .Where(e => e.PayrollRunId == june.Id && e.EmployeeId == a.Id && e.Source == "Arrears").ToListAsync();
        earnLines.Should().HaveCount(2);
        earnLines.Select(e => e.ComponentName).Should()
            .Contain(n => n.Contains("2026-04")).And.Contain(n => n.Contains("2026-05"));

        // ANTI-DOUBLE-PAY: re-processing the SAME run rebuilds rather than duplicates …
        db.ChangeTracker.Clear();
        (await db.PayrollRuns.FirstAsync(x => x.Id == june.Id)).Status = "Draft";
        await db.SaveChangesAsync();
        (await Ctrl(db, tenantId).Process(june.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await db.PayrollArrearsLines.AsNoTracking().Where(x => x.PayrollRunId == june.Id).Select(x => x.Amount).ToListAsync()).Sum()
            .Should().Be(4_000m, "the wipe removes this run's lines BEFORE entitlement is recomputed");
    }

    /// <summary>
    /// … and a SECOND run cannot settle the same shortfall again: the "previously settled" term absorbs
    /// it. The formula is SELF-CORRECTING, not merely guarded.
    /// </summary>
    [Fact]
    public async Task ASecondRun_SettlesZero_BecauseTheFormulaIsSelfCorrecting()
    {
        var (db, conn) = CreateDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, a, _) = await SeedAsync(db, tenantId);

        var april = AddRun(db, tenantId, companyId, 2026, 4);
        await db.SaveChangesAsync();
        (await Ctrl(db, tenantId).Process(april.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await db.PayrollRuns.FirstAsync(x => x.Id == april.Id)).Status = "Locked";

        var structureId = (await db.EmployeeSalaryStructures.AsNoTracking().FirstAsync(x => x.EmployeeId == a.Id)).SalaryStructureId;
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = a.Id, SalaryStructureId = structureId,
            BasicSalary = 12_000m, HousingAllowance = 2_000m, TransportAllowance = 1_000m,
            EffectiveDate = new DateOnly(2026, 4, 1), IsActive = true, CreatedAtUtc = new DateTime(2026, 5, 1),
        });
        var may = AddRun(db, tenantId, companyId, 2026, 5);
        await db.SaveChangesAsync();
        (await Ctrl(db, tenantId).Process(may.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await db.PayrollArrearsLines.AsNoTracking().Where(x => x.PayrollRunId == may.Id).Select(x => x.Amount).ToListAsync()).Sum()
            .Should().Be(2_000m);
        (await db.PayrollRuns.FirstAsync(x => x.Id == may.Id)).Status = "Locked";
        await db.SaveChangesAsync();

        var june = AddRun(db, tenantId, companyId, 2026, 6);
        await db.SaveChangesAsync();
        (await Ctrl(db, tenantId).Process(june.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        (await db.PayrollArrearsLines.AsNoTracking()
            .Where(x => x.PayrollRunId == june.Id && x.CoveredYear == 2026 && x.CoveredMonth == 4)
            .Select(x => x.Amount).ToListAsync()).Sum()
            .Should().Be(0m, "April was already settled by the May run; the third term absorbs it");
    }

    /// <summary>
    /// REQUIREMENT 4, LITERALLY: "It must never silently re-pay a whole month." A period in which the
    /// employee was paid NOTHING (a B2 hold-out, an excluded joiner, a run that produced no lines) is a
    /// MISSED month, not an underpaid one. Paying it here would bypass the entire run lifecycle.
    /// </summary>
    [Fact]
    public async Task APeriodWithNothingPaid_IsNeverSettledAsArrears()
    {
        var (db, conn) = CreateDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, a, _) = await SeedAsync(db, tenantId);

        // A locked May run that produced NO lines for this employee at all.
        AddRun(db, tenantId, companyId, 2026, 5, status: "Locked");
        var june = AddRun(db, tenantId, companyId, 2026, 6);
        await db.SaveChangesAsync();

        (await Ctrl(db, tenantId).Process(june.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        (await db.PayrollArrearsLines.AsNoTracking().AnyAsync(x => x.PayrollRunId == june.Id))
            .Should().BeFalse("a month with nothing paid is a MISSED month — that is an OffCycle run's job");
    }

    /// <summary>
    /// A retro DECREASE is COMPUTED, PERSISTED as PendingRecovery, and the run REFUSES. Never netted into
    /// pay (a negative earning breaks WPS and the control accounts) and never silently dropped (which
    /// would leave the employee permanently overpaid and invisible).
    /// </summary>
    [Fact]
    public async Task BackdatedDecrease_IsPersistedAsPendingRecovery_AndRefusesTheRun()
    {
        var (db, conn) = CreateDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, a, _) = await SeedAsync(db, tenantId);

        var may = AddRun(db, tenantId, companyId, 2026, 5);
        await db.SaveChangesAsync();
        (await Ctrl(db, tenantId).Process(may.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await db.PayrollRuns.FirstAsync(x => x.Id == may.Id)).Status = "Locked";

        var structureId = (await db.EmployeeSalaryStructures.AsNoTracking().FirstAsync(x => x.EmployeeId == a.Id)).SalaryStructureId;
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = a.Id, SalaryStructureId = structureId,
            BasicSalary = 8_000m, HousingAllowance = 2_000m, TransportAllowance = 1_000m,
            EffectiveDate = new DateOnly(2026, 5, 1), IsActive = true, CreatedAtUtc = new DateTime(2026, 6, 1),
        });
        var june = AddRun(db, tenantId, companyId, 2026, 6);
        await db.SaveChangesAsync();

        var res = await Ctrl(db, tenantId).Process(june.Id, CancellationToken.None);
        res.Should().BeOfType<UnprocessableEntityObjectResult>();
        res.As<ObjectResult>().Value!.ToString().Should().Contain("retro_decrease_unsupported");

        db.ChangeTracker.Clear();
        var pending = await db.PayrollArrearsLines.AsNoTracking()
            .Where(x => x.Status == PayrollArrearsStatuses.PendingRecovery).ToListAsync();
        pending.Should().NotBeEmpty("the amount and its covered period survive the refusal — nothing is lost");
        pending.Should().OnlyContain(x => x.Amount < 0m);
        (await db.PayrollSlips.AsNoTracking().AnyAsync(s => s.RunId == june.Id))
            .Should().BeFalse("the run is refused before anything is written");
    }

    // ══ POD-B3 HANDOFF: NETTING THE 1420 RECEIVABLE ════════════════════════════════════════════

    /// <summary>
    /// POD-B3 posted an aggregate DR 1420 whenever a void left already-disbursed cash standing, and
    /// explicitly handed the netting to C3. The recovery is capped at the employee's net before recovery,
    /// CREDITS the 1420 asset (never a payable), and decrements the per-employee sub-ledger.
    /// </summary>
    [Fact]
    public async Task ReplacementRun_NetsThePriorReceivable_CappedAtNet_AndCreditsTheAsset()
    {
        var (db, conn) = CreateDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, a, _) = await SeedAsync(db, tenantId);

        var voidedRunId = Guid.NewGuid();
        db.PayrollEmployeeReceivables.Add(new PayrollEmployeeReceivable
        {
            TenantId = tenantId, CompanyId = companyId, EmployeeId = a.Id, EmployeeCode = a.EmployeeCode,
            SourceRunId = voidedRunId, EventType = "PayrollNetSettlementReclass", Period = "2026-05",
            Amount = 4_000m, Status = PayrollReceivableStatuses.Outstanding,
        });
        var run = AddRun(db, tenantId, companyId, 2026, 6);
        run.NetsPriorReceivable = true;
        await db.SaveChangesAsync();

        (await Ctrl(db, tenantId).Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var recovery = await db.PayrollDeductions.AsNoTracking().FirstOrDefaultAsync(
            d => d.PayrollRunId == run.Id && d.EmployeeId == a.Id
              && d.ComponentCode == PayrollRecoveryComponents.ReceivableRecovery);
        recovery.Should().NotBeNull("the receivable must be netted, not left to age forever");
        recovery!.Amount.Should().Be(4_000m);
        recovery.Source.Should().Be(PayrollRecoveryComponents.RecoverySource,
            "the source is what routes it to DED:RECEIVABLE_RECOVERY (1420) rather than DED:OTHER (2199)");

        var rcv = await db.PayrollEmployeeReceivables.AsNoTracking().FirstAsync(r => r.EmployeeId == a.Id);
        rcv.RecoveredAmount.Should().Be(4_000m);
        rcv.Status.Should().Be(PayrollReceivableStatuses.Recovered);
        rcv.RecoveredByRunId.Should().Be(run.Id);

        var slip = await db.PayrollSlips.AsNoTracking().FirstAsync(s => s.RunId == run.Id && s.EmployeeId == a.Id);
        slip.NetSalary.Should().BeGreaterThanOrEqualTo(0m, "recovery never drives net negative");
    }

    /// <summary>A receivable larger than the month's net leaves a RESIDUAL that ages — never forgiven.</summary>
    [Fact]
    public async Task ReceivableLargerThanNet_LeavesAnAgeingResidual_AndIsReported()
    {
        var (db, conn) = CreateDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, a, _) = await SeedAsync(db, tenantId);

        db.PayrollEmployeeReceivables.Add(new PayrollEmployeeReceivable
        {
            TenantId = tenantId, CompanyId = companyId, EmployeeId = a.Id, EmployeeCode = a.EmployeeCode,
            SourceRunId = Guid.NewGuid(), EventType = "PayrollNetSettlementReclass", Period = "2026-05",
            Amount = 50_000m, Status = PayrollReceivableStatuses.Outstanding,
        });
        var run = AddRun(db, tenantId, companyId, 2026, 6);
        run.NetsPriorReceivable = true;
        await db.SaveChangesAsync();

        (await Ctrl(db, tenantId).Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var slip = await db.PayrollSlips.AsNoTracking().FirstAsync(s => s.RunId == run.Id && s.EmployeeId == a.Id);
        slip.NetSalary.Should().Be(0m, "recovery takes the whole net but can never make it negative");
        var rcv = await db.PayrollEmployeeReceivables.AsNoTracking().FirstAsync(r => r.EmployeeId == a.Id);
        (rcv.Amount - rcv.RecoveredAmount).Should().BeGreaterThan(0m);
        rcv.Status.Should().Be(PayrollReceivableStatuses.Outstanding, "the residual keeps ageing");
        (await db.PayrollValidationResults.AsNoTracking()
            .AnyAsync(r => r.PayrollRunId == run.Id && r.Code == "WARN_RECEIVABLE_RESIDUAL"))
            .Should().BeTrue();
    }

    /// <summary>Default-OFF: a run that does not opt in must not touch the receivable at all.</summary>
    [Fact]
    public async Task WithoutTheExplicitFlag_TheReceivableIsUntouched()
    {
        var (db, conn) = CreateDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var (companyId, a, _) = await SeedAsync(db, tenantId);
        db.PayrollEmployeeReceivables.Add(new PayrollEmployeeReceivable
        {
            TenantId = tenantId, CompanyId = companyId, EmployeeId = a.Id, EmployeeCode = a.EmployeeCode,
            SourceRunId = Guid.NewGuid(), EventType = "PayrollNetSettlementReclass", Period = "2026-05",
            Amount = 4_000m, Status = PayrollReceivableStatuses.Outstanding,
        });
        var run = AddRun(db, tenantId, companyId, 2026, 6);   // NetsPriorReceivable defaults to false
        await db.SaveChangesAsync();

        (await Ctrl(db, tenantId).Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        (await db.PayrollDeductions.AsNoTracking().AnyAsync(
            d => d.PayrollRunId == run.Id && d.ComponentCode == PayrollRecoveryComponents.ReceivableRecovery))
            .Should().BeFalse("recovering an overpayment out of salary is an act the operator chooses");
        (await db.PayrollEmployeeReceivables.AsNoTracking().FirstAsync(r => r.EmployeeId == a.Id))
            .RecoveredAmount.Should().Be(0m);
    }

    // ══ HARNESS ════════════════════════════════════════════════════════════════════════════════

    private static (ZayraDbContext db, SqliteConnection conn) CreateDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static PayrollController Ctrl(ZayraDbContext db, Guid tenantId)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "c3-test"),
            new("permission", "payroll.write"),
            new("permission", "payroll.read"),
            new("permission", "payroll.approve"),
            new("permission", "payroll.lock"),
            new(ClaimTypes.Role, "Admin"),
        };
        var httpCtx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        var ctrl = new PayrollController(
            db, new _C3Scope(), new _C3Http(httpCtx), new _C3Notifications(),
            new _C3PackResolver(), _C3Rules.Rules, new _C3Letters(), new _C3Storage(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(4));
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }

    private static async Task<(Guid companyId, Employee a, Employee b)> SeedAsync(ZayraDbContext db, Guid tenantId)
    {
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "C3 KSA Co",
            CountryCode = "SAU", Jurisdiction = "KSA-mainland", IsActive = true, DefaultCurrency = "SAR",
        };
        db.Companies.Add(company);
        var structure = new SalaryStructure
        {
            TenantId = tenantId, CompanyId = company.Id, Code = "STR-C3", Name = "Base",
            Currency = "SAR", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
        };
        db.SalaryStructures.Add(structure);
        var a = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "C001", FullName = "Ali Hassan",
            Status = "Active", JoiningDate = new DateTime(2023, 1, 1),
            WorkEmail = "ali@c3.com", Nationality = "Indian", ContractType = "Indefinite",
        };
        var b = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "C002", FullName = "Bilal Omar",
            Status = "Active", JoiningDate = new DateTime(2023, 1, 1),
            WorkEmail = "bilal@c3.com", Nationality = "Indian", ContractType = "Indefinite",
        };
        db.Employees.AddRange(a, b);
        await db.SaveChangesAsync();
        foreach (var e in new[] { a, b })
        {
            db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
            {
                TenantId = tenantId, EmployeeId = e.Id, SalaryStructureId = structure.Id,
                BasicSalary = 10_000m, HousingAllowance = 2_000m, TransportAllowance = 1_000m,
                EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true, CreatedAtUtc = new DateTime(2025, 1, 1),
            });
            db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
            {
                TenantId = tenantId, EmployeeId = e.Id,
                Iban = "SA4420000001234567891234", MolId = $"MOL-{e.EmployeeCode}", SalaryCurrency = "SAR",
            });
        }
        await db.SaveChangesAsync();
        return (company.Id, a, b);
    }

    private static PayrollRun AddRun(ZayraDbContext db, Guid tenantId, Guid? companyId, int year, int month,
        string runType = PayrollRunTypes.Regular, string status = "Draft")
    {
        var run = new PayrollRun
        {
            TenantId = tenantId, CompanyId = companyId, Year = year, Month = month,
            Status = status, RunType = runType, IncludesRecurringPay = true, SettlesArrears = true,
        };
        db.PayrollRuns.Add(run);
        return run;
    }
}

file static class _C3Rules
{
    internal static readonly StubRuleReader Rules = new StubRuleReader()
        .Set("gosi.saudi_employee_rate",            0.09m)
        .Set("gosi.saudi_employer_rate",            0.09m)
        .Set("gosi.saned_rate",                     0.0075m)
        .Set("gosi.expat_occupational_hazard_rate", 0.02m)
        .Set("gosi.covered_wage_ceiling_sar",       45_000m)
        .Set("ot.standard_multiplier",              1.5m)
        .Set("ot.standard_monthly_hours",           240m)
        .Set("lop.monthly_day_divisor",             30m)
        .Set("lop.standard_work_minutes_per_day",   480m);
}

file sealed class _C3Scope : Zayra.Api.Application.Common.IDataScopeService
{
    public Task<Zayra.Api.Application.Common.DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Application.Common.DataScope
        {
            Level = Zayra.Api.Application.Common.DataScopeLevel.Organization, AllowedEmployeeIds = null,
        });
}

file sealed class _C3Http : IHttpContextAccessor
{
    public _C3Http(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _C3Notifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _C3PackResolver : ICountryPackResolver
{
    private static readonly KsaDeductionCalculator _calc = new(_C3Rules.Rules);
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => cc == "SAU" ? _calc : new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class _C3Letters : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class _C3Storage : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument(file.FileName, file.ContentType, "storage/test", "/tmp/test"));
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public string ResolvePath(string storageUrl) => "/tmp/test";
}
