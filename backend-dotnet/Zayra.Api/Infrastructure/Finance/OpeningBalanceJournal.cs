using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Finance;

/// <summary>A payroll-collected debt carried in by the opening-balance import, as the opening journal sees it.</summary>
/// <param name="SourceModule">The module every journal of this debt uses: "Loan" or "Advance".</param>
/// <param name="ReceivableDriver">The posting driver of its receivable: LOAN_RECEIVABLE (1400) or ADVANCE_RECEIVABLE (1410).</param>
public sealed record OpeningBalanceDebt(
    Guid TenantId, Guid? CompanyId, string SourceModule, string ReceivableDriver, Guid EntityId, string Reference,
    string Kind, decimal OutstandingBalance)
{
    public static OpeningBalanceDebt For(SalaryAdvance a) => new(
        a.TenantId, a.CompanyId, "Advance", GlControlAccounts.AdvanceReceivableDriver, a.Id, a.AdvanceNumber, "Advance", a.OutstandingBalance);

    public static OpeningBalanceDebt For(EmployeeLoan l) => new(
        l.TenantId, l.CompanyId, "Loan", GlControlAccounts.LoanReceivableDriver, l.Id, l.LoanNumber, "Loan", l.OutstandingBalance);
}

/// <summary>
/// The opening journal of a loan or salary advance carried in by the opening-balance import: DR the debt's receivable (1400
/// Employee Loans Receivable, or 1410 Employee Salary Advances) / CR 3900 Opening Balance Equity (OPENING_BALANCE_EQUITY, the
/// account the provision catch-up route already uses), for the outstanding balance, dated on the import's cutover date and
/// stamped with the debt's company. EventType <see cref="GlEventTypes.OpeningBalance"/>, which the ERP journal export leaves out:
/// the client's ERP already holds these balances from the previous system.
///
/// <para>Without it payroll credited the receivable for every instalment it collected from a carried-in debt while nothing had
/// ever debited it, so the receivable ran into credit and the sub-ledger never tied out with the ledger.</para>
///
/// <para><b>Once, then only the difference.</b> What this debt's opening lines already carry is read back and only the
/// difference to the stated balance is posted: a repeated package posts nothing, a restatement up posts DR receivable / CR 3900
/// for the increase, a restatement down DR 3900 / CR receivable for the decrease. The ledger is append-only, so nothing is ever
/// rewritten. The caller holds the debt's finance.loan / finance.advance lock inside the import's retry-safe transaction
/// (ApplyDebtSectionLockedAsync), so two imports of the same debt cannot both read the same "already posted" figure.</para>
/// </summary>
public static class OpeningBalanceJournal
{
    /// <summary>Adds the opening line the debt still needs to the context (saved by the caller) and returns the signed amount
    /// posted to the receivable: positive a debit, negative a credit, zero when the ledger already carries the balance.</summary>
    public static async Task<decimal> PostAsync(
        ZayraDbContext db, OpeningBalanceDebt debt, DateOnly openingDate, string currency, Guid? postedBy, string postedByName,
        CancellationToken ct)
    {
        var glCtx = await GlAccountResolver.LoadAsync(db, debt.TenantId, debt.CompanyId, ct);
        var receivable = GlAccountResolver.AccountLabel(debt.ReceivableDriver, glCtx);
        var equity = GlAccountResolver.AccountLabel(ProvisionCatchUpRoute.EquityDriverKey, glCtx);

        var posted = await db.FinanceGlEntries.AsNoTracking()
            .Where(x => x.TenantId == debt.TenantId && x.SourceModule == debt.SourceModule && x.SourceEntityId == debt.EntityId
                     && x.EventType == GlEventTypes.OpeningBalance && !x.IsReversed)
            .Select(x => new { x.DebitAccount, x.CreditAccount, x.Amount })
            .ToListAsync(ct);
        var onLedger = 0m;
        foreach (var line in posted)
        {
            if (line.DebitAccount == receivable || line.CreditAccount == equity) onLedger += line.Amount;
            else if (line.CreditAccount == receivable || line.DebitAccount == equity) onLedger -= line.Amount;
            else
                // The chart was remapped since the opening was posted, so what the ledger carries for this debt cannot be told
                // apart from the accounts it names now. Refuse the row rather than post a difference against the wrong figure.
                throw new InvalidOperationException(
                    $"[opening_journal_remapped] {debt.Kind} {debt.Reference}'s opening journal was posted to accounts the chart no longer maps " +
                    "(the receivable or opening balance equity was remapped), so its opening balance cannot be restated here. Correct it with a manual GL adjustment.");
        }

        var difference = Math.Round(debt.OutstandingBalance - onLedger, 2);
        if (difference == 0m) return 0m;

        var period = openingDate.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        await PeriodCloseGuard.ThrowIfClosedAsync(db, debt.TenantId, debt.CompanyId, period, ct);
        var increase = difference > 0m;
        db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = debt.TenantId, CompanyId = debt.CompanyId,
            SourceModule = debt.SourceModule, SourceEntityId = debt.EntityId, SourceEntityRef = debt.Reference,
            EventType = GlEventTypes.OpeningBalance,
            DebitAccount = increase ? receivable : equity,
            CreditAccount = increase ? equity : receivable,
            Amount = Math.Abs(difference), Currency = currency,
            EntryDate = openingDate, Period = period,
            Description = posted.Count == 0
                ? $"{debt.Kind} opening balance carried in: {debt.Reference}"
                : $"{debt.Kind} opening balance restated: {debt.Reference}",
            PostedBy = postedBy, PostedByName = postedByName,
        });
        return difference;
    }
}
