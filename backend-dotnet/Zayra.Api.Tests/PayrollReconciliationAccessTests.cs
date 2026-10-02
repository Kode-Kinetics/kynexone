using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// F10 — the run mismatch report and the month-over-month reconciliation required
/// <c>payroll.review</c>, a permission that is not in the catalog, so every role (Admin included) got
/// 403 and the payroll workspace's Reconciliation tab could never load. Each role here carries the
/// permissions the real seeder gives it, so the test fails if the gate and the catalog drift apart
/// again.
/// </summary>
public class PayrollReconciliationAccessTests
{
    private static async Task<(ZayraDbContext Db, Guid TenantId, Guid RunId, Dictionary<string, List<string>> RolePermissions)> SeedAsync()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Recon Tenant", Slug = $"recon-{Guid.NewGuid():N}"[..20] });
        await db.SaveChangesAsync();
        await new Zayra.Api.Infrastructure.Seed.AuthSeeder(db).EnsureTenantRolesAsync(tenantId, CancellationToken.None);
        var rolePermissions = (await db.Roles.AsNoTracking()
                .Where(r => r.TenantId == tenantId)
                .Select(r => new { r.Name, Keys = r.RolePermissions.Select(rp => rp.Permission!.Key).ToList() })
                .ToListAsync())
            .ToDictionary(r => r.Name, r => r.Keys);

        var prior = new PayrollRun { TenantId = tenantId, Year = 2026, Month = 8, Status = "Locked", RunType = PayrollRunTypes.Regular };
        var current = new PayrollRun { TenantId = tenantId, Year = 2026, Month = 9, Status = "Locked", RunType = PayrollRunTypes.Regular };
        db.PayrollRuns.AddRange(prior, current);
        PayrollSlip Slip(Guid runId, int employeeId, decimal gross) => new()
        {
            TenantId = tenantId, RunId = runId, EmployeeId = employeeId, EmployeeCode = $"E{employeeId}",
            EmployeeName = $"Employee {employeeId}", BasicSalary = gross, GrossSalary = gross, NetSalary = gross,
        };
        db.PayrollSlips.AddRange(Slip(prior.Id, 1, 10_000m), Slip(current.Id, 1, 11_000m), Slip(current.Id, 2, 8_000m));
        await db.SaveChangesAsync();
        return (db, tenantId, current.Id, rolePermissions);
    }

    private static PayrollController ControllerFor(ZayraDbContext db, Guid tenantId, string role, IEnumerable<string> permissions)
    {
        var rules = PayComponentNetPayDefectTests.KsaRules();
        var ctrl = new PayrollController(
            db, new DataScopeService(db), new HttpContextAccessor(),
            new F2NullNotifications(), new KsaTestPackResolver(rules), rules,
            new F2NullLetters(), new NullDocumentStorage(), new Zayra.Api.Infrastructure.Documents.PdfRenderGate(8));
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, role),
        };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
        };
        return ctrl;
    }

    /// <summary>
    /// Every role the seeder gives payroll.read — the permission that already admits a caller to the
    /// run's slips and register these reports are derived from.
    /// </summary>
    [Theory]
    [InlineData("Admin")]
    [InlineData("Payroll Manager")]
    [InlineData("Payroll Officer")]
    [InlineData("HR Manager")]
    [InlineData("HR Director")]
    [InlineData("Finance Approver")]
    [InlineData("Auditor")]
    public async Task PayrollReaders_CanOpenBothReports(string role)
    {
        var (db, tenantId, runId, roles) = await SeedAsync();
        await using var _ = db;

        var reconciliation = await ControllerFor(db, tenantId, role, roles[role]).Reconciliation(runId, CancellationToken.None);
        var mismatch = await ControllerFor(db, tenantId, role, roles[role]).MismatchReport(runId, CancellationToken.None);

        reconciliation.Should().BeOfType<OkObjectResult>($"{role} holds payroll.read");
        mismatch.Should().BeOfType<OkObjectResult>();
        var body = System.Text.Json.JsonSerializer.Serialize(((OkObjectResult)reconciliation).Value);
        body.Should().Contain("\"joinerCount\":1").And.Contain("\"currentTotalGross\":19000");
    }

    /// <summary>Roles with no payroll access at all.</summary>
    [Theory]
    [InlineData("HR Officer")]
    [InlineData("Manager")]
    [InlineData("Employee")]
    [InlineData("Recruiter")]
    [InlineData("Compliance Officer")]
    public async Task RolesWithoutPayrollAccess_AreRefused(string role)
    {
        var (db, tenantId, runId, roles) = await SeedAsync();
        await using var _ = db;
        roles[role].Should().NotContain("payroll.read", "fixture sanity: this role has no payroll access");

        (await ControllerFor(db, tenantId, role, roles[role]).Reconciliation(runId, CancellationToken.None))
            .Should().BeOfType<ForbidResult>();
        (await ControllerFor(db, tenantId, role, roles[role]).MismatchReport(runId, CancellationToken.None))
            .Should().BeOfType<ForbidResult>();
    }

    /// <summary>
    /// Why payroll.read is the right gate, pinned: the run's slips and register — the rows these reports
    /// are built from — are already open to payroll.read through the permission-aware role handler, so
    /// the reports hand no one a figure they could not already read.
    /// </summary>
    [Theory]
    [InlineData("Slips")]
    [InlineData("Register")]
    [InlineData("ListEmployeeSalaryStructures")]
    public void TheRowsTheReportsAreBuiltFrom_AreAlreadyReadableWithPayrollRead(string action) =>
        Zayra.Api.Infrastructure.Authorization.LegacyRolePermissionResolver.Resolve("Payroll", action, new[] { "GET" })
            .Should().Be("payroll.read");

    /// <summary>The gate is only meaningful if the permissions it accepts can actually be granted.</summary>
    [Fact]
    public async Task ThePermissionsTheReportsAccept_ExistInTheCatalog()
    {
        var (db, _, _, _) = await SeedAsync();
        await using var __ = db;
        var catalog = await db.Permissions.AsNoTracking().Select(p => p.Key).ToListAsync();
        catalog.Should().Contain("payroll.read");
        catalog.Should().NotContain("payroll.review", "it was never a real permission");
    }
}
