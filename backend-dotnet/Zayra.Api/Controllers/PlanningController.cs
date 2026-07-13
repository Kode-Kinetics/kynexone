using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;

namespace Zayra.Api.Controllers;

/// <summary>
/// Headcount & budget planning ("establishment"): each department carries an approved headcount and
/// monthly budget; current headcount is computed LIVE from active employees so vacancies and
/// resignations reflect immediately. Also powers the budget-aware requisition check.
/// </summary>
[ApiController]
[Route("api/planning")]
[Authorize(Roles = "Admin,HR Manager,HR Officer,Manager")]
public class PlanningController : ControllerBase
{
    private readonly ZayraDbContext _db;
    public PlanningController(ZayraDbContext db) => _db = db;

    // Requisitions still "consuming" budget (not yet filled/closed).
    private static readonly string[] OpenReqStatuses = { "Draft", "Submitted", "PendingApproval", "Approved" };

    [HttpGet("establishment")]
    public async Task<IActionResult> Establishment(CancellationToken ct)
    {
        if (!HasAnyPermission("organization.read", "organization.write", "reports.read")) return Forbid();
        var tenantId = this.GetTenantId()!.Value;

        var scope = this.GetEntityScope();

        var depts = await _db.Departments.AsNoTracking()
            .Where(d => d.TenantId == tenantId && !d.IsDeleted && d.IsActive)
            .OrderBy(d => d.NameEn).ToListAsync(ct);

        var costCenters = await _db.CostCenters.AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        // Pull employed staff and open requisitions once, then aggregate in memory (cheap, avoids N queries).
        // "Offboarded" employees are serving notice — still employed, so they count toward current headcount.
        var emps = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && (e.Status == "Active" || e.Status == "Offboarded"))
            .Select(e => new { e.DepartmentId, e.Department, e.Salary })
            .ToListAsync(ct);
        if (!scope.IsGroupLevel)
            emps = emps.Where(e => e.DepartmentId == null || depts.Any(d => d.Id == e.DepartmentId)).ToList();
        var reqs = await _db.ManpowerRequisitions.AsNoTracking()
            .Where(r => r.TenantId == tenantId && OpenReqStatuses.Contains(r.Status))
            .Select(r => new { r.DepartmentId, r.DepartmentName, r.HeadCount })
            .ToListAsync(ct);

        var rows = depts.Select(d =>
        {
            bool Match(Guid? did, string? dname) => did == d.Id || (did == null && dname == d.NameEn);
            var deptEmps = emps.Where(e => Match(e.DepartmentId, e.Department)).ToList();
            var current = deptEmps.Count;
            var spend = deptEmps.Sum(e => e.Salary ?? 0m);
            var openReq = reqs.Where(r => Match(r.DepartmentId, r.DepartmentName)).Sum(r => r.HeadCount);
            return new EstablishmentRow(
                d.Id, d.NameEn,
                d.CostCenterId,
                d.CostCenterId is { } cc && costCenters.TryGetValue(cc, out var cn) ? cn : "",
                d.ApprovedHeadcount, current,
                d.ApprovedHeadcount > 0 ? d.ApprovedHeadcount - current : 0,
                openReq, d.MonthlyBudgetAmount, spend);
        }).ToList();
        return Ok(rows);
    }

    [HttpGet("workforce-summary")]
    public async Task<IActionResult> WorkforceSummary(CancellationToken ct)
    {
        if (!HasAnyPermission("organization.read", "organization.write", "reports.read")) return Forbid();
        var establishment = await BuildEstablishmentRows(ct);
        var totalApproved = establishment.Sum(x => x.ApprovedHeadcount);
        var totalCurrent = establishment.Sum(x => x.CurrentHeadcount);
        var totalOpenReq = establishment.Sum(x => x.OpenRequisitionHeadcount);
        var totalBudget = establishment.Sum(x => x.MonthlyBudgetAmount);
        var totalSpend = establishment.Sum(x => x.CurrentMonthlySpend);
        return Ok(new
        {
            totalApprovedHeadcount = totalApproved,
            totalCurrentHeadcount = totalCurrent,
            totalOpenRequisitionHeadcount = totalOpenReq,
            totalVacancy = establishment.Sum(x => Math.Max(0, x.Gap)),
            totalProjectedHeadcount = totalCurrent + totalOpenReq,
            monthlyBudgetAmount = totalBudget,
            currentMonthlySpend = totalSpend,
            budgetVariance = totalBudget - totalSpend,
            overBudgetDepartments = establishment.Count(x => x.MonthlyBudgetAmount > 0 && x.CurrentMonthlySpend > x.MonthlyBudgetAmount),
            generatedAt = DateTime.UtcNow
        });
    }

    [HttpPatch("departments/{id:guid}/establishment")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> SetEstablishment(Guid id, [FromBody] EstablishmentUpdate body, CancellationToken ct)
    {
        if (!HasPermission("organization.write")) return Forbid();
        var tenantId = this.GetTenantId()!.Value;
        var dept = await _db.Departments.FirstOrDefaultAsync(d => d.Id == id && d.TenantId == tenantId, ct);
        if (dept is null) return NotFound(new { message = "Department not found." });
        dept.ApprovedHeadcount = Math.Max(0, body.ApprovedHeadcount);
        dept.MonthlyBudgetAmount = Math.Max(0, body.MonthlyBudgetAmount);
        if (body.CostCenterId.HasValue)
            dept.CostCenterId = body.CostCenterId.Value == Guid.Empty ? null : body.CostCenterId.Value;
        dept.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { dept.Id, dept.ApprovedHeadcount, dept.MonthlyBudgetAmount, dept.CostCenterId });
    }

    /// <summary>Pre-flight for raising a requisition: does it fit the department's approved headcount?</summary>
    [HttpGet("headcount-check")]
    public async Task<IActionResult> HeadcountCheck([FromQuery] Guid? departmentId, [FromQuery] string? departmentName, [FromQuery] int headCount, CancellationToken ct)
    {
        if (!HasAnyPermission("organization.read", "organization.write", "recruitment.write")) return Forbid();
        var tenantId = this.GetTenantId()!.Value;
        if (headCount <= 0)
            return BadRequest(new { message = "Requested headcount must be greater than zero." });
        var dept = await _db.Departments.AsNoTracking().FirstOrDefaultAsync(d =>
            d.TenantId == tenantId && !d.IsDeleted &&
            (departmentId != null ? d.Id == departmentId : d.NameEn == departmentName), ct);

        if (dept is null)
            return Ok(new HeadcountCheckResult(false, 0, 0, 0, headCount, 0, true, "No establishment set for this department — no budget limit enforced."));

        var current = await _db.Employees.CountAsync(e =>
            e.TenantId == tenantId && !e.IsDeleted && (e.Status == "Active" || e.Status == "Offboarded") &&
            (e.DepartmentId == dept.Id || (e.DepartmentId == null && e.Department == dept.NameEn)), ct);
        var openReq = await _db.ManpowerRequisitions
            .Where(r => r.TenantId == tenantId && OpenReqStatuses.Contains(r.Status) &&
                        (r.DepartmentId == dept.Id || r.DepartmentName == dept.NameEn))
            .SumAsync(r => (int?)r.HeadCount, ct) ?? 0;

        var approved = dept.ApprovedHeadcount;
        var projected = current + openReq + headCount;
        var within = approved <= 0 || projected <= approved;
        var msg = approved <= 0
            ? "No approved headcount set for this department — not enforced."
            : within
                ? $"Within budget: {current} filled + {openReq} in pipeline + {headCount} requested = {projected} of {approved} approved."
                : $"Over budget: {projected} would exceed the approved {approved} (filled {current} + pipeline {openReq} + requested {headCount}).";
        return Ok(new HeadcountCheckResult(true, dept.ApprovedHeadcount, current, openReq, headCount, projected, within, msg));
    }

    private async Task<List<EstablishmentRow>> BuildEstablishmentRows(CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var depts = await _db.Departments.AsNoTracking()
            .Where(d => d.TenantId == tenantId && !d.IsDeleted && d.IsActive)
            .OrderBy(d => d.NameEn).ToListAsync(ct);
        var costCenters = await _db.CostCenters.AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var emps = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && (e.Status == "Active" || e.Status == "Offboarded"))
            .Select(e => new { e.DepartmentId, e.Department, e.Salary })
            .ToListAsync(ct);
        var reqs = await _db.ManpowerRequisitions.AsNoTracking()
            .Where(r => r.TenantId == tenantId && OpenReqStatuses.Contains(r.Status))
            .Select(r => new { r.DepartmentId, r.DepartmentName, r.HeadCount })
            .ToListAsync(ct);

        return depts.Select(d =>
        {
            bool Match(Guid? did, string? dname) => did == d.Id || (did == null && dname == d.NameEn);
            var deptEmps = emps.Where(e => Match(e.DepartmentId, e.Department)).ToList();
            var current = deptEmps.Count;
            var spend = deptEmps.Sum(e => e.Salary ?? 0m);
            var openReq = reqs.Where(r => Match(r.DepartmentId, r.DepartmentName)).Sum(r => r.HeadCount);
            return new EstablishmentRow(
                d.Id, d.NameEn,
                d.CostCenterId,
                d.CostCenterId is { } cc && costCenters.TryGetValue(cc, out var cn) ? cn : "",
                d.ApprovedHeadcount, current,
                d.ApprovedHeadcount > 0 ? d.ApprovedHeadcount - current : 0,
                openReq, d.MonthlyBudgetAmount, spend);
        }).ToList();
    }

    private bool HasPermission(string permission) =>
        User.Claims.Any(c => c.Type == "permission" && string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));

    private bool HasAnyPermission(params string[] permissions) => permissions.Any(HasPermission);
}

public record EstablishmentRow(
    Guid DepartmentId, string DepartmentName, Guid? CostCenterId, string CostCenterName,
    int ApprovedHeadcount, int CurrentHeadcount, int Gap, int OpenRequisitionHeadcount,
    decimal MonthlyBudgetAmount, decimal CurrentMonthlySpend);

public record EstablishmentUpdate(int ApprovedHeadcount, decimal MonthlyBudgetAmount, Guid? CostCenterId);

public record HeadcountCheckResult(
    bool HasEstablishment, int ApprovedHeadcount, int CurrentHeadcount, int OpenRequisitionHeadcount,
    int Requested, int Projected, bool WithinBudget, string Message);
