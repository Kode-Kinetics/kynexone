using System.Data.Common;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Against REAL PostgreSQL: withdrawing a pending leave and the final approval of the same leave cannot both apply.
/// Withdraw used to read the status, check it in memory and save "Withdrawn" over whatever had committed since: an
/// approval that landed between the read and the write was overwritten, leaving a Withdrawn request that still held
/// its Used days. The race is forced deterministically: a command interceptor commits a real approval on another
/// connection right after the withdraw's first read of the request.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class LeaveWithdrawRacePostgresTests
{
    private readonly PostgresFixture _fx;
    public LeaveWithdrawRacePostgresTests(PostgresFixture fx) => _fx = fx;

    private sealed record F(Guid TenantId, int EmployeeId, Guid HrUserId, Guid LeaveTypeId, DateOnly Day);

    [Fact]
    public async Task Withdraw_RacingAFinalApproval_LosesAndTheApprovalStands()
    {
        var f = await SeedAsync();
        var id = await SubmitAsync(f);

        var gate = new AfterFirstRead("FROM leave_requests", async () =>
        {
            await using var other = Db();
            await new LeaveService(other, new ApprovalRouter(other)).ApproveRequestAsync(f.TenantId, id, f.HrUserId, "HR", "ok");
        });
        IActionResult result;
        await using (var db = Db(gate))
            result = await Controller(db, f.TenantId).Withdraw(id, new WithdrawLeaveRequest("changed plans"), CancellationToken.None);
        gate.Fired.Should().BeTrue();

        result.Should().NotBeOfType<OkObjectResult>("the leave was approved first, so it is no longer withdrawable");
        await using var check = _fx.CreateDb();
        (await check.LeaveRequests.AsNoTracking().SingleAsync(x => x.Id == id)).Status.Should().Be("Approved");
        var balance = await check.EmployeeLeaveBalances.AsNoTracking().SingleAsync(x => x.TenantId == f.TenantId && x.EmployeeId == f.EmployeeId);
        (balance.Used, balance.Pending).Should().Be((1m, 0m), "the approval moved the day from Pending to Used and the withdraw must not undo it");
        (await check.LeaveAuditLogs.CountAsync(a => a.TenantId == f.TenantId && a.EntityId == id.ToString() && a.Action == "Withdrawn")).Should().Be(0);
        (await check.LeaveBalanceTransactions.CountAsync(x => x.TenantId == f.TenantId && x.TransactionType == "Reversed")).Should().Be(0);
    }

    [Fact]
    public async Task Withdraw_NoRace_ReleasesOnceAuditsOnceAndWithdraws()
    {
        var f = await SeedAsync();
        var id = await SubmitAsync(f);

        IActionResult result;
        await using (var db = Db())
            result = await Controller(db, f.TenantId).Withdraw(id, null, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        await using var check = _fx.CreateDb();
        (await check.LeaveRequests.AsNoTracking().SingleAsync(x => x.Id == id)).Status.Should().Be("Withdrawn");
        (await check.EmployeeLeaveBalances.AsNoTracking().SingleAsync(x => x.TenantId == f.TenantId && x.EmployeeId == f.EmployeeId)).Pending.Should().Be(0m);
        (await check.LeaveBalanceTransactions.CountAsync(x => x.TenantId == f.TenantId && x.TransactionType == "Reversed")).Should().Be(1);
        (await check.LeaveAuditLogs.CountAsync(a => a.TenantId == f.TenantId && a.EntityId == id.ToString() && a.Action == "Withdrawn")).Should().Be(1);
    }

    [Fact]
    public async Task TwoWithdrawsOfTheSameLeave_ReleaseTheBalanceOnce()
    {
        var f = await SeedAsync();
        var id = await SubmitAsync(f);

        IActionResult? second = null;
        var gate = new AfterFirstRead("FROM leave_requests", async () =>
        {
            await using var other = Db();
            second = await Controller(other, f.TenantId).Withdraw(id, new WithdrawLeaveRequest("dup"), CancellationToken.None);
        });
        IActionResult first;
        await using (var db = Db(gate))
            first = await Controller(db, f.TenantId).Withdraw(id, new WithdrawLeaveRequest("first"), CancellationToken.None);
        gate.Fired.Should().BeTrue();

        // One of the two wins; the other is refused, and the day is released exactly once.
        new[] { first, second! }.Count(r => r is OkObjectResult).Should().Be(1);
        await using var check = _fx.CreateDb();
        (await check.LeaveBalanceTransactions.CountAsync(x => x.TenantId == f.TenantId && x.TransactionType == "Reversed")).Should().Be(1);
        (await check.LeaveAuditLogs.CountAsync(a => a.TenantId == f.TenantId && a.Action == "Withdrawn")).Should().Be(1);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private async Task<F> SeedAsync()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var hrUserId = Guid.NewGuid();
        var day = new DateOnly(2026, 10, 14); // a Wednesday
        var lt = new LeaveType { TenantId = tenantId, Code = "AL", NameEn = "Annual Leave", Category = "Annual", IsActive = true, IsPaid = true };
        var hr = new Employee { TenantId = tenantId, UserAccountId = hrUserId, EmployeeCode = $"H{Guid.NewGuid():N}"[..12], FullName = "HR", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-5), WorkEmail = $"{Guid.NewGuid():N}@p.test" };
        db.LeaveTypes.Add(lt); db.Employees.Add(hr);
        await db.SaveChangesAsync();
        var emp = new Employee { TenantId = tenantId, EmployeeCode = $"E{Guid.NewGuid():N}"[..12], FullName = "Emp", UserAccountId = Guid.NewGuid(), Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-2), WorkEmail = $"{Guid.NewGuid():N}@p.test" };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();
        db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance { TenantId = tenantId, EmployeeId = emp.Id, EmployeeName = "Emp", LeaveTypeId = lt.Id, LeaveTypeName = lt.NameEn, Year = day.Year, Entitled = 30 });
        await db.SaveChangesAsync();
        return new F(tenantId, emp.Id, hrUserId, lt.Id, day);
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
}
