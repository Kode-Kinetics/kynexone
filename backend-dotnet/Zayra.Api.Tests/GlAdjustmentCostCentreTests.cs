using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Cost-accounting v1, WP-C3 (AM-36): the two maker-checker finance journals (EOSB actuarial true-up, legacy 2104/2105 reclass) take an
/// optional <c>costCenterId</c>. It is validated by the one cost-centre reference check, may sit on the profit-and-loss side only, obeys
/// the account's rule, and is stamped (id and the three snapshots) on that side's ledger line at approval. Costing off: nothing changes.
/// </summary>
public class GlAdjustmentCostCentreTests
{
    private const string Provision = "2310 - End of Service Benefit Provision";
    private const string Expense = "5110 - End of Service Benefit Expense";
    private const string Attendance = "2104 - Attendance Adjustment Payable";
    private const string AttendanceContra = "5008 - Attendance & Late Deductions (contra)";

    private static (ZayraDbContext db, SqliteConnection conn) NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static GlAdjustmentsController Gl(ZayraDbContext db, Guid tenantId, Guid userId)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()), new(ClaimTypes.NameIdentifier, userId.ToString()), new(ClaimTypes.Name, "finance"),
            new("permission", "finance.gl.manage"), new("permission", "finance.gl.read"),
        };
        return new GlAdjustmentsController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } },
        };
    }

    private sealed record Fx(Guid Tid, Company Co, CostCenter Cc, CostCenter OtherCompanyCc, CostCenter Inactive);

    private static async Task<Fx> SeedAsync(ZayraDbContext db, bool costing = true, string expenseRule = "Optional")
    {
        var tid = Guid.NewGuid();
        var co = new Company { TenantId = tid, LegalNameEn = "A Co", CountryCode = "SAU", DefaultCurrency = "SAR", IsActive = true, CostAccountingEnabled = costing };
        var other = new Company { TenantId = tid, LegalNameEn = "B Co", CountryCode = "SAU", DefaultCurrency = "SAR", IsActive = true };
        db.Companies.AddRange(co, other);
        await db.SaveChangesAsync();
        var cc = new CostCenter { TenantId = tid, CompanyId = co.Id, Code = "CC-100", Name = "Head office", GlSegment = "100", IsActive = true };
        var foreign = new CostCenter { TenantId = tid, CompanyId = other.Id, Code = "CC-900", Name = "Other", IsActive = true };
        var inactive = new CostCenter { TenantId = tid, CompanyId = co.Id, Code = "CC-OLD", Name = "Closed", IsActive = false };
        db.CostCenters.AddRange(cc, foreign, inactive);
        db.GlAccounts.AddRange(
            new GlAccount { TenantId = tid, CompanyId = co.Id, Code = "5110", Name = "End of Service Benefit Expense", AccountType = "Expense", CostCenterRule = expenseRule },
            new GlAccount { TenantId = tid, CompanyId = co.Id, Code = "2310", Name = "End of Service Benefit Provision", AccountType = "Liability", CostCenterRule = "Optional" },
            new GlAccount { TenantId = tid, CompanyId = co.Id, Code = "5008", Name = "Attendance & Late Deductions (contra)", AccountType = "Expense", CostCenterRule = "Optional" },
            new GlAccount { TenantId = tid, CompanyId = co.Id, Code = "2104", Name = "Attendance Adjustment Payable", AccountType = "Liability", CostCenterRule = "Optional" },
            new GlAccount { TenantId = tid, CompanyId = co.Id, Code = "3200", Name = "Retained Earnings", AccountType = "Equity", CostCenterRule = "Optional" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Fx(tid, co, cc, foreign, inactive);
    }

    private static async Task SeedLegacyBalanceAsync(ZayraDbContext db, Fx fx)
    {
        db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = fx.Tid, CompanyId = fx.Co.Id, SourceModule = "Payroll", SourceEntityId = Guid.NewGuid(), EventType = GlEventTypes.Accrual,
            DebitAccount = string.Empty, CreditAccount = Attendance, Amount = 700m, Currency = "SAR", EntryDate = new DateOnly(2026, 8, 31), Period = "2026-08", Description = "legacy",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static int Status(IActionResult r) => r is ObjectResult o ? o.StatusCode ?? 200 : 200;
    private static string Body(IActionResult r) => JsonSerializer.Serialize((r as ObjectResult)?.Value);
    private static string Code(IActionResult r) => JsonDocument.Parse(Body(r)).RootElement.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";

    private static async Task<Guid> RequestTrueUpAsync(ZayraDbContext db, Fx fx, Guid maker, Guid? cc, decimal amount = 1_000m)
    {
        var res = await Gl(db, fx.Tid, maker).RequestTrueUp(new EosbTrueUpRequest(fx.Co.Id, "2026-06", amount, "valuation", "ACT-1", cc), default);
        res.Should().BeOfType<OkObjectResult>(Body(res));
        db.ChangeTracker.Clear();
        return (await db.GlAdjustmentRequests.AsNoTracking().SingleAsync()).Id;
    }

    // ── true-up ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TrueUp_WithACostCentre_StampsTheExpenseLine_AndNotTheProvisionLine()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db);
        var id = await RequestTrueUpAsync(db, fx, Guid.NewGuid(), fx.Cc.Id);
        (await db.GlAdjustmentRequests.AsNoTracking().SingleAsync()).CostCenterId.Should().Be(fx.Cc.Id, "the request carries the choice the approver sees");

        (await Gl(db, fx.Tid, Guid.NewGuid()).Approve(id, null, default)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var posted = await db.FinanceGlEntries.AsNoTracking().Where(l => l.SourceEntityId == id).ToListAsync();
        var expense = posted.Single(l => l.DebitAccount == Expense);
        (expense.CostCenterId, expense.CostCenterCode, expense.CostCenterName, expense.CostCenterGlSegment).Should().Be((fx.Cc.Id, "CC-100", "Head office", "100"));
        var provision = posted.Single(l => l.CreditAccount == Provision);
        (provision.CostCenterId, provision.CostCenterCode).Should().Be(((Guid?)null, (string?)null), "the balance-sheet side is never costed");
    }

    [Fact]
    public async Task TrueUp_ThatLowersTheProvision_StampsTheCreditedExpenseLine()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db);
        db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = fx.Tid, CompanyId = fx.Co.Id, SourceModule = "Payroll", SourceEntityId = Guid.NewGuid(), EventType = GlEventTypes.EosbProvisionAccrual,
            DebitAccount = string.Empty, CreditAccount = Provision, Amount = 5_000m, Currency = "SAR", EntryDate = new DateOnly(2026, 5, 31), Period = "2026-05", Description = "seed",
        });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var id = await RequestTrueUpAsync(db, fx, Guid.NewGuid(), fx.Cc.Id, amount: -800m);

        (await Gl(db, fx.Tid, Guid.NewGuid()).Approve(id, null, default)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var posted = await db.FinanceGlEntries.AsNoTracking().Where(l => l.SourceEntityId == id).ToListAsync();
        posted.Single(l => l.CreditAccount == Expense).CostCenterId.Should().Be(fx.Cc.Id);
        posted.Single(l => l.DebitAccount == Provision).CostCenterId.Should().BeNull();
    }

    [Fact]
    public async Task TrueUp_WithoutACostCentre_PostsExactlyAsBefore()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db);
        var id = await RequestTrueUpAsync(db, fx, Guid.NewGuid(), null);
        (await Gl(db, fx.Tid, Guid.NewGuid()).Approve(id, null, default)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await db.FinanceGlEntries.AsNoTracking().Where(l => l.SourceEntityId == id).ToListAsync()).Should().OnlyContain(l => l.CostCenterId == null && l.CostCenterCode == null);
    }

    [Fact]
    public async Task TrueUp_WithCostingOff_PostsUncosted_EvenWhenACostCentreWasNamed()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db, costing: false);
        var id = await RequestTrueUpAsync(db, fx, Guid.NewGuid(), fx.Cc.Id);
        (await Gl(db, fx.Tid, Guid.NewGuid()).Approve(id, null, default)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await db.FinanceGlEntries.AsNoTracking().Where(l => l.SourceEntityId == id).ToListAsync()).Should().OnlyContain(l => l.CostCenterId == null, "the switch is the single on/off for costing");
    }

    [Theory]
    [InlineData("other", "cost_centre_other_company")]
    [InlineData("inactive", "cost_centre_inactive")]
    [InlineData("unknown", "cost_centre_not_found")]
    public async Task TrueUp_RefusesACostCentreThatIsNotAnActiveCostCentreOfThisCompany_AndSavesNothing(string which, string code)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db);
        var cc = which switch { "other" => fx.OtherCompanyCc.Id, "inactive" => fx.Inactive.Id, _ => Guid.NewGuid() };

        var res = await Gl(db, fx.Tid, Guid.NewGuid()).RequestTrueUp(new EosbTrueUpRequest(fx.Co.Id, "2026-06", 100m, "r", "ACT-1", cc), default);

        Status(res).Should().Be(422);
        Code(res).Should().Be(code);
        (await db.GlAdjustmentRequests.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TrueUp_OnANotAllowedExpenseAccount_IsRefusedWithM3_AndSavesNothing()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db, expenseRule: "NotAllowed");

        var res = await Gl(db, fx.Tid, Guid.NewGuid()).RequestTrueUp(new EosbTrueUpRequest(fx.Co.Id, "2026-06", 100m, "r", "ACT-1", fx.Cc.Id), default);

        Status(res).Should().Be(422);
        Code(res).Should().Be("cost_centre_not_allowed");
        Body(res).Should().Contain("5110").And.Contain("messageAr");
        (await db.GlAdjustmentRequests.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Approval_IsRefused_WhenTheCostCentreWasDeactivatedAfterTheRequest_AndTheRequestStaysPending()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db);
        var id = await RequestTrueUpAsync(db, fx, Guid.NewGuid(), fx.Cc.Id);
        await db.CostCenters.Where(c => c.Id == fx.Cc.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.IsActive, false));

        var res = await Gl(db, fx.Tid, Guid.NewGuid()).Approve(id, null, default);

        Status(res).Should().Be(422);
        Code(res).Should().Be("cost_centre_inactive");
        db.ChangeTracker.Clear();
        (await db.GlAdjustmentRequests.AsNoTracking().SingleAsync()).Status.Should().Be(GlAdjustmentStatuses.Pending);
        (await db.FinanceGlEntries.CountAsync(l => l.SourceEntityId == id)).Should().Be(0);
    }

    // ── legacy 2104/2105 reclass ─────────────────────────────────────────────────

    [Fact]
    public async Task Reclass_ToAContraExpense_StampsThatLine_AndNotTheLiabilityLine()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db);
        await SeedLegacyBalanceAsync(db, fx);
        var req = new LegacyAbsenceReclassRequest(fx.Co.Id, Attendance, new DateOnly(2026, 9, 30), null, "tidy", fx.Cc.Id);
        (await Gl(db, fx.Tid, Guid.NewGuid()).RequestLegacyReclass(req, default)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        var id = (await db.GlAdjustmentRequests.AsNoTracking().SingleAsync()).Id;

        (await Gl(db, fx.Tid, Guid.NewGuid()).Approve(id, null, default)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var posted = await db.FinanceGlEntries.AsNoTracking().Where(l => l.SourceEntityId == id).ToListAsync();
        var contra = posted.Single(l => l.CreditAccount == AttendanceContra);
        (contra.CostCenterId, contra.CostCenterCode, contra.CostCenterGlSegment).Should().Be((fx.Cc.Id, "CC-100", "100"));
        posted.Single(l => l.DebitAccount == Attendance).CostCenterId.Should().BeNull();
    }

    [Fact]
    public async Task Reclass_ToAnEquityAccount_WithACostCentre_IsRefusedBecauseThereIsNoProfitAndLossSide()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db);
        await SeedLegacyBalanceAsync(db, fx);

        var res = await Gl(db, fx.Tid, Guid.NewGuid()).RequestLegacyReclass(
            new LegacyAbsenceReclassRequest(fx.Co.Id, Attendance, new DateOnly(2026, 9, 30), "3200", "prior year", fx.Cc.Id), default);

        Status(res).Should().Be(422);
        Code(res).Should().Be("cost_centre_not_allowed");
        (await db.GlAdjustmentRequests.CountAsync()).Should().Be(0);
    }
}
