using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The shared world of the bulk attendance tests (CSV import, preview, mass entry): an in-memory tenant on Riyadh time with a
/// fixed clock, one caller (an HR user with their own employee record) and a handful of employees around them. No Docker.
/// </summary>
internal sealed class BulkKit
{
    /// <summary>"Now" for every test: Saturday 2026-06-20 09:00 UTC, which is 12:00 in Riyadh.</summary>
    public static readonly DateTimeOffset Now = new(2026, 6, 20, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A past working day (Wednesday).</summary>
    public static readonly DateOnly Wednesday = new(2026, 6, 10);

    public required ZayraDbContext Db { get; init; }
    public required Guid TenantId { get; init; }
    public required Guid CompanyA { get; init; }
    public required Guid UserId { get; init; }
    /// <summary>The caller's own employee record.</summary>
    public required Employee Caller { get; init; }
    public required Employee Report { get; init; }
    public required Employee Stranger { get; init; }
    public required Employee Inactive { get; init; }
    /// <summary>Another company's employee.</summary>
    public required Employee Outsider { get; init; }

    public IEnumerable<Employee> All => new[] { Caller, Report, Stranger, Inactive, Outsider };

    public static async Task<BulkKit> CreateAsync()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenantId = Guid.NewGuid();
        var companyA = Guid.NewGuid();
        var companyB = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Bulk attendance", Slug = $"bulk-{tenantId:N}" });
        db.TenantLocalizationSettings.Add(new TenantLocalizationSetting { TenantId = tenantId, CountryCode = "SA", DefaultTimezone = "Asia/Riyadh" });
        Employee Make(string code, Guid company, string status = EmployeeStatuses.Active) => new()
        {
            TenantId = tenantId, CompanyId = company, EmployeeCode = code, FullName = code, EnglishName = code, Status = status,
            JoiningDate = DateTime.UtcNow.AddYears(-2), Salary = 9_000m,
        };
        var caller = Make("E-CALLER", companyA);
        var report = Make("E-REPORT", companyA);
        var stranger = Make("E-STRANGER", companyA);
        var inactive = Make("E-GONE", companyA, "Terminated");
        var outsider = Make("E-OUT", companyB);
        db.Employees.AddRange(caller, report, stranger, inactive, outsider);
        await db.SaveChangesAsync();
        return new BulkKit { Db = db, TenantId = tenantId, CompanyA = companyA, UserId = Guid.NewGuid(), Caller = caller, Report = report, Stranger = stranger, Inactive = inactive, Outsider = outsider };
    }

    public AttendanceService Service() => new(Db, new NullNotifications(), new NullHttpClients(), new FixedClock(Now));

    public RequestContext Context() => new("127.0.0.1", "test", UserId, TenantId);

    /// <summary>Organisation-wide, writing as the caller (so the caller's own record is off limits).</summary>
    public AttendanceBulkAuthority Authority(params int[] allowed) =>
        new(allowed.Length == 0 ? null : allowed, Caller.Id);

    public ClaimsPrincipal Principal(params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", TenantId.ToString()),
            new(ClaimTypes.NameIdentifier, UserId.ToString()),
            new(ClaimTypes.Role, "HR Manager"),
            new("employee_id", Caller.Id.ToString()),
        };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    /// <summary>The attendance controller over this world, with the given data scope (null allowed = organisation-wide).</summary>
    public AttendanceController Controller(int[]? allowed = null, params string[] permissions)
    {
        var controller = new AttendanceController(Service(), new FixedScope(allowed), new HrmHierarchyService(Db, new NullAudit()), Db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Principal(permissions.Length == 0 ? new[] { "attendance.write", "attendance.bulk_import" } : permissions) } },
        };
        return controller;
    }

    /// <summary>One CSV line for an employee code at a local (tenant) time.</summary>
    public static string Line(string code, string local, string direction = "In", string? location = null, string? method = null) =>
        $"{code},{local},{direction},{location},{method}";

    public const string Header = "employeeCode,punchTimestamp,punchDirection,location,method";

    public static string File(params string[] lines) => Header + "\n" + string.Join("\n", lines) + "\n";

    public List<AttendanceRawEvent> Events() => Db.AttendanceRawEvents.AsNoTracking().OrderBy(e => e.PunchTimestampUtc).ThenBy(e => e.EmployeeId).ToList();

    private sealed class FixedScope(int[]? allowed) : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(allowed is null
                ? new DataScope { Level = DataScopeLevel.Organization }
                : new DataScope { Level = DataScopeLevel.Team, AllowedEmployeeIds = allowed });
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class NullNotifications : INotificationService
    {
        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NullHttpClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class NullAudit : IAuditService
    {
        public Task WriteAsync(string action, string entityName, string? entityId, RequestContext context, string? metadata, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
