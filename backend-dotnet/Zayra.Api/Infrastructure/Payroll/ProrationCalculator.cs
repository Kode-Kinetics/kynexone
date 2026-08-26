using Zayra.Api.Application.WorkWeek;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// POD-C3 — the NAMED, per-company PRORATION BASIS.
///
/// <para><b>THE DEFAULT IS <see cref="Calendar30"/>, AND IT IS A DELIBERATE CHOICE, NOT AN ACCIDENT.</b>
/// Day-rate = monthly amount ÷ 30; numerator = the CALENDAR days the employee was actually employed
/// inside the period; factor capped at 1.0. It is chosen because this codebase ALREADY pays unpaid
/// absence at <c>basic ÷ 30</c> (<c>lop.monthly_day_divisor</c>, PayrollController's LOP block, and
/// KsaCalculators' RuleKeys.LopMonthlyDayDivisor). Any other proration basis would put TWO different
/// day-rates on ONE payslip — a joiner's absence day valued differently from their unworked days — so
/// coherence with the existing LOP divisor is the binding constraint. A tenant that sets them
/// inconsistently gets <c>WARN_PRORATION_BASIS_MISMATCH</c> on every run.</para>
///
/// <para><b>THE FULL-PERIOD RULE.</b> An employee employed for the WHOLE period is NEVER prorated —
/// factor is exactly 1.0 whatever the basis and whatever the calendar length. Without this, February
/// (28 days) would pay every employee 28/30 of their salary. The proration arithmetic is reached ONLY
/// when the employment window is strictly narrower than the period, which is also what makes this pod
/// provably inert for every employee in every existing tenant.</para>
///
/// <para><b>THE /30 DISCONTINUITY IS REAL AND INTENDED.</b> Under <see cref="Calendar30"/> a joiner on
/// 1 Feb is paid a full month while a joiner on 2 Feb is paid 27/30. That is the accepted behaviour of a
/// 30-day-month convention and is EXACTLY how the existing LOP deduction already behaves (one absent day
/// in February costs basic/30, not basic/28). Tenants wanting the calendar-exact behaviour choose
/// <see cref="CalendarActual"/>.</para>
/// </summary>
public static class ProrationBases
{
    /// <summary>Default. Numerator = calendar days employed in the period (capped at 30); denominator = 30.</summary>
    public const string Calendar30 = "Calendar30";

    /// <summary>Numerator = calendar days employed in the period; denominator = actual days in the month.</summary>
    public const string CalendarActual = "CalendarActual";

    /// <summary>Numerator = scheduled working days inside the employment window; denominator = scheduled
    /// working days in the whole period. REFUSES when no working week is configured for the company —
    /// silently falling back to the platform Fri/Sat default would put a basis nobody chose onto payroll.</summary>
    public const string WorkingDays = "WorkingDays";

    /// <summary>Escape hatch: factor is always 1.0 (pre-C3 behaviour). Emits <c>WARN_PRORATION_DISABLED</c>
    /// on EVERY run so it can never be silently forgotten.</summary>
    public const string None = "None";

    public static readonly string[] All = { Calendar30, CalendarActual, WorkingDays, None };

    /// <summary>Case-insensitive canonicalisation. Returns null for an unrecognised value so the caller
    /// can refuse — NEVER silently coerces to the default (a typo'd basis must not become a tenant's
    /// payroll basis without anyone choosing it).</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        foreach (var b in All)
            if (string.Equals(b, value.Trim(), StringComparison.OrdinalIgnoreCase)) return b;
        return null;
    }

    /// <summary>Human label used on the payslip narrative.</summary>
    public static string Label(string basis) => basis switch
    {
        Calendar30     => "30-day month",
        CalendarActual => "calendar days",
        WorkingDays    => "working days",
        None           => "no proration",
        _              => basis,
    };
}

/// <summary>
/// POD-C3 — whether the SOCIAL-INSURANCE (GOSI) contributory wage follows the prorated wage actually
/// paid, or stays at the full registered monthly wage in a joining/leaving month.
/// </summary>
public static class ProrationGosiBases
{
    /// <summary>
    /// DEFAULT for KSA. GOSI assesses a MONTHLY contributory wage: the month in which an employee is
    /// registered attracts a full month's contribution on the registered wage, and the GOSI portal has no
    /// partial-month proration of the contributory wage. Prorating it under-remits in every joining and
    /// leaving month — the penalty-bearing direction. It is also the only value CONSISTENT with the
    /// treatment already shipped and tested for unpaid absence, where LOP explicitly does NOT reduce the
    /// covered wage. [FLAG-COMPLIANCE-KSA]
    /// </summary>
    public const string FullMonth = "FullMonth";

    /// <summary>Opt-in: the contribution follows the wage actually paid.</summary>
    public const string Prorated = "Prorated";

    public static readonly string[] All = { FullMonth, Prorated };

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        foreach (var b in All)
            if (string.Equals(b, value.Trim(), StringComparison.OrdinalIgnoreCase)) return b;
        return null;
    }
}

/// <summary>POD-C3 — statutory treatment of retro/arrears amounts.</summary>
public static class ArrearsGosiTreatments
{
    /// <summary>DEFAULT. Contributory in the period the arrears are PAID, for the contributory components
    /// only (ARREARS_BASIC / ARREARS_HOUSING — mirroring SalaryBreakdown.GosiCoveredWage = Basic + Housing).</summary>
    public const string PeriodPaid = "PeriodPaid";

    /// <summary>Arrears carry NO social-insurance charge. Opt-in, and it switches BOTH the run and the
    /// A1 reconstruction off together (one flag governs both sides, so they cannot drift).</summary>
    public const string None = "None";

    /// <summary>
    /// NOT IMPLEMENTED, and deliberately REFUSED rather than silently degraded: filing arrears against the
    /// months EARNED requires an AMENDED GOSI DECLARATION workflow that does not exist in this product.
    /// The earned-basis NUMBER is still computed and persisted on every arrears line
    /// (<c>PayrollArrearsLine.EarnedBasisGosiDelta</c>) so a compliance officer can act on it.
    /// </summary>
    public const string PeriodEarned = "PeriodEarned";

    public static readonly string[] All = { PeriodPaid, None, PeriodEarned };

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        foreach (var b in All)
            if (string.Equals(b, value.Trim(), StringComparison.OrdinalIgnoreCase)) return b;
        return null;
    }
}

/// <summary>
/// One employee's employment window inside one pay period, and the resulting entitlement factor.
/// </summary>
public sealed record ProrationResult(
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    DateOnly PaidFrom,
    DateOnly PaidTo,
    int PaidDays,
    int PeriodDays,
    int DenominatorDays,
    decimal Factor,
    string Basis,
    bool IsProrated,
    bool IsExcluded,
    bool IsFirstWageMonth,
    bool IsFinalWageMonth)
{
    /// <summary>Payslip narrative, e.g. "18/30 days · 30-day month · joined 2026-11-13".
    /// Empty when the employee was employed for the whole period, which is what keeps every existing
    /// payslip byte-identical.</summary>
    public string Narrative
    {
        get
        {
            if (!IsProrated) return string.Empty;
            var parts = new List<string> { $"{PaidDays}/{DenominatorDays} days", ProrationBases.Label(Basis) };
            if (IsFirstWageMonth) parts.Add($"joined {PaidFrom:yyyy-MM-dd}");
            if (IsFinalWageMonth) parts.Add($"last day {PaidTo:yyyy-MM-dd}");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>Prorated money for one employee, split so the components still SUM to the prorated package.</summary>
public sealed record ProratedPackage(
    decimal Basic, decimal Housing, decimal Transport, decimal OtherAllowances, decimal Gross);

/// <summary>
/// POD-C3 — pure proration arithmetic. No DB, no clock, no policy lookup: everything it needs is handed
/// in, so it is exhaustively unit-testable and cannot drift from the run.
/// </summary>
public static class ProrationCalculator
{
    /// <summary>The <see cref="ProrationBases.Calendar30"/> denominator. Deliberately the same 30 the
    /// default LOP divisor uses (<c>lop.monthly_day_divisor</c>).</summary>
    public const int Calendar30Days = 30;

    /// <summary>
    /// Computes the employment window and factor for one employee in one period.
    /// </summary>
    /// <param name="joiningDate">Null (or unset) means "employed before this period" — the safe default
    /// that reproduces pre-C3 behaviour exactly for every legacy row with no joining date.</param>
    /// <param name="lastWorkingDay">Applied ONLY when it falls on/after <paramref name="periodStart"/>.
    /// A stale offboarding from a prior year (a re-hire) must never zero a live employee's wage.</param>
    public static ProrationResult Compute(
        DateOnly periodStart, DateOnly periodEnd,
        DateOnly? joiningDate, DateOnly? lastWorkingDay,
        string basis, WorkWeekConfig? workWeek)
    {
        var periodDays = periodEnd.DayNumber - periodStart.DayNumber + 1;

        var paidFrom = joiningDate is DateOnly jd && jd > periodStart ? jd : periodStart;
        var paidTo   = lastWorkingDay is DateOnly lwd && lwd >= periodStart && lwd < periodEnd ? lwd : periodEnd;

        var isFirstWageMonth = joiningDate is DateOnly j && j > periodStart && j <= periodEnd;
        var isFinalWageMonth = lastWorkingDay is DateOnly l && l >= periodStart && l <= periodEnd;

        // Joined AFTER the period ended, or left BEFORE it started, or the window inverted (an LWD before
        // the joining date). There is no entitlement at all; the caller EXCLUDES the employee rather than
        // writing a zero slip and an empty GL group.
        var joinedAfterPeriod = joiningDate is DateOnly ja && ja > periodEnd;
        if (joinedAfterPeriod || paidTo < paidFrom)
            return new ProrationResult(periodStart, periodEnd, paidFrom, paidTo, 0, periodDays,
                DenominatorFor(basis, periodStart, periodEnd, workWeek), 0m, basis,
                IsProrated: true, IsExcluded: true, isFirstWageMonth, isFinalWageMonth);

        // ── THE FULL-PERIOD RULE ────────────────────────────────────────────────────────────────────
        // Employed for the whole period ⇒ factor is EXACTLY 1.0, whatever the basis and whatever the
        // month length. This is what makes February safe and what makes this pod inert for every
        // employee who neither joined nor left.
        var isProrated = basis != ProrationBases.None && (paidFrom > periodStart || paidTo < periodEnd);
        if (!isProrated)
            return new ProrationResult(periodStart, periodEnd, periodStart, periodEnd,
                periodDays, periodDays, periodDays, 1m, basis,
                IsProrated: false, IsExcluded: false, isFirstWageMonth, isFinalWageMonth);

        var denominator = DenominatorFor(basis, periodStart, periodEnd, workWeek);
        int numerator;
        switch (basis)
        {
            case ProrationBases.WorkingDays:
                numerator = workWeek!.CountWorkingDays(paidFrom, paidTo);
                break;
            case ProrationBases.CalendarActual:
                numerator = paidTo.DayNumber - paidFrom.DayNumber + 1;
                break;
            default: // Calendar30
                numerator = Math.Min(paidTo.DayNumber - paidFrom.DayNumber + 1, Calendar30Days);
                break;
        }

        var factor = denominator <= 0 ? 0m : Math.Round(numerator / (decimal)denominator, 6, MidpointRounding.AwayFromZero);
        if (factor > 1m) factor = 1m;
        if (factor < 0m) factor = 0m;

        return new ProrationResult(periodStart, periodEnd, paidFrom, paidTo, numerator, periodDays,
            denominator, factor, basis, IsProrated: true, IsExcluded: false, isFirstWageMonth, isFinalWageMonth);
    }

    private static int DenominatorFor(string basis, DateOnly periodStart, DateOnly periodEnd, WorkWeekConfig? workWeek)
        => basis switch
        {
            ProrationBases.WorkingDays    => workWeek?.CountWorkingDays(periodStart, periodEnd) ?? 0,
            ProrationBases.CalendarActual => periodEnd.DayNumber - periodStart.DayNumber + 1,
            _                              => Calendar30Days,
        };

    /// <summary>
    /// Applies a factor to a monthly package so the prorated COMPONENTS still sum EXACTLY to the prorated
    /// PACKAGE. Housing/transport/other are rounded individually and BASIC absorbs the residual, so
    /// Σ components == round(factor × gross, 2) to the halala — no rounding dust leaks into the GL, where
    /// it would show up as a dr/cr imbalance at Lock.
    /// </summary>
    public static ProratedPackage Apply(decimal basic, decimal housing, decimal transport, decimal other, decimal factor)
    {
        if (factor == 1m) return new ProratedPackage(basic, housing, transport, other, basic + housing + transport + other);
        var gross = basic + housing + transport + other;
        var targetGross = Math.Round(gross * factor, 2, MidpointRounding.AwayFromZero);
        var h = Math.Round(housing   * factor, 2, MidpointRounding.AwayFromZero);
        var t = Math.Round(transport * factor, 2, MidpointRounding.AwayFromZero);
        var o = Math.Round(other     * factor, 2, MidpointRounding.AwayFromZero);
        var b = targetGross - h - t - o;
        // Defensive: an all-allowance package with a zero basic must not be handed a negative residual.
        if (b < 0m) { b = 0m; targetGross = h + t + o; }
        return new ProratedPackage(b, h, t, o, targetGross);
    }

    /// <summary>Prorates a single scalar (fixed deduction, taxable base) on the same factor and rounding.</summary>
    public static decimal ApplyScalar(decimal amount, decimal factor)
        => factor == 1m ? amount : Math.Round(amount * factor, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// POD-C3-FIX — the SAME narrative as <see cref="ProrationResult.Narrative"/>, rebuilt from the
    /// witnesses a slip actually persisted rather than from a recomputed policy. The admin payslip PDF
    /// (<c>DownloadSlipPdf</c>) builds its line items from the slip HEADER columns, so it never saw the
    /// note that <c>WithProrationNote</c> stamps onto the earning ROWS — the employee's ESS PDF explained
    /// a halved wage and the auditor's PDF did not. Reading the stored columns (never recomputing) is what
    /// guarantees the two PDFs cannot disagree.
    ///
    /// <para>Returns empty for every slip that was not prorated, including every pre-C3 slip (all the
    /// witness columns are null there), which keeps existing payslip PDFs byte-identical.</para>
    /// </summary>
    /// <param name="periodStart">First day of the run's period — the only thing not on the slip, and what
    /// distinguishes a JOINER (window starts late) from a leaver.</param>
    public static string NarrativeFromSlip(Zayra.Api.Models.PayrollSlip slip, DateOnly periodStart)
    {
        if (slip.ProrationFactor is not decimal f || f == 1m) return string.Empty;
        if (slip.PaidDays is not int paid || slip.ProrationDenominatorDays is not int denom) return string.Empty;

        var parts = new List<string> { $"{paid}/{denom} days", ProrationBases.Label(slip.ProrationBasis ?? string.Empty) };
        if (slip.PaidFromDate is DateOnly from && from > periodStart) parts.Add($"joined {from:yyyy-MM-dd}");
        if (slip.IsFinalWageMonth && slip.PaidToDate is DateOnly to)  parts.Add($"last day {to:yyyy-MM-dd}");
        return string.Join(" · ", parts);
    }
}
