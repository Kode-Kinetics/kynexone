using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Learning;

namespace Zayra.Api.Controllers.Learning;

public sealed record LearningReasonInput(string? Reason);

/// <summary>
/// Assignments (LRN-03), the records behind them, HR's corrections (LRN-07/08) and reporting (LRN-11). Every list and
/// by-id read is limited to the employees the caller's data scope reaches; an id outside it answers 404, as an id in
/// another tenant does.
/// </summary>
[ApiController]
[Authorize]
[Route("api/learning")]
public sealed class LearningRecordsController(LearningAssignmentService assignments, LearningProgressService progress,
    LearningReportService reports) : LearningControllerBase
{
    // ── Assignments ──

    /// <summary>
    /// POST /api/learning/assignments — assign a path to employees, a department, a designation or a grade. Returns the
    /// created / skipped (already assigned) / refused (with reasons) counts. A single employee who already has the path
    /// live is 409 duplicate_assignment.
    /// </summary>
    [HttpPost("assignments")]
    [HasPermission("learning.assign")]
    public async Task<IActionResult> Assign([FromBody] AssignInput input, CancellationToken ct) =>
        Ok(await assignments.AssignAsync(await ActorAsync(ct), input, ct));

    [HttpGet("assignments")]
    [HasPermission("learning.assign", "learning.report", "learning.records.correct")]
    public async Task<IActionResult> List([FromQuery] Guid? pathId, [FromQuery] int? employeeId, [FromQuery] string? status, CancellationToken ct) =>
        Ok(await progress.ListAssignmentsAsync(await ActorAsync(ct), pathId, employeeId, status, ct));

    [HttpGet("assignments/{id:guid}")]
    [HasPermission("learning.assign", "learning.report", "learning.records.correct")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Ok(await progress.AssignmentViewAsync(await ActorAsync(ct), id, ct));

    [HttpPost("assignments/{id:guid}/cancel")]
    [HasPermission("learning.assign")]
    public async Task<IActionResult> Cancel(Guid id, [FromBody] LearningReasonInput input, CancellationToken ct)
    {
        var actor = await ActorAsync(ct);
        await assignments.CancelAsync(actor, id, input.Reason, ct);
        return Ok(await progress.AssignmentViewAsync(actor, id, ct));
    }

    [HttpGet("enrollments/{id:guid}/attempts")]
    [HasPermission("learning.assign", "learning.report", "learning.records.correct")]
    public async Task<IActionResult> Attempts(Guid id, CancellationToken ct) => Ok(await progress.AttemptsAsync(await ActorAsync(ct), id, ct));

    // ── Corrections (learning.records.correct; reason required; audited) ──

    /// <summary>POST /api/learning/enrollments/{id}/reset-attempts — reopen an item locked after its last failed attempt.</summary>
    [HttpPost("enrollments/{id:guid}/reset-attempts")]
    [HasPermission("learning.records.correct")]
    public async Task<IActionResult> ResetAttempts(Guid id, [FromBody] LearningReasonInput input, CancellationToken ct) =>
        Ok(await progress.ResetAttemptsAsync(await ActorAsync(ct), id, input.Reason, ct));

    [HttpGet("credentials")]
    [HasPermission("learning.report", "learning.records.correct")]
    public async Task<IActionResult> Credentials([FromQuery] int? employeeId, [FromQuery] string? status, CancellationToken ct) =>
        Ok(await progress.ListCredentialsAsync(await ActorAsync(ct), employeeId, status, ct));

    /// <summary>GET /api/learning/credentials/verify?number=&amp;code= — both the number and the printed code must match.</summary>
    [HttpGet("credentials/verify")]
    [HasPermission("learning.read", "learning.report", "learning.records.correct")]
    public async Task<IActionResult> Verify([FromQuery] string? number, [FromQuery] string? code, CancellationToken ct) =>
        Ok(await progress.VerifyAsync(await ActorAsync(ct), number, code, ct));

    [HttpGet("credentials/{id:guid}")]
    [HasPermission("learning.report", "learning.records.correct")]
    public async Task<IActionResult> Credential(Guid id, CancellationToken ct) => Ok(await progress.HrCredentialAsync(await ActorAsync(ct), id, ct));

    /// <summary>POST /api/learning/credentials/{id}/revoke {reason, dueDate} — revokes and reopens the learning behind it.</summary>
    [HttpPost("credentials/{id:guid}/revoke")]
    [HasPermission("learning.records.correct")]
    public async Task<IActionResult> Revoke(Guid id, [FromBody] RevokeCredentialInput input, CancellationToken ct) =>
        Ok(await progress.RevokeAsync(await ActorAsync(ct), id, input, ct));

    /// <summary>POST /api/learning/enrollments/{id}/verify — accept a content-reported (cmi5) completion; issues the credential.</summary>
    [HttpPost("enrollments/{id:guid}/verify")]
    [HasPermission("learning.records.correct")]
    public async Task<IActionResult> Verify(Guid id, CancellationToken ct) => Ok(await progress.VerifyAsync(await ActorAsync(ct), id, ct));

    /// <summary>POST /api/learning/enrollments/{id}/reject-verification {reason} — the item goes back to in progress.</summary>
    [HttpPost("enrollments/{id:guid}/reject-verification")]
    [HasPermission("learning.records.correct")]
    public async Task<IActionResult> RejectVerification(Guid id, [FromBody] LearningReasonInput input, CancellationToken ct) =>
        Ok(await progress.RejectVerificationAsync(await ActorAsync(ct), id, input.Reason, ct));

    /// <summary>POST /api/learning/credentials/{id}/correct — supersede with a corrected new row (new number); history kept.</summary>
    [HttpPost("credentials/{id:guid}/correct")]
    [HasPermission("learning.records.correct")]
    public async Task<IActionResult> Correct(Guid id, [FromBody] CorrectCredentialInput input, CancellationToken ct) =>
        Ok(await progress.CorrectAsync(await ActorAsync(ct), id, input, ct));

    // ── Reports (learning.report; HR = their companies, manager = their reporting tree) ──

    [HttpGet("reports/summary")]
    [HasPermission("learning.report")]
    public async Task<IActionResult> Summary(CancellationToken ct) => Ok(await reports.SummaryAsync(await ActorAsync(ct), ct));

    /// <summary>GET /api/learning/reports/status?pathId&amp;departmentId&amp;status=open|Assigned|…&amp;format=csv — one row per assignment.</summary>
    [HttpGet("reports/status")]
    [HasPermission("learning.report")]
    public async Task<IActionResult> Status([FromQuery] Guid? pathId, [FromQuery] Guid? departmentId, [FromQuery] string? status,
        [FromQuery] string? format, CancellationToken ct) =>
        Rows(await reports.StatusMatrixAsync(await ActorAsync(ct), pathId, departmentId, status, null, ct), format, "status");

    [HttpGet("reports/overdue")]
    [HasPermission("learning.report")]
    public async Task<IActionResult> Overdue([FromQuery] Guid? pathId, [FromQuery] Guid? departmentId, [FromQuery] string? format, CancellationToken ct) =>
        Rows(await reports.StatusMatrixAsync(await ActorAsync(ct), pathId, departmentId, null, true, ct), format, "overdue");

    [HttpGet("reports/pending")]
    [HasPermission("learning.report")]
    public async Task<IActionResult> Pending([FromQuery] string? format, CancellationToken ct) =>
        Rows(await reports.PendingVerificationAsync(await ActorAsync(ct), ct), format, "pending-verification");

    [HttpGet("reports/locked")]
    [HasPermission("learning.report")]
    public async Task<IActionResult> Locked([FromQuery] string? format, CancellationToken ct) =>
        Rows(await reports.LockedAsync(await ActorAsync(ct), ct), format, "locked");

    /// <summary>GET /api/learning/reports/completion?groupBy=path|department — Assigned = Completed + InProgress + NotStarted.</summary>
    [HttpGet("reports/completion")]
    [HasPermission("learning.report")]
    public async Task<IActionResult> Completion([FromQuery] string? groupBy, [FromQuery] string? format, CancellationToken ct) =>
        Rows(await reports.CompletionAsync(await ActorAsync(ct), groupBy, ct), format, "completion");

    [HttpGet("reports/expiring")]
    [HasPermission("learning.report")]
    public async Task<IActionResult> Expiring([FromQuery] int? days, [FromQuery] string? format, CancellationToken ct) =>
        Rows(await reports.ExpiringAsync(await ActorAsync(ct), days ?? 30, ct), format, "expiring");
}
