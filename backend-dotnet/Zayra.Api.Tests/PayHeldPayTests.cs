using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Payroll;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Models;
using static Zayra.Api.Tests.PayNext;

namespace Zayra.Api.Tests;

/// <summary>
/// Suspended pay is HELD, not dropped. The run computes a suspended employee's pay, keeps it out of net pay, and records
/// it with the employee, the run, the amount and the reason "suspended". Finance (payroll.approve, never the run's
/// preparer) then pays it in the next open run as a labelled earning, or forfeits it with a reason; both are audited.
/// When a forfeiture is lawful (Art. 69) is not decided here.
/// </summary>
public class PayHeldPayTests
{
    private static Task<List<PayrollAdjustment>> Holds(Zayra.Api.Data.ZayraDbContext db) =>
        db.PayrollAdjustments.AsNoTracking().Where(a => a.SourceType == HeldPay.HoldSource).ToListAsync();

    private static async Task Reactivate(Zayra.Api.Data.ZayraDbContext db, int employeeId)
    {
        (await db.Employees.FirstAsync(e => e.Id == employeeId)).Status = "Active";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    /// <summary>March processed with one suspended employee, then Approved; the employee reactivated; April open.</summary>
    private static async Task<(Co co, Employee suspended, PayrollRun mar, PayrollRun apr, PayrollAdjustment hold)> HeldInMarch(
        Zayra.Api.Data.ZayraDbContext db, bool reactivate = true, bool openApril = true)
    {
        var co = await SeedCompany(db, Guid.NewGuid());
        await AddEmployee(db, co, "Active");
        var s = await AddEmployee(db, co, "Suspended");
        var mar = await AddRun(db, co, 2026, 3);
        await Process(db, co, mar.Id);
        await SetStatus(db, mar.Id, "Approved");
        if (reactivate) await Reactivate(db, s.Id);
        var apr = openApril ? await AddRun(db, co, 2026, 4) : null!;
        var hold = (await Holds(db)).Single();
        return (co, s, mar, apr, hold);
    }

    [Fact]
    public async Task ASuspendedEmployeesPay_IsComputedAndRecordedAsHeld_AndIsNotPaid()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        var active = await AddEmployee(db, co, "Active");
        var s = await AddEmployee(db, co, "Suspended");
        var mar = await AddRun(db, co, 2026, 3);

        await Process(db, co, mar.Id);

        (await Slip(db, mar.Id, s.Id)).Should().BeNull("a suspended employee is not paid by the run");
        var hold = (await Holds(db)).Should().ContainSingle().Subject;
        hold.EmployeeId.Should().Be(s.Id);
        hold.PayrollRunId.Should().Be(mar.Id);
        hold.Amount.Should().Be(Basic + Housing, "the month's recurring package is computed and held, not lost");
        hold.Status.Should().Be(HeldPay.Held);
        hold.Reason.Should().Be("suspended");
        var run = await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == mar.Id);
        run.TotalNetSalary.Should().Be((await Slip(db, mar.Id, active.Id))!.NetSalary, "held pay is not in net pay");
    }

    [Fact]
    public async Task TheRunScreen_KeepsListingAHold_AfterTheEmployeeIsReactivated_UntilItIsDecided()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var (co, s, mar, apr, hold) = await HeldInMarch(db);

        var root = Json(await Payroll(db, co.TenantId, Checker).GetRunHeldPay(apr.Id, CancellationToken.None));

        var item = root.GetProperty("items").EnumerateArray().Should().ContainSingle().Subject;
        item.GetProperty("holdId").GetGuid().Should().Be(hold.Id);
        item.GetProperty("employeeId").GetInt32().Should().Be(s.Id);
        item.GetProperty("status").GetString().Should().Be(HeldPay.Held);
        item.GetProperty("heldPeriod").GetString().Should().Be("2026-03");
        item.GetProperty("heldByThisRun").GetBoolean().Should().BeFalse();
        root.GetProperty("heldTotal").GetDecimal().Should().Be(Basic + Housing);
        mar.Id.Should().NotBe(apr.Id);
    }

    [Fact]
    public async Task ThePreparerOfTheRun_CannotReleaseItsHold()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var (co, _, _, _, hold) = await HeldInMarch(db);

        var result = await Payroll(db, co.TenantId, Maker).DecideHeldPay(hold.Id, new HeldPayDecisionRequest("pay", null), CancellationToken.None);

        Status(result).Should().Be(403);
        Json(result).GetProperty("error").GetString().Should().Be("maker_checker_violation");
        (await Holds(db)).Single().Status.Should().Be(HeldPay.Held);
    }

    [Fact]
    public async Task Releasing_PaysTheHeldAmountOnceInTheNextOpenRun_AsALabelledEarning_AndIsAudited()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var (co, s, _, apr, hold) = await HeldInMarch(db);

        var result = await Payroll(db, co.TenantId, Checker).DecideHeldPay(hold.Id, new HeldPayDecisionRequest("pay", null), CancellationToken.None);

        Status(result).Should().Be(200);
        (await Holds(db)).Single().Status.Should().Be(HeldPay.Released);
        var release = await db.PayrollAdjustments.AsNoTracking().SingleAsync(a => a.SourceType == HeldPay.ReleaseSource);
        release.SourceId.Should().Be(hold.Id);
        release.PayrollRunId.Should().Be(apr.Id);
        release.Amount.Should().Be(Basic + Housing);
        release.Status.Should().Be("Approved");
        (await db.AuditLogs.AsNoTracking().Where(a => a.Action == "payroll.held_pay.released" && a.EntityId == hold.Id.ToString()).CountAsync())
            .Should().Be(1);

        var again = await Payroll(db, co.TenantId, Checker).DecideHeldPay(hold.Id, new HeldPayDecisionRequest("pay", null), CancellationToken.None);
        Status(again).Should().Be(409, "a hold is decided exactly once");
        (await db.PayrollAdjustments.AsNoTracking().CountAsync(a => a.SourceType == HeldPay.ReleaseSource)).Should().Be(1);

        await Process(db, co, apr.Id);
        var line = (await Earnings(db, apr.Id, s.Id)).Should().ContainSingle(l => l.ComponentCode == "ADJ_HELD_PAY_RELEASED").Subject;
        line.Amount.Should().Be(Basic + Housing);
        line.ComponentName.Should().Contain("Held pay released").And.Contain("2026-03");
        (await Slip(db, apr.Id, s.Id))!.GrossSalary.Should().Be(2 * (Basic + Housing), "April's own pay plus March's released pay");
    }

    [Fact]
    public async Task Forfeiting_NeedsAReason_IsAudited_AndPaysNothing()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var (co, s, _, apr, hold) = await HeldInMarch(db);
        var finance = Payroll(db, co.TenantId, Checker);

        Status(await finance.DecideHeldPay(hold.Id, new HeldPayDecisionRequest("forfeit", " "), CancellationToken.None)).Should().Be(400);
        Status(await finance.DecideHeldPay(hold.Id, new HeldPayDecisionRequest("forfeit", "Investigation upheld; written decision on file"), CancellationToken.None))
            .Should().Be(200);

        (await Holds(db)).Single().Status.Should().Be(HeldPay.Forfeited);
        (await db.AuditLogs.AsNoTracking().CountAsync(a => a.Action == "payroll.held_pay.forfeited" && a.EntityId == hold.Id.ToString())).Should().Be(1);
        (await db.PayrollAdjustments.AsNoTracking().AnyAsync(a => a.SourceType == HeldPay.ReleaseSource)).Should().BeFalse();
        Status(await finance.DecideHeldPay(hold.Id, new HeldPayDecisionRequest("pay", null), CancellationToken.None)).Should().Be(409);
        await Process(db, co, apr.Id);
        (await EarningSum(db, apr.Id, s.Id, "ADJ_HELD_PAY_RELEASED")).Should().Be(0m);
    }

    [Fact]
    public async Task Releasing_IsRefused_WithNoOpenRun_WhileStillSuspended_OrWhileTheHoldingRunCanBeReprocessed()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var (co, s, mar, _, hold) = await HeldInMarch(db, reactivate: true, openApril: false);
        var finance = Payroll(db, co.TenantId, Checker);

        var noRun = await finance.DecideHeldPay(hold.Id, new HeldPayDecisionRequest("pay", null), CancellationToken.None);
        Status(noRun).Should().Be(409);
        Json(noRun).GetProperty("error").GetString().Should().Be("held_pay_no_open_run");

        await AddRun(db, co, 2026, 4);
        (await db.Employees.FirstAsync(e => e.Id == s.Id)).Status = "Suspended";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var stillSuspended = await finance.DecideHeldPay(hold.Id, new HeldPayDecisionRequest("pay", null), CancellationToken.None);
        Json(stillSuspended).GetProperty("error").GetString().Should().Be("held_pay_employee_still_suspended");

        await SetStatus(db, mar.Id, "Failed");
        var open = await finance.DecideHeldPay(hold.Id, new HeldPayDecisionRequest("forfeit", "Not decided yet but trying"), CancellationToken.None);
        Json(open).GetProperty("error").GetString().Should().Be("held_pay_run_open");
        (await Holds(db)).Single().Status.Should().Be(HeldPay.Held, "nothing was decided by any refused attempt");
    }

    [Fact]
    public void Deciding_NeedsPayrollApprove_AndReadingNeedsPayrollRead()
    {
        static string[] Needs(string action) => typeof(PayrollController).GetMethod(action)!
            .GetCustomAttributes<HasPermissionAttribute>().Single().Permissions.ToArray();
        Needs(nameof(PayrollController.DecideHeldPay)).Should().Equal("payroll.approve");
        Needs(nameof(PayrollController.GetRunHeldPay)).Should().Equal("payroll.read");
    }

    // ══ Money audit 2 ══════════════════════════════════════════════════════════════════════════════

    private static async Task SetEmployeeStatus(Zayra.Api.Data.ZayraDbContext db, int employeeId, string status)
    {
        (await db.Employees.FirstAsync(e => e.Id == employeeId)).Status = status;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static Task<List<PayrollAdjustment>> Releases(Zayra.Api.Data.ZayraDbContext db) =>
        db.PayrollAdjustments.AsNoTracking().Where(a => a.SourceType == HeldPay.ReleaseSource).ToListAsync();

    /// <summary>P1-A1: hold -> release -> void the holding run -> replacement run: the month is never held or paid twice.</summary>
    [Fact]
    public async Task AVoidOfTheHoldingRunAfterARelease_LetsNoReplacementHoldOrPayThatMonthAgain()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        await AddEmployee(db, co, "Active");
        var s = await AddEmployee(db, co, "Suspended");
        var mar = await AddRun(db, co, 2026, 3);
        await Process(db, co, mar.Id);                       // March stays Processed: closed to re-processing, so a hold may be decided
        await SetEmployeeStatus(db, s.Id, "Active");
        var apr = await AddRun(db, co, 2026, 4);
        var hold = (await Holds(db)).Single();
        Status(await Payroll(db, co.TenantId, Checker).DecideHeldPay(hold.Id, new HeldPayDecisionRequest("pay", null), CancellationToken.None)).Should().Be(200);

        await SetEmployeeStatus(db, s.Id, "Suspended");      // suspended again while March is corrected
        (await new PayrollVoidService(db).VoidAsync(mar.Id, co.TenantId, null, "System", "wrong cost centre", CancellationToken.None)).IsVoided.Should().BeTrue();
        db.ChangeTracker.Clear();
        var marReplacement = await AddRun(db, co, 2026, 3);
        await Process(db, co, marReplacement.Id);

        (await Holds(db)).Where(h => h.PayrollRunId == marReplacement.Id).Should().BeEmpty(
            "March's pay was already released; the replacement run must not hold it again");
        await SetEmployeeStatus(db, s.Id, "Active");
        await Process(db, co, apr.Id);
        (await Releases(db)).Should().ContainSingle();
        (await EarningSum(db, apr.Id, s.Id, "ADJ_HELD_PAY_RELEASED")).Should().Be(Basic + Housing, "paid exactly once");
    }

    /// <summary>P1-A2: deleting the run a release was put on returns the hold to Held, so nothing is lost.</summary>
    [Fact]
    public async Task DeletingTheRunAReleaseWasPutOn_ReturnsTheHoldToHeld()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var (co, _, _, apr, hold) = await HeldInMarch(db);
        Status(await Payroll(db, co.TenantId, Checker).DecideHeldPay(hold.Id, new HeldPayDecisionRequest("pay", null), CancellationToken.None)).Should().Be(200);

        var deleted = await Payroll(db, co.TenantId, Checker).DeleteRun(apr.Id, CancellationToken.None);
        deleted.Should().BeOfType<NoContentResult>();
        db.ChangeTracker.Clear();

        (await Holds(db)).Single().Status.Should().Be(HeldPay.Held, "the pay is owed again and Finance can release it into another run");
        (await Releases(db)).Should().BeEmpty("no release may sit on a run that no longer exists");
        (await db.AuditLogs.AsNoTracking().CountAsync(a => a.Action == "payroll.held_pay.release_returned" && a.EntityId == hold.Id.ToString())).Should().Be(1);
    }

    /// <summary>P1-A2: voiding the run that paid a release returns the hold to Held; re-released, it is paid exactly once.</summary>
    [Fact]
    public async Task VoidingTheRunThatPaidARelease_ReturnsTheHold_AndTheReleaseIsPaidOnceAfterwards()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var (co, s, _, apr, hold) = await HeldInMarch(db);
        var finance = Payroll(db, co.TenantId, Checker);
        Status(await finance.DecideHeldPay(hold.Id, new HeldPayDecisionRequest("pay", null), CancellationToken.None)).Should().Be(200);
        await Process(db, co, apr.Id);
        (await EarningSum(db, apr.Id, s.Id, "ADJ_HELD_PAY_RELEASED")).Should().Be(Basic + Housing);

        (await new PayrollVoidService(db).VoidAsync(apr.Id, co.TenantId, null, "System", "wrong bank file", CancellationToken.None)).IsVoided.Should().BeTrue();
        db.ChangeTracker.Clear();

        (await Holds(db)).Single().Status.Should().Be(HeldPay.Held, "the voided run no longer pays it");
        (await Releases(db)).Should().BeEmpty();
        var aprReplacement = await AddRun(db, co, 2026, 4);
        Status(await finance.DecideHeldPay(hold.Id, new HeldPayDecisionRequest("pay", null), CancellationToken.None)).Should().Be(200);
        await Process(db, co, aprReplacement.Id);
        (await EarningSum(db, aprReplacement.Id, s.Id, "ADJ_HELD_PAY_RELEASED")).Should().Be(Basic + Housing);
        (await Releases(db)).Should().ContainSingle();
    }
}
