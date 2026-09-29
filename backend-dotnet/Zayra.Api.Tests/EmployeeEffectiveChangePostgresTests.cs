using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// An approved employee change with a FUTURE effective date — a new IBAN, above all — must take effect when
/// that date arrives, exactly once, and never over a value that moved since it was approved.
///
/// <para>THE DEFECT. The Approval Center set such a change to <c>ApprovedPendingEffectiveDate</c> and
/// nothing ever read that status again: the IBAN never reached the payroll profile, and payroll paid the
/// old account indefinitely. <see cref="FutureDatedBankChange_TakesEffectOnceWhenItsDateArrives"/> is the
/// reproduction — it fails on the base branch at the first IBAN assertion.</para>
///
/// <para>Real PostgreSQL: the guarantees are row locks (<c>FOR UPDATE</c>), the queue's <c>SKIP LOCKED</c>
/// claim and the item transaction, none of which the in-memory provider models. The whole path runs the
/// production pieces: the controller submits, <see cref="ApprovalWorkflowService"/> approves,
/// <see cref="EffectiveChangeScheduler"/> enqueues against a controllable clock, and the real
/// <see cref="BackgroundJobRunner"/> executes <see cref="EffectiveChangeJobHandler"/>.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class EmployeeEffectiveChangePostgresTests(PostgresFixture fx)
{
    private const string OldIban = "SA0380000000608010167519";
    private const string NewIban = "SA5380000000006080101001";
    private const string OtherIban = "SA4420000001234567891234";

    // One key ring for the approver (seals the baseline) and the job (opens it) — as in production, where
    // every instance shares the ring persisted in PostgreSQL.
    private static readonly IDataProtectionProvider Keys = new EphemeralDataProtectionProvider();

    private static DateOnly UtcToday => DateOnly.FromDateTime(DateTime.UtcNow);

    // ─────────────────────── the defect ───────────────────────

    [Fact]
    public async Task FutureDatedBankChange_TakesEffectOnceWhenItsDateArrives()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var notes = new RecordingNotifications();
        await using var sp = BuildInstance(clock, notes);
        var (tenant, employeeId) = await SeedAsync();
        var approver = Guid.NewGuid();
        var changeId = await RequestAndApproveAsync(tenant, employeeId, UtcToday.AddDays(1), "bankIban", NewIban, approver);

        await using (var db = fx.CreateDb())
        {
            (await db.EmployeeChangeRequests.SingleAsync(x => x.Id == changeId)).Status
                .Should().Be(EmployeeChangeStatuses.ApprovedPendingEffectiveDate);
            (await ProfileAsync(db, tenant, employeeId)).Iban.Should().Be(OldIban, "nothing is applied before its date");
        }

        // Today: not due yet, so no job is created for this tenant.
        await sp.GetRequiredService<EffectiveChangeScheduler>().EnqueueDueAsync(default);
        (await JobCountAsync(tenant)).Should().Be(0);

        // The date arrives.
        clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromHours(1));
        await sp.GetRequiredService<EffectiveChangeScheduler>().EnqueueDueAsync(default);
        (await JobCountAsync(tenant)).Should().Be(1);
        await DrainAsync(sp, tenant);

        await using (var db = fx.CreateDb())
        {
            var employee = await db.Employees.SingleAsync(x => x.Id == employeeId);
            var profile = await ProfileAsync(db, tenant, employeeId);
            employee.BankIban.Should().Be(NewIban);
            profile.Iban.Should().Be(NewIban, "WPS/SIF pays from the payroll profile, not the employee scalar");
            // Field-scoped sync (#129): an IBAN-only approval leaves the rest of the profile alone.
            profile.BankName.Should().Be("Old Bank");
            profile.MolId.Should().Be("KEEP-MOL");
            profile.AccountNumber.Should().Be("KEEP-ACCOUNT");

            var change = await db.EmployeeChangeRequests.SingleAsync(x => x.Id == changeId);
            change.Status.Should().Be(EmployeeChangeStatuses.ApprovedApplied);
            change.AppliedAtUtc.Should().NotBeNull();
            change.ApprovedByUserId.Should().Be(approver);
        }
        (await AuditCountAsync(tenant, changeId, EffectiveChangeJobHandler.AppliedAction)).Should().Be(1);
        (await HistoryCountAsync(tenant, employeeId, EffectiveChangeJobHandler.AppliedEventType)).Should().Be(1);
        notes.Sent.Should().ContainSingle(n => n.UserId == approver && n.Title.Contains("took effect"));

        // Later ticks find nothing due; a forced extra run finds it decided and writes nothing.
        clock.Advance(TimeSpan.FromHours(2));
        await sp.GetRequiredService<EffectiveChangeScheduler>().EnqueueDueAsync(default);
        (await JobCountAsync(tenant)).Should().Be(1);
        await EnqueueAsync(sp, tenant, "forced-rerun", clock.GetUtcNow().UtcDateTime);
        await DrainAsync(sp, tenant);
        (await AuditCountAsync(tenant, changeId, EffectiveChangeJobHandler.AppliedAction)).Should().Be(1);
        (await HistoryCountAsync(tenant, employeeId, EffectiveChangeJobHandler.AppliedEventType)).Should().Be(1);
    }

    // ─────────────────────── exactly once ───────────────────────

    [Fact]
    public async Task TwoJobsRunningConcurrently_ApplyTheChangeExactlyOnce()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var sp = BuildInstance(clock, new RecordingNotifications());
        var (tenant, employeeId) = await SeedAsync();
        var changeId = await RequestAndApproveAsync(tenant, employeeId, UtcToday.AddDays(1), "bankIban", NewIban);
        clock.Advance(TimeSpan.FromDays(2));

        // Two instances / two keys (e.g. a deploy cutover across local midnight): both jobs are live at once.
        await EnqueueAsync(sp, tenant, "instance-a", clock.GetUtcNow().UtcDateTime);
        await EnqueueAsync(sp, tenant, "instance-b", clock.GetUtcNow().UtcDateTime);
        var runner = sp.GetRequiredService<BackgroundJobRunner>();
        await Task.WhenAll(
            Task.Run(() => runner.RunNextAsync("worker-a", default, default, [EffectiveChangeJobHandler.JobType])),
            Task.Run(() => runner.RunNextAsync("worker-b", default, default, [EffectiveChangeJobHandler.JobType])));
        await DrainAsync(sp, tenant);

        (await AuditCountAsync(tenant, changeId, EffectiveChangeJobHandler.AppliedAction)).Should().Be(1);
        (await HistoryCountAsync(tenant, employeeId, EffectiveChangeJobHandler.AppliedEventType)).Should().Be(1);
        await using var db = fx.CreateDb();
        (await ProfileAsync(db, tenant, employeeId)).Iban.Should().Be(NewIban);
    }

    [Fact]
    public async Task SecondTransaction_WaitsOnTheRowLock_ThenFindsTheChangeAlreadyApplied()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var sp = BuildInstance(clock, new RecordingNotifications());
        var (tenant, employeeId) = await SeedAsync();
        var changeId = await RequestAndApproveAsync(tenant, employeeId, UtcToday.AddDays(1), "bankIban", NewIban);
        var localToday = UtcToday.AddDays(2);
        var handler = new EffectiveChangeJobHandler(new EffectiveChangeOptions(),
            NullLogger<EffectiveChangeJobHandler>.Instance, Keys);
        var noServices = new ServiceCollection().BuildServiceProvider();

        var firstHoldsLock = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();
        await using var dbA = fx.CreateDb();
        var first = dbA.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await dbA.Database.BeginTransactionAsync();
            var result = await handler.ProcessAsync(dbA, noServices, tenant, changeId, localToday, null, default);
            await dbA.SaveChangesAsync();
            firstHoldsLock.TrySetResult();
            await releaseFirst.Task;
            await tx.CommitAsync();
            return result;
        });
        await firstHoldsLock.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await using var dbB = fx.CreateDb();
        var second = dbB.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await dbB.Database.BeginTransactionAsync();
            var result = await handler.ProcessAsync(dbB, noServices, tenant, changeId, localToday, null, default);
            await dbB.SaveChangesAsync();
            await tx.CommitAsync();
            return result;
        });

        // The second is blocked on SELECT … FOR UPDATE of the change row while the first is uncommitted.
        (await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(1)))).Should().NotBe(second);
        releaseFirst.SetResult();

        (await first).Outcome.Should().Be(EffectiveChangeOutcome.Applied);
        (await second).Outcome.Should().Be(EffectiveChangeOutcome.AlreadyHandled);
        (await AuditCountAsync(tenant, changeId, EffectiveChangeJobHandler.AppliedAction)).Should().Be(1);
        (await HistoryCountAsync(tenant, employeeId, EffectiveChangeJobHandler.AppliedEventType)).Should().Be(1);
    }

    // ─────────────────────── never blind ───────────────────────

    [Fact]
    public async Task FieldChangedAfterApproval_IsReturnedForReview_NotOverwritten_AndReapprovalAppliesIt()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var notes = new RecordingNotifications();
        await using var sp = BuildInstance(clock, notes);
        var (tenant, employeeId) = await SeedAsync();
        var approver = Guid.NewGuid();
        var changeId = await RequestAndApproveAsync(tenant, employeeId, UtcToday.AddDays(1), "bankIban", NewIban, approver);
        Guid originalApprovalId;
        await using (var db = fx.CreateDb())
        {
            originalApprovalId = (await db.EmployeeChangeRequests.SingleAsync(x => x.Id == changeId)).ApprovalRequestId!.Value;
            // After approval, someone edits the account payroll pays into (the payroll-profile editor path).
            var profile = await ProfileAsync(db, tenant, employeeId);
            profile.Iban = OtherIban;
            (await db.Employees.SingleAsync(x => x.Id == employeeId)).BankIban = OtherIban;
            await db.SaveChangesAsync();
        }

        clock.Advance(TimeSpan.FromDays(2));
        await sp.GetRequiredService<EffectiveChangeScheduler>().EnqueueDueAsync(default);
        await DrainAsync(sp, tenant);

        Guid reviewApprovalId;
        await using (var db = fx.CreateDb())
        {
            (await ProfileAsync(db, tenant, employeeId)).Iban.Should().Be(OtherIban, "a newer value is never overwritten blindly");
            (await db.Employees.SingleAsync(x => x.Id == employeeId)).BankIban.Should().Be(OtherIban);
            var change = await db.EmployeeChangeRequests.SingleAsync(x => x.Id == changeId);
            change.Status.Should().Be(EmployeeChangeStatuses.PendingApproval);
            change.AppliedAtUtc.Should().BeNull();
            change.ApprovalRequestId.Should().NotBe(originalApprovalId);
            reviewApprovalId = change.ApprovalRequestId!.Value;
            var review = await db.ApprovalRequests.SingleAsync(x => x.Id == reviewApprovalId);
            review.Status.Should().Be("Pending");
            review.EntityId.Should().Be(changeId.ToString());
            review.Title.Should().Contain("re-review").And.Contain("IBAN changed after this change was approved");
            (await db.ApprovalRequests.SingleAsync(x => x.Id == originalApprovalId)).Status.Should().Be("Approved");
        }
        var audit = await SingleAuditAsync(tenant, changeId, EffectiveChangeJobHandler.ReturnedForReviewAction);
        audit.Metadata.Should().Contain("changed_since_approval").And.NotContain(OtherIban).And.NotContain(NewIban);
        (await HistoryCountAsync(tenant, employeeId, EffectiveChangeJobHandler.ReturnedForReviewEventType)).Should().Be(1);
        notes.Sent.Should().ContainSingle(n => n.UserId == approver && n.Title.Contains("needs review"));

        // Closing the loop: a reviewer approves the re-review in the Approval Center; its date has passed,
        // so it applies now through the normal path, bank sync included. (The approval service reads the
        // real clock, which this test has not advanced; bring the date to today to stand for the day passing.)
        await using (var db = fx.CreateDb())
        {
            await db.EmployeeChangeRequests.Where(x => x.Id == changeId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.EffectiveDate, UtcToday));
            var service = new ApprovalWorkflowService(db, new AuditService(db), new HrmHierarchyService(db, new AuditService(db)), dataProtection: Keys);
            await service.DecideAsync(tenant, reviewApprovalId, new ApprovalDecisionRequest("Approve", "Confirmed with the employee"),
                new RequestContext("127.0.0.1", "test", Guid.NewGuid(), tenant, ["HR Manager"], []), default);
        }
        await using (var db = fx.CreateDb())
        {
            (await ProfileAsync(db, tenant, employeeId)).Iban.Should().Be(NewIban);
            (await db.EmployeeChangeRequests.SingleAsync(x => x.Id == changeId)).Status.Should().Be(EmployeeChangeStatuses.ApprovedApplied);
        }
    }

    [Fact]
    public async Task TerminatedEmployee_IsReturnedForReview_AndNothingIsWritten()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var sp = BuildInstance(clock, new RecordingNotifications());
        var (tenant, employeeId) = await SeedAsync();
        var changeId = await RequestAndApproveAsync(tenant, employeeId, UtcToday.AddDays(1), "bankIban", NewIban);
        await using (var db = fx.CreateDb())
        {
            (await db.Employees.SingleAsync(x => x.Id == employeeId)).Status = "Terminated";
            await db.SaveChangesAsync();
        }

        clock.Advance(TimeSpan.FromDays(2));
        await sp.GetRequiredService<EffectiveChangeScheduler>().EnqueueDueAsync(default);
        await DrainAsync(sp, tenant);

        await using var verify = fx.CreateDb();
        (await ProfileAsync(verify, tenant, employeeId)).Iban.Should().Be(OldIban);
        (await verify.EmployeeChangeRequests.SingleAsync(x => x.Id == changeId)).Status.Should().Be(EmployeeChangeStatuses.PendingApproval);
        (await SingleAuditAsync(tenant, changeId, EffectiveChangeJobHandler.ReturnedForReviewAction)).Metadata
            .Should().Contain("employee_separated");
    }

    [Fact]
    public async Task DeletedEmployee_IsCancelled_AndAudited()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var sp = BuildInstance(clock, new RecordingNotifications());
        var (tenant, employeeId) = await SeedAsync();
        var changeId = await RequestAndApproveAsync(tenant, employeeId, UtcToday.AddDays(1), "bankIban", NewIban);
        await using (var db = fx.CreateDb())
        {
            var employee = await db.Employees.SingleAsync(x => x.Id == employeeId);
            employee.IsDeleted = true;
            employee.DeletedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        clock.Advance(TimeSpan.FromDays(2));
        await sp.GetRequiredService<EffectiveChangeScheduler>().EnqueueDueAsync(default);
        await DrainAsync(sp, tenant);

        await using var verify = fx.CreateDb();
        var change = await verify.EmployeeChangeRequests.SingleAsync(x => x.Id == changeId);
        change.Status.Should().Be(EmployeeChangeStatuses.Cancelled);
        change.RejectionReason.Should().Contain("removed");
        (await SingleAuditAsync(tenant, changeId, EffectiveChangeJobHandler.CancelledAction)).Metadata.Should().Contain("employee_removed");
        (await ProfileAsync(verify, tenant, employeeId)).Iban.Should().Be(OldIban);
    }

    [Fact]
    public async Task ChangeApprovedBeforeBaselinesExisted_IsReturnedForReview_NotApplied()
    {
        // Exactly what production holds today: an approved future-dated row and no baseline anywhere.
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var sp = BuildInstance(clock, new RecordingNotifications());
        var (tenant, employeeId) = await SeedAsync();
        var changeId = await RequestAndApproveAsync(tenant, employeeId, UtcToday.AddDays(1), "bankIban", NewIban);
        await using (var db = fx.CreateDb())
        {
            await db.EmployeeHistories
                .Where(h => h.TenantId == tenant && h.EventType == EmployeeChangeBaseline.ScheduledEventType)
                .ExecuteDeleteAsync();
        }

        clock.Advance(TimeSpan.FromDays(2));
        await sp.GetRequiredService<EffectiveChangeScheduler>().EnqueueDueAsync(default);
        await DrainAsync(sp, tenant);

        await using var verify = fx.CreateDb();
        (await ProfileAsync(verify, tenant, employeeId)).Iban.Should().Be(OldIban);
        (await verify.EmployeeChangeRequests.SingleAsync(x => x.Id == changeId)).Status.Should().Be(EmployeeChangeStatuses.PendingApproval);
        (await SingleAuditAsync(tenant, changeId, EffectiveChangeJobHandler.ReturnedForReviewAction)).Metadata.Should().Contain("no_baseline");
    }

    // ─────────────────────── payroll safety ───────────────────────

    [Fact]
    public async Task BankChange_WaitsForALockedRunsPaymentBatch_ThenApplies()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var notes = new RecordingNotifications();
        await using var sp = BuildInstance(clock, notes);
        var (tenant, employeeId) = await SeedAsync();
        var effective = UtcToday.AddDays(1);
        var changeId = await RequestAndApproveAsync(tenant, employeeId, effective, "bankIban", NewIban);
        Guid runId;
        await using (var db = fx.CreateDb())
        {
            // Last month's run: calculated, approved and locked, but its payment batch not created yet —
            // the batch is where the IBAN is copied (PayrollPaymentRecord.Iban) and the SIF is built from it.
            var period = effective.AddMonths(-1);
            var run = new PayrollRun { TenantId = tenant, Year = period.Year, Month = period.Month, Status = "Locked" };
            db.PayrollRuns.Add(run);
            db.PayrollSlips.Add(new PayrollSlip { TenantId = tenant, RunId = run.Id, EmployeeId = employeeId, EmployeeCode = "EFF-1", NetSalary = 1000m });
            await db.SaveChangesAsync();
            runId = run.Id;
        }

        clock.Advance(TimeSpan.FromDays(2));
        for (var tick = 0; tick < 2; tick++)
        {
            await sp.GetRequiredService<EffectiveChangeScheduler>().EnqueueDueAsync(default);
            await DrainAsync(sp, tenant);
            clock.Advance(TimeSpan.FromHours(1));
        }

        await using (var db = fx.CreateDb())
        {
            (await ProfileAsync(db, tenant, employeeId)).Iban.Should().Be(OldIban, "the locked run pays the account it was prepared with");
            (await db.EmployeeChangeRequests.SingleAsync(x => x.Id == changeId)).Status
                .Should().Be(EmployeeChangeStatuses.ApprovedPendingEffectiveDate);
        }
        (await AuditCountAsync(tenant, changeId, EffectiveChangeJobHandler.DeferredAction))
            .Should().Be(1, "two ticks, one deferral record");
        notes.Sent.Count(n => n.Title.Contains("waiting for payroll")).Should().Be(1);

        await using (var db = fx.CreateDb())
        {
            db.PayrollPaymentBatches.Add(new PayrollPaymentBatch { TenantId = tenant, PayrollRunId = runId, WpsStatus = WpsStatuses.Draft });
            await db.SaveChangesAsync();
        }
        await sp.GetRequiredService<EffectiveChangeScheduler>().EnqueueDueAsync(default);
        await DrainAsync(sp, tenant);

        await using var verify = fx.CreateDb();
        (await ProfileAsync(verify, tenant, employeeId)).Iban.Should().Be(NewIban);
        (await AuditCountAsync(tenant, changeId, EffectiveChangeJobHandler.AppliedAction)).Should().Be(1);
    }

    // ─────────────────────── tenant isolation and timezone ───────────────────────

    [Fact]
    public async Task AJobForOneTenant_NeverTouchesAnotherTenantsChange()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var sp = BuildInstance(clock, new RecordingNotifications());
        var (tenantA, employeeA) = await SeedAsync();
        var (tenantB, employeeB) = await SeedAsync();
        await RequestAndApproveAsync(tenantA, employeeA, UtcToday.AddDays(1), "bankIban", NewIban);
        var changeB = await RequestAndApproveAsync(tenantB, employeeB, UtcToday.AddDays(1), "bankIban", NewIban);
        clock.Advance(TimeSpan.FromDays(2));

        // Only tenant A's job runs.
        await EnqueueAsync(sp, tenantA, "a-only", clock.GetUtcNow().UtcDateTime);
        await DrainAsync(sp, tenantA);
        // And tenant A's context cannot reach B's change even when handed its id.
        await using (var db = fx.CreateDb())
        {
            var handler = new EffectiveChangeJobHandler(new EffectiveChangeOptions(), NullLogger<EffectiveChangeJobHandler>.Instance, Keys);
            var crossTenant = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                var result = await handler.ProcessAsync(db, new ServiceCollection().BuildServiceProvider(), tenantA, changeB, UtcToday.AddDays(2), null, default);
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return result;
            });
            crossTenant.Outcome.Should().Be(EffectiveChangeOutcome.AlreadyHandled);
        }

        await using var verify = fx.CreateDb();
        (await ProfileAsync(verify, tenantA, employeeA)).Iban.Should().Be(NewIban);
        (await ProfileAsync(verify, tenantB, employeeB)).Iban.Should().Be(OldIban);
        (await verify.EmployeeChangeRequests.SingleAsync(x => x.Id == changeB)).Status
            .Should().Be(EmployeeChangeStatuses.ApprovedPendingEffectiveDate);
    }

    [Fact]
    public async Task TheEffectiveDateArrivesInTheTenantsTimezone_NotUtc()
    {
        var effective = UtcToday.AddDays(2);
        // 22:00 UTC the day before = 01:00 on the effective date in Riyadh (UTC+3), still the day before in UTC.
        var instant = new DateTimeOffset(effective.AddDays(-1).ToDateTime(new TimeOnly(22, 0)), TimeSpan.Zero);
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var sp = BuildInstance(clock, new RecordingNotifications());
        var (riyadh, riyadhEmployee) = await SeedAsync("Asia/Riyadh");
        var (utc, utcEmployee) = await SeedAsync();
        await RequestAndApproveAsync(riyadh, riyadhEmployee, effective, "bankIban", NewIban);
        await RequestAndApproveAsync(utc, utcEmployee, effective, "bankIban", NewIban);

        clock.Set(instant);
        await sp.GetRequiredService<EffectiveChangeScheduler>().EnqueueDueAsync(default);
        (await JobCountAsync(riyadh)).Should().Be(1, "it is already the effective date in Riyadh");
        (await JobCountAsync(utc)).Should().Be(0, "it is still the day before in UTC");
        await DrainAsync(sp, riyadh);

        await using var verify = fx.CreateDb();
        (await ProfileAsync(verify, riyadh, riyadhEmployee)).Iban.Should().Be(NewIban);
        (await ProfileAsync(verify, utc, utcEmployee)).Iban.Should().Be(OldIban);
    }

    // ─────────────────────── harness ───────────────────────

    private ServiceProvider BuildInstance(TimeProvider clock, INotificationService notifications)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => fx.CreateDb());
        services.AddSingleton(new BackgroundJobOptions
        {
            LeaseDuration = TimeSpan.FromMinutes(2),
            HeartbeatInterval = TimeSpan.FromSeconds(30),
        });
        services.AddSingleton(new EffectiveChangeOptions());
        services.AddSingleton(clock);
        services.AddSingleton(Keys);
        services.AddSingleton(notifications);
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<IApprovalWorkflowService>(p => new ApprovalWorkflowService(
            p.GetRequiredService<ZayraDbContext>(), p.GetRequiredService<IAuditService>(),
            new HrmHierarchyService(p.GetRequiredService<ZayraDbContext>(), p.GetRequiredService<IAuditService>()),
            dataProtection: Keys));
        services.AddSingleton(EffectiveChangeJobHandler.Descriptor);
        services.AddScoped<EffectiveChangeJobHandler>();
        services.AddSingleton<BackgroundJobTypeRegistry>();
        services.AddScoped<BackgroundJobStore>();
        services.AddSingleton<BackgroundJobRunner>();
        services.AddSingleton<EffectiveChangeScheduler>();
        return services.BuildServiceProvider();
    }

    private async Task<(Guid Tenant, int EmployeeId)> SeedAsync(string? timeZone = null)
    {
        await using var db = fx.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(db);
        if (timeZone is not null)
            db.TenantLocalizationSettings.Add(new TenantLocalizationSetting { TenantId = tenant, DefaultTimezone = timeZone });
        var employee = new Employee
        {
            TenantId = tenant, EmployeeCode = "EFF-1", FullName = "Future Dated", Status = "Active",
            JoiningDate = DateTime.UtcNow.Date.AddYears(-1), BankName = "Old Bank", BankIban = OldIban,
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
        {
            TenantId = tenant, EmployeeId = employee.Id, BankName = "Old Bank", Iban = OldIban, SalaryCurrency = "SAR",
            MolId = "KEEP-MOL", AccountNumber = "KEEP-ACCOUNT",
        });
        await db.SaveChangesAsync();
        return (tenant, employee.Id);
    }

    /// <summary>Submits through PUT /employees/{id} (sensitive ⇒ change request + approval) and approves it
    /// in the Approval Center as a different user — the production path end to end.</summary>
    private async Task<Guid> RequestAndApproveAsync(Guid tenant, int employeeId, DateOnly effective, string field, string value,
        Guid? approver = null)
    {
        Guid changeId, approvalId;
        await using (var db = fx.CreateDb())
        {
            var controller = HrmHierarchyTests.BuildImportControllerInternal(db, tenant);
            controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("tenant_id", tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Role, "Admin"), new Claim("permission", "employees.write"),
                new Claim("permission", "employees.sensitive"),
            }, "Test"));
            var result = await controller.UpdateEmployee(employeeId,
                new EmployeeUpdateRequest(effective, new() { [field] = JsonSerializer.SerializeToElement(value) }), default);
            result.Should().BeOfType<AcceptedResult>();
            var change = await db.EmployeeChangeRequests.SingleAsync(x => x.TenantId == tenant && x.EmployeeId == employeeId
                && x.Status == EmployeeChangeStatuses.PendingApproval);
            changeId = change.Id;
            approvalId = change.ApprovalRequestId!.Value;
        }
        await using (var db = fx.CreateDb())
        {
            var service = new ApprovalWorkflowService(db, new AuditService(db), new HrmHierarchyService(db, new AuditService(db)), dataProtection: Keys);
            await service.DecideAsync(tenant, approvalId, new ApprovalDecisionRequest("Approve", "Checked"),
                new RequestContext("127.0.0.1", "test", approver ?? Guid.NewGuid(), tenant, ["HR Manager"], []), default);
        }
        return changeId;
    }

    private async Task EnqueueAsync(ServiceProvider sp, Guid tenant, string key, DateTime asOfUtc)
    {
        await using var scope = sp.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<BackgroundJobStore>().EnqueueAsync(
            tenant, EffectiveChangeJobHandler.JobType, key, new EffectiveChangePayload(asOfUtc), null, default);
    }

    /// <summary>Runs the real runner until every effective-change job of this tenant is terminal, and
    /// requires each to have succeeded.</summary>
    private async Task DrainAsync(ServiceProvider sp, Guid tenant)
    {
        var runner = sp.GetRequiredService<BackgroundJobRunner>();
        for (var i = 0; i < 60; i++)
        {
            await using (var db = fx.CreateDb())
            {
                var jobs = await db.BackgroundJobs.IgnoreQueryFilters()
                    .Where(j => j.TenantId == tenant && j.JobType == EffectiveChangeJobHandler.JobType)
                    .Select(j => new { j.Status, j.LastError }).ToListAsync();
                if (jobs.All(j => BackgroundJobStatuses.IsTerminal(j.Status)))
                {
                    jobs.Should().OnlyContain(j => j.Status == BackgroundJobStatuses.Succeeded,
                        because: string.Join(" | ", jobs.Select(j => j.LastError)));
                    return;
                }
            }
            // The job type is shared across tests in the collection; keep draining until OURS finish.
            if (!await runner.RunNextAsync("test-worker", default, default, [EffectiveChangeJobHandler.JobType]))
                await Task.Delay(50);
        }
        throw new TimeoutException($"Effective-change jobs for tenant {tenant} did not finish.");
    }

    private async Task<int> JobCountAsync(Guid tenant)
    {
        await using var db = fx.CreateDb();
        return await db.BackgroundJobs.IgnoreQueryFilters()
            .CountAsync(j => j.TenantId == tenant && j.JobType == EffectiveChangeJobHandler.JobType);
    }

    private static Task<EmployeePayrollProfile> ProfileAsync(ZayraDbContext db, Guid tenant, int employeeId) =>
        db.EmployeePayrollProfiles.SingleAsync(x => x.TenantId == tenant && x.EmployeeId == employeeId);

    private async Task<int> AuditCountAsync(Guid tenant, Guid changeId, string action)
    {
        await using var db = fx.CreateDb();
        return await db.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.TenantId == tenant
            && a.EntityName == nameof(EmployeeChangeRequest) && a.EntityId == changeId.ToString() && a.Action == action);
    }

    private async Task<Zayra.Api.Domain.Entities.AuditLog> SingleAuditAsync(Guid tenant, Guid changeId, string action)
    {
        await using var db = fx.CreateDb();
        return await db.AuditLogs.IgnoreQueryFilters().SingleAsync(a => a.TenantId == tenant
            && a.EntityName == nameof(EmployeeChangeRequest) && a.EntityId == changeId.ToString() && a.Action == action);
    }

    private async Task<int> HistoryCountAsync(Guid tenant, int employeeId, string eventType)
    {
        await using var db = fx.CreateDb();
        return await db.EmployeeHistories.CountAsync(h => h.TenantId == tenant && h.EmployeeId == employeeId && h.EventType == eventType);
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
        public void Set(DateTimeOffset to) => _now = to;
    }

    private sealed class RecordingNotifications : INotificationService
    {
        public List<(Guid? UserId, string Title, string Message)> Sent { get; } = [];

        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId,
            CancellationToken cancellationToken)
        {
            lock (Sent) Sent.Add((userId, title, message));
            return Task.CompletedTask;
        }

        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName,
            Dictionary<string, string> variables, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
