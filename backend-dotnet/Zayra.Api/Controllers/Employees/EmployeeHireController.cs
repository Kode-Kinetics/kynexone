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
using Zayra.Api.Infrastructure.Http;
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
    [TenantUserRateLimit("hire-draft")]
    [RequestSizeLimit(65_536)]
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
        // A caller who cannot see pay or identity values resumed a form that holds none of them: the server must not read
        // those empty (or masked) fields as an instruction to clear what is stored. What they typed is still applied.
        if (!User.HasPermission("employees.sensitive"))
            request = await KeepStoredSensitiveValuesAsync(tenantId, id, request, ct);
        var guard = HttpContext.RequestServices.GetService(typeof(IEmployeeInputGuard)) as IEmployeeInputGuard;
        if (guard is not null) request = await guard.EnforceDraftAsync(tenantId, id, request, Context(tenantId), ct);

        // Take the draft atomically: only one of two saves made from the same basis gets past this line.
        var claimedAt = DateTime.UtcNow;
        var claimed = await ScopedBypass.NullableTenantWide(_db.Employees, tenantId, "Draft autosave claim: a conditional update on the draft the caller already resolved in scope; tenant pinned.")
            .Where(e => e.Id == id && !e.IsDeleted && e.UpdatedAtUtc == employee.UpdatedAtUtc)
            .ExecuteUpdateAsync(set => set.SetProperty(e => e.UpdatedAtUtc, (DateTime?)claimedAt), ct);
        if (claimed == 0)
            return Problem(409, "draft_changed_elsewhere", "This draft was changed in another window. Reload it before saving.");
        EmployeeDetailDto? saved;
        try { saved = await _management.UpdateAsync(tenantId, id, request, Context(tenantId), ct, User.HasPermission("employees.sensitive")); }
        catch
        {
            // The save failed: give the draft its previous stamp back so the client's basis is still valid.
            await ScopedBypass.NullableTenantWide(_db.Employees, tenantId, "Draft autosave claim release; tenant pinned.")
                .Where(e => e.Id == id && e.UpdatedAtUtc == claimedAt)
                .ExecuteUpdateAsync(set => set.SetProperty(e => e.UpdatedAtUtc, employee.UpdatedAtUtc), CancellationToken.None);
            throw;
        }
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
    [TenantUserRateLimit("draft-discard")]
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
    [TenantUserRateLimit("employee-rehire")]
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
        // The start date follows the same hard window as a new hire's joining date (today - 50 years .. today + 2 years).
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var requested = DateOnly.FromDateTime(body.StartDate);
        if (requested < today.AddYears(-50) || requested > today.AddYears(2))
            throw new EmployeeValidationException(400, EmployeeProblemCodes.ValidationFailed,
                new Dictionary<string, string[]> { ["startDate"] = [EmployeeFieldErrors.JoiningOutOfRange] },
                "The start date is outside the allowed range.");
        // A changed designation or grade is checked like a new hire's: this tenant, active, the employing company or shared,
        // and the grade the designation belongs to.
        var gradeChanged = body.GradeId is Guid requestedGrade && requestedGrade != employee.GradeId;
        if (body.DesignationId is not null || body.GradeId is not null)
        {
            var refs = new EmployeeReferenceValidator(_db);
            var probe = new EmployeeCreateRequest(null, false, employee.EnglishName ?? employee.FullName, null, null, null, null, null, null, null, null, null, null,
                employee.CompanyId, null, null, body.DesignationId, body.GradeId ?? (body.DesignationId is not null ? employee.GradeId : null), null, null, null, null,
                null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);
            await refs.ValidateAsync(tenantId, probe, this.GetEntityScope(), id, ct);
        }

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
        // The person is a new hire again: whatever pay they carry waits for Finance (a grade change always does).
        await CompensationApprovalGate.ReconcileAsync(_db, employee, null, context, ct, forceReopen: gradeChanged);
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
        var blockers = readiness.Blocking.Select(i => new { key = ReviewKey(i), label = i.Label }).ToList();
        if (await CompensationApprovalGate.IsOpenAsync(_db, tenantId, id, ct) || await CompensationApprovalGate.CarriesUnapprovedPayAsync(_db, tenantId, id, ct))
            blockers.Add(new { key = CompensationApprovalGate.GapType, label = "Pay package awaiting Finance approval" });
        return Ok(new
        {
            blockers,
            warnings = readiness.Recommended.Select(i => new { key = ReviewKey(i), label = i.Label }),
            notProvided = readiness.PayBlocking.Select(i => new { key = ReviewKey(i), label = i.Label }),
            essSections = Array.Empty<string>(),
        });
    }

    private static bool BlankOrMasked(string? v) => string.IsNullOrWhiteSpace(v) || v.Contains('\u2022') || v.Contains('*');

    /// <summary>
    /// CD46: for a caller without <c>employees.sensitive</c>, a blank or masked pay, bank or identity value in an autosave means
    /// "not shown to me", never "clear it": the stored value is kept. A value they actually typed is applied as before (and still
    /// opens the pay-approval gap).
    /// </summary>
    private async Task<EmployeeCreateRequest> KeepStoredSensitiveValuesAsync(Guid tenantId, int id, EmployeeCreateRequest request, CancellationToken ct)
    {
        if (request.PayrollProfile is { } p)
        {
            var stored = await _db.EmployeePayrollProfiles.AsNoTracking()
                .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.EmployeeId == id && !x.IsDeleted, ct);
            if (stored is not null)
            {
                static string? Keep(string? sent, string? kept) => BlankOrMasked(sent) ? kept : sent;
                request = request with { PayrollProfile = p with
                {
                    BankName = Keep(p.BankName, stored.BankName), Iban = Keep(p.Iban, stored.Iban), AccountNumber = Keep(p.AccountNumber, stored.AccountNumber),
                    PaymentMethod = Keep(p.PaymentMethod, stored.PaymentMethod), SalaryCurrency = Keep(p.SalaryCurrency, stored.SalaryCurrency),
                    PayrollGroup = Keep(p.PayrollGroup, stored.PayrollGroup), SalaryStructureReference = Keep(p.SalaryStructureReference, stored.SalaryStructureReference),
                    SocialInsuranceReference = Keep(p.SocialInsuranceReference, stored.SocialInsuranceReference), MolId = Keep(p.MolId, stored.MolId),
                    BankRoutingCode = Keep(p.BankRoutingCode, stored.BankRoutingCode),
                } };
            }
        }
        if (request.ComplianceRecords is { } records)
            request = request with { ComplianceRecords = records.Where(r => !BlankOrMasked(r.FieldValue)).ToList() };
        if (request.SalaryBreakdown is { } b
            && (b.BasicSalary ?? 0m) == 0m && (b.HousingAllowance ?? 0m) == 0m && (b.TransportAllowance ?? 0m) == 0m && (b.FoodAllowance ?? 0m) == 0m
            && (b.MobileAllowance ?? 0m) == 0m && (b.OtherAllowance ?? 0m) == 0m && (b.FixedDeduction ?? 0m) == 0m)
            request = request with { SalaryBreakdown = null };
        return request;
    }

    /// <summary>CD47: every review item carries a stable key (the client renders its sentence from it); a blank one is sent as "unknown".</summary>
    internal static string ReviewKey(ReadinessItem item) => string.IsNullOrWhiteSpace(item.Key) ? "unknown" : item.Key;

    // ── Finance approves the pay package set at create ───────────────────────────────────────────
    [HttpPost("{id:int}/compensation/approve")]
    [HasPermission(CompensationApprovalGate.ApprovePermission)]
    [TenantUserRateLimit("compensation-approve")]
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
        var livePay = await CompensationApprovalGate.LiveFingerprintAsync(_db, tenantId, id, ct);
        var alreadyApproved = await CompensationApprovalGate.ApprovedFingerprintAsync(_db, tenantId, id, ct);
        // Nothing waiting AND the stored figures are the approved ones: nothing to do. A package that moved on without a
        // gap (a write that bypassed the hooks) is approved afresh.
        if (open.Count == 0 && string.Equals(alreadyApproved, livePay, StringComparison.Ordinal))
            return Conflict(new { error = "nothing_to_approve", message = "This employee has no pay package waiting for approval." });

        var profile = await _db.EmployeePayrollProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.EmployeeId == id && !p.IsDeleted, ct);
        var makers = new HashSet<Guid>(await CompensationApprovalGate.PayWritersSinceApprovalAsync(_db, tenantId, id, ct));
        if (profile?.CreatedBy is Guid pc) makers.Add(pc);
        if (profile?.UpdatedBy is Guid pu) makers.Add(pu);
        if (employee.UpdatedBy is Guid eu) makers.Add(eu);
        var subjectUsers = await ApprovalUnblockSubjects(tenantId, id, employee, ct);
        var refusal = CompensationApprovalGate.RefusalFor(caller.Value, employee.CreatedBy, subjectUsers.Contains(caller.Value) ? caller.Value : employee.UserAccountId, makers);
        if (refusal is not null || await CredentialHandlerBar.IsBarredAsync(_db, tenantId, id, caller.Value, DateTime.UtcNow, ct))
            return StatusCode(403, new { error = "second_person_required",
                message = "A pay package must be approved by someone other than the person who entered it, the employee, or the person who issued the employee's sign-in code." });

        var now = DateTime.UtcNow;
        if (open.Count == 0)
            open.Add(NewResolvedGap(tenantId, employee));
        foreach (var g in open)
        {
            g.ResolvedAtUtc = now;
            g.RawValue = CompensationApprovalGate.ApprovedMarker + livePay;   // the approval is bound to these exact figures
        }
        await _audit.WriteAsync(CompensationApprovalGate.ApprovedAction, "Employee", id.ToString(), Context(tenantId),
            JsonSerializer.Serialize(new
            {
                note = body.Note.Trim(),
                fingerprint = CompensationApprovalGate.FingerprintForApproval(employee.Salary, profile?.Iban),
            }), ct);
        return Ok(new { approved = true });
    }

    private EmployeeImportGap NewResolvedGap(Guid tenantId, Employee employee)
    {
        var gap = new EmployeeImportGap
        {
            TenantId = tenantId, CompanyId = employee.CompanyId, ImportBatchId = Guid.Empty, EmployeeId = employee.Id, RowNumber = 0,
            GapType = CompensationApprovalGate.GapType, GapCategory = "pay", Detail = "Pay package and bank details approved by Finance.",
        };
        _db.EmployeeImportGaps.Add(gap);
        return gap;
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
        // A record with no company is invisible to a scoped caller everywhere else; the hire routes agree (group-level callers pass).
        return scope.CanAccessCompany(employee.CompanyId) ? employee : null;
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
    private RequestContext Context(Guid tenantId) => new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), GetUserId(), tenantId,
        Permissions: User.EffectivePermissions());
}
