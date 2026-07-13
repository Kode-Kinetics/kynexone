using System.Security.Cryptography;
using System.Text;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;

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
            Metadata = metadata,
            PreviousHash = previousHash,
            CreatedAtUtc = now
        };
        log.EntryHash = ComputeHash(log);
        _db.AuditLogs.Add(log);
        await _db.SaveChangesAsync(cancellationToken);
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

    public static string ComputeHash(AuditLog log)
    {
        var canonical = string.Join('\n',
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
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
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
