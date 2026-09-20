using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Models;
using static Zayra.Api.Tests.PayrollJobTestKit;

namespace Zayra.Api.Tests;

/// <summary>
/// W2-A — architecture-review defect S1, pinned on real PostgreSQL through the SYNCHRONOUS Lock endpoint
/// (so the same file runs unchanged before and after the fix).
///
/// <para>Before the fix Lock committed in three independent units: two <c>ExecuteUpdateAsync</c> calls
/// (slips → Final, payslips → published to ESS) auto-committed on the spot, and the GL lines plus
/// <c>run.Status = "Locked"</c> committed later in <c>SaveChangesAsync</c>. The post-once probe was a plain
/// <c>AnyAsync</c> with no database backstop and <c>PayrollRun</c> had no concurrency token.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class PayrollLockAtomicityTests
{
    private readonly PostgresFixture _fx;
    public PayrollLockAtomicityTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task DoubleSubmittedLock_PostsTheAccrualJournalExactlyOnce()
    {
        var s = await SeedKsaRunAsync(_fx, new SeedOptions(Employees: 3));
        await ProcessSyncAsync(_fx, s);
        await ApproveDirectAsync(_fx, s);

        // Both requests are parked at the journal INSERT, i.e. after every check each one makes.
        var barrier = new GlInsertBarrier(participants: 2, timeout: TimeSpan.FromSeconds(4));
        await using var db1 = CreateDbWith(_fx, barrier);
        await using var db2 = CreateDbWith(_fx, barrier);
        var results = await Task.WhenAll(
            Task.Run(() => LockOutcome(db1, s)),
            Task.Run(() => LockOutcome(db2, s)));

        await using var verify = _fx.CreateDb();
        var accrual = await LiveAccrualAsync(verify, s);
        var netPayableLines = accrual.Count(g => g.CreditAccount.StartsWith("2100"));
        var run = await verify.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == s.RunId);
        var credit2100 = accrual.Where(g => g.CreditAccount.StartsWith("2100")).Sum(g => g.Amount);
        var lockedAudits = await verify.PayrollAuditLogs.AsNoTracking()
            .CountAsync(a => a.TenantId == s.TenantId && a.Action == "payroll.run.locked" && a.EntityId == s.RunId.ToString());

        Assert.True(netPayableLines == 1,
            $"The run's net-pay liability was accrued {netPayableLines} time(s): CR 2100 = {credit2100:N2} " +
            $"against a run net of {run.TotalNetSalary:N2}. Lock results: [{string.Join(", ", results)}]; " +
            $"{accrual.Count} live accrual line(s); {lockedAudits} 'payroll.run.locked' audit row(s).");
        Assert.Equal(run.TotalNetSalary, credit2100);
        Assert.Equal(1, results.Count(r => r == 200));
        Assert.Equal(1, lockedAudits);
        // The doubled journal would STILL balance — which is why the GL check could never catch it.
        Assert.Equal(accrual.Where(g => g.DebitAccount != "").Sum(g => g.Amount), accrual.Where(g => g.CreditAccount != "").Sum(g => g.Amount));
    }

    [Fact]
    public async Task LockThatFailsBeforeItsJournalCommits_PublishesNothing_FinalisesNothing_PostsNothing()
    {
        var s = await SeedKsaRunAsync(_fx, new SeedOptions(Employees: 2));
        await ProcessSyncAsync(_fx, s);
        await ApproveDirectAsync(_fx, s);
        await GeneratePayslipsAsync(_fx, s); // ESS payslips exist, unpublished, before Lock

        var fault = new FailGlInsert();
        await using (var db = CreateDbWith(_fx, fault))
        {
            var ex = await Record.ExceptionAsync(() => Build(db, s.TenantId, "payroll.lock").Lock(s.RunId, CancellationToken.None));
            Assert.NotNull(ex);
        }
        Assert.Equal(1, fault.Fired);

        await using var verify = _fx.CreateDb();
        var finalSlips = await verify.PayrollSlips.AsNoTracking().CountAsync(x => x.RunId == s.RunId && x.Status == "Final");
        var publishedPayslips = await verify.Payslips.AsNoTracking().CountAsync(x => x.PayrollRunId == s.RunId && x.IsPublishedToEss);
        var gl = await LiveAccrualAsync(verify, s);
        var run = await verify.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == s.RunId);

        Assert.True(finalSlips == 0 && publishedPayslips == 0,
            $"A Lock that never posted its journal left {finalSlips} slip(s) Final and {publishedPayslips} payslip(s) " +
            $"published to ESS, for a run still '{run.Status}' with {gl.Count} GL line(s).");
        Assert.Empty(gl);
        Assert.Equal("Approved", run.Status);

        // And the run is not wedged: a clean retry locks it exactly once.
        await using (var db = _fx.CreateDb())
            Assert.IsType<OkObjectResult>(await Build(db, s.TenantId, "payroll.lock").Lock(s.RunId, CancellationToken.None));
        await using var after = _fx.CreateDb();
        Assert.Equal(1, (await LiveAccrualAsync(after, s)).Count(g => g.CreditAccount.StartsWith("2100")));
        Assert.Equal(2, await after.Payslips.AsNoTracking().CountAsync(x => x.PayrollRunId == s.RunId && x.IsPublishedToEss));
    }

    private static async Task<int> LockOutcome(Zayra.Api.Data.ZayraDbContext db, Scenario s)
    {
        try
        {
            var r = await Build(db, s.TenantId, "payroll.lock").Lock(s.RunId, CancellationToken.None);
            return r switch { ObjectResult o => o.StatusCode ?? 200, StatusCodeResult c => c.StatusCode, _ => 200 };
        }
        catch (Exception ex)
        {
            return ex is DbUpdateException ? 5091 : 500;
        }
    }
}
