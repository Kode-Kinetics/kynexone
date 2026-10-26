using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Employees.Onboarding;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Employees.Onboarding;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Employees;

/// <summary>
/// The hire lifecycle endpoints that sit around <c>POST /api/employees</c>: draft autosave, creator discard, rehire,
/// the review read, and Finance's approval of a new hire's pay package. A separate controller keeps the new routes
/// out of the very large <c>EmployeesController</c>. Every route resolves the employee inside the caller's tenant and
/// company scope and answers a missing record and an out-of-scope record identically (404).
/// </summary>
[ApiController]
[Route("api/employees")]
[Authorize]
public sealed class EmployeeHireController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IAuditService _audit;
    private readonly IEmployeeManagementService _management;
    private readonly IEmployeeActivationGuard _activation;
    private readonly DraftDiscardService _discard;

    public EmployeeHireController(ZayraDbContext db, IAuditService audit, IEmployeeManagementService management,
        IEmployeeActivationGuard activation, DraftDiscardService discard)
    {
        _db = db;
        _audit = audit;
        _management = management;
        _activation = activation;
        _discard = discard;
    }

    public sealed record HireDraftRequest(DateTime? BaseUpdatedAtUtc, EmployeeCreateRequest Request);
    public sealed record RehireRequest(DateTime StartDate, Guid? DesignationId, Guid? GradeId, string? Reason);
    public sealed record ApproveCompensationRequest(string? Note);

    // ── Draft autosave ───────────────────────────────────────────────────────────────────────────
    [HttpPut("{id:int}/hire-draft")]
    [HasPermission("employees.write")]
    public async Task<IActionResult> SaveDraft(int id, [FromBody] HireDraftRequest body, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var employee = await FindInScopeAsync(tenantId, id, ct);
        if (employee is null) return NotFound();
        if (!string.Equals(employee.Status, EmployeeStatuses.Draft, StringComparison.OrdinalIgnoreCase))
            return Problem(409, "not_a_draft", "Only a draft can be autosaved.");
        var current = employee.UpdatedAtUtc ?? employee.CreatedAtUtc;
        if (body.BaseUpdatedAtUtc is { } basis && Math.Abs((current - basis).TotalMilliseconds) > 1)
            return Problem(409, "draft_changed_elsewhere", "This draft was changed in another window. Reload it before saving.");

        var request = body.Request;
        var guard = HttpContext.RequestServices.GetService(typeof(IEmployeeInputGuard)) as IEmployeeInputGuard;
        if (guard is not null) request = await guard.EnforceDraftAsync(tenantId, id, request, Context(tenantId), ct);

        var saved = await _management.UpdateAsync(tenantId, id, request, Context(tenantId), ct, User.HasPermission("employees.sensitive"));
        if (saved is null) return NotFound();
        var after = await _db.Employees.AsNoTracking().Where(e => e.TenantId == tenantId && e.Id == id)
            .Select(e => new { e.UpdatedAtUtc, e.CreatedAtUtc, e.ReadinessState, e.ActivationBlockersCount }).FirstAsync(ct);
        return Ok(new
        {
            updatedAtUtc = after.UpdatedAtUtc ?? after.CreatedAtUtc,
            readiness = new { state = after.ReadinessState, blockers = after.ActivationBlockersCount },
        });
    }

    // ── Creator discard ──────────────────────────────────────────────────────────────────────────
    [HttpDelete("{id:int}/draft")]
    [HasPermission("employees.write")]
    public async Task<IActionResult> DiscardDraft(int id, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var caller = GetUserId();
        var employee = await FindInScopeAsync(tenantId, id, ct);
        if (employee is null || caller is null) return NotFound();
        var activity = await _discard.HasActivityAsync(tenantId, id, ct);
        var verdict = DraftDiscardRules.Evaluate(employee.Status, employee.CreatedBy, caller.Value, employee.CreatedAtUtc, DateTime.UtcNow, activity);
        if (verdict != DraftDiscardRules.Refusal.None)
            return StatusCode(403, new { error = "draft_discard_not_allowed", reason = verdict.ToString(),
                message = "Only the person who created a draft can discard it, within seven days, before it has any activity. Use Delete for anything else." });
        await _discard.DiscardAsync(tenantId, id, Context(tenantId), ct);
        return NoContent();
    }

    // ── Rehire ───────────────────────────────────────────────────────────────────────────────────
    private static readonly string[] RehirableStatuses =
        [EmployeeStatuses.Terminated, EmployeeStatuses.Exited, EmployeeStatuses.Archived, EmployeeStatuses.Offboarded];

    [HttpPost("{id:int}/rehire")]
    [HasPermission("employees.write")]
    public async Task<IActionResult> Rehire(int id, [FromBody] RehireRequest body, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var employee = await FindInScopeAsync(tenantId, id, ct, track: true);
        if (employee is null) return NotFound();
        if (!RehirableStatuses.Contains(employee.Status, StringComparer.OrdinalIgnoreCase))
            return Problem(409, "not_rehirable", "Only a former employee can be rehired.");
        var reason = body.Reason?.Trim() ?? string.Empty;
        if (reason.Length is < 10 or > 500)
            return Problem(422, "override_reason_required", "Say why this person is being rehired (10 to 500 characters).");
        if (await _db.EmployeeFinalSettlements.AnyAsync(s => s.TenantId == tenantId && s.EmployeeId == id
                && s.Status != FinalSettlementStatuses.Paid && s.Status != FinalSettlementStatuses.Cancelled, ct))
            return Problem(409, "final_settlement_open", "The previous final settlement is not settled yet.");
        if (body.DesignationId is Guid d && !await _db.Designations.AnyAsync(x => x.TenantId == tenantId && x.Id == d && !x.IsDeleted, ct))
            return Problem(422, "invalid_reference", "The selected designation is not available.");
        if (body.GradeId is Guid g && !await _db.Grades.AnyAsync(x => x.TenantId == tenantId && x.Id == g && !x.IsDeleted, ct))
            return Problem(422, "invalid_reference", "The selected grade is not available.");

        var context = Context(tenantId);
        var oldStatus = employee.Status;
        var startDate = DateOnly.FromDateTime(body.StartDate);
        // History anchor: the first joining date is kept the first time this person is rehired, never overwritten.
        employee.OriginalJoiningDate ??= DateOnly.FromDateTime(employee.JoiningDate);
        employee.Status = EmployeeStatuses.Draft;
        employee.JoiningDate = body.StartDate.Date;
        employee.ActivatedAtUtc = null;
        employee.UpdatedAtUtc = DateTime.UtcNow;
        employee.UpdatedBy = context.UserId;
        if (body.DesignationId is Guid newDesignation) employee.DesignationId = newDesignation;
        if (body.GradeId is Guid newGrade) employee.GradeId = newGrade;
        _db.EmployeeStatusHistories.Add(new EmployeeStatusHistory
        {
            TenantId = tenantId, EmployeeId = id, OldStatus = oldStatus, NewStatus = EmployeeStatuses.Draft,
            EffectiveDate = startDate, Reason = $"Rehired: {reason}", ChangedByUserId = context.UserId,
        });
        _db.EmployeeHistories.Add(new EmployeeHistory
        {
            TenantId = tenantId, EmployeeId = id, EventType = "Rehired", FieldName = "Status",
            OldValue = oldStatus, NewValue = EmployeeStatuses.Draft, EffectiveDate = startDate, Reason = reason,
            CreatedByUserId = context.UserId,
        });
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("employee.rehired", "Employee", id.ToString(), context,
            JsonSerializer.Serialize(new { previousStatus = oldStatus, startDate }), ct);
        return Ok(new { id, status = employee.Status, joiningDate = employee.JoiningDate });
    }

    // ── Review read ──────────────────────────────────────────────────────────────────────────────
    [HttpGet("{id:int}/hire-review")]
    [HasPermission("employees.read")]
    public async Task<IActionResult> Review(int id, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        if (await FindInScopeAsync(tenantId, id, ct) is null) return NotFound();
        var evaluated = await _activation.EvaluateEmployeeAsync(tenantId, id, ct);
        if (evaluated is null) return NotFound();
        var readiness = evaluated.Value.Readiness;
        var blockers = readiness.Blocking.Select(i => new { key = i.Key, label = i.Label }).ToList();
        if (await CompensationApprovalGate.IsOpenAsync(_db, tenantId, id, ct))
            blockers.Add(new { key = CompensationApprovalGate.GapType, label = "Pay package awaiting Finance approval" });
        return Ok(new
        {
            blockers,
            warnings = readiness.Recommended.Select(i => new { key = i.Key, label = i.Label }),
            notProvided = readiness.PayBlocking.Select(i => new { key = i.Key, label = i.Label }),
            essSections = Array.Empty<string>(),
        });
    }

    // ── Finance approves the pay package set at create ───────────────────────────────────────────
    [HttpPost("{id:int}/compensation/approve")]
    [HasPermission(CompensationApprovalGate.ApprovePermission)]
    public async Task<IActionResult> ApproveCompensation(int id, [FromBody] ApproveCompensationRequest body, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var caller = GetUserId();
        var employee = await FindInScopeAsync(tenantId, id, ct);
        if (employee is null || caller is null) return NotFound();
        if (!User.HasPermission("employees.sensitive"))
            return StatusCode(403, new { error = "compensation_not_visible", message = "Approving a pay package needs a role that can see it (employees.sensitive)." });
        if (string.IsNullOrWhiteSpace(body.Note))
            return BadRequest(new { error = "note_required", message = "Add a note saying what was checked." });

        var open = await _db.EmployeeImportGaps
            .Where(g => g.TenantId == tenantId && g.EmployeeId == id && g.GapType == CompensationApprovalGate.GapType && g.ResolvedAtUtc == null)
            .ToListAsync(ct);
        if (open.Count == 0)
            return Conflict(new { error = "nothing_to_approve", message = "This employee has no pay package waiting for approval." });

        var profile = await _db.EmployeePayrollProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.EmployeeId == id && !p.IsDeleted, ct);
        var makers = new HashSet<Guid>();
        if (profile?.CreatedBy is Guid pc) makers.Add(pc);
        if (profile?.UpdatedBy is Guid pu) makers.Add(pu);
        var subjectUsers = await ApprovalUnblockSubjects(tenantId, id, employee, ct);
        var refusal = CompensationApprovalGate.RefusalFor(caller.Value, employee.CreatedBy, subjectUsers.Contains(caller.Value) ? caller.Value : employee.UserAccountId, makers);
        if (refusal is not null || await CredentialHandlerBar.IsBarredAsync(_db, tenantId, id, caller.Value, DateTime.UtcNow, ct))
            return StatusCode(403, new { error = "second_person_required",
                message = "A pay package must be approved by someone other than the person who entered it, the employee, or the person who issued the employee's sign-in code." });

        var now = DateTime.UtcNow;
        foreach (var g in open) g.ResolvedAtUtc = now;
        await _audit.WriteAsync(CompensationApprovalGate.ApprovedAction, "Employee", id.ToString(), Context(tenantId),
            JsonSerializer.Serialize(new
            {
                note = body.Note.Trim(),
                fingerprint = CompensationApprovalGate.FingerprintForApproval(employee.Salary, profile?.Iban),
            }), ct);
        return Ok(new { approved = true });
    }

    private async Task<IReadOnlyCollection<Guid>> ApprovalUnblockSubjects(Guid tenantId, int id, Employee employee, CancellationToken ct)
    {
        var users = new HashSet<Guid>(await Zayra.Api.Infrastructure.Approvals.ApprovalUnblock.SubjectUserIdsAsync(_db, tenantId, id, ct));
        if (employee.UserAccountId is Guid u) users.Add(u);
        return users;
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────
    private async Task<Employee?> FindInScopeAsync(Guid tenantId, int id, CancellationToken ct, bool track = false)
    {
        var query = ScopedBypass.NullableTenantWide(_db.Employees, tenantId,
            "Hire lifecycle: the employee's company is resolved before the ambient company filter can hide it; the scope check follows.");
        var employee = await (track ? query.AsTracking() : query.AsNoTracking()).FirstOrDefaultAsync(e => e.Id == id && !e.IsDeleted, ct);
        if (employee is null) return null;
        var scope = this.GetEntityScope();
        return employee.CompanyId is null || scope.CanAccessCompany(employee.CompanyId) ? employee : null;
    }

    private ObjectResult Problem(int status, string code, string detail) => BuildProblem(HttpContext.TraceIdentifier, status, code, detail);

    internal static ObjectResult BuildProblem(string traceId, int status, string code, string detail)
    {
        var problem = new ProblemDetails { Status = status, Title = code, Detail = detail };
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = traceId;
        return new ObjectResult(problem) { StatusCode = status, ContentTypes = { "application/problem+json" } };
    }

    private Guid RequireTenant() => Guid.Parse(User.FindFirstValue("tenant_id") ?? throw new UnauthorizedAccessException("Tenant claim missing."));
    private Guid? GetUserId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id) ? id : null;
    private RequestContext Context(Guid tenantId) => new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), GetUserId(), tenantId);
}
