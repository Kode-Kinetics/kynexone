using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Finance;

public partial class LoansController
{
    private const string LoanJournalReconciliationRequired =
        "The original loan journal is missing, ambiguous, or inconsistent. Finance must reconcile the payment evidence and link the correct original journal before proceeding.";

    private async Task<FinanceGlEntry?> OriginalDisbursementAsync(EmployeeLoan loan, CancellationToken ct)
    {
        var lines = await _db.LoanDisbursementLines.Where(x => x.TenantId == loan.TenantId
            && x.LoanId == loan.Id && !x.IsCancelled && x.Status == "Paid").ToListAsync(ct);
        if (lines.Count > 1) return null;
        var linkedId = lines.SingleOrDefault()?.GlEntryId;
        var candidates = await _db.FinanceGlEntries.Where(x => x.TenantId == loan.TenantId
            && x.SourceModule == "Loan" && x.SourceEntityId == loan.Id && x.EventType == "Disbursement")
            .Take(2).ToListAsync(ct);
        // A pre-link payout is usable only when there is exactly one original journal. Never
        // choose among candidates by amount/date, which can be identical for separate events.
        if (candidates.Count != 1 || (linkedId.HasValue && candidates[0].Id != linkedId)) return null;
        var original = candidates[0];
        if (!JournalMatchesLoan(original, loan) || original.Amount != loan.ApprovedAmount
            || original.EntryDate != loan.DisbursementDate || await HasContraAsync(original, ct)) return null;
        return original;
    }

    private async Task<FinanceGlEntry?> OriginalReceiptAsync(EmployeeLoan loan, LoanRepayment receipt, CancellationToken ct)
    {
        // Legacy loan-level repayment source IDs do not distinguish repeated identical receipts.
        // Only an explicit persisted receipt link can authorize a reversal.
        if (!receipt.GlEntryId.HasValue || receipt.ReversalGlEntryId.HasValue) return null;
        var original = await _db.FinanceGlEntries.SingleOrDefaultAsync(x => x.TenantId == loan.TenantId
            && x.Id == receipt.GlEntryId, ct);
        if (original == null || !JournalMatchesLoan(original, loan) || original.SourceModule != "Loan"
            || original.EventType != "Repayment" || (original.SourceEntityId != receipt.Id && original.SourceEntityId != loan.Id)
            || original.CompanyId != receipt.CompanyId || original.Amount != receipt.Amount
            || original.EntryDate != receipt.PaidDate || await HasContraAsync(original, ct)) return null;
        if (await _db.LoanRepayments.AnyAsync(x => x.TenantId == loan.TenantId && x.Id != receipt.Id && x.GlEntryId == original.Id, ct)) return null;
        return original;
    }

    private static bool JournalMatchesLoan(FinanceGlEntry journal, EmployeeLoan loan) =>
        !journal.IsReversed && !journal.ReversalOfEntryId.HasValue && journal.CompanyId == loan.CompanyId
        && journal.Currency == loan.Currency && IsMoney(journal.Amount)
        && !string.IsNullOrWhiteSpace(journal.Currency) && !string.IsNullOrWhiteSpace(journal.DebitAccount)
        && !string.IsNullOrWhiteSpace(journal.CreditAccount);

    private Task<bool> HasContraAsync(FinanceGlEntry journal, CancellationToken ct) =>
        _db.FinanceGlEntries.AnyAsync(x => x.TenantId == journal.TenantId && x.ReversalOfEntryId == journal.Id, ct);

    private async Task<FinanceGlEntry> AddLoanJournalAsync(EmployeeLoan loan, Guid sourceId, string eventType,
        Guid? companyId, string currency, string debitAccount, string creditAccount, decimal amount,
        DateOnly date, CancellationToken ct, Guid? reversalOfEntryId = null)
    {
        await PeriodCloseGuard.ThrowIfClosedAsync(_db, loan.TenantId, companyId, date.ToString("yyyy-MM"), ct);
        var journal = new FinanceGlEntry
        {
            TenantId = loan.TenantId, CompanyId = companyId, SourceModule = "Loan", SourceEntityId = sourceId,
            SourceEntityRef = loan.LoanNumber, EventType = eventType, DebitAccount = debitAccount,
            CreditAccount = creditAccount, Amount = amount, Currency = currency, EntryDate = date,
            Period = date.ToString("yyyy-MM"), Description = $"Loan {eventType}: {loan.LoanNumber}",
            PostedBy = GetUserId(), PostedByName = GetUserName(), ReversalOfEntryId = reversalOfEntryId,
        };
        _db.FinanceGlEntries.Add(journal);
        return journal;
    }

    private async Task<FinanceGlEntry> ReverseLoanJournalAsync(EmployeeLoan loan, FinanceGlEntry original,
        string eventType, DateOnly date, CancellationToken ct)
    {
        var reversal = await AddLoanJournalAsync(loan, original.SourceEntityId, eventType, original.CompanyId,
            original.Currency, original.CreditAccount, original.DebitAccount, original.Amount, date, ct, original.Id);
        original.IsReversed = true;
        return reversal;
    }
}
