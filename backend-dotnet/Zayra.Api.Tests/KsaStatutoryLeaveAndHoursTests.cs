using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Localization;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// KSA Labour Law (Royal Decree M/51) Art. 98 (Ramadan working hours), Art. 109 (annual-leave
/// tiering) and Art. 117 (sick-leave pay scale).
///
/// Every money assertion below is a figure computed by hand in the accompanying report, not a
/// figure read back out of the implementation.
/// </summary>
public class KsaStatutoryLeaveAndHoursTests
{
    // Um al-Qura: Ramadan 1447 runs 2026-02-18 … 2026-03-19; Ramadan 1448 runs 2027-02-08 … 2027-03-08.
    // The ~10-day annual drift is exactly why this can never be a fixed Gregorian range.
    private static readonly DateOnly RamadanWednesday = new(2026, 3, 4);      // 1447-09-15
    private static readonly DateOnly OrdinaryWednesday = new(2026, 3, 25);    // 1447-10-06, after Eid

    // ─────────────────────────────────────────────────────────────────────────
    //  Art. 98 — Ramadan reduced working hours
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ramadan_IsResolvedFromTheHijriCalendar_AndDriftsEachGregorianYear()
    {
        var hijri = new HijriDateService();

        KsaRamadanWorkingHours.IsRamadan(hijri, RamadanWednesday).Should().BeTrue();
        KsaRamadanWorkingHours.IsRamadan(hijri, OrdinaryWednesday).Should().BeFalse();

        // The drift that makes a hard-coded Gregorian range wrong within twelve months.
        // Ramadan 1447 = 2026-02-18 … 2026-03-19; Ramadan 1448 = 2027-02-08 … 2027-03-08.
        // 15 March is inside Ramadan in 2026 and outside it in 2027:
        KsaRamadanWorkingHours.IsRamadan(hijri, new DateOnly(2026, 3, 15)).Should().BeTrue();
        KsaRamadanWorkingHours.IsRamadan(hijri, new DateOnly(2027, 3, 15)).Should().BeFalse();
        // …and 10 February is outside it in 2026 and inside it in 2027:
        KsaRamadanWorkingHours.IsRamadan(hijri, new DateOnly(2026, 2, 10)).Should().BeFalse();
        KsaRamadanWorkingHours.IsRamadan(hijri, new DateOnly(2027, 2, 10)).Should().BeTrue();
    }

    [Fact]
    public void RamadanBaseline_NeverRisesAboveTheOrdinaryBaseline()
    {
        // A misconfigured "reduced" value above the standard one would RAISE the overtime
        // threshold and under-pay overtime. It is clamped.
        KsaRamadanWorkingHours.DailyBaselineMinutes(true, 480, 600).Should().Be(480);
        KsaRamadanWorkingHours.DailyBaselineMinutes(true, 480, 360).Should().Be(360);
        KsaRamadanWorkingHours.DailyBaselineMinutes(true, 480, 0).Should().Be(480);
        KsaRamadanWorkingHours.DailyBaselineMinutes(false, 480, 360).Should().Be(480);
    }

    [Fact]
    public async Task RamadanScope_FoldsMuslimOnlyToAllEmployees_AndAnnouncesIt()
    {
        await using var db = CreateDb();
        db.StatutoryRules.Add(PlatformRule(KsaLeaveHoursRuleKeys.RamadanScope, "muslim", "string"));
        await db.SaveChangesAsync();

        var svc = new KsaWorkingHoursBaselineService(new StatutoryRuleReader(db), new HijriDateService());
        var baseline = await svc.ResolveDailyAsync("SA", RamadanWednesday, 480, CancellationToken.None);

        // The Employee model has no religion attribute, so "muslim" cannot be evaluated. Applying the
        // reduction to nobody would strip a statutory entitlement from every Muslim employee, so the
        // product widens to all staff — and says so.
        baseline.DailyMinutes.Should().Be(360);
        baseline.IsRamadan.Should().BeTrue();
        baseline.Notice.Should().NotBeNull();
        baseline.Notice.Should().Contain("no religion attribute");
    }

    [Fact]
    public async Task RamadanBaseline_IsReadFromTheEffectiveDatedRule_NotACompiledLiteral()
    {
        await using var db = CreateDb();
        // A rule effective only from 2026-06-01 must NOT alter a March 2026 day.
        db.StatutoryRules.Add(PlatformRule(
            KsaLeaveHoursRuleKeys.RamadanWorkMinutesPerDay, "300", "decimal",
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();

        var svc = new KsaWorkingHoursBaselineService(new StatutoryRuleReader(db), new HijriDateService());

        var march = await svc.ResolveDailyAsync("SA", RamadanWednesday, 480, CancellationToken.None);
        march.DailyMinutes.Should().Be(360, "the 300-minute rule is not yet effective in March 2026");

        // Ramadan 1448 falls in Feb 2027, after the rule's effective date.
        var laterRamadan = await svc.ResolveDailyAsync("SA", new DateOnly(2027, 2, 22), 480, CancellationToken.None);
        laterRamadan.DailyMinutes.Should().Be(300, "the rule is effective by February 2027");
    }

    [Fact]
    public async Task NonKsaCompany_IsUntouchedDuringRamadan()
    {
        await using var db = CreateDb();
        var svc = new KsaWorkingHoursBaselineService(new StatutoryRuleReader(db), new HijriDateService());

        var uae = await svc.ResolveDailyAsync("AE", RamadanWednesday, 480, CancellationToken.None);
        uae.DailyMinutes.Should().Be(480);
        uae.IsRamadan.Should().BeFalse("the KSA pack must not claim a Ramadan finding for another country");
    }

    [Fact]
    public async Task EightHoursWorkedOnARamadanDay_ProducesTwoHoursOfOvertime()
    {
        // WORKED EXAMPLE. Art. 98 reduces actual working hours for Muslims to 6 h/day in Ramadan and
        // cuts HOURS, not wages. An employee who works a full ordinary 8-hour day in Ramadan has
        // therefore worked 2 hours of OVERTIME, payable at the Art. 107 rate.
        //   punch span 06:00 → 15:00 UTC = 540 min, less the 60-minute policy break = 480 worked min
        //   Ramadan baseline 360 min  ⇒  overtime = 480 − 360 = 120 min (2 h), undertime = 0
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = AddKsaEmployee(db, tenantId);
        await db.SaveChangesAsync();
        AddPunches(db, tenantId, employee.Id, RamadanWednesday);
        await db.SaveChangesAsync();

        await AttendanceSvc(db).ProcessAsync(tenantId,
            new ProcessAttendanceRequest(RamadanWednesday, RamadanWednesday, employee.Id),
            new RequestContext(null, null, Guid.NewGuid(), tenantId), CancellationToken.None);

        var daily = await db.AttendanceDailyRecords.SingleAsync();
        daily.TotalWorkedMinutes.Should().Be(480);
        daily.OvertimeMinutes.Should().Be(120,
            "Art. 98 reduces the Ramadan baseline to 6 h, so the 7th and 8th hours are overtime");
        daily.UndertimeMinutes.Should().Be(0);

        var otImpact = await db.AttendancePayrollImpacts
            .SingleAsync(x => x.ImpactType == "Overtime payable");
        otImpact.Minutes.Should().Be(120);
    }

    [Fact]
    public async Task TheSameEightHoursOutsideRamadan_ProducesNoOvertime()
    {
        // The control for the test above: identical punches, a date outside Ramadan.
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = AddKsaEmployee(db, tenantId);
        await db.SaveChangesAsync();
        AddPunches(db, tenantId, employee.Id, OrdinaryWednesday);
        await db.SaveChangesAsync();

        await AttendanceSvc(db).ProcessAsync(tenantId,
            new ProcessAttendanceRequest(OrdinaryWednesday, OrdinaryWednesday, employee.Id),
            new RequestContext(null, null, Guid.NewGuid(), tenantId), CancellationToken.None);

        var daily = await db.AttendanceDailyRecords.SingleAsync();
        daily.TotalWorkedMinutes.Should().Be(480);
        daily.OvertimeMinutes.Should().Be(0);
        (await db.AttendancePayrollImpacts.AnyAsync(x => x.ImpactType == "Overtime payable"))
            .Should().BeFalse();
    }

    [Fact]
    public async Task AbsenceOnARamadanDay_CostsSixHoursNotEight()
    {
        // The absence deduction used to be a hard-coded 480 minutes. On a 6-hour Ramadan day that
        // over-deducts by a third of a day.
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = AddKsaEmployee(db, tenantId);
        await db.SaveChangesAsync();   // no punches at all → Absent

        await AttendanceSvc(db).ProcessAsync(tenantId,
            new ProcessAttendanceRequest(RamadanWednesday, RamadanWednesday, employee.Id),
            new RequestContext(null, null, Guid.NewGuid(), tenantId), CancellationToken.None);

        var daily = await db.AttendanceDailyRecords.SingleAsync();
        daily.Status.Should().Be("Absent");
        var impact = await db.AttendancePayrollImpacts.SingleAsync(x => x.ImpactType == "Absence deduction");
        impact.Minutes.Should().Be(360, "an absent Ramadan day costs the reduced 6-hour day, not 8 hours");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Art. 109 — annual leave 21 → 30 days after five consecutive years
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnnualLeaveTier_StepsAtCompletionOfTheFifthYear()
    {
        var f = (decimal y) => KsaAnnualLeaveScale.EntitlementDays(y, 21m, 30m, 5m);
        f(0m).Should().Be(21m);
        f(4.99m).Should().Be(21m);
        f(5m).Should().Be(30m, "Art. 109 uplifts on COMPLETION of five consecutive years");
        f(12m).Should().Be(30m);

        // A misconfigured tiered value below the base must never make the scale go backwards.
        KsaAnnualLeaveScale.EntitlementDays(7m, 25m, 21m, 5m).Should().Be(25m);
    }

    [Fact]
    public async Task MonthlyAccrual_TiersOnLengthOfService_ForAKsaEntity()
    {
        // WORKED EXAMPLE, three employees on ONE 21-day policy at a KSA company:
        //   veteran   joined 7 years ago      → 5 yrs complete → 30 days/yr → 30/12 = 2.5000 d/month
        //   boundary  joined exactly 5 yrs ago→ 5 yrs complete → 30 days/yr → 2.5000 d/month
        //   juniorest joined 5 yrs ago + 1 day→ 4.99 yrs       → 21 days/yr → 21/12 = 1.7500 d/month
        // Before this change every one of them accrued 1.7500, under-accruing the two who had
        // completed five years by 9 days a year, indefinitely.
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var company = AddKsaCompany(db, tenantId);
        var today = DateTime.UtcNow;

        var veteran = AddEmployee(db, tenantId, "VET", company.Id, today.AddYears(-7));
        var boundary = AddEmployee(db, tenantId, "BND", company.Id, today.AddYears(-5));
        var justUnder = AddEmployee(db, tenantId, "JUN", company.Id, today.AddYears(-5).AddDays(1));

        var annual = new LeaveType
        {
            TenantId = tenantId, Code = "ANNUAL", NameEn = "Annual Leave", Category = "Annual", IsActive = true,
        };
        db.LeaveTypes.Add(annual);
        db.LeavePolicies.Add(new LeavePolicy
        {
            TenantId = tenantId, Name = "Default Annual Leave", LeaveTypeId = annual.Id,
            AnnualEntitlementDays = 21m, AccrualMethod = "Monthly", Status = "Active",
        });
        await db.SaveChangesAsync();

        await LeaveSvc(db).AccrueMonthlyAsync(tenantId, CancellationToken.None);

        var balances = await db.EmployeeLeaveBalances.ToDictionaryAsync(b => b.EmployeeId, b => b.Accrued);
        balances.Should().HaveCount(3, "the sweep must credit every eligible employee — an empty result is not a pass");

        balances[veteran.Id].Should().Be(2.5m, "30 statutory days ÷ 12");
        balances[boundary.Id].Should().Be(2.5m, "five years complete to the day is five years");
        balances[justUnder.Id].Should().Be(1.75m, "one day short of five years is still the 21-day tier");

        // The ledger must explain the uplift rather than silently differ from the policy.
        var vetTxn = await db.LeaveBalanceTransactions.SingleAsync(t => t.EmployeeId == veteran.Id);
        vetTxn.Amount.Should().Be(2.5m);
        vetTxn.Reason.Should().Contain("Art.109");
    }

    [Fact]
    public async Task MonthlyAccrual_LeavesANonKsaEntityAndANonAnnualTypeAlone()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var uaeCompany = new Company { TenantId = tenantId, LegalNameEn = "UAE Co", CountryCode = "AE" };
        db.Companies.Add(uaeCompany);
        var emp = AddEmployee(db, tenantId, "AE1", uaeCompany.Id, DateTime.UtcNow.AddYears(-9));

        var annual = new LeaveType
        {
            TenantId = tenantId, Code = "ANNUAL", NameEn = "Annual Leave", Category = "Annual", IsActive = true,
        };
        db.LeaveTypes.Add(annual);
        db.LeavePolicies.Add(new LeavePolicy
        {
            TenantId = tenantId, Name = "UAE Annual", LeaveTypeId = annual.Id,
            AnnualEntitlementDays = 21m, AccrualMethod = "Monthly", Status = "Active",
        });
        await db.SaveChangesAsync();

        await LeaveSvc(db).AccrueMonthlyAsync(tenantId, CancellationToken.None);

        var balance = await db.EmployeeLeaveBalances.SingleAsync(b => b.EmployeeId == emp.Id);
        balance.Accrued.Should().Be(1.75m, "the KSA Art. 109 tier must not reach a UAE entity");
    }

    [Fact]
    public async Task MonthlyAccrual_DoesNotInflateANonAnnualLeaveType()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var company = AddKsaCompany(db, tenantId);
        var emp = AddEmployee(db, tenantId, "SK1", company.Id, DateTime.UtcNow.AddYears(-9));

        var sick = new LeaveType
        {
            TenantId = tenantId, Code = "SICK", NameEn = "Sick Leave", Category = "Sick", IsActive = true,
        };
        db.LeaveTypes.Add(sick);
        db.LeavePolicies.Add(new LeavePolicy
        {
            TenantId = tenantId, Name = "KSA Sick", LeaveTypeId = sick.Id,
            AnnualEntitlementDays = 21m, AccrualMethod = "Monthly", Status = "Active",
        });
        await db.SaveChangesAsync();

        await LeaveSvc(db).AccrueMonthlyAsync(tenantId, CancellationToken.None);

        var balance = await db.EmployeeLeaveBalances.SingleAsync(b => b.EmployeeId == emp.Id);
        balance.Accrued.Should().Be(1.75m, "Art. 109 is an ANNUAL-leave rule; it must not inflate sick leave");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Art. 117 — sick leave: 30 days full, 60 days at ¾, 30 days unpaid
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Art117_OneHundredAndTwentyDays_IsFortyFiveUnpaidEquivalentDays()
    {
        // WORKED EXAMPLE (the arithmetic the money test below asserts on):
        //   days   1– 30  at 100%  →  30 × 0.00 =  0.00 unpaid-equivalent days
        //   days  31– 90  at  75%  →  60 × 0.25 = 15.00
        //   days  91–120  at   0%  →  30 × 1.00 = 30.00
        //                                        ───────
        //                                          45.00
        var a = KsaSickLeaveScale.Allocate(priorDaysInEntitlementYear: 0m, requestedDays: 120m);

        a.UnpaidEquivalentDays.Should().Be(45m);
        a.DaysBeyondScale.Should().Be(0m);
        a.Bands.Should().BeEquivalentTo(new[]
        {
            (Days: 30m, PayRate: 1.00m),
            (Days: 60m, PayRate: 0.75m),
            (Days: 30m, PayRate: 0.00m),
        }, o => o.WithStrictOrdering());
    }

    [Fact]
    public void Art117_AccumulatesIntermittently_NotPerRequest()
    {
        // Art. 117 applies "whether such leaves are continuous or intermittent", so a second absence
        // resumes where the first left off rather than restarting at day 1.
        //   25 days already taken, 20 more requested:
        //     days 26–30 (5 d) at 100% → 0.00
        //     days 31–45 (15 d) at 75% → 3.75
        //                                ─────
        //                                 3.75 unpaid-equivalent days
        var a = KsaSickLeaveScale.Allocate(priorDaysInEntitlementYear: 25m, requestedDays: 20m);

        a.UnpaidEquivalentDays.Should().Be(3.75m);
        a.Bands.Should().BeEquivalentTo(new[]
        {
            (Days: 5m, PayRate: 1.00m),
            (Days: 15m, PayRate: 0.75m),
        }, o => o.WithStrictOrdering());
    }

    [Fact]
    public void Art117_FirstThirtyDays_AreWhollyPaid()
    {
        KsaSickLeaveScale.Allocate(0m, 30m).UnpaidEquivalentDays.Should().Be(0m);
        KsaSickLeaveScale.Allocate(29m, 1m).UnpaidEquivalentDays.Should().Be(0m);
        // The 31st day is the first reduced one: 1 × 0.25.
        KsaSickLeaveScale.Allocate(30m, 1m).UnpaidEquivalentDays.Should().Be(0.25m);
    }

    [Fact]
    public void Art117_DaysBeyondTheScale_ContinueUnpaid()
    {
        var a = KsaSickLeaveScale.Allocate(0m, 150m);
        a.DaysBeyondScale.Should().Be(30m);
        // 15.00 (band 2) + 30.00 (band 3) + 30.00 (beyond, at band 3's 0% rate) = 75.00
        a.UnpaidEquivalentDays.Should().Be(75m);
    }

    [Fact]
    public void Art117_EntitlementYear_RunsFromTheFirstSickLeave_NotTheCalendarYear()
    {
        var first = new DateOnly(2025, 4, 10);

        KsaSickLeaveScale.EntitlementYearStart(first, new DateOnly(2025, 4, 10)).Should().Be(first);
        KsaSickLeaveScale.EntitlementYearStart(first, new DateOnly(2026, 1, 2)).Should().Be(first,
            "crossing 1 January does not reset the Art. 117 year");
        KsaSickLeaveScale.EntitlementYearStart(first, new DateOnly(2026, 4, 9)).Should().Be(first);
        KsaSickLeaveScale.EntitlementYearStart(first, new DateOnly(2026, 4, 11))
            .Should().Be(first.AddDays(365), "a new entitlement year opens 365 days after the first sick leave");
    }

    [Fact]
    public async Task OneHundredAndTwentySickDays_On12000Basic_Deducts18000()
    {
        // WORKED EXAMPLE — the money.
        //   basic SAR 12,000/month, LOP divisor 30 → day rate SAR 400.00
        //   full pay for 120 days would be 120 × 400          = SAR 48,000
        //   Art. 117 entitlement 30×400 + 60×400×0.75 + 30×0  = SAR 30,000
        //   deduction                                          = SAR 18,000
        //   cross-check: 45 unpaid-equivalent days × 400       = SAR 18,000  ✓
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var (sickType, emp) = await SeedKsaSickScenarioAsync(db, tenantId, basicSalary: 12_000m);

        var svc = await TestApprovalConfig.LeaveServiceAsync(db, tenantId);
        var start = new DateOnly(2026, 1, 5);
        var submitted = await svc.SubmitRequestAsync(tenantId, new LeaveRequest
        {
            TenantId = tenantId, CompanyId = emp.CompanyId, EmployeeId = emp.Id, EmployeeName = emp.FullName,
            LeaveTypeId = sickType.Id, StartDate = start, EndDate = start.AddDays(119), DayType = "Full",
            Reason = "Certified illness",
        }, CancellationToken.None);
        await svc.ApproveRequestAsync(tenantId, submitted.Id, Guid.NewGuid(), "Manager", null, CancellationToken.None);

        var impact = await db.LeavePayrollImpacts.SingleAsync(x => x.LeaveRequestId == submitted.Id);
        impact.Amount.Should().Be(18_000m);
        impact.Days.Should().Be(45m, "45 unpaid-equivalent days out of 120 calendar sick days");
        impact.ImpactType.Should().Contain("Deduction",
            "payroll only sums impacts whose type contains 'Deduction'");
        impact.ImpactType.Should().Contain("Art.117");
        impact.PayPeriod.Should().Be("2026-01");
        impact.Status.Should().Be("Pending");
    }

    [Fact]
    public async Task SickLeaveInsideTheFirstThirtyDays_StillProducesNoDeduction()
    {
        // Regression guard: the behaviour that existed before Art. 117 was implemented must survive
        // unchanged for any employee who has not exhausted the full-pay band.
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var (sickType, emp) = await SeedKsaSickScenarioAsync(db, tenantId, basicSalary: 12_000m);

        var svc = await TestApprovalConfig.LeaveServiceAsync(db, tenantId);
        var start = new DateOnly(2026, 1, 5);
        var submitted = await svc.SubmitRequestAsync(tenantId, new LeaveRequest
        {
            TenantId = tenantId, CompanyId = emp.CompanyId, EmployeeId = emp.Id, EmployeeName = emp.FullName,
            LeaveTypeId = sickType.Id, StartDate = start, EndDate = start.AddDays(9), DayType = "Full",
            Reason = "Certified illness",
        }, CancellationToken.None);
        await svc.ApproveRequestAsync(tenantId, submitted.Id, Guid.NewGuid(), "Manager", null, CancellationToken.None);

        (await db.LeavePayrollImpacts.AnyAsync(x => x.LeaveRequestId == submitted.Id))
            .Should().BeFalse("ten sick days sit wholly inside the Art. 117 full-pay band");
    }

    [Fact]
    public async Task IntermittentSickLeave_RollsUpAcrossRequestsInTheSameEntitlementYear()
    {
        // 25 days in January, then 20 more in April of the SAME Art. 117 year.
        //   second request: 5 d at 100% + 15 d at 75% → 3.75 unpaid-equivalent days
        //   3.75 × (12,000 ÷ 30) = 3.75 × 400 = SAR 1,500
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var (sickType, emp) = await SeedKsaSickScenarioAsync(db, tenantId, basicSalary: 12_000m);

        var svc = await TestApprovalConfig.LeaveServiceAsync(db, tenantId);

        var first = new DateOnly(2026, 1, 5);
        var r1 = await svc.SubmitRequestAsync(tenantId, new LeaveRequest
        {
            TenantId = tenantId, CompanyId = emp.CompanyId, EmployeeId = emp.Id, EmployeeName = emp.FullName,
            LeaveTypeId = sickType.Id, StartDate = first, EndDate = first.AddDays(24), DayType = "Full",
            Reason = "Certified illness",
        }, CancellationToken.None);
        await svc.ApproveRequestAsync(tenantId, r1.Id, Guid.NewGuid(), "Manager", null, CancellationToken.None);

        (await db.LeavePayrollImpacts.AnyAsync(x => x.LeaveRequestId == r1.Id))
            .Should().BeFalse("25 days is inside the full-pay band");

        var second = new DateOnly(2026, 4, 6);
        var r2 = await svc.SubmitRequestAsync(tenantId, new LeaveRequest
        {
            TenantId = tenantId, CompanyId = emp.CompanyId, EmployeeId = emp.Id, EmployeeName = emp.FullName,
            LeaveTypeId = sickType.Id, StartDate = second, EndDate = second.AddDays(19), DayType = "Full",
            Reason = "Certified illness",
        }, CancellationToken.None);
        await svc.ApproveRequestAsync(tenantId, r2.Id, Guid.NewGuid(), "Manager", null, CancellationToken.None);

        var impact = await db.LeavePayrollImpacts.SingleAsync(x => x.LeaveRequestId == r2.Id);
        impact.Days.Should().Be(3.75m);
        impact.Amount.Should().Be(1_500m,
            "the second absence resumes at day 26 of the SAME Art. 117 year, not at day 1");
    }

    [Fact]
    public async Task SickLeaveOnANonKsaEntity_IsNotReduced()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var (sickType, emp) = await SeedKsaSickScenarioAsync(db, tenantId, basicSalary: 12_000m, countryCode: "AE");

        var svc = await TestApprovalConfig.LeaveServiceAsync(db, tenantId);
        var start = new DateOnly(2026, 1, 5);
        var submitted = await svc.SubmitRequestAsync(tenantId, new LeaveRequest
        {
            TenantId = tenantId, CompanyId = emp.CompanyId, EmployeeId = emp.Id, EmployeeName = emp.FullName,
            LeaveTypeId = sickType.Id, StartDate = start, EndDate = start.AddDays(119), DayType = "Full",
            Reason = "Certified illness",
        }, CancellationToken.None);
        await svc.ApproveRequestAsync(tenantId, submitted.Id, Guid.NewGuid(), "Manager", null, CancellationToken.None);

        (await db.LeavePayrollImpacts.AnyAsync(x => x.LeaveRequestId == submitted.Id))
            .Should().BeFalse("the KSA Art. 117 scale must not reach a UAE entity");
    }

    [Fact]
    public async Task SickLeaveScale_CanBeDisabledByTheStatutoryRule_ForAnEmployerWhoPaysAbove()
    {
        // Art. 117 is a FLOOR. An employer whose contracts promise full sick pay is paying ABOVE
        // statute, which is lawful, and must be able to keep doing so.
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var (sickType, emp) = await SeedKsaSickScenarioAsync(db, tenantId, basicSalary: 12_000m);
        db.StatutoryRules.Add(PlatformRule(KsaLeaveHoursRuleKeys.SickApplyStatutoryScale, "false", "bool"));
        await db.SaveChangesAsync();

        var svc = await TestApprovalConfig.LeaveServiceAsync(db, tenantId);
        var start = new DateOnly(2026, 1, 5);
        var submitted = await svc.SubmitRequestAsync(tenantId, new LeaveRequest
        {
            TenantId = tenantId, CompanyId = emp.CompanyId, EmployeeId = emp.Id, EmployeeName = emp.FullName,
            LeaveTypeId = sickType.Id, StartDate = start, EndDate = start.AddDays(119), DayType = "Full",
            Reason = "Certified illness",
        }, CancellationToken.None);
        await svc.ApproveRequestAsync(tenantId, submitted.Id, Guid.NewGuid(), "Manager", null, CancellationToken.None);

        (await db.LeavePayrollImpacts.AnyAsync(x => x.LeaveRequestId == submitted.Id)).Should().BeFalse();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task<(LeaveType SickType, Employee Employee)> SeedKsaSickScenarioAsync(
        ZayraDbContext db, Guid tenantId, decimal basicSalary, string countryCode = "SA")
    {
        var company = new Company { TenantId = tenantId, LegalNameEn = "Test Co", CountryCode = countryCode };
        db.Companies.Add(company);
        var emp = AddEmployee(db, tenantId, "SICK1", company.Id, DateTime.UtcNow.AddYears(-3));
        var sick = new LeaveType
        {
            TenantId = tenantId, Code = "SICK", NameEn = "Sick Leave", Category = "Sick",
            IsPaid = true, IsActive = true,
        };
        db.LeaveTypes.Add(sick);
        await db.SaveChangesAsync();

        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = emp.Id, SalaryStructureId = Guid.NewGuid(),
            BasicSalary = basicSalary, EffectiveDate = new DateOnly(2020, 1, 1), IsActive = true,
        });
        db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
        {
            TenantId = tenantId, EmployeeId = emp.Id, LeaveTypeId = sick.Id,
            LeaveTypeName = sick.NameEn, EmployeeName = emp.FullName,
            Year = 2026, Entitled = 400, Accrued = 0,
        });
        await db.SaveChangesAsync();
        return (sick, emp);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Art. 98 — WHOSE country decides. The employing company's, not the person's.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE DEFECT, DIRECTION ONE — MISSED. Art. 98 was gated on <c>Employee.CountryCode</c>, a
    /// PERSONAL field that defaults to the empty string and is routinely never filled in. An
    /// employee of a Saudi company with a blank country code was therefore denied the reduced
    /// Ramadan baseline: eight hours worked produced ZERO overtime instead of the two hours Art. 98
    /// makes overtime-bearing, and the employee was under-paid for them.
    ///
    /// Art. 109 and Art. 117 have always resolved the employing company. Art. 98 now does too.
    /// </summary>
    [Fact]
    public async Task Art98_AppliesToAnEmployeeOfAKsaCompany_EvenWithNoPersonalCountryCode()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var ksaCompany = AddKsaCompany(db, tenantId);
        // CountryCode deliberately blank — the default for every employee nobody has filled in.
        var employee = AddEmployeeOfCompany(db, tenantId, ksaCompany.Id, "BLANK", string.Empty);
        await db.SaveChangesAsync();

        AddPunches(db, tenantId, employee.Id, RamadanWednesday);
        await db.SaveChangesAsync();

        await AttendanceSvc(db).ProcessAsync(
            tenantId, new ProcessAttendanceRequest(RamadanWednesday, RamadanWednesday, employee.Id),
            new RequestContext(null, null, Guid.NewGuid(), tenantId), CancellationToken.None);

        var daily = await db.AttendanceDailyRecords.SingleAsync(r => r.EmployeeId == employee.Id);
        daily.TotalWorkedMinutes.Should().Be(480);
        daily.OvertimeMinutes.Should().Be(120,
            "Art. 98 binds on the EMPLOYER's jurisdiction; a blank personal country code cannot "
            + "strip a statutory entitlement from an employee of a Saudi company");
    }

    /// <summary>
    /// THE DEFECT, DIRECTION TWO — LEAKED. The same wrong gate handed the KSA Ramadan reduction to
    /// an employee of a NON-KSA entity who happened to carry "SA" on their personal record, making
    /// two ordinary hours overtime-bearing under a labour law that does not govern that employer and
    /// over-paying them. Both directions came from the same line, which is why one fix closes both.
    /// </summary>
    [Fact]
    public async Task Art98_DoesNotReachAnEmployeeOfANonKsaCompany_WhateverTheirPersonalCountryCode()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var uaeCompany = new Company { TenantId = tenantId, LegalNameEn = "UAE Co", CountryCode = "AE" };
        db.Companies.Add(uaeCompany);
        var employee = AddEmployeeOfCompany(db, tenantId, uaeCompany.Id, "SAPERSON", "SA");
        await db.SaveChangesAsync();

        AddPunches(db, tenantId, employee.Id, RamadanWednesday);
        await db.SaveChangesAsync();

        await AttendanceSvc(db).ProcessAsync(
            tenantId, new ProcessAttendanceRequest(RamadanWednesday, RamadanWednesday, employee.Id),
            new RequestContext(null, null, Guid.NewGuid(), tenantId), CancellationToken.None);

        var daily = await db.AttendanceDailyRecords.SingleAsync(r => r.EmployeeId == employee.Id);
        daily.TotalWorkedMinutes.Should().Be(480);
        daily.OvertimeMinutes.Should().Be(0,
            "KSA Art. 98 does not govern a UAE employer, whatever country code sits on the person");
    }

    private static Company AddKsaCompany(ZayraDbContext db, Guid tenantId)
    {
        var company = new Company { TenantId = tenantId, LegalNameEn = "KSA Co", CountryCode = "SA" };
        db.Companies.Add(company);
        return company;
    }

    private static Employee AddEmployee(
        ZayraDbContext db, Guid tenantId, string code, Guid? companyId, DateTime joiningDate)
    {
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"{code}-{Guid.NewGuid():N}", EnglishName = code,
            FullName = code, Status = "Active", CompanyId = companyId, JoiningDate = joiningDate,
        };
        db.Employees.Add(employee);
        return employee;
    }

    /// <summary>
    /// An employee OF A KSA COMPANY. The employing entity is what puts them inside Art. 98, so the
    /// company is what the fixture models. This helper used to set Employee.CountryCode = "SA" and
    /// leave CompanyId null, which matched the gate Art. 98 used to use — a personal field that
    /// defaults empty, and therefore a fixture that could not distinguish a correct implementation
    /// from an incorrect one. Nationality is left unset on purpose: Art. 98 does not turn on it.
    /// </summary>
    private static Employee AddKsaEmployee(ZayraDbContext db, Guid tenantId)
    {
        var company = AddKsaCompany(db, tenantId);
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"KSA-{Guid.NewGuid():N}", EnglishName = "Ramadan Tester",
            FullName = "Ramadan Tester", Status = "Active", CompanyId = company.Id,
            JoiningDate = new DateTime(2020, 1, 1),
        };
        db.Employees.Add(employee);
        return employee;
    }

    private static Employee AddEmployeeOfCompany(
        ZayraDbContext db, Guid tenantId, Guid companyId, string code, string personalCountryCode)
    {
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"{code}-{Guid.NewGuid():N}", EnglishName = code,
            FullName = code, Status = "Active", CompanyId = companyId,
            CountryCode = personalCountryCode, JoiningDate = new DateTime(2020, 1, 1),
        };
        db.Employees.Add(employee);
        return employee;
    }

    /// <summary>Punch span 06:00 → 15:00 UTC = 540 minutes; the 60-minute policy break leaves 480 worked.</summary>
    private static void AddPunches(ZayraDbContext db, Guid tenantId, int employeeId, DateOnly date)
    {
        db.AttendanceRawEvents.Add(new AttendanceRawEvent
        {
            TenantId = tenantId, EmployeeId = employeeId, PunchDirection = "In",
            PunchTimestampUtc = date.ToDateTime(new TimeOnly(6, 0), DateTimeKind.Utc),
        });
        db.AttendanceRawEvents.Add(new AttendanceRawEvent
        {
            TenantId = tenantId, EmployeeId = employeeId, PunchDirection = "Out",
            PunchTimestampUtc = date.ToDateTime(new TimeOnly(15, 0), DateTimeKind.Utc),
        });
    }

    private static StatutoryRule PlatformRule(
        string key, string value, string dataType, DateTime? effectiveFrom = null) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = null,
        CountryCode = CountryCodes.Saudi,
        Jurisdiction = Jurisdictions.KsaMainland,
        RuleKey = key,
        RuleValue = value,
        DataType = dataType,
        EffectiveFrom = effectiveFrom ?? new DateTime(2005, 9, 27, 0, 0, 0, DateTimeKind.Utc),
        EffectiveTo = null,
    };

    private static AttendanceService AttendanceSvc(ZayraDbContext db) =>
        new(db, new NullNotifications(), new NullHttpClients());

    private static LeaveService LeaveSvc(ZayraDbContext db) =>
        new(db, new Zayra.Api.Infrastructure.Approvals.ApprovalRouter(db));

    private static ZayraDbContext CreateDb() => new(
        new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class NullNotifications : INotificationService
    {
        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NullHttpClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
