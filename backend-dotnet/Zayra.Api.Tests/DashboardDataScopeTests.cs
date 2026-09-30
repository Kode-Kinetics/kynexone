using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// F10 — the home dashboard computes every figure and list inside the caller's data scope.
///
/// <para>Before: a manager or an employee calling <c>/api/dashboard/full</c> was served the same cached,
/// organization-wide payload as HR — the whole company's headcount, approval queue (with names),
/// compliance alerts, activity feed and payroll run. Only the KPI counters were scoped. Payroll
/// figures went to anyone, with or without <c>payroll.read</c>.</para>
///
/// <para>The world: one manager with three reports in Engineering, six other employees elsewhere,
/// every employee with a pending leave request, a pending approval about them, an expiring Iqama, a
/// leave audit row, attendance today and a slip in this month's payroll run.</para>
/// </summary>
public class DashboardDataScopeTests
{
    private sealed record World(Guid TenantId, Employee Manager, Employee[] Team, Employee[] Others)
    {
        public HashSet<int> TeamIds => Team.Select(e => e.Id).ToHashSet();
        public int Everyone => 1 + Team.Length + Others.Length;
    }

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static IDistributedCache CreateCache() =>
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

    private static async Task<World> SeedAsync(ZayraDbContext db)
    {
        var tid = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tid, Name = "Scope Tenant", Slug = $"scope-{tid:N}" });
        Employee Emp(string code, string department, string nationality) => new()
        {
            TenantId = tid, EmployeeCode = code, FullName = $"Person {code}", Status = "Active",
            Department = department, EmploymentType = "Full Time", Nationality = nationality,
            JoiningDate = DateTime.UtcNow.AddYears(-2),
        };
        var manager = Emp("MGR", "Engineering", "Saudi");
        db.Employees.Add(manager);
        await db.SaveChangesAsync();
        var team = Enumerable.Range(1, 3).Select(i => Emp($"TEAM{i}", "Engineering", "Saudi")).ToArray();
        foreach (var member in team) member.ManagerEmployeeId = manager.Id;
        var others = Enumerable.Range(1, 6).Select(i => Emp($"OTHER{i}", i % 2 == 0 ? "Finance" : "Sales", "Indian")).ToArray();
        db.Employees.AddRange(team);
        db.Employees.AddRange(others);
        await db.SaveChangesAsync();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var run = new PayrollRun
        {
            TenantId = tid, Year = today.Year, Month = today.Month, Status = "Locked",
            TotalGrossSalary = 12_000m, TotalNetSalary = 10_000m, TotalDeductions = 2_000m, EmployeeCount = 10,
        };
        db.PayrollRuns.Add(run);
        foreach (var e in new[] { manager }.Concat(team).Concat(others))
        {
            var leave = new LeaveRequest
            {
                TenantId = tid, EmployeeId = e.Id, Status = "Submitted", LeaveTypeName = "Annual",
                StartDate = today.AddDays(5), EndDate = today.AddDays(6),
            };
            db.LeaveRequests.Add(leave);
            db.ApprovalRequests.Add(new ApprovalRequest
            {
                TenantId = tid, Status = "Pending", EntityName = nameof(LeaveRequest), EntityId = leave.Id.ToString(),
                Title = $"Annual leave {e.EmployeeCode}", RequestedForEmployeeId = e.Id,
            });
            db.LeaveAuditLogs.Add(new LeaveAuditLog
            {
                TenantId = tid, EntityType = nameof(LeaveRequest), EntityId = leave.Id.ToString(),
                Action = $"Submitted {e.EmployeeCode}", PerformedByName = e.FullName,
                CreatedAtUtc = DateTime.UtcNow.AddHours(-1),
            });
            db.EmployeeComplianceRecords.Add(new EmployeeComplianceRecord
            {
                TenantId = tid, EmployeeId = e.Id, FieldKey = "iqama_number", FieldLabel = "Iqama",
                ExpiryDate = today.AddDays(10),
            });
            db.AttendanceDailyRecords.Add(new AttendanceDailyRecord
            {
                TenantId = tid, EmployeeId = e.Id, WorkDate = today, Status = "Present",
            });
            db.AttendanceRecords.Add(new AttendanceRecord
            {
                TenantId = tid, EmployeeId = e.Id, WorkDate = today,
                Status = team.Contains(e) ? "Present" : "Absent",
            });
            db.PayrollSlips.Add(new PayrollSlip
            {
                TenantId = tid, RunId = run.Id, EmployeeId = e.Id, EmployeeCode = e.EmployeeCode,
                EmployeeName = e.FullName, Department = e.Department ?? "",
                GrossSalary = 1_200m, Deductions = 200m, NetSalary = 1_000m, EmployerStatutoryTotal = 90m,
            });
        }
        // A company-level approval that is about nobody in particular: HR's business, not a manager's.
        db.ApprovalRequests.Add(new ApprovalRequest
        {
            TenantId = tid, Status = "Pending", EntityName = "PayrollRun", EntityId = run.Id.ToString(),
            Title = "Payroll run approval",
        });
        db.PayrollAuditLogs.Add(new PayrollAuditLog
        {
            TenantId = tid, Action = "payroll.processed", EntityName = "PayrollRun", EntityId = run.Id.ToString(),
            CreatedAtUtc = DateTime.UtcNow.AddHours(-2),
        });
        await db.SaveChangesAsync();
        return new World(tid, manager, team, others);
    }

    private static DashboardController Ctrl(ZayraDbContext db, IDistributedCache cache, Guid tid, IDataScopeService scope,
        params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tid.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        return new DashboardController(db, cache, scope)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
            },
        };
    }

    private static async Task<DashboardFullDto> FullAs(ZayraDbContext db, IDistributedCache cache, Guid tid,
        IDataScopeService scope, params string[] permissions) =>
        Assert.IsType<DashboardFullDto>(Assert.IsType<OkObjectResult>(
            await Ctrl(db, cache, tid, scope, permissions).Full(6)).Value);

    private static IDataScopeService ManagerScope(World w) =>
        new FixedScope(DataScopeLevel.Team, w.Manager.Id, new[] { w.Manager.Id }.Concat(w.TeamIds).ToList());

    private static IDataScopeService EmployeeScope(Employee e) =>
        new FixedScope(DataScopeLevel.Own, e.Id, new[] { e.Id });

    private static readonly IDataScopeService OrganizationScope = new FixedScope(DataScopeLevel.Organization, null, null);

    [Fact]
    public async Task Manager_SeesTheirTeam_NotTheOrganization()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);

        var d = await FullAs(db, CreateCache(), w.TenantId, ManagerScope(w), "dashboard.read", "manager.read", "employees.read");

        d.Summary.TotalEmployees.Should().Be(3, "a manager's dashboard is their three reports, not the tenant's ten people");
        d.Summary.ActiveEmployees.Should().Be(3);
        d.Summary.PresentToday.Should().Be(3);
        d.Overview.PendingApprovals.Should().Be(3);
        d.Overview.ApprovalQueue.Should().HaveCount(3)
            .And.OnlyContain(a => a.EmployeeId != null && w.TeamIds.Contains(a.EmployeeId.Value),
                "approvals about people outside the team — or about nobody — are not theirs to see");
        d.Overview.OpenLeaveRequests.Should().Be(3);
        d.Overview.HeadcountByDepartment.Should().ContainSingle(x => x.Name == "Engineering" && x.Value == 3);
        d.Overview.WorkforceMix.Sum(x => x.Value).Should().Be(3);
        d.Overview.ComplianceAlertsTotal.Should().Be(3);
        d.Overview.Alerts.Should().OnlyContain(a => a.EmployeeId != null && w.TeamIds.Contains(a.EmployeeId.Value));
        d.ActivityFeed.Should().HaveCount(3).And.OnlyContain(a => a.Module == "Leave" && a.Action.Contains("TEAM"));
        d.Trends.Last().AttendanceRate.Should().Be(100m, "the team was present; the other six were absent");
        d.Analytics!.AttendanceHeatmap.Departments.Should().ContainSingle(x => x.Name == "Engineering" && x.Headcount == 3);
        d.Analytics.HeadcountTrend.Last().Active.Should().Be(3);
        d.Analytics.Nationality!.Saudi.Should().Be(3);
        d.Analytics.Nationality.NonSaudi.Should().Be(0);
        d.Analytics.Nationality.NitaqatBand.Should().BeNull("a Nitaqat band belongs to an establishment, not a team");

        d.Overview.PayrollSummary.Should().BeNull("a manager without payroll.read sees no payroll figures");
        d.Overview.PayrollByEntity.Should().BeEmpty();
        d.PayrollTrends.Should().BeEmpty();
        ((object?)d.Kpis.MissingSalaryAssignments).Should().BeNull("payroll readiness is payroll information");
        ((object?)d.Kpis.MissingBankDetails).Should().BeNull();
    }

    [Fact]
    public async Task Employee_SeesOnlyThemselves()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);
        var me = w.Others[0];

        var d = await FullAs(db, CreateCache(), w.TenantId, EmployeeScope(me), "dashboard.read", "ess.read");

        d.Summary.TotalEmployees.Should().Be(1, "an employee's dashboard is their own record");
        d.Overview.PendingApprovals.Should().Be(1);
        d.Overview.ApprovalQueue.Should().ContainSingle().Which.EmployeeId.Should().Be(me.Id);
        d.Overview.OpenLeaveRequests.Should().Be(1);
        d.Overview.ComplianceAlertsTotal.Should().Be(1);
        d.Overview.Alerts.Should().OnlyContain(a => a.EmployeeId == me.Id);
        d.Overview.HeadcountByDepartment.Sum(x => x.Value).Should().Be(1);
        d.ActivityFeed.Should().ContainSingle().Which.Action.Should().Contain(me.EmployeeCode);
        d.Overview.PayrollSummary.Should().BeNull();
        d.PayrollTrends.Should().BeEmpty();
    }

    /// <summary>
    /// The cache was keyed by tenant and company only. Whoever loaded the dashboard first decided what
    /// everybody in the company saw for the next 60 s.
    /// </summary>
    [Fact]
    public async Task AScopedCaller_NeverReadsTheOrganizationCacheEntry_NorPoisonsIt()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);
        var shared = CreateCache();

        var hr = await FullAs(db, shared, w.TenantId, OrganizationScope, "employees.write", "payroll.read");
        var manager = await FullAs(db, shared, w.TenantId, ManagerScope(w), "manager.read", "employees.read");
        var hrAgain = await FullAs(db, shared, w.TenantId, OrganizationScope, "employees.write", "payroll.read");

        hr.Summary.TotalEmployees.Should().Be(w.Everyone);
        manager.Summary.TotalEmployees.Should().Be(3, "the manager must not be served HR's cached organization payload");
        manager.Overview.PendingApprovals.Should().Be(3);
        manager.Overview.PayrollSummary.Should().BeNull();
        hrAgain.Summary.TotalEmployees.Should().Be(w.Everyone, "nor may the manager's entry be served to HR");
        hrAgain.Overview.PendingApprovals.Should().Be(w.Everyone + 1, "HR also sees the approval about nobody in particular");
        hrAgain.Overview.PayrollSummary!.TotalNet.Should().Be(10_000m);
    }

    [Fact]
    public async Task OrganizationCaller_WithoutPayrollRead_GetsNoPayrollFigures()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);
        var shared = CreateCache();

        var payroll = await FullAs(db, shared, w.TenantId, OrganizationScope, "employees.write", "payroll.read");
        var hrOfficer = await FullAs(db, shared, w.TenantId, OrganizationScope, "employees.write");

        payroll.Overview.PayrollSummary!.TotalNet.Should().Be(10_000m);
        payroll.PayrollTrends.Should().Contain(p => p.TotalNet == 10_000m);
        payroll.ActivityFeed.Should().Contain(a => a.Module == "Payroll");

        hrOfficer.Summary.TotalEmployees.Should().Be(w.Everyone, "headcount is not payroll information");
        hrOfficer.Overview.PayrollSummary.Should().BeNull("the shared organization entry carries payroll; it is removed per caller");
        hrOfficer.Overview.PayrollByEntity.Should().BeEmpty();
        hrOfficer.PayrollTrends.Should().BeEmpty();
        hrOfficer.ActivityFeed.Should().NotContain(a => a.Module == "Payroll");
        ((object?)hrOfficer.Kpis.MissingSalaryAssignments).Should().BeNull();
    }

    [Fact]
    public async Task ScopedCaller_WithPayrollRead_SeesTheirPeoplesPay_NotTheRunTotal()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);

        var d = await FullAs(db, CreateCache(), w.TenantId, ManagerScope(w), "manager.read", "employees.read", "payroll.read");

        d.Overview.PayrollSummary.Should().NotBeNull();
        d.Overview.PayrollSummary!.TotalNet.Should().Be(3_000m, "three reports at 1,000 net each — not the run's 10,000");
        d.Overview.PayrollSummary.TotalGross.Should().Be(3_600m);
        d.Overview.PayrollSummary.EmployeeCount.Should().Be(3);
        d.Overview.PayrollByEntity.Should().ContainSingle(x => x.Name == "Engineering" && x.Value == 3_000m);
        d.PayrollTrends.Should().Contain(p => p.TotalNet == 3_000m && p.EmployeeCount == 3);
        d.PayrollTrends.Should().NotContain(p => p.TotalNet == 10_000m);
        ((object?)d.Kpis.MissingSalaryAssignments).Should().NotBeNull();
    }

    [Fact]
    public async Task TheOverviewAndTrendsEndpoints_AreScopedToo()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);
        var shared = CreateCache();
        // HR loads first, so an organization entry is sitting in the cache.
        await Ctrl(db, shared, w.TenantId, OrganizationScope, "employees.write", "payroll.read").Overview(default);
        await Ctrl(db, shared, w.TenantId, OrganizationScope, "employees.write", "payroll.read").Trends(6);

        var overview = Assert.IsType<DashboardOverviewDto>(Assert.IsType<OkObjectResult>(
            await Ctrl(db, shared, w.TenantId, ManagerScope(w), "manager.read").Overview(default)).Value);
        var trends = Assert.IsAssignableFrom<IReadOnlyList<DashboardTrendDto>>(Assert.IsType<OkObjectResult>(
            await Ctrl(db, shared, w.TenantId, ManagerScope(w), "manager.read").Trends(6)).Value);

        overview.PendingApprovals.Should().Be(3);
        overview.OpenLeaveRequests.Should().Be(3);
        overview.PayrollSummary.Should().BeNull();
        trends.Last().AttendanceRate.Should().Be(100m);
    }

    private sealed class FixedScope(DataScopeLevel level, int? self, IReadOnlyCollection<int>? allowed) : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = level, CallerEmployeeId = self, AllowedEmployeeIds = allowed });
    }
}
