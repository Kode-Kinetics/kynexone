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
/// import posted them. Such a debt sits in the sub-ledger with nothing on 1400 / 1410, so the receivable never ties out. Nothing
/// runs this on deploy: Finance (or an Admin) previews a company, reviews the rows, and applies.
///
/// <para><b>What is posted, and for which debts.</b> Only the opening line, DR 1400 Employee Loans Receivable or 1410 Employee
/// Salary Advances / CR 3900 Opening Balance Equity, for the balance the import carried in at cutover
/// (<see cref="OpeningBalanceOrigin.CarriedAmount"/>), dated on that cutover date, through the import's own writer
/// (<see cref="OpeningBalanceJournal"/>): exactly the line the import posts today, so applying twice posts nothing. And only for a
/// debt nothing has happened to since cutover: no payroll instalment witnessed on any run (voided or not), no repayment, no move
/// to the 1420 employee receivable, and a balance still equal to what was carried in. Every other debt has already been booked
/// against some account by what happened to it (a payroll take went to the clearing account, a void reversed it, a settlement
/// moved it), and an automatic correction cannot be proven right for all of those paths; it is listed as
/// <c>needs_manual_reconciliation</c> with the figures (carried, outstanding, payroll takes found) and apply refuses it. Finance
/// reconciles it with a GL adjustment.</para>
///
/// <para>The decision is taken under the debts' finance.loan / finance.advance locks, which payroll Process also takes: a run
/// racing the backfill either finished first (its witness is then seen, so the debt is manual) or waits until the opening line
/// is committed (and then relieves the receivable as usual). A debt whose cutover period is closed, or whose import record names
/// another company, is refused for its row in English and Arabic. Finance roles or Admin only, company-scoped. No second person is
/// required: the action restates no balance and moves no money, it books a figure the import already recorded (the origin row),
/// it is idempotent and every posting is audited.</para>
/// </summary>
public sealed class OpeningBalanceBackfillService(ZayraDbContext db)
{
    private readonly ZayraDbContext _db = db;

    /// <summary>Dry run: every carried-in loan and advance of the company and what applying would post, changing nothing.</summary>
    /// <param name="includeUnattributed">Whether import records naming no company are considered (a group-level caller only).</param>
    public async Task<object> PreviewAsync(Guid tenantId, Guid companyId, bool includeUnattributed, CancellationToken ct)
    {
        var rows = await PlanAsync(tenantId, companyId, includeUnattributed, ct);
        return new { companyId, digest = Digest(rows), rows = rows.Select(Project).ToList(), ready = rows.Count(r => r.Status == Ready) };
    }

    /// <summary>The result of an apply: <see cref="Stale"/> when the plan no longer matches the preview the caller confirmed.</summary>
    public sealed record ApplyResult(bool Stale, object Body);

    /// <summary>Posts every ready row's opening journal (idempotent) and refuses the others per row. Audited per posting.</summary>
    /// <param name="previewDigest">The <c>digest</c> of the preview the caller confirmed. The plan is recomputed under the debt locks;
    /// if it differs (a row added or removed, a status or an amount changed) nothing is posted and the result is stale.</param>
    public async Task<ApplyResult> ApplyAsync(Guid tenantId, Guid companyId, bool includeUnattributed, string? previewDigest,
        Guid? userId, string userName, string? ipAddress, CancellationToken ct)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        var attempt = 0;
        return await strategy.ExecuteAsync(async () =>
        {
            if (attempt++ > 0) _db.ChangeTracker.Clear();
            await using var tx = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync(ct) : null;
            var origins = await OriginsAsync(tenantId, companyId, includeUnattributed, ct);
            // The debt locks first, then the decision: a payroll pass holding one of them finishes before the plan reads its witness.
            await FinanceDecisionSerializer.AcquireDebtLocksAsync(_db, tenantId,
                origins.Where(o => o.EntityType == OpeningBalanceEntityTypes.Loan).Select(o => o.EntityId),
                origins.Where(o => o.EntityType == OpeningBalanceEntityTypes.Advance).Select(o => o.EntityId), ct);
            var rows = await PlanAsync(tenantId, companyId, includeUnattributed, ct);
            var digest = Digest(rows);
            if (!string.Equals(digest, previewDigest, StringComparison.Ordinal))
                return new ApplyResult(true, new
                {
                    error = "backfill_preview_stale",
                    message = "The opening balances have changed since the preview you confirmed (a debt was added, moved or posted). Nothing was posted. Preview again and confirm the new figures.",
                    messageAr = "تغيّرت الأرصدة الافتتاحية منذ المعاينة التي أكدتها (أُضيف دين أو تغيّر أو رُحّل). لم يُرحَّل شيء. أعد المعاينة وأكّد الأرقام الجديدة.",
                    digest,
                });
            var posted = 0;
            foreach (var row in rows.Where(r => r.Status == Ready))
            {
                OpeningBalanceJournal.Apply(_db, row.Prepared!, row.EntityId, row.Currency, userId, userName);
                _db.AuditLogs.Add(new AuditLog
                {
                    TenantId = tenantId, CompanyId = companyId, Action = "finance.gl.opening_balance_backfill.applied",
                    EntityName = row.EntityType == OpeningBalanceEntityTypes.Loan ? "EmployeeLoan" : "SalaryAdvance", EntityId = row.EntityId.ToString(),
                    UserId = userId, IpAddress = ipAddress,
                    Metadata = JsonSerializer.Serialize(new { row.Reference, row.CutoverDate, row.CarriedAmount, posted = row.ToPost, row.CurrentOutstanding }),
                    CreatedAtUtc = DateTime.UtcNow,
                });
                posted++;
            }
            await _db.SaveChangesAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);
            return new ApplyResult(false, new
            {
                companyId,
                posted,
                refused = rows.Count(r => r.Status is not (Ready or AlreadyPosted)),
                rows = rows.Select(Project).ToList(),
            });
        });
    }

    /// <summary>A fingerprint of the plan a caller is shown: every row's debt, status and amounts, in a fixed order.</summary>
    private static string Digest(IEnumerable<Row> rows)
    {
        var text = string.Join("\n", rows.OrderBy(r => r.EntityId).Select(r => string.Join("|",
            r.EntityType, r.EntityId.ToString("N"), r.Status,
            r.CarriedAmount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            r.CurrentOutstanding.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            r.PayrollTakes.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            r.ToPost.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            // What apply posts WITH, not only how much: entry date/period, currency and the resolved accounts.
            r.CutoverDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), r.Currency,
            r.Prepared?.Receivable ?? "", r.Prepared?.Equity ?? "", r.Prepared?.Period ?? "",
            r.Prepared?.OpeningDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "")));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
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
        decimal CurrentOutstanding, decimal PayrollTakes, decimal ToPost, string Currency, string Status, string? Reason, string? ReasonAr,
        OpeningBalanceJournal.Prepared? Prepared);

    private static object Project(Row r) => new
    {
        entityType = r.EntityType, entityId = r.EntityId, reference = r.Reference, employeeCode = r.EmployeeCode,
        cutoverDate = r.CutoverDate, carriedAmount = r.CarriedAmount, currentOutstanding = r.CurrentOutstanding,
        payrollTakes = r.PayrollTakes, toPost = r.ToPost, currency = r.Currency, status = r.Status,
        reason = r.Reason, reasonAr = r.ReasonAr,
    };

    /// <summary>The company's origin rows, and the origin rows that name no company (their debt's own company decides).</summary>
    private Task<List<OpeningBalanceOrigin>> OriginsAsync(Guid tenantId, Guid companyId, bool includeUnattributed, CancellationToken ct) =>
        ScopedBypass.TenantWide(_db.OpeningBalanceOrigins, tenantId,
                "Opening-journal backfill: the carried-in debts of a company the caller's scope was checked for, and the unattributed ones whose debt decides.")
            .AsNoTracking()
            .Where(o => (o.CompanyId == companyId || (includeUnattributed && o.CompanyId == null))
                     && (o.EntityType == OpeningBalanceEntityTypes.Loan || o.EntityType == OpeningBalanceEntityTypes.Advance))
            .OrderBy(o => o.CutoverDate).ThenBy(o => o.EmployeeCode)
            .ToListAsync(ct);

    private static Row Refusal(OpeningBalanceOrigin o, string reference, decimal outstanding, decimal takes, string status, string reason, string reasonAr) =>
        new(o.EntityType, o.EntityId, reference, o.EmployeeCode, o.CutoverDate, o.CarriedAmount, outstanding, takes, 0m, o.Currency,
            status, reason, reasonAr, null);

    /// <summary>The audit payloads of the tenant's run voids and reopens, read once per plan (a run's void names the debts it gave
    /// back to). The audit column is json, which the database cannot search as text, so it is searched here.</summary>
    private List<string>? _unwound;

    private async Task<List<Row>> PlanAsync(Guid tenantId, Guid companyId, bool includeUnattributed, CancellationToken ct)
    {
        _unwound = null;
        var origins = await OriginsAsync(tenantId, companyId, includeUnattributed, ct);
        var loanIds = origins.Where(o => o.EntityType == OpeningBalanceEntityTypes.Loan).Select(o => o.EntityId).ToList();
        var advanceIds = origins.Where(o => o.EntityType == OpeningBalanceEntityTypes.Advance).Select(o => o.EntityId).ToList();
        var loans = await ScopedBypass.TenantWide(_db.EmployeeLoans, tenantId, "Opening-journal backfill: the company's carried-in loans.")
            .AsNoTracking().Where(l => loanIds.Contains(l.Id) && !l.IsDeleted).ToDictionaryAsync(l => l.Id, ct);
        var advances = await ScopedBypass.TenantWide(_db.SalaryAdvances, tenantId, "Opening-journal backfill: the company's carried-in advances.")
            .AsNoTracking().Where(a => advanceIds.Contains(a.Id) && !a.IsDeleted).ToDictionaryAsync(a => a.Id, ct);

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
                rows.Add(Refusal(o, debt.Reference, outstanding, 0m, CompanyMismatch,
                    $"[company_mismatch] The import recorded {debt.Reference} under one company, but the {debt.Kind.ToLowerInvariant()} now belongs to another, so it is not backfilled. Correct its company (or the import record) first.",
                    $"سجّل الاستيراد {debt.Reference} تحت شركة، لكنه أصبح يتبع شركة أخرى، فلا يُرحَّل رصيده الافتتاحي. صحّح الشركة أولًا."));
                continue;
            }
            if (o.CompanyId is null)
            {
                if (debt.CompanyId is null)
                {
                    rows.Add(Refusal(o, debt.Reference, outstanding, 0m, NotBackfillable,
                        $"[not_backfillable] Neither the import record nor the {debt.Kind.ToLowerInvariant()} {debt.Reference} names a company, so there is no company to book its opening balance to. Assign the debt to its company first.",
                        $"لا يحدد سجل الاستيراد ولا {debt.Reference} أي شركة، فلا توجد شركة يُرحَّل إليها رصيده الافتتاحي. اربطه بشركته أولًا."));
                    continue;
                }
                if (debt.CompanyId != companyId) continue;   // another company's debt: its own backfill lists it
            }

            try
            {
                var prepared = await OpeningBalanceJournal.PrepareAsync(_db, debt, o.CutoverDate, o.Currency, ct);
                if (prepared.Difference == 0m)
                {
                    rows.Add(new Row(o.EntityType, o.EntityId, debt.Reference, o.EmployeeCode, o.CutoverDate, o.CarriedAmount, outstanding,
                        0m, 0m, o.Currency, AlreadyPosted, null, null, prepared));
                    continue;
                }
                var (untouched, takes) = await UntouchedSinceCutoverAsync(tenantId, o, debt, outstanding, ct);
                if (!untouched)
                {
                    rows.Add(Refusal(o, debt.Reference, outstanding, takes, NeedsManual,
                        $"[needs_manual_reconciliation] {debt.Kind} {debt.Reference} has moved since cutover (carried in {o.CarriedAmount:N2}, outstanding now {outstanding:N2}, payroll takes found {takes:N2}), " +
                        "so its receivable is not backfilled automatically: what happened to it was booked to other accounts. Reconcile it with a GL adjustment.",
                        $"تغيّر {debt.Reference} منذ التحويل (المُرحّل {o.CarriedAmount:N2}، المتبقي الآن {outstanding:N2}، ما حصّلته الرواتب {takes:N2})، " +
                        "فلا يُرحَّل رصيده الافتتاحي تلقائيًا لأن ما جرى عليه قُيّد في حسابات أخرى. سوِّه بقيد تسوية يدوي."));
                    continue;
                }
                rows.Add(new Row(o.EntityType, o.EntityId, debt.Reference, o.EmployeeCode, o.CutoverDate, o.CarriedAmount, outstanding,
                    0m, prepared.Difference, o.Currency, Ready, null, null, prepared));
            }
            catch (InvalidOperationException ex)
            {
                var closed = ex.Message.StartsWith("[opening_journal_period_closed]", StringComparison.Ordinal);
                var parts = ex.Message.Split(" / ", 2);
                rows.Add(Refusal(o, debt.Reference, outstanding, 0m, closed ? PeriodClosed : Refused, parts[0], parts.Length > 1 ? parts[1] : string.Empty));
            }
        }
        return rows;
    }

    /// <summary>
    /// Whether nothing has happened to the debt since cutover, and the payroll takes found (for the reason): no consumption witness
    /// of the debt or its instalments on any run, and no void or reopen of a run that gave an instalment of it back; for a loan no repayment recorded against it (reversed or not); no
    /// move to the 1420 employee receivable (an advance's residual audit row; for either, a balance below what was carried); and
    /// the balance still equal to what was carried in.
    /// </summary>
    private async Task<(bool Untouched, decimal Takes)> UntouchedSinceCutoverAsync(
        Guid tenantId, OpeningBalanceOrigin o, OpeningBalanceDebt debt, decimal outstanding, CancellationToken ct)
    {
        var isLoan = debt.Kind == "Loan";
        var debtArtifact = isLoan ? PayrollConsumptionArtifacts.Loan : PayrollConsumptionArtifacts.Advance;
        var witnesses = await ScopedBypass.TenantWide(_db.PayrollRunConsumptions, tenantId,
                "Opening-journal backfill: whether any payroll run, live or voided, took from this carried-in debt.")
            .AsNoTracking()
            .Where(c => c.ArtifactType == debtArtifact && c.ArtifactId == debt.EntityId)
            .Select(c => c.Amount).ToListAsync(ct);
        var takes = Math.Round(witnesses.Sum(), 2);
        if (witnesses.Count > 0) return (false, takes);

        var instalmentArtifact = isLoan ? PayrollConsumptionArtifacts.LoanInstallment : PayrollConsumptionArtifacts.AdvanceInstallment;
        var instalmentIds = isLoan
            ? await _db.LoanInstallments.AsNoTracking().Where(i => i.TenantId == tenantId && i.LoanId == debt.EntityId).Select(i => i.Id).ToListAsync(ct)
            : await _db.AdvanceInstallments.AsNoTracking().Where(i => i.TenantId == tenantId && i.AdvanceId == debt.EntityId).Select(i => i.Id).ToListAsync(ct);
        if (instalmentIds.Count > 0 && await ScopedBypass.TenantWide(_db.PayrollRunConsumptions, tenantId,
                    "Opening-journal backfill: whether any payroll run stamped this carried-in debt's instalments.")
                .AsNoTracking().AnyAsync(c => c.ArtifactType == instalmentArtifact && instalmentIds.Contains(c.ArtifactId), ct))
            return (false, takes);

        // A run that took from the debt and was then voided or reopened: the void replays and deletes its witnesses, but its audit
        // row names the debt among what it gave back. That take and its contra were booked to accounts the opening line would have
        // to reconcile with, so the debt is not untouched.
        var id = debt.EntityId.ToString();
        _unwound ??= await _db.PayrollAuditLogs.AsNoTracking()
            .Where(a => a.TenantId == tenantId && (a.Action == "payroll.run.voided" || a.Action == "payroll.run.reopened"))
            .Select(a => a.MetadataJson).ToListAsync(ct);
        if (_unwound.Any(m => m.Contains(id, StringComparison.OrdinalIgnoreCase))) return (false, takes);

        if (isLoan && await _db.LoanRepayments.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.LoanId == debt.EntityId, ct))
            return (false, takes);
        if (!isLoan && await _db.AdvanceAuditLogs.AsNoTracking().AnyAsync(
                l => l.TenantId == tenantId && l.AdvanceId == debt.EntityId && l.Action == AdvanceResidualAudit.Action, ct))
            return (false, takes);
        return (outstanding == o.CarriedAmount, takes);
    }
}
