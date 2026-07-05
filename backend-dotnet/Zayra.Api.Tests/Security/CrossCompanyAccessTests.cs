using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Phase 2 negative matrix (spec 6+7): approval/read/export surfaces cannot cross the
/// company boundary. Approvals work by loading the target row — a row the filter hides
/// cannot be approved (404 semantics); DataScopeService additionally pins the employee-id
/// universe to the actor's companies so even filter-bypassing consumers stay contained.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class CrossCompanyAccessTests
{
    private readonly PostgresFixture _fx;
    public CrossCompanyAccessTests(PostgresFixture fx) => _fx = fx;

    private sealed record World(Guid TenantId, Guid CompanyA, Guid CompanyB, int EmpA, int EmpB, Guid LeaveB, Guid RunB);

    private async Task<World> SeedWorld()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var a = new Company { TenantId = tenantId, LegalNameEn = "XC A", RegistrationNumber = $"R-{Guid.NewGuid():N}", IsActive = true };
        var b = new Company { TenantId = tenantId, LegalNameEn = "XC B", RegistrationNumber = $"R-{Guid.NewGuid():N}", IsActive = true };
        db.Companies.AddRange(a, b);
        var empA = new Employee { TenantId = tenantId, CompanyId = a.Id, EmployeeCode = $"XCA-{Guid.NewGuid():N}"[..12], FullName = "Alpha Emp", Status = "Active", JoiningDate = DateTime.UtcNow };
        var empB = new Employee { TenantId = tenantId, CompanyId = b.Id, EmployeeCode = $"XCB-{Guid.NewGuid():N}"[..12], FullName = "Beta Emp", Status = "Active", JoiningDate = DateTime.UtcNow, Salary = 9000m, BankIban = "SA4420000001234567891234" };
        db.Employees.AddRange(empA, empB);
        await db.SaveChangesAsync();

        var leaveB = new LeaveRequest { TenantId = tenantId, CompanyId = b.Id, EmployeeId = empB.Id, StartDate = new DateOnly(2026, 9, 1), EndDate = new DateOnly(2026, 9, 3), Status = "PendingHRApproval" };
        db.LeaveRequests.Add(leaveB);
        db.AttendanceRegularizationRequests.Add(new AttendanceRegularizationRequest { TenantId = tenantId, CompanyId = b.Id, EmployeeId = empB.Id, WorkDate = new DateOnly(2026, 9, 1), Status = "Submitted" });
        var runB = new PayrollRun { TenantId = tenantId, CompanyId = b.Id, Year = 2026, Month = 8, Status = "Approved" };
        db.PayrollRuns.Add(runB);
        db.PayrollSlips.Add(new PayrollSlip { TenantId = tenantId, CompanyId = b.Id, RunId = runB.Id, EmployeeId = empB.Id, EmployeeCode = empB.EmployeeCode, EmployeeName = empB.FullName, BasicSalary = 9000m, NetSalary = 8500m });
        await db.SaveChangesAsync();
        return new World(tenantId, a.Id, b.Id, empA.Id, empB.Id, leaveB.Id, runB.Id);
    }

    // ── Approvals cannot cross the boundary (leave / attendance) ───────────────

    [Fact]
    public async Task CompanyA_HR_CannotSeeOrApprove_CompanyB_LeaveAndAttendance()
    {
        var w = await SeedWorld();
        await using var db = _fx.CreateDbWithAccessor(Accessor(ScopedHr(w.TenantId, w.CompanyA)));

        (await db.LeaveRequests.FirstOrDefaultAsync(l => l.Id == w.LeaveB))
            .Should().BeNull("an approval endpoint loads the row first — an invisible row is a 404, not an approval");
        (await db.AttendanceRegularizationRequests.Where(r => r.TenantId == w.TenantId).ToListAsync())
            .Should().BeEmpty("Company B attendance corrections must be invisible to Company A HR");
    }

    // ── Payroll run/export cannot cross the boundary ───────────────────────────

    [Fact]
    public async Task CompanyA_Payroll_CannotLoadOrExport_CompanyB_RunOrSlips()
    {
        var w = await SeedWorld();
        await using var db = _fx.CreateDbWithAccessor(Accessor(ScopedHr(w.TenantId, w.CompanyA)));

        (await db.PayrollRuns.FirstOrDefaultAsync(r => r.Id == w.RunB))
            .Should().BeNull("the register export endpoint 404s before any CSV is built");
        (await db.PayrollSlips.Where(s => s.TenantId == w.TenantId).ToListAsync())
            .Should().BeEmpty("no sibling-company salary rows can reach an export payload (spec 8)");
    }

    // ── Sensitive employee update cannot cross the boundary ────────────────────

    [Fact]
    public async Task CompanyA_User_CannotReadOrUpdate_CompanyB_EmployeeBankFields()
    {
        var w = await SeedWorld();
        await using var db = _fx.CreateDbWithAccessor(Accessor(ScopedHr(w.TenantId, w.CompanyA)));

        (await db.Employees.FirstOrDefaultAsync(e => e.Id == w.EmpB))
            .Should().BeNull("update endpoints load-then-mutate — the load already fails");

        // Even a forged direct write (attach without loading) is stopped by write stamping.
        var forged = new EmployeeLoan { TenantId = w.TenantId, CompanyId = w.CompanyB, EmployeeIntId = w.EmpB, EmployeeId = Guid.NewGuid(), LoanTypeId = Guid.NewGuid(), LoanNumber = $"LN-{Guid.NewGuid():N}" };
        db.EmployeeLoans.Add(forged);
        var act = () => db.SaveChangesAsync();
        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*company_scope_denied*");
    }

    // ── Selected-companies grant cannot operate outside the selection ──────────

    [Fact]
    public async Task SelectedCompaniesGrant_CannotOperateOutsideSelection()
    {
        var w = await SeedWorld();
        // Grant covers A only; B exists in the same tenant.
        await using var db = _fx.CreateDbWithAccessor(Accessor(ScopedHr(w.TenantId, w.CompanyA)));

        (await db.Employees.Where(e => e.TenantId == w.TenantId).Select(e => e.CompanyId).Distinct().ToListAsync())
            .Should().BeEquivalentTo(new Guid?[] { w.CompanyA });
    }

    // ── DataScopeService pins the employee universe to accessible companies ────

    [Fact]
    public async Task DataScope_OrgLevelScopedUser_IsMaterializedToOwnCompanyEmployees()
    {
        var w = await SeedWorld();
        await using var db = _fx.CreateDbWithAccessor(Accessor(ScopedHr(w.TenantId, w.CompanyA)));
        var scopeService = new DataScopeService(db);

        var scope = await scopeService.ResolveAsync(ScopedHr(w.TenantId, w.CompanyA), w.TenantId, CancellationToken.None);

        scope.IsUnrestricted.Should().BeFalse(
            "org-wide permission + company scope must materialize an explicit id set, not 'unrestricted'");
        scope.CanAccessEmployee(w.EmpA).Should().BeTrue();
        scope.CanAccessEmployee(w.EmpB).Should().BeFalse("DataScope must not cross the company boundary (spec 6)");
    }

    [Fact]
    public async Task DataScope_GroupScopeUser_RemainsUnrestricted()
    {
        var w = await SeedWorld();
        await using var db = _fx.CreateDbWithAccessor(Accessor(GroupHr(w.TenantId)));
        var scopeService = new DataScopeService(db);

        var scope = await scopeService.ResolveAsync(GroupHr(w.TenantId), w.TenantId, CancellationToken.None);

        scope.IsUnrestricted.Should().BeTrue("group-scope admins keep tenant-wide employee visibility");
    }

    // ── The whole matrix holds under global StrictMode (spec 11) ────────────────

    [Fact]
    public async Task StrictMode_V2Tokens_Work_And_ClaimlessTokensSeeNothing()
    {
        var w = await SeedWorld();

        // v2 scoped token behaves identically under strict rules (per-token marker used
        // here as the strict trigger — same code path as the global flag).
        var strictScoped = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", w.TenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(EntityScopeContext.StrictScopeClaim, "true"),
            new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { w.CompanyA } })),
        }, "Test"));
        await using var db = _fx.CreateDbWithAccessor(Accessor(strictScoped));
        (await db.Employees.Where(e => e.TenantId == w.TenantId).ToListAsync())
            .Should().OnlyContain(e => e.CompanyId == w.CompanyA);

        // Claims-stripped token under strict rules sees no company-scoped data at all.
        var stripped = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", w.TenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(EntityScopeContext.StrictScopeClaim, "true"),
        }, "Test"));
        await using var db2 = _fx.CreateDbWithAccessor(Accessor(stripped));
        (await db2.Employees.Where(e => e.TenantId == w.TenantId).ToListAsync())
            .Should().BeEmpty("no silent fail-open path may exist for company-sensitive data");
    }

    // ── Harness ────────────────────────────────────────────────────────────────

    private static ClaimsPrincipal ScopedHr(Guid tenantId, Guid companyId) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim("permission", "employees.read"),
            new Claim("permission", "employees.write"),
            new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { companyId } })),
        }, "Test"));

    private static ClaimsPrincipal GroupHr(Guid tenantId) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim("permission", "employees.read"),
            new Claim("permission", "employees.write"),
            new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
        }, "Test"));

    private static IHttpContextAccessor Accessor(ClaimsPrincipal principal) =>
        new FixedAccessor { HttpContext = new DefaultHttpContext { User = principal } };

    private sealed class FixedAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
