using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// F3 — the durable job queue against REAL PostgreSQL (Testcontainers) with REAL concurrency: separate
/// DbContexts / connection pools per simulated instance, the production retrying execution strategy,
/// and the production row-locking interceptor. Nothing here is mocked except the work itself, which is
/// a test handler that writes one non-idempotent row per applied item so double application would be
/// visible in the database, not just in memory.
///
/// Every test registers its OWN job type (a fresh guid), so a worker in one test can never claim a job
/// left behind by another test sharing the container.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class BackgroundJobQueuePostgresTests
{
    private readonly PostgresFixture _fx;
    public BackgroundJobQueuePostgresTests(PostgresFixture fx) => _fx = fx;

    // ───────────────────────────── 1. Concurrent claiming ─────────────────────────────

    [Fact]
    public async Task TwoInstances_DrainingOneQueue_RunEveryJobExactlyOnce()
    {
        var type = NewType();
        var probe = new ItemProbe { ItemDelayMs = 5 };
        await using var instanceA = BuildInstance(type, probe);
        await using var instanceB = BuildInstance(type, probe);
        var tenantId = await SeedTenantAsync();

        var jobIds = new List<Guid>();
        await using (var scope = instanceA.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<BackgroundJobStore>();
            for (var i = 0; i < 40; i++)
                jobIds.Add((await store.EnqueueAsync(tenantId, type.JobType, $"job-{i}", new ItemsPayload(3), null, default)).Job.Id);
        }

        // Two instances x two slots each, all hammering the same queue at once.
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Drain(ServiceProvider sp, string id)
        {
            await start.Task;
            var runner = sp.GetRequiredService<BackgroundJobRunner>();
            var idle = 0;
            while (idle < 3)
            {
                if (await runner.RunNextAsync(id, CancellationToken.None, CancellationToken.None, [type.JobType])) idle = 0;
                else { idle++; await Task.Delay(50); }
            }
        }
        var drains = new[]
        {
            Drain(instanceA, "A#0"), Drain(instanceA, "A#1"),
            Drain(instanceB, "B#0"), Drain(instanceB, "B#1"),
        };
        start.SetResult();
        await Task.WhenAll(drains);

        // Every job executed by exactly one worker, exactly once.
        Assert.Equal(40, probe.Executions.Count);
        Assert.All(jobIds, id => Assert.Equal(1, probe.Executions.GetValueOrDefault(id)));
        Assert.True(probe.ExecutedBy.Values.Distinct().Count() > 1,
            "the load should actually have been shared between instances");

        await using var verify = _fx.CreateDb();
        var jobs = await verify.BackgroundJobs.Where(j => j.JobType == type.JobType).ToListAsync();
        Assert.All(jobs, j =>
        {
            Assert.Equal(BackgroundJobStatuses.Succeeded, j.Status);
            Assert.Equal(1, j.AttemptCount);
            Assert.Equal(3, j.ProgressCompleted);
        });
        // Durable evidence: one applied-row per (job, item), none duplicated.
        var applied = await AppliedRowsAsync(verify, tenantId);
        Assert.Equal(120, applied.Count);
        Assert.Equal(applied.Count, applied.Distinct().Count());
    }

    [Theory]
    [InlineData(true)]   // production configuration: FOR UPDATE SKIP LOCKED + CAS
    [InlineData(false)]  // interceptor absent: the compare-and-set alone must still hold
    public async Task SimultaneousClaims_OnOneJob_ExactlyOneWins(bool withRowLocking)
    {
        var type = NewType();
        var tenantId = await SeedTenantAsync();
        await using var sp = BuildInstance(type, new ItemProbe());

        for (var round = 0; round < 15; round++)
        {
            Guid jobId;
            await using (var scope = sp.CreateAsyncScope())
                jobId = (await scope.ServiceProvider.GetRequiredService<BackgroundJobStore>()
                    .EnqueueAsync(tenantId, type.JobType, $"race-{round}", new ItemsPayload(1), null, default)).Job.Id;

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var claims = Enumerable.Range(0, 8).Select(async i =>
            {
                await using var db = withRowLocking ? _fx.CreateDb() : CreateDbWithoutRowLocking();
                await gate.Task;
                return await BackgroundJobStore.TryClaimAsync(db, [type.JobType], $"claimer-{i}", TimeSpan.FromMinutes(1), default);
            }).ToList();
            gate.SetResult();
            var results = await Task.WhenAll(claims);

            var winners = results.Where(r => r is not null).ToList();
            Assert.Single(winners);
            Assert.Equal(jobId, winners[0]!.Id);

            await using var verify = _fx.CreateDb();
            var job = await verify.BackgroundJobs.SingleAsync(j => j.Id == jobId);
            Assert.Equal(BackgroundJobStatuses.Running, job.Status);
            Assert.Equal(1, job.AttemptCount);
            Assert.Equal(winners[0]!.LeaseToken, job.LeaseToken);
            // finish it so the next round has a single candidate
            Assert.True(await BackgroundJobStore.CompleteAsync(verify, winners[0]!, null, default));
        }
    }

    /// <summary>
    /// Proves FOR UPDATE SKIP LOCKED is really emitted on the production claim path (not just that the
    /// CAS saves us): while one transaction holds the claim row lock, a second claimer must return
    /// "nothing to claim" IMMEDIATELY instead of blocking on the lock.
    /// </summary>
    [Fact]
    public async Task ClaimQuery_SkipsRowsLockedByAnotherInstance_InsteadOfBlocking()
    {
        var type = NewType();
        var tenantId = await SeedTenantAsync();
        await using var sp = BuildInstance(type, new ItemProbe());
        await using (var scope = sp.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<BackgroundJobStore>()
                .EnqueueAsync(tenantId, type.JobType, "locked", new ItemsPayload(1), null, default);

        var captured = new List<string>();
        await using var holder = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(_fx.ConnectionString)
            .AddInterceptors(RowLockingInterceptor.Instance, new CommandCapture(captured))
            .Options);
        await using var tx = await holder.Database.BeginTransactionAsync();
        var locked = await holder.BackgroundJobs.Where(j => j.JobType == type.JobType)
            .TagWith(RowLockingInterceptor.ForUpdateSkipLockedTag).ToListAsync();
        Assert.Single(locked);
        Assert.Contains(captured, c => c.TrimEnd().EndsWith("FOR UPDATE SKIP LOCKED", StringComparison.Ordinal));

        await using var claimer = _fx.CreateDb();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var claim = await BackgroundJobStore.TryClaimAsync(claimer, [type.JobType], "second", TimeSpan.FromMinutes(1), default)
            .WaitAsync(TimeSpan.FromSeconds(10));
        sw.Stop();
        Assert.Null(claim);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"claim blocked for {sw.Elapsed} — row lock was not skipped");

        await tx.RollbackAsync();
        await using var after = _fx.CreateDb();
        Assert.NotNull(await BackgroundJobStore.TryClaimAsync(after, [type.JobType], "third", TimeSpan.FromMinutes(1), default));
    }

    private sealed class CommandCapture(List<string> sink) : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            lock (sink) sink.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    // ─────────────────────── 2. Crash → lease expiry → reclaim → resume ───────────────────────

    [Fact]
    public async Task WorkerDiesMidJob_LeaseExpires_AnotherWorkerResumesFromCheckpoint_NoItemAppliedTwice()
    {
        var type = NewType();
        var probe = new ItemProbe();
        var lease = TimeSpan.FromSeconds(2);
        await using var deadInstance = BuildInstance(type, probe, lease);
        await using var survivor = BuildInstance(type, probe, lease);
        var tenantId = await SeedTenantAsync();

        Guid jobId;
        await using (var scope = survivor.CreateAsyncScope())
            jobId = (await scope.ServiceProvider.GetRequiredService<BackgroundJobStore>()
                .EnqueueAsync(tenantId, type.JobType, "crash", new ItemsPayload(6), null, default)).Job.Id;

        // Instance A claims and runs the handler itself — no runner, so no heartbeat and no outcome
        // write: when the handler "dies" at item 3 NOTHING is recorded, exactly as when the process is
        // killed. Item 3's writes were staged inside its transaction and are rolled back.
        probe.CrashOnFirstAttemptAtItem = 3;
        ClaimedBackgroundJob claimA;
        await using (var claimDb = _fx.CreateDb())
            claimA = (await BackgroundJobStore.TryClaimAsync(claimDb, [type.JobType], "A", lease, default))!;
        Assert.Equal(jobId, claimA.Id);

        var scopeA = deadInstance.CreateAsyncScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var ctxA = new JobExecutionContext(claimA, dbA, scopeA.ServiceProvider, [], lease, default, default);
        var handlerA = scopeA.ServiceProvider.GetRequiredService<ItemsTestHandler>();
        await Assert.ThrowsAsync<SimulatedProcessDeath>(() => handlerA.ExecuteAsync(ctxA));

        // Before the lease expires the job is NOT claimable: a slow worker is not a dead one.
        var runnerB = survivor.GetRequiredService<BackgroundJobRunner>();
        Assert.False(await runnerB.RunNextAsync("B", default, default, [type.JobType]));

        // After expiry B reclaims, skips the 3 checkpointed items and applies the other 3.
        await WaitUntilAsync(async () =>
            await runnerB.RunNextAsync("B", default, default, [type.JobType]), TimeSpan.FromSeconds(15));

        await using var verify = _fx.CreateDb();
        var job = await verify.BackgroundJobs.SingleAsync(j => j.Id == jobId);
        Assert.Equal(BackgroundJobStatuses.Succeeded, job.Status);
        Assert.Equal(2, job.AttemptCount);
        Assert.Equal(6, job.ProgressCompleted);

        var checkpoints = await verify.BackgroundJobItems.Where(i => i.JobId == jobId)
            .OrderBy(i => i.ItemKey).Select(i => new { i.ItemKey, i.Attempt }).ToListAsync();
        Assert.Equal(6, checkpoints.Count);
        Assert.All(checkpoints.Where(c => string.CompareOrdinal(c.ItemKey, "item:3") < 0), c => Assert.Equal(1, c.Attempt));
        Assert.All(checkpoints.Where(c => string.CompareOrdinal(c.ItemKey, "item:3") >= 0), c => Assert.Equal(2, c.Attempt));

        // Each item's durable effect exists exactly once...
        var applied = await AppliedRowsAsync(verify, tenantId);
        Assert.Equal(Enumerable.Range(0, 6).Select(i => $"{jobId}:item:{i}").OrderBy(x => x),
            applied.OrderBy(x => x));
        // ...even though item 3 was EXECUTED twice (once rolled back by the crash): proof that the
        // rollback, not luck, is what kept it single.
        Assert.Equal(2, probe.Invocations[$"{jobId}:item:3"]);
        Assert.Equal(1, probe.Invocations[$"{jobId}:item:0"]);

        // The zombie wakes up: its fence no longer matches, so it can neither apply an item nor
        // overwrite the outcome.
        await Assert.ThrowsAsync<BackgroundJobLeaseLostException>(() =>
            ctxA.RunItemAsync("item:zombie", _ => { probe.Record(jobId, "item:zombie"); return Task.CompletedTask; }));
        Assert.False(await BackgroundJobStore.FailAttemptAsync(dbA, claimA, "zombie", default));
        await scopeA.DisposeAsync();

        await using var verify2 = _fx.CreateDb();
        Assert.Equal(BackgroundJobStatuses.Succeeded, (await verify2.BackgroundJobs.SingleAsync(j => j.Id == jobId)).Status);
        Assert.Equal(6, await verify2.BackgroundJobItems.CountAsync(i => i.JobId == jobId));
    }

    [Fact]
    public async Task JobThatKeepsKillingItsWorker_IsFailed_AfterMaxAttempts_NotRetriedForever()
    {
        var type = NewType() with { MaxAttempts = 2 };
        var tenantId = await SeedTenantAsync();
        await using var sp = BuildInstance(type, new ItemProbe());
        Guid jobId;
        await using (var scope = sp.CreateAsyncScope())
            jobId = (await scope.ServiceProvider.GetRequiredService<BackgroundJobStore>()
                .EnqueueAsync(tenantId, type.JobType, "poison", new ItemsPayload(1), null, default)).Job.Id;

        var lease = TimeSpan.FromMilliseconds(300);
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await using var db = _fx.CreateDb();
            ClaimedBackgroundJob? claim = null;
            await WaitUntilAsync(async () =>
                (claim = await BackgroundJobStore.TryClaimAsync(db, [type.JobType], $"doomed-{attempt}", lease, default)) is not null,
                TimeSpan.FromSeconds(10));
            Assert.Equal(attempt, claim!.Attempt);
            // ...and the process dies without a trace.
        }
        await Task.Delay(lease + TimeSpan.FromMilliseconds(200));
        await using (var db = _fx.CreateDb())
            Assert.Null(await BackgroundJobStore.TryClaimAsync(db, [type.JobType], "next", lease, default));

        await using var verify = _fx.CreateDb();
        var job = await verify.BackgroundJobs.SingleAsync(j => j.Id == jobId);
        Assert.Equal(BackgroundJobStatuses.Failed, job.Status);
        Assert.Contains("Lease expired after 2 attempt(s)", job.LastError);
    }

    // ───────────────────────────── 3. Idempotent enqueue ─────────────────────────────

    [Fact]
    public async Task SameIdempotencyKey_EnqueuedTwice_YieldsOneJob()
    {
        var type = NewType();
        var tenantId = await SeedTenantAsync();
        await using var sp = BuildInstance(type, new ItemProbe());

        await using var scope = sp.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<BackgroundJobStore>();
        var first = await store.EnqueueAsync(tenantId, type.JobType, "double-click", new ItemsPayload(1), null, default);
        var second = await store.EnqueueAsync(tenantId, type.JobType, "double-click", new ItemsPayload(1), null, default);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Job.Id, second.Job.Id);
        await using var verify = _fx.CreateDb();
        Assert.Equal(1, await verify.BackgroundJobs.CountAsync(j => j.JobType == type.JobType && j.IdempotencyKey == "double-click"));
    }

    [Fact]
    public async Task SameIdempotencyKey_EnqueuedConcurrently_YieldsOneJob()
    {
        var type = NewType();
        var tenantId = await SeedTenantAsync();
        await using var sp = BuildInstance(type, new ItemProbe());

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = Enumerable.Range(0, 12).Select(async _ =>
        {
            await using var scope = sp.CreateAsyncScope(); // own DbContext / connection per caller
            var store = scope.ServiceProvider.GetRequiredService<BackgroundJobStore>();
            await gate.Task;
            return await store.EnqueueAsync(tenantId, type.JobType, "storm", new ItemsPayload(1), null, default);
        }).ToList();
        gate.SetResult();
        var results = await Task.WhenAll(calls);

        Assert.Single(results.Select(r => r.Job.Id).Distinct());
        Assert.Single(results, r => r.Created);
        await using var verify = _fx.CreateDb();
        Assert.Equal(1, await verify.BackgroundJobs.CountAsync(j => j.JobType == type.JobType && j.IdempotencyKey == "storm"));
    }

    [Fact]
    public async Task KeyRetention_WhileActive_AllowsRerun_Forever_IsPostOnce()
    {
        var active = NewType();
        var forever = NewType() with { KeyRetention = BackgroundJobKeyRetention.Forever };
        var tenantId = await SeedTenantAsync();
        await using var sp = BuildInstance([active, forever], new ItemProbe(), TimeSpan.FromMinutes(1));
        var runner = sp.GetRequiredService<BackgroundJobRunner>();

        // The partial-unique-index predicates must really exist in the database (EnsureCreated has
        // dropped HasFilter predicates in some Npgsql versions — see PostgresFixture).
        Assert.Contains("WHERE", await _fx.GetIndexDefinitionAsync("ux_background_jobs_active_key"));
        Assert.Contains("'Forever'", await _fx.GetIndexDefinitionAsync("ux_background_jobs_retained_key"));

        async Task<BackgroundJobEnqueueResult> Enqueue(BackgroundJobTypeDescriptor t, string key)
        {
            await using var scope = sp.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<BackgroundJobStore>()
                .EnqueueAsync(tenantId, t.JobType, key, new ItemsPayload(1), null, default);
        }

        var a1 = await Enqueue(active, "range-2026-09");
        Assert.True(await runner.RunNextAsync("w", default, default, [active.JobType]));
        var a2 = await Enqueue(active, "range-2026-09");
        Assert.True(a2.Created); // finished → the same range can be reprocessed
        Assert.NotEqual(a1.Job.Id, a2.Job.Id);

        var f1 = await Enqueue(forever, "lock:run-42");
        Assert.True(await runner.RunNextAsync("w", default, default, [forever.JobType]));
        var f2 = await Enqueue(forever, "lock:run-42");
        Assert.False(f2.Created); // succeeded → the key is held forever: at most once
        Assert.Equal(f1.Job.Id, f2.Job.Id);
        Assert.Equal(BackgroundJobStatuses.Succeeded, f2.Job.Status);
    }

    // ───────────────────────────── 4. Tenant isolation ─────────────────────────────

    [Fact]
    public async Task TenantA_CannotSee_Cancel_OrReadProgress_OfTenantBJobs()
    {
        var type = BackgroundJobQueueTestTypes.Attendance; // a type the API knows about
        var tenantA = await SeedTenantAsync();
        var tenantB = await SeedTenantAsync();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        await using var sp = BuildInstance(type, new ItemProbe());
        BackgroundJob jobA, jobB;
        await using (var scope = sp.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<BackgroundJobStore>();
            jobA = (await store.EnqueueAsync(tenantA, type.JobType, $"a-{Guid.NewGuid()}", new ItemsPayload(1), userA, default)).Job;
            jobB = (await store.EnqueueAsync(tenantB, type.JobType, $"b-{Guid.NewGuid()}", new ItemsPayload(1), userB, default)).Job;
        }
        // Give B's job visible progress, so "cannot read progress" is a meaningful assertion.
        await using (var db = _fx.CreateDb())
            await db.BackgroundJobs.Where(j => j.Id == jobB.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.ProgressTotal, 10).SetProperty(j => j.ProgressCompleted, 7));

        var (controllerA, dbA) = CreateJobsController(sp, tenantA, userA, "attendance.read", "attendance.write");
        await using (dbA)
        {
            // The GLOBAL FILTER alone (no explicit tenant predicate) hides B's rows from A's context.
            Assert.False(await dbA.BackgroundJobs.AnyAsync(j => j.Id == jobB.Id));
            Assert.True(await dbA.BackgroundJobs.AnyAsync(j => j.Id == jobA.Id));

            Assert.IsType<NotFoundResult>((await controllerA.Get(jobB.Id, default)).Result);
            Assert.IsType<NotFoundResult>((await controllerA.Cancel(jobB.Id, default)).Result);

            var list = Assert.IsType<OkObjectResult>((await controllerA.List(null, null, 1, 100, default)).Result);
            var page = Assert.IsType<Zayra.Api.Application.Common.PagedResult<BackgroundJobDto>>(list.Value);
            Assert.Contains(page.Items, j => j.Id == jobA.Id);
            Assert.DoesNotContain(page.Items, j => j.Id == jobB.Id);

            // A can see its own.
            var own = Assert.IsType<OkObjectResult>((await controllerA.Get(jobA.Id, default)).Result);
            Assert.Equal(jobA.Id, Assert.IsType<BackgroundJobDto>(own.Value).Id);
        }

        await using var verify = _fx.CreateDb();
        var b = await verify.BackgroundJobs.SingleAsync(j => j.Id == jobB.Id);
        Assert.Equal(BackgroundJobStatuses.Queued, b.Status);
        Assert.Null(b.CancelRequestedAtUtc);
        await CancelLeftoversAsync(verify, jobA.Id, jobB.Id);
    }

    /// <summary>These tests use the real attendance job-type key; never leave such a job claimable.</summary>
    private static Task CancelLeftoversAsync(ZayraDbContext db, params Guid[] ids) =>
        db.BackgroundJobs.Where(j => ids.Contains(j.Id) && j.Status == BackgroundJobStatuses.Queued)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, BackgroundJobStatuses.Cancelled));

    [Fact]
    public async Task CallerWithoutTheJobTypesViewPermission_GetsNotFound_AndCannotCancelOthersJobs()
    {
        var type = BackgroundJobQueueTestTypes.Attendance;
        var tenant = await SeedTenantAsync();
        var owner = Guid.NewGuid();
        await using var sp = BuildInstance(type, new ItemProbe());
        BackgroundJob job;
        await using (var scope = sp.CreateAsyncScope())
            job = (await scope.ServiceProvider.GetRequiredService<BackgroundJobStore>()
                .EnqueueAsync(tenant, type.JobType, $"k-{Guid.NewGuid()}", new ItemsPayload(1), owner, default)).Job;

        var (noView, db1) = CreateJobsController(sp, tenant, Guid.NewGuid(), "payroll.read");
        await using (db1) Assert.IsType<NotFoundResult>((await noView.Get(job.Id, default)).Result);

        var (viewOnly, db2) = CreateJobsController(sp, tenant, Guid.NewGuid(), "attendance.read");
        await using (db2) Assert.IsType<ForbidResult>((await viewOnly.Cancel(job.Id, default)).Result);

        // ...but the creator may always cancel their own job.
        var (creator, db3) = CreateJobsController(sp, tenant, owner, "attendance.read");
        await using (db3) Assert.IsType<OkObjectResult>((await creator.Cancel(job.Id, default)).Result);
        await using var verify = _fx.CreateDb();
        Assert.Equal(BackgroundJobStatuses.Cancelled, (await verify.BackgroundJobs.SingleAsync(j => j.Id == job.Id)).Status);
    }

    // ───────────────────────────── 5. Cancellation ─────────────────────────────

    [Fact]
    public async Task CancelWhileRunning_StopsAtNextCheckpoint_CompletedItemsStay_NoFurtherItemsApplied()
    {
        var type = NewType();
        var tenantId = await SeedTenantAsync();
        var probe = new ItemProbe();
        var atBoundary4 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.BeforeItem = async i => { if (i == 4) { atBoundary4.SetResult(); await release.Task; } };
        await using var sp = BuildInstance(type, probe);

        Guid jobId;
        await using (var scope = sp.CreateAsyncScope())
            jobId = (await scope.ServiceProvider.GetRequiredService<BackgroundJobStore>()
                .EnqueueAsync(tenantId, type.JobType, "cancel-me", new ItemsPayload(10), null, default)).Job.Id;

        var run = sp.GetRequiredService<BackgroundJobRunner>().RunNextAsync("w", default, default, [type.JobType]);
        await atBoundary4.Task.WaitAsync(TimeSpan.FromSeconds(20));

        // Cancel through the store exactly as the API does, from a different context.
        await using (var scope = sp.CreateAsyncScope())
            Assert.Equal(BackgroundJobCancelOutcome.CancelRequested,
                await scope.ServiceProvider.GetRequiredService<BackgroundJobStore>().RequestCancelAsync(tenantId, jobId, default));
        release.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(20));

        await using var verify = _fx.CreateDb();
        var job = await verify.BackgroundJobs.SingleAsync(j => j.Id == jobId);
        Assert.Equal(BackgroundJobStatuses.Cancelled, job.Status);
        Assert.Equal(4, job.ProgressCompleted);
        Assert.Null(job.LeaseToken);
        Assert.Equal(4, await verify.BackgroundJobItems.CountAsync(i => i.JobId == jobId));
        Assert.Equal(4, (await AppliedRowsAsync(verify, tenantId)).Count(r => r.StartsWith(jobId.ToString())));
        Assert.False(probe.Invocations.ContainsKey($"{jobId}:item:4"), "item 4 must never have started");
    }

    [Fact]
    public async Task CancelWhileQueued_JobNeverRuns()
    {
        var type = NewType();
        var tenantId = await SeedTenantAsync();
        var probe = new ItemProbe();
        await using var sp = BuildInstance(type, probe);
        Guid jobId;
        await using (var scope = sp.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<BackgroundJobStore>();
            jobId = (await store.EnqueueAsync(tenantId, type.JobType, "q", new ItemsPayload(3), null, default)).Job.Id;
            Assert.Equal(BackgroundJobCancelOutcome.Cancelled, await store.RequestCancelAsync(tenantId, jobId, default));
            Assert.Equal(BackgroundJobCancelOutcome.AlreadyFinished, await store.RequestCancelAsync(tenantId, jobId, default));
        }
        Assert.False(await sp.GetRequiredService<BackgroundJobRunner>().RunNextAsync("w", default, default, [type.JobType]));
        Assert.Empty(probe.Executions);
    }

    // ───────────────────────────── 6. Graceful shutdown ─────────────────────────────

    [Fact]
    public async Task Sigterm_InFlightItemReachesCheckpoint_LeaseReleased_NextInstanceResumes()
    {
        var type = NewType();
        var tenantId = await SeedTenantAsync();
        var probe = new ItemProbe();
        var inItem2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.InsideItem = async i =>
        {
            if (i != 2 || inItem2.Task.IsCompleted) return;
            inItem2.SetResult();
            await stopped.Task; // item 2 is mid-transaction while the host is asked to stop
        };
        await using var instance1 = BuildInstance(type, probe);
        Guid jobId;
        await using (var scope = instance1.CreateAsyncScope())
            jobId = (await scope.ServiceProvider.GetRequiredService<BackgroundJobStore>()
                .EnqueueAsync(tenantId, type.JobType, "deploy", new ItemsPayload(8), null, default)).Job.Id;

        // The REAL hosted service, driven through the host lifecycle calls a SIGTERM produces.
        var worker = new BackgroundJobWorker(instance1.GetRequiredService<BackgroundJobRunner>(),
            instance1.GetRequiredService<BackgroundJobOptions>(), NullLogger<BackgroundJobWorker>.Instance);
        await worker.StartAsync(default);
        await inItem2.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var stopping = worker.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);
        await Task.Delay(100);
        stopped.SetResult(); // let the in-flight item finish
        await stopping.WaitAsync(TimeSpan.FromSeconds(25));
        worker.Dispose();

        await using (var verify = _fx.CreateDb())
        {
            var job = await verify.BackgroundJobs.SingleAsync(j => j.Id == jobId);
            Assert.Equal(BackgroundJobStatuses.Queued, job.Status);       // handed back
            Assert.Null(job.LeaseToken);                                    // lease released
            Assert.Equal(0, job.AttemptCount);                              // a deploy is not a failed attempt
            Assert.Equal(3, job.ProgressCompleted);                         // items 0,1 and the in-flight 2
            Assert.Equal(3, await verify.BackgroundJobItems.CountAsync(i => i.JobId == jobId));
        }

        // The next instance resumes immediately (no lease wait) and finishes the remaining 5.
        await using var instance2 = BuildInstance(type, probe);
        Assert.True(await instance2.GetRequiredService<BackgroundJobRunner>().RunNextAsync("next", default, default, [type.JobType]));
        await using var final = _fx.CreateDb();
        var done = await final.BackgroundJobs.SingleAsync(j => j.Id == jobId);
        Assert.Equal(BackgroundJobStatuses.Succeeded, done.Status);
        Assert.Equal(1, done.AttemptCount);
        Assert.Equal(8, done.ProgressCompleted);
        var applied = (await AppliedRowsAsync(final, tenantId)).Where(r => r.StartsWith(jobId.ToString())).ToList();
        Assert.Equal(8, applied.Count);
        Assert.Equal(8, applied.Distinct().Count());
    }

    [Fact]
    public async Task RetryableFailure_RequeuesWithBackoff_AndTheRetryResumesFromCheckpoint()
    {
        var type = NewType();
        var tenantId = await SeedTenantAsync();
        var probe = new ItemProbe { FailOnceAtItem = 2 };
        await using var sp = BuildInstance(type, probe);
        Guid jobId;
        await using (var scope = sp.CreateAsyncScope())
            jobId = (await scope.ServiceProvider.GetRequiredService<BackgroundJobStore>()
                .EnqueueAsync(tenantId, type.JobType, "flaky", new ItemsPayload(4), null, default)).Job.Id;
        var runner = sp.GetRequiredService<BackgroundJobRunner>();
        Assert.True(await runner.RunNextAsync("w", default, default, [type.JobType]));

        await using (var db = _fx.CreateDb())
        {
            var job = await db.BackgroundJobs.SingleAsync(j => j.Id == jobId);
            Assert.Equal(BackgroundJobStatuses.Queued, job.Status);
            Assert.True(job.RunAfterUtc > DateTime.UtcNow.AddSeconds(5), "back-off applied");
            Assert.Contains("transient", job.LastError);
            Assert.Equal(2, job.ProgressCompleted);
            // Make it due now instead of waiting out the back-off.
            await db.BackgroundJobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s.SetProperty(j => j.RunAfterUtc, DateTime.UtcNow.AddSeconds(-1)));
        }
        Assert.True(await runner.RunNextAsync("w", default, default, [type.JobType]));
        await using var verify = _fx.CreateDb();
        var final = await verify.BackgroundJobs.SingleAsync(j => j.Id == jobId);
        Assert.Equal(BackgroundJobStatuses.Succeeded, final.Status);
        Assert.Equal(2, final.AttemptCount);
        Assert.Equal(1, probe.Invocations[$"{jobId}:item:0"]); // not re-run on retry
        Assert.Equal(4, (await AppliedRowsAsync(verify, tenantId)).Count(r => r.StartsWith(jobId.ToString())));
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static BackgroundJobTypeDescriptor NewType() => new(
        $"test.items.{Guid.NewGuid():N}", typeof(ItemsTestHandler), ["attendance.read"], ["attendance.write"]);

    private ServiceProvider BuildInstance(BackgroundJobTypeDescriptor type, ItemProbe probe, TimeSpan? lease = null) =>
        BuildInstance([type], probe, lease ?? TimeSpan.FromMinutes(1));

    /// <summary>One simulated API instance: its own DI container, its own DbContexts and connection pool use.</summary>
    private ServiceProvider BuildInstance(BackgroundJobTypeDescriptor[] types, ItemProbe probe, TimeSpan lease)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => _fx.CreateDb());
        services.AddSingleton(probe);
        services.AddSingleton(new InstanceTag(Guid.NewGuid().ToString("N")));
        services.AddSingleton(new BackgroundJobOptions
        {
            LeaseDuration = lease,
            HeartbeatInterval = TimeSpan.FromMilliseconds(Math.Max(100, lease.TotalMilliseconds / 3)),
            PollInterval = TimeSpan.FromMilliseconds(50),
            Concurrency = 1,
        });
        foreach (var t in types) services.AddSingleton(t);
        services.AddScoped<ItemsTestHandler>();
        services.AddSingleton<BackgroundJobTypeRegistry>();
        services.AddScoped<BackgroundJobStore>();
        services.AddSingleton<BackgroundJobRunner>();
        return services.BuildServiceProvider();
    }

    private ZayraDbContext CreateDbWithoutRowLocking() => new(
        new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(_fx.ConnectionString, o => o.EnableRetryOnFailure(5, TimeSpan.FromSeconds(5), null))
            .Options);

    private (JobsController Controller, ZayraDbContext Db) CreateJobsController(
        ServiceProvider sp, Guid tenantId, Guid userId, params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new("sub", userId.ToString()),
        };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        http.Connection.RemoteIpAddress = IPAddress.Loopback;
        var db = _fx.CreateDbWithAccessor(new HttpContextAccessor { HttpContext = http });
        var controller = new JobsController(new BackgroundJobStore(db, sp.GetRequiredService<BackgroundJobTypeRegistry>()),
            sp.GetRequiredService<BackgroundJobTypeRegistry>())
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
        return (controller, db);
    }

    private async Task<Guid> SeedTenantAsync()
    {
        await using var db = _fx.CreateDb();
        return await PostgresFixture.SeedMinimalTenant(db);
    }

    private static Task<List<string>> AppliedRowsAsync(ZayraDbContext db, Guid tenantId) =>
        db.AttendanceAuditLogs.Where(a => a.TenantId == tenantId && a.Action == ItemsTestHandler.AppliedAction)
            .Select(a => a.EntityId).ToListAsync();

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("Condition not met within " + timeout);
    }
}

internal static class BackgroundJobQueueTestTypes
{
    /// <summary>The attendance type key (so the API's type registry recognises it) served by the test handler.</summary>
    public static readonly BackgroundJobTypeDescriptor Attendance = new(
        Zayra.Api.Infrastructure.Attendance.AttendanceProcessingJobHandler.JobType,
        typeof(ItemsTestHandler), ["attendance.read"], ["attendance.write"]);
}

public sealed record ItemsPayload(int Items);

/// <summary>Identifies which simulated instance (DI container) ran a job.</summary>
public sealed record InstanceTag(string Name);

public sealed class SimulatedProcessDeath() : Exception("simulated process death");

/// <summary>Shared, thread-safe observation point for the test handler across simulated instances.</summary>
public sealed class ItemProbe
{
    public int ItemDelayMs { get; init; }
    public int? CrashOnFirstAttemptAtItem { get; set; }
    public int? FailOnceAtItem { get; set; }
    public Func<int, Task>? BeforeItem { get; set; }
    public Func<int, Task>? InsideItem { get; set; }
    private int _failedOnce;

    public ConcurrentDictionary<Guid, int> Executions { get; } = new();
    public ConcurrentDictionary<Guid, string> ExecutedBy { get; } = new();
    /// <summary>Every time an item's apply delegate RAN — including runs whose transaction rolled back.</summary>
    public ConcurrentDictionary<string, int> Invocations { get; } = new();

    public void Record(Guid jobId, string item) => Invocations.AddOrUpdate($"{jobId}:{item}", 1, (_, n) => n + 1);
    public bool ShouldFailOnce() => Interlocked.Exchange(ref _failedOnce, 1) == 0;
}

/// <summary>
/// Applies N items. Each item writes one AttendanceAuditLog row keyed "{jobId}:item:{i}" — a plain insert
/// with no de-duplication, so an item applied twice would leave two rows.
/// </summary>
public sealed class ItemsTestHandler(ItemProbe probe, InstanceTag instance) : IBackgroundJobHandler
{
    public const string AppliedAction = "f3.test.item_applied";

    public async Task ExecuteAsync(JobExecutionContext ctx)
    {
        probe.Executions.AddOrUpdate(ctx.JobId, 1, (_, n) => n + 1);
        probe.ExecutedBy[ctx.JobId] = instance.Name;
        var payload = ctx.GetPayload<ItemsPayload>();
        await ctx.SetTotalAsync(payload.Items);
        for (var i = 0; i < payload.Items; i++)
        {
            var index = i;
            var key = $"item:{index}";
            if (ctx.IsItemCompleted(key)) continue;
            if (probe.BeforeItem is not null) await probe.BeforeItem(index);
            await ctx.RunItemAsync(key, async ct =>
            {
                probe.Record(ctx.JobId, key);
                ctx.Db.AttendanceAuditLogs.Add(new AttendanceAuditLog
                {
                    TenantId = ctx.TenantId,
                    Action = AppliedAction,
                    EntityName = "BackgroundJobTest",
                    EntityId = $"{ctx.JobId}:{key}",
                });
                if (probe.InsideItem is not null) await probe.InsideItem(index);
                if (probe.ItemDelayMs > 0) await Task.Delay(probe.ItemDelayMs, ct);
                if (probe.CrashOnFirstAttemptAtItem == index && ctx.Attempt == 1) throw new SimulatedProcessDeath();
                if (probe.FailOnceAtItem == index && probe.ShouldFailOnce()) throw new InvalidOperationException("transient downstream error");
            });
        }
        ctx.SetResult(new { items = payload.Items });
    }
}
