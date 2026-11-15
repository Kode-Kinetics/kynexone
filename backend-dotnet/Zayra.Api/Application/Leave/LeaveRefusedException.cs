namespace Zayra.Api.Application.Leave;

/// <summary>
/// A leave request the rules refuse, with a stable <see cref="Code"/> the web and mobile clients translate (EN/AR) and the
/// values the sentence needs (<see cref="Args"/>, e.g. <c>days</c>, <c>year</c>, <c>leaveType</c>). <see cref="Exception.Message"/>
/// stays the English sentence, so every existing <c>catch (InvalidOperationException)</c> keeps working and an older client
/// that only reads <c>message</c> sees exactly what it saw before.
/// Codes are part of the API contract: add new ones, never rename or reuse one. The catalogue is <see cref="LeaveRefusalCodes"/>.
/// </summary>
public sealed class LeaveRefusedException : InvalidOperationException
{
    private static readonly IReadOnlyDictionary<string, string> NoArgs = new Dictionary<string, string>();

    public LeaveRefusedException(string code, string message, IReadOnlyDictionary<string, string>? args = null) : base(message)
    {
        Code = code;
        Args = args ?? NoArgs;
    }

    public string Code { get; }
    public IReadOnlyDictionary<string, string> Args { get; }

    /// <summary>The args map, each value culture-invariant (a decimal as <c>0.##</c>), so clients parse the same text everywhere.</summary>
    public static IReadOnlyDictionary<string, string> ArgsOf(params (string Key, object Value)[] pairs)
    {
        var map = new Dictionary<string, string>(pairs.Length);
        foreach (var (key, value) in pairs)
            map[key] = value switch
            {
                decimal d => d.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                _ => value?.ToString() ?? string.Empty,
            };
        return map;
    }

    /// <summary>
    /// The args for a refusal that names a leave type: <c>leaveType</c> (the English name, which the English message uses) and,
    /// when the type has an Arabic name, <c>leaveTypeAr</c> so an Arabic client can name it in Arabic. Left out when blank,
    /// so a client falls back to the English name.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ArgsWithLeaveType(string nameEn, string? nameAr, params (string Key, object Value)[] pairs)
    {
        var map = new Dictionary<string, string>(ArgsOf(pairs)) { ["leaveType"] = nameEn };
        if (!string.IsNullOrWhiteSpace(nameAr)) map["leaveTypeAr"] = nameAr.Trim();
        return map;
    }
}

/// <summary>The leave refusal codes. Clients map each to a translated sentence; an unknown code falls back to the English message.</summary>
public static class LeaveRefusalCodes
{
    public const string Overlap = "leave_overlap";
    public const string NoticeRequired = "leave_notice_required";                 // args: days
    public const string InsufficientBalance = "leave_insufficient_balance";       // args: year
    public const string BalanceNotSetUp = "leave_balance_not_set_up";             // args: year, leaveType
    public const string Probation = "leave_probation";
    public const string MinServiceMonths = "leave_min_service_months";            // args: months
    public const string MaxConsecutiveDays = "leave_max_consecutive_days";        // args: days
    public const string MinDaysPerRequest = "leave_min_days";                     // args: days
    public const string MaxDaysPerRequest = "leave_max_days";                     // args: days
    public const string ReasonRequired = "leave_reason_required";
    public const string AttachmentRequired = "leave_attachment_required";
    public const string AttachmentNotFound = "leave_attachment_not_found";
    public const string HalfDayNotAllowed = "leave_half_day_not_allowed";
    public const string HourlyNotAllowed = "leave_hourly_not_allowed";
    public const string Blackout = "leave_blackout";
    public const string TypeUnavailable = "leave_type_unavailable";
    public const string StatutoryLimit = "leave_statutory_limit";                 // args: days (when known)
    public const string NotEligible = "leave_not_eligible";                       // args: leaveType, leaveTypeAr
    public const string InvalidDates = "leave_invalid_dates";
    public const string Other = "leave_refused";                                  // a refusal with no dedicated sentence yet
}
