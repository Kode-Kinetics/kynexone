using Zayra.Api.Models;

namespace Zayra.Api.Application.Organization;

/// <summary>
/// THE single occupancy predicate for the establishment (headcount) system. Used by BOTH the
/// Cost Centres &amp; Budget panel reads (PlanningController / EstablishmentController.Matrix) and
/// the enforcement guard (EstablishmentGuardService) so displayed numbers and enforced numbers
/// are always identical (spec Conflict R3).
///
/// Occupying statuses:
///  - Active     — employed.
///  - Offboarded — serving notice; still employed, still occupies the seat.
///  - Suspended  — disciplinary hold; under KSA Labor Law (Arts. 66–68) suspension is a
///    CONTINUING, GOSI-registered employment relationship, so the seat stays occupied. Counting
///    it here closes two compliance holes at once: (a) suspend→hire replacement→reinstate can no
///    longer be used as an unaudited quota-evasion loop, and (b) reinstatement (Suspended→Active)
///    is occupying→occupying and therefore never hard-blocked — the system can never refuse to
///    record a legally mandated reinstatement.
///  - Legacy "Inactive" / Draft / Invited / Terminated / Exited / Archived do NOT occupy.
///
/// FIREWALL: this is a workforce-PLANNING predicate only. It must never feed Nitaqat / GOSI
/// statutory counts — those follow GOSI registration, not planning occupancy.
/// </summary>
public static class EstablishmentOccupancy
{
    /// <summary>Statuses that occupy an establishment seat. Employment-lifecycle vocabulary
    /// (EmployeeStatuses constants), not tenant business data.</summary>
    public static readonly string[] OccupyingStatuses =
    {
        EmployeeStatuses.Active, EmployeeStatuses.Offboarded, EmployeeStatuses.Suspended
    };

    public static bool IsOccupyingStatus(string? status) =>
        status is not null && OccupyingStatuses.Contains(status, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Occupying employees of a tenant. Written as explicit literals (not
    /// <c>OccupyingStatuses.Contains</c>) purely so the expression stays translatable by every
    /// provider including InMemory; keep in sync with <see cref="OccupyingStatuses"/> — the
    /// EstablishmentGuardTests parity test enforces this.
    /// The explicit <c>!IsDeleted</c> is deliberate: callers pass <c>IgnoreQueryFilters()</c>
    /// sources (the guard must count absolutely, independent of the caller's company scope).
    /// </summary>
    public static IQueryable<Employee> Occupying(IQueryable<Employee> employees, Guid tenantId) =>
        employees.Where(e => e.TenantId == tenantId && !e.IsDeleted
                          && (e.Status == "Active" || e.Status == "Offboarded" || e.Status == "Suspended"));

    /// <summary>
    /// Department membership including the legacy free-text fallback the panel has always used:
    /// an employee with no DepartmentId still counts toward the department whose NameEn equals
    /// their Department string. (Batch A stops NEW string-only rows being created; this keeps
    /// pre-existing ones counted rather than silently dropping them.)
    /// </summary>
    public static bool MatchesDepartment(Guid? employeeDeptId, string? employeeDeptName, Guid departmentId, string departmentNameEn) =>
        employeeDeptId == departmentId || (employeeDeptId == null && employeeDeptName == departmentNameEn);
}
