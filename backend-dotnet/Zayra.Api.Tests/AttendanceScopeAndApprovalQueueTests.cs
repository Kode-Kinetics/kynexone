using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class AttendanceScopeAndApprovalQueueTests
{
    [Fact]
    public async Task PushEvent_ForbidsResolvedEmployeeOutsideCallerScope()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var allowed = await AddEmployee(db, tenantId, "E-100");
        var outOfScope = await AddEmployee(db, tenantId, "E-200");
        var controller = CreateAttendanceController(db, tenantId, new FixedScope(allowed.Id));

        var result = await controller.PushEvent(
            new AttendanceRawEventRequest(
                outOfScope.Id,
                null,
                null,
                "API push",
                DateTime.UtcNow,
                "In",
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                "API",
                null),
            CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Empty(db.AttendanceRawEvents);
    }

    [Fact]
    public async Task Import_ForbidsCsvWithResolvedEmployeeOutsideCallerScope()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var allowed = await AddEmployee(db, tenantId, "E-100");
        await AddEmployee(db, tenantId, "E-200");
        var controller = CreateAttendanceController(db, tenantId, new FixedScope(allowed.Id));

        var result = await controller.Import(
            new ImportAttendanceRequest(
                "attendance.csv",
                "employeeCode,punchTimestamp,punchDirection\nE-200,2026-07-17T08:00:00Z,In"),
            CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Empty(db.AttendanceRawEvents);
        Assert.Empty(db.AttendanceImportBatches);
    }

    [Fact]
    public async Task MineQueue_MatchesRoleCaseInsensitively()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.ApprovalRequests.Add(new ApprovalRequest
        {
            TenantId = tenantId,
            WorkflowId = Guid.NewGuid(),
            EntityName = "AttendanceRegularizationRequest",
            EntityId = Guid.NewGuid().ToString(),
            Title = "Correction",
            Status = "Pending",
            CurrentApproverType = "Role",
            CurrentApproverRole = "HR Manager",
            DueAtUtc = DateTime.UtcNow.AddHours(4)
        });
        await db.SaveChangesAsync();

        var service = new ApprovalWorkflowService(db, new NullAuditService());
        var result = await service.GetRequestsAsync(
            tenantId,
            null,
            null,
            "mine",
            1,
            25,
            new RequestContext(null, null, Guid.NewGuid(), tenantId, new[] { "hr manager" }),
            CancellationToken.None);

        Assert.Equal(1, result.Total);
    }

    [Fact]
    public async Task TeamQueue_UsesRequestedForEmployeeIdForManagerTeam()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var managerUserId = Guid.NewGuid();
        var manager = await AddEmployee(db, tenantId, "MGR", userId: managerUserId);
        var report = await AddEmployee(db, tenantId, "REP", managerId: manager.Id);
        var unrelated = await AddEmployee(db, tenantId, "OTHER");
        db.ApprovalRequests.AddRange(
            new ApprovalRequest
            {
                TenantId = tenantId,
                WorkflowId = Guid.NewGuid(),
                EntityName = "AttendanceRegularizationRequest",
                EntityId = Guid.NewGuid().ToString(),
                Title = "Team correction",
                Status = "Pending",
                RequestedForEmployeeId = report.Id,
                CurrentApproverType = "Role",
                CurrentApproverRole = "Manager"
            },
            new ApprovalRequest
            {
                TenantId = tenantId,
                WorkflowId = Guid.NewGuid(),
                EntityName = "AttendanceRegularizationRequest",
                EntityId = Guid.NewGuid().ToString(),
                Title = "Other correction",
                Status = "Pending",
                RequestedForEmployeeId = unrelated.Id,
                CurrentApproverType = "Role",
                CurrentApproverRole = "Manager"
            });
        await db.SaveChangesAsync();

        var service = new ApprovalWorkflowService(db, new NullAuditService());
        var result = await service.GetRequestsAsync(
            tenantId,
            null,
            null,
            "team",
            1,
            25,
            new RequestContext(null, null, managerUserId, tenantId, new[] { "Manager" }),
            CancellationToken.None);

        Assert.Equal(1, result.Total);
        Assert.Equal("Team correction", result.Items.Single().Title);
    }

    [Fact]
    public async Task LegacyWorkflowRequests_ForbidsScopedRoleWithoutQueue()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var controller = CreateApprovalWorkflowsController(db, tenantId, Guid.NewGuid(), "Auditor");

        var result = await controller.Requests(null, null, null, 1, 25, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task LegacyWorkflowRequests_UsesExplicitMineQueueForScopedRole()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var managerUserId = Guid.NewGuid();
        await AddEmployee(db, tenantId, "MGR", userId: managerUserId);
        db.ApprovalRequests.AddRange(
            new ApprovalRequest
            {
                TenantId = tenantId,
                WorkflowId = Guid.NewGuid(),
                EntityName = "AttendanceRegularizationRequest",
                EntityId = Guid.NewGuid().ToString(),
                Title = "Manager queue item",
                Status = "Pending",
                CurrentApproverType = "Role",
                CurrentApproverRole = "Manager"
            },
            new ApprovalRequest
            {
                TenantId = tenantId,
                WorkflowId = Guid.NewGuid(),
                EntityName = "AttendanceRegularizationRequest",
                EntityId = Guid.NewGuid().ToString(),
                Title = "HR queue item",
                Status = "Pending",
                CurrentApproverType = "Role",
                CurrentApproverRole = "HR Manager"
            });
        await db.SaveChangesAsync();
        var controller = CreateApprovalWorkflowsController(db, tenantId, managerUserId, "Manager");

        var result = await controller.Requests(null, null, "mine", 1, 25, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var page = Assert.IsType<PagedResult<ApprovalRequestDto>>(ok.Value);
        Assert.Equal(1, page.Total);
        Assert.Equal("Manager queue item", page.Items.Single().Title);
    }

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<Employee> AddEmployee(
        ZayraDbContext db,
        Guid tenantId,
        string code,
        Guid? userId = null,
        int? managerId = null)
    {
        var employee = new Employee
        {
            TenantId = tenantId,
            UserAccountId = userId,
            EmployeeCode = code,
            FullName = code,
            EnglishName = code,
            Status = EmployeeStatuses.Active,
            JoiningDate = DateTime.UtcNow.AddDays(-30),
            ManagerEmployeeId = managerId
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return employee;
    }

    private static AttendanceController CreateAttendanceController(ZayraDbContext db, Guid tenantId, IDataScopeService scope)
    {
        var service = new AttendanceService(db, new NullNotificationService(), new NullHttpClientFactory());
        var controller = new AttendanceController(service, scope, new HrmHierarchyService(db, new NullAuditService()), db);
        var userId = Guid.NewGuid();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                    new Claim("sub", userId.ToString()),
                    new Claim(ClaimTypes.Role, "HR Manager")
                }, "Test"))
            }
        };
        controller.ControllerContext.HttpContext.Connection.RemoteIpAddress = IPAddress.Loopback;
        controller.ControllerContext.HttpContext.Request.Headers.UserAgent = "test";
        return controller;
    }

    private static ApprovalWorkflowsController CreateApprovalWorkflowsController(ZayraDbContext db, Guid tenantId, Guid userId, string role)
    {
        var service = new ApprovalWorkflowService(db, new NullAuditService());
        var controller = new ApprovalWorkflowsController(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", tenantId.ToString()),
                        new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                        new Claim("sub", userId.ToString()),
                        new Claim(ClaimTypes.Role, role)
                    }, "Test"))
                }
            }
        };
        controller.ControllerContext.HttpContext.Connection.RemoteIpAddress = IPAddress.Loopback;
        controller.ControllerContext.HttpContext.Request.Headers.UserAgent = "test";
        return controller;
    }

    private sealed class FixedScope(params int[] allowedEmployeeIds) : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope
            {
                Level = DataScopeLevel.Team,
                CallerEmployeeId = allowedEmployeeIds.FirstOrDefault(),
                AllowedEmployeeIds = allowedEmployeeIds
            });
    }

    private sealed class NullNotificationService : INotificationService
    {
        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class NullHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class NullAuditService : IAuditService
    {
        public Task WriteAsync(string action, string entityName, string? entityId, RequestContext context, string? metadata, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
