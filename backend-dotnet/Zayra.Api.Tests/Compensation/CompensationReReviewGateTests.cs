using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Employees;
using Zayra.Api.Infrastructure.Compensation;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Compensation.MeritTestKit;

namespace Zayra.Api.Tests.Compensation;

/// <summary>
/// An award the effective-date job sent back for review (drift) is later approved in the Approval Center. That decision
/// applies it immediately when the effective date has passed — through ApprovalWorkflowService, not the job — so the
/// origin's gate must run there too. On real PostgreSQL.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class CompensationReReviewGateTests(PostgresFixture fx)
{
    private static readonly DateOnly FirstOfThisMonth = new(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    [Fact]
    public async Task AReReviewedAwardApprovedAfterTheEffectiveDate_ForAnEmployeeNowOnNotice_IsCancelledNotApplied()
    {
        var w = await SeedAsync(fx);
        var applied = await AppliedAsync(fx, w, FirstOfThisMonth);
        Guid changeId;
        decimal basicBefore;
        await using (var db = fx.CreateDb())
        {
            // The ordinary employee-change route the re-review is raised on.
            var wf = new ApprovalWorkflow { TenantId = w.Tenant, Code = "EMP-CHANGE", Name = "Employee change", EntityName = nameof(EmployeeChangeRequest), IsDefault = true, IsActive = true };
            wf.Steps.Add(new ApprovalWorkflowStep { TenantId = w.Tenant, WorkflowId = wf.Id, StepOrder = 1, StepName = "HR", ApproverType = "Role", ApproverRole = "HR Manager", IsFinalStep = true });
            db.ApprovalWorkflows.Add(wf);
            changeId = (await db.CompWorksheetLines.AsNoTracking().SingleAsync(l => l.CycleId == applied.Id && l.EmployeeId == w.E["A1"])).ChangeRequestId!.Value;
            // Drift: the salary row the award was approved against moves before the job applies it.
            var row = await db.EmployeeSalaryStructures.SingleAsync(s => s.EmployeeId == w.E["A1"] && s.IsActive);
            row.HousingAllowance += 100m;
            basicBefore = row.BasicSalary;
            await db.SaveChangesAsync();
        }
        (await RunJobAsync(fx, w.Tenant, changeId, Today)).Outcome.Should().Be(EffectiveChangeOutcome.ReturnedForReview);
        Guid reReview;
        await using (var db = fx.CreateDb())
        {
            var change = await db.EmployeeChangeRequests.AsNoTracking().SingleAsync(c => c.Id == changeId);
            change.Status.Should().Be(EmployeeChangeStatuses.PendingApproval);
            reReview = change.ApprovalRequestId!.Value;
            // Then the employee hands in notice, while the re-review waits.
            db.EmployeeOffboardings.Add(new EmployeeOffboarding { TenantId = w.Tenant, EmployeeId = w.E["A1"], NoticeDate = Today, NoticePeriodDays = 30, LastWorkingDay = Today.AddDays(30), Status = "InProgress" });
            await db.SaveChangesAsync();
        }

        // A different approver approves the re-review after the effective date has passed.
        await DecideAsync(fx, w, reReview, w.UserHr2, ["HR Manager"], "Approve", "Reviewed the new housing figure");

        await using var verify = fx.CreateDb();
        var after = await verify.EmployeeChangeRequests.AsNoTracking().SingleAsync(c => c.Id == changeId);
        after.Status.Should().Be(EmployeeChangeStatuses.Cancelled, "an employee on notice is not paid the merit award");
        after.RejectionReason.Should().Contain("On notice");
        (await verify.EmployeeSalaryStructures.AsNoTracking().Where(s => s.EmployeeId == w.E["A1"] && s.IsActive).OrderByDescending(s => s.EffectiveDate).FirstAsync())
            .BasicSalary.Should().Be(basicBefore, "nothing was written");
        (await verify.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.EntityId == changeId.ToString() && a.Action == EffectiveChangeJobHandler.CancelledAction)).Should().Be(1);
        (await verify.EmployeeHistories.IgnoreQueryFilters().CountAsync(h => h.EmployeeId == w.E["A1"] && h.EventType == EffectiveChangeJobHandler.CancelledEventType)).Should().Be(1);
        (await verify.Notifications.IgnoreQueryFilters().CountAsync(n => n.TenantId == w.Tenant && n.UserId == w.UserHr && n.EntityId == changeId.ToString())).Should().Be(1, "HR, who scheduled the awards, is told");
        (await verify.CompWorksheetLines.AsNoTracking().SingleAsync(l => l.ChangeRequestId == changeId)).ApplyBlockedReason.Should().Contain("no longer eligible");
    }

    private sealed class ThrowingGate : IEffectiveChangeGate
    {
        public Task<EffectiveChangeRefusal?> CheckAsync(Zayra.Api.Data.ZayraDbContext db, Guid tenantId, EmployeeChangeRequest change, Employee employee, DateOnly localToday, CancellationToken ct) =>
            throw new InvalidOperationException("gate backend unavailable");
    }

    [Fact]
    public async Task AGateThatKeepsThrowing_IsRetriedAtMostFiveTimes_ThenTheChangeIsCancelledAndHrIsTold()
    {
        var w = await SeedAsync(fx);
        var applied = await AppliedAsync(fx, w);
        Guid changeId;
        await using (var db = fx.CreateDb())
            changeId = (await db.CompWorksheetLines.AsNoTracking().SingleAsync(l => l.CycleId == applied.Id && l.EmployeeId == w.E["A1"])).ChangeRequestId!.Value;

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var result = await RunJobAsync(fx, w.Tenant, changeId, Effective, [new ThrowingGate()]);
            result.Outcome.Should().Be(EffectiveChangeOutcome.Deferred, $"attempt {attempt} is retried on the next run");
            result.ReasonCode.Should().Be("gate_failed");
        }
        var last = await RunJobAsync(fx, w.Tenant, changeId, Effective, [new ThrowingGate()]);
        last.Outcome.Should().Be(EffectiveChangeOutcome.Cancelled);
        last.ReasonCode.Should().Be("gate_failed");
        last.NotifyUserId.Should().Be(w.UserHr, "the HR user who raised the change is told");
        (await RunJobAsync(fx, w.Tenant, changeId, Effective, [new ThrowingGate()])).Outcome.Should().Be(EffectiveChangeOutcome.AlreadyHandled, "no sixth attempt");

        await using var verify = fx.CreateDb();
        (await verify.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.EntityId == changeId.ToString() && a.Action == EffectiveChangeJobHandler.GateFailedAction)).Should().Be(5);
        (await verify.EmployeeChangeRequests.AsNoTracking().SingleAsync(c => c.Id == changeId)).Status.Should().Be(EmployeeChangeStatuses.Cancelled);
        (await verify.EmployeeSalaryStructures.AsNoTracking().AnyAsync(s => s.EmployeeId == w.E["A1"] && s.EffectiveDate == Effective)).Should().BeFalse();
    }
}
