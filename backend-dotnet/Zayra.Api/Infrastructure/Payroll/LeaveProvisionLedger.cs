using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// How much annual-leave PROVISION is still carried for ONE employee, and on which account.
/// Per employee for the same reason the EOSB provision is: a pooled balance would let one leaver's settlement relieve the
/// provision built for someone still employed.
/// </summary>
/// <param name="ProvisionAccount">The accrual line's STORED CreditAccount — what a consumption must DEBIT, so a
/// chart-of-accounts remap between the monthly accrual and the settlement can never leave 2330 off zero.</param>
public sealed record LeaveProvisionPosition(
    int EmployeeId, Guid? CompanyId, string ProvisionAccount, string Currency, decimal Accrued, decimal Consumed)
{
    public decimal Remaining => Math.Max(0m, Math.Round(Accrued - Consumed, 2));

    public (int EmployeeId, Guid? CompanyId, string ProvisionAccount) Key => (EmployeeId, CompanyId, ProvisionAccount);
}

/// <summary>The provision one employee still carries on ONE stored account (credit positive). See <see cref="LeaveProvisionLedger.HoldingsFor"/>.</summary>
public sealed record LeaveProvisionHolding(string Account, decimal Balance);

/// <summary>
/// A MUTABLE, single-call view over the immutable snapshot returned by <see cref="LeaveProvisionLedger.LoadPositionsAsync"/>,
/// so a caller settling several employees against one snapshot can never hand the same position to two of them. Nothing is
/// written here — <see cref="Take"/> only RESERVES — so a caller stays free to abandon the posting.
/// </summary>
public sealed class LeaveProvisionCursor
{
    private readonly List<LeaveProvisionPosition> _positions;
    private readonly Dictionary<(int, Guid?, string), decimal> _taken = new();

    public LeaveProvisionCursor(IEnumerable<LeaveProvisionPosition> positions) => _positions = positions.ToList();

    public IReadOnlyList<LeaveProvisionPosition> Positions => _positions;

    public decimal RemainingOn(LeaveProvisionPosition position)
        => Math.Max(0m, Math.Round(position.Remaining - _taken.GetValueOrDefault(position.Key), 2));

    /// <summary>The provision a settlement in <paramref name="companyId"/> may relieve: the exact company first, then an
    /// unattributed (CompanyId == null) accrual, evaluated over what is left AFTER this call's own reservations.</summary>
    public LeaveProvisionPosition? PositionFor(int employeeId, Guid? companyId)
    {
        var forEmployee = _positions.Where(p => p.EmployeeId == employeeId && RemainingOn(p) > 0m).ToList();
        return forEmployee.FirstOrDefault(p => p.CompanyId == companyId)
            ?? (companyId is null ? null : forEmployee.FirstOrDefault(p => p.CompanyId is null));
    }

    /// <summary>Reserves up to <paramref name="wanted"/> against this employee's provision and returns what was actually
    /// available. The returned amount is the ONLY amount the caller may debit to 2330.</summary>
    public (LeaveProvisionPosition? Position, decimal Taken) Take(int employeeId, Guid? companyId, decimal wanted)
    {
        if (wanted <= 0m) return (null, 0m);
        var position = PositionFor(employeeId, companyId);
        if (position is null) return (null, 0m);
        var taken = Math.Min(Math.Round(wanted, 2), RemainingOn(position));
        if (taken <= 0m) return (position, 0m);
        _taken[position.Key] = _taken.GetValueOrDefault(position.Key) + taken;
        return (position, taken);
    }

    /// <summary>
    /// Reserves up to <paramref name="wanted"/> across EVERY account that holds this employee's provision, with the same rule a monthly
    /// fall uses (<see cref="LeaveProvisionPlanner"/>): accounts from <see cref="LeaveProvisionLedger.HoldingsFor"/> net of this call's
    /// reservations, positive balances only, largest first (ties by account label, ordinal), each up to its own balance, so no account
    /// is driven below zero. Within one account the exact company's position is taken before an unattributed one. Returns what was
    /// taken per account; the sum is the ONLY amount the caller may debit to the provision.
    /// </summary>
    public List<(string Account, decimal Taken)> TakeAcrossAccounts(int employeeId, Guid? companyId, decimal wanted)
    {
        var result = new List<(string Account, decimal Taken)>();
        var left = Math.Round(wanted, 2);
        if (left <= 0m) return result;
        var holdings = LeaveProvisionLedger.HoldingsFor(_positions, employeeId, companyId)
            .Select(h => (h.Account, Balance: Math.Round(h.Balance - _positions
                .Where(p => p.EmployeeId == employeeId && p.ProvisionAccount == h.Account && (p.CompanyId == companyId || p.CompanyId is null))
                .Sum(p => _taken.GetValueOrDefault(p.Key)), 2)))
            .Where(h => h.Balance > 0m)
            .OrderByDescending(h => h.Balance).ThenBy(h => h.Account, StringComparer.Ordinal)
            .ToList();
        foreach (var (account, balance) in holdings)
        {
            if (left <= 0m) break;
            var forAccount = Math.Min(left, balance);
            var takenHere = 0m;
            foreach (var position in _positions
                         .Where(p => p.EmployeeId == employeeId && p.ProvisionAccount == account
                                  && (p.CompanyId == companyId || (companyId is not null && p.CompanyId is null)))
                         .OrderBy(p => p.CompanyId is null ? 1 : 0))
            {
                var need = Math.Round(forAccount - takenHere, 2);
                if (need <= 0m) break;
                var take = Math.Min(need, RemainingOn(position));
                if (take <= 0m) continue;
                _taken[position.Key] = _taken.GetValueOrDefault(position.Key) + take;
                takenHere = Math.Round(takenHere + take, 2);
            }
            if (takenHere <= 0m) continue;
            result.Add((account, takenHere));
            left = Math.Round(left - takenHere, 2);
        }
        return result;
    }

    /// <summary>
    /// Reserves what the employee still carries after this call's own reservations — <see cref="LeaveProvisionLedger.CarriedFor"/>
    /// (netted over every account, clamped once) less what was taken — spread across accounts by the <see cref="TakeAcrossAccounts"/>
    /// rule. This is the provision a leaver's settlement releases. Releasing what each POSITION still showed overstated it whenever
    /// one account was below zero: A +100 and B -30 carry 70, so after an encashment of 50 only 20 is left, not 50.
    /// </summary>
    public List<(string Account, decimal Taken)> TakeLeftover(int employeeId, Guid? companyId)
    {
        var carried = LeaveProvisionLedger.CarriedFor(_positions, employeeId, companyId);
        var reserved = _positions
            .Where(p => p.EmployeeId == employeeId && (p.CompanyId == companyId || p.CompanyId is null))
            .Sum(p => _taken.GetValueOrDefault(p.Key));
        var left = Math.Max(0m, Math.Round(carried - reserved, 2));
        return TakeAcrossAccounts(employeeId, companyId, left);
    }
}

/// <summary>
/// The ANNUAL-LEAVE PROVISION SUB-LEDGER: the live monthly accruals for an employee, less what a final settlement has already
/// consumed. The employee link rides <c>SourceEntityRef = employeeId</c> on the provision-side lines (the expense side carries
/// the period and is never an employee id), exactly as <see cref="EosbProvisionLedger"/> does.
/// </summary>
public static class LeaveProvisionLedger
{
    /// <summary>Employee-id link carried in <c>FinanceGlEntry.SourceEntityRef</c>.</summary>
    public static string EmployeeRef(int employeeId) => employeeId.ToString();

    /// <summary>
    /// Outstanding provision positions for the given employees, for ONE tenant. A void flags a run's accrual lines reversed, and
    /// a cancelled settlement flags its consumption lines reversed, so neither is counted. The tenant predicate is applied by
    /// <see cref="ScopedBypass.TenantWide"/>: the sub-ledger is a system integrity read over every company, never another tenant.
    /// </summary>
    public static async Task<List<LeaveProvisionPosition>> LoadPositionsAsync(
        ZayraDbContext db, Guid tenantId, IReadOnlyCollection<int> employeeIds, CancellationToken ct)
    {
        if (employeeIds.Count == 0) return new List<LeaveProvisionPosition>();
        var refs = employeeIds.Distinct().Select(EmployeeRef).ToList();

        var accrualRows = await ScopedBypass.TenantWide(db.FinanceGlEntries, tenantId,
                "Leave provision integrity: the balance must be read across every legal entity and for unattributed rows, " +
                "or a settlement could relieve less than was provided.")
            .AsNoTracking()
            .Where(x => x.SourceModule == FinalSettlementGlDescriptions.SourceModule
                     && x.EventType == GlEventTypes.LeaveProvisionAccrual
                     && refs.Contains(x.SourceEntityRef) && !x.IsReversed)
            .Select(x => new { x.SourceEntityRef, x.CompanyId, x.DebitAccount, x.CreditAccount, x.Currency, x.Amount })
            .ToListAsync(ct);
        // A RISE in the unused-leave value credits the provision; a FALL (leave taken, a pay cut) debits it. Both are per-employee
        // lines and net against the same (employee, company, account) position.
        var accruals = accrualRows
            .Where(r => !string.IsNullOrEmpty(r.CreditAccount))
            .Select(r => (r.SourceEntityRef, r.CompanyId, r.CreditAccount, r.Currency, r.Amount))
            .Concat(accrualRows
                .Where(r => string.IsNullOrEmpty(r.CreditAccount) && !string.IsNullOrEmpty(r.DebitAccount))
                .Select(r => (r.SourceEntityRef, r.CompanyId, CreditAccount: r.DebitAccount, r.Currency, Amount: -r.Amount)))
            .ToList();
        if (accruals.Count == 0) return new List<LeaveProvisionPosition>();

        var consumptionRows = await ScopedBypass.TenantWide(db.FinanceGlEntries, tenantId,
                "Leave provision consumption must net against accruals booked in any entity.")
            .AsNoTracking()
            .Where(x => x.SourceModule == FinalSettlementGlDescriptions.SourceModule
                     && x.EventType == GlEventTypes.LeaveProvisionConsumption
                     && refs.Contains(x.SourceEntityRef) && !x.IsReversed)
            .Select(x => new { x.SourceEntityRef, x.CompanyId, x.DebitAccount, x.Amount })
            .ToListAsync(ct);

        var accrualKeys = accruals.Select(a => (a.SourceEntityRef, a.CompanyId, a.CreditAccount)).ToHashSet();
        var consumed = new Dictionary<(string, Guid?, string), decimal>();
        foreach (var r in consumptionRows)
        {
            if (string.IsNullOrEmpty(r.DebitAccount) || r.Amount <= 0m) continue;
            var key = (r.SourceEntityRef, r.CompanyId, r.DebitAccount);
            // Mirror PositionFor's precedence: exact company first, then an unattributed accrual (see EosbProvisionLedger).
            if (r.CompanyId is not null && !accrualKeys.Contains(key)
                && accrualKeys.Contains((r.SourceEntityRef, (Guid?)null, r.DebitAccount)))
                key = (r.SourceEntityRef, null, r.DebitAccount);
            consumed[key] = consumed.GetValueOrDefault(key) + r.Amount;
        }

        return accruals
            .GroupBy(a => (a.SourceEntityRef, a.CompanyId, a.CreditAccount))
            .Select(g => new LeaveProvisionPosition(
                int.TryParse(g.Key.SourceEntityRef, out var eid) ? eid : 0,
                g.Key.CompanyId, g.Key.CreditAccount, g.First().Currency, g.Sum(x => x.Amount),
                consumed.GetValueOrDefault((g.Key.SourceEntityRef, g.Key.CompanyId, g.Key.CreditAccount))))
            .Where(p => p.EmployeeId > 0)
            .OrderBy(p => p.EmployeeId)
            .ToList();
    }

    /// <summary>
    /// What an employee still carries: Σ (accrued − consumed) over EVERY position of theirs (company or unattributed), clamped ONCE. Clamping each
    /// position first lets a chart remap between months (a fall booked against the new account, a rise against the old) read as more than the
    /// ledger holds, and the monthly release then repeats.
    /// </summary>
    public static decimal CarriedFor(IEnumerable<LeaveProvisionPosition> positions, int employeeId, Guid? companyId)
        => Math.Max(0m, Math.Round(positions
            .Where(p => p.EmployeeId == employeeId && (p.CompanyId == companyId || p.CompanyId is null))
            .Sum(p => p.Accrued - p.Consumed), 2));

    /// <summary>
    /// What an employee's provision is carried ON: per stored account, Σ (accrued − consumed) over the same positions
    /// <see cref="CarriedFor"/> reads (the company's and unattributed ones), unclamped and ordered by account label. A monthly fall
    /// debits these accounts, so a remap of the provision account never relieves an account that was never credited. The sum of
    /// the balances equals <see cref="CarriedFor"/> whenever that is positive.
    /// </summary>
    public static List<LeaveProvisionHolding> HoldingsFor(IEnumerable<LeaveProvisionPosition> positions, int employeeId, Guid? companyId)
        => positions
            .Where(p => p.EmployeeId == employeeId && (p.CompanyId == companyId || p.CompanyId is null))
            .GroupBy(p => p.ProvisionAccount, StringComparer.Ordinal)
            .Select(g => new LeaveProvisionHolding(g.Key, Math.Round(g.Sum(p => p.Accrued - p.Consumed), 2)))
            .OrderBy(h => h.Account, StringComparer.Ordinal)
            .ToList();

    /// <summary>Convenience single-employee load, returning a cursor ready to <c>Take</c> from.</summary>
    public static async Task<LeaveProvisionCursor> LoadCursorAsync(
        ZayraDbContext db, Guid tenantId, int employeeId, CancellationToken ct)
        => new(await LoadPositionsAsync(db, tenantId, new[] { employeeId }, ct));
}
