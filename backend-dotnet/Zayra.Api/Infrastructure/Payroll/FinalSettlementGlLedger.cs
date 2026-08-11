using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// POD-C1 — the settlement's GL ledger: the ACCRUAL posted on approval, the CLEARING plan the disbursing
/// payroll run consumes, and the reads that make both provably post-once.
///
/// <para><b>THE JOURNALS, END TO END.</b> For a leaver owed 52,000 gratuity + 8,000 encashment, with
/// 3,000 of loan outstanding and 1,000 of notice shortfall:</para>
/// <code>
/// 1. APPROVE  (here — GlEventTypes.SettlementAccrual, SourceEntityId = settlement.Id)
///      DR 2310 End of Service Benefit Provision   min(52,000, provision for THIS employee)   ← POD-C2 seam
///      DR 5110 End of Service Benefit Expense     52,000 − provision consumed
///      DR 5111 Leave Encashment Expense            8,000
///          CR 2320 Final Settlement Payable       60,000   (= Σ DR, the GROSS of the earning lines)
///
/// 2. LOCK of the disbursing OffCycle run (BuildPayrollGlEntries — SettlementPayrollClearing)
///      DR 2320 Final Settlement Payable           60,000   ← the STORED account, remap-immune
///          CR 5113 Final Settlement Deductions      1,000   (notice shortfall — a CONTRA-EXPENSE, not 2199)
///          CR 2107 Loan &amp; Advance Deductions       3,000
///          CR 2100 Salaries Payable                56,000
///
/// 3. SETTLE the payment batch (POD-B1, unchanged code)
///      DR 2100 / CR 1000 Cash-Bank                56,000
///
/// 4. REMIT the LOAN group (POD-B1/B1b, unchanged code)
///      DR 2107 / CR 1400 Employee Loans Receivable 3,000   (capped at what 1400 actually carries)
/// </code>
/// <para>Net effect: 2320 → 0, 2100 → 0, 2107 → 0, the loan receivable amortises, Cash/Bank carries the
/// true outflow, and the settlement expense is recognised EXACTLY ONCE — at approval.</para>
///
/// <para><b>DEDUCTIONS ARE NOT NETTED INTO THE ACCRUAL.</b> 2320 is accrued GROSS and every deduction
/// rides the disbursing run's own lines, where the routing is already correct and already clears
/// (DED:LOAN → 2107 cleared by the LOAN remittance against 1400/1410; DED:RECEIVABLE_RECOVERY credits
/// 1420 directly). Netting them at accrual would double-count them against those paths.</para>
/// </summary>
public static class FinalSettlementGlLedger
{
    /// <summary>Driver key for the payable a settlement accrues to.</summary>
    public const string PayableDriver = "SETTLEMENT_PAYABLE";
    /// <summary>Driver key for the POD-C2 provision this pod only ever CONSUMES.</summary>
    public const string ProvisionDriver = "EOSB_PROVISION";

    /// <summary>Earning component code → the expense driver its accrual debit lands on. Deliberately the
    /// SAME mapping <c>PayrollController.EarningDriverKey</c> uses for the run's own earning lines, so
    /// the accrual and the payslip can never disagree about which account the cost belongs in.</summary>
    public static string ExpenseDriverFor(string componentCode) => componentCode switch
    {
        FinalSettlementComponents.Gratuity        => "EARN:EOSB",
        FinalSettlementComponents.LeaveEncashment => "EARN:LEAVE_ENCASHMENT",
        FinalSettlementComponents.NoticePay       => "EARN:NOTICE_PAY",
        _                                         => "EARN:OTHER",
    };

    /// <summary>
    /// Has this settlement's accrual already posted and not been reversed? The exact predicate shape
    /// <c>Lock</c> / <c>SettlePaymentBatch</c> / <c>RemitStatutory</c> use, so every "has this posted?"
    /// question in payroll has ONE answer.
    /// </summary>
    public static Task<bool> HasLiveAccrualAsync(
        ZayraDbContext db, Guid tenantId, Guid settlementId, CancellationToken ct)
        => db.FinanceGlEntries.AnyAsync(
            x => x.TenantId == tenantId
              && x.SourceModule == FinalSettlementGlDescriptions.SourceModule
              && x.SourceEntityId == settlementId
              && x.EventType == GlEventTypes.SettlementAccrual && !x.IsReversed, ct);

    /// <summary>Has a payroll run already CONSUMED this settlement's payable (and not had it voided)?
    /// A settlement can therefore never be disbursed twice.</summary>
    public static Task<bool> HasLiveClearingAsync(
        ZayraDbContext db, Guid tenantId, Guid settlementId, CancellationToken ct)
    {
        var settlementRef = FinalSettlementGlDescriptions.SettlementRef(settlementId);
        return db.FinanceGlEntries.AnyAsync(
            x => x.TenantId == tenantId
              && x.EventType == GlEventTypes.SettlementPayrollClearing
              && x.SourceEntityRef == settlementRef && !x.IsReversed, ct);
    }

    /// <summary>
    /// Builds the APPROVAL journal. Balanced by construction: Σ DR == the settlement's GROSS earnings ==
    /// the single CR to the payable. The caller still asserts <c>|dr − cr| &lt;= 0.01</c> and 422s
    /// <c>gl_unbalanced</c>, exactly as Lock/settle/remit do, so an ill-behaved input surfaces as a
    /// refusal rather than a crooked ledger.
    /// </summary>
    /// <param name="provisionConsumed">What <see cref="EosbProvisionLedger"/> allowed this settlement to
    /// take out of POD-C2's provision for THIS employee, and the STORED account it must debit. Zero (and
    /// null) until C2 ships, which is why every settlement today expenses the full gratuity.</param>
    public static (List<FinanceGlEntry> Lines, decimal TotalDebits, decimal TotalCredits) BuildAccrual(
        Guid tenantId, EmployeeFinalSettlement settlement, IReadOnlyList<FinalSettlementLine> lines,
        string period, GlResolutionContext gl, Guid? postedBy, string postedByName,
        decimal provisionConsumed = 0m, string? provisionAccount = null)
    {
        var entries = new List<FinanceGlEntry>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var currency = settlement.Currency;
        var employeeRef = EosbProvisionLedger.EmployeeRef(settlement.EmployeeId);

        FinanceGlEntry Line(string debit, string credit, decimal amount, string description, string eventType, string entityRef)
            => new()
            {
                TenantId = tenantId, CompanyId = settlement.CompanyId,
                SourceModule = FinalSettlementGlDescriptions.SourceModule,
                SourceEntityId = settlement.Id, SourceEntityRef = entityRef,
                EventType = eventType,
                DebitAccount = debit, CreditAccount = credit,
                Amount = amount, Currency = currency,
                EntryDate = today, Period = period,
                Description = description,
                PostedBy = postedBy, PostedByName = postedByName,
            };

        // ── DEBITS: one per earning component, on the component's OWN expense account ────────────────
        var gratuityRelief = Math.Min(Math.Max(0m, provisionConsumed), settlement.GratuityAmount);
        foreach (var grp in lines
                     .Where(l => l.LineType == FinalSettlementLineTypes.Earning && l.Amount > 0m)
                     .GroupBy(l => l.ComponentCode, StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var amount = Math.Round(grp.Sum(l => l.Amount), 2);
            if (amount <= 0m) continue;

            // POD-C2 SEAM. The gratuity debit is split: whatever POD-C2 has already PROVIDED for this
            // employee relieves the 2310 liability (that cost was expensed when it was provided), and only
            // the shortfall is expensed now. With no provision the relief is 0 and this is a single debit
            // to 5110 — i.e. exactly what a tenant with no monthly accrual expects, today and forever.
            if (grp.Key == FinalSettlementComponents.Gratuity && gratuityRelief > 0m
                && !string.IsNullOrWhiteSpace(provisionAccount))
            {
                var relieved = Math.Min(gratuityRelief, amount);
                entries.Add(Line(provisionAccount!, string.Empty, relieved,
                    $"{FinalSettlementGlDescriptions.ProvisionReliefPrefix}{settlement.EmployeeCode}",
                    GlEventTypes.EosbProvisionConsumption, employeeRef));
                amount = Math.Round(amount - relieved, 2);
                if (amount <= 0m) continue;
            }

            entries.Add(Line(GlAccountResolver.AccountLabel(ExpenseDriverFor(grp.Key), gl), string.Empty, amount,
                $"{FinalSettlementGlDescriptions.AccrualPrefix}{grp.Key}",
                GlEventTypes.SettlementAccrual, FinalSettlementGlDescriptions.SettlementRef(settlement.Id)));
        }

        // ── CREDIT: the payable, at the GROSS of the earning lines ───────────────────────────────────
        var gross = Math.Round(lines.Where(l => l.LineType == FinalSettlementLineTypes.Earning).Sum(l => l.Amount), 2);
        if (gross > 0m)
            entries.Add(Line(string.Empty, GlAccountResolver.AccountLabel(PayableDriver, gl), gross,
                $"{FinalSettlementGlDescriptions.AccrualPrefix}{settlement.EmployeeCode} — payable",
                GlEventTypes.SettlementAccrual, FinalSettlementGlDescriptions.SettlementRef(settlement.Id)));

        var dr = entries.Where(l => !string.IsNullOrEmpty(l.DebitAccount)).Sum(l => l.Amount);
        var cr = entries.Where(l => !string.IsNullOrEmpty(l.CreditAccount)).Sum(l => l.Amount);
        return (entries, dr, cr);
    }

    /// <summary>
    /// Builds the CLEARING plan for a payroll run's Lock journal: for every settlement the run is
    /// disbursing, how much of its payable is still live and the STORED account that payable sits on.
    ///
    /// <para><paramref name="settlementEarningTotal"/> is the run's ACTUAL Σ of settlement-sourced earning
    /// lines and is spent as a hard budget, so Σ(clearing) + remainder == that total by construction and
    /// the Lock journal balances no matter how the operational data and the ledger disagree (the exact
    /// discipline <see cref="BonusGlLedger.BuildPayrollClearingAsync"/> established). The caller expenses
    /// any leftover to the components' own drivers, which is what a settlement injected into a run with
    /// no accrual at all (a seeder, a legacy fixture) correctly does.</para>
    /// </summary>
    public static async Task<IReadOnlyList<SettlementPayableClearing>> BuildPayrollClearingAsync(
        ZayraDbContext db, Guid tenantId, Guid runId, decimal settlementEarningTotal, CancellationToken ct)
    {
        if (settlementEarningTotal <= 0m) return Array.Empty<SettlementPayableClearing>();

        // The settlements this run is disbursing (stamped by Process).
        // IgnoreQueryFilters is intentional: company filter only — settlement ledger integrity is a system
        // read across entities. Tenant re-applied in the WHERE.
        var settlements = await db.EmployeeFinalSettlements.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.PayrollRunId == runId
                     && s.Status != FinalSettlementStatuses.Cancelled)
            .Select(s => new { s.Id, s.CompanyId, s.EmployeeId, s.GrossPayable })
            .ToListAsync(ct);
        if (settlements.Count == 0) return Array.Empty<SettlementPayableClearing>();

        var ids = settlements.Select(s => s.Id).ToList();

        // Live accrual credits, per settlement, at their STORED account.
        // IgnoreQueryFilters is intentional: company filter only — accrual lines must be seen wherever
        // booked. Tenant re-applied.
        var accruals = await db.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenantId
                     && x.SourceModule == FinalSettlementGlDescriptions.SourceModule
                     && ids.Contains(x.SourceEntityId)
                     && x.EventType == GlEventTypes.SettlementAccrual && !x.IsReversed
                     && x.CreditAccount != "")
            .Select(x => new { x.SourceEntityId, x.CompanyId, x.CreditAccount, x.Amount })
            .ToListAsync(ct);
        if (accruals.Count == 0) return Array.Empty<SettlementPayableClearing>();

        // Anything that has ALREADY debited that payable (a clearing on an earlier, non-voided run, or a
        // cancellation contra), so a payable can never be cleared twice.
        var refs = ids.Select(FinalSettlementGlDescriptions.SettlementRef).ToList();
        // IgnoreQueryFilters is intentional: company filter only — cleared rows must net against their
        // accrual. Tenant re-applied.
        var clearedRows = await db.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsReversed
                     && ((x.EventType == GlEventTypes.SettlementPayrollClearing && refs.Contains(x.SourceEntityRef))
                      || (x.EventType == GlEventTypes.SettlementAccrualReversal && ids.Contains(x.SourceEntityId)))
                     && x.DebitAccount != "")
            .Select(x => new { x.SourceEntityId, x.SourceEntityRef, x.EventType, x.DebitAccount, x.Amount })
            .ToListAsync(ct);

        var cleared = new Dictionary<(Guid, string), decimal>();
        foreach (var r in clearedRows)
        {
            var sid = r.EventType == GlEventTypes.SettlementPayrollClearing
                ? (Guid.TryParse(r.SourceEntityRef, out var parsed) ? parsed : Guid.Empty)
                : r.SourceEntityId;
            if (sid == Guid.Empty) continue;
            var key = (sid, r.DebitAccount);
            cleared[key] = cleared.GetValueOrDefault(key) + r.Amount;
        }

        var plan = new List<SettlementPayableClearing>();
        var budget = settlementEarningTotal;
        foreach (var grp in accruals
                     .GroupBy(a => (a.SourceEntityId, a.CompanyId, a.CreditAccount))
                     .OrderBy(g => g.Key.SourceEntityId))
        {
            if (budget <= 0m) break;
            var outstanding = Math.Max(0m, Math.Round(
                grp.Sum(a => a.Amount) - cleared.GetValueOrDefault((grp.Key.SourceEntityId, grp.Key.CreditAccount)), 2));
            if (outstanding <= 0m) continue;
            var take = Math.Min(outstanding, budget);
            if (take <= 0m) continue;
            budget -= take;
            var settlement = settlements.First(s => s.Id == grp.Key.SourceEntityId);
            plan.Add(new SettlementPayableClearing(
                grp.Key.SourceEntityId,
                EosbProvisionLedger.EmployeeRef(settlement.EmployeeId),
                grp.Key.CreditAccount,
                take,
                // Carry the ACCRUAL's legal entity, not the run's: the clearing retires a payable sitting in
                // that entity's books, and the match above is on (settlement, account).
                grp.Key.CompanyId));
        }
        return plan;
    }
}
