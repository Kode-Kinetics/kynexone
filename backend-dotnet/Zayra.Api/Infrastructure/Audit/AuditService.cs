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
        var now = DateTime.UtcNow;
        var previousHash = _db.AuditLogs
            .Where(x => x.TenantId == context.TenantId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ThenByDescending(x => x.Id)
            .Select(x => x.EntryHash)
            .FirstOrDefault() ?? string.Empty;

        var log = new AuditLog
        {
            Action = action,
            EntityName = entityName,
            EntityId = entityId,
            TenantId = context.TenantId,
            UserId = context.UserId,
            IpAddress = context.IpAddress,
            UserAgent = context.UserAgent,
            Metadata = NormalizeMetadata(metadata),
            PreviousHash = previousHash,
            CreatedAtUtc = now
        };
        log.EntryHash = ComputeHash(log);
        _db.AuditLogs.Add(log);
        await _db.SaveChangesAsync(cancellationToken);
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
        var broken = new List<AuditIntegrityFailure>();
        string expectedPreviousHash = string.Empty;

        foreach (var log in logs)
        {
            if (!VerifyIntegrity(log))
            {
                broken.Add(new AuditIntegrityFailure(
                    log.Id,
                    log.CreatedAtUtc,
                    log.Action,
                    log.EntityName,
                    "entry_hash_mismatch"));
                continue;
            }

            if (!string.Equals(log.PreviousHash, expectedPreviousHash, StringComparison.Ordinal))
            {
                broken.Add(new AuditIntegrityFailure(
                    log.Id,
                    log.CreatedAtUtc,
                    log.Action,
                    log.EntityName,
                    "previous_hash_mismatch"));
            }

            expectedPreviousHash = log.EntryHash;
        }

        return new AuditIntegrityReport(
            IsValid: broken.Count == 0,
            CheckedEntries: logs.Count,
            FirstEntryUtc: logs.Count == 0 ? null : logs[0].CreatedAtUtc,
            LastEntryUtc: logs.Count == 0 ? null : logs[^1].CreatedAtUtc,
            Failures: broken);
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
        var broken = new List<AuditIntegrityFailure>();
        string expectedPreviousHash = string.Empty;
        long? previousSeq = null;

        foreach (var log in logs)
        {
            if (string.IsNullOrEmpty(log.EntryHash))
            {
                broken.Add(Failure(log, "unsealed_row"));
                expectedPreviousHash = log.EntryHash;
                previousSeq = log.Seq;
                continue;
            }

            if (!VerifyPayrollIntegrity(log))
            {
                broken.Add(Failure(log, "entry_hash_mismatch"));
                expectedPreviousHash = log.EntryHash;
                previousSeq = log.Seq;
                continue;
            }

            if (previousSeq is not null && log.Seq <= previousSeq.Value)
                broken.Add(Failure(log, "sequence_out_of_order"));

            if (!string.Equals(log.PreviousHash, expectedPreviousHash, StringComparison.Ordinal))
                broken.Add(Failure(log, "previous_hash_mismatch"));

            expectedPreviousHash = log.EntryHash;
            previousSeq = log.Seq;
        }

        return new AuditIntegrityReport(
            IsValid: broken.Count == 0,
            CheckedEntries: logs.Count,
            FirstEntryUtc: logs.Count == 0 ? null : logs[0].CreatedAtUtc,
            LastEntryUtc: logs.Count == 0 ? null : logs[^1].CreatedAtUtc,
            Failures: broken);
    }

    private static AuditIntegrityFailure Failure(PayrollAuditLog log, string reason) =>
        new(log.Id, log.CreatedAtUtc, log.Action, log.EntityName, reason);
}

public sealed record AuditIntegrityReport(
    bool IsValid,
    int CheckedEntries,
    DateTime? FirstEntryUtc,
    DateTime? LastEntryUtc,
    IReadOnlyList<AuditIntegrityFailure> Failures);

public sealed record AuditIntegrityFailure(
    Guid AuditLogId,
    DateTime CreatedAtUtc,
    string Action,
    string EntityName,
    string Reason);
