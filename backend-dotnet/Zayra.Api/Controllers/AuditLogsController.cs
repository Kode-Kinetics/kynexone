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

    [HttpGet("integrity")]
    public async Task<IActionResult> Integrity(CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId();
        if (tenantId is null) return Unauthorized();
        var logs = await _db.AuditLogs
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .OrderBy(x => x.CreatedAtUtc)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

        return Ok(AuditService.VerifyChain(logs));
    }

    private Guid? GetTenantId()
    {
        var value = User.FindFirstValue("tenant_id");
        return Guid.TryParse(value, out var id) ? id : null;
    }
}
