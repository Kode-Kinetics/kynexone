using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Production-provider regression for the browser failure where Approval Center returned:
/// "NpgsqlRetryingExecutionStrategy does not support user-initiated transactions".
/// The test intentionally enables the same retry strategy as Program.cs and crosses the real
/// Leave aggregate -> role-routed ApprovalRequest -> global Approval Center boundary.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class LeaveApprovalPostgresIntegrationTests
{
    private readonly PostgresFixture _fixture;

    public LeaveApprovalPostgresIntegrationTests(PostgresFixture fixture) => _fixture = fixture;

    private ZayraDbContext CreateRetryingDb() => new(
        new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(_fixture.ConnectionString, options =>
                options.EnableRetryOnFailure(maxRetryCount: 3))
            .Options);

    [Fact]
    public async Task RoleRoutedLeave_SubmitThenHrManagerDecides_IsAtomicAndExactlyOnce()
    {
        Guid tenantId;
        Guid leaveTypeId;
        Guid requestId;
        Guid employeeUserId = Guid.NewGuid();
        Guid hrManagerUserId = Guid.NewGuid();
        int employeeId;
        var start = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(14));

        await using (var db = CreateRetryingDb())
        {
            tenantId = await PostgresFixture.SeedMinimalTenant(db);
            var leaveType = new LeaveType
            {
                TenantId = tenantId,
                Code = $"PG-{Guid.NewGuid():N}"[..12],
                NameEn = "Postgres Annual Leave",
                IsActive = true,
                IsPaid = true
            };
            var hrManager = new Employee
            {
                TenantId = tenantId,
                UserAccountId = hrManagerUserId,
                EmployeeCode = $"HR-{Guid.NewGuid():N}"[..12],
                FullName = "Postgres HR Manager",
                Department = "HR",
                Designation = "HR Manager",
                Status = "Active",
                JoiningDate = DateTime.UtcNow.AddYears(-4)
            };
            var employee = new Employee
            {
                TenantId = tenantId,
                UserAccountId = employeeUserId,
                EmployeeCode = $"EE-{Guid.NewGuid():N}"[..12],
                FullName = "Postgres Leave Requester",
                // No direct manager and no ApprovalPolicy intentionally exercises the visible
                // HR Manager role fallback used by the client-pilot seed.
                Status = "Active",
                JoiningDate = DateTime.UtcNow.AddYears(-2)
            };
            db.AddRange(leaveType, hrManager, employee);
            await db.SaveChangesAsync();

            employeeId = employee.Id;
            leaveTypeId = leaveType.Id;
            db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
            {
                TenantId = tenantId,
                EmployeeId = employee.Id,
                EmployeeName = employee.FullName,
                LeaveTypeId = leaveType.Id,
                LeaveTypeName = leaveType.NameEn,
                Year = start.Year,
                Entitled = 20m
            });
            await db.SaveChangesAsync();

            var submitted = await new LeaveService(db, new ApprovalPolicyService(db))
                .SubmitRequestAsync(tenantId, new LeaveRequest
                {
                    EmployeeId = employee.Id,
                    LeaveTypeId = leaveType.Id,
                    StartDate = start,
                    EndDate = start,
                    DayType = "Full",
                    Reason = "Real PostgreSQL pilot workflow"
                }, employeeUserId);
            requestId = submitted.Id;
        }

        // A failure after the transaction begins must roll back cleanly and leave the route
        // actionable. This also proves maker-checker remains inside the retry-safe transaction.
        await using (var rollbackDb = CreateRetryingDb())
        {
            var selfApproval = () => new LeaveService(rollbackDb, new ApprovalPolicyService(rollbackDb))
                .ApproveRequestAsync(tenantId, requestId, employeeUserId, "Postgres Leave Requester", "self");
            await selfApproval.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*Maker-checker*");
        }

        await using (var afterRollback = CreateRetryingDb())
        {
            (await afterRollback.LeaveApprovals.SingleAsync(a => a.LeaveRequestId == requestId))
                .Decision.Should().Be("Pending");
            (await afterRollback.ApprovalRequests.SingleAsync(a => a.Id == requestId))
                .Status.Should().Be("Pending");
            (await afterRollback.ApprovalDecisions.CountAsync(a => a.ApprovalRequestId == requestId))
                .Should().Be(0);
        }

        await using (var decideDb = CreateRetryingDb())
        {
            var projection = await decideDb.ApprovalRequests.SingleAsync(a => a.Id == requestId);
            projection.EntityName.Should().Be(nameof(LeaveRequest));
            projection.EntityId.Should().Be(requestId.ToString());
            projection.CurrentApproverType.Should().Be("Role");
            projection.CurrentApproverRole.Should().Be("HR Manager");
            projection.Status.Should().Be("Pending");

            var approvalCenter = new ApprovalWorkflowService(decideDb, new AuditService(decideDb));
            var result = await approvalCenter.DecideAsync(
                tenantId,
                requestId,
                new ApprovalDecisionRequest("Approve", "Approved from the PostgreSQL global queue"),
                new RequestContext(
                    "127.0.0.1", "postgres-integration", hrManagerUserId, tenantId,
                    ["HR Manager"], ["approvals.decide"]),
                CancellationToken.None);

            result.Should().NotBeNull();
            result!.Status.Should().Be("Approved");
            result.Decisions.Should().ContainSingle(d =>
                d.StepOrder == 1 && d.Decision == "Approved" && d.DecidedByUserId == hrManagerUserId);
        }

        await using (var verifyDb = CreateRetryingDb())
        {
            var leave = await verifyDb.LeaveRequests.SingleAsync(r => r.Id == requestId);
            leave.Status.Should().Be("Approved");
            leave.DecidedAtUtc.Should().NotBeNull();

            var route = await verifyDb.ApprovalRequests.SingleAsync(a => a.Id == requestId);
            route.Status.Should().Be("Approved");
            route.CompletedAtUtc.Should().NotBeNull();
            route.CurrentApproverUserId.Should().BeNull();

            (await verifyDb.LeaveApprovals.CountAsync(a =>
                a.LeaveRequestId == requestId && a.Decision == "Approved" && a.ApproverId == hrManagerUserId))
                .Should().Be(1);
            (await verifyDb.ApprovalDecisions.CountAsync(a =>
                a.ApprovalRequestId == requestId && a.Decision == "Approved" && a.DecidedByUserId == hrManagerUserId))
                .Should().Be(1);

            var balance = await verifyDb.EmployeeLeaveBalances.SingleAsync(b =>
                b.TenantId == tenantId && b.EmployeeId == employeeId && b.LeaveTypeId == leaveTypeId);
            balance.Pending.Should().Be(0m);
            balance.Used.Should().Be(1m);
            balance.Available.Should().Be(19m);

            (await verifyDb.LeaveBalanceTransactions.CountAsync(t =>
                t.TenantId == tenantId && t.Reference == requestId.ToString() && t.TransactionType == "Pending"))
                .Should().Be(1);
            (await verifyDb.LeaveBalanceTransactions.CountAsync(t =>
                t.TenantId == tenantId && t.Reference == requestId.ToString() && t.TransactionType == "Used"))
                .Should().Be(1);
            (await verifyDb.EmployeeNotifications.CountAsync(n =>
                n.TenantId == tenantId && n.EmployeeId == employeeId && n.Title == "Leave approved"))
                .Should().Be(1);
        }

        // A replay through the same public Approval Center contract must fail without consuming
        // balance, adding a second projection decision, or notifying twice.
        await using (var replayDb = CreateRetryingDb())
        {
            var approvalCenter = new ApprovalWorkflowService(replayDb, new AuditService(replayDb));
            var replay = () => approvalCenter.DecideAsync(
                tenantId,
                requestId,
                new ApprovalDecisionRequest("Approve", "replay"),
                new RequestContext(
                    "127.0.0.1", "postgres-integration", hrManagerUserId, tenantId,
                    ["HR Manager"], ["approvals.decide"]),
                CancellationToken.None);
            await replay.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*already completed*");
        }

        await using (var finalDb = CreateRetryingDb())
        {
            (await finalDb.LeaveBalanceTransactions.CountAsync(t =>
                t.TenantId == tenantId && t.Reference == requestId.ToString() && t.TransactionType == "Used"))
                .Should().Be(1);
            (await finalDb.ApprovalDecisions.CountAsync(d => d.ApprovalRequestId == requestId))
                .Should().Be(1);
            (await finalDb.EmployeeNotifications.CountAsync(n =>
                n.TenantId == tenantId && n.EmployeeId == employeeId && n.Title == "Leave approved"))
                .Should().Be(1);
        }
    }
}
