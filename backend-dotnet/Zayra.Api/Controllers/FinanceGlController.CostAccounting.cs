using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

/// <summary>
/// Cost-accounting settings of the GL module (WP-B): the bulk cost-centre rule preset, the per-company switch (with its
/// default cost centre and small-cell K) and the readiness dry-run. Finance-owned: <c>finance.gl.manage</c> writes,
/// <c>finance.gl.read</c> reads; HR permissions never apply here.
/// </summary>
[Zayra.Api.Infrastructure.Filters.ReasonLimit]
public partial class FinanceGlController
{
    private const int MaxSmallCellMin = 1000;

    // ── Recommended rule preset ──────────────────────────────────────────────

    /// <summary>
    /// "Apply recommended rules": every Expense/Income account of the scope becomes Required and every Asset, Liability and
    /// Equity account becomes NotAllowed. Explicit action only, never automatic. <paramref name="dryRun"/> returns the counts
    /// and writes nothing. Applying needs a reason (M-10), is refused while a run awaits finance review or lock (M-7), and
    /// commits the rule changes and the audit row together.
    /// </summary>
    [HttpPost("accounts/cost-centre-rule-preset")]
    public async Task<IActionResult> ApplyCostCentreRulePreset([FromQuery] Guid? companyId, [FromQuery] bool dryRun, [FromBody] CostCentreRulePresetRequest? req, CancellationToken ct)
    {
        if (!HasPermission("finance.gl.manage")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        if (ScopeError(companyId) is { } err) return err;
        return await CostAccountingGuards.SerializeAsync(_db, tid.Value, companyId, () => PresetCoreAsync(tid.Value, companyId, dryRun, req?.Reason, ct), ct);
    }

    private async Task<IActionResult> PresetCoreAsync(Guid tid, Guid? companyId, bool dryRun, string? reason, CancellationToken ct)
    {
        var accounts = await ScopeAccounts(tid, companyId).OrderBy(a => a.Code).ToListAsync(ct);
        var changes = accounts
            .Select(a => (Account: a, To: IsProfitAndLossType(a.AccountType) ? "Required" : "NotAllowed"))
            .Where(x => !string.Equals(x.Account.CostCenterRule, x.To, StringComparison.Ordinal))
            .ToList();
        var byType = changes.GroupBy(x => (x.Account.AccountType, x.To))
            .Select(g => new { accountType = g.Key.AccountType, to = g.Key.To, count = g.Count() }).OrderBy(x => x.accountType, StringComparer.Ordinal).ToList();

        if (dryRun) return Ok(new { dryRun = true, changed = changes.Count, total = accounts.Count, byType });

        if (changes.Count > 0)
        {
            if (!CostAccountingGuards.HasReason(reason)) return UnprocessableEntity(CostAccountingGuards.ReasonRequiredBody);
            if (await CostAccountingGuards.HasRunAwaitingFinanceOrLockAsync(_db, tid, companyId, ct))
                return Conflict(CostAccountingGuards.LockedIntoApprovedRunBody);
            foreach (var (a, to) in changes) { a.CostCenterRule = to; a.UpdatedAtUtc = DateTime.UtcNow; }
            await WriteAudit("finance.gl.account.rule_preset_applied", "GlAccount", companyId?.ToString() ?? "tenant-default", companyId,
                new { companyId, reason = reason!.Trim(), count = changes.Count,
                      changed = changes.Select(x => new { code = x.Account.Code, accountType = x.Account.AccountType, before = x.Account.CostCenterRule, after = x.To }) }, ct);
            await _db.SaveChangesAsync(ct);
        }
        else if (!CostAccountingGuards.HasReason(reason))
            return UnprocessableEntity(CostAccountingGuards.ReasonRequiredBody);
        return Ok(new { dryRun = false, changed = changes.Count, total = accounts.Count, byType });
    }

    // ── The switch, the default cost centre and K ────────────────────────────

    /// <summary>
    /// Changes <c>cost_accounting_enabled</c>, <c>default_cost_center_id</c> and/or <c>cost_centre_small_cell_min</c> for one
    /// company. A field that is omitted stays as it is. Every change needs a reason (M-10), <c>finance.gl.manage</c> and is
    /// refused (M-7) while a run awaits finance review or lock. Turning the switch ON with readiness blockers or small-cell
    /// exposure needs <c>acknowledgeBlockers</c>; a 3-decimal-currency company is refused outright; turning it OFF after a
    /// costed run was locked is refused (the attempt is audited). Every change is audited with before and after in the
    /// same transaction.
    /// </summary>
    [HttpPut("cost-accounting")]
    public async Task<IActionResult> SetCostAccounting([FromQuery] Guid companyId, [FromBody] CostAccountingSettingsRequest req, CancellationToken ct)
    {
        if (!HasPermission("finance.gl.manage")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        if (ScopeError(companyId) is { } err) return err;
        req ??= new CostAccountingSettingsRequest();
        return await CostAccountingGuards.SerializeAsync(_db, tid.Value, companyId, () => SetCostAccountingCoreAsync(tid.Value, companyId, req, ct), ct);
    }

    private async Task<IActionResult> SetCostAccountingCoreAsync(Guid tid, Guid companyId, CostAccountingSettingsRequest req, CancellationToken ct)
    {
        var company = await ScopedBypass.TenantWide(_db.Companies, tid, "Settings of one authorised company; scope was checked by the caller.").FirstOrDefaultAsync(c => c.Id == companyId, ct);
        if (company is null) return NotFound();

        var enableChange = req.Enabled is { } want && want != company.CostAccountingEnabled;
        Guid? targetDefault = req.ClearDefaultCostCenter ? null : req.DefaultCostCenterId ?? company.DefaultCostCenterId;
        var defaultChange = (req.ClearDefaultCostCenter || req.DefaultCostCenterId.HasValue) && targetDefault != company.DefaultCostCenterId;
        var targetK = req.SmallCellMin ?? company.CostCentreSmallCellMin;
        var kChange = req.SmallCellMin.HasValue && targetK != company.CostCentreSmallCellMin;
        if (!enableChange && !defaultChange && !kChange) return Ok(StateBody(company));

        if (!CostAccountingGuards.HasReason(req.Reason)) return UnprocessableEntity(CostAccountingGuards.ReasonRequiredBody);
        var reason = req.Reason!.Trim();

        if (kChange && (targetK < 0 || targetK > MaxSmallCellMin))
            return UnprocessableEntity(CostAccountingGuards.Error("small_cell_min_invalid", $"The small-cell minimum must be between 0 and {MaxSmallCellMin}.", $"يجب أن يكون الحد الأدنى لعدد الموظفين في المركز بين 0 و{MaxSmallCellMin}."));
        if (defaultChange && targetDefault is { } dcc)
        {
            var row = await ScopedBypass.TenantWide(_db.CostCenters, tid, "The default cost centre must be a live row of this company; foreign rows are refused, not described.")
                .AsNoTracking().Where(c => c.Id == dcc).Select(c => new { c.CompanyId, c.IsActive, c.IsDeleted }).FirstOrDefaultAsync(ct);
            if (row is null || row.CompanyId != company.Id || !row.IsActive || row.IsDeleted)
                return UnprocessableEntity(CostAccountingGuards.Error("default_cost_centre_invalid", "The default cost centre must be an active cost centre of this company.",
                    "يجب أن يكون مركز التكلفة الافتراضي مركزًا نشطًا تابعًا لهذه الشركة."));
        }
        if (enableChange && req.Enabled == true && CurrencyMinorUnits.Scale(company.DefaultCurrency) == 3)
            return UnprocessableEntity(CostAccountingGuards.Error("currency_scale_unsupported_for_payroll",
                "This company's currency uses three decimal places, which payroll storage cannot carry yet, so cost accounting cannot be turned on.",
                "تستخدم عملة هذه الشركة ثلاث منازل عشرية، ولا يدعم تخزين الرواتب حاليًا سوى منزلتين عشريتين، لذا لا يمكن تفعيل محاسبة التكلفة."));

        // M-7 (AM-3, AM-9): none of the three may move while a run awaits finance review or lock.
        if (await CostAccountingGuards.HasRunAwaitingFinanceOrLockAsync(_db, tid, companyId, ct))
            return Conflict(CostAccountingGuards.LockedIntoApprovedRunBody);

        // S-006: once a costed run has been locked, the switch stays on (mixed basis inside a fiscal year is a controls failure).
        if (enableChange && req.Enabled == false
            && await ScopedBypass.TenantWide(_db.PayrollRuns, tid, "Whether any locked run of the company carries cost-centre stamps.")
                .AsNoTracking().AnyAsync(r => r.CompanyId == companyId && r.CostAccountingApplied && r.LockedAtUtc != null, ct))
        {
            await WriteAudit("finance.gl.cost_accounting.switch_change_refused", "Company", companyId.ToString(), companyId,
                new { companyId, attempted = new { enabled = false }, reason, refusedBecause = "cost_accounting_has_costed_runs" }, ct);
            await _db.SaveChangesAsync(ct);
            return Conflict(CostAccountingGuards.Error("cost_accounting_has_costed_runs",
                "Cost accounting cannot be turned off because a payroll run of this company was already locked with cost centres. Contact support if it must be reversed.",
                "لا يمكن إيقاف محاسبة التكلفة لأن مسير رواتب لهذه الشركة قُفل بعد تفعيلها وبمراكز التكلفة. تواصل مع الدعم الفني إن احتجت إلى التراجع."));
        }

        // Readiness is evaluated with the PROPOSED default and K, before anything is mutated.
        ReadinessResult? readiness = null;
        if (enableChange && req.Enabled == true)
        {
            readiness = await CostAccountingReadinessService.ComputeAsync(_db, company, targetDefault, targetK, ct);
            var hard = readiness.HardChecks.ToList();
            if ((hard.Count > 0 || readiness.SmallCellExposure > 0) && !req.AcknowledgeBlockers)
                return Conflict(new
                {
                    error = "cost_accounting_blockers", code = "cost_accounting_blockers",
                    message = "Cost accounting has readiness blockers or small-cell exposure. Fix them, or acknowledge them to turn it on anyway.",
                    messageAr = "توجد موانع تمنع التفعيل أو مراكز تكلفة بعدد موظفين قليل قد يكشف رواتب أفراد. عالجها أو أقرّ بها لتفعيل محاسبة التكلفة على أي حال.",
                    checks = readiness.Checks, blockers = readiness.Blockers, smallCellExposure = new { count = readiness.SmallCellExposure, threshold = CostAccountingReadinessService.SmallCellThreshold },
                });
        }

        var changed = new List<string>();
        if (enableChange)
        {
            var hardKeys = readiness?.HardChecks.Select(c => new { key = c.Key, count = c.Count }).ToList();
            await WriteAudit("finance.gl.cost_accounting.switch_changed", "Company", companyId.ToString(), companyId,
                new
                {
                    companyId, reason, before = new { enabled = company.CostAccountingEnabled }, after = new { enabled = req.Enabled },
                    acknowledgeBlockers = req.AcknowledgeBlockers, blockers = hardKeys,
                    smallCellExposure = readiness?.SmallCellExposure ?? 0,
                    smallCellExposureAcknowledged = req.AcknowledgeBlockers && (readiness?.SmallCellExposure ?? 0) > 0,
                }, ct);
            company.CostAccountingEnabled = req.Enabled!.Value; changed.Add("enabled");
        }
        if (defaultChange)
        {
            await WriteAudit("finance.gl.cost_accounting.default_cost_centre_changed", "Company", companyId.ToString(), companyId,
                new { companyId, reason, before = new { defaultCostCenterId = company.DefaultCostCenterId }, after = new { defaultCostCenterId = targetDefault } }, ct);
            company.DefaultCostCenterId = targetDefault; changed.Add("defaultCostCenterId");
        }
        if (kChange)
        {
            await WriteAudit("finance.gl.cost_accounting.small_cell_changed", "Company", companyId.ToString(), companyId,
                new { companyId, reason, before = new { smallCellMin = company.CostCentreSmallCellMin }, after = new { smallCellMin = targetK } }, ct);
            company.CostCentreSmallCellMin = targetK; changed.Add("smallCellMin");
        }
        company.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var fresh = readiness ?? await CostAccountingReadinessService.ComputeAsync(_db, company, company.DefaultCostCenterId, company.CostCentreSmallCellMin, ct);
        return Ok(new
        {
            companyId, changed, settings = SettingsBody(company, false),
            smallCellExposure = new { count = fresh.SmallCellExposure, threshold = CostAccountingReadinessService.SmallCellThreshold },
            checks = fresh.Checks,
        });
    }

    // ── Readiness dry-run (AM-2, AM-13) ──────────────────────────────────────

    /// <summary>
    /// Read-only dry run for one company: counts and capped samples per readiness check, the AM-1 blocker detail for
    /// employees whose cost centre cannot be resolved, and the current settings. Needs <c>finance.gl.read</c> and company
    /// access; it never names a row of another company (foreign items are counts only).
    /// </summary>
    [HttpGet("cost-accounting/readiness")]
    public async Task<IActionResult> CostAccountingReadiness([FromQuery] Guid companyId, CancellationToken ct)
    {
        if (!HasPermission("finance.gl.read")) return Forbid();
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        if (ScopeError(companyId) is { } err) return err;
        var company = await ScopedBypass.TenantWide(_db.Companies, tid.Value, "Readiness of one authorised company; scope was checked above.")
            .AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId, ct);
        if (company is null) return NotFound();

        var r = await CostAccountingReadinessService.ComputeAsync(_db, company, company.DefaultCostCenterId, company.CostCentreSmallCellMin, ct);
        return Ok(new
        {
            companyId, settings = SettingsBody(company, r.AwaitingRun), checks = r.Checks, blockers = r.Blockers,
            smallCellExposure = new { count = r.SmallCellExposure, threshold = CostAccountingReadinessService.SmallCellThreshold },
        });
    }

    private static object SettingsBody(Company c, bool awaitingRun) =>
        new { enabled = c.CostAccountingEnabled, defaultCostCenterId = c.DefaultCostCenterId, smallCellMin = (int)c.CostCentreSmallCellMin, awaitingRun };

    private static object StateBody(Company c) => new { companyId = c.Id, changed = Array.Empty<string>(), settings = SettingsBody(c, false) };
}
