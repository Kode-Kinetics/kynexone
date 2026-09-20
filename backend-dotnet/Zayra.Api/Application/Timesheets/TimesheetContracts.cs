using System.ComponentModel.DataAnnotations;
using Zayra.Api.Application.Common;

namespace Zayra.Api.Application.Timesheets;

// ══ Settings ═══════════════════════════════════════════════════════════════════════════════════

/// <summary>Tenant-level timesheet settings (SystemSettings, Category = "Timesheets").</summary>
public sealed record TimesheetSettingsDto(
    /// <summary>Weekly | Monthly.</summary>
    string PeriodType,
    /// <summary>First day of a weekly period (ignored for Monthly). GCC default: Sunday.</summary>
    DayOfWeek WeekStartDay,
    /// <summary>Minutes an employee may log above attendance before the day is flagged.</summary>
    int OverAllocationToleranceMinutes,
    /// <summary>true ⇒ an over-allocated day blocks submit; false ⇒ it only warns.</summary>
    bool OverAllocationBlocksSubmit,
    /// <summary>Available minutes per working day, used by the utilisation report.</summary>
    int StandardDailyMinutes);

public sealed record TimesheetSettingsRequest(
    string? PeriodType,
    DayOfWeek? WeekStartDay,
    [Range(0, 1440)] int? OverAllocationToleranceMinutes,
    bool? OverAllocationBlocksSubmit,
    [Range(60, 1440)] int? StandardDailyMinutes);

// ══ Projects ═══════════════════════════════════════════════════════════════════════════════════

public sealed record ProjectTaskDto(Guid Id, string Name, string TaskTypeCode, bool IsBillable, bool IsActive, int SortOrder);

public sealed record ProjectAssignmentDto(
    Guid Id, int EmployeeId, string EmployeeName, string EmployeeCode, decimal? AllocationPercent,
    DateOnly? StartDate, DateOnly? EndDate, bool IsActive);

public sealed record ProjectDto(
    Guid Id, string Code, string Name, string ClientName, Guid? CompanyId, Guid? CostCenterId, string? CostCenterName,
    string Status, decimal? BudgetHours, bool IsBillable, string Description, DateOnly? StartDate, DateOnly? EndDate,
    int LoggedMinutes, int AssignedEmployees, DateTime CreatedAtUtc,
    IReadOnlyList<ProjectTaskDto> Tasks, IReadOnlyList<ProjectAssignmentDto> Assignments);

public sealed record SaveProjectRequest(
    [Required, MaxLength(40)] string Code,
    [Required, MaxLength(200)] string Name,
    [MaxLength(200)] string? ClientName,
    Guid? CompanyId,
    Guid? CostCenterId,
    string? Status,
    [Range(0, 1_000_000)] decimal? BudgetHours,
    bool IsBillable = true,
    [MaxLength(2000)] string? Description = null,
    DateOnly? StartDate = null,
    DateOnly? EndDate = null);

public sealed record SaveProjectTaskRequest(
    [Required, MaxLength(200)] string Name,
    [MaxLength(80)] string? TaskTypeCode,
    bool IsBillable = true,
    bool IsActive = true,
    int SortOrder = 0);

public sealed record SaveProjectAssignmentRequest(
    int EmployeeId,
    [Range(0, 100)] decimal? AllocationPercent,
    DateOnly? StartDate = null,
    DateOnly? EndDate = null,
    bool IsActive = true);

public sealed record ProjectQuery(string? Status = null, Guid? CompanyId = null, string? Search = null, bool IncludeClosed = true);

// ══ Timesheets ═════════════════════════════════════════════════════════════════════════════════

/// <summary>A project the employee may log to, with its active tasks.</summary>
public sealed record AssignedProjectDto(
    Guid Id, string Code, string Name, string ClientName, bool IsBillable, decimal? AllocationPercent,
    IReadOnlyList<ProjectTaskDto> Tasks);

public sealed record TimesheetEntryDto(
    Guid Id, DateOnly WorkDate, Guid ProjectId, string ProjectCode, string ProjectName, Guid? ProjectTaskId, string? TaskName,
    int Minutes, bool IsBillable, string Notes);

/// <summary>One day of the period: logged vs attendance.</summary>
public sealed record TimesheetDayDto(
    DateOnly Date,
    bool IsWorkingDay,
    int LoggedMinutes,
    int BillableMinutes,
    /// <summary>AttendanceDailyRecord.TotalWorkedMinutes, or null when no record exists for the day.</summary>
    int? AttendanceMinutes,
    string? AttendanceStatus,
    /// <summary>logged − attendance (only when attendance exists).</summary>
    int? VarianceMinutes,
    /// <summary>logged &gt; attendance + tolerance.</summary>
    bool IsOverAllocated);

public sealed record TimesheetApprovalProgressDto(
    Guid ApprovalRequestId, string Status, int CurrentStepOrder, string CurrentApproverName, string CurrentApproverRole,
    DateTime? DueAtUtc, bool CanDecide);

public sealed record TimesheetReconciliationDto(
    int LoggedMinutes,
    int BillableMinutes,
    /// <summary>Sum of TotalWorkedMinutes over the days that have an attendance record.</summary>
    int AttendanceMinutes,
    int DaysWithAttendance,
    int OverAllocatedDays,
    int ToleranceMinutes,
    bool BlocksSubmit,
    IReadOnlyList<TimesheetDayDto> Days);

public sealed record TimesheetDto(
    Guid Id, int EmployeeId, string EmployeeName, Guid? CompanyId, string PeriodType, DateOnly PeriodStart, DateOnly PeriodEnd,
    string Status, int TotalMinutes, int BillableMinutes, bool IsEditable,
    DateTime? SubmittedAtUtc, DateTime? DecidedAtUtc, string? DecisionComments, DateTime? LockedAtUtc,
    Guid? ApprovalRequestId, int Version, DateTime CreatedAtUtc, DateTime? UpdatedAtUtc,
    IReadOnlyList<TimesheetEntryDto> Entries,
    TimesheetReconciliationDto? Reconciliation,
    TimesheetApprovalProgressDto? Approval);

public sealed record TimesheetSummaryDto(
    Guid Id, int EmployeeId, string EmployeeName, Guid? CompanyId, string PeriodType, DateOnly PeriodStart, DateOnly PeriodEnd,
    string Status, int TotalMinutes, int BillableMinutes, DateTime? SubmittedAtUtc, DateTime? DecidedAtUtc, DateTime? LockedAtUtc,
    int OverAllocatedDays, TimesheetApprovalProgressDto? Approval);

public sealed record TimesheetEntryRequest(
    DateOnly WorkDate,
    Guid ProjectId,
    Guid? ProjectTaskId,
    [Range(0, 1440)] int Minutes,
    bool? IsBillable,
    [MaxLength(500)] string? Notes);

/// <summary>Replaces every entry of the timesheet. Zero-minute rows are dropped.</summary>
public sealed record SaveTimesheetEntriesRequest(IReadOnlyList<TimesheetEntryRequest> Entries, int? Version = null);

public sealed record TimesheetDecisionRequest(
    /// <summary>Approve | Reject | SendBack.</summary>
    [Required] string Decision,
    [MaxLength(1000)] string? Comments);

public sealed record TimesheetQuery(
    string? Status = null, int? EmployeeId = null, Guid? CompanyId = null, DateOnly? From = null, DateOnly? To = null,
    string? Search = null, int Page = 1, int PageSize = 25);

public sealed record TimesheetPeriodDto(DateOnly Start, DateOnly End, string Label, Guid? TimesheetId, string? Status, int TotalMinutes);

public sealed record LockPeriodRequest(DateOnly From, DateOnly To, Guid? CompanyId = null);
public sealed record LockPeriodResult(int Locked, int Skipped);

// ══ Reporting ══════════════════════════════════════════════════════════════════════════════════

public sealed record ApprovedHoursExportRow(
    string ProjectCode, string ProjectName, string ClientName, string CostCenterCode, string CostCenterName,
    string TaskName, string EmployeeCode, string EmployeeName, DateOnly PeriodStart, DateOnly PeriodEnd,
    bool IsBillable, int Minutes);

// ══ Violations / exceptions ════════════════════════════════════════════════════════════════════

public sealed record TimesheetViolation(DateOnly? Date, string Code, string Message);

public sealed class TimesheetValidationException : Exception
{
    public IReadOnlyList<TimesheetViolation> Violations { get; }
    public TimesheetValidationException(IReadOnlyList<TimesheetViolation> violations)
        : base(violations.Count == 1 ? violations[0].Message : $"{violations.Count} problems stop this timesheet.")
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

// ══ Services ═══════════════════════════════════════════════════════════════════════════════════

public interface ITimesheetSettingsService
{
    Task<TimesheetSettingsDto> GetAsync(Guid tenantId, CancellationToken ct);
    Task<TimesheetSettingsDto> UpdateAsync(Guid tenantId, TimesheetSettingsRequest request, Guid? userId, CancellationToken ct);
}

public interface IProjectService
{
    Task<IReadOnlyList<ProjectDto>> ListAsync(Guid tenantId, ProjectQuery query, CancellationToken ct);
    Task<ProjectDto?> GetAsync(Guid tenantId, Guid projectId, CancellationToken ct);
    Task<ProjectDto> CreateAsync(Guid tenantId, SaveProjectRequest request, Guid? userId, CancellationToken ct);
    Task<ProjectDto> UpdateAsync(Guid tenantId, Guid projectId, SaveProjectRequest request, Guid? userId, CancellationToken ct);
    Task DeleteAsync(Guid tenantId, Guid projectId, Guid? userId, CancellationToken ct);
    Task<ProjectDto> SaveTaskAsync(Guid tenantId, Guid projectId, Guid? taskId, SaveProjectTaskRequest request, CancellationToken ct);
    Task<ProjectDto> RemoveTaskAsync(Guid tenantId, Guid projectId, Guid taskId, CancellationToken ct);
    Task<ProjectDto> SaveAssignmentAsync(Guid tenantId, Guid projectId, SaveProjectAssignmentRequest request, Guid? userId, CancellationToken ct);
    Task<ProjectDto> RemoveAssignmentAsync(Guid tenantId, Guid projectId, int employeeId, CancellationToken ct);
    /// <summary>Projects the employee may log to (active assignment, active project), with active tasks.</summary>
    Task<IReadOnlyList<AssignedProjectDto>> GetAssignedProjectsAsync(Guid tenantId, int employeeId, DateOnly? onDate, CancellationToken ct);
}

public interface ITimesheetService
{
    // Employee self-service — every call is pinned to the caller's own employee id.
    Task<TimesheetDto> GetOrCreateOwnAsync(Guid tenantId, int employeeId, DateOnly? anyDateInPeriod, CancellationToken ct);
    Task<TimesheetDto?> GetOwnAsync(Guid tenantId, int employeeId, Guid timesheetId, CancellationToken ct);
    Task<IReadOnlyList<TimesheetPeriodDto>> GetOwnPeriodsAsync(Guid tenantId, int employeeId, int count, CancellationToken ct);
    Task<TimesheetDto> SaveOwnEntriesAsync(Guid tenantId, int employeeId, Guid timesheetId, SaveTimesheetEntriesRequest request, Guid? userId, CancellationToken ct);
    Task<TimesheetDto> CopyPreviousPeriodAsync(Guid tenantId, int employeeId, Guid timesheetId, Guid? userId, CancellationToken ct);
    Task<TimesheetDto> SubmitAsync(Guid tenantId, int employeeId, Guid timesheetId, RequestContext context, CancellationToken ct);

    // Managers / HR.
    Task<PagedResult<TimesheetSummaryDto>> ListAsync(Guid tenantId, TimesheetQuery query, IReadOnlyCollection<int>? allowedEmployeeIds, RequestContext? context, CancellationToken ct);
    Task<TimesheetDto?> GetAsync(Guid tenantId, Guid timesheetId, RequestContext? context, CancellationToken ct);
    Task<IReadOnlyList<TimesheetSummaryDto>> GetApprovalInboxAsync(Guid tenantId, RequestContext context, CancellationToken ct);
    Task<TimesheetDto> DecideAsync(Guid tenantId, Guid timesheetId, TimesheetDecisionRequest request, RequestContext context, CancellationToken ct);
    Task<TimesheetDto> LockAsync(Guid tenantId, Guid timesheetId, RequestContext context, CancellationToken ct);
    Task<LockPeriodResult> LockPeriodAsync(Guid tenantId, LockPeriodRequest request, RequestContext context, CancellationToken ct);
    Task<TimesheetDto> ReopenAsync(Guid tenantId, Guid timesheetId, string? reason, RequestContext context, CancellationToken ct);

    // Billing hand-off.
    Task<IReadOnlyList<ApprovedHoursExportRow>> GetApprovedHoursAsync(Guid tenantId, DateOnly from, DateOnly to, Guid? companyId, Guid? projectId, IReadOnlyCollection<int>? allowedEmployeeIds, CancellationToken ct);
}
