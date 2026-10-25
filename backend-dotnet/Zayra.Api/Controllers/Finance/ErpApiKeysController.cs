using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Extensions;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.Swagger;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Finance;

/// <summary>
/// ERP machine access — Setup → GL &amp; rates → ERP API access.
///
/// <para>Managing keys is a tenant <b>Admin</b> who is enrolled in two-step sign-in (the same enrolment
/// <see cref="PrivilegedMfaPolicy"/> uses). Every create and revoke is audited. The key's plaintext is
/// returned exactly once, by <see cref="Create"/>; only its SHA-256 is stored. Rotation is "create a new
/// key, then revoke the old one" — there is deliberately no edit, so a key's scope can never widen after
/// the fact.</para>
///
/// <para>This controller is JWT-only: none of its actions carries <see cref="ErpApiKeyAccessAttribute"/>,
/// so a key can never manage keys.</para>
/// </summary>
[ApiController]
[Route("api/finance/gl/api-keys")]
[Authorize]
public sealed class ErpApiKeysController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly ITokenService _tokens;

    public ErpApiKeysController(ZayraDbContext db, ITokenService tokens)
    {
        _db = db;
        _tokens = tokens;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (await AdminMfaGateAsync(ct) is { } refused) return refused;
        var keys = await VisibleKeys().OrderByDescending(k => k.CreatedAtUtc).Take(200).ToListAsync(ct);
        var names = await CompanyNamesAsync(keys.Select(k => k.CompanyId), ct);
        var now = DateTime.UtcNow;
        return Ok(new { keys = keys.Select(k => Summary(k, names, now)).ToList() });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateErpApiKeyRequest? req, CancellationToken ct)
    {
        if (await AdminMfaGateAsync(ct) is { } refused) return refused;
        if (req is null) return Invalid("The request body is missing.");
        var tid = this.GetTenantId()!.Value;
        var now = DateTime.UtcNow;

        var name = (req.Name ?? string.Empty).Trim();
        if (name.Length is 0 or > 120) return Invalid("Give the key a name of up to 120 characters.", "name");

        var scopes = (req.Scopes ?? new List<string>()).Select(s => (s ?? string.Empty).Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (scopes.Count == 0 || scopes.Any(s => !ErpApiKeyScopes.All.Contains(s, StringComparer.Ordinal)))
            return Invalid("Choose what the key may do: read journal exports, and optionally confirm or reject them.", "scopes");
        if (!scopes.Contains(ErpApiKeyScopes.ExportsRead))
            return Invalid("A key must be able to read journal exports.", "scopes");

        if (req.ExpiresAtUtc is not DateTime requestedExpiry)
            return Invalid("Choose when the key expires.", "expiresAtUtc");
        var expiresAt = requestedExpiry.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(requestedExpiry, DateTimeKind.Utc) : requestedExpiry.ToUniversalTime();
        if (expiresAt <= now.AddMinutes(5))
            return Invalid("The expiry date must be in the future.", "expiresAtUtc");
        // Five minutes of grace for the clock difference between the browser that picked "365 days" and us.
        if (expiresAt > now.AddDays(ErpApiKeyDefaults.MaxLifetimeDays).AddMinutes(5))
            return Invalid($"A key can be valid for at most {ErpApiKeyDefaults.MaxLifetimeDays} days.", "expiresAtUtc");

        if (!ErpApiKeyIpAllowList.TryNormalize(req.IpAllowList, out var allowList, out var ipError))
            return Invalid(ipError!, "ipAllowList");

        if (req.CompanyId is Guid companyId)
        {
            var exists = await _db.Companies.AsNoTracking().AnyAsync(c => c.Id == companyId && c.TenantId == tid, ct);
            if (!exists || !this.GetEntityScope().CanAccessCompany(companyId))
                return Invalid("Choose a company you manage.", "companyId");
        }
        else if (!this.HasGroupEntityScope())
            return Invalid("Only a group administrator can create a key for all companies. Choose a company.", "companyId");

        var active = await _db.ErpApiKeys.CountAsync(k => k.RevokedAtUtc == null && k.ExpiresAtUtc > now, ct);
        if (active >= ErpApiKeyDefaults.MaxActiveKeysPerTenant)
            return Invalid($"This organisation already has {ErpApiKeyDefaults.MaxActiveKeysPerTenant} active keys. Revoke one you no longer use first.");

        var plaintext = ErpApiKeyCodec.Generate();
        var key = new ErpApiKey
        {
            TenantId = tid,
            CompanyId = req.CompanyId,
            Name = name,
            Prefix = ErpApiKeyCodec.DisplayPrefixOf(plaintext),
            KeyHash = _tokens.HashToken(plaintext),
            Scopes = string.Join(' ', ErpApiKeyScopes.All.Where(scopes.Contains)),
            IpAllowList = string.Join('\n', allowList),
            ExpiresAtUtc = expiresAt,
            CreatedAtUtc = now,
            CreatedByUserId = this.GetUserId()!.Value,
            CreatedByName = UserName(),
        };
        _db.ErpApiKeys.Add(key);
        WriteAudit("finance.gl.erp_api_key.created", key, new
        {
            key.Name, key.Prefix, key.CompanyId, scopes = ErpApiKeyScopes.Parse(key.Scopes), key.ExpiresAtUtc, ipAllowList = allowList,
        });
        await _db.SaveChangesAsync(ct);

        var names = await CompanyNamesAsync(new[] { key.CompanyId }, ct);
        return StatusCode(StatusCodes.Status201Created, new { key = Summary(key, names, now), plaintext });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        if (await AdminMfaGateAsync(ct) is { } refused) return refused;
        var key = await VisibleKeys().FirstOrDefaultAsync(k => k.Id == id, ct);
        if (key is null) return NotFound(new { error = "not_found", message = "That key does not exist." });

        var now = DateTime.UtcNow;
        if (key.RevokedAtUtc is null)
        {
            key.RevokedAtUtc = now;
            key.RevokedByUserId = this.GetUserId();
            key.RevokedByName = UserName();
            WriteAudit("finance.gl.erp_api_key.revoked", key, new { key.Name, key.Prefix, key.CompanyId });
            await _db.SaveChangesAsync(ct);
        }
        var names = await CompanyNamesAsync(new[] { key.CompanyId }, ct);
        return Ok(new { key = Summary(key, names, now) });
    }

    /// <summary>
    /// The OpenAPI description of the ERP endpoints — and ONLY those (the actions carrying
    /// <see cref="ErpApiKeyAccessAttribute"/>), in every environment. The full Swagger UI stays
    /// Development-only. Signed-in finance readers and Admins may fetch it to hand to their ERP team.
    /// </summary>
    [HttpGet("openapi.json")]
    public IActionResult OpenApi([FromServices] ISwaggerProvider swagger)
    {
        if (!User.IsInRole("Admin") && !User.HasPermission("finance.gl.read") && !User.HasPermission("finance.gl.manage"))
            return Forbid();
        var document = swagger.GetSwagger(ErpApiKeyDefaults.OpenApiDocument);
        return Content(document.SerializeAsJson(OpenApiSpecVersion.OpenApi3_0), "application/json");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Tenant-filtered by the query filter; a company-scoped Admin sees only their companies' keys.</summary>
    private IQueryable<ErpApiKey> VisibleKeys()
    {
        var q = _db.ErpApiKeys.AsQueryable();
        var scope = this.GetEntityScope();
        if (scope.IsGroupLevel) return q;
        var ids = scope.AccessibleCompanyIds;
        return q.Where(k => k.CompanyId != null && ids.Contains(k.CompanyId.Value));
    }

    private async Task<IActionResult?> AdminMfaGateAsync(CancellationToken ct)
    {
        var tid = this.GetTenantId();
        var uid = this.GetUserId();
        if (tid is null || uid is null) return Unauthorized();
        if (ErpApiKeyPrincipal.IsApiKey(User)) return Unauthorized();
        if (!User.IsInRole("Admin"))
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "admin_required",
                message = "Only an administrator of your organisation can manage ERP API keys.",
            });
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == uid && u.TenantId == tid, ct);
        if (user is null || !user.IsActive) return Unauthorized();
        if (!PrivilegedMfaPolicy.IsDisabledForTesting(HttpContext.RequestServices.GetService<IConfiguration>())
            && !PrivilegedMfaPolicy.IsTenantUserEnrolled(user))
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "mfa_required",
                message = "Turn on two-step sign-in for your account before managing ERP API keys.",
            });
        return null;
    }

    private async Task<Dictionary<Guid, string>> CompanyNamesAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var wanted = ids.Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToList();
        if (wanted.Count == 0) return new();
        return await _db.Companies.AsNoTracking().Where(c => wanted.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.LegalNameEn, ct);
    }

    internal static string StatusOf(ErpApiKey key, DateTime nowUtc) =>
        key.RevokedAtUtc is not null ? "Revoked" : key.ExpiresAtUtc <= nowUtc ? "Expired" : "Active";

    private static object Summary(ErpApiKey k, IReadOnlyDictionary<Guid, string> companyNames, DateTime nowUtc) => new
    {
        k.Id,
        k.Name,
        k.Prefix,
        k.CompanyId,
        companyName = k.CompanyId is Guid c && companyNames.TryGetValue(c, out var n) ? n : null,
        scopes = ErpApiKeyScopes.Parse(k.Scopes),
        k.ExpiresAtUtc,
        ipAllowList = ErpApiKeyIpAllowList.Split(k.IpAllowList),
        k.CreatedAtUtc,
        k.CreatedByName,
        k.LastUsedAtUtc,
        k.LastUsedIp,
        k.RevokedAtUtc,
        k.RevokedByName,
        status = StatusOf(k, nowUtc),
    };

    private ObjectResult Invalid(string message, string? field = null) =>
        BadRequest(new { error = "invalid_request", message, field });

    private string UserName() =>
        User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value
        ?? User.FindFirst("name")?.Value ?? "Unknown";

    private void WriteAudit(string action, ErpApiKey key, object metadata) =>
        _db.AuditLogs.Add(new AuditLog
        {
            TenantId = key.TenantId,
            CompanyId = key.CompanyId,
            Action = action,
            EntityName = nameof(ErpApiKey),
            EntityId = key.Id.ToString(),
            UserId = this.GetUserId(),
            IpAddress = HttpContext?.Connection.RemoteIpAddress?.ToString(),
            Metadata = JsonSerializer.Serialize(metadata),
            CreatedAtUtc = DateTime.UtcNow,
        });
}

public sealed record CreateErpApiKeyRequest(
    string? Name, Guid? CompanyId, List<string>? Scopes, DateTime? ExpiresAtUtc, List<string>? IpAllowList);
