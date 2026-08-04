using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>How much of one (batch, company) bonus accrual is still sitting on the balance sheet.</summary>
/// <param name="BatchId">The BonusBatch the accrual belongs to (FinanceGlEntry.SourceEntityId).</param>
/// <param name="CompanyId">Legal entity the accrual was booked in; null for pre-B1b / unattributed rows.</param>
/// <param name="AccrualAccount">The accrual line's STORED CreditAccount — what any clearing entry must DR.</param>
/// <param name="Accrued">Gross credited to Bonus Payable (may be NET for pre-B1b accruals).</param>
/// <param name="Cleared">Already debited back out by payroll clearing, manual payment, or cancellation contra.</param>
public sealed record BonusAccrualPosition(
    Guid BatchId, Guid? CompanyId, string AccrualAccount, string Currency, decimal Accrued, decimal Cleared)
{
    public decimal Remaining => Math.Max(0m, Math.Round(Accrued - Cleared, 2));

    /// <summary>Identity of this position in the payable sub-ledger — the exact tuple
    /// <see cref="BonusGlLedger.LoadPositionsAsync"/> groups by, so two positions are the same accrual
    /// if and only if their keys match. Used by <see cref="BonusAccrualCursor"/> to meter consumption.</summary>
    public (Guid BatchId, Guid? CompanyId, string AccrualAccount) Key => (BatchId, CompanyId, AccrualAccount);
}

/// <summary>
/// POD-B1b-FIX (P0-1) — a MUTABLE, single-call view over the immutable positions snapshot returned by
/// <see cref="BonusGlLedger.LoadPositionsAsync"/>.
///
/// <para>WHY THIS EXISTS. <see cref="BonusGlLedger.PositionFor"/> falls back to the unattributed
/// (CompanyId == null) legacy accrual for ANY company that has no exact match — which is every company
/// on all ~55 live tenants, because every accrual posted before this pod carries CompanyId = NULL. A
/// caller that loads the snapshot once and then loops over company groups therefore re-reads the SAME
/// position for every group and clears it in full each time: a 1,000 accrual spanning companies A and B
/// at 600 gross each is debited 600 + 600 = 1,200, Bonus Payable ends up with a 200 DEBIT balance, and
/// 200 of bonus expense is never recognised — invisibly, because <see cref="BonusAccrualPosition.Remaining"/>
/// clamps at zero with Math.Max.</para>
///
/// <para>The cursor closes that hole by metering what each caller has already taken IN THIS CALL, the
/// same way <see cref="BonusGlLedger.BuildPayrollClearingAsync"/> already meters its
/// <c>budget</c>. Nothing is written here — <see cref="Take"/> only reserves — so the caller stays free
/// to abandon the posting (a period-close throw, a failed guard) with no side effect.</para>
/// </summary>
public sealed class BonusAccrualCursor
{
    private readonly List<BonusAccrualPosition> _positions;
    private readonly Dictionary<(Guid, Guid?, string), decimal> _taken = new();

    public BonusAccrualCursor(IEnumerable<BonusAccrualPosition> positions)
        => _positions = positions.ToList();

    /// <summary>The snapshot this cursor meters, exactly as loaded from the ledger.</summary>
    public IReadOnlyList<BonusAccrualPosition> Positions => _positions;

    /// <summary>What is still clearable on <paramref name="position"/> after this call's reservations.</summary>
    public decimal RemainingOn(BonusAccrualPosition position)
        => Math.Max(0m, Math.Round(position.Remaining - _taken.GetValueOrDefault(position.Key), 2));

    /// <summary>How much of <paramref name="position"/> this call has reserved so far.</summary>
    public decimal TakenFrom(BonusAccrualPosition position) => _taken.GetValueOrDefault(position.Key);

    /// <summary>
    /// The accrual a posting in <paramref name="companyId"/> should clear: exact company match first,
    /// then the unattributed legacy accrual — <see cref="BonusGlLedger.PositionFor"/>'s precedence,
    /// but over what is left AFTER this call's own reservations.
    /// </summary>
    public BonusAccrualPosition? PositionFor(Guid batchId, Guid? companyId)
    {
        var forBatch = _positions.Where(p => p.BatchId == batchId && RemainingOn(p) > 0m).ToList();
        return forBatch.FirstOrDefault(p => p.CompanyId == companyId)
            ?? (companyId is null ? null : forBatch.FirstOrDefault(p => p.CompanyId is null));
    }

    /// <summary>
    /// Reserves up to <paramref name="wanted"/> against the accrual that a posting in
    /// <paramref name="companyId"/> should clear, and returns what was actually available.
    /// The returned amount is the ONLY amount the caller may debit to the payable.
    /// </summary>
    public (BonusAccrualPosition? Position, decimal Taken) Take(Guid batchId, Guid? companyId, decimal wanted)
    {
        if (wanted <= 0m) return (null, 0m);
        var position = PositionFor(batchId, companyId);
        if (position is null) return (null, 0m);
        var taken = Math.Min(Math.Round(wanted, 2), RemainingOn(position));
        if (taken <= 0m) return (position, 0m);
        _taken[position.Key] = _taken.GetValueOrDefault(position.Key) + taken;
        return (position, taken);
    }
}

/// <summary>
/// POD-B1b — the bonus payable sub-ledger. Answers the single question every bonus GL path needs:
/// "how much of this batch's accrual is still outstanding, and on which account was it credited?".
///
/// <para>Why this exists: before B1b a bonus paid through payroll was expensed TWICE — once by
/// <c>ApproveBatch</c> (DR 6100 / CR 2300, BonusesController.cs:512-522) and again by the payroll Lock
/// journal, whose earnings loop debits EARN:BONUS for the same money (PayrollController.cs:3042-3057) —
/// and the 2300 credit was never cleared, because <c>MarkBatchPaid</c> 409s once Process sets
/// <c>IsLockedByPayroll</c> (BonusesController.cs:557, PayrollController.cs:1139). The fix is that the
/// payroll run CLEARS the accrual instead of re-expensing it; this class computes exactly how much may
/// be cleared so the expense lands EXACTLY ONCE and 2300 provably returns to zero.</para>
///
/// <para>Every clearing amount is capped by <see cref="BonusAccrualPosition.Remaining"/>, which is what
/// makes the change safe with ZERO data migration:
/// <list type="bullet">
/// <item>a bonus injected straight into a run with no accrual at all (seeders, FinanceP1BonusGlTests)
///   clears nothing and is expensed exactly as it is today;</item>
/// <item>a pre-B1b accrual booked at NET clears NET, and only the never-accrued tax portion is
///   expensed by the run.</item>
/// </list></para>
/// </summary>
public static class BonusGlLedger
{
    public const string SourceModule = "Bonus";

    /// <summary>Machine-precise link from a payroll clearing line back to its bonus batch. Stored in
    /// <see cref="FinanceGlEntry.SourceEntityRef"/> (the batch NUMBER goes in the Description for humans)
    /// because the clearing line lives inside the payroll journal, whose SourceEntityId is the run.</summary>
    public static string BatchRef(Guid batchId) => batchId.ToString();

    /// <summary>
    /// POD-B1b-FIX (re-audit #5) — THE single derivation of a bonus's payroll EARNING component code.
    /// Both emission paths (the legacy inline block, PayrollController.cs:955, and the PayComponentEngine
    /// line list, :1008) call this, and so does the clearing plan, so the batch→component link used to
    /// route the un-accrued remainder can never drift from the code the earning row actually carries.
    /// </summary>
    public static string EarningComponentCode(string bonusTypeName)
        => $"BONUS_{(bonusTypeName ?? string.Empty).ToUpperInvariant().Replace(' ', '_')}";

    /// <summary>
    /// Outstanding accrual positions for the given batches. IgnoreQueryFilters is intentional: GL
    /// integrity is a SYSTEM read that must see every company's rows AND unattributed (CompanyId == null)
    /// legacy rows regardless of the caller's own company claims; the explicit WHERE re-applies exact
    /// tenant scope and never reads another tenant.
    /// </summary>
    public static async Task<List<BonusAccrualPosition>> LoadPositionsAsync(
        ZayraDbContext db, Guid tenantId, IReadOnlyCollection<Guid> batchIds, CancellationToken ct)
    {
        if (batchIds.Count == 0) return new List<BonusAccrualPosition>();
        var ids = batchIds.Distinct().ToList();

        // IgnoreQueryFilters is intentional: GL integrity is a SYSTEM read that must see every
        // company's rows AND unattributed legacy rows regardless of the caller's claims; the explicit
        // TenantId predicate re-applies exact tenant scope and never reads another tenant.
        // Accruals: the bonus module's own DR expense / CR payable journals that are still live.
        // Both the B1b tag and the pre-B1b "BonusApproval" tag count, so in-flight batches approved on
        // the old code are still recognised (and therefore cleared) by the new payroll path.
        var accrualRows = await db.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.SourceModule == SourceModule
                     && ids.Contains(x.SourceEntityId) && !x.IsReversed
                     && (x.EventType == GlEventTypes.BonusAccrual || x.EventType == GlEventTypes.BonusAccrualLegacy))
            .Select(x => new { x.SourceEntityId, x.CompanyId, x.CreditAccount, x.Currency, x.Amount })
            .ToListAsync(ct);
        var accruals = accrualRows.Where(r => !string.IsNullOrEmpty(r.CreditAccount)).ToList();
        if (accruals.Count == 0) return new List<BonusAccrualPosition>();

        // IgnoreQueryFilters is intentional: same system-read rationale as the accrual load above;
        // the TenantId + batch-id predicate keeps this tenant-contained.
        // Clearings booked by the bonus module itself: manual payment + cancellation contra.
        var moduleRows = await db.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.SourceModule == SourceModule
                     && ids.Contains(x.SourceEntityId) && !x.IsReversed
                     && (x.EventType == GlEventTypes.BonusPayment || x.EventType == GlEventTypes.BonusAccrualReversal))
            .Select(x => new { x.SourceEntityId, x.CompanyId, x.DebitAccount, x.Amount })
            .ToListAsync(ct);

        // Clearings booked INSIDE a payroll Lock journal (SourceModule="Payroll", SourceEntityId=runId).
        // A run void contras these and flags the originals IsReversed=true (PayrollVoidService.cs:83),
        // so a voided run automatically re-opens the payable here.
        // IgnoreQueryFilters is intentional: same system-read rationale; TenantId predicate applies.
        var refs = ids.Select(BatchRef).ToList();
        var payrollRows = await db.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EventType == GlEventTypes.BonusPayrollClearing
                     && refs.Contains(x.SourceEntityRef) && !x.IsReversed)
            .Select(x => new { x.SourceEntityRef, x.CompanyId, x.DebitAccount, x.Amount })
            .ToListAsync(ct);

        // The set of positions that actually exist, so a clearing can be matched to the accrual it
        // retires rather than to a key nothing was ever accrued under.
        var accrualKeys = accruals
            .Select(a => (a.SourceEntityId, a.CompanyId, a.CreditAccount))
            .ToHashSet();

        var cleared = new Dictionary<(Guid, Guid?, string), decimal>();
        void AddCleared(Guid batchId, Guid? companyId, string account, decimal amount)
        {
            if (string.IsNullOrEmpty(account) || amount <= 0m) return;
            var key = (batchId, companyId, account);
            // Mirror PositionFor's precedence: exact company first, then the unattributed legacy accrual.
            // A pre-B1b accrual carries no CompanyId, but the payment/clearing that retires it is stamped
            // with one (MarkBatchPaid stamps the bonus's company; the payroll journal stamps the run's).
            // Without this fallback that clearing would land on a key with no position behind it, the
            // legacy accrual would read as permanently outstanding, and it could be cleared a SECOND time.
            if (companyId is not null && !accrualKeys.Contains(key)
                && accrualKeys.Contains((batchId, (Guid?)null, account)))
                key = (batchId, null, account);
            cleared[key] = cleared.GetValueOrDefault(key) + amount;
        }
        foreach (var r in moduleRows) AddCleared(r.SourceEntityId, r.CompanyId, r.DebitAccount, r.Amount);
        foreach (var r in payrollRows)
            if (Guid.TryParse(r.SourceEntityRef, out var bid)) AddCleared(bid, r.CompanyId, r.DebitAccount, r.Amount);

        return accruals
            .GroupBy(a => (a.SourceEntityId, a.CompanyId, a.CreditAccount))
            .Select(g => new BonusAccrualPosition(
                g.Key.SourceEntityId, g.Key.CompanyId, g.Key.CreditAccount,
                g.First().Currency,
                g.Sum(x => x.Amount),
                cleared.GetValueOrDefault((g.Key.SourceEntityId, g.Key.CompanyId, g.Key.CreditAccount))))
            .OrderBy(p => p.BatchId)
            .ToList();
    }

    /// <summary>
    /// Picks the accrual position a posting in <paramref name="companyId"/> should clear: the exact
    /// company match first, then an unattributed (CompanyId == null) legacy accrual. Without the fallback,
    /// a batch approved BEFORE this pod (no company stamp) would never be cleared and would be expensed
    /// twice for the whole in-flight window.
    ///
    /// <para>POD-B1b-FIX (P0-1) — READ-ONLY probe. A caller that clears more than one company against the
    /// same snapshot MUST go through <see cref="BonusAccrualCursor"/> instead: this overload has no memory
    /// of what the caller already took, so consecutive calls hand the SAME unattributed legacy accrual to
    /// every company and over-clear it.</para>
    /// </summary>
    public static BonusAccrualPosition? PositionFor(
        IEnumerable<BonusAccrualPosition> positions, Guid batchId, Guid? companyId)
        => new BonusAccrualCursor(positions).PositionFor(batchId, companyId);

    /// <summary>
    /// Builds the per-batch clearing plan for a payroll run's Lock journal.
    ///
    /// <para><paramref name="bonusEarningTotal"/> is the run's ACTUAL Σ of bonus earning lines, and it is
    /// spent as a hard budget: Σ(clearing) + remainder == bonusEarningTotal by construction, so the Lock
    /// journal stays balanced no matter how the operational data and the ledger disagree (e.g. a run
    /// re-Processed after its bonuses were consumed has zero bonus earnings left, and therefore clears
    /// nothing). The caller expenses whatever budget is left over to EARN:BONUS.</para>
    /// </summary>
    public static async Task<IReadOnlyList<BonusAccrualClearing>> BuildPayrollClearingAsync(
        ZayraDbContext db, Guid tenantId, Guid runId, Guid? runCompanyId,
        decimal bonusEarningTotal, CancellationToken ct)
    {
        if (bonusEarningTotal <= 0m) return Array.Empty<BonusAccrualClearing>();

        // POD-B1b — resolve the run's EFFECTIVE legal entity before matching accruals.
        // A legacy/seeded run can carry NO CompanyId at all: Process explicitly accepts one via
        // legacySingleCompanyScope (PayrollController.cs:557-560) and both seeders create runs that way
        // (Infrastructure/Seed/AuthSeeder.cs:849, Infrastructure/Seed/DemoDataSeeder.cs:783, in a system
        // context that ZayraDbContext.cs:379 deliberately exempts from company stamping) — while the
        // bonus batch it pays IS company-stamped, because ApproveBatch groups by EmployeeBonus.CompanyId.
        // Matching null→null only would find no accrual, re-expense the bonus and orphan the payable:
        // precisely the double-count this pod exists to kill. Resolve it exactly the way Process does —
        // the tenant's single active company. With 2+ active companies Process refuses a null-company run
        // outright (422 company_not_resolved), so this stays null and nothing clears, unchanged.
        // IgnoreQueryFilters is intentional: same system-read rationale as the loads below; the TenantId
        // predicate re-applies exact tenant scope and never reads another tenant.
        var effectiveCompanyId = runCompanyId;
        if (effectiveCompanyId is null)
        {
            var activeCompanyIds = await db.Companies.IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.TenantId == tenantId && c.IsActive && !c.IsDeleted)
                .Select(c => c.Id)
                .Take(2)
                .ToListAsync(ct);
            if (activeCompanyIds.Count == 1) effectiveCompanyId = activeCompanyIds[0];
        }

        // IgnoreQueryFilters: system read for GL integrity — must see every bonus this run consumed
        // regardless of the locking user's company scope; the WHERE keeps it tenant- and run-contained.
        var consumed = await db.EmployeeBonuses.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.TenantId == tenantId && b.PayrollRunId == runId && !b.IsDeleted)
            .Select(b => new { b.BonusBatchId, b.GrossBonusAmount, b.BonusTypeName })
            .ToListAsync(ct);
        if (consumed.Count == 0) return Array.Empty<BonusAccrualClearing>();

        var grossByBatch = consumed
            .GroupBy(b => b.BonusBatchId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.GrossBonusAmount));

        // POD-B1b-FIX (re-audit #5) — the batch→EARNING-COMPONENT link, carried out of the same consumed
        // rows. The remainder router needs to know WHICH components a clearing covered, not just how much.
        var grossByBatchComponent = consumed
            .GroupBy(b => b.BonusBatchId)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(x => EarningComponentCode(x.BonusTypeName), StringComparer.Ordinal)
                      .ToDictionary(k => k.Key, k => k.Sum(x => x.GrossBonusAmount), StringComparer.Ordinal));

        var positions = await LoadPositionsAsync(db, tenantId, grossByBatch.Keys.ToList(), ct);
        if (positions.Count == 0) return Array.Empty<BonusAccrualClearing>();

        // IgnoreQueryFilters is intentional: batch NUMBERS are ledger descriptions for a run this
        // caller is already locking; the TenantId predicate keeps the read tenant-contained.
        var batchNumbers = await db.BonusBatches.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.TenantId == tenantId && grossByBatch.Keys.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, b => b.BatchNumber, ct);

        var plan = new List<BonusAccrualClearing>();
        var budget = bonusEarningTotal;
        // POD-B1b-FIX (P0-1) — meter the snapshot through the cursor instead of re-reading it. This loop
        // visits each batch once so it could not over-clear today, but routing BOTH clearing paths through
        // one metered abstraction is what stops the defect coming back the moment a second dimension
        // (per-company splits inside a run) is added here.
        var cursor = new BonusAccrualCursor(positions);
        foreach (var batchId in grossByBatch.Keys.OrderBy(k => k))
        {
            if (budget <= 0m) break;
            var (position, clearable) = cursor.Take(
                batchId, effectiveCompanyId, Math.Min(grossByBatch[batchId], budget));
            if (position is null || clearable <= 0m) continue;
            budget -= clearable;
            plan.Add(new BonusAccrualClearing(
                batchId,
                batchNumbers.GetValueOrDefault(batchId, batchId.ToString()),
                position.AccrualAccount,
                clearable,
                // Carry the ACCRUAL's legal entity, not the run's: the clearing retires a payable that is
                // sitting in that entity's books, and LoadPositionsAsync matches clearings to positions by
                // (batch, company, account). Stamping a null-company run's clearing with null would leave
                // the company-stamped accrual reading as still-outstanding — and therefore clearable a
                // second time — even though the money has been paid.
                position.CompanyId,
                // POD-B1b-FIX (re-audit #5) — apportion what this batch actually cleared across its OWN
                // components, pro rata of their consumed gross. Only needed because `clearable` can be
                // less than the batch's gross (capped by the outstanding accrual and by the run's budget);
                // when it equals the gross this is just the per-component gross.
                ApportionToComponents(grossByBatchComponent.GetValueOrDefault(batchId), clearable)));
        }
        return plan;
    }

    /// <summary>
    /// Splits <paramref name="total"/> across <paramref name="grossByCode"/> in proportion to each code's
    /// gross. Deterministic (ordinal code order) and exact: the last code absorbs the rounding residue, so
    /// Σ(result) == <paramref name="total"/> to the cent. Returns null when there is nothing to apportion,
    /// which tells the caller to fall back to its pro-rata path.
    /// </summary>
    internal static IReadOnlyDictionary<string, decimal>? ApportionToComponents(
        IReadOnlyDictionary<string, decimal>? grossByCode, decimal total)
    {
        if (grossByCode is null || grossByCode.Count == 0 || total <= 0m) return null;
        var codes = grossByCode.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        var grossTotal = grossByCode.Values.Sum();
        if (grossTotal <= 0m) return null;

        var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var left = total;
        for (var i = 0; i < codes.Count; i++)
        {
            var share = i == codes.Count - 1
                ? left
                : Math.Min(left, Math.Round(total * (grossByCode[codes[i]] / grossTotal), 2));
            left = Math.Round(left - share, 2);
            if (share > 0m) result[codes[i]] = share;
        }
        return result;
    }
}
