using System.Text.Json;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// W2-A — WHO is performing a payroll operation, captured once at the edge (the request's claims, or the
/// job payload) so the payroll pipelines can run identically under an HTTP principal and under the
/// background-job worker, which has no principal at all.
/// </summary>
/// <param name="TenantId">The tenant every write is pinned to.</param>
/// <param name="UserId">The acting user (job: the user who enqueued it). Null only for system work.</param>
/// <param name="UserName">Display name for GL <c>PostedByName</c>.</param>
/// <param name="Ip">Caller IP for the payroll audit metadata; "unknown" when there is no HTTP request.</param>
public sealed record PayrollActor(Guid TenantId, Guid? UserId, string UserName, string Ip);

/// <summary>
/// W2-A — the ONE writer of payroll audit rows. Byte-for-byte the metadata shape
/// <c>PayrollController.PayrollAudit</c> has always written (<c>{ ip, userId, data }</c>); the controller
/// now forwards here so the synchronous endpoints and the job handlers produce identical rows. The
/// Seq / PreviousHash / EntryHash chain fields are stamped by the <see cref="ZayraDbContext"/> sealer at
/// the business SaveChanges — never here.
/// </summary>
public static class PayrollAuditWriter
{
    public static void Append(ZayraDbContext db, PayrollActor actor, string action, string entity, string entityId, object? metadata)
    {
        var meta = new { ip = actor.Ip, userId = actor.UserId?.ToString(), data = metadata };
        db.PayrollAuditLogs.Add(new PayrollAuditLog
        {
            TenantId = actor.TenantId,
            Action = action,
            EntityName = entity,
            EntityId = entityId,
            UserId = actor.UserId,
            MetadataJson = JsonSerializer.Serialize(meta),
        });
    }
}

/// <summary>
/// W2-A — a payroll pipeline refused the operation with a specific HTTP outcome. Thrown (not returned)
/// because a pipeline step runs inside a transaction or a job item and cannot return an
/// <c>IActionResult</c>; throwing is also what guarantees the enclosing transaction rolls back. The
/// synchronous endpoint maps it to the same 4xx it always returned; a job handler maps it to a permanent
/// failure whose message carries the payload.
/// </summary>
public sealed class PayrollRefusalException : Exception
{
    public int StatusCode { get; }
    /// <summary>The response body (null for 404).</summary>
    public object? Payload { get; }
    /// <summary>
    /// Process only: a retro salary DECREASE is refused (422 retro_decrease_unsupported) but its
    /// PendingRecovery arrears lines must still be persisted so the amount is not lost. Building the plan
    /// never writes, so the caller persists these BEFORE surfacing the refusal.
    /// </summary>
    public IReadOnlyList<PayrollArrearsLine>? PendingRecoveryToPersist { get; }

    public PayrollRefusalException(int statusCode, object? payload, IReadOnlyList<PayrollArrearsLine>? pendingRecoveryToPersist = null)
        : base(payload is null ? $"Refused with HTTP {statusCode}." : $"Refused with HTTP {statusCode}: {JsonSerializer.Serialize(payload)}")
    {
        StatusCode = statusCode;
        Payload = payload;
        PendingRecoveryToPersist = pendingRecoveryToPersist;
    }

    /// <summary>A short, human-readable form for a job's LastError (max 4000 chars is enforced by the store).</summary>
    public string Summary()
    {
        if (Payload is null) return $"HTTP {StatusCode}";
        try
        {
            var json = JsonSerializer.SerializeToElement(Payload);
            var error = json.TryGetProperty("error", out var e) ? e.GetString() : null;
            var message = json.TryGetProperty("message", out var m) ? m.GetString() : null;
            var head = string.IsNullOrWhiteSpace(error) ? $"HTTP {StatusCode}" : $"{error} (HTTP {StatusCode})";
            return string.IsNullOrWhiteSpace(message) ? head : $"{head}: {message}";
        }
        catch
        {
            return $"HTTP {StatusCode}";
        }
    }
}
