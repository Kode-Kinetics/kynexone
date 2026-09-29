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
    /// What the data rules mean for the roles a new tenant is provisioned with. This is the record of the
    /// narrowing: before, every role holding reports.read could run every report.
    /// </summary>
    [Theory]
    [InlineData("HR Director", "", "")]
    [InlineData("HR Manager", "hr.headcount,attendance.daily,leave.balance,overtime.requests,payroll.register,compliance.passport-expiry,qiwa.readiness",
        "recruitment.pipeline,finance.loan-balance")]
    [InlineData("Payroll Manager", "hr.headcount,attendance.daily,leave.balance,overtime.requests,payroll.register,finance.loan-balance,finance.bonus-payout",
        "compliance.passport-expiry,recruitment.pipeline,qiwa.readiness")]
    [InlineData("Payroll Officer", "hr.headcount,attendance.daily,payroll.register,finance.loan-balance",
        "leave.balance,overtime.requests,compliance.passport-expiry,recruitment.pipeline")]
    [InlineData("Compliance Officer", "hr.headcount,compliance.passport-expiry,compliance.document-compliance,qiwa.readiness,compliance.saudization",
        "attendance.daily,leave.balance,overtime.requests,payroll.register,finance.loan-balance,recruitment.pipeline")]
    [InlineData("Auditor", "hr.headcount,attendance.daily,leave.balance,payroll.register,compliance.passport-expiry,qiwa.readiness",
        "overtime.requests,recruitment.pipeline,finance.loan-balance")]
    public async Task SeededRoles_CanRunTheReportsForTheirOwnData_AndNoOthers(string role, string allowed, string denied)
    {
        await using var db = Db();
        var tid = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tid, Name = "Roles", Slug = $"roles-{tid:N}" });
        await db.SaveChangesAsync();
        await new AuthSeeder(db).EnsureTenantRolesAsync(tid);
        var keys = await db.Roles.Where(r => r.TenantId == tid && r.Name == role)
            .SelectMany(r => r.RolePermissions.Select(rp => rp.Permission!.Key)).ToListAsync();
        bool Has(string p) => keys.Contains(p, StringComparer.OrdinalIgnoreCase);

        Assert.True(Has("reports.read"), $"{role} is expected to hold reports.read");
        if (role == "HR Director")
            Assert.All(ReportAccessPolicy.Keys, k => Assert.True(ReportAccessPolicy.CanAccess(k, Has), k));
        foreach (var key in Split(allowed)) Assert.True(ReportAccessPolicy.CanAccess(key, Has), $"{role} should run {key}");
        foreach (var key in Split(denied)) Assert.False(ReportAccessPolicy.CanAccess(key, Has), $"{role} should not run {key}");
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

    private static ReportsController Reports(ZayraDbContext db, Guid tid, params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tid.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "Policy Tester"),
        };
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
