using System.Security.Claims;
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
using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Tests;

/// <summary>
/// A small in-memory leave world for the Batch 1 tests (withdraw, cancel, pending-status consumers, attachment keys):
/// no web host, so these run anywhere. The approval workflow routes every leave to the HR queue; <see cref="HrUserId"/>
/// is the HR login that decides.
/// </summary>
internal sealed class LeaveBatch1World
{
    public required ZayraDbContext Db { get; init; }
    public required DbContextOptions<ZayraDbContext> Options { get; init; }
    public required Guid TenantId { get; init; }
    public required Guid CompanyId { get; init; }
    public required Employee Emp { get; init; }
    public required Employee Other { get; init; }
    public required Employee Boss { get; init; }
    public required Guid EmpUserId { get; init; }
    public required Guid OtherUserId { get; init; }
    public required Guid BossUserId { get; init; }
    public required Guid HrUserId { get; init; }
    public required LeaveType LeaveType { get; init; }
    public required LeaveService Service { get; init; }
    public RecordingNotifications Notices { get; } = new();

    private int _dayCursor = 20;

    public static async Task<LeaveBatch1World> CreateAsync(bool requireAttachment = false)
    {
        var options = new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new ZayraDbContext(options);
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Leave batch 1", Slug = $"lb1-{tenantId:N}", IsActive = true });
        var leaveType = new LeaveType { TenantId = tenantId, Code = "AL", NameEn = "Annual Leave", IsActive = true, IsPaid = true, RequiresAttachment = requireAttachment };
        db.LeaveTypes.Add(leaveType);
        Guid empUser = Guid.NewGuid(), otherUser = Guid.NewGuid(), bossUser = Guid.NewGuid(), hrUser = Guid.NewGuid();
        Employee Make(string code, Guid? user, int? manager = null) => new()
        {
            TenantId = tenantId, CompanyId = companyId, EmployeeCode = code, FullName = $"Person {code}", Department = "Ops", Designation = "Officer",
            JobTitle = "Officer", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-3), UserAccountId = user,
            WorkEmail = $"{code}@lb1.test", ManagerEmployeeId = manager,
        };
        var hr = Make("HR1", hrUser);
        var boss = Make("BOSS1", bossUser);
        db.Employees.AddRange(hr, boss);
        await db.SaveChangesAsync();
        var emp = Make("EMP1", empUser, boss.Id);
        var other = Make("OTH1", otherUser);
        db.Employees.AddRange(emp, other);
        await db.SaveChangesAsync();
        foreach (var year in new[] { DateTime.UtcNow.Year, DateTime.UtcNow.Year + 1 })
            db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
            {
                TenantId = tenantId, EmployeeId = emp.Id, EmployeeName = emp.FullName, LeaveTypeId = leaveType.Id,
                LeaveTypeName = leaveType.NameEn, Year = year, Entitled = 60,
            });
        await db.SaveChangesAsync();
        var service = await TestApprovalConfig.LeaveServiceAsync(db, tenantId, "HR");
        return new LeaveBatch1World
        {
            Db = db, Options = options, TenantId = tenantId, CompanyId = companyId, Emp = emp, Other = other, Boss = boss, EmpUserId = empUser,
            OtherUserId = otherUser, BossUserId = bossUser, HrUserId = hrUser, LeaveType = leaveType, Service = service,
        };
    }

    /// <summary>A second context over the same in-memory store: another request, as the database sees it.</summary>
    public ZayraDbContext NewContext() => new(Options);

    /// <summary>The next working day (never a Fri/Sat rest day), each call a later one, so requests never overlap.</summary>
    public DateOnly NextWorkday()
    {
        while (true)
        {
            var day = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(++_dayCursor));
            if (day.DayOfWeek is not (DayOfWeek.Friday or DayOfWeek.Saturday)) return day;
        }
    }

    /// <summary>The employee submits a one-day leave through the service; it waits for HR.</summary>
    public async Task<LeaveRequest> SubmitAsync(string attachmentPath = "")
    {
        var day = NextWorkday();
        return await Service.SubmitRequestAsync(TenantId, new LeaveRequest
        {
            TenantId = TenantId, EmployeeId = Emp.Id, LeaveTypeId = LeaveType.Id, StartDate = day, EndDate = day, DayType = "Full",
            AttachmentPath = attachmentPath,
        }, EmpUserId);
    }

    /// <summary>A row written directly in a status the service may never save (Submitted, Draft…).</summary>
    public async Task<LeaveRequest> SeedAsync(string status, DateOnly start, DateOnly end, int? employeeId = null, string attachmentPath = "")
    {
        var row = new LeaveRequest
        {
            TenantId = TenantId, CompanyId = CompanyId, EmployeeId = employeeId ?? Emp.Id, EmployeeName = Emp.FullName, LeaveTypeId = LeaveType.Id,
            LeaveTypeName = LeaveType.NameEn, StartDate = start, EndDate = end, TotalDays = 1, DayType = "Full", Status = status,
            Reason = $"seed-{status}", AttachmentPath = attachmentPath, SubmittedAtUtc = DateTime.UtcNow,
        };
        Db.LeaveRequests.Add(row);
        await Db.SaveChangesAsync();
        return row;
    }

    public async Task SeedEveryStatusAroundTodayAsync(string[] statuses)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var status in statuses) await SeedAsync(status, today.AddDays(-1), today.AddDays(1));
    }

    public DataScope OwnScope(Employee who) => new() { Level = DataScopeLevel.Own, CallerEmployeeId = who.Id, AllowedEmployeeIds = new[] { who.Id } };

    public DataScope TeamScope(Employee manager, params Employee[] reports) => new()
    {
        Level = DataScopeLevel.DirectReports, CallerEmployeeId = manager.Id, AllowedEmployeeIds = reports.Select(r => r.Id).ToArray(),
    };

    public static DataScope OrgScope() => new() { Level = DataScopeLevel.Organization };

    public ClaimsPrincipal User(Guid userId, string? role, params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", TenantId.ToString()), new(ClaimTypes.NameIdentifier, userId.ToString()), new(ClaimTypes.Name, "Test User"),
        };
        if (role is not null) claims.Add(new Claim(ClaimTypes.Role, role));
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    public LeaveRequestsController Leave(DataScope scope, ClaimsPrincipal user) => new(Db, Service, new FixedScope(scope), Notices)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } },
    };

    internal sealed class FixedScope(DataScope scope) : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) => Task.FromResult(scope);
    }

    internal sealed class RecordingNotifications : INotificationService
    {
        public List<(Guid? UserId, string Title, string? EntityId)> Sent { get; } = [];
        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken cancellationToken)
        {
            Sent.Add((userId, title, entityId));
            return Task.CompletedTask;
        }
        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
