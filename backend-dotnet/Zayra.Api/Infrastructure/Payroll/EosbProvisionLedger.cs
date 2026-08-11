using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// POD-C1 — how much end-of-service PROVISION is still carried for ONE employee, and on which account.
/// </summary>
/// <param name="EmployeeId">The employee the provision was accrued FOR. Per-employee is not a nicety: a
/// tenant-wide pool would let employee A's settlement relieve the provision built for employee B,
/// understating expense in the settlement period and understating the liability at every balance-sheet
/// date.</param>
/// <param name="CompanyId">Legal entity the provision sits in; null for unattributed/legacy rows.</param>
/// <param name="ProvisionAccount">The accrual line's STORED CreditAccount — what a consumption must DEBIT,
/// so a chart-of-accounts remap between the monthly accrual and the settlement can never leave 2310 off
/// zero (the same doctrine as <see cref="BonusAccrualClearing.AccrualAccount"/>).</param>
public sealed record EosbProvisionPosition(
    int EmployeeId, Guid? CompanyId, string ProvisionAccount, string Currency,
    decimal Accrued, decimal Consumed)
{
    public decimal Remaining => Math.Max(0m, Math.Round(Accrued - Consumed, 2));

    /// <summary>Identity of this position in the provision sub-ledger — the exact tuple
    /// <see cref="EosbProvisionLedger.LoadPositionsAsync"/> groups by.</summary>
    public (int EmployeeId, Guid? CompanyId, string ProvisionAccount) Key
        => (EmployeeId, CompanyId, ProvisionAccount);
}

/// <summary>
/// POD-C1 — a MUTABLE, single-call view over the immutable snapshot returned by
/// <see cref="EosbProvisionLedger.LoadPositionsAsync"/>, so a caller settling several employees against
/// one snapshot can never hand the same position to two of them. Byte-for-byte the discipline
/// <see cref="BonusAccrualCursor"/> established for the bonus payable (POD-B1b-FIX P0-1); nothing is
/// written here — <see cref="Take"/> only RESERVES — so a caller stays free to abandon the posting (a
/// failed guard, a closed period) with no side effect.
/// </summary>
public sealed class EosbProvisionCursor
{
    private readonly List<EosbProvisionPosition> _positions;
    private readonly Dictionary<(int, Guid?, string), decimal> _taken = new();

    public EosbProvisionCursor(IEnumerable<EosbProvisionPosition> positions)
        => _positions = positions.ToList();

    public IReadOnlyList<EosbProvisionPosition> Positions => _positions;

    public decimal RemainingOn(EosbProvisionPosition position)
        => Math.Max(0m, Math.Round(position.Remaining - _taken.GetValueOrDefault(position.Key), 2));

    /// <summary>
    /// The provision a settlement in <paramref name="companyId"/> may relieve: exact company match first,
    /// then an unattributed (CompanyId == null) legacy accrual — <see cref="BonusAccrualCursor.PositionFor"/>'s
    /// precedence, evaluated over what is left AFTER this call's own reservations.
    /// </summary>
    public EosbProvisionPosition? PositionFor(int employeeId, Guid? companyId)
    {
        var forEmployee = _positions.Where(p => p.EmployeeId == employeeId && RemainingOn(p) > 0m).ToList();
        return forEmployee.FirstOrDefault(p => p.CompanyId == companyId)
            ?? (companyId is null ? null : forEmployee.FirstOrDefault(p => p.CompanyId is null));
    }

    /// <summary>Reserves up to <paramref name="wanted"/> against this employee's provision and returns what
    /// was actually available. The returned amount is the ONLY amount the caller may debit to 2310.</summary>
    public (EosbProvisionPosition? Position, decimal Taken) Take(int employeeId, Guid? companyId, decimal wanted)
    {
        if (wanted <= 0m) return (null, 0m);
        var position = PositionFor(employeeId, companyId);
        if (position is null) return (null, 0m);
        var taken = Math.Min(Math.Round(wanted, 2), RemainingOn(position));
        if (taken <= 0m) return (position, 0m);
        _taken[position.Key] = _taken.GetValueOrDefault(position.Key) + taken;
        return (position, taken);
    }
}

/// <summary>
/// POD-C1 — the END-OF-SERVICE PROVISION SUB-LEDGER, and the explicit, correct seam POD-C2 plugs into.
///
/// <para><b>WHAT C1 DOES AND DOES NOT DO.</b> C1 never ACCRUES a provision — the monthly gratuity
/// liability accrual for all active employees is POD-C2 and is deliberately out of scope. C1 only asks
/// one question at approval time: <i>has anything already been provided for THIS employee, and on which
/// account?</i> With no C2 the answer is "nothing", so the whole gratuity is expensed to 5110 and the
/// journal is exactly what a no-provision tenant expects. When C2 starts posting
/// <c>DR 5110 / CR 2310</c> monthly per employee, this same code consumes the provision FIRST and
/// expenses only the shortfall — no rework, no re-audit.</para>
///
/// <para><b>WHY IT IS NOT <c>ControlAccountBalance.AvailableForRelief</c>.</b> That clamp was designed
/// for RECEIVABLES, which carry DEBIT balances. A gratuity provision is a LIABILITY and carries a CREDIT
/// balance, so <c>Net = Σ(DR) − Σ(CR)</c> is NEGATIVE and
/// <c>AvailableForRelief = Max(0, Min(Scoped, TenantWide))</c> is <b>always 0 — forever, including after
/// C2 ships</b>. A seam built on it would be inert by construction: C2 would expense the gratuity monthly
/// AND C1 would expense it again in full at settlement, while 2310 grew without bound. That is exactly
/// the POD-B1b bonus double-count, rebuilt one pod later — which is why B1b itself used a positional
/// sub-ledger (<see cref="BonusAccrualPosition"/>) for the liability rather than the receivable clamp.
/// This class is that sub-ledger for gratuity.</para>
///
/// <para><b>THE EMPLOYEE DIMENSION.</b> <c>FinanceGlEntry</c> has no employee column (POD-C3 documented
/// this when it had to build the 1420 sub-ledger). The provision therefore rides
/// <c>SourceEntityRef = employeeId</c> with <c>SourceModule = "Payroll"</c>, which is the same mechanism
/// <c>BonusGlLedger</c> uses to attribute a clearing line inside a payroll journal to its batch.</para>
/// </summary>
public static class EosbProvisionLedger
{
    /// <summary>Employee-id link carried in <c>FinanceGlEntry.SourceEntityRef</c>.</summary>
    public static string EmployeeRef(int employeeId) => employeeId.ToString();

    /// <summary>
    /// Outstanding provision positions for the given employees.
    ///
    /// <para>IgnoreQueryFilters is intentional: GL integrity is a SYSTEM read that must see every
    /// company's rows AND unattributed (CompanyId == null) legacy rows regardless of the caller's own
    /// company claims; the explicit TenantId predicate re-applies exact tenant scope and never reads
    /// another tenant.</para>
    /// </summary>
    public static async Task<List<EosbProvisionPosition>> LoadPositionsAsync(
        ZayraDbContext db, Guid tenantId, IReadOnlyCollection<int> employeeIds, CancellationToken ct)
    {
        if (employeeIds.Count == 0) return new List<EosbProvisionPosition>();
        var refs = employeeIds.Distinct().Select(EmployeeRef).ToList();

        // Accruals: POD-C2's monthly DR expense / CR provision journals that are still live.
        // IgnoreQueryFilters is intentional: company filter only — the EOSB provision balance is a system
        // integrity read over the whole ledger. Tenant re-applied in the WHERE.
        var accrualRows = await db.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenantId
                     && x.SourceModule == FinalSettlementGlDescriptions.SourceModule
                     && x.EventType == GlEventTypes.EosbProvisionAccrual
                     && refs.Contains(x.SourceEntityRef) && !x.IsReversed)
            .Select(x => new { x.SourceEntityRef, x.CompanyId, x.CreditAccount, x.Currency, x.Amount })
            .ToListAsync(ct);
        var accruals = accrualRows.Where(r => !string.IsNullOrEmpty(r.CreditAccount)).ToList();
        if (accruals.Count == 0) return new List<EosbProvisionPosition>();

        // Consumptions: what C1's own settlements have already taken out of the provision.
        // IgnoreQueryFilters is intentional: company filter only — provision consumption must net against
        // accruals booked in any entity. Tenant re-applied.
        var consumptionRows = await db.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenantId
                     && x.SourceModule == FinalSettlementGlDescriptions.SourceModule
                     && x.EventType == GlEventTypes.EosbProvisionConsumption
                     && refs.Contains(x.SourceEntityRef) && !x.IsReversed)
            .Select(x => new { x.SourceEntityRef, x.CompanyId, x.DebitAccount, x.Amount })
            .ToListAsync(ct);

        var accrualKeys = accruals
            .Select(a => (a.SourceEntityRef, a.CompanyId, a.CreditAccount))
            .ToHashSet();

        var consumed = new Dictionary<(string, Guid?, string), decimal>();
        foreach (var r in consumptionRows)
        {
            if (string.IsNullOrEmpty(r.DebitAccount) || r.Amount <= 0m) continue;
            var key = (r.SourceEntityRef, r.CompanyId, r.DebitAccount);
            // Mirror PositionFor's precedence: exact company first, then the unattributed accrual. A
            // provision accrued before company attribution existed carries CompanyId = NULL while the
            // settlement that consumes it is company-stamped; without this fallback the consumption would
            // land on a key with no position behind it and the provision would read as permanently
            // outstanding — i.e. consumable a SECOND time.
            if (r.CompanyId is not null && !accrualKeys.Contains(key)
                && accrualKeys.Contains((r.SourceEntityRef, (Guid?)null, r.DebitAccount)))
                key = (r.SourceEntityRef, null, r.DebitAccount);
            consumed[key] = consumed.GetValueOrDefault(key) + r.Amount;
        }

        return accruals
            .GroupBy(a => (a.SourceEntityRef, a.CompanyId, a.CreditAccount))
            .Select(g => new EosbProvisionPosition(
                int.TryParse(g.Key.SourceEntityRef, out var eid) ? eid : 0,
                g.Key.CompanyId,
                g.Key.CreditAccount,
                g.First().Currency,
                g.Sum(x => x.Amount),
                consumed.GetValueOrDefault((g.Key.SourceEntityRef, g.Key.CompanyId, g.Key.CreditAccount))))
            .Where(p => p.EmployeeId > 0)
            .OrderBy(p => p.EmployeeId)
            .ToList();
    }

    /// <summary>Convenience single-employee load, returning a cursor ready to <c>Take</c> from.</summary>
    public static async Task<EosbProvisionCursor> LoadCursorAsync(
        ZayraDbContext db, Guid tenantId, int employeeId, CancellationToken ct)
        => new(await LoadPositionsAsync(db, tenantId, new[] { employeeId }, ct));
}
