using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// Why a run did not pay someone in its company scope (the "left out" list), and the warnings on people it DOES pay that
/// Finance must acknowledge before Approve. Stable machine codes: the screen translates them (EN/AR) and the run stores
/// them as validation findings, so a code is never renamed.
/// </summary>
public static class PayPopulationReasons
{
    // ── Left out: not paid by this run ──────────────────────────────────────────────────────────────
    /// <summary>Draft, Invited or Pre-boarding with a salary, or joins after the period ends.</summary>
    public const string NotYetEmployed = "NOT_YET_EMPLOYED";
    /// <summary>The separation date (offboarding last working day, or the exit status's effective date) is before the period.</summary>
    public const string LeftBeforePeriod = "LEFT_BEFORE_PERIOD";
    /// <summary>A non-voided final-wage-month payslip already exists: the final wage is paid exactly once.</summary>
    public const string FinalWageAlreadyPaid = "FINAL_WAGE_ALREADY_PAID";
    /// <summary>Suspended: still employed, but pay is on hold.</summary>
    public const string SuspendedPayHold = "SUSPENDED_PAY_HOLD";
    /// <summary>Inactive, Archived or Blacklisted with no separation date in or after the period.</summary>
    public const string InactiveOrArchived = "INACTIVE_OR_ARCHIVED";
    /// <summary>Data quality: an exit status (Offboarded, Terminated, Exited, Retired, Deceased, Absconded) with no date anywhere.</summary>
    public const string ExitStatusNoDate = "EXIT_STATUS_NO_DATE";
    /// <summary>Soft-deleted, with no offboarding or settlement that leaves wages owed.</summary>
    public const string DeletedNoWagesOwed = "DELETED_NO_WAGES_OWED";
    /// <summary>A status this product does not recognise: not paid until HR corrects it.</summary>
    public const string UnknownStatus = "UNKNOWN_STATUS";
    /// <summary>Deliberately excluded by the run's include/exclude selection.</summary>
    public const string ExcludedFromRun = "EXCLUDED_FROM_RUN";
    /// <summary>Named in the run's selection but not eligible for it.</summary>
    public const string SelectionNotEligible = "SELECTION_NOT_ELIGIBLE";

    // ── Paid, with a warning Finance must acknowledge before Approve ───────────────────────────────
    /// <summary>Resigned / Notice period with no last working day: paid in full for the month.</summary>
    public const string WarnNoLastWorkingDay = "WARN_NO_LAST_WORKING_DAY";
    /// <summary>Status "On leave" with no approved leave covering any day of the period: paid in full.</summary>
    public const string WarnOnLeaveNoLeaveRequest = "WARN_STATUS_ON_LEAVE_NO_LEAVE_REQUEST";
    /// <summary>Soft-deleted but still owed wages through the last working day: paid.</summary>
    public const string WarnDeletedButWagesOwed = "DELETED_BUT_WAGES_OWED";

    /// <summary>
    /// The status says employed (Active, Probation, Confirmed, On leave) but the run does not pay them: a last working day
    /// before the period with no recorded reactivation, or a final wage month already paid. Left out, and counted in the
    /// Approve acknowledgement, never shown as routine.
    /// </summary>
    public const string WarnEmployedStatusNotPaid = "WARN_EMPLOYED_STATUS_NOT_PAID";

    /// <summary>
    /// /validate: a payslip on this run was computed under a standing the employee no longer has (no longer paid by the
    /// run, or their final wage month changed). A blocking Error: re-process the run before approving.
    /// </summary>
    public const string StandingChangedReprocess = "PAY_STANDING_CHANGED_REPROCESS";

    /// <summary>The codes Approve requires an acknowledged count for: paid with a warning, or employed but not paid.</summary>
    public static readonly string[] AcknowledgeBeforeApprove =
        [WarnNoLastWorkingDay, WarnOnLeaveNoLeaveRequest, WarnDeletedButWagesOwed, WarnEmployedStatusNotPaid];

    /// <summary>Left-out codes that are routine (shown as a count, never omitted); the rest are raised as warnings.</summary>
    public static readonly string[] Routine = [NotYetEmployed, LeftBeforePeriod, FinalWageAlreadyPaid, InactiveOrArchived];

    public static readonly string[] LeftOut =
        [NotYetEmployed, LeftBeforePeriod, FinalWageAlreadyPaid, SuspendedPayHold, InactiveOrArchived, ExitStatusNoDate, DeletedNoWagesOwed, UnknownStatus,
         WarnEmployedStatusNotPaid];
}

/// <summary>One employee the run leaves out, or pays with a warning.</summary>
public sealed record PayPopulationEntry(int EmployeeId, string EmployeeCode, string EmployeeName, string Status, string Code, string Message, DateOnly? Date = null);

/// <summary>What the population loader decided beyond "who is paid".</summary>
public sealed class PayPopulationReport
{
    public List<PayPopulationEntry> LeftOut { get; } = new();
    public List<PayPopulationEntry> PayWarnings { get; } = new();
}

/// <summary>Turns the population report into the run's stored findings (Process writes them; /validate re-adds them).</summary>
public static class PayPopulationFindings
{
    /// <param name="paidEmployeeIds">Who the run actually pays (Process: its population; /validate: the run's payslips): a
    /// paid-with-warning entry is only about someone paid.</param>
    public static IEnumerable<PayrollValidationResult> ToResults(Guid tenantId, Guid runId, PayPopulationReport report, IReadOnlySet<int> paidEmployeeIds)
    {
        foreach (var x in report.LeftOut)
            yield return new PayrollValidationResult
            {
                TenantId = tenantId, PayrollRunId = runId, EmployeeId = x.EmployeeId,
                Severity = PayPopulationReasons.Routine.Contains(x.Code) ? "Info" : "Warning",
                Code = x.Code,
                Message = $"{x.EmployeeName} ({x.EmployeeCode}) is not paid by this run: {x.Message}"
                          + (PayPopulationReasons.AcknowledgeBeforeApprove.Contains(x.Code) ? " Acknowledge it when approving the run." : string.Empty),
            };
        foreach (var x in report.PayWarnings.Where(w => paidEmployeeIds.Contains(w.EmployeeId)))
            yield return new PayrollValidationResult
            {
                TenantId = tenantId, PayrollRunId = runId, EmployeeId = x.EmployeeId,
                Severity = "Warning",
                Code = x.Code,
                Message = $"{x.EmployeeName} ({x.EmployeeCode}): {x.Message} Acknowledge it when approving the run.",
            };
    }
}
