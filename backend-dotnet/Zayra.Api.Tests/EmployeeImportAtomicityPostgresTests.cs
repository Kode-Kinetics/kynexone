using System.Data.Common;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// F01 on REAL PostgreSQL (with production's retrying execution strategy): the employee import is ONE unit of
/// work. On main it committed at four separate SaveChanges boundaries, so a failure at any later one returned
/// 422 "import_persist_failed" with the people already committed — no salaries, no review gaps — and the
/// retry then duplicated them. Every test here asserts on persisted rows, never on a status code alone.
/// (Boundary-failure and 250-row shape adapted from the parallel WT session's EmployeeImportAtomicityPostgresTests.)
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class EmployeeImportAtomicityPostgresTests
{
    private readonly PostgresFixture _fixture;
    public EmployeeImportAtomicityPostgresTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>Fails the first SaveChanges that carries a new <paramref name="entityType"/>.</summary>
    private sealed class FailWhenSaving(Type entityType) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (eventData.Context!.ChangeTracker.Entries().Any(e => e.State == EntityState.Added && entityType.IsInstanceOfType(e.Entity)))
                throw new DbUpdateException("Injected import failure while saving " + entityType.Name);
            return base.SavingChangesAsync(eventData, result, ct);
        }
    }

    /// <summary>A transient network fault on the import's COMMIT — raised once, either AFTER the server has
    /// committed (the reply is lost) or BEFORE the commit is sent.</summary>
    private sealed class TransientCommitFault(bool afterCommit) : DbTransactionInterceptor
    {
        public int Faults;
        private static NpgsqlException Transient() =>
            new("Simulated connection loss during COMMIT", new System.IO.IOException("connection reset by peer"));

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken ct = default)
        {
            if (!afterCommit && Faults++ == 0) throw Transient();
            return base.TransactionCommittingAsync(transaction, eventData, result, ct);
        }

        public override async Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default)
        {
            await base.TransactionCommittedAsync(transaction, eventData, ct);
            if (afterCommit && Faults++ == 0) throw Transient();
        }
    }

    private ZayraDbContext Db(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseNpgsql(_fixture.ConnectionString, pg => pg.EnableRetryOnFailure())
        .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance)
        .AddInterceptors(interceptors)
        .Options);

    private async Task<Guid> SeedAsync()
    {
        await using var seed = _fixture.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(seed);
        seed.TenantSubscriptions.Add(new TenantSubscription { TenantId = tenant, Plan = "Enterprise", Status = "Active", MaxEmployees = 300 });
        seed.Companies.Add(new Company { TenantId = tenant, LegalNameEn = "Atomicity Demo", CountryCode = "SA", Jurisdiction = "test", RegistrationNumber = $"DEMO-{Guid.NewGuid():N}"[..20], DefaultCurrency = "SAR", IsActive = true });
        seed.Grades.Add(new Grade { TenantId = tenant, Code = "G1", Name = "Demo grade", Currency = "SAR", MinSalary = 1, MaxSalary = 50000, IsActive = true });
        await seed.SaveChangesAsync();
        return tenant;
    }

    /// <summary>A file that exercises every boundary: people, payroll profiles + salary structures (grade G1),
    /// manager links to the first row, and review gaps (the department does not exist).</summary>
    private static string File(int count, bool generatedCodes = false) =>
        "EmployeeCode,FullName,CompanyLegalName,Department,Grade,BasicSalary,Currency,JoiningDate,ManagerEmployeeCode,BankName\n"
        + string.Join("\n", Enumerable.Range(1, count).Select(i =>
            $"{(generatedCodes ? "" : $"AT{i:D4}")},Synthetic Atomic {i},Atomicity Demo,Unresolved Department,G1,{5000 + i},SAR,2024-01-01,{(i == 1 || generatedCodes ? "" : "AT0001")},Synthetic Bank"));

    private static Task<IActionResult> Import(ZayraDbContext db, Guid tenant, string csv, Guid? key = null) =>
        HrmHierarchyTests.BuildImportControllerInternal(db, tenant)
            .Import(new EmployeesController.ImportEmployeesRequest(csv, key), CancellationToken.None);

    private static JsonElement Body(IActionResult r) => JsonSerializer.SerializeToElement(((ObjectResult)r).Value);

    private async Task AssertNothingPersisted(Guid tenant)
    {
        await using var verify = _fixture.CreateDb();
        Assert.Equal(0, await verify.Employees.IgnoreQueryFilters().CountAsync(e => e.TenantId == tenant));
        Assert.Equal(0, await verify.EmployeePayrollProfiles.IgnoreQueryFilters().CountAsync(e => e.TenantId == tenant));
        Assert.Equal(0, await verify.EmployeeSalaryStructures.IgnoreQueryFilters().CountAsync(e => e.TenantId == tenant));
        Assert.Equal(0, await verify.SalaryStructures.IgnoreQueryFilters().CountAsync(e => e.TenantId == tenant));
        Assert.Equal(0, await verify.ReportingLines.IgnoreQueryFilters().CountAsync(e => e.TenantId == tenant));
        Assert.Equal(0, await verify.EmployeeImportGaps.IgnoreQueryFilters().CountAsync(e => e.TenantId == tenant));
        Assert.Equal(0, await verify.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.TenantId == tenant && a.EntityName == EmployeesController.ImportBatchEntityName));
    }

    private async Task AssertImportedOnce(Guid tenant, int count)
    {
        await using var verify = _fixture.CreateDb();
        Assert.Equal(count, await verify.Employees.IgnoreQueryFilters().CountAsync(e => e.TenantId == tenant));
        Assert.Equal(count, await verify.EmployeePayrollProfiles.IgnoreQueryFilters().CountAsync(e => e.TenantId == tenant));
        Assert.Equal(count, await verify.EmployeeSalaryStructures.IgnoreQueryFilters().CountAsync(e => e.TenantId == tenant));
        Assert.Equal(1, await verify.SalaryStructures.IgnoreQueryFilters().CountAsync(e => e.TenantId == tenant));
        Assert.Equal(1, await verify.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.TenantId == tenant && a.Action == EmployeesController.ImportCommittedAction));
    }

    // ── A failure at ANY save boundary persists nothing, and the retry imports each person exactly once ──

    [Theory]
    [InlineData(typeof(Employee), "employees", 3)]
    [InlineData(typeof(EmployeePayrollProfile), "payroll", 250)]
    [InlineData(typeof(EmployeeSalaryStructure), "payroll", 3)]
    [InlineData(typeof(ReportingLine), "links", 3)]
    [InlineData(typeof(EmployeeImportGap), "links", 3)]
    public async Task AFailureAtAnySaveBoundary_PersistsNothing_AndTheRetryImportsEachPersonOnce(Type failOn, string stage, int count)
    {
        var tenant = await SeedAsync();
        var csv = File(count);

        IActionResult response;
        await using (var failing = Db(new FailWhenSaving(failOn)))
            response = await Import(failing, tenant, csv);
        // Persisted state first: on main this 422 was returned with the people already committed.
        var refused = Assert.IsType<UnprocessableEntityObjectResult>(response);
        await AssertNothingPersisted(tenant);
        var body = Body(refused);
        Assert.Equal("import_persist_failed", body.GetProperty("error").GetString());
        Assert.Equal(stage, body.GetProperty("stage").GetString());
        Assert.Equal(0, body.GetProperty("created").GetInt32());
        Assert.Equal(count, body.GetProperty("failed").GetInt32());
        Assert.Contains("Nothing from this file was imported", body.GetProperty("message").GetString());

        await using var clean = Db();
        var ok = Assert.IsType<OkObjectResult>(await Import(clean, tenant, csv));
        var summary = Body(ok);
        Assert.Equal(count, summary.GetProperty("created").GetInt32());
        Assert.Equal(0, summary.GetProperty("repaired").GetInt32());
        Assert.Equal(0, summary.GetProperty("skipped").GetInt32());
        Assert.Equal(0, summary.GetProperty("failed").GetInt32());
        await AssertImportedOnce(tenant, count);
        await using var verify = _fixture.CreateDb();
        Assert.Equal(count - 1, await verify.Employees.IgnoreQueryFilters().CountAsync(e => e.TenantId == tenant && e.ManagerEmployeeId != null));
    }

    [Fact]
    public async Task AFailureWritingTheImportAudit_RollsBackTheWholeFile()
    {
        // The audit rows are written last, inside the same transaction — so they cannot fail on their own.
        var tenant = await SeedAsync();
        await using (var failing = Db(new FailWhenSaving(typeof(AuditLog))))
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => Import(failing, tenant, File(3)));
        await AssertNothingPersisted(tenant);
    }

    // ── A transient fault on COMMIT: never a second import of the same file ─────────────────────────

    [Fact]
    public async Task ACommitThatLandedButReportedATransientError_IsNotImportedTwice()
    {
        // The server committed; the reply was lost. The retrying strategy would re-run the whole file — every
        // auto-coded row a second time. It first checks the batch's marker row, finds it, and returns the
        // first attempt's summary.
        var tenant = await SeedAsync();
        var fault = new TransientCommitFault(afterCommit: true);
        await using (var db = Db(fault))
        {
            var ok = Assert.IsType<OkObjectResult>(await Import(db, tenant, File(4, generatedCodes: true)));
            Assert.Equal(4, Body(ok).GetProperty("created").GetInt32());
        }
        Assert.Equal(1, fault.Faults);
        await AssertImportedOnce(tenant, 4);
    }

    [Fact]
    public async Task ACommitThatNeverLanded_IsRetriedFromScratch_AndImportsEachPersonOnce()
    {
        var tenant = await SeedAsync();
        var fault = new TransientCommitFault(afterCommit: false);
        await using (var db = Db(fault))
        {
            var ok = Assert.IsType<OkObjectResult>(await Import(db, tenant, File(4, generatedCodes: true)));
            Assert.Equal(4, Body(ok).GetProperty("created").GetInt32());
        }
        Assert.True(fault.Faults >= 1);
        await AssertImportedOnce(tenant, 4);
    }

    // ── A client import key: a re-submitted file is replayed, never imported again ─────────────────

    [Fact]
    public async Task TheSameImportKey_ReplaysTheRecordedSummary_AndADifferentFileUnderItIsRefused()
    {
        var tenant = await SeedAsync();
        var key = Guid.NewGuid();
        var csv = File(3, generatedCodes: true);   // generated codes: a blind re-run would duplicate every row

        await using (var first = Db())
        {
            var ok = Body(Assert.IsType<OkObjectResult>(await Import(first, tenant, csv, key)));
            Assert.Equal(3, ok.GetProperty("created").GetInt32());
            Assert.False(ok.GetProperty("replayed").GetBoolean());
            Assert.Equal(key, ok.GetProperty("importBatchId").GetGuid());
        }
        await using (var again = Db())
        {
            var replay = Body(Assert.IsType<OkObjectResult>(await Import(again, tenant, csv, key)));
            Assert.True(replay.GetProperty("replayed").GetBoolean());
            Assert.Equal(3, replay.GetProperty("created").GetInt32());
        }
        await using (var different = Db())
            Assert.IsType<ConflictObjectResult>(await Import(different, tenant, File(2, generatedCodes: true), key));

        await AssertImportedOnce(tenant, 3);
    }

    [Fact]
    public async Task TwoConcurrentSubmissionsWithTheSameKey_ImportTheFileOnce()
    {
        var tenant = await SeedAsync();
        var key = Guid.NewGuid();
        var csv = File(5, generatedCodes: true);
        await using var a = Db();
        await using var b = Db();

        var results = await Task.WhenAll(Import(a, tenant, csv, key), Import(b, tenant, csv, key));

        var bodies = results.Select(r => Body(Assert.IsType<OkObjectResult>(r))).ToList();
        Assert.Equal(1, bodies.Count(x => x.GetProperty("replayed").GetBoolean()));
        Assert.All(bodies, x => Assert.Equal(5, x.GetProperty("created").GetInt32()));
        await AssertImportedOnce(tenant, 5);
    }

    // ── An unreadable joining date is stored as UNKNOWN on Postgres, and nothing is dated from it ──────

    [Fact]
    public async Task AnUnreadableJoiningDate_IsStoredUnknown_WithNoSalaryOrLinkDatedFromIt()
    {
        var tenant = await SeedAsync();
        var csv = "EmployeeCode,FullName,CompanyLegalName,Grade,BasicSalary,JoiningDate,Status,ManagerEmployeeCode,BankName,IBAN\n"
                  + "JD1,Known Manager,Atomicity Demo,G1,9000,2023-05-01,Active,,Bank,\n"
                  + "JD2,Unknown Date,Atomicity Demo,G1,5000,YYYY-MM-DD,Active,JD1,Bank Two,SA0380000000608010167519\n";
        await using (var db = Db())
            Assert.IsType<OkObjectResult>(await Import(db, tenant, csv));

        await using var verify = _fixture.CreateDb();
        var unknown = await verify.Employees.IgnoreQueryFilters().SingleAsync(e => e.TenantId == tenant && e.EmployeeCode == "JD2");
        Assert.Equal(default, unknown.JoiningDate);
        Assert.Equal(EmployeeStatuses.Draft, unknown.Status);
        Assert.Equal("Blocked", unknown.ReadinessState);
        Assert.False(await verify.EmployeeSalaryStructures.IgnoreQueryFilters().AnyAsync(s => s.EmployeeId == unknown.Id));
        var line = await verify.ReportingLines.IgnoreQueryFilters().SingleAsync(l => l.EmployeeId == unknown.Id);
        Assert.True(line.EffectiveFrom.Year >= 2026, $"reporting line dated from an unknown joining date: {line.EffectiveFrom:O}");
        // Bank details are in BOTH homes (defect 5), even though no salary structure was created.
        var profile = await verify.EmployeePayrollProfiles.IgnoreQueryFilters().SingleAsync(p => p.EmployeeId == unknown.Id);
        Assert.Equal("SA0380000000608010167519", profile.Iban);
        Assert.Equal(profile.Iban, unknown.BankIban);
        Assert.Equal(profile.BankName, unknown.BankName);
    }
}
