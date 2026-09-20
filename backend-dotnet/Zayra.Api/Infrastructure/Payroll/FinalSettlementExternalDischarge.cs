using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// S2-B2 — discharging a final settlement that was PAID OUTSIDE PAYROLL (bank transfer, cheque, cash).
///
/// <para><b>WHY THIS EXISTS.</b> POD-C1 disburses a settlement through the payroll rails and nothing else,
/// and <c>OffboardingController.Complete</c> refuses to archive a leaver until the settlement reaches
/// <c>Paid</c> — which only the rails can produce. A GCC HR department settles one mid-month leaver by
/// bank transfer within the KSA Art. 88 window; they do not open an OffCycle payroll run for one person.
/// For those clients the offboarding could NEVER be completed: it sat <c>InProgress</c> forever, the
/// employee stayed <c>Offboarded</c> (an OCCUPYING status, so headcount and the staffing budget stayed
/// wrong), and the "Offboard an employee end to end" UAT script stopped at step 6.</para>
///
/// <para><b>WHY IT IS A JOURNAL AND NOT A TICKBOX.</b> Approval already credited 2320 Final Settlement
/// Payable at GROSS. If "paid by bank transfer" only flipped a flag, 2320 would carry a liability that no
/// longer exists — the exact "stored configuration that changes no behaviour" failure this codebase
/// treats as unacceptable. So the discharge posts the same journal the payroll rail posts, minus the
/// rail:</para>
/// <code>
///     DR 2320 Final Settlement Payable        GROSS   ← the accrual's STORED account, remap-immune
///         CR 5113 Final Settlement Deductions  TotalDeductions  (notice shortfall / other)
///         CR 1000 Cash-Bank                    NetPayable       (what actually left the account)
/// </code>
/// <para>Net effect on 2320 is identical to the payroll route, so the payable still provably returns to
/// zero and the expense is still recognised exactly once — at approval.</para>
///
/// <para><b>WHAT IT REFUSES, AND WHY.</b> This is deliberately the NARROW path, not a second payment
/// mechanism:</para>
/// <list type="bullet">
///   <item>No live accrual, or an accrual already partly cleared → refused. The run rail owns that
///     payable and clearing it twice would drive 2320 into debit.</item>
///   <item>Any planned loan / advance / receivable recovery → refused. Those relieve control accounts
///     (1400/1410/1420) through the run's own deduction lines and its <c>AvailableForRelief</c> clamp;
///     re-implementing that here would be the double-recovery POD-C1 designed the single decrement path
///     to prevent.</item>
///   <item>A closed GL period → refused, the same guard approval uses.</item>
/// </list>
/// <para>An externally-discharged settlement registers as CLEARED (see
/// <c>FinalSettlementGlLedger.HasLiveClearingAsync</c> / <c>BuildPayrollClearingAsync</c>), so it can
/// never afterwards be swept into a payroll run or cancelled in isolation.</para>
/// </summary>
public static class FinalSettlementExternalDischarge
{
    /// <summary>Payment methods the discharge accepts. Closed on purpose: it is evidence of a real bank
    /// movement, not a free-text note.</summary>
    public static readonly string[] Methods = ["BankTransfer", "Cheque", "Cash", "Other"];

    public static bool TryNormalizeMethod(string? requested, out string normalized)
    {
        normalized = Methods.FirstOrDefault(
            m => string.Equals(m, requested?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        return normalized.Length > 0;
    }

    /// <summary>Why a discharge was refused. <c>null</c> <see cref="DischargeRefusal.Error"/> means allowed.</summary>
    public sealed record DischargeRefusal(string Error, string Message);

    public sealed record DischargeResult(
        IReadOnlyList<FinanceGlEntry> Journal, decimal PayableCleared, string PayableAccount, string Period);

    /// <summary>
    /// Stages the discharge journal onto <paramref name="db"/> (no SaveChanges — the caller commits it
    /// with the status change so a failure cannot leave a journal without a payment or vice versa).
    /// Returns either the journal or the refusal.
    /// </summary>
    public static async Task<(DischargeResult? Result, DischargeRefusal? Refusal)> StageAsync(
        ZayraDbContext db, EmployeeFinalSettlement s, DateOnly paidOn, string method, string reference,
        Guid? postedBy, string postedByName, CancellationToken ct)
    {
        if (s.Status != FinalSettlementStatuses.Approved)
            return (null, new DischargeRefusal("settlement_not_approved",
                $"The settlement is '{s.Status}'. Only an Approved settlement — one whose amount is signed off and "
                + "whose accrual has posted to the ledger — can be recorded as paid outside payroll."));

        var plannedRecovery = s.PlannedLoanRecovery + s.PlannedAdvanceRecovery + s.PlannedReceivableRecovery;
        if (plannedRecovery > 0.01m)
            return (null, new DischargeRefusal("settlement_recovers_employee_debt",
                $"This settlement plans {plannedRecovery:N2} of loan/advance/receivable recovery, which is executed "
                + "by the disbursing payroll run against the employee's own control accounts. Disburse it through an "
                + "OffCycle run so the recovery is taken exactly once, or clear the debt before settling externally."));

        var period = $"{paidOn.Year}-{paidOn.Month:D2}";
        if (await PeriodCloseGuard.IsClosedAsync(db, s.TenantId, s.CompanyId, period, ct))
            return (null, new DischargeRefusal("gl_period_closed",
                $"GL period {period} is closed. Reopen it, or record the payment in an open period."));

        // The accrual, at its STORED credit account — the clearing DEBIT must hit the exact account the
        // approval credited, so a chart-of-accounts remap can never leave 2320 off zero.
        // No .IgnoreQueryFilters() here (unlike FinalSettlementGlLedger's reads): FinanceGlEntry is
        // tenant-owned but deliberately NOT ICompanyScoped, so the only ambient filter is the tenant one
        // — which is exactly the scope this read wants, and the WHERE re-applies it explicitly anyway.
        var accruals = await db.FinanceGlEntries.AsNoTracking()
            .Where(x => x.TenantId == s.TenantId
                     && x.SourceModule == FinalSettlementGlDescriptions.SourceModule
                     && x.SourceEntityId == s.Id
                     && x.EventType == GlEventTypes.SettlementAccrual
                     && !x.IsReversed && x.CreditAccount != "")
            .Select(x => new { x.CreditAccount, x.Amount })
            .ToListAsync(ct);
        if (accruals.Count == 0)
            return (null, new DischargeRefusal("settlement_not_accrued",
                "No live accrual journal was found for this settlement, so there is no payable to discharge. "
                + "Re-approve the settlement (which posts the accrual) before recording a payment against it."));

        var payableAccount = accruals[0].CreditAccount;
        var accrued = Math.Round(accruals.Sum(a => a.Amount), 2);

        var settlementRef = FinalSettlementGlDescriptions.SettlementRef(s.Id);
        var alreadyCleared = await db.FinanceGlEntries.AsNoTracking()
            .Where(x => x.TenantId == s.TenantId && !x.IsReversed && x.DebitAccount != ""
                     && ((x.EventType == GlEventTypes.SettlementPayrollClearing && x.SourceEntityRef == settlementRef)
                      || ((x.EventType == GlEventTypes.SettlementAccrualReversal
                        || x.EventType == GlEventTypes.SettlementExternalPayment) && x.SourceEntityId == s.Id)))
            .SumAsync(x => (decimal?)x.Amount, ct) ?? 0m;
        var outstanding = Math.Round(accrued - alreadyCleared, 2);
        if (outstanding <= 0m)
            return (null, new DischargeRefusal("settlement_payable_already_cleared",
                "This settlement's payable has already been cleared. It cannot be paid a second time."));
        if (Math.Abs(outstanding - Math.Round(s.GrossPayable, 2)) > 0.01m)
            return (null, new DischargeRefusal("settlement_payable_partially_cleared",
                $"The outstanding payable ({outstanding:N2}) does not equal the settlement's gross ({s.GrossPayable:N2}) — "
                + "part of it has already been consumed by a payroll run. Finish the disbursement on that run."));

        var gross      = outstanding;
        var deductions = Math.Round(s.TotalDeductions, 2);
        var net        = Math.Round(gross - deductions, 2);
        if (net < 0m)
            return (null, new DischargeRefusal("settlement_net_negative",
                $"The settlement's deductions ({deductions:N2}) exceed its gross ({gross:N2}); nothing can be paid out."));

        var glCtx = await GlAccountResolver.LoadAsync(db, s.TenantId, s.CompanyId, ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var label = $"{s.EmployeeName} ({s.EmployeeCode}) — {method}{(string.IsNullOrWhiteSpace(reference) ? "" : " " + reference)}";

        FinanceGlEntry Row(string credit, decimal amount, string note) => new()
        {
            TenantId = s.TenantId, CompanyId = s.CompanyId,
            SourceModule = FinalSettlementGlDescriptions.SourceModule,
            SourceEntityId = s.Id, SourceEntityRef = settlementRef,
            EventType = GlEventTypes.SettlementExternalPayment,
            DebitAccount = payableAccount, CreditAccount = credit,
            Amount = amount, Currency = s.Currency,
            EntryDate = today, Period = period,
            Description = note + label,
            PostedBy = postedBy, PostedByName = postedByName,
        };

        var journal = new List<FinanceGlEntry>();
        if (deductions > 0m)
            journal.Add(Row(GlAccountResolver.AccountLabel("DED:SETTLEMENT_RECOVERY", glCtx), deductions,
                FinalSettlementGlDescriptions.ExternalPaymentPrefix + "deductions — "));
        if (net > 0m)
            journal.Add(Row(GlAccountResolver.AccountLabel("CASH_BANK", glCtx), net,
                FinalSettlementGlDescriptions.ExternalPaymentPrefix));

        // Balanced by construction (deductions + net == gross == the payable being retired), asserted the
        // same way every other posting in this module asserts it, so a drifted input refuses instead of
        // writing a crooked ledger.
        var emitted = Math.Round(journal.Sum(l => l.Amount), 2);
        if (Math.Abs(emitted - gross) > 0.01m)
            return (null, new DischargeRefusal("gl_unbalanced",
                $"Discharge journal does not balance: emitted {emitted:N2} against a payable of {gross:N2}."));

        db.FinanceGlEntries.AddRange(journal);
        return (new DischargeResult(journal, gross, payableAccount, period), null);
    }
}
