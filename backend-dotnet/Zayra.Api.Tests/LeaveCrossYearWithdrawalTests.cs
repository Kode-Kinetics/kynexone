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

public class LeaveCrossYearWithdrawalTests
{
    [Fact]
    public async Task Submit_WithDelegate_PersistsLinkedActiveDelegation()
    {
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenantId = Guid.NewGuid();
        var leaveType = new LeaveType { TenantId = tenantId, Code = "DL", NameEn = "Delegated", IsActive = true };
        var employee = new Employee { TenantId = tenantId, EmployeeCode = "DL-1", FullName = "Delegator", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-2) };
        var delegateEmployee = new Employee { TenantId = tenantId, EmployeeCode = "DL-2", FullName = "Delegate", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-2) };
        db.AddRange(leaveType, employee, delegateEmployee);
        await db.SaveChangesAsync();
        var date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10));
        db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
        {
            TenantId = tenantId, EmployeeId = employee.Id, LeaveTypeId = leaveType.Id, Year = date.Year, Entitled = 5
        });
        await db.SaveChangesAsync();

        var submitted = await new LeaveService(db, new ApprovalPolicyService(db)).SubmitRequestAsync(tenantId, new LeaveRequest
        {
            TenantId = tenantId, EmployeeId = employee.Id, LeaveTypeId = leaveType.Id,
            StartDate = date, EndDate = date, DayType = "Full",
            DelegateEmployeeId = delegateEmployee.Id, DelegateEmployeeName = delegateEmployee.FullName
        });

        var delegation = await db.LeaveDelegations.SingleAsync();
        delegation.LeaveRequestId.Should().Be(submitted.Id);
        delegation.EmployeeId.Should().Be(employee.Id);
        delegation.DelegateEmployeeId.Should().Be(delegateEmployee.Id);
        delegation.StartDate.Should().Be(date);
        delegation.Status.Should().Be("Active");
    }

    [Fact]
    public async Task Withdraw_ReleasesEachCalendarYearReservation_AndRecordsReversals()
    {
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var leaveType = new LeaveType { TenantId = tenantId, Code = "AL", NameEn = "Annual", IsActive = true };
        var employee = new Employee
        {
            TenantId = tenantId, UserAccountId = userId, EmployeeCode = "CY-1", FullName = "Cross Year",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-2)
        };
        db.AddRange(leaveType, employee);
        await db.SaveChangesAsync();
        db.EmployeeLeaveBalances.AddRange(
            new EmployeeLeaveBalance { TenantId = tenantId, EmployeeId = employee.Id, LeaveTypeId = leaveType.Id, Year = 2026, Entitled = 5 },
            new EmployeeLeaveBalance { TenantId = tenantId, EmployeeId = employee.Id, LeaveTypeId = leaveType.Id, Year = 2027, Entitled = 5 });
        await db.SaveChangesAsync();

        var service = new LeaveService(db, new ApprovalPolicyService(db));
        var submitted = await service.SubmitRequestAsync(tenantId, new LeaveRequest
        {
            TenantId = tenantId, EmployeeId = employee.Id, LeaveTypeId = leaveType.Id,
            StartDate = new DateOnly(2026, 12, 31), EndDate = new DateOnly(2027, 1, 1), DayType = "Full"
        }, userId);

        var controller = new LeaveRequestsController(db, service, new OwnScope(employee.Id), new NullNotifications())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", tenantId.ToString()),
                        new Claim(ClaimTypes.NameIdentifier, userId.ToString())
                    }, "Test"))
                }
            }
        };

        (await controller.Withdraw(submitted.Id, new WithdrawLeaveRequest("changed plans"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();

        var balances = await db.EmployeeLeaveBalances.OrderBy(x => x.Year).ToListAsync();
        balances.Should().OnlyContain(x => x.Pending == 0);
        var reversals = await db.LeaveBalanceTransactions
            .Where(x => x.Reference == submitted.Id.ToString() && x.TransactionType == "Reversed")
            .OrderBy(x => x.Year).ToListAsync();
        reversals.Should().HaveCount(2);
        reversals.Select(x => x.Year).Should().Equal(2026, 2027);
        reversals.Should().OnlyContain(x => x.Amount == 1);
    }

    private sealed class OwnScope(int employeeId) : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope
            {
                Level = DataScopeLevel.Own,
                CallerEmployeeId = employeeId,
                AllowedEmployeeIds = new[] { employeeId }
            });
    }

    private sealed class NullNotifications : INotificationService
    {
        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
