namespace Zayra.Api.Application.Attendance;

/// <summary>
/// THE attendance-day vocabulary, and THE definition of what an attendance rate divides by.
///
/// <para><b>Why this type exists.</b> The strings below were spelled inline at every call site and had
/// drifted into three mutually inconsistent readings of the same day:</para>
/// <list type="number">
/// <item>The dashboard trend divided <c>Status == "Present"</c> by EVERY attendance row in the month,
///   so a rest day, an approved leave day and a public holiday each counted against the employee as
///   though they had failed to turn up — and a <c>"Late"</c> day, on which they DID turn up, counted
///   against them too. Evostel's dashboard read 69.9% where the true figure is 87.1%.</item>
/// <item>The attendance module's own dashboard counted <c>"Present" or "Late" or "Half day"</c> as
///   attended, so the same day produced two different answers on two screens.</item>
/// <item>The summary's on-leave tile compared against <c>"Leave"</c> and <c>"On Leave"</c> while the
///   processor writes <c>"On leave"</c> (lower-case L), so the tile was structurally always zero on
///   processed data. The one test covering it seeded <c>"On Leave"</c> by hand and passed.</item>
/// </list>
///
/// <para><b>Both leave spellings are live in production data</b> and neither can be retired without a
/// backfill: the processor writes <see cref="OnLeave"/>, while older seeded rows carry
/// <see cref="LeaveLegacy"/>. <see cref="IsOnLeave"/> and <see cref="IsScheduledWorkingDay"/> therefore
/// accept both, deliberately, rather than pretending the data is uniform.</para>
/// </summary>
public static class AttendanceStatuses
{
    public const string Present       = "Present";
    public const string Late          = "Late";
    public const string HalfDay       = "Half day";
    public const string Absent        = "Absent";

    /// <summary>What <c>AttendanceService.ProcessEmployeeDay</c> writes for an approved-leave day.</summary>
    public const string OnLeave       = "On leave";
    /// <summary>The older seeded spelling. Still present in live tenant data; read, never written.</summary>
    public const string LeaveLegacy   = "Leave";
    /// <summary>A third spelling the dashboard used to compare against. Read-only, for the same reason.</summary>
    public const string OnLeaveTitle  = "On Leave";

    public const string PublicHoliday = "Public holiday";
    public const string RestDay       = "Rest day";

    /// <summary>
    /// The day was worked. <b>This is the numerator of every attendance rate.</b> A late arrival and a
    /// half day are attendance — they are penalised through the late/short-hours deduction, not by
    /// being counted as an absence a second time. Matches the predicate the attendance module's own
    /// dashboard has always used, so the two surfaces now answer alike.
    /// </summary>
    public static bool IsAttended(string? status) =>
        status is Present or Late or HalfDay;

    /// <summary>True for either spelling of an approved-leave day.</summary>
    public static bool IsOnLeave(string? status) =>
        status is OnLeave or LeaveLegacy or OnLeaveTitle;

    /// <summary>
    /// The employee was rostered to work that day. <b>This is the denominator of every attendance
    /// rate.</b> A rest day, a public holiday and an approved leave day are days on which no attendance
    /// was ever owed, so dividing by them measures the calendar, not the employee.
    /// </summary>
    public static bool IsScheduledWorkingDay(string? status) =>
        status is not (RestDay or PublicHoliday or OnLeave or LeaveLegacy or OnLeaveTitle);
}

/// <summary>
/// THE <c>AttendancePayrollImpact.ImpactType</c> vocabulary, exactly as
/// <c>AttendanceService.UpsertImpacts</c> writes it.
///
/// <para>These strings were compared by <c>Contains</c> against substrings, which is how an absent day
/// came to be charged twice: <see cref="AbsenceDeduction"/> contains the word "deduction", so it fell
/// into the late/early short-hours bucket as well as the loss-of-pay bucket. Payroll still matches by
/// substring for backwards compatibility with rows written before the vocabulary settled, but anything
/// that means "this exact impact type" must use a constant from here.</para>
/// </summary>
public static class AttendanceImpactTypes
{
    public const string LateDeduction      = "Late deduction";
    public const string EarlyExitDeduction = "Early exit deduction";
    public const string AbsenceDeduction   = "Absence deduction";

    /// <summary>
    /// Attendance-derived overtime. <b>Nothing pays this.</b> Overtime money comes from
    /// <c>OvertimePayrollImpact</c>, raised when an overtime REQUEST is approved; the only bridge from
    /// attendance is the explicit <c>POST /api/overtime/detect-from-attendance</c>. A payroll run must
    /// therefore not consume rows of this type — see the load query in <c>PayrollController</c>.
    /// </summary>
    public const string OvertimePayable    = "Overtime payable";
}
