using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>Which configured limit bound an overtime quantity first, in policy order.</summary>
public enum OvertimeLimitKind
{
    /// <summary>Nothing bound: the rounded quantity is payable in full.</summary>
    None = 0,
    /// <summary>Below <see cref="OvertimePolicy.MinimumMinutes"/> — the policy pays nothing for it.</summary>
    BelowMinimum,
    /// <summary>Above <see cref="OvertimePolicy.MaximumMinutesPerDay"/> for the work date.</summary>
    AboveDailyMaximum,
    /// <summary>Would take the employee past <see cref="OvertimePolicy.MonthlyCapMinutes"/> for the month.</summary>
    AboveMonthlyCap,
}

/// <summary>
/// What an <see cref="OvertimePolicy"/> makes of a raw overtime quantity.
/// </summary>
/// <param name="RawMinutes">The quantity as measured, before the policy touched it.</param>
/// <param name="RoundedMinutes">After <c>RoundingRule</c>. Rounding can raise as well as lower it.</param>
/// <param name="PayableMinutes">What may be raised as an overtime request — never above the daily
/// maximum, and never enough to take the month past the monthly cap.</param>
/// <param name="ExcessMinutes"><c>RoundedMinutes - PayableMinutes</c>: the time worked that the
/// configured policy does not pay. Never silently dropped — every caller must surface it.</param>
/// <param name="Limit">The FIRST limit that bound, in the order the policy states them. Both caps
/// are applied to <see cref="PayableMinutes"/>; this names the one to report.</param>
public readonly record struct OvertimeLimitOutcome(
    int RawMinutes, int RoundedMinutes, int PayableMinutes, int ExcessMinutes, OvertimeLimitKind Limit)
{
    public bool IsPayable => PayableMinutes > 0;
    public bool HasExcess => ExcessMinutes > 0;
}

/// <summary>
/// The ONE place an <see cref="OvertimePolicy"/>'s scheduling limits are applied — rounding rule,
/// minimum, daily maximum and monthly cap.
///
/// <para><b>The defect this closes.</b> There are two doors into overtime pay. The manual door
/// (<c>POST /api/overtime/requests</c>) rounded the measured minutes, refused anything under
/// <c>MinimumMinutes</c>, refused anything over <c>MaximumMinutesPerDay</c>, and refused anything
/// that would take the month past <c>MonthlyCapMinutes</c>. The attendance door
/// (<c>POST /api/overtime/detect-from-attendance</c>) wrote
/// <c>RequestedMinutes = record.OvertimeMinutes</c> raw and applied NONE of them — it did not even
/// load the policy, it stamped the caller-supplied policy id unvalidated. A fourteen-hour attendance
/// day was raised, approved and paid in full on a policy configured to cap a day at four hours, and a
/// month of them sailed past a monthly cap that the same hours keyed by hand could not get past.
/// Unbounded, and always in the employee's favour: the payslip did not match the policy the client
/// configured, which for a payroll product is the whole product.</para>
///
/// <para><b>Truncation is not a fix.</b> Capping silently would swap an overpayment for an
/// unexplained underpayment. <see cref="OvertimeLimitOutcome.ExcessMinutes"/> is the time the policy
/// will not pay, and callers raise it as a visible exception row against the attendance day it came
/// from rather than dropping it.</para>
/// </summary>
public static class OvertimePolicyLimits
{
    /// <summary>
    /// Rounds <paramref name="rawMinutes"/> by the policy's rule. A zero/unknown rule is a no-op.
    /// </summary>
    public static int ApplyRounding(int minutes, string? rule) => (rule ?? string.Empty) switch
    {
        "Nearest15" => (int)(Math.Round(minutes / 15m, MidpointRounding.AwayFromZero) * 15),
        "Up15" => (int)(Math.Ceiling(minutes / 15m) * 15),
        "Down15" => (int)(Math.Floor(minutes / 15m) * 15),
        "Nearest30" => (int)(Math.Round(minutes / 30m, MidpointRounding.AwayFromZero) * 30),
        _ => minutes
    };

    /// <summary>
    /// Applies the policy's rounding rule and all three quantity limits.
    /// </summary>
    /// <param name="rawMinutes">Overtime as measured — from the punch pair, or from
    /// <c>AttendanceDailyRecord.OvertimeMinutes</c>.</param>
    /// <param name="policy">The tenant's policy. A non-positive limit means "not configured" and is
    /// skipped, so a policy row written outside the create endpoint (which defaults them) cannot
    /// accidentally cap everything to zero.</param>
    /// <param name="monthToDateMinutes">Minutes already requested for this employee in the work
    /// date's calendar month, excluding rejected requests — the same basis the manual door sums.</param>
    public static OvertimeLimitOutcome Apply(int rawMinutes, OvertimePolicy policy, int monthToDateMinutes)
    {
        var rounded = ApplyRounding(rawMinutes, policy.RoundingRule);

        if (policy.MinimumMinutes > 0 && rounded < policy.MinimumMinutes)
            return new OvertimeLimitOutcome(rawMinutes, rounded, 0, rounded, OvertimeLimitKind.BelowMinimum);

        var payable = rounded;
        var limit = OvertimeLimitKind.None;

        if (policy.MaximumMinutesPerDay > 0 && payable > policy.MaximumMinutesPerDay)
        {
            payable = policy.MaximumMinutesPerDay;
            limit = OvertimeLimitKind.AboveDailyMaximum;
        }

        if (policy.MonthlyCapMinutes > 0)
        {
            var remaining = policy.MonthlyCapMinutes - monthToDateMinutes;
            if (payable > remaining)
            {
                payable = Math.Max(0, remaining);
                // First-binding wins: a day that is over BOTH the daily maximum and the monthly cap
                // is reported as over the daily maximum, which is what the manual door tells the user.
                if (limit == OvertimeLimitKind.None) limit = OvertimeLimitKind.AboveMonthlyCap;
            }
        }

        // A remainder smaller than the policy's own minimum is not a payable request — the manual
        // door would refuse it. Pay nothing rather than raise a request the policy would not accept.
        if (policy.MinimumMinutes > 0 && payable < policy.MinimumMinutes) payable = 0;

        return new OvertimeLimitOutcome(rawMinutes, rounded, payable, rounded - payable, limit);
    }

    /// <summary>The message the manual door returns for a bound quantity. One wording, one place.</summary>
    public static string? RefusalMessage(OvertimeLimitKind limit, OvertimePolicy policy) => limit switch
    {
        OvertimeLimitKind.BelowMinimum => $"Minimum overtime is {policy.MinimumMinutes} minutes.",
        OvertimeLimitKind.AboveDailyMaximum => $"Maximum overtime is {policy.MaximumMinutesPerDay} minutes per day.",
        OvertimeLimitKind.AboveMonthlyCap => $"Monthly overtime cap of {policy.MonthlyCapMinutes} minutes would be exceeded.",
        _ => null,
    };

    /// <summary>
    /// The <c>AttendanceException.ExceptionType</c> raised when a detected attendance day carries
    /// overtime the policy will not pay in full.
    /// </summary>
    public const string OvertimeAbovePolicyExceptionType = "OvertimeAbovePolicy";

    /// <summary>
    /// The <c>AttendanceException.ExceptionType</c> raised when a detected attendance day's overtime
    /// falls under the policy's minimum, so no request is raised for it at all.
    /// </summary>
    public const string OvertimeBelowMinimumExceptionType = "OvertimeBelowPolicyMinimum";

    /// <summary>Plain commercial English for what the policy did to this day, for the exception row.</summary>
    public static string ExplainForHuman(OvertimeLimitOutcome outcome, OvertimePolicy policy)
    {
        var rounding = outcome.RoundedMinutes == outcome.RawMinutes
            ? string.Empty
            : $" ({outcome.RawMinutes} min measured, rounded to {outcome.RoundedMinutes} by rule '{policy.RoundingRule}')";
        return outcome.Limit switch
        {
            OvertimeLimitKind.BelowMinimum =>
                $"{outcome.RoundedMinutes} min of attendance overtime{rounding} is below the policy minimum of "
                + $"{policy.MinimumMinutes} min, so no overtime request was raised and none of it is payable.",
            OvertimeLimitKind.AboveDailyMaximum =>
                $"{outcome.RoundedMinutes} min of attendance overtime{rounding} exceeds the policy maximum of "
                + $"{policy.MaximumMinutesPerDay} min per day. {outcome.PayableMinutes} min were raised for payment; "
                + $"{outcome.ExcessMinutes} min are NOT payable under this policy and need a decision.",
            OvertimeLimitKind.AboveMonthlyCap =>
                $"{outcome.RoundedMinutes} min of attendance overtime{rounding} would take this employee past the "
                + $"policy's monthly cap of {policy.MonthlyCapMinutes} min. {outcome.PayableMinutes} min were raised "
                + $"for payment; {outcome.ExcessMinutes} min are NOT payable under this policy and need a decision.",
            _ => $"{outcome.PayableMinutes} min of attendance overtime were raised for payment.",
        };
    }
}
