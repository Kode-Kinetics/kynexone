using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Dashboard /full endpoint:
///  - Correct KPIs for a seeded tenant (headcount, net payroll)
///  - Tenant isolation: tenant B cannot see tenant A's data
///  - Scope filtering: restricted scope only counts its own employees
///  - ActivityFeed: entries from all three audit-log tables merged + ordered
///  - No-tenant-claim path returns zeroed payload (no exception)
/// </summary>
public class DashboardTests
{
    // ── DB / cache / controller factories ────────────────────────────────────

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static IDistributedCache CreateCache() =>
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

    private static DashboardController MakeCtrl(ZayraDbContext db, Guid tenantId, IDataScopeService? scope = null)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var httpCtx = new DefaultHttpContext { User = principal };

        var ctrl = new DashboardController(db, CreateCache(), scope ?? new _DashUnrestricted());
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }

    private static DashboardController MakeCtrlNoTenant(ZayraDbContext db)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new Claim[]
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        }, "test"));
        var httpCtx = new DefaultHttpContext { User = principal };
        var ctrl = new DashboardController(db, CreateCache(), new _DashUnrestricted());
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DashboardFullDto ExtractFull(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<DashboardFullDto>(ok.Value);
    }

    // Seeds 15 active employees for the tenant (matching rasalmanar fixture).
    private static List<Employee> SeedEmployees(ZayraDbContext db, Guid tenantId, int count = 15)
    {
        var employees = Enumerable.Range(1, count).Select(i => new Employee
        {
            TenantId      = tenantId,
            EmployeeCode  = $"EMP-{i:D3}",
            FullName      = $"Employee {i}",
            Status        = "Active",
            Department    = i % 3 == 0 ? "Finance" : i % 2 == 0 ? "Engineering" : "HR",
            EmploymentType = "Full Time",
            JoiningDate   = DateTime.UtcNow.AddMonths(-6),
        }).ToList();
        db.Employees.AddRange(employees);
        db.SaveChanges();
        return employees;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Full_ActiveHeadcount_Matches_SeededEmployees()
    {
        var db = CreateDb();
        var tid = Guid.NewGuid();
        SeedEmployees(db, tid, count: 15);

        var result = await MakeCtrl(db, tid).Full();

        var payload = ExtractFull(result);
        payload.Summary.TotalEmployees.Should().Be(15);
        payload.Summary.ActiveEmployees.Should().Be(15);
    }

    [Fact]
    public async Task Full_PayrollSummary_ReflectsLatestRun()
    {
        var db = CreateDb();
        var tid = Guid.NewGuid();
        SeedEmployees(db, tid, count: 15);

        var run = new PayrollRun
        {
            TenantId          = tid,
            Year              = 2026,
            Month             = 6,
            Status            = "Locked",
            TotalGrossSalary  = 361_834m,
            TotalDeductions   = 51_844m,
            TotalNetSalary    = 309_990m,
            EmployeeCount     = 15,
        };
        db.PayrollRuns.Add(run);
        db.SaveChanges();

        var result = await MakeCtrl(db, tid).Full();

        var payload = ExtractFull(result);
        payload.Overview.PayrollSummary.Should().NotBeNull();
        payload.Overview.PayrollSummary!.TotalNet.Should().Be(309_990m);
        payload.Overview.PayrollSummary.EmployeeCount.Should().Be(15);
        payload.Overview.PayrollSummary.PeriodLabel.Should().Be("Jun 2026");
        payload.Overview.PayrollSummary.Status.Should().Be("Locked");
    }

    [Fact]
    public async Task Full_CrossTenantIsolation_TenantB_SeesZero()
    {
        var db  = CreateDb();
        var tidA = Guid.NewGuid();
        var tidB = Guid.NewGuid();

        // Seed 15 employees + run for tenant A only.
        SeedEmployees(db, tidA, count: 15);
        db.PayrollRuns.Add(new PayrollRun
        {
            TenantId         = tidA,
            Year             = 2026, Month = 6,
            Status           = "Locked",
            TotalNetSalary   = 309_990m,
            EmployeeCount    = 15,
        });
        db.SaveChanges();

        // Tenant B controller — should see nothing.
        var result = await MakeCtrl(db, tidB).Full();

        var payload = ExtractFull(result);
        payload.Summary.TotalEmployees.Should().Be(0);
        payload.Summary.ActiveEmployees.Should().Be(0);
        payload.Overview.PayrollSummary.Should().BeNull();
    }

    [Fact]
    public async Task Full_NoTenantClaim_ReturnsZeroPayload()
    {
        var db = CreateDb();
        SeedEmployees(db, Guid.NewGuid(), count: 15);

        var result = await MakeCtrlNoTenant(db).Full();

        var payload = ExtractFull(result);
        payload.Summary.TotalEmployees.Should().Be(0);
        payload.Overview.PayrollSummary.Should().BeNull();
        payload.Kpis.PendingLeaveRequests.Should().Be(0);
    }

    [Fact]
    public async Task Full_ScopeFiltering_RestrictedScopeSeesOnlySubset()
    {
        var db = CreateDb();
        var tid = Guid.NewGuid();
        var employees = SeedEmployees(db, tid, count: 15);

        // Leave requests from ALL 15 employees.
        db.LeaveRequests.AddRange(employees.Select(e => new LeaveRequest
        {
            TenantId      = tid,
            EmployeeId    = e.Id,
            Status        = "Submitted",
            StartDate     = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)),
            EndDate       = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)),
            LeaveTypeName = "Annual",
        }));
        db.SaveChanges();

        // Restricted scope: only 5 employees' IDs visible.
        var allowedIds = employees.Take(5).Select(e => e.Id).ToHashSet();
        var restrictedScope = new _DashRestrictedScope(allowedIds);

        var result = await MakeCtrl(db, tid, restrictedScope).Full();

        var payload = ExtractFull(result);
        // KPI: pending leave requests — scope limits it to the 5 allowed employees.
        payload.Kpis.PendingLeaveRequests.Should().Be(5,
            because: "restricted scope should only count leave requests for the 5 allowed employees");
    }

    [Fact]
    public async Task Full_PayrollTrends_PopulatedForCurrentMonth()
    {
        var db = CreateDb();
        var tid = Guid.NewGuid();

        db.PayrollRuns.AddRange(
            new PayrollRun { TenantId = tid, Year = 2026, Month = 6, Status = "Locked", TotalNetSalary = 309_990m, EmployeeCount = 15 },
            new PayrollRun { TenantId = tid, Year = 2026, Month = 5, Status = "Locked", TotalNetSalary = 305_000m, EmployeeCount = 15 }
        );
        db.SaveChanges();

        var result = await MakeCtrl(db, tid).Full(months: 6);

        var payload = ExtractFull(result);
        payload.PayrollTrends.Should().HaveCount(6, because: "6 months requested");

        var junEntry = payload.PayrollTrends.FirstOrDefault(t => t.Month == "Jun");
        junEntry.Should().NotBeNull(because: "Jun 2026 run was seeded");
        junEntry!.TotalNet.Should().Be(309_990m);
        junEntry.EmployeeCount.Should().Be(15);
    }

    [Fact]
    public async Task Full_ActivityFeed_MergesAllThreeAuditTables()
    {
        var db = CreateDb();
        var tid = Guid.NewGuid();
        var recent = DateTime.UtcNow.AddHours(-1);

        db.PayrollAuditLogs.Add(new PayrollAuditLog
        {
            TenantId = tid, Action = "payroll.processed", CreatedAtUtc = recent.AddSeconds(-3)
        });
        db.LeaveAuditLogs.Add(new LeaveAuditLog
        {
            TenantId = tid, Action = "leave.approved", PerformedByName = "HR Manager",
            CreatedAtUtc = recent.AddSeconds(-2)
        });
        db.AttendanceAuditLogs.Add(new AttendanceAuditLog
        {
            TenantId = tid, Action = "attendance.corrected", CreatedAtUtc = recent.AddSeconds(-1)
        });
        db.SaveChanges();

        var result = await MakeCtrl(db, tid).Full();

        var payload = ExtractFull(result);
        payload.ActivityFeed.Should().HaveCount(3, because: "one entry from each audit table");

        var modules = payload.ActivityFeed.Select(f => f.Module).ToHashSet();
        modules.Should().Contain("Payroll");
        modules.Should().Contain("Leave");
        modules.Should().Contain("Attendance");

        // Feed should be newest-first.
        payload.ActivityFeed[0].Module.Should().Be("Attendance",
            because: "attendance entry has the most recent timestamp");

        var leaveEntry = payload.ActivityFeed.Single(f => f.Module == "Leave");
        leaveEntry.Actor.Should().Be("HR Manager");
    }

    [Fact]
    public async Task Full_ActivityFeed_OnlyIncludesLast7Days()
    {
        var db = CreateDb();
        var tid = Guid.NewGuid();

        db.PayrollAuditLogs.Add(new PayrollAuditLog
        {
            TenantId = tid, Action = "payroll.old",
            CreatedAtUtc = DateTime.UtcNow.AddDays(-10)   // older than 7 days
        });
        db.PayrollAuditLogs.Add(new PayrollAuditLog
        {
            TenantId = tid, Action = "payroll.recent",
            CreatedAtUtc = DateTime.UtcNow.AddHours(-2)   // within 7 days
        });
        db.SaveChanges();

        var result = await MakeCtrl(db, tid).Full();

        var payload = ExtractFull(result);
        payload.ActivityFeed.Should().HaveCount(1, because: "only the entry within 7 days should appear");
        payload.ActivityFeed[0].Action.Should().Be("payroll.recent");
    }

    [Fact]
    public async Task Full_HeadcountByDepartment_GroupedCorrectly()
    {
        var db = CreateDb();
        var tid = Guid.NewGuid();

        // 8 HR + 5 Engineering + 2 Finance
        db.Employees.AddRange(
            Enumerable.Range(1, 8).Select(i => new Employee { TenantId = tid, Status = "Active", Department = "HR",          EmploymentType = "Full Time", JoiningDate = DateTime.UtcNow.AddMonths(-1) })
            .Concat(Enumerable.Range(1, 5).Select(i => new Employee { TenantId = tid, Status = "Active", Department = "Engineering", EmploymentType = "Full Time", JoiningDate = DateTime.UtcNow.AddMonths(-1) }))
            .Concat(Enumerable.Range(1, 2).Select(i => new Employee { TenantId = tid, Status = "Active", Department = "Finance",     EmploymentType = "Full Time", JoiningDate = DateTime.UtcNow.AddMonths(-1) }))
        );
        db.SaveChanges();

        var result = await MakeCtrl(db, tid).Full();

        var payload = ExtractFull(result);
        var deptBreakdown = payload.Overview.HeadcountByDepartment;
        deptBreakdown.Should().HaveCount(3);

        var hr = deptBreakdown.Single(d => d.Name == "HR");
        hr.Value.Should().Be(8);

        var eng = deptBreakdown.Single(d => d.Name == "Engineering");
        eng.Value.Should().Be(5);

        // Results must be sorted descending by headcount.
        deptBreakdown[0].Name.Should().Be("HR");
    }

    [Fact]
    public async Task Full_InactiveEmployees_NotCountedInActiveHeadcount()
    {
        var db = CreateDb();
        var tid = Guid.NewGuid();

        db.Employees.AddRange(
            new Employee { TenantId = tid, Status = "Active",     EmploymentType = "Full Time", JoiningDate = DateTime.UtcNow.AddMonths(-1) },
            new Employee { TenantId = tid, Status = "Active",     EmploymentType = "Full Time", JoiningDate = DateTime.UtcNow.AddMonths(-1) },
            new Employee { TenantId = tid, Status = "Terminated", EmploymentType = "Full Time", JoiningDate = DateTime.UtcNow.AddMonths(-6) },
            new Employee { TenantId = tid, Status = "Archived",   EmploymentType = "Full Time", JoiningDate = DateTime.UtcNow.AddMonths(-12) }
        );
        db.SaveChanges();

        var result = await MakeCtrl(db, tid).Full();

        var payload = ExtractFull(result);
        payload.Summary.TotalEmployees.Should().Be(4);
        payload.Summary.ActiveEmployees.Should().Be(2);
    }
}

// ── File-scoped stubs ─────────────────────────────────────────────────────────

file sealed class _DashUnrestricted : IDataScopeService
{
    public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
}

file sealed class _DashRestrictedScope : IDataScopeService
{
    private readonly IReadOnlyCollection<int> _allowed;
    public _DashRestrictedScope(IReadOnlyCollection<int> allowed) => _allowed = allowed;
    public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new DataScope { Level = DataScopeLevel.Department, AllowedEmployeeIds = _allowed });
}
