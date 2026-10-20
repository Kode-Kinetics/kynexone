using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// Two pending changes to the same field of one employee are a race the approver cannot see: approve the newer,
/// then the older, and the older (stale) value is what stays on the record. Two guards close it.
///
/// <para><b>At submission</b> (<see cref="FindPendingOverlapAsync"/>): a change touching a field that another
/// pending change already proposes is REFUSED with a 409 naming the field, rather than superseding the waiting
/// one. Refusing was chosen over cancelling the older request because that request may already be half-way
/// through its chain (an approver signed step 1 of it), may belong to someone else, and cancelling it silently
/// would throw away a decision nobody was told about. The remedy is explicit and in the requester's hands: wait
/// for it, withdraw it (the requester can), or ask an approver to reject it.</para>
///
/// <para><b>At apply</b> (<see cref="FindLaterAppliedOverlapAsync"/>): defensive, for pairs already in flight
/// when the submission guard shipped, and for producers that do not pass through it. A change is never applied
/// when a change SUBMITTED AFTER it and touching one of its fields has already been applied: it is closed as
/// <see cref="EmployeeChangeStatuses.Superseded"/> with an audit row instead of writing the older value back.</para>
/// </summary>
public static class EmployeeChangeOverlap
{
    public sealed record Overlap(EmployeeChangeRequest Change, IReadOnlyList<string> Keys);

    /// <summary>The oldest pending change (its approval still pending) of this employee that proposes any of
    /// <paramref name="keys"/>, with the overlapping keys. Null when none does.</summary>
    public static async Task<Overlap?> FindPendingOverlapAsync(ZayraDbContext db, Guid tenantId, int employeeId,
        IEnumerable<string> keys, Guid? excludeChangeId, CancellationToken ct)
    {
        var wanted = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return null;
        var pending = await db.EmployeeChangeRequests.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId && x.Id != excludeChangeId
                && x.Status == EmployeeChangeStatuses.PendingApproval && x.ApprovalRequestId != null
                && db.ApprovalRequests.Any(a => a.TenantId == tenantId && a.Id == x.ApprovalRequestId && a.Status == "Pending"))
            .OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id)
            .ToListAsync(ct);
        foreach (var change in pending)
        {
            var shared = KeysOf(change).Where(wanted.Contains).OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (shared.Count > 0) return new Overlap(change, shared);
        }
        return null;
    }

    /// <summary>A change submitted after <paramref name="change"/>, already applied, that wrote one of its fields.
    /// Null when none did, so applying <paramref name="change"/> cannot put back an older value.</summary>
    public static async Task<Overlap?> FindLaterAppliedOverlapAsync(ZayraDbContext db, EmployeeChangeRequest change, CancellationToken ct)
    {
        var mine = KeysOf(change).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (mine.Count == 0) return null;
        var later = await db.EmployeeChangeRequests.AsNoTracking()
            .Where(x => x.TenantId == change.TenantId && x.EmployeeId == change.EmployeeId && x.Id != change.Id
                && x.Status == EmployeeChangeStatuses.ApprovedApplied && x.CreatedAtUtc > change.CreatedAtUtc)
            .OrderByDescending(x => x.AppliedAtUtc).ThenBy(x => x.Id)
            .ToListAsync(ct);
        foreach (var other in later)
        {
            var shared = KeysOf(other).Where(mine.Contains).OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (shared.Count > 0) return new Overlap(other, shared);
        }
        return null;
    }

    /// <summary>The field keys a change proposes: its stored patch, else its SensitiveFields list.</summary>
    public static IReadOnlyCollection<string> KeysOf(EmployeeChangeRequest change)
    {
        try
        {
            if (JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(change.ProposedChangesJson) is { Count: > 0 } patch)
                return patch.Keys.ToList();
        }
        catch (JsonException) { /* fall back to the field list */ }
        return change.SensitiveFields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
