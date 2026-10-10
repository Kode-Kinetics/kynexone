using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Payroll;

namespace Zayra.Api.Controllers.Finance;

/// <summary>
/// Read-only answer to "where will each payroll posting rule post for this company, and why?". Every
/// row comes from <see cref="GlAccountResolver"/> — the same resolver Lock posts with — so the screen can
/// never show an account the journal would not use. Permission and company scope are exactly those of
/// GET /api/finance/gl/mappings: finance.gl.read or finance.gl.manage; a company view requires access to
/// that company, and the group view (no companyId) requires a group-level caller.
/// </summary>
[ApiController]
[Route("api/finance/gl")]
[Authorize]
public class GlResolutionController : ControllerBase
{
    private readonly ZayraDbContext _db;
    public GlResolutionController(ZayraDbContext db) => _db = db;

    [HttpGet("resolve")]
    public async Task<IActionResult> Resolve([FromQuery] Guid? companyId, CancellationToken ct)
    {
        if (!HasPermission("finance.gl.read") && !HasPermission("finance.gl.manage")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        var scope = this.GetEntityScope();
        if (companyId is null ? !scope.IsGroupLevel : !scope.CanAccessCompany(companyId)) return Forbid();

        if (companyId is Guid cid)
        {
            // The scope was authorised above; this only confirms the company belongs to the caller's
            // tenant (an id from another tenant reads as not found).
            var exists = await ScopedBypass.TenantWide(_db.Companies, tid.Value,
                    "company scope was authorised above; only tenant membership of the requested company is checked")
                .AsNoTracking().AnyAsync(c => c.Id == cid, ct);
            if (!exists) return NotFound(new { message = "Company not found." });
        }

        var rows = await GlAccountResolver.DescribeAsync(_db, tid.Value, companyId, ct);
        var ctx = await GlAccountResolver.LoadAsync(_db, tid.Value, companyId, ct);
        var codes = await KnownComponentCodesAsync(tid.Value, companyId, ctx, ct);

        return Ok(new
        {
            companyId,
            rows = rows.Select(r => new
            {
                driverKey = r.DriverKey,
                label = r.Label,
                category = r.Category,
                side = r.Side,
                componentCodes = codes.TryGetValue(r.DriverKey, out var c)
                    ? c.OrderBy(x => x, StringComparer.Ordinal).ToArray()
                    : Array.Empty<string>(),
                account = new { id = r.AccountId, code = r.AccountCode, name = r.AccountName },
                source = r.Source,
                existsInChart = r.ExistsInChart,
                isActive = r.IsActive,
                problem = r.Problem,
            }),
        });
    }

    /// <summary>
    /// Best-effort: the component codes known to route to each driver — the codes the payroll engine
    /// emits itself (evaluated through the posting routing), the tenant's configured pay components that
    /// pin a driver, and every Exact-code driver predicate. Catch-all drivers list only what is known.
    /// </summary>
    private async Task<Dictionary<string, HashSet<string>>> KnownComponentCodesAsync(
        Guid tid, Guid? companyId, GlResolutionContext ctx, CancellationToken ct)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Add(string? key, string? code)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(code)) return;
            if (!map.TryGetValue(key, out var set)) map[key] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(code);
        }

        foreach (var (code, source, isDeduction) in PayrollController.KnownRoutedComponents)
            Add(PayrollController.RouteComponentToDriverKey(ctx.Drivers, code, source, isDeduction), code);

        foreach (var d in ctx.Drivers.Where(d => d.MatchMode == Models.GlDriverMatchModes.Exact))
            Add(d.Key, d.MatchComponentCode);

        // Scope authorised in Resolve; the company's own components plus the tenant defaults.
        var pinned = await ScopedBypass.TenantWide(_db.PayComponents, tid,
                "pay components that pin a GL driver for the authorised company plus the tenant defaults")
            .AsNoTracking()
            .Where(p => p.IsActive && !p.IsDeleted && !p.IsFamily && p.GlDriverKey != null
                     && (p.CompanyId == companyId || p.CompanyId == null))
            .Select(p => new { p.Code, p.GlDriverKey })
            .ToListAsync(ct);
        foreach (var p in pinned) Add(p.GlDriverKey, p.Code);
        return map;
    }

    private bool HasPermission(string permission) =>
        User.Claims.Any(c => c.Type == "permission" && string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));
}
