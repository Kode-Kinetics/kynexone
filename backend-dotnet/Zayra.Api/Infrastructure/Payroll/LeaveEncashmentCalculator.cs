using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>One encashable leave balance: the row, the days actually payable, and why.</summary>
/// <param name="BalanceId">The <see cref="EmployeeLeaveBalance"/> the days come from. Carried onto the
/// settlement line so the disbursing run decrements the EXACT row that was planned and a void restores
/// that same row — never a re-derived one.</param>
public sealed record EncashableLeave(
    Guid BalanceId, Guid LeaveTypeId, string LeaveTypeName, int Year,
    decimal AvailableDays, decimal EncashableDays, decimal Amount, string Basis);

/// <summary>The whole encashment answer for one employee, including what was REFUSED and why.</summary>
public sealed record LeaveEncashmentResult(
    IReadOnlyList<EncashableLeave> Lines,
    decimal TotalDays,
    decimal TotalAmount,
    IReadOnlyList<string> Warnings);

/// <summary>
/// POD-C1 — THE ONE authoritative "how many leave days does this leaver get paid for?" function.
///
/// <para><b>THE THREE DEFECTS IT CLOSES.</b> The pre-C1 <c>/final-settlement</c> encashment arithmetic
/// (PayrollController's inline block) was a display-only number; C1 turns it into CASH, so each of these
/// becomes a real over- or under-payment:</para>
/// <list type="number">
/// <item><b>It summed EVERY leave type.</b> <c>LeavePolicy.EncashmentAllowed</c> and
///   <c>LeavePolicy.EncashmentMaxDays</c> exist and were simply ignored, so sick-leave balances were
///   encashed and per-type caps were never applied. Over-payment.</item>
/// <item><b>It used a formula that DIVERGED from <c>EmployeeLeaveBalance.Available</c></b> — the
///   definition ESS shows the employee. Its own expression omitted <c>Entitled</c>, so a tenant that
///   populates <c>Entitled</c> rather than <c>Accrued</c> got ZERO encashment from the settlement while
///   the employee's self-service screen showed a positive balance. Under-payment, and it is POD-A2's
///   two-formulas lesson repeating one module across.</item>
/// <item><b>It filtered <c>Year == lastWorkingDay.Year</c></b>, silently dropping any balance row not
///   stamped with the leaving year (carry-forward rows are routinely stamped with the year they were
///   earned).</item>
/// </list>
///
/// <para><b>NO DIVERGENCE FROM <c>Available</c>. NOT EVEN A CONSERVATIVE ONE.</b> This paragraph used to
/// record a deliberate divergence: <c>Available</c> did not subtract <c>Expired</c>, so
/// <see cref="ComputeAsync"/> subtracted it here. Commit <c>16ad1b3</c> then added <c>- Expired</c> to
/// <see cref="EmployeeLeaveBalance.Available"/> itself and did not delete this paragraph or the line it
/// justified, so lapsed days were deducted TWICE and every leaver with a non-zero <c>Expired</c> was
/// UNDER-PAID their Art. 111 settlement. The paragraph written to stop the two definitions drifting is
/// what concealed the drift, because it reads as a standing justification for a line that had become a
/// duplicate.</para>
///
/// <para>The rule is therefore now absolute and has no exception to remember: the encashable figure is
/// <see cref="EmployeeLeaveBalance.Available"/>, unmodified. <c>Available</c> already nets off
/// <c>Expired</c>, so lapsed days are still never paid — they are simply netted off ONCE, in the single
/// place ESS, the balance screen and the settlement all read. <c>EncashmentControllerTests</c>
/// .<c>AvailableBalance_SubtractsExpiredAndPendingWithoutDoubleSubtractingEncashmentTransfer</c> pins
/// that definition; <c>LeaveEncashmentCalculatorTests</c> pins that this calculator does not re-apply any
/// part of it. If a future change needs a divergence, it must be expressed as a test in both files, not
/// as a paragraph here.</para>
///
/// <para><b>FAIL-CLOSED ON POLICY.</b> A leave type with NO applicable <c>LeavePolicy</c> encashes
/// NOTHING and raises a warning. Today's behaviour (encash everything) is only safe because the figure is
/// never paid; the moment it is disbursed, "no policy" must mean "no entitlement stated", not "pay it".
/// [FLAG-COMPLIANCE-KSA] Art. 111 grants payment for accrued, untaken ANNUAL leave on termination —
/// whether a given tenant's other leave types are encashable is a policy/legal determination, which is
/// exactly what <c>LeavePolicy.EncashmentAllowed</c> records.</para>
/// </summary>
public static class LeaveEncashmentCalculator
{
    /// <summary>The day-rate basis. Kept as gross ÷ 30 — byte-identical to the figure the pre-C1
    /// endpoint displayed — so making the number PAYABLE does not also silently re-rate it.</summary>
    public const string DayRateBasis = "gross/30";

    public static async Task<LeaveEncashmentResult> ComputeAsync(
        ZayraDbContext db, Guid tenantId, int employeeId, Guid? companyId,
        DateOnly lastWorkingDay, decimal monthlyGross, CancellationToken ct)
    {
        var warnings = new List<string>();
        var dailyRate = monthlyGross > 0m ? Math.Round(monthlyGross / 30m, 6) : 0m;

        // EVERY year up to and including the leaving year — not just the leaving year (defect 3).
        var balances = await db.EmployeeLeaveBalances.AsNoTracking()
            .Where(b => b.TenantId == tenantId && b.EmployeeId == employeeId && b.Year <= lastWorkingDay.Year)
            .ToListAsync(ct);
        if (balances.Count == 0)
            return new LeaveEncashmentResult(Array.Empty<EncashableLeave>(), 0m, 0m, warnings);

        var typeIds = balances.Select(b => b.LeaveTypeId).Distinct().ToList();
        var leaveTypes = await db.LeaveTypes.AsNoTracking()
            .Where(t => t.TenantId == tenantId && typeIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, ct);

        // Company-first policy resolution, mirroring every other company-scoped config read in payroll:
        // the company's own policy wins over the tenant-default (CompanyId == null) row.
        var policies = await db.LeavePolicies.AsNoTracking()
            .Where(p => p.TenantId == tenantId && typeIds.Contains(p.LeaveTypeId)
                     && (p.CompanyId == companyId || p.CompanyId == null))
            .ToListAsync(ct);
        var policyByType = policies
            .GroupBy(p => p.LeaveTypeId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.CompanyId != null)
                                            .ThenByDescending(p => p.Status == "Active")
                                            .First());

        var lines = new List<EncashableLeave>();
        // The per-type cap applies to the TYPE, not to each yearly row, so carry-forward + current-year
        // rows for one type share one budget instead of each getting the full cap.
        var capRemainingByType = new Dictionary<Guid, decimal>();

        foreach (var b in balances.OrderBy(b => b.LeaveTypeId).ThenByDescending(b => b.Year))
        {
            var typeName = leaveTypes.TryGetValue(b.LeaveTypeId, out var lt) && !string.IsNullOrWhiteSpace(lt.NameEn)
                ? lt.NameEn
                : (string.IsNullOrWhiteSpace(b.LeaveTypeName) ? b.LeaveTypeId.ToString() : b.LeaveTypeName);

            if (!policyByType.TryGetValue(b.LeaveTypeId, out var policy))
            {
                if (b.Available > 0m)
                    warnings.Add($"Leave type '{typeName}' has {b.Available:N2} day(s) of balance but NO leave " +
                                 "policy for this legal entity, so nothing is encashed for it. Configure a leave " +
                                 "policy with encashmentAllowed before settling, or add the amount as an explicit " +
                                 "other-dues line.");
                continue;
            }
            if (!policy.EncashmentAllowed)
                continue;   // a deliberate policy answer, not a gap — no warning

            // THE shipped definition, unmodified: Entitled + Accrued + CarriedForward + ManualAdjustment
            // − Used − Pending − Encashed − Expired. Expired is inside `Available` (16ad1b3); subtracting
            // it again here under-paid the leaver. Do not net anything off this line — see class remarks.
            var available = Math.Round(b.Available, 2);
            if (available <= 0m) continue;

            var cap = policy.EncashmentMaxDays > 0m
                ? capRemainingByType.TryGetValue(b.LeaveTypeId, out var left) ? left : policy.EncashmentMaxDays
                : decimal.MaxValue;
            if (cap <= 0m) continue;

            var days = Math.Min(available, cap);
            if (policy.EncashmentMaxDays > 0m) capRemainingByType[b.LeaveTypeId] = cap - days;
            if (days <= 0m) continue;

            lines.Add(new EncashableLeave(
                b.Id, b.LeaveTypeId, typeName, b.Year,
                available, days, Math.Round(days * dailyRate, 2), DayRateBasis));
        }

        return new LeaveEncashmentResult(
            lines,
            Math.Round(lines.Sum(l => l.EncashableDays), 2),
            Math.Round(lines.Sum(l => l.Amount), 2),
            warnings);
    }
}
