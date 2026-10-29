using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Claims;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.AI;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Covers: template download (both route aliases), export audit logging, tenant isolation,
/// company filter in overview, readiness step logic, employee salary import validation,
/// cross-tenant code rejection, AI insight engine anomaly generation, and deduplication.
/// </summary>
public class PayrollModuleTests
{
    // ── DB factories ─────────────────────────────────────────────────────────

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // SQLite in-memory is needed for endpoints that use ExecuteUpdateAsync
    private static (ZayraDbContext db, SqliteConnection conn) CreateSqliteDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(
            new DbContextOptionsBuilder<ZayraDbContext>()
                .UseSqlite(conn)
                .Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    // ── Controller factory ───────────────────────────────────────────────────

    private static PayrollController MakeCtrl(ZayraDbContext db, Guid tenantId, Zayra.Api.Application.CountryPack.ICountryPackResolver? packResolver = null, string[]? permissions = null,
        Zayra.Api.Infrastructure.Documents.IDocumentStorage? storage = null, Guid? userId = null)
    {
        // Salary changes need someone besides the proposer who could approve them (the up-front second-approver check).
        SalaryApprovalKit.EnsureApprover(db, tenantId);
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, (userId ?? Guid.NewGuid()).ToString()),
        };
        if (permissions != null)
            claims.AddRange(permissions.Select(p => new Claim("permission", p)));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var httpCtx = new DefaultHttpContext { User = principal };

        var ctrl = new PayrollController(
            db,
            new _UnrestrictedScope(),
            new _HttpAccessor(httpCtx),
            new _NullNotifications(),
            packResolver ?? new _NullPackResolver(),
            new StubRuleReader(),
            new _NullLetterService(),
            storage ?? new NullDocumentStorage(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(8));
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }

    /// <summary>Finance's side of a salary change: a DIFFERENT user (MakeCtrl gives every controller its own login) approves
    /// every pending change. Salary changes are proposed by HR and only reach the schedule once this has run.</summary>
    private static async Task ApprovePendingSalaryChangesAsync(ZayraDbContext db, Guid tenantId)
    {
        var finance = MakeCtrl(db, tenantId);
        foreach (var id in await db.SalaryChangeRequests.AsNoTracking()
                     .Where(x => x.TenantId == tenantId && x.Status == SalaryChangeRequest.Pending).Select(x => x.Id).ToListAsync())
            Assert.IsType<OkObjectResult>(await finance.ApproveSalaryChange(id, null, CancellationToken.None));
    }


    // ── Salary changes: HR proposes, a different Finance approver decides ─────────────────

    private sealed record SalaryChangeWorld(ZayraDbContext Db, SqliteConnection Conn, Guid Tenant, Employee Alice, Employee Bob, SalaryStructure Structure, Guid AliceLogin, Guid BobLogin);

    private static async Task<SalaryChangeWorld> SeedSalaryChangeWorldAsync()
    {
        var (db, conn) = CreateSqliteDb();
        var tenantId = Guid.NewGuid();
        var aliceLogin = Guid.NewGuid(); var bobLogin = Guid.NewGuid();
        var alice = new Employee { TenantId = tenantId, EmployeeCode = "SC-001", FullName = "Alice", Status = "Active", JoiningDate = new DateTime(2025, 1, 1), UserAccountId = aliceLogin };
        var bob = new Employee { TenantId = tenantId, EmployeeCode = "SC-002", FullName = "Bob", Status = "Active", JoiningDate = new DateTime(2025, 1, 1), UserAccountId = bobLogin };
        var structure = new SalaryStructure { TenantId = tenantId, Code = "STD", Name = "Standard", Currency = "SAR", EffectiveDate = new DateOnly(2025, 1, 1) };
        db.Employees.AddRange(alice, bob);
        db.SalaryStructures.Add(structure);
        await db.SaveChangesAsync();
        return new SalaryChangeWorld(db, conn, tenantId, alice, bob, structure, aliceLogin, bobLogin);
    }

    private static EmployeeSalaryStructureRequest Package(SalaryChangeWorld w, Employee e, decimal basic, string date = "2026-01-01") =>
        new(e.Id, w.Structure.Id, basic, 0m, 0m, 0m, 0m, 0m, 0m, DateOnly.Parse(date), "SAR");

    private static async Task<Guid> ProposeAsync(SalaryChangeWorld w, Guid maker, Employee e, decimal basic, string date = "2026-01-01")
    {
        var result = await MakeCtrl(w.Db, w.Tenant, userId: maker).AssignEmployeeSalary(Package(w, e, basic, date), CancellationToken.None);
        Assert.IsType<AcceptedResult>(result);
        return (await w.Db.SalaryChangeRequests.AsNoTracking().OrderByDescending(x => x.RequestedAtUtc).FirstAsync(x => x.EmployeeId == e.Id)).Id;
    }

    [Fact]
    public async Task SalaryChange_IsPending_UntilADifferentApproverApproves_ThenItAppliesOnce()
    {
        var w = await SeedSalaryChangeWorldAsync(); await using var _ = w.Conn; await using var __ = w.Db;
        var hr = Guid.NewGuid(); var finance = Guid.NewGuid();
        var id = await ProposeAsync(w, hr, w.Alice, 9_000m);

        (await w.Db.EmployeeSalaryStructures.AnyAsync(x => x.EmployeeId == w.Alice.Id)).Should().BeFalse("payroll must not see an unapproved figure");
        var pending = await w.Db.SalaryChangeRequests.AsNoTracking().SingleAsync(x => x.Id == id);
        pending.Status.Should().Be(SalaryChangeRequest.Pending);
        pending.RequestedByUserId.Should().Be(hr);

        Assert.IsType<OkObjectResult>(await MakeCtrl(w.Db, w.Tenant, userId: finance).ApproveSalaryChange(id, new(null), CancellationToken.None));

        var decided = await w.Db.SalaryChangeRequests.AsNoTracking().SingleAsync(x => x.Id == id);
        decided.Status.Should().Be(SalaryChangeRequest.Approved);
        decided.DecidedByUserId.Should().Be(finance);
        var assignment = await w.Db.EmployeeSalaryStructures.SingleAsync(x => x.EmployeeId == w.Alice.Id);
        assignment.BasicSalary.Should().Be(9_000m);
        decided.AppliedAssignmentId.Should().Be(assignment.Id);

        // A second approval of the same request is refused and changes nothing.
        Assert.IsType<ConflictObjectResult>(await MakeCtrl(w.Db, w.Tenant, userId: Guid.NewGuid()).ApproveSalaryChange(id, null, CancellationToken.None));
        (await w.Db.EmployeeSalaryStructures.CountAsync(x => x.EmployeeId == w.Alice.Id)).Should().Be(1);
    }

    [Fact]
    public async Task SalaryChange_TheProposerCannotApproveTheirOwnProposal()
    {
        var w = await SeedSalaryChangeWorldAsync(); await using var _ = w.Conn; await using var __ = w.Db;
        var hr = Guid.NewGuid();
        var id = await ProposeAsync(w, hr, w.Alice, 9_000m);

        var refusal = Assert.IsType<BadRequestObjectResult>(await MakeCtrl(w.Db, w.Tenant, userId: hr).ApproveSalaryChange(id, null, CancellationToken.None));
        System.Text.Json.JsonSerializer.Serialize(refusal.Value).Should().Contain("maker_cannot_approve");
        (await w.Db.SalaryChangeRequests.AsNoTracking().SingleAsync(x => x.Id == id)).Status.Should().Be(SalaryChangeRequest.Pending);
        (await w.Db.EmployeeSalaryStructures.AnyAsync(x => x.EmployeeId == w.Alice.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task SalaryChange_TheEmployeeItIsAbout_CannotProposeOrApproveIt()
    {
        var w = await SeedSalaryChangeWorldAsync(); await using var _ = w.Conn; await using var __ = w.Db;

        // Alice cannot propose a change to her own pay, whatever her role.
        var own = Assert.IsType<BadRequestObjectResult>(await MakeCtrl(w.Db, w.Tenant, userId: w.AliceLogin)
            .AssignEmployeeSalary(Package(w, w.Alice, 50_000m), CancellationToken.None));
        System.Text.Json.JsonSerializer.Serialize(own.Value).Should().Contain("own salary");
        (await w.Db.SalaryChangeRequests.AnyAsync()).Should().BeFalse();

        // Nor can she approve (or reject) a change about her that someone else proposed.
        var id = await ProposeAsync(w, Guid.NewGuid(), w.Alice, 9_000m);
        Assert.IsType<BadRequestObjectResult>(await MakeCtrl(w.Db, w.Tenant, userId: w.AliceLogin).ApproveSalaryChange(id, null, CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await MakeCtrl(w.Db, w.Tenant, userId: w.AliceLogin).RejectSalaryChange(id, null, CancellationToken.None));
        (await w.Db.SalaryChangeRequests.AsNoTracking().SingleAsync(x => x.Id == id)).Status.Should().Be(SalaryChangeRequest.Pending);
    }

    [Fact]
    public async Task SalaryChange_Rejected_AppliesNothing_AndCannotBeApprovedLater()
    {
        var w = await SeedSalaryChangeWorldAsync(); await using var _ = w.Conn; await using var __ = w.Db;
        var id = await ProposeAsync(w, Guid.NewGuid(), w.Alice, 9_000m);

        Assert.IsType<OkObjectResult>(await MakeCtrl(w.Db, w.Tenant).RejectSalaryChange(id, new("Not budgeted"), CancellationToken.None));

        var row = await w.Db.SalaryChangeRequests.AsNoTracking().SingleAsync(x => x.Id == id);
        row.Status.Should().Be(SalaryChangeRequest.Rejected);
        row.DecisionNote.Should().Be("Not budgeted");
        Assert.IsType<ConflictObjectResult>(await MakeCtrl(w.Db, w.Tenant).ApproveSalaryChange(id, null, CancellationToken.None));
        (await w.Db.EmployeeSalaryStructures.AnyAsync(x => x.EmployeeId == w.Alice.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task SalaryChange_OnlyTheProposerCancels_AndACancelledChangeCannotBeApproved()
    {
        var w = await SeedSalaryChangeWorldAsync(); await using var _ = w.Conn; await using var __ = w.Db;
        var hr = Guid.NewGuid();
        var id = await ProposeAsync(w, hr, w.Alice, 9_000m);

        Assert.IsType<ForbidResult>(await MakeCtrl(w.Db, w.Tenant, userId: Guid.NewGuid()).CancelSalaryChange(id, CancellationToken.None));
        (await w.Db.SalaryChangeRequests.AsNoTracking().SingleAsync(x => x.Id == id)).Status.Should().Be(SalaryChangeRequest.Pending);

        Assert.IsType<OkObjectResult>(await MakeCtrl(w.Db, w.Tenant, userId: hr).CancelSalaryChange(id, CancellationToken.None));
        (await w.Db.SalaryChangeRequests.AsNoTracking().SingleAsync(x => x.Id == id)).Status.Should().Be(SalaryChangeRequest.Cancelled);
        Assert.IsType<ConflictObjectResult>(await MakeCtrl(w.Db, w.Tenant).ApproveSalaryChange(id, null, CancellationToken.None));
        (await w.Db.EmployeeSalaryStructures.AnyAsync(x => x.EmployeeId == w.Alice.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task SalaryChange_ANewerProposalForTheSameDate_SupersedesTheOlderPending()
    {
        var w = await SeedSalaryChangeWorldAsync(); await using var _ = w.Conn; await using var __ = w.Db;
        var hr = Guid.NewGuid();
        var first = await ProposeAsync(w, hr, w.Alice, 9_000m);
        var second = await ProposeAsync(w, hr, w.Alice, 9_500m);

        (await w.Db.SalaryChangeRequests.AsNoTracking().SingleAsync(x => x.Id == first)).Status.Should().Be(SalaryChangeRequest.Cancelled);
        (await w.Db.SalaryChangeRequests.AsNoTracking().SingleAsync(x => x.Id == second)).Status.Should().Be(SalaryChangeRequest.Pending);
    }

    [Fact]
    public async Task SalaryImport_OnlyStagesRows_RefusesTheCallersOwnPay_AndABatchApprovalHoldsTheApproversOwnRow()
    {
        var w = await SeedSalaryChangeWorldAsync(); await using var _ = w.Conn; await using var __ = w.Db;
        var csv = "EmployeeCode,SalaryStructureCode,BasicSalary,HousingAllowance,TransportAllowance,FoodAllowance,MobileAllowance,OtherAllowance,FixedDeduction,Currency,EffectiveDate\n" +
                  "SC-001,STD,9000,0,0,0,0,0,0,SAR,2026-01-01\nSC-002,STD,8000,0,0,0,0,0,0,SAR,2026-01-01\n";

        // Alice (the importer) is also a row in the file: her own row is refused, Bob's is staged.
        var import = Assert.IsType<OkObjectResult>(await MakeCtrl(w.Db, w.Tenant, userId: w.AliceLogin)
            .ImportEmployeeSalaries(new ImportSalaryStructuresRequest(csv), CancellationToken.None));
        var body = System.Text.Json.JsonSerializer.SerializeToElement(import.Value);
        body.GetProperty("pendingApproval").GetInt32().Should().Be(1);
        body.GetProperty("skipped").GetInt32().Should().Be(1);
        body.GetProperty("created").GetInt32().Should().Be(0);
        (await w.Db.EmployeeSalaryStructures.AnyAsync()).Should().BeFalse("an import applies nothing");
        var staged = await w.Db.SalaryChangeRequests.AsNoTracking().SingleAsync();
        staged.EmployeeId.Should().Be(w.Bob.Id);
        staged.Source.Should().Be("Import");
        var batchId = staged.BatchId!.Value;

        // A second import by HR (a later date, so it supersedes nothing) stages Alice's row too, into its own batch.
        var hr = Guid.NewGuid();
        Assert.IsType<OkObjectResult>(await MakeCtrl(w.Db, w.Tenant, userId: hr).ImportEmployeeSalaries(new ImportSalaryStructuresRequest(csv.Replace("2026-01-01", "2026-02-01")), CancellationToken.None));
        var batchTwo = (await w.Db.SalaryChangeRequests.AsNoTracking().Where(x => x.RequestedByUserId == hr).Select(x => x.BatchId).Distinct().ToListAsync()).Should().ContainSingle().Subject!.Value;
        (await w.Db.SalaryChangeRequests.CountAsync(x => x.BatchId == batchTwo)).Should().Be(2);

        // Bob approves HR's batch: his own row is held, Alice's is applied. The maker (HR) cannot approve any of it.
        var asHr = Assert.IsType<OkObjectResult>(await MakeCtrl(w.Db, w.Tenant, userId: hr).ApproveSalaryChangeBatch(batchTwo, null, CancellationToken.None));
        System.Text.Json.JsonSerializer.SerializeToElement(asHr.Value).GetProperty("approved").GetInt32().Should().Be(0);

        var asBob = Assert.IsType<OkObjectResult>(await MakeCtrl(w.Db, w.Tenant, userId: w.BobLogin).ApproveSalaryChangeBatch(batchTwo, null, CancellationToken.None));
        var result = System.Text.Json.JsonSerializer.SerializeToElement(asBob.Value);
        result.GetProperty("approved").GetInt32().Should().Be(1);
        result.GetProperty("held").GetArrayLength().Should().Be(1);
        (await w.Db.EmployeeSalaryStructures.SingleAsync()).EmployeeId.Should().Be(w.Alice.Id);
        (await w.Db.SalaryChangeRequests.AsNoTracking().SingleAsync(x => x.BatchId == batchTwo && x.EmployeeId == w.Bob.Id)).Status.Should().Be(SalaryChangeRequest.Pending);

        // Finance (neither maker nor subject) decides the earlier batch and the held row.
        Assert.IsType<OkObjectResult>(await MakeCtrl(w.Db, w.Tenant).ApproveSalaryChangeBatch(batchId, null, CancellationToken.None));
        (await w.Db.EmployeeSalaryStructures.CountAsync(x => x.EmployeeId == w.Bob.Id)).Should().Be(1);
    }

    [Fact]
    public async Task SalaryChange_CannotReplaceARowCarryingPercentOrQiwaTerms_AndCancelAndSupersedeAreAudited()
    {
        var w = await SeedSalaryChangeWorldAsync(); await using var _ = w.Conn; await using var __ = w.Db;
        w.Db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = w.Tenant, EmployeeId = w.Alice.Id, SalaryStructureId = w.Structure.Id, BasicSalary = 8_000m, Currency = "SAR",
            EffectiveDate = new DateOnly(2026, 1, 1), IsActive = true, QiwaConfirmedOn = new DateOnly(2026, 1, 1),
        });
        await w.Db.SaveChangesAsync();
        var hr = Guid.NewGuid();
        var id = await ProposeAsync(w, hr, w.Alice, 9_000m);

        var refused = Assert.IsType<ConflictObjectResult>(await MakeCtrl(w.Db, w.Tenant).ApproveSalaryChange(id, null, CancellationToken.None));
        System.Text.Json.JsonSerializer.Serialize(refused.Value).Should().Contain("salary_change_stale");
        (await w.Db.EmployeeSalaryStructures.AsNoTracking().SingleAsync(x => x.EmployeeId == w.Alice.Id)).IsActive.Should().BeTrue("the contract-linked row stands");
        (await w.Db.SalaryChangeRequests.AsNoTracking().SingleAsync(x => x.Id == id)).Status.Should().Be(SalaryChangeRequest.Pending);

        // A newer proposal for the same date supersedes the first (audited), and the proposer can cancel it (audited).
        var second = await ProposeAsync(w, hr, w.Alice, 9_500m);
        Assert.IsType<OkObjectResult>(await MakeCtrl(w.Db, w.Tenant, userId: hr).CancelSalaryChange(second, CancellationToken.None));
        var actions = await w.Db.PayrollAuditLogs.AsNoTracking().Select(a => a.Action).ToListAsync();
        actions.Should().Contain("payroll.employee_salary.change_superseded").And.Contain("payroll.employee_salary.change_cancelled");
    }

    // ── Section 1: Template download — both route aliases ────────────────────

    [Fact]
    public void SalaryStructureTemplate_ReturnsHeaderRow()
    {
        // Endpoint is route-aliased so both /structures/ and /salary-structures/ routes work.
        // We test the action method directly — both routes invoke the same method.
        var db = CreateDb();
        var ctrl = MakeCtrl(db, Guid.NewGuid());

        var result = ctrl.StructuresImportTemplate();

        var content = CsvTestHelpers.Body(result);
        foreach (var col in new[] { "CompanyLegalName", "Code", "Name", "Currency", "EffectiveDate", "IsActive",
            "MinGrossSalary", "MaxGrossSalary", "MinBasicSalary", "MaxBasicSalary",
            "EligibleGradeIds", "EligibleDesignationIds",
            "ComponentCode", "ComponentName", "ComponentType", "CalculationType", "Amount", "Percentage", "IsTaxable", "ComponentIsActive" })
            content.Should().Contain(col, because: $"column {col} must appear in template");
    }

    [Fact]
    public void EmployeeSalaryTemplate_ReturnsAllHeaders()
    {
        var db = CreateDb();
        var ctrl = MakeCtrl(db, Guid.NewGuid());

        var result = ctrl.EmployeeSalariesImportTemplate();

        var content = CsvTestHelpers.Body(result);
        foreach (var col in new[] { "EmployeeCode", "SalaryStructureCode", "BasicSalary",
            "HousingAllowance", "TransportAllowance", "FoodAllowance",
            "MobileAllowance", "OtherAllowance", "FixedDeduction", "Currency", "EffectiveDate" })
        {
            content.Should().Contain(col, because: $"column {col} must appear in template");
        }
    }

    [Fact]
    public async Task AssignEmployeeSalary_FutureEffectiveChange_PreservesCurrentAssignment()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn;
        var tenantId = Guid.NewGuid();
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = "ES-001", FullName = "Effective Schedule",
            Status = "Active", JoiningDate = new DateTime(2025, 1, 1)
        };
        var structure = new SalaryStructure
        {
            TenantId = tenantId, Code = "STD", Name = "Standard", Currency = "SAR",
            EffectiveDate = new DateOnly(2025, 1, 1)
        };
        db.Employees.Add(employee);
        db.SalaryStructures.Add(structure);
        await db.SaveChangesAsync();
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = employee.Id, SalaryStructureId = structure.Id,
            BasicSalary = 10_000m, Currency = "SAR", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true
        });
        await db.SaveChangesAsync();

        var result = await MakeCtrl(db, tenantId).AssignEmployeeSalary(
            new EmployeeSalaryStructureRequest(employee.Id, structure.Id, 12_000m, 0m, 0m, 0m, 0m, 0m, 0m, new DateOnly(2099, 1, 1), "SAR"),
            CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>("a salary change waits for a different Finance approver");
        (await db.EmployeeSalaryStructures.CountAsync(x => x.TenantId == tenantId && x.EmployeeId == employee.Id))
            .Should().Be(1, "nothing reaches the salary schedule before approval");
        await ApprovePendingSalaryChangesAsync(db, tenantId);
        var assignments = await db.EmployeeSalaryStructures
            .Where(x => x.TenantId == tenantId && x.EmployeeId == employee.Id)
            .OrderBy(x => x.EffectiveDate)
            .ToListAsync();
        assignments.Should().HaveCount(2);
        assignments.Should().OnlyContain(x => x.IsActive);
        assignments[0].BasicSalary.Should().Be(10_000m);
        assignments[1].BasicSalary.Should().Be(12_000m);
    }

    [Fact]
    public async Task Process_UsesLatestSalaryAssignmentEffectiveForRunPeriod()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn;
        await using var __ = db;
        var tenantId = Guid.NewGuid();
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "Effective Payroll Co", CountryCode = "ARE",
            Jurisdiction = "UAE-mainland", DefaultCurrency = "AED", IsActive = true,
            RegistrationNumber = "EPC-001"
        };
        var structure = new SalaryStructure
        {
            TenantId = tenantId, CompanyId = company.Id, Code = "STD", Name = "Standard",
            Currency = "AED", EffectiveDate = new DateOnly(2025, 1, 1)
        };
        var employee = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "EFF-001",
            FullName = "Effective Dated Employee", Nationality = "Indian",
            Status = "Active", JoiningDate = new DateTime(2025, 1, 1)
        };
        db.Companies.Add(company);
        db.SalaryStructures.Add(structure);
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        db.EmployeeSalaryStructures.AddRange(
            new EmployeeSalaryStructure
            {
                TenantId = tenantId, EmployeeId = employee.Id, SalaryStructureId = structure.Id,
                BasicSalary = 10_000m, Currency = "AED", EffectiveDate = new DateOnly(2026, 1, 1), IsActive = true
            },
            new EmployeeSalaryStructure
            {
                TenantId = tenantId, EmployeeId = employee.Id, SalaryStructureId = structure.Id,
                BasicSalary = 20_000m, Currency = "AED", EffectiveDate = new DateOnly(2026, 2, 1), IsActive = true
            });
        var run = new PayrollRun { TenantId = tenantId, CompanyId = company.Id, Year = 2026, Month = 1 };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();

        var result = await MakeCtrl(db, tenantId, new _ModuleZeroPackResolver()).Process(run.Id, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var slip = await db.PayrollSlips.SingleAsync(s => s.RunId == run.Id);
        slip.BasicSalary.Should().Be(10_000m);
        slip.GrossSalary.Should().Be(10_000m);
    }

    [Fact]
    public async Task Process_IncludesImportedOpeningBalancesInYtdTotals()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn;
        await using var __ = db;
        var tenantId = Guid.NewGuid();
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "Opening Balance Co", CountryCode = "ARE",
            Jurisdiction = "UAE-mainland", DefaultCurrency = "AED", IsActive = true,
            RegistrationNumber = "OBC-001"
        };
        var structure = new SalaryStructure
        {
            TenantId = tenantId, CompanyId = company.Id, Code = "STD", Name = "Standard",
            Currency = "AED", EffectiveDate = new DateOnly(2025, 1, 1)
        };
        var employee = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "OB-001",
            FullName = "Opening Balance Employee", Nationality = "Indian",
            Status = "Active", JoiningDate = new DateTime(2025, 1, 1)
        };
        db.Companies.Add(company);
        db.SalaryStructures.Add(structure);
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = employee.Id, SalaryStructureId = structure.Id,
            BasicSalary = 10_000m, Currency = "AED", EffectiveDate = new DateOnly(2026, 1, 1), IsActive = true
        });
        db.PayrollOpeningBalances.AddRange(
            new PayrollOpeningBalance { TenantId = tenantId, CompanyId = company.Id, EmployeeId = employee.Id, EmployeeCode = employee.EmployeeCode, Year = 2026, BalanceType = "YTD_GROSS", ComponentCode = "BASIC", Amount = 50_000m, Currency = "AED" },
            new PayrollOpeningBalance { TenantId = tenantId, CompanyId = company.Id, EmployeeId = employee.Id, EmployeeCode = employee.EmployeeCode, Year = 2026, BalanceType = "YTD_DEDUCTIONS", ComponentCode = "GOSI", Amount = 5_000m, Currency = "AED" },
            new PayrollOpeningBalance { TenantId = tenantId, CompanyId = company.Id, EmployeeId = employee.Id, EmployeeCode = employee.EmployeeCode, Year = 2026, BalanceType = "YTD_NET", ComponentCode = "NET", Amount = 45_000m, Currency = "AED" });
        var run = new PayrollRun { TenantId = tenantId, CompanyId = company.Id, Year = 2026, Month = 1 };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();

        var result = await MakeCtrl(db, tenantId, new _ModuleZeroPackResolver()).Process(run.Id, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var slip = await db.PayrollSlips.SingleAsync(s => s.RunId == run.Id);
        slip.YtdGross.Should().Be(60_000m);
        slip.YtdDeductions.Should().Be(5_000m);
        slip.YtdNet.Should().Be(55_000m);
    }

    [Fact]
    public async Task Process_DoesNotDeductLoansOrAdvancesBeforeRepaymentStartDate()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn;
        await using var __ = db;
        var tenantId = Guid.NewGuid();
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "Benefits Timing Co", CountryCode = "ARE",
            Jurisdiction = "UAE-mainland", DefaultCurrency = "AED", IsActive = true,
            RegistrationNumber = "BTC-001"
        };
        var structure = new SalaryStructure { TenantId = tenantId, CompanyId = company.Id, Code = "STD", Name = "Standard", Currency = "AED" };
        var employee = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "BEN-001",
            FullName = "Benefits Timing", Nationality = "Indian",
            Status = "Active", JoiningDate = new DateTime(2025, 1, 1)
        };
        db.Companies.Add(company);
        db.SalaryStructures.Add(structure);
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = employee.Id, SalaryStructureId = structure.Id,
            BasicSalary = 8_000m, Currency = "AED", EffectiveDate = new DateOnly(2026, 1, 1), IsActive = true
        });
        var loan = new EmployeeLoan
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeIntId = employee.Id,
            EmployeeName = employee.FullName, LoanNumber = "LN-001", Status = "Active",
            InstallmentAmount = 500m, OutstandingBalance = 1_500m, RepaymentStartDate = new DateOnly(2026, 2, 1)
        };
        var advance = new SalaryAdvance
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeIntId = employee.Id,
            EmployeeName = employee.FullName, AdvanceNumber = "ADV-001", Status = "Active",
            InstallmentAmount = 300m, OutstandingBalance = 900m, RepaymentStartDate = new DateOnly(2026, 2, 1)
        };
        db.EmployeeLoans.Add(loan);
        db.SalaryAdvances.Add(advance);
        var run = new PayrollRun { TenantId = tenantId, CompanyId = company.Id, Year = 2026, Month = 1 };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();

        var result = await MakeCtrl(db, tenantId, new _ModuleZeroPackResolver()).Process(run.Id, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var slip = await db.PayrollSlips.SingleAsync(s => s.RunId == run.Id);
        slip.LoanDeductions.Should().Be(0m);
        (await db.PayrollDeductions.CountAsync(d => d.PayrollRunId == run.Id && (d.ComponentCode == "LOAN_EMI" || d.ComponentCode == "ADVANCE_EMI"))).Should().Be(0);
        (await db.EmployeeLoans.SingleAsync(x => x.Id == loan.Id)).OutstandingBalance.Should().Be(1_500m);
        (await db.SalaryAdvances.SingleAsync(x => x.Id == advance.Id)).OutstandingBalance.Should().Be(900m);
    }

    [Fact]
    public async Task Process_AppliesApprovedPayrollAdjustmentsAsVariablePayAndDeductions()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn;
        await using var __ = db;
        var tenantId = Guid.NewGuid();
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "Variable Pay Co", CountryCode = "ARE",
            Jurisdiction = "UAE-mainland", DefaultCurrency = "AED", IsActive = true,
            RegistrationNumber = "VPC-001"
        };
        var structure = new SalaryStructure { TenantId = tenantId, CompanyId = company.Id, Code = "STD", Name = "Standard", Currency = "AED" };
        var employee = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "VAR-001",
            FullName = "Variable Pay", Nationality = "Indian",
            Status = "Active", JoiningDate = new DateTime(2025, 1, 1)
        };
        db.Companies.Add(company);
        db.SalaryStructures.Add(structure);
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = employee.Id, SalaryStructureId = structure.Id,
            BasicSalary = 5_000m, Currency = "AED", EffectiveDate = new DateOnly(2026, 1, 1), IsActive = true
        });
        var run = new PayrollRun { TenantId = tenantId, CompanyId = company.Id, Year = 2026, Month = 1 };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        var encashmentId = Guid.NewGuid();
        var encashmentAdjustment = new PayrollAdjustment
        {
            TenantId = tenantId, PayrollRunId = run.Id, EmployeeId = employee.Id,
            AdjustmentType = "Leave Encashment", Amount = 250m, Reason = "Approved leave encashment", Status = "Approved",
            SourceType = PayrollAdjustmentSources.LeaveEncashment, SourceId = encashmentId
        };
        db.LeaveEncashmentRequests.Add(new LeaveEncashmentRequest
        {
            Id = encashmentId, TenantId = tenantId, CompanyId = company.Id,
            EmployeeId = employee.Id, EmployeeName = employee.FullName,
            LeaveTypeId = Guid.NewGuid(), LeaveTypeName = "Annual", Year = 2026,
            DaysToEncash = 1m, AmountPerDay = 250m, TotalAmount = 250m, Currency = "AED",
            Status = LeaveEncashmentStatuses.PayrollApproved,
            PayrollRunId = run.Id, PayrollAdjustmentId = encashmentAdjustment.Id
        });
        db.PayrollAdjustments.AddRange(
            encashmentAdjustment,
            new PayrollAdjustment
            {
                TenantId = tenantId, PayrollRunId = run.Id, EmployeeId = employee.Id,
                AdjustmentType = "Recovery", Amount = -100m, Reason = "Overpayment recovery", Status = "Approved"
            });
        await db.SaveChangesAsync();

        var result = await MakeCtrl(db, tenantId, new _ModuleZeroPackResolver()).Process(run.Id, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var slip = await db.PayrollSlips.SingleAsync(s => s.RunId == run.Id);
        slip.GrossSalary.Should().Be(5_250m);
        slip.Deductions.Should().Be(100m);
        slip.NetSalary.Should().Be(5_150m);
        (await db.PayrollEarnings.AnyAsync(e => e.PayrollRunId == run.Id && e.Source == "Adjustment" && e.Amount == 250m)).Should().BeTrue();
        (await db.PayrollDeductions.AnyAsync(d => d.PayrollRunId == run.Id && d.Source == "Adjustment" && d.Amount == 100m)).Should().BeTrue();
        (await db.PayrollAdjustments.Where(a => a.PayrollRunId == run.Id).Select(a => a.Status).Distinct().SingleAsync()).Should().Be("Processed");
        var encashment = await db.LeaveEncashmentRequests.AsNoTracking().SingleAsync(x => x.Id == encashmentId);
        encashment.Status.Should().Be(LeaveEncashmentStatuses.Processed);
        encashment.ProcessedAtUtc.Should().NotBeNull();
        (await db.PayrollRunConsumptions.AnyAsync(x => x.PayrollRunId == run.Id
            && x.ArtifactType == PayrollConsumptionArtifacts.Adjustment
            && x.ArtifactId == encashmentAdjustment.Id)).Should().BeTrue();
    }

    // ── Section 2: Salary structure export — both route aliases ──────────────

    [Fact]
    public async Task SalaryStructureExport_ReturnsCsvWithStructures()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.SalaryStructures.Add(new SalaryStructure
        {
            TenantId = tenantId, Code = "STR-001", Name = "Staff Grade A",
            Currency = "AED", EffectiveDate = new DateOnly(2025, 1, 1)
        });
        await db.SaveChangesAsync();
        var ctrl = MakeCtrl(db, tenantId);

        var result = await ctrl.ExportStructures(CancellationToken.None);

        var content = CsvTestHelpers.Body(result);
        content.Should().Contain("STR-001");
        content.Should().Contain("Staff Grade A");
    }

    // ── Section 3: Tenant isolation — overview only returns own companies ─────

    [Fact]
    public async Task Overview_TenantIsolation_OnlyReturnsTenantCompanies()
    {
        var db = CreateDb();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        db.Companies.AddRange(
            new Company { TenantId = tenantA, LegalNameEn = "Alpha Corp", CountryCode = "AE" },
            new Company { TenantId = tenantA, LegalNameEn = "Alpha Retail", CountryCode = "AE" },
            new Company { TenantId = tenantB, LegalNameEn = "Beta Corp", CountryCode = "KW" });
        await db.SaveChangesAsync();

        var ctrl = MakeCtrl(db, tenantA);
        var result = await ctrl.PayrollOverview(null, null, null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = ok.Value!;
        var companies = (IEnumerable<object>)body.GetType().GetProperty("Companies")!.GetValue(body)!;
        companies.Should().HaveCount(2, "tenantA has 2 companies");

        // None of the returned companies should belong to tenantB
        var names = companies.Select(c => c.GetType().GetProperty("CompanyName")!.GetValue(c)!.ToString());
        names.Should().NotContain("Beta Corp");
    }

    // ── Section 4: Company filter in overview ─────────────────────────────────

    [Fact]
    public async Task Overview_CompanyFilter_ReturnsOnlyMatchingCompany()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var companyA = new Company { TenantId = tenantId, LegalNameEn = "Company A", CountryCode = "AE" };
        var companyB = new Company { TenantId = tenantId, LegalNameEn = "Company B", CountryCode = "AE" };
        db.Companies.AddRange(companyA, companyB);
        await db.SaveChangesAsync();

        var ctrl = MakeCtrl(db, tenantId);
        var result = await ctrl.PayrollOverview(companyA.Id, null, null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = ok.Value!;
        var companies = (IEnumerable<object>)body.GetType().GetProperty("Companies")!.GetValue(body)!;
        companies.Should().HaveCount(1);
        companies.Single().GetType().GetProperty("CompanyName")!.GetValue(companies.Single())
            .Should().Be("Company A");
    }

    [Fact]
    public async Task Overview_Coverage_IsDistinctAndEffectiveAtRequestedPeriodEnd()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var company = new Company { TenantId = tenantId, LegalNameEn = "Period Co", CountryCode = "SAU" };
        var structure = new SalaryStructure
        {
            TenantId = tenantId, CompanyId = company.Id, Code = "KSA", Name = "KSA",
            Currency = "SAR", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
        };
        var covered = new Employee { TenantId=tenantId, CompanyId=company.Id, EmployeeCode="E1", FullName="Covered", Status="Active" };
        var futureOnly = new Employee { TenantId=tenantId, CompanyId=company.Id, EmployeeCode="E2", FullName="Future", Status="Active" };
        db.AddRange(company, structure, covered, futureOnly);
        await db.SaveChangesAsync();
        db.EmployeeSalaryStructures.AddRange(
            new EmployeeSalaryStructure { TenantId=tenantId, EmployeeId=covered.Id, SalaryStructureId=structure.Id, BasicSalary=8_000m, EffectiveDate=new DateOnly(2025, 1, 1), IsActive=true },
            // A second history row must not make coverage exceed 100%.
            new EmployeeSalaryStructure { TenantId=tenantId, EmployeeId=covered.Id, SalaryStructureId=structure.Id, BasicSalary=9_000m, EffectiveDate=new DateOnly(2025, 6, 1), IsActive=true },
            // Not effective in the requested July period.
            new EmployeeSalaryStructure { TenantId=tenantId, EmployeeId=futureOnly.Id, SalaryStructureId=structure.Id, BasicSalary=7_000m, EffectiveDate=new DateOnly(2025, 8, 1), IsActive=true });
        await db.SaveChangesAsync();

        var result = await MakeCtrl(db, tenantId).PayrollOverview(company.Id, 2025, 7, CancellationToken.None);

        var body = Assert.IsType<OkObjectResult>(result).Value!;
        var rows = (IEnumerable<object>)body.GetType().GetProperty("Companies")!.GetValue(body)!;
        var row = Assert.Single(rows);
        row.GetType().GetProperty("EmployeesWithSalary")!.GetValue(row).Should().Be(1);
        row.GetType().GetProperty("EmployeesMissingSalary")!.GetValue(row).Should().Be(1);
        row.GetType().GetProperty("SalaryCoveragePercent")!.GetValue(row).Should().Be(50d);
    }

    // ── Section 5: Readiness — zero completion when nothing configured ─────────

    [Fact]
    public async Task Readiness_NoConfiguration_ReturnsZeroCompletion()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var ctrl = MakeCtrl(db, tenantId);

        var result = await ctrl.PayrollReadiness(null, null, null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = ok.Value!;
        var pct = (double)body.GetType().GetProperty("CompletionPercent")!.GetValue(body)!;
        pct.Should().Be(0, "no components, structures, or assignments exist");
        var ready = (bool)body.GetType().GetProperty("IsReadyForProcessing")!.GetValue(body)!;
        ready.Should().BeFalse();
    }

    // ── Section 6: Readiness — steps complete when data is configured ─────────

    [Fact]
    public async Task Readiness_FullyConfigured_ReturnsHighCompletion()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();

        // Step 1: Salary component
        var component = new SalaryComponent
        {
            TenantId = tenantId, Code = "BASIC", Name = "Basic", ComponentType = "Earning",
            CalculationType = "Fixed", Amount = 5000, IsActive = true,
            SalaryStructureId = null
        };
        db.SalaryComponents.Add(component);

        // Step 2: Salary structure
        var structure = new SalaryStructure
        {
            TenantId = tenantId, Code = "STR-001", Name = "Grade A",
            Currency = "AED", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true
        };
        db.SalaryStructures.Add(structure);

        // Step 3: Employee + salary assignment (80%+ coverage)
        var emp = new Employee
        {
            TenantId = tenantId, EmployeeCode = "E001", FullName = "Alice",
            Status = "Active", CompanyId = companyId, JoiningDate = DateTime.UtcNow.AddYears(-1)
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();

        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = emp.Id, SalaryStructureId = structure.Id,
            BasicSalary = 5000, Currency = "AED", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true
        });

        // Step 4: Payroll run in Processed status
        db.PayrollRuns.Add(new PayrollRun
        {
            TenantId = tenantId, CompanyId = companyId, Year = 2025, Month = 1,
            Status = "Processed", TotalGrossSalary = 5000, TotalNetSalary = 5000
        });
        await db.SaveChangesAsync();

        var ctrl = MakeCtrl(db, tenantId);
        var result = await ctrl.PayrollReadiness(null, 2025, 1, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = ok.Value!;
        var pct = (double)body.GetType().GetProperty("CompletionPercent")!.GetValue(body)!;
        pct.Should().BeGreaterOrEqualTo(66, "at least steps 1, 2, 3, 4, and 6 should be complete");

        var ready = (bool)body.GetType().GetProperty("IsReadyForProcessing")!.GetValue(body)!;
        ready.Should().BeTrue();
    }

    // ── Section 7: Readiness — company-scoped isolation ──────────────────────

    [Fact]
    public async Task Readiness_CompanyScoped_OnlyCountsCompanyEmployees()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var companyA = Guid.NewGuid();
        var companyB = Guid.NewGuid();

        var structure = new SalaryStructure
        {
            TenantId = tenantId, Code = "STR-A", Name = "Grade A",
            Currency = "AED", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true
        };
        db.SalaryStructures.Add(structure);

        // Employee in Company A (assigned)
        var empA = new Employee
        {
            TenantId = tenantId, EmployeeCode = "EA01", FullName = "Bob", Status = "Active",
            CompanyId = companyA, JoiningDate = DateTime.UtcNow.AddYears(-1)
        };
        // Employee in Company B (no assignment)
        var empB = new Employee
        {
            TenantId = tenantId, EmployeeCode = "EB01", FullName = "Carol", Status = "Active",
            CompanyId = companyB, JoiningDate = DateTime.UtcNow.AddYears(-1)
        };
        db.Employees.AddRange(empA, empB);
        await db.SaveChangesAsync();

        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = empA.Id, SalaryStructureId = structure.Id,
            BasicSalary = 5000, Currency = "AED", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true
        });
        await db.SaveChangesAsync();

        var ctrl = MakeCtrl(db, tenantId);

        // Readiness for Company A — 1/1 employees assigned → coverage = 100%
        var resultA = await ctrl.PayrollReadiness(companyA, 2025, 1, CancellationToken.None);
        var bodyA = ((OkObjectResult)resultA).Value!;
        var coverageA = (double)bodyA.GetType().GetProperty("SalaryCoveragePercent")!.GetValue(bodyA)!;
        coverageA.Should().Be(100);

        // Readiness for Company B — 1/1 employees, 0 assigned → coverage = 0%
        var resultB = await ctrl.PayrollReadiness(companyB, 2025, 1, CancellationToken.None);
        var bodyB = ((OkObjectResult)resultB).Value!;
        var coverageB = (double)bodyB.GetType().GetProperty("SalaryCoveragePercent")!.GetValue(bodyB)!;
        coverageB.Should().Be(0);
    }

    // ── Section 8: Employee salary import — validation errors ─────────────────
    // Moved to PayrollSalaryImportPostgresTests (real PostgreSQL): the import is transactional, which InMemory cannot run.

    // ── Section 9: Employee salary import — valid row, uses SQLite ────────────

    [Fact]
    public async Task EmployeeSalaryImport_ValidRow_CreatesRecord()
    {
        // Uses SQLite because the deactivation step uses ExecuteUpdateAsync
        // which is not supported by the InMemory provider.
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn;
        await using var __ = db;

        var tenantId = Guid.NewGuid();
        var structure = new SalaryStructure
        {
            TenantId = tenantId, Code = "STR-BASIC", Name = "Basic",
            Currency = "AED", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true
        };
        db.SalaryStructures.Add(structure);
        var emp = new Employee
        {
            TenantId = tenantId, EmployeeCode = "E001", FullName = "Alice",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1)
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();

        var ctrl = MakeCtrl(db, tenantId);
        var csv = "EmployeeCode,SalaryStructureCode,BasicSalary,HousingAllowance,TransportAllowance," +
                  "FoodAllowance,MobileAllowance,OtherAllowance,FixedDeduction,Currency,EffectiveDate\n" +
                  $"E001,STR-BASIC,7500,2000,500,300,200,0,0,AED,2025-01-01\n";

        var result = await ctrl.ImportEmployeeSalaries(
            new ImportSalaryStructuresRequest(csv), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = ok.Value!;
        ((int)body.GetType().GetProperty("created")!.GetValue(body)!).Should().Be(0, "an import only stages changes");
        ((int)body.GetType().GetProperty("pendingApproval")!.GetValue(body)!).Should().Be(1);
        (await db.EmployeeSalaryStructures.AnyAsync(s => s.EmployeeId == emp.Id)).Should().BeFalse("nothing is applied before Finance decides");
        await ApprovePendingSalaryChangesAsync(db, tenantId);

        var savedAssignment = await db.EmployeeSalaryStructures
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.EmployeeId == emp.Id);
        savedAssignment.Should().NotBeNull();
        savedAssignment!.BasicSalary.Should().Be(7500);
        savedAssignment.HousingAllowance.Should().Be(2000);
        savedAssignment.SalaryStructureId.Should().Be(structure.Id);
    }

    [Fact]
    public async Task EmployeeSalaryImport_ReimportSameEffectiveDate_UpdatesExistingRecord()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn;
        await using var __ = db;

        var tenantId = Guid.NewGuid();
        var structure = new SalaryStructure
        {
            TenantId = tenantId, Code = "STR-BASIC", Name = "Basic",
            Currency = "AED", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true
        };
        var emp = new Employee
        {
            TenantId = tenantId, EmployeeCode = "E001", FullName = "Alice",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1)
        };
        db.SalaryStructures.Add(structure);
        db.Employees.Add(emp);
        await db.SaveChangesAsync();
        var ctrl = MakeCtrl(db, tenantId);
        var csv1 = "EmployeeCode,SalaryStructureCode,BasicSalary,HousingAllowance,TransportAllowance," +
                   "FoodAllowance,MobileAllowance,OtherAllowance,FixedDeduction,Currency,EffectiveDate\n" +
                   "E001,STR-BASIC,7500,2000,500,300,200,0,0,AED,2025-01-01\n";
        var csv2 = "EmployeeCode,SalaryStructureCode,BasicSalary,HousingAllowance,TransportAllowance," +
                   "FoodAllowance,MobileAllowance,OtherAllowance,FixedDeduction,Currency,EffectiveDate\n" +
                   "E001,STR-BASIC,8000,2200,500,300,200,0,0,AED,2025-01-01\n";

        await ctrl.ImportEmployeeSalaries(new ImportSalaryStructuresRequest(csv1), CancellationToken.None);
        await ApprovePendingSalaryChangesAsync(db, tenantId);
        var result = await ctrl.ImportEmployeeSalaries(new ImportSalaryStructuresRequest(csv2), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = ok.Value!;
        ((int)body.GetType().GetProperty("pendingApproval")!.GetValue(body)!).Should().Be(1);
        (await db.EmployeeSalaryStructures.SingleAsync(s => s.EmployeeId == emp.Id)).BasicSalary
            .Should().Be(7500, "the re-import is still pending, so the approved figure stands");
        await ApprovePendingSalaryChangesAsync(db, tenantId);

        var assignments = await db.EmployeeSalaryStructures
            .Where(s => s.TenantId == tenantId && s.EmployeeId == emp.Id && s.EffectiveDate == new DateOnly(2025, 1, 1))
            .ToListAsync();
        // The approved replacement is the one active row for the date; the superseded figure stays as inactive history.
        var active = assignments.Should().ContainSingle(x => x.IsActive).Subject;
        active.BasicSalary.Should().Be(8000);
        active.HousingAllowance.Should().Be(2200);
        assignments.Where(x => !x.IsActive).Should().ContainSingle().Which.BasicSalary.Should().Be(7500);
    }

    [Fact]
    public async Task EmployeeSalaryImport_EnforcesStructureAssignmentGuards()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn;
        await using var __ = db;

        var tenantId = Guid.NewGuid();
        var companyA = new Company { TenantId = tenantId, LegalNameEn = "Company A", CountryCode = "AE", RegistrationNumber = "A" };
        var companyB = new Company { TenantId = tenantId, LegalNameEn = "Company B", CountryCode = "AE", RegistrationNumber = "B" };
        var eligibleGrade = new Grade { TenantId = tenantId, Code = "G1", Name = "G1" };
        var blockedGrade = new Grade { TenantId = tenantId, Code = "G2", Name = "G2" };
        var employee = new Employee
        {
            TenantId = tenantId,
            CompanyId = companyA.Id,
            GradeId = blockedGrade.Id,
            EmployeeCode = "E001",
            FullName = "Alice",
            Status = "Active",
            JoiningDate = DateTime.UtcNow.AddYears(-1)
        };
        db.Companies.AddRange(companyA, companyB);
        db.Grades.AddRange(eligibleGrade, blockedGrade);
        db.Employees.Add(employee);
        db.SalaryStructures.AddRange(
            new SalaryStructure { TenantId = tenantId, CompanyId = companyA.Id, Code = "INACTIVE", Name = "Inactive", Currency = "AED", EffectiveDate = new DateOnly(2026, 1, 1), IsActive = false },
            new SalaryStructure { TenantId = tenantId, CompanyId = companyB.Id, Code = "OTHERCO", Name = "Other Company", Currency = "AED", EffectiveDate = new DateOnly(2026, 1, 1), IsActive = true },
            new SalaryStructure { TenantId = tenantId, CompanyId = companyA.Id, Code = "FUTURE", Name = "Future", Currency = "AED", EffectiveDate = new DateOnly(2026, 6, 1), IsActive = true },
            new SalaryStructure { TenantId = tenantId, CompanyId = companyA.Id, Code = "RANGE", Name = "Range", Currency = "AED", EffectiveDate = new DateOnly(2026, 1, 1), IsActive = true, MinGrossSalary = 10_000m },
            new SalaryStructure { TenantId = tenantId, CompanyId = companyA.Id, Code = "GRADE", Name = "Grade", Currency = "AED", EffectiveDate = new DateOnly(2026, 1, 1), IsActive = true, EligibleGradeIdsJson = System.Text.Json.JsonSerializer.Serialize(new[] { eligibleGrade.Id }) });
        await db.SaveChangesAsync();

        var ctrl = MakeCtrl(db, tenantId);
        var csv = "EmployeeCode,SalaryStructureCode,BasicSalary,HousingAllowance,TransportAllowance," +
                  "FoodAllowance,MobileAllowance,OtherAllowance,FixedDeduction,Currency,EffectiveDate\n" +
                  "E001,INACTIVE,12000,0,0,0,0,0,0,AED,2026-01-01\n" +
                  "E001,OTHERCO,12000,0,0,0,0,0,0,AED,2026-01-01\n" +
                  "E001,FUTURE,12000,0,0,0,0,0,0,AED,2026-01-01\n" +
                  "E001,RANGE,8000,0,0,0,0,0,0,AED,2026-01-01\n" +
                  "E001,GRADE,12000,0,0,0,0,0,0,AED,2026-01-01\n";

        var result = await ctrl.ImportEmployeeSalaries(new ImportSalaryStructuresRequest(csv), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = ok.Value!;
        ((int)body.GetType().GetProperty("created")!.GetValue(body)!).Should().Be(0);
        ((int)body.GetType().GetProperty("skipped")!.GetValue(body)!).Should().Be(5);
        var errors = ((IEnumerable<object>)body.GetType().GetProperty("errors")!.GetValue(body)!).Select(e => e.ToString()).ToList();
        errors.Should().Contain(e => e!.Contains("inactive"));
        errors.Should().Contain(e => e!.Contains("different legal entity"));
        errors.Should().Contain(e => e!.Contains("cannot start before"));
        errors.Should().Contain(e => e!.Contains("below salary structure minimum"));
        errors.Should().Contain(e => e!.Contains("grade is not eligible"));
        (await db.EmployeeSalaryStructures.AnyAsync()).Should().BeFalse();
    }

    /// <summary>
    /// Salary structures follow grades: a pay package is defined on its grade (Setup → Grades) and the grade's structure is
    /// derived from it (GradeSalaryStructureSync — GradePackageTests). Every structure write endpoint — create, update,
    /// delete and the CSV import — refuses with 409 salary_structures_follow_grades pointing at Setup, and writes nothing.
    /// </summary>
    [Fact]
    public async Task SalaryStructureWrites_RefuseAndPointToSetupGrades()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var structure = new SalaryStructure { TenantId = tenantId, Code = "STD", Name = "Standard", Currency = "AED", EffectiveDate = new DateOnly(2026, 1, 1), IsActive = true };
        db.SalaryStructures.Add(structure);
        await db.SaveChangesAsync();
        var ctrl = MakeCtrl(db, tenantId);
        var request = new SalaryStructureRequest("NEW", "New", "AED", new DateOnly(2026, 1, 1),
            new[] { new SalaryComponentRequest("BASIC", "Basic Salary", "Earning", "Fixed", 1000m, 0m, true) });
        const string csv = "CompanyLegalName,Code,Name,Currency,EffectiveDate,IsActive\n,IMP,Imported,AED,2026-01-01,true\n";

        foreach (var result in new[]
                 {
                     ctrl.CreateSalaryStructure(request), ctrl.UpdateSalaryStructure(structure.Id, request),
                     ctrl.DeleteSalaryStructure(structure.Id), ctrl.ImportStructures(new ImportSalaryStructuresRequest(csv)),
                 })
        {
            var conflict = Assert.IsType<ConflictObjectResult>(result);
            var body = conflict.Value!;
            body.GetType().GetProperty("error")!.GetValue(body).Should().Be(PayrollController.SalaryStructuresFollowGrades);
            ((string)body.GetType().GetProperty("message")!.GetValue(body)!).Should().Contain("Setup → Grades");
            body.GetType().GetProperty("setupHref")!.GetValue(body).Should().Be("/setup?tab=grades");
        }

        var stored = await db.SalaryStructures.IgnoreQueryFilters().Where(s => s.TenantId == tenantId).ToListAsync();
        stored.Should().ContainSingle().Which.Should().Match<SalaryStructure>(s => s.Name == "Standard" && s.IsActive && !s.IsDeleted);
        (await db.SalaryComponents.AnyAsync()).Should().BeFalse();

        // Reads stay: the list and the single structure.
        Assert.IsType<OkObjectResult>(await ctrl.SalaryStructures(null, CancellationToken.None));
        Assert.IsType<OkObjectResult>(await ctrl.GetSalaryStructure(structure.Id, CancellationToken.None));
    }

    [Fact]
    public async Task AssignEmployeeSalary_EnforcesStructureGrossRange()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn;
        await using var __ = db;

        var tenantId = Guid.NewGuid();
        var structure = new SalaryStructure
        {
            TenantId = tenantId,
            Code = "RANGE",
            Name = "Range",
            Currency = "AED",
            EffectiveDate = new DateOnly(2026, 1, 1),
            IsActive = true,
            MinGrossSalary = 10_000m,
            MaxGrossSalary = 20_000m
        };
        var employee = new Employee
        {
            TenantId = tenantId,
            EmployeeCode = "E-RANGE",
            FullName = "Range Employee",
            Status = "Active",
            JoiningDate = DateTime.UtcNow.AddYears(-1)
        };
        db.SalaryStructures.Add(structure);
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var ctrl = MakeCtrl(db, tenantId);
        var result = await ctrl.AssignEmployeeSalary(new EmployeeSalaryStructureRequest(
            employee.Id, structure.Id, 8_000m, 0m, 0m, 0m, 0m, 0m, 0m,
            new DateOnly(2026, 1, 1), "AED"), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        (await db.EmployeeSalaryStructures.AnyAsync()).Should().BeFalse();
    }

    /// <summary>A salary assigned without naming a structure goes on the employee's grade package structure, created from
    /// the grade when it does not exist yet — structures are no longer made by hand.</summary>
    [Fact]
    public async Task AssignEmployeeSalary_WithoutAStructure_UsesTheGradesPackageStructure()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn;
        await using var __ = db;
        var tenantId = Guid.NewGuid();
        var company = new Company { TenantId = tenantId, LegalNameEn = "Acme Arabia LLC", CountryCode = "SA", RegistrationNumber = "REG-9", DefaultCurrency = "SAR" };
        var grade = new Grade { TenantId = tenantId, CompanyId = company.Id, Code = "G3", Name = "Grade 3", MinSalary = 5_000m, MaxSalary = 20_000m, Currency = "SAR" };
        db.AddRange(company, grade);
        db.GradePayScaleComponents.Add(new GradePayScaleComponent { TenantId = tenantId, GradeId = grade.Id, ComponentCode = "BASIC", ComponentName = "Basic salary", Amount = 9_000m, IsTaxable = true });
        var employee = new Employee { TenantId = tenantId, CompanyId = company.Id, GradeId = grade.Id, EmployeeCode = "E-G3", FullName = "Graded", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1) };
        var gradeless = new Employee { TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "E-NG", FullName = "No grade", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1) };
        db.Employees.AddRange(employee, gradeless);
        await db.SaveChangesAsync();
        var ctrl = MakeCtrl(db, tenantId);

        var result = await ctrl.AssignEmployeeSalary(new EmployeeSalaryStructureRequest(
            employee.Id, null, 9_000m, 1_000m, 0m, 0m, 0m, 0m, 0m, new DateOnly(2026, 1, 1), "SAR"), CancellationToken.None);
        Assert.IsType<AcceptedResult>(result);
        (await db.EmployeeSalaryStructures.AnyAsync(a => a.EmployeeId == employee.Id)).Should().BeFalse("pending until Finance approves");
        await ApprovePendingSalaryChangesAsync(db, tenantId);
        var structure = await db.SalaryStructures.SingleAsync(s => s.Code == "GRADE-G3");
        structure.CompanyId.Should().Be(company.Id);
        (await db.SalaryComponents.SingleAsync(c => c.SalaryStructureId == structure.Id)).Code.Should().Be("BASIC");
        (await db.EmployeeSalaryStructures.SingleAsync(a => a.EmployeeId == employee.Id)).SalaryStructureId.Should().Be(structure.Id);

        Assert.IsType<AcceptedResult>(await ctrl.AssignEmployeeSalary(new EmployeeSalaryStructureRequest(
            gradeless.Id, null, 6_000m, 0m, 0m, 0m, 0m, 0m, 0m, new DateOnly(2026, 1, 1), "SAR"), CancellationToken.None));
        await ApprovePendingSalaryChangesAsync(db, tenantId);
        var direct = await db.SalaryStructures.SingleAsync(s => s.Code == "DIRECT");
        (await db.EmployeeSalaryStructures.SingleAsync(a => a.EmployeeId == gradeless.Id)).SalaryStructureId.Should().Be(direct.Id);

        // Payroll → Grade packages: the grade's package, its gross and its people; DIRECT (not a grade's) is listed apart.
        var view = (GradePackagesView)((OkObjectResult)await ctrl.GradePackages(null, CancellationToken.None)).Value!;
        view.Source.Should().Be("GradePayScale");
        var package = view.Grades.Should().ContainSingle().Subject;
        package.Should().Match<GradePackageDto>(p => p.GradeCode == "G3" && p.MonthlyGross == 9_000m && !p.OutsideRange
            && p.EmployeeCount == 1 && p.StructureCode == "GRADE-G3" && p.SalariesOnStructure == 1);
        package.Lines.Should().ContainSingle(l => l.Code == "BASIC" && l.PeriodAmount == 9_000m);
        view.OlderStructures.Should().ContainSingle(s => s.Code == "DIRECT" && s.AssignedEmployeeCount == 1);
    }

    // ── Section 10: Export writes audit log ───────────────────────────────────

    [Fact]
    public async Task EmployeeSalaryExport_WritesAuditLog()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var ctrl = MakeCtrl(db, tenantId, permissions: new[] { "payroll.export" });

        var result = await ctrl.ExportEmployeeSalaries(null, CancellationToken.None);

        Assert.IsType<FileContentResult>(result);
        var auditLog = await db.PayrollAuditLogs.FirstOrDefaultAsync(
            l => l.TenantId == tenantId && l.Action == "payroll.employee_salary.exported");
        auditLog.Should().NotBeNull("export must always produce an audit log");
    }

    [Fact]
    public async Task WpsStatus_SubmittedRequiresReference_AndAcceptedPersistsAcknowledgement()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        // D3: a payment batch always belongs to a payroll run — the run is where its legal entity comes
        // from. This fixture used to point PayrollRunId at a Guid with no row behind it, a state the
        // product cannot produce, and the batch endpoints now (correctly) refuse an unresolvable owner
        // rather than guessing one. The run is seeded so the test exercises WPS transitions, which is
        // what it is actually about. CompanyId is null and the test caller is group-level.
        var run = new PayrollRun
        {
            Id = Guid.NewGuid(), TenantId = tenantId, CompanyId = null,
            Year = 2026, Month = 7, Status = "Locked",
        };
        var batch = new PayrollPaymentBatch
        {
            TenantId = tenantId,
            PayrollRunId = run.Id,
            BatchNumber = "PAY-STAT-001",
            WpsStatus = WpsStatuses.Generated
        };
        db.PayrollRuns.Add(run);
        var file = new WPSFileBatch
        {
            TenantId = tenantId,
            PaymentBatchId = batch.Id,
            SifFileName = "wps.sif",
            FilingStatus = WpsStatuses.Generated,
            FileHash = "hash"
        };
        db.PayrollPaymentBatches.Add(batch);
        db.WPSFileBatches.Add(file);
        await db.SaveChangesAsync();

        var storage = new MemoryDocumentStorage();
        var ctrl = MakeCtrl(db, tenantId, permissions: new[] { "payroll.export" }, storage: storage);

        var missingReference = await ctrl.UpdateWpsStatus(batch.Id, new WpsStatusRequest(WpsStatuses.Submitted, null), CancellationToken.None);
        missingReference.Should().BeOfType<BadRequestObjectResult>();

        (await ctrl.UpdateWpsStatus(batch.Id, new WpsStatusRequest(WpsStatuses.Submitted, null, "SUB-123"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        // Accepted is no longer a dropdown choice: it needs stored evidence.
        (await ctrl.UpdateWpsStatus(batch.Id, new WpsStatusRequest(WpsStatuses.Accepted, null, "ACK-456"), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
        var bankOutput = System.Text.Encoding.UTF8.GetBytes("[DEST-ID]\tRJHI\t[FILE-REF]\t2026070101\n");
        var upload = await ctrl.UploadWpsEvidence(batch.Id, new WpsEvidenceUploadForm
        {
            Kind = Zayra.Api.Infrastructure.Payroll.WpsEvidenceKinds.BankOutputFile,
            File = new FormFile(new MemoryStream(bankOutput), 0, bankOutput.Length, "file", "wps-out.txt")
                { Headers = new HeaderDictionary(), ContentType = "text/plain" },
        }, CancellationToken.None);
        var evidenceId = (Guid)upload.Should().BeOfType<OkObjectResult>().Subject.Value!
            .GetType().GetProperty("EvidenceId")!.GetValue(((OkObjectResult)upload).Value)!;
        // Maker-checker: whoever uploaded the evidence may not record Accepted; a second person does.
        (await ctrl.UpdateWpsStatus(batch.Id, new WpsStatusRequest(WpsStatuses.Accepted, null, "ACK-456", evidenceId), CancellationToken.None))
            .Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        var checker = MakeCtrl(db, tenantId, permissions: new[] { "payroll.export" }, storage: storage);
        (await checker.UpdateWpsStatus(batch.Id, new WpsStatusRequest(WpsStatuses.Accepted, null, "ACK-456", evidenceId), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();

        var savedBatch = await db.PayrollPaymentBatches.FindAsync(batch.Id);
        var savedFile = await db.WPSFileBatches.FindAsync(file.Id);
        savedBatch!.WpsStatus.Should().Be(WpsStatuses.Accepted);
        savedBatch.WpsSubmissionReference.Should().Be("ACK-456");
        savedFile!.FilingStatus.Should().Be(WpsStatuses.Accepted);
        savedFile.AcknowledgedAtUtc.Should().NotBeNull();
        savedFile.SubmissionReference.Should().Be("ACK-456");
    }

    /// <summary>
    /// POD-D4 rewrote what this test asserts, and the rewrite IS the fix.
    ///
    /// <para>It used to assert that <c>UpdateErpPostingStatus</c> writes <c>Posted</c> off a free-text
    /// reference and stamps it onto the run's GL rows — with no file, no hash, and nothing that was ever
    /// exported anywhere. That is the exact hole POD-D4 closes: the two statuses that ASSERT a journal was
    /// produced and accepted by an ERP now require an artifact, and this endpoint refuses them with
    /// 422 evidence_required. The reachable-by-hand states (ReadyForErp, Rejected) still work here, and
    /// the gl_not_posted precondition is unchanged.</para>
    /// </summary>
    [Fact]
    public async Task ErpPostingStatus_RequiresGlAndRefusesPostedWithoutExportEvidence()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var runWithoutGl = new PayrollRun { TenantId = tenantId, Year = 2026, Month = 7, Status = "Locked", ErpPostingStatus = ErpPostingStatuses.ReadyForErp };
        var runWithGl = new PayrollRun { TenantId = tenantId, Year = 2026, Month = 8, Status = "Locked", ErpPostingStatus = ErpPostingStatuses.ReadyForErp };
        db.PayrollRuns.AddRange(runWithoutGl, runWithGl);
        db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = tenantId,
            SourceModule = "Payroll",
            SourceEntityId = runWithGl.Id,
            SourceEntityRef = "2026-08",
            EventType = "PayrollLock",
            DebitAccount = "5001 - Salary Expense",
            Amount = 100m,
            Period = "2026-08",
            EntryDate = new DateOnly(2026, 8, 31),
            ErpPostingStatus = ErpPostingStatuses.ReadyForErp
        });
        await db.SaveChangesAsync();

        var ctrl = MakeCtrl(db, tenantId, permissions: new[] { "payroll.export" });

        // Precondition unchanged: a run with no live accrual has no journal at all.
        var noGl = await ctrl.UpdateErpPostingStatus(runWithoutGl.Id, new ErpPostingStatusRequest(ErpPostingStatuses.ReadyForErp), CancellationToken.None);
        noGl.Should().BeOfType<BadRequestObjectResult>();

        // POD-D4: Posted (and Exported) are no longer producible by hand — they need an export artifact.
        var posted = await ctrl.UpdateErpPostingStatus(runWithGl.Id, new ErpPostingStatusRequest(ErpPostingStatuses.Posted, "ERP-2026-08"), CancellationToken.None);
        posted.Should().BeOfType<UnprocessableEntityObjectResult>();
        System.Text.Json.JsonSerializer.Serialize(((UnprocessableEntityObjectResult)posted).Value)
            .Should().Contain("evidence_required");

        var stillReady = await db.PayrollRuns.FindAsync(runWithGl.Id);
        stillReady!.ErpPostingStatus.Should().Be(ErpPostingStatuses.ReadyForErp,
            "a bare status write must not be able to claim the journal reached an ERP");
        var untouchedGl = await db.FinanceGlEntries.SingleAsync(x => x.SourceEntityId == runWithGl.Id);
        untouchedGl.ErpDocumentNumber.Should().BeNull();

        // Operational intent still belongs here, and still persists.
        var rejected = await ctrl.UpdateErpPostingStatus(runWithGl.Id,
            new ErpPostingStatusRequest(ErpPostingStatuses.Rejected, null, "ERP refused the batch"), CancellationToken.None);
        rejected.Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await db.PayrollRuns.FindAsync(runWithGl.Id))!.ErpPostingStatus.Should().Be(ErpPostingStatuses.Rejected);
    }

    // ── Section 11: AI Insight Engine — anomaly generation ───────────────────
    // Tests call AnalyzeTenantAsync via reflection to bypass the per-tenant
    // try-catch that swallows exceptions when using NullLogger.

    [Fact]
    public async Task AiInsightEngine_MissingSalarySetup_GeneratesInsight()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();

        db.Employees.AddRange(
            new Employee { TenantId = tenantId, EmployeeCode = "E1", FullName = "Alice", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1) },
            new Employee { TenantId = tenantId, EmployeeCode = "E2", FullName = "Bob", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1) },
            new Employee { TenantId = tenantId, EmployeeCode = "E3", FullName = "Carol", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1) });
        await db.SaveChangesAsync();

        // Verify the employee query works before calling the engine
        var empCount = await db.Employees
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && e.Status == "Active")
            .CountAsync();
        empCount.Should().Be(3);

        var engine = new AiInsightEngine(new _TestScopeFactory(db), NullLogger<AiInsightEngine>.Instance);
        await InvokeAnalyzeTenant(engine, db, tenantId);

        var insights = await db.AIInsights
            .Where(i => i.TenantId == tenantId && i.InsightType == "MissingSalarySetup")
            .ToListAsync();
        insights.Should().NotBeEmpty("3 employees have no salary assignment → engine must flag it");
        insights[0].Severity.Should().BeOneOf("Warning", "Critical");
        insights[0].Module.Should().Be("Payroll");
    }

    [Fact]
    public async Task AiInsightEngine_PayrollVariance_GeneratesInsightAboveThreshold()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();

        // 3-month baseline at 10 000, latest month at 14 000 (40% increase → Critical)
        var baseDate = DateTime.UtcNow.AddMonths(-3);
        for (var i = 0; i < 3; i++)
        {
            db.PayrollRuns.Add(new PayrollRun
            {
                TenantId = tenantId, Year = baseDate.AddMonths(i).Year,
                Month = baseDate.AddMonths(i).Month, Status = "Locked",
                TotalGrossSalary = 11000, TotalNetSalary = 10000
            });
        }
        db.PayrollRuns.Add(new PayrollRun
        {
            TenantId = tenantId, Year = DateTime.UtcNow.Year, Month = DateTime.UtcNow.Month,
            Status = "Locked", TotalGrossSalary = 15000, TotalNetSalary = 14000
        });
        await db.SaveChangesAsync();

        var engine = new AiInsightEngine(new _TestScopeFactory(db), NullLogger<AiInsightEngine>.Instance);
        await InvokeAnalyzeTenant(engine, db, tenantId);

        var insight = await db.AIInsights
            .FirstOrDefaultAsync(i => i.TenantId == tenantId && i.InsightType == "PayrollVariance");
        insight.Should().NotBeNull("40% payroll variance must trigger an insight");
        insight!.Severity.Should().Be("Critical", "40% exceeds the 20% critical threshold");
    }

    [Fact]
    public async Task AiInsightEngine_BelowVarianceThreshold_NoInsightGenerated()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();

        // All 4 runs at the same net salary — 0% variance
        var baseDate = DateTime.UtcNow.AddMonths(-3);
        for (var i = 0; i < 4; i++)
        {
            db.PayrollRuns.Add(new PayrollRun
            {
                TenantId = tenantId, Year = baseDate.AddMonths(i).Year,
                Month = baseDate.AddMonths(i).Month, Status = "Locked",
                TotalGrossSalary = 12000, TotalNetSalary = 10000
            });
        }
        await db.SaveChangesAsync();

        var engine = new AiInsightEngine(new _TestScopeFactory(db), NullLogger<AiInsightEngine>.Instance);
        await InvokeAnalyzeTenant(engine, db, tenantId);

        var insight = await db.AIInsights
            .FirstOrDefaultAsync(i => i.TenantId == tenantId && i.InsightType == "PayrollVariance");
        insight.Should().BeNull("0% variance must not produce an insight");
    }

    // ── Section 12: AI Insight Engine — deduplication ─────────────────────────

    [Fact]
    public async Task AiInsightEngine_Deduplication_SameTypeNotCreatedWithin24h()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();

        db.Employees.AddRange(
            new Employee { TenantId = tenantId, EmployeeCode = "X1", FullName = "Alice", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1) },
            new Employee { TenantId = tenantId, EmployeeCode = "X2", FullName = "Bob", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1) });
        await db.SaveChangesAsync();

        var engine = new AiInsightEngine(new _TestScopeFactory(db), NullLogger<AiInsightEngine>.Instance);

        // First analysis pass — should create the insight
        await InvokeAnalyzeTenant(engine, db, tenantId);
        var countAfterFirst = await db.AIInsights
            .CountAsync(i => i.TenantId == tenantId && i.InsightType == "MissingSalarySetup");
        countAfterFirst.Should().Be(1);

        // Second pass immediately after — deduplicated within 24h
        await InvokeAnalyzeTenant(engine, db, tenantId);
        var countAfterSecond = await db.AIInsights
            .CountAsync(i => i.TenantId == tenantId && i.InsightType == "MissingSalarySetup");
        countAfterSecond.Should().Be(1, "deduplication window is 24h; second run must not create a duplicate");
    }

    [Fact]
    public async Task AiInsightEngine_CrossTenantIsolation_InsightsNotLeakedAcrossTenants()
    {
        var db = CreateDb();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        // Only tenant A has employees without salary
        db.Employees.Add(new Employee
        {
            TenantId = tenantA, EmployeeCode = "A1", FullName = "Alice",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1)
        });
        await db.SaveChangesAsync();

        var engine = new AiInsightEngine(new _TestScopeFactory(db), NullLogger<AiInsightEngine>.Instance);

        // Analyze tenant A
        await InvokeAnalyzeTenant(engine, db, tenantA);
        // Analyze tenant B (no anomalies)
        await InvokeAnalyzeTenant(engine, db, tenantB);

        var tenantBInsights = await db.AIInsights.Where(i => i.TenantId == tenantB).ToListAsync();
        tenantBInsights.Should().BeEmpty("tenant B has no anomalies — no insights must be written for it");
    }

    // ── F06/I01: salary coverage is not payment readiness ────────────────────
    // QA (Evostel, Sep 2026): 250 active employees at 100% salary coverage read as ready, and the first
    // validation of the run then raised 250 missing-IBAN errors that block approval. Readiness must
    // name those gaps per employee before a run exists, separately from recommendations.

    private static async Task<(Guid TenantId, Company Company, Employee[] Employees)> SeedPayableSaudiCompanyAsync(
        ZayraDbContext db, int employeeCount)
    {
        var tenantId = Guid.NewGuid();
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "Evostel Certification KSA", CountryCode = "SA",
            DefaultCurrency = "SAR", WpsEmployerId = "7001234567", IsActive = true,
        };
        var structure = new SalaryStructure
        {
            TenantId = tenantId, CompanyId = company.Id, Code = "STD", Name = "Standard",
            Currency = "SAR", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
        };
        db.AddRange(company, structure, new SalaryComponent
        {
            TenantId = tenantId, Code = "BASIC", Name = "Basic", ComponentType = "Earning",
            CalculationType = "Fixed", Amount = 5000, IsActive = true,
        });
        var employees = Enumerable.Range(1, employeeCount).Select(i => new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = $"EVO{i:0000}", FullName = $"Employee {i}",
            Status = "Active", Nationality = "Saudi", JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            // PAYABLE means payable under Saudi rules, which is what these tests assert. The employee now
            // inherits the Saudi company's jurisdiction instead of being stored with a blank country, so
            // the statutory identity that jurisdiction requires has to be here: the national ID to
            // activate, and the GOSI reference the PAY gate needs before contributions can be computed.
            // Without them every one of them is pay-blocked, for a reason none of these tests is about.
            CountryCode = "SA", IdNumber = $"10000000{i:00}", GosiReference = $"GOSI-{i:0000}",
            // KSA also requires the Qiwa contract before payroll (pay-stage, 2026-10-24).
            QiwaContractNumber = $"QIWA-{i:0000}",
        }).ToArray();
        db.Employees.AddRange(employees);
        await db.SaveChangesAsync();
        db.EmployeeSalaryStructures.AddRange(employees.Select(e => new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = e.Id, SalaryStructureId = structure.Id,
            BasicSalary = 5000, Currency = "SAR", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
        }));
        await db.SaveChangesAsync();
        return (tenantId, company, employees);
    }

    private static T Prop<T>(object body, string name) => (T)body.GetType().GetProperty(name)!.GetValue(body)!;

    [Fact]
    public async Task Readiness_FullSalaryCoverage_WithoutBankDetails_IsNotReadyToPay()
    {
        var db = CreateDb();
        var (tenantId, company, _) = await SeedPayableSaudiCompanyAsync(db, 3);

        var result = await MakeCtrl(db, tenantId).PayrollReadiness(company.Id, 2025, 7, CancellationToken.None);
        var body = Assert.IsType<OkObjectResult>(result).Value!;

        Prop<double>(body, "SalaryCoveragePercent").Should().Be(100);
        Prop<bool>(body, "IsReadyForProcessing").Should().BeTrue("coverage alone still permits processing");
        Prop<bool>(body, "IsReadyToPay").Should().BeFalse("no employee has bank details, so none can be paid");

        var pre = Prop<Zayra.Api.Infrastructure.Payroll.PayrollPaymentPrerequisitesDto>(body, "PaymentPrerequisites");
        pre.EvaluatedEmployees.Should().Be(3);
        pre.BlockedEmployees.Should().Be(3);
        var blocking = pre.Blocking.Should().ContainSingle().Subject;
        blocking.Code.Should().Be("MISSING_PAYROLL_PROFILE");
        blocking.Count.Should().Be(3);
        blocking.NextAction.Should().NotBeNullOrWhiteSpace();
        pre.Employees.Select(e => e.EmployeeCode).Should().Equal("EVO0001", "EVO0002", "EVO0003");
        pre.Employees.Should().OnlyContain(e => e.Blocking.Contains("MISSING_PAYROLL_PROFILE"));
        // Recommendations never leak into the blocking list, and vice versa.
        pre.Recommended.Select(r => r.Code).Should().Equal("WARN_NO_ATTENDANCE");
        pre.Recommended.Should().NotContain(r => pre.Blocking.Any(b => b.Code == r.Code));
    }

    [Fact]
    public async Task Readiness_EveryPrerequisiteMet_IsReadyToPay_AndStaysPerEmployeeWhenOneLapses()
    {
        var db = CreateDb();
        var (tenantId, company, employees) = await SeedPayableSaudiCompanyAsync(db, 2);
        db.EmployeePayrollProfiles.AddRange(employees.Select(e => new EmployeePayrollProfile
        {
            TenantId = tenantId, EmployeeId = e.Id, Iban = "SA0380000000608010167519", MolId = $"MOL-{e.Id}", SalaryCurrency = "SAR",
        }));
        db.AttendanceDailyRecords.AddRange(employees.Select(e => new AttendanceDailyRecord
        {
            TenantId = tenantId, EmployeeId = e.Id, WorkDate = new DateOnly(2025, 7, 1), Status = "Present",
        }));
        await db.SaveChangesAsync();

        var ctrl = MakeCtrl(db, tenantId);
        var ready = Assert.IsType<OkObjectResult>(await ctrl.PayrollReadiness(company.Id, 2025, 7, CancellationToken.None)).Value!;
        var readyPre = Prop<Zayra.Api.Infrastructure.Payroll.PayrollPaymentPrerequisitesDto>(ready, "PaymentPrerequisites");
        readyPre.Blocking.Should().BeEmpty();
        readyPre.Recommended.Should().BeEmpty();
        readyPre.CompanyBlocking.Should().BeEmpty();
        Prop<bool>(ready, "IsReadyToPay").Should().BeTrue();

        // One IBAN is blanked and one MOL ID removed: a blocker for one employee, a recommendation for the other.
        var profiles = db.EmployeePayrollProfiles.OrderBy(p => p.EmployeeId).ToList();
        profiles[0].Iban = "";
        profiles[1].MolId = "";
        await db.SaveChangesAsync();

        var lapsed = Assert.IsType<OkObjectResult>(await ctrl.PayrollReadiness(company.Id, 2025, 7, CancellationToken.None)).Value!;
        var pre = Prop<Zayra.Api.Infrastructure.Payroll.PayrollPaymentPrerequisitesDto>(lapsed, "PaymentPrerequisites");
        Prop<bool>(lapsed, "IsReadyToPay").Should().BeFalse();
        pre.BlockedEmployees.Should().Be(1);
        pre.Blocking.Should().ContainSingle(b => b.Code == "MISSING_IBAN" && b.Count == 1);
        pre.Recommended.Should().ContainSingle(r => r.Code == "MISSING_MOL_ID" && r.Count == 1);
        pre.Employees.Single(e => e.EmployeeId == employees[0].Id).Blocking.Should().Equal("MISSING_IBAN");
        pre.Employees.Single(e => e.EmployeeId == employees[1].Id).Blocking.Should().BeEmpty();
        pre.Employees.Single(e => e.EmployeeId == employees[1].Id).Recommended.Should().Equal("MISSING_MOL_ID");
    }

    [Fact]
    public async Task Readiness_Prerequisites_NeverCountAnotherTenantsBankDetailsOrAttendance()
    {
        var db = CreateDb();
        var (tenantId, company, employees) = await SeedPayableSaudiCompanyAsync(db, 1);
        var otherTenant = Guid.NewGuid();
        // Rows that name tenant A's employee id but belong to tenant B must not satisfy tenant A.
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
        {
            TenantId = otherTenant, EmployeeId = employees[0].Id, Iban = "SA0380000000608010167519", MolId = "X",
        });
        db.AttendanceDailyRecords.Add(new AttendanceDailyRecord
        {
            TenantId = otherTenant, EmployeeId = employees[0].Id, WorkDate = new DateOnly(2025, 7, 1), Status = "Present",
        });
        await db.SaveChangesAsync();

        var body = Assert.IsType<OkObjectResult>(await MakeCtrl(db, tenantId).PayrollReadiness(company.Id, 2025, 7, CancellationToken.None)).Value!;
        var pre = Prop<Zayra.Api.Infrastructure.Payroll.PayrollPaymentPrerequisitesDto>(body, "PaymentPrerequisites");

        pre.Employees.Single().Blocking.Should().Equal("MISSING_PAYROLL_PROFILE");
        pre.Employees.Single().Recommended.Should().Equal("WARN_NO_ATTENDANCE");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Calls the internal AnalyzeTenantAsync directly (InternalsVisibleTo in AssemblyInfo.cs),
    // bypassing the per-tenant try-catch so test failures surface as real exceptions.
    private static Task InvokeAnalyzeTenant(AiInsightEngine engine, ZayraDbContext db, Guid tenantId)
        => engine.AnalyzeTenantAsync(db, null, tenantId, CancellationToken.None);

    private static IServiceScopeFactory CreateScopeFactory(ZayraDbContext db) => new _TestScopeFactory(db);
}

// ── File-scoped test stubs ────────────────────────────────────────────────────

file sealed class _UnrestrictedScope : IDataScopeService
{
    public Task<DataScope> ResolveAsync(System.Security.Claims.ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
}

file sealed class _HttpAccessor : IHttpContextAccessor
{
    public _HttpAccessor(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _NullNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
}

// No-op pack resolver — returns default (zero-result) implementations. Only used in PayrollModule
// unit tests that do not exercise the statutory calculation or WPS export paths.
file sealed class _NullPackResolver : Zayra.Api.Application.CountryPack.ICountryPackResolver
{
    public Zayra.Api.Application.CountryPack.IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => new Zayra.Api.Infrastructure.CountryPack.DefaultStatutoryDeductionCalculator();
    public Zayra.Api.Application.CountryPack.IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j)
        => new Zayra.Api.Infrastructure.CountryPack.DefaultEndOfServiceCalculator();
    public Zayra.Api.Application.CountryPack.IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j)
        => new Zayra.Api.Infrastructure.CountryPack.DefaultWageProtectionExporter();
    public Zayra.Api.Application.CountryPack.INationalizationTracker ResolveNationalizationTracker(string cc, string j)
        => new Zayra.Api.Infrastructure.CountryPack.DefaultNationalizationTracker();
    public Zayra.Api.Application.CountryPack.ILocalizationProfile ResolveLocalizationProfile(string cc, string j)
        => new Zayra.Api.Infrastructure.CountryPack.DefaultLocalizationProfile();
    public Zayra.Api.Application.CountryPack.ICountryPackDescriptor ResolveDescriptor(string cc, string j)
        => new Zayra.Api.Infrastructure.CountryPack.DefaultCountryPackDescriptor();
}

file sealed class _ModuleZeroPackResolver : Zayra.Api.Application.CountryPack.ICountryPackResolver
{
    public Zayra.Api.Application.CountryPack.IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => new _ModuleZeroDeductionCalculator();
    public Zayra.Api.Application.CountryPack.IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j)
        => new Zayra.Api.Infrastructure.CountryPack.DefaultEndOfServiceCalculator();
    public Zayra.Api.Application.CountryPack.IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j)
        => new Zayra.Api.Infrastructure.CountryPack.DefaultWageProtectionExporter();
    public Zayra.Api.Application.CountryPack.INationalizationTracker ResolveNationalizationTracker(string cc, string j)
        => new Zayra.Api.Infrastructure.CountryPack.DefaultNationalizationTracker();
    public Zayra.Api.Application.CountryPack.ILocalizationProfile ResolveLocalizationProfile(string cc, string j)
        => new Zayra.Api.Infrastructure.CountryPack.DefaultLocalizationProfile();
    public Zayra.Api.Application.CountryPack.ICountryPackDescriptor ResolveDescriptor(string cc, string j)
        => new Zayra.Api.Infrastructure.CountryPack.DefaultCountryPackDescriptor();
}

file sealed class _ModuleZeroDeductionCalculator : Zayra.Api.Application.CountryPack.IStatutoryDeductionCalculator
{
    public Task<Zayra.Api.Application.CountryPack.StatutoryDeductionResult> CalculateAsync(
        Zayra.Api.Application.CountryPack.StatutoryDeductionInput input,
        CancellationToken ct = default)
        => Task.FromResult(new Zayra.Api.Application.CountryPack.StatutoryDeductionResult(
            0m, 0m, Array.Empty<Zayra.Api.Application.CountryPack.StatutoryDeductionLine>()));
}

// Minimal IServiceScopeFactory that hands a fixed ZayraDbContext to any scope.
// The engine calls CreateAsyncScope() twice (outer query + per-tenant), both get same DB.
file sealed class _TestScopeFactory : IServiceScopeFactory
{
    private readonly ZayraDbContext _db;
    public _TestScopeFactory(ZayraDbContext db) => _db = db;
    public IServiceScope CreateScope() => new _TestScope(_db);
}

file sealed class _TestScope : IServiceScope
{
    public _TestScope(ZayraDbContext db) => ServiceProvider = new _TestProvider(db);
    public IServiceProvider ServiceProvider { get; }
    public void Dispose() { }
}

file sealed class _TestProvider : IServiceProvider
{
    private readonly ZayraDbContext _db;
    public _TestProvider(ZayraDbContext db) => _db = db;
    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(ZayraDbContext)) return _db;
        // ILlmClient is optional in the engine (GetService, not GetRequiredService)
        return null;
    }
}

file sealed class _NullLetterService : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}
