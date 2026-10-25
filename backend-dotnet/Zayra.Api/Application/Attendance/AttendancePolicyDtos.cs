namespace Zayra.Api.Application.Attendance;

/// <summary>
/// One attendance rule set as Setup shows it. Only the settings attendance processing actually reads are here:
/// grace (lateness), early-exit tolerance, half-day threshold, standard working minutes and the unpaid break.
/// </summary>
public sealed record AttendancePolicyDto(
    Guid Id, string Code, string Name, Guid? BranchId, Guid? DepartmentId, Guid? GradeId,
    int GraceMinutes, int EarlyExitThresholdMinutes, int HalfDayThresholdMinutes, int StandardWorkMinutes, int BreakMinutes,
    bool IsActive);

public sealed record AttendancePolicyRequest(
    string Code, string Name, Guid? BranchId, Guid? DepartmentId, Guid? GradeId,
    int GraceMinutes, int EarlyExitThresholdMinutes, int HalfDayThresholdMinutes, int StandardWorkMinutes, int BreakMinutes);
