using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
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

        db.ApprovalPolicies.Add(new ApprovalPolicy
        {
            TenantId = tenantId, WorkflowType = "Leave", Name = "Manager then HR",
            IsDefault = true, IsActive = true,
            Steps =
            {
                new ApprovalPolicyStep { TenantId = tenantId, StepOrder = 1, StepName = "Manager", ApproverType = "Manager" },
                new ApprovalPolicyStep { TenantId = tenantId, StepOrder = 2, StepName = "HR", ApproverType = "HR", IsFinalStep = true },
            }
        });
        var start = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(7));
        db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
        {
            TenantId = tenantId, EmployeeId = employee.Id, EmployeeName = employee.FullName,
            LeaveTypeId = leaveType.Id, LeaveTypeName = leaveType.NameEn,
            Year = start.Year, Entitled = 21
        });
        await db.SaveChangesAsync();

        var service = new LeaveService(db, new ApprovalPolicyService(db));
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
        approvals[1].ApproverId.Should().Be(hrUserId);

        await service.ApproveRequestAsync(tenantId, submitted.Id, hrUserId, "HR One", "final");

        var finalRequest = await db.LeaveRequests.SingleAsync(r => r.Id == submitted.Id);
        finalRequest.Status.Should().Be("Approved");
        finalRequest.DecidedAtUtc.Should().NotBeNull();
        var finalBalance = await db.EmployeeLeaveBalances.SingleAsync(b => b.EmployeeId == employee.Id);
        finalBalance.Pending.Should().Be(0);
        finalBalance.Used.Should().Be(2);
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
            new LeaveService(db, new ApprovalPolicyService(db)),
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
