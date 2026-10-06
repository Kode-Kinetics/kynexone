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

    /// <summary>Whether any version of <paramref name="contract"/>'s term has an open renewal review.</summary>
    public static async Task<bool> HasOpenReviewAsync(ZayraDbContext db, Guid tenantId, EmployeeContract contract, CancellationToken ct)
    {
        var open = await db.ContractRenewalCases.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.EmployeeId == contract.EmployeeId && c.ClosedAt == null)
            .Select(c => c.ExpiringContractId).ToListAsync(ct);
        if (open.Count == 0) return false;
        if (open.Contains(contract.Id)) return true;
        var versions = await LoadAsync(db, tenantId, contract.EmployeeId, ct);
        return open.Any(id => versions.SameTerm(id, contract.Id));
    }

    /// <summary>The first version of the term <paramref name="id"/> belongs to.</summary>
    public Guid Root(Guid id)
    {
        var current = id;
        var seen = new HashSet<Guid> { id };
        while (_byId.TryGetValue(current, out var row) && row.PreviousVersionId is { } prev && IsAmendmentOf(row, prev) && seen.Add(prev))
            current = prev;
        return current;
    }

    /// <summary>
    /// The latest ACTIVATED, not-deleted version of the term <paramref name="id"/> belongs to (itself when never amended).
    /// A drafted amendment (Draft / PendingApproval) is not the term in force until it is activated: until then the
    /// review, the radar and the reconciliation stay on the version that was in force (R4 re-verification P2).
    /// </summary>
    public TermVersionRow? Current(Guid id)
    {
        if (!_byId.TryGetValue(id, out var row)) return null;
        var seen = new HashSet<Guid> { id };
        while (true)
        {
            var next = _children[row.Id].Where(c => !c.IsDeleted && WasActivated(c.Status) && seen.Add(c.Id))
                .OrderByDescending(c => c.Version).FirstOrDefault();
            if (next is null) return row;
            row = next;
        }
    }

    /// <summary>An amendment of this version has been drafted but not activated yet (the radar's "amendment drafted" note).</summary>
    public bool HasPendingAmendment(Guid id) =>
        Current(id) is { } current && _children[current.Id].Any(c => !c.IsDeleted && !WasActivated(c.Status));

    /// <summary>
    /// The version is the term in force: Active, or Superseded only by an amendment still waiting to be activated (the
    /// old version stays in force until its replacement takes effect).
    /// </summary>
    public bool InForce(Guid id) =>
        _byId.TryGetValue(id, out var row) && !row.IsDeleted && Current(id)?.Id == id
        && (row.Status == "Active" || (row.Status == "Superseded" && HasPendingAmendment(id)));

    /// <summary>The start of the term's FIRST version: the anchor its length and renewal deadlines are measured from.</summary>
    public DateOnly? TermStartedOn(Guid id) => Find(Root(id))?.StartDate;

    private static bool WasActivated(string status) => status is "Active" or "Expired" or "Terminated" or "Superseded";

    /// <summary>Whether <paramref name="a"/> and <paramref name="b"/> are versions of the same term.</summary>
    public bool SameTerm(Guid a, Guid b) => Root(a) == Root(b);

    private bool IsAmendmentOf(TermVersionRow child, Guid parentId) =>
        _byId.TryGetValue(parentId, out var parent) && child.EmployeeId == parent.EmployeeId
        && child.StartDate >= parent.StartDate && (parent.EndDate is null || child.StartDate <= parent.EndDate.Value);
}
