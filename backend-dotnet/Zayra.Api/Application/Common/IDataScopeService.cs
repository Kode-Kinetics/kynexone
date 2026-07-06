using System.Security.Claims;
namespace Zayra.Api.Application.Common;

public enum DataScopeLevel { Own, DirectReports, Department, Team, Organization }

public sealed class DataScope
{
    public DataScopeLevel Level { get; init; }
    public int? CallerEmployeeId { get; init; }
    /// <summary>null = unrestricted (entire organisation)</summary>
    public IReadOnlyCollection<int>? AllowedEmployeeIds { get; init; }

    public bool IsUnrestricted => AllowedEmployeeIds is null;

    /// <summary>
    /// True when the caller is allowed to see the given employee's records under this scope.
    /// Use this to authorize by-id ("detail") reads, which cannot be protected by <see cref="Constrain"/>
    /// (that method silently falls back to the caller's own id for a filtered LIST; a detail read must
    /// instead be rejected outright). Org-wide callers pass; scoped callers must have the id in-scope.
    /// </summary>
    public bool CanAccessEmployee(int employeeId) =>
        IsUnrestricted || (AllowedEmployeeIds?.Contains(employeeId) ?? false);

    /// Constrains a client-supplied employeeId filter against this scope.
    /// Returns (singleId, setFilter):
    ///   singleId non-null → filter by one employee
    ///   setFilter non-null → filter by set (use .Contains in query)
    ///   both null → no restriction (org-wide)
    public (int? SingleId, IReadOnlyCollection<int>? SetFilter) Constrain(int? requestedEmployeeId)
    {
        if (IsUnrestricted)
            return (requestedEmployeeId, null);
        if (requestedEmployeeId.HasValue)
        {
            // Requested a specific employee: allow only if in scope
            return AllowedEmployeeIds!.Contains(requestedEmployeeId.Value)
                ? (requestedEmployeeId, null)
                : (CallerEmployeeId, null); // fall back to own record
        }
        // No specific employee requested: return the full allowed set
        return (null, AllowedEmployeeIds);
    }
}

public interface IDataScopeService
{
    Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct);
}
