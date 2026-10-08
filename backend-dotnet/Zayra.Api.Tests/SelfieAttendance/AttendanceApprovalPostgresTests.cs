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
/// PR #213 review, items 1 and 4, on real PostgreSQL: HR's approval of a correction is ONE transaction — in a tenant with
/// no active policy too, where creating the first DEFAULT policy used to commit the approval on its own before the day
/// was recomputed. A transient failure leaves nothing written and answers in plain words. Compiles against the reviewed
/// head (e83c1673; the message is written as a string) and fails there.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class AttendanceApprovalPostgresTests
{
    private const string PlainTransientMessage =
        "The correction could not be approved because the database was briefly unavailable. Nothing was changed. Try again.";

    private readonly PostgresFixture _fx;
    public AttendanceApprovalPostgresTests(PostgresFixture fx) => _fx = fx;

    [Theory]
    [InlineData(true)]   // the common case: the corrected punches were invisible to the recompute
    [InlineData(false)]  // no policy yet: the first DEFAULT policy is created inside the same transaction
    public async Task Item1_TheApprovedDay_ReflectsTheCorrection(bool tenantHasPolicy)
    {
        var (tenantId, regId, requestedIn, requestedOut, workDate, employeeId) = await SeedAsync(tenantHasPolicy);

        await using (var db = _fx.CreateDb())
            await Service(db).ApproveRegularizationAsync(tenantId, regId, new RegularizationDecisionRequest("ok"), Ctx(tenantId), default);

        await using var verify = _fx.CreateDb();
        var daily = await verify.AttendanceDailyRecords.IgnoreQueryFilters().SingleAsync(d => d.TenantId == tenantId && d.EmployeeId == employeeId && d.WorkDate == workDate);
        Assert.Equal(requestedIn, daily.FirstInUtc);
        Assert.Equal(requestedOut, daily.LastOutUtc);
        Assert.NotEqual("Absent", daily.Status);
        Assert.Equal("Approved", daily.ManualCorrectionStatus);
        Assert.Equal(1, await verify.AttendancePolicies.IgnoreQueryFilters().CountAsync(p => p.TenantId == tenantId));
    }

    [Fact]
    public async Task Item4_ATransientFailureWhileRecomputingTheDay_InATenantWithNoPolicy_WritesNothing_AndAnswersInPlainWords()
    {
        var (tenantId, regId, _, _, workDate, employeeId) = await SeedAsync(withPolicy: false);
        var options = new DbContextOptionsBuilder<ZayraDbContext>()
            // Production's retrying strategy, with fast retries so the test is quick.
            .UseNpgsql(_fx.ConnectionString, o => o.EnableRetryOnFailure(2, TimeSpan.FromMilliseconds(20), null))
            .AddInterceptors(Zayra.Api.Infrastructure.Data.AdvisoryXactLockGuardInterceptor.Instance, new TransientOnDailyRecordSave())
            .Options;

        await using (var db = new ZayraDbContext(options))
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Service(db).ApproveRegularizationAsync(tenantId, regId, new RegularizationDecisionRequest("ok"), Ctx(tenantId), default));
            Assert.Equal(PlainTransientMessage, ex.Message);
        }

        await using var verify = _fx.CreateDb();
        Assert.Equal("PendingHRApproval", (await verify.AttendanceRegularizationRequests.IgnoreQueryFilters().SingleAsync(r => r.Id == regId)).Status);
        Assert.Equal(0, await verify.AttendanceRawEvents.IgnoreQueryFilters().CountAsync(r => r.TenantId == tenantId && r.Source == "Manual HR correction"));
        Assert.Equal(0, await verify.AttendancePolicies.IgnoreQueryFilters().CountAsync(p => p.TenantId == tenantId)); // the DEFAULT policy rolled back too
        Assert.False(await verify.AttendanceDailyRecords.IgnoreQueryFilters().AnyAsync(d => d.TenantId == tenantId && d.EmployeeId == employeeId && d.WorkDate == workDate));
        Assert.Equal("Pending", (await verify.AttendanceCorrectionApprovals.IgnoreQueryFilters().SingleAsync(a => a.RegularizationRequestId == regId)).Decision);
    }

    private async Task<(Guid Tenant, Guid RegId, DateTime In, DateTime Out, DateOnly WorkDate, int EmployeeId)> SeedAsync(bool withPolicy)
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        if (withPolicy) db.AttendancePolicies.Add(new AttendancePolicy { TenantId = tenantId, Code = "STD", Name = "Standard", IsActive = true });
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"APPR-{Guid.NewGuid():N}"[..20], FullName = "Approval Person",
            Status = EmployeeStatuses.Active, JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var tz = TenantTimeZone.FromId(null);
        var workDate = TenantTimeZone.LocalDate(tz, DateTime.UtcNow).AddDays(-5);
        var dayStart = TenantTimeZone.LocalDayStartUtc(tz, workDate);
        var reg = new AttendanceRegularizationRequest
        {
            TenantId = tenantId, EmployeeId = employee.Id, WorkDate = workDate, RequestType = "Missed punch",
            RequestedInUtc = dayStart.AddHours(9), RequestedOutUtc = dayStart.AddHours(17), Reason = "Forgot", Status = "PendingHRApproval",
            RequestedByUserId = Guid.NewGuid(),
        };
        db.AttendanceRegularizationRequests.Add(reg);
        db.AttendanceCorrectionApprovals.Add(new AttendanceCorrectionApproval { TenantId = tenantId, RegularizationRequestId = reg.Id, ApprovalLevel = "HR" });
        await db.SaveChangesAsync();
        // Postgres keeps microseconds: compare against what was stored.
        var stored = await db.AttendanceRegularizationRequests.AsNoTracking().SingleAsync(r => r.Id == reg.Id);
        return (tenantId, reg.Id, stored.RequestedInUtc!.Value, stored.RequestedOutUtc!.Value, workDate, employee.Id);
    }

    private static AttendanceService Service(ZayraDbContext db) => new(db, new NullNotifications(), new NullHttpClients());

    private static RequestContext Ctx(Guid tenantId) => new("127.0.0.1", "test", Guid.NewGuid(), tenantId);

    /// <summary>Every save that writes a daily record fails with a transient provider error (a dropped connection).</summary>
    private sealed class TransientOnDailyRecordSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<AttendanceDailyRecord>().Any(e => e.State is EntityState.Added or EntityState.Modified))
                throw new Npgsql.NpgsqlException("simulated dropped connection", new TimeoutException());
            return ValueTask.FromResult(result);
        }
    }
}
