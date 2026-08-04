using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Governance;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Finance;

[Authorize]
[ApiController]
[Route("api/finance/bonuses")]
public class BonusesController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IDataScopeService _scopeService;
    private readonly ICompanyTaxPolicyResolver _taxResolver;

    // Legacy region-based tax rate (used only when no CompanyTaxPolicy is configured for the
    // bonus's company — see ResolveBonusTaxRateAsync).
    private static decimal ResolveTaxRate(BonusType bonusType) => bonusType.TaxRegion switch
    {
        "US"     => 0.22m,                                                    // US: 22% federal supplemental withholding
        "UK"     => bonusType.TaxRate > 0 ? bonusType.TaxRate / 100m : 0.20m, // UK: PAYE basic rate (overrideable)
        "Custom" => bonusType.TaxRate / 100m,
        _        => 0m, // GCC (UAE/KSA/Qatar/Bahrain/Oman/Kuwait) — no personal income tax
    };

    public BonusesController(ZayraDbContext db, IDataScopeService scopeService, ICompanyTaxPolicyResolver? taxResolver = null)
    {
        _db = db;
        _scopeService = scopeService;
        // Optional so existing test constructors keep working; DI always supplies the real one.
        _taxResolver = taxResolver ?? new CompanyTaxPolicyResolver(db);
    }

    /// <summary>
    /// FIX 3: resolve the effective bonus tax rate. A per-company CompanyTaxPolicy with
    /// AppliesToBonus (company override → tenant default) is the source of truth when configured;
    /// otherwise fall back to the legacy region switch. Null-safe: with no policy the behaviour is
    /// identical to before, so existing bonus flows are unchanged.
    /// </summary>
    private async Task<decimal> ResolveBonusTaxRateAsync(Guid tenantId, Guid? companyId, BonusType bonusType, DateOnly onDate, CancellationToken ct)
    {
        if (companyId is not null)
        {
            var policy = await _taxResolver.ResolveAsync(tenantId, companyId, onDate, ct);
            if (policy is { AppliesToBonus: true, IncomeTaxRatePercent: decimal pct })
                return pct / 100m;
        }
        return ResolveTaxRate(bonusType);
    }

    private Guid GetTenantId() =>
        Guid.TryParse(User.FindFirst("tenant_id")?.Value, out var id) ? id : Guid.Empty;
    private Guid? GetUserId() =>
        Guid.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;
    private string GetUserName() => User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? "Unknown";

    // ── Bonus Types ───────────────────────────────────────────────────────────

    [HttpGet("types")]
    public async Task<IActionResult> ListBonusTypes([FromQuery] bool includeInactive = false, CancellationToken ct = default)
    {
        var tid = GetTenantId();
        var q = _db.BonusTypes.Where(x => x.TenantId == tid && !x.IsDeleted);
        if (!includeInactive) q = q.Where(x => x.IsActive);
        return Ok(await q.OrderBy(x => x.NameEn).ToListAsync(ct));
    }

    [HttpPost("types")]
    [HasPermission("payroll.write")]
    public async Task<IActionResult> CreateBonusType([FromBody] BonusTypeRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        if (await _db.BonusTypes.AnyAsync(x => x.TenantId == tid && x.Code == req.Code && !x.IsDeleted, ct))
            return Conflict("Bonus type code already exists.");
        var t = new BonusType
        {
            TenantId = tid, Code = req.Code, NameEn = req.NameEn, NameAr = req.NameAr ?? string.Empty,
            CalculationMethod = req.CalculationMethod,
            DefaultCalculationValue = req.DefaultCalculationValue ?? 0,
            Frequency = req.Frequency ?? "OneTime",
            MinServiceMonths = req.MinServiceMonths ?? 0,
            ProRataEligibility = req.ProRataEligibility ?? false,
            RequiresApproval = req.RequiresApproval ?? true,
            IsIncludedInEosb = req.IsIncludedInEosb ?? false,
            IsIncludedInGosiBase = req.IsIncludedInGosiBase ?? false,
            IsIncludedInWps = req.IsIncludedInWps ?? true,
            IsTaxable = req.IsTaxable,
            TaxRegion = req.TaxRegion ?? "GCC",
            TaxRate = req.TaxRate ?? 0,
            Notes = req.Notes ?? string.Empty,
            CreatedBy = GetUserId(),
        };
        _db.BonusTypes.Add(t);
        await _db.SaveChangesAsync(ct);
        // SAFE-SERIALIZATION: BonusType is policy config (rates, tax region) — no personal PII.
        return Ok(t);
    }

    [HttpPut("types/{id:guid}")]
    [HasPermission("payroll.write")]
    public async Task<IActionResult> UpdateBonusType(Guid id, [FromBody] BonusTypeRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var t = await _db.BonusTypes.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (t == null) return NotFound();
        if (t.Code != req.Code && await _db.BonusTypes.AnyAsync(x => x.TenantId == tid && x.Code == req.Code && x.Id != id && !x.IsDeleted, ct))
            return Conflict("Bonus type code already exists.");
        t.Code = req.Code; t.NameEn = req.NameEn; t.NameAr = req.NameAr ?? string.Empty;
        t.CalculationMethod = req.CalculationMethod;
        t.DefaultCalculationValue = req.DefaultCalculationValue ?? t.DefaultCalculationValue;
        t.Frequency = req.Frequency ?? t.Frequency;
        t.MinServiceMonths = req.MinServiceMonths ?? t.MinServiceMonths;
        t.ProRataEligibility = req.ProRataEligibility ?? t.ProRataEligibility;
        t.RequiresApproval = req.RequiresApproval ?? t.RequiresApproval;
        t.IsIncludedInEosb = req.IsIncludedInEosb ?? t.IsIncludedInEosb;
        t.IsIncludedInGosiBase = req.IsIncludedInGosiBase ?? t.IsIncludedInGosiBase;
        t.IsIncludedInWps = req.IsIncludedInWps ?? t.IsIncludedInWps;
        t.IsTaxable = req.IsTaxable;
        t.TaxRegion = req.TaxRegion ?? t.TaxRegion;
        t.TaxRate = req.TaxRate ?? t.TaxRate;
        t.Notes = req.Notes ?? string.Empty;
        t.IsActive = req.IsActive ?? t.IsActive;
        t.UpdatedAtUtc = DateTime.UtcNow; t.UpdatedBy = GetUserId();
        await _db.SaveChangesAsync(ct);
        // SAFE-SERIALIZATION: BonusType is policy config (rates, tax region) — no personal PII.
        return Ok(t);
    }

    [HttpDelete("types/{id:guid}")]
    [HasPermission("payroll.write")]
    public async Task<IActionResult> DeleteBonusType(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        var t = await _db.BonusTypes.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (t == null) return NotFound();
        if (await _db.BonusBatches.AnyAsync(x => x.BonusTypeId == id && !x.IsDeleted && x.Status != "Cancelled", ct))
            return BadRequest("Cannot delete a bonus type that has active or approved batches. Deactivate it instead.");
        t.IsDeleted = true; t.IsActive = false;
        t.UpdatedAtUtc = DateTime.UtcNow; t.UpdatedBy = GetUserId();
        await _db.SaveChangesAsync(ct);
        return Ok(new { deleted = true });
    }

    // ── Bonus Batches ─────────────────────────────────────────────────────────

    [HttpGet("batches")]
    public async Task<IActionResult> ListBatches(
        [FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var tid = GetTenantId();
        var q = _db.BonusBatches.Where(x => x.TenantId == tid && !x.IsDeleted);
        if (!string.IsNullOrEmpty(status)) q = q.Where(x => x.Status == status);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(x => x.CreatedAtUtc)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return Ok(new { total, items });
    }

    [HttpGet("batches/{id:guid}")]
    public async Task<IActionResult> GetBatch(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        var batch = await _db.BonusBatches.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (batch == null) return NotFound();

        var scope = await _scopeService.ResolveAsync(User, tid, ct);
        IQueryable<EmployeeBonus> bonusQuery = _db.EmployeeBonuses.Where(x => x.BonusBatchId == id && !x.IsDeleted);
        if (!scope.IsUnrestricted)
        {
            var callerUserId = GetUserId();
            if (callerUserId.HasValue)
                bonusQuery = bonusQuery.Where(x => x.EmployeeId == callerUserId.Value);
            else
                bonusQuery = bonusQuery.Where(_ => false);
        }

        var bonuses = await bonusQuery.ToListAsync(ct);
        var approvals = await _db.BonusApprovals.Where(x => x.BonusBatchId == id).OrderBy(x => x.StepOrder).ToListAsync(ct);
        var auditLogs = await _db.BonusAuditLogs.Where(x => x.BonusBatchId == id).OrderByDescending(x => x.CreatedAtUtc).ToListAsync(ct);
        var glEntries = await _db.FinanceGlEntries.Where(x => x.SourceEntityId == id).OrderByDescending(x => x.EntryDate).ToListAsync(ct);
        // SAFE-SERIALIZATION: batch is BonusBatch (aggregate totals, no per-employee PII).
        return Ok(new { batch, bonuses = bonuses.Select(b => EmployeeBonusDto.Project(b, scope.IsUnrestricted)).ToList(), approvals, auditLogs, glEntries });
    }

    [HttpPost("batches")]
    [HasPermission("payroll.write")]
    public async Task<IActionResult> CreateBatch([FromBody] CreateBatchRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        var bonusType = await _db.BonusTypes.FirstOrDefaultAsync(x => x.Id == req.BonusTypeId && x.TenantId == tid, ct);
        if (bonusType == null) return NotFound("Bonus type not found.");

        var count = await _db.BonusBatches.CountAsync(x => x.TenantId == tid, ct);
        var batchNumber = $"BON-{DateTime.UtcNow.Year}-{(count + 1):D3}";

        var batch = new BonusBatch
        {
            TenantId = tid, BonusTypeId = req.BonusTypeId, BonusTypeName = bonusType.NameEn,
            BatchNumber = batchNumber, BatchName = req.BatchName, PaymentPeriod = req.PaymentPeriod,
            PaymentDate = req.PaymentDate, Notes = req.Notes ?? string.Empty, Status = "Draft",
            CreatedBy = uid,
        };
        _db.BonusBatches.Add(batch);
        await _db.SaveChangesAsync(ct);
        await WriteBonusAudit(tid, uid, batch.Id, null, "BatchCreated", null,
            JsonSerializer.Serialize(new { batch.BatchNumber, batch.BatchName }), ct);
        // SAFE-SERIALIZATION: BonusBatch is a workflow aggregate (total amounts, batch metadata) — no per-employee PII.
        return Ok(batch);
    }

    [HttpPut("batches/{id:guid}")]
    [HasPermission("payroll.write")]
    public async Task<IActionResult> UpdateBatch(Guid id, [FromBody] UpdateBatchRequest req, CancellationToken ct)
    {
        var tid = GetTenantId(); var uid = GetUserId();
        var batch = await _db.BonusBatches.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (batch == null) return NotFound();
        if (batch.Status != "Draft") return BadRequest("Only Draft batches can be edited.");
        batch.BatchName = req.BatchName ?? batch.BatchName;
        if (req.PaymentPeriod != null) batch.PaymentPeriod = req.PaymentPeriod;
        if (req.PaymentDate.HasValue) batch.PaymentDate = req.PaymentDate.Value;
        batch.Notes = req.Notes ?? batch.Notes;
        batch.UpdatedAtUtc = DateTime.UtcNow; batch.UpdatedBy = uid;
        await _db.SaveChangesAsync(ct);
        // SAFE-SERIALIZATION: BonusBatch is a workflow aggregate (total amounts, batch metadata) — no per-employee PII.
        return Ok(batch);
    }

    [HttpDelete("batches/{id:guid}")]
    [HasPermission("payroll.write")]
    public async Task<IActionResult> DeleteBatch(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId(); var uid = GetUserId();
        var batch = await _db.BonusBatches.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (batch == null) return NotFound();
        if (batch.Status is not ("Draft" or "Cancelled"))
            return BadRequest("Only Draft or Cancelled batches can be deleted. Reject the batch first.");
        batch.IsDeleted = true; batch.UpdatedAtUtc = DateTime.UtcNow; batch.UpdatedBy = uid;
        // Soft-delete child bonuses
        await _db.EmployeeBonuses.Where(b => b.BonusBatchId == id && !b.IsDeleted)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.IsDeleted, true), ct);
        await _db.SaveChangesAsync(ct);
        return Ok(new { deleted = true });
    }

    // ── Employee Bonuses ──────────────────────────────────────────────────────

    [HttpPost("batches/{batchId:guid}/employees")]
    [HasPermission("payroll.write")]
    public async Task<IActionResult> AddEmployeeBonus(Guid batchId, [FromBody] AddEmployeeBonusRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        var batch = await _db.BonusBatches.FirstOrDefaultAsync(x => x.Id == batchId && x.TenantId == tid && !x.IsDeleted, ct);
        if (batch == null) return NotFound("Batch not found.");
        if (batch.Status != "Draft") return BadRequest("Can only add employees to a Draft batch.");

        var bonusType = await _db.BonusTypes.FirstOrDefaultAsync(x => x.Id == batch.BonusTypeId && x.TenantId == tid, ct);

        decimal grossBonusAmount = req.CalculationMethod switch
        {
            "PercentageSalary" => Math.Round(req.BasicSalary * (req.CalculationValue / 100m), 2),
            _ => req.CalculationValue,
        };

        // Resolve int PK (Employee.Id) for payroll-run matching + company (for tax policy) up front.
        var linked = await _db.Employees
            .Where(e => e.TenantId == tid && e.FullName == req.EmployeeName && !e.IsDeleted)
            .Select(e => new { e.Id, e.CompanyId })
            .FirstOrDefaultAsync(ct);
        var linkedEmployee = (int?)linked?.Id;

        // Tax: per-company CompanyTaxPolicy override (FIX 3) → legacy region switch. GCC = zero PIT.
        var bonusTaxRate = bonusType?.IsTaxable == true
            ? await ResolveBonusTaxRateAsync(tid, linked?.CompanyId, bonusType!, DateOnly.FromDateTime(DateTime.UtcNow), ct)
            : 0m;
        decimal taxWithheld = Math.Round(grossBonusAmount * bonusTaxRate, 2);
        decimal netBonusAmount = grossBonusAmount - taxWithheld;

        var eb = new EmployeeBonus
        {
            TenantId = tid, BonusBatchId = batchId, EmployeeId = req.EmployeeId,
            EmployeeIntId = linkedEmployee,
            EmployeeName = req.EmployeeName, Department = req.Department ?? string.Empty,
            BonusTypeId = batch.BonusTypeId, BonusTypeName = batch.BonusTypeName,
            BasicSalary = req.BasicSalary, CalculationMethod = req.CalculationMethod,
            CalculationValue = req.CalculationValue,
            GrossBonusAmount = grossBonusAmount, TaxWithheld = taxWithheld, BonusAmount = netBonusAmount,
            TaxRegion = bonusType?.TaxRegion ?? "GCC",
            PaymentPeriod = batch.PaymentPeriod, Status = "Draft", Notes = req.Notes ?? string.Empty,
            CreatedBy = uid,
        };
        _db.EmployeeBonuses.Add(eb);

        // Track net (post-tax) amount in batch totals
        batch.TotalAmount += netBonusAmount;
        batch.EmployeeCount++;
        batch.UpdatedAtUtc = DateTime.UtcNow; batch.UpdatedBy = uid;

        await _db.SaveChangesAsync(ct);
        return Ok(new { bonus = EmployeeBonusDto.Project(eb, true), grossBonusAmount, taxWithheld, netBonusAmount });
    }

    [HttpPost("batches/{batchId:guid}/employees/bulk")]
    [HasPermission("payroll.write")]
    public async Task<IActionResult> BulkAddEmployees(Guid batchId, [FromBody] BulkAddRequest req, CancellationToken ct)
    {
        var tid = GetTenantId(); var uid = GetUserId();
        var batch = await _db.BonusBatches.FirstOrDefaultAsync(x => x.Id == batchId && x.TenantId == tid && !x.IsDeleted, ct);
        if (batch == null) return NotFound("Batch not found.");
        if (batch.Status != "Draft") return BadRequest("Can only add employees to a Draft batch.");

        var bonusType = await _db.BonusTypes.FirstOrDefaultAsync(x => x.Id == batch.BonusTypeId && x.TenantId == tid, ct);
        if (bonusType == null) return NotFound("Bonus type not found.");

        // Already-added employee int IDs (to skip duplicates)
        var alreadyAdded = await _db.EmployeeBonuses
            .Where(x => x.BonusBatchId == batchId && !x.IsDeleted)
            .Select(x => x.EmployeeId)
            .ToListAsync(ct);

        // Load active employees for this tenant
        var empQuery = _db.Employees
            .Where(e => e.TenantId == tid && e.Status == "Active");
        if (req.DepartmentId.HasValue)
            empQuery = empQuery.Where(e => e.DepartmentId == req.DepartmentId.Value);
        else if (!string.IsNullOrEmpty(req.Department))
            empQuery = empQuery.Where(e => e.Department == req.Department);
        if (req.CompanyId.HasValue)
            empQuery = empQuery.Where(e => e.CompanyId == req.CompanyId.Value);
        if (req.GradeId.HasValue)
            empQuery = empQuery.Where(e => e.GradeId == req.GradeId.Value);

        var employees = await empQuery.ToListAsync(ct);

        // Load salary structures once — keyed by Employee.Id
        var empIds = employees.Select(e => e.Id).ToList();
        var salaryMap = await _db.EmployeeSalaryStructures
            .Where(s => empIds.Contains(s.EmployeeId) && s.IsActive && s.EffectiveDate <= DateOnly.FromDateTime(DateTime.UtcNow))
            .GroupBy(s => s.EmployeeId)
            .Select(g => new { EmployeeId = g.Key, BasicSalary = g.OrderByDescending(s => s.EffectiveDate).First().BasicSalary })
            .ToDictionaryAsync(x => x.EmployeeId, x => x.BasicSalary, ct);

        var minServiceCutoff = DateTime.UtcNow.AddMonths(-bonusType.MinServiceMonths);
        // Per-company CompanyTaxPolicy override (FIX 3) keyed on the bulk target company.
        decimal taxRate = await ResolveBonusTaxRateAsync(tid, req.CompanyId, bonusType, DateOnly.FromDateTime(DateTime.UtcNow), ct);

        int added = 0; int skippedDuplicate = 0; int skippedMinService = 0; int skippedNoSalary = 0;
        decimal totalNetAdded = 0m;
        var newBonuses = new List<EmployeeBonus>();

        // Build a set of already-added EmployeeId GUIDs for fast lookup — but our EmployeeBonus.EmployeeId is Guid
        // We track by employee int Id via EmployeeIntId linkage; fall back to name match if needed
        // Use a simpler approach: load existing by employee int ids
        var alreadyAddedIntIds = await _db.EmployeeBonuses
            .Where(x => x.BonusBatchId == batchId && !x.IsDeleted)
            .Select(x => x.EmployeeName)
            .ToListAsync(ct);
        var alreadyAddedSet = new HashSet<string>(alreadyAddedIntIds);

        foreach (var emp in employees)
        {
            // Duplicate check by name (EmployeeBonus.EmployeeName)
            if (alreadyAddedSet.Contains(emp.FullName)) { skippedDuplicate++; continue; }

            // Min service months
            if (bonusType.MinServiceMonths > 0 && emp.JoiningDate > minServiceCutoff) { skippedMinService++; continue; }

            // Get basic salary — prefer salary structure, fall back to Employee.Salary
            if (!salaryMap.TryGetValue(emp.Id, out var basicSalary))
                basicSalary = emp.Salary ?? 0;
            if (basicSalary <= 0) { skippedNoSalary++; continue; }

            var calcValue = req.OverrideCalculationValue.HasValue ? req.OverrideCalculationValue.Value : bonusType.DefaultCalculationValue;
            var method = bonusType.CalculationMethod;
            decimal gross = method == "PercentageSalary" ? Math.Round(basicSalary * (calcValue / 100m), 2) : calcValue;
            decimal tax  = bonusType.IsTaxable ? Math.Round(gross * taxRate, 2) : 0m;
            decimal net  = gross - tax;

            var eb = new EmployeeBonus
            {
                TenantId = tid, BonusBatchId = batchId, EmployeeId = Guid.NewGuid(),
                EmployeeIntId = emp.Id,   // int PK for payroll-run join
                EmployeeName = emp.FullName, Department = emp.Department,
                BonusTypeId = bonusType.Id, BonusTypeName = bonusType.NameEn,
                BasicSalary = basicSalary, CalculationMethod = method, CalculationValue = calcValue,
                GrossBonusAmount = gross, TaxWithheld = tax, BonusAmount = net,
                TaxRegion = bonusType.TaxRegion, PaymentPeriod = batch.PaymentPeriod,
                Status = "Draft", CreatedBy = uid,
            };
            newBonuses.Add(eb);
            totalNetAdded += net;
            added++;
        }

        if (newBonuses.Count > 0)
        {
            _db.EmployeeBonuses.AddRange(newBonuses);
            batch.TotalAmount += totalNetAdded;
            batch.EmployeeCount += added;
            batch.UpdatedAtUtc = DateTime.UtcNow; batch.UpdatedBy = uid;
            await _db.SaveChangesAsync(ct);
        }

        return Ok(new { added, skippedDuplicate, skippedMinService, skippedNoSalary, totalNetAdded });
    }

    [HttpPut("batches/{batchId:guid}/employees/{bonusId:guid}")]
    [HasPermission("payroll.write")]
    public async Task<IActionResult> UpdateEmployeeBonus(Guid batchId, Guid bonusId, [FromBody] UpdateEmployeeBonusRequest req, CancellationToken ct)
    {
        var tid = GetTenantId(); var uid = GetUserId();
        var eb = await _db.EmployeeBonuses.FirstOrDefaultAsync(x => x.Id == bonusId && x.BonusBatchId == batchId && x.TenantId == tid && !x.IsDeleted, ct);
        if (eb == null) return NotFound();
        var batch = await _db.BonusBatches.FirstAsync(x => x.Id == batchId && x.TenantId == tid, ct);
        if (batch.Status != "Draft") return BadRequest("Can only edit employee bonuses in Draft batches.");
        var bonusType = await _db.BonusTypes.FirstOrDefaultAsync(x => x.Id == eb.BonusTypeId && x.TenantId == tid, ct);

        var oldNet = eb.BonusAmount;
        var method = req.CalculationMethod ?? eb.CalculationMethod;
        var value  = req.CalculationValue ?? eb.CalculationValue;
        var salary = req.BasicSalary ?? eb.BasicSalary;
        decimal gross = method == "PercentageSalary" ? Math.Round(salary * (value / 100m), 2) : value;
        // Per-company CompanyTaxPolicy override (FIX 3) keyed on the bonus's company.
        decimal tax   = (bonusType?.IsTaxable == true)
            ? Math.Round(gross * await ResolveBonusTaxRateAsync(tid, eb.CompanyId, bonusType!, DateOnly.FromDateTime(DateTime.UtcNow), ct), 2)
            : 0m;
        decimal net   = gross - tax;

        eb.CalculationMethod = method; eb.CalculationValue = value; eb.BasicSalary = salary;
        eb.GrossBonusAmount = gross; eb.TaxWithheld = tax; eb.BonusAmount = net;
        eb.Notes = req.Notes ?? eb.Notes; eb.UpdatedAtUtc = DateTime.UtcNow; eb.UpdatedBy = uid;

        batch.TotalAmount = batch.TotalAmount - oldNet + net;
        batch.UpdatedAtUtc = DateTime.UtcNow; batch.UpdatedBy = uid;
        await _db.SaveChangesAsync(ct);
        return Ok(new { bonus = EmployeeBonusDto.Project(eb, true), grossBonusAmount = gross, taxWithheld = tax, netBonusAmount = net });
    }

    [HttpDelete("batches/{batchId:guid}/employees/{bonusId:guid}")]
    [HasPermission("payroll.write")]
    public async Task<IActionResult> RemoveEmployeeBonus(Guid batchId, Guid bonusId, CancellationToken ct)
    {
        var tid = GetTenantId();
        var eb = await _db.EmployeeBonuses.FirstOrDefaultAsync(x => x.Id == bonusId && x.BonusBatchId == batchId && x.TenantId == tid, ct);
        if (eb == null) return NotFound();
        var batch = await _db.BonusBatches.FirstAsync(x => x.Id == batchId && x.TenantId == tid, ct);
        if (batch.Status != "Draft") return BadRequest("Cannot remove employees from non-Draft batch.");
        eb.IsDeleted = true; eb.UpdatedAtUtc = DateTime.UtcNow;
        batch.TotalAmount -= eb.BonusAmount; batch.EmployeeCount--;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Batch Workflow ────────────────────────────────────────────────────────

    [HttpPatch("batches/{id:guid}/submit")]
    [HasPermission("payroll.write")]
    public async Task<IActionResult> SubmitBatch(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        var batch = await _db.BonusBatches.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (batch == null) return NotFound();
        if (batch.Status != "Draft") return BadRequest("Only Draft batches can be submitted.");
        if (batch.EmployeeCount == 0) return BadRequest("Batch has no employees.");
        batch.Status = "PendingApproval"; batch.UpdatedAtUtc = DateTime.UtcNow; batch.UpdatedBy = uid;
        await _db.SaveChangesAsync(ct);
        await WriteBonusAudit(tid, uid, id, null, "BatchSubmitted", "Draft", "PendingApproval", ct);
        // SAFE-SERIALIZATION: BonusBatch is a workflow aggregate (total amounts, batch metadata) — no per-employee PII.
        return Ok(batch);
    }

    [HttpPatch("batches/{id:guid}/approve")]
    [HasPermission("payroll.approve")]
    public async Task<IActionResult> ApproveBatch(Guid id, [FromBody] BatchApproveRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        var batch = await _db.BonusBatches.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (batch == null) return NotFound();
        if (batch.Status != "PendingApproval") return BadRequest("Batch is not pending approval.");

        // POD-B1b-FIX (P1-6) — approving accrues per company; a company-scoped approver used to see only
        // their own slice, accrue only that, and still flip the whole batch to Approved, stranding the
        // other entities' bonuses in a batch nothing would ever accrue for. Checked BEFORE any mutation.
        var (bonuses, scopeRefusal) = await LoadWholeBatchBonusesAsync(tid, id, ct);
        if (scopeRefusal is not null) return scopeRefusal;

        batch.Status = "Approved"; batch.UpdatedAtUtc = DateTime.UtcNow; batch.UpdatedBy = uid;

        var approval = new BonusApproval
        {
            TenantId = tid, BonusBatchId = id, StepOrder = 1, ApproverRole = "Finance",
            ApprovedBy = uid, ApprovedByName = GetUserName(),
            Status = "Approved", Comments = req.Comments ?? string.Empty,
            DecidedAtUtc = DateTime.UtcNow,
        };
        _db.BonusApprovals.Add(approval);

        foreach (var b in bonuses) b.Status = "Approved";

        // ── GL: accrue the bonus, ONCE, at GROSS ──────────────────────────────────────────────────
        // POD-B1b. Two things changed here and both are required for the ledger to tie out:
        //
        //  1. GROSS, not batch.TotalAmount (which accumulates NET — see :305/:401-408/:443). The payroll
        //     run pays the bonus GROSS and withholds the tax as its own deduction, so accruing net left
        //     the payable permanently short by Σ TaxWithheld. For GCC tenants tax is 0, so the amount is
        //     numerically identical to before.
        //  2. PER COMPANY, through the same company-first resolution payroll uses
        //     (GlAccountResolver.LoadAsync + ResolveGlAccount). A batch may span legal entities; each
        //     entity gets its own journal, on its own accounts, in its own currency, guarded by its own
        //     period close, and stamped with CompanyId so the payroll clearing can find it exactly.
        //
        // Idempotent: an accrual already posted for (batch, company) is never posted twice.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        // IgnoreQueryFilters is intentional: an idempotency probe over the ledger must see accruals
        // booked for ANY company in this batch, including unattributed legacy rows, regardless of the
        // approver's own company claims; the TenantId + SourceEntityId predicate keeps it tenant-contained.
        var existingAccrualCompanies = await _db.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tid && x.SourceModule == BonusGlLedger.SourceModule && x.SourceEntityId == id
                     && (x.EventType == GlEventTypes.BonusAccrual || x.EventType == GlEventTypes.BonusAccrualLegacy))
            .Select(x => x.CompanyId)
            .ToListAsync(ct);
        var alreadyAccrued = existingAccrualCompanies.ToHashSet();

        foreach (var group in bonuses.GroupBy(b => b.CompanyId).OrderBy(g => g.Key))
        {
            var companyId = group.Key;
            if (alreadyAccrued.Contains(companyId)) continue;
            var grossTotal = Math.Round(group.Sum(b => b.GrossBonusAmount), 2);
            if (grossTotal <= 0m) continue;

            // Throws BEFORE the Add/SaveChanges so nothing commits into a closed period. Per-company now
            // that attribution exists; PeriodCloseGuard still ORs the group-wide close (PeriodCloseGuard.cs:31).
            await PeriodCloseGuard.ThrowIfClosedAsync(_db, tid, companyId, batch.PaymentPeriod, ct);

            var glCtx = await GlAccountResolver.LoadAsync(_db, tid, companyId, ct);
            _db.FinanceGlEntries.Add(new FinanceGlEntry
            {
                TenantId = tid, CompanyId = companyId,
                SourceModule = BonusGlLedger.SourceModule, SourceEntityId = id,
                SourceEntityRef = batch.BatchNumber, EventType = GlEventTypes.BonusAccrual,
                DebitAccount = GlAccountResolver.AccountLabel("EARN:BONUS", glCtx),
                CreditAccount = GlAccountResolver.AccountLabel("BONUS_PAYABLE", glCtx),
                Amount = grossTotal,
                Currency = await GlAccountResolver.ResolveCurrencyAsync(_db, tid, companyId, ct),
                EntryDate = today, Period = batch.PaymentPeriod,
                Description = $"{BonusGlDescriptions.AccrualPrefix}{batch.BatchName} ({batch.BatchNumber})",
                PostedBy = uid, PostedByName = GetUserName(),
            });
        }

        await _db.SaveChangesAsync(ct);
        await WriteBonusAudit(tid, uid, id, null, "BatchApproved", "PendingApproval",
            JsonSerializer.Serialize(new { Status = "Approved", Comments = req.Comments, batch.TotalAmount }), ct);
        // SAFE-SERIALIZATION: BonusBatch is a workflow aggregate (total amounts, batch metadata) — no per-employee PII.
        return Ok(batch);
    }

    [HttpPatch("batches/{id:guid}/reject")]
    [HasPermission("payroll.approve")]
    public async Task<IActionResult> RejectBatch(Guid id, [FromBody] RejectRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        var batch = await _db.BonusBatches.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (batch == null) return NotFound();

        // ── POD-B1b-FIX (P1-5) — this endpoint is now an ACCRUAL-REVERSAL path, so it must be guarded ──
        // It used to set Status="Cancelled" from ANY status. Cancelling a Paid batch contra'd nothing but
        // rewrote history; cancelling an already-Cancelled one could contra a second time; and cancelling
        // mid-payroll raced the run that was consuming it. Only a batch that has not been paid and is not
        // being paid may be cancelled. ("Approved" is the accrued state; Draft/PendingApproval carry no
        // GL at all and keep the endpoint's original reject-a-submission purpose.)
        if (batch.IsLockedByPayroll)
            return Conflict(new
            {
                error   = "locked_by_payroll",
                message = "This batch is locked by a payroll run and cannot be cancelled. Void the payroll " +
                          "run first — that re-opens the bonuses and the Bonus Payable.",
            });
        if (batch.Status is not ("Draft" or "PendingApproval" or "Approved"))
            return BadRequest(new
            {
                error   = "invalid_status",
                message = $"A '{batch.Status}' batch cannot be cancelled. Only Draft, PendingApproval or " +
                          "Approved batches can be.",
            });

        // P1-6 — cancelling flips BATCH-level state and contras EVERY company's accrual, so it must never
        // run from a partial, company-filtered view of the children. Checked BEFORE any mutation.
        var (childBonuses, scopeRefusal) = await LoadWholeBatchBonusesAsync(tid, id, ct);
        if (scopeRefusal is not null) return scopeRefusal;

        // ── POD-B1b-FIX (re-audit #2) — refuse while ANY child is held by a payroll run ───────────────
        // IsLockedByPayroll above only catches a FULLY consumed batch: PayrollController.cs:1161-1169
        // locks the batch only when no approved bonus is left, so a partially consumed batch is still
        // Approved / unlocked. Cancelling it contra'd the outstanding accrual and cancelled the unpaid
        // children, but left the consumed ones attached to the run — and if that run was later VOIDED,
        // the void's P1-4 re-open put them back to Approved / PayrollRunId = null under a Cancelled
        // parent, where PayrollController.cs:670-677 (which keys on the bonus status alone) had the next
        // run pay them. Voiding the run FIRST releases those children (they return to Approved under an
        // Approved batch) and the cancel then behaves exactly as it does for an untouched batch.
        var heldByRun = childBonuses.Where(b => b.Status == "PaidInPayroll" || b.PayrollRunId != null).ToList();
        if (heldByRun.Count > 0)
            return Conflict(new
            {
                error   = "locked_by_payroll",
                message = "Part of this batch has already been consumed by a payroll run and cannot be " +
                          "cancelled. Void that payroll run first — that re-opens the bonuses and the " +
                          "Bonus Payable — then cancel the batch.",
                batchId = id,
                consumedEmployeeBonuses = heldByRun.Count,
                payrollRunIds = heldByRun.Select(b => b.PayrollRunId).Where(r => r != null).Distinct().ToList(),
            });

        var previousStatus = batch.Status;
        batch.Status = "Cancelled"; batch.UpdatedAtUtc = DateTime.UtcNow; batch.UpdatedBy = uid;

        // POD-B1b-FIX (P1-5) — cancel the CHILDREN too. PayrollController.cs:670-677 selects pending
        // bonuses on the BONUS status alone and ignores the batch, so leaving them "Approved" meant the
        // next payroll run happily paid a cancelled batch's bonuses — after this method had already
        // contra'd their accrual. The consumed-child filter is now unreachable (the heldByRun refusal
        // above rejects the whole request in that case) and is kept as a belt-and-braces invariant: a
        // bonus a run is holding is never rewritten from here, and the contra below only ever reverses
        // what is still OUTSTANDING on the accrual.
        var cancelledChildren = 0;
        foreach (var b in childBonuses.Where(x => x.Status != "PaidInPayroll" && x.PayrollRunId == null))
        {
            b.Status = "Cancelled";
            b.UpdatedAtUtc = DateTime.UtcNow; b.UpdatedBy = uid;
            cancelledChildren++;
        }

        // POD-B1b — cancelling an ALREADY-APPROVED batch used to leave its accrual standing forever:
        // the expense and the Bonus Payable both stayed on the books for a bonus nobody would ever be
        // paid. Post a contra for whatever is still outstanding (swap DR/CR, dated into the accrual
        // period, ReversalOfEntryId set — the BuildContraGl / PayrollVoidService pattern) so both the
        // expense and the payable return to zero. Nothing outstanding ⇒ nothing posted.
        var reversalToday = DateOnly.FromDateTime(DateTime.UtcNow);
        var openPositions = await BonusGlLedger.LoadPositionsAsync(_db, tid, new[] { id }, ct);
        if (openPositions.Any(p => p.Remaining > 0m))
        {
            // IgnoreQueryFilters is intentional: the contra must reverse the accrual lines exactly as
            // posted, for every company in the batch; TenantId + SourceEntityId keeps it tenant-contained.
            var accrualEntries = await _db.FinanceGlEntries.IgnoreQueryFilters()
                .Where(x => x.TenantId == tid && x.SourceModule == BonusGlLedger.SourceModule && x.SourceEntityId == id
                         && !x.IsReversed
                         && (x.EventType == GlEventTypes.BonusAccrual || x.EventType == GlEventTypes.BonusAccrualLegacy))
                .ToListAsync(ct);
            foreach (var position in openPositions.Where(p => p.Remaining > 0m))
            {
                await PeriodCloseGuard.ThrowIfClosedAsync(_db, tid, position.CompanyId, batch.PaymentPeriod, ct);
                var origin = accrualEntries.FirstOrDefault(
                    x => x.CompanyId == position.CompanyId && x.CreditAccount == position.AccrualAccount);
                if (origin is null) continue;
                _db.FinanceGlEntries.Add(new FinanceGlEntry
                {
                    TenantId = tid, CompanyId = position.CompanyId,
                    SourceModule = BonusGlLedger.SourceModule, SourceEntityId = id,
                    SourceEntityRef = batch.BatchNumber, EventType = GlEventTypes.BonusAccrualReversal,
                    DebitAccount = origin.CreditAccount, CreditAccount = origin.DebitAccount,
                    Amount = position.Remaining, Currency = position.Currency,
                    EntryDate = reversalToday, Period = origin.Period,
                    Description = $"{BonusGlDescriptions.ReversalPrefix}{batch.BatchName} ({batch.BatchNumber}) — {req.Reason}",
                    PostedBy = uid, PostedByName = GetUserName(),
                    ReversalOfEntryId = origin.Id,
                });
                // Only flag the original spent when the contra closes it out completely; a partially
                // consumed accrual must stay visible to the payable sub-ledger.
                if (position.Remaining >= position.Accrued) origin.IsReversed = true;
            }
        }

        await _db.SaveChangesAsync(ct);
        await WriteBonusAudit(tid, uid, id, null, "BatchRejected", previousStatus,
            JsonSerializer.Serialize(new { Status = "Cancelled", req.Reason, cancelledChildren }), ct);
        // SAFE-SERIALIZATION: BonusBatch is a workflow aggregate (total amounts, batch metadata) — no per-employee PII.
        return Ok(batch);
    }

    [HttpPatch("batches/{id:guid}/mark-paid")]
    [HasPermission("payroll.approve")]
    public async Task<IActionResult> MarkBatchPaid(Guid id, [FromBody] MarkBatchPaidRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        var batch = await _db.BonusBatches.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (batch == null) return NotFound();
        // Double-pay guard must run before the status check: Process() sets IsLockedByPayroll=true
        // and Status="Paid" simultaneously, so without this order the 409 is swallowed by the 400.
        if (batch.IsLockedByPayroll)
            return Conflict(new
            {
                error   = "already_paid_via_payroll",
                message = "This batch was already paid through a payroll run. " +
                          "Marking it paid again would double-pay employees.",
            });
        if (batch.Status != "Approved") return BadRequest("Only Approved batches can be marked as paid.");

        // POD-B1b-FIX (P1-6) — this method sets batch.Status="Paid" + IsLockedByPayroll=true for the WHOLE
        // batch, which is irreversible (the :631 409 blocks every retry). Reading the children through the
        // company filter therefore let a company-A-scoped user pay A's slice and permanently lock B out of
        // ever being paid or cleared. Refuse rather than half-process. Checked BEFORE any mutation.
        var (allBonuses, scopeRefusal) = await LoadWholeBatchBonusesAsync(tid, id, ct);
        if (scopeRefusal is not null) return scopeRefusal;

        batch.Status = "Paid"; batch.IsLockedByPayroll = true;
        batch.UpdatedAtUtc = DateTime.UtcNow; batch.UpdatedBy = uid;

        // Only mark bonuses that are still "Approved" — already-consumed bonuses (PaidInPayroll
        // via a payroll run) must not have their Status or PayrollRunId clobbered. This makes
        // MarkBatchPaid safe to call on a partially-consumed batch.
        var unpaidBonuses = allBonuses.Where(x => x.Status == "Approved").ToList();
        foreach (var b in unpaidBonuses)
        {
            b.Status = "PaidInPayroll";
            if (req.PayrollRunId.HasValue) b.PayrollRunId = req.PayrollRunId;
        }

        // ── GL: pay the bonus OUTSIDE payroll ─────────────────────────────────────────────────────
        // Covers only the remaining (previously-unpaid) bonuses so the payroll-consumed portion is never
        // double-counted. POD-B1b: per company, gross basis —
        //     DR Bonus Payable (the accrual's STORED account, capped by what is still outstanding)
        //     CR Income Tax Payable  (withheld)
        //     CR Cash/Bank           (net actually paid out)
        // Expense is NOT touched: it was recognised once, at approval. Any un-accrued remainder (a batch
        // paid without ever being accrued) debits the bonus expense here instead, so the expense still
        // lands exactly once and the journal is balanced either way.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var positions = await BonusGlLedger.LoadPositionsAsync(_db, tid, new[] { id }, ct);

        // ── POD-B1b-FIX (P0-1) — METER the accrual across company groups ──────────────────────────────
        // `positions` is an immutable snapshot taken ONCE, and BonusGlLedger.PositionFor falls back to the
        // unattributed (CompanyId == null) accrual for any company without an exact match — which is EVERY
        // company on all ~55 live tenants, because every pre-B1b accrual is CompanyId=NULL. Re-reading the
        // snapshot per group therefore handed the same 1,000 accrual to company A (600) and company B
        // (600): DR 2300 = 1,200, a 200 DEBIT balance on a liability, and 200 of bonus expense never
        // recognised — silently, because Remaining clamps at 0 (BonusGlLedger.cs:16). The cursor deducts
        // what this call has already reserved, so the second group sees only the 400 that is really left
        // and books its remaining 200 to bonus expense as un-accrued. Nothing is written by Take(), so a
        // period-close throw or a failed guard below still leaves the ledger untouched.
        var cursor = new BonusAccrualCursor(positions);
        // Independent re-derivation for the P0-3 assertion: what we ACTUALLY debited, per accrual.
        var payableDebited = new Dictionary<(Guid, Guid?, string), decimal>();

        foreach (var group in unpaidBonuses.GroupBy(b => b.CompanyId).OrderBy(g => g.Key))
        {
            var companyId = group.Key;
            var grossTotal = Math.Round(group.Sum(b => b.GrossBonusAmount), 2);
            var taxTotal   = Math.Round(group.Sum(b => b.TaxWithheld), 2);
            var netTotal   = Math.Round(grossTotal - taxTotal, 2);
            if (grossTotal <= 0m) continue;

            // Throws BEFORE the Add/SaveChanges so nothing commits into a closed period. Per-company.
            await PeriodCloseGuard.ThrowIfClosedAsync(_db, tid, companyId, batch.PaymentPeriod, ct);

            var glCtx = await GlAccountResolver.LoadAsync(_db, tid, companyId, ct);
            var currency = await GlAccountResolver.ResolveCurrencyAsync(_db, tid, companyId, ct);
            var (position, clearable) = cursor.Take(id, companyId, grossTotal);
            // Remap-immune: DR the account the accrual actually credited, never a re-resolved one.
            var payableAccount = position?.AccrualAccount ?? GlAccountResolver.AccountLabel("BONUS_PAYABLE", glCtx);
            var unaccrued = Math.Round(grossTotal - clearable, 2);
            if (position is not null && clearable > 0m)
                payableDebited[position.Key] = payableDebited.GetValueOrDefault(position.Key) + clearable;

            var label = $"{batch.BatchName} ({batch.BatchNumber})";
            var debits = new List<(string Account, decimal Amount, string Note)>();
            var credits = new List<(string Account, decimal Amount, string Note)>();
            if (clearable > 0m) debits.Add((payableAccount, clearable, BonusGlDescriptions.PaymentPrefix + label));
            if (unaccrued > 0m) debits.Add((GlAccountResolver.AccountLabel("EARN:BONUS", glCtx), unaccrued,
                $"{BonusGlDescriptions.PaymentPrefix}{label} (un-accrued)"));
            if (taxTotal > 0m) credits.Add((GlAccountResolver.AccountLabel("DED:TAX", glCtx), taxTotal,
                BonusGlDescriptions.TaxWithheldPrefix + label));
            if (netTotal > 0m) credits.Add((GlAccountResolver.AccountLabel("CASH_BANK", glCtx), netTotal,
                BonusGlDescriptions.PaymentPrefix + label));

            // Emit TWO-SIDED rows (DebitAccount + CreditAccount on one row) — the format this module has
            // always used, unlike payroll's one-sided lines. The debit and credit legs are equal in total
            // and are walked in lockstep, so an untaxed single-company payment stays exactly ONE row just
            // like before, and a taxed one splits into DR payable→CR tax + DR payable→CR cash.
            var lines = new List<FinanceGlEntry>();
            int di = 0, ci = 0;
            decimal dRem = debits.Count > 0 ? debits[0].Amount : 0m;
            decimal cRem = credits.Count > 0 ? credits[0].Amount : 0m;
            while (di < debits.Count && ci < credits.Count)
            {
                var chunk = Math.Min(dRem, cRem);
                if (chunk > 0m)
                    lines.Add(new FinanceGlEntry
                    {
                        TenantId = tid, CompanyId = companyId,
                        SourceModule = BonusGlLedger.SourceModule, SourceEntityId = id,
                        SourceEntityRef = batch.BatchNumber, EventType = GlEventTypes.BonusPayment,
                        DebitAccount = debits[di].Account, CreditAccount = credits[ci].Account,
                        Amount = chunk, Currency = currency,
                        EntryDate = today, Period = batch.PaymentPeriod,
                        Description = credits[ci].Account.Contains("Tax", StringComparison.OrdinalIgnoreCase)
                            ? credits[ci].Note : debits[di].Note,
                        PostedBy = uid, PostedByName = GetUserName(),
                    });
                dRem -= chunk; cRem -= chunk;
                if (dRem <= 0m && ++di < debits.Count) dRem = debits[di].Amount;
                if (cRem <= 0m && ++ci < credits.Count) cRem = credits[ci].Amount;
            }

            // ── POD-B1b-FIX (P0-3 + re-audit #4) — assert the TWO LEGS against each other ─────────────
            // The original check compared `clearable + unaccrued` with `taxTotal + netTotal`: both are
            // grossTotal by definition (:750 and :739), so it was algebraically 0 == 0 and could never
            // fire. Comparing the EMITTED rows to grossTotal was no better — the lockstep walker emits
            // min(Σdebits, Σcredits) and both legs are again grossTotal by construction.
            //
            // What is NOT a restatement is comparing the debit leg to the credit leg, because they are
            // built from different inputs and are only equal while netTotal >= 0. A row with
            // TaxWithheld > GrossBonusAmount (a mis-keyed tax, or a gross edited down after the tax was
            // computed) makes netTotal negative, drops the cash leg entirely (:762) and leaves
            // Σcredits = taxTotal > Σdebits = grossTotal — the walker still emits exactly grossTotal, so
            // the tax payable is silently UNDER-credited and the ledger is short. That case fires here,
            // and so does any drift the walker introduces between what was planned and what was built.
            var debitTotal  = debits.Sum(d => d.Amount);
            var creditTotal = credits.Sum(c => c.Amount);
            var emitted     = lines.Sum(l => l.Amount);
            if (Math.Abs(debitTotal - creditTotal) > 0.01m
                || Math.Abs(emitted - debitTotal) > 0.01m
                || Math.Abs(emitted - grossTotal) > 0.01m)
                return UnprocessableEntity(new
                {
                    error = "gl_unbalanced",
                    message = netTotal < 0m
                        ? "Bonus tax withheld exceeds the gross bonus for this company, so the payment " +
                          "journal cannot balance. Correct the bonus rows; nothing was posted."
                        : "Bonus payment GL does not tie to the gross being paid; nothing was posted.",
                    emittedTotal = emitted, totalDebits = debitTotal, totalCredits = creditTotal,
                    grossTotal, taxTotal, netTotal,
                    clearedFromPayable = clearable, expensedUnaccrued = unaccrued, companyId,
                });
            _db.FinanceGlEntries.AddRange(lines);
        }

        // ── POD-B1b-FIX (P0-3/P0-1) — the sub-ledger assertion, across ALL company groups ─────────────
        // Re-derived from what was actually debited (payableDebited), compared against the accrual as
        // LOADED FROM THE DATABASE (position.Remaining, untouched by the cursor). No batch may ever debit
        // Bonus Payable for more than is outstanding on it: that is exactly the invariant P0-1 broke.
        //
        // HONESTLY SCOPED (re-audit #4): `payableDebited` accumulates the cursor's own output, so this is
        // a REGRESSION NET over the cursor, not an independent re-derivation — it fires when a future
        // edit debits the payable around the cursor (a second Take path, a hand-rolled clearable, a
        // snapshot re-read per group, i.e. the exact shape of P0-1), not when the cursor's own arithmetic
        // is wrong. That is still the check whose absence let P0-1 ship. Fails loudly with 422 and
        // nothing is persisted (SaveChanges is below).
        foreach (var position in positions)
        {
            var debited = payableDebited.GetValueOrDefault(position.Key);
            if (debited - position.Remaining > 0.01m)
                return UnprocessableEntity(new
                {
                    error = "gl_overclears_accrual",
                    message = "Bonus payment would debit Bonus Payable for more than the batch has accrued, " +
                              "driving the liability to a debit balance. Nothing was posted.",
                    batchId = position.BatchId, accrualCompanyId = position.CompanyId,
                    account = position.AccrualAccount,
                    outstanding = position.Remaining, attemptedDebit = debited,
                });
        }

        await _db.SaveChangesAsync(ct);
        await WriteBonusAudit(tid, uid, id, null, "BatchPaid", "Approved",
            JsonSerializer.Serialize(new { Status = "Paid", PayrollRunId = req.PayrollRunId, batch.TotalAmount }), ct);
        // SAFE-SERIALIZATION: BonusBatch is a workflow aggregate (total amounts, batch metadata) — no per-employee PII.
        return Ok(batch);
    }

    // ── Payroll Integration ───────────────────────────────────────────────────

    [HttpGet("payroll-pending")]
    [Authorize(Roles = "Admin,Finance,HR Manager")]
    public async Task<IActionResult> GetPayrollPendingBonuses([FromQuery] string paymentPeriod, CancellationToken ct)
    {
        var tid = GetTenantId();
        var bonuses = await _db.EmployeeBonuses
            .Where(x => x.TenantId == tid && !x.IsDeleted && x.Status == "Approved"
                && x.PaymentPeriod == paymentPeriod && x.PayrollRunId == null)
            .ToListAsync(ct);
        return Ok(new { count = bonuses.Count, totalAmount = bonuses.Sum(x => x.BonusAmount), bonuses = bonuses.Select(b => EmployeeBonusDto.Project(b, true)).ToList() });
    }

    // ── Audit & Reconciliation Report ────────────────────────────────────────

    [HttpGet("audit")]
    [Authorize(Roles = "Admin,Finance,HR Manager")]
    public async Task<IActionResult> AuditReport([FromQuery] string? period, CancellationToken ct)
    {
        var tid = GetTenantId();
        var batchQuery = _db.BonusBatches.Where(x => x.TenantId == tid && !x.IsDeleted);
        if (!string.IsNullOrEmpty(period)) batchQuery = batchQuery.Where(x => x.PaymentPeriod == period);

        var batches = await batchQuery.OrderByDescending(x => x.CreatedAtUtc).ToListAsync(ct);
        var glEntries = await _db.FinanceGlEntries
            .Where(x => x.TenantId == tid && x.SourceModule == "Bonus").ToListAsync(ct);

        var bonusSummary = await _db.EmployeeBonuses
            .Where(x => x.TenantId == tid && !x.IsDeleted)
            .GroupBy(x => x.Department)
            .Select(g => new { Department = g.Key, Count = g.Count(), TotalAmount = g.Sum(b => b.BonusAmount) })
            .ToListAsync(ct);

        // POD-B1b-FIX (re-audit #7) — the control-account SIGN invariants, on this tenant's own resolved
        // accounts. A DEBIT balance on Bonus Payable means an accrual was cleared for more than it holds
        // (P0-1's fingerprint); a CREDIT balance on a receivable means one was relieved without ever
        // being disbursed (P0-2's). Surfaced HERE because this report is where bonuses are reconciled —
        // otherwise the invariant is only ever asserted in tests and a live break is invisible.
        var controlAccounts = await GlControlAccounts.CheckAsync(_db, tid, null, ct);

        return Ok(new
        {
            GeneratedAtUtc = DateTime.UtcNow,
            Period = period ?? "All",
            TotalBatches = batches.Count,
            ApprovedBatches = batches.Count(x => x.Status == "Approved"),
            PaidBatches = batches.Count(x => x.Status == "Paid"),
            TotalBonusAmount = batches.Sum(x => x.TotalAmount),
            PaidAmount = batches.Where(x => x.Status == "Paid").Sum(x => x.TotalAmount),
            PendingPaymentAmount = batches.Where(x => x.Status == "Approved").Sum(x => x.TotalAmount),
            GlEntriesCount = glEntries.Count,
            ControlAccountsHealthy = controlAccounts.All(a => a.IsHealthy),
            ControlAccountViolations = controlAccounts.Where(a => !a.IsHealthy).Select(a => a.Violation).ToList(),
            ControlAccounts = controlAccounts.Select(a => new
            {
                a.Driver, a.Account, a.Nature, a.TenantBalance, a.IsHealthy,
            }).ToList(),
            ByDepartment = bonusSummary,
            Batches = batches.Select(b => new
            {
                b.BatchNumber, b.BatchName, b.BonusTypeName,
                b.PaymentPeriod, b.PaymentDate, b.EmployeeCount,
                b.TotalAmount, b.Status, b.IsLockedByPayroll,
            }).ToList(),
        });
    }

    /// <summary>
    /// POD-B1b-FIX (P1-6) — loads EVERY EmployeeBonus in a batch, and refuses the operation outright when
    /// the caller's company scope cannot see all of them.
    ///
    /// <para>WHY. ApproveBatch / RejectBatch / MarkBatchPaid all flip BATCH-level state
    /// (<c>Status</c>, <c>IsLockedByPayroll</c>) unconditionally, but they used to read their children
    /// through the EmployeeBonus company query filter (ICompanyScopedOperational,
    /// ZayraDbContext.cs:3369-3374). For a batch spanning companies A and B, a company-A-scoped user
    /// therefore accrued/paid only A's slice and then permanently marked the WHOLE batch
    /// Paid + IsLockedByPayroll — after which MarkBatchPaid 409s (BonusesController.cs:637) and B's
    /// employees are never paid, B's Bonus Payable can never clear, and no error was ever surfaced.</para>
    ///
    /// <para>Failing loudly beats silently half-processing: a group-scope approver (or a company switch)
    /// completes the batch correctly, and the ledger is never left in a state only a DBA can repair.
    /// IgnoreQueryFilters is intentional on the full read — batch integrity is a SYSTEM question — and
    /// the explicit TenantId predicate re-applies exact tenant scope, so this never reads another tenant.</para>
    /// </summary>
    private async Task<(List<EmployeeBonus> Bonuses, IActionResult? Refusal)> LoadWholeBatchBonusesAsync(
        Guid tid, Guid batchId, CancellationToken ct)
    {
        var all = await _db.EmployeeBonuses.IgnoreQueryFilters()
            .Where(x => x.TenantId == tid && x.BonusBatchId == batchId && !x.IsDeleted)
            .ToListAsync(ct);

        // The same set as the caller's own scope sees. Equal ⇒ full visibility ⇒ safe to proceed.
        var visibleIds = (await _db.EmployeeBonuses.AsNoTracking()
            .Where(x => x.BonusBatchId == batchId && !x.IsDeleted)
            .Select(x => x.Id)
            .ToListAsync(ct)).ToHashSet();

        var hidden = all.Where(b => !visibleIds.Contains(b.Id)).ToList();
        if (hidden.Count == 0) return (all, null);

        // POD-B1b-FIX (re-audit #8) — distinguish the two causes, because the remedies differ.
        // EmployeeBonus is ICompanyScopedOperational, so a row with CompanyId == null is visible to GROUP
        // scope only (ZayraDbContext.cs:3369-3374). Every pre-company-layer bonus row is null, which would
        // make this refusal read as "spans other entities" when it really means "not yet attributed".
        // CompanyScopeBackfill.RunAsync (Program.cs:679, Infrastructure/Boot/CompanyScopeBackfill.cs:111)
        // stamps those rows from the owning employee on every boot and is idempotent — but it is
        // non-fatal and can be switched off (CompanyScope:Backfill=false), so a tenant CAN still be
        // sitting on unattributed rows. Say which it is.
        var unattributed = hidden.Count(b => b.CompanyId is null);
        return (all, StatusCode(403, new
        {
            error = "company_scope_partial",
            message = unattributed == hidden.Count
                ? "Some bonuses in this batch are not assigned to a legal entity yet, so they are visible " +
                  "to group scope only. Completing the batch from a partial view would lock the whole " +
                  "batch after processing only your slice, leaving the rest unpaid and their Bonus " +
                  "Payable permanently open. Retry in group scope; the company backfill assigns these " +
                  "rows on the next restart (or a re-run of CompanyScopeBackfill)."
                : "This bonus batch spans legal entities outside your company scope. Completing it " +
                  "from a partial view would lock the whole batch after processing only your slice, " +
                  "leaving the other entities unpaid and their Bonus Payable permanently open. " +
                  "Switch to group scope (or a scope covering every company in the batch) and retry.",
            batchId,
            totalEmployeeBonuses = all.Count,
            visibleEmployeeBonuses = visibleIds.Count,
            unattributedEmployeeBonuses = unattributed,
            outOfScopeCompanyIds = hidden.Select(b => b.CompanyId).Distinct().ToList(),
        }));
    }

    private async Task WriteBonusAudit(Guid tid, Guid? uid, Guid? batchId, Guid? bonusId, string action, string? oldVal, string newVal, CancellationToken ct)
    {
        _db.BonusAuditLogs.Add(new BonusAuditLog
        {
            TenantId = tid, BonusBatchId = batchId, EmployeeBonusId = bonusId,
            EntityType = batchId.HasValue ? "BonusBatch" : "EmployeeBonus",
            Action = action, OldValuesJson = oldVal ?? string.Empty, NewValuesJson = newVal,
            PerformedBy = uid, PerformedByName = GetUserName(),
        });
        await _db.SaveChangesAsync(ct);
    }
}

public record BonusTypeRequest(
    string Code, string NameEn, string? NameAr, string CalculationMethod, decimal? DefaultCalculationValue,
    bool IsTaxable, string? Frequency, int? MinServiceMonths, bool? ProRataEligibility, bool? RequiresApproval,
    bool? IsIncludedInEosb, bool? IsIncludedInGosiBase, bool? IsIncludedInWps,
    string? TaxRegion, decimal? TaxRate, string? Notes, bool? IsActive = null);
public record BulkAddRequest(Guid? CompanyId, Guid? DepartmentId, string? Department, Guid? GradeId, decimal? OverrideCalculationValue);
public record CreateBatchRequest(Guid BonusTypeId, string BatchName, string PaymentPeriod, DateOnly PaymentDate, string? Notes);
public record UpdateBatchRequest(string? BatchName, string? PaymentPeriod, DateOnly? PaymentDate, string? Notes);
public record AddEmployeeBonusRequest(Guid EmployeeId, string EmployeeName, string? Department, decimal BasicSalary, string CalculationMethod, decimal CalculationValue, string? Notes);
public record UpdateEmployeeBonusRequest(string? CalculationMethod, decimal? CalculationValue, decimal? BasicSalary, string? Notes);
public record BatchApproveRequest(string? Comments);
public record MarkBatchPaidRequest(Guid? PayrollRunId);
