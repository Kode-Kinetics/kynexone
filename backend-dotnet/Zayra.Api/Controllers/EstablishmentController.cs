using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

/// <summary>
/// Establishment matrix API: tenant-configurable staffing-level catalog, per-department per-level
/// budgeted headcount, and the designation→level mapping workflow (suggest → preview impact →
/// approve). Authorization is PERMISSION-FIRST: [Authorize] + claim checks only — no role lists,
/// so HR Director and custom roles holding organization.establishment.write are never 403'd by a
/// role gate (permission registry pattern; PositionsController's role strings are not the model).
/// Reads share <see cref="EstablishmentOccupancy"/> with the guard, so the numbers in this panel
/// are exactly the numbers the 409 popup reports.
/// </summary>
[ApiController]
[Route("api/establishment")]
[Authorize]
public class EstablishmentController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IAuditService _audit;
    private readonly IEstablishmentGuard _guard;

    public EstablishmentController(ZayraDbContext db, IAuditService audit, IEstablishmentGuard guard)
    {
        _db = db;
        _audit = audit;
        _guard = guard;
    }

    private const string WritePermission = EstablishmentHttp.WritePermission;

    // ── Staffing-level catalog ────────────────────────────────────────────────

    /// <summary>Active + inactive levels ordered by rank. Lazy-seeds the editable defaults for
    /// tenants provisioned before the matrix existed (idempotent; respects deliberate deletion;
    /// tolerates a concurrent first visit).</summary>
    [HttpGet("levels")]
    public async Task<IActionResult> Levels([FromServices] EstablishmentSeeder seeder, CancellationToken ct)
    {
        if (!HasAnyPermission("organization.read", "organization.write", "reports.read")) return Forbid();
        var tenantId = this.GetTenantId()!.Value;
        await seeder.EnsureStaffingLevelsAsync(tenantId, ct);
        var levels = await _db.StaffingLevels.AsNoTracking()
            .Where(l => l.TenantId == tenantId && !l.IsDeleted)
            .OrderBy(l => l.Rank).ThenBy(l => l.NameEn)
            .Select(l => new StaffingLevelDto(l.Id, l.Code, l.NameEn, l.NameAr, l.Rank, l.IsActive))
            .ToListAsync(ct);
        return Ok(levels);
    }

    [HttpPost("levels")]
    public async Task<IActionResult> CreateLevel([FromBody] StaffingLevelRequest body, CancellationToken ct)
    {
        if (!HasPermission(WritePermission)) return Forbid();
        var tenantId = this.GetTenantId()!.Value;
        var code = (body.Code ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(body.NameEn))
            return BadRequest(new { message = "Code and NameEn are required." });
        // The unique (TenantId, Code) DB index includes soft-deleted rows (house convention) —
        // check both and answer legibly instead of surfacing a raw constraint violation.
        // IgnoreQueryFilters is intentional: establishment reference/occupancy lookups must be absolute (caller-scope independent, incl. soft-deleted code clashes); explicit TenantId filters are applied inline.
        var clash = await _db.StaffingLevels.IgnoreQueryFilters()
            .FirstOrDefaultAsync(l => l.TenantId == tenantId && l.Code == code, ct);
        if (clash is not null)
            return Conflict(new
            {
                message = clash.IsDeleted
                    ? $"A deleted staffing level with code '{code}' still exists; choose a different code (deleted level codes are retained for audit history)."
                    : $"A staffing level with code '{code}' already exists."
            });
        var level = new StaffingLevel
        {
            TenantId = tenantId,
            Code = code,
            NameEn = body.NameEn.Trim(),
            NameAr = (body.NameAr ?? string.Empty).Trim(),
            Rank = body.Rank,
            IsActive = body.IsActive ?? true,
            CreatedBy = this.GetUserId()
        };
        _db.StaffingLevels.Add(level);
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("establishment.level_created", "StaffingLevel", level.Id.ToString(), Context(),
            JsonSerializer.Serialize(new { level.Code, level.NameEn, level.NameAr, level.Rank, level.IsActive }), ct);
        return Created($"/api/establishment/levels/{level.Id}", new StaffingLevelDto(level.Id, level.Code, level.NameEn, level.NameAr, level.Rank, level.IsActive));
    }

    [HttpPut("levels/{id:guid}")]
    public async Task<IActionResult> UpdateLevel(Guid id, [FromBody] StaffingLevelRequest body, CancellationToken ct)
    {
        if (!HasPermission(WritePermission)) return Forbid();
        var tenantId = this.GetTenantId()!.Value;
        var level = await _db.StaffingLevels.FirstOrDefaultAsync(l => l.TenantId == tenantId && l.Id == id && !l.IsDeleted, ct);
        if (level is null) return NotFound();
        var code = (body.Code ?? level.Code).Trim().ToUpperInvariant();
        // IgnoreQueryFilters is intentional: establishment reference/occupancy lookups must be absolute (caller-scope independent, incl. soft-deleted code clashes); explicit TenantId filters are applied inline.
        if (code != level.Code && await _db.StaffingLevels.IgnoreQueryFilters().AnyAsync(l => l.TenantId == tenantId && l.Code == code, ct))
            return Conflict(new { message = $"A staffing level with code '{code}' already exists." });
        var before = new { level.Code, level.NameEn, level.NameAr, level.Rank, level.IsActive };
        level.Code = code;
        level.NameEn = string.IsNullOrWhiteSpace(body.NameEn) ? level.NameEn : body.NameEn.Trim();
        level.NameAr = body.NameAr?.Trim() ?? level.NameAr;
        level.Rank = body.Rank;
        if (body.IsActive.HasValue) level.IsActive = body.IsActive.Value;
        level.UpdatedAtUtc = DateTime.UtcNow;
        level.UpdatedBy = this.GetUserId();
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("establishment.level_updated", "StaffingLevel", level.Id.ToString(), Context(),
            JsonSerializer.Serialize(new { before, after = new { level.Code, level.NameEn, level.NameAr, level.Rank, level.IsActive } }), ct);
        return Ok(new StaffingLevelDto(level.Id, level.Code, level.NameEn, level.NameAr, level.Rank, level.IsActive));
    }

    /// <summary>Deactivation is always allowed; the response lists what still references the level
    /// so the admin sees the consequences (referencing budgets stay enforced; designations stay
    /// mapped). Hard delete is the gated one below.</summary>
    [HttpPost("levels/{id:guid}/deactivate")]
    public async Task<IActionResult> DeactivateLevel(Guid id, CancellationToken ct)
    {
        if (!HasPermission(WritePermission)) return Forbid();
        var tenantId = this.GetTenantId()!.Value;
        var level = await _db.StaffingLevels.FirstOrDefaultAsync(l => l.TenantId == tenantId && l.Id == id && !l.IsDeleted, ct);
        if (level is null) return NotFound();
        var (designationRefs, budgetRefs) = await CountReferencesAsync(tenantId, id, ct);
        level.IsActive = false;
        level.UpdatedAtUtc = DateTime.UtcNow;
        level.UpdatedBy = this.GetUserId();
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("establishment.level_deactivated", "StaffingLevel", level.Id.ToString(), Context(),
            JsonSerializer.Serialize(new { level.Code, level.NameEn, designationRefs, budgetRefs }), ct);
        return Ok(new
        {
            level.Id,
            level.IsActive,
            warning = designationRefs + budgetRefs == 0 ? null :
                $"{designationRefs} designation(s) and {budgetRefs} budget row(s) still reference this level; they remain in force."
        });
    }

    [HttpDelete("levels/{id:guid}")]
    public async Task<IActionResult> DeleteLevel(Guid id, CancellationToken ct)
    {
        if (!HasPermission(WritePermission)) return Forbid();
        var tenantId = this.GetTenantId()!.Value;
        var level = await _db.StaffingLevels.FirstOrDefaultAsync(l => l.TenantId == tenantId && l.Id == id && !l.IsDeleted, ct);
        if (level is null) return NotFound();
        var (designationRefs, budgetRefs) = await CountReferencesAsync(tenantId, id, ct);
        if (designationRefs + budgetRefs > 0)
            return Conflict(new
            {
                message = $"Staffing level '{level.NameEn}' is referenced by {designationRefs} designation(s) and {budgetRefs} budget row(s). Re-map the designations and clear the budget rows first, or deactivate the level instead.",
                designationRefs,
                budgetRefs
            });
        level.IsDeleted = true;
        level.DeletedAtUtc = DateTime.UtcNow;
        level.DeletedBy = this.GetUserId();
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("establishment.level_deleted", "StaffingLevel", level.Id.ToString(), Context(),
            JsonSerializer.Serialize(new { level.Code, level.NameEn }), ct);
        return NoContent();
    }

    // ── Matrix read ───────────────────────────────────────────────────────────

    /// <summary>Panel payload: superset of the planning establishment rows plus the per-level
    /// breakdown. budgeted=null means UNCONTROLLED ("—"); 0 means FROZEN — the UI must render the
    /// two distinctly. managerEmployeeId is display-only reconciliation (never enforcement).</summary>
    [HttpGet("matrix")]
    public async Task<IActionResult> Matrix(CancellationToken ct)
    {
        if (!HasAnyPermission("organization.read", "organization.write", "reports.read")) return Forbid();
        var tenantId = this.GetTenantId()!.Value;
        var scope = this.GetEntityScope();

        var depts = await _db.Departments.AsNoTracking()
            .Where(d => d.TenantId == tenantId && !d.IsDeleted && d.IsActive)
            .OrderBy(d => d.NameEn).ToListAsync(ct);
        var costCenters = await _db.CostCenters.AsNoTracking()
            .Where(c => c.TenantId == tenantId).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var levels = await _db.StaffingLevels.AsNoTracking()
            .Where(l => l.TenantId == tenantId && !l.IsDeleted && l.IsActive)
            .OrderBy(l => l.Rank).ToListAsync(ct);
        var levelByDesignation = await _db.Designations.AsNoTracking()
            .Where(d => d.TenantId == tenantId && !d.IsDeleted && d.StaffingLevelId != null)
            .ToDictionaryAsync(d => d.Id, d => d.StaffingLevelId!.Value, ct);
        var budgets = await _db.DepartmentStaffingBudgets.AsNoTracking()
            .Where(b => b.TenantId == tenantId && !b.IsDeleted)
            .ToDictionaryAsync(b => (b.DepartmentId, b.StaffingLevelId), b => b.BudgetedHeadcount, ct);
        var emps = await EstablishmentOccupancy.Occupying(_db.Employees.AsNoTracking(), tenantId)
            .Select(e => new { e.DepartmentId, e.Department, e.DesignationId, e.Status, e.Salary })
            .ToListAsync(ct);
        if (!scope.IsGroupLevel)
            emps = emps.Where(e => e.DepartmentId == null || depts.Any(d => d.Id == e.DepartmentId)).ToList();
        var reqs = await _db.ManpowerRequisitions.AsNoTracking()
            .Where(r => r.TenantId == tenantId && PlanningOpenReqStatuses.Contains(r.Status))
            .Select(r => new { r.DepartmentId, r.DepartmentName, r.HeadCount })
            .ToListAsync(ct);
        var positions = await _db.Positions.AsNoTracking()
            .Where(p => p.TenantId == tenantId && !p.IsDeleted
                     && (p.Status == PositionStatuses.Open || p.Status == PositionStatuses.Filled)
                     && p.DepartmentId != null && p.DesignationId != null)
            .Select(p => new { p.DepartmentId, p.DesignationId })
            .ToListAsync(ct);

        var deptNames = depts.Select(d => d.NameEn).ToHashSet();
        var unresolvedDepartmentCount = emps.Count(e => e.DepartmentId == null
            && (string.IsNullOrWhiteSpace(e.Department) || !deptNames.Contains(e.Department)));

        var rows = depts.Select(d =>
        {
            bool Match(Guid? did, string? dname) => EstablishmentOccupancy.MatchesDepartment(did, dname, d.Id, d.NameEn);
            var deptEmps = emps.Where(e => Match(e.DepartmentId, e.Department)).ToList();
            var openReq = reqs.Where(r => Match(r.DepartmentId, r.DepartmentName)).Sum(r => r.HeadCount);
            var spend = deptEmps.Sum(e => e.Salary ?? 0m);
            var unclassified = deptEmps.Count(e => e.DesignationId == null || !levelByDesignation.ContainsKey(e.DesignationId.Value));

            var levelRows = levels.Select(l =>
            {
                var cellEmps = deptEmps.Where(e => e.DesignationId != null
                    && levelByDesignation.TryGetValue(e.DesignationId.Value, out var lv) && lv == l.Id).ToList();
                int? budgeted = budgets.TryGetValue((d.Id, l.Id), out var b) ? b : null;
                var positionsAtLevel = positions.Count(p => p.DepartmentId == d.Id
                    && levelByDesignation.TryGetValue(p.DesignationId!.Value, out var plv) && plv == l.Id);
                return new
                {
                    staffingLevelId = l.Id,
                    levelCode = l.Code,
                    levelNameEn = l.NameEn,
                    levelNameAr = l.NameAr,
                    rank = l.Rank,
                    budgeted,
                    current = cellEmps.Count,
                    gap = budgeted is null ? (int?)null : budgeted.Value - cellEmps.Count,
                    exitingIncumbents = cellEmps.Count(e => e.Status == EmployeeStatuses.Offboarded),
                    positionsAtLevel,
                    // Reconciliation WARNING only (never a block): the fine-grained Position layer
                    // promises more seats at this level than the coarse budget sanctions.
                    positionsExceedBudget = budgeted is not null && positionsAtLevel > budgeted.Value
                };
            }).ToList();

            var allocated = levelRows.Where(r => r.budgeted is not null).Sum(r => r.budgeted!.Value);
            return new
            {
                departmentId = d.Id,
                departmentName = d.NameEn,
                costCenterId = d.CostCenterId,
                costCenterName = d.CostCenterId is { } cc && costCenters.TryGetValue(cc, out var cn) ? cn : "",
                approvedHeadcount = d.ApprovedHeadcount,
                currentHeadcount = deptEmps.Count,
                gap = d.ApprovedHeadcount > 0 ? d.ApprovedHeadcount - deptEmps.Count : 0,
                openRequisitionHeadcount = openReq,
                monthlyBudgetAmount = d.MonthlyBudgetAmount,
                currentMonthlySpend = spend,
                allocated,
                unallocated = Math.Max(0, d.ApprovedHeadcount - allocated),
                allocationExceedsEnvelope = d.ApprovedHeadcount > 0 && allocated > d.ApprovedHeadcount,
                managerEmployeeId = d.ManagerEmployeeId,
                levels = levelRows,
                unclassifiedCount = unclassified
            };
        }).ToList();

        return Ok(new
        {
            enforcementMode = await _guard.GetEnforcementModeAsync(tenantId, ct),
            unresolvedDepartmentCount,
            departments = rows
        });
    }

    private static readonly string[] PlanningOpenReqStatuses = { "Draft", "Submitted", "PendingApproval", "Approved" };

    // ── Budget write ──────────────────────────────────────────────────────────

    /// <summary>Upserts the per-level budget rows of one department. reason is MANDATORY.
    /// budgetedHeadcount null ⇒ the row is removed (level returns to uncontrolled — audited as
    /// such). Below-occupancy values and allocation&gt;envelope produce WARNINGS, never blocks
    /// (grandfathering: reducing a budget under current occupancy renders red, forces nothing).</summary>
    [HttpPut("departments/{departmentId:guid}/budgets")]
    public async Task<IActionResult> PutBudgets(Guid departmentId, [FromBody] DepartmentBudgetsRequest body, CancellationToken ct)
    {
        if (!HasPermission(WritePermission)) return Forbid();
        var tenantId = this.GetTenantId()!.Value;
        if (string.IsNullOrWhiteSpace(body.Reason))
            return BadRequest(new { message = "A reason is required for every staffing-budget change." });
        if (body.Rows is null || body.Rows.Count == 0)
            return BadRequest(new { message = "At least one budget row is required." });
        if (body.Rows.Any(r => r.BudgetedHeadcount is < 0))
            return BadRequest(new { message = "Budgeted headcount cannot be negative (empty = uncontrolled, 0 = frozen)." });

        var dept = await _db.Departments.FirstOrDefaultAsync(d => d.TenantId == tenantId && d.Id == departmentId && !d.IsDeleted, ct);
        if (dept is null) return NotFound(new { message = "Department not found." });

        // Company-scope validation (Conflict: group tenants — each company is its own legal
        // employer/Nitaqat file; a company-scoped admin edits only their companies' departments).
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

        var levelIds = body.Rows.Select(r => r.StaffingLevelId).Distinct().ToList();
        if (levelIds.Count != body.Rows.Count)
            return BadRequest(new { message = "Duplicate staffing level in request." });
        var levels = await _db.StaffingLevels.AsNoTracking()
            .Where(l => l.TenantId == tenantId && !l.IsDeleted && levelIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, ct);
        if (levels.Count != levelIds.Count)
            return UnprocessableEntity(new { message = "One or more staffing levels were not found." });

        var existing = await _db.DepartmentStaffingBudgets
            .Where(b => b.TenantId == tenantId && b.DepartmentId == departmentId && !b.IsDeleted)
            .ToListAsync(ct);

        var userId = this.GetUserId();
        var changes = new List<object>();
        var warnings = new List<string>();

        async Task<int> ApplyAsync()
        {
            foreach (var row in body.Rows)
            {
                var current = existing.FirstOrDefault(b => b.StaffingLevelId == row.StaffingLevelId);
                var before = current?.BudgetedHeadcount;
                if (row.BudgetedHeadcount is null)
                {
                    if (current is null) continue; // nothing to clear
                    current.IsDeleted = true;
                    current.DeletedAtUtc = DateTime.UtcNow;
                    current.DeletedBy = userId;
                }
                else if (current is null)
                {
                    _db.DepartmentStaffingBudgets.Add(new DepartmentStaffingBudget
                    {
                        TenantId = tenantId,
                        DepartmentId = departmentId,
                        StaffingLevelId = row.StaffingLevelId,
                        BudgetedHeadcount = row.BudgetedHeadcount.Value,
                        CreatedBy = userId
                    });
                }
                else
                {
                    if (current.BudgetedHeadcount == row.BudgetedHeadcount.Value) continue;
                    current.BudgetedHeadcount = row.BudgetedHeadcount.Value;
                    current.UpdatedAtUtc = DateTime.UtcNow;
                    current.UpdatedBy = userId;
                }
                var level = levels[row.StaffingLevelId];
                changes.Add(new
                {
                    staffingLevelId = row.StaffingLevelId,
                    levelCode = level.Code,
                    levelNameEn = level.NameEn,
                    levelNameAr = level.NameAr,
                    before,
                    after = row.BudgetedHeadcount
                });
            }
            return await _db.SaveChangesAsync(ct);
        }

        if (_db.Database.IsRelational())
        {
            // Same advisory locks as the hires: a budget edit racing a hire for the same cell
            // serializes with it (sorted keys — deadlock-free).
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                foreach (var levelId in levelIds.OrderBy(l => EstablishmentGuardService.ComputeLockKey(tenantId, departmentId, l)))
                    await _guard.AcquireSlotLockAsync(tenantId, departmentId, levelId, ct);
                await ApplyAsync();
                await tx.CommitAsync(ct);
                return true;
            });
        }
        else
        {
            await ApplyAsync();
        }

        // Post-save warnings (display state, never blocks) — absolute counts, guard-identical.
        // IgnoreQueryFilters is intentional: establishment reference/occupancy lookups must be absolute (caller-scope independent, incl. soft-deleted code clashes); explicit TenantId filters are applied inline.
        var levelByDesignation = await _db.Designations.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.TenantId == tenantId && !d.IsDeleted && d.StaffingLevelId != null)
            .ToDictionaryAsync(d => d.Id, d => d.StaffingLevelId!.Value, ct);
        var occupants = await EstablishmentOccupancy.Occupying(_db.Employees.IgnoreQueryFilters().AsNoTracking(), tenantId)
            .Where(e => e.DepartmentId == departmentId || (e.DepartmentId == null && e.Department == dept.NameEn))
            .Select(e => new { e.DesignationId })
            .ToListAsync(ct);
        foreach (var row in body.Rows.Where(r => r.BudgetedHeadcount is not null))
        {
            var occ = occupants.Count(e => e.DesignationId != null
                && levelByDesignation.TryGetValue(e.DesignationId.Value, out var lv) && lv == row.StaffingLevelId);
            if (occ > row.BudgetedHeadcount!.Value)
                warnings.Add($"{levels[row.StaffingLevelId].NameEn}: budget {row.BudgetedHeadcount} is below current occupancy {occ} — existing employees are never invalidated; the level shows over-establishment until attrition or a raise.");
        }
        var allBudgets = await _db.DepartmentStaffingBudgets.AsNoTracking()
            .Where(b => b.TenantId == tenantId && b.DepartmentId == departmentId && !b.IsDeleted)
            .SumAsync(b => (int?)b.BudgetedHeadcount, ct) ?? 0;
        if (dept.ApprovedHeadcount > 0 && allBudgets > dept.ApprovedHeadcount)
            warnings.Add($"Allocated {allBudgets} exceeds the department's approved headcount envelope of {dept.ApprovedHeadcount}.");

        if (changes.Count > 0)
            await _audit.WriteAsync("establishment.budget_updated", "Department", departmentId.ToString(), Context(),
                JsonSerializer.Serialize(new
                {
                    scope = "matrix",
                    departmentId,
                    departmentCode = dept.Code,
                    departmentName = dept.NameEn,
                    reason = body.Reason,
                    levels = changes
                }), ct);

        return Ok(new { departmentId, changed = changes.Count, allocated = allBudgets, approvedHeadcount = dept.ApprovedHeadcount, warnings });
    }

    // ── Designation → level mapping (suggest → impact preview → approved apply) ──

    /// <summary>One-time SUGGESTED mapping from structural heuristics only (JobLevel string equals
    /// a level's NameEn/Code; else LevelRank equals a level's Rank). Nothing is written — the admin
    /// approves via /level-mapping/apply (house opt-in rule). No band-name literals in code: the
    /// heuristics compare against the tenant's own (editable) level rows.</summary>
    [HttpGet("level-mapping/suggestions")]
    public async Task<IActionResult> MappingSuggestions(CancellationToken ct)
    {
        if (!HasAnyPermission("organization.read", "organization.write")) return Forbid();
        var tenantId = this.GetTenantId()!.Value;
        var levels = await _db.StaffingLevels.AsNoTracking()
            .Where(l => l.TenantId == tenantId && !l.IsDeleted && l.IsActive)
            .OrderBy(l => l.Rank).ToListAsync(ct);
        var unmapped = await _db.Designations.AsNoTracking()
            .Where(d => d.TenantId == tenantId && !d.IsDeleted && d.IsActive && d.StaffingLevelId == null)
            .ToListAsync(ct);
        var suggestions = unmapped.Select(d =>
        {
            var byJobLevel = string.IsNullOrWhiteSpace(d.JobLevel)
                ? null
                : levels.FirstOrDefault(l => string.Equals(l.NameEn, d.JobLevel, StringComparison.OrdinalIgnoreCase)
                                          || string.Equals(l.Code, d.JobLevel, StringComparison.OrdinalIgnoreCase));
            var byRank = byJobLevel is not null || d.LevelRank <= 0
                ? null
                : levels.Where(l => l.Rank == d.LevelRank)
                        .OrderBy(l => d.IsManagerRole ? l.Rank : -l.Rank)
                        .FirstOrDefault();
            var suggested = byJobLevel ?? byRank;
            return new
            {
                designationId = d.Id,
                titleEn = d.TitleEn,
                jobLevel = d.JobLevel,
                levelRank = d.LevelRank,
                isManagerRole = d.IsManagerRole,
                currentLevelId = (Guid?)null,
                suggestedLevelId = suggested?.Id,
                suggestedLevelName = suggested?.NameEn,
                basis = byJobLevel is not null ? "jobLevel" : byRank is not null ? "levelRank" : null
            };
        }).ToList();
        return Ok(suggestions);
    }

    /// <summary>Impact preview for re-mapping one designation: which departments' target-level
    /// budgets would go over ("this makes Sales +2 Managers over budget"). Read-only.</summary>
    [HttpGet("level-mapping/impact")]
    public async Task<IActionResult> MappingImpact([FromQuery] Guid designationId, [FromQuery] Guid? staffingLevelId, CancellationToken ct)
    {
        if (!HasAnyPermission("organization.read", "organization.write")) return Forbid();
        var tenantId = this.GetTenantId()!.Value;
        var impacts = await ComputeMappingImpactAsync(tenantId, designationId, staffingLevelId, ct);
        return impacts is null ? NotFound(new { message = "Designation not found." }) : Ok(impacts);
    }

    /// <summary>Applies ONLY the admin-approved pairs. Requires the establishment permission and a
    /// reason (this is the second override lever after budget edits). Re-maps are allowed with
    /// warning — grandfathering: an over-budget result renders red, nothing blocks retroactively.
    /// Each change is audited with before/after and impact counts.</summary>
    [HttpPost("level-mapping/apply")]
    public async Task<IActionResult> ApplyMapping([FromBody] LevelMappingApplyRequest body, CancellationToken ct)
    {
        if (!HasPermission(WritePermission)) return Forbid();
        // Mapping is tenant-wide data affecting every company's counts ⇒ group scope required.
        if (!this.GetEntityScope().IsGroupLevel) return Forbid();
        var tenantId = this.GetTenantId()!.Value;
        if (string.IsNullOrWhiteSpace(body.Reason))
            return BadRequest(new { message = "A reason is required for staffing-level mapping changes." });
        if (body.Mappings is null || body.Mappings.Count == 0)
            return BadRequest(new { message = "No mappings supplied." });

        var designationIds = body.Mappings.Select(m => m.DesignationId).Distinct().ToList();
        var designations = await _db.Designations
            .Where(d => d.TenantId == tenantId && !d.IsDeleted && designationIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, ct);
        var levelIds = body.Mappings.Where(m => m.StaffingLevelId is not null).Select(m => m.StaffingLevelId!.Value).Distinct().ToList();
        var levels = await _db.StaffingLevels.AsNoTracking()
            .Where(l => l.TenantId == tenantId && !l.IsDeleted && levelIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, ct);
        if (levels.Count != levelIds.Count)
            return UnprocessableEntity(new { message = "One or more staffing levels were not found." });

        var results = new List<object>();
        var userId = this.GetUserId();
        foreach (var mapping in body.Mappings)
        {
            if (!designations.TryGetValue(mapping.DesignationId, out var designation))
                return UnprocessableEntity(new { message = $"Designation '{mapping.DesignationId}' was not found." });
            var before = designation.StaffingLevelId;
            if (before == mapping.StaffingLevelId) continue;
            var impacts = await ComputeMappingImpactAsync(tenantId, mapping.DesignationId, mapping.StaffingLevelId, ct);
            designation.StaffingLevelId = mapping.StaffingLevelId;
            designation.UpdatedAtUtc = DateTime.UtcNow;
            designation.UpdatedBy = userId;
            var levelName = mapping.StaffingLevelId is { } lid && levels.TryGetValue(lid, out var lvl) ? lvl.NameEn : null;
            await _audit.WriteAsync("establishment.level_mapping_changed", "Designation", designation.Id.ToString(), Context(),
                JsonSerializer.Serialize(new
                {
                    designationId = designation.Id,
                    designationTitle = designation.TitleEn,
                    reason = body.Reason,
                    beforeLevelId = before,
                    afterLevelId = mapping.StaffingLevelId,
                    afterLevelName = levelName,
                    impacts
                }), ct);
            results.Add(new { designationId = designation.Id, beforeLevelId = before, afterLevelId = mapping.StaffingLevelId, impacts });
        }
        await _db.SaveChangesAsync(ct);
        return Ok(new { applied = results.Count, results });
    }

    // ── Legacy string-only department reconciliation (security review R1) ─────

    /// <summary>
    /// One-time (idempotent, re-runnable) backfill: employees with a NULL DepartmentId whose
    /// free-text Department exactly equals an active department's NameEn get the ID stamped.
    /// Shrinks the legacy name-fallback counting surface the same way the transfer fix does —
    /// closing the "rename the department, its string-matched occupants vanish from the counts"
    /// bypass. Ambiguous or unmatched strings are left alone and reported (never guessed).
    /// </summary>
    [HttpPost("reconcile-departments")]
    public async Task<IActionResult> ReconcileDepartments(CancellationToken ct)
    {
        if (!HasPermission(WritePermission)) return Forbid();
        // Touches employees tenant-wide ⇒ group scope required (same rule as level-mapping/apply).
        if (!this.GetEntityScope().IsGroupLevel) return Forbid();
        var tenantId = this.GetTenantId()!.Value;

        // IgnoreQueryFilters is intentional: reconciliation must see every employee of the tenant regardless of the caller's company scope; explicit TenantId + !IsDeleted filters are applied inline.
        var deptsByName = (await _db.Departments.IgnoreQueryFilters().AsNoTracking()
                .Where(d => d.TenantId == tenantId && !d.IsDeleted && d.IsActive)
                .Select(d => new { d.Id, d.NameEn })
                .ToListAsync(ct))
            .GroupBy(d => d.NameEn)
            .ToDictionary(g => g.Key, g => g.ToList());
        var candidates = await _db.Employees.IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && e.DepartmentId == null && e.Department != "")
            .ToListAsync(ct);

        var resolved = new List<object>();
        int ambiguous = 0, unmatched = 0;
        foreach (var employee in candidates)
        {
            if (!deptsByName.TryGetValue(employee.Department, out var matches)) { unmatched++; continue; }
            if (matches.Count != 1) { ambiguous++; continue; }
            employee.DepartmentId = matches[0].Id;
            employee.UpdatedAtUtc = DateTime.UtcNow;
            resolved.Add(new { employeeId = employee.Id, employee.EmployeeCode, departmentId = matches[0].Id, departmentName = matches[0].NameEn });
        }
        if (resolved.Count > 0) await _db.SaveChangesAsync(ct);

        if (resolved.Count > 0)
            await _audit.WriteAsync("establishment.departments_reconciled", "Employee", tenantId.ToString(), Context(),
                JsonSerializer.Serialize(new { resolvedCount = resolved.Count, ambiguous, unmatched, resolved }), ct);

        return Ok(new { resolvedCount = resolved.Count, ambiguous, unmatched });
    }

    private async Task<object?> ComputeMappingImpactAsync(Guid tenantId, Guid designationId, Guid? targetLevelId, CancellationToken ct)
    {
        var designation = await _db.Designations.AsNoTracking()
            .FirstOrDefaultAsync(d => d.TenantId == tenantId && d.Id == designationId && !d.IsDeleted, ct);
        if (designation is null) return null;
        if (targetLevelId is null) return new { overBudget = Array.Empty<object>() };

        // Departments whose TARGET-level budget row would be exceeded once this designation's
        // occupants count toward it.
        // IgnoreQueryFilters is intentional: establishment reference/occupancy lookups must be absolute (caller-scope independent, incl. soft-deleted code clashes); explicit TenantId filters are applied inline.
        var budgetRows = await _db.DepartmentStaffingBudgets.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.TenantId == tenantId && !b.IsDeleted && b.StaffingLevelId == targetLevelId)
            .ToListAsync(ct);
        if (budgetRows.Count == 0) return new { overBudget = Array.Empty<object>() };
        var deptIds = budgetRows.Select(b => b.DepartmentId).ToList();
        var deptById = await _db.Departments.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.TenantId == tenantId && deptIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, d => d.NameEn, ct);
        var targetLevelDesignations = await _db.Designations.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.TenantId == tenantId && !d.IsDeleted && d.StaffingLevelId == targetLevelId && d.Id != designationId)
            .Select(d => d.Id).ToListAsync(ct);
        // IgnoreQueryFilters is intentional: establishment reference/occupancy lookups must be absolute (caller-scope independent, incl. soft-deleted code clashes); explicit TenantId filters are applied inline.
        var occupants = await EstablishmentOccupancy.Occupying(_db.Employees.IgnoreQueryFilters().AsNoTracking(), tenantId)
            .Where(e => e.DesignationId != null
                && (e.DesignationId == designationId || targetLevelDesignations.Contains(e.DesignationId.Value)))
            .Select(e => new { e.DepartmentId, e.Department, e.DesignationId })
            .ToListAsync(ct);

        var overBudget = new List<object>();
        var level = await _db.StaffingLevels.AsNoTracking().FirstOrDefaultAsync(l => l.TenantId == tenantId && l.Id == targetLevelId, ct);
        foreach (var row in budgetRows)
        {
            if (!deptById.TryGetValue(row.DepartmentId, out var deptName)) continue;
            var projected = occupants.Count(e => EstablishmentOccupancy.MatchesDepartment(e.DepartmentId, e.Department, row.DepartmentId, deptName));
            if (projected > row.BudgetedHeadcount)
                overBudget.Add(new
                {
                    departmentId = row.DepartmentId,
                    departmentName = deptName,
                    levelNameEn = level?.NameEn,
                    levelNameAr = level?.NameAr,
                    budgeted = row.BudgetedHeadcount,
                    projectedCurrent = projected
                });
        }
        return new { overBudget };
    }

    private async Task<(int DesignationRefs, int BudgetRefs)> CountReferencesAsync(Guid tenantId, Guid levelId, CancellationToken ct)
    {
        // IgnoreQueryFilters is intentional: establishment reference/occupancy lookups must be absolute (caller-scope independent, incl. soft-deleted code clashes); explicit TenantId filters are applied inline.
        var designationRefs = await _db.Designations.IgnoreQueryFilters()
            .CountAsync(d => d.TenantId == tenantId && !d.IsDeleted && d.StaffingLevelId == levelId, ct);
        var budgetRefs = await _db.DepartmentStaffingBudgets.IgnoreQueryFilters()
            .CountAsync(b => b.TenantId == tenantId && !b.IsDeleted && b.StaffingLevelId == levelId, ct);
        return (designationRefs, budgetRefs);
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

public record StaffingLevelDto(Guid Id, string Code, string NameEn, string NameAr, int Rank, bool IsActive);
public record StaffingLevelRequest(string? Code, string? NameEn, string? NameAr, int Rank, bool? IsActive);
public record DepartmentBudgetRow(Guid StaffingLevelId, int? BudgetedHeadcount);
public record DepartmentBudgetsRequest(List<DepartmentBudgetRow> Rows, string? Reason);
public record LevelMappingPair(Guid DesignationId, Guid? StaffingLevelId);
public record LevelMappingApplyRequest(List<LevelMappingPair> Mappings, string? Reason);
