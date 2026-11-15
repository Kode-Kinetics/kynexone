using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Audit P1-5: the advance sub-ledger (Σ OutstandingBalance over Active advances) and the 1410 Employee Salary Advances balance must
/// tie out per company. The control-accounts health endpoint and the advances audit report now report the comparison, the
/// difference, and a flag when the difference is not zero and not explained by the settlement reclass to 1420. Both responses keep
/// every field they had; the comparison is added.
/// </summary>
public class AdvanceReceivableTieOutTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string Adv1410 = "1410 - Employee Salary Advances";
    private const string Cash1000 = "1000 - Cash/Bank";
    private const string Emp1420 = "1420 - Employee Overpayment Receivable";

    [Fact]
    public async Task WhenTheSubLedgerAndTheGlAgree_TheDifferenceIsZero_AndNothingIsFlagged()
    {
        var (db, conn) = NewDb();
        await using var connScope = conn; await using var dbScope = db;
        var (tid, coA, coB) = await SeedAsync(db);
        Advance(db, tid, coA, "Active", 1_000m);
        Gl(db, tid, coA, Adv1410, Cash1000, 1_000m, "Disbursement");
        Advance(db, tid, coB, "Active", 400m);
        Gl(db, tid, coB, Adv1410, Cash1000, 700m, "Disbursement");
        Gl(db, tid, coB, "2107 - Loan & Advance Deductions Payable", Adv1410, 300m, "PayrollLock");
        Advance(db, tid, coB, "Approved", 900m, outstanding: 0m);   // not paid: in neither ledger
        await db.SaveChangesAsync();

        var rows = await AdvanceReceivableTieOut.ComputeAsync(db, tid, null, Ct);

        var a = rows.Single(r => r.CompanyId == coA);
        Assert.Equal((1_000m, 1_000m, 0m, false), (a.SubLedgerOutstanding, a.GlBalance, a.Difference, a.Flagged));
        Assert.Equal(Adv1410, a.Account);
        var b = rows.Single(r => r.CompanyId == coB);
        Assert.Equal((400m, 400m, 0m, false), (b.SubLedgerOutstanding, b.GlBalance, b.Difference, b.Flagged));
        Assert.True(rows.All(r => r.IsTiedOut));
    }

    [Fact]
    public async Task WhenTheyDisagree_TheDifferenceIsReported_PerCompany_AndFlagged()
    {
        var (db, conn) = NewDb();
        await using var connScope = conn; await using var dbScope = db;
        var (tid, coA, coB) = await SeedAsync(db);
        Advance(db, tid, coA, "Active", 1_000m);                     // a legacy seeded advance: no journal behind it
        Advance(db, tid, coB, "Active", 500m);
        Gl(db, tid, coB, Adv1410, Cash1000, 800m, "Disbursement");  // the GL carries 300 more than the sub-ledger
        await db.SaveChangesAsync();

        var rows = await AdvanceReceivableTieOut.ComputeAsync(db, tid, null, Ct);

        var a = rows.Single(r => r.CompanyId == coA);
        Assert.Equal((1_000m, 0m, -1_000m, true), (a.SubLedgerOutstanding, a.GlBalance, a.Difference, a.Flagged));
        var b = rows.Single(r => r.CompanyId == coB);
        Assert.Equal((500m, 800m, 300m, true), (b.SubLedgerOutstanding, b.GlBalance, b.Difference, b.Flagged));
    }

    [Fact]
    public async Task ADifferenceExactlyEqualToTheSettlementReclassTo1420_IsExplained_NotFlagged()
    {
        var (db, conn) = NewDb();
        await using var connScope = conn; await using var dbScope = db;
        var (tid, coA, _) = await SeedAsync(db);
        Advance(db, tid, coA, "Active", 1_000m);
        Gl(db, tid, coA, Adv1410, Cash1000, 1_000m, "Disbursement");
        Gl(db, tid, coA, Emp1420, Adv1410, 250m, GlEventTypes.SettlementResidualReclass);   // the GL moved 250 to 1420
        await db.SaveChangesAsync();

        var a = (await AdvanceReceivableTieOut.ComputeAsync(db, tid, new[] { coA }, Ct)).Single();

        Assert.Equal((-250m, 250m, true, false), (a.Difference, a.ReclassedTo1420, a.ExplainedByReclass, a.Flagged));
    }

    [Fact]
    public async Task ControlAccountHealth_KeepsItsShape_AndAddsTheAdvanceTieOut()
    {
        var (db, conn) = NewDb();
        await using var connScope = conn; await using var dbScope = db;
        var (tid, coA, _) = await SeedAsync(db);
        Advance(db, tid, coA, "Active", 1_000m);
        Gl(db, tid, coA, Adv1410, Cash1000, 600m, "Disbursement");
        await db.SaveChangesAsync();

        var result = await new FinanceGlController(db) { ControllerContext = Ctx(tid, "finance.gl.read") }.ControlAccountHealth(coA, Ct);

        var body = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);
        foreach (var field in new[] { "generatedAtUtc", "companyId", "healthy", "violations", "accounts" })
            Assert.True(body.TryGetProperty(field, out _), $"the existing field '{field}' is kept");
        var tieOut = body.GetProperty("advanceReceivableTieOut");
        Assert.False(tieOut.GetProperty("healthy").GetBoolean());
        var row = Assert.Single(tieOut.GetProperty("companies").EnumerateArray().ToList());
        Assert.Equal(coA, row.GetProperty("CompanyId").GetGuid());
        Assert.Equal(-400m, row.GetProperty("Difference").GetDecimal());
        Assert.True(row.GetProperty("Flagged").GetBoolean());
    }

    [Fact]
    public async Task AdvancesAuditReport_AddsTheTieOut_ForAnOrgWideReader_AndNotForATeamScopedOne()
    {
        var (db, conn) = NewDb();
        await using var connScope = conn; await using var dbScope = db;
        var (tid, coA, _) = await SeedAsync(db);
        Advance(db, tid, coA, "Active", 1_000m);
        Gl(db, tid, coA, Adv1410, Cash1000, 1_000m, "Disbursement");
        await db.SaveChangesAsync();

        var orgWide = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(
            await Controller(db, tid, "Finance", unrestricted: true).AuditReport(Ct)).Value);
        var rows = orgWide.GetProperty("ReceivableTieOut").EnumerateArray().ToList();
        var row = Assert.Single(rows, r => r.GetProperty("CompanyId").ValueKind != JsonValueKind.Null && r.GetProperty("CompanyId").GetGuid() == coA);
        Assert.Equal((1_000m, 1_000m, 0m), (row.GetProperty("SubLedgerOutstanding").GetDecimal(), row.GetProperty("GlBalance").GetDecimal(), row.GetProperty("Difference").GetDecimal()));
        Assert.All(rows, r => Assert.False(r.GetProperty("Flagged").GetBoolean()));
        Assert.True(orgWide.TryGetProperty("TotalOutstanding", out _));

        var team = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(
            await Controller(db, tid, "Manager", unrestricted: false).AuditReport(Ct)).Value);
        Assert.Equal(JsonValueKind.Null, team.GetProperty("ReceivableTieOut").ValueKind);
    }

    // ── Fixture ─────────────────────────────────────────────────────────────────────────────────────

    private static (ZayraDbContext db, SqliteConnection conn) NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static async Task<(Guid Tenant, Guid CompanyA, Guid CompanyB)> SeedAsync(ZayraDbContext db)
    {
        var tid = Guid.NewGuid();
        var a = new Company { TenantId = tid, LegalNameEn = "Co A", CountryCode = "SAU", DefaultCurrency = "SAR", IsActive = true };
        var b = new Company { TenantId = tid, LegalNameEn = "Co B", CountryCode = "SAU", DefaultCurrency = "SAR", IsActive = true };
        db.Companies.AddRange(a, b);
        await db.SaveChangesAsync();
        return (tid, a.Id, b.Id);
    }

    private static void Advance(ZayraDbContext db, Guid tid, Guid companyId, string status, decimal amount, decimal? outstanding = null) =>
        db.SalaryAdvances.Add(new SalaryAdvance
        {
            TenantId = tid, CompanyId = companyId, EmployeeId = Guid.NewGuid(), EmployeeIntId = 1, EmployeeName = "E",
            AdvanceNumber = $"ADV-T-{Guid.NewGuid():N}"[..16], RequestedAmount = amount, ApprovedAmount = amount,
            OutstandingBalance = outstanding ?? amount, Status = status,
        });

    private static void Gl(ZayraDbContext db, Guid tid, Guid companyId, string dr, string cr, decimal amount, string eventType) =>
        db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = tid, CompanyId = companyId, SourceModule = "Advance", SourceEntityId = Guid.NewGuid(), EventType = eventType,
            DebitAccount = dr, CreditAccount = cr, Amount = amount, Currency = "SAR",
            EntryDate = DateOnly.FromDateTime(DateTime.UtcNow), Period = DateTime.UtcNow.ToString("yyyy-MM"),
        });

    private static ControllerContext Ctx(Guid tid, params string[] permissions)
    {
        var claims = new List<Claim> { new("tenant_id", tid.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        return new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } };
    }

    private static AdvancesController Controller(ZayraDbContext db, Guid tid, string role, bool unrestricted)
    {
        var ctx = Ctx(tid, "loans.read");
        ((ClaimsIdentity)ctx.HttpContext.User.Identity!).AddClaim(new Claim(ClaimTypes.Role, role));
        return new AdvancesController(db, new Scope(unrestricted)) { ControllerContext = ctx };
    }

    private sealed class Scope(bool unrestricted) : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(unrestricted
                ? new DataScope { Level = DataScopeLevel.Organization }
                : new DataScope { Level = DataScopeLevel.Department, AllowedEmployeeIds = new[] { 1 } });
    }
}
