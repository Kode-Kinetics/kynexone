using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

/// <summary>
/// Chart of accounts + payroll GL driver→account mappings + the extensible driver store. Lets
/// finance point payroll posting at their own GL codes per legal entity (company) instead of the
/// built-in defaults.
///
/// Phase 2 governance:
///  - Permission-first: finance.gl.read on reads, finance.gl.manage on account/mapping writes,
///    finance.gl.drivers.manage on driver writes (predicate authoring beyond Exact needs the
///    higher-trust finance.gl.drivers.author_predicates).
///  - Company-scoped: every companyId is authorised against the caller's entity scope; group-level
///    (companyId == null) writes require a group-scoped caller.
///  - Audited with per-change diffs (before→after), not counts.
/// </summary>
[ApiController]
[Route("api/finance/gl")]
[Authorize]
public class FinanceGlController : ControllerBase
{
    private readonly ZayraDbContext _db;
    public FinanceGlController(ZayraDbContext db) => _db = db;

    public const string PredicateAuthorPermission = "finance.gl.drivers.author_predicates";

    // ── Chart of accounts ────────────────────────────────────────────────────

    [HttpGet("accounts")]
    public async Task<IActionResult> ListAccounts([FromQuery] Guid? companyId, CancellationToken ct)
    {
        if (!HasPermission("finance.gl.read") && !HasPermission("finance.gl.manage")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        if (ScopeError(companyId) is { } err) return err;

        var scope = this.GetEntityScope();
        var q = _db.GlAccounts.AsNoTracking().Where(a => a.TenantId == tid);
        if (companyId is not null) q = q.Where(a => a.CompanyId == companyId);
        else if (!scope.IsGroupLevel)
            q = q.Where(a => a.CompanyId == null || scope.AccessibleCompanyIds.Contains(a.CompanyId!.Value));
        var accounts = await q.OrderBy(a => a.Code).ToListAsync(ct);
        return Ok(accounts.Select(a => ToDto(a, companyId)));
    }

    [HttpPost("accounts")]
    public async Task<IActionResult> CreateAccount([FromQuery] Guid? companyId, [FromBody] GlAccountRequest req, CancellationToken ct)
    {
        if (!HasPermission("finance.gl.manage")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        if (ScopeError(companyId) is { } err) return err;

        var code = (req.Code ?? "").Trim();
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { message = "Code and Name are required." });
        // IgnoreQueryFilters is intentional: the scope was authorised above (ScopeError); the WHERE
        // re-applies exact tenant+company scope so the uniqueness check sees the correct scope's rows.
        if (await _db.GlAccounts.IgnoreQueryFilters().AnyAsync(a => a.TenantId == tid && a.CompanyId == companyId && a.Code == code, ct))
            return BadRequest(new { message = $"Account code '{code}' already exists in this scope." });
        var acct = new GlAccount { TenantId = tid.Value, CompanyId = companyId, Code = code, Name = req.Name.Trim(), AccountType = req.AccountType, IsActive = req.IsActive };
        _db.GlAccounts.Add(acct);
        await WriteAudit("finance.gl.account.created", "GlAccount", acct.Id.ToString(), companyId,
            new { code = acct.Code, name = acct.Name, accountType = acct.AccountType, companyId }, ct);
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(acct, companyId));
    }

    [HttpPut("accounts/{id:guid}")]
    public async Task<IActionResult> UpdateAccount(Guid id, [FromQuery] bool force, [FromBody] GlAccountRequest req, CancellationToken ct)
    {
        if (!HasPermission("finance.gl.manage")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        // Residual Phase-1 guard graduated to server: blank name must 400, not 500 on Trim().
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { message = "Name is required." });

        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        var acct = await _db.GlAccounts.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.TenantId == tid && a.Id == id, ct);
        if (acct is null) return NotFound();
        if (ScopeError(acct.CompanyId) is { } err) return err;

        // Residual Phase-1 guard: cannot deactivate an account still referenced by a same-scope mapping
        // unless force=true. The client confirm() was advisory only.
        if (acct.IsActive && !req.IsActive && !force)
        {
            // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
            var inUse = await _db.GlAccountMappings.IgnoreQueryFilters()
                .AnyAsync(m => m.TenantId == tid && m.CompanyId == acct.CompanyId && m.AccountId == id && m.IsActive, ct);
            if (inUse) return BadRequest(new { message = "Account is referenced by an active mapping in this scope. Pass force=true to deactivate anyway.", requiresForce = true });
        }

        var before = new { acct.Name, acct.AccountType, acct.IsActive };
        acct.Name = req.Name.Trim(); acct.AccountType = req.AccountType; acct.IsActive = req.IsActive; acct.UpdatedAtUtc = DateTime.UtcNow;
        await WriteAudit("finance.gl.account.updated", "GlAccount", acct.Id.ToString(), acct.CompanyId,
            new { before, after = new { acct.Name, acct.AccountType, acct.IsActive }, companyId = acct.CompanyId }, ct);
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(acct, acct.CompanyId));
    }

    [HttpDelete("accounts/{id:guid}")]
    public async Task<IActionResult> DeleteAccount(Guid id, CancellationToken ct)
    {
        if (!HasPermission("finance.gl.manage")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        var acct = await _db.GlAccounts.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.TenantId == tid && a.Id == id, ct);
        if (acct is null) return NotFound();
        if (ScopeError(acct.CompanyId) is { } err) return err;
        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        if (await _db.GlAccountMappings.IgnoreQueryFilters().AnyAsync(m => m.TenantId == tid && m.AccountId == id, ct))
            return BadRequest(new { message = "Account is in use by a payroll mapping; remap it first." });
        _db.GlAccounts.Remove(acct);
        await WriteAudit("finance.gl.account.deleted", "GlAccount", acct.Id.ToString(), acct.CompanyId,
            new { code = acct.Code, name = acct.Name, companyId = acct.CompanyId }, ct);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Driver → account mappings ────────────────────────────────────────────

    /// <summary>Full driver catalog (scoped) with the mapping (or seeded default) for each driver.</summary>
    [HttpGet("mappings")]
    public async Task<IActionResult> ListMappings([FromQuery] Guid? companyId, CancellationToken ct)
    {
        if (!HasPermission("finance.gl.read") && !HasPermission("finance.gl.manage")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        if (ScopeError(companyId) is { } err) return err;

        var drivers = await LoadScopedDriversAsync(tid.Value, companyId, ct);
        // Company view: company mapping wins over tenant default; group view: tenant defaults only.
        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        var maps = await _db.GlAccountMappings.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.TenantId == tid && (m.CompanyId == companyId || m.CompanyId == null))
            .ToListAsync(ct);
        var byDriver = maps
            .GroupBy(m => m.DriverKey)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.CompanyId != null).First());

        var result = drivers.Select(d => new
        {
            driverKey = d.Key,
            label = d.Label,
            category = d.Category,
            accountType = d.AccountType,
            defaultAccount = $"{d.DefaultCode} - {d.DefaultName}",
            mappedAccountId = byDriver.TryGetValue(d.Key, out var m) ? m.AccountId : (Guid?)null,
            segmentCostCenterId = byDriver.TryGetValue(d.Key, out var m2) ? m2.SegmentCostCenterId : null,
            inherited = companyId is not null && byDriver.TryGetValue(d.Key, out var m3) && m3.CompanyId == null,
        });
        return Ok(result);
    }

    /// <summary>Replace the scope's driver→account mappings wholesale. CRITICAL: the wholesale delete
    /// is scoped to (Tenant, CompanyId==scope) so saving one company's overrides never wipes another
    /// company's overrides or the tenant defaults.</summary>
    [HttpPut("mappings")]
    public async Task<IActionResult> SetMappings([FromQuery] Guid? companyId, [FromBody] List<GlMappingRequest> mappings, CancellationToken ct)
    {
        if (!HasPermission("finance.gl.manage")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        if (ScopeError(companyId) is { } err) return err;

        var drivers = await LoadScopedDriversAsync(tid.Value, companyId, ct);
        var validDrivers = drivers.Select(d => d.Key).ToHashSet();

        // Account cross-scope rule: a mapping may only reference an account visible to its scope —
        // the company's own accounts + tenant defaults. A tenant-default save (companyId==null) may
        // only reference tenant-default accounts.
        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        var accountSet = await _db.GlAccounts.IgnoreQueryFilters()
            .Where(a => a.TenantId == tid && (companyId != null ? (a.CompanyId == companyId || a.CompanyId == null) : a.CompanyId == null))
            .Select(a => a.Id).ToListAsync(ct);
        var accounts = accountSet.ToHashSet();

        // Cost-center overlay must be visible to the scope (tenant-global cost centers + company scope).
        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        var validCostCenters = await _db.CostCenters.IgnoreQueryFilters()
            .Where(c => c.TenantId == tid && !c.IsDeleted && (c.CompanyId == null || c.CompanyId == companyId))
            .Select(c => c.Id).ToListAsync(ct);
        var costCenters = validCostCenters.ToHashSet();

        foreach (var m in mappings)
        {
            if (!validDrivers.Contains(m.DriverKey)) return BadRequest(new { message = $"Unknown driver '{m.DriverKey}' in this scope." });
            if (!accounts.Contains(m.AccountId)) return BadRequest(new { message = $"Account '{m.AccountId}' is not visible to this scope." });
            if (m.SegmentCostCenterId is { } cc && !costCenters.Contains(cc)) return BadRequest(new { message = $"Cost center '{cc}' is not visible to this scope." });
        }

        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        var existing = await _db.GlAccountMappings.IgnoreQueryFilters()
            .Where(m => m.TenantId == tid && m.CompanyId == companyId).ToListAsync(ct);

        // Per-driver before→after diff for the audit trail (who repointed which driver, from what to what).
        var prevByDriver = existing.ToDictionary(m => m.DriverKey, m => m.AccountId);
        var diff = mappings
            .Where(m => !prevByDriver.TryGetValue(m.DriverKey, out var old) || old != m.AccountId)
            .Select(m => new { m.DriverKey, oldAccount = prevByDriver.TryGetValue(m.DriverKey, out var o) ? o : (Guid?)null, newAccount = m.AccountId })
            .ToList();
        var removed = prevByDriver.Keys.Except(mappings.Select(m => m.DriverKey)).ToList();

        _db.GlAccountMappings.RemoveRange(existing);
        foreach (var m in mappings)
            _db.GlAccountMappings.Add(new GlAccountMapping
            {
                TenantId = tid.Value, CompanyId = companyId, DriverKey = m.DriverKey,
                AccountId = m.AccountId, SegmentCostCenterId = m.SegmentCostCenterId, IsActive = true,
            });
        await WriteAudit("finance.gl.mapping.saved", "GlAccountMapping", companyId?.ToString() ?? "tenant-default", companyId,
            new { companyId, changed = diff, removed, count = mappings.Count }, ct);
        await _db.SaveChangesAsync(ct);
        return Ok(new { count = mappings.Count, changed = diff.Count, removed = removed.Count });
    }

    // ── Driver store (extensible posting drivers) ─────────────────────────────

    [HttpGet("drivers")]
    public async Task<IActionResult> ListDrivers([FromQuery] Guid? companyId, CancellationToken ct)
    {
        if (!HasPermission("finance.gl.read") && !HasPermission("finance.gl.manage") && !HasPermission("finance.gl.drivers.manage")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        if (ScopeError(companyId) is { } err) return err;

        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        var rows = await _db.GlDrivers.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.TenantId == tid && (companyId != null ? (d.CompanyId == companyId || d.CompanyId == null) : d.CompanyId == null))
            .OrderBy(d => d.SortOrder).ThenBy(d => d.Key)
            .ToListAsync(ct);
        return Ok(rows.Select(d => ToDriverDto(d, companyId)));
    }

    [HttpPost("drivers")]
    public async Task<IActionResult> CreateDriver([FromQuery] Guid? companyId, [FromBody] GlDriverRequest req, CancellationToken ct)
    {
        if (!HasPermission("finance.gl.drivers.manage")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        if (ScopeError(companyId) is { } err) return err;

        var validation = await ValidateDriverAsync(tid.Value, companyId, req, null, ct);
        if (validation is not null) return validation;

        var driver = new GlDriver
        {
            TenantId = tid.Value, CompanyId = companyId,
            Key = req.Key.Trim(), Label = (req.Label ?? "").Trim(), Category = req.Category,
            PostingSide = req.PostingSide, AccountType = string.IsNullOrWhiteSpace(req.AccountType) ? "Expense" : req.AccountType,
            DefaultCode = (req.DefaultCode ?? "").Trim(), DefaultName = (req.DefaultName ?? "").Trim(),
            MatchSource = string.IsNullOrWhiteSpace(req.MatchSource) ? null : req.MatchSource,
            MatchMode = string.IsNullOrWhiteSpace(req.MatchMode) ? GlDriverMatchModes.Exact : req.MatchMode,
            MatchComponentCode = string.IsNullOrWhiteSpace(req.MatchComponentCode) ? null : req.MatchComponentCode.Trim(),
            EmitsEmployerExpensePair = req.EmitsEmployerExpensePair,
            PairedExpenseDriverKey = string.IsNullOrWhiteSpace(req.PairedExpenseDriverKey) ? null : req.PairedExpenseDriverKey.Trim(),
            IsSystem = false, IsActive = req.IsActive, SortOrder = req.SortOrder,
        };
        _db.GlDrivers.Add(driver);
        await WriteAudit("finance.gl.driver.created", "GlDriver", driver.Id.ToString(), companyId,
            new { driver.Key, driver.Category, driver.MatchMode, driver.MatchSource, driver.MatchComponentCode, companyId }, ct);
        await _db.SaveChangesAsync(ct);

        var shadowed = await IsFullyShadowedBySystemExactAsync(tid.Value, companyId, driver, ct);
        return Ok(new { driver = ToDriverDto(driver, companyId), warning = shadowed ? "This driver is fully shadowed by a system Exact driver and may never win routing." : null });
    }

    [HttpPut("drivers/{id:guid}")]
    public async Task<IActionResult> UpdateDriver(Guid id, [FromBody] GlDriverRequest req, CancellationToken ct)
    {
        if (!HasPermission("finance.gl.drivers.manage")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        var driver = await _db.GlDrivers.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.TenantId == tid && d.Id == id, ct);
        if (driver is null) return NotFound();
        if (ScopeError(driver.CompanyId) is { } err) return err;

        if (driver.IsSystem)
        {
            // System rows: only Label, DefaultCode/Name, SortOrder, IsActive are editable.
            var before = new { driver.Label, driver.DefaultCode, driver.DefaultName, driver.SortOrder, driver.IsActive };
            if (req.Category != driver.Category || req.PostingSide != driver.PostingSide
                || (req.MatchSource ?? "") != (driver.MatchSource ?? "") || (req.MatchMode ?? "") != driver.MatchMode
                || (req.MatchComponentCode ?? "") != (driver.MatchComponentCode ?? "")
                || req.EmitsEmployerExpensePair != driver.EmitsEmployerExpensePair
                || (req.PairedExpenseDriverKey ?? "") != (driver.PairedExpenseDriverKey ?? ""))
                return BadRequest(new { message = "System driver routing fields are read-only. Only label, default account, sort order and active state may change." });
            driver.Label = (req.Label ?? driver.Label).Trim();
            driver.DefaultCode = (req.DefaultCode ?? driver.DefaultCode).Trim();
            driver.DefaultName = (req.DefaultName ?? driver.DefaultName).Trim();
            driver.SortOrder = req.SortOrder;
            driver.IsActive = req.IsActive;
            driver.UpdatedAtUtc = DateTime.UtcNow;
            await WriteAudit("finance.gl.driver.updated", "GlDriver", driver.Id.ToString(), driver.CompanyId,
                new { system = true, before, after = new { driver.Label, driver.DefaultCode, driver.DefaultName, driver.SortOrder, driver.IsActive } }, ct);
            await _db.SaveChangesAsync(ct);
            return Ok(ToDriverDto(driver, driver.CompanyId));
        }

        // Custom row: full validation (key cannot change to a system key; predicate authoring gated).
        var v = await ValidateDriverAsync(tid.Value, driver.CompanyId, req, driver.Id, ct);
        if (v is not null) return v;
        var beforeCustom = new { driver.Key, driver.Category, driver.MatchMode, driver.MatchSource, driver.MatchComponentCode, driver.EmitsEmployerExpensePair, driver.PairedExpenseDriverKey };
        driver.Key = req.Key.Trim(); driver.Label = (req.Label ?? "").Trim(); driver.Category = req.Category;
        driver.PostingSide = req.PostingSide; driver.AccountType = string.IsNullOrWhiteSpace(req.AccountType) ? "Expense" : req.AccountType;
        driver.DefaultCode = (req.DefaultCode ?? "").Trim(); driver.DefaultName = (req.DefaultName ?? "").Trim();
        driver.MatchSource = string.IsNullOrWhiteSpace(req.MatchSource) ? null : req.MatchSource;
        driver.MatchMode = string.IsNullOrWhiteSpace(req.MatchMode) ? GlDriverMatchModes.Exact : req.MatchMode;
        driver.MatchComponentCode = string.IsNullOrWhiteSpace(req.MatchComponentCode) ? null : req.MatchComponentCode.Trim();
        driver.EmitsEmployerExpensePair = req.EmitsEmployerExpensePair;
        driver.PairedExpenseDriverKey = string.IsNullOrWhiteSpace(req.PairedExpenseDriverKey) ? null : req.PairedExpenseDriverKey.Trim();
        driver.IsActive = req.IsActive; driver.SortOrder = req.SortOrder; driver.UpdatedAtUtc = DateTime.UtcNow;
        await WriteAudit("finance.gl.driver.updated", "GlDriver", driver.Id.ToString(), driver.CompanyId,
            new { system = false, before = beforeCustom, after = new { driver.Key, driver.Category, driver.MatchMode, driver.MatchSource, driver.MatchComponentCode, driver.EmitsEmployerExpensePair, driver.PairedExpenseDriverKey } }, ct);
        await _db.SaveChangesAsync(ct);
        return Ok(ToDriverDto(driver, driver.CompanyId));
    }

    [HttpDelete("drivers/{id:guid}")]
    public async Task<IActionResult> DeleteDriver(Guid id, CancellationToken ct)
    {
        if (!HasPermission("finance.gl.drivers.manage")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        var driver = await _db.GlDrivers.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.TenantId == tid && d.Id == id, ct);
        if (driver is null) return NotFound();
        if (ScopeError(driver.CompanyId) is { } err) return err;
        if (driver.IsSystem) return BadRequest(new { message = "System drivers cannot be deleted." });
        _db.GlDrivers.Remove(driver);
        await WriteAudit("finance.gl.driver.deleted", "GlDriver", driver.Id.ToString(), driver.CompanyId,
            new { driver.Key, companyId = driver.CompanyId }, ct);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Seed defaults ──────────────────────────────────────────────────────────

    /// <summary>Seed the built-in default chart of accounts + tenant-default mappings + the 17 system
    /// drivers + client-rate allow-list for tenants starting fresh. Group-level only (writes tenant
    /// defaults, CompanyId == null).</summary>
    [HttpPost("seed-defaults")]
    public async Task<IActionResult> SeedDefaults(CancellationToken ct)
    {
        if (!HasPermission("finance.gl.manage")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        if (!this.GetEntityScope().IsGroupLevel) return Forbid();

        var seeded = await GlDriverSeeder.SeedTenantDefaultsAsync(_db, tid.Value, ct);
        // Configurability keystone: seed the data-driven pay-component defaults alongside the GL defaults
        // (same tenant-default, idempotent, add-only contract) so the two catalogs are always provisioned together.
        var seededComponents = await Zayra.Api.Infrastructure.Seed.PayComponentSeeder.SeedTenantDefaultsAsync(_db, tid.Value, ct);
        await WriteAudit("finance.gl.seed_defaults", "GlDriver", tid.ToString(), null,
            new { accounts = seeded.Accounts, mappings = seeded.Mappings, drivers = seeded.Drivers, rateDefs = seeded.RateDefinitions, payComponents = seededComponents.Components }, ct);
        await _db.SaveChangesAsync(ct);
        return Ok(new { accounts = seeded.Accounts, mappings = seeded.Mappings, drivers = seeded.Drivers, rateDefinitions = seeded.RateDefinitions, payComponents = seededComponents.Components });
    }

    // ── Control-account health ───────────────────────────────────────────────

    /// <summary>
    /// POD-B1b-FIX (re-audit #7) — the SIGN invariants of the control accounts the payroll / bonus /
    /// loan / advance journals move, evaluated against the live ledger.
    ///
    /// <para>Bonus Payable is a LIABILITY and may never carry a DEBIT balance (the fingerprint of one
    /// accrual being cleared twice); Loans / Advances Receivable are ASSETS and may never carry a CREDIT
    /// balance (the fingerprint of a receivable relieved on remittance that was never debited at
    /// disbursement). Both invariants existed only inside the test project until now, so neither failure
    /// was detectable on a live tenant — a broken control account would simply sit there.</para>
    ///
    /// <para>Accounts are resolved through <see cref="GlAccountResolver"/> for the requested entity, so a
    /// tenant that remapped them per company is checked on ITS accounts, not the shipped defaults.
    /// Read-only: computes nothing, writes nothing, and 200s with <c>healthy: false</c> rather than
    /// erroring, so a monitor can poll it.</para>
    ///
    /// <para><c>healthy</c> is judged on <c>tenantBalance</c> — the whole-tenant carrying balance, which
    /// is the view the cap arithmetic actually guarantees. <c>scopedBalance</c> is informational: it is
    /// the requested company's own rows plus the unattributed (CompanyId == null) pool every pre-POD-B1b
    /// posting carries (with <c>companyId</c> omitted it is that unattributed pool alone), and because
    /// that pool is shared, a legitimate posting order can leave one entity's slice net-negative without
    /// anything being wrong.</para>
    /// </summary>
    [HttpGet("control-accounts/health")]
    public async Task<IActionResult> ControlAccountHealth([FromQuery] Guid? companyId, CancellationToken ct)
    {
        if (!HasPermission("finance.gl.read") && !HasPermission("finance.gl.manage")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        if (ScopeError(companyId) is { } err) return err;

        var rows = await GlControlAccounts.CheckAsync(_db, tid.Value, companyId, ct);
        return Ok(new
        {
            generatedAtUtc = DateTime.UtcNow,
            companyId,
            healthy = rows.All(r => r.IsHealthy),
            violations = rows.Where(r => !r.IsHealthy).Select(r => r.Violation).ToList(),
            accounts = rows.Select(r => new
            {
                r.Driver, r.Account, r.Nature, r.ScopedBalance, r.TenantBalance, r.IsHealthy, r.Violation,
            }).ToList(),
        });
    }

    // ── GL period-close / reopen ─────────────────────────────────────────────
    //
    // POD-B1 — a Closed period BLOCKS new GL postings dated into it (enforced by PeriodCloseGuard on the
    // payroll Lock/settle/remit/void paths and the Loans/Advances/Bonus disbursement posts). Absence of
    // a Closed row = Open (open is the default; no backfill). A group-wide close (companyId omitted /
    // null) blocks every company for the period; a company-specific close blocks only that company.

    [HttpGet("periods")]
    public async Task<IActionResult> ListPeriods([FromQuery] Guid? companyId, CancellationToken ct)
    {
        if (!HasPermission("finance.gl.read") && !HasPermission("finance.gl.manage")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        if (ScopeError(companyId) is { } err) return err;

        var scope = this.GetEntityScope();
        var q = _db.GlPeriodCloses.AsNoTracking().Where(p => p.TenantId == tid);
        // A group-wide close (CompanyId == null) is relevant to every company's list view (P2-9), so a
        // company query includes tenant-wide rows too.
        if (companyId is not null) q = q.Where(p => p.CompanyId == companyId || p.CompanyId == null);
        else if (!scope.IsGroupLevel)
            q = q.Where(p => p.CompanyId == null || scope.AccessibleCompanyIds.Contains(p.CompanyId!.Value));
        var rows = await q.OrderByDescending(p => p.Period).ThenBy(p => p.CompanyId).ToListAsync(ct);
        return Ok(rows.Select(ToPeriodDto));
    }

    [HttpPost("periods/{period}/close")]
    public async Task<IActionResult> ClosePeriod(string period, [FromQuery] Guid? companyId, [FromBody] GlPeriodActionRequest? req, CancellationToken ct)
    {
        if (!HasPermission("finance.gl.manage")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        if (ScopeError(companyId) is { } err) return err;
        if (!IsValidPeriod(period)) return BadRequest(new { message = "Period must be in YYYY-MM format." });

        // IgnoreQueryFilters is intentional: scope authorised above (ScopeError); WHERE re-applies exact
        // tenant+company scope so the upsert targets the right scope's row, never another tenant's.
        var existing = await _db.GlPeriodCloses.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.TenantId == tid && p.CompanyId == companyId && p.Period == period, ct);
        if (existing is not null && existing.Status == GlPeriodStatuses.Closed)
            return Ok(ToPeriodDto(existing));   // idempotent — already closed

        var before = existing?.Status ?? GlPeriodStatuses.Open;
        if (existing is null)
        {
            existing = new GlPeriodClose { TenantId = tid.Value, CompanyId = companyId, Period = period };
            _db.GlPeriodCloses.Add(existing);
        }
        existing.Status = GlPeriodStatuses.Closed;
        existing.ClosedAtUtc = DateTime.UtcNow;
        existing.ClosedByUserId = this.GetUserId();
        existing.ClosedByName = UserName();
        existing.ClosedReason = req?.Reason;
        existing.UpdatedAtUtc = DateTime.UtcNow;
        await WriteAudit("finance.gl.period.closed", "GlPeriodClose", existing.Id.ToString(), companyId,
            new { period, companyId, before, after = GlPeriodStatuses.Closed, reason = req?.Reason }, ct);
        await _db.SaveChangesAsync(ct);
        return Ok(ToPeriodDto(existing));
    }

    [HttpPost("periods/{period}/reopen")]
    public async Task<IActionResult> ReopenPeriod(string period, [FromQuery] Guid? companyId, [FromBody] GlPeriodActionRequest? req, CancellationToken ct)
    {
        if (!HasPermission("finance.gl.manage")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        if (ScopeError(companyId) is { } err) return err;
        if (string.IsNullOrWhiteSpace(req?.Reason))
            return BadRequest(new { message = "A reason is required to reopen a closed period (audited)." });

        // IgnoreQueryFilters is intentional: same scope-authorised system read as ClosePeriod.
        var existing = await _db.GlPeriodCloses.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.TenantId == tid && p.CompanyId == companyId && p.Period == period, ct);
        if (existing is null || existing.Status != GlPeriodStatuses.Closed)
            return BadRequest(new { message = $"Period {period} is not closed in this scope." });

        existing.Status = GlPeriodStatuses.Open;
        existing.ReopenedAtUtc = DateTime.UtcNow;
        existing.ReopenedByUserId = this.GetUserId();
        existing.ReopenedByName = UserName();
        existing.ReopenReason = req.Reason;
        existing.UpdatedAtUtc = DateTime.UtcNow;
        await WriteAudit("finance.gl.period.reopened", "GlPeriodClose", existing.Id.ToString(), companyId,
            new { period, companyId, before = GlPeriodStatuses.Closed, after = GlPeriodStatuses.Open, reason = req.Reason }, ct);
        await _db.SaveChangesAsync(ct);
        return Ok(ToPeriodDto(existing));
    }

    private static bool IsValidPeriod(string period) =>
        System.Text.RegularExpressions.Regex.IsMatch(period ?? string.Empty, @"^\d{4}-(0[1-9]|1[0-2])$");

    private string? UserName() => User.FindFirstValue(ClaimTypes.Name) ?? User.FindFirstValue("name");

    private static object ToPeriodDto(GlPeriodClose p) => new
    {
        p.Id, p.CompanyId, p.Period, p.Status,
        p.ClosedAtUtc, p.ClosedByName, p.ClosedReason,
        p.ReopenedAtUtc, p.ReopenedByName, p.ReopenReason,
    };

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>Loads the drivers visible to a scope (company rows + tenant defaults; company wins per key).
    /// Falls back to the compiled system template when the store is empty so a not-yet-seeded tenant
    /// still shows the full catalog.</summary>
    private async Task<IReadOnlyList<GlDriver>> LoadScopedDriversAsync(Guid tid, Guid? companyId, CancellationToken ct)
    {
        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        var rows = await _db.GlDrivers.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.TenantId == tid && d.IsActive && (companyId != null ? (d.CompanyId == companyId || d.CompanyId == null) : d.CompanyId == null))
            .ToListAsync(ct);
        if (rows.Count == 0)
            return PayrollGlCatalog.SystemDriverSeeds(tid);
        return rows.GroupBy(d => d.Key).Select(g => g.OrderByDescending(d => d.CompanyId != null).First()).ToList();
    }

    private async Task<IActionResult?> ValidateDriverAsync(Guid tid, Guid? companyId, GlDriverRequest req, Guid? selfId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Key)) return BadRequest(new { message = "Key is required." });
        var key = req.Key.Trim();
        if (req.Category is not (GlDriverCategories.Earning or GlDriverCategories.Deduction or GlDriverCategories.Balancing))
            return BadRequest(new { message = "Category must be Earning, Deduction or Balancing." });
        if (req.PostingSide is not ("DR" or "CR")) return BadRequest(new { message = "PostingSide must be DR or CR." });
        var mode = string.IsNullOrWhiteSpace(req.MatchMode) ? GlDriverMatchModes.Exact : req.MatchMode;
        if (mode is not (GlDriverMatchModes.Exact or GlDriverMatchModes.Suffix or GlDriverMatchModes.Prefix or GlDriverMatchModes.Any))
            return BadRequest(new { message = "MatchMode must be Exact, Suffix, Prefix or Any." });
        var hasCode = !string.IsNullOrWhiteSpace(req.MatchComponentCode);
        if (mode == GlDriverMatchModes.Any && hasCode) return BadRequest(new { message = "MatchMode=Any must not carry a component code." });
        if (mode != GlDriverMatchModes.Any && !hasCode) return BadRequest(new { message = $"MatchMode={mode} requires a component code." });

        // Predicate-authoring guardrail: only Admin/vendor (author-predicates) may create non-Exact
        // predicates or employer-expense pairs — the 5% that can post an unbalanced journal.
        var authoringPredicate = mode != GlDriverMatchModes.Exact || req.EmitsEmployerExpensePair || !string.IsNullOrWhiteSpace(req.PairedExpenseDriverKey);
        if (authoringPredicate && !HasPermission(PredicateAuthorPermission))
            return BadRequest(new { message = "Authoring Suffix/Prefix/Any predicates or employer-expense pairs requires the finance.gl.drivers.author_predicates permission. Clients may create Exact-code drivers and remap accounts." });

        // Key-shadowing guard: a custom key must NEVER equal a system driver key in ANY scope of the
        // tenant (the per-scope unique index would not catch company-vs-default collisions, and the
        // balancing drivers are resolved by explicit key lookup outside the specificity rules).
        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        var collidesSystem = await _db.GlDrivers.IgnoreQueryFilters()
            .AnyAsync(d => d.TenantId == tid && d.IsSystem && d.Key == key, ct)
            || PayrollGlCatalog.Defaults.ContainsKey(key);
        if (collidesSystem) return BadRequest(new { message = $"'{key}' is a reserved system driver key. Custom drivers must use a new key." });

        // Uniqueness within scope (self excluded on update).
        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        var dupKey = await _db.GlDrivers.IgnoreQueryFilters()
            .AnyAsync(d => d.TenantId == tid && d.CompanyId == companyId && d.Key == key && (selfId == null || d.Id != selfId), ct);
        if (dupKey) return BadRequest(new { message = $"Driver key '{key}' already exists in this scope." });

        // Duplicate active predicate triple in the same category+scope.
        var srcNorm = string.IsNullOrWhiteSpace(req.MatchSource) ? null : req.MatchSource;
        var codeNorm = hasCode ? req.MatchComponentCode!.Trim() : null;
        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        var dupTriple = await _db.GlDrivers.IgnoreQueryFilters()
            .AnyAsync(d => d.TenantId == tid && d.CompanyId == companyId && d.IsActive && d.Category == req.Category
                       && d.MatchMode == mode && d.MatchSource == srcNorm && d.MatchComponentCode == codeNorm
                       && (selfId == null || d.Id != selfId), ct);
        if (dupTriple) return BadRequest(new { message = "Another active driver in this category+scope already has this match predicate." });

        if (req.EmitsEmployerExpensePair)
        {
            var pk = req.PairedExpenseDriverKey?.Trim();
            if (string.IsNullOrWhiteSpace(pk)) return BadRequest(new { message = "PairedExpenseDriverKey is required when EmitsEmployerExpensePair is set." });
            // Paired expense must resolve to a SYSTEM DR balancing driver (client cannot invent the pair target).
            // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
            var pairOk = await _db.GlDrivers.IgnoreQueryFilters()
                .AnyAsync(d => d.TenantId == tid && d.IsSystem && d.Key == pk && d.PostingSide == "DR" && (d.CompanyId == null || d.CompanyId == companyId), ct)
                || (PayrollGlCatalog.Defaults.ContainsKey(pk) && pk == "EMPLOYER_STATUTORY_EXPENSE");
            if (!pairOk) return BadRequest(new { message = "PairedExpenseDriverKey must reference an existing system DR balancing driver." });
        }
        return null;
    }

    private async Task<bool> IsFullyShadowedBySystemExactAsync(Guid tid, Guid? companyId, GlDriver driver, CancellationToken ct)
    {
        if (driver.MatchMode != GlDriverMatchModes.Exact || driver.MatchComponentCode is null) return false;
        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        return await _db.GlDrivers.IgnoreQueryFilters()
            .AnyAsync(d => d.TenantId == tid && d.IsSystem && d.IsActive && d.Category == driver.Category
                       && d.MatchMode == GlDriverMatchModes.Exact && d.MatchComponentCode == driver.MatchComponentCode
                       && (d.CompanyId == null || d.CompanyId == companyId) && d.Id != driver.Id, ct);
    }

    /// <summary>Authorises a companyId against the caller's entity scope. Fail-closed: a company write
    /// requires CanAccessCompany; a group-default (companyId==null) write requires a group scope.</summary>
    private IActionResult? ScopeError(Guid? companyId)
    {
        var scope = this.GetEntityScope();
        if (companyId is null)
            return scope.IsGroupLevel ? null : Forbid();
        return scope.CanAccessCompany(companyId) ? null : Forbid();
    }

    private bool HasPermission(string permission) =>
        User.Claims.Any(c => c.Type == "permission" && string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));

    private async Task WriteAudit(string action, string entity, string entityId, Guid? companyId, object metadata, CancellationToken ct)
    {
        var tid = this.GetTenantId();
        _db.AuditLogs.Add(new AuditLog
        {
            TenantId = tid,
            CompanyId = companyId,   // stamped from the VALIDATED scope value, never client-trusted
            Action = action,
            EntityName = entity,
            EntityId = entityId,
            UserId = this.GetUserId(),
            IpAddress = HttpContext?.Connection.RemoteIpAddress?.ToString(),
            Metadata = JsonSerializer.Serialize(metadata),
            CreatedAtUtc = DateTime.UtcNow,
        });
        await Task.CompletedTask;
    }

    private static object ToDto(GlAccount a, Guid? _) => new { a.Id, a.CompanyId, a.Code, a.Name, a.AccountType, a.IsActive };

    private static object ToDriverDto(GlDriver d, Guid? viewCompanyId) => new
    {
        d.Id, d.CompanyId, d.Key, d.Label, d.Category, d.PostingSide, d.AccountType,
        d.DefaultCode, d.DefaultName, d.MatchSource, d.MatchMode, d.MatchComponentCode,
        d.EmitsEmployerExpensePair, d.PairedExpenseDriverKey, d.IsSystem, d.IsActive, d.SortOrder,
        inherited = viewCompanyId is not null && d.CompanyId == null,
    };
}

public record GlPeriodActionRequest(string? Reason = null);
public record GlAccountRequest(string Code, string Name, string AccountType = "Expense", bool IsActive = true);
public record GlMappingRequest(string DriverKey, Guid AccountId, Guid? SegmentCostCenterId = null);
public record GlDriverRequest(
    string Key, string Label, string Category, string PostingSide, string DefaultCode, string DefaultName,
    string AccountType = "Expense", string? MatchSource = null, string MatchMode = "Exact",
    string? MatchComponentCode = null, bool EmitsEmployerExpensePair = false, string? PairedExpenseDriverKey = null,
    int SortOrder = 500, bool IsActive = true);
