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
        Assert.False(string.IsNullOrWhiteSpace(preview.GetProperty("digest").GetString()));
        Assert.Equal(300m, Row(rows, loanId).GetProperty("toPost").GetDecimal());
        Assert.Equal("ready", Row(rows, advanceId).GetProperty("status").GetString());
        Assert.Equal("period_closed", Row(rows, closedAdvanceId).GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(Row(rows, closedAdvanceId).GetProperty("reasonAr").GetString()));
        await using (var db = fixture.CreateDb())
            Assert.False(await db.FinanceGlEntries.AnyAsync(g => g.TenantId == b.Tenant && g.EventType == K.OpeningBalanceEvent), "the preview posts nothing");

        var applied = Json(Assert.IsType<OkObjectResult>(await PreviewThenApplyAsync(b, "Finance Approver")));
        Assert.Equal(2, applied.GetProperty("posted").GetInt32());
        Assert.Equal(1, applied.GetProperty("refused").GetInt32());

        var loanLine = Assert.Single(await K.OpeningJournalAsync(fixture, b, loanId));
        Assert.Equal((300m, cutover), (loanLine.Amount, loanLine.EntryDate));
        Assert.StartsWith("1400", loanLine.DebitAccount, StringComparison.Ordinal);
        Assert.StartsWith("3900", loanLine.CreditAccount, StringComparison.Ordinal);
        var advanceLine = Assert.Single(await K.OpeningJournalAsync(fixture, b, advanceId));
        Assert.StartsWith("1410", advanceLine.DebitAccount, StringComparison.Ordinal);
        Assert.Empty(await K.OpeningJournalAsync(fixture, b, closedAdvanceId));

        applied = Json(Assert.IsType<OkObjectResult>(await PreviewThenApplyAsync(b, "Admin")));
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
        Assert.IsType<ForbidResult>(await Controller(db, b.Tenant, "HR Manager").Apply(new OpeningBalanceBackfillRequest(b.CompanyId, "x"), default));
    }

    /// <summary>A carried-in loan payroll has taken an instalment from (a locked run) has moved since cutover: the backfill does
    /// not book it automatically. The preview lists it for manual reconciliation with the figures, and apply posts nothing for it.</summary>
    [Fact]
    public async Task ADebtPayrollTookFrom_IsLeftForManualReconciliation_AndNothingIsPosted()
    {
        var (b, cutover, _) = await K.SeedAsync(fixture, "BF-TK");
        var loanId = await SeedCarriedInLoanAsync(b, 10_000m, cutover, original: 10_000m, instalment: 3_000m);
        await LockOneRunAsync(b);

        var row = Row((await PreviewAsync(b, "Finance Approver")).GetProperty("rows").EnumerateArray().ToList(), loanId);
        Assert.Equal("needs_manual_reconciliation", row.GetProperty("status").GetString());
        Assert.Equal((10_000m, 7_000m, 3_000m), (row.GetProperty("carriedAmount").GetDecimal(), row.GetProperty("currentOutstanding").GetDecimal(),
            row.GetProperty("payrollTakes").GetDecimal()));
        Assert.Contains("3,000.00", row.GetProperty("reason").GetString());
        Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("reasonAr").GetString()));

        var applied = Json(Assert.IsType<OkObjectResult>(await PreviewThenApplyAsync(b, "Finance Approver")));
        Assert.Equal((0, 1), (applied.GetProperty("posted").GetInt32(), applied.GetProperty("refused").GetInt32()));
        Assert.Empty(await K.OpeningJournalAsync(fixture, b, loanId));
    }

    /// <summary>A payroll take that was voided still leaves the debt to Finance: the void and the original take were booked to
    /// accounts the opening line would have to reconcile with, so a witness on any run, voided or not, makes the row manual.</summary>
    [Fact]
    public async Task ADebtAVoidedRunTookFrom_IsStillLeftForManualReconciliation()
    {
        var (b, cutover, _) = await K.SeedAsync(fixture, "BF-VD");
        var advanceId = await SeedCarriedInAdvanceAsync(b, 300m, cutover);
        var runId = await LockOneRunAsync(b);
        await using (var db = fixture.CreateDb())
            Assert.IsType<OkObjectResult>(await Kit.Payroll(db, b.Tenant).VoidRun(runId, new Zayra.Api.Controllers.PayrollDecisionRequest("wrong run"), default));
        await using (var db = fixture.CreateDb())
            Assert.Equal(300m, (await db.SalaryAdvances.AsNoTracking().SingleAsync(a => a.Id == advanceId)).OutstandingBalance);

        var row = Row((await PreviewAsync(b, "Finance Approver")).GetProperty("rows").EnumerateArray().ToList(), advanceId);
        Assert.Equal("needs_manual_reconciliation", row.GetProperty("status").GetString());
        Json(Assert.IsType<OkObjectResult>(await PreviewThenApplyAsync(b, "Finance Approver")));
        Assert.Empty(await K.OpeningJournalAsync(fixture, b, advanceId));
    }

    /// <summary>
    /// Process and the backfill decide under the same debt lock. A payroll pass holding the lock (paused just before it commits)
    /// makes the apply wait; once the pass commits its take, the apply sees the witness and leaves the debt to Finance. The
    /// opening line and an unbooked take never both land.
    /// </summary>
    [Fact]
    public async Task AProcessHoldingTheDebtLock_MakesTheApplyWait_AndTheApplyThenSeesTheTake()
    {
        var (b, cutover, _) = await K.SeedAsync(fixture, "BF-RC");
        var advanceId = await SeedCarriedInAdvanceAsync(b, 300m, cutover);
        var runId = await Kit.SeedDraftRunAsync(fixture, b, b.Today.Year, b.Today.Month);
        var digest = (await PreviewAsync(b, "Finance Approver")).GetProperty("digest").GetString()!;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var processDb = fixture.CreateDb();
        var process = Task.Run(() => Kit.ProcessAsync(processDb, b, runId, new Zayra.Api.Infrastructure.Payroll.PayrollProcessHooks
        {
            BeforeCommit = async _ => { reached.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(60)); },
        }, timeout.Token));
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(60));

        await using var applyDb = fixture.CreateDb();
        var applyPid = await Kit.OpenAndPidAsync(applyDb, timeout.Token);
        var apply = Task.Run(() => Controller(applyDb, b.Tenant, "Finance Approver").Apply(new OpeningBalanceBackfillRequest(b.CompanyId, digest), timeout.Token));
        var waited = await Kit.WaitForLockAsync(fixture, applyPid, advisoryOnly: true, apply, timeout.Token);
        release.TrySetResult();
        Assert.Equal(200, (await process).StatusCode);
        var applied = await apply;

        Assert.True(waited, "the apply must queue behind the debt lock payroll holds");
        // The plan the apply confirmed changed under it (the take arrived), so it is refused as stale: nothing posts.
        Assert.IsType<ConflictObjectResult>(applied);
        Assert.Empty(await K.OpeningJournalAsync(fixture, b, advanceId));
        var row = Row((await PreviewAsync(b, "Finance Approver")).GetProperty("rows").EnumerateArray().ToList(), advanceId);
        Assert.Equal("needs_manual_reconciliation", row.GetProperty("status").GetString());
    }

    /// <summary>Apply confirms the preview it was shown: if the plan has changed since (a row added, an amount moved), it is refused
    /// with 409 in both languages and posts nothing.</summary>
    [Fact]
    public async Task AnApplyConfirmingAStalePreview_IsRefused()
    {
        var (b, cutover, _) = await K.SeedAsync(fixture, "BF-DG");
        await SeedCarriedInLoanAsync(b, 300m, cutover);
        var digest = (await PreviewAsync(b, "Finance Approver")).GetProperty("digest").GetString()!;
        var later = await SeedCarriedInAdvanceAsync(b, 200m, cutover);

        await using var db = fixture.CreateDb();
        var refused = Assert.IsType<ConflictObjectResult>(await Controller(db, b.Tenant, "Finance Approver").Apply(new OpeningBalanceBackfillRequest(b.CompanyId, digest), default));
        var body = JsonSerializer.Serialize(refused.Value);
        Assert.Contains("backfill_preview_stale", body, StringComparison.Ordinal);
        Assert.Contains("messageAr", body, StringComparison.Ordinal);
        Assert.False(await db.FinanceGlEntries.AnyAsync(g => g.TenantId == b.Tenant && g.EventType == K.OpeningBalanceEvent));
        Assert.IsType<OkObjectResult>(await PreviewThenApplyAsync(b, "Finance Approver"));
        Assert.Single(await K.OpeningJournalAsync(fixture, b, later));
    }

    /// <summary>The action needs finance.opening_balances.import as well as a finance role, so a per-user Deny of the key is honoured;
    /// and an import record with no company is shown only to a group-level caller.</summary>
    [Fact]
    public async Task ADeniedPermissionIsForbidden_AndUnattributedRowsAreForGroupCallersOnly()
    {
        var (b, cutover, _) = await K.SeedAsync(fixture, "BF-PM");
        var own = await SeedCarriedInAdvanceAsync(b, 100m, cutover);
        var unattributed = await SeedCarriedInAdvanceAsync(b, 200m, cutover, nullOrigin: true);
        await using var db = fixture.CreateDb();
        Assert.IsType<ForbidResult>(await Controller(db, b.Tenant, "Finance Approver", deniedPermission: true).Preview(b.CompanyId, default));
        Assert.IsType<ForbidResult>(await Controller(db, b.Tenant, "Admin", deniedPermission: true).Apply(new OpeningBalanceBackfillRequest(b.CompanyId, "x"), default));
        Assert.True(typeof(OpeningBalanceJournalsController).GetCustomAttributes(typeof(Zayra.Api.Infrastructure.Authorization.HasPermissionAttribute), true)
            .Cast<Zayra.Api.Infrastructure.Authorization.HasPermissionAttribute>().Any(a => a.Permissions.Contains("finance.opening_balances.import")));

        var scoped = Json(Assert.IsType<OkObjectResult>(await Controller(db, b.Tenant, "Finance Approver", companyScope: b.CompanyId).Preview(b.CompanyId, default)));
        var ids = scoped.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("entityId").GetGuid()).ToList();
        Assert.Contains(own, ids);
        Assert.DoesNotContain(unattributed, ids);
        var group = (await PreviewAsync(b, "Finance Approver")).GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("entityId").GetGuid()).ToList();
        Assert.Contains(unattributed, group);
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

        Assert.IsType<OkObjectResult>(await PreviewThenApplyAsync(b, "Finance Approver"));
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

    private async Task<JsonElement> PreviewAsync(Kit.Borrower b, string role)
    {
        await using var db = fixture.CreateDb();
        return Json(Assert.IsType<OkObjectResult>(await Controller(db, b.Tenant, role).Preview(b.CompanyId, default)));
    }

    /// <summary>Previews, then applies confirming that preview's digest, as the screen does.</summary>
    private async Task<IActionResult> PreviewThenApplyAsync(Kit.Borrower b, string role)
    {
        var digest = (await PreviewAsync(b, role)).GetProperty("digest").GetString()!;
        await using var db = fixture.CreateDb();
        return await Controller(db, b.Tenant, role).Apply(new OpeningBalanceBackfillRequest(b.CompanyId, digest), default);
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

    private static OpeningBalanceJournalsController Controller(ZayraDbContext db, Guid tid, string role, bool deniedPermission = false,
        Guid? companyScope = null)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tid.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, role), new(ClaimTypes.Role, role),
            companyScope is Guid cid ? new("entity_access", JsonSerializer.Serialize(new { c = cid, r = role })) : new("is_group_scope", "true"),
        };
        // The seeded bundles: Admin, Finance and Finance Approver hold finance.opening_balances.import; a Deny takes it away.
        if (!deniedPermission && role is "Admin" or "Finance" or "Finance Approver") claims.Add(new("permission", "finance.opening_balances.import"));
        return new(new Zayra.Api.Infrastructure.Finance.OpeningBalanceBackfillService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
            },
        };
    }
}
