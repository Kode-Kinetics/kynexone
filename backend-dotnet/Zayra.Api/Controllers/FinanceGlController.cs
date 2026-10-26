using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Finance;
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
///    higher-trust finance.gl.drivers.author_predicates; pointing a driver's default at an account
///    also needs finance.gl.manage). Reopening a closed period needs finance.gl.period.reopen and
///    is never done by the person who closed it.
///  - Company-scoped: every companyId is authorised against the caller's entity scope; group-level
///    (companyId == null) writes require a group-scoped caller (judged on the caller's grant, not on
///    the company selected in the switcher).
///  - Journals store account LABELS as text ("code - name"), so an account whose label any journal
///    line carries can no longer change code or name, and an account still referenced anywhere can
///    be neither deactivated nor deleted.
///  - Audited with per-change diffs (before→after), not counts.
/// </summary>
[ApiController]
[Route("api/finance/gl")]
[Authorize]
public partial class FinanceGlController : ControllerBase
{
    private readonly ZayraDbContext _db;
    public FinanceGlController(ZayraDbContext db) => _db = db;

    public const string PredicateAuthorPermission = "finance.gl.drivers.author_predicates";
    public const string PeriodReopenPermission = "finance.gl.period.reopen";

    /// <summary>The only account types the chart accepts.</summary>
    public static readonly IReadOnlyList<string> AccountTypes = new[] { "Asset", "Liability", "Equity", "Income", "Expense" };
    private static readonly Regex CodePattern = new(@"^[A-Za-z0-9.\-]{1,30}$", RegexOptions.CultureInvariant);
    public const int ImportMaxBytes = 2 * 1024 * 1024;
    public const int ImportMaxRows = 5000;
    private static readonly string[] CsvHeaders = { "Code", "Name", "Type", "IsActive" };
    /// <summary>Optional chart CSV column: the account string the client's ERP expects. A file
    /// without it leaves every account's ERP code unchanged; an empty cell clears it.</summary>
    public const string ExternalCodeHeader = "ExternalCode";
    private static readonly string[] ExportHeaders = { "Code", "Name", "Type", "IsActive", ExternalCodeHeader };
    public const int ExternalCodeMaxLength = 60;
    /// <summary>Cost-accounting M1: the cost-centre rule an account may carry.</summary>
    public static readonly IReadOnlyList<string> CostCenterRules = new[] { "Required", "Optional", "NotAllowed" };
    /// <summary>Functional classes of a cost centre. CAPITALISABLE is stored but refused for posting in v1.</summary>
    public static readonly IReadOnlyList<string> FunctionalClasses = new[] { "COGS", "SELLING", "G_AND_A", "R_AND_D", "OPERATIONS", "CAPITALISABLE" };
    public const string ReservedFunctionalClass = "CAPITALISABLE";
    /// <summary>Only profit-and-loss accounts may require a cost centre (the contract's Expense and Revenue; the chart calls revenue "Income").</summary>
    public static bool IsProfitAndLossType(string? type) =>
        string.Equals(type, "Expense", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "Income", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "Revenue", StringComparison.OrdinalIgnoreCase);
    private static readonly Regex ExternalCodePattern = new(@"^[A-Za-z0-9.\-_/ ]{0,60}$", RegexOptions.CultureInvariant);

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
        if (CodeProblem(code) is { } codeErr) return BadRequest(new { code = "GL_ACCOUNT_CODE_INVALID", message = codeErr });
        if (CanonicalAccountType(req.AccountType) is not { } type) return BadRequest(AccountTypeRefusal(req.AccountType));
        if (ExternalCodeProblem(req.ExternalCode) is { } extErr) return BadRequest(new { code = "GL_EXTERNAL_CODE_INVALID", message = extErr });
        var createRule = req.CostCenterRule ?? "Optional";
        if (RuleProblem(createRule, type) is { } ruleErr) return ruleErr;
        // IgnoreQueryFilters is intentional: the scope was authorised above (ScopeError); the WHERE
        // re-applies exact tenant+company scope so the uniqueness check sees the correct scope's rows.
        if (await _db.GlAccounts.IgnoreQueryFilters().AnyAsync(a => a.TenantId == tid && a.CompanyId == companyId && a.Code == code, ct))
            return BadRequest(new { message = $"Account code '{code}' already exists in this scope." });
        var acct = new GlAccount { TenantId = tid.Value, CompanyId = companyId, Code = code, Name = req.Name.Trim(), AccountType = type, IsActive = req.IsActive,
            ExternalCode = NormalizeExternalCode(req.ExternalCode), CostCenterRule = createRule };
        _db.GlAccounts.Add(acct);
        await WriteAudit("finance.gl.account.created", "GlAccount", acct.Id.ToString(), companyId,
            new { code = acct.Code, name = acct.Name, accountType = acct.AccountType, acct.IsActive, externalCode = acct.ExternalCode, costCenterRule = acct.CostCenterRule, companyId }, ct);
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(acct, companyId));
    }

    /// <summary>
    /// Updates an account. <paramref name="force"/> is accepted for compatibility and ignored: a
    /// deactivation that would leave a live mapping (in ANY company) or a driver default pointing at an
    /// inactive account is refused, because posting would silently fall back to a different account.
    /// </summary>
    [HttpPut("accounts/{id:guid}")]
    public async Task<IActionResult> UpdateAccount(Guid id, [FromQuery] bool force, [FromBody] GlAccountRequest req, CancellationToken ct)
    {
        if (!HasPermission("finance.gl.manage")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        // A request that carries a cost-centre rule is a posting-rule change: it runs as the company's sole cost-attribution
        // writer (advisory lock + transaction) so the M-7 check below cannot race an Approve. Every other edit is unchanged.
        if (req.CostCenterRule is null) return await UpdateAccountCoreAsync(tid.Value, id, req, ct);
        // IgnoreQueryFilters is intentional: system/config read — scope authorised below; WHERE re-applies exact tenant scope.
        var owner = await ScopedBypass.TenantWide(_db.GlAccounts, tid.Value, "Resolve the account's company before taking the cost-accounting lock; scope is authorised right after.")
            .AsNoTracking().Where(a => a.Id == id).Select(a => new { a.CompanyId }).FirstOrDefaultAsync(ct);
        if (owner is null) return NotFound();
        if (ScopeError(owner.CompanyId) is { } ownerErr) return ownerErr;
        return await CostAccountingGuards.SerializeAsync(_db, tid.Value, owner.CompanyId, () => UpdateAccountCoreAsync(tid.Value, id, req, ct), ct);
    }

    private async Task<IActionResult> UpdateAccountCoreAsync(Guid tidValue, Guid id, GlAccountRequest req, CancellationToken ct)
    {
        Guid? tid = tidValue;
        // Residual Phase-1 guard graduated to server: blank name must 400, not 500 on Trim().
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { message = "Name is required." });
        if (CanonicalAccountType(req.AccountType) is not { } type) return BadRequest(AccountTypeRefusal(req.AccountType));
        if (ExternalCodeProblem(req.ExternalCode) is { } extErr) return BadRequest(new { code = "GL_EXTERNAL_CODE_INVALID", message = extErr });

        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        var acct = await _db.GlAccounts.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.TenantId == tid && a.Id == id, ct);
        if (acct is null) return NotFound();
        if (ScopeError(acct.CompanyId) is { } err) return err;

        var newCode = string.IsNullOrWhiteSpace(req.Code) ? acct.Code : req.Code.Trim();
        var newName = req.Name.Trim();
        if (await AccountChangeProblemAsync(tid.Value, acct, newCode, newName, type, req.IsActive, ct) is { } problem)
            return problem;

        var newRule = req.CostCenterRule ?? acct.CostCenterRule;
        if (RuleProblem(newRule, type) is { } ruleErr) return ruleErr;
        var ruleChanged = !string.Equals(newRule, acct.CostCenterRule, StringComparison.Ordinal);
        if (ruleChanged && await CostAccountingGuards.HasRunAwaitingFinanceOrLockAsync(_db, tid.Value, acct.CompanyId, ct))
            return Conflict(CostAccountingGuards.LockedIntoApprovedRunBody);

        var before = new { acct.Code, acct.Name, acct.AccountType, acct.IsActive, acct.ExternalCode, costCenterRule = acct.CostCenterRule };
        // A re-code (allowed only while no journal names the account) must carry the driver defaults that
        // name the old code with it, in the same SaveChanges; otherwise an unmapped driver keeps resolving
        // the old code, which is no longer in the chart (and Lock now refuses that).
        var repointedDrivers = !string.Equals(newCode, acct.Code, StringComparison.Ordinal)
            ? await RepointDriverDefaultsAsync(tid.Value, acct, newCode, newName, ct)
            : new List<string>();
        acct.Code = newCode; acct.Name = newName; acct.AccountType = type; acct.IsActive = req.IsActive; acct.UpdatedAtUtc = DateTime.UtcNow;
        acct.CostCenterRule = newRule;
        // null = leave the ERP code as it is; "" (or whitespace) = clear it.
        if (req.ExternalCode is not null) acct.ExternalCode = NormalizeExternalCode(req.ExternalCode);
        await WriteAudit("finance.gl.account.updated", "GlAccount", acct.Id.ToString(), acct.CompanyId,
            new { before, after = new { acct.Code, acct.Name, acct.AccountType, acct.IsActive, acct.ExternalCode, costCenterRule = acct.CostCenterRule }, companyId = acct.CompanyId,
                  driverDefaultsRepointed = repointedDrivers }, ct);
        if (ruleChanged)
            await WriteAudit("finance.gl.account.rule_changed", "GlAccount", acct.Id.ToString(), acct.CompanyId,
                new { accountCode = acct.Code, before = new { costCenterRule = before.costCenterRule }, after = new { costCenterRule = acct.CostCenterRule }, companyId = acct.CompanyId }, ct);
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
        var refs = await AccountReferencesAsync(tid.Value, acct, ct);
        if (refs.Count > 0)
            return Conflict(new { code = "GL_ACCOUNT_IN_USE", message = $"Account '{Label(acct)}' is still used by {string.Join("; ", refs)}. Remap those first.", references = refs });
        if (await IsLabelPostedAsync(tid.Value, Label(acct), ct))
            return Conflict(new { code = "GL_ACCOUNT_POSTED", message = $"Journal lines already name '{Label(acct)}', so it cannot be deleted. Deactivate it instead once nothing maps to it." });
        _db.GlAccounts.Remove(acct);
        await WriteAudit("finance.gl.account.deleted", "GlAccount", acct.Id.ToString(), acct.CompanyId,
            new { code = acct.Code, name = acct.Name, accountType = acct.AccountType, companyId = acct.CompanyId }, ct);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>The chart of accounts for one scope as CSV (Code, Name, Type, IsActive, ExternalCode).</summary>
    [HttpGet("accounts/export")]
    public async Task<IActionResult> ExportAccounts([FromQuery] Guid? companyId, CancellationToken ct)
    {
        if (!HasPermission("finance.gl.read") && !HasPermission("finance.gl.manage")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        if (ScopeError(companyId) is { } err) return err;
        var rows = await ScopeAccounts(tid.Value, companyId).AsNoTracking().OrderBy(a => a.Code).ToListAsync(ct);
        var csv = Csv.Build(ExportHeaders, rows.Select(a => (IReadOnlyList<object?>)new object?[] { a.Code, a.Name, a.AccountType, a.IsActive ? "true" : "false", a.ExternalCode ?? "" }));
        var suffix = companyId is null ? "group" : companyId.Value.ToString("N")[..8];
        return File(Encoding.UTF8.GetBytes(csv), "text/csv", $"chart_of_accounts_{suffix}_{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    /// <summary>
    /// Imports a chart of accounts for one scope from CSV (Code, Name, Type, IsActive, optional
    /// ExternalCode — a file without that column leaves ERP codes unchanged). ALL OR NOTHING:
    /// every row is validated first against the same rules as the single-account endpoints (code
    /// format, account type, posted-label immutability, in-use deactivation); any error writes nothing.
    /// <paramref name="dryRun"/> defaults to true so a file is always previewed before it is applied.
    /// </summary>
    [HttpPost("accounts/import")]
    [RequestSizeLimit(ImportMaxBytes + 64 * 1024)]
    public async Task<IActionResult> ImportAccounts([FromQuery] Guid? companyId, [FromQuery] bool? dryRun, IFormFile? file, CancellationToken ct)
    {
        if (!HasPermission("finance.gl.manage")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        if (ScopeError(companyId) is { } err) return err;
        var isDryRun = dryRun ?? true;

        if (file is null || file.Length == 0)
            return BadRequest(new { message = "Attach the CSV as the multipart field 'file'." });
        if (file.Length > ImportMaxBytes)
            return BadRequest(new { message = $"The file is larger than {ImportMaxBytes / (1024 * 1024)} MB. Split it and import the parts." });

        string content;
        using (var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            content = await reader.ReadToEndAsync(ct);

        List<Dictionary<string, string>> rows;
        try { rows = Csv.Parse(content); }
        catch (CsvShapeException ex) { return BadRequest(new { message = ex.Message }); }
        var headerLine = content.Split('\n').FirstOrDefault()?.Trim().TrimStart('﻿') ?? "";
        var headers = Csv.SplitRow(headerLine).Select(h => h.Trim()).ToList();
        var missing = CsvHeaders.Where(h => !headers.Contains(h, StringComparer.OrdinalIgnoreCase)).ToList();
        if (missing.Count > 0)
            return BadRequest(new { message = $"The file is missing the column(s) {string.Join(", ", missing)}. Expected: {string.Join(",", CsvHeaders)}." });
        if (rows.Count > ImportMaxRows)
            return BadRequest(new { message = $"The file has {rows.Count} rows; the limit is {ImportMaxRows}." });
        var hasExternalCode = headers.Contains(ExternalCodeHeader, StringComparer.OrdinalIgnoreCase);

        // IgnoreQueryFilters-free: ScopeAccounts pins tenant + exact scope.
        var existing = await ScopeAccounts(tid.Value, companyId).ToListAsync(ct);
        var byCode = existing.GroupBy(a => a.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var errors = new List<object>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var creates = new List<GlAccount>();
        var updates = new List<(GlAccount Account, object Before, string Name, string Type, bool Active, string? External)>();
        var unchanged = 0;

        for (var i = 0; i < rows.Count; i++)
        {
            var rowNum = i + 2;
            var r = rows[i];
            var code = r.GetValueOrDefault("Code", "").Trim();
            var name = r.GetValueOrDefault("Name", "").Trim();
            var typeRaw = r.GetValueOrDefault("Type", "").Trim();
            var activeRaw = r.GetValueOrDefault("IsActive", "").Trim();
            // The export prefixes a cell that starts with '-' with an apostrophe (formula-injection
            // guard), and an apostrophe is never a legal ERP-code character, so it is dropped here.
            var externalRaw = hasExternalCode ? r.GetValueOrDefault(ExternalCodeHeader, "").Trim() : null;
            if (externalRaw is not null && externalRaw.StartsWith('\'')) externalRaw = externalRaw[1..].Trim();
            void Fail(string message) => errors.Add(new { row = rowNum, code, message });

            if (CodeProblem(code) is { } codeErr) { Fail(codeErr); continue; }
            if (!seen.Add(code)) { Fail($"Code '{code}' appears more than once in this file."); continue; }
            if (name.Length == 0) { Fail("Name is required."); continue; }
            if (CanonicalAccountType(typeRaw) is not { } type) { Fail($"Type '{typeRaw}' is not one of {string.Join(", ", AccountTypes)}."); continue; }
            bool active;
            if (activeRaw.Length == 0 || activeRaw.Equals("true", StringComparison.OrdinalIgnoreCase) || activeRaw == "1" || activeRaw.Equals("yes", StringComparison.OrdinalIgnoreCase)) active = true;
            else if (activeRaw.Equals("false", StringComparison.OrdinalIgnoreCase) || activeRaw == "0" || activeRaw.Equals("no", StringComparison.OrdinalIgnoreCase)) active = false;
            else { Fail($"IsActive '{activeRaw}' must be true or false."); continue; }
            if (ExternalCodeProblem(externalRaw) is { } extErr) { Fail(extErr); continue; }

            if (!byCode.TryGetValue(code, out var acct))
            {
                creates.Add(new GlAccount { TenantId = tid.Value, CompanyId = companyId, Code = code, Name = name, AccountType = type, IsActive = active,
                    ExternalCode = NormalizeExternalCode(externalRaw) });
                continue;
            }
            var external = externalRaw is null ? acct.ExternalCode : NormalizeExternalCode(externalRaw);
            if (acct.Name == name && string.Equals(acct.AccountType, type, StringComparison.Ordinal) && acct.IsActive == active
                && string.Equals(acct.ExternalCode, external, StringComparison.Ordinal)) { unchanged++; continue; }
            if (await AccountChangeProblemMessageAsync(tid.Value, acct, acct.Code, name, type, active, ct) is { } problem) { Fail(problem); continue; }
            updates.Add((acct, new { acct.Code, acct.Name, acct.AccountType, acct.IsActive, acct.ExternalCode }, name, type, active, external));
        }

        var body = new { dryRun = isDryRun, created = creates.Count, updated = updates.Count, unchanged, errors };
        if (errors.Count > 0) return isDryRun ? Ok(body) : UnprocessableEntity(body);
        if (isDryRun) return Ok(body);

        foreach (var a in creates) _db.GlAccounts.Add(a);
        foreach (var (a, _, name, type, active, external) in updates)
        {
            a.Name = name; a.AccountType = type; a.IsActive = active; a.ExternalCode = external; a.UpdatedAtUtc = DateTime.UtcNow;
        }
        await WriteAudit("finance.gl.account.imported", "GlAccount", companyId?.ToString() ?? "tenant-default", companyId,
            new
            {
                companyId, fileName = file.FileName, created = creates.Select(a => new { a.Code, a.Name, a.AccountType, a.IsActive, a.ExternalCode }),
                updated = updates.Select(u => new { before = u.Before, after = new { u.Account.Code, Name = u.Name, AccountType = u.Type, IsActive = u.Active, ExternalCode = u.External } }),
                unchanged,
            }, ct);
        await _db.SaveChangesAsync(ct);
        return Ok(body);
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
        var byDriver = maps.Where(m => m.FunctionalClass == null)
            .GroupBy(m => m.DriverKey)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.CompanyId != null).First());
        // Functional-class rows ride beside the driver's classless row; company beats tenant for the same (driver, class).
        var classRulesByDriver = maps.Where(m => m.FunctionalClass != null)
            .GroupBy(m => (m.DriverKey, m.FunctionalClass))
            .Select(g => g.OrderByDescending(m => m.CompanyId != null).First())
            .GroupBy(m => m.DriverKey)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.FunctionalClass, StringComparer.Ordinal)
                .Select(m => new { functionalClass = m.FunctionalClass, accountId = m.AccountId, inherited = companyId is not null && m.CompanyId == null }).ToList());

        var result = drivers.Select(d => new
        {
            driverKey = d.Key,
            label = d.Label,
            category = d.Category,
            accountType = d.AccountType,
            allowedAccountTypes = AllowedAccountTypes(d),
            defaultAccount = $"{d.DefaultCode} - {d.DefaultName}",
            mappedAccountId = byDriver.TryGetValue(d.Key, out var m) ? m.AccountId : (Guid?)null,
            segmentCostCenterId = byDriver.TryGetValue(d.Key, out var m2) ? m2.SegmentCostCenterId : null,
            classRules = classRulesByDriver.TryGetValue(d.Key, out var cr) ? cr.Cast<object>().ToList() : new List<object>(),
            inherited = companyId is not null && byDriver.TryGetValue(d.Key, out var m3) && m3.CompanyId == null,
        });
        return Ok(result);
    }

    /// <summary>Replace the scope's driver→account mappings wholesale. CRITICAL: the wholesale delete
    /// is scoped to (Tenant, CompanyId==scope) so saving one company's overrides never wipes another
    /// company's overrides or the tenant defaults. A new or changed mapping must point at an ACTIVE
    /// account whose type the driver accepts (earnings → Expense, deductions → the type the catalog
    /// declares, NET_PAYABLE → Liability).</summary>
    [HttpPut("mappings")]
    public async Task<IActionResult> SetMappings([FromQuery] Guid? companyId, [FromBody] List<GlMappingRequest> mappings, CancellationToken ct, [FromQuery] string? reason = null)
    {
        if (!HasPermission("finance.gl.manage")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        if (ScopeError(companyId) is { } err) return err;
        mappings ??= new List<GlMappingRequest>();
        // The whole save is one cost-attribution write: advisory lock + transaction, so the M-7 check cannot race an Approve.
        return await CostAccountingGuards.SerializeAsync(_db, tid.Value, companyId, () => SetMappingsCoreAsync(tid.Value, companyId, mappings, reason, ct), ct);
    }

    private static string? NormalizeClass(string? c) => string.IsNullOrWhiteSpace(c) ? null : c.Trim();

    private async Task<IActionResult> SetMappingsCoreAsync(Guid tid, Guid? companyId, List<GlMappingRequest> mappings, string? reason, CancellationToken ct)
    {
        // (driver, class) is the key (AM-16); a driver may carry one classless row plus one row per class.
        var dupes = mappings.GroupBy(m => ((m.DriverKey ?? "").Trim(), NormalizeClass(m.FunctionalClass)), new DriverClassComparer())
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dupes.Count > 0)
        {
            var classless = dupes.Where(d => d.Item2 is null).Select(d => d.Item1).ToList();
            if (classless.Count > 0)
                return BadRequest(new { code = "GL_MAPPING_DUPLICATE_DRIVER", message = $"Each driver may be mapped once. Repeated: {string.Join(", ", classless)}." });
            return BadRequest(new { code = "GL_MAPPING_DUPLICATE_DRIVER_CLASS", message = $"Each driver may be mapped once per functional class. Repeated: {string.Join(", ", dupes.Select(d => $"{d.Item1} ({d.Item2})"))}." });
        }

        var drivers = await LoadScopedDriversAsync(tid, companyId, ct);
        var driverByKey = drivers.ToDictionary(d => d.Key, StringComparer.Ordinal);

        // Account cross-scope rule: a mapping may only reference an account visible to its scope —
        // the company's own accounts + tenant defaults. A tenant-default save (companyId==null) may
        // only reference tenant-default accounts.
        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        var accountSet = await _db.GlAccounts.IgnoreQueryFilters()
            .Where(a => a.TenantId == tid && (companyId != null ? (a.CompanyId == companyId || a.CompanyId == null) : a.CompanyId == null))
            .Select(a => new { a.Id, a.Code, a.Name, a.AccountType, a.IsActive, a.CostCenterRule }).ToListAsync(ct);
        var accounts = accountSet.ToDictionary(a => a.Id);

        // Fixed cost centre (AM-4, AM-16): company mappings only, and the cost centre must be a live, active row of THAT company.
        var fixedIds = mappings.Where(m => m.SegmentCostCenterId.HasValue).Select(m => m.SegmentCostCenterId!.Value).Distinct().ToList();
        var fixedRows = fixedIds.Count == 0
            ? new Dictionary<Guid, (Guid? CompanyId, bool IsActive, bool IsDeleted)>()
            : (await ScopedBypass.TenantWide(_db.CostCenters, tid, "A mapping's fixed cost centre is validated against its own company; foreign and group-level rows are rejected here.")
                .AsNoTracking().Where(c => fixedIds.Contains(c.Id)).Select(c => new { c.Id, c.CompanyId, c.IsActive, c.IsDeleted }).ToListAsync(ct))
                .ToDictionary(c => c.Id, c => (c.CompanyId, c.IsActive, c.IsDeleted));

        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        var existing = await _db.GlAccountMappings.IgnoreQueryFilters()
            .Where(m => m.TenantId == tid && m.CompanyId == companyId).ToListAsync(ct);
        var prevByKey = existing.GroupBy(m => (m.DriverKey, m.FunctionalClass), new DriverClassComparer())
            .ToDictionary(g => g.Key, g => g.First(), new DriverClassComparer());

        foreach (var m in mappings)
        {
            var cls = NormalizeClass(m.FunctionalClass);
            if (!driverByKey.TryGetValue(m.DriverKey ?? "", out var driver)) return BadRequest(new { message = $"Unknown driver '{m.DriverKey}' in this scope." });
            if (!accounts.TryGetValue(m.AccountId, out var account)) return BadRequest(new { message = $"Account '{m.AccountId}' is not visible to this scope." });
            if (cls is not null && !FunctionalClasses.Contains(cls, StringComparer.Ordinal))
                return UnprocessableEntity(CostAccountingGuards.Error("functional_class_invalid", $"'{cls}' is not a functional class. Use one of {string.Join(", ", FunctionalClasses.Where(c => c != ReservedFunctionalClass))}.",
                    "التصنيف الوظيفي غير صالح."));
            if (cls == ReservedFunctionalClass)
                return UnprocessableEntity(CostAccountingGuards.Error("functional_class_reserved", "The Capitalisable class is reserved and cannot be used for posting yet.",
                    "التصنيف الوظيفي «قابل للرسملة» محجوز ولا يمكن استخدامه في الترحيل بعد."));
            if (m.SegmentCostCenterId is { } cc)
            {
                if (cls is not null)
                    return UnprocessableEntity(CostAccountingGuards.Error("fixed_cost_centre_with_class", "A posting rule is either for one functional class or fixed to one cost centre, not both.",
                        "قاعدة الترحيل إما لتصنيف وظيفي واحد أو مثبّتة على مركز تكلفة واحد، وليس كلاهما."));
                if (companyId is null)
                    return UnprocessableEntity(CostAccountingGuards.Error("fixed_cost_centre_requires_company", "Fixed cost centres are set per company. Switch to a company to set one.",
                        "تُحدَّد مراكز التكلفة الثابتة لكل شركة. انتقل إلى شركة لتحديد مركز."));
                if (!fixedRows.TryGetValue(cc, out var row) || row.CompanyId != companyId || !row.IsActive || row.IsDeleted)
                    return UnprocessableEntity(CostAccountingGuards.Error("fixed_cost_centre_invalid", "The fixed cost centre must be an active cost centre of this company.",
                        "يجب أن يكون مركز التكلفة الثابت مركزًا نشطًا تابعًا لهذه الشركة."));
                // M-3 (oracle S-B2): a fixed cost centre on an account that does not accept one could never post, so it is refused here.
                if (string.Equals(account.CostCenterRule, "NotAllowed", StringComparison.Ordinal))
                    return UnprocessableEntity(CostAccountingGuards.Error("cost_centre_not_allowed",
                        $"Account {account.Code} {account.Name} does not accept a cost centre. Remove the cost centre from this posting rule or change the account's cost centre rule.",
                        GlCostCentreRuleFilterAttribute.ArabicFor("cost_centre_not_allowed")));
            }
            var changed = !prevByKey.TryGetValue((m.DriverKey!, cls), out var prev) || prev.AccountId != m.AccountId;
            if (!changed) continue;
            if (!account.IsActive)
                return BadRequest(new { code = "GL_MAPPING_INACTIVE_ACCOUNT", message = $"'{account.Code} - {account.Name}' is inactive. Activate it or choose another account for {driver.Label}." });
            var allowed = AllowedAccountTypes(driver);
            if (!allowed.Contains(account.AccountType, StringComparer.OrdinalIgnoreCase))
                return BadRequest(new
                {
                    code = "GL_MAPPING_TYPE_MISMATCH",
                    message = $"{driver.Label} posts to a {string.Join(" or ", allowed)} account, but '{account.Code} - {account.Name}' is {account.AccountType}. Choose a matching account.",
                });
        }

        // Per-(driver, class) before→after diff for the audit trail (who repointed which driver, from what to what,
        // which functional class, and which cost centre it carries).
        var diff = mappings
            .Select(m => (Req: m, Cls: NormalizeClass(m.FunctionalClass)))
            .Where(x => !prevByKey.TryGetValue((x.Req.DriverKey, x.Cls), out var old) || old.AccountId != x.Req.AccountId || old.SegmentCostCenterId != x.Req.SegmentCostCenterId)
            .Select(x =>
            {
                prevByKey.TryGetValue((x.Req.DriverKey, x.Cls), out var o);
                return new
                {
                    x.Req.DriverKey,
                    oldAccount = o?.AccountId, newAccount = x.Req.AccountId,
                    oldClass = o?.FunctionalClass, newClass = x.Cls,
                    oldCostCenter = o?.SegmentCostCenterId, newCostCenter = x.Req.SegmentCostCenterId,
                };
            })
            .ToList();
        // Only the drivers this scope LISTS (the active ones GET /mappings returns, which is all the UI can
        // send) are replaced wholesale; omitting one of those still clears its mapping. A mapping keyed to
        // a driver that is switched off (or otherwise not listed) is left exactly as it is: the caller
        // could not have sent it, so its absence is not a decision to delete it.
        var replaceable = existing.Where(m => driverByKey.ContainsKey(m.DriverKey)).ToList();
        var sentKeys = mappings.Select(m => (m.DriverKey, NormalizeClass(m.FunctionalClass))).ToHashSet(new DriverClassComparer());
        var removedRows = replaceable.Where(m => !sentKeys.Contains((m.DriverKey, m.FunctionalClass))).ToList();
        var removed = removedRows.Select(m => new { m.DriverKey, functionalClass = m.FunctionalClass, hadCostCenter = m.SegmentCostCenterId != null }).ToList();

        if (diff.Count > 0 || removed.Count > 0)
        {
            // M-7 (AM-9): a mapping edit while a run awaits finance review or lock would change what it posts.
            if (await CostAccountingGuards.HasRunAwaitingFinanceOrLockAsync(_db, tid, companyId, ct))
                return Conflict(CostAccountingGuards.LockedIntoApprovedRunBody);

            // M-10: a reason is required when the save touches classes or fixed cost centres, or whenever costing is on
            // (plain account re-pointing for a tenant that has never used the feature keeps its legacy contract).
            var touchesCosting = diff.Any(d => d.oldClass != null || d.newClass != null || d.oldCostCenter != null || d.newCostCenter != null)
                                 || removedRows.Any(r => r.FunctionalClass != null || r.SegmentCostCenterId != null);
            var costingOn = await ScopedBypass.TenantWide(_db.Companies, tid, "Whether costing is on decides if a mapping change needs a reason; read across the tenant for a tenant-default save.")
                .AsNoTracking().AnyAsync(c => (companyId == null || c.Id == companyId) && c.CostAccountingEnabled, ct);
            if ((touchesCosting || costingOn) && !CostAccountingGuards.HasReason(reason))
                return UnprocessableEntity(CostAccountingGuards.ReasonRequiredBody);
        }

        _db.GlAccountMappings.RemoveRange(replaceable);
        foreach (var m in mappings)
            _db.GlAccountMappings.Add(new GlAccountMapping
            {
                TenantId = tid, CompanyId = companyId, DriverKey = m.DriverKey, FunctionalClass = NormalizeClass(m.FunctionalClass),
                AccountId = m.AccountId, SegmentCostCenterId = m.SegmentCostCenterId, IsActive = true,
            });
        await WriteAudit("finance.gl.mapping.saved", "GlAccountMapping", companyId?.ToString() ?? "tenant-default", companyId,
            new { companyId, reason = reason?.Trim(), changed = diff, removed, count = mappings.Count }, ct);
        await _db.SaveChangesAsync(ct);
        return Ok(new { count = mappings.Count, changed = diff.Count, removed = removed.Count });
    }

    /// <summary>Equality on (driver, class) with the class compared ordinally; a null class is its own key.</summary>
    private sealed class DriverClassComparer : IEqualityComparer<(string, string?)>
    {
        public bool Equals((string, string?) x, (string, string?) y) => string.Equals(x.Item1, y.Item1, StringComparison.Ordinal) && string.Equals(x.Item2, y.Item2, StringComparison.Ordinal);
        public int GetHashCode((string, string?) k) => HashCode.Combine(k.Item1, k.Item2);
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
        if (string.IsNullOrWhiteSpace(req.DefaultCode))
            return BadRequest(new { code = "GL_DRIVER_DEFAULT_REQUIRED", message = "Choose the account this driver posts to by default (DefaultCode)." });
        var defaultAccount = await DefaultAccountAsync(tid.Value, companyId, req.DefaultCode, ct);
        if (defaultAccount.Error is not null) return defaultAccount.Error;

        var driver = new GlDriver
        {
            TenantId = tid.Value, CompanyId = companyId,
            Key = req.Key.Trim(), Label = (req.Label ?? "").Trim(), Category = req.Category,
            PostingSide = req.PostingSide, AccountType = string.IsNullOrWhiteSpace(req.AccountType) ? "Expense" : req.AccountType,
            DefaultCode = defaultAccount.Account!.Code, DefaultName = defaultAccount.Account.Name,
            MatchSource = string.IsNullOrWhiteSpace(req.MatchSource) ? null : req.MatchSource,
            MatchMode = string.IsNullOrWhiteSpace(req.MatchMode) ? GlDriverMatchModes.Exact : req.MatchMode,
            MatchComponentCode = string.IsNullOrWhiteSpace(req.MatchComponentCode) ? null : req.MatchComponentCode.Trim(),
            EmitsEmployerExpensePair = req.EmitsEmployerExpensePair,
            PairedExpenseDriverKey = string.IsNullOrWhiteSpace(req.PairedExpenseDriverKey) ? null : req.PairedExpenseDriverKey.Trim(),
            IsSystem = false, IsActive = req.IsActive, SortOrder = req.SortOrder,
        };
        _db.GlDrivers.Add(driver);
        await WriteAudit("finance.gl.driver.created", "GlDriver", driver.Id.ToString(), companyId,
            new { driver.Key, driver.Label, driver.Category, driver.PostingSide, driver.AccountType, driver.DefaultCode, driver.DefaultName,
                  driver.MatchMode, driver.MatchSource, driver.MatchComponentCode, driver.IsActive, companyId }, ct);
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
        if (!string.IsNullOrWhiteSpace(req.Key) && !string.Equals(req.Key.Trim(), driver.Key, StringComparison.Ordinal))
            return BadRequest(new { code = "GL_DRIVER_KEY_IMMUTABLE", message = $"A driver's key cannot change ('{driver.Key}'): mappings and journals refer to it. Create a new driver instead." });

        // DefaultCode: a change must point at an existing ACTIVE account visible to the driver's scope,
        // and repointing posting is an account decision, so it also needs finance.gl.manage.
        GlAccount? newDefault = null;
        var requestedDefault = (req.DefaultCode ?? "").Trim();
        if (requestedDefault.Length > 0 && !string.Equals(requestedDefault, driver.DefaultCode, StringComparison.Ordinal))
        {
            var d = await DefaultAccountAsync(tid.Value, driver.CompanyId, requestedDefault, ct);
            if (d.Error is not null) return d.Error;
            newDefault = d.Account;
        }

        var before = DriverAuditView(driver);
        if (driver.IsSystem)
        {
            // System rows: only Label, default account, SortOrder are editable. They cannot be switched
            // off — payroll's journal relies on every system driver existing. They CAN be switched back on:
            // a system row turned off before this rule existed blocks every Lock that would have used it,
            // and this is the only way back short of editing the database.
            if (req.Category != driver.Category || req.PostingSide != driver.PostingSide
                || (req.MatchSource ?? "") != (driver.MatchSource ?? "") || (req.MatchMode ?? "") != driver.MatchMode
                || (req.MatchComponentCode ?? "") != (driver.MatchComponentCode ?? "")
                || req.EmitsEmployerExpensePair != driver.EmitsEmployerExpensePair
                || (req.PairedExpenseDriverKey ?? "") != (driver.PairedExpenseDriverKey ?? ""))
                return BadRequest(new { message = "System driver routing fields are read-only. Only label, default account and sort order may change." });
            if (!req.IsActive)
                return BadRequest(new { code = "GL_SYSTEM_DRIVER_REQUIRED", message = "System drivers cannot be deactivated: payroll posting depends on every one of them. Remap its account instead." });
            driver.Label = (req.Label ?? driver.Label).Trim();
            if (newDefault is not null) { driver.DefaultCode = newDefault.Code; driver.DefaultName = newDefault.Name; }
            driver.IsActive = true;
            driver.SortOrder = req.SortOrder;
            driver.UpdatedAtUtc = DateTime.UtcNow;
            await WriteAudit("finance.gl.driver.updated", "GlDriver", driver.Id.ToString(), driver.CompanyId,
                new { system = true, before, after = DriverAuditView(driver) }, ct);
            await _db.SaveChangesAsync(ct);
            return Ok(ToDriverDto(driver, driver.CompanyId));
        }

        // Custom row: full validation (predicate authoring gated, category→side enforced).
        var v = await ValidateDriverAsync(tid.Value, driver.CompanyId, req with { Key = driver.Key }, driver.Id, ct);
        if (v is not null) return v;
        driver.Label = (req.Label ?? "").Trim(); driver.Category = req.Category;
        driver.PostingSide = req.PostingSide; driver.AccountType = string.IsNullOrWhiteSpace(req.AccountType) ? "Expense" : req.AccountType;
        if (newDefault is not null) { driver.DefaultCode = newDefault.Code; driver.DefaultName = newDefault.Name; }
        driver.MatchSource = string.IsNullOrWhiteSpace(req.MatchSource) ? null : req.MatchSource;
        driver.MatchMode = string.IsNullOrWhiteSpace(req.MatchMode) ? GlDriverMatchModes.Exact : req.MatchMode;
        driver.MatchComponentCode = string.IsNullOrWhiteSpace(req.MatchComponentCode) ? null : req.MatchComponentCode.Trim();
        driver.EmitsEmployerExpensePair = req.EmitsEmployerExpensePair;
        driver.PairedExpenseDriverKey = string.IsNullOrWhiteSpace(req.PairedExpenseDriverKey) ? null : req.PairedExpenseDriverKey.Trim();
        driver.IsActive = req.IsActive; driver.SortOrder = req.SortOrder; driver.UpdatedAtUtc = DateTime.UtcNow;
        await WriteAudit("finance.gl.driver.updated", "GlDriver", driver.Id.ToString(), driver.CompanyId,
            new { system = false, before, after = DriverAuditView(driver) }, ct);
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
        // A tenant-wide driver is visible to every company, so a mapping in ANY company may use it.
        var mappedIn = await Mappings(tid.Value)
            .Where(m => m.DriverKey == driver.Key && (driver.CompanyId == null || m.CompanyId == driver.CompanyId))
            .Select(m => m.CompanyId).Distinct().ToListAsync(ct);
        if (mappedIn.Count > 0)
            return Conflict(new { code = "GL_DRIVER_IN_USE", message = $"Driver '{driver.Key}' is still mapped to an account in {mappedIn.Count} scope(s). Remove those mappings first, or deactivate the driver." });
        _db.GlDrivers.Remove(driver);
        await WriteAudit("finance.gl.driver.deleted", "GlDriver", driver.Id.ToString(), driver.CompanyId,
            new { driver.Key, before = DriverAuditView(driver), companyId = driver.CompanyId }, ct);
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
        if (!this.HasGroupEntityScope()) return Forbid();

        var seeded = await GlDriverSeeder.SeedTenantDefaultsAsync(_db, tid.Value, ct);
        // Configurability keystone: seed the data-driven pay-component defaults alongside the GL defaults
        // (same tenant-default, idempotent, add-only contract) so the two catalogs are always provisioned together.
        var seededComponents = await Zayra.Api.Infrastructure.Seed.PayComponentSeeder.SeedTenantDefaultsAsync(_db, tid.Value, ct);
        await WriteAudit("finance.gl.seed_defaults", "GlDriver", tid.Value.ToString(), null,
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

    /// <summary>Reopens a closed period. Needs finance.gl.period.reopen (Admin by default) and is refused
    /// to the person who closed it — reopening the books is a second person's decision.</summary>
    [HttpPost("periods/{period}/reopen")]
    public async Task<IActionResult> ReopenPeriod(string period, [FromQuery] Guid? companyId, [FromBody] GlPeriodActionRequest? req, CancellationToken ct)
    {
        if (!HasPermission(PeriodReopenPermission)) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        if (ScopeError(companyId) is { } err) return err;
        if (string.IsNullOrWhiteSpace(req?.Reason))
            return BadRequest(new { message = "A reason is required to reopen a closed period (audited)." });

        // IgnoreQueryFilters is intentional: same scope-authorised system read as ClosePeriod.
        var existing = await _db.GlPeriodCloses.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.TenantId == tid && p.CompanyId == companyId && p.Period == period, ct);
        if (existing is null || existing.Status != GlPeriodStatuses.Closed)
            return BadRequest(new { message = $"Period {period} is not closed in this scope." });
        var me = this.GetUserId();
        if (me is null || me == existing.ClosedByUserId)
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                code = "GL_PERIOD_REOPEN_SOD",
                message = $"You closed {period}, so someone else must reopen it. Ask another person with period-reopen rights.",
            });

        existing.Status = GlPeriodStatuses.Open;
        existing.ReopenedAtUtc = DateTime.UtcNow;
        existing.ReopenedByUserId = me;
        existing.ReopenedByName = UserName();
        existing.ReopenReason = req.Reason;
        existing.UpdatedAtUtc = DateTime.UtcNow;
        await WriteAudit("finance.gl.period.reopened", "GlPeriodClose", existing.Id.ToString(), companyId,
            new { period, companyId, before = GlPeriodStatuses.Closed, after = GlPeriodStatuses.Open, reason = req.Reason, closedBy = existing.ClosedByUserId }, ct);
        await _db.SaveChangesAsync(ct);
        return Ok(ToPeriodDto(existing));
    }

    private static bool IsValidPeriod(string period) =>
        Regex.IsMatch(period ?? string.Empty, @"^\d{4}-(0[1-9]|1[0-2])$");

    private string? UserName() => User.FindFirstValue(ClaimTypes.Name) ?? User.FindFirstValue("name");

    private static object ToPeriodDto(GlPeriodClose p) => new
    {
        p.Id, p.CompanyId, p.Period, p.Status,
        p.ClosedAtUtc, p.ClosedByName, p.ClosedReason,
        p.ReopenedAtUtc, p.ReopenedByName, p.ReopenReason,
    };

    // ── Account rules ─────────────────────────────────────────────────────────

    public static string? CanonicalAccountType(string? type) =>
        AccountTypes.FirstOrDefault(t => string.Equals(t, (type ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

    private static object AccountTypeRefusal(string? type) => new
    {
        code = "GL_ACCOUNT_TYPE_INVALID",
        message = $"Account type '{type}' is not recognised. Use one of: {string.Join(", ", AccountTypes)}.",
        allowed = AccountTypes,
    };

    private static string? CodeProblem(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "Code is required.";
        return CodePattern.IsMatch(code) ? null
            : $"Code '{code}' may only use letters, digits, '.' and '-', up to 30 characters.";
    }

    /// <summary>Null when the ERP code is acceptable (null and blank included: blank means "none").</summary>
    public static string? ExternalCodeProblem(string? external)
    {
        if (external is null) return null;
        var v = external.Trim();
        return ExternalCodePattern.IsMatch(v) ? null
            : $"ERP account code '{v}' may only use letters, digits, spaces and . - _ /, up to {ExternalCodeMaxLength} characters.";
    }

    /// <summary>Trimmed ERP code; blank becomes null so "no ERP code" has one representation.</summary>
    public static string? NormalizeExternalCode(string? external) =>
        string.IsNullOrWhiteSpace(external) ? null : external.Trim();

    private static string Label(GlAccount a) => $"{a.Code} - {a.Name}";

    /// <summary>Accounts that a driver may post to. System drivers: exactly what the catalog declares.
    /// Custom drivers: earnings are expenses; deductions a liability, a receivable (asset) or a
    /// contra-expense; balancing drivers their declared type.</summary>
    public static IReadOnlyList<string> AllowedAccountTypes(GlDriver d)
    {
        if (d.IsSystem || d.Category == GlDriverCategories.Balancing)
            return new[] { CanonicalAccountType(d.AccountType) ?? d.AccountType };
        return d.Category switch
        {
            GlDriverCategories.Earning => new[] { "Expense" },
            GlDriverCategories.Deduction => new[] { "Liability", "Asset", "Expense" },
            _ => new[] { CanonicalAccountType(d.AccountType) ?? d.AccountType },
        };
    }

    /// <summary>Validates a cost-centre rule against the account type: the value must be one of the three, and Required is
    /// refused on Asset, Liability and Equity accounts (422 — the database cannot see the type of a ledger label).</summary>
    private IActionResult? RuleProblem(string rule, string accountType)
    {
        if (!CostCenterRules.Contains(rule, StringComparer.Ordinal))
            return UnprocessableEntity(CostAccountingGuards.Error("cost_centre_rule_invalid",
                $"The cost centre rule must be one of {string.Join(", ", CostCenterRules)}.",
                "يجب أن تكون قاعدة مركز التكلفة إحدى القيم: Required أو Optional أو NotAllowed."));
        if (rule == "Required" && !IsProfitAndLossType(accountType))
            return UnprocessableEntity(CostAccountingGuards.Error("cost_centre_rule_invalid_for_type",
                "Only expense and income accounts can require a cost centre.",
                "لا يمكن إلزام مركز التكلفة إلا في حسابات المصروفات والإيرادات."));
        return null;
    }

    private IQueryable<GlAccount> ScopeAccounts(Guid tid, Guid? companyId) =>
        ScopedBypass.TenantWide(_db.GlAccounts, tid,
                "Chart of accounts of one authorised scope; the company is pinned in the predicate below.")
            .Where(a => a.CompanyId == companyId);

    private IQueryable<GlAccountMapping> Mappings(Guid tid) =>
        ScopedBypass.TenantWide(_db.GlAccountMappings, tid,
            "Reference checks must see mappings in EVERY company of the tenant: a company may map a tenant-default account.");

    private IQueryable<GlDriver> Drivers(Guid tid) =>
        ScopedBypass.TenantWide(_db.GlDrivers, tid,
            "Reference checks must see drivers in every company of the tenant.");

    private Task<bool> IsLabelPostedAsync(Guid tid, string label, CancellationToken ct) =>
        _db.FinanceGlEntries.AsNoTracking()
            .AnyAsync(e => e.TenantId == tid && (e.DebitAccount == label || e.CreditAccount == label), ct);

    /// <summary>Everything that points at an account: mappings in ANY company (an account id is unique,
    /// and a company may map a tenant-default account) and active drivers whose default is its code.</summary>
    private async Task<List<string>> AccountReferencesAsync(Guid tid, GlAccount acct, CancellationToken ct)
    {
        var refs = new List<string>();
        var mapped = await Mappings(tid).AsNoTracking()
            .Where(m => m.AccountId == acct.Id && m.IsActive)
            .Select(m => new { m.DriverKey, m.CompanyId }).ToListAsync(ct);
        foreach (var m in mapped)
            refs.Add($"the {m.DriverKey} mapping ({(m.CompanyId is null ? "group default" : "company " + m.CompanyId)})");
        var drivers = await Drivers(tid).AsNoTracking()
            .Where(d => d.IsActive && d.DefaultCode == acct.Code
                     && (acct.CompanyId == null || d.CompanyId == acct.CompanyId))
            .Select(d => d.Key).ToListAsync(ct);
        foreach (var d in drivers) refs.Add($"the default account of driver {d}");
        return refs;
    }

    /// <summary>
    /// Moves every driver default that resolves to <paramref name="acct"/> by its OLD code onto the new
    /// code (and name): drivers at the account's own scope, and — for a tenant-default account — company
    /// drivers in companies that do not carry their own account with the old code (theirs resolve to the
    /// company account, not this one). Tracked changes; the caller's SaveChanges commits them with the
    /// account. Returns the driver keys moved, for the audit row.
    /// </summary>
    private async Task<List<string>> RepointDriverDefaultsAsync(Guid tid, GlAccount acct, string newCode, string newName, CancellationToken ct)
    {
        var oldCode = acct.Code;
        var candidates = await Drivers(tid)
            .Where(d => d.DefaultCode == oldCode && (acct.CompanyId == null || d.CompanyId == acct.CompanyId))
            .ToListAsync(ct);
        if (acct.CompanyId is null && candidates.Any(d => d.CompanyId != null))
        {
            var shadowing = (await ScopedBypass.TenantWide(_db.GlAccounts, tid,
                    "Which companies carry their own account with the old code (their drivers resolve to it, not to the tenant default).")
                    .AsNoTracking()
                    .Where(a => a.CompanyId != null && a.Code == oldCode)
                    .Select(a => a.CompanyId).ToListAsync(ct)).ToHashSet();
            candidates = candidates.Where(d => d.CompanyId == null || !shadowing.Contains(d.CompanyId)).ToList();
        }
        foreach (var d in candidates)
        {
            d.DefaultCode = newCode; d.DefaultName = newName; d.UpdatedAtUtc = DateTime.UtcNow;
        }
        return candidates.Select(d => d.CompanyId is null ? d.Key : $"{d.Key} (company {d.CompanyId})").ToList();
    }

    private async Task<IActionResult?> AccountChangeProblemAsync(Guid tid, GlAccount acct, string newCode, string newName, string newType, bool newActive, CancellationToken ct)
    {
        var (status, code, message, extra) = await AccountChangeProblemCoreAsync(tid, acct, newCode, newName, newType, newActive, ct);
        if (status is null) return null;
        var body = new { code, message, references = extra };
        return status == StatusCodes.Status400BadRequest ? BadRequest(body) : Conflict(body);
    }

    private async Task<string?> AccountChangeProblemMessageAsync(Guid tid, GlAccount acct, string newCode, string newName, string newType, bool newActive, CancellationToken ct)
        => (await AccountChangeProblemCoreAsync(tid, acct, newCode, newName, newType, newActive, ct)).Message;

    /// <summary>The single rule set for changing an account (form and CSV import alike).</summary>
    private async Task<(int? Status, string? Code, string? Message, List<string>? References)> AccountChangeProblemCoreAsync(
        Guid tid, GlAccount acct, string newCode, string newName, string newType, bool newActive, CancellationToken ct)
    {
        var codeChanged = !string.Equals(newCode, acct.Code, StringComparison.Ordinal);
        var nameChanged = !string.Equals(newName, acct.Name, StringComparison.Ordinal);
        if (codeChanged && CodeProblem(newCode) is { } codeErr)
            return (StatusCodes.Status400BadRequest, "GL_ACCOUNT_CODE_INVALID", codeErr, null);
        if ((codeChanged || nameChanged) && await IsLabelPostedAsync(tid, Label(acct), ct))
            return (StatusCodes.Status409Conflict, "GL_ACCOUNT_POSTED",
                $"Journal lines already name '{Label(acct)}', so its code and name can no longer change — the books would stop matching the chart. Create a new account and remap the drivers to it.", null);
        if (codeChanged && await ScopeAccounts(tid, acct.CompanyId).AnyAsync(a => a.Code == newCode && a.Id != acct.Id, ct))
            return (StatusCodes.Status400BadRequest, "GL_ACCOUNT_CODE_TAKEN", $"Account code '{newCode}' already exists in this scope.", null);

        if (!string.Equals(newType, acct.AccountType, StringComparison.OrdinalIgnoreCase))
        {
            var mappedKeys = await Mappings(tid).AsNoTracking().Where(m => m.AccountId == acct.Id)
                .Select(m => m.DriverKey).Distinct().ToListAsync(ct);
            if (mappedKeys.Count > 0)
            {
                var drivers = await Drivers(tid).AsNoTracking().Where(d => mappedKeys.Contains(d.Key)).ToListAsync(ct);
                var compiled = PayrollGlCatalog.SystemDriverSeeds(tid).Where(d => mappedKeys.Contains(d.Key) && drivers.All(x => x.Key != d.Key));
                var clash = drivers.Concat(compiled).Where(d => !AllowedAccountTypes(d).Contains(newType, StringComparer.OrdinalIgnoreCase))
                    .Select(d => d.Key).Distinct().ToList();
                if (clash.Count > 0)
                    return (StatusCodes.Status409Conflict, "GL_ACCOUNT_TYPE_IN_USE",
                        $"'{Label(acct)}' is mapped to {string.Join(", ", clash)}, which cannot post to a {newType} account. Remap those drivers first.", clash);
            }
        }

        if (acct.IsActive && !newActive)
        {
            var refs = await AccountReferencesAsync(tid, acct, ct);
            if (refs.Count > 0)
                return (StatusCodes.Status409Conflict, "GL_ACCOUNT_IN_USE",
                    $"'{Label(acct)}' is still used by {string.Join("; ", refs)}. Remap those first — an inactive account must not receive postings.", refs);
        }
        return (null, null, null, null);
    }

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

    /// <summary>The ACTIVE account a driver default names, visible to the driver's scope (its company's
    /// accounts, then tenant defaults). Setting it is an account decision: finance.gl.manage.</summary>
    private async Task<(IActionResult? Error, GlAccount? Account)> DefaultAccountAsync(Guid tid, Guid? companyId, string? defaultCode, CancellationToken ct)
    {
        if (!HasPermission("finance.gl.manage"))
            return (StatusCode(StatusCodes.Status403Forbidden, new { code = "GL_DRIVER_DEFAULT_NEEDS_MANAGE", message = "Choosing the account a driver posts to needs the finance.gl.manage permission." }), null);
        var code = (defaultCode ?? "").Trim();
        var candidates = await ScopedBypass.TenantWide(_db.GlAccounts, tid,
                "Driver default account lookup: the driver's company and tenant defaults, pinned below.")
            .AsNoTracking()
            .Where(a => a.Code == code && (a.CompanyId == null || (companyId != null && a.CompanyId == companyId)))
            .ToListAsync(ct);
        var account = candidates.OrderByDescending(a => a.CompanyId != null).FirstOrDefault();
        if (account is null)
            return (BadRequest(new { code = "GL_DRIVER_DEFAULT_UNKNOWN", message = $"There is no account '{code}' in this scope's chart of accounts. Create it first, then choose it." }), null);
        if (!account.IsActive)
            return (BadRequest(new { code = "GL_DRIVER_DEFAULT_INACTIVE", message = $"'{Label(account)}' is inactive and cannot be a driver's default." }), null);
        return (null, account);
    }

    private static object DriverAuditView(GlDriver d) => new
    {
        d.Key, d.Label, d.Category, d.PostingSide, d.AccountType, d.DefaultCode, d.DefaultName,
        d.MatchMode, d.MatchSource, d.MatchComponentCode, d.EmitsEmployerExpensePair, d.PairedExpenseDriverKey,
        d.IsActive, d.SortOrder,
    };

    private async Task<IActionResult?> ValidateDriverAsync(Guid tid, Guid? companyId, GlDriverRequest req, Guid? selfId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Key)) return BadRequest(new { message = "Key is required." });
        var key = req.Key.Trim();
        if (req.Category is not (GlDriverCategories.Earning or GlDriverCategories.Deduction or GlDriverCategories.Balancing))
            return BadRequest(new { message = "Category must be Earning, Deduction or Balancing." });
        if (req.PostingSide is not ("DR" or "CR")) return BadRequest(new { message = "PostingSide must be DR or CR." });
        // An earning is an expense debit; a deduction is a credit (a payable, a receivable relieved or a
        // contra-expense). The opposite side would post the journal backwards.
        if (req.Category == GlDriverCategories.Earning && req.PostingSide != "DR")
            return BadRequest(new { code = "GL_DRIVER_SIDE_INVALID", message = "An earning driver posts on the debit side (DR)." });
        if (req.Category == GlDriverCategories.Deduction && req.PostingSide != "CR")
            return BadRequest(new { code = "GL_DRIVER_SIDE_INVALID", message = "A deduction driver posts on the credit side (CR)." });
        if (!string.IsNullOrWhiteSpace(req.AccountType) && CanonicalAccountType(req.AccountType) is null)
            return BadRequest(AccountTypeRefusal(req.AccountType));
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

    /// <summary>Authorises a companyId against the caller's entity scope. Fail-closed: a company scope
    /// requires CanAccessCompany; a group-default (companyId==null) scope requires a GROUP grant —
    /// judged by HasGroupEntityScope (the caller's grant), not GetEntityScope (which narrows to the
    /// company selected in the switcher and used to 403 a group admin viewing one company).</summary>
    private IActionResult? ScopeError(Guid? companyId)
    {
        if (companyId is null)
            return this.HasGroupEntityScope() ? null : Forbid();
        return this.GetEntityScope().CanAccessCompany(companyId) ? null : Forbid();
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

    private static object ToDto(GlAccount a, Guid? _) => new { a.Id, a.CompanyId, a.Code, a.Name, a.AccountType, a.IsActive, a.ExternalCode, costCenterRule = a.CostCenterRule };

    private static object ToDriverDto(GlDriver d, Guid? viewCompanyId) => new
    {
        d.Id, d.CompanyId, d.Key, d.Label, d.Category, d.PostingSide, d.AccountType,
        d.DefaultCode, d.DefaultName, d.MatchSource, d.MatchMode, d.MatchComponentCode,
        d.EmitsEmployerExpensePair, d.PairedExpenseDriverKey, d.IsSystem, d.IsActive, d.SortOrder,
        inherited = viewCompanyId is not null && d.CompanyId == null,
    };
}

public record GlPeriodActionRequest(string? Reason = null);
/// <param name="ExternalCode">The account code in the client's ERP. Create: optional. Update: null leaves it
/// unchanged, "" clears it.</param>
/// <param name="CostCenterRule">Required | Optional | NotAllowed. Create: null = Optional. Update: null leaves the rule unchanged.</param>
public record GlAccountRequest(string Code, string Name, string AccountType = "Expense", bool IsActive = true, string? ExternalCode = null, string? CostCenterRule = null);
/// <param name="FunctionalClass">Cost-centre functional class this row applies to; null = every class. Mutually exclusive with a fixed cost centre.</param>
public record GlMappingRequest(string DriverKey, Guid AccountId, Guid? SegmentCostCenterId = null, string? FunctionalClass = null);
public record CostCentreRulePresetRequest(string? Reason);
/// <param name="Enabled">null = unchanged.</param>
/// <param name="DefaultCostCenterId">null = unchanged (use <paramref name="ClearDefaultCostCenter"/> to remove).</param>
/// <param name="SmallCellMin">null = unchanged.</param>
public record CostAccountingSettingsRequest(
    bool? Enabled = null, Guid? DefaultCostCenterId = null, bool ClearDefaultCostCenter = false, short? SmallCellMin = null,
    string? Reason = null, bool AcknowledgeBlockers = false);
public record GlDriverRequest(
    string Key, string Label, string Category, string PostingSide, string DefaultCode, string DefaultName,
    string AccountType = "Expense", string? MatchSource = null, string MatchMode = "Exact",
    string? MatchComponentCode = null, bool EmitsEmployerExpensePair = false, string? PairedExpenseDriverKey = null,
    int SortOrder = 500, bool IsActive = true);
