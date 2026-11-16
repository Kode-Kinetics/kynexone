using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// A loan balance a final settlement could not recover is carried on 1420 until someone decides it will not be collected. Writing it
/// off is a maker-checker journal: one person requests it with a reason, a DIFFERENT person approves it, and only then does
/// DR 5114 Employee Receivable Write-off Expense / CR 1420 post. It never posts into a closed period, never for more than 1420
/// carries, never for a figure other than the one approved, and the receivable stops being netted into any later run.
/// </summary>
public class ReceivableWriteOffTests
{
    private const string Receivable1420 = "1420 - Employee Overpayment Receivable";
    private const string Loan1400 = "1400 - Employee Loans Receivable";
    private const string WriteOffExpense = "5114 - Employee Receivable Write-off Expense";

    private static (ZayraDbContext db, SqliteConnection conn) NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static GlAdjustmentsController Gl(ZayraDbContext db, Guid tenantId, Guid userId, params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()), new(ClaimTypes.NameIdentifier, userId.ToString()), new(ClaimTypes.Name, "finance"),
        };
        claims.AddRange((permissions.Length == 0 ? new[] { "finance.gl.manage", "finance.gl.read" } : permissions).Select(p => new Claim("permission", p)));
        return new GlAdjustmentsController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } },
        };
    }

    private sealed record Fixture(Guid TenantId, Guid CompanyId, Guid ReceivableId);

    /// <summary>The state a final settlement leaves: 1,800 reclassified from the loan receivable to 1420, with its sub-ledger row.</summary>
    private static async Task<Fixture> SeedAsync(ZayraDbContext db, decimal amount = 1_800m, decimal recovered = 0m,
        string eventType = GlEventTypes.SettlementResidualReclass, bool onBooks = true)
    {
        var tenantId = Guid.NewGuid();
        var company = new Company { TenantId = tenantId, LegalNameEn = "Lender Co", CountryCode = "SAU", DefaultCurrency = "SAR", IsActive = true };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var receivable = new PayrollEmployeeReceivable
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeId = 7, EmployeeCode = "E007", SourceRunId = Guid.NewGuid(),
            EventType = eventType, Period = "2026-10", Amount = amount, RecoveredAmount = recovered,
            Status = PayrollReceivableStatuses.Outstanding,
        };
        db.PayrollEmployeeReceivables.Add(receivable);
        if (onBooks)
            db.FinanceGlEntries.Add(new FinanceGlEntry
            {
                TenantId = tenantId, CompanyId = company.Id, SourceModule = "FinalSettlement", SourceEntityId = Guid.NewGuid(),
                EventType = GlEventTypes.SettlementResidualReclass, DebitAccount = Receivable1420, CreditAccount = Loan1400,
                Amount = amount, Currency = "SAR", EntryDate = new DateOnly(2026, 10, 31), Period = "2026-10", Description = "Residual reclass",
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Fixture(tenantId, company.Id, receivable.Id);
    }

    private static ReceivableWriteOffRequest Request(Fixture f, string? reason = "Employee untraceable; recovery abandoned") =>
        new(f.CompanyId, f.ReceivableId, new DateOnly(2026, 11, 5), reason);

    private static string Json(IActionResult r) => JsonSerializer.Serialize((r as ObjectResult)?.Value);

    [Fact]
    public async Task Request_IsPendingAndPostsNothing_UntilASecondPersonApproves()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var f = await SeedAsync(db);
        var maker = Guid.NewGuid();

        var created = await Gl(db, f.TenantId, maker).RequestReceivableWriteOff(Request(f), CancellationToken.None);

        created.Should().BeOfType<OkObjectResult>(Json(created));
        var row = await db.GlAdjustmentRequests.AsNoTracking().SingleAsync();
        row.Kind.Should().Be(GlAdjustmentKinds.ReceivableWriteOff);
        row.Status.Should().Be(GlAdjustmentStatuses.Pending);
        row.Amount.Should().Be(1_800m);
        row.SubjectId.Should().Be(f.ReceivableId);
        row.SourceAccount.Should().Be(Receivable1420);
        row.TargetAccount.Should().Be(WriteOffExpense);
        (await db.FinanceGlEntries.AnyAsync(x => x.SourceEntityId == row.Id)).Should().BeFalse("nothing posts before the approval");
        (await db.PayrollEmployeeReceivables.AsNoTracking().SingleAsync()).Status.Should().Be(PayrollReceivableStatuses.Outstanding);
    }

    [Fact]
    public async Task Approval_ByTheRequester_IsRefused_AndByASecondPerson_PostsDr5114Cr1420_AndRetiresTheReceivable()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var f = await SeedAsync(db);
        var maker = Guid.NewGuid();
        (await Gl(db, f.TenantId, maker).RequestReceivableWriteOff(Request(f), CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        var id = (await db.GlAdjustmentRequests.AsNoTracking().SingleAsync()).Id;
        db.ChangeTracker.Clear();

        var self = await Gl(db, f.TenantId, maker).Approve(id, new GlAdjustmentDecisionRequest("ok"), CancellationToken.None);
        self.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403, "the requester can never approve their own write-off");
        (await db.FinanceGlEntries.AnyAsync(x => x.SourceEntityId == id)).Should().BeFalse();
        db.ChangeTracker.Clear();

        var approved = await Gl(db, f.TenantId, Guid.NewGuid()).Approve(id, new GlAdjustmentDecisionRequest("approved by finance"), CancellationToken.None);
        approved.Should().BeOfType<OkObjectResult>(Json(approved));
        db.ChangeTracker.Clear();

        var posted = await db.FinanceGlEntries.AsNoTracking().Where(x => x.SourceEntityId == id).ToListAsync();
        posted.Should().HaveCount(2);
        posted.Single(l => l.DebitAccount != string.Empty).Should().Match<FinanceGlEntry>(l =>
            l.DebitAccount == WriteOffExpense && l.Amount == 1_800m && l.EventType == GlEventTypes.ReceivableWriteOff);
        posted.Single(l => l.CreditAccount != string.Empty).Should().Match<FinanceGlEntry>(l =>
            l.CreditAccount == Receivable1420 && l.Amount == 1_800m && l.CompanyId == f.CompanyId);
        var all = await db.FinanceGlEntries.AsNoTracking().ToListAsync();
        GlControlAccounts.Balance(all, Receivable1420).Should().Be(0m, "the write-off empties the receivable it retires");
        GlControlAccounts.Balance(all, WriteOffExpense).Should().Be(1_800m, "5114 is a P&L expense");
        (await db.PayrollEmployeeReceivables.AsNoTracking().SingleAsync()).Status.Should().Be(PayrollReceivableStatuses.WrittenOff);
    }

    [Fact]
    public async Task Request_WithoutAReason_ForAnotherKindOfReceivable_OrOneAlreadyRecovered_IsRefused()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var f = await SeedAsync(db);

        (await Gl(db, f.TenantId, Guid.NewGuid()).RequestReceivableWriteOff(Request(f, reason: "  "), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>("a reason is mandatory");

        var voidRecovery = await SeedAsync(db, eventType: "NetSettlementReclass");
        (await Gl(db, voidRecovery.TenantId, Guid.NewGuid()).RequestReceivableWriteOff(Request(voidRecovery), CancellationToken.None))
            .Should().BeOfType<UnprocessableEntityObjectResult>("only a loan residual from a final settlement can be written off here");

        var recovered = await SeedAsync(db, amount: 500m, recovered: 500m);
        var row = await db.PayrollEmployeeReceivables.FirstAsync(r => r.Id == recovered.ReceivableId);
        row.Status = PayrollReceivableStatuses.Recovered;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        (await Gl(db, recovered.TenantId, Guid.NewGuid()).RequestReceivableWriteOff(Request(recovered), CancellationToken.None))
            .Should().BeOfType<UnprocessableEntityObjectResult>("a recovered receivable has nothing left to write off");
    }

    [Fact]
    public async Task Request_NeedsTheManagePermission_AndASecondPendingRequestForTheSameReceivableIsRefused()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var f = await SeedAsync(db);

        (await Gl(db, f.TenantId, Guid.NewGuid(), "finance.gl.read").RequestReceivableWriteOff(Request(f), CancellationToken.None))
            .Should().BeOfType<ForbidResult>("reading the ledger is not the right to propose writing it off");

        (await Gl(db, f.TenantId, Guid.NewGuid()).RequestReceivableWriteOff(Request(f), CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await Gl(db, f.TenantId, Guid.NewGuid()).RequestReceivableWriteOff(Request(f), CancellationToken.None))
            .Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Approval_IsRefused_IntoAClosedPeriod_WhenTheReceivableMovedSinceRequest_AndWhen1420CarriesLess()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var f = await SeedAsync(db);
        var maker = Guid.NewGuid();
        (await Gl(db, f.TenantId, maker).RequestReceivableWriteOff(Request(f), CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        var id = (await db.GlAdjustmentRequests.AsNoTracking().SingleAsync()).Id;

        // The receivable is partly recovered after the request: the figure approved is no longer the figure that would post.
        var rcv = await db.PayrollEmployeeReceivables.FirstAsync();
        rcv.RecoveredAmount = 300m;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        (await Gl(db, f.TenantId, Guid.NewGuid()).Approve(id, new GlAdjustmentDecisionRequest(null), CancellationToken.None))
            .Should().BeOfType<ConflictObjectResult>("approve the figure that posts");
        (await db.FinanceGlEntries.AnyAsync(x => x.SourceEntityId == id)).Should().BeFalse();

        // 1420 itself carries less than the sub-ledger says: the credit would drive an asset into credit.
        var g = await SeedAsync(db, onBooks: false);
        (await Gl(db, g.TenantId, maker).RequestReceivableWriteOff(Request(g), CancellationToken.None))
            .Should().BeOfType<UnprocessableEntityObjectResult>("1420 does not carry what the sub-ledger claims");
    }

    [Fact]
    public async Task Approval_IsRefused_WhenThePeriodClosedAfterTheRequest()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var f = await SeedAsync(db);
        (await Gl(db, f.TenantId, Guid.NewGuid()).RequestReceivableWriteOff(Request(f), CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        var id = (await db.GlAdjustmentRequests.AsNoTracking().SingleAsync()).Id;
        db.GlPeriodCloses.Add(new GlPeriodClose { TenantId = f.TenantId, CompanyId = f.CompanyId, Period = "2026-11", Status = GlPeriodStatuses.Closed });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await Gl(db, f.TenantId, Guid.NewGuid()).Approve(id, new GlAdjustmentDecisionRequest(null), CancellationToken.None);

        result.Should().BeOfType<UnprocessableEntityObjectResult>();
        (await db.FinanceGlEntries.AnyAsync(x => x.SourceEntityId == id)).Should().BeFalse();
    }

    /// <summary>Gives the receivable's employee (Id 7) a login, so that person can be the caller.</summary>
    private static async Task<Guid> LinkReceivableEmployeeAsync(ZayraDbContext db, Fixture f)
    {
        var login = Guid.NewGuid();
        db.Employees.Add(new Employee
        {
            Id = 7, TenantId = f.TenantId, CompanyId = f.CompanyId, EmployeeCode = "E007", FullName = "Owes Balance", EnglishName = "Owes Balance",
            Status = EmployeeStatuses.Active, UserAccountId = login,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return login;
    }

    [Fact]
    public async Task Request_ByTheEmployeeWhoseBalanceItIs_IsRefused()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var f = await SeedAsync(db);
        var subject = await LinkReceivableEmployeeAsync(db, f);

        var result = await Gl(db, f.TenantId, subject).RequestReceivableWriteOff(Request(f), CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403, Json(result));
        (await db.GlAdjustmentRequests.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Approval_ByTheEmployeeWhoseBalanceItIs_IsRefused_AndAnotherApproverStillPosts()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var f = await SeedAsync(db);
        var subject = await LinkReceivableEmployeeAsync(db, f);
        (await Gl(db, f.TenantId, Guid.NewGuid()).RequestReceivableWriteOff(Request(f), CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        var id = (await db.GlAdjustmentRequests.AsNoTracking().SingleAsync()).Id;
        db.ChangeTracker.Clear();

        var own = await Gl(db, f.TenantId, subject).Approve(id, new GlAdjustmentDecisionRequest("ok"), CancellationToken.None);
        own.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403, Json(own));
        (await db.FinanceGlEntries.AnyAsync(x => x.SourceEntityId == id)).Should().BeFalse();
        (await db.PayrollEmployeeReceivables.AsNoTracking().SingleAsync()).Status.Should().Be(PayrollReceivableStatuses.Outstanding);
        db.ChangeTracker.Clear();

        var other = await Gl(db, f.TenantId, Guid.NewGuid()).Approve(id, new GlAdjustmentDecisionRequest("approved by finance"), CancellationToken.None);
        other.Should().BeOfType<OkObjectResult>(Json(other));
    }

    [Fact]
    public void TheWriteOffExpenseAccountIsASeededSystemDriver_OnTheFreeCode5114()
    {
        PayrollGlCatalog.Defaults["RECEIVABLE_WRITE_OFF_EXPENSE"].Should().Be(("5114", "Employee Receivable Write-off Expense"));
        PayrollGlCatalog.Drivers.Where(d => d.DefaultCode == "5114").Should().ContainSingle("5114 belongs to exactly one driver");
        PayrollGlCatalog.SystemDriverSeeds(Guid.NewGuid()).Should().ContainSingle(d => d.Key == "RECEIVABLE_WRITE_OFF_EXPENSE")
            .Which.Category.Should().Be(GlDriverCategories.Balancing);
    }
}
