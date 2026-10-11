using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zayra.Api.Controllers;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Reopen vs Lock. Reopen checked "reopenable status, no Payroll GL" OUTSIDE its transaction and then
/// wrote under a plain read, so a Lock that committed in between (accrual posted, status Locked) was wiped
/// back to Draft with its journal still live. Reopen now takes the same FOR UPDATE row lock as Lock/Void
/// and repeats both checks under it. Real Postgres only: an in-memory provider has no row locks.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class ReopenLockRacePostgresTests
{
    private readonly PostgresFixture _fx;
    public ReopenLockRacePostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task AReopenThatPassedItsChecks_WhileALockWasPosting_IsRefused_OnceTheLockCommits()
    {
        await using var k = await NewKitAsync();
        var run = await k.SeedApprovedRunAsync(new[] { (1, "BASIC", "Salary", 5_000m) }, Array.Empty<(int, string, string, decimal)>(), 5_000m);

        // A Lock mid-flight: holds the run's row lock, has posted its accrual and set Locked, not yet committed.
        // A plain (non-retrying) context, so the test may open the transaction by hand.
        await using var lockDb = new Zayra.Api.Data.ZayraDbContext(
            new DbContextOptionsBuilder<Zayra.Api.Data.ZayraDbContext>().UseNpgsql(_fx.ConnectionString).Options);
        await using var lockTx = await lockDb.Database.BeginTransactionAsync();
        await lockDb.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM payroll_runs WHERE id = {run.Id} FOR UPDATE");
        lockDb.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = k.TenantId, CompanyId = k.CompanyId, SourceModule = "Payroll", SourceEntityId = run.Id, SourceEntityRef = "2026-09",
            EventType = GlEventTypes.Accrual, DebitAccount = "5001 - Basic Salary Expense", CreditAccount = string.Empty,
            Amount = 5_000m, Currency = "SAR", Period = "2026-09", EntryDate = new DateOnly(2026, 9, 30), Description = "Payroll earning: BASIC",
        });
        await lockDb.SaveChangesAsync();
        await lockDb.Database.ExecuteSqlInterpolatedAsync($"UPDATE payroll_runs SET status = 'Locked' WHERE id = {run.Id}");

        // Reopen's pre-transaction checks see Approved and no GL (the Lock has not committed).
        var reopen = Task.Run(async () =>
        {
            await using var db = k.NewContext();
            return await k.Payroll(db).ReopenRun(run.Id, new PayrollReasonRequest("race"), default);
        });
        await WaitUntilABackendWaitsOnALockAsync(reopen);
        await lockTx.CommitAsync();
        var result = await reopen;

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().BeOneOf(400, 422);
        ((ObjectResult)result).Value!.ToString().Should().Contain("not_reopenable");
        await using var check = k.NewContext();
        (await check.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id)).Status.Should().Be("Locked");
        (await k.RunGlAsync(run.Id, check)).Should().HaveCount(1, "the posted accrual stays");
        (await check.PayrollEarnings.AsNoTracking().CountAsync(e => e.PayrollRunId == run.Id))
            .Should().Be(1, "the earnings the journal was built from must not be deleted");
    }

    [Fact]
    public async Task LockAndReopenTogether_NeverLeaveADraftRunWithALiveJournal()
    {
        await using var k = await NewKitAsync();
        for (var round = 0; round < 5; round++)
        {
            var run = await k.SeedApprovedRunAsync(new[] { (1, "BASIC", "Salary", 5_000m), (2, "BASIC", "Salary", 4_000m) },
                new[] { (1, "ATTENDANCE", "Attendance", 100m) }, net: 8_900m, month: round + 1);

            var results = await RaceAsync(
                async () => { await using var db = k.NewContext(); return await k.Payroll(db).Lock(run.Id, default); },
                async () => { await using var db = k.NewContext(); return await k.Payroll(db).ReopenRun(run.Id, new PayrollReasonRequest("race"), default); });

            results.Should().NotContainNulls($"round {round}: the loser must fail cleanly, never with a 500");
            await using var db = k.NewContext();
            var status = (await db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id)).Status;
            var gl = await k.RunGlAsync(run.Id, db);
            if (status == "Locked")
            {
                gl.Should().NotBeEmpty($"round {round}");
                (await db.PayrollEarnings.AsNoTracking().CountAsync(e => e.PayrollRunId == run.Id)).Should().BeGreaterThan(0,
                    $"round {round}: a locked run keeps the earnings its journal was built from");
            }
            else
            {
                status.Should().Be("Draft", $"round {round}");
                gl.Should().BeEmpty($"round {round}: a reopened run must never carry a live accrual");
            }
        }
    }

    private async Task<GlPostingKit> NewKitAsync()
    {
        Guid tenantId;
        await using (var db = _fx.CreateDb()) tenantId = await PostgresFixture.SeedMinimalTenant(db);
        return await GlPostingKit.CreateAsync(_fx.CreateDb, tenantId);
    }

    /// <summary>Polls until some backend of this database is blocked on a lock (the contender reached the
    /// row lock), or fails if the contender finished first.</summary>
    private async Task WaitUntilABackendWaitsOnALockAsync(Task contender)
    {
        await using var db = _fx.CreateDb();
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (contender.IsCompleted) return; // asserted on by the caller
            await using var cmd = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'", conn);
            if ((long)(await cmd.ExecuteScalarAsync())! > 0) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("The contender never reached the run's row lock.");
    }

    private static async Task<T?[]> RaceAsync<T>(Func<Task<T>> first, Func<Task<T>> second) where T : class
    {
        var ready = new SemaphoreSlim(0, 2);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<T?> Run(Func<Task<T>> contender)
        {
            await Task.Yield();
            ready.Release();
            await go.Task;
            try { return await contender(); }
            catch { return null; }
        }
        var a = Task.Run(() => Run(first));
        var b = Task.Run(() => Run(second));
        await ready.WaitAsync();
        await ready.WaitAsync();
        go.SetResult();
        return await Task.WhenAll(a, b);
    }
}
