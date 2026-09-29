using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

/// <summary>
/// F3 — one durable unit of long-running work (attendance processing today; payroll Process / Lock /
/// GenerateWps in Wave 2).
///
/// <para>WHY A ROW AND NOT A REQUEST. Every long operation used to run inside one HTTP request holding
/// one transaction: a timeout, a deploy or a crashed instance rolled the whole thing back and nothing
/// resumed. A job row outlives the request and the process. The worker that runs it holds a LEASE
/// (<see cref="LeaseToken"/> + <see cref="LeaseExpiresAtUtc"/>), renews it by heartbeat, and every
/// write it makes on the job's behalf is FENCED by that token — so when a process dies and another
/// instance reclaims the expired lease, the dead (or merely stalled) instance can no longer commit
/// anything.</para>
///
/// <para>TENANCY. <see cref="ITenantOwned"/>, so the reflection-applied global query filter in
/// <c>ZayraDbContext.ApplyTenantQueryFilters</c> pins every request-path read to the caller's tenant
/// with no code here. There is deliberately NO <c>CompanyId</c>: a job is not company data, and the
/// caller's company scope is captured in the payload and RE-APPLIED by the handler to the rows it
/// touches (see <c>AttendanceProcessingJobHandler</c>). The worker, which has no request tenant,
/// reaches jobs only through <c>ScopedBypass</c>.</para>
/// </summary>
public sealed class BackgroundJob : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>Handler key, e.g. <c>attendance.process</c>. Workers only claim types they can run.</summary>
    public string JobType { get; set; } = string.Empty;

    /// <summary>Handler-defined JSON input. Immutable after enqueue.</summary>
    public string PayloadJson { get; set; } = "{}";

    public string Status { get; set; } = BackgroundJobStatuses.Queued;

    /// <summary>
    /// Logical identity of the request. Enqueueing the same (tenant, type, key) while an earlier job
    /// still holds the key returns that job instead of creating a second one — this is what makes a
    /// double-clicked "Process" (and, in Wave 2, "Run payroll" / "Lock") safe. How long a key is held
    /// is <see cref="KeyRetention"/>.
    /// </summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>
    /// <see cref="BackgroundJobKeyRetention.WhileActive"/>: the key is held while Queued/Running, then
    /// released so the same work can be re-run later (attendance reprocessing).
    /// <see cref="BackgroundJobKeyRetention.Forever"/>: a SUCCEEDED job keeps the key permanently, so
    /// the operation can happen at most once (payroll Lock's post-once guarantee).
    /// Enforced by two partial unique indexes, not by application code.
    /// </summary>
    public string KeyRetention { get; set; } = BackgroundJobKeyRetention.WhileActive;

    // ── Progress ─────────────────────────────────────────────────────────────────
    public int? ProgressTotal { get; set; }
    public int ProgressCompleted { get; set; }
    public string? ProgressMessage { get; set; }

    // ── Execution / lease ────────────────────────────────────────────────────────
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; } = 5;
    /// <summary>Earliest time a Queued job may be claimed (retry back-off).</summary>
    public DateTime RunAfterUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Opaque worker identity (host:pid:guid) — diagnostics only; the fence is <see cref="LeaseToken"/>.</summary>
    public string? LeaseOwner { get; set; }
    /// <summary>Fencing token, regenerated on every claim. Every job write by a worker requires it.</summary>
    public Guid? LeaseToken { get; set; }
    public DateTime? LeaseExpiresAtUtc { get; set; }
    public DateTime? HeartbeatAtUtc { get; set; }
    public DateTime? CancelRequestedAtUtc { get; set; }

    // ── Outcome ──────────────────────────────────────────────────────────────────
    public string? LastError { get; set; }
    public string? ResultJson { get; set; }

    // ── Audit ────────────────────────────────────────────────────────────────────
    public Guid? CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}

/// <summary>
/// F3 — the CHECKPOINT. One row per item a job has durably applied, written in the SAME transaction as
/// the item's own writes and the lease fence. Consequences, by construction:
/// <list type="bullet">
///   <item>the item's effects and its checkpoint commit or roll back together — there is no window in
///     which an item is applied but not recorded, or recorded but not applied;</item>
///   <item>a resumed or retried job loads these keys and skips them, so an item is never applied twice;</item>
///   <item>the unique (job_id, item_key) index is a second, database-level guarantee of the same.</item>
/// </list>
/// </summary>
public sealed class BackgroundJobItem : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid JobId { get; set; }
    /// <summary>Handler-defined stable key, e.g. <c>employee:1042</c>. Must be deterministic across attempts.</summary>
    public string ItemKey { get; set; } = string.Empty;
    /// <summary>The attempt that applied the item — lets tests and operators see where a resume picked up.</summary>
    public int Attempt { get; set; }
    /// <summary>Optional small handler result for the item (e.g. rows written).</summary>
    public string? ResultJson { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public static class BackgroundJobStatuses
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";

    public static bool IsTerminal(string status) => status is Succeeded or Failed or Cancelled;
}

public static class BackgroundJobKeyRetention
{
    public const string WhileActive = "WhileActive";
    public const string Forever = "Forever";
}
