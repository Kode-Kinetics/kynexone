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

namespace Zayra.Api.Tests.CompanyStructure;

/// <summary>A stored organisation import batch is never left claimed forever, and a batch being committed is not reset by another dry run.</summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class OrgImportBatchLifecyclePostgresTests(PostgresFixture fixture)
{
    private static ClaimsPrincipal Admin(Guid tenantId, Guid userId) => new(new ClaimsIdentity(new[]
    {
        new Claim("tenant_id", tenantId.ToString()),
        new Claim("sub", userId.ToString()),
        new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
        new Claim(ClaimTypes.Role, "Admin"),
        new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
    }, "Test"));

    private static OrganizationStructureImportController Controller(ZayraDbContext db, ClaimsPrincipal user) =>
        new(db, new AuditService(db)) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } } };

    private static OrganizationStructureImportRequest Package(string gradeCode) =>
        new(CompaniesCsv: null, BranchesCsv: null, CostCentersCsv: null, DepartmentsCsv: null,
            GradesCsv: $"Code,Name,Band,Level,MinSalary,MidSalary,MaxSalary,Currency,IsActive\n{gradeCode},Grade {gradeCode},B,3,1000,2000,3000,SAR,true\n",
            GradePayComponentsCsv: null, DesignationsCsv: null, PositionsCsv: null);

    private async Task<(Guid Tenant, ClaimsPrincipal User, Guid BatchId)> DryRunAsync(string gradeCode)
    {
        Guid tenant;
        await using (var db = fixture.CreateDb()) tenant = await PostgresFixture.SeedMinimalTenant(db);
        var user = Admin(tenant, Guid.NewGuid());
        await using var run = fixture.CreateRetryingDb();
        var dry = await Controller(run, user).CreateDryRunBatch(Package(gradeCode), default);
        return (tenant, user, ((MigrationImportBatchDto)((OkObjectResult)dry.Result!).Value!).Id);
    }

    private async Task<string> StatusAsync(Guid batchId)
    {
        await using var db = fixture.CreateDb();
        return (await db.MigrationImportBatches.AsNoTracking().SingleAsync(b => b.Id == batchId)).Status;
    }

    [Fact]
    public async Task AFailureAfterTheDataWasApplied_LeavesTheBatchFailed_NeverStuckCommitting()
    {
        var (tenant, user, batchId) = await DryRunAsync("LATE1");
        var trigger = $"trg_late_{tenant:N}";
        await using (var ddl = fixture.CreateDb())
            await ddl.Database.ExecuteSqlRawAsync(
                $"CREATE FUNCTION {trigger}() RETURNS trigger AS $$ BEGIN IF NEW.status = 'Committed' THEN RAISE EXCEPTION 'batch record update blocked for test'; END IF; RETURN NEW; END $$ LANGUAGE plpgsql; " +
                $"CREATE TRIGGER {trigger} BEFORE UPDATE ON migration_import_batches FOR EACH ROW WHEN (NEW.tenant_id = '{tenant}') EXECUTE FUNCTION {trigger}();");
        Exception? thrown;
        try
        {
            await using var db = fixture.CreateRetryingDb();
            thrown = await Record.ExceptionAsync(async () => await Controller(db, user).CommitBatch(batchId, default));
        }
        finally
        {
            await using var ddl = fixture.CreateDb();
            await ddl.Database.ExecuteSqlRawAsync($"DROP TRIGGER IF EXISTS {trigger} ON migration_import_batches; DROP FUNCTION IF EXISTS {trigger}();");
        }

        thrown.Should().NotBeNull("the final record update was refused");
        (await StatusAsync(batchId)).Should().Be("Failed", "a later step failing must not leave the batch claimed for ever");
    }

    [Fact]
    public async Task ASecondDryRunOfABatchBeingCommitted_IsRefused_AndDoesNotResetIt()
    {
        var (tenant, user, batchId) = await DryRunAsync("BUSY2");
        await using (var db = fixture.CreateDb())
            await db.MigrationImportBatches.Where(b => b.Id == batchId).ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, "Committing").SetProperty(b => b.UpdatedAtUtc, DateTime.UtcNow));

        await using var run = fixture.CreateRetryingDb();
        var again = await Controller(run, user).CreateDryRunBatch(Package("BUSY2"), default);
        again.Result.Should().BeOfType<ConflictObjectResult>();
        (await StatusAsync(batchId)).Should().Be("Committing", "so a second commit cannot be claimed while the first runs");
    }

    [Fact]
    public async Task ACommittingBatchThatHasGoneStale_IsMarkedFailed_AndAFreshDryRunRecoversIt()
    {
        var (tenant, user, batchId) = await DryRunAsync("STALE3");
        await using (var db = fixture.CreateDb())
            await db.MigrationImportBatches.Where(b => b.Id == batchId)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, "Committing").SetProperty(b => b.UpdatedAtUtc, DateTime.UtcNow.AddHours(-3)));

        await using (var run = fixture.CreateRetryingDb())
            (await Controller(run, user).CommitBatch(batchId, default)).Result.Should().BeOfType<ConflictObjectResult>();
        (await StatusAsync(batchId)).Should().Be("Failed", "an abandoned claim is released, not honoured for ever");

        await using var again = fixture.CreateRetryingDb();
        var dry = await Controller(again, user).CreateDryRunBatch(Package("STALE3"), default);
        ((MigrationImportBatchDto)((OkObjectResult)dry.Result!).Value!).Status.Should().Be("DryRunPassed");
        var commit = await Controller(fixture.CreateRetryingDb(), user).CommitBatch(batchId, default);
        commit.Result.Should().BeOfType<OkObjectResult>();
        await using var verify = fixture.CreateDb();
        (await verify.Grades.CountAsync(g => g.TenantId == tenant && g.Code == "STALE3")).Should().Be(1);
    }

    [Fact]
    public async Task AStoredBatchThatLosesACodeRace_IsA409_AndTheBatchIsRecordedFailed()
    {
        Guid tenant; string legal;
        await using (var seed = fixture.CreateDb())
        {
            tenant = await PostgresFixture.SeedMinimalTenant(seed);
            legal = "Race Legal " + Guid.NewGuid().ToString("N")[..6];
            seed.Companies.Add(new Zayra.Api.Models.Company { TenantId = tenant, LegalNameEn = legal, CountryCode = "SA", DefaultCurrency = "SAR", IsActive = true });
            await seed.SaveChangesAsync();
        }
        var user = Admin(tenant, Guid.NewGuid());
        var pkg = new OrganizationStructureImportRequest(CompaniesCsv: null, BranchesCsv: null, CostCentersCsv: null,
            DepartmentsCsv: $"CompanyLegalName,BranchCode,Code,NameEn,ParentDepartmentCode,CostCenterCode,ApprovedHeadcount,MonthlyBudgetAmount,IsActive\n{legal},,RACEB,Race B,,,10,1000,true\n",
            GradesCsv: null, GradePayComponentsCsv: null, DesignationsCsv: null, PositionsCsv: null);
        Guid batchId;
        await using (var run = fixture.CreateRetryingDb())
            batchId = ((MigrationImportBatchDto)((OkObjectResult)(await Controller(run, user).CreateDryRunBatch(pkg, default)).Result!).Value!).Id;

        var trigger = $"trg_race_{tenant:N}";
        await using (var ddl = fixture.CreateDb())
            await ddl.Database.ExecuteSqlRawAsync(
                $"CREATE FUNCTION {trigger}() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'duplicate key value violates unique constraint' USING ERRCODE = '23505'; RETURN NEW; END $$ LANGUAGE plpgsql; " +
                $"CREATE TRIGGER {trigger} BEFORE INSERT ON departments FOR EACH ROW WHEN (NEW.tenant_id = '{tenant}') EXECUTE FUNCTION {trigger}();");
        ActionResult<MigrationImportBatchDto> outcome;
        try
        {
            await using var db = fixture.CreateRetryingDb();
            outcome = await Controller(db, user).CommitBatch(batchId, default);
        }
        finally
        {
            await using var ddl = fixture.CreateDb();
            await ddl.Database.ExecuteSqlRawAsync($"DROP TRIGGER IF EXISTS {trigger} ON departments; DROP FUNCTION IF EXISTS {trigger}();");
        }

        outcome.Result.Should().BeOfType<ConflictObjectResult>("the lost race is the intended 409, not a second unique violation from the failed inserts being retried");
        (await StatusAsync(batchId)).Should().Be("Failed");
    }
}
