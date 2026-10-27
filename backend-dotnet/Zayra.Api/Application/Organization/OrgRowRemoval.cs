using Zayra.Api.Application.Auth;

namespace Zayra.Api.Application.Organization;

/// <summary>The organisation rows Setup lets an admin add and remove with one uniform rule.</summary>
public enum OrgRowKind { Branch, Department, Designation, Grade, CostCenter, Location }

/// <summary>One kind of live reference to a row, e.g. 12 employees.</summary>
public sealed record OrgRowUsageItem(string Kind, string Label, int Count);

/// <summary>What still points at a row. <see cref="Summary"/> is the plain-language reason shown beside a locked
/// code or a turned-off row ("Used by 12 employees, 3 designations").</summary>
public sealed record OrgRowUsage(IReadOnlyList<OrgRowUsageItem> Items)
{
    public int Total => Items.Sum(i => i.Count);
    public bool InUse => Total > 0;
    public string Summary => Items.Count == 0 ? string.Empty : "Used by " + string.Join(", ", Items.Select(i => $"{i.Count} {i.Label}"));
    public static OrgRowUsage None { get; } = new(Array.Empty<OrgRowUsageItem>());
}

public static class OrgRowOutcomes
{
    /// <summary>Never used: the row was deleted (soft delete).</summary>
    public const string Deleted = "deleted";
    /// <summary>In use or has history: the row was turned off, stays in history, and can be restored.</summary>
    public const string TurnedOff = "turnedOff";
}

/// <summary>The result of a Remove. <see cref="Outcome"/> is decided by the server, never by the caller.</summary>
public sealed record OrgRowRemovalResult(string Outcome, OrgRowUsage Usage, string Message);

public interface IOrgRowRemoval
{
    Task<OrgRowUsage> UsageAsync(OrgRowKind kind, Guid tenantId, Guid id, CancellationToken ct);

    /// <summary>Usage of many rows of one kind in one pass (wizard prefill). Keyed by row id; rows with no
    /// references are absent.</summary>
    Task<IReadOnlyDictionary<Guid, OrgRowUsage>> UsageManyAsync(OrgRowKind kind, Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>Removes a live row: never used -> deleted; used -> turned off. Null when the row does not exist.</summary>
    /// <param name="reason">Optional text recorded with the audit row (cost centres require one at the endpoint).</param>
    Task<OrgRowRemovalResult?> RemoveAsync(OrgRowKind kind, Guid tenantId, Guid id, RequestContext context, CancellationToken ct, string? reason = null);

    /// <summary>Turns a turned-off row back on. Null when the row does not exist.</summary>
    Task<bool?> RestoreAsync(OrgRowKind kind, Guid tenantId, Guid id, RequestContext context, CancellationToken ct);

    /// <summary>Audits a remove / restore that was refused (out of scope, unknown row), so probing shows in the trail.</summary>
    Task RecordRefusalAsync(OrgRowKind kind, Guid tenantId, Guid id, string action, string reason, RequestContext context, CancellationToken ct);

    /// <summary>The owning company of a live row (null company = tenant-wide), or <c>Found: false</c>.</summary>
    Task<(bool Found, Guid? CompanyId)> CompanyOfAsync(OrgRowKind kind, Guid tenantId, Guid id, CancellationToken ct);
}
