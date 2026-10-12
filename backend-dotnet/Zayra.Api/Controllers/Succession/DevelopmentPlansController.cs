using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Succession;
using Zayra.Api.Models;
using P = Zayra.Api.Infrastructure.Succession.SuccessionPermissions;

namespace Zayra.Api.Controllers.Succession;

/// <summary>
/// Development plans (SUC-04) under <c>/api/succession/development-plans</c>: HR for the employees in their companies, a
/// line manager for their own reporting line (IDataScopeService). The nomination a plan supports is returned only to
/// callers holding succession.slates.read — never to the line manager.
/// </summary>
[ApiController]
[Authorize]
[Route("api/succession/development-plans")]
public sealed class DevelopmentPlansController(DevelopmentPlanService plans, ZayraDbContext db) : SuccessionControllerBase
{
    [HttpGet]
    [HasPermission(P.DevPlansRead)]
    public async Task<IActionResult> List([FromQuery] int? employeeId, [FromQuery] string? status, CancellationToken ct) =>
        Ok(await plans.ListAsync(await ActorAsync(ct), employeeId, status, ct));

    [HttpGet("{id:guid}")]
    [HasPermission(P.DevPlansRead)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Ok(await plans.GetAsync(await ActorAsync(ct), id, ct));

    [HttpPost]
    [HasPermission(P.DevPlansManage)]
    public async Task<IActionResult> Create([FromBody] PlanInput input, CancellationToken ct) => Ok(await plans.CreateAsync(await ActorAsync(ct), input, ct));

    [HttpPost("{id:guid}/activate")]
    [HasPermission(P.DevPlansManage)]
    public async Task<IActionResult> Activate(Guid id, CancellationToken ct) => Ok(await plans.ActivateAsync(await ActorAsync(ct), id, ct));

    [HttpPost("{id:guid}/hold")]
    [HasPermission(P.DevPlansManage)]
    public async Task<IActionResult> Hold(Guid id, [FromBody] RetireInput input, CancellationToken ct) =>
        Ok(await plans.HoldAsync(await ActorAsync(ct), id, input.Reason, ct));

    [HttpPost("{id:guid}/complete")]
    [HasPermission(P.DevPlansManage)]
    public async Task<IActionResult> Complete(Guid id, CancellationToken ct) => Ok(await plans.CompleteAsync(await ActorAsync(ct), id, ct));

    [HttpPost("{id:guid}/cancel")]
    [HasPermission(P.DevPlansManage)]
    public async Task<IActionResult> Cancel(Guid id, [FromBody] RetireInput input, CancellationToken ct) =>
        Ok(await plans.CancelAsync(await ActorAsync(ct), id, input.Reason, ct));

    [HttpPost("{id:guid}/review")]
    [HasPermission(P.DevPlansManage)]
    public async Task<IActionResult> Review(Guid id, [FromBody] PlanReviewInput input, CancellationToken ct) =>
        Ok(await plans.ReviewAsync(await ActorAsync(ct), id, input, ct));

    [HttpPost("{id:guid}/actions")]
    [HasPermission(P.DevPlansManage)]
    public async Task<IActionResult> AddAction(Guid id, [FromBody] ActionInput input, CancellationToken ct) =>
        Ok(await plans.AddActionAsync(await ActorAsync(ct), id, input, ct));

    [HttpPut("actions/{actionId:guid}")]
    [HasPermission(P.DevPlansManage)]
    public async Task<IActionResult> UpdateAction(Guid actionId, [FromBody] ActionUpdateInput input, CancellationToken ct) =>
        Ok(await plans.UpdateActionAsync(await ActorAsync(ct), actionId, input, ct));

    public sealed record ConfirmInput(int Version);

    /// <summary>POST …/actions/{id}/confirm-completion — the owner or HR confirms a completion the employee proposed.</summary>
    [HttpPost("actions/{actionId:guid}/confirm-completion")]
    [HasPermission(P.DevPlansManage)]
    public async Task<IActionResult> ConfirmCompletion(Guid actionId, [FromBody] ConfirmInput input, CancellationToken ct) =>
        Ok(await plans.ConfirmCompletionAsync(await ActorAsync(ct), actionId, input.Version, ct));

    /// <summary>Active learning paths, for the Learning-action picker (empty when Learning is off for the tenant).</summary>
    [HttpGet("learning-paths")]
    [HasPermission(P.DevPlansManage)]
    public async Task<IActionResult> LearningPaths(CancellationToken ct)
    {
        var actor = await ActorAsync(ct);
        return Ok(await db.LearningPaths.AsNoTracking().Where(p => p.TenantId == actor.TenantId && p.Status == LearningPathStatuses.Active)
            .OrderBy(p => p.Name).Select(p => new { p.Id, p.Code, p.Name, p.NameAr }).Take(200).ToListAsync(ct));
    }

    /// <summary>An in-scope employee's goals, for the Goal-action picker.</summary>
    [HttpGet("goals")]
    [HasPermission(P.DevPlansManage)]
    public async Task<IActionResult> Goals([FromQuery] int employeeId, CancellationToken ct)
    {
        var actor = await ActorAsync(ct);
        if (!actor.Scope.CanAccessEmployee(employeeId)) throw SuccessionException.NotFound("Employee");
        return Ok(await db.EmployeeGoals.AsNoTracking().Where(g => g.TenantId == actor.TenantId && g.EmployeeId == employeeId && !g.IsDeleted)
            .OrderByDescending(g => g.CreatedAtUtc).Select(g => new { g.Id, g.Title, g.Status, g.DueDate }).Take(100).ToListAsync(ct));
    }
}
