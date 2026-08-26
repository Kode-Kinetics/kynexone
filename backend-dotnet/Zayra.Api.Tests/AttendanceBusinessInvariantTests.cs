using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class AttendanceBusinessInvariantTests
{
    [Fact]
    public async Task ApprovedLeave_WithNoPunches_DoesNotCreateAbsenceDeduction()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = AddEmployee(db, tenantId);
        var date = new DateOnly(2026, 8, 18);
        db.LeaveRequests.Add(new LeaveRequest
        {
            TenantId = tenantId, EmployeeId = employee.Id, EmployeeName = employee.FullName,
            LeaveTypeId = Guid.NewGuid(), LeaveTypeName = "Annual Leave", StartDate = date,
            EndDate = date, TotalDays = 1, Status = "Approved"
        });
        await db.SaveChangesAsync();

        await Service(db).ProcessAsync(tenantId, new ProcessAttendanceRequest(date, date, employee.Id),
            new RequestContext(null, null, Guid.NewGuid(), tenantId), CancellationToken.None);

        var daily = await db.AttendanceDailyRecords.SingleAsync();
        Assert.Equal("On leave", daily.Status);
        Assert.False(daily.MissingPunch);
        Assert.DoesNotContain(await db.AttendancePayrollImpacts.ToListAsync(), x => x.ImpactType == "Absence deduction");
    }

    [Fact]
    public async Task ConfiguredRestDay_WithNoPunches_DoesNotCreateAbsenceDeduction()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = AddEmployee(db, tenantId);
        var saturday = new DateOnly(2026, 8, 22); // GCC default rest day
        await db.SaveChangesAsync();

        await Service(db).ProcessAsync(tenantId, new ProcessAttendanceRequest(saturday, saturday, employee.Id),
            new RequestContext(null, null, Guid.NewGuid(), tenantId), CancellationToken.None);

        Assert.Equal("Rest day", (await db.AttendanceDailyRecords.SingleAsync()).Status);
        Assert.Empty(await db.AttendancePayrollImpacts.ToListAsync());
    }

    [Fact]
    public async Task Process_RejectsAnyRangeOverlappingPayrollLock()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var date = new DateOnly(2026, 8, 18);
        AddEmployee(db, tenantId);
        db.AttendanceLockPeriods.Add(new AttendanceLockPeriod
        {
            TenantId = tenantId, PeriodStart = date, PeriodEnd = date, Status = "Locked"
        });
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(db).ProcessAsync(tenantId, new ProcessAttendanceRequest(date, date, null),
                new RequestContext(null, null, Guid.NewGuid(), tenantId), CancellationToken.None));

        Assert.Contains("payroll-locked", error.Message);
        Assert.Empty(await db.AttendanceDailyRecords.ToListAsync());
    }

    [Fact]
    public async Task RegularizationApproval_RechecksCurrentLockState()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var date = new DateOnly(2026, 8, 18);
        var requester = Guid.NewGuid();
        var request = new AttendanceRegularizationRequest
        {
            TenantId = tenantId, EmployeeId = 42, WorkDate = date, Status = "Submitted",
            RequestedByUserId = requester, PayrollLockChecked = false
        };
        db.AttendanceRegularizationRequests.Add(request);
        db.AttendanceLockPeriods.Add(new AttendanceLockPeriod
        {
            TenantId = tenantId, PeriodStart = date, PeriodEnd = date, Status = "Locked"
        });
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(db).ApproveRegularizationAsync(tenantId, request.Id,
                new RegularizationDecisionRequest("approved"),
                new RequestContext(null, null, Guid.NewGuid(), tenantId), CancellationToken.None));

        Assert.Contains("payroll locked", error.Message);
        Assert.Equal("Submitted", request.Status);
    }

    private static Employee AddEmployee(ZayraDbContext db, Guid tenantId)
    {
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"E-{Guid.NewGuid():N}", EnglishName = "Test Employee",
            FullName = "Test Employee", Status = "Active", JoiningDate = new DateTime(2020, 1, 1)
        };
        db.Employees.Add(employee);
        return employee;
    }

    private static AttendanceService Service(ZayraDbContext db) =>
        new(db, new NullNotifications(), new NullHttpClients());

    private static ZayraDbContext CreateDb() => new(
        new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class NullNotifications : INotificationService
    {
        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NullHttpClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
