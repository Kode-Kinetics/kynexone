using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Finance;

public partial class LoansController
{
    private async Task<string?> LegacyCollectionConversionIssueAsync(EmployeeLoan loan, CancellationToken ct)
    {
        if (loan.RepaymentMethod != "PayrollDeduction" || loan.PolicyId.HasValue || loan.PolicySnapshotJson.Trim() != "{}")
            return "This conversion is only for reconciled legacy loans without a frozen policy; it cannot override a current agreement.";
        if (!loan.CompanyId.HasValue || !loan.DisbursementDate.HasValue || loan.Status is not ("Active" or "Overdue") || loan.OutstandingBalance <= 0)
            return "A company-attributed, disbursed legacy loan with remaining debt is required.";
        if (loan.CollectionStatus != "OnHold" || loan.IsLockedByPayroll)
            return "HR must place collection on hold before Finance requests conversion. Payroll-locked loans cannot be converted.";
        var latestReview = await _db.LoanAuditLogs.Where(x => x.TenantId == loan.TenantId && x.LoanId == loan.Id
            && (x.Action == "LifecycleReviewed" || x.Action == "EmploymentReviewRequired"))
            .OrderByDescending(x => x.CreatedAtUtc).FirstOrDefaultAsync(ct);
        if (latestReview?.Action != "LifecycleReviewed" || !latestReview.PerformedBy.HasValue)
            return "An explicit HR hold review is required; an automatic lifecycle hold is not conversion approval.";
        try
        {
            using var review = JsonDocument.Parse(latestReview.NewValuesJson);
            if (review.RootElement.ValueKind != JsonValueKind.Object
                || !review.RootElement.TryGetProperty("Decision", out var decision) || decision.ValueKind != JsonValueKind.String || decision.GetString() != "Hold"
                || !review.RootElement.TryGetProperty("CollectionStatus", out var state) || state.ValueKind != JsonValueKind.String || state.GetString() != "OnHold")
                return "The latest HR review must explicitly place collection on hold.";
        }
        catch (JsonException) { return "The HR hold evidence requires reconciliation."; }
        // The committed hold prevents newly calculated payrolls consuming this loan. Existing runs
        // must finish/void first; no pre-existing calculated deduction may survive a mode change.
        if (await _db.PayrollRuns.AnyAsync(x => x.TenantId == loan.TenantId && (x.CompanyId == loan.CompanyId || x.CompanyId == null)
            && x.Status != "Paid" && x.Status != "Voided" && x.Status != "Cancelled", ct))
            return "Complete or void all open payroll runs for this lender before converting historical collection.";
        if (await ScopedBypass.TenantWide(_db.PayrollRunConsumptions, loan.TenantId,
            "Boolean payroll-history check for this authorized legacy loan only; historical cross-company deductions must block manual-only conversion.")
            .AnyAsync(x => x.TenantId == loan.TenantId
            && x.ArtifactType == PayrollConsumptionArtifacts.Loan && x.ArtifactId == loan.Id, ct)
            || await _db.LoanInstallments.AnyAsync(x => x.TenantId == loan.TenantId && x.LoanId == loan.Id && x.PayrollRunId.HasValue, ct))
            return "Payroll collection evidence exists. This is not a legacy manual-only loan; use payroll reconciliation instead.";
        var schedule = await _db.LoanInstallments.Where(x => x.TenantId == loan.TenantId && x.LoanId == loan.Id).ToListAsync(ct);
        if (loan.ApprovedAmount - loan.TotalRepaid != loan.OutstandingBalance || schedule.Count == 0
            || schedule.Any(x => x.AmountPaid < 0 || x.AmountPaid > x.AmountDue || x.Status is "Cancelled" or "Waived")
            || schedule.Sum(x => x.AmountDue) != loan.ApprovedAmount || schedule.Sum(x => x.AmountPaid) != loan.TotalRepaid)
            return "Reconcile the principal, paid history, remaining balance and installment schedule before conversion.";
        var original = await OriginalDisbursementAsync(loan, ct);
        if (original == null) return LoanJournalReconciliationRequired;
        var receiptIds = _db.LoanRepayments.Where(x => x.TenantId == loan.TenantId && x.LoanId == loan.Id).Select(x => x.Id);
        var journals = await _db.FinanceGlEntries.Where(x => x.TenantId == loan.TenantId && x.SourceModule == "Loan"
            && (x.SourceEntityId == loan.Id || receiptIds.Contains(x.SourceEntityId))).ToListAsync(ct);
        if (journals.Any(x => x.CompanyId != loan.CompanyId || x.Currency != loan.Currency || !IsMoney(x.Amount)
            || string.IsNullOrWhiteSpace(x.DebitAccount) || string.IsNullOrWhiteSpace(x.CreditAccount)
            || (x.DebitAccount != original.DebitAccount && x.CreditAccount != original.DebitAccount)))
            return "Historical loan journals do not consistently use the original lender, currency and receivable account. Reconcile them first.";
        var receivableBalance = journals.Sum(x => (x.DebitAccount == original.DebitAccount ? x.Amount : 0m)
            - (x.CreditAccount == original.DebitAccount ? x.Amount : 0m));
        if (receivableBalance != loan.OutstandingBalance)
            return "Historical repayment accounting does not reconcile to the paid amount and remaining loan receivable. Reconcile it first.";
        return null;
    }
}
