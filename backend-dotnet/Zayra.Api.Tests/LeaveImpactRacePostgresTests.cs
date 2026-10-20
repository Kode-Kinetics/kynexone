using System.Data.Common;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Against REAL PostgreSQL: the leave paths that delete payroll impacts, and the cancellation decision around them.
///
/// <list type="bullet">
///   <item>Leave approval clears the day's pending absence charge. It now deletes by the day's natural key under the
///     attendance day lock, so a charge an attendance reprocess REPLACED (new id) is cleared too, and a charge a
///     payroll run consumed first is left alone and reported.</item>
///   <item>Cancelling, and approving or rejecting a cancellation, run in one transaction under the employee's leave
///     lock and re-check the leave's status under it: one decision wins, the balance is released once, and the
///     cancellation row's decision commits with the cancellation it describes.</item>
/// </list>
/// Seeding note: the day is a Wednesday. A KSA Friday is a rest day, so a request for it has zero working days.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class LeaveImpactRacePostgresTests
{
    private readonly PostgresFixture _fx;
    public LeaveImpactRacePostgresTests(PostgresFixture fx) => _fx = fx;

    private sealed record F(Guid TenantId, int EmployeeId, Guid HrUserId, Guid LeaveTypeId, DateOnly Day, Guid ChargeId);

    // ── Approval clears the absence charge ──────────────────────────────────────────────────────

    [Fact]
    public async Task Approval_NoRace_DeletesTheChargeAndSetsTheDayOnLeave()
    {
        var f = await SeedAsync();
        var id = await SubmitAsync(f);
        await ApproveAsync(f, id, null);

        await using var db = _fx.CreateDb();
        (await db.AttendancePayrollImpacts.AnyAsync(x => x.TenantId == f.TenantId)).Should().BeFalse();
        (await db.AttendanceDailyRecords.SingleAsync(x => x.TenantId == f.TenantId)).Status.Should().Be(AttendanceStatuses.OnLeave);
    }

    [Fact]
    public async Task Approval_ChargeConsumedByARunFirst_IsLeftAloneAndReportedAsSettled()
    {
        var f = await SeedAsync();
        var id = await SubmitAsync(f);
        var gate = new BeforeFirst("DELETE FROM attendance_payroll_impacts", async () =>
        {
            await using var other = _fx.CreateDb();
            await other.AttendancePayrollImpacts.Where(x => x.Id == f.ChargeId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, "Processed"));
        });
        await ApproveAsync(f, id, gate);
        gate.Fired.Should().BeTrue();

        await using var db = _fx.CreateDb();
        (await db.AttendancePayrollImpacts.SingleAsync(x => x.TenantId == f.TenantId)).Status.Should().Be("Processed");
        (await db.AttendanceDailyRecords.SingleAsync(x => x.TenantId == f.TenantId)).Status
            .Should().Be(AttendanceStatuses.Absent, "the day stays exactly as the run paid it");
        (await db.LeaveAuditLogs.SingleAsync(a => a.TenantId == f.TenantId && a.Action == "Approved")).Reason
            .Should().Contain("[FLAG-PAYROLL]");
    }

    /// <summary>
    /// A reprocess replaced the charge with a new pending row (new id) between the approval's read and its delete.
    /// Deleting by the old id alone cleared nothing, yet the day became On leave and the replacement survived: the
    /// next run docked pay for an approved leave day.
    /// </summary>
    [Fact]
    public async Task Approval_ChargeReplacedByAReprocess_ClearsTheReplacementToo()
    {
        var f = await SeedAsync();
        var id = await SubmitAsync(f);
        var gate = new BeforeFirst("DELETE FROM attendance_payroll_impacts", async () =>
        {
            await using var other = _fx.CreateDb();
            await other.AttendancePayrollImpacts.Where(x => x.Id == f.ChargeId).ExecuteDeleteAsync();
            other.AttendancePayrollImpacts.Add(new AttendancePayrollImpact
            {
                TenantId = f.TenantId, EmployeeId = f.EmployeeId, WorkDate = f.Day,
                ImpactType = AttendanceImpactTypes.AbsenceDeduction, Minutes = 480, Status = "PendingPayroll",
            });
            await other.SaveChangesAsync();
        });
        await ApproveAsync(f, id, gate);
        gate.Fired.Should().BeTrue();

        await using var db = _fx.CreateDb();
        (await db.AttendanceDailyRecords.SingleAsync(x => x.TenantId == f.TenantId)).Status.Should().Be(AttendanceStatuses.OnLeave);
        (await db.AttendancePayrollImpacts.AsNoTracking().CountAsync(x => x.TenantId == f.TenantId && x.Status != "Processed"))
            .Should().Be(0, "an approved leave day must not keep a pending absence charge for the next run to deduct");
    }

    /// <summary>
    /// A reprocess of the day is in flight: it holds the day lock and has written Absent plus a pending charge (it read
    /// the leave as not yet approved), uncommitted. The approval used to pick the days to lock from an unlocked read of
    /// COMMITTED absent days, found none, and approved without waiting, leaving an approved leave over an Absent day
    /// with a pending charge. It now locks every date of the leave, so it waits for the reprocess and clears its charge.
    /// </summary>
    [Fact]
    public async Task Approval_ReprocessInFlightForTheDay_WaitsForIt_AndClearsItsCharge()
    {
        var f = await SeedAsync();
        await using (var clean = _fx.CreateDb())
        {
            await clean.AttendancePayrollImpacts.Where(x => x.TenantId == f.TenantId).ExecuteDeleteAsync();
            await clean.AttendanceDailyRecords.Where(x => x.TenantId == f.TenantId).ExecuteDeleteAsync();
        }
        var id = await SubmitAsync(f);

        // The reprocess's own transaction, held open by the test (no retrying strategy, so it may be user-initiated).
        await using var job = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(_fx.ConnectionString)
            .AddInterceptors(Zayra.Api.Infrastructure.Data.AdvisoryXactLockGuardInterceptor.Instance).Options);
        await using var tx = await job.Database.BeginTransactionAsync();
        await AttendanceDailyRecordLock.AcquireInCurrentTransactionAsync(job, f.TenantId, f.EmployeeId, f.Day, CancellationToken.None);
        var daily = new AttendanceDailyRecord { TenantId = f.TenantId, EmployeeId = f.EmployeeId, EmployeeName = "Emp", WorkDate = f.Day, Status = AttendanceStatuses.Absent, UndertimeMinutes = 480 };
        job.AttendanceDailyRecords.Add(daily);
        job.AttendancePayrollImpacts.Add(new AttendancePayrollImpact { TenantId = f.TenantId, EmployeeId = f.EmployeeId, WorkDate = f.Day, ImpactType = AttendanceImpactTypes.AbsenceDeduction, Minutes = 480, Status = "PendingPayroll", DailyRecordId = daily.Id });
        await job.SaveChangesAsync();

        var approve = ApproveAsync(f, id, null);
        var finishedWhileReprocessOpen = await Task.WhenAny(approve, Task.Delay(1500)) == approve;
        await tx.CommitAsync();
        await approve;

        finishedWhileReprocessOpen.Should().BeFalse("the approval must wait for the in-flight reprocess of a leave day");
        await using var db = _fx.CreateDb();
        (await db.LeaveRequests.AsNoTracking().SingleAsync(x => x.Id == id)).Status.Should().Be("Approved");
        (await db.AttendanceDailyRecords.AsNoTracking().SingleAsync(x => x.TenantId == f.TenantId)).Status.Should().Be(AttendanceStatuses.OnLeave);
        (await db.AttendancePayrollImpacts.AsNoTracking().CountAsync(x => x.TenantId == f.TenantId && x.Status != "Processed"))
            .Should().Be(0, "an approved leave day must not keep a pending absence charge for the next run to deduct");
    }

    // ── Cancellation ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A leave already awaiting a cancellation decision goes to the reviewer. A direct cancel used to cancel it outright
    /// (releasing its used days) and left the Pending cancellation row with no way to decide it.
    /// </summary>
    [Fact]
    public async Task Cancel_OnALeaveAwaitingACancellationDecision_IsRefused()
    {
        var (f, id, cancellationId) = await SeedCancellationRequestedAsync();

        IActionResult result;
        await using (var db = Db())
            result = await Controller(db, f.TenantId).Cancel(id, new CancelLeaveRequest("direct"), CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
        await using var check = _fx.CreateDb();
        (await check.LeaveRequests.AsNoTracking().SingleAsync(x => x.Id == id)).Status.Should().Be("CancellationRequested");
        (await check.LeaveCancellationRequests.AsNoTracking().SingleAsync(x => x.Id == cancellationId)).Status.Should().Be("Pending");
        (await check.LeaveBalanceTransactions.CountAsync(x => x.TenantId == f.TenantId && x.TransactionType == "Reversed")).Should().Be(0);
    }

    /// <summary>A direct cancel closes any Pending cancellation row of that leave in the same transaction.</summary>
    [Fact]
    public async Task Cancel_DirectCancel_ClosesAPendingCancellationRowOfThatLeave()
    {
        var f = await SeedAsync();
        var id = await SubmitAsync(f);
        Guid cancellationId;
        await using (var s = _fx.CreateDb())
        {
            var c = new LeaveCancellationRequest { TenantId = f.TenantId, LeaveRequestId = id, EmployeeId = f.EmployeeId, Reason = "stale", Status = "Pending" };
            s.LeaveCancellationRequests.Add(c);
            await s.SaveChangesAsync();
            cancellationId = c.Id;
        }

        await using (var db = Db())
            await new LeaveService(db, new ApprovalRouter(db)).CancelRequestAsync(f.TenantId, id, "HR", "no longer needed");

        await using var check = _fx.CreateDb();
        (await check.LeaveRequests.AsNoTracking().SingleAsync(x => x.Id == id)).Status.Should().Be("Cancelled");
        (await check.LeaveCancellationRequests.AsNoTracking().SingleAsync(x => x.Id == cancellationId)).Status
            .Should().Be("Closed", "no Pending cancellation row may outlive the leave it asks to cancel");
    }

    /// <summary>
    /// The commit of a rejection fails ambiguously and the strategy asks whether it landed. An earlier cancellation
    /// of the same leave, rejected by the same reviewer, used to answer "yes": nothing had committed, yet the call
    /// reported success. The check is now by this cancellation row's id, so the strategy retries and the decision lands.
    /// </summary>
    [Fact]
    public async Task RejectCancellation_AmbiguousCommit_IsVerifiedAgainstThisCancellationRow()
    {
        var (f, id, cancellationId) = await SeedCancellationRequestedAsync();
        await using (var s = _fx.CreateDb())
        {
            s.LeaveCancellationRequests.Add(new LeaveCancellationRequest
            {
                TenantId = f.TenantId, LeaveRequestId = id, EmployeeId = f.EmployeeId, Reason = "earlier", Status = "Rejected",
                ReviewedByName = "HR Reviewer", ReviewedAtUtc = DateTime.UtcNow.AddDays(-3), CreatedAtUtc = DateTime.UtcNow.AddDays(-4),
            });
            await s.SaveChangesAsync();
        }

        var failCommit = new FailFirstCommit();
        IActionResult result;
        await using (var db = Db(failCommit))
            result = await Controller(db, f.TenantId).RejectCancellation(id, new CancellationDecisionRequest("no"), CancellationToken.None);
        failCommit.Fired.Should().BeTrue();

        result.Should().BeOfType<OkObjectResult>();
        await using var check = _fx.CreateDb();
        (await check.LeaveCancellationRequests.AsNoTracking().SingleAsync(x => x.Id == cancellationId)).Status.Should().Be("Rejected",
            "a success answer must mean THIS cancellation was decided");
        (await check.LeaveRequests.AsNoTracking().SingleAsync(x => x.Id == id)).Status.Should().Be("Approved");
    }

    /// <summary>A transient failure on the first attempt: the retry releases the balance once and the
    /// cancellation's approval is persisted.</summary>
    [Fact]
    public async Task ApproveCancellation_TransientRetry_ReleasesOnce_AndPersistsTheDecision()
    {
        var (f, id, cancellationId) = await SeedCancellationRequestedAsync();

        var fail = new FailOnce("INSERT INTO leave_audit_logs", transient: true);
        await using (var db = Db(fail))
            (await Controller(db, f.TenantId).ApproveCancellation(id, new CancellationDecisionRequest("ok"), CancellationToken.None))
                .Should().BeOfType<OkObjectResult>();
        fail.Fired.Should().BeTrue();

        await AssertCancelledOnceAsync(f, id, cancellationId);
    }

    /// <summary>
    /// The decision used to be a second save after the cancellation committed. A failure in between left the leave
    /// Cancelled and the cancellation Pending forever (re-approving: 400 "already cancelled"; rejecting: 409).
    /// </summary>
    [Fact]
    public async Task ApproveCancellation_FailureWritingTheDecision_CommitsNothing()
    {
        var (f, id, cancellationId) = await SeedCancellationRequestedAsync();

        var fail = new FailOnce("UPDATE leave_cancellation_requests", transient: false);
        await using (var db = Db(fail))
        {
            var act = () => Controller(db, f.TenantId).ApproveCancellation(id, new CancellationDecisionRequest("ok"), CancellationToken.None);
            await act.Should().ThrowAsync<Exception>();
        }
        fail.Fired.Should().BeTrue();

        await using var check = _fx.CreateDb();
        var leave = await check.LeaveRequests.AsNoTracking().SingleAsync(x => x.Id == id);
        var cancellation = await check.LeaveCancellationRequests.AsNoTracking().SingleAsync(x => x.Id == cancellationId);
        (leave.Status, cancellation.Status).Should().Be(("CancellationRequested", "Pending"),
            "the leave's cancellation and the decision that caused it commit together or not at all");
        (await check.LeaveBalanceTransactions.CountAsync(x => x.TenantId == f.TenantId && x.TransactionType == "Reversed")).Should().Be(0);
        (await check.LeavePayrollImpacts.CountAsync(x => x.TenantId == f.TenantId)).Should().Be(1);
    }

    /// <summary>Two cancels of the same leave interleave: the second must see the first and refuse.</summary>
    [Fact]
    public async Task ConcurrentCancels_ReleaseTheBalanceOnce()
    {
        var f = await SeedAsync(unpaid: true);
        var id = await SubmitAsync(f);
        await ApproveAsync(f, id, null);

        Exception? second = null;
        var gate = new AfterFirstRead("FROM leave_requests", async () =>
        {
            await using var other = Db();
            await new LeaveService(other, new ApprovalRouter(other)).CancelRequestAsync(f.TenantId, id, "HR-2", "dup");
        });
        await using (var db = Db(gate))
        {
            try { await new LeaveService(db, new ApprovalRouter(db)).CancelRequestAsync(f.TenantId, id, "HR-1", "first"); }
            catch (InvalidOperationException ex) { second = ex; }
        }
        gate.Fired.Should().BeTrue();

        await using var check = _fx.CreateDb();
        (await check.LeaveBalanceTransactions.CountAsync(x => x.TenantId == f.TenantId && x.TransactionType == "Reversed"))
            .Should().Be(1, "one leave is released once, however many cancels race");
        (await check.LeaveAuditLogs.CountAsync(x => x.TenantId == f.TenantId && x.Action == "Cancelled")).Should().Be(1);
        second.Should().NotBeNull("the losing cancel is refused, not applied a second time");
    }

    /// <summary>A reject of the cancellation commits while an approval of it is in flight: the reject stands.</summary>
    [Fact]
    public async Task RejectCommittedDuringApproveCancellation_TheRejectStands()
    {
        var (f, id, cancellationId) = await SeedCancellationRequestedAsync();

        var gate = new AfterFirstRead("FROM leave_requests", async () =>
        {
            await using var other = Db();
            (await Controller(other, f.TenantId).RejectCancellation(id, new CancellationDecisionRequest("no"), CancellationToken.None))
                .Should().BeOfType<OkObjectResult>();
        });
        IActionResult approveResult;
        await using (var db = Db(gate))
            approveResult = await Controller(db, f.TenantId).ApproveCancellation(id, new CancellationDecisionRequest("ok"), CancellationToken.None);
        gate.Fired.Should().BeTrue();

        await using var check = _fx.CreateDb();
        var leave = await check.LeaveRequests.AsNoTracking().SingleAsync(x => x.Id == id);
        var cancellation = await check.LeaveCancellationRequests.AsNoTracking().SingleAsync(x => x.Id == cancellationId);
        (leave.Status, cancellation.Status).Should().Be(("Approved", "Rejected"),
            "a committed rejection must not be overwritten by an approval that read the request before it");
        (await check.LeaveBalanceTransactions.CountAsync(x => x.TenantId == f.TenantId && x.TransactionType == "Reversed")).Should().Be(0);
        approveResult.Should().NotBeOfType<OkObjectResult>();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private async Task AssertCancelledOnceAsync(F f, Guid id, Guid cancellationId)
    {
        await using var check = _fx.CreateDb();
        (await check.LeaveCancellationRequests.AsNoTracking().SingleAsync(x => x.Id == cancellationId)).Status.Should().Be("Approved");
        (await check.LeaveRequests.AsNoTracking().SingleAsync(x => x.Id == id)).Status.Should().Be("Cancelled");
        (await check.EmployeeLeaveBalances.AsNoTracking().SingleAsync(x => x.TenantId == f.TenantId && x.EmployeeId == f.EmployeeId))
            .Used.Should().Be(0m);
        (await check.LeaveBalanceTransactions.CountAsync(x => x.TenantId == f.TenantId && x.TransactionType == "Reversed")).Should().Be(1);
        (await check.LeaveAuditLogs.CountAsync(x => x.TenantId == f.TenantId && x.Action == "Cancelled")).Should().Be(1);
        (await check.LeavePayrollImpacts.CountAsync(x => x.TenantId == f.TenantId)).Should().Be(0);
    }

    private async Task<(F f, Guid leaveId, Guid cancellationId)> SeedCancellationRequestedAsync()
    {
        var f = await SeedAsync(unpaid: true);
        var id = await SubmitAsync(f);
        await ApproveAsync(f, id, null);
        await using var s = _fx.CreateDb();
        var lr = await s.LeaveRequests.SingleAsync(x => x.Id == id);
        lr.Status = "CancellationRequested";
        var c = new LeaveCancellationRequest { TenantId = f.TenantId, LeaveRequestId = id, EmployeeId = f.EmployeeId, Reason = "x", Status = "Pending" };
        s.LeaveCancellationRequests.Add(c);
        await s.SaveChangesAsync();
        (await s.LeavePayrollImpacts.CountAsync(x => x.TenantId == f.TenantId)).Should().Be(1, "the unpaid leave's deduction exists before the cancel");
        return (f, id, c.Id);
    }

    private async Task<F> SeedAsync(bool unpaid = false)
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var hrUserId = Guid.NewGuid();
        var day = new DateOnly(2026, 10, 14); // a Wednesday
        var lt = new LeaveType { TenantId = tenantId, Code = "SICK", NameEn = "Sick Leave", Category = "Sick", IsActive = true, IsPaid = !unpaid };
        var hr = new Employee { TenantId = tenantId, UserAccountId = hrUserId, EmployeeCode = $"H{Guid.NewGuid():N}"[..12], FullName = "HR", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-5), WorkEmail = $"{Guid.NewGuid():N}@p.test" };
        db.LeaveTypes.Add(lt); db.Employees.Add(hr);
        await db.SaveChangesAsync();
        var emp = new Employee { TenantId = tenantId, EmployeeCode = $"E{Guid.NewGuid():N}"[..12], FullName = "Emp", UserAccountId = Guid.NewGuid(), Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-2), WorkEmail = $"{Guid.NewGuid():N}@p.test" };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();
        db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance { TenantId = tenantId, EmployeeId = emp.Id, EmployeeName = "Emp", LeaveTypeId = lt.Id, LeaveTypeName = lt.NameEn, Year = day.Year, Entitled = 30 });
        var daily = new AttendanceDailyRecord { TenantId = tenantId, EmployeeId = emp.Id, EmployeeName = "Emp", WorkDate = day, Status = AttendanceStatuses.Absent, UndertimeMinutes = 480 };
        db.AttendanceDailyRecords.Add(daily);
        var charge = new AttendancePayrollImpact { TenantId = tenantId, EmployeeId = emp.Id, WorkDate = day, ImpactType = AttendanceImpactTypes.AbsenceDeduction, Minutes = 480, Status = "PendingPayroll", DailyRecordId = daily.Id };
        db.AttendancePayrollImpacts.Add(charge);
        await db.SaveChangesAsync();
        return new F(tenantId, emp.Id, hrUserId, lt.Id, day, charge.Id);
    }

    private ZayraDbContext Db(params IInterceptor[] extra)
    {
        var o = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(_fx.ConnectionString, PostgresFixture.ProductionProviderOptions)
            .AddInterceptors(RowLockingInterceptor.Instance, Zayra.Api.Infrastructure.Data.AdvisoryXactLockGuardInterceptor.Instance);
        if (extra.Length > 0) o.AddInterceptors(extra);
        return new ZayraDbContext(o.Options);
    }

    private async Task<Guid> SubmitAsync(F f)
    {
        await using var db = _fx.CreateDb();
        var svc = await TestApprovalConfig.LeaveServiceAsync(db, f.TenantId, "HR");
        var r = await svc.SubmitRequestAsync(f.TenantId, new LeaveRequest { TenantId = f.TenantId, EmployeeId = f.EmployeeId, LeaveTypeId = f.LeaveTypeId, StartDate = f.Day, EndDate = f.Day, DayType = "Full" });
        return r.Id;
    }

    private async Task ApproveAsync(F f, Guid id, IInterceptor? gate)
    {
        await using var db = gate is null ? Db() : Db(gate);
        await new LeaveService(db, new ApprovalRouter(db)).ApproveRequestAsync(f.TenantId, id, f.HrUserId, "HR", "ok");
    }

    private static LeaveRequestsController Controller(ZayraDbContext db, Guid tenantId)
    {
        var controller = new LeaveRequestsController(db, new LeaveService(db, new ApprovalRouter(db)), new OrgWideScope(), TestNotifications.For(db));
        var claims = new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Name, "HR Reviewer"),
            new Claim(ClaimTypes.Role, "HR Manager"),
            new Claim("permission", "leave.write"),
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
        };
        return controller;
    }

    private sealed class OrgWideScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
            => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }

    /// <summary>Runs <c>before</c> once, immediately before the first command containing <c>marker</c> is sent.</summary>
    private sealed class BeforeFirst(string marker, Func<Task> before) : DbCommandInterceptor
    {
        private int _fired;
        public bool Fired => _fired == 1;
        private async Task Maybe(DbCommand c) { if (c.CommandText.Contains(marker, StringComparison.Ordinal) && Interlocked.Exchange(ref _fired, 1) == 0) await before(); }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c, CommandEventData e, InterceptionResult<DbDataReader> r, CancellationToken ct = default) { await Maybe(c); return r; }
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c, CommandEventData e, InterceptionResult<int> r, CancellationToken ct = default) { await Maybe(c); return r; }
    }

    /// <summary>Runs <c>between</c> once, right after the first read containing <c>marker</c> has executed.</summary>
    private sealed class AfterFirstRead(string marker, Func<Task> between) : DbCommandInterceptor
    {
        private int _fired;
        public bool Fired => _fired == 1;
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand c, CommandExecutedEventData e, DbDataReader r, CancellationToken ct = default)
        {
            if (c.CommandText.Contains(marker, StringComparison.Ordinal) && Interlocked.Exchange(ref _fired, 1) == 0) await between();
            return r;
        }
    }

    /// <summary>Throws a transient error just before the first transaction commit, so the commit never happens and the
    /// execution strategy has to ask verifySucceeded whether it did.</summary>
    private sealed class FailFirstCommit : DbTransactionInterceptor
    {
        private int _fired;
        public bool Fired => _fired == 1;
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _fired, 1) == 0)
                throw new NpgsqlException("injected ambiguous commit", new TimeoutException());
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Fails the first command containing <c>marker</c>: transiently (the strategy retries) or not.</summary>
    private sealed class FailOnce(string marker, bool transient) : DbCommandInterceptor
    {
        private int _fired;
        public bool Fired => _fired == 1;
        private void Maybe(DbCommand c)
        {
            if (!c.CommandText.Contains(marker, StringComparison.Ordinal) || Interlocked.Exchange(ref _fired, 1) != 0) return;
            if (transient) throw new NpgsqlException("injected transient failure", new TimeoutException());
            throw new InvalidOperationException("injected permanent failure");
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c, CommandEventData e, InterceptionResult<DbDataReader> r, CancellationToken ct = default) { Maybe(c); return ValueTask.FromResult(r); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c, CommandEventData e, InterceptionResult<int> r, CancellationToken ct = default) { Maybe(c); return ValueTask.FromResult(r); }
    }
}
