using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
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

    [Fact]
    public async Task WebPunch_ImmediatelyProjectsDailyRecordUsingRiyadhWorkDate()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = AddEmployee(db, tenantId);
        db.TenantLocalizationSettings.Add(new TenantLocalizationSetting
        {
            TenantId = tenantId, CountryCode = "SAU", DefaultTimezone = "Asia/Riyadh",
            CurrencyCode = "SAR"
        });
        await db.SaveChangesAsync();
        var expected = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh")));

        var raw = await Service(db).PunchAsync(tenantId,
            new WebPunchRequest(employee.Id, "In", "Riyadh HQ", null, null), "Web punch",
            new RequestContext("127.0.0.1", "test", Guid.NewGuid(), tenantId), CancellationToken.None);

        raw.EmployeeId.Should().Be(employee.Id);
        var daily = await db.AttendanceDailyRecords.SingleAsync();
        daily.WorkDate.Should().Be(expected);
        daily.FirstInUtc.Should().NotBeNull();
        daily.Status.Should().Be("Present");
        daily.MissingPunch.Should().BeTrue();
    }

    [Fact]
    public async Task OvernightOutPunch_IsAnchoredToPreviousAssignedWorkDate()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = AddEmployee(db, tenantId);
        var workDate = new DateOnly(2026, 9, 18);
        var shift = new ShiftDefinition
        {
            TenantId = tenantId, Code = "NIGHT", Name = "Night",
            StartTime = new TimeOnly(22, 0), EndTime = new TimeOnly(6, 0), BreakMinutes = 60
        };
        db.TenantLocalizationSettings.Add(new TenantLocalizationSetting
        {
            TenantId = tenantId, CountryCode = "SAU", DefaultTimezone = "Asia/Riyadh"
        });
        db.ShiftDefinitions.Add(shift);
        db.ShiftAssignments.Add(new ShiftAssignment
        {
            TenantId = tenantId, EmployeeId = employee.Id, EmployeeName = employee.FullName,
            ShiftDefinitionId = shift.Id, ShiftName = shift.Name, ShiftCode = shift.Code,
            AssignedDate = workDate
        });
        db.AttendanceRawEvents.AddRange(
            new AttendanceRawEvent
            {
                TenantId = tenantId, EmployeeId = employee.Id, PunchDirection = "In",
                PunchTimestampUtc = new DateTime(2026, 9, 18, 19, 0, 0, DateTimeKind.Utc)
            },
            new AttendanceRawEvent
            {
                TenantId = tenantId, EmployeeId = employee.Id, PunchDirection = "Out",
                PunchTimestampUtc = new DateTime(2026, 9, 19, 2, 30, 0, DateTimeKind.Utc)
            });
        await db.SaveChangesAsync();

        await Service(db).ProcessAsync(tenantId, new ProcessAttendanceRequest(workDate, workDate, employee.Id),
            new RequestContext(null, null, Guid.NewGuid(), tenantId), CancellationToken.None);

        var daily = await db.AttendanceDailyRecords.SingleAsync();
        daily.WorkDate.Should().Be(workDate);
        daily.FirstInUtc.Should().Be(new DateTime(2026, 9, 18, 19, 0, 0, DateTimeKind.Utc));
        daily.LastOutUtc.Should().Be(new DateTime(2026, 9, 19, 2, 30, 0, DateTimeKind.Utc));
        daily.TotalWorkedMinutes.Should().Be(390);
    }

    [Fact]
    public async Task Process_ExcludesInactiveEmployees()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = AddEmployee(db, tenantId);
        employee.Status = EmployeeStatuses.Inactive;
        await db.SaveChangesAsync();

        var processed = await Service(db).ProcessAsync(tenantId,
            new ProcessAttendanceRequest(new DateOnly(2026, 9, 18), new DateOnly(2026, 9, 18), null),
            new RequestContext(null, null, Guid.NewGuid(), tenantId), CancellationToken.None);

        processed.Should().Be(0);
        (await db.AttendanceDailyRecords.CountAsync()).Should().Be(0);
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

[Trait("Category", "Integration")]
public class AttendancePunchConcurrencyPostgresTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;
    public AttendancePunchConcurrencyPostgresTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ConcurrentIdenticalSelfServicePunches_CreateExactlyOneRawEvent()
    {
        var tenantId = Guid.NewGuid();
        int employeeId;
        await using (var seed = _fixture.CreateDb())
        {
            seed.Tenants.Add(new Tenant
            {
                Id = tenantId, Name = "Attendance concurrency tenant", Slug = $"att-{tenantId:N}"
            });
            var employee = new Employee
            {
                TenantId = tenantId, EmployeeCode = $"RACE-{Guid.NewGuid():N}",
                FullName = "Concurrent Punch", Status = EmployeeStatuses.Active,
                JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            };
            seed.Employees.Add(employee);
            await seed.SaveChangesAsync();
            employeeId = employee.Id;
        }

        var timestamp = new DateTime(2026, 9, 19, 5, 30, 0, DateTimeKind.Utc);
        var request = new AttendanceRawEventRequest(employeeId, null, null, "Mobile app punch",
            timestamp, "In", "Riyadh HQ", null, null, "127.0.0.1", null, null, null,
            "Mobile", null);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<bool> AttemptAsync()
        {
            await using var db = _fixture.CreateDb();
            var service = new AttendanceService(db, new PgAttendanceNotifications(), new PgAttendanceHttpClients());
            await gate.Task;
            try
            {
                await service.PushEventAsync(tenantId, request,
                    new RequestContext("127.0.0.1", "test", Guid.NewGuid(), tenantId),
                    CancellationToken.None);
                return true;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Duplicate attendance punch"))
            {
                return false;
            }
        }

        var first = AttemptAsync();
        var second = AttemptAsync();
        gate.SetResult();
        var outcomes = await Task.WhenAll(first, second);

        outcomes.Count(x => x).Should().Be(1);
        await using var verify = _fixture.CreateDb();
        (await verify.AttendanceRawEvents.CountAsync(x => x.TenantId == tenantId
            && x.EmployeeId == employeeId && x.PunchTimestampUtc == timestamp)).Should().Be(1);
    }
}

file sealed class PgAttendanceNotifications : INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
}

file sealed class PgAttendanceHttpClients : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
