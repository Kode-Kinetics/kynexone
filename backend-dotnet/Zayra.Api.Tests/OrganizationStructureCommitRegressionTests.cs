using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
namespace Zayra.Api.Tests;

[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class OrganizationStructureCommitRegressionTests
{
    private readonly PostgresFixture _fx;
    public OrganizationStructureCommitRegressionTests(PostgresFixture fx) => _fx = fx;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HierarchyAndSharedComponentCodes_PersistAndReimportWithoutDuplicates(bool parentFirst)
    {
        await using var db = _fx.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(db);
        var c = new OrganizationStructureImportController(db, new AuditService(db));
        c.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] {
            new Claim("tenant_id", tenant.ToString()), new Claim("sub", Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, "Admin"),
            new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() }))
        }, "Test")) } };
        var parent = "EXEC,Executive,\n";
        var child = "FIN,Finance,EXEC\n";
        var req = new OrganizationStructureImportRequest(
            CompaniesCsv: null, BranchesCsv: null, CostCentersCsv: null,
            DepartmentsCsv: "Code,NameEn,ParentDepartmentCode\n" + (parentFirst ? parent + child : child + parent),
            GradesCsv: "Code,Name,MinSalary,MidSalary,MaxSalary,Currency\nG1,Grade 1,5000,7500,10000,SAR\nG2,Grade 2,10000,15000,20000,SAR\n",
            GradePayComponentsCsv: "GradeCode,ComponentCode,ComponentName,CalculationType,Amount\nG1,BASIC,Basic Salary,Fixed,7500\nG2,BASIC,Basic Salary,Fixed,15000\n",
            DesignationsCsv: null);
        var first = await c.Commit(req, CancellationToken.None);
        first.Result.Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        var departments = await db.Departments.Where(x => x.TenantId == tenant).ToListAsync();
        departments.Should().HaveCount(2);
        departments.Single(x => x.Code == "FIN").ParentDepartmentId.Should().Be(departments.Single(x => x.Code == "EXEC").Id);
        var components = await db.GradePayScaleComponents.Where(x => x.TenantId == tenant).ToListAsync();
        components.Should().HaveCount(2);
        components.Select(x => x.GradeId).Distinct().Should().HaveCount(2);
        components.Sum(x => x.Amount).Should().Be(22500m);
        var again = await c.Commit(req, CancellationToken.None);
        again.Result.Should().BeOfType<OkObjectResult>();
        (await db.Departments.CountAsync(x => x.TenantId == tenant)).Should().Be(2);
        (await db.GradePayScaleComponents.CountAsync(x => x.TenantId == tenant)).Should().Be(2);
        (await db.AuditLogs.AnyAsync(x => x.TenantId == tenant && x.Action == "setup.organization_structure_import_committed")).Should().BeTrue();
    }
}
