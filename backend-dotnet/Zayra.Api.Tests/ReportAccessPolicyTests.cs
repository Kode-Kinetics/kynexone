using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Reports;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Reports;
using Zayra.Api.Infrastructure.Seed;

namespace Zayra.Api.Tests;

/// <summary>
/// Guards on <see cref="ReportAccessPolicy"/> itself: that it covers exactly the reports that exist, that
/// its scope rule cannot drift from <see cref="DataScopeService"/>'s, and what it means for each seeded role.
/// </summary>
public sealed class ReportAccessPolicyTests
{
    [Fact]
    public void EveryCatalogReport_HasADataRule_AndEveryRuleIsInTheCatalog()
    {
        using var db = Db();
        // Every permission the policy mentions, so the catalog filter hides nothing.
        var everything = ReportAccessPolicy.Keys.SelectMany(ReportAccessPolicy.AcceptedPermissions).Distinct()
            .Append("reports.read").ToArray();
        var catalog = JsonSerializer.SerializeToElement(
                Assert.IsType<OkObjectResult>(Reports(db, Guid.NewGuid(), everything).GetCatalog()).Value)
            .EnumerateArray().Select(x => x.GetProperty("key").GetString()!).ToHashSet();

        Assert.Equal(ReportAccessPolicy.Keys.OrderBy(k => k), catalog.OrderBy(k => k));
    }

    [Fact]
    public void EverySchedulableReport_HasADataRule()
    {
        // The worker refuses a key the policy does not know, so a schedulable key without a rule would be
        // a schedule that fails on every run.
        Assert.All(ReportSchedulePolicy.ReportKeys, key => Assert.True(ReportAccessPolicy.IsKnown(key), key));
    }

    [Fact]
    public async Task EveryReportWithARule_Runs_ForACallerHoldingItsData()
    {
        // A rule for a key the executor has no arm for would 404 at run time: the catalog would offer it,
        // and the worker would fail every delivery of it.
        await using var db = Db();
        var tid = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tid, Name = "All", Slug = $"all-{tid:N}" });
        await db.SaveChangesAsync();
        var everything = ReportAccessPolicy.Keys.SelectMany(ReportAccessPolicy.AcceptedPermissions).Distinct()
            .Concat(["reports.read", "employees.read"]).ToArray();
        var ctrl = Reports(db, tid, everything);

        foreach (var key in ReportAccessPolicy.Keys)
            Assert.IsType<OkObjectResult>(await ctrl.RunReport(new RunReportRequest(key, null), default));
    }

    [Theory]
    [InlineData("")]
    [InlineData("employees.read")]
    [InlineData("employees.write")]
    [InlineData("employees.read,manager.read")]
    [InlineData("employees.write,manager.read")]
    [InlineData("payroll.read")]
    [InlineData("manager.read")]
    public async Task OrganisationScopeRule_AgreesWithDataScopeService(string permissionList)
    {
        // The worker and the analytics endpoints decide "organisation-wide or not" from permissions alone;
        // the interactive reports ask DataScopeService. The two must give the same answer.
        var permissions = Split(permissionList).ToArray();
        await using var db = Db();
        var tid = Guid.NewGuid();
        var claims = new List<Claim> { new("tenant_id", tid.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        var scope = await new DataScopeService(db).ResolveAsync(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")), tid, default);

        Assert.Equal(
            scope.Level == DataScopeLevel.Organization,
            ReportAccessPolicy.GrantsOrganisationScope(p => permissions.Contains(p)));
    }

    /// <summary>
    /// The final effect on every seeded role that holds reports.read, run through the controller with the
    /// role's real permission bundle AND its role name, exactly as its token carries them.
    ///
    /// <para>A report now admits at least everyone its module admits — the module's permission, its role
    /// list, or (attendance, leave, overtime, employee documents) any caller, filtered to their employee
    /// scope, because those module lists have no permission gate at all. What is still denied below is
    /// denied by the module too: each entry names the module gate that refuses the same role.</para>
    /// </summary>
    [Theory]
    [InlineData("HR Director", "")]
    [InlineData("HR Manager", "")]
    // Recruitment reports: role-gated Admin/HR Manager/HR Officer/Recruiter, no recruitment.read.
    // Visa/passport/contract: VisaTracking and Contracts are Admin/HR Manager/HR Officer, no compliance.read.
    // Qiwa and Saudization: the Qiwa and Saudi-compliance modules need qiwa.read or compliance.read.
    [InlineData("Payroll Manager", "recruitment.pipeline,recruitment.time-to-hire,compliance.visa-expiry,compliance.passport-expiry,compliance.contract-expiry,qiwa.readiness,compliance.saudization")]
    [InlineData("Payroll Officer", "recruitment.pipeline,recruitment.time-to-hire,compliance.visa-expiry,compliance.passport-expiry,compliance.contract-expiry,qiwa.readiness,compliance.saudization")]
    // Payroll: Admin/HR Manager/Payroll Manager/Payroll Officer or payroll.read. Loans: loans.read or
    // loans.write. Bonus batches: payroll.read. Recruitment: as above.
    [InlineData("Compliance Officer", "payroll.register,payroll.summary,payroll.slips,finance.bonus-payout,finance.loan-balance,finance.advance-report,recruitment.pipeline,recruitment.time-to-hire")]
    [InlineData("Auditor", "finance.loan-balance,finance.advance-report,recruitment.pipeline,recruitment.time-to-hire")]
    public async Task SeededRoles_RunEveryReportTheirModulesAllow_AndOnlyThose(string role, string denied)
    {
        await using var db = Db();
        var tid = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tid, Name = "Roles", Slug = $"roles-{tid:N}" });
        await db.SaveChangesAsync();
        await new AuthSeeder(db).EnsureTenantRolesAsync(tid);
        var permissions = await db.Roles.Where(r => r.TenantId == tid && r.Name == role)
            .SelectMany(r => r.RolePermissions.Select(rp => rp.Permission!.Key)).ToListAsync();
        Assert.Contains("reports.read", permissions);
        var ctrl = ReportsAs(db, tid, role, permissions.ToArray());
        var deniedKeys = Split(denied).ToHashSet();

        foreach (var key in ReportAccessPolicy.Keys)
        {
            var result = await ctrl.RunReport(new RunReportRequest(key, null), default);
            if (deniedKeys.Contains(key))
                ReportDataAuthorizationTests.AssertForbiddenWithReason(result, "role");
            else
                Assert.True(result is OkObjectResult, $"{role} should run {key}, got {result.GetType().Name}");
        }
    }

    [Fact]
    public async Task AReportAdmitsTheModulesRoleList_NotOnlyItsPermission()
    {
        // HR Manager holds no recruitment.read, yet the recruitment reports module admits the role by name.
        await using var db = Db();
        var tid = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tid, Name = "Role", Slug = $"role-{tid:N}" });
        await db.SaveChangesAsync();
        var ctrl = ReportsAs(db, tid, "HR Manager", "reports.read", "employees.read", "employees.write");

        Assert.IsType<OkObjectResult>(await ctrl.RunReport(new RunReportRequest("recruitment.pipeline", null), default));
    }

    [Theory]
    [InlineData("Finance Approver")]
    [InlineData("Manager")]
    [InlineData("Recruiter")]
    [InlineData("Employee")]
    public async Task RolesWithoutReportsRead_AreUnchanged(string role)
    {
        // The data rules only ever narrow. No seeded role gained reports.read in this change.
        await using var db = Db();
        var tid = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tid, Name = "Roles", Slug = $"roles-{tid:N}" });
        await db.SaveChangesAsync();
        await new AuthSeeder(db).EnsureTenantRolesAsync(tid);

        var keys = await db.Roles.Where(r => r.TenantId == tid && r.Name == role)
            .SelectMany(r => r.RolePermissions.Select(rp => rp.Permission!.Key)).ToListAsync();

        Assert.NotEmpty(keys);
        Assert.DoesNotContain("reports.read", keys);
    }

    private static IEnumerable<string> Split(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static ZayraDbContext Db() => new(
        new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ReportsController Reports(ZayraDbContext db, Guid tid, params string[] permissions) =>
        ReportsAs(db, tid, null, permissions);

    private static ReportsController ReportsAs(ZayraDbContext db, Guid tid, string? role, params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tid.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "Policy Tester"),
        };
        if (role is not null) claims.Add(new Claim(ClaimTypes.Role, role));
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        return new ReportsController(db, new DataScopeService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
            },
        };
    }
}
