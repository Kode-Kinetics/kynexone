using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// The makers of a hire as the employee module knows them: whoever created the draft (for an accepted
/// offer, whoever accepted it) and whoever has changed its content since, read from the draft's own
/// edit audit rows (<see cref="EmployeeDraftAuditActions.Edits"/>). Two queries for any number of drafts.
/// </summary>
public sealed class DraftHireMakers : IDraftHireMakers
{
    private readonly ZayraDbContext _db;

    public DraftHireMakers(ZayraDbContext db) => _db = db;

    public async Task<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>> MakersAsync(
        Guid tenantId, IReadOnlyCollection<Guid> draftIds, CancellationToken ct)
    {
        var result = draftIds.Distinct().ToDictionary(id => id, _ => new HashSet<Guid>());
        if (result.Count == 0) return new Dictionary<Guid, IReadOnlySet<Guid>>();

        var ids = result.Keys.ToList();
        var creators = await _db.EmployeeDrafts.AsNoTracking()
            .Where(d => d.TenantId == tenantId && ids.Contains(d.Id) && d.CreatedByUserId != null)
            .Select(d => new { d.Id, Creator = d.CreatedByUserId!.Value })
            .ToListAsync(ct);
        foreach (var c in creators) result[c.Id].Add(c.Creator);

        var entityIds = ids.Select(id => id.ToString()).ToList();
        var editors = await ScopedBypass.NullableTenantWide(_db.AuditLogs, tenantId,
                "Who changed these drafts, whichever company their edit rows were stamped with.")
            .AsNoTracking()
            .Where(a => a.EntityName == "EmployeeDraft" && a.EntityId != null && entityIds.Contains(a.EntityId)
                && EmployeeDraftAuditActions.Edits.Contains(a.Action) && a.UserId != null)
            .Select(a => new { a.EntityId, Editor = a.UserId!.Value })
            .Distinct()
            .ToListAsync(ct);
        foreach (var e in editors)
            if (Guid.TryParse(e.EntityId, out var id) && result.TryGetValue(id, out var set)) set.Add(e.Editor);

        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<Guid>)kv.Value);
    }
}
