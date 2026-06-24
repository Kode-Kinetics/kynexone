using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Finance;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Finance;

[Authorize]
[ApiController]
[Route("api/finance/bonuses")]
public class BonusesController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IDataScopeService _scopeService;

    // Tax rates by region (used when IsTaxable = true)
    private static decimal ResolveTaxRate(BonusType bonusType) => bonusType.TaxRegion switch
    {
        "US"     => 0.22m,                                                    // US: 22% federal supplemental withholding
        "UK"     => bonusType.TaxRate > 0 ? bonusType.TaxRate / 100m : 0.20m, // UK: PAYE basic rate (overrideable)
        "Custom" => bonusType.TaxRate / 100m,
        _        => 0m, // GCC (UAE/KSA/Qatar/Bahrain/Oman/Kuwait) — no personal income tax
    };

    public BonusesController(ZayraDbContext db, IDataScopeService scopeService)
    {
        _db = db;
        _scopeService = scopeService;
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
    [Authorize(Roles = "Admin,HR Manager")]
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
    [Authorize(Roles = "Admin,HR Manager")]
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
    [Authorize(Roles = "Admin,HR Manager")]
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
    [Authorize(Roles = "Admin,HR Manager,Finance")]
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
    [Authorize(Roles = "Admin,HR Manager,Finance")]
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
    [Authorize(Roles = "Admin,HR Manager,Finance")]
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
    [Authorize(Roles = "Admin,HR Manager,Finance")]
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

        // Region-aware tax calculation — GCC countries have zero personal income tax on bonuses
        decimal taxWithheld = (bonusType?.IsTaxable == true)
            ? Math.Round(grossBonusAmount * ResolveTaxRate(bonusType!), 2)
            : 0m;
        decimal netBonusAmount = grossBonusAmount - taxWithheld;

        // Resolve int PK (Employee.Id) for payroll-run matching — same bridge as Loans/Advances.
        var linkedEmployee = await _db.Employees
            .Where(e => e.TenantId == tid && e.FullName == req.EmployeeName && !e.IsDeleted)
            .Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(ct);

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
    [Authorize(Roles = "Admin,HR Manager,Finance")]
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
            .Where(s => empIds.Contains(s.EmployeeId) && s.IsActive)
            .GroupBy(s => s.EmployeeId)
            .Select(g => new { EmployeeId = g.Key, BasicSalary = g.OrderByDescending(s => s.EffectiveDate).First().BasicSalary })
            .ToDictionaryAsync(x => x.EmployeeId, x => x.BasicSalary, ct);

        var minServiceCutoff = DateTime.UtcNow.AddMonths(-bonusType.MinServiceMonths);
        decimal taxRate = ResolveTaxRate(bonusType);

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
    [Authorize(Roles = "Admin,HR Manager,Finance")]
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
        decimal tax   = (bonusType?.IsTaxable == true) ? Math.Round(gross * ResolveTaxRate(bonusType!), 2) : 0m;
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
    [Authorize(Roles = "Admin,HR Manager,Finance")]
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
    [Authorize(Roles = "Admin,HR Manager,Finance")]
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
    [Authorize(Roles = "Admin,Finance")]
    public async Task<IActionResult> ApproveBatch(Guid id, [FromBody] BatchApproveRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        var batch = await _db.BonusBatches.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (batch == null) return NotFound();
        if (batch.Status != "PendingApproval") return BadRequest("Batch is not pending approval.");
        batch.Status = "Approved"; batch.UpdatedAtUtc = DateTime.UtcNow; batch.UpdatedBy = uid;

        var approval = new BonusApproval
        {
            TenantId = tid, BonusBatchId = id, StepOrder = 1, ApproverRole = "Finance",
            ApprovedBy = uid, ApprovedByName = GetUserName(),
            Status = "Approved", Comments = req.Comments ?? string.Empty,
            DecidedAtUtc = DateTime.UtcNow,
        };
        _db.BonusApprovals.Add(approval);

        var bonuses = await _db.EmployeeBonuses.Where(x => x.BonusBatchId == id && !x.IsDeleted).ToListAsync(ct);
        foreach (var b in bonuses) b.Status = "Approved";

        // GL: Bonus Expense Dr / Bonus Payable Cr
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var bonusApprovalCurrency = await _db.ResolveTenantCurrencyAsync(tid, ct);
        _db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = tid, SourceModule = "Bonus", SourceEntityId = id,
            SourceEntityRef = batch.BatchNumber, EventType = "BonusApproval",
            DebitAccount = "6100 - Employee Bonus Expense",
            CreditAccount = "2300 - Bonus Payable",
            Amount = batch.TotalAmount, Currency = bonusApprovalCurrency,
            EntryDate = today, Period = batch.PaymentPeriod,
            Description = $"Bonus approval: {batch.BatchName} ({batch.BatchNumber})",
            PostedBy = uid, PostedByName = GetUserName(),
        });

        await _db.SaveChangesAsync(ct);
        await WriteBonusAudit(tid, uid, id, null, "BatchApproved", "PendingApproval",
            JsonSerializer.Serialize(new { Status = "Approved", Comments = req.Comments, batch.TotalAmount }), ct);
        // SAFE-SERIALIZATION: BonusBatch is a workflow aggregate (total amounts, batch metadata) — no per-employee PII.
        return Ok(batch);
    }

    [HttpPatch("batches/{id:guid}/reject")]
    [Authorize(Roles = "Admin,Finance")]
    public async Task<IActionResult> RejectBatch(Guid id, [FromBody] RejectRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        var batch = await _db.BonusBatches.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (batch == null) return NotFound();
        batch.Status = "Cancelled"; batch.UpdatedAtUtc = DateTime.UtcNow; batch.UpdatedBy = uid;
        await _db.SaveChangesAsync(ct);
        await WriteBonusAudit(tid, uid, id, null, "BatchRejected", "PendingApproval",
            $"Cancelled: {req.Reason}", ct);
        // SAFE-SERIALIZATION: BonusBatch is a workflow aggregate (total amounts, batch metadata) — no per-employee PII.
        return Ok(batch);
    }

    [HttpPatch("batches/{id:guid}/mark-paid")]
    [Authorize(Roles = "Admin,Finance")]
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

        batch.Status = "Paid"; batch.IsLockedByPayroll = true;
        batch.UpdatedAtUtc = DateTime.UtcNow; batch.UpdatedBy = uid;

        // Only mark bonuses that are still "Approved" — already-consumed bonuses (PaidInPayroll
        // via a payroll run) must not have their Status or PayrollRunId clobbered. This makes
        // MarkBatchPaid safe to call on a partially-consumed batch.
        var unpaidBonuses = await _db.EmployeeBonuses
            .Where(x => x.BonusBatchId == id && !x.IsDeleted && x.Status == "Approved")
            .ToListAsync(ct);
        foreach (var b in unpaidBonuses)
        {
            b.Status = "PaidInPayroll";
            if (req.PayrollRunId.HasValue) b.PayrollRunId = req.PayrollRunId;
        }

        // GL amount covers only the remaining (previously-unpaid) bonuses so that the
        // payroll-consumed portion is not double-counted in the bonus GL.
        var remainingGlAmount = unpaidBonuses.Sum(b => b.BonusAmount);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var bonusPayCurrency = await _db.ResolveTenantCurrencyAsync(tid, ct);
        if (remainingGlAmount > 0)
            _db.FinanceGlEntries.Add(new FinanceGlEntry
            {
                TenantId = tid, SourceModule = "Bonus", SourceEntityId = id,
                SourceEntityRef = batch.BatchNumber, EventType = "BonusPayment",
                DebitAccount = "2300 - Bonus Payable",
                CreditAccount = "1000 - Cash/Bank",
                Amount = remainingGlAmount, Currency = bonusPayCurrency,
                EntryDate = today, Period = batch.PaymentPeriod,
                Description = $"Bonus payment: {batch.BatchName} ({batch.BatchNumber})",
                PostedBy = uid, PostedByName = GetUserName(),
            });

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
            ByDepartment = bonusSummary,
            Batches = batches.Select(b => new
            {
                b.BatchNumber, b.BatchName, b.BonusTypeName,
                b.PaymentPeriod, b.PaymentDate, b.EmployeeCount,
                b.TotalAmount, b.Status, b.IsLockedByPayroll,
            }).ToList(),
        });
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
