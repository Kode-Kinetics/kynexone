using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Models;
using K = Zayra.Api.Tests.OpeningBalanceImportKit;
using Kit = Zayra.Api.Tests.DebtRacePostgresKit;

namespace Zayra.Api.Tests;

/// <summary>
/// Loans and advances carried in before the import posted opening journals have none. A Finance or Admin user backfills them per
/// company: a dry-run preview lists each carried-in debt with no opening journal and what would post; apply posts DR 1400/1410 /
/// CR 3900 for the balance carried in at cutover, dated on the cutover date, through the import's own writer (so it is
/// idempotent: applying again posts nothing), refuses a debt whose cutover period is closed (per row, in both languages), and is
/// audited. Nothing runs it on deploy.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class OpeningBalanceJournalBackfillPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ThePreviewListsTheDebtsWithNoOpeningJournal_ApplyPostsThemOnce_AClosedPeriodIsRefusedPerRow_AndItIsAudited()
    {
        var (b, cutover, _) = await K.SeedAsync(fixture, "BF");
        var earlier = cutover.AddMonths(-1);
        var loanId = await SeedCarriedInLoanAsync(b, 300m, cutover);
        var advanceId = await SeedCarriedInAdvanceAsync(b, 200m, cutover);
        var closedAdvanceId = await SeedCarriedInAdvanceAsync(b, 50m, earlier);
        await K.ClosePeriodAsync(fixture, b, earlier);

        JsonElement preview;
        await using (var db = fixture.CreateDb())
            preview = Json(Assert.IsType<OkObjectResult>(await Controller(db, b.Tenant, "Finance Approver").Preview(b.CompanyId, default)));
        var rows = preview.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal(300m, Row(rows, loanId).GetProperty("toPost").GetDecimal());
        Assert.Equal("ready", Row(rows, advanceId).GetProperty("status").GetString());
        Assert.Equal("period_closed", Row(rows, closedAdvanceId).GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(Row(rows, closedAdvanceId).GetProperty("reasonAr").GetString()));
        await using (var db = fixture.CreateDb())
            Assert.False(await db.FinanceGlEntries.AnyAsync(g => g.TenantId == b.Tenant && g.EventType == K.OpeningBalanceEvent), "the preview posts nothing");

        JsonElement applied;
        await using (var db = fixture.CreateDb())
            applied = Json(Assert.IsType<OkObjectResult>(await Controller(db, b.Tenant, "Finance Approver").Apply(new OpeningBalanceBackfillRequest(b.CompanyId), default)));
        Assert.Equal(2, applied.GetProperty("posted").GetInt32());
        Assert.Equal(1, applied.GetProperty("refused").GetInt32());

        var loanLine = Assert.Single(await K.OpeningJournalAsync(fixture, b, loanId));
        Assert.Equal((300m, cutover), (loanLine.Amount, loanLine.EntryDate));
        Assert.StartsWith("1400", loanLine.DebitAccount, StringComparison.Ordinal);
        Assert.StartsWith("3900", loanLine.CreditAccount, StringComparison.Ordinal);
        var advanceLine = Assert.Single(await K.OpeningJournalAsync(fixture, b, advanceId));
        Assert.StartsWith("1410", advanceLine.DebitAccount, StringComparison.Ordinal);
        Assert.Empty(await K.OpeningJournalAsync(fixture, b, closedAdvanceId));

        await using (var db = fixture.CreateDb())
            applied = Json(Assert.IsType<OkObjectResult>(await Controller(db, b.Tenant, "Admin").Apply(new OpeningBalanceBackfillRequest(b.CompanyId), default)));
        Assert.Equal(0, applied.GetProperty("posted").GetInt32());
        Assert.Single(await K.OpeningJournalAsync(fixture, b, loanId));

        await using var verify = fixture.CreateDb();
        Assert.Equal(2, await verify.AuditLogs.CountAsync(a => a.TenantId == b.Tenant && a.Action == "finance.gl.opening_balance_backfill.applied"));
    }

    [Fact]
    public async Task AnHrManagerIsForbidden()
    {
        var (b, cutover, _) = await K.SeedAsync(fixture, "BF-HR");
        await SeedCarriedInLoanAsync(b, 300m, cutover);
        await using var db = fixture.CreateDb();
        Assert.IsType<ForbidResult>(await Controller(db, b.Tenant, "HR Manager").Preview(b.CompanyId, default));
        Assert.IsType<ForbidResult>(await Controller(db, b.Tenant, "HR Manager").Apply(new OpeningBalanceBackfillRequest(b.CompanyId), default));
    }

    private static JsonElement Row(List<JsonElement> rows, Guid id) => rows.Single(r => r.GetProperty("entityId").GetGuid() == id);

    private static JsonElement Json(OkObjectResult ok) => JsonSerializer.SerializeToElement(ok.Value);

    /// <summary>A loan as the import carried it in before it posted opening journals: the loan and its origin row, no journal.</summary>
    private async Task<Guid> SeedCarriedInLoanAsync(Kit.Borrower b, decimal outstanding, DateOnly cutover)
    {
        await using var db = fixture.CreateDb();
        var type = await db.LoanTypes.FirstAsync(t => t.TenantId == b.Tenant);
        var loan = new EmployeeLoan
        {
            TenantId = b.Tenant, CompanyId = b.CompanyId, EmployeeIntId = b.EmployeeId, EmployeeId = b.EmployeePublicId, EmployeeName = "Debt Race Borrower",
            LoanNumber = $"BF-LN-{Guid.NewGuid():N}"[..18], LoanTypeId = type.Id, LoanTypeName = type.NameEn, Currency = "SAR",
            RequestedAmount = 600m, ApprovedAmount = 600m, RequestedInstallments = 6, ApprovedInstallments = 6, InstallmentAmount = 100m,
            TotalRepaid = 600m - outstanding, OutstandingBalance = outstanding, Status = "Active", RepaymentStartDate = cutover,
            DisbursementDate = new DateOnly(2025, 1, 1),
        };
        db.EmployeeLoans.Add(loan);
        db.OpeningBalanceOrigins.Add(Origin(b, OpeningBalanceEntityTypes.Loan, loan.Id, cutover, outstanding));
        await db.SaveChangesAsync();
        return loan.Id;
    }

    private async Task<Guid> SeedCarriedInAdvanceAsync(Kit.Borrower b, decimal outstanding, DateOnly cutover)
    {
        await using var db = fixture.CreateDb();
        var advance = new SalaryAdvance
        {
            TenantId = b.Tenant, CompanyId = b.CompanyId, EmployeeId = b.EmployeePublicId, EmployeeIntId = b.EmployeeId, EmployeeName = "Debt Race Borrower",
            AdvanceNumber = $"BF-ADV-{Guid.NewGuid():N}"[..18], RequestedAmount = 600m, ApprovedAmount = 600m, RepaymentType = "Installments",
            Installments = 6, InstallmentAmount = 100m, TotalRepaid = 600m - outstanding, OutstandingBalance = outstanding, Status = "Active",
            RepaymentStartDate = cutover,
        };
        db.SalaryAdvances.Add(advance);
        db.OpeningBalanceOrigins.Add(Origin(b, OpeningBalanceEntityTypes.Advance, advance.Id, cutover, outstanding));
        await db.SaveChangesAsync();
        return advance.Id;
    }

    private static OpeningBalanceOrigin Origin(Kit.Borrower b, string type, Guid id, DateOnly cutover, decimal amount) => new()
    {
        TenantId = b.Tenant, CompanyId = b.CompanyId, EmployeeId = b.EmployeeId, EmployeeCode = b.EmployeeCode, EntityType = type, EntityId = id,
        CutoverDate = cutover, CarriedAmount = amount, Currency = "SAR", SourceSystem = "SAP", SourceRecordId = "X", MigrationBatchId = Guid.NewGuid(),
    };

    private static OpeningBalanceJournalsController Controller(ZayraDbContext db, Guid tid, string role) => new(db)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tid.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Name, role), new Claim(ClaimTypes.Role, role), new Claim("is_group_scope", "true"),
                }, "test")),
            },
        },
    };
}
