using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.SelfieAttendance;

/// <summary>
/// The narrow verification of #213 (on d426a885), items 1 and 5, on real PostgreSQL: correction status transitions
/// racing each other (one wins, the other is told plainly that someone else decided first — never a silent overwrite),
/// and an HR approval whose commit acknowledgement was lost (the retry succeeds instead of answering 400). Compiles
/// against d426a885 (the conflict is recognised by its message) and fails there.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class AttendanceCorrectionRacePostgresTests
{
    private const string ConflictMessage = "This request was just decided by someone else. Refresh to see its status.";
    private readonly PostgresFixture _fx;
    public AttendanceCorrectionRacePostgresTests(PostgresFixture fx) => _fx = fx;

    // ── Item 1 ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Item1_HrApprovalRacingAReject_OneWins_TheOtherGetsThePlainConflict_AndNothingIsOverwritten()
    {
        var (tenantId, regId) = await SeedAsync("PendingHRApproval");

        // The reject has read the request and is about to save when the approval commits (it waits up to 3 s for it).
        var approval = Task.Run(() => Attempt(tenantId, null, db => Service(db).ApproveRegularizationAsync(tenantId, regId, new RegularizationDecisionRequest("ok"), Ctx(tenantId), default)));
        var reject = Task.Run(() => Attempt(tenantId, approval, db => Service(db).RejectRegularizationAsync(tenantId, regId, new RegularizationDecisionRequest("no"), Ctx(tenantId), default)));
        var outcomes = await Task.WhenAll(approval, reject);

        Assert.Equal(1, outcomes.Count(o => o == "ok"));
        Assert.Equal(1, outcomes.Count(o => o == ConflictMessage));
        await using var verify = _fx.CreateDb();
        var reg = await verify.AttendanceRegularizationRequests.IgnoreQueryFilters().SingleAsync(r => r.Id == regId);
        var corrections = await verify.AttendanceRawEvents.IgnoreQueryFilters().CountAsync(r => r.TenantId == tenantId && r.Source == "Manual HR correction");
        // The stored status is the winner's, and the punches agree with it.
        Assert.Equal(outcomes[0] == "ok" ? "Approved" : "Rejected", reg.Status);
        Assert.Equal(reg.Status == "Approved" ? 2 : 0, corrections);
    }

    [Fact]
    public async Task Item1_AManagerApprovalRacingACancel_OneWins_TheOtherGetsThePlainConflict()
    {
        var (tenantId, regId) = await SeedAsync("Submitted");

        var approve = Task.Run(() => Attempt(tenantId, null, db => Service(db).ApproveRegularizationAsync(tenantId, regId, new RegularizationDecisionRequest("ok"), Ctx(tenantId), default)));
        var cancel = Task.Run(() => Attempt(tenantId, approve, db => Service(db).CancelRegularizationAsync(tenantId, regId, "changed my mind", Ctx(tenantId), default)));
        var outcomes = await Task.WhenAll(approve, cancel);

        Assert.Equal(1, outcomes.Count(o => o == "ok"));
        Assert.Equal(1, outcomes.Count(o => o == ConflictMessage));
        await using var verify = _fx.CreateDb();
        var reg = await verify.AttendanceRegularizationRequests.IgnoreQueryFilters().SingleAsync(r => r.Id == regId);
        Assert.Equal(outcomes[0] == "ok" ? "PendingHRApproval" : "Cancelled", reg.Status);
    }

    // ── Item 5 ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Item5_AnHrApprovalWhoseCommitAcknowledgementIsLost_SucceedsOnTheRetry_WithoutApplyingTwice()
    {
        var (tenantId, regId) = await SeedAsync("PendingHRApproval");
        var lostAck = new LoseTheFirstCommitAcknowledgement();
        var options = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(_fx.ConnectionString, o => o.EnableRetryOnFailure(2, TimeSpan.FromMilliseconds(20), null))
            .AddInterceptors(Zayra.Api.Infrastructure.Data.AdvisoryXactLockGuardInterceptor.Instance, lostAck)
            .Options;
        var hr = Ctx(tenantId);

        AttendanceRegularizationRequest? approved;
        await using (var db = new ZayraDbContext(options))
            approved = await Service(db).ApproveRegularizationAsync(tenantId, regId, new RegularizationDecisionRequest("ok"), hr, default);

        Assert.True(lostAck.Fired, "the commit acknowledgement was never lost: the test did not exercise a retry");
        Assert.Equal("Approved", approved!.Status);
        await using var verify = _fx.CreateDb();
        Assert.Equal("Approved", (await verify.AttendanceRegularizationRequests.IgnoreQueryFilters().SingleAsync(r => r.Id == regId)).Status);
        Assert.Equal(2, await verify.AttendanceRawEvents.IgnoreQueryFilters().CountAsync(r => r.TenantId == tenantId && r.Source == "Manual HR correction"));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs one decision on its own context and returns "ok" or the refusal message. With <paramref name="waitFor"/>,
    /// the decision's save of the request waits (up to 3 s) until that other decision has finished — the interleaving in
    /// which an unlocked writer overwrites the winner.
    /// </summary>
    private async Task<string> Attempt(Guid tenantId, Task? waitFor, Func<ZayraDbContext, Task<AttendanceRegularizationRequest?>> decide)
    {
        var options = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(_fx.ConnectionString, PostgresFixture.ProductionProviderOptions)
            .AddInterceptors(Zayra.Api.Infrastructure.Data.AdvisoryXactLockGuardInterceptor.Instance, new HoldTheRequestSaveUntil(waitFor))
            .Options;
        await using var db = new ZayraDbContext(options);
        try { await decide(db); return "ok"; }
        catch (InvalidOperationException ex) { return ex.Message; }
    }

    private async Task<(Guid Tenant, Guid RegId)> SeedAsync(string status)
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        db.AttendancePolicies.Add(new AttendancePolicy { TenantId = tenantId, Code = "STD", Name = "Standard", IsActive = true });
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"CRACE-{Guid.NewGuid():N}"[..20], FullName = "Correction Race",
            Status = EmployeeStatuses.Active, JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var tz = TenantTimeZone.FromId(null);
        var date = TenantTimeZone.LocalDate(tz, DateTime.UtcNow).AddDays(-3);
        var dayStart = TenantTimeZone.LocalDayStartUtc(tz, date);
        var reg = new AttendanceRegularizationRequest
        {
            TenantId = tenantId, EmployeeId = employee.Id, WorkDate = date, RequestedInUtc = dayStart.AddHours(9), RequestedOutUtc = dayStart.AddHours(17),
            Reason = "race", Status = status, RequestedByUserId = Guid.NewGuid(),
        };
        db.AttendanceRegularizationRequests.Add(reg);
        db.AttendanceCorrectionApprovals.AddRange(
            new AttendanceCorrectionApproval { TenantId = tenantId, RegularizationRequestId = reg.Id, ApprovalLevel = "Manager" },
            new AttendanceCorrectionApproval { TenantId = tenantId, RegularizationRequestId = reg.Id, ApprovalLevel = "HR" });
        await db.SaveChangesAsync();
        return (tenantId, reg.Id);
    }

    private static AttendanceService Service(ZayraDbContext db) => new(db, new NullNotifications(), new NullHttpClients());

    private static RequestContext Ctx(Guid tenantId) => new("127.0.0.1", "test", Guid.NewGuid(), tenantId);

    private sealed class HoldTheRequestSaveUntil(Task? other) : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (other is not null && eventData.Context!.ChangeTracker.Entries<AttendanceRegularizationRequest>().Any(e => e.State == EntityState.Modified))
                await Task.WhenAny(other, Task.Delay(TimeSpan.FromSeconds(3), ct));
            return result;
        }
    }

    /// <summary>The first transaction commits, but the caller is told it failed (a transient error after the commit).</summary>
    private sealed class LoseTheFirstCommitAcknowledgement : DbTransactionInterceptor
    {
        public bool Fired { get; private set; }

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default)
        {
            if (!Fired)
            {
                Fired = true;
                throw new Npgsql.NpgsqlException("simulated lost commit acknowledgement", new TimeoutException());
            }
            return Task.CompletedTask;
        }
    }
}
