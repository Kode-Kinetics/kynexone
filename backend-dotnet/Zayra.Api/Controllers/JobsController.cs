using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

/// <summary>
/// F3 — status, progress, cancellation and listing of durable background jobs for the caller's tenant.
/// Jobs are ENQUEUED by the owning module's endpoint (e.g. <c>POST /api/attendance/process/async</c>),
/// which applies that module's own authorization and captures the caller's scope into the payload.
///
/// <para>AUTHORIZATION — three layers, all fail-closed:</para>
/// <list type="number">
///   <item><c>[HasPermission]</c> on the controller: the union of every registered job type's view
///     permissions (<see cref="BackgroundJobPermissions.AnyView"/>) — a caller who can see no job type
///     gets 403 without touching the database. <c>BackgroundJobInfrastructureTests</c> pins this union to
///     the registry so a new job type cannot be added without it.</item>
///   <item>Per job TYPE: the descriptor's ANY-of view / cancel keys, evaluated with the same
///     <see cref="ClaimsPrincipalPermissionExtensions.HasAnyPermission"/> the <c>[HasPermission]</c>
///     handler uses. A job of a type you may not view is indistinguishable from a missing one (404).</item>
///   <item>Tenant: <see cref="BackgroundJob"/> is <c>ITenantOwned</c>, so the global query filter pins
///     every read and cancel to the token's tenant; the store repeats the predicate. Another tenant's job
///     id returns 404 — never 403, which would confirm it exists. A caller without group-level entity
///     scope sees only jobs they created: a job's progress message and result describe the scope it was
///     run for.</item>
/// </list>
/// </summary>
[ApiController]
[Route("api/jobs")]
[Authorize]
[HasPermission(BackgroundJobPermissions.AttendanceRead)]
public sealed class JobsController : ControllerBase
{
    private readonly BackgroundJobStore _store;
    private readonly BackgroundJobTypeRegistry _registry;

    public JobsController(BackgroundJobStore store, BackgroundJobTypeRegistry registry)
    {
        _store = store;
        _registry = registry;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<BackgroundJobDto>>> List(
        [FromQuery] string? status, [FromQuery] string? type,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        if (this.GetTenantId() is not Guid tenantId) return Forbid();
        var visible = _registry.All.Where(d => User.HasAnyPermission(d.ViewPermissions)).Select(d => d.JobType).ToList();
        if (visible.Count == 0) return Forbid();
        var onlyMine = OnlyOwnJobs();
        if (onlyMine && this.GetUserId() is null) return Forbid();
        var (items, total) = await _store.ListAsync(tenantId, visible, onlyMine ? this.GetUserId() : null,
            status, type, page, pageSize, ct);
        return Ok(new PagedResult<BackgroundJobDto>(items.Select(BackgroundJobDto.From).ToList(), total,
            Math.Max(1, page), Math.Clamp(pageSize, 1, 100)));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<BackgroundJobDto>> Get(Guid id, CancellationToken ct)
    {
        var job = await FindVisibleAsync(id, ct);
        return job is null ? NotFound() : Ok(BackgroundJobDto.From(job));
    }

    /// <summary>
    /// 200 — a Queued job was cancelled before it ran. 202 — a Running job will stop at its next
    /// checkpoint (poll GET for <c>Cancelled</c>). 409 — already finished. 404 — not yours to see.
    /// </summary>
    [HttpPost("{id:guid}/cancel")]
    public async Task<ActionResult<BackgroundJobDto>> Cancel(Guid id, CancellationToken ct)
    {
        var job = await FindVisibleAsync(id, ct);
        if (job is null) return NotFound();
        var descriptor = _registry.Get(job.JobType);
        var isCreator = job.CreatedByUserId is Guid creator && creator == this.GetUserId();
        if (!isCreator && !User.HasAnyPermission(descriptor.CancelPermissions)) return Forbid();

        var outcome = await _store.RequestCancelAsync(job.TenantId, id, ct);
        var current = await _store.GetAsync(job.TenantId, id, ct);
        return outcome switch
        {
            BackgroundJobCancelOutcome.Cancelled => Ok(BackgroundJobDto.From(current!)),
            BackgroundJobCancelOutcome.CancelRequested => Accepted(BackgroundJobDto.From(current!)),
            BackgroundJobCancelOutcome.AlreadyFinished => Conflict(new { message = "The job has already finished.", job = BackgroundJobDto.From(current!) }),
            _ => NotFound(),
        };
    }

    private async Task<BackgroundJob?> FindVisibleAsync(Guid id, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tenantId) return null;
        var job = await _store.GetAsync(tenantId, id, ct);
        if (job is null) return null;
        var descriptor = _registry.Find(job.JobType);
        if (descriptor is null || !User.HasAnyPermission(descriptor.ViewPermissions)) return null;
        if (OnlyOwnJobs() && (job.CreatedByUserId is null || job.CreatedByUserId != this.GetUserId())) return null;
        return job;
    }

    private bool OnlyOwnJobs()
    {
        var scope = this.GetRequestScope();
        return !(scope.IsGroupLevel || scope.IsSystemScope);
    }
}

/// <summary>Permission keys used by the job API's coarse controller gate. Must cover every registered type.</summary>
public static class BackgroundJobPermissions
{
    public const string AttendanceRead = "attendance.read";

    /// <summary>Everything the controller-level <c>[HasPermission]</c> admits.</summary>
    public static readonly IReadOnlyList<string> AnyView = [AttendanceRead];
}

/// <summary>Public shape of a job. Lease internals and the raw payload are deliberately not exposed.</summary>
public sealed record BackgroundJobDto(
    Guid Id,
    string JobType,
    string Status,
    int? ProgressTotal,
    int ProgressCompleted,
    decimal? PercentComplete,
    string? ProgressMessage,
    int AttemptCount,
    int MaxAttempts,
    bool CancelRequested,
    string? LastError,
    JsonElement? Result,
    Guid? CreatedByUserId,
    DateTime CreatedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc)
{
    public static BackgroundJobDto From(BackgroundJob j) => new(
        j.Id, j.JobType, j.Status, j.ProgressTotal, j.ProgressCompleted,
        j.ProgressTotal is > 0 ? Math.Round(100m * Math.Min(j.ProgressCompleted, j.ProgressTotal.Value) / j.ProgressTotal.Value, 1)
            : j.Status == BackgroundJobStatuses.Succeeded ? 100m : null,
        j.ProgressMessage, j.AttemptCount, j.MaxAttempts, j.CancelRequestedAtUtc is not null, j.LastError,
        string.IsNullOrWhiteSpace(j.ResultJson) ? null : JsonSerializer.Deserialize<JsonElement>(j.ResultJson),
        j.CreatedByUserId, j.CreatedAtUtc, j.StartedAtUtc, j.CompletedAtUtc);
}
