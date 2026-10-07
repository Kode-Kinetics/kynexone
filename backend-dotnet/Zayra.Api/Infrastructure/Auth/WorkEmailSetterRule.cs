using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;

namespace Zayra.Api.Infrastructure.Auth;

/// <summary>
/// PLACEHOLDER for the shared helper landing on fix/login-takeover-p0 (contract Amendment 1). When that branch is merged,
/// its version replaces this file; the call sites use only <see cref="IsSetterAsync"/>, <see cref="IsPlusAddressed"/>
/// and the two code constants.
///
/// <para>THE RULE. Whoever last set an employee's work email (the actor of the latest
/// <c>employee.work_email_changed</c> row, or the drafter named on it for a draft-created hire) must not also hand out
/// that employee's first credential: a person who chose the address and printed the code would hold the whole login.</para>
/// </summary>
public static class WorkEmailSetterRule
{
    public const string SetByCallerCode = "work_email_set_by_caller";
    public const string PlusAddressCode = "work_email_plus_address";
    public const string PlusAddressMessage = "Work email can't contain '+'.";

    private const string Why =
        "Work-email setter rule: who last set one employee's work email is read from the tenant's audit trail; the tenant is re-applied and the employee was already scope-checked.";

    /// <summary>A '+' in the local part (plus-addressing routes mail to the same mailbox under a different username).</summary>
    public static bool IsPlusAddressed(string? workEmail)
    {
        var value = (workEmail ?? string.Empty).Trim();
        var at = value.LastIndexOf('@');
        var local = at < 0 ? value : value[..at];
        return local.Contains('+');
    }

    /// <summary>True when <paramref name="callerUserId"/> is the employee's last work-email setter (or its drafter).</summary>
    public static async Task<bool> IsSetterAsync(ZayraDbContext db, Guid tenantId, int employeeId, Guid callerUserId, CancellationToken ct)
    {
        var key = employeeId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var last = await ScopedBypass.NullableTenantWide(db.AuditLogs, tenantId, Why).AsNoTracking()
            .Where(x => x.EntityName == "Employee" && x.EntityId == key && x.Action == AccessManagementService.WorkEmailChangedAction)
            .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id)
            .Select(x => new { x.UserId, x.Metadata })
            .FirstOrDefaultAsync(ct);
        if (last is null) return false;
        if (last.UserId == callerUserId) return true;
        return DraftedBy(last.Metadata) == callerUserId;
    }

    private static Guid? DraftedBy(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata)) return null;
        try
        {
            using var doc = JsonDocument.Parse(metadata);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("draftedBy", out var d)
                   && d.ValueKind == JsonValueKind.String
                   && Guid.TryParse(d.GetString(), out var id)
                ? id
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
