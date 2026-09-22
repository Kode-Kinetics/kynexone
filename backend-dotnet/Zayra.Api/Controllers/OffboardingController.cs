using System.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

/// <summary>
/// Employee separation lifecycle: initiate (resignation/termination) → serve notice (Offboarded,
/// still counted in headcount) → exit interview + checklist → complete (Archived). Raises a backfill
/// requisition at initiation so hiring can start during the notice period.
/// </summary>
[ApiController]
[Route("api/offboarding")]
// Reads keep their original role list (re-applied per GET below); write actions move to
// employees.write / employees.approve so a custom HR role can be granted the offboarding surface.
[Authorize]
public class OffboardingController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IAuditService _audit;
    private readonly IEmployeeActivationGuard _activationGuard;
    public OffboardingController(ZayraDbContext db, IAuditService? audit = null, IEmployeeActivationGuard? activationGuard = null)
    {
        _db = db;
        // Mirror EmployeesController's ApprovalWorkflowService default: DI always supplies the audit
        // service in production; the optional fallback keeps direct-construction call sites working.
        _audit = audit ?? new Zayra.Api.Infrastructure.Audit.AuditService(db);
        _activationGuard = activationGuard ?? new EmployeeActivationGuard(db);
    }

    [HttpGet]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> List([FromQuery] string? status, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var q = _db.EmployeeOffboardings.AsNoTracking().Where(o => o.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(o => o.Status == status);
        var items = await q.OrderByDescending(o => o.CreatedAtUtc).ToListAsync(ct);
        return Ok(items);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var o = await _db.EmployeeOffboardings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        return o is null ? NotFound() : Ok(o);
    }

    /// <summary>Attrition insight: in-notice/completed counts, avg exit rating, and reasons breakdown.</summary>
    [HttpGet("summary")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> Summary(CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var all = await _db.EmployeeOffboardings.AsNoTracking().Where(o => o.TenantId == tenantId).ToListAsync(ct);
        var withRating = all.Where(o => o.ExitInterviewRating > 0).ToList();
        var reasons = all.Where(o => !string.IsNullOrWhiteSpace(o.ExitReasonCategory))
            .GroupBy(o => o.ExitReasonCategory)
            .Select(g => new { category = g.Key, count = g.Count() })
            .OrderByDescending(x => x.count).ToList();
        return Ok(new
        {
            inNotice = all.Count(o => o.Status == "InProgress"),
            completed = all.Count(o => o.Status == "Completed"),
            exitInterviewsPending = all.Count(o => o.Status == "InProgress" && o.ExitInterviewStatus == "Pending"),
            avgExitRating = withRating.Count > 0 ? Math.Round(withRating.Average(o => o.ExitInterviewRating), 1) : 0,
            reasons,
        });
    }

    /// <summary>
    /// S2-B3 — the separation vocabulary, served rather than re-typed in the client.
    ///
    /// <para>The backend has always had a closed, legally load-bearing vocabulary
    /// (<c>EmployeeManagementService.AllowedSeparationTypes</c>) whose doc comment explains that an
    /// unrecognised value "would silently pay a full gratuity where the statute forfeits it entirely".
    /// The offboarding SCREEN offered a different list — <c>Resignation, Termination, End of Contract,
    /// Retirement, Other</c> — so <c>Article80</c>, <c>Death</c>, <c>ProbationFailure</c> and
    /// <c>Redundancy</c> were unreachable, and two of the five values it did offer were not in the
    /// allowed set at all. Serving the list is what stops the two drifting apart again.</para>
    /// </summary>
    [HttpGet("separation-types")]
    public IActionResult SeparationTypes() => Ok(SeparationTypeCatalog.All);

    [HttpPost("initiate")]
    [HasPermission("employees.approve")]
    public async Task<IActionResult> Initiate([FromBody] InitiateOffboardingRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var emp = await _db.Employees.FirstOrDefaultAsync(e => e.Id == req.EmployeeId && e.TenantId == tenantId && !e.IsDeleted, ct);
        if (emp is null) return NotFound(new { message = "Employee not found." });
        // Establishment integrity (security review R5): "Offboarded" is an OCCUPYING status
        // (serving notice still holds the seat). Only someone already occupying a seat
        // (Active / Suspended) may enter offboarding — otherwise a non-occupying employee
        // (Draft / Invited / Terminated / legacy Inactive) would transition INTO occupancy
        // through this side door without the establishment guard ever running.
        if (!Zayra.Api.Application.Organization.EstablishmentOccupancy.IsOccupyingStatus(emp.Status))
            return BadRequest(new { message = $"Employee is '{emp.Status}' and cannot be offboarded — only an actively employed person (Active/Suspended) can serve notice. Reactivate the employee first if this is a data correction." });
        if (await _db.EmployeeOffboardings.AnyAsync(o => o.TenantId == tenantId && o.EmployeeId == req.EmployeeId && o.Status == "InProgress", ct))
            return BadRequest(new { message = "This employee already has an offboarding in progress." });

        // ── S2-B3 — NORMALISE THE SEPARATION TYPE ────────────────────────────────────────────────────
        // This endpoint used to write req.SeparationType VERBATIM, unlike the `terminate` command path
        // which calls the same normaliser and throws on an unknown value. The screen therefore persisted
        // "End of Contract" and "Other" — neither in the vocabulary — straight past the guard that exists
        // because the value decides the end-of-service award. A record saved that way then throws the
        // first time anyone routes it through PATCH /employees/{id}/status.
        if (!EmployeeManagementService.TryNormalizeSeparationType(req.SeparationType, out var separationType))
            return BadRequest(new
            {
                error   = "unknown_separation_type",
                message = $"'{req.SeparationType}' is not a recognised separation type. This value decides the "
                        + "end-of-service award, so an unrecognised one is refused rather than silently paid as a "
                        + "full award.",
                allowed = EmployeeManagementService.AllowedSeparationTypes,
            });

        // Article 80 FORFEITS the award entirely. A dismissal for cause recorded with no stated cause is
        // an unevidenced forfeiture of a statutory entitlement, and it is the single most litigated
        // decision in this whole flow — so it is the one separation type that cannot be keyed silently.
        if (EmployeeManagementService.ForfeitsEndOfServiceAward(separationType)
            && string.IsNullOrWhiteSpace(req.Reason))
            return BadRequest(new
            {
                error   = "article80_reason_required",
                message = "An Article 80 summary dismissal forfeits the end-of-service award in full. Record the "
                        + "specific ground relied on (KSA Labour Law Art. 80 (1)–(9)) before saving.",
            });

        var notice = req.NoticeDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        // ── S2-F4 — NOTICE PERIOD AND LAST WORKING DAY ───────────────────────────────────────────────
        // Initiate ignored Employee.NoticePeriodDays entirely — the CONTRACTUAL notice period, stored on
        // the employee record — so whoever raised the offboarding typed a number and nothing reconciled
        // it. A negative/absent value now falls back to the contract, and the explicit last working day
        // the API has always accepted is validated rather than trusted (the screen used to compute it
        // client-side and post it read-only, which made immediate termination with pay in lieu, a
        // negotiated early release and a non-weekend LWD all unenterable).
        var noticeDays = req.NoticePeriodDays >= 0 ? req.NoticePeriodDays : (emp.NoticePeriodDays ?? 0);
        var lwd = req.LastWorkingDay ?? notice.AddDays(noticeDays);

        if (lwd < notice)
            return BadRequest(new
            {
                error   = "last_working_day_before_notice",
                message = $"The last working day ({lwd:yyyy-MM-dd}) cannot be before the notice date ({notice:yyyy-MM-dd}).",
            });
        var joining = DateOnly.FromDateTime(emp.JoiningDate);
        if (lwd < joining)
            return BadRequest(new
            {
                error   = "last_working_day_before_joining",
                message = $"The last working day ({lwd:yyyy-MM-dd}) cannot be before the employee's joining date "
                        + $"({joining:yyyy-MM-dd}) — the end-of-service award is computed from that span.",
            });
        // Served notice is a DERIVED fact once the last working day is explicit: it is what the
        // settlement's unserved-notice test reads, so it must describe what actually happened rather than
        // what someone typed. An early release therefore records the notice it really served.
        var servedNoticeDays = lwd.DayNumber - notice.DayNumber;
        var shortNotice = req.LastWorkingDay is not null && servedNoticeDays < noticeDays;

        // Raise a backfill requisition so hiring can begin during notice (skip for retirement/non-backfill).
        Guid? backfillId = null;
        if (req.RaiseBackfill)
        {
            var year = DateTime.UtcNow.Year;
            var seq = await _db.ManpowerRequisitions.CountAsync(r => r.TenantId == tenantId, ct) + 1;
            var reqEntity = new ManpowerRequisition
            {
                TenantId = tenantId,
                RequisitionNumber = $"MRQ-{year}-{seq:0000}",
                DepartmentId = emp.DepartmentId,
                DepartmentName = emp.Department ?? string.Empty,
                DesignationId = emp.DesignationId,
                DesignationTitle = emp.Designation ?? string.Empty,
                HeadCount = 1,
                EmploymentType = string.IsNullOrWhiteSpace(emp.ContractType) ? "Full-Time" : emp.ContractType,
                Priority = "High",
                Status = "Draft",
                TargetJoiningDate = lwd,
                Justification = $"Backfill for {emp.FullName} ({emp.EmployeeCode}) — {separationType.ToLowerInvariant()}; last working day {lwd:yyyy-MM-dd}.",
                RequestedByUserId = this.GetUserId(),
            };
            _db.ManpowerRequisitions.Add(reqEntity);
            backfillId = reqEntity.Id;
        }

        var off = new EmployeeOffboarding
        {
            TenantId = tenantId,
            EmployeeId = emp.Id, EmployeeName = emp.FullName, EmployeeCode = emp.EmployeeCode,
            Department = emp.Department ?? string.Empty, Designation = emp.Designation ?? string.Empty,
            SeparationType = separationType, Reason = req.Reason ?? string.Empty,
            // The CONTRACTUAL notice, not the served count: PayrollController's settlement plan tests
            // `NoticeDate.AddDays(NoticePeriodDays) > LastWorkingDay` to raise the unserved-notice /
            // pay-in-lieu warning. Storing the served count here would make that test vacuous and the
            // warning could never fire on the early release it exists to catch.
            NoticeDate = notice, NoticePeriodDays = Math.Max(0, noticeDays), LastWorkingDay = lwd,
            RehireEligible = req.RehireEligible,
            Status = "InProgress", ExitInterviewStatus = "Pending",
            BackfillRequisitionId = backfillId,
            CreatedByUserId = this.GetUserId(),
        };
        _db.EmployeeOffboardings.Add(off);

        // Serving notice: still employed (counts in headcount) but flagged as leaving.
        // S2-B1: this is the write that made the WPS file un-generable. It is correct — the person IS
        // still employed and must still be paid — and WpsSifValidator no longer treats it as terminal.
        emp.Status = "Offboarded";
        emp.UpdatedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        var ictx = new RequestContext(HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString(), this.GetUserId(), tenantId);
        await _audit.WriteAsync("offboarding.initiated", "EmployeeOffboarding", off.Id.ToString(), ictx,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                employeeId = emp.Id, emp.EmployeeCode, separationType,
                noticeDate = notice, contractualNoticeDays = off.NoticePeriodDays,
                lastWorkingDay = lwd, servedNoticeDays,
                forfeitsEndOfServiceAward = EmployeeManagementService.ForfeitsEndOfServiceAward(separationType),
                off.RehireEligible,
            }), ct);

        return Ok(new
        {
            offboarding = off,
            // Surfaced, never silently applied: an early release is a real decision with a pay-in-lieu
            // consequence, and the operator should see it at the moment they make it.
            noticeShortfallDays = shortNotice ? Math.Max(0, noticeDays - servedNoticeDays) : 0,
            forfeitsEndOfServiceAward = EmployeeManagementService.ForfeitsEndOfServiceAward(separationType),
        });
    }

    [HttpPatch("{id:guid}/exit-interview")]
    [HasPermission("employees.write")]
    public async Task<IActionResult> ExitInterview(Guid id, [FromBody] ExitInterviewRequest req, CancellationToken ct)
    {
        var off = await Find(id, ct);
        if (off is null) return NotFound();
        off.ExitInterviewStatus = string.IsNullOrWhiteSpace(req.Status) ? off.ExitInterviewStatus : req.Status;
        off.ExitInterviewDate = req.Date ?? off.ExitInterviewDate;
        off.ExitReasonCategory = req.ReasonCategory ?? off.ExitReasonCategory;
        off.ExitInterviewRating = req.Rating is >= 0 and <= 5 ? req.Rating : off.ExitInterviewRating;
        off.ExitInterviewNotes = req.Notes ?? off.ExitInterviewNotes;
        off.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(off);
    }

    [HttpPatch("{id:guid}/checklist")]
    [HasPermission("employees.write")]
    public async Task<IActionResult> Checklist(Guid id, [FromBody] OffboardingChecklistRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var employeeId = await ResolveOffboardingEmployeeIdAsync(tenantId, id, ct);
        if (employeeId is null) return NotFound();

        var changedAtUtc = DateTime.UtcNow;
        var checklistAuditId = Guid.NewGuid();
        var revocationAuditId = Guid.NewGuid();
        IActionResult? refusal = null;

        async Task<bool> MutateOnceAsync(CancellationToken token)
        {
            _db.ChangeTracker.Clear();
            var graph = await LockOffboardingGraphAsync(tenantId, id, employeeId.Value, token);
            if (graph is null)
            {
                refusal = NotFound();
                return false;
            }
            if (graph.Offboarding.Status != "InProgress")
            {
                refusal = Conflict(new
                {
                    error = "offboarding_not_active",
                    message = "Only an in-progress offboarding checklist can be changed."
                });
                return false;
            }
            if (req.AccessRevoked == false && graph.Offboarding.AccessRevoked)
            {
                refusal = Conflict(new
                {
                    error = "access_revocation_is_irreversible",
                    message = "Access revocation cannot be undone from the checklist. If the separation is "
                            + "withdrawn, cancel the offboarding and issue a new controlled login invitation."
                });
                return false;
            }

            graph.Offboarding.AssetsReturned = req.AssetsReturned ?? graph.Offboarding.AssetsReturned;
            graph.Offboarding.KnowledgeHandover = req.KnowledgeHandover ?? graph.Offboarding.KnowledgeHandover;
            graph.Offboarding.FinalSettlementDone = req.FinalSettlementDone ?? graph.Offboarding.FinalSettlementDone;

            var accessRevokedNow = req.AccessRevoked == true && !graph.Offboarding.AccessRevoked;
            if (accessRevokedNow)
            {
                EnsureAnotherAdministratorSurvives(graph, changedAtUtc);
                StageAccessRevocation(graph, this.GetUserId(), unlinkAccount: false, changedAtUtc,
                    HttpContext.Connection.RemoteIpAddress?.ToString());
                _db.AuditLogs.Add(CreateOffboardingAudit(
                    revocationAuditId, changedAtUtc, "offboarding.access_revoked", graph,
                    new { source = "checklist", userIds = graph.TargetUsers.Select(x => x.Id).ToArray() }));
            }

            graph.Offboarding.UpdatedAtUtc = changedAtUtc;
            _db.AuditLogs.Add(CreateOffboardingAudit(
                checklistAuditId, changedAtUtc, "offboarding.checklist_updated", graph,
                new
                {
                    graph.Offboarding.AssetsReturned,
                    graph.Offboarding.KnowledgeHandover,
                    graph.Offboarding.FinalSettlementDone,
                    graph.Offboarding.AccessRevoked,
                    accessRevokedNow
                }));
            await _db.SaveChangesAsync(token);
            return true;
        }

        try
        {
            await ExecuteAtomicMutationAsync(MutateOnceAsync, checklistAuditId,
                "offboarding.checklist_updated", tenantId, ct);
        }
        catch (OffboardingSafetyException ex)
        {
            _db.ChangeTracker.Clear();
            return Conflict(new { error = ex.Error, message = ex.Message });
        }

        if (refusal is not null) return refusal;
        var committed = await _db.EmployeeOffboardings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        return committed is null ? NotFound() : Ok(committed);
    }

    /// <summary>
    /// S2-B2 — revoke a leaver's system access on their last working day, without waiting for the
    /// settlement. The same effect the checklist tickbox now has, as an explicit, nameable action.
    /// Idempotent.
    /// </summary>
    [HttpPost("{id:guid}/revoke-access")]
    [HasPermission("employees.approve")]
    public async Task<IActionResult> RevokeAccess(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var employeeId = await ResolveOffboardingEmployeeIdAsync(tenantId, id, ct);
        if (employeeId is null) return NotFound();

        var changedAtUtc = DateTime.UtcNow;
        var auditId = Guid.NewGuid();
        IActionResult? refusal = null;
        var alreadyRevoked = false;

        async Task<bool> MutateOnceAsync(CancellationToken token)
        {
            _db.ChangeTracker.Clear();
            var graph = await LockOffboardingGraphAsync(tenantId, id, employeeId.Value, token);
            if (graph is null)
            {
                refusal = NotFound();
                return false;
            }
            if (graph.Offboarding.AccessRevoked)
            {
                alreadyRevoked = true;
                return false;
            }
            if (graph.Offboarding.Status is not ("InProgress" or "Completed"))
            {
                refusal = Conflict(new
                {
                    error = "offboarding_not_active",
                    message = "Only a live offboarding can revoke access."
                });
                return false;
            }

            EnsureAnotherAdministratorSurvives(graph, changedAtUtc);
            StageAccessRevocation(graph, this.GetUserId(), unlinkAccount: false, changedAtUtc,
                HttpContext.Connection.RemoteIpAddress?.ToString());
            graph.Offboarding.UpdatedAtUtc = changedAtUtc;
            _db.AuditLogs.Add(CreateOffboardingAudit(
                auditId, changedAtUtc, "offboarding.access_revoked", graph,
                new { source = "explicit", userIds = graph.TargetUsers.Select(x => x.Id).ToArray() }));
            await _db.SaveChangesAsync(token);
            return true;
        }

        try
        {
            await ExecuteAtomicMutationAsync(MutateOnceAsync, auditId,
                "offboarding.access_revoked", tenantId, ct);
        }
        catch (OffboardingSafetyException ex)
        {
            _db.ChangeTracker.Clear();
            return Conflict(new { error = ex.Error, message = ex.Message });
        }

        if (refusal is not null) return refusal;
        var committed = await _db.EmployeeOffboardings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (committed is null) return NotFound();
        return Ok(new
        {
            committed.Id,
            committed.AccessRevoked,
            committed.AccessRevokedAtUtc,
            alreadyRevoked
        });
    }

    /// <summary>Finalise: archive the employee (removes them from live headcount).</summary>
    [HttpPost("{id:guid}/complete")]
    [HasPermission("employees.approve")]
    public Task<IActionResult> Complete(Guid id, CancellationToken ct) => CompleteAtomicAsync(id, ct);

    private async Task<IActionResult> CompleteAtomicAsync(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var employeeId = await ResolveOffboardingEmployeeIdAsync(tenantId, id, ct);
        if (employeeId is null) return NotFound();

        var completedAtUtc = DateTime.UtcNow;
        var auditId = Guid.NewGuid();
        IActionResult? refusal = null;

        async Task<bool> MutateOnceAsync(CancellationToken token)
        {
            _db.ChangeTracker.Clear();
            var graph = await LockOffboardingGraphAsync(tenantId, id, employeeId.Value, token);
            if (graph is null)
            {
                refusal = NotFound();
                return false;
            }
            var off = graph.Offboarding;
            if (off.Status != "InProgress")
            {
                refusal = BadRequest(new { message = "Offboarding is not in progress." });
                return false;
            }

            var today = DateOnly.FromDateTime(completedAtUtc);
            if (off.LastWorkingDay == default || off.LastWorkingDay > today)
            {
                refusal = Conflict(new
                {
                    error = "last_working_day_not_reached",
                    message = off.LastWorkingDay == default
                        ? "A valid last working day is required before offboarding can be completed."
                        : $"Offboarding cannot be completed before the last working day ({off.LastWorkingDay:yyyy-MM-dd})."
                });
                return false;
            }
            if (!off.AssetsReturned || !off.KnowledgeHandover
                || off.ExitInterviewStatus is not ("Completed" or "Waived"))
            {
                refusal = Conflict(new
                {
                    error = "offboarding_checklist_incomplete",
                    message = "Complete asset return and knowledge handover, and complete or waive the exit interview before archiving the employee.",
                    off.AssetsReturned,
                    off.KnowledgeHandover,
                    off.ExitInterviewStatus,
                });
                return false;
            }

            var settlements = await _db.EmployeeFinalSettlements
                .TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(s => s.TenantId == tenantId && s.OffboardingId == id && s.EmployeeId == employeeId.Value)
                .OrderBy(s => s.Id)
                .ToListAsync(token);
            if (!settlements.Any(s => s.Status == FinalSettlementStatuses.Paid))
            {
                refusal = Conflict(new
                {
                    error = "final_settlement_not_paid",
                    message = "The authoritative final settlement must be paid before offboarding can be completed. "
                            + "Disburse it through a payroll run, or record an approved external payment first "
                            + $"(POST /api/offboarding/{off.Id}/settlement/external-payment)."
                });
                return false;
            }

            EnsureAnotherAdministratorSurvives(graph, completedAtUtc);
            off.FinalSettlementDone = true;
            off.Status = "Completed";
            off.CompletedAtUtc = completedAtUtc;
            off.UpdatedAtUtc = completedAtUtc;
            graph.Employee.Status = "Archived";
            graph.Employee.UpdatedAtUtc = completedAtUtc;
            StageAccessRevocation(graph, this.GetUserId(), unlinkAccount: true, completedAtUtc,
                HttpContext.Connection.RemoteIpAddress?.ToString());

            var context = new RequestContext(HttpContext.Connection.RemoteIpAddress?.ToString(),
                Request.Headers.UserAgent.ToString(), this.GetUserId(), tenantId);
            var footprint = await EmployeeManagementService.StagePayrollFootprintDeactivationAsync(
                _db, tenantId, employeeId.Value, deactivateSalaryStructure: true, context, token);
            _db.AuditLogs.Add(CreateOffboardingAudit(
                auditId, completedAtUtc, "offboarding.completed", graph,
                new
                {
                    userIds = graph.TargetUsers.Select(x => x.Id).ToArray(),
                    payrollProfilesDeactivated = footprint.Profiles,
                    salaryStructuresDeactivated = footprint.SalaryStructures
                }));
            await _db.SaveChangesAsync(token);
            return true;
        }

        try
        {
            await ExecuteAtomicMutationAsync(MutateOnceAsync, auditId,
                "offboarding.completed", tenantId, ct);
        }
        catch (OffboardingSafetyException ex)
        {
            _db.ChangeTracker.Clear();
            return Conflict(new { error = ex.Error, message = ex.Message });
        }

        if (refusal is not null) return refusal;
        var committed = await _db.EmployeeOffboardings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        return committed is null ? NotFound() : Ok(committed);
    }

    [NonAction]
    private async Task<IActionResult> CompleteLegacyAsync(Guid id, CancellationToken ct)
    {
        var off = await Find(id, ct);
        if (off is null) return NotFound();
        if (off.Status != "InProgress") return BadRequest(new { message = "Offboarding is not in progress." });

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (off.LastWorkingDay == default || off.LastWorkingDay > today)
            return Conflict(new
            {
                error = "last_working_day_not_reached",
                message = off.LastWorkingDay == default
                    ? "A valid last working day is required before offboarding can be completed."
                    : $"Offboarding cannot be completed before the last working day ({off.LastWorkingDay:yyyy-MM-dd})."
            });
        if (!off.AssetsReturned || !off.KnowledgeHandover
            || off.ExitInterviewStatus is not ("Completed" or "Waived"))
            return Conflict(new
            {
                error = "offboarding_checklist_incomplete",
                message = "Complete asset return and knowledge handover, and complete or waive the exit interview before archiving the employee.",
                off.AssetsReturned,
                off.KnowledgeHandover,
                off.ExitInterviewStatus,
            });

        // ── S2-B2 — the settlement must be DISCHARGED, by either rail ───────────────────────────────
        // This gate used to require Status == Paid, which only a payroll-run disbursement can produce. A
        // client who settles one mid-month leaver by bank transfer or cheque — the normal GCC practice
        // inside the KSA Art. 88 window — could therefore NEVER complete the offboarding: it sat
        // InProgress forever, the employee stayed Offboarded (an occupying status, so headcount and the
        // staffing budget were wrong for the whole window) and the "offboard end to end" UAT script
        // stopped at step 6. `Paid` is still the bar; an out-of-payroll discharge now also reaches it,
        // and it reaches it by posting the same journal, not by ticking a box (see
        // FinalSettlementExternalDischarge).
        var paidSettlement = await _db.EmployeeFinalSettlements.AsNoTracking()
            .AnyAsync(s => s.TenantId == off.TenantId
                        && s.OffboardingId == off.Id
                        && s.EmployeeId == off.EmployeeId
                        && s.Status == FinalSettlementStatuses.Paid, ct);
        if (!paidSettlement)
            return Conflict(new
            {
                error = "final_settlement_not_paid",
                message = "The authoritative final settlement must be paid before offboarding can be completed. "
                        + "Disburse it through a payroll run, or — if it was paid by bank transfer, cheque or cash "
                        + $"— record that payment first (POST /api/offboarding/{off.Id}/settlement/external-payment).",
            });
        off.FinalSettlementDone = true;

        off.Status = "Completed";
        off.CompletedAtUtc = DateTime.UtcNow;
        off.UpdatedAtUtc = DateTime.UtcNow;
        var emp = await _db.Employees.FirstOrDefaultAsync(e => e.Id == off.EmployeeId && e.TenantId == off.TenantId, ct);
        if (emp is not null)
        {
            emp.Status = "Archived";
            emp.UpdatedAtUtc = DateTime.UtcNow;
            // Still unconditional and still unlinks: archiving IS the end of the employment, and the
            // revocation is idempotent when the checklist already ran it.
            await RevokeEmployeeAccessAsync(emp, this.GetUserId(), unlinkAccount: true, ct);
            if (!off.AccessRevoked)
            {
                off.AccessRevoked = true;
                off.AccessRevokedAtUtc = DateTime.UtcNow;
                off.AccessRevokedByUserId = this.GetUserId();
            }
        }
        await _db.SaveChangesAsync(ct);

        // EXIT CASCADE (offboarding complete → Archived): always deactivate WPS eligibility. Deactivate
        // the salary STRUCTURE only once final settlement is recorded done — otherwise a still-pending
        // EOSB / final-settlement calculation (which reads the active salary row) would break. WPS-off
        // is always safe. Idempotent; the employee record itself is untouched (retention preserved).
        if (emp is not null)
        {
            var ctx = new RequestContext(HttpContext.Connection.RemoteIpAddress?.ToString(),
                Request.Headers.UserAgent.ToString(), this.GetUserId(), off.TenantId);
            await EmployeeManagementService.DeactivatePayrollFootprintAsync(
                _db, _audit, off.TenantId, off.EmployeeId, "offboarding_completed",
                deactivateSalaryStructure: off.FinalSettlementDone, ctx, ct);
        }
        return Ok(off);
    }

    /// <summary>
    /// S2-B2 — record a final settlement that was PAID OUTSIDE PAYROLL (bank transfer, cheque, cash).
    ///
    /// <para>Requires <c>payroll.approve</c>, NOT the HR write permission: asserting that money left the
    /// company's bank account is a finance act, and the person who runs the offboarding checklist is not
    /// automatically the person who can make it. The discharge posts the DR payable / CR cash journal, so
    /// 2320 Final Settlement Payable closes to zero exactly as it does on the payroll rail — a flag alone
    /// would leave a liability on the books that no longer exists.</para>
    /// </summary>
    [HttpPost("{id:guid}/settlement/external-payment")]
    [HasPermission("payroll.approve")]
    public async Task<IActionResult> RecordExternalSettlementPayment(
        Guid id, [FromBody] ExternalSettlementPaymentRequest req, CancellationToken ct)
    {
        var off = await Find(id, ct);
        if (off is null) return NotFound();

        if (!Infrastructure.Payroll.FinalSettlementExternalDischarge.TryNormalizeMethod(req.Method, out var method))
            return BadRequest(new
            {
                error   = "unknown_payment_method",
                message = $"'{req.Method}' is not a recognised payment method.",
                allowed = Infrastructure.Payroll.FinalSettlementExternalDischarge.Methods,
            });
        if (string.IsNullOrWhiteSpace(req.Reference))
            return BadRequest(new
            {
                error   = "payment_reference_required",
                message = "A bank reference or cheque number is required — it is the evidence that the money moved, "
                        + "and it is what replaces the payroll run's payment batch in the audit trail.",
            });

        var settlement = await _db.EmployeeFinalSettlements
            .FirstOrDefaultAsync(s => s.TenantId == off.TenantId && s.OffboardingId == off.Id
                                   && s.EmployeeId == off.EmployeeId
                                   && s.Status != FinalSettlementStatuses.Cancelled, ct);
        if (settlement is null)
            return Conflict(new
            {
                error   = "no_settlement",
                message = "There is no live final settlement for this offboarding. Compute and approve the "
                        + "settlement first — the amount paid has to be the one the system determined.",
            });

        var paidOn = req.PaidOn ?? DateOnly.FromDateTime(DateTime.UtcNow);
        // The amount is CONFIRMED against the settlement, never taken from the caller: a settlement
        // recorded as paid for a figure the system did not compute is how an underpayment becomes
        // evidenced by the employer's own signed record.
        if (Math.Abs(req.Amount - Math.Round(settlement.NetPayable, 2)) > 0.01m)
            return UnprocessableEntity(new
            {
                error   = "amount_does_not_match_settlement",
                message = $"The amount paid ({req.Amount:N2}) does not match the settlement's net payable "
                        + $"({settlement.NetPayable:N2}). Correct the payment record, or cancel and recompute the "
                        + "settlement if the figure itself is wrong.",
                netPayable = settlement.NetPayable,
            });

        var (discharge, refusal) = await Infrastructure.Payroll.FinalSettlementExternalDischarge.StageAsync(
            _db, settlement, paidOn, method, req.Reference.Trim(), this.GetUserId(), GetActorName(), ct);
        if (refusal is not null)
            return UnprocessableEntity(new { error = refusal.Error, message = refusal.Message });

        settlement.Status = FinalSettlementStatuses.Paid;
        settlement.PaidAtUtc = DateTime.UtcNow;
        settlement.PaidOutsidePayroll = true;
        settlement.ExternalPaymentMethod = method;
        settlement.ExternalPaymentReference = req.Reference.Trim();
        settlement.ExternalPaymentDate = paidOn;
        settlement.ExternalPaymentRecordedByUserId = this.GetUserId();
        settlement.ExternalPaymentRecordedByName = GetActorName();
        settlement.UpdatedAtUtc = DateTime.UtcNow;

        off.FinalSettlementDone = true;
        off.UpdatedAtUtc = DateTime.UtcNow;

        var ctx = new RequestContext(HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString(), this.GetUserId(), off.TenantId);
        await _audit.WriteAsync("payroll.final_settlement.paid_outside_payroll", "EmployeeFinalSettlement",
            settlement.Id.ToString(), ctx, System.Text.Json.JsonSerializer.Serialize(new
            {
                offboardingId = off.Id, settlement.EmployeeId, settlement.EmployeeCode,
                settlement.NetPayable, settlement.Currency, method, reference = req.Reference.Trim(),
                paidOn, period = discharge!.Period,
                payableCleared = discharge.PayableCleared, payableAccount = discharge.PayableAccount,
            }), ct);

        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            settlementId  = settlement.Id,
            status        = settlement.Status,
            paidOutsidePayroll = true,
            method, reference = settlement.ExternalPaymentReference, paidOn,
            period        = discharge.Period,
            payableCleared = discharge.PayableCleared,
            journal = discharge.Journal.Select(l => new
            {
                l.EventType, debit = l.DebitAccount, credit = l.CreditAccount, l.Amount, l.Description,
            }).ToList(),
            nextStep = "The payable is discharged. Complete the offboarding to archive the employee.",
        });
    }

    /// <summary>Rescind a resignation while serving notice — reinstates the employee.</summary>
    [HttpPost("{id:guid}/cancel")]
    [HasPermission("employees.approve")]
    public Task<IActionResult> Cancel(
        Guid id,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)]
        CancelOffboardingRequest? req,
        CancellationToken ct) => CancelAtomicAsync(id, req, ct);

    private async Task<IActionResult> CancelAtomicAsync(
        Guid id, CancelOffboardingRequest? req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var employeeId = await ResolveOffboardingEmployeeIdAsync(tenantId, id, ct);
        if (employeeId is null) return NotFound();

        var cancelledAtUtc = DateTime.UtcNow;
        var auditId = Guid.NewGuid();
        IActionResult? refusal = null;
        var backfillWithdrawn = false;
        var accessReprovisioningRequired = false;

        async Task<bool> MutateOnceAsync(CancellationToken token)
        {
            _db.ChangeTracker.Clear();
            var graph = await LockOffboardingGraphAsync(tenantId, id, employeeId.Value, token);
            if (graph is null)
            {
                refusal = NotFound();
                return false;
            }
            var off = graph.Offboarding;
            if (off.Status != "InProgress")
            {
                refusal = BadRequest(new { message = "Only an in-progress offboarding can be cancelled." });
                return false;
            }
            if (graph.Employee.Status != "Offboarded")
                throw new OffboardingSafetyException(
                    "employee_lifecycle_conflict",
                    $"The employee is '{graph.Employee.Status}', not 'Offboarded'. Resolve the lifecycle conflict before rescinding.");

            var liveSettlement = await _db.EmployeeFinalSettlements
                .TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(s => s.TenantId == tenantId && s.OffboardingId == id
                         && s.Status != FinalSettlementStatuses.Cancelled)
                .OrderBy(s => s.Id)
                .Select(s => new { s.Id, s.Status, s.NetPayable })
                .FirstOrDefaultAsync(token);
            if (liveSettlement is not null)
            {
                refusal = Conflict(new
                {
                    error = "final_settlement_live",
                    message = $"A final settlement for this separation exists in '{liveSettlement.Status}' status "
                            + $"({liveSettlement.NetPayable:N2}). Cancel that settlement before rescinding the offboarding.",
                    settlementId = liveSettlement.Id,
                    settlementStatus = liveSettlement.Status,
                });
                return false;
            }

            var context = new RequestContext(HttpContext.Connection.RemoteIpAddress?.ToString(),
                Request.Headers.UserAgent.ToString(), this.GetUserId(), tenantId);
            if (_activationGuard.ShouldGate(graph.Employee.Status, "Active"))
            {
                var snapshot = await _activationGuard.BuildSnapshotAsync(tenantId, graph.Employee.Id, token);
                if (snapshot is not null)
                    await _activationGuard.EnsureActivatableAsync(
                        tenantId, graph.Employee.CompanyId, snapshot, context, token);
            }

            graph.Employee.Status = "Active";
            graph.Employee.UpdatedAtUtc = cancelledAtUtc;
            off.Status = "Cancelled";
            off.UpdatedAtUtc = cancelledAtUtc;
            off.CancelledAtUtc = cancelledAtUtc;
            off.CancelledByUserId = this.GetUserId();
            off.CancelReason = req?.Reason?.Trim();

            // A rescind restores employment only. Revoked credentials remain fail-closed; access is
            // re-issued through the invitation workflow so roles, scope and MFA are freshly approved.
            accessReprovisioningRequired = off.AccessRevoked;

            backfillWithdrawn = false;
            if (off.BackfillRequisitionId is Guid requisitionId)
            {
                var requisition = await _db.ManpowerRequisitions
                    .TagWith(RowLockingInterceptor.ForUpdateTag)
                    .SingleOrDefaultAsync(x => x.Id == requisitionId && x.TenantId == tenantId, token);
                if (requisition is not null && requisition.Status is "Draft" or "Pending" or "Submitted")
                {
                    requisition.Status = "Cancelled";
                    backfillWithdrawn = true;
                }
            }

            _db.AuditLogs.Add(CreateOffboardingAudit(
                auditId, cancelledAtUtc, "offboarding.rescinded", graph,
                new
                {
                    reason = off.CancelReason,
                    accessRestored = false,
                    accessReprovisioningRequired,
                    backfillWithdrawn
                }));
            await _db.SaveChangesAsync(token);
            return true;
        }

        try
        {
            await ExecuteAtomicMutationAsync(MutateOnceAsync, auditId,
                "offboarding.rescinded", tenantId, ct);
        }
        catch (EmployeeActivationBlockedException ex)
        {
            _db.ChangeTracker.Clear();
            return this.NotActivatable(ex);
        }
        catch (OffboardingSafetyException ex)
        {
            _db.ChangeTracker.Clear();
            return Conflict(new { error = ex.Error, message = ex.Message });
        }

        if (refusal is not null) return refusal;
        var committed = await _db.EmployeeOffboardings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (committed is null) return NotFound();
        return Ok(new
        {
            offboarding = committed,
            accessRestored = false,
            accessReprovisioningRequired,
            backfillWithdrawn,
            nextStep = accessReprovisioningRequired
                ? "Issue a new employee login invitation after approving roles and entity scope."
                : null
        });
    }

    [NonAction]
    private async Task<IActionResult> CancelLegacyAsync(
        Guid id,
        CancelOffboardingRequest? req,
        CancellationToken ct)
    {
        var off = await Find(id, ct);
        if (off is null) return NotFound();
        if (off.Status != "InProgress") return BadRequest(new { message = "Only an in-progress offboarding can be cancelled." });
        // ── POD-C1 — a LIVE SETTLEMENT blocks the rescind ────────────────────────────────────────────
        // The settlement pipeline reads the offboarding once, at creation, and keys its uniqueness on it.
        // Cancelling it underneath an Approved settlement would leave a real payable (2320 credited, and
        // possibly already disbursed) attached to a separation the system says never happened — and it
        // would silently re-activate an employee the payroll has already paid out and de-registered.
        var liveSettlement = await _db.EmployeeFinalSettlements.AsNoTracking()
            .Where(s => s.TenantId == off.TenantId && s.OffboardingId == off.Id
                     && s.Status != FinalSettlementStatuses.Cancelled)
            .Select(s => new { s.Id, s.Status, s.NetPayable })
            .FirstOrDefaultAsync(ct);
        if (liveSettlement is not null)
            return Conflict(new
            {
                error   = "final_settlement_live",
                message = $"A final settlement for this separation exists in '{liveSettlement.Status}' status " +
                          $"({liveSettlement.NetPayable:N2}). Cancel the settlement first " +
                          $"(POST /api/payroll/final-settlements/{liveSettlement.Id}/cancel), which contras its GL " +
                          "accrual, before rescinding the offboarding.",
                settlementId = liveSettlement.Id,
                settlementStatus = liveSettlement.Status,
            });
        off.Status = "Cancelled";
        off.UpdatedAtUtc = DateTime.UtcNow;
        var accessRestored = false;
        var emp = await _db.Employees.FirstOrDefaultAsync(e => e.Id == off.EmployeeId && e.TenantId == off.TenantId, ct);
        if (emp is not null)
        {
            // 4th Active path (§5.3): rescind-resignation reinstates the employee. Route through the
            // SHARED readiness gate rather than a raw write (guard-parity). Old status is Offboarded —
            // an OCCUPYING status — so the §5.2 gate auto-exempts (a mandated reinstatement is never
            // refused); the guard is still consulted so no Active write bypasses it.
            var ctx = new RequestContext(HttpContext.Connection.RemoteIpAddress?.ToString(),
                Request.Headers.UserAgent.ToString(), this.GetUserId(), off.TenantId);
            try
            {
                if (_activationGuard.ShouldGate(emp.Status, "Active"))
                {
                    var snap = await _activationGuard.BuildSnapshotAsync(off.TenantId, emp.Id, ct);
                    if (snap is not null)
                        await _activationGuard.EnsureActivatableAsync(off.TenantId, emp.CompanyId, snap, ctx, ct);
                }
            }
            catch (EmployeeActivationBlockedException ex)
            {
                _db.ChangeTracker.Clear();
                await _audit.WriteAsync("employee.activation_blocked", "Employee", emp.Id.ToString(), ctx, null, ct);
                return this.NotActivatable(ex);
            }
            emp.Status = "Active";
            emp.UpdatedAtUtc = DateTime.UtcNow;

            // S2-B2: the checklist may already have revoked this person's login. A withdrawn resignation
            // means they are still employed, so the access has to come back — deliberately, and only
            // here, where an actor and a reason are recorded against it.
            if (off.AccessRevoked)
            {
                accessRestored = await RestoreEmployeeAccessAsync(emp, this.GetUserId(), ct);
                off.AccessRevoked = false;
                off.AccessRevokedAtUtc = null;
                off.AccessRevokedByUserId = null;
            }
        }

        // ── S2-F4 — a rescind used to leave NO trace ────────────────────────────────────────────────
        // No CancelledBy, no CancelledAt, no reason, and no audit row: a withdrawn resignation that
        // reinstates an employee and re-grants their login was unattributable. It also orphaned the
        // backfill requisition raised at initiate, which is now withdrawn with it.
        off.CancelledAtUtc = DateTime.UtcNow;
        off.CancelledByUserId = this.GetUserId();
        off.CancelReason = req?.Reason?.Trim();

        var backfillWithdrawn = false;
        if (off.BackfillRequisitionId is Guid mrqId)
        {
            var mrq = await _db.ManpowerRequisitions
                .FirstOrDefaultAsync(r => r.Id == mrqId && r.TenantId == off.TenantId, ct);
            // Only a requisition nobody has acted on yet: once it is approved or being recruited against,
            // withdrawing it silently would destroy work that is genuinely in flight.
            if (mrq is not null && mrq.Status is "Draft" or "Pending" or "Submitted")
            {
                mrq.Status = "Cancelled";
                backfillWithdrawn = true;
            }
        }

        var cctx = new RequestContext(HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString(), this.GetUserId(), off.TenantId);
        await _audit.WriteAsync("offboarding.rescinded", "EmployeeOffboarding", off.Id.ToString(), cctx,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                off.EmployeeId, off.EmployeeCode, off.SeparationType,
                reason = off.CancelReason, accessRestored, backfillWithdrawn,
            }), ct);

        await _db.SaveChangesAsync(ct);
        return Ok(new { offboarding = off, accessRestored, backfillWithdrawn });
    }

    private async Task<int?> ResolveOffboardingEmployeeIdAsync(
        Guid tenantId, Guid offboardingId, CancellationToken ct) =>
        await _db.EmployeeOffboardings.AsNoTracking()
            .Where(x => x.Id == offboardingId && x.TenantId == tenantId)
            .Select(x => (int?)x.EmployeeId)
            .SingleOrDefaultAsync(ct);

    /// <summary>
    /// Locks the complete authentication graph in the common lifecycle order: tenant, employee,
    /// employee links, sorted users, then credential artifacts. Every offboarding exit/rescind writer
    /// uses this primitive, so a refresh/MFA completion cannot commit against a half-transitioned user.
    /// </summary>
    private async Task<LockedOffboardingGraph?> LockOffboardingGraphAsync(
        Guid tenantId, Guid offboardingId, int employeeId, CancellationToken ct)
    {
        var tenant = await _db.Tenants.TagWith(RowLockingInterceptor.ForUpdateTag)
            .SingleOrDefaultAsync(x => x.Id == tenantId, ct);
        if (tenant is null) return null;

        var employee = await _db.Employees.IgnoreQueryFilters()
            .TagWith(RowLockingInterceptor.ForUpdateTag)
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == employeeId && !x.IsDeleted, ct);
        if (employee is null) return null;

        var offboarding = await _db.EmployeeOffboardings
            .TagWith(RowLockingInterceptor.ForUpdateTag)
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == offboardingId, ct);
        if (offboarding is null) return null;
        if (offboarding.EmployeeId != employeeId)
            throw new OffboardingSafetyException(
                "offboarding_employee_mismatch",
                "The offboarding record changed employee identity during the operation.");

        var targetLinks = await _db.EmployeeUserAccounts.IgnoreQueryFilters()
            .TagWith(RowLockingInterceptor.ForUpdateTag)
            .Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId && !x.IsDeleted)
            .OrderBy(x => x.Id)
            .ToListAsync(ct);
        var targetUserIds = targetLinks.Where(x => x.UserId.HasValue).Select(x => x.UserId!.Value)
            .Append(employee.UserAccountId ?? Guid.Empty)
            .Where(x => x != Guid.Empty)
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        // Tenant writers take the tenant anchor before role changes, so this cohort cannot shrink while
        // the exit is deciding whether it would remove the final operational administrator.
        var administratorIds = await _db.UserRoles.AsNoTracking()
            .Where(x => x.User != null && x.User.TenantId == tenantId && !x.User.IsDeleted
                     && x.Role != null && x.Role.NormalizedName == "ADMIN"
                     && x.Role.IsActive && !x.Role.IsDeleted
                     && (x.Role.TenantId == tenantId || x.Role.TenantId == null))
            .Select(x => x.UserId)
            .Distinct()
            .ToListAsync(ct);
        var allUserIds = targetUserIds.Concat(administratorIds).Distinct().OrderBy(x => x).ToList();

        if (allUserIds.Count > 0)
        {
            await _db.EmployeeUserAccounts.IgnoreQueryFilters()
                .TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => x.TenantId == tenantId && x.UserId.HasValue && allUserIds.Contains(x.UserId.Value))
                .OrderBy(x => x.UserId).ThenBy(x => x.Id)
                .Select(x => x.Id)
                .ToListAsync(ct);
            await _db.Users.IgnoreQueryFilters().TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => x.TenantId == tenantId && allUserIds.Contains(x.Id))
                .OrderBy(x => x.Id)
                .Select(x => x.Id)
                .ToListAsync(ct);
            await _db.UserRoles.TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => allUserIds.Contains(x.UserId))
                .OrderBy(x => x.UserId).ThenBy(x => x.RoleId)
                .Select(x => new { x.UserId, x.RoleId })
                .ToListAsync(ct);
        }

        List<User> cohort = allUserIds.Count == 0
            ? []
            : await _db.Users.IgnoreQueryFilters()
                .Include(x => x.UserRoles).ThenInclude(x => x.Role)
                .Include(x => x.EmployeeUserAccounts)
                .Where(x => x.TenantId == tenantId && allUserIds.Contains(x.Id))
                .OrderBy(x => x.Id)
                .ToListAsync(ct);
        var targetUsers = cohort.Where(x => targetUserIds.Contains(x.Id)).OrderBy(x => x.Id).ToList();
        if (targetUsers.Count != targetUserIds.Count)
            throw new OffboardingSafetyException(
                "identity_graph_inconsistent",
                "An employee login link points to a missing or cross-tenant identity. Access was not changed.");
        if (targetUsers.Any(user => user.EmployeeUserAccounts.Any(link =>
                !link.IsDeleted && link.TenantId == tenantId && link.EmployeeId != employeeId)))
            throw new OffboardingSafetyException(
                "shared_identity_requires_review",
                "A login identity is linked to another employee. Resolve the identity graph before offboarding.");

        List<PasswordResetToken> passwordResets = targetUserIds.Count == 0
            ? []
            : await _db.PasswordResetTokens.TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => targetUserIds.Contains(x.UserId) && x.UsedAtUtc == null)
                .OrderBy(x => x.Id)
                .ToListAsync(ct);
        List<MfaChallengeToken> mfaChallenges = targetUserIds.Count == 0
            ? []
            : await _db.MfaChallengeTokens.IgnoreQueryFilters().TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => x.TenantId == tenantId && x.UserId.HasValue
                         && targetUserIds.Contains(x.UserId.Value) && x.UsedAtUtc == null)
                .OrderBy(x => x.Id)
                .ToListAsync(ct);
        List<RefreshToken> refreshTokens = targetUserIds.Count == 0
            ? []
            : await _db.RefreshTokens.TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => targetUserIds.Contains(x.UserId) && x.RevokedAtUtc == null)
                .OrderBy(x => x.Id)
                .ToListAsync(ct);

        return new LockedOffboardingGraph(
            offboarding, employee, targetLinks, targetUsers, cohort,
            passwordResets, mfaChallenges, refreshTokens);
    }

    private void StageAccessRevocation(
        LockedOffboardingGraph graph,
        Guid? actorUserId,
        bool unlinkAccount,
        DateTime changedAtUtc,
        string? actorIp)
    {
        foreach (var user in graph.TargetUsers)
        {
            user.IsActive = false;
            user.IsEmailConfirmed = false;
            // PendingPasswordSetup remains non-operational but is the only state accepted by the
            // controlled invitation workflow after a rescind. The employee lifecycle still blocks an
            // invitation while they are Offboarded/Archived.
            user.Status = "PendingPasswordSetup";
            user.AccessMode = AccessModes.NoLogin;
            user.PasswordHash = $"OFFBOARDED${user.Id:N}";
            user.MustChangePassword = false;
            user.IsLocked = false;
            user.LockoutEnd = null;
            user.FailedLoginCount = 0;
            user.MFAEnabled = false;
            user.MfaSecretEncrypted = null;
            user.MfaConfiguredAtUtc = null;
            user.MfaLastVerifiedAtUtc = null;
            user.MfaFailedCount = 0;
            TenantSessionSecurity.RotateStamp(user, changedAtUtc);
        }

        foreach (var link in graph.TargetLinks)
        {
            link.AccessMode = AccessModes.NoLogin;
            link.Status = "NoLogin";
            link.RequiresPasswordSetup = false;
            link.InvitationTokenHash = string.Empty;
            link.InvitationExpiresAtUtc = null;
            link.InvitationAcceptedAtUtc = null;
            link.LoginDisabledReason = unlinkAccount
                ? "Offboarding completed"
                : "Offboarding — access revoked";
            link.UpdatedAtUtc = changedAtUtc;
            link.UpdatedBy = actorUserId;
        }
        foreach (var reset in graph.PasswordResetTokens)
            reset.UsedAtUtc = changedAtUtc;
        foreach (var challenge in graph.MfaChallenges)
            challenge.UsedAtUtc = changedAtUtc;
        foreach (var refresh in graph.RefreshTokens)
        {
            refresh.RevokedAtUtc = changedAtUtc;
            refresh.RevokedByIp = actorIp;
        }

        if (unlinkAccount) graph.Employee.UserAccountId = null;
        graph.Offboarding.AccessRevoked = true;
        graph.Offboarding.AccessRevokedAtUtc ??= changedAtUtc;
        graph.Offboarding.AccessRevokedByUserId ??= actorUserId;
    }

    private static void EnsureAnotherAdministratorSurvives(
        LockedOffboardingGraph graph, DateTime atUtc)
    {
        var targetIds = graph.TargetUsers.Select(x => x.Id).ToHashSet();
        var removesAdministrator = graph.TargetUsers.Any(IsAdministrator);
        if (!removesAdministrator) return;
        if (graph.AdministratorCohort.Any(x => !targetIds.Contains(x.Id) && IsOperationalAdministrator(x, atUtc)))
            return;
        throw new OffboardingSafetyException(
            "last_administrator",
            "This offboarding would disable the tenant's last operational administrator. Assign another administrator first.");
    }

    private static bool IsAdministrator(User user) =>
        user.UserRoles.Any(x => x.Role is
        {
            NormalizedName: "ADMIN",
            IsActive: true,
            IsDeleted: false
        });

    private static bool IsOperationalAdministrator(User user, DateTime atUtc)
    {
        if (!IsAdministrator(user)
            || user.IsDeleted
            || !user.IsActive
            || !user.IsEmailConfirmed
            || user.Status != "Active"
            || user.MustChangePassword
            || user.AccessMode == AccessModes.NoLogin
            || (user.IsLocked && (!user.LockoutEnd.HasValue || user.LockoutEnd > atUtc))
            || (user.LockoutEnd.HasValue && user.LockoutEnd > atUtc))
            return false;
        var primary = user.EmployeeUserAccounts.Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.IsPrimary).ThenByDescending(x => x.CreatedAtUtc).FirstOrDefault();
        return primary?.AccessMode != AccessModes.NoLogin && primary?.RequiresPasswordSetup != true;
    }

    private AuditLog CreateOffboardingAudit(
        Guid auditId,
        DateTime createdAtUtc,
        string action,
        LockedOffboardingGraph graph,
        object details) =>
        AuthAuditEntry.Create(
            auditId,
            createdAtUtc,
            action,
            "EmployeeOffboarding",
            graph.Offboarding.Id.ToString(),
            new RequestContext(
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                Request.Headers.UserAgent.ToString(),
                this.GetUserId(),
                graph.Offboarding.TenantId),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                graph.Offboarding.EmployeeId,
                graph.Offboarding.EmployeeCode,
                details
            }));

    private async Task ExecuteAtomicMutationAsync(
        Func<CancellationToken, Task<bool>> operation,
        Guid auditId,
        string auditAction,
        Guid tenantId,
        CancellationToken ct)
    {
        if (!_db.Database.IsRelational())
        {
            await operation(ct);
            return;
        }

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteInTransactionAsync(
            operation,
            async token => await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(x => x.Id == auditId && x.TenantId == tenantId && x.Action == auditAction, token),
            IsolationLevel.ReadCommitted,
            ct);
    }

    private sealed record LockedOffboardingGraph(
        EmployeeOffboarding Offboarding,
        Employee Employee,
        IReadOnlyList<EmployeeUserAccount> TargetLinks,
        IReadOnlyList<User> TargetUsers,
        IReadOnlyList<User> AdministratorCohort,
        IReadOnlyList<PasswordResetToken> PasswordResetTokens,
        IReadOnlyList<MfaChallengeToken> MfaChallenges,
        IReadOnlyList<RefreshToken> RefreshTokens);

    private sealed class OffboardingSafetyException(string error, string message) : Exception(message)
    {
        public string Error { get; } = error;
    }

    private async Task<EmployeeOffboarding?> Find(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        return await _db.EmployeeOffboardings.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
    }

    private string GetActorName() =>
        User.FindFirst("name")?.Value
        ?? User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value
        ?? "System";

    /// <param name="unlinkAccount">
    /// S2-B2 — whether to clear <c>Employee.UserAccountId</c>. <c>true</c> at Complete (the employment is
    /// over; that is the retention decision the archive step already makes). <c>false</c> when access is
    /// revoked DURING notice: the person is still employed, so the pointer is kept and a rescind can put
    /// the login back. The token revocation and the NoLogin flags are identical either way.
    /// </param>
    private async Task RevokeEmployeeAccessAsync(
        Employee employee, Guid? actorUserId, bool unlinkAccount, CancellationToken ct)
    {
        // Resolve through the link table as well as the pointer, so a second call after a mid-notice
        // revocation (which deliberately keeps the pointer) is still able to find the account.
        var userId = employee.UserAccountId
            ?? await _db.EmployeeUserAccounts
                .Where(l => l.TenantId == employee.TenantId && l.EmployeeId == employee.Id && !l.IsDeleted)
                .Select(l => (Guid?)l.UserId)
                .FirstOrDefaultAsync(ct);
        if (userId is not Guid uid) return;

        var user = await _db.Users
            .Include(u => u.EmployeeUserAccounts)
            .FirstOrDefaultAsync(u => u.Id == uid && u.TenantId == employee.TenantId && !u.IsDeleted, ct);
        if (user is null) return;

        user.IsActive = false;
        user.Status = "Deactivated";
        user.AccessMode = AccessModes.NoLogin;
        user.UpdatedAtUtc = DateTime.UtcNow;
        foreach (var link in user.EmployeeUserAccounts.Where(l => l.TenantId == employee.TenantId && l.EmployeeId == employee.Id && !l.IsDeleted))
        {
            link.AccessMode = AccessModes.NoLogin;
            link.Status = "NoLogin";
            link.RequiresPasswordSetup = false;
            link.LoginDisabledReason = unlinkAccount ? "Offboarding completed" : "Offboarding — access revoked";
            link.UpdatedAtUtc = DateTime.UtcNow;
            link.UpdatedBy = actorUserId;
        }

        var activeTokens = await _db.RefreshTokens
            .Where(t => t.UserId == uid && t.RevokedAtUtc == null)
            .ToListAsync(ct);
        foreach (var token in activeTokens)
            token.RevokedAtUtc = DateTime.UtcNow;

        if (unlinkAccount) employee.UserAccountId = null;
    }

    /// <summary>
    /// S2-B2 — the inverse, used ONLY by rescind. Re-enables the user account and the employee↔user link
    /// that a mid-notice revocation disabled. Revoked refresh tokens are deliberately NOT un-revoked:
    /// the old sessions stay dead and the person signs in again.
    /// </summary>
    private async Task<bool> RestoreEmployeeAccessAsync(Employee employee, Guid? actorUserId, CancellationToken ct)
    {
        var userId = employee.UserAccountId
            ?? await _db.EmployeeUserAccounts
                .Where(l => l.TenantId == employee.TenantId && l.EmployeeId == employee.Id && !l.IsDeleted)
                .Select(l => (Guid?)l.UserId)
                .FirstOrDefaultAsync(ct);
        if (userId is not Guid uid) return false;

        var user = await _db.Users
            .Include(u => u.EmployeeUserAccounts)
            .FirstOrDefaultAsync(u => u.Id == uid && u.TenantId == employee.TenantId && !u.IsDeleted, ct);
        if (user is null) return false;

        user.IsActive = true;
        user.Status = "Active";
        user.AccessMode = AccessModes.FullPortal;
        user.UpdatedAtUtc = DateTime.UtcNow;
        foreach (var link in user.EmployeeUserAccounts.Where(l => l.TenantId == employee.TenantId && l.EmployeeId == employee.Id && !l.IsDeleted))
        {
            link.AccessMode = AccessModes.FullPortal;
            link.Status = "Active";
            link.LoginDisabledReason = string.Empty;
            link.UpdatedAtUtc = DateTime.UtcNow;
            link.UpdatedBy = actorUserId;
        }
        employee.UserAccountId = uid;
        return true;
    }
}

/// <summary>
/// S2-B3 — the separation vocabulary with the labels and consequences an operator needs to choose
/// correctly. The CODES are <c>EmployeeManagementService.AllowedSeparationTypes</c> and nothing else;
/// the parity test asserts the two never drift.
/// </summary>
public static class SeparationTypeCatalog
{
    public sealed record SeparationTypeInfo(
        string Code, string Label, string Description, bool ForfeitsEndOfServiceAward, bool RequiresReason);

    public static readonly IReadOnlyList<SeparationTypeInfo> All =
    [
        new("Resignation", "Resignation",
            "The employee resigned. KSA Art. 85 reduces the end-of-service award by length of service.",
            false, false),
        new("Article87", "Resignation — Art. 87 exception (force majeure / marriage / childbirth)",
            "The employee left owing to force majeure beyond their control, or is a female employee who "
            + "terminated the contract within SIX months of her marriage or THREE months of giving birth. "
            + "Art. 87 is an express exception to Art. 85: the FULL Art. 84 award is paid, with no "
            + "length-of-service reduction. Record the ground and the qualifying date on the offboarding "
            + "file — the product cannot verify the window.",
            false, true),
        new("Article81", "Resignation — Art. 81 (employer at fault)",
            "The employee left without notice on one of the Art. 81 employer-fault grounds and retains "
            + "full statutory rights. No Art. 85 reduction: the FULL Art. 84 award is paid. Record which "
            + "Art. 81 ground is relied on.",
            false, true),
        new("Termination", "Termination by employer",
            "Employer-initiated termination with notice. Full Art. 84 end-of-service award.",
            false, false),
        new("EndOfContract", "End of fixed-term contract",
            "A fixed-term contract expired and was not renewed. Full Art. 84 award.",
            false, false),
        new("Redundancy", "Redundancy",
            "The role was eliminated. Full Art. 84 award.",
            false, false),
        new("Retirement", "Retirement",
            "The employee reached retirement. Full Art. 84 award.",
            false, false),
        new("ProbationFailure", "Probation not passed",
            "Separation during or at the end of probation.",
            false, false),
        new("Death", "Death in service",
            "The employee died in service. The award is payable to the estate / legal heirs.",
            false, false),
        new("Article80", "Article 80 — summary dismissal for cause",
            "Dismissal for one of the grounds in KSA Labour Law Art. 80 (1)–(9). This FORFEITS the "
            + "end-of-service award in full. Recording any other type for a summary dismissal pays the "
            + "full Art. 84 award instead.",
            true, true),
    ];
}

/// <param name="NoticePeriodDays">
/// The CONTRACTUAL notice period. A negative value means "use <c>Employee.NoticePeriodDays</c>" — the
/// number already on the employment record, which this endpoint used to ignore entirely.
/// </param>
/// <param name="LastWorkingDay">
/// The real last working day. Always accepted by the API; the screen used to compute it as
/// notice + calendar days and post it read-only, which made pay-in-lieu, a negotiated early release and
/// an LWD that avoids a weekend all unenterable. Validated against the notice and joining dates.
/// </param>
public record InitiateOffboardingRequest(
    int EmployeeId, string SeparationType, string? Reason,
    DateOnly? NoticeDate, int NoticePeriodDays, DateOnly? LastWorkingDay,
    bool RehireEligible = true, bool RaiseBackfill = true);

/// <summary>S2-F4 — a rescind reinstates an employee and re-grants their login; it is attributable now.</summary>
public record CancelOffboardingRequest(string? Reason);

/// <summary>S2-B2 — evidence that a final settlement was paid outside the payroll rails.</summary>
public record ExternalSettlementPaymentRequest(
    string Method, string Reference, decimal Amount, DateOnly? PaidOn);

public record ExitInterviewRequest(string? Status, DateOnly? Date, string? ReasonCategory, int Rating, string? Notes);
public record OffboardingChecklistRequest(bool? AssetsReturned, bool? AccessRevoked, bool? KnowledgeHandover, bool? FinalSettlementDone);
