using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class LeaveApprovalScopeTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>
    /// F1 — a tenant with NO applicable approval workflow used to be routed to a guessed approver
    /// ("direct manager, else HR Manager") in code. That is removed: the submission is refused with a
    /// typed configuration error and nothing is written.
    /// </summary>
    [Fact]
    public async Task MissingWorkflow_IsRefusedWithTypedError_InsteadOfRoutingToAGuessedManager()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var leaveType = new LeaveType { TenantId = tenantId, Code = "AL", NameEn = "Annual Leave", IsActive = true };
        var manager = new Employee
        {
            TenantId = tenantId, UserAccountId = Guid.NewGuid(), EmployeeCode = "MGR-NOCFG",
            FullName = "Would-be Manager", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-3)
        };
        db.LeaveTypes.Add(leaveType);
        db.Employees.Add(manager);
        await db.SaveChangesAsync();
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = "EMP-NOCFG", FullName = "No Config", ManagerEmployeeId = manager.Id,
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1)
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var start = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7));
        db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
        {
            TenantId = tenantId, EmployeeId = employee.Id, EmployeeName = employee.FullName,
            LeaveTypeId = leaveType.Id, LeaveTypeName = leaveType.NameEn, Year = start.Year, Entitled = 21
        });
        await db.SaveChangesAsync();

        var submit = () => new LeaveService(db, new ApprovalRouter(db)).SubmitRequestAsync(tenantId, new LeaveRequest
        {
            TenantId = tenantId, EmployeeId = employee.Id, LeaveTypeId = leaveType.Id,
            StartDate = start, EndDate = start, DayType = "Full"
        });

        (await submit.Should().ThrowAsync<ApprovalRouteNotConfiguredException>()).Which.Code.Should().Be("approval_route_not_configured");
        (await db.LeaveRequests.AnyAsync()).Should().BeFalse();
        (await db.LeaveApprovals.AnyAsync()).Should().BeFalse("no approver may be guessed");
        (await db.ApprovalRequests.AnyAsync()).Should().BeFalse();
        (await db.EmployeeLeaveBalances.SingleAsync()).Pending.Should().Be(0);
    }

    [Fact]
    public async Task ConfiguredManagerStep_RoutesToActionableManagerQueue_AndHardeningHolds()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var managerUserId = Guid.NewGuid();
        var employeeUserId = Guid.NewGuid();
        var leaveType = new LeaveType { TenantId = tenantId, Code = "AL", NameEn = "Annual Leave", IsActive = true };
        var manager = new Employee
        {
            TenantId = tenantId, UserAccountId = managerUserId, EmployeeCode = "MGR-FALLBACK",
            FullName = "Fallback Manager", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-3)
        };
        db.LeaveTypes.Add(leaveType);
        db.Employees.Add(manager);
        await db.SaveChangesAsync();
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = "EMP-FALLBACK", FullName = "Needs Approval",
            UserAccountId = employeeUserId, ManagerEmployeeId = manager.Id,
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1)
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var start = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7));
        db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
        {
            TenantId = tenantId, EmployeeId = employee.Id, EmployeeName = employee.FullName,
            LeaveTypeId = leaveType.Id, LeaveTypeName = leaveType.NameEn, Year = start.Year, Entitled = 21
        });
        await db.SaveChangesAsync();

        var service = await TestApprovalConfig.LeaveServiceAsync(db, tenantId, approverType: "Manager");
        var submitted = await service.SubmitRequestAsync(tenantId, new LeaveRequest
        {
            TenantId = tenantId, EmployeeId = employee.Id, LeaveTypeId = leaveType.Id,
            StartDate = start, EndDate = start, DayType = "Full"
        });

        submitted.Status.Should().Be("PendingManagerApproval");
        var approval = await db.LeaveApprovals.SingleAsync(a => a.LeaveRequestId == submitted.Id);
        approval.Decision.Should().Be("Pending");
        approval.ApproverRole.Should().Be("Manager");
        approval.ApproverId.Should().Be(managerUserId);
        approval.ApproverName.Should().Be(manager.FullName);

        var projection = await db.ApprovalRequests.Include(a => a.Decisions).SingleAsync(a => a.Id == submitted.Id);
        projection.EntityId.Should().Be(submitted.Id.ToString());
        projection.RequestedByUserId.Should().Be(employeeUserId);
        projection.RequestedForEmployeeId.Should().Be(employee.Id);
        projection.CurrentApproverUserId.Should().Be(managerUserId);
        projection.CurrentApproverRole.Should().Be("Manager");
        projection.Status.Should().Be("Pending");

        var directSelfReject = () => service.RejectRequestAsync(
            tenantId, submitted.Id, employeeUserId, employee.FullName, "self rejection");
        await directSelfReject.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Maker-checker*");

        var approvalCenter = new ApprovalWorkflowService(db, new AuditService(db));
        (await approvalCenter.DecideAsync(Guid.NewGuid(), projection.Id,
            new ApprovalDecisionRequest("Approve", "wrong tenant"),
            new RequestContext("127.0.0.1", "tests", managerUserId, Guid.NewGuid(), ["Manager"], ["approvals.decide"]),
            CancellationToken.None)).Should().BeNull("tenant scope must be applied before resolving the leave link");

        var selfDecision = () => approvalCenter.DecideAsync(tenantId, projection.Id,
            new ApprovalDecisionRequest("Approve", "self approval"),
            new RequestContext("127.0.0.1", "tests", employeeUserId, tenantId, ["Manager"], ["approvals.decide"]),
            CancellationToken.None);
        await selfDecision.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Maker-checker*");

        var managerContext = new RequestContext("127.0.0.1", "tests", managerUserId, tenantId, ["Manager"], ["approvals.decide"]);
        var decided = await approvalCenter.DecideAsync(tenantId, projection.Id,
            new ApprovalDecisionRequest("Approve", "Approved in the global queue"), managerContext, CancellationToken.None);

        decided!.Status.Should().Be("Approved");
        decided.Decisions.Should().ContainSingle(d => d.StepOrder == 1 && d.Decision == "Approved");
        (await db.LeaveRequests.SingleAsync(r => r.Id == submitted.Id)).Status.Should().Be("Approved");
        (await db.LeaveApprovals.SingleAsync(a => a.LeaveRequestId == submitted.Id)).Decision.Should().Be("Approved");
        var decidedBalance = await db.EmployeeLeaveBalances.SingleAsync(b => b.EmployeeId == employee.Id);
        decidedBalance.Pending.Should().Be(0);
        decidedBalance.Used.Should().Be(1);
        (await db.LeaveBalanceTransactions.CountAsync(t => t.Reference == submitted.Id.ToString() && t.TransactionType == "Used"))
            .Should().Be(1);

        var replay = () => approvalCenter.DecideAsync(tenantId, projection.Id,
            new ApprovalDecisionRequest("Approve", "replay"), managerContext, CancellationToken.None);
        await replay.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already completed*");
        (await db.LeaveBalanceTransactions.CountAsync(t => t.Reference == submitted.Id.ToString() && t.TransactionType == "Used"))
            .Should().Be(1, "a replay must not consume the balance twice");
    }

    [Fact]
    public async Task MultiStepApproval_FirstStepAdvancesWithoutConsumingBalance_FinalStepApproves()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var managerUserId = Guid.NewGuid();
        var hrUserId = Guid.NewGuid();
        var leaveType = new LeaveType { TenantId = tenantId, Code = "AL", NameEn = "Annual Leave", IsActive = true };
        var manager = new Employee
        {
            TenantId = tenantId, UserAccountId = managerUserId, EmployeeCode = "MGR-1",
            FullName = "Manager One", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-3)
        };
        var hr = new Employee
        {
            TenantId = tenantId, UserAccountId = hrUserId, EmployeeCode = "HR-1",
            FullName = "HR One", Department = "HR", Designation = "HR Manager",
            ManagerEmployeeId = 999, Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-3)
        };
        db.LeaveTypes.Add(leaveType);
        db.Employees.AddRange(manager, hr);
        await db.SaveChangesAsync();

        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = "EMP-1", FullName = "Employee One",
            ManagerEmployeeId = manager.Id, Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1)
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        // F1: configured as an ApprovalWorkflow — the single model (ApprovalPolicy is retired).
        var managerThenHr = new ApprovalWorkflow
        {
            TenantId = tenantId, Code = "MGR-HR", Name = "Manager then HR", EntityName = nameof(LeaveRequest),
            IsDefault = true, IsActive = true
        };
        managerThenHr.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantId, WorkflowId = managerThenHr.Id, StepOrder = 1, StepName = "Manager", ApproverType = "Manager" });
        managerThenHr.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantId, WorkflowId = managerThenHr.Id, StepOrder = 2, StepName = "HR", ApproverType = "HR", IsFinalStep = true });
        db.ApprovalWorkflows.Add(managerThenHr);
        var start = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(7));
        db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
        {
            TenantId = tenantId, EmployeeId = employee.Id, EmployeeName = employee.FullName,
            LeaveTypeId = leaveType.Id, LeaveTypeName = leaveType.NameEn,
            Year = start.Year, Entitled = 21
        });
        await db.SaveChangesAsync();

        var service = new LeaveService(db, new ApprovalRouter(db));
        var submitted = await service.SubmitRequestAsync(tenantId, new LeaveRequest
        {
            TenantId = tenantId, EmployeeId = employee.Id, LeaveTypeId = leaveType.Id,
            StartDate = start, EndDate = start.AddDays(1), DayType = "Full"
        });

        var afterSubmitBalance = await db.EmployeeLeaveBalances.SingleAsync(b => b.EmployeeId == employee.Id);
        afterSubmitBalance.Pending.Should().Be(2);
        afterSubmitBalance.Used.Should().Be(0);

        await service.ApproveRequestAsync(tenantId, submitted.Id, managerUserId, "Manager One", "ok");

        var afterManager = await db.LeaveRequests.SingleAsync(r => r.Id == submitted.Id);
        afterManager.Status.Should().Be("PendingHRApproval");
        afterManager.DecidedAtUtc.Should().BeNull();
        var managerBalance = await db.EmployeeLeaveBalances.SingleAsync(b => b.EmployeeId == employee.Id);
        managerBalance.Pending.Should().Be(2, "intermediate approval must not consume leave");
        managerBalance.Used.Should().Be(0);

        var approvals = await db.LeaveApprovals
            .Where(a => a.LeaveRequestId == submitted.Id)
            .OrderBy(a => a.StepNumber)
            .ToListAsync();
        approvals.Should().HaveCount(2);
        approvals[0].Decision.Should().Be("Approved");
        approvals[1].Decision.Should().Be("Pending");
        // F1: an "HR" step is the HR Manager role queue — deterministic — not one employee picked by
        // an unordered designation match.
        approvals[1].ApproverId.Should().BeNull();
        approvals[1].ApproverRole.Should().Be("HR");

        var afterManagerProjection = await db.ApprovalRequests.Include(a => a.Decisions)
            .SingleAsync(a => a.Id == submitted.Id);
        afterManagerProjection.Status.Should().Be("Pending");
        afterManagerProjection.CurrentStepOrder.Should().Be(2);
        afterManagerProjection.CurrentApproverUserId.Should().BeNull();
        afterManagerProjection.CurrentApproverRole.Should().Be("HR Manager");
        afterManagerProjection.WorkflowId.Should().Be(managerThenHr.Id);
        afterManagerProjection.Decisions.Should().ContainSingle(d => d.StepOrder == 1 && d.Decision == "Approved");

        await service.ApproveRequestAsync(tenantId, submitted.Id, hrUserId, "HR One", "final");

        var finalRequest = await db.LeaveRequests.SingleAsync(r => r.Id == submitted.Id);
        finalRequest.Status.Should().Be("Approved");
        finalRequest.DecidedAtUtc.Should().NotBeNull();
        var finalBalance = await db.EmployeeLeaveBalances.SingleAsync(b => b.EmployeeId == employee.Id);
        finalBalance.Pending.Should().Be(0);
        finalBalance.Used.Should().Be(2);
        var finalProjection = await db.ApprovalRequests.Include(a => a.Decisions)
            .SingleAsync(a => a.Id == submitted.Id);
        finalProjection.Status.Should().Be("Approved");
        finalProjection.CompletedAtUtc.Should().NotBeNull();
        finalProjection.Decisions.Should().HaveCount(2);
    }

    [Fact]
    public async Task RelationalDecision_ConsumesPendingStepOnce_AndKeepsProjectionBalanceAndAuditAtomic()
    {
        var connectionString = $"Data Source=leave-cas-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=10";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(); // anchor keeps the shared in-memory database alive
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseSqlite(connection)
            .Options);
        await db.Database.EnsureCreatedAsync();

        var tenantId = Guid.NewGuid();
        var managerUserId = Guid.NewGuid();
        var leaveType = new LeaveType { TenantId = tenantId, Code = "CAS", NameEn = "CAS Leave", IsActive = true };
        var manager = new Employee
        {
            TenantId = tenantId, UserAccountId = managerUserId, EmployeeCode = "MGR-CAS",
            FullName = "CAS Manager", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-4)
        };
        db.AddRange(leaveType, manager);
        await db.SaveChangesAsync();
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = "EMP-CAS", FullName = "CAS Employee",
            ManagerEmployeeId = manager.Id, Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-2)
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var start = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(10));
        db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
        {
            TenantId = tenantId, EmployeeId = employee.Id, EmployeeName = employee.FullName,
            LeaveTypeId = leaveType.Id, LeaveTypeName = leaveType.NameEn, Year = start.Year, Entitled = 10
        });
        await db.SaveChangesAsync();

        var service = await TestApprovalConfig.LeaveServiceAsync(db, tenantId, approverType: "Manager");
        var submitted = await service.SubmitRequestAsync(tenantId, new LeaveRequest
        {
            EmployeeId = employee.Id, LeaveTypeId = leaveType.Id,
            StartDate = start, EndDate = start, DayType = "Full"
        });

        async Task<bool> TryApproveAsync(string comment)
        {
            try
            {
                await using var workerConnection = new SqliteConnection(connectionString);
                await workerConnection.OpenAsync();
                await using var workerDb = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
                    .UseSqlite(workerConnection)
                    .Options);
                await new LeaveService(workerDb, new ApprovalRouter(workerDb))
                    .ApproveRequestAsync(tenantId, submitted.Id, managerUserId, manager.FullName, comment);
                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or SqliteException)
            {
                // SQLite can surface the losing writer as a lock error before it reaches the CAS;
                // PostgreSQL reaches the zero-row CAS. Both prove that only one transition commits.
                return false;
            }
        }

        var outcomes = await Task.WhenAll(
            TryApproveAsync("CAS contender A"),
            TryApproveAsync("CAS contender B"));
        outcomes.Count(x => x).Should().Be(1, "only one concurrent approver may consume the pending step");

        await using var verifyConnection = new SqliteConnection(connectionString);
        await verifyConnection.OpenAsync();
        await using var verifyDb = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseSqlite(verifyConnection)
            .Options);

        (await verifyDb.LeaveApprovals.CountAsync(a => a.LeaveRequestId == submitted.Id && a.Decision == "Approved"))
            .Should().Be(1);
        (await verifyDb.ApprovalDecisions.CountAsync(a => a.ApprovalRequestId == submitted.Id && a.Decision == "Approved"))
            .Should().Be(1);
        (await verifyDb.LeaveBalanceTransactions.CountAsync(t => t.Reference == submitted.Id.ToString() && t.TransactionType == "Used"))
            .Should().Be(1);
        (await verifyDb.ApprovalRequests.SingleAsync(a => a.Id == submitted.Id)).Status.Should().Be("Approved");

        var replayService = new LeaveService(verifyDb, new ApprovalRouter(verifyDb));
        var replay = () => replayService.ApproveRequestAsync(tenantId, submitted.Id, managerUserId, manager.FullName, "CAS replay");
        await replay.Should().ThrowAsync<InvalidOperationException>();
        (await verifyDb.LeaveBalanceTransactions.CountAsync(t => t.Reference == submitted.Id.ToString() && t.TransactionType == "Used"))
            .Should().Be(1);
    }

    [Fact]
    public async Task LeaveCsvExport_UsesCallerEmployeeScope()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var leaveType = new LeaveType { TenantId = tenantId, Code = "AL", NameEn = "Annual Leave", IsActive = true };
        var inScope = new Employee { TenantId = tenantId, EmployeeCode = "EMP-IN", FullName = "In Scope", Status = "Active", JoiningDate = DateTime.UtcNow };
        var outOfScope = new Employee { TenantId = tenantId, EmployeeCode = "EMP-OUT", FullName = "Out Scope", Status = "Active", JoiningDate = DateTime.UtcNow };
        db.LeaveTypes.Add(leaveType);
        db.Employees.AddRange(inScope, outOfScope);
        await db.SaveChangesAsync();
        db.LeaveRequests.AddRange(
            new LeaveRequest { TenantId = tenantId, EmployeeId = inScope.Id, EmployeeName = inScope.FullName, LeaveTypeId = leaveType.Id, LeaveTypeName = leaveType.NameEn, StartDate = new DateOnly(2026, 8, 1), EndDate = new DateOnly(2026, 8, 1), TotalDays = 1, Status = "Submitted" },
            new LeaveRequest { TenantId = tenantId, EmployeeId = outOfScope.Id, EmployeeName = outOfScope.FullName, LeaveTypeId = leaveType.Id, LeaveTypeName = leaveType.NameEn, StartDate = new DateOnly(2026, 8, 2), EndDate = new DateOnly(2026, 8, 2), TotalDays = 1, Status = "Submitted" });
        await db.SaveChangesAsync();

        await TestApprovalConfig.EnsureDefaultLeaveWorkflowAsync(db, tenantId);
        var controller = CreateController(db, tenantId, new FixedScopeService(inScope.Id));
        var result = await controller.Export(CancellationToken.None);

        var csv = result.Should().BeOfType<ContentResult>().Subject.Content;
        csv.Should().Contain("EMP-IN");
        csv.Should().NotContain("EMP-OUT");
        csv.Should().NotContain("Out Scope");
    }

    [Fact]
    public async Task LeaveCsvImport_UsesCallerEmployeeScope()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var leaveType = new LeaveType { TenantId = tenantId, Code = "AL", NameEn = "Annual Leave", IsActive = true };
        var inScope = new Employee { TenantId = tenantId, EmployeeCode = "EMP-IN", FullName = "In Scope", Status = "Active", JoiningDate = DateTime.UtcNow };
        var outOfScope = new Employee { TenantId = tenantId, EmployeeCode = "EMP-OUT", FullName = "Out Scope", Status = "Active", JoiningDate = DateTime.UtcNow };
        db.LeaveTypes.Add(leaveType);
        db.Employees.AddRange(inScope, outOfScope);
        await db.SaveChangesAsync();
        db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
        {
            TenantId = tenantId, EmployeeId = inScope.Id, EmployeeName = inScope.FullName,
            LeaveTypeId = leaveType.Id, LeaveTypeName = leaveType.NameEn, Year = 2026, Entitled = 10
        });
        await db.SaveChangesAsync();

        const string csv = """
EmployeeCode,EmployeeName,LeaveTypeCode,LeaveType,StartDate,EndDate,DayType,HoursRequested,IsEmergency,AttachmentPath,Days,Status,Reason
EMP-IN,In Scope,AL,Annual Leave,2026-08-01,2026-08-01,Full,0,false,,,Submitted,Scoped ok
EMP-OUT,Out Scope,AL,Annual Leave,2026-08-02,2026-08-02,Full,0,false,,,Submitted,Should not import
""";

        await TestApprovalConfig.EnsureDefaultLeaveWorkflowAsync(db, tenantId);
        var controller = CreateController(db, tenantId, new FixedScopeService(inScope.Id));
        var result = await controller.Import(new ImportLeaveRequestsRequest(csv), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var requests = await db.LeaveRequests.OrderBy(r => r.EmployeeName).ToListAsync();
        requests.Should().ContainSingle();
        requests[0].EmployeeId.Should().Be(inScope.Id);
        requests[0].Reason.Should().Be("Scoped ok");
    }

    private static LeaveRequestsController CreateController(ZayraDbContext db, Guid tenantId, IDataScopeService scope)
    {
        var controller = new LeaveRequestsController(
            db,
            new LeaveService(db, new ApprovalRouter(db)),
            scope,
            new NullNotificationService());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("tenant_id", tenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                ], "Test"))
            }
        };
        return controller;
    }

    private sealed class FixedScopeService(params int[] allowedEmployeeIds) : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
            => Task.FromResult(new DataScope
            {
                Level = DataScopeLevel.Team,
                CallerEmployeeId = allowedEmployeeIds.FirstOrDefault(),
                AllowedEmployeeIds = allowedEmployeeIds
            });
    }

    private sealed class NullNotificationService : INotificationService
    {
        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
