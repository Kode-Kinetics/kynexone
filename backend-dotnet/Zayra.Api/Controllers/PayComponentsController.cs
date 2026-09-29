using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

/// <summary>
/// F2 — the tenant pay-component catalog: the first WRITE path for <c>pay_components</c>.
///
/// <para><b>Versioned, effective-dated writes.</b> A component is a sequence of versions sharing
/// (tenant, company, code, type). Create, update and deactivate all take an <c>effectiveFrom</c> (the
/// first day of a payroll month) and act from that period FORWARD: an update closes the version in effect
/// and opens a new one; a deactivation closes it. Nothing is ever rewritten for a period that already has
/// an approved/locked run or a closed GL period — that date is refused (409 period_committed). Processed
/// runs on or after the date are reported back (<c>staleRuns</c>) because they must be reopened and
/// re-processed to pick the change up.</para>
///
/// <para><b>What can be written</b> is decided by <see cref="PayComponentPolicy"/>: tenant components are
/// Fixed / PercentOfBasic / PercentOfGross earnings and deductions with a validated GL driver. System rows
/// are read-only and statutory rows are pack-owned — never editable here.</para>
///
/// <para><b>Seed-on-first-write.</b> The first write for a tenant that has never been seeded inserts the
/// system catalog in the SAME SaveChanges, so a tenant's store can never hold a tenant component without
/// the system rows beside it. (The resolver also defends against that state — see
/// <see cref="PayComponentEngine.ResolveInEffect"/> — but the store should not reach it.)</para>
///
/// <para>Every write is one SaveChanges, hence one implicit transaction: no BeginTransaction is opened, so
/// there is no execution-strategy wrapping to get wrong (ExecutionStrategyLintTests).</para>
/// </summary>
[ApiController]
[Route("api/payroll/components")]
[Authorize]
public class PayComponentsController : ControllerBase
{
    private readonly ZayraDbContext _db;
    public PayComponentsController(ZayraDbContext db) => _db = db;

    public sealed record CreateRequest(
        string Code, string NameEn, string? NameAr, string ComponentType, string CalcMethod, decimal Value,
        string GlDriverKey, DateOnly EffectiveFrom, bool IsTaxable = false, bool EosbIncluded = false,
        int DisplayOrder = 500);

    public sealed record UpdateRequest(
        string? NameEn, string? NameAr, string? CalcMethod, decimal? Value, string? GlDriverKey,
        DateOnly EffectiveFrom, bool? IsTaxable = null, bool? EosbIncluded = null, int? DisplayOrder = null);

    public sealed record DeactivateRequest(DateOnly EffectiveFrom);

    // ── Reads ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The catalog IN EFFECT for a payroll period (default: the current month), resolved by the SAME
    /// resolver Process uses — so this is exactly the set a run for that period would pay. <c>source</c> says
    /// whether it came from the tenant's stored catalog or the compiled fallback (never-seeded tenant).</summary>
    [HttpGet]
    [HasPermission("payroll.read", "payroll.write")]
    public async Task<IActionResult> List([FromQuery] Guid? companyId, [FromQuery] string? period, CancellationToken ct)
    {
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        if (ScopeError(companyId, read: true) is { } err) return err;
        if (!TryParsePeriod(period, out var periodStart))
            return BadRequest(new { error = "invalid_period", message = "period must be YYYY-MM." });

        var rows = await ScopeRowsAsync(tid.Value, companyId, ct);
        var inEffect = PayComponentEngine.ResolveInEffect(rows, tid.Value, periodStart);
        return Ok(new
        {
            period = $"{periodStart:yyyy-MM}",
            companyId,
            source = rows.Any(r => r.IsSystem && r.IsActive && !r.IsDeleted) ? "catalog" : "compiled-fallback",
            components = inEffect
                .OrderBy(c => c.ComponentType, StringComparer.Ordinal)
                .ThenBy(c => c.DisplayOrder).ThenBy(c => c.Code, StringComparer.Ordinal)
                .Select(ToDto),
        });
    }

    /// <summary>Every version of one component code in a scope, oldest first — the audit view of its dating.</summary>
    [HttpGet("{code}/versions")]
    [HasPermission("payroll.read", "payroll.write")]
    public async Task<IActionResult> Versions(string code, [FromQuery] Guid? companyId, CancellationToken ct)
    {
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        if (ScopeError(companyId, read: true) is { } err) return err;
        var versions = await _db.PayComponents.AsNoTracking()
            .Where(c => c.TenantId == tid && c.CompanyId == companyId && c.Code == code && !c.IsDeleted)
            .OrderBy(c => c.ComponentType).ThenBy(c => c.EffectiveFrom)
            .ToListAsync(ct);
        return versions.Count == 0 ? NotFound(new { error = "not_found" }) : Ok(versions.Select(ToDto));
    }

    // ── Writes ───────────────────────────────────────────────────────────────────────────────────

    [HttpPost]
    [HasPermission("payroll.write")]
    public async Task<IActionResult> Create([FromQuery] Guid? companyId, [FromBody] CreateRequest req, CancellationToken ct)
    {
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        if (ScopeError(companyId, read: false) is { } err) return err;
        if (companyId is not null && !await CompanyExistsAsync(tid.Value, companyId.Value, ct))
            return NotFound(new { error = "company_not_found" });

        var def = new PayComponentPolicy.Definition(
            (req.Code ?? string.Empty).Trim().ToUpperInvariant(), (req.NameEn ?? string.Empty).Trim(),
            string.IsNullOrWhiteSpace(req.NameAr) ? (req.NameEn ?? string.Empty).Trim() : req.NameAr.Trim(),
            req.ComponentType ?? string.Empty, req.CalcMethod ?? string.Empty, req.Value, req.IsTaxable,
            req.EosbIncluded, (req.GlDriverKey ?? string.Empty).Trim(), req.DisplayOrder, req.EffectiveFrom);
        if (await ValidateAsync(tid.Value, companyId, def, ct) is { } invalid) return invalid;

        // One identity per code in a scope, across types — GL groups lines by code.
        var clash = await ScopedBypass.TenantWide(_db.PayComponents, tid.Value,
                "a code is one identity across every company of the tenant (GL groups lines by code)")
            .AsNoTracking()
            .Where(c => c.Code == def.Code && !c.IsDeleted
                     && (c.CompanyId == companyId || c.ComponentType != def.ComponentType))
            .Select(c => new { c.Id, c.CompanyId, c.ComponentType })
            .FirstOrDefaultAsync(ct);
        if (clash is not null)
            return Conflict(new
            {
                error = "component_exists",
                message = clash.ComponentType != def.ComponentType
                    ? $"Code '{def.Code}' is already used by a {clash.ComponentType} component in this tenant."
                    : $"Component '{def.Code}' already exists in this scope — change it with PUT (a new dated version).",
                existingId = clash.Id,
            });

        await EnsureSystemCatalogAsync(tid.Value, ct);
        var row = new PayComponent
        {
            TenantId = tid.Value, CompanyId = companyId, Code = def.Code, NameEn = def.NameEn, NameAr = def.NameAr,
            ComponentType = def.ComponentType, CalcMethod = def.CalcMethod, Value = def.Value,
            IsTaxable = def.IsTaxable, EosbIncluded = def.EosbIncluded, GosiSubject = false,
            WpsIncluded = def.ComponentType == PayComponentTypes.Earning,
            GlDriverKey = def.GlDriverKey, DisplayOrder = def.DisplayOrder,
            EffectiveFrom = def.EffectiveFrom, EffectiveTo = null,
            IsSystem = false, IsStatutory = false, IsActive = true, CreatedBy = UserId(),
        };
        _db.PayComponents.Add(row);
        Audit("payroll.component.created", row, new { def.Code, companyId, def.EffectiveFrom });
        await _db.SaveChangesAsync(ct);
        return Ok(new { component = ToDto(row), staleRuns = await PayComponentPolicy.StaleRunsAsync(_db, tid.Value, companyId, def.EffectiveFrom, ct) });
    }

    /// <summary>Changes a tenant component FROM <c>effectiveFrom</c> forward: the version in effect then is
    /// closed the day before and a new version opens (or, when that version itself starts on that date, it is
    /// amended in place — no committed run can have used it, by the period guard).</summary>
    [HttpPut("{id:guid}")]
    [HasPermission("payroll.write")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateRequest req, CancellationToken ct)
    {
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        var anchor = await _db.PayComponents.FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tid && !c.IsDeleted, ct);
        if (anchor is null) return NotFound(new { error = "not_found" });
        if (ScopeError(anchor.CompanyId, read: false) is { } err) return err;
        if (ReadOnlyError(anchor) is { } ro) return ro;

        var (current, later, lookupError) = await VersionAtAsync(anchor, req.EffectiveFrom, ct);
        if (lookupError is not null) return lookupError;
        var basis = current ?? anchor; // reactivation after a deactivation builds from the last definition

        var def = new PayComponentPolicy.Definition(
            anchor.Code, (req.NameEn ?? basis.NameEn).Trim(), (req.NameAr ?? basis.NameAr).Trim(), anchor.ComponentType,
            req.CalcMethod ?? basis.CalcMethod, req.Value ?? basis.Value ?? 0m, req.IsTaxable ?? basis.IsTaxable,
            req.EosbIncluded ?? basis.EosbIncluded, (req.GlDriverKey ?? basis.GlDriverKey ?? string.Empty).Trim(),
            req.DisplayOrder ?? basis.DisplayOrder, req.EffectiveFrom);
        if (await ValidateAsync(tid.Value, anchor.CompanyId, def, ct, skipReservedCode: true) is { } invalid) return invalid;

        PayComponent result;
        if (current is not null && current.EffectiveFrom == req.EffectiveFrom)
        {
            Apply(current, def);
            current.UpdatedAtUtc = DateTime.UtcNow; current.UpdatedBy = UserId();
            result = current;
        }
        else
        {
            if (current is not null) current.EffectiveTo = req.EffectiveFrom.AddDays(-1);
            result = new PayComponent
            {
                TenantId = anchor.TenantId, CompanyId = anchor.CompanyId, Code = anchor.Code,
                ComponentType = anchor.ComponentType, GosiSubject = false, WpsIncluded = anchor.WpsIncluded,
                EffectiveFrom = req.EffectiveFrom, EffectiveTo = null, IsSystem = false, IsStatutory = false,
                IsActive = true, CreatedBy = UserId(),
            };
            Apply(result, def);
            _db.PayComponents.Add(result);
        }
        Audit("payroll.component.versioned", result, new { anchor.Code, anchor.CompanyId, req.EffectiveFrom, previousVersionId = current?.Id });
        await _db.SaveChangesAsync(ct);
        return Ok(new { component = ToDto(result), staleRuns = await PayComponentPolicy.StaleRunsAsync(_db, tid.Value, anchor.CompanyId, req.EffectiveFrom, ct) });
    }

    /// <summary>Stops a tenant component FROM <c>effectiveFrom</c> forward. Earlier periods keep it.</summary>
    [HttpPost("{id:guid}/deactivate")]
    [HasPermission("payroll.write")]
    public async Task<IActionResult> Deactivate(Guid id, [FromBody] DeactivateRequest req, CancellationToken ct)
    {
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        var anchor = await _db.PayComponents.FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tid && !c.IsDeleted, ct);
        if (anchor is null) return NotFound(new { error = "not_found" });
        if (ScopeError(anchor.CompanyId, read: false) is { } err) return err;
        if (ReadOnlyError(anchor) is { } ro) return ro;
        if (req.EffectiveFrom.Day != 1)
            return ValidationProblemBody(new() { ["effectiveFrom"] = "EffectiveFrom must be the first day of a payroll month." });
        if (await PeriodGuardAsync(tid.Value, anchor.CompanyId, req.EffectiveFrom, ct) is { } committed) return committed;

        var (current, _, lookupError) = await VersionAtAsync(anchor, req.EffectiveFrom, ct);
        if (lookupError is not null) return lookupError;
        if (current is null)
            return Conflict(new { error = "not_in_effect", message = $"'{anchor.Code}' is not in effect from {req.EffectiveFrom:yyyy-MM}." });

        if (current.EffectiveFrom == req.EffectiveFrom)
        {
            // It never applied to any committed period (period guard) — retire the version entirely.
            current.IsDeleted = true; current.IsActive = false;
        }
        else
            current.EffectiveTo = req.EffectiveFrom.AddDays(-1);
        current.UpdatedAtUtc = DateTime.UtcNow; current.UpdatedBy = UserId();
        Audit("payroll.component.deactivated", current, new { anchor.Code, anchor.CompanyId, req.EffectiveFrom });
        await _db.SaveChangesAsync(ct);
        return Ok(new { component = ToDto(current), staleRuns = await PayComponentPolicy.StaleRunsAsync(_db, tid.Value, anchor.CompanyId, req.EffectiveFrom, ct) });
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private async Task<IActionResult?> ValidateAsync(
        Guid tenantId, Guid? companyId, PayComponentPolicy.Definition def, CancellationToken ct, bool skipReservedCode = false)
    {
        var errors = PayComponentPolicy.ValidateShape(def);
        if (skipReservedCode) errors.Remove("code"); // identity is immutable; it was validated when created
        if (errors.Count > 0) return ValidationProblemBody(errors);
        if (await PeriodGuardAsync(tenantId, companyId, def.EffectiveFrom, ct) is { } committed) return committed;
        var glError = await PayComponentPolicy.ValidateGlDriverAsync(_db, tenantId, companyId, def.ComponentType, def.GlDriverKey, ct);
        return glError is null ? null : ValidationProblemBody(new() { ["glDriverKey"] = glError });
    }

    private async Task<IActionResult?> PeriodGuardAsync(Guid tenantId, Guid? companyId, DateOnly effectiveFrom, CancellationToken ct)
    {
        var latest = await PayComponentPolicy.LatestCommittedPeriodAsync(_db, tenantId, companyId, ct);
        if (latest is { } l && effectiveFrom <= l)
            return Conflict(new
            {
                error = "period_committed",
                message = $"Payroll period {l:yyyy-MM} is already approved, locked, paid or GL-closed in this scope, so a " +
                          $"change effective {effectiveFrom:yyyy-MM} would rewrite a committed period. The earliest " +
                          $"allowed effective date is {l.AddMonths(1):yyyy-MM-dd}.",
                earliestEffectiveFrom = l.AddMonths(1),
            });
        return null;
    }

    /// <summary>The version of <paramref name="anchor"/>'s component in effect at <paramref name="at"/>, refusing
    /// when a LATER version already exists (a change is always "from here forward"; editing under a future
    /// version would silently be overridden by it).</summary>
    private async Task<(PayComponent? Current, PayComponent? Later, IActionResult? Error)> VersionAtAsync(
        PayComponent anchor, DateOnly at, CancellationToken ct)
    {
        var versions = await _db.PayComponents
            .Where(c => c.TenantId == anchor.TenantId && c.CompanyId == anchor.CompanyId && c.Code == anchor.Code
                     && c.ComponentType == anchor.ComponentType && !c.IsDeleted && c.IsActive)
            .ToListAsync(ct);
        var later = versions.Where(v => v.EffectiveFrom is { } f && f > at).OrderBy(v => v.EffectiveFrom).FirstOrDefault();
        if (later is not null)
            return (null, later, Conflict(new
            {
                error = "later_version_exists",
                message = $"'{anchor.Code}' already has a version effective {later.EffectiveFrom:yyyy-MM}. Change that version, " +
                          "or deactivate it first — a change always applies from its date forward.",
                laterVersionId = later.Id,
            }));
        var current = versions.FirstOrDefault(v => v.IsInEffect(at));
        return (current, null, null);
    }

    private IActionResult? ReadOnlyError(PayComponent c)
    {
        if (c.IsStatutory || PayComponentGuard.IsStatutoryComponentCode(c.Code))
            return Conflict(new { error = "statutory_component", message = "Statutory components are owned by the country pack (GOSI/GPSSA/GRSIA) and cannot be changed here." });
        if (c.IsSystem)
            return Conflict(new { error = "system_component", message = "System components reproduce the standard payslip and are read-only. Add a tenant component instead." });
        return null;
    }

    private static void Apply(PayComponent c, PayComponentPolicy.Definition d)
    {
        c.NameEn = d.NameEn; c.NameAr = string.IsNullOrWhiteSpace(d.NameAr) ? d.NameEn : d.NameAr;
        c.CalcMethod = d.CalcMethod; c.Value = d.Value; c.IsTaxable = d.IsTaxable; c.EosbIncluded = d.EosbIncluded;
        c.GlDriverKey = d.GlDriverKey; c.DisplayOrder = d.DisplayOrder;
    }

    private async Task EnsureSystemCatalogAsync(Guid tenantId, CancellationToken ct)
    {
        var hasSystem = await ScopedBypass.TenantWide(_db.PayComponents, tenantId,
                "existence probe over the tenant-default system rows (CompanyId == null)")
            .AnyAsync(c => c.CompanyId == null && c.IsSystem, ct);
        if (!hasSystem) await PayComponentSeeder.SeedTenantDefaultsAsync(_db, tenantId, ct);
    }

    private Task<List<PayComponent>> ScopeRowsAsync(Guid tenantId, Guid? companyId, CancellationToken ct)
        => ScopedBypass.TenantWide(_db.PayComponents, tenantId,
                "the view must be EXACTLY what a run resolves: the company's rows plus the tenant defaults")
            .AsNoTracking()
            .Where(c => c.CompanyId == companyId || c.CompanyId == null)
            .ToListAsync(ct);

    private Task<bool> CompanyExistsAsync(Guid tenantId, Guid companyId, CancellationToken ct)
        => _db.Companies.AnyAsync(c => c.TenantId == tenantId && c.Id == companyId, ct);

    private IActionResult? ScopeError(Guid? companyId, bool read)
    {
        var scope = this.GetEntityScope();
        if (companyId is null)
            // Tenant-wide components apply to every company, so only a group-level user may WRITE them.
            return read || scope.IsGroupLevel ? null : Forbid();
        return scope.CanAccessCompany(companyId) ? null : Forbid();
    }

    private static bool TryParsePeriod(string? period, out DateOnly start)
    {
        if (string.IsNullOrWhiteSpace(period))
        {
            var t = DateOnly.FromDateTime(DateTime.UtcNow);
            start = new DateOnly(t.Year, t.Month, 1);
            return true;
        }
        return DateOnly.TryParseExact(period.Trim() + "-01", "yyyy-MM-dd", out start);
    }

    private IActionResult ValidationProblemBody(Dictionary<string, string> errors)
        => UnprocessableEntity(new { error = "invalid_pay_component", errors });

    private Guid? UserId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id) ? id : null;

    /// <summary>Catalog changes are payroll configuration: recorded on the tamper-evident payroll audit chain
    /// (sealed by ZayraDbContext on the same SaveChanges as the change itself).</summary>
    private void Audit(string action, PayComponent c, object details)
    {
        _db.PayrollAuditLogs.Add(new PayrollAuditLog
        {
            TenantId = c.TenantId, Action = action, EntityName = "PayComponent", EntityId = c.Id.ToString(),
            UserId = UserId(),
            MetadataJson = JsonSerializer.Serialize(new
            {
                ip = HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                data = details,
                version = ToDto(c),
            }),
        });
    }

    private static object ToDto(PayComponent c) => new
    {
        c.Id, c.CompanyId, c.Code, c.NameEn, c.NameAr, c.ComponentType, c.CalcMethod, c.Value, c.StructureField,
        c.ProviderKey, c.IsTaxable, c.GosiSubject, c.WpsIncluded, c.EosbIncluded, c.GlDriverKey, c.DisplayOrder,
        c.EffectiveFrom, c.EffectiveTo, c.IsSystem, c.IsStatutory, c.IsActive,
        editable = !c.IsSystem && !c.IsStatutory,
    };
}
