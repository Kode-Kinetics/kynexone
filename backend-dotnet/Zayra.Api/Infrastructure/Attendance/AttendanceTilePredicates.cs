using System.Linq.Expressions;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Attendance;

/// <summary>
/// The Live Dashboard's KPI tiles, defined ONCE. <c>DashboardAsync</c> counts with these and the daily endpoint's
/// <c>tile</c> filter lists with these, so the number on a tile and the records shown after clicking it are the same set
/// and cannot drift apart again. Both read the same day's records of ACTIVE employees (<see cref="ActiveEmployeeIds"/>).
/// </summary>
public static class AttendanceTilePredicates
{
    public const string PresentTile = "present";
    public const string AbsentTile = "absent";
    public const string LateTile = "late";
    public const string MissingPunchTile = "missingPunch";

    /// <summary>Attended the day: on time, late or half day (a late or half day is attendance, as <see cref="AttendanceStatuses"/> documents).</summary>
    public static readonly Expression<Func<AttendanceDailyRecord, bool>> Present =
        x => x.Status == AttendanceStatuses.Present || x.Status == AttendanceStatuses.Late || x.Status == AttendanceStatuses.HalfDay;

    public static readonly Expression<Func<AttendanceDailyRecord, bool>> Absent = x => x.Status == AttendanceStatuses.Absent;

    /// <summary>Any recorded late minutes, whatever the status says (a "Present" day can carry late minutes).</summary>
    public static readonly Expression<Func<AttendanceDailyRecord, bool>> Late = x => x.LateMinutes > 0;

    public static readonly Expression<Func<AttendanceDailyRecord, bool>> MissingPunch = x => x.MissingPunch;

    /// <summary>The predicate for a tile name (case-insensitive), or false when the name is not a tile.</summary>
    public static bool TryGet(string? tile, out Expression<Func<AttendanceDailyRecord, bool>> predicate)
    {
        predicate = null!;
        if (string.Equals(tile, PresentTile, StringComparison.OrdinalIgnoreCase)) predicate = Present;
        else if (string.Equals(tile, AbsentTile, StringComparison.OrdinalIgnoreCase)) predicate = Absent;
        else if (string.Equals(tile, LateTile, StringComparison.OrdinalIgnoreCase)) predicate = Late;
        else if (string.Equals(tile, MissingPunchTile, StringComparison.OrdinalIgnoreCase)) predicate = MissingPunch;
        return predicate is not null;
    }

    /// <summary>The tenant's active, not-deleted employees: the population the tiles count.</summary>
    public static IQueryable<int> ActiveEmployeeIds(IQueryable<Employee> employees, Guid tenantId) =>
        employees.Where(e => e.TenantId == tenantId && !e.IsDeleted && e.Status == EmployeeStatuses.Active).Select(e => e.Id);
}
