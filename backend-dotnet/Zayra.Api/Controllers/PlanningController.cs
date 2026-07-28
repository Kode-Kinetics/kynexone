using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;

namespace Zayra.Api.Controllers;

/// <summary>
/// Headcount & budget planning ("establishment"): each department carries an approved headcount and
/// monthly budget; current headcount is computed LIVE from employed staff so vacancies and
/// resignations reflect immediately. Also powers the budget-aware requisition check.
/// Occupancy everywhere in this controller uses the shared <see cref="EstablishmentOccupancy"/>
/// predicate so these numbers are always identical to the enforcement guard's (Conflict R3).
/// Authorization is permission-first ([Authorize] + per-action claim checks — no role list, so
/// custom roles and HR Director holding the claims are not 403'd before the claim check runs).
/// </summary>
[ApiController]
[Route("api/planning")]
[Authorize]
public class PlanningController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IAuditService _audit;
    private readonly IEstablishmentGuard? _establishmentGuard;

    public PlanningController(ZayraDbContext db, IAuditService? audit = null, IEstablishmentGuard? establishmentGuard = null)
    {
        _db = db;
        // Optional with concrete fallback (house pattern — OffboardingController): DI supplies both
        // in production; direct constructions in tests keep compiling.
        _audit = audit ?? new Zayra.Api.Infrastructure.Audit.AuditService(db);
        _establishmentGuard = establishmentGuard;
    }

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

        // Pull employed staff and open requisitions once, then aggregate in memory (cheap, avoids N
        // queries). Shared predicate note: adopting EstablishmentOccupancy here (a) added the
        // explicit !IsDeleted this endpoint's inline Where previously relied on the global filter
        // for (behaviour-preserving), and (b) deliberately counts Suspended — a disciplinary hold
        // is a continuing employment relationship and still occupies the seat (see predicate doc).
        var emps = await EstablishmentOccupancy.Occupying(_db.Employees.AsNoTracking(), tenantId)
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
            bool Match(Guid? did, string? dname) => EstablishmentOccupancy.MatchesDepartment(did, dname, d.Id, d.NameEn);
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

    /// <summary>
    /// Coarse establishment envelope (total approved headcount + monthly budget). Re-gated onto
    /// the dedicated establishment permission (spec §7): the person who hits the budget wall must
    /// not automatically be the person who can move the wall. Company-scope validated and audited
    /// (this PATCH previously wrote no audit at all — fixed regardless).
    /// </summary>
    [HttpPatch("departments/{id:guid}/establishment")]
    public async Task<IActionResult> SetEstablishment(Guid id, [FromBody] EstablishmentUpdate body, CancellationToken ct)
    {
        if (!HasPermission("organization.establishment.write")) return Forbid();
        var tenantId = this.GetTenantId()!.Value;
        var dept = await _db.Departments.FirstOrDefaultAsync(d => d.Id == id && d.TenantId == tenantId, ct);
        if (dept is null) return NotFound(new { message = "Department not found." });

        // Company-scope validation (group tenants: each company is a separate legal employer —
        // a company-scoped admin must not set another company's establishment). Departments reach
        // a company via Branch; an unbranched department is tenant-wide ⇒ group scope required.
        var scope = this.GetEntityScope();
        if (!scope.IsGroupLevel)
        {
            var companyId = dept.BranchId is null
                ? null
                : await _db.Branches.AsNoTracking()
                    .Where(b => b.TenantId == tenantId && b.Id == dept.BranchId)
                    .Select(b => (Guid?)b.CompanyId)
                    .FirstOrDefaultAsync(ct);
            if (!scope.CanAccessCompany(companyId)) return Forbid();
        }

        var before = new { dept.ApprovedHeadcount, dept.MonthlyBudgetAmount, dept.CostCenterId };
        var newApproved = Math.Max(0, body.ApprovedHeadcount);
        var newBudget = Math.Max(0, body.MonthlyBudgetAmount);
        var changedEnvelope = newApproved != dept.ApprovedHeadcount || newBudget != dept.MonthlyBudgetAmount;
        if (changedEnvelope && string.IsNullOrWhiteSpace(body.Reason))
            return BadRequest(new { message = "A reason is required when changing the approved headcount or monthly budget." });

        dept.ApprovedHeadcount = newApproved;
        dept.MonthlyBudgetAmount = newBudget;
        if (body.CostCenterId.HasValue)
            dept.CostCenterId = body.CostCenterId.Value == Guid.Empty ? null : body.CostCenterId.Value;
        dept.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("establishment.budget_updated", "Department", dept.Id.ToString(), Context(),
            JsonSerializer.Serialize(new
            {
                scope = "envelope",
                departmentId = dept.Id,
                departmentCode = dept.Code,
                departmentName = dept.NameEn,
                reason = body.Reason,
                before,
                after = new { dept.ApprovedHeadcount, dept.MonthlyBudgetAmount, dept.CostCenterId }
            }), ct);
        return Ok(new { dept.Id, dept.ApprovedHeadcount, dept.MonthlyBudgetAmount, dept.CostCenterId });
    }

    /// <summary>Pre-flight for raising a requisition (and, with <paramref name="designationId"/>,
    /// a pre-submit advisory for the establishment matrix): does it fit the approved headcount /
    /// the per-level budget? ADVISORY ONLY — the authoritative answer is the 409 at apply time.</summary>
    [HttpGet("headcount-check")]
    public async Task<IActionResult> HeadcountCheck([FromQuery] Guid? departmentId, [FromQuery] string? departmentName, [FromQuery] int headCount, [FromQuery] Guid? designationId, CancellationToken ct)
    {
        if (!HasAnyPermission("organization.read", "organization.write", "recruitment.write")) return Forbid();
        var tenantId = this.GetTenantId()!.Value;
        if (headCount <= 0)
            return BadRequest(new { message = "Requested headcount must be greater than zero." });
        var dept = await _db.Departments.AsNoTracking().FirstOrDefaultAsync(d =>
            d.TenantId == tenantId && !d.IsDeleted &&
            (departmentId != null ? d.Id == departmentId : d.NameEn == departmentName), ct);

        EstablishmentLevelCheck? levelCheck = null;
        if (designationId is not null && _establishmentGuard is not null)
        {
            var verdict = await _establishmentGuard.CheckAsync(tenantId, dept?.Id, designationId, null, headCount, ct);
            levelCheck = new EstablishmentLevelCheck(
                verdict.Allowed || verdict.Advisory,
                verdict.Unclassified,
                verdict.Block?.StaffingLevelId, verdict.Block?.LevelCode, verdict.Block?.LevelNameEn, verdict.Block?.LevelNameAr,
                verdict.Block?.Budgeted, verdict.Block?.Current, verdict.Block?.ExitingIncumbents);
        }

        if (dept is null)
            return Ok(new HeadcountCheckResult(false, 0, 0, 0, headCount, 0, true, "No establishment set for this department — no budget limit enforced.", levelCheck));

        var current = await EstablishmentOccupancy.Occupying(_db.Employees.AsNoTracking(), tenantId)
            .CountAsync(e => e.DepartmentId == dept.Id || (e.DepartmentId == null && e.Department == dept.NameEn), ct);
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
        return Ok(new HeadcountCheckResult(true, dept.ApprovedHeadcount, current, openReq, headCount, projected, within, msg, levelCheck));
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
        var emps = await EstablishmentOccupancy.Occupying(_db.Employees.AsNoTracking(), tenantId)
            .Select(e => new { e.DepartmentId, e.Department, e.Salary })
            .ToListAsync(ct);
        var reqs = await _db.ManpowerRequisitions.AsNoTracking()
            .Where(r => r.TenantId == tenantId && OpenReqStatuses.Contains(r.Status))
            .Select(r => new { r.DepartmentId, r.DepartmentName, r.HeadCount })
            .ToListAsync(ct);

        return depts.Select(d =>
        {
            bool Match(Guid? did, string? dname) => EstablishmentOccupancy.MatchesDepartment(did, dname, d.Id, d.NameEn);
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

    private RequestContext Context() => new(
        HttpContext.Connection.RemoteIpAddress?.ToString(),
        Request.Headers.UserAgent.ToString(),
        this.GetUserId(),
        this.GetTenantId());

    private bool HasPermission(string permission) =>
        User.Claims.Any(c => c.Type == "permission" && string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));

    private bool HasAnyPermission(params string[] permissions) => permissions.Any(HasPermission);
}

public record EstablishmentRow(
    Guid DepartmentId, string DepartmentName, Guid? CostCenterId, string CostCenterName,
    int ApprovedHeadcount, int CurrentHeadcount, int Gap, int OpenRequisitionHeadcount,
    decimal MonthlyBudgetAmount, decimal CurrentMonthlySpend);

public record EstablishmentUpdate(int ApprovedHeadcount, decimal MonthlyBudgetAmount, Guid? CostCenterId, string? Reason = null);

/// <summary>Advisory per-level verdict merged into the headcount pre-check when a designation is
/// supplied. Null numeric fields = the level is uncontrolled (no budget row) or unclassified.</summary>
public record EstablishmentLevelCheck(
    bool WithinLevelBudget, bool Unclassified,
    Guid? StaffingLevelId, string? LevelCode, string? LevelNameEn, string? LevelNameAr,
    int? Budgeted, int? Current, int? ExitingIncumbents);

public record HeadcountCheckResult(
    bool HasEstablishment, int ApprovedHeadcount, int CurrentHeadcount, int OpenRequisitionHeadcount,
    int Requested, int Projected, bool WithinBudget, string Message, EstablishmentLevelCheck? LevelCheck = null);
