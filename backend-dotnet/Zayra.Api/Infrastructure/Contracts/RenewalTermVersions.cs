using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Contracts;

/// <summary>One row of employee_contracts as the version index reads it.</summary>
public sealed record TermVersionRow(Guid Id, Guid EmployeeId, Guid? PreviousVersionId, DateOnly StartDate, DateOnly? EndDate, string Status,
    bool IsDeleted, int Version, string ContractNumber, short? RenewalNumber, DateOnly? ChainStartedOn, string? WorkerNationalityClass);

/// <summary>
/// A renewal case reviews a TERM, and a term can be amended while its review is open (Supersede carries the case,
/// R4 re-review P1-2). The case keeps the contract it was opened on (its identity is fixed by the database), so every
/// reader resolves the term's CURRENT version through this index: follow amendment links forward — a newer version
/// whose start falls inside the version it replaces — to the latest version that is not deleted. Two contracts belong
/// to the same term when they share an amendment <see cref="Root"/>. Slice R4.
/// </summary>
public sealed class RenewalTermVersions
{
    private readonly Dictionary<Guid, TermVersionRow> _byId;
    private readonly ILookup<Guid, TermVersionRow> _children;

    public RenewalTermVersions(IEnumerable<TermVersionRow> rows)
    {
        _byId = rows.ToDictionary(r => r.Id);
        _children = _byId.Values.Where(r => r.PreviousVersionId is not null && IsAmendmentOf(r, r.PreviousVersionId.Value))
            .ToLookup(r => r.PreviousVersionId!.Value);
    }

    /// <summary>Every version of every contract of a tenant (or one employee), deleted rows included.</summary>
    public static async Task<RenewalTermVersions> LoadAsync(ZayraDbContext db, Guid tenantId, Guid? employeePublicId, CancellationToken ct)
    {
        var rows = await ScopedBypass.TenantWide(db.EmployeeContracts, tenantId,
                "Renewal term versions: follows a reviewed term across its amendments, deleted versions included; tenant pinned.")
            .AsNoTracking()
            .Where(c => employeePublicId == null || c.EmployeeId == employeePublicId)
            .Select(c => new TermVersionRow(c.Id, c.EmployeeId, c.PreviousVersionId, c.StartDate, c.EndDate, c.Status, c.IsDeleted, c.Version,
                c.ContractNumber, c.RenewalNumber, c.ChainStartedOn, c.WorkerNationalityClass))
            .ToListAsync(ct);
        return new RenewalTermVersions(rows);
    }

    public TermVersionRow? Find(Guid id) => _byId.GetValueOrDefault(id);

    /// <summary>The first version of the term <paramref name="id"/> belongs to.</summary>
    public Guid Root(Guid id)
    {
        var current = id;
        var seen = new HashSet<Guid> { id };
        while (_byId.TryGetValue(current, out var row) && row.PreviousVersionId is { } prev && IsAmendmentOf(row, prev) && seen.Add(prev))
            current = prev;
        return current;
    }

    /// <summary>The latest, not-deleted version of the term <paramref name="id"/> belongs to (itself when never amended).</summary>
    public TermVersionRow? Current(Guid id)
    {
        if (!_byId.TryGetValue(id, out var row)) return null;
        var seen = new HashSet<Guid> { id };
        while (true)
        {
            var next = _children[row.Id].Where(c => !c.IsDeleted && seen.Add(c.Id)).OrderByDescending(c => c.Version).FirstOrDefault();
            if (next is null) return row;
            row = next;
        }
    }

    /// <summary>Whether <paramref name="a"/> and <paramref name="b"/> are versions of the same term.</summary>
    public bool SameTerm(Guid a, Guid b) => Root(a) == Root(b);

    private bool IsAmendmentOf(TermVersionRow child, Guid parentId) =>
        _byId.TryGetValue(parentId, out var parent) && child.EmployeeId == parent.EmployeeId
        && child.StartDate >= parent.StartDate && (parent.EndDate is null || child.StartDate <= parent.EndDate.Value);
}
