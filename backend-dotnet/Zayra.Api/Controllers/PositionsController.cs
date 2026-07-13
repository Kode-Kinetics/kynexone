using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/positions")]
[Authorize(Roles = "Admin,HR Manager,HR Officer,Auditor")]
public class PositionsController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IAuditService _audit;

    public PositionsController(ZayraDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid? companyId, [FromQuery] Guid? departmentId, [FromQuery] string? status, CancellationToken ct)
    {
        if (!HasAnyPermission("organization.read", "organization.write", "reports.read")) return Forbid();
        var tenantId = RequireTenant();
        var scope = this.GetEntityScope();
        var query = _db.Positions.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted);
        if (!scope.IsGroupLevel)
            query = query.Where(x => x.CompanyId.HasValue && scope.AccessibleCompanyIds.Contains(x.CompanyId.Value));
        if (companyId is not null) query = query.Where(x => x.CompanyId == companyId);
        if (departmentId is not null) query = query.Where(x => x.DepartmentId == departmentId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        return Ok(await query.OrderBy(x => x.Code).ToListAsync(ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        if (!HasAnyPermission("organization.read", "organization.write", "reports.read")) return Forbid();
        var tenantId = RequireTenant();
        var position = await Scoped().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && !x.IsDeleted, ct);
        return position is null ? NotFound() : Ok(position);
    }

    [HttpPost]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Create(PositionRequest request, CancellationToken ct)
    {
        if (!HasPermission("organization.write")) return Forbid();
        try
        {
            var tenantId = RequireTenant();
            await ValidateAsync(tenantId, request, null, ct);
            var position = Apply(new Position { TenantId = tenantId, CreatedBy = UserId() }, request);
            _db.Positions.Add(position);
            await _db.SaveChangesAsync(ct);
            await WriteAudit("position.created", position, ct);
            return Created($"/api/positions/{position.Id}", position);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Update(Guid id, PositionRequest request, CancellationToken ct)
    {
        if (!HasPermission("organization.write")) return Forbid();
        try
        {
            var tenantId = RequireTenant();
            var position = await Scoped().FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && !x.IsDeleted, ct);
            if (position is null) return NotFound();
            await ValidateAsync(tenantId, request, position, ct);
            Apply(position, request);
            position.UpdatedAtUtc = DateTime.UtcNow;
            position.UpdatedBy = UserId();
            await _db.SaveChangesAsync(ct);
            await WriteAudit("position.updated", position, ct);
            return Ok(position);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("{id:guid}/freeze")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Freeze(Guid id, CancellationToken ct) => await ChangeStatus(id, PositionStatuses.Frozen, ct);

    [HttpPost("{id:guid}/reopen")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Reopen(Guid id, CancellationToken ct) => await ChangeStatus(id, PositionStatuses.Open, ct);

    private async Task<IActionResult> ChangeStatus(Guid id, string status, CancellationToken ct)
    {
        if (!HasPermission("organization.write")) return Forbid();
        var tenantId = RequireTenant();
        var position = await Scoped().FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && !x.IsDeleted, ct);
        if (position is null) return NotFound();
        if (status == PositionStatuses.Frozen && position.IncumbentEmployeeId is not null)
            return Conflict(new { message = "An occupied position cannot be frozen. Transfer or unassign its incumbent first." });
        position.Status = status;
        position.UpdatedAtUtc = DateTime.UtcNow;
        position.UpdatedBy = UserId();
        await _db.SaveChangesAsync(ct);
        await WriteAudit("position.status_changed", position, ct);
        return Ok(position);
    }

    private async Task ValidateAsync(Guid tenantId, PositionRequest request, Position? existing, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Title))
            throw new InvalidOperationException("Position code and title are required.");
        if (request.Fte <= 0 || request.Fte > 100) throw new InvalidOperationException("FTE must be greater than zero and no more than 100.");
        if (request.BudgetedMonthlyCost < 0) throw new InvalidOperationException("Budgeted monthly cost cannot be negative.");
        if (string.IsNullOrWhiteSpace(request.Currency)) throw new InvalidOperationException("Currency is required.");
        if (request.EffectiveTo is not null && request.EffectiveTo < request.EffectiveFrom)
            throw new InvalidOperationException("Effective-to date cannot be before effective-from date.");
        var existingId = existing?.Id ?? Guid.Empty;
        if (await _db.Positions.AnyAsync(x => x.TenantId == tenantId && x.Code == request.Code.Trim() && !x.IsDeleted && x.Id != existingId, ct))
            throw new InvalidOperationException("Position code already exists.");
        if (request.CompanyId is not null && !await _db.Companies.AnyAsync(x => x.TenantId == tenantId && x.Id == request.CompanyId && x.IsActive && !x.IsDeleted, ct))
            throw new InvalidOperationException("Selected legal entity is not active.");
        if (request.DepartmentId is not null && !await _db.Departments.AnyAsync(x => x.TenantId == tenantId && x.Id == request.DepartmentId && x.IsActive && !x.IsDeleted, ct))
            throw new InvalidOperationException("Selected department is not active.");
        if (request.DesignationId is not null && !await _db.Designations.AnyAsync(x => x.TenantId == tenantId && x.Id == request.DesignationId && x.IsActive && !x.IsDeleted, ct))
            throw new InvalidOperationException("Selected designation is not active.");
        if (request.GradeId is not null && !await _db.Grades.AnyAsync(x => x.TenantId == tenantId && x.Id == request.GradeId && x.IsActive && !x.IsDeleted, ct))
            throw new InvalidOperationException("Selected grade is not active.");
        if (request.DesignationId is not null && request.GradeId is not null)
        {
            var designationGrade = await _db.Designations.Where(x => x.TenantId == tenantId && x.Id == request.DesignationId).Select(x => x.GradeId).FirstAsync(ct);
            if (designationGrade is not null && designationGrade != request.GradeId)
                throw new InvalidOperationException("The selected designation is not eligible for the selected grade.");
        }
    }

    private static Position Apply(Position position, PositionRequest request)
    {
        position.Code = request.Code.Trim(); position.Title = request.Title.Trim(); position.CompanyId = request.CompanyId;
        position.BranchId = request.BranchId; position.DepartmentId = request.DepartmentId; position.CostCenterId = request.CostCenterId;
        position.DesignationId = request.DesignationId; position.GradeId = request.GradeId; position.Fte = request.Fte;
        position.BudgetedMonthlyCost = request.BudgetedMonthlyCost; position.Currency = request.Currency.Trim().ToUpperInvariant();
        position.EffectiveFrom = request.EffectiveFrom; position.EffectiveTo = request.EffectiveTo;
        return position;
    }

    private IQueryable<Position> Scoped() => _db.Positions.Where(x => this.GetEntityScope().IsGroupLevel || (x.CompanyId.HasValue && this.GetEntityScope().AccessibleCompanyIds.Contains(x.CompanyId.Value)));
    private Guid RequireTenant() => this.GetTenantId() ?? throw new UnauthorizedAccessException();
    private Guid? UserId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id) ? id : null;
    private Task WriteAudit(string action, Position position, CancellationToken ct) => _audit.WriteAsync(action, "Position", position.Id.ToString(), new RequestContext(null, null, UserId(), RequireTenant()), JsonSerializer.Serialize(new { position.Code, position.Status, position.CompanyId, position.DepartmentId, position.DesignationId, position.GradeId, position.Fte, position.BudgetedMonthlyCost }), ct);
    private bool HasPermission(string permission) =>
        User.Claims.Any(c => c.Type == "permission" && string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));
    private bool HasAnyPermission(params string[] permissions) => permissions.Any(HasPermission);
}

public record PositionRequest(string Code, string Title, Guid? CompanyId, Guid? BranchId, Guid? DepartmentId, Guid? CostCenterId, Guid? DesignationId, Guid? GradeId, decimal Fte, decimal BudgetedMonthlyCost, string Currency, DateOnly EffectiveFrom, DateOnly? EffectiveTo);
