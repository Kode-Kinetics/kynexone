using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

public static class PayrollRuleKeys
{
    public const string RestDayWorkMode = "rest_day_work_mode";
    public const string RestDayMultiplier = "rest_day_multiplier";
    public const string HolidayMultiplier = "holiday_multiplier";
    public const string LeaveCashOutOnExit = "leave_cashout_on_exit";
    public const string LeaveCashOutBasis = "leave_cashout_basis";
    public const string MaxProbationDays = "max_probation_days";
    public const string ProrationBasis = "proration_basis";
    public const string AbsenceDeductionBase = "absence_deduction_base";

    public static readonly IReadOnlyList<string> All = new[]
    {
        RestDayWorkMode, RestDayMultiplier, HolidayMultiplier, LeaveCashOutOnExit,
        LeaveCashOutBasis, MaxProbationDays, ProrationBasis, AbsenceDeductionBase,
    };
}

public static class RestDayWorkModes
{
    /// <summary>Hours worked on a rest day or company holiday are overtime, paid at the premium multiplier.</summary>
    public const string PaidPremium = "PaidPremium";
    /// <summary>Hours worked on a rest day or company holiday earn a compensatory day off instead of pay.</summary>
    public const string CompensatoryDay = "CompensatoryDay";
}

/// <summary>The company's effective payroll rules: its own choices, with the platform default wherever it made none.</summary>
public sealed record CompanyPayrollRuleSet(
    string RestDayWorkMode,
    decimal? RestDayMultiplier,
    decimal? HolidayMultiplier,
    bool LeaveCashOutOnExit,
    string LeaveCashOutBasis,
    int? MaxProbationDays,
    string? ProrationBasis,
    string AbsenceDeductionBase)
{
    /// <summary>What every company gets until HR chooses otherwise. Each value reproduces the behaviour that existed
    /// before these were settings, except <see cref="LeaveCashOutOnExit"/>, which is the statutory default.</summary>
    public static readonly CompanyPayrollRuleSet Defaults = new(
        RestDayWorkModes.PaidPremium, null, null, true, CompanyPayrollRules.BasisGross, null,
        null, CompanyPayrollRules.AbsenceBasic);
}

/// <summary>One rule as shown on the settings screen.</summary>
public sealed record PayrollRuleView(
    string Key, string? Value, bool IsSet, string? WarningCode, string? Warning,
    bool Acknowledged, DateTime? AcknowledgedAtUtc, Guid? AcknowledgedBy, string? AcknowledgementNote,
    DateOnly? EffectiveFrom = null, string? UpcomingValue = null, DateOnly? UpcomingFrom = null, bool HasUpcoming = false);

public sealed record PayrollRuleChange(string Key, string? Value);

public sealed record PayrollRuleSaveResult(
    bool Saved, IReadOnlyList<string> Errors, IReadOnlyList<PayrollRuleWarning> UnacknowledgedWarnings);

public sealed record PayrollRuleWarning(string Key, string Code, string Message);

/// <summary>
/// Per-company payroll rules. Every rule is the company's own choice. Where Saudi law sets a minimum, a value
/// below it is accepted, but only with an explicit acknowledgement that is stored with who and when; it is never a
/// hard stop.
/// </summary>
public static class CompanyPayrollRules
{
    public const string BasisGross = "Gross";
    public const string BasisBasic = "Basic";
    public const string BasisBasicHousing = "BasicHousing";
    public const string ProrationCalendar30 = "Calendar30";
    public const string ProrationActualDays = "CalendarActual";
    public const string AbsenceBasic = "Basic";
    public const string AbsenceGross = "Gross";

    /// <summary>Art. 107: overtime is the hour plus at least half of it again.</summary>
    public const decimal StatutoryOvertimeFloor = 1.5m;
    /// <summary>Art. 53: probation of at most 90 days, extendable once by written agreement to 180.</summary>
    public const int StatutoryProbationDays = 90;
    public const int ExtendedProbationDays = 180;
    public const int MaxProbationDaysAllowed = 365;

    public const string WarnBelowOvertimeFloor = "BELOW_OVERTIME_FLOOR";
    public const string WarnLeaveCashOutOff = "LEAVE_CASHOUT_OFF";
    public const string WarnProbationOver90 = "PROBATION_OVER_90";
    public const string WarnProbationOver180 = "PROBATION_OVER_180";

    /// <summary>Validates one value. Returns an error for a value that is meaningless (never for a legal-minimum
    /// shortfall) and the warning for one that is below a statutory minimum.</summary>
    public static (string? Error, PayrollRuleWarning? Warning) Evaluate(string key, string? value)
    {
        var v = value?.Trim();
        // Every legal value is short; a long one is refused here with a reason instead of failing the column width later.
        if (v is { Length: > 24 }) return ($"{key} is too long.", null);
        switch (key)
        {
            case PayrollRuleKeys.RestDayWorkMode:
                return OneOf(key, v, RestDayWorkModes.PaidPremium, RestDayWorkModes.CompensatoryDay);
            case PayrollRuleKeys.LeaveCashOutBasis:
                return OneOf(key, v, BasisGross, BasisBasic, BasisBasicHousing);
            case PayrollRuleKeys.ProrationBasis:
                return OneOf(key, v, ProrationCalendar30, ProrationActualDays);
            case PayrollRuleKeys.AbsenceDeductionBase:
                return OneOf(key, v, AbsenceBasic, AbsenceGross);
            case PayrollRuleKeys.RestDayMultiplier:
            case PayrollRuleKeys.HolidayMultiplier:
            {
                if (!decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var m) || m < 1m || m > 5m)
                    return ($"{key} must be a multiplier between 1 and 5.", null);
                return (null, m < StatutoryOvertimeFloor
                    ? new PayrollRuleWarning(key, WarnBelowOvertimeFloor,
                        $"{m:0.##}x is below the statutory 150% (Art. 107). Paying less than the minimum is the company's choice and is recorded against the person who confirms it.")
                    : null);
            }
            case PayrollRuleKeys.LeaveCashOutOnExit:
            {
                if (!bool.TryParse(v, out var on)) return ($"{key} must be true or false.", null);
                return (null, on ? null : new PayrollRuleWarning(key, WarnLeaveCashOutOff,
                    "Saudi law pays out accrued, untaken annual leave when employment ends (Art. 111). Turning this off withholds that pay and is recorded against the person who confirms it."));
            }
            case PayrollRuleKeys.MaxProbationDays:
            {
                if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) || d < 0 || d > MaxProbationDaysAllowed)
                    return ($"{key} must be a whole number of days between 0 and {MaxProbationDaysAllowed}.", null);
                if (d > ExtendedProbationDays)
                    return (null, new PayrollRuleWarning(key, WarnProbationOver180,
                        $"{d} days is beyond the 180-day statutory maximum even with written agreement (Art. 53). It is recorded against the person who confirms it."));
                if (d > StatutoryProbationDays)
                    return (null, new PayrollRuleWarning(key, WarnProbationOver90,
                        $"{d} days is beyond the 90-day statutory probation (Art. 53). Up to 180 days is allowed only with the employee's written agreement. It is recorded against the person who confirms it."));
                return (null, null);
            }
            default:
                return ($"Unknown payroll rule '{key}'.", null);
        }
    }

    private static (string?, PayrollRuleWarning?) OneOf(string key, string? v, params string[] allowed)
        => allowed.Contains(v) ? (null, null) : ($"{key} must be one of: {string.Join(", ", allowed)}.", null);

    /// <summary>
    /// The refusal message when an offered probation is longer than the company's own maximum (Setup -> Payroll rules),
    /// or null. A company that set no maximum keeps the previous limit of twelve months. The probation is measured in
    /// real calendar days from the start date, so 3 months from 1 Jan is 90 days and from 1 Feb is 89.
    /// </summary>
    public static string? ProbationOverLimit(CompanyPayrollRuleSet rules, DateOnly startDate, int probationMonths)
    {
        if (rules.MaxProbationDays is not { } max || probationMonths <= 0 || startDate == DateOnly.MinValue) return null;
        var days = startDate.AddMonths(probationMonths).DayNumber - startDate.DayNumber;
        return days > max
            ? $"A probation of {probationMonths} month(s) is {days} days, longer than this company's maximum of {max} days. Shorten the probation, or raise the maximum in Setup > Payroll rules."
            : null;
    }

    /// <summary>
    /// The warning text when a probation that runs from <paramref name="start"/> to <paramref name="end"/> (both days
    /// counted) is longer than the company's own maximum, or null. Used for employee records and import rows, where the
    /// dates are given rather than a number of months. Never a refusal: the row is kept and flagged.
    /// </summary>
    public static string? ProbationDatesOverLimit(CompanyPayrollRuleSet rules, DateOnly start, DateOnly? end)
    {
        if (rules.MaxProbationDays is not { } max || end is not { } last || start == DateOnly.MinValue) return null;
        var days = last.DayNumber - start.DayNumber + 1;
        return days > max
            ? $"Probation of {days} days is longer than this company's maximum of {max} days (Setup > Payroll rules). It was kept and needs HR review."
            : null;
    }

    /// <summary>Whether any active holiday calendar of the company (or a tenant-wide one) has a non-optional
    /// public holiday dated inside the period. Feeds the non-blocking payroll pre-check.</summary>
    public static Task<bool> HasHolidayInPeriodAsync(
        ZayraDbContext db, Guid tenantId, Guid? companyId, DateOnly periodStart, DateOnly periodEnd, CancellationToken ct)
        => (from h in db.PublicHolidays.AsNoTracking()
            join c in db.PublicHolidayCalendars.AsNoTracking() on h.CalendarId equals c.Id
            where h.TenantId == tenantId && c.IsActive && !h.IsOptional && h.Date >= periodStart && h.Date <= periodEnd
                  && (c.CompanyId == null || c.CompanyId == companyId)
            select h.Id).AnyAsync(ct);

    /// <summary>The monthly amount an absent day is priced on: basic (default) or the whole recurring package.</summary>
    public static decimal AbsenceDeductionBaseAmount(string baseChoice, decimal basic, decimal housing, decimal transport, decimal other)
        => baseChoice == AbsenceGross ? basic + housing + transport + other : basic;

    /// <summary>The impact type LeaveService writes for an approved unpaid-leave request.</summary>
    public const string UnpaidLeaveImpactType = "Leave Deduction (Unpaid)";

    /// <summary>The hourly rate a late/early (short-hours) minute is deducted at: the absence base over the month's standard hours.</summary>
    public static decimal ShortHoursHourlyRate(string baseChoice, decimal basic, decimal housing, decimal transport, decimal other, decimal standardMonthlyHours)
        => standardMonthlyHours > 0 ? AbsenceDeductionBaseAmount(baseChoice, basic, housing, transport, other) / standardMonthlyHours : 0m;

    /// <summary>An approved unpaid-leave request's deduction. On the default (basic) base it is the amount stored at approval;
    /// on the full-package base it is the days at the same day rate an absent day costs.</summary>
    public static decimal UnpaidLeaveDeduction(string baseChoice, decimal storedAmount, decimal days, decimal dayRate, string impactType)
        => baseChoice == AbsenceGross && impactType == UnpaidLeaveImpactType ? Math.Round(days * dayRate, 2) : storedAmount;

    public readonly record struct DayMinutesResult(int Overtime, int Undertime, bool EarnsCompensatoryDay);

    /// <summary>
    /// Overtime and undertime for one day. On an ordinary day, overtime is what exceeds the day's baseline and
    /// undertime what falls short of it. On a rest day or company holiday there is no baseline: with the premium
    /// mode every minute worked is overtime, and with the compensatory mode none is, because the work earns a day
    /// off instead.
    /// </summary>
    public static DayMinutesResult DayMinutes(int workedMinutes, int baselineMinutes, bool restDayOrHoliday, string mode)
    {
        workedMinutes = Math.Max(0, workedMinutes);
        if (!restDayOrHoliday)
            return new(Math.Max(0, workedMinutes - baselineMinutes), Math.Max(0, baselineMinutes - workedMinutes), false);
        return mode == RestDayWorkModes.CompensatoryDay
            ? new(0, 0, workedMinutes > 0)
            : new(workedMinutes, 0, false);
    }

    /// <summary>Today in UTC; the one clock the rules use for "now".</summary>
    public static DateOnly Today(DateTime? nowUtc = null) => DateOnly.FromDateTime(nowUtc ?? DateTime.UtcNow);

    private sealed record RuleRow(string Key, string Value, DateOnly From, CompanyPayrollRule Row);

    /// <summary>For each rule, the dated row in force on <paramref name="asOf"/>: the latest one starting on or before it.
    /// A cleared marker (empty value) means the platform default applies from that date, so it yields no value.</summary>
    private static Dictionary<string, RuleRow> InForce(IEnumerable<CompanyPayrollRule> rows, DateOnly asOf)
        => rows.Where(r => r.EffectiveFrom <= asOf)
            .GroupBy(r => r.RuleKey)
            .Select(g => g.OrderByDescending(r => r.EffectiveFrom).First())
            .Where(r => r.RuleValue.Length > 0)
            .ToDictionary(r => r.RuleKey, r => new RuleRow(r.RuleKey, r.RuleValue, r.EffectiveFrom, r));

    /// <summary>The company's effective rules on <paramref name="asOf"/> (default: today). A payroll period resolves them as of
    /// its first day, an attendance day as of that day, so a rule changed later never reprices a period already begun.
    /// Pass a null company for "no particular company" (platform defaults).</summary>
    public static async Task<CompanyPayrollRuleSet> ResolveAsync(
        ZayraDbContext db, Guid tenantId, Guid? companyId, CancellationToken ct, DateOnly? asOf = null)
    {
        if (companyId is null) return CompanyPayrollRuleSet.Defaults;
        var rows = await db.CompanyPayrollRules.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.CompanyId == companyId.Value)
            .ToListAsync(ct);
        return FromValues(InForce(rows, asOf ?? Today()).ToDictionary(r => r.Key, r => r.Value.Value));
    }

    public static CompanyPayrollRuleSet FromValues(IReadOnlyDictionary<string, string> v)
    {
        var d = CompanyPayrollRuleSet.Defaults;
        string Get(string key, string fallback) => v.TryGetValue(key, out var x) && Evaluate(key, x).Error is null ? x.Trim() : fallback;
        decimal? Dec(string key) => v.TryGetValue(key, out var x) && Evaluate(key, x).Error is null
            ? decimal.Parse(x.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture) : null;
        int? Int(string key) => v.TryGetValue(key, out var x) && Evaluate(key, x).Error is null
            ? int.Parse(x.Trim(), CultureInfo.InvariantCulture) : null;
        return new CompanyPayrollRuleSet(
            Get(PayrollRuleKeys.RestDayWorkMode, d.RestDayWorkMode),
            Dec(PayrollRuleKeys.RestDayMultiplier),
            Dec(PayrollRuleKeys.HolidayMultiplier),
            v.TryGetValue(PayrollRuleKeys.LeaveCashOutOnExit, out var c) && bool.TryParse(c?.Trim(), out var on) ? on : d.LeaveCashOutOnExit,
            Get(PayrollRuleKeys.LeaveCashOutBasis, d.LeaveCashOutBasis),
            Int(PayrollRuleKeys.MaxProbationDays),
            v.TryGetValue(PayrollRuleKeys.ProrationBasis, out var pb) && Evaluate(PayrollRuleKeys.ProrationBasis, pb).Error is null ? pb.Trim() : null,
            Get(PayrollRuleKeys.AbsenceDeductionBase, d.AbsenceDeductionBase));
    }

    /// <summary>The rules as shown on the settings screen on <paramref name="asOf"/> (default: today), with the next dated change if one is queued.</summary>
    public static async Task<IReadOnlyList<PayrollRuleView>> ListAsync(
        ZayraDbContext db, Guid tenantId, Guid companyId, CancellationToken ct, DateOnly? asOf = null)
    {
        var rows = await db.CompanyPayrollRules.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.CompanyId == companyId).ToListAsync(ct);
        var day = asOf ?? Today();
        var inForce = InForce(rows, day);
        return PayrollRuleKeys.All.Select(key =>
        {
            inForce.TryGetValue(key, out var current);
            var warning = current is null ? null : Evaluate(key, current.Value).Warning;
            var upcoming = rows.Where(r => r.RuleKey == key && r.EffectiveFrom > day).OrderBy(r => r.EffectiveFrom).FirstOrDefault();
            return new PayrollRuleView(key, current?.Value, current is not null, warning?.Code, warning?.Message,
                current?.Row.AcknowledgedAtUtc is not null, current?.Row.AcknowledgedAtUtc, current?.Row.AcknowledgedBy, current?.Row.AcknowledgementNote,
                current is { From.Year: > 1 } ? current.From : null,
                upcoming is { RuleValue.Length: > 0 } ? upcoming.RuleValue : null, upcoming?.EffectiveFrom, upcoming is not null);
        }).ToList();
    }

    /// <summary>The "key:CODE" strings for the warnings these changes raise. The acknowledgement must echo exactly these.</summary>
    public static IReadOnlyList<string> WarningCodesFor(IEnumerable<PayrollRuleChange> changes)
        => changes.Where(c => c.Value is not null)
            .Select(c => Evaluate(c.Key, c.Value).Warning)
            .Where(w => w is not null)
            .Select(w => $"{w!.Key}:{w.Code}")
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

    private static readonly Dictionary<string, int> CashOutBasisRank = new()
    {
        [BasisBasic] = 1, [BasisBasicHousing] = 2, [BasisGross] = 3,
    };

    /// <summary>
    /// Whether this change can lower anyone's pay and so needs a second person's approval. <paramref name="oldRaw"/> and
    /// <paramref name="newRaw"/> are the stored values, null meaning "no choice, the platform default". Anything that cannot
    /// be shown to be pay-neutral or pay-raising is treated as lowering: a multiplier set for the first time or cleared
    /// (the platform value behind it is not known here), and a change of part-month basis (it moves pay up or down by month).
    /// </summary>
    public static bool LowersPay(string key, string? oldRaw, string? newRaw)
    {
        var o = oldRaw?.Trim(); var n = newRaw?.Trim();
        if (string.IsNullOrEmpty(o)) o = null;
        if (string.IsNullOrEmpty(n)) n = null;
        if (o == n) return false;
        switch (key)
        {
            case PayrollRuleKeys.RestDayMultiplier:
            case PayrollRuleKeys.HolidayMultiplier:
                return !(o is not null && n is not null
                    && decimal.TryParse(o, NumberStyles.Number, CultureInfo.InvariantCulture, out var lo)
                    && decimal.TryParse(n, NumberStyles.Number, CultureInfo.InvariantCulture, out var hi) && hi > lo);
            case PayrollRuleKeys.LeaveCashOutOnExit:
                return (bool.TryParse(n ?? "true", out var newOn) ? newOn : true) == false
                    && (bool.TryParse(o ?? "true", out var oldOn) ? oldOn : true);
            case PayrollRuleKeys.LeaveCashOutBasis:
                return CashOutBasisRank.GetValueOrDefault(n ?? BasisGross) < CashOutBasisRank.GetValueOrDefault(o ?? BasisGross);
            case PayrollRuleKeys.AbsenceDeductionBase:
                return (n ?? AbsenceBasic) == AbsenceGross && (o ?? AbsenceBasic) == AbsenceBasic;
            case PayrollRuleKeys.RestDayWorkMode:
                return (n ?? RestDayWorkModes.PaidPremium) == RestDayWorkModes.CompensatoryDay
                    && (o ?? RestDayWorkModes.PaidPremium) == RestDayWorkModes.PaidPremium;
            case PayrollRuleKeys.ProrationBasis:
                return true;
            default:
                return false; // max_probation_days moves no pay; a value over the statutory limit is caught by its warning
        }
    }

    /// <summary>Validates a batch. Errors are meaningless values; warnings are values below a statutory minimum.</summary>
    public static (List<string> Errors, List<PayrollRuleWarning> Warnings) Validate(IEnumerable<PayrollRuleChange> changes)
    {
        var errors = new List<string>();
        var warnings = new List<PayrollRuleWarning>();
        var seen = new HashSet<string>();
        foreach (var c in changes)
        {
            if (!seen.Add(c.Key)) { errors.Add($"{c.Key} is listed twice."); continue; }
            if (c.Value is null)
            {
                if (!PayrollRuleKeys.All.Contains(c.Key)) errors.Add($"Unknown payroll rule '{c.Key}'.");
                continue;
            }
            var (error, warning) = Evaluate(c.Key, c.Value);
            if (error is not null) errors.Add(error);
            else if (warning is not null) warnings.Add(warning);
        }
        return (errors, warnings);
    }

    /// <summary>Whether at least one other active person can approve a pay-lowering change: holds <c>payroll.approve</c>,
    /// is not the maker, and has access to the company.</summary>
    public static Task<bool> HasEligibleApproverAsync(
        ZayraDbContext db, Guid tenantId, Guid companyId, Guid? makerUserId, CancellationToken ct)
        => (from user in db.Users.AsNoTracking()
            join assignment in db.UserRoles.AsNoTracking() on user.Id equals assignment.UserId
            join role in db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
            join rolePermission in db.RolePermissions.AsNoTracking() on role.Id equals rolePermission.RoleId
            join permission in db.Permissions.AsNoTracking() on rolePermission.PermissionId equals permission.Id
            where user.TenantId == tenantId && user.IsActive && !user.IsDeleted
                  && (makerUserId == null || user.Id != makerUserId)
                  && (role.TenantId == tenantId || role.TenantId == null) && role.IsActive && !role.IsDeleted
                  && permission.Key == "payroll.approve"
                  && (user.IsGroupScope || db.UserEntityAccesses.Any(x =>
                      x.TenantId == tenantId && x.UserId == user.Id && x.IsActive
                      && (x.CompanyId == companyId || x.GrantMode == "AllCurrentCompanies" || x.GrantMode == "AllCurrentAndFutureCompanies")))
            select user.Id).AnyAsync(ct);

    /// <summary>
    /// Saves a batch of rule changes for one company, effective from <paramref name="effectiveFrom"/> (default: today; never in
    /// the past). A null value clears the rule back to the platform default from that date. Any value below a statutory
    /// minimum is refused (without saving anything) unless <paramref name="acknowledge"/> is set, the reason is given and
    /// <paramref name="acknowledgedWarnings"/> echoes exactly the warnings raised; the acknowledgement is stored with
    /// <paramref name="userId"/> and the time. History is kept: a change adds a dated row, so a period that began before it is
    /// resolved against the earlier value.
    /// </summary>
    public static async Task<PayrollRuleSaveResult> SaveAsync(
        ZayraDbContext db, Guid tenantId, Guid companyId, Guid? userId,
        IReadOnlyList<PayrollRuleChange> changes, bool acknowledge, string? note, DateTime nowUtc, CancellationToken ct,
        DateOnly? effectiveFrom = null, IReadOnlyCollection<string>? acknowledgedWarnings = null)
    {
        var today = Today(nowUtc);
        var startDay = effectiveFrom ?? today;
        var (errors, warnings) = Validate(changes);
        if (startDay < today) errors.Add("A rule cannot start on a day that has already passed: choose today or a later date.");
        if (errors.Count > 0) return new PayrollRuleSaveResult(false, errors, Array.Empty<PayrollRuleWarning>());
        if (warnings.Count > 0 && !acknowledge) return new PayrollRuleSaveResult(false, errors, warnings);
        if (warnings.Count > 0 && (note?.Trim().Length ?? 0) < 5)
            return new PayrollRuleSaveResult(false, new[] { "A reason of at least 5 characters is required to confirm a value below the statutory minimum." }, Array.Empty<PayrollRuleWarning>());
        if (warnings.Count > 0)
        {
            var expected = WarningCodesFor(changes);
            var echoed = (acknowledgedWarnings ?? Array.Empty<string>()).OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (!expected.SequenceEqual(echoed))
                return new PayrollRuleSaveResult(false, new[] { "The acknowledgement does not match the warnings raised by these values. Review them and confirm again." }, warnings);
        }

        await ApplyAsync(db, tenantId, companyId, userId, changes, warnings.Select(w => w.Key).ToHashSet(), note, nowUtc, startDay, ct);
        return new PayrollRuleSaveResult(true, errors, Array.Empty<PayrollRuleWarning>());
    }

    /// <summary>Writes validated changes as dated rows. The warned keys are stamped with the acknowledging person.</summary>
    public static async Task ApplyAsync(
        ZayraDbContext db, Guid tenantId, Guid companyId, Guid? acknowledgedBy,
        IReadOnlyList<PayrollRuleChange> changes, ISet<string> warnedKeys, string? note, DateTime nowUtc, DateOnly effectiveFrom, CancellationToken ct)
    {
        var existing = await db.CompanyPayrollRules
            .Where(r => r.TenantId == tenantId && r.CompanyId == companyId).ToListAsync(ct);
        foreach (var c in changes)
        {
            var row = existing.FirstOrDefault(r => r.RuleKey == c.Key && r.EffectiveFrom == effectiveFrom);
            if (c.Value is null && row is null && !existing.Any(r => r.RuleKey == c.Key)) continue; // nothing to clear
            var warned = c.Value is not null && warnedKeys.Contains(c.Key);
            if (row is null)
            {
                row = new CompanyPayrollRule { TenantId = tenantId, CompanyId = companyId, RuleKey = c.Key, EffectiveFrom = effectiveFrom };
                db.CompanyPayrollRules.Add(row);
                existing.Add(row);
            }
            row.RuleValue = c.Value?.Trim() ?? string.Empty;
            row.UpdatedBy = acknowledgedBy;
            row.UpdatedAtUtc = nowUtc;
            row.AcknowledgedBy = warned ? acknowledgedBy : null;
            row.AcknowledgedAtUtc = warned ? nowUtc : null;
            row.AcknowledgementNote = warned ? note?.Trim() : null;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Dates of the non-optional public holidays in the window that apply to this company: from an active calendar
    /// that is company-wide or the company's own. Attendance and payroll both read holidays through this one query.</summary>
    public static async Task<HashSet<DateOnly>> HolidayDatesAsync(
        ZayraDbContext db, Guid tenantId, Guid? companyId, DateOnly windowFrom, DateOnly windowTo, CancellationToken ct)
        // Tenant-wide with the company named in the predicate: attendance runs on the anonymous device webhook, whose company
        // scope is empty by design, so the ambient company filter would hide the very calendar being asked about.
        => (await (from h in db.PublicHolidays.AsNoTracking()
                   join c in Zayra.Api.Infrastructure.Data.ScopedBypass.TenantWide(db.PublicHolidayCalendars, tenantId,
                       "Holiday detection for attendance processing on the anonymous device webhook: the employing company is named in the predicate and the tenant is re-applied.").AsNoTracking()
                       on h.CalendarId equals c.Id
                   where h.TenantId == tenantId && c.IsActive && !h.IsOptional && h.Date >= windowFrom && h.Date <= windowTo
                         && (c.CompanyId == null || c.CompanyId == companyId)
                   select h.Date).ToListAsync(ct)).ToHashSet();
}
