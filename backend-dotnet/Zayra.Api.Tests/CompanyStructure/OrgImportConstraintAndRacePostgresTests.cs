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
using Zayra.Api.Models;

namespace Zayra.Api.Tests.CompanyStructure;

/// <summary>
/// A database constraint refusing row N of an import commit leaves nothing behind, and two imports creating the same
/// department code in one company end as one winner and a clean 409, never a 500.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class OrgImportConstraintAndRacePostgresTests(PostgresFixture fixture)
{
    private static ClaimsPrincipal Admin(Guid tenantId) => new(new ClaimsIdentity(new[]
    {
        new Claim("tenant_id", tenantId.ToString()),
        new Claim("sub", Guid.NewGuid().ToString()),
        new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        new Claim(ClaimTypes.Role, "Admin"),
        new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
    }, "Test"));

    private static OrganizationStructureImportController Controller(ZayraDbContext db, ClaimsPrincipal user) =>
        new(db, new AuditService(db)) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } } };

    private const string DeptHeader = "CompanyLegalName,BranchCode,Code,NameEn,ParentDepartmentCode,CostCenterCode,ApprovedHeadcount,MonthlyBudgetAmount,IsActive\n";

    private static OrganizationStructureImportRequest Departments(string legalName, params (string Code, string Name)[] rows) =>
        new(CompaniesCsv: null, BranchesCsv: null, CostCentersCsv: null,
            DepartmentsCsv: DeptHeader + string.Join("\n", rows.Select(r => $"{legalName},,{r.Code},{r.Name},,,10,1000,true")),
            GradesCsv: null, GradePayComponentsCsv: null, DesignationsCsv: null, PositionsCsv: null);

    private async Task<(Guid Tenant, string Legal)> TenantWithCompanyAsync()
    {
        await using var db = fixture.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(db);
        var legal = "Constraint Legal " + Guid.NewGuid().ToString("N")[..6];
        db.Companies.Add(new Company { TenantId = tenant, LegalNameEn = legal, CountryCode = "SA", DefaultCurrency = "SAR", IsActive = true });
        await db.SaveChangesAsync();
        return (tenant, legal);
    }

    private async Task CreateTriggerAsync(string name, Guid tenant, string body, string? extraWhen = null)
    {
        await using var ddl = fixture.CreateDb();
        await ddl.Database.ExecuteSqlRawAsync(
            $"CREATE FUNCTION {name}() RETURNS trigger AS $$ BEGIN {body} RETURN NEW; END $$ LANGUAGE plpgsql; " +
            $"CREATE TRIGGER {name} BEFORE INSERT ON departments FOR EACH ROW WHEN (NEW.tenant_id = '{tenant}'{extraWhen}) EXECUTE FUNCTION {name}();");
    }

    private async Task DropTriggerAsync(string name)
    {
        await using var ddl = fixture.CreateDb();
        await ddl.Database.ExecuteSqlRawAsync($"DROP TRIGGER IF EXISTS {name} ON departments; DROP FUNCTION IF EXISTS {name}();");
    }

    [Fact]
    public async Task AConstraintViolationOnTheSecondRow_LeavesZeroNewRowsAndNoAuditTrail_AndIsAConflict()
    {
        var (tenant, legal) = await TenantWithCompanyAsync();
        var trigger = $"trg_unique_dept_{tenant:N}";
        // Row 2 meets a unique violation (what a rival writer committing first looks like); row 1 was already inserted in the same transaction.
        await CreateTriggerAsync(trigger, tenant, "RAISE EXCEPTION 'duplicate key value violates unique constraint' USING ERRCODE = '23505';", " AND NEW.code = 'ROWTWO'");
        ActionResult<OrganizationStructureImportResult> outcome;
        try
        {
            await using var db = fixture.CreateRetryingDb();
            outcome = await Controller(db, Admin(tenant)).Commit(Departments(legal, ("ROWONE", "First"), ("ROWTWO", "Second")), default);
        }
        finally { await DropTriggerAsync(trigger); }

        (outcome.Result as ObjectResult)?.StatusCode.Should().Be(409, "a unique violation at commit time is a conflict, not an unhandled database error");
        await using var verify = fixture.CreateDb();
        (await verify.Departments.IgnoreQueryFilters().CountAsync(d => d.TenantId == tenant)).Should().Be(0, "row 1 was rolled back with row 2");
        (await verify.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.TenantId == tenant && (a.Action.StartsWith("organization.") || a.Action.StartsWith("setup."))))
            .Should().Be(0, "no import trail without the import");
    }

    [Fact]
    public async Task TwoParallelImportsCreatingTheSameDepartmentCode_YieldOneWinner_AndTheLoserIsA409()
    {
        var (tenant, legal) = await TenantWithCompanyAsync();
        var trigger = $"trg_slow_dept_{tenant:N}";
        // Hold each insert open long enough that both imports have validated (seen no existing row) before either commits.
        await CreateTriggerAsync(trigger, tenant, "PERFORM pg_sleep(0.7);");
        ActionResult<OrganizationStructureImportResult>[] results;
        try
        {
            var start = new TaskCompletionSource();
            var tasks = new[] { "Alpha", "Beta" }.Select(async name =>
            {
                await start.Task;
                await using var db = fixture.CreateRetryingDb();
                return await Controller(db, Admin(tenant)).Commit(Departments(legal, ("RACEDEP", name)), default);
            }).ToList();
            start.SetResult();
            results = await Task.WhenAll(tasks);
        }
        finally { await DropTriggerAsync(trigger); }

        results.Count(r => r.Result is OkObjectResult).Should().Be(1, "exactly one import creates the department");
        results.Single(r => r.Result is not OkObjectResult).Result.Should().BeOfType<ConflictObjectResult>("the loser is told the code is taken");
        await using var verify = fixture.CreateDb();
        (await verify.Departments.IgnoreQueryFilters().CountAsync(d => d.TenantId == tenant && d.Code == "RACEDEP")).Should().Be(1);
    }
}
