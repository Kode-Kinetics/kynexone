using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.AI;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Operations;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Multi-instance safety for the periodic sweeps that are not driven by background_jobs. Every API
/// instance hosts every worker, so once two instances run (scale-out, or old and new overlapping in a
/// deploy) the same sweep fires twice. <see cref="SingletonWorkerLease"/> lets exactly one instance run
/// a given sweep at a time; the others skip the tick. Real PostgreSQL: the lease is an advisory lock,
/// which InMemory/SQLite cannot model.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class SingletonWorkerLeasePostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task SecondLeaseForTheSameSweepIsRefused_UntilTheFirstIsReleased()
    {
        await using var a = fixture.CreateDb();
        await using var b = fixture.CreateDb();

        var first = await SingletonWorkerLease.TryAcquireAsync(a, "lease-test", CancellationToken.None);
        first.Should().NotBeNull();
        first!.IsHeld.Should().BeTrue();
        (await SingletonWorkerLease.TryAcquireAsync(b, "lease-test", CancellationToken.None)).Should().BeNull();
        // A different sweep is independent.
        await using (var other = await SingletonWorkerLease.TryAcquireAsync(b, "lease-test-other", CancellationToken.None))
            other.Should().NotBeNull();

        await first.DisposeAsync();
        await using var again = await SingletonWorkerLease.TryAcquireAsync(b, "lease-test", CancellationToken.None);
        again.Should().NotBeNull();
    }

    [Fact]
    public async Task ComplianceReminderSweep_SkipsWhileAnotherInstanceHoldsTheLease()
    {
        var (tenantId, reminderId) = await SeedDueReminderAsync();
        await using var provider = BuildProvider();

        await using (var holder = fixture.CreateDb())
        await using (var held = await SingletonWorkerLease.TryAcquireAsync(holder, ProductionWorkerNames.ComplianceReminders, CancellationToken.None))
        {
            held.Should().NotBeNull();
            (await ReminderWorker(provider).DrainOnceAsync(CancellationToken.None)).Should().Be(0);
            await using var verify = fixture.CreateDb();
            (await verify.ComplianceReminders.AsNoTracking().SingleAsync(r => r.Id == reminderId)).Status.Should().Be("Pending");
            (await DeliveriesFor(verify, tenantId, reminderId)).Should().Be(0);
        }

        (await ReminderWorker(provider).DrainOnceAsync(CancellationToken.None)).Should().BeGreaterThanOrEqualTo(1);
        await using var after = fixture.CreateDb();
        (await after.ComplianceReminders.AsNoTracking().SingleAsync(r => r.Id == reminderId)).Status.Should().Be("Sent");
    }

    [Fact]
    public async Task TwoConcurrentComplianceSweeps_EnqueueTheReminderOnce()
    {
        var (tenantId, reminderId) = await SeedDueReminderAsync();
        await using var providerA = BuildProvider();
        await using var providerB = BuildProvider();

        using var start = new ManualResetEventSlim(false);
        var a = Task.Run(async () => { start.Wait(); return await ReminderWorker(providerA).DrainOnceAsync(CancellationToken.None); });
        var b = Task.Run(async () => { start.Wait(); return await ReminderWorker(providerB).DrainOnceAsync(CancellationToken.None); });
        start.Set();
        await Task.WhenAll(a, b);

        await using var verify = fixture.CreateDb();
        (await verify.ComplianceReminders.AsNoTracking().SingleAsync(r => r.Id == reminderId)).Status.Should().Be("Sent");
        var employee = await verify.Employees.AsNoTracking().SingleAsync(e => e.TenantId == tenantId);
        (await verify.EmployeeNotifications.AsNoTracking()
            .CountAsync(n => n.TenantId == tenantId && n.EmployeeId == employee.Id)).Should().Be(1);
        (await verify.NotificationDeliveries.AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.EntityId == reminderId.ToString())
            .GroupBy(d => d.Channel).Select(g => g.Count()).ToListAsync())
            .Should().OnlyContain(n => n == 1, "each channel is enqueued exactly once");
    }

    [Fact]
    public async Task AiInsightCycle_SkipsWhileAnotherInstanceHoldsTheLease()
    {
        var tenantId = await SeedTenantMissingSalarySetupAsync();
        await using var provider = BuildProvider();

        await using (var holder = fixture.CreateDb())
        await using (var held = await SingletonWorkerLease.TryAcquireAsync(holder, ProductionWorkerNames.AiInsights, CancellationToken.None))
        {
            held.Should().NotBeNull();
            await InsightEngine(provider).AnalyzeOnceAsync(CancellationToken.None);
            await using var verify = fixture.CreateDb();
            (await verify.AIInsights.AsNoTracking().CountAsync(i => i.TenantId == tenantId)).Should().Be(0);
        }

        await InsightEngine(provider).AnalyzeOnceAsync(CancellationToken.None);
        await using var after = fixture.CreateDb();
        (await after.AIInsights.AsNoTracking().CountAsync(i => i.TenantId == tenantId && i.InsightType == "MissingSalarySetup"))
            .Should().Be(1);
    }

    /// <summary>
    /// Deterministic version of the race. Instance A is frozen at the exact point that loses data
    /// without the lease: after its 24h dedupe read, before its INSERT commits. Instance B then runs a
    /// whole cycle. Unguarded, B also reads "no insight yet" and inserts, A resumes and inserts again:
    /// two rows. Guarded, B skips because A holds the sweep lease.
    /// </summary>
    [Fact]
    public async Task TwoConcurrentAiInsightCycles_MaterialiseEachInsightOnce()
    {
        var tenantId = await SeedTenantMissingSalarySetupAsync();
        var gate = new InsertGate(tenantId);
        await using var providerA = BuildProvider(gate);
        await using var providerB = BuildProvider();

        var a = Task.Run(() => InsightEngine(providerA).AnalyzeOnceAsync(CancellationToken.None));
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(60));
        await InsightEngine(providerB).AnalyzeOnceAsync(CancellationToken.None);
        gate.Release.SetResult();
        await a;

        await using var verify = fixture.CreateDb();
        var types = await verify.AIInsights.AsNoTracking()
            .Where(i => i.TenantId == tenantId).Select(i => i.InsightType).ToListAsync();
        types.Should().Contain("MissingSalarySetup");
        types.Should().OnlyHaveUniqueItems("two concurrent cycles must not each insert the same insight");
    }

    [Fact]
    public async Task LoanLifecycleSweep_SkipsWhileAnotherInstanceHoldsTheLease()
    {
        await using var provider = BuildProvider();
        var worker = new LoanLifecycleWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<ILogger<LoanLifecycleWorker>>());

        await using (var holder = fixture.CreateDb())
        await using (var held = await SingletonWorkerLease.TryAcquireAsync(holder, SingletonWorkerNames.LoanLifecycle, CancellationToken.None))
        {
            held.Should().NotBeNull();
            (await worker.RunOnceAsync(CancellationToken.None)).Outcome.Should().Be(SweepOutcome.Skipped, "the tick is skipped, not run");
        }

        (await worker.RunOnceAsync(CancellationToken.None)).Outcome.Should().Be(SweepOutcome.Completed, "the lease is free again");
    }

    /// <summary>
    /// The lease's connection dies mid-sweep (here: terminated by the server, as an idle-ceiling kill or
    /// a network drop would). The keepalive only notices every 30s, so without the end-of-sweep check
    /// the sweep would finish and report a clean run it did not have.
    /// </summary>
    [Fact]
    public async Task ComplianceReminderSweep_ThatLosesItsLeaseMidway_ReportsLeaseLost_NotCompleted()
    {
        var (_, reminderId) = await SeedDueReminderAsync();
        var gate = new FirstCommandGate("compliance_reminders");
        await using var provider = BuildProvider(gate);
        var worker = ReminderWorker(provider);

        var sweep = Task.Run(() => worker.DrainOnceAsync(CancellationToken.None));
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(60));
        (await TerminateLeaseHolderAsync(ProductionWorkerNames.ComplianceReminders)).Should().Be(1);
        gate.Release.SetResult();
        await sweep;

        worker.LastSweepOutcome.Should().Be(SweepOutcome.LeaseLost);
        await using var verify = fixture.CreateDb();
        (await verify.ComplianceReminders.AsNoTracking().SingleAsync(r => r.Id == reminderId)).Status
            .Should().Be("Sent", "rows already enqueued before the loss are still recorded");
    }

    [Fact]
    public async Task ComplianceReminderSweep_ReportsSkippedAndCompleted()
    {
        await using var provider = BuildProvider();
        var worker = ReminderWorker(provider);
        await using (var holder = fixture.CreateDb())
        await using (var held = await SingletonWorkerLease.TryAcquireAsync(holder, ProductionWorkerNames.ComplianceReminders, CancellationToken.None))
        {
            await worker.DrainOnceAsync(CancellationToken.None);
            worker.LastSweepOutcome.Should().Be(SweepOutcome.Skipped);
        }
        await worker.DrainOnceAsync(CancellationToken.None);
        worker.LastSweepOutcome.Should().Be(SweepOutcome.Completed);
    }

    [Fact]
    public async Task AiInsightCycle_ThatLosesItsLeaseMidway_ReportsLeaseLost_NotCompleted()
    {
        var tenantId = await SeedTenantMissingSalarySetupAsync();
        var gate = new InsertGate(tenantId);
        await using var provider = BuildProvider(gate);

        var cycle = Task.Run(() => InsightEngine(provider).AnalyzeOnceAsync(CancellationToken.None));
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(60));
        (await TerminateLeaseHolderAsync(ProductionWorkerNames.AiInsights)).Should().Be(1);
        gate.Release.SetResult();

        (await cycle).Should().Be(SweepOutcome.LeaseLost);
    }

    /// <summary>Kills the server session holding the sweep's advisory lock (a 64-bit key: classid = high word, objid = low word).</summary>
    private async Task<int> TerminateLeaseHolderAsync(string workerName)
    {
        var key = SingletonWorkerLease.LockKey(workerName);
        await using var admin = fixture.CreateDb();
        var hi = (long)(uint)(key >> 32);
        var lo = (long)(uint)key;
        return await admin.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM (
                SELECT pg_terminate_backend(pid) FROM pg_locks
                WHERE locktype = 'advisory' AND granted AND objsubid = 1
                  AND classid::bigint = {hi} AND objid::bigint = {lo}) t
            """).SingleAsync();
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    /// <summary>One DI container per simulated API instance, each with its own DbContexts.</summary>
    private ServiceProvider BuildProvider(DbCommandInterceptor? gate = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddDataProtection();
        services.AddDbContext<ZayraDbContext>(o => o
            .UseNpgsql(fixture.ConnectionString, PostgresFixture.ProductionProviderOptions)
            .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance)
            .AddInterceptors(AdvisoryXactLockGuardInterceptor.Instance)
            .AddInterceptors(gate is null ? [] : [gate]));
        services.AddSingleton<IEmailService, NoEmail>();
        services.AddScoped<INotificationRecipientResolver, NotificationRecipientResolver>();
        services.AddScoped<INotificationProviderConfigReader, NotificationProviderConfigReader>();
        services.AddScoped<ISmsProvider, NullSmsProvider>();
        services.AddScoped<IWhatsAppProvider, NullWhatsAppProvider>();
        services.AddScoped<IPushProvider, NullPushProvider>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<Zayra.Api.Infrastructure.Modules.ITenantModuleService,
                           Zayra.Api.Infrastructure.Modules.TenantModuleService>();
        return services.BuildServiceProvider();
    }

    private static ComplianceReminderWorker ReminderWorker(IServiceProvider p) =>
        new(p.GetRequiredService<IServiceScopeFactory>(), p.GetRequiredService<ILogger<ComplianceReminderWorker>>());

    private static AiInsightEngine InsightEngine(IServiceProvider p) =>
        new(p.GetRequiredService<IServiceScopeFactory>(), p.GetRequiredService<ILogger<AiInsightEngine>>());

    private static Task<int> DeliveriesFor(ZayraDbContext db, Guid tenantId, Guid reminderId) =>
        db.NotificationDeliveries.AsNoTracking()
            .CountAsync(d => d.TenantId == tenantId && d.EntityName == "ComplianceReminder" && d.EntityId == reminderId.ToString());

    private async Task<(Guid TenantId, Guid ReminderId)> SeedDueReminderAsync()
    {
        await using var db = fixture.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var employee = new Employee { TenantId = tenantId, EmployeeCode = "LEASE-1", FullName = "Lease Test Employee", Status = "Active" };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var reminder = new ComplianceReminder
        {
            TenantId = tenantId, EmployeeId = employee.PublicId, EmployeeName = "snapshot",
            ReminderType = "PassportExpiry", DocumentType = "Passport",
            ExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            ScheduledAtUtc = DateTime.UtcNow.AddMinutes(-1), Status = "Pending",
        };
        db.ComplianceReminders.Add(reminder);
        await db.SaveChangesAsync();
        return (tenantId, reminder.Id);
    }

    /// <summary>An active employee with no salary structure: the engine always raises MissingSalarySetup.</summary>
    private async Task<Guid> SeedTenantMissingSalarySetupAsync()
    {
        await using var db = fixture.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        db.Employees.Add(new Employee { TenantId = tenantId, EmployeeCode = "LEASE-AI", FullName = "Unpaid Setup", Status = "Active" });
        await db.SaveChangesAsync();
        return tenantId;
    }

    /// <summary>Freezes the first ai_insights INSERT for one tenant until the test releases it.</summary>
    private sealed class InsertGate(Guid tenantId) : DbCommandInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await HoldIfTargetAsync(command, cancellationToken);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await HoldIfTargetAsync(command, cancellationToken);
            return result;
        }

        private async Task HoldIfTargetAsync(DbCommand command, CancellationToken ct)
        {
            if (command.CommandText.Contains("INSERT INTO ai_insights", StringComparison.OrdinalIgnoreCase)
                && command.Parameters.Cast<DbParameter>().Any(p => p.Value is Guid g && g == tenantId)
                && Reached.TrySetResult())
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
        }
    }

    /// <summary>Freezes the first command whose SQL mentions <paramref name="table"/> until the test releases it.</summary>
    private sealed class FirstCommandGate(string table) : DbCommandInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(table, StringComparison.OrdinalIgnoreCase) && Reached.TrySetResult())
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
            return result;
        }
    }

    private sealed class NoEmail : IEmailService
    {
        public Task SendAsync(string toAddress, string toName, string subject, string htmlBody,
            IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("the reminder sweep only enqueues; it never sends");
        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
