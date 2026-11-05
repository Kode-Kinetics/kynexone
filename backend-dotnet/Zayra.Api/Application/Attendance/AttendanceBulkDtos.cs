namespace Zayra.Api.Application.Attendance;

/// <summary>
/// Who is writing, as the shared bulk routine needs to know it (I1 and I2): <see cref="AllowedEmployeeIds"/> is the caller's data
/// scope (null is organisation-wide; a list is the COMPLETE set, so an empty list may write for nobody) and
/// <see cref="SelfEmployeeId"/> is the caller's own employee record, which no bulk path may ever write a punch for.
/// </summary>
public sealed record AttendanceBulkAuthority(IReadOnlyCollection<int>? AllowedEmployeeIds, int? SelfEmployeeId)
{
    /// <summary>Organisation-wide, with no employee record of its own (a platform or service caller; tests).</summary>
    public static readonly AttendanceBulkAuthority Unrestricted = new(null, null);
}

/// <summary>One row of the punch file that was not taken. <see cref="Column"/> names the column when the problem is one value.</summary>
public sealed record AttendanceImportRowError(int RowNumber, string? Column, string Message, string RawRow);

/// <summary>
/// The answer to a preview (nothing written: <see cref="BatchId"/> and <see cref="Status"/> are null, <see cref="ImportedRows"/> 0) and to
/// an import. <c>TotalRows = ValidRows + DuplicateRows + FailedRows</c>: a valid row is one that is new; a duplicate (a punch already on
/// record) is counted, not an error. After an import <see cref="ValidRows"/> equals <see cref="ImportedRows"/>.
/// <see cref="Errors"/> lists at most 500 rows; <see cref="FailedRows"/> is the full count.
/// </summary>
public sealed record AttendanceImportResult(
    Guid? BatchId, string? Status, string FileName, int TotalRows, int ValidRows, int ImportedRows, int DuplicateRows, int FailedRows,
    string TimeZone, IReadOnlyList<AttendanceImportRowError> Errors);

/// <summary>Mass entry of the same punches for a selection of employees over a date range. Times are tenant-local "HH:mm".</summary>
public sealed record BulkAttendanceEntryRequest(
    IReadOnlyList<int>? EmployeeIds, DateOnly FromDate, DateOnly ToDate, string? InTime, string? OutTime, string? Reason,
    string? VerificationMethod = null, string? LocationName = null);

public sealed record BulkAttendanceEntryError(string EmployeeCode, DateOnly Date, string Message);

/// <summary>Counts are PUNCHES (an In and an Out are two): <c>Requested = Created + Duplicates + Failed</c>. <see cref="Errors"/> has one
/// entry per employee-day that was refused (at most 500; <see cref="Failed"/> is the full count).</summary>
public sealed record BulkAttendanceEntryResult(int Requested, int Created, int Duplicates, int Failed, IReadOnlyList<BulkAttendanceEntryError> Errors);
