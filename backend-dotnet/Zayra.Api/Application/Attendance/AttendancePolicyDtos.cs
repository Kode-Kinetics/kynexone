namespace Zayra.Api.Application.Attendance;

/// <summary>
/// One attendance rule set as Setup shows it. Only the settings attendance processing actually reads are here:
/// grace (lateness), early-exit tolerance, half-day threshold, standard working minutes and the unpaid break.
/// </summary>
public sealed record AttendancePolicyDto(
    Guid Id, string Code, string Name, Guid? BranchId, Guid? DepartmentId, Guid? GradeId,
    int GraceMinutes, int EarlyExitThresholdMinutes, int HalfDayThresholdMinutes, int StandardWorkMinutes, int BreakMinutes,
    bool IsActive,
    // "HH:mm" local time a day starts when no shift is assigned; null = the long-standing 09:00.
    string? DefaultShiftStart = null);

public sealed record AttendancePolicyRequest(
    string Code, string Name, Guid? BranchId, Guid? DepartmentId, Guid? GradeId,
    int GraceMinutes, int EarlyExitThresholdMinutes, int HalfDayThresholdMinutes, int StandardWorkMinutes, int BreakMinutes,
    // null = leave the stored start unchanged (callers that predate the field must not reset it); "" = clear it (back to 09:00); "HH:mm" = set it.
    string? DefaultShiftStart = null);
