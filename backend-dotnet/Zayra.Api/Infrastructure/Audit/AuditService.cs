using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Audit;

public class AuditService : IAuditService
{
    private readonly ZayraDbContext _db;

    public AuditService(ZayraDbContext db)
    {
        _db = db;
    }

    public async Task WriteAsync(string action, string entityName, string? entityId, RequestContext context, string? metadata, CancellationToken cancellationToken)
    {
        Stage(action, entityName, entityId, context, metadata);
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Adds the audit row to the context WITHOUT saving it. Call this before the business
    /// <c>SaveChangesAsync</c> so the change and its audit row commit in one transaction: a business
    /// change can no longer commit without its audit row (or the reverse). The chain link
    /// (PreviousHash/EntryHash/CreatedAtUtc) is assigned by the context's sealer under the per-tenant
    /// advisory lock at save time, so it is never computed here from an unlocked tail read.
    /// </summary>
    public void Stage(string action, string entityName, string? entityId, RequestContext context, string? metadata)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            Action = action,
            EntityName = entityName,
            EntityId = entityId,
            TenantId = context.TenantId,
            UserId = context.UserId,
            IpAddress = context.IpAddress,
            UserAgent = context.UserAgent,
            Metadata = NormalizeMetadata(metadata),
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    /// <summary>Detaches every audit row that is staged (Added) but not yet saved.</summary>
    public void DiscardStaged()
    {
        foreach (var entry in _db.ChangeTracker.Entries<AuditLog>().Where(e => e.State == Microsoft.EntityFrameworkCore.EntityState.Added).ToList())
            entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
    }

    /// <inheritdoc />
    public void Stage(string action, string entityName, string? entityId, RequestContext context, string? beforeAfterJson, Guid? companyId)
    {
        if (context.TenantId is null)
            throw new InvalidOperationException("A staged audit row needs the tenant it belongs to.");
        // PreviousHash / EntryHash / CreatedAtUtc ordering are set by ZayraDbContext when the row is saved: it sees every pending
        // row of the unit of work together and chains them under the per-tenant advisory lock.
        _db.AuditLogs.Add(new AuditLog
        {
            Action = action,
            EntityName = entityName,
            EntityId = entityId,
            TenantId = context.TenantId,
            CompanyId = companyId,
            UserId = context.UserId,
            IpAddress = context.IpAddress,
            UserAgent = context.UserAgent,
            Metadata = NormalizeMetadata(beforeAfterJson),
            CreatedAtUtc = DateTime.UtcNow,
        });
    }

    // The audit_logs.metadata column is Postgres type `json` (ZayraDbContext + schema.sql). A caller
    // passing a bare, non-JSON string (e.g. "Approved") makes the INSERT fail with a 22P02 invalid-json
    // error, which — because the business change was already committed before the audit write — surfaces
    // as a post-commit 500 that leaves the record updated but the caller erroring. Normalize here so no
    // caller can trigger that: null/blank stays SQL NULL, already-valid JSON is kept verbatim, and any
    // other raw string is encoded as a JSON string literal ("Approved" -> "\"Approved\"").
    private static string? NormalizeMetadata(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata)) return null;
        var trimmed = metadata.Trim();
        try
        {
            using var _ = JsonDocument.Parse(trimmed);
            return trimmed;
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(metadata);
        }
    }

    public static bool VerifyIntegrity(AuditLog log) =>
        string.Equals(log.HashAlgorithm, "SHA-256", StringComparison.OrdinalIgnoreCase)
        && string.Equals(log.EntryHash, ComputeHash(log), StringComparison.Ordinal);

    public static AuditIntegrityReport VerifyChain(IReadOnlyList<AuditLog> logs)
    {
        var walker = new CentralChainWalker(string.Empty);
        foreach (var log in logs) walker.Add(log);
        return walker.Report();
    }

    public static string ComputeHash(AuditLog log) =>
        // Field order/coalescing is IDENTICAL to the original inline implementation — the primitive
        // was extracted only to share it with the payroll chain; this stays byte-for-byte inert so
        // every already-persisted AuditLog EntryHash still verifies.
        ComputeChainHash(
            log.TenantId?.ToString("D") ?? string.Empty,
            log.UserId?.ToString("D") ?? string.Empty,
            log.Action,
            log.EntityName,
            log.EntityId ?? string.Empty,
            log.IpAddress ?? string.Empty,
            log.UserAgent ?? string.Empty,
            log.Metadata ?? string.Empty,
            log.PreviousHash,
            log.CreatedAtUtc.ToUniversalTime().ToString("O"));

    /// <summary>
    /// Shared SHA-256 chain primitive for both the central AuditLog chain and the payroll audit
    /// chain: hex(lowercase) of SHA256 over the '\n'-joined canonical field list. Fields are
    /// null-coalesced to "" so a null vs "" difference cannot silently change the digest.
    /// </summary>
    internal static string ComputeChainHash(params string?[] fields)
    {
        var canonical = string.Join('\n', fields.Select(static f => f ?? string.Empty));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    // ── Payroll audit chain (POD-A3) ────────────────────────────────────────────────
    // Same construction as the AuditLog chain, reusing ComputeChainHash. Two payroll-specific
    // rules that make the trail defensible to an external (Big-4) auditor on real Postgres:
    //   • CreatedAtUtc is normalized to MICROSECOND precision before hashing AND persisting, so the
    //     in-memory seal matches the value Npgsql reads back from `timestamptz` (µs) on verify —
    //     without this the "O" format's 100-ns 7th digit would false-positive every row.
    //   • Seq (monotonic per-tenant ordinal) is bound into the digest, so reordering is detectable
    //     even when two events share a CreatedAtUtc tick.

    /// <summary>
    /// Truncates to microsecond precision and forces Kind=Utc. Postgres `timestamptz` stores µs
    /// (rounds, not truncates), so aligning the persisted value to a µs boundary (100-ns digit = 0)
    /// makes the round-trip value byte-identical under ToString("O").
    /// </summary>
    public static DateTime NormalizeUtcMicroseconds(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
        return new DateTime(utc.Ticks - (utc.Ticks % 10), DateTimeKind.Utc);
    }

    public static string ComputePayrollHash(PayrollAuditLog log) =>
        ComputeChainHash(
            log.TenantId.ToString("D"),
            log.Seq.ToString(CultureInfo.InvariantCulture),
            log.UserId?.ToString("D") ?? string.Empty,
            log.Action,
            log.EntityName,
            log.EntityId,
            log.MetadataJson,
            log.PreviousHash,
            NormalizeUtcMicroseconds(log.CreatedAtUtc).ToString("O"));

    public static bool VerifyPayrollIntegrity(PayrollAuditLog log) =>
        string.Equals(log.HashAlgorithm, "SHA-256", StringComparison.OrdinalIgnoreCase)
        && string.Equals(log.EntryHash, ComputePayrollHash(log), StringComparison.Ordinal);

    /// <summary>
    /// Validates the payroll audit chain for a tenant and reports the first break/tamper. Callers
    /// MUST pass the rows ordered by (Seq, Id). Strict by design: an empty EntryHash is a hard
    /// failure ("unsealed_row"), not a lenient "legacy" pass — that is what closes the
    /// erase-to-legacy bypass (an attacker cannot blank a tampered row's hash to hide it). Legacy
    /// rows are sealed by the boot backfill before traffic, so a clean deployment never trips this.
    /// </summary>
    public static AuditIntegrityReport VerifyPayrollChain(IReadOnlyList<PayrollAuditLog> logs)
    {
        var walker = new PayrollChainWalker(string.Empty);
        foreach (var log in logs) walker.Add(log);
        return walker.Report();
    }

    internal static AuditIntegrityFailure Failure(PayrollAuditLog log, string reason) =>
        new(log.Id, log.CreatedAtUtc, log.Action, log.EntityName, reason);
}

/// <summary>
/// Incremental central-chain verifier: rows are fed in chain order (CreatedAtUtc, Id), one page at a time,
/// so a long chain is verified in bounded memory. <paramref name="expectedPrevious"/> is the hash the first
/// row must link to; pass null when verifying a window that starts mid-chain (the first link is then not
/// checked, every later link is).
/// </summary>
public sealed class CentralChainWalker
{
    private string? _expected;
    private readonly List<AuditIntegrityFailure> _broken = new();
    private int _count;
    private DateTime? _first, _last;

    public CentralChainWalker(string? expectedPrevious) => _expected = expectedPrevious;

    public void Add(AuditLog log)
    {
        _count++;
        _first ??= log.CreatedAtUtc;
        _last = log.CreatedAtUtc;
        if (!AuditService.VerifyIntegrity(log))
        {
            _broken.Add(new AuditIntegrityFailure(log.Id, log.CreatedAtUtc, log.Action, log.EntityName, "entry_hash_mismatch"));
            return;
        }
        if (_expected is not null && !string.Equals(log.PreviousHash, _expected, StringComparison.Ordinal))
            _broken.Add(new AuditIntegrityFailure(log.Id, log.CreatedAtUtc, log.Action, log.EntityName, "previous_hash_mismatch"));
        _expected = log.EntryHash;
    }

    public AuditIntegrityReport Report() => new(_broken.Count == 0, _count, _first, _last, _broken);
}

/// <summary>Incremental payroll-chain verifier (rows fed in (Seq, Id) order); see <see cref="CentralChainWalker"/>.</summary>
public sealed class PayrollChainWalker
{
    private string? _expected;
    private long? _previousSeq;
    private readonly List<AuditIntegrityFailure> _broken = new();
    private int _count;
    private DateTime? _first, _last;

    public PayrollChainWalker(string? expectedPrevious) => _expected = expectedPrevious;

    public void Add(PayrollAuditLog log)
    {
        _count++;
        _first ??= log.CreatedAtUtc;
        _last = log.CreatedAtUtc;
        if (string.IsNullOrEmpty(log.EntryHash))
        {
            _broken.Add(AuditService.Failure(log, "unsealed_row"));
            _expected = log.EntryHash; _previousSeq = log.Seq;
            return;
        }
        if (!AuditService.VerifyPayrollIntegrity(log))
        {
            _broken.Add(AuditService.Failure(log, "entry_hash_mismatch"));
            _expected = log.EntryHash; _previousSeq = log.Seq;
            return;
        }
        if (_previousSeq is not null && log.Seq <= _previousSeq.Value)
            _broken.Add(AuditService.Failure(log, "sequence_out_of_order"));
        if (_expected is not null && !string.Equals(log.PreviousHash, _expected, StringComparison.Ordinal))
            _broken.Add(AuditService.Failure(log, "previous_hash_mismatch"));
        _expected = log.EntryHash; _previousSeq = log.Seq;
    }

    public AuditIntegrityReport Report() => new(_broken.Count == 0, _count, _first, _last, _broken);
}

/// <summary>
/// One chain verification at a time per tenant and kind, with a short cool-down after each, so the
/// read-only integrity endpoints cannot be used to hammer the database. Process-local by design.
/// </summary>
public static class AuditIntegrityGate
{
    public static readonly TimeSpan CoolDown = TimeSpan.FromSeconds(5);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid, string), DateTime> State = new();
    private static readonly DateTime Running = DateTime.MaxValue;

    public enum Outcome { Started, AlreadyRunning, TooSoon }

    public static Outcome TryBegin(Guid tenantId, string kind, DateTime nowUtc)
    {
        var key = (tenantId, kind);
        while (true)
        {
            if (!State.TryGetValue(key, out var st))
            {
                if (State.TryAdd(key, Running)) return Outcome.Started;
                continue;
            }
            if (st == Running) return Outcome.AlreadyRunning;
            if (nowUtc - st < CoolDown) return Outcome.TooSoon;
            if (State.TryUpdate(key, Running, st)) return Outcome.Started;
        }
    }

    public static void End(Guid tenantId, string kind, DateTime nowUtc) => State[(tenantId, kind)] = nowUtc;

    public static void ResetForTests() => State.Clear();
}

public sealed record AuditIntegrityReport(
    bool IsValid,
    int CheckedEntries,
    DateTime? FirstEntryUtc,
    DateTime? LastEntryUtc,
    IReadOnlyList<AuditIntegrityFailure> Failures)
{
    /// <summary>The first break in chain order, or null when the chain is intact.</summary>
    public AuditIntegrityFailure? FirstBreak => Failures.Count == 0 ? null : Failures[0];
}

public sealed record AuditIntegrityFailure(
    Guid AuditLogId,
    DateTime CreatedAtUtc,
    string Action,
    string EntityName,
    string Reason);
