using System.Data.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>How the merge takes its locks and when it counts: the finance create lock, counts before row locks, and a repeat of a merge that already happened.</summary>
public sealed partial class EmployeeMergeOwnedRecordsPostgresTests
{
    private sealed class CommandCounter : DbCommandInterceptor
    {
        public List<string> Readers { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            lock (Readers) Readers.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>Holds one tenant finance lock in an open transaction until released; returns once the lock is held.</summary>
    private async Task<(Task Holder, TaskCompletionSource Release)> HoldFinanceLockAsync(Twins t, string scope)
    {
        var acquired = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var holder = Task.Run(async () =>
        {
            await using var db = _fx.CreateDb();
            await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                await FinanceDecisionSerializer.AcquireAsync(db, scope, t.TenantId, t.TenantId);
                acquired.TrySetResult();
                await release.Task;
                await tx.CommitAsync();
            });
        });
        await acquired.Task;
        return (holder, release);
    }

    [Theory]
    [InlineData("finance.loan-create")]
    [InlineData("finance.advance-create")]
    public async Task Merge_WaitsForTheTenantFinanceCreateLock_ThatLoanOrAdvanceCreationHolds(string scope)
    {
        var t = await SeedTwinsAsync();
        var (holder, release) = await HoldFinanceLockAsync(t, scope);

        var merge = MergeAsync(t);
        await Task.Delay(1500);
        merge.IsCompleted.Should().BeFalse($"creating a loan or an advance holds {scope}, and the merge must wait for it so nothing slips in behind the check");

        release.SetResult();
        Assert.IsType<OkObjectResult>(await merge);
        await holder;
    }

    [Fact]
    public async Task Merge_RecordsBothFinanceLocksInAFixedOrder_LoanThenAdvance()
    {
        var t = await SeedTwinsAsync();
        var seen = new List<(string Scope, Guid TenantId, Guid AggregateId)>();
        FinanceDecisionSerializer.Recorder.Value = seen;
        try { Assert.IsType<OkObjectResult>(await MergeAsync(t)); }
        finally { FinanceDecisionSerializer.Recorder.Value = null; }

        seen.Select(x => x.Scope).Should().ContainInOrder("finance.loan-create", "finance.advance-create");
    }

    [Fact]
    public async Task Merge_ReChecksEveryBlockerTypeUnderTheLock_ARecordCommittedWhileItWaitsGives409()
    {
        var t = await SeedTwinsAsync();
        var (holder, release) = await HoldFinanceLockAsync(t, "finance.loan-create");

        var merge = MergeAsync(t);          // passes the pre-count (nothing there yet), then waits for the lock
        await Task.Delay(1500);
        await using (var late = _fx.CreateDb())
        {
            // A document is NOT one of the cheap types: only a full re-check under the lock sees it.
            await Catalogue().First(r => r.Name == "employee document").Seed(late, t);
            await late.SaveChangesAsync();
        }
        release.SetResult();

        var conflict = Assert.IsAssignableFrom<ObjectResult>(await merge);
        conflict.StatusCode.Should().Be(Microsoft.AspNetCore.Http.StatusCodes.Status409Conflict);
        System.Text.Json.JsonSerializer.Serialize(conflict.Value).Should().Contain("\"documents\"");
        (await IsDeletedAsync(t.Duplicate.Id)).Should().BeFalse();
        await holder;
    }

    [Fact]
    public async Task TheBlockerCounts_RunAsOneUnionAllQuery()
    {
        var t = await SeedTwinsAsync();
        var counter = new CommandCounter();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(_fx.ConnectionString).AddInterceptors(counter).Options);

        var blockers = await EmployeeMergeBlockers.FindAsync(db, t.TenantId, t.Duplicate.Id, CancellationToken.None);

        blockers.Should().BeEmpty();
        counter.Readers.Count(sql => sql.Contains("UNION ALL", StringComparison.OrdinalIgnoreCase)).Should().Be(1, "all the counts are one statement, not one round trip each");
        counter.Readers.Count.Should().BeLessThanOrEqualTo(2, "one lookup of the employee plus the single counting statement");
        var counting = counter.Readers.Single(sql => sql.Contains("UNION ALL", StringComparison.OrdinalIgnoreCase));
        System.Text.RegularExpressions.Regex.Matches(counting, "UNION ALL", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count
            .Should().BeGreaterThan(40, "every per-table counter is a branch of that one statement");
    }

    [Fact]
    public async Task Merge_CountsBeforeItTakesTheRowLocks_SoARefusalDoesNotWaitOnALockedRow()
    {
        var t = await SeedTwinsAsync();
        await using (var seed = _fx.CreateDb())
        {
            await Catalogue().First(r => r.Name == "open loan").Seed(seed, t);
            await seed.SaveChangesAsync();
        }
        var locked = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var holder = Task.Run(async () =>
        {
            await using var db = _fx.CreateDb();
            await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                await db.Employees.TagWith(RowLockingInterceptor.ForUpdateTag).Where(e => e.Id == t.Duplicate.Id).ToListAsync();
                locked.TrySetResult();
                await release.Task;
                await tx.CommitAsync();
            });
        });
        await locked.Task;

        var merge = MergeAsync(t);
        var finished = await Task.WhenAny(merge, Task.Delay(8000));
        var returned = finished == merge;
        release.SetResult();
        await holder;

        returned.Should().BeTrue("the blocker counts run before the row locks, so a duplicate that is going to be refused never queues behind a locked row");
        Assert.IsAssignableFrom<ObjectResult>(await merge).StatusCode.Should().Be(Microsoft.AspNetCore.Http.StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task Merge_RepeatedAfterItAlreadyHappened_Is200_WithNoFurtherWrites()
    {
        var t = await SeedTwinsAsync();
        Assert.IsType<OkObjectResult>(await MergeAsync(t));
        int auditsAfterFirst;
        await using (var read = _fx.CreateDb())
            auditsAfterFirst = await read.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.TenantId == t.TenantId);

        var repeat = await MergeAsync(t);

        Assert.IsType<OkObjectResult>(repeat);
        await using var after = _fx.CreateDb();
        (await after.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.TenantId == t.TenantId)).Should().Be(auditsAfterFirst, "a repeat of a merge whose answer was lost writes nothing");
    }

    [Fact]
    public async Task Merge_OfADeletedRecordIntoSomeoneElse_IsStill404()
    {
        var t = await SeedTwinsAsync();
        Assert.IsType<OkObjectResult>(await MergeAsync(t));
        Employee other;
        await using (var seed = _fx.CreateDb())
        {
            other = new Employee { TenantId = t.TenantId, CompanyId = t.CompanyId, EmployeeCode = "OTHER-1", FullName = "Other", EnglishName = "Other", Status = "Active", ReadinessState = "Ready", JoiningDate = DateTime.SpecifyKind(new DateTime(2025, 1, 1), DateTimeKind.Utc) };
            seed.Employees.Add(other);
            await seed.SaveChangesAsync();
        }

        var result = await MergeAsync(new Twins(t.TenantId, t.CompanyId, t.Duplicate, other));

        Assert.IsType<NotFoundResult>(result);
    }
}
