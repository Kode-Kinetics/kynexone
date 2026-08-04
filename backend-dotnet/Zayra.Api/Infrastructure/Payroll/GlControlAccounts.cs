using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// One control account's carrying balance, read in the two views that matter.
/// </summary>
/// <param name="Scoped">
/// What the posting company owns: its own rows PLUS the unattributed (CompanyId == null) rows every
/// pre-POD-B1b posting carries. For <c>companyId == null</c> this is the unattributed pool ALONE — a
/// posting with no legal entity has no claim on an entity's attributed balances.
/// </param>
/// <param name="TenantWide">Σ DR − Σ CR over every row in the tenant, whatever company stamped it.</param>
public sealed record ControlAccountBalance(string Account, Guid? CompanyId, decimal Scoped, decimal TenantWide)
{
    /// <summary>
    /// POD-B1b-FIX (re-audit #1) — how much of this account a posting in <see cref="CompanyId"/> may
    /// legitimately RELIEVE (credit an asset / debit a liability).
    ///
    /// <para>The <see cref="Scoped"/> view alone is NOT a safe cap in a multi-company tenant, because the
    /// unattributed pool is inside EVERY company's view while the relief posted against it is stamped
    /// with the relieving company and therefore invisible to the next one. Companies A and B against one
    /// 10,000 unattributed 1400 balance would each read 10,000 and each relieve 6,000 — 12,000 of credits
    /// against 10,000 of debits, i.e. a 2,000 CREDIT balance on an asset. That is P0-2 all over again, one
    /// level up, and <c>BuildLiabilityClearingGl</c> is balanced by construction so no dr/cr guard can see it.</para>
    ///
    /// <para>Clamping to <see cref="TenantWide"/> — which already nets every other company's relief —
    /// makes Σ(relief) ≤ Σ(debits) TENANT-WIDE an arithmetic certainty, whatever order the entities post in:
    /// B now reads min(10,000, 4,000) = 4,000, credits 4,000 to the receivable and routes 2,000 to Cash/Bank,
    /// and 1400 lands exactly on zero.</para>
    /// </summary>
    public decimal AvailableForRelief => Math.Max(0m, Math.Min(Scoped, TenantWide));
}

/// <summary>
/// POD-B1b-FIX — carrying balances of the control accounts the payroll/bonus/loan/advance journals move,
/// plus the sign invariants a controller signs off on.
///
/// <para>BALANCE SEMANTICS. A balance is Σ debits − Σ credits over EVERY row that names the account,
/// reversed rows included. <c>IsReversed</c> is an audit LINK ("this line has been contra'd"), not a
/// balance filter: the contra is itself a persisted row (PayrollVoidService.cs:63-88,
/// BonusesController RejectBatch), so excluding the original while keeping its contra would report the
/// negative of the truth. The sub-ledger reads (<see cref="BonusGlLedger.LoadPositionsAsync"/>) filter on
/// !IsReversed because they ask a different question — which accrual is still LIVE — not what an account
/// is worth.</para>
/// </summary>
public static class GlControlAccounts
{
    /// <summary>POD-B1b-FIX (re-audit #7) — the control accounts whose SIGN this pod is accountable for,
    /// as driver keys, so a tenant that remapped them per company is covered too.</summary>
    public const string BonusPayableDriver      = "BONUS_PAYABLE";
    public const string LoanReceivableDriver    = "LOAN_RECEIVABLE";
    public const string AdvanceReceivableDriver = "ADVANCE_RECEIVABLE";

    /// <summary>
    /// Loads both views of one persisted account LABEL. See <see cref="ControlAccountBalance"/> for what
    /// each view means and why the cap needs both.
    ///
    /// <para>IgnoreQueryFilters is intentional: an account balance is a SYSTEM read that must see rows
    /// posted by every company (FinanceGlEntry.CompanyId is a plain dimension, never a scope) regardless
    /// of the caller's claims; the explicit TenantId predicate re-applies exact tenant scope and never
    /// reads another tenant.</para>
    /// </summary>
    public static async Task<ControlAccountBalance> LoadAsync(
        ZayraDbContext db, Guid tenantId, Guid? companyId, string accountLabel, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accountLabel))
            return new ControlAccountBalance(accountLabel ?? string.Empty, companyId, 0m, 0m);

        // IgnoreQueryFilters is intentional: an account balance is a SYSTEM read that must see rows
        // posted by every company (FinanceGlEntry.CompanyId is a plain reporting dimension, never a
        // scope) regardless of the caller's own claims — and the tenant-wide view below is precisely
        // what stops one company relieving another's receivable. The explicit TenantId predicate
        // re-applies exact tenant scope, so this never reads another tenant.
        var rows = await db.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenantId
                     && (x.DebitAccount == accountLabel || x.CreditAccount == accountLabel))
            .Select(x => new { x.CompanyId, x.DebitAccount, x.Amount })
            .ToListAsync(ct);

        decimal Net(Func<Guid?, bool> inScope) =>
            Math.Round(rows.Where(r => inScope(r.CompanyId))
                           .Sum(r => r.DebitAccount == accountLabel ? r.Amount : -r.Amount), 2);

        // companyId == null ⇒ the unattributed pool alone. A run with no legal entity must not be able to
        // spend an entity's receivable (the re-audit's "run.CompanyId == null is worse" case).
        var scoped = companyId is null
            ? Net(c => c is null)
            : Net(c => c == companyId || c is null);

        return new ControlAccountBalance(accountLabel, companyId, scoped, Net(_ => true));
    }

    /// <summary>The scoped carrying balance only — see <see cref="ControlAccountBalance.Scoped"/>.
    /// Callers CAPPING a relief posting must use <see cref="ControlAccountBalance.AvailableForRelief"/>
    /// instead: this view double-counts the unattributed pool across companies by design.</summary>
    public static async Task<decimal> BalanceAsync(
        ZayraDbContext db, Guid tenantId, Guid? companyId, string accountLabel, CancellationToken ct)
        => (await LoadAsync(db, tenantId, companyId, accountLabel, ct)).Scoped;

    /// <summary>
    /// POD-B1b-FIX (P2-3) — the sign invariants of the control accounts this pod touches, evaluated over
    /// a set of ledger rows. Returns one human-readable violation per broken account, empty when clean.
    ///
    /// <list type="bullet">
    /// <item>Bonus Payable is a LIABILITY: it may sit at zero or in credit, never in debit. A debit
    ///   balance is the fingerprint of P0-1 (the same accrual cleared by two companies).</item>
    /// <item>Loans / Advances Receivable are ASSETS: they may sit at zero or in debit, never in credit.
    ///   A credit balance is the fingerprint of P0-2 (a receivable credited on remittance that was never
    ///   debited at disbursement).</item>
    /// </list>
    ///
    /// <para>Takes rows rather than querying so it can assert over a posting that has not been committed
    /// yet, and so tests can hold the whole ledger in memory. Account matching is on the CODE token
    /// ("2300 "), never a substring, so 12300 / 23000 can never satisfy a 2300 assertion.</para>
    /// </summary>
    public static IReadOnlyList<string> FindSignViolations(
        IEnumerable<FinanceGlEntry> ledger,
        string bonusPayableAccount = "2300 - Bonus Payable",
        string loanReceivableAccount = "1400 - Employee Loans Receivable",
        string advanceReceivableAccount = "1410 - Employee Salary Advances")
    {
        var rows = ledger.ToList();
        var violations = new List<string>();

        void Liability(string label)
        {
            var balance = Balance(rows, label);
            if (balance > 0.01m)
                violations.Add($"LIABILITY '{label}' carries a DEBIT balance of {balance:0.00} — a payable " +
                               "was cleared for more than was ever accrued.");
        }
        void Asset(string label)
        {
            var balance = Balance(rows, label);
            if (balance < -0.01m)
                violations.Add($"ASSET '{label}' carries a CREDIT balance of {-balance:0.00} — a receivable " +
                               "was credited for more than was ever debited at disbursement.");
        }

        Liability(bonusPayableAccount);
        Asset(loanReceivableAccount);
        Asset(advanceReceivableAccount);
        return violations;
    }

    /// <summary>Σ DR − Σ CR for one account label over an in-memory ledger (same semantics as
    /// <see cref="BalanceAsync"/>). Positive = debit balance, negative = credit balance.</summary>
    public static decimal Balance(IEnumerable<FinanceGlEntry> ledger, string accountLabel)
    {
        var rows = ledger.ToList();
        return Math.Round(
            rows.Where(l => l.DebitAccount == accountLabel).Sum(l => l.Amount)
          - rows.Where(l => l.CreditAccount == accountLabel).Sum(l => l.Amount), 2);
    }

    /// <summary>
    /// POD-B1b-FIX (re-audit #7) — the SAME sign invariants as <see cref="FindSignViolations"/>, but
    /// evaluated against the PERSISTED ledger, on the account labels THIS tenant/company actually posts
    /// to (resolved through <see cref="GlAccountResolver"/>, so a per-company <c>GlAccountMapping</c>
    /// remap is covered), so a live tenant can be told it has a broken control account.
    ///
    /// <para>Without this the invariant existed only inside the test project: neither a debit balance on
    /// Bonus Payable nor a credit balance on a receivable was surfaced by any endpoint, so the exact
    /// failures this pod fixes would have gone unnoticed in production. Wired into
    /// <c>GET /api/finance/gl/control-accounts/health</c> and the bonus <c>/audit</c> report.</para>
    ///
    /// <para>Both views are reported: TENANT-WIDE is the one the arithmetic guarantees (see
    /// <see cref="ControlAccountBalance.AvailableForRelief"/>) and a violation there is a hard defect;
    /// the SCOPED view is informational for a single entity, where the shared unattributed pool means a
    /// legitimate posting order can leave one entity's own rows net-negative.</para>
    /// </summary>
    public static async Task<IReadOnlyList<ControlAccountHealth>> CheckAsync(
        ZayraDbContext db, Guid tenantId, Guid? companyId, CancellationToken ct)
    {
        var glCtx = await GlAccountResolver.LoadAsync(db, tenantId, companyId, ct);
        var checks = new (string Driver, string Nature)[]
        {
            (BonusPayableDriver,      "Liability"),
            (LoanReceivableDriver,    "Asset"),
            (AdvanceReceivableDriver, "Asset"),
        };

        var results = new List<ControlAccountHealth>();
        foreach (var (driver, nature) in checks)
        {
            var label = GlAccountResolver.AccountLabel(driver, glCtx);
            var balance = await LoadAsync(db, tenantId, companyId, label, ct);
            // A liability must never sit in DEBIT; an asset must never sit in CREDIT.
            var sign = nature == "Liability" ? 1m : -1m;
            string? violation = null;
            if (balance.TenantWide * sign > 0.01m)
                violation = nature == "Liability"
                    ? $"LIABILITY '{label}' carries a DEBIT balance of {balance.TenantWide:0.00} — a payable " +
                       "was cleared for more than was ever accrued."
                    : $"ASSET '{label}' carries a CREDIT balance of {-balance.TenantWide:0.00} — a receivable " +
                       "was credited for more than was ever debited at disbursement.";
            results.Add(new ControlAccountHealth(
                driver, label, nature, balance.Scoped, balance.TenantWide, violation));
        }
        return results;
    }
}

/// <summary>One control account's sign health, as surfaced to finance. <see cref="Violation"/> null = clean.</summary>
public sealed record ControlAccountHealth(
    string Driver, string Account, string Nature, decimal ScopedBalance, decimal TenantBalance, string? Violation)
{
    public bool IsHealthy => Violation is null;
}

/// <summary>
/// POD-B1b-FIX (P0-2) — splits a withheld-EMI clearing between the RECEIVABLE it amortises and Cash/Bank,
/// capped at what the receivable actually holds.
///
/// <para>THE DEFECT. POD-B1b changed the LOAN remittance group to credit 1400 / 1410 instead of Cash/Bank,
/// on the reasoning that the cash already left at disbursement. That reasoning is right ONLY when the
/// disbursement was actually booked, and very often it was not: LoansController predates the disbursement
/// GL (added in 72d3983; the controller in 74195b2), so every loan Active before that commit has no 1400
/// debit at all; AuthSeeder.cs:1132/:1155 create Active loans and advances with a real OutstandingBalance
/// and no GL whatsoever; and the post-once probe (LoansController.cs:247) means a second Approved decision
/// that RAISES ApprovedAmount (:237-240) increases the operational balance with no offsetting GL. Crediting
/// a receivable that was never debited drives an ASSET to a credit balance, and because
/// <c>BuildLiabilityClearingGl</c> is balanced by construction the gl_unbalanced 422 can never catch it.</para>
///
/// <para>THE FIX. Credit the receivable only up to its ACTUAL carrying balance and route the excess to
/// Cash/Bank — which is precisely the pre-B1b behaviour for the uncovered portion, so a tenant whose loans
/// were never booked as receivables is byte-identical to POD-B1, while a tenant whose loans WERE booked
/// finally amortises 1400 to zero. Mixed populations split line by line. The budget is metered across the
/// whole remittance (the P0-1 lesson: never re-read an immutable balance per iteration), and the split is
/// returned so the caller can surface and audit it.</para>
///
/// <para>Σ(split) == the amount handed in, EXACTLY (no rounding on this path — see <see cref="Split"/>),
/// so the journal stays balanced by construction and the caller's existing dr/cr 422 keeps its meaning.</para>
///
/// <para>POD-B1b-FIX (re-audit #1) — the budget this is constructed with MUST be
/// <see cref="ControlAccountBalance.AvailableForRelief"/>, never the raw scoped balance: the splitter
/// meters within one remittance, and the unattributed (CompanyId == null) pool is shared by every
/// company, so two entities reading the same pool would each relieve it in full.</para>
/// </summary>
public sealed class ReceivableClearingSplitter
{
    private readonly string _cashAccount;
    private readonly Dictionary<string, decimal> _available;
    private readonly Dictionary<string, decimal> _appliedToReceivable = new(StringComparer.Ordinal);
    private decimal _appliedToCash;

    /// <param name="cashAccount">Where the uncovered excess goes — the pre-B1b destination.</param>
    /// <param name="receivableBalances">Account label → carrying (debit) balance. A label appearing twice
    /// (a tenant that mapped LOAN_RECEIVABLE and ADVANCE_RECEIVABLE to the SAME account) correctly shares
    /// one budget rather than double-spending it.</param>
    public ReceivableClearingSplitter(
        string cashAccount, IEnumerable<KeyValuePair<string, decimal>> receivableBalances)
    {
        _cashAccount = cashAccount;
        _available = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var kv in receivableBalances)
            _available[kv.Key] = Math.Max(0m, kv.Value);   // a receivable already at/below zero funds nothing
    }

    /// <summary>Receivable label → how much of it this remittance amortised.</summary>
    public IReadOnlyDictionary<string, decimal> AppliedToReceivable => _appliedToReceivable;

    /// <summary>How much of this remittance was a real cash payout (uncovered by any receivable).</summary>
    public decimal AppliedToCash => _appliedToCash;

    /// <summary>
    /// Splits <paramref name="amount"/> for one accrual line.
    /// <paramref name="receivableAccount"/> null ⇒ the component is not an employer-granted loan/advance
    /// (e.g. a third-party garnishment routed to DED:LOAN) and is a genuine cash payment in full.
    /// </summary>
    public IReadOnlyList<(string Account, decimal Amount)> Split(string? receivableAccount, decimal amount)
    {
        if (amount <= 0m) return Array.Empty<(string, decimal)>();

        if (receivableAccount is null || !_available.TryGetValue(receivableAccount, out var available))
        {
            _appliedToCash += amount;
            return new[] { (_cashAccount, amount) };
        }

        // POD-B1b-FIX (re-audit #6) — NO rounding anywhere on this path. The DEBIT leg posts the accrual's
        // stored amount verbatim (decimal(18,4); an EMI is ApprovedAmount / Installments and is routinely
        // 4dp), so rounding the credit split to 2dp made Σ CR ≠ Σ DR by up to ~0.01 per line — right at
        // the caller's dr/cr tolerance (PayrollController.cs:2429), which either posts a journal off by a
        // cent or 422s a legitimate remittance. The residual is now EXACT by construction:
        // toReceivable + toCash == amount, bit for bit.
        var toReceivable = Math.Min(amount, available);
        var toCash = amount - toReceivable;
        _available[receivableAccount] = available - toReceivable;

        var split = new List<(string Account, decimal Amount)>(2);
        if (toReceivable > 0m)
        {
            split.Add((receivableAccount, toReceivable));
            _appliedToReceivable[receivableAccount] =
                _appliedToReceivable.GetValueOrDefault(receivableAccount) + toReceivable;
        }
        if (toCash > 0m)
        {
            split.Add((_cashAccount, toCash));
            _appliedToCash += toCash;
        }
        return split;
    }
}
