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
/// Attendance and unpaid-leave deductions used to CREDIT 2104 / 2105 — liabilities with no remittance
/// path, so they grew every month. They now credit the contra-expenses 5008 / 5009. What already sits in
/// 2104/2105 is moved only on an accountant's request and a second person's approval: never
/// automatically, never into a closed period, never for a zero balance, and never for a figure other
/// than the one approved.
/// </summary>
public class LegacyAbsenceReclassTests
{
    private const string Attendance = "2104 - Attendance Adjustment Payable";
    private const string Leave = "2105 - Leave Deduction Payable";
    private const string AttendanceContra = "5008 - Attendance & Late Deductions (contra)";

    private static (ZayraDbContext db, SqliteConnection conn) NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static GlAdjustmentsController Gl(ZayraDbContext db, Guid tenantId, Guid userId, string name = "finance")
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, name),
            new("permission", "finance.gl.manage"),
            new("permission", "finance.gl.read"),
        };
        var ctrl = new GlAdjustmentsController(db);
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
        };
        return ctrl;
    }

    private sealed record Fixture(Guid TenantId, Guid CompanyId, Guid OtherCompanyId);

    private static FinanceGlEntry Row(Guid tenantId, Guid? companyId, string debit, string credit, decimal amount) => new()
    {
        TenantId = tenantId, CompanyId = companyId, SourceModule = "Payroll", SourceEntityId = Guid.NewGuid(),
        EventType = GlEventTypes.Accrual, DebitAccount = debit, CreditAccount = credit, Amount = amount,
        Currency = "SAR", EntryDate = new DateOnly(2026, 8, 31), Period = "2026-08", Description = "Payroll deduction: LATE",
    };

    private static async Task<Fixture> SeedAsync(ZayraDbContext db)
    {
        var tenantId = Guid.NewGuid();
        var a = new Company { TenantId = tenantId, LegalNameEn = "A Co", CountryCode = "SAU", DefaultCurrency = "SAR", IsActive = true };
        var b = new Company { TenantId = tenantId, LegalNameEn = "B Co", CountryCode = "SAU", DefaultCurrency = "SAR", IsActive = true };
        db.Companies.AddRange(a, b);
        await db.SaveChangesAsync();
        db.FinanceGlEntries.AddRange(
            // Company A: 1,200 credited to 2104 over two months, 300 to 2105.
            Row(tenantId, a.Id, string.Empty, Attendance, 700m),
            Row(tenantId, a.Id, string.Empty, Attendance, 500m),
            Row(tenantId, a.Id, string.Empty, Leave, 300m),
            // Company B's balance is B's alone.
            Row(tenantId, b.Id, string.Empty, Attendance, 999m),
            // Posted before company attribution: reported, never moved by a company's reclass.
            Row(tenantId, null, string.Empty, Attendance, 50m));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Fixture(tenantId, a.Id, b.Id);
    }

    private static readonly JsonSerializerOptions Raw = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static string Json(IActionResult r) => JsonSerializer.Serialize((r as ObjectResult)?.Value, Raw);

    [Fact]
    public async Task Get_ReportsEachCompanysOwnBalances_WithTheContraExpenseAsTheSuggestedTarget()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db);

        var json = Json(await Gl(db, fx.TenantId, Guid.NewGuid()).GetLegacyAbsence(fx.CompanyId, CancellationToken.None));

        json.Should().Contain($"\"account\":\"{Attendance}\"").And.Contain("\"balance\":1200")
            .And.Contain($"\"suggestedTarget\":\"{AttendanceContra}\"")
            .And.Contain("\"balance\":300")
            .And.NotContain("999", "another company's balance is not this company's to move");
        json.Should().Contain("\"unattributed\":[{\"account\":\"2104 - Attendance Adjustment Payable\",\"code\":\"2104\",\"balance\":50");
    }

    [Fact]
    public async Task Reclass_IsPendingUntilASecondPersonApproves_ThenEmptiesTheLegacyAccount()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db);
        var maker = Guid.NewGuid();

        var created = await Gl(db, fx.TenantId, maker).RequestLegacyReclass(
            new LegacyAbsenceReclassRequest(fx.CompanyId, Attendance, new DateOnly(2026, 10, 10), null, "Absence deductions are not owed to anyone"),
            CancellationToken.None);
        created.Should().BeOfType<OkObjectResult>(Json(created));
        var request = await db.GlAdjustmentRequests.AsNoTracking().SingleAsync();
        request.Amount.Should().Be(1_200m);
        request.TargetAccount.Should().Be(AttendanceContra);
        (await db.FinanceGlEntries.AnyAsync(x => x.SourceEntityId == request.Id)).Should().BeFalse("never automatic");

        // The same balance cannot be requested twice.
        (await Gl(db, fx.TenantId, maker).RequestLegacyReclass(
            new LegacyAbsenceReclassRequest(fx.CompanyId, Attendance, new DateOnly(2026, 10, 10), null, "again"), CancellationToken.None))
            .Should().BeOfType<ConflictObjectResult>();

        var self = await Gl(db, fx.TenantId, maker).Approve(request.Id, null, CancellationToken.None);
        ((ObjectResult)self).StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        db.ChangeTracker.Clear();

        (await Gl(db, fx.TenantId, Guid.NewGuid(), "checker").Approve(request.Id, null, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var posted = await db.FinanceGlEntries.AsNoTracking().Where(x => x.SourceEntityId == request.Id).ToListAsync();
        posted.Single(l => l.DebitAccount == Attendance).Amount.Should().Be(1_200m);
        posted.Single(l => l.CreditAccount == AttendanceContra).Amount.Should().Be(1_200m);
        posted.Should().OnlyContain(l => l.EventType == GlEventTypes.LegacyAbsenceReclass && l.CompanyId == fx.CompanyId
                                      && l.Period == "2026-10");

        var after = Json(await Gl(db, fx.TenantId, Guid.NewGuid()).GetLegacyAbsence(fx.CompanyId, CancellationToken.None));
        after.Should().NotContain($"\"account\":\"{Attendance}\",\"code\":\"2104\",\"debits\"", "2104 is empty for company A now");
        (await db.AuditLogs.AsNoTracking().Select(a => a.Action).ToListAsync()).Should()
            .Contain(new[] { "finance.gl.legacy_absence_reclass.requested", "finance.gl.legacy_absence_reclass.approved" });

        // Company B's 2104 is untouched.
        var b = Json(await Gl(db, fx.TenantId, Guid.NewGuid()).GetLegacyAbsence(fx.OtherCompanyId, CancellationToken.None));
        b.Should().Contain("\"balance\":999");
    }

    [Fact]
    public async Task Reclass_RefusesAZeroBalance_AndAClosedPeriod()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db);

        (await Gl(db, fx.TenantId, Guid.NewGuid()).RequestLegacyReclass(
            new LegacyAbsenceReclassRequest(fx.CompanyId, "2104 - Some Other Label", new DateOnly(2026, 10, 10), null, "x"), CancellationToken.None))
            .Should().BeOfType<UnprocessableEntityObjectResult>().Which.Value.Should().BeEquivalentTo(new { error = "legacy_balance_zero" },
                o => o.ExcludingMissingMembers());

        db.GlPeriodCloses.Add(new GlPeriodClose { TenantId = fx.TenantId, CompanyId = null, Period = "2026-09", Status = GlPeriodStatuses.Closed });
        await db.SaveChangesAsync();
        var closed = await Gl(db, fx.TenantId, Guid.NewGuid()).RequestLegacyReclass(
            new LegacyAbsenceReclassRequest(fx.CompanyId, Attendance, new DateOnly(2026, 9, 30), null, "x"), CancellationToken.None);
        Json(closed).Should().Contain("gl_period_closed");
        (await db.GlAdjustmentRequests.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Approve_RefusesWhenTheBalanceMovedSinceTheRequest()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db);
        await Gl(db, fx.TenantId, Guid.NewGuid()).RequestLegacyReclass(
            new LegacyAbsenceReclassRequest(fx.CompanyId, Leave, new DateOnly(2026, 10, 10), null, "move it"), CancellationToken.None);
        var id = (await db.GlAdjustmentRequests.AsNoTracking().SingleAsync()).Id;
        db.FinanceGlEntries.Add(Row(fx.TenantId, fx.CompanyId, string.Empty, Leave, 25m));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await Gl(db, fx.TenantId, Guid.NewGuid()).Approve(id, null, CancellationToken.None);
        result.Should().BeOfType<ConflictObjectResult>();
        Json(result).Should().Contain("GL_ADJUSTMENT_BALANCE_CHANGED");
        (await db.FinanceGlEntries.AnyAsync(x => x.SourceEntityId == id)).Should().BeFalse();
    }

    [Fact]
    public async Task Reclass_ToRetainedEarnings_ForAPriorClosedYear_AndRefusesALiabilityTarget()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var fx = await SeedAsync(db);
        db.GlAccounts.AddRange(
            new GlAccount { TenantId = fx.TenantId, Code = "3200", Name = "Retained Earnings", AccountType = "Equity" },
            new GlAccount { TenantId = fx.TenantId, Code = "2199", Name = "Other Deductions", AccountType = "Liability" });
        await db.SaveChangesAsync();

        (await Gl(db, fx.TenantId, Guid.NewGuid()).RequestLegacyReclass(
            new LegacyAbsenceReclassRequest(fx.CompanyId, Attendance, new DateOnly(2026, 10, 10), "2199", "x"), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();

        (await Gl(db, fx.TenantId, Guid.NewGuid()).RequestLegacyReclass(
            new LegacyAbsenceReclassRequest(fx.CompanyId, Attendance, new DateOnly(2026, 10, 10), "3200", "FY2025 is closed"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        var id = (await db.GlAdjustmentRequests.AsNoTracking().SingleAsync()).Id;
        (await Gl(db, fx.TenantId, Guid.NewGuid()).Approve(id, null, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        (await db.FinanceGlEntries.AsNoTracking().SingleAsync(x => x.SourceEntityId == id && x.CreditAccount != ""))
            .CreditAccount.Should().Be("3200 - Retained Earnings");
    }
}
