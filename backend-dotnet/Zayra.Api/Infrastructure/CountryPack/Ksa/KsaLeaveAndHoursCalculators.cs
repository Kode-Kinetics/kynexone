using Zayra.Api.Application.CountryPack;
using Zayra.Api.Infrastructure.Localization;

namespace Zayra.Api.Infrastructure.CountryPack.Ksa;

// ─────────────────────────────────────────────────────────────────────────────
//  KSA statutory LEAVE and WORKING HOURS — Royal Decree M/51 (2005) as amended.
//
//  Three statutory rules live here, as PURE functions over values that are read
//  from effective-dated StatutoryRule rows rather than compiled in as literals.
//  The literals that do appear are FALLBACK defaults for an unseeded database and
//  are identical to the seeded values in StatutoryRuleSeeder.
//
//    Art. 98  — Ramadan reduced working hours  → KsaRamadanWorkingHours
//    Art. 109 — Annual leave 21 → 30 days      → KsaAnnualLeaveScale
//    Art. 117 — Sick leave 30/60/30 pay scale  → KsaSickLeaveScale
//
//  SOURCE NOTE — read this before changing any number below.
//  The Ministry of Human Resources and Social Development publishes TWO English
//  texts that DISAGREE about Art. 98:
//    (a) hrsd.gov.sa knowledge centre, article 312 (last modified 2025-09-02):
//        8 h/day, 48 h/week; Ramadan for Muslims 6 h/day or 36 h/week.
//    (b) hrsd.gov.sa/sites/default/files/2023-02/Labor.pdf:
//        9 h/day, 45 h/week; Ramadan 7 h/day or 35 h/week; and TWO weekly rest
//        days at Art. 104.
//  (b) is NOT the operative text: its Art. 104 grants two rest days a week, where
//  the operative Art. 104 grants one rest day of not less than 24 consecutive
//  hours (Friday). (b) reads as an un-enacted five-day-week amendment package.
//  This pack therefore implements (a) — which is also the employee-favourable
//  reading, because a LOWER Ramadan baseline makes MORE hours overtime-bearing.
//  [COUNSEL] Confirm the operative Art. 98 figures before filing KSA payroll.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// KSA Labour Law Art. 98 — reduced actual working hours during Ramadan.
/// "During the month of Ramadan, the actual working hours for Muslims shall be
/// reduced so that they do not exceed six hours a day or thirty-six hours a week."
///
/// This changes the OVERTIME BASELINE, not pay: Art. 98 cuts hours, it does not cut
/// the monthly wage, so a Ramadan day is worth the same money for fewer hours and
/// every hour worked beyond the reduced baseline is overtime under Art. 107.
/// </summary>
public static class KsaRamadanWorkingHours
{
    /// <summary>Ramadan is the 9th month of the Hijri calendar.</summary>
    public const int RamadanHijriMonth = 9;

    /// <summary>
    /// True when <paramref name="date"/> falls in Ramadan on the Um al-Qura calendar —
    /// the official Saudi civil calendar, which is what <see cref="IHijriDateService"/> uses.
    /// Ramadan moves roughly 11 days earlier each Gregorian year, so this must never be
    /// expressed as a fixed Gregorian range.
    ///
    /// Returns false rather than throwing outside UmAlQuraCalendar's supported range
    /// (roughly 1900-04-30 … 2077-11-16): a date that far out cannot be a real work date,
    /// and falling back to the standard baseline is the non-destructive answer.
    /// </summary>
    public static bool IsRamadan(IHijriDateService hijri, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(hijri);
        try
        {
            return hijri.FromGregorian(date).HijriMonth == RamadanHijriMonth;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>
    /// The daily working-minutes baseline for a date. During Ramadan the reduced baseline
    /// applies, but ONLY if it is genuinely lower — a misconfigured "reduced" value above
    /// the standard one would RAISE the overtime threshold and under-pay overtime, so it
    /// is clamped. Non-positive configured values fall back to the standard baseline.
    /// </summary>
    public static int DailyBaselineMinutes(bool isRamadan, int standardMinutes, int ramadanMinutes)
    {
        if (standardMinutes <= 0) standardMinutes = KsaLeaveHoursDefaults.StandardWorkMinutesPerDay;
        if (!isRamadan) return standardMinutes;
        if (ramadanMinutes <= 0) return standardMinutes;
        return Math.Min(ramadanMinutes, standardMinutes);
    }
}

/// <summary>
/// Who the Art. 98 Ramadan reduction is applied to. The statute says "for Muslims".
/// </summary>
public static class RamadanScopes
{
    /// <summary>Applied to every employee of a KSA company. The default — see the note on
    /// <see cref="Normalize"/> for why.</summary>
    public const string AllEmployees = "all";

    /// <summary>Not applied at all. An employer may not lawfully choose this for Muslim
    /// employees; it exists so a tenant can suppress the behaviour while it verifies its
    /// own attendance data, and it is announced when it takes effect.</summary>
    public const string None = "none";

    /// <summary>
    /// Nominally "Muslim employees only". NOT SUPPORTED: the Employee model carries no
    /// religion attribute, so the product cannot identify who is in scope. Normalizes to
    /// <see cref="AllEmployees"/> — applying the reduction to everyone over-delivers to
    /// non-Muslim staff, whereas applying it to nobody would strip a statutory entitlement
    /// from every Muslim employee. Over-delivery is the only safe direction here.
    /// </summary>
    public const string MuslimEmployees = "muslim";

    /// <summary>
    /// Folds a configured scope to one the product can actually evaluate. Unrecognised and
    /// empty values fold to <see cref="AllEmployees"/>: an unreadable configuration must not
    /// silently disable a statutory entitlement.
    /// </summary>
    public static string Normalize(string? configured)
    {
        var v = (configured ?? string.Empty).Trim().ToLowerInvariant();
        return v == None ? None : AllEmployees;
    }

    /// <summary>True when the configured scope was "muslim", i.e. the caller should announce
    /// that the product widened it to all staff because it cannot identify religion.</summary>
    public static bool WasWidenedFromMuslimOnly(string? configured)
        => string.Equals((configured ?? string.Empty).Trim(), MuslimEmployees, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// KSA Labour Law Art. 109(1) — "A worker shall be entitled to a prepaid annual leave of not
/// less than 21 days, to be increased to a period of not less than 30 days if the worker
/// spends five consecutive years in the service of the employer."
/// </summary>
public static class KsaAnnualLeaveScale
{
    /// <summary>
    /// Statutory annual-leave days for a given length of CONTINUOUS service.
    ///
    /// The step is applied at COMPLETION of the threshold (>=, not >). Art. 109 says the
    /// entitlement rises "if the worker spends five consecutive years in the service of the
    /// employer" — a worker who has spent exactly five years has spent five years. The
    /// alternative reading (strictly more than five) delays a statutory uplift, i.e. it
    /// under-accrues, so it is not the reading to guess with.
    /// [COUNSEL] Confirm whether the uplift attaches from the fifth anniversary itself or
    /// from the start of the following leave year. This implementation takes the earlier,
    /// employee-favourable date.
    ///
    /// Both day figures are statutory FLOORS ("not less than"), so a configured value BELOW
    /// the floor is ignored by the caller, exactly as KsaEndOfServiceCalculator does for Art. 84.
    /// </summary>
    public static decimal EntitlementDays(
        decimal continuousServiceYears,
        decimal baseDays,
        decimal tieredDays,
        decimal thresholdYears)
    {
        if (baseDays <= 0m) baseDays = KsaLeaveHoursDefaults.AnnualLeaveBaseDays;
        if (tieredDays <= 0m) tieredDays = KsaLeaveHoursDefaults.AnnualLeaveTieredDays;
        if (thresholdYears <= 0m) thresholdYears = KsaLeaveHoursDefaults.AnnualLeaveTierThresholdYears;

        // A tiered figure below the base would be a misconfiguration that REDUCES entitlement
        // at the five-year mark. Never let the scale go backwards.
        if (tieredDays < baseDays) tieredDays = baseDays;

        return continuousServiceYears >= thresholdYears ? tieredDays : baseDays;
    }

    /// <summary>
    /// Completed years of CONTINUOUS service at <paramref name="on"/>, measured from the
    /// employee's service start date. Art. 2 defines Continuous Service as "the uninterrupted
    /// service of a worker for the same employer or his legal successor FROM THE STARTING DATE
    /// OF SERVICE", so a genuine break in service restarts the clock and the caller must pass
    /// the CURRENT engagement's start date for a rehire.
    /// [COUNSEL] This product has no bridged-service / service-continuity field (only
    /// EmployeeOffboarding.RehireEligible, which is an eligibility flag, not a continuity one),
    /// so a rehire whose prior service was contractually bridged will under-count here.
    /// That is flagged rather than guessed.
    /// </summary>
    public static decimal ContinuousServiceYears(DateOnly serviceStart, DateOnly on)
    {
        if (on <= serviceStart) return 0m;
        // Whole months then the day remainder, matching KsaEndOfServiceCalculator.ServicePeriod
        // so tenure means the same thing in leave as it does in gratuity.
        int months = (on.Year - serviceStart.Year) * 12 + (on.Month - serviceStart.Month);
        int days = on.Day - serviceStart.Day;
        if (days < 0)
        {
            months--;
            days += DateTime.DaysInMonth(on.Year, on.Month == 1 ? 12 : on.Month - 1);
        }
        if (months < 0) return 0m;
        return months / 12m + days / 365m;
    }
}

/// <summary>One band of the Art. 117 sick-leave scale.</summary>
/// <param name="Days">Length of the band in days.</param>
/// <param name="PayRate">Fraction of wage payable for days in this band (1.0, 0.75, 0.0).</param>
public sealed record SickLeaveBand(decimal Days, decimal PayRate);

/// <summary>
/// How a single sick-leave request splits across the Art. 117 bands.
/// <paramref name="UnpaidEquivalentDays"/> is the deduction quantity: the sum over the
/// request of (1 − payRate) per day. 4 days at 75% pay is 1.0 unpaid-equivalent days.
/// </summary>
public sealed record SickLeaveAllocation(
    IReadOnlyList<(decimal Days, decimal PayRate)> Bands,
    decimal UnpaidEquivalentDays,
    decimal DaysBeyondScale);

/// <summary>
/// KSA Labour Law Art. 117 — "A worker whose illness has been proven shall be eligible for a
/// paid sick leave for the first 30 days, three quarters of the wage for the next 60 days, and
/// without pay for the following 30 days, during a single year, whether such leaves are
/// continuous or intermittent. A single year shall mean the year which begins from the date of
/// the first sick leave."
/// </summary>
public static class KsaSickLeaveScale
{
    /// <summary>The statutory scale. A FLOOR — an employer may pay more, never less.</summary>
    public static IReadOnlyList<SickLeaveBand> StatutoryBands => new[]
    {
        new SickLeaveBand(KsaLeaveHoursDefaults.SickBand1Days, KsaLeaveHoursDefaults.SickBand1PayRate),
        new SickLeaveBand(KsaLeaveHoursDefaults.SickBand2Days, KsaLeaveHoursDefaults.SickBand2PayRate),
        new SickLeaveBand(KsaLeaveHoursDefaults.SickBand3Days, KsaLeaveHoursDefaults.SickBand3PayRate),
    };

    /// <summary>
    /// Splits <paramref name="requestedDays"/> across the bands, given how many sick days the
    /// employee has ALREADY taken in the same Art. 117 entitlement year.
    ///
    /// Days beyond the end of the scale (past 120 on the statutory bands) are reported in
    /// <see cref="SickLeaveAllocation.DaysBeyondScale"/> and charged at the LAST band's rate.
    /// Art. 117 grants nothing past the third band, so on the statutory scale that is 0% —
    /// the same as the unpaid band, which is the correct and non-surprising continuation.
    /// </summary>
    public static SickLeaveAllocation Allocate(
        decimal priorDaysInEntitlementYear,
        decimal requestedDays,
        IReadOnlyList<SickLeaveBand>? bands = null)
    {
        bands ??= StatutoryBands;
        var result = new List<(decimal Days, decimal PayRate)>();
        decimal unpaidEquivalent = 0m;
        decimal beyond = 0m;

        if (requestedDays <= 0m) return new SickLeaveAllocation(result, 0m, 0m);
        if (priorDaysInEntitlementYear < 0m) priorDaysInEntitlementYear = 0m;

        decimal consumed = priorDaysInEntitlementYear;   // position on the scale before this request
        decimal remaining = requestedDays;
        decimal bandStart = 0m;
        decimal lastRate = bands.Count > 0 ? bands[^1].PayRate : 0m;

        foreach (var band in bands)
        {
            if (remaining <= 0m) break;
            decimal bandEnd = bandStart + Math.Max(0m, band.Days);

            // How much of THIS request lands inside [bandStart, bandEnd)?
            decimal availableInBand = bandEnd - Math.Max(consumed, bandStart);
            if (availableInBand > 0m)
            {
                decimal take = Math.Min(remaining, availableInBand);
                if (take > 0m)
                {
                    result.Add((take, band.PayRate));
                    unpaidEquivalent += take * (1m - band.PayRate);
                    remaining -= take;
                    consumed += take;
                }
            }

            bandStart = bandEnd;
        }

        if (remaining > 0m)
        {
            beyond = remaining;
            result.Add((remaining, lastRate));
            unpaidEquivalent += remaining * (1m - lastRate);
        }

        return new SickLeaveAllocation(result, decimal.Round(unpaidEquivalent, 4), beyond);
    }

    /// <summary>
    /// The start of the Art. 117 entitlement year containing <paramref name="on"/>.
    ///
    /// "A single year shall mean the year which begins from the date of the first sick leave" —
    /// so the window is NOT the calendar year and NOT a trailing 365 days. It is anchored on the
    /// employee's first-ever sick leave and rolls forward in whole 365-day steps from there.
    /// [COUNSEL] Two readings are open and both change money: (i) the anchor re-arms on the first
    /// sick leave AFTER a clear year, rather than stepping mechanically every 365 days from the
    /// very first one; (ii) a request that straddles a window boundary. This implementation steps
    /// mechanically and assigns a whole request to the window of its START date; both choices are
    /// flagged rather than presented as settled.
    /// </summary>
    public static DateOnly EntitlementYearStart(DateOnly firstSickLeaveDate, DateOnly on)
    {
        if (on <= firstSickLeaveDate) return firstSickLeaveDate;
        int elapsed = on.DayNumber - firstSickLeaveDate.DayNumber;
        int windows = elapsed / 365;
        return firstSickLeaveDate.AddDays(365 * windows);
    }
}

/// <summary>
/// Fallback defaults, used only when the StatutoryRule table has not been seeded. Every one of
/// these is mirrored by a seeded, effective-dated row in StatutoryRuleSeeder — the rule row is
/// the source of truth and these exist so an unseeded database behaves statutorily rather than
/// arbitrarily.
/// </summary>
public static class KsaLeaveHoursDefaults
{
    // Art. 98
    public const int StandardWorkMinutesPerDay = 480;   // 8 h
    public const int RamadanWorkMinutesPerDay = 360;    // 6 h
    public const int RamadanWorkMinutesPerWeek = 2160;  // 36 h

    // Art. 109
    public const decimal AnnualLeaveBaseDays = 21m;
    public const decimal AnnualLeaveTieredDays = 30m;
    public const decimal AnnualLeaveTierThresholdYears = 5m;

    // Art. 117
    public const decimal SickBand1Days = 30m;
    public const decimal SickBand1PayRate = 1.00m;
    public const decimal SickBand2Days = 60m;
    public const decimal SickBand2PayRate = 0.75m;
    public const decimal SickBand3Days = 30m;
    public const decimal SickBand3PayRate = 0.00m;
}

/// <summary>
/// Rule keys for the Art. 98 / 109 / 117 parameters. Separate from the internal
/// <c>RuleKeys</c> class in KsaCalculators.cs because these are read from Infrastructure/Leave
/// and Infrastructure/Attendance, which are outside that class's assembly-internal usage site
/// but in the same assembly — this class is public so the intent is explicit.
/// </summary>
public static class KsaLeaveHoursRuleKeys
{
    // Art. 98 — working-hours baseline.
    public const string StandardWorkMinutesPerDay = "workhours.standard_minutes_per_day";
    public const string RamadanWorkMinutesPerDay = "workhours.ramadan_minutes_per_day";
    public const string RamadanWorkMinutesPerWeek = "workhours.ramadan_minutes_per_week";
    public const string RamadanScope = "workhours.ramadan_scope";

    // Art. 109 — annual leave tiering.
    public const string AnnualLeaveBaseDays = "leave.annual_base_days";
    public const string AnnualLeaveTieredDays = "leave.annual_tiered_days";
    public const string AnnualLeaveTierThresholdYears = "leave.annual_tier_threshold_years";

    // Art. 117 — sick-leave pay scale.
    public const string SickBand1Days = "leave.sick_band1_days";
    public const string SickBand1PayRate = "leave.sick_band1_pay_rate";
    public const string SickBand2Days = "leave.sick_band2_days";
    public const string SickBand2PayRate = "leave.sick_band2_pay_rate";
    public const string SickBand3Days = "leave.sick_band3_days";
    public const string SickBand3PayRate = "leave.sick_band3_pay_rate";
    public const string SickApplyStatutoryScale = "leave.sick_apply_statutory_scale";
    public const string SickReductionWageBase = "leave.sick_reduction_wage_base";   // "basic" | "wage"
}

/// <summary>
/// Reads the Art. 98 working-hours baseline for a date, resolving Ramadan through the Um al-Qura
/// calendar rather than any Gregorian range. Country-scoped: a non-KSA company is unaffected.
/// </summary>
public sealed class KsaWorkingHoursBaselineService
{
    private readonly IStatutoryRuleReader _rules;
    private readonly IHijriDateService _hijri;

    public KsaWorkingHoursBaselineService(IStatutoryRuleReader rules, IHijriDateService hijri)
    {
        _rules = rules;
        _hijri = hijri;
    }

    /// <summary>
    /// The daily working-minutes baseline to measure overtime and undertime against.
    /// <paramref name="policyStandardMinutes"/> is the tenant's own AttendancePolicy value and is
    /// returned unchanged for any non-KSA company or any date outside Ramadan.
    /// </summary>
    public async Task<WorkingHoursBaseline> ResolveDailyAsync(
        string? countryCode, DateOnly workDate, int policyStandardMinutes, CancellationToken ct = default)
    {
        var cc = (countryCode ?? string.Empty).Trim();
        bool isKsa = cc.Equals("SA", StringComparison.OrdinalIgnoreCase)
                  || cc.Equals("SAU", StringComparison.OrdinalIgnoreCase);
        if (!isKsa) return new WorkingHoursBaseline(policyStandardMinutes, false, null);

        if (!KsaRamadanWorkingHours.IsRamadan(_hijri, workDate))
            return new WorkingHoursBaseline(policyStandardMinutes, false, null);

        var scopeRaw = await _rules.GetStringAsync(
            CountryCodes.Saudi, Jurisdictions.KsaMainland,
            KsaLeaveHoursRuleKeys.RamadanScope, workDate, null, ct);
        var scope = RamadanScopes.Normalize(scopeRaw);

        if (scope == RamadanScopes.None)
            return new WorkingHoursBaseline(policyStandardMinutes, true,
                "[CONF-KSA] Ramadan reduced working hours (Art. 98) are DISABLED for this jurisdiction by the " +
                $"statutory rule '{KsaLeaveHoursRuleKeys.RamadanScope}'. Art. 98 reduces actual working hours for " +
                "Muslim employees during Ramadan and an employer cannot contract out of it, so this setting is " +
                "lawful only where no employee is in scope. Overtime for this day was measured against the " +
                "ordinary baseline.");

        int ramadanMinutes = (int)(await _rules.GetDecimalAsync(
            CountryCodes.Saudi, Jurisdictions.KsaMainland,
            KsaLeaveHoursRuleKeys.RamadanWorkMinutesPerDay, workDate, null, ct)
            ?? KsaLeaveHoursDefaults.RamadanWorkMinutesPerDay);

        int baseline = KsaRamadanWorkingHours.DailyBaselineMinutes(true, policyStandardMinutes, ramadanMinutes);

        string? notice = null;
        if (RamadanScopes.WasWidenedFromMuslimOnly(scopeRaw))
            notice = "[COUNSEL-KSA] Art. 98 reduces Ramadan working hours \"for Muslims\", and this tenant configured " +
                     $"'{KsaLeaveHoursRuleKeys.RamadanScope}' = 'muslim'. The employee record carries no religion " +
                     "attribute, so the product cannot identify who is in scope and has applied the reduction to ALL " +
                     "employees of this KSA company. That over-delivers to non-Muslim staff; applying it to nobody " +
                     "would strip a statutory entitlement from every Muslim employee, which is the unlawful direction.";

        return new WorkingHoursBaseline(baseline, true, notice);
    }
}

/// <param name="DailyMinutes">Minutes of actual work before overtime begins.</param>
/// <param name="IsRamadan">Whether the date fell in Ramadan on the Um al-Qura calendar.</param>
/// <param name="Notice">A compliance notice to surface, or null.</param>
public sealed record WorkingHoursBaseline(int DailyMinutes, bool IsRamadan, string? Notice);
