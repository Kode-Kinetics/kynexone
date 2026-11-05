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
/// The organisation-structure import: a package has a size, a stored batch is read only by its creator's scope, one batch is applied
/// once however many callers press Commit, and a commit that fails part-way leaves nothing behind.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class OrgStructureImportHardeningPostgresTests(PostgresFixture fixture)
{
    private static string Grade(string code) =>
        $"Code,Name,Band,Level,MinSalary,MidSalary,MaxSalary,Currency,IsActive\n{code},Grade {code},B,3,1000,2000,3000,SAR,true\n";

    private static OrganizationStructureImportRequest Package(string? grades = null, string? companies = null, string? branches = null, string? departments = null) =>
        new(CompaniesCsv: companies, BranchesCsv: branches, CostCentersCsv: null, DepartmentsCsv: departments, GradesCsv: grades,
            GradePayComponentsCsv: null, DesignationsCsv: null, PositionsCsv: null);

    private static ClaimsPrincipal Principal(Guid tenantId, string role, params Guid[] companies) => new(new ClaimsIdentity(new[]
    {
        new Claim("tenant_id", tenantId.ToString()),
        new Claim("sub", Guid.NewGuid().ToString()),
        new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        new Claim(ClaimTypes.Role, role),
        new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(companies.Length == 0
            ? new { v = 2, m = "group", c = Array.Empty<Guid>() }
            : new { v = 2, m = "companies", c = companies })),
    }, "Test"));

    private static OrganizationStructureImportController Controller(ZayraDbContext db, ClaimsPrincipal user) =>
        new(db, new AuditService(db)) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } } };

    private async Task<Guid> TenantAsync()
    {
        await using var db = fixture.CreateDb();
        return await PostgresFixture.SeedMinimalTenant(db);
    }

    // ── size caps ────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task APackageWithTooManyRows_Is422_AndOneOverTheByteCap_Is413_OnEveryEntryPoint()
    {
        var tenant = await TenantAsync();
        await using var db = fixture.CreateDb();
        var user = Principal(tenant, "Admin");
        var tooManyRows = Package(grades: "Code,Name,Band,Level\n" + string.Join("\n", Enumerable.Repeat("G,Grade,B,1", 20_001)));
        var tooBig = Package(grades: "Code,Name,Band,Level\nG,Grade,B,1\n" + new string('#', 6_000_000));

        foreach (var (request, status) in new[] { (tooManyRows, 422), (tooBig, 413) })
        {
            var preview = await Controller(db, user).Preview(request, default);
            (preview.Result as ObjectResult)?.StatusCode.Should().Be(status, "preview");
            var dry = await Controller(db, user).CreateDryRunBatch(request, default);
            (dry.Result as ObjectResult)?.StatusCode.Should().Be(status, "dry run");
            var commit = await Controller(db, user).Commit(request, default);
            (commit.Result as ObjectResult)?.StatusCode.Should().Be(status, "commit");
        }
        (await db.MigrationImportBatches.CountAsync(b => b.TenantId == tenant)).Should().Be(0, "a refused package stores nothing");
    }

    // ── stored batches belong to their creator's scope ───────────────────────────────────────────
    [Fact]
    public async Task AStoredBatch_IsNotReadCommittedOrReusedByACallerOutsideItsScope()
    {
        var tenant = await TenantAsync();
        Guid company;
        await using (var seed = fixture.CreateDb())
        {
            var c = new Company { TenantId = tenant, LegalNameEn = "Scope Legal", CountryCode = "SA", DefaultCurrency = "SAR", IsActive = true };
            seed.Companies.Add(c);
            await seed.SaveChangesAsync();
            company = c.Id;
        }
        var request = Package(grades: Grade("SCP1"));
        await using var db = fixture.CreateDb();
        var dry = await Controller(db, Principal(tenant, "Admin")).CreateDryRunBatch(request, default);
        var batch = ((MigrationImportBatchDto)((OkObjectResult)dry.Result!).Value!);

        var scoped = Principal(tenant, "HR Manager", company);
        (await Controller(db, scoped).GetBatch(batch.Id, default)).Result.Should().BeOfType<NotFoundResult>();
        (await Controller(db, scoped).CommitBatch(batch.Id, default)).Result.Should().BeOfType<NotFoundResult>();
        (await Controller(db, Principal(tenant, "Admin")).GetBatch(batch.Id, default)).Result.Should().BeOfType<OkObjectResult>("a group-scope caller reads the tenant's batches");

        var theirs = await Controller(db, scoped).CreateDryRunBatch(request, default);
        var theirDto = (MigrationImportBatchDto)((OkObjectResult)theirs.Result!).Value!;
        theirDto.Id.Should().NotBe(batch.Id, "the same package from another scope is its own batch, not a reuse of the group's");

        await using var verify = fixture.CreateDb();
        (await verify.MigrationImportBatches.AsNoTracking().SingleAsync(b => b.Id == batch.Id)).Status.Should().Be("DryRunPassed");
    }

    // ── one batch, applied once ──────────────────────────────────────────────────────────────────
    [Fact]
    public async Task ParallelCommitsOfOneBatch_ApplyItOnce_AndNobodyGetsAnError()
    {
        var tenant = await TenantAsync();
        var admin = Principal(tenant, "Admin");
        Guid batchId;
        await using (var db = fixture.CreateRetryingDb())
        {
            var dry = await Controller(db, admin).CreateDryRunBatch(Package(grades: Grade("PAR1")), default);
            batchId = ((MigrationImportBatchDto)((OkObjectResult)dry.Result!).Value!).Id;
        }

        var start = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 6).Select(async _ =>
        {
            await start.Task;
            await using var db = fixture.CreateRetryingDb();
            return (await Controller(db, admin).CommitBatch(batchId, default)).Result;
        }).ToList();
        start.SetResult();
        var results = await Task.WhenAll(tasks);

        results.Should().OnlyContain(r => r is OkObjectResult || r is ConflictObjectResult, "a caller who loses the claim is told it is already being applied");
        await using var verify = fixture.CreateDb();
        (await verify.AuditLogs.CountAsync(a => a.TenantId == tenant && a.Action == "setup.organization_structure_import_committed")).Should().Be(1, "applied once");
        (await verify.Grades.CountAsync(g => g.TenantId == tenant && g.Code == "PAR1")).Should().Be(1);
        (await verify.MigrationImportBatches.AsNoTracking().SingleAsync(b => b.Id == batchId)).Status.Should().Be("Committed");
    }

    [Fact]
    public async Task ABatchThatIsBeingCommitted_IsAConflict_NotRunAgain()
    {
        var tenant = await TenantAsync();
        var admin = Principal(tenant, "Admin");
        await using var db = fixture.CreateRetryingDb();
        var dry = await Controller(db, admin).CreateDryRunBatch(Package(grades: Grade("BUSY1")), default);
        var id = ((MigrationImportBatchDto)((OkObjectResult)dry.Result!).Value!).Id;
        await db.MigrationImportBatches.Where(b => b.Id == id).ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, "Committing"));

        (await Controller(fixture.CreateRetryingDb(), admin).CommitBatch(id, default)).Result.Should().BeOfType<ConflictObjectResult>();
        await using var verify = fixture.CreateDb();
        (await verify.Grades.CountAsync(g => g.TenantId == tenant && g.Code == "BUSY1")).Should().Be(0);
    }

    // ── a failure part-way leaves nothing ────────────────────────────────────────────────────────
    [Fact]
    public async Task ACommitThatFailsMidway_LeavesNoNewRows_AndTheBatchIsFailedNotStuck()
    {
        var tenant = await TenantAsync();
        var admin = Principal(tenant, "Admin");
        var trigger = $"trg_break_dept_{tenant:N}";
        var request = Package(
            companies: "LegalNameEn,TradeName,CountryCode,Jurisdiction,RegistrationNumber,TaxNumber,DefaultCurrency,IsActive\nAtomic Legal,Atomic,SA,SA,CR-AT,TAX-AT,SAR,true",
            branches: "CompanyLegalName,Code,NameEn,CountryCode,City,IsHeadOffice,IsActive\nAtomic Legal,HQ,Head Office,SA,Riyadh,true,true",
            departments: "CompanyLegalName,BranchCode,Code,NameEn,ParentDepartmentCode,CostCenterCode,ApprovedHeadcount,MonthlyBudgetAmount,IsActive\nAtomic Legal,HQ,HR,Human Resources,,,10,120000,true",
            grades: Grade("ATOM1"));

        Guid batchId;
        await using (var db = fixture.CreateRetryingDb())
        {
            var dry = await Controller(db, admin).CreateDryRunBatch(request, default);
            batchId = ((MigrationImportBatchDto)((OkObjectResult)dry.Result!).Value!).Id;
        }

        await using (var ddl = fixture.CreateDb())
            await ddl.Database.ExecuteSqlRawAsync(
                $"CREATE FUNCTION {trigger}() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'department insert blocked for test'; END $$ LANGUAGE plpgsql; " +
                $"CREATE TRIGGER {trigger} BEFORE INSERT ON departments FOR EACH ROW WHEN (NEW.tenant_id = '{tenant}') EXECUTE FUNCTION {trigger}();");
        try
        {
            await using var db = fixture.CreateRetryingDb();
            var outcome = await Record.ExceptionAsync(async () => await Controller(db, admin).CommitBatch(batchId, default));
            if (outcome is null) { /* answered with a failure response instead of throwing */ }
        }
        finally
        {
            await using var ddl = fixture.CreateDb();
            await ddl.Database.ExecuteSqlRawAsync($"DROP TRIGGER IF EXISTS {trigger} ON departments; DROP FUNCTION IF EXISTS {trigger}();");
        }

        await using var verify = fixture.CreateDb();
        (await verify.Companies.CountAsync(c => c.TenantId == tenant)).Should().Be(0);
        (await verify.Branches.CountAsync(b => b.TenantId == tenant)).Should().Be(0);
        (await verify.Grades.CountAsync(g => g.TenantId == tenant)).Should().Be(0);
        (await verify.MigrationImportBatches.AsNoTracking().SingleAsync(b => b.Id == batchId)).Status.Should().Be("Failed", "a failed apply is retryable from a fresh dry run, not stuck in Committing");
    }
}
