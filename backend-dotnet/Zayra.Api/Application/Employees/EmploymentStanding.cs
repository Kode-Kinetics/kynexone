using Zayra.Api.Models;

namespace Zayra.Api.Application.Employees;

/// <summary>What an employee status says about employment, before any date is looked at.</summary>
public enum EmploymentStatusClass
{
    /// <summary>Draft, Invited, Pre-boarding: not employed yet.</summary>
    PreEmployment,
    /// <summary>Active, Probation, Confirmed, On leave: employed, paid.</summary>
    Employed,
    /// <summary>Suspended: employed, occupies the seat, but pay is on hold.</summary>
    PayHold,
    /// <summary>Resigned, Notice period: employed until the offboarding's last working day; a status date is NOT one.</summary>
    Notice,
    /// <summary>Offboarded, Terminated, Exited, Retired, Deceased, Absconded: employed until the separation date; none known = not employed.</summary>
    Exit,
    /// <summary>Inactive, Archived, Blacklisted: not employed unless a separation date in the future says otherwise.</summary>
    Inactive,
    /// <summary>A status this product does not know. Never treated as employed by guesswork.</summary>
    Unknown,
}

/// <summary>The dated facts the predicate needs. Dates are the employee's own; nothing here is "today".</summary>
/// <param name="OffboardingLastWorkingDay">The newest non-cancelled offboarding's last working day, if any.</param>
/// <param name="StatusExitDate">The effective date of the status change into the current status, if recorded.</param>
public sealed record EmploymentFacts(string? Status, DateOnly? JoiningDate, DateOnly? OffboardingLastWorkingDay, DateOnly? StatusExitDate);

/// <summary>
/// THE one employment predicate (payroll pay population spec, section 1.3): who is employed on a date. Payroll, the
/// establishment occupancy and the headcount trend must all answer from it, so a label like "Probation" or "Resigned"
/// can never make someone vanish from pay while they are still employed.
///
/// <para>Employed on day D when: the status is not pre-employment, the joining date is on or before D, and D is on or
/// before the separation date. The separation date is the newest non-cancelled offboarding's last working day (one before
/// the current joining date belongs to an earlier service period and is ignored); else, for an exit or inactive status,
/// the effective date of the status change; else there is none. Without a separation date an exit or inactive status is
/// NOT employed (and payroll reports it), while every other status is.</para>
///
/// <para>Being paid is a second step: employed AND not on a pay hold (<see cref="IsOnPayHold"/>, today Suspended).</para>
/// </summary>
public static class EmploymentStanding
{
    private static readonly Dictionary<string, EmploymentStatusClass> Classes = new(StringComparer.Ordinal)
    {
        [Key(EmployeeStatuses.Draft)] = EmploymentStatusClass.PreEmployment,
        [Key(EmployeeStatuses.Invited)] = EmploymentStatusClass.PreEmployment,
        [Key("Pre-boarding")] = EmploymentStatusClass.PreEmployment,
        [Key(EmployeeStatuses.Active)] = EmploymentStatusClass.Employed,
        [Key("Probation")] = EmploymentStatusClass.Employed,
        [Key("Confirmed")] = EmploymentStatusClass.Employed,
        [Key("On leave")] = EmploymentStatusClass.Employed,
        [Key(EmployeeStatuses.Suspended)] = EmploymentStatusClass.PayHold,
        [Key("Resigned")] = EmploymentStatusClass.Notice,
        [Key("Notice period")] = EmploymentStatusClass.Notice,
        [Key(EmployeeStatuses.Offboarded)] = EmploymentStatusClass.Exit,
        [Key(EmployeeStatuses.Terminated)] = EmploymentStatusClass.Exit,
        [Key(EmployeeStatuses.Exited)] = EmploymentStatusClass.Exit,
        [Key("Retired")] = EmploymentStatusClass.Exit,
        [Key("Deceased")] = EmploymentStatusClass.Exit,
        [Key("Absconded")] = EmploymentStatusClass.Exit,
        [Key(EmployeeStatuses.Inactive)] = EmploymentStatusClass.Inactive,
        [Key(EmployeeStatuses.Archived)] = EmploymentStatusClass.Inactive,
        [Key("Blacklisted")] = EmploymentStatusClass.Inactive,
    };

    /// <summary>Case, spaces, hyphens and underscores do not change a status ("Pre-boarding" = "PreBoarding" = "pre boarding").</summary>
    private static string Key(string? status) =>
        new((status ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public static EmploymentStatusClass Classify(string? status) =>
        Classes.TryGetValue(Key(status), out var c) ? c : EmploymentStatusClass.Unknown;

    /// <summary>Every status string the predicate knows for <paramref name="cls"/>, in its canonical spelling.</summary>
    public static IReadOnlyList<string> StatusesIn(EmploymentStatusClass cls) => cls switch
    {
        EmploymentStatusClass.PreEmployment => [EmployeeStatuses.Draft, EmployeeStatuses.Invited, "Pre-boarding"],
        EmploymentStatusClass.Employed => [EmployeeStatuses.Active, "Probation", "Confirmed", "On leave"],
        EmploymentStatusClass.PayHold => [EmployeeStatuses.Suspended],
        EmploymentStatusClass.Notice => ["Resigned", "Notice period"],
        EmploymentStatusClass.Exit => [EmployeeStatuses.Offboarded, EmployeeStatuses.Terminated, EmployeeStatuses.Exited, "Retired", "Deceased", "Absconded"],
        EmploymentStatusClass.Inactive => [EmployeeStatuses.Inactive, EmployeeStatuses.Archived, "Blacklisted"],
        _ => [],
    };

    /// <summary>The day employment ends (inclusive), or null when none is recorded for the current service period.</summary>
    public static DateOnly? SeparationDate(EmploymentFacts f)
    {
        if (f.OffboardingLastWorkingDay is DateOnly lwd && (f.JoiningDate is not DateOnly j1 || lwd >= j1)) return lwd;
        var cls = Classify(f.Status);
        if (cls is EmploymentStatusClass.Exit or EmploymentStatusClass.Inactive
            && f.StatusExitDate is DateOnly exit && (f.JoiningDate is not DateOnly j2 || exit >= j2)) return exit;
        return null;
    }

    public static bool IsEmployedOn(EmploymentFacts f, DateOnly day)
    {
        var cls = Classify(f.Status);
        if (cls is EmploymentStatusClass.PreEmployment or EmploymentStatusClass.Unknown) return false;
        if (f.JoiningDate is DateOnly joined && joined > day) return false;
        if (SeparationDate(f) is DateOnly sep) return day <= sep;
        return cls is EmploymentStatusClass.Employed or EmploymentStatusClass.PayHold or EmploymentStatusClass.Notice;
    }

    /// <summary>Employed on at least one day of [<paramref name="start"/>, <paramref name="end"/>] (employment is one interval).</summary>
    public static bool IsEmployedDuring(EmploymentFacts f, DateOnly start, DateOnly end)
    {
        var first = f.JoiningDate is DateOnly joined && joined > start ? joined : start;
        return first <= end && IsEmployedOn(f, first);
    }

    /// <summary>Employed, but pay is held (today: Suspended).</summary>
    public static bool IsOnPayHold(EmploymentFacts f) => Classify(f.Status) == EmploymentStatusClass.PayHold;

    /// <summary>Resigned / Notice period with no last working day: paid in full until HR records one.</summary>
    public static bool NeedsLastWorkingDay(EmploymentFacts f) =>
        Classify(f.Status) == EmploymentStatusClass.Notice && SeparationDate(f) is null;

    /// <summary>The hand-picked "On leave" status (not derived from leave dates).</summary>
    public static bool IsOnLeaveLabel(string? status) => Key(status) == Key("On leave");
}
