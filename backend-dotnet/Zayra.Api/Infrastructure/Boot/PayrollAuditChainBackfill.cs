using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Boot;

/// <summary>
/// POD-A3 one-time (idempotent) sealer for legacy payroll_audit_logs rows that predate the
/// hash-chain feature. It establishes the TRUSTED BASELINE as of migration time: it seals existing
/// content so every row is immutable and chain-verifiable from now on. Like every tamper-evidence
/// control, it cannot retroactively prove pre-feature history was untampered — that is inherent to
/// introducing a hash chain and is the same guarantee the central AuditLog chain gives.
///
/// Runs at boot BEFORE traffic (Program.cs, right after CompanyScopeBackfill), so steady state has
/// no unsealed rows and the verifier stays strict (an empty EntryHash is a hard failure, never a
/// lenient "legacy" pass — that is what closes the erase-to-legacy bypass).
///
/// Design:
///   • Per tenant that owns ANY unsealed (entry_hash == "") row, load ALL that tenant's rows in
///     (CreatedAtUtc, Id) order and walk them, assigning Seq / PreviousHash / EntryHash with the
///     SAME AuditService.ComputePayrollHash the live sealer uses.
///   • Already-sealed rows are trusted as chain links (their stored EntryHash becomes the next
///     PreviousHash) and are NOT rewritten — so a partial run is CONTINUATION-SAFE and the Postgres
///     append-only trigger (which permits UPDATE only while entry_hash is empty) never blocks it.
///   • Persistence is set-based ExecuteUpdateAsync keyed on Id: no ChangeTracker entries, so the
///     in-process append-only guard does not intercept it, and it is the one mutation the DB
///     trigger allows.
///   • IDEMPOTENT: only rows with entry_hash == "" are ever written; a second run is a no-op.
/// </summary>
public static class PayrollAuditChainBackfill
{
    public static async Task<int> RunAsync(ZayraDbContext db, ILogger logger, CancellationToken ct = default)
    {
        // IgnoreQueryFilters is intentional — SYSTEM CONTEXT: this is a boot-time repair pass with no
        // HttpContext; every statement re-applies an explicit TenantId / Id predicate, so no
        // cross-tenant read or write is possible, and the ambient tenant/company filter would
        // otherwise hide the very rows this sealer exists to fix.
        var tenantIds = await db.PayrollAuditLogs.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.EntryHash == string.Empty)
            .Select(x => x.TenantId)
            .Distinct()
            .ToListAsync(ct);

        if (tenantIds.Count == 0)
        {
            logger.LogInformation("PayrollAuditChainBackfill: no unsealed payroll audit rows — nothing to do.");
            return 0;
        }

        var sealedCount = 0;
        foreach (var tenantId in tenantIds)
        {
            // IgnoreQueryFilters is intentional — SYSTEM CONTEXT (boot repair, no HttpContext); the
            // explicit TenantId predicate re-scopes the read to this tenant only.
            var rows = await db.PayrollAuditLogs.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.TenantId == tenantId)
                .OrderBy(x => x.CreatedAtUtc)
                .ThenBy(x => x.Id)
                .ToListAsync(ct);

            var prev = string.Empty;
            long seq = 0;
            foreach (var row in rows)
            {
                seq++;
                if (!string.IsNullOrEmpty(row.EntryHash))
                {
                    // Already sealed (continuation of an interrupted run) — trust it as the link and
                    // keep the ordinal advancing so downstream Seq values stay consistent.
                    prev = row.EntryHash;
                    continue;
                }

                row.Seq = seq;
                row.PreviousHash = prev;
                row.HashAlgorithm = "SHA-256";
                row.CreatedAtUtc = AuditService.NormalizeUtcMicroseconds(row.CreatedAtUtc);
                row.EntryHash = AuditService.ComputePayrollHash(row);
                prev = row.EntryHash;

                var newSeq = row.Seq;
                var newPrev = row.PreviousHash;
                var newHash = row.EntryHash;
                // IgnoreQueryFilters is intentional — SYSTEM CONTEXT (boot repair); keyed on the
                // row's own Id. Set-based ExecuteUpdate bypasses the ChangeTracker append-only guard
                // by design, and the DB trigger permits it only while entry_hash is still empty.
                await db.PayrollAuditLogs.IgnoreQueryFilters()
                    .Where(x => x.Id == row.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.Seq, newSeq)
                        .SetProperty(x => x.PreviousHash, newPrev)
                        .SetProperty(x => x.EntryHash, newHash)
                        .SetProperty(x => x.HashAlgorithm, "SHA-256"), ct);
                sealedCount++;
            }
        }

        logger.LogInformation(
            "PayrollAuditChainBackfill complete: sealed {Count} legacy payroll audit rows across {Tenants} tenant(s).",
            sealedCount, tenantIds.Count);
        return sealedCount;
    }
}
