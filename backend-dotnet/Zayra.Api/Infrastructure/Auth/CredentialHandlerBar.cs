using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Data;

namespace Zayra.Api.Infrastructure.Auth;

/// <summary>
/// Amendment 3 F1 — ISSUER EXCLUSION. Whoever was SHOWN the welcome code an employee then redeemed (a printed slip, not
/// an emailed code) could have set that person's password first. For <see cref="Window"/> after the redeem they may not
/// decide anything about that employee: no approval whose subject is the employee (<c>DecisionBar.CredentialHandler</c>)
/// and no ESS profile-change decision.
/// </summary>
public static class CredentialHandlerBar
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(30);
    public const string Message =
        "You gave this person their sign-in code, so someone else must decide their requests for 30 days after they first signed in.";

    private const string Why =
        "Credential-handler bar: who was shown the welcome code a subject employee redeemed is read from the tenant's audit trail; the tenant is re-applied.";

    public static async Task<bool> IsBarredAsync(ZayraDbContext db, Guid tenantId, int subjectEmployeeId, Guid callerUserId, DateTime nowUtc, CancellationToken ct)
    {
        var subjectUsers = (await ApprovalUnblock.SubjectUserIdsAsync(db, tenantId, subjectEmployeeId, ct)).Select(x => x.ToString()).ToList();
        if (subjectUsers.Count == 0) return false;
        var since = nowUtc - Window;
        var rows = await ScopedBypass.NullableTenantWide(db.AuditLogs, tenantId, Why).AsNoTracking()
            .Where(a => a.Action == WelcomeCodeRedeemer.RedeemedAction && a.EntityName == "User" && a.EntityId != null
                && subjectUsers.Contains(a.EntityId) && a.CreatedAtUtc >= since)
            .Select(a => a.Metadata)
            .ToListAsync(ct);
        foreach (var meta in rows)
        {
            if (string.IsNullOrEmpty(meta)) continue;
            try
            {
                using var doc = JsonDocument.Parse(meta);
                var root = doc.RootElement;
                if (root.TryGetProperty("disclosed", out var d) && d.ValueKind == JsonValueKind.True
                    && root.TryGetProperty("issuedBy", out var i) && i.ValueKind == JsonValueKind.String
                    && Guid.TryParse(i.GetString(), out var issuer) && issuer == callerUserId)
                    return true;
            }
            catch (JsonException) { }
        }
        return false;
    }
}
