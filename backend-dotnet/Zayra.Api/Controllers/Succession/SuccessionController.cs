using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Succession;
using Zayra.Api.Models;
using P = Zayra.Api.Infrastructure.Succession.SuccessionPermissions;

namespace Zayra.Api.Controllers.Succession;

public sealed record RetireInput(string? Reason);
public sealed record SubmitSlateInput(int? Version);

/// <summary>
/// Succession (SUC-01..09) under <c>/api/succession</c>, closed unless the tenant has the succession opt-in flag.
/// Critical positions, pools, nominations, talent review and the 9-box, and slates. Every by-id read outside the caller's
/// reach answers 404, exactly like an id that does not exist. Candidate-level data needs succession.slates.read.
/// </summary>
[ApiController]
[Authorize]
[Route("api/succession")]
public sealed class SuccessionController(SuccessionPositionService positions, SuccessionNominationService nominations,
    TalentAssessmentService assessments, SuccessionSlateService slates, SuccessionReportService reports, ZayraDbContext db) : SuccessionControllerBase
{
    // ── Critical positions ──

    /// <summary>GET /api/succession/positions — the workspace list, gaps first. Counts and gap codes, no candidate names.</summary>
    [HttpGet("positions")]
    [HasPermission(P.Read)]
    public async Task<IActionResult> Positions([FromQuery] string? status, CancellationToken ct) =>
        Ok(await positions.ListAsync(await ActorAsync(ct), status, ct));

    [HttpGet("positions/available")]
    [HasPermission(P.PositionsManage)]
    public async Task<IActionResult> Available(CancellationToken ct) => Ok(await positions.AvailableAsync(await ActorAsync(ct), ct));

    /// <summary>GET /api/succession/positions/mine — the positions the caller is the accountable executive for.</summary>
    [HttpGet("positions/mine")]
    [HasPermission(P.Self, P.Read)]
    public async Task<IActionResult> Mine(CancellationToken ct) => Ok(await positions.MineAsync(await ActorAsync(ct), ct));

    [HttpGet("positions/{id:guid}")]
    [HasPermission(P.Read, P.Self)]
    public async Task<IActionResult> Position(Guid id, CancellationToken ct)
    {
        var (_, summary, viaRelationship) = await positions.GetAsync(await ActorAsync(ct), id, ct);
        return Ok(new { position = summary, viaRelationship });
    }

    [HttpPost("positions")]
    [HasPermission(P.PositionsManage)]
    public async Task<IActionResult> Designate([FromBody] DesignateInput input, CancellationToken ct) =>
        Ok(await positions.DesignateAsync(await ActorAsync(ct), input, ct));

    [HttpPut("positions/{id:guid}")]
    [HasPermission(P.PositionsManage)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateCriticalPositionInput input, CancellationToken ct) =>
        Ok(await positions.UpdateAsync(await ActorAsync(ct), id, input, ct));

    /// <summary>POST …/retire — retires the designation, withdraws its open nominations and cancels any open slate (and its approval).</summary>
    [HttpPost("positions/{id:guid}/retire")]
    [HasPermission(P.PositionsManage)]
    public async Task<IActionResult> Retire(Guid id, [FromBody] RetireInput input, CancellationToken ct) =>
        Ok(await positions.RetireAsync(await ActorAsync(ct), id, input.Reason, ct));

    [HttpGet("positions/{id:guid}/slates")]
    [HasPermission(P.SlatesRead, P.Self)]
    public async Task<IActionResult> Slates(Guid id, CancellationToken ct) => Ok(await slates.ListAsync(await ActorAsync(ct), id, ct));

    [HttpPost("positions/{id:guid}/slates")]
    [HasPermission(P.SlatesManage)]
    public async Task<IActionResult> CreateSlate(Guid id, [FromBody] SlateDraftInput input, CancellationToken ct) =>
        Ok(await slates.CreateDraftAsync(await ActorAsync(ct), id, input, ct));

    // ── Talent pools ──

    [HttpGet("pools")]
    [HasPermission(P.Read)]
    public async Task<IActionResult> Pools([FromQuery] bool includeArchived, CancellationToken ct) =>
        Ok(await positions.ListPoolsAsync(await ActorAsync(ct), includeArchived, ct));

    [HttpGet("pools/{id:guid}")]
    [HasPermission(P.Read)]
    public async Task<IActionResult> Pool(Guid id, CancellationToken ct) => Ok(await positions.GetPoolAsync(await ActorAsync(ct), id, ct));

    [HttpPost("pools")]
    [HasPermission(P.PoolsManage)]
    public async Task<IActionResult> CreatePool([FromBody] PoolInput input, CancellationToken ct) =>
        Ok(await positions.CreatePoolAsync(await ActorAsync(ct), input, ct));

    [HttpPost("pools/{id:guid}/archive")]
    [HasPermission(P.PoolsManage)]
    public async Task<IActionResult> ArchivePool(Guid id, [FromBody] RetireInput input, CancellationToken ct) =>
        Ok(await positions.ArchivePoolAsync(await ActorAsync(ct), id, input.Reason, ct));

    // ── Nominations ──

    /// <summary>What a nominator may nominate to: titles only.</summary>
    [HttpGet("nomination-targets")]
    [HasPermission(P.Nominate)]
    public async Task<IActionResult> Targets(CancellationToken ct) => Ok(await positions.NominationTargetsAsync(await ActorAsync(ct), ct));

    [HttpGet("nominations")]
    [HasPermission(P.SlatesRead)]
    public async Task<IActionResult> Nominations([FromQuery] Guid? positionId, [FromQuery] Guid? poolId, [FromQuery] int? employeeId,
        [FromQuery] string? status, CancellationToken ct) =>
        Ok(await nominations.ListAsync(await ActorAsync(ct), positionId, poolId, employeeId, status, ct));

    /// <summary>GET /api/succession/nominations/mine — the caller's own nominations, stage masked (never slate membership).</summary>
    [HttpGet("nominations/mine")]
    [HasPermission(P.Nominate)]
    public async Task<IActionResult> MyNominations(CancellationToken ct) => Ok(await nominations.MineAsync(await ActorAsync(ct), ct));

    [HttpGet("nominations/{id:guid}")]
    [HasPermission(P.SlatesRead)]
    public async Task<IActionResult> Nomination(Guid id, CancellationToken ct) => Ok(await nominations.GetAsync(await ActorAsync(ct), id, ct));

    [HttpGet("nominations/{id:guid}/history")]
    [HasPermission(P.SlatesRead)]
    public async Task<IActionResult> NominationHistory(Guid id, CancellationToken ct) => Ok(await nominations.HistoryAsync(await ActorAsync(ct), id, ct));

    [HttpPost("nominations")]
    [HasPermission(P.Nominate)]
    public async Task<IActionResult> Nominate([FromBody] NominateInput input, CancellationToken ct) =>
        Ok(await nominations.NominateAsync(await ActorAsync(ct), input, ct));

    [HttpPost("nominations/{id:guid}/review")]
    [HasPermission(P.SlatesManage)]
    public async Task<IActionResult> StartReview(Guid id, CancellationToken ct) => Ok(await nominations.StartReviewAsync(await ActorAsync(ct), id, ct));

    [HttpPost("nominations/{id:guid}/calibrate")]
    [HasPermission(P.Calibrate)]
    public async Task<IActionResult> CalibrateNomination(Guid id, [FromBody] ReadinessInput input, CancellationToken ct) =>
        Ok(await nominations.CalibrateAsync(await ActorAsync(ct), id, input, ct));

    [HttpPost("nominations/{id:guid}/readiness")]
    [HasPermission(P.SlatesManage)]
    public async Task<IActionResult> Readiness(Guid id, [FromBody] ReadinessInput input, CancellationToken ct) =>
        Ok(await nominations.ChangeReadinessAsync(await ActorAsync(ct), id, input, ct));

    [HttpPost("nominations/{id:guid}/decline")]
    [HasPermission(P.SlatesManage)]
    public async Task<IActionResult> Decline(Guid id, [FromBody] ReasonInput input, CancellationToken ct) =>
        Ok(await nominations.DeclineAsync(await ActorAsync(ct), id, input, ct));

    [HttpPost("nominations/{id:guid}/withdraw")]
    [HasPermission(P.Nominate)]
    public async Task<IActionResult> Withdraw(Guid id, [FromBody] ReasonInput input, CancellationToken ct) =>
        Ok(await nominations.WithdrawAsync(await ActorAsync(ct), id, input, ct));

    [HttpPost("nominations/{id:guid}/clear-review")]
    [HasPermission(P.SlatesManage)]
    public async Task<IActionResult> ClearReview(Guid id, [FromBody] ReasonInput input, CancellationToken ct) =>
        Ok(await nominations.ClearReviewFlagAsync(await ActorAsync(ct), id, input, ct));

    // ── Talent review and the 9-box ──

    [HttpGet("assessments")]
    [HasPermission(P.SlatesRead)]
    public async Task<IActionResult> Assessments([FromQuery] string? period, [FromQuery] int? employeeId, [FromQuery] string? status, CancellationToken ct) =>
        Ok(await assessments.ListAsync(await ActorAsync(ct), period, employeeId, status, ct));

    [HttpGet("assessments/periods")]
    [HasPermission(P.SlatesRead)]
    public async Task<IActionResult> Periods(CancellationToken ct) => Ok(await assessments.PeriodsAsync(await ActorAsync(ct), ct));

    [HttpGet("assessments/{id:guid}")]
    [HasPermission(P.SlatesRead)]
    public async Task<IActionResult> Assessment(Guid id, CancellationToken ct) => Ok(await assessments.GetAsync(await ActorAsync(ct), id, ct));

    [HttpPost("assessments")]
    [HasPermission(P.Assess)]
    public async Task<IActionResult> Assess([FromBody] AssessInput input, CancellationToken ct) =>
        Ok(await assessments.CreateAsync(await ActorAsync(ct), input, ct));

    [HttpPost("assessments/{id:guid}/submit")]
    [HasPermission(P.Assess)]
    public async Task<IActionResult> SubmitAssessment(Guid id, CancellationToken ct) => Ok(await assessments.SubmitAsync(await ActorAsync(ct), id, ct));

    [HttpPost("assessments/{id:guid}/calibrate")]
    [HasPermission(P.Calibrate)]
    public async Task<IActionResult> Calibrate(Guid id, [FromBody] CalibrateInput input, CancellationToken ct) =>
        Ok(await assessments.CalibrateAsync(await ActorAsync(ct), id, input, ct));

    [HttpGet("nine-box")]
    [HasPermission(P.SlatesRead)]
    public async Task<IActionResult> NineBox([FromQuery] string? period, [FromQuery] Guid? positionId, [FromQuery] Guid? poolId, CancellationToken ct) =>
        Ok(await assessments.NineBoxAsync(await ActorAsync(ct), period, positionId, poolId, ct));

    // ── Slates ──

    /// <summary>
    /// GET /api/succession/slates/{id} — HR (slates.read), the accountable executive (approved versions), or the person
    /// the pending approval is routed to (approvals.decide). Not anyone on the slate, nor anyone reporting to them.
    /// </summary>
    [HttpGet("slates/{id:guid}")]
    [HasPermission(P.SlatesRead, P.Self, "approvals.decide")]
    public async Task<IActionResult> Slate(Guid id, CancellationToken ct) => Ok(await slates.GetAsync(await ActorAsync(ct), id, ct));

    [HttpPut("slates/{id:guid}")]
    [HasPermission(P.SlatesManage)]
    public async Task<IActionResult> UpdateSlate(Guid id, [FromBody] SlateDraftInput input, CancellationToken ct) =>
        Ok(await slates.UpdateDraftAsync(await ActorAsync(ct), id, input, ct));

    [HttpPost("slates/{id:guid}/submit")]
    [HasPermission(P.SlatesManage)]
    public async Task<IActionResult> SubmitSlate(Guid id, [FromBody] SubmitSlateInput? input, CancellationToken ct) =>
        Ok(await slates.SubmitAsync(await ActorAsync(ct), id, input?.Version, ct));

    [HttpPost("slates/{id:guid}/revise")]
    [HasPermission(P.SlatesManage)]
    public async Task<IActionResult> Revise(Guid id, CancellationToken ct) => Ok(await slates.ReviseAsync(await ActorAsync(ct), id, ct));

    [HttpPost("slates/{id:guid}/cancel")]
    [HasPermission(P.SlatesManage)]
    public async Task<IActionResult> CancelSlate(Guid id, [FromBody] RetireInput input, CancellationToken ct) =>
        Ok(await slates.CancelDraftAsync(await ActorAsync(ct), id, input.Reason, ct));

    // ── Reports (SUC-05): every figure drills to its rows ──

    [HttpGet("reports/summary")]
    [HasPermission(P.ReportsRead)]
    public async Task<IActionResult> Summary(CancellationToken ct) => Ok(await reports.SummaryAsync(await ActorAsync(ct), ct));

    [HttpGet("reports/drill")]
    [HasPermission(P.ReportsRead)]
    public async Task<IActionResult> Drill([FromQuery] string metric, CancellationToken ct) => Ok(await reports.DrillAsync(await ActorAsync(ct), metric, ct));

    [HttpGet("reports/trend")]
    [HasPermission(P.ReportsRead)]
    public async Task<IActionResult> Trend([FromQuery] int? months, CancellationToken ct) => Ok(await reports.TrendAsync(await ActorAsync(ct), months ?? 12, ct));

    // ── Pickers ──

    /// <summary>Employees in the caller's data scope (a manager: their reporting line), for the nomination and plan pickers.</summary>
    [HttpGet("people")]
    [HasPermission(P.Nominate, P.DevPlansManage, P.PositionsManage, P.PoolsManage, P.Assess)]
    public async Task<IActionResult> People([FromQuery] string? search, CancellationToken ct)
    {
        var actor = await ActorAsync(ct);
        var query = db.Employees.AsNoTracking().Where(e => e.TenantId == actor.TenantId && !e.IsDeleted && e.Status == EmployeeStatuses.Active);
        if (!actor.Scope.IsUnrestricted)
        {
            var allowed = actor.Scope.AllowedEmployeeIds!.ToList();
            query = query.Where(e => allowed.Contains(e.Id));
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(e => e.FullName.ToLower().Contains(s) || e.EmployeeCode.ToLower().Contains(s) || e.ArabicName.Contains(search.Trim()));
        }
        var rows = await SuccessionFacts.Project(query.OrderBy(e => e.FullName).Take(50)).ToListAsync(ct);
        return Ok(rows.Select(PersonRef.From));
    }
}
