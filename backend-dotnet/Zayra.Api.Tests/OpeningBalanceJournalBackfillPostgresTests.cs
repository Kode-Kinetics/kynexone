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

    /// <summary>
    /// A carried-in loan payroll collected from before the backfill: the Lock booked its instalment to the 2107 clearing account,
    /// because the loan had nothing on 1400 to relieve. The backfill posts the opening balance carried in at cutover (as the ERP
    /// holds it) and, in the same transaction, moves that recovery back off 2107 against 1400 with an ordinary, exportable line,
    /// so 1400 ends at what is still owed and 2107 holds nothing for it. Applying again posts nothing.
    /// </summary>
    [Fact]
    public async Task ALoanCollectedFromBeforeTheBackfill_HasItsClearingMovedBack_SoTheReceivableIsWhatIsStillOwed()
    {
        var (b, cutover, _) = await K.SeedAsync(fixture, "BF-CL");
        var loanId = await SeedCarriedInLoanAsync(b, 10_000m, cutover, original: 10_000m, instalment: 3_000m);
        await LockOneRunAsync(b);
        Assert.Equal(7_000m, (await LoanAsync(loanId)).OutstandingBalance);

        JsonElement applied;
        await using (var db = fixture.CreateDb())
            applied = Json(Assert.IsType<OkObjectResult>(await Controller(db, b.Tenant, "Finance Approver").Apply(new OpeningBalanceBackfillRequest(b.CompanyId), default)));
        Assert.Equal(1, applied.GetProperty("posted").GetInt32());

        var lines = await LinesAsync(b, loanId);
        var opening = Assert.Single(lines, l => l.EventType == K.OpeningBalanceEvent);
        Assert.Equal(10_000m, opening.Amount);
        var companion = Assert.Single(lines, l => l.EventType == GlEventTypes.OpeningBalanceClearingReclass);
        Assert.Equal(3_000m, companion.Amount);
        Assert.StartsWith("2107", companion.DebitAccount, StringComparison.Ordinal);
        Assert.StartsWith("1400", companion.CreditAccount, StringComparison.Ordinal);
        Assert.Equal(7_000m, Net(lines, opening.DebitAccount));
        Assert.Equal(0m, await BalanceAsync(b, companion.DebitAccount));
        Assert.Equal(await LoanSubLedgerAsync(b), await BalanceAsync(b, opening.DebitAccount));

        await using (var db = fixture.CreateDb())
            applied = Json(Assert.IsType<OkObjectResult>(await Controller(db, b.Tenant, "Admin").Apply(new OpeningBalanceBackfillRequest(b.CompanyId), default)));
        Assert.Equal(0, applied.GetProperty("posted").GetInt32());
        Assert.Equal(2, (await LinesAsync(b, loanId)).Count);
    }

    /// <summary>When the clearing account no longer holds the recovery (the LOAN remittance already paid it out), the backfill does
    /// not guess: the preview flags the row for manual reconciliation with the figures, and apply refuses it in both languages.</summary>
    [Fact]
    public async Task ARecoveryAlreadyRemittedFromClearing_IsFlaggedForManualReconciliation_AndRefused()
    {
        var (b, cutover, _) = await K.SeedAsync(fixture, "BF-RM");
        var loanId = await SeedCarriedInLoanAsync(b, 10_000m, cutover, original: 10_000m, instalment: 3_000m);
        var runId = await LockOneRunAsync(b);
        // The run's LOAN clearing paid out to cash, as a remittance made before the remit guard refused unbacked loan clearing
        // would have booked it (the guard now refuses it, so it is seeded as the historical journal it was).
        await using (var db = fixture.CreateDb())
        {
            var clearing = await db.FinanceGlEntries.AsNoTracking()
                .Where(g => g.SourceEntityId == runId && g.Description == "Payroll deduction: LOAN_EMI").Select(g => g.CreditAccount).FirstAsync();
            db.FinanceGlEntries.Add(new FinanceGlEntry
            {
                TenantId = b.Tenant, CompanyId = b.CompanyId, SourceModule = "Payroll", SourceEntityId = runId, SourceEntityRef = $"{cutover:yyyy-MM}",
                EventType = GlEventTypes.Remit("LOAN"), DebitAccount = clearing, CreditAccount = "1000 - Cash at Bank", Amount = 3_000m, Currency = "SAR",
                EntryDate = b.Today, Period = $"{b.Today:yyyy-MM}", Description = "Statutory remittance — LOAN",
            });
            await db.SaveChangesAsync();
        }

        JsonElement preview;
        await using (var db = fixture.CreateDb())
            preview = Json(Assert.IsType<OkObjectResult>(await Controller(db, b.Tenant, "Finance Approver").Preview(b.CompanyId, default)));
        var row = Row(preview.GetProperty("rows").EnumerateArray().ToList(), loanId);
        Assert.Equal("needs_manual_reconciliation", row.GetProperty("status").GetString());
        Assert.Equal(3_000m, row.GetProperty("recoveredThroughClearing").GetDecimal());
        Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("reasonAr").GetString()));

        JsonElement applied;
        await using (var db = fixture.CreateDb())
            applied = Json(Assert.IsType<OkObjectResult>(await Controller(db, b.Tenant, "Finance Approver").Apply(new OpeningBalanceBackfillRequest(b.CompanyId), default)));
        Assert.Equal((0, 1), (applied.GetProperty("posted").GetInt32(), applied.GetProperty("refused").GetInt32()));
        Assert.Empty(await LinesAsync(b, loanId));
    }

    /// <summary>An origin row must name the debt's own company: a mismatch is refused with the reason. An origin with no company
    /// is backfilled under the debt's company when the debt has one, and reported as not backfillable when it has none.</summary>
    [Fact]
    public async Task TheOriginsCompanyMustBeTheDebts_ANullOriginUsesTheDebtsCompany_OrIsNotBackfillable()
    {
        var (b, cutover, _) = await K.SeedAsync(fixture, "BF-CO");
        Guid otherCompany;
        await using (var db = fixture.CreateDb())
        {
            var other = new Company { TenantId = b.Tenant, LegalNameEn = "Other Co", CountryCode = "SAU", Jurisdiction = "KSA-mainland",
                RegistrationNumber = $"OT-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true };
            db.Companies.Add(other);
            await db.SaveChangesAsync();
            otherCompany = other.Id;
        }
        var mismatched = await SeedCarriedInAdvanceAsync(b, 100m, cutover, debtCompany: otherCompany);
        var nullOriginOwn = await SeedCarriedInAdvanceAsync(b, 200m, cutover, nullOrigin: true);
        var nullBoth = await SeedCarriedInAdvanceAsync(b, 300m, cutover, nullOrigin: true, clearDebtCompany: true);

        JsonElement preview;
        await using (var db = fixture.CreateDb())
            preview = Json(Assert.IsType<OkObjectResult>(await Controller(db, b.Tenant, "Finance Approver").Preview(b.CompanyId, default)));
        var rows = preview.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal("company_mismatch", Row(rows, mismatched).GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(Row(rows, mismatched).GetProperty("reason").GetString()));
        Assert.Equal("ready", Row(rows, nullOriginOwn).GetProperty("status").GetString());
        Assert.Equal("not_backfillable", Row(rows, nullBoth).GetProperty("status").GetString());

        await using (var db = fixture.CreateDb())
            Assert.IsType<OkObjectResult>(await Controller(db, b.Tenant, "Finance Approver").Apply(new OpeningBalanceBackfillRequest(b.CompanyId), default));
        Assert.Empty(await K.OpeningJournalAsync(fixture, b, mismatched));
        var line = Assert.Single(await K.OpeningJournalAsync(fixture, b, nullOriginOwn));
        Assert.Equal((200m, (Guid?)b.CompanyId), (line.Amount, line.CompanyId));
        Assert.Empty(await K.OpeningJournalAsync(fixture, b, nullBoth));
    }

    /// <summary>One payroll run for this month, processed, approved and locked. Returns its id.</summary>
    private async Task<Guid> LockOneRunAsync(Kit.Borrower b)
    {
        var runId = await Kit.SeedDraftRunAsync(fixture, b, b.Today.Year, b.Today.Month);
        await using (var db = fixture.CreateDb())
            Assert.Equal(200, (await Kit.ProcessAsync(db, b, runId, null, CancellationToken.None)).StatusCode);
        await using (var db = fixture.CreateDb()) await ReconTestSupport.SignOffAsync(db, runId);
        await using (var db = fixture.CreateDb())
            Assert.IsType<OkObjectResult>(await Kit.Payroll(db, b.Tenant).Approve(runId, new Zayra.Api.Controllers.PayrollDecisionRequest("reviewed"), default));
        await using (var db = fixture.CreateDb())
            Assert.IsType<OkObjectResult>(await Kit.Payroll(db, b.Tenant).Lock(runId, default));
        return runId;
    }

    private async Task<EmployeeLoan> LoanAsync(Guid id)
    {
        await using var db = fixture.CreateDb();
        return await db.EmployeeLoans.AsNoTracking().SingleAsync(l => l.Id == id);
    }

    private async Task<List<FinanceGlEntry>> LinesAsync(Kit.Borrower b, Guid entityId)
    {
        await using var db = fixture.CreateDb();
        return await db.FinanceGlEntries.AsNoTracking().Where(g => g.TenantId == b.Tenant && g.SourceEntityId == entityId).ToListAsync();
    }

    private static decimal Net(IEnumerable<FinanceGlEntry> lines, string account) =>
        lines.Sum(l => (l.DebitAccount == account ? l.Amount : 0m) - (l.CreditAccount == account ? l.Amount : 0m));

    private async Task<decimal> BalanceAsync(Kit.Borrower b, string account)
    {
        await using var db = fixture.CreateDb();
        return (await Zayra.Api.Infrastructure.Payroll.GlControlAccounts.LoadAsync(db, b.Tenant, b.CompanyId, account, default)).Scoped;
    }

    private async Task<decimal> LoanSubLedgerAsync(Kit.Borrower b)
    {
        await using var db = fixture.CreateDb();
        return (await db.EmployeeLoans.AsNoTracking()
            .Where(l => l.TenantId == b.Tenant && l.CompanyId == b.CompanyId && !l.IsDeleted && (l.Status == "Active" || l.Status == "Overdue"))
            .Select(l => l.OutstandingBalance).ToListAsync()).Sum();
    }

    private static JsonElement Row(List<JsonElement> rows, Guid id) => rows.Single(r => r.GetProperty("entityId").GetGuid() == id);

    private static JsonElement Json(OkObjectResult ok) => JsonSerializer.SerializeToElement(ok.Value);

    /// <summary>A loan as the import carried it in before it posted opening journals: the loan and its origin row, no journal.</summary>
    private async Task<Guid> SeedCarriedInLoanAsync(Kit.Borrower b, decimal outstanding, DateOnly cutover,
        decimal original = 600m, decimal instalment = 100m)
    {
        await using var db = fixture.CreateDb();
        var type = await db.LoanTypes.FirstAsync(t => t.TenantId == b.Tenant);
        var loan = new EmployeeLoan
        {
            TenantId = b.Tenant, CompanyId = b.CompanyId, EmployeeIntId = b.EmployeeId, EmployeeId = b.EmployeePublicId, EmployeeName = "Debt Race Borrower",
            LoanNumber = $"BF-LN-{Guid.NewGuid():N}"[..18], LoanTypeId = type.Id, LoanTypeName = type.NameEn, Currency = "SAR",
            RequestedAmount = original, ApprovedAmount = original, RequestedInstallments = 6, ApprovedInstallments = 6, InstallmentAmount = instalment,
            TotalRepaid = original - outstanding, OutstandingBalance = outstanding, Status = "Active", RepaymentStartDate = cutover,
            DisbursementDate = new DateOnly(2025, 1, 1),
        };
        db.EmployeeLoans.Add(loan);
        db.OpeningBalanceOrigins.Add(Origin(b, OpeningBalanceEntityTypes.Loan, loan.Id, cutover, outstanding));
        await db.SaveChangesAsync();
        return loan.Id;
    }

    private async Task<Guid> SeedCarriedInAdvanceAsync(Kit.Borrower b, decimal outstanding, DateOnly cutover,
        bool nullOrigin = false, Guid? debtCompany = null, bool clearDebtCompany = false)
    {
        await using var db = fixture.CreateDb();
        var advance = new SalaryAdvance
        {
            TenantId = b.Tenant, CompanyId = clearDebtCompany ? null : debtCompany ?? b.CompanyId, EmployeeId = b.EmployeePublicId, EmployeeIntId = b.EmployeeId, EmployeeName = "Debt Race Borrower",
            AdvanceNumber = $"BF-ADV-{Guid.NewGuid():N}"[..18], RequestedAmount = 600m, ApprovedAmount = 600m, RepaymentType = "Installments",
            Installments = 6, InstallmentAmount = 100m, TotalRepaid = 600m - outstanding, OutstandingBalance = outstanding, Status = "Active",
            RepaymentStartDate = cutover,
        };
        db.SalaryAdvances.Add(advance);
        var origin = Origin(b, OpeningBalanceEntityTypes.Advance, advance.Id, cutover, outstanding);
        if (nullOrigin) origin.CompanyId = null;
        db.OpeningBalanceOrigins.Add(origin);
        await db.SaveChangesAsync();
        return advance.Id;
    }

    private static OpeningBalanceOrigin Origin(Kit.Borrower b, string type, Guid id, DateOnly cutover, decimal amount) => new()
    {
        TenantId = b.Tenant, CompanyId = b.CompanyId, EmployeeId = b.EmployeeId, EmployeeCode = b.EmployeeCode, EntityType = type, EntityId = id,
        CutoverDate = cutover, CarriedAmount = amount, Currency = "SAR", SourceSystem = "SAP", SourceRecordId = "X", MigrationBatchId = Guid.NewGuid(),
    };

    private static OpeningBalanceJournalsController Controller(ZayraDbContext db, Guid tid, string role) => new(new Zayra.Api.Infrastructure.Finance.OpeningBalanceBackfillService(db))
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
