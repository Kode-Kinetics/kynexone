using System.ComponentModel.DataAnnotations;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;

namespace Zayra.Api.Application.Timesheets;

// ══ Read models ════════════════════════════════════════════════════════════════════════════════

public sealed record TimesheetEntryDto(
    Guid Id, DateOnly WorkDate, Guid? CostCenterId, string? CostCenterCode, string? CostCenterName,
    int Minutes, string Notes);

/// <summary>One day of the period: what was logged against what attendance recorded.</summary>
public sealed record TimesheetDayDto(
    DateOnly Date,
    int LoggedMinutes,
    int? AttendanceMinutes,
    string AttendanceStatus,
    int? VarianceMinutes,
    bool IsOverAllocated);

/// <summary>Where the submitted timesheet currently sits in the approval engine.</summary>
public sealed record TimesheetApprovalDto(
    Guid ApprovalRequestId, string Status, int CurrentStepOrder,
    string CurrentApproverName, string CurrentApproverRole, DateTime? DueAtUtc);

public sealed record TimesheetDto(
    Guid Id, int EmployeeId, string EmployeeName, Guid? CompanyId,
    DateOnly PeriodStart, DateOnly PeriodEnd, string Status, int TotalMinutes, bool IsEditable,
    DateTime? SubmittedAtUtc, DateTime? DecidedAtUtc, string? DecisionComments,
    Guid? ApprovalRequestId, int Version, DateTime CreatedAtUtc, DateTime? UpdatedAtUtc,
    IReadOnlyList<TimesheetEntryDto> Entries,
    IReadOnlyList<TimesheetDayDto> Days,
    TimesheetApprovalDto? Approval);

public sealed record TimesheetSummaryDto(
    Guid Id, int EmployeeId, string EmployeeName, Guid? CompanyId,
    DateOnly PeriodStart, DateOnly PeriodEnd, string Status, int TotalMinutes,
    DateTime? SubmittedAtUtc, DateTime? DecidedAtUtc, Guid? ApprovalRequestId);

/// <summary>A persisted reconciliation row, as the HR variance report serves it.</summary>
public sealed record TimesheetVarianceRowDto(
    Guid TimesheetId, int EmployeeId, string EmployeeName, DateOnly WorkDate,
    int LoggedMinutes, int? AttendanceMinutes, int? VarianceMinutes,
    string AttendanceStatus, bool IsOverAllocated);

// ══ Write models ═══════════════════════════════════════════════════════════════════════════════

public sealed record TimesheetEntryRequest(
    DateOnly WorkDate,
    Guid? CostCenterId,
    [Range(0, TimesheetConstantsBridge.MaxMinutesPerDay)] int Minutes,
    [MaxLength(500)] string? Notes);

/// <summary>Replaces every entry on the timesheet. Zero-minute rows are dropped.</summary>
public sealed record SaveTimesheetEntriesRequest(
    IReadOnlyList<TimesheetEntryRequest> Entries,
    /// <summary>The Version the client last read. Omit to skip the concurrency check.</summary>
    int? Version = null);

public sealed record TimesheetQuery(
    string? Status = null, int? EmployeeId = null, DateOnly? From = null, DateOnly? To = null,
    string? Search = null, int Page = 1, int PageSize = 25);

// DataAnnotations Range needs a compile-time constant; Models is a different assembly-internal
// namespace and referencing it from an attribute argument here would be a cycle in readability,
// not in compilation. This bridge keeps the number in one place.
internal static class TimesheetConstantsBridge
{
    public const int MaxMinutesPerDay = 24 * 60;
}

// ══ Failures ═══════════════════════════════════════════════════════════════════════════════════

public sealed record TimesheetViolation(DateOnly? Date, string Code, string Message);

/// <summary>
/// The submission (or save) is refused. Carries every problem at once — a form that reveals its
/// objections one at a time is a form people give up on.
/// </summary>
public sealed class TimesheetValidationException : Exception
{
    public IReadOnlyList<TimesheetViolation> Violations { get; }

    public TimesheetValidationException(IReadOnlyList<TimesheetViolation> violations)
        : base(violations.Count == 1
            ? violations[0].Message
            : $"{violations.Count} problems stop this timesheet.")
        => Violations = violations;

    public TimesheetValidationException(string code, string message)
        : this(new[] { new TimesheetViolation(null, code, message) }) { }
}

public sealed class TimesheetNotFoundException : Exception
{
    public TimesheetNotFoundException(string message) : base(message) { }
}

public sealed class TimesheetConflictException : Exception
{
    public TimesheetConflictException(string message, Exception? inner = null) : base(message, inner) { }
}

// ══ Service ════════════════════════════════════════════════════════════════════════════════════

public interface ITimesheetService
{
    // ── Employee self-service. Every call is pinned to the caller's own employee id. ──
    Task<TimesheetDto> GetOrCreateOwnAsync(Guid tenantId, int employeeId, DateOnly? anyDateInPeriod, CancellationToken ct);
    Task<IReadOnlyList<TimesheetSummaryDto>> ListOwnAsync(Guid tenantId, int employeeId, int count, CancellationToken ct);
    Task<TimesheetDto> SaveOwnEntriesAsync(Guid tenantId, int employeeId, Guid timesheetId, SaveTimesheetEntriesRequest request, Guid? userId, CancellationToken ct);
    Task<TimesheetDto> SubmitOwnAsync(Guid tenantId, int employeeId, Guid timesheetId, RequestContext context, CancellationToken ct);

    // ── Managers / HR. ──
    Task<PagedResult<TimesheetSummaryDto>> ListAsync(Guid tenantId, TimesheetQuery query, CancellationToken ct);
    Task<TimesheetDto?> GetAsync(Guid tenantId, Guid timesheetId, CancellationToken ct);
    Task<IReadOnlyList<TimesheetVarianceRowDto>> GetAttendanceVarianceAsync(Guid tenantId, DateOnly from, DateOnly to, int? employeeId, bool overAllocatedOnly, CancellationToken ct);
}
