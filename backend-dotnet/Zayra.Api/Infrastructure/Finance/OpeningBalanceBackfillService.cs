using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Finance;

/// <summary>
/// One-off backfill of the opening journals of loans and salary advances carried in by the opening-balance import before the
/// import posted them. Such a debt sits in the sub-ledger with nothing on 1400 / 1410, so the receivable never ties out and a
/// payroll recovery has no debit to relieve. Nothing runs this on deploy: Finance (or an Admin) previews a company, reviews the
/// rows, and applies.
///
/// <para><b>What is posted.</b> DR 1400 Employee Loans Receivable or 1410 Employee Salary Advances / CR 3900 Opening Balance Equity
/// for the balance the import carried in at cutover (<see cref="OpeningBalanceOrigin.CarriedAmount"/>), dated on that cutover date,
/// through the import's own writer (<see cref="OpeningBalanceJournal"/>). That is exactly the line the import would have posted
/// on the day, so a backfilled debt and a debt imported today carry the same opening journal, a later restatement compares with
/// the same figure, and applying twice posts nothing (the writer posts only the difference). Where payroll recovered from the debt
/// before its receivable was booked, the Lock credited that recovery to the clearing account (2107) instead; in the same
/// transaction the backfill moves exactly this debt's share back (DR the clearing account the run used / CR 1400 or 1410,
/// <see cref="GlEventTypes.OpeningBalanceClearingReclass"/>, an ordinary journal the ERP receives), so the receivable ends at what
/// is still owed. That share is read from the runs' own witnesses and journals (<c>ClearingRecoveriesAsync</c>); whatever cannot
/// be traced, or was already remitted out of the clearing account, is flagged for manual reconciliation and refused, never
/// guessed. </para>
///
/// <para>A debt whose cutover period is closed is refused for its row, in English and Arabic; reopen the period to backfill it.
/// Finance roles or Admin only, company-scoped. No second person is required: the action restates no balance and moves no money,
/// it books a figure the import already recorded (the origin row), it is idempotent and every posting is audited. Each debt is
/// posted under its finance.loan / finance.advance lock inside one retry-safe transaction, so it cannot race the import or payroll.</para>
/// </summary>
public sealed class OpeningBalanceBackfillService(ZayraDbContext db)
{
    private readonly ZayraDbContext _db = db;

    /// <summary>Dry run: every carried-in loan and advance of the company and what applying would post, changing nothing.</summary>
    public async Task<object> PreviewAsync(Guid tenantId, Guid companyId, CancellationToken ct)
    {
        var rows = await PlanAsync(tenantId, companyId, ct);
        return new { companyId, rows = rows.Select(Project).ToList(), ready = rows.Count(r => r.Status == Ready) };
    }

    /// <summary>Posts every ready row's opening journal (idempotent) and refuses the others per row. Audited per posting.</summary>
    public async Task<object> ApplyAsync(Guid tenantId, Guid companyId, Guid? userId, string userName, string? ipAddress, CancellationToken ct)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        var attempt = 0;
        return await strategy.ExecuteAsync(async () =>
        {
            if (attempt++ > 0) _db.ChangeTracker.Clear();
            await using var tx = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync(ct) : null;
            var origins = await OriginsAsync(tenantId, companyId, ct);
            await FinanceDecisionSerializer.AcquireDebtLocksAsync(_db, tenantId,
                origins.Where(o => o.EntityType == OpeningBalanceEntityTypes.Loan).Select(o => o.EntityId),
                origins.Where(o => o.EntityType == OpeningBalanceEntityTypes.Advance).Select(o => o.EntityId), ct);
            var rows = await PlanAsync(tenantId, companyId, ct);
            var posted = 0;
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            foreach (var row in rows.Where(r => r.Status == Ready))
            {
                OpeningBalanceJournal.Apply(_db, row.Prepared!, row.EntityId, row.Currency, userId, userName);
                if (row.CompanionToPost > 0m)
                    // The recovery payroll booked to the clearing account before the receivable carried the debt goes back against the
                    // receivable: DR the clearing account the run used / CR 1400 or 1410. An ordinary, exportable journal.
                    _db.FinanceGlEntries.Add(new FinanceGlEntry
                    {
                        TenantId = tenantId, CompanyId = row.CompanyId,
                        SourceModule = row.Prepared!.Debt.SourceModule, SourceEntityId = row.EntityId, SourceEntityRef = row.Reference,
                        EventType = GlEventTypes.OpeningBalanceClearingReclass,
                        DebitAccount = row.ClearingAccount!, CreditAccount = row.Prepared.Receivable,
                        Amount = row.CompanionToPost, Currency = row.Currency,
                        EntryDate = today, Period = today.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture),
                        Description = $"Payroll recovery moved from clearing to the receivable (opening balance backfill): {row.Reference}",
                        PostedBy = userId, PostedByName = userName,
                    });
                _db.AuditLogs.Add(new AuditLog
                {
                    TenantId = tenantId, CompanyId = companyId, Action = "finance.gl.opening_balance_backfill.applied",
                    EntityName = row.EntityType == OpeningBalanceEntityTypes.Loan ? "EmployeeLoan" : "SalaryAdvance", EntityId = row.EntityId.ToString(),
                    UserId = userId, IpAddress = ipAddress,
                    Metadata = JsonSerializer.Serialize(new { row.Reference, row.CutoverDate, row.CarriedAmount, posted = row.ToPost, row.CurrentOutstanding,
                        clearingMovedBack = row.CompanionToPost, row.ClearingAccount }),
                    CreatedAtUtc = DateTime.UtcNow,
                });
                posted++;
            }
            await _db.SaveChangesAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);
            return (object)new
            {
                companyId,
                posted,
                refused = rows.Count(r => r.Status is not (Ready or AlreadyPosted)),
                rows = rows.Select(Project).ToList(),
            };
        });
    }

    private const string Ready = "ready";
    private const string AlreadyPosted = "already_posted";
    private const string PeriodClosed = "period_closed";
    private const string Refused = "refused";
    private const string NeedsManual = "needs_manual_reconciliation";
    private const string CompanyMismatch = "company_mismatch";
    private const string NotBackfillable = "not_backfillable";

    private sealed record Row(
        string EntityType, Guid EntityId, string Reference, string EmployeeCode, DateOnly CutoverDate, decimal CarriedAmount,
        decimal CurrentOutstanding, decimal RecoveredThroughClearing, string? ClearingAccount, decimal ToPost, decimal CompanionToPost,
        string Currency, Guid? CompanyId, string Status, string? Reason, string? ReasonAr, OpeningBalanceJournal.Prepared? Prepared);

    private static object Project(Row r) => new
    {
        entityType = r.EntityType, entityId = r.EntityId, reference = r.Reference, employeeCode = r.EmployeeCode,
        cutoverDate = r.CutoverDate, carriedAmount = r.CarriedAmount, currentOutstanding = r.CurrentOutstanding,
        recoveredThroughClearing = r.RecoveredThroughClearing, clearingAccount = r.ClearingAccount,
        toPost = r.ToPost, clearingToMoveBack = r.CompanionToPost, currency = r.Currency, status = r.Status,
        reason = r.Reason, reasonAr = r.ReasonAr,
    };

    /// <summary>The company's origin rows, and the origin rows that name no company (their debt's own company decides).</summary>
    private Task<List<OpeningBalanceOrigin>> OriginsAsync(Guid tenantId, Guid companyId, CancellationToken ct) =>
        ScopedBypass.TenantWide(_db.OpeningBalanceOrigins, tenantId,
                "Opening-journal backfill: the carried-in debts of a company the caller's scope was checked for, and the unattributed ones whose debt decides.")
            .AsNoTracking()
            .Where(o => (o.CompanyId == companyId || o.CompanyId == null)
                     && (o.EntityType == OpeningBalanceEntityTypes.Loan || o.EntityType == OpeningBalanceEntityTypes.Advance))
            .OrderBy(o => o.CutoverDate).ThenBy(o => o.EmployeeCode)
            .ToListAsync(ct);

    private Row Refusal(OpeningBalanceOrigin o, string reference, decimal outstanding, Guid? companyId, string status, string reason, string reasonAr,
        decimal throughClearing = 0m, string? clearingAccount = null) =>
        new(o.EntityType, o.EntityId, reference, o.EmployeeCode, o.CutoverDate, o.CarriedAmount, outstanding, throughClearing, clearingAccount,
            0m, 0m, o.Currency, companyId, status, reason, reasonAr, null);

    private async Task<List<Row>> PlanAsync(Guid tenantId, Guid companyId, CancellationToken ct)
    {
        var origins = await OriginsAsync(tenantId, companyId, ct);
        var loanIds = origins.Where(o => o.EntityType == OpeningBalanceEntityTypes.Loan).Select(o => o.EntityId).ToList();
        var advanceIds = origins.Where(o => o.EntityType == OpeningBalanceEntityTypes.Advance).Select(o => o.EntityId).ToList();
        var loans = await ScopedBypass.TenantWide(_db.EmployeeLoans, tenantId, "Opening-journal backfill: the company's carried-in loans.")
            .AsNoTracking().Where(l => loanIds.Contains(l.Id) && !l.IsDeleted).ToDictionaryAsync(l => l.Id, ct);
        var advances = await ScopedBypass.TenantWide(_db.SalaryAdvances, tenantId, "Opening-journal backfill: the company's carried-in advances.")
            .AsNoTracking().Where(a => advanceIds.Contains(a.Id) && !a.IsDeleted).ToDictionaryAsync(a => a.Id, ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var rows = new List<Row>();
        foreach (var o in origins)
        {
            OpeningBalanceDebt? debt = null;
            decimal outstanding = 0m;
            if (o.EntityType == OpeningBalanceEntityTypes.Loan && loans.TryGetValue(o.EntityId, out var loan))
            {
                debt = OpeningBalanceDebt.For(loan) with { OutstandingBalance = o.CarriedAmount };
                outstanding = loan.OutstandingBalance;
            }
            else if (o.EntityType == OpeningBalanceEntityTypes.Advance && advances.TryGetValue(o.EntityId, out var advance))
            {
                debt = OpeningBalanceDebt.For(advance) with { OutstandingBalance = o.CarriedAmount };
                outstanding = advance.OutstandingBalance;
            }
            if (debt is null) continue;   // deleted since it was carried in: nothing to book

            // The company: the origin row must name the debt's own company. An origin with no company takes the debt's company
            // (and belongs to that company's backfill); with no company on either there is nowhere to book it.
            if (o.CompanyId is Guid originCompany && debt.CompanyId != originCompany)
            {
                rows.Add(Refusal(o, debt.Reference, outstanding, debt.CompanyId, CompanyMismatch,
                    $"[company_mismatch] The import recorded {debt.Reference} under one company, but the {debt.Kind.ToLowerInvariant()} now belongs to another, so it is not backfilled. Correct its company (or the import record) first.",
                    $"سجّل الاستيراد {debt.Reference} تحت شركة، لكنه أصبح يتبع شركة أخرى، فلا يُرحَّل رصيده الافتتاحي. صحّح الشركة أولًا."));
                continue;
            }
            if (o.CompanyId is null)
            {
                if (debt.CompanyId is null)
                {
                    rows.Add(Refusal(o, debt.Reference, outstanding, null, NotBackfillable,
                        $"[not_backfillable] Neither the import record nor the {debt.Kind.ToLowerInvariant()} {debt.Reference} names a company, so there is no company to book its opening balance to. Assign the debt to its company first.",
                        $"لا يحدد سجل الاستيراد ولا {debt.Reference} أي شركة، فلا توجد شركة يُرحَّل إليها رصيده الافتتاحي. اربطه بشركته أولًا."));
                    continue;
                }
                if (debt.CompanyId != companyId) continue;   // another company's debt: its own backfill lists it
            }

            var clearing = await ClearingRecoveriesAsync(tenantId, debt, ct);
            if (clearing.ManualReason is { } manual)
            {
                rows.Add(Refusal(o, debt.Reference, outstanding, debt.CompanyId, NeedsManual, manual.En, manual.Ar, clearing.Amount, clearing.Account));
                continue;
            }
            try
            {
                var prepared = await OpeningBalanceJournal.PrepareAsync(_db, debt, o.CutoverDate, ct);
                var companion = Math.Max(0m, clearing.Amount - clearing.AlreadyMovedBack);
                if (companion > 0m && await PeriodCloseGuard.IsClosedAsync(_db, tenantId, debt.CompanyId,
                        today.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture), ct))
                {
                    rows.Add(Refusal(o, debt.Reference, outstanding, debt.CompanyId, PeriodClosed,
                        $"[opening_journal_period_closed] {debt.Reference}: the current GL period is closed, so the payroll recovery that went to the clearing account cannot be moved back today. Reopen the period first; nothing was changed.",
                        $"{debt.Reference}: الفترة المحاسبية الحالية مقفلة، فلا يمكن إعادة ما حصّلته الرواتب عبر حساب المقاصة اليوم. أعد فتح الفترة أولًا؛ لم يتغير شيء.",
                        clearing.Amount, clearing.Account));
                    continue;
                }
                rows.Add(new Row(o.EntityType, o.EntityId, debt.Reference, o.EmployeeCode, o.CutoverDate, o.CarriedAmount, outstanding,
                    clearing.Amount, clearing.Account, prepared.Difference, companion, o.Currency, debt.CompanyId,
                    prepared.Difference == 0m && companion == 0m ? AlreadyPosted : Ready, null, null, prepared));
            }
            catch (InvalidOperationException ex)
            {
                var closed = ex.Message.StartsWith("[opening_journal_period_closed]", StringComparison.Ordinal);
                var parts = ex.Message.Split(" / ", 2);
                rows.Add(Refusal(o, debt.Reference, outstanding, debt.CompanyId, closed ? PeriodClosed : Refused, parts[0],
                    parts.Length > 1 ? parts[1] : string.Empty, clearing.Amount, clearing.Account));
            }
        }
        return rows;
    }

    private sealed record ClearingRecovery(decimal Amount, string? Account, decimal AlreadyMovedBack, (string En, string Ar)? ManualReason);

    /// <summary>
    /// What payroll recovered from this debt that the Lock booked to the clearing account (2107) instead of the receivable, read
    /// from the run's own witnesses and journal, never inferred from the balance (which a 1420 move or a cash receipt also lowers).
    /// Per live (non-voided) run that took from the debt and has been locked (an unlocked run has booked nothing; its Lock after the
    /// backfill relieves the receivable as usual):
    /// <list type="bullet">
    /// <item>a debt whose opening journal was already on the ledger when the run locked was relieved there: nothing to move;</item>
    /// <item>a loan with no booking at that time was never eligible for relief (LoanReliefEligibility), so its take went to the
    /// clearing line, which must hold at least that take;</item>
    /// <item>an advance's take went to clearing when the run's advance recovery has no receivable line at all, and to 1410 when it
    /// has no clearing line; with both, it cannot be told which part was this advance's.</item>
    /// </list>
    /// Anything that cannot be traced, or a clearing amount the run has already remitted (the clearing account no longer holds it),
    /// is not guessed: the row needs manual reconciliation, with the figures.
    /// </summary>
    private async Task<ClearingRecovery> ClearingRecoveriesAsync(Guid tenantId, OpeningBalanceDebt debt, CancellationToken ct)
    {
        var artifact = debt.Kind == "Loan" ? PayrollConsumptionArtifacts.Loan : PayrollConsumptionArtifacts.Advance;
        var code = debt.Kind == "Loan" ? "LOAN_EMI" : "ADVANCE_EMI";
        var takes = await (from c in ScopedBypass.TenantWide(_db.PayrollRunConsumptions, tenantId,
                               "Opening-journal backfill: the payroll instalments taken from one carried-in debt.").AsNoTracking()
                           join r in ScopedBypass.TenantWide(_db.PayrollRuns, tenantId,
                               "Opening-journal backfill: whether the runs that took from the debt are live.").AsNoTracking() on c.PayrollRunId equals r.Id
                           where c.ArtifactType == artifact && c.ArtifactId == debt.EntityId && r.Status != "Voided"
                           select new { c.PayrollRunId, c.Amount }).ToListAsync(ct);
        var firstOpening = await _db.FinanceGlEntries.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.SourceModule == debt.SourceModule && x.SourceEntityId == debt.EntityId
                     && x.EventType == GlEventTypes.OpeningBalance)
            .OrderBy(x => x.CreatedAtUtc).Select(x => (DateTime?)x.CreatedAtUtc).FirstOrDefaultAsync(ct);
        var movedBack = (await _db.FinanceGlEntries.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.SourceModule == debt.SourceModule && x.SourceEntityId == debt.EntityId
                     && x.EventType == GlEventTypes.OpeningBalanceClearingReclass && !x.IsReversed)
            .Select(x => x.Amount).ToListAsync(ct)).Sum();

        decimal amount = 0m;
        var accounts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var run in takes.GroupBy(t => t.PayrollRunId))
        {
            var take = run.Sum(t => t.Amount);
            var lines = await _db.FinanceGlEntries.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.SourceModule == "Payroll" && x.SourceEntityId == run.Key
                         && x.EventType == GlEventTypes.Accrual && !x.IsReversed
                         && (x.Description == PayrollGlDescriptions.LoanRecoveryPrefix + code || x.Description == PayrollGlDescriptions.DeductionPrefix + code))
                .Select(x => new { x.Description, x.CreditAccount, x.Amount, x.CreatedAtUtc }).ToListAsync(ct);
            if (lines.Count == 0) continue;   // not locked yet: nothing booked
            var lockedAt = lines.Min(l => l.CreatedAtUtc);
            if (firstOpening is DateTime opened && opened < lockedAt) continue;   // the receivable carried it: relieved there
            var relief = lines.Where(l => l.Description == PayrollGlDescriptions.LoanRecoveryPrefix + code).Sum(l => l.Amount);
            var clearingLines = lines.Where(l => l.Description == PayrollGlDescriptions.DeductionPrefix + code).ToList();
            var cleared = clearingLines.Sum(l => l.Amount);
            if (debt.Kind != "Loan" && cleared == 0m) continue;          // the advance recovery was all relieved on 1410
            var traced = cleared >= take && (debt.Kind == "Loan" || relief == 0m);
            var remitted = await _db.FinanceGlEntries.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.SourceModule == "Payroll"
                && x.SourceEntityId == run.Key && x.EventType == GlEventTypes.Remit("LOAN") && !x.IsReversed, ct);
            amount += take;
            foreach (var l in clearingLines) accounts.Add(l.CreditAccount);
            if (!traced || remitted || accounts.Count > 1)
                return new ClearingRecovery(Math.Round(amount, 2), accounts.FirstOrDefault(), movedBack, (
                    $"[needs_manual_reconciliation] Payroll recovered {take:N2} from {debt.Reference} before its receivable was booked, through the clearing account " +
                    $"{string.Join(", ", accounts)}{(remitted ? ", and that clearing has since been remitted, so the clearing account no longer holds it" : ", in a way that cannot be traced to this debt alone")}. " +
                    "The backfill does not guess: reconcile it with a GL adjustment, then backfill again.",
                    $"حصّلت الرواتب {take:N2} من {debt.Reference} قبل ترحيل ذمته المدينة، عبر حساب المقاصة {string.Join(", ", accounts)}" +
                    (remitted ? "، وقد سُدّد هذا المبلغ من حساب المقاصة فلم يعد يحتفظ به" : "، بطريقة لا يمكن نسبتها إلى هذا الدين وحده") +
                    ". لا يفترض الترحيل شيئًا: سوِّ المبلغ بقيد تسوية يدوي ثم أعد الترحيل."));
        }
        return new ClearingRecovery(Math.Round(amount, 2), accounts.FirstOrDefault(), movedBack, null);
    }
}
