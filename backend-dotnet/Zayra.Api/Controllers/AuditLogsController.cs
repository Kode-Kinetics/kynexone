using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/audit-logs")]
[Authorize(Roles = "Admin")]
public class AuditLogsController : ControllerBase
{
    private readonly ZayraDbContext _db;

    public AuditLogsController(ZayraDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> Recent([FromQuery] int limit = 100, CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId();
        if (tenantId is null) return Unauthorized();
        limit = Math.Clamp(limit, 1, 500);
        // Raw metadata, IP address and user agent carry attempted sign-in emails and client
        // addresses. Readers of the audit trail (audit.read) see who did what and when; only
        // security administrators (security.manage) see that raw detail.
        var includeRaw = HoldsPermission("security.manage");
        var logs = await _db.AuditLogs
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(limit)
            .Select(x => new
            {
                x.Id,
                x.TenantId,
                x.UserId,
                x.Action,
                x.EntityName,
                x.EntityId,
                x.CreatedAtUtc,
                IpAddress = includeRaw ? x.IpAddress : null,
                UserAgent = includeRaw ? x.UserAgent : null,
                Metadata = includeRaw ? x.Metadata : null,
                RawDetailRedacted = !includeRaw,
            })
            .ToListAsync(cancellationToken);
        return Ok(logs);
    }

    private bool HoldsPermission(string permission) =>
        User.Claims.Any(c => c.Type == "permission" && string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));

    private const int VerifyPageSize = 1000;
    private const int VerifyMaxRows = 2_000_000;

    /// <summary>
    /// Read-only walk of the tenant's central audit chain, streamed in bounded keyset pages (memory is one
    /// page, not the chain). Optional window: <c>fromUtc</c>/<c>toUtc</c> verify a slice (the first link of
    /// a slice that starts mid-chain is not checked; every later one is); <c>maxRows</c> caps the walk.
    /// One verification at a time per tenant (409) with a short cool-down (429).
    /// </summary>
    [HttpGet("integrity")]
    public async Task<IActionResult> Integrity([FromQuery] DateTime? fromUtc = null, [FromQuery] DateTime? toUtc = null,
        [FromQuery] int? maxRows = null, CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId();
        if (tenantId is null) return Unauthorized();
        if (Gate(tenantId.Value, "central") is { } refused) return refused;
        try
        {
            var cap = Math.Clamp(maxRows ?? VerifyMaxRows, 1, VerifyMaxRows);
            var walker = new CentralChainWalker(fromUtc is null ? string.Empty : null);
            var q = _db.AuditLogs.AsNoTracking().Where(x => x.TenantId == tenantId);
            if (fromUtc is { } f) q = q.Where(x => x.CreatedAtUtc >= f.ToUniversalTime());
            if (toUtc is { } t) q = q.Where(x => x.CreatedAtUtc <= t.ToUniversalTime());
            DateTime? lastCreated = null;
            Guid lastId = Guid.Empty;
            var seen = 0;
            var truncated = false;
            while (seen < cap)
            {
                var page = q;
                if (lastCreated is { } lc)
                    page = page.Where(x => x.CreatedAtUtc > lc || (x.CreatedAtUtc == lc && x.Id.CompareTo(lastId) > 0));
                var take = Math.Min(VerifyPageSize, cap - seen);
                var candidates = await page.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id)
                    .Take(take + 1).ToListAsync(cancellationToken);
                var hasMore = candidates.Count > take;
                var rows = candidates.Take(take).ToList();
                if (rows.Count == 0) break;
                foreach (var row in rows) walker.Add(row);
                seen += rows.Count;
                lastCreated = rows[^1].CreatedAtUtc;
                lastId = rows[^1].Id;
                if (seen >= cap) { truncated = hasMore; break; }
                if (!hasMore) break;
            }
            var report = walker.Report();
            return Ok(report with
            {
                IsComplete = !truncated,
                IsValid = report.IsValid && !truncated,
                ContinuationBoundary = truncated ? $"{lastCreated:O}/{lastId:D}" : null
            });
        }
        finally { AuditIntegrityGate.End(tenantId.Value, "central", DateTime.UtcNow); }
    }

    /// <summary>
    /// Read-only walk of the tenant's payroll audit chain (keyset by Seq, bounded pages). Reports the first
    /// break (<c>firstBreak</c>) and every failure; changes nothing. Needs a payroll permission on top of the
    /// audit role: the failure rows name payroll actions and entities.
    /// </summary>
    [HttpGet("integrity/payroll")]
    [Zayra.Api.Infrastructure.Authorization.HasPermission("payroll.read")]
    public async Task<IActionResult> PayrollIntegrity([FromQuery] long? fromSeq = null, [FromQuery] long? toSeq = null,
        [FromQuery] int? maxRows = null, CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId();
        if (tenantId is null) return Unauthorized();
        if (Gate(tenantId.Value, "payroll") is { } refused) return refused;
        try
        {
            var cap = Math.Clamp(maxRows ?? VerifyMaxRows, 1, VerifyMaxRows);
            var walker = new PayrollChainWalker(fromSeq is null ? string.Empty : null);
            var q = _db.PayrollAuditLogs.AsNoTracking().Where(x => x.TenantId == tenantId);
            if (fromSeq is { } fs) q = q.Where(x => x.Seq >= fs);
            if (toSeq is { } ts) q = q.Where(x => x.Seq <= ts);
            long? lastSeq = null;
            Guid lastId = Guid.Empty;
            var seen = 0;
            var truncated = false;
            while (seen < cap)
            {
                var page = q;
                if (lastSeq is { } ls)
                    page = page.Where(x => x.Seq > ls || (x.Seq == ls && x.Id.CompareTo(lastId) > 0));
                var take = Math.Min(VerifyPageSize, cap - seen);
                var candidates = await page.OrderBy(x => x.Seq).ThenBy(x => x.Id)
                    .Take(take + 1).ToListAsync(cancellationToken);
                var hasMore = candidates.Count > take;
                var rows = candidates.Take(take).ToList();
                if (rows.Count == 0) break;
                foreach (var row in rows) walker.Add(row);
                seen += rows.Count;
                lastSeq = rows[^1].Seq;
                lastId = rows[^1].Id;
                if (seen >= cap) { truncated = hasMore; break; }
                if (!hasMore) break;
            }
            var report = walker.Report();
            return Ok(report with
            {
                IsComplete = !truncated,
                IsValid = report.IsValid && !truncated,
                ContinuationBoundary = truncated ? $"{lastSeq}/{lastId:D}" : null
            });
        }
        finally { AuditIntegrityGate.End(tenantId.Value, "payroll", DateTime.UtcNow); }
    }

    private IActionResult? Gate(Guid tenantId, string kind)
    {
        switch (AuditIntegrityGate.TryBegin(tenantId, kind, DateTime.UtcNow))
        {
            case AuditIntegrityGate.Outcome.AlreadyRunning:
                return Conflict(new { message = "A verification of this chain is already running. Try again when it finishes." });
            case AuditIntegrityGate.Outcome.TooSoon:
                Response.Headers["Retry-After"] = ((int)AuditIntegrityGate.CoolDown.TotalSeconds).ToString();
                return StatusCode(StatusCodes.Status429TooManyRequests, new { message = "Chain verification was just run. Wait a few seconds and retry." });
            default:
                return null;
        }
    }

    private Guid? GetTenantId()
    {
        var value = User.FindFirstValue("tenant_id");
        return Guid.TryParse(value, out var id) ? id : null;
    }
}
