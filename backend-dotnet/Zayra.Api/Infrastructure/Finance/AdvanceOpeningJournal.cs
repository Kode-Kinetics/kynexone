using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Finance;

/// <summary>
/// The opening journal of a salary advance carried in by the opening-balance import: DR 1410 Employee Salary Advances (the
/// ADVANCE_RECEIVABLE driver) / CR 3900 Opening Balance Equity (OPENING_BALANCE_EQUITY, the account the provision catch-up route
/// already uses), for the advance's outstanding balance, dated on the import's cutover date and stamped with the advance's company.
///
/// <para>Without it payroll credited 1410 for every instalment it collected from a carried-in advance while nothing had ever
/// debited it, so the receivable ran into credit and the advance tie-out flagged the company.</para>
///
/// <para><b>Once, then only the difference.</b> What this advance's opening lines already carry is read back and only the
/// difference to the stated balance is posted: a repeated package posts nothing, a restatement up posts DR 1410 / CR 3900 for the
/// increase, a restatement down DR 3900 / CR 1410 for the decrease. The ledger is append-only, so nothing is ever rewritten. The
/// caller holds the advance's finance.advance lock inside the import's retry-safe transaction (ApplyDebtSectionLockedAsync), so two
/// imports of the same advance cannot both read the same "already posted" figure; and it only restates an advance payroll has not
/// collected from, so the opening lines are the whole of the advance's 1410 story at that point.</para>
/// </summary>
public static class AdvanceOpeningJournal
{
    /// <summary>The source module every advance journal uses (the disbursement too).</summary>
    public const string SourceModule = "Advance";

    /// <summary>Adds the opening line the advance still needs to the context (saved by the caller) and returns the signed amount
    /// posted to 1410: positive a debit, negative a credit, zero when the ledger already carries the balance.</summary>
    public static async Task<decimal> PostAsync(
        ZayraDbContext db, SalaryAdvance advance, DateOnly openingDate, string currency, Guid? postedBy, string postedByName,
        CancellationToken ct)
    {
        var glCtx = await GlAccountResolver.LoadAsync(db, advance.TenantId, advance.CompanyId, ct);
        var receivable = GlAccountResolver.AccountLabel(GlControlAccounts.AdvanceReceivableDriver, glCtx);
        var equity = GlAccountResolver.AccountLabel(ProvisionCatchUpRoute.EquityDriverKey, glCtx);

        var posted = await db.FinanceGlEntries.AsNoTracking()
            .Where(x => x.TenantId == advance.TenantId && x.SourceModule == SourceModule && x.SourceEntityId == advance.Id
                     && x.EventType == GlEventTypes.OpeningBalance && !x.IsReversed)
            .Select(x => new { x.DebitAccount, x.CreditAccount, x.Amount })
            .ToListAsync(ct);
        var onLedger = 0m;
        foreach (var line in posted)
        {
            if (line.DebitAccount == receivable || line.CreditAccount == equity) onLedger += line.Amount;
            else if (line.CreditAccount == receivable || line.DebitAccount == equity) onLedger -= line.Amount;
            else
                // The chart was remapped since the opening was posted, so what the ledger carries for this advance cannot be told
                // apart from the accounts it names now. Refuse the row rather than post a difference against the wrong figure.
                throw new InvalidOperationException(
                    $"[advance_opening_journal_remapped] Advance {advance.AdvanceNumber}'s opening journal was posted to accounts the chart no longer maps " +
                    "(the advance receivable or opening balance equity was remapped), so its opening balance cannot be restated here. Correct it with a manual GL adjustment.");
        }

        var difference = Math.Round(advance.OutstandingBalance - onLedger, 2);
        if (difference == 0m) return 0m;

        var period = openingDate.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        await PeriodCloseGuard.ThrowIfClosedAsync(db, advance.TenantId, advance.CompanyId, period, ct);
        var increase = difference > 0m;
        db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = advance.TenantId, CompanyId = advance.CompanyId,
            SourceModule = SourceModule, SourceEntityId = advance.Id, SourceEntityRef = advance.AdvanceNumber,
            EventType = GlEventTypes.OpeningBalance,
            DebitAccount = increase ? receivable : equity,
            CreditAccount = increase ? equity : receivable,
            Amount = Math.Abs(difference), Currency = currency,
            EntryDate = openingDate, Period = period,
            Description = posted.Count == 0
                ? $"Advance opening balance carried in: {advance.AdvanceNumber}"
                : $"Advance opening balance restated: {advance.AdvanceNumber}",
            PostedBy = postedBy, PostedByName = postedByName,
        });
        return difference;
    }
}
