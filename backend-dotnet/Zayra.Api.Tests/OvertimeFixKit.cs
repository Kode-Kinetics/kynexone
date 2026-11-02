using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Shared in-memory harness for the overtime audit-fix tests (OVT-01..15). In-memory on purpose: these
/// tests pin controller decisions, not Postgres semantics, and must run without a container.
/// </summary>
internal static class OvertimeFixKit
{
    public sealed record World(ZayraDbContext Db, Guid TenantId, Employee Subject, Guid SubjectUserId, OvertimePolicy Policy);

    public static ZayraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase($"ovt-fix-{Guid.NewGuid():N}").Options);

    public static async Task<World> SeedAsync(bool allowCompOff = true, string employeeStatus = "Active")
    {
        var db = NewDb();
        var tenantId = Guid.NewGuid();
        var subjectUser = Guid.NewGuid();
        var subject = new Employee
        {
            TenantId = tenantId, EmployeeCode = "E-SUBJ", FullName = "Subject Employee", Status = employeeStatus,
            JoiningDate = DateTime.UtcNow.AddYears(-2), Salary = 18_000m, UserAccountId = subjectUser,
        };
        var policy = new OvertimePolicy
        {
            TenantId = tenantId, Code = "STD", Name = "Standard", IsActive = true, RoundingRule = "None",
            MinimumMinutes = 30, MaximumMinutesPerDay = 600, MonthlyCapMinutes = 6000, AllowCompOffConversion = allowCompOff,
        };
        db.Employees.Add(subject);
        db.OvertimePolicies.Add(policy);
        await db.SaveChangesAsync();
        return new World(db, tenantId, subject, subjectUser, policy);
    }

    public static async Task<OvertimeRequest> AddRequestAsync(World w, string status = "PendingManager", int minutes = 120,
        DateOnly? workDate = null, Guid? createdBy = null, int approvedMinutes = 0)
    {
        var day = workDate ?? DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3);
        var start = day.ToDateTime(new TimeOnly(17, 0), DateTimeKind.Utc);
        var r = new OvertimeRequest
        {
            TenantId = w.TenantId, EmployeeId = w.Subject.Id, EmployeeName = w.Subject.FullName, OvertimePolicyId = w.Policy.Id,
            WorkDate = day, StartTimeUtc = start, EndTimeUtc = start.AddMinutes(minutes), RequestedMinutes = minutes,
            ApprovedMinutes = approvedMinutes, Reason = "Close", Status = status, CreatedBy = createdBy ?? Guid.NewGuid(),
        };
        w.Db.OvertimeRequests.Add(r);
        await w.Db.SaveChangesAsync();
        return r;
    }

    public static OvertimeController Controller(World w, Guid userId, string[] roles, string[]? permissions = null,
        int? callerEmployeeId = null, IEnumerable<Claim>? extraClaims = null, Microsoft.Extensions.Configuration.IConfiguration? configuration = null)
    {
        var scope = new FixedScope(callerEmployeeId);
        var ctrl = new OvertimeController(w.Db, scope, new HrmHierarchyService(w.Db, new AuditService(w.Db)), configuration: configuration);
        var claims = new List<Claim> { new("tenant_id", w.TenantId.ToString()), new(ClaimTypes.NameIdentifier, userId.ToString()) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange((permissions ?? Array.Empty<string>()).Select(p => new Claim("permission", p)));
        if (extraClaims is not null) claims.AddRange(extraClaims);
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
        };
        return ctrl;
    }

    private sealed class FixedScope(int? callerEmployeeId) : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, CallerEmployeeId = callerEmployeeId });
    }

    public static string? Message(object? result)
    {
        var value = (result as ObjectResult)?.Value;
        return value?.GetType().GetProperty("message")?.GetValue(value) as string;
    }
}
