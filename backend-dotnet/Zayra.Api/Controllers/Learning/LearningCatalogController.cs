using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Learning;

namespace Zayra.Api.Controllers.Learning;

/// <summary>
/// The learning catalogue (LRN-01/02/03): course versions, learning paths and saved assignment rules. Closed unless the
/// tenant has the learning opt-in flag on (OptInFeatures → FeatureFlagGuardFilter, 403 feature_not_enabled).
/// </summary>
[ApiController]
[Authorize]
[Route("api/learning")]
public sealed class LearningCatalogController(LearningCatalogService catalog) : LearningControllerBase
{
    // ── Courses ──

    /// <summary>GET /api/learning/courses?status= — every version of every course, newest version first within a code.</summary>
    [HttpGet("courses")]
    [HasPermission("learning.read", "learning.manage")]
    public async Task<IActionResult> Courses([FromQuery] string? status, CancellationToken ct) =>
        Ok(await catalog.ListCoursesAsync((await ActorAsync(ct)).TenantId, status, ct));

    /// <summary>GET /api/learning/courses/{id} — one version. The answer key is included only for learning.manage.</summary>
    [HttpGet("courses/{id:guid}")]
    [HasPermission("learning.read", "learning.manage")]
    public async Task<IActionResult> Course(Guid id, CancellationToken ct) =>
        Ok(await catalog.GetCourseAsync((await ActorAsync(ct)).TenantId, id, Holds("learning.manage"), ct));

    /// <summary>POST /api/learning/courses — a new course (version 1, Draft).</summary>
    [HttpPost("courses")]
    [HasPermission("learning.manage")]
    public async Task<IActionResult> CreateCourse([FromBody] CourseInput input, CancellationToken ct) =>
        StatusCode(201, await catalog.CreateCourseAsync(await ActorAsync(ct), input, ct));

    /// <summary>PUT /api/learning/courses/{id} — edits a Draft. A published version is immutable (409 course_immutable).</summary>
    [HttpPut("courses/{id:guid}")]
    [HasPermission("learning.manage")]
    public async Task<IActionResult> UpdateCourse(Guid id, [FromBody] CourseInput input, CancellationToken ct) =>
        Ok(await catalog.UpdateCourseAsync(await ActorAsync(ct), id, input, ct));

    /// <summary>POST /api/learning/courses/{id}/publish — Draft → Published; the previous published version is retired.</summary>
    [HttpPost("courses/{id:guid}/publish")]
    [HasPermission("learning.manage")]
    public async Task<IActionResult> Publish(Guid id, [FromQuery] int? version, CancellationToken ct) =>
        Ok(await catalog.PublishCourseAsync(await ActorAsync(ct), id, version, ct));

    /// <summary>POST /api/learning/courses/{id}/new-version — copies a version into the next Draft (one Draft per code).</summary>
    [HttpPost("courses/{id:guid}/new-version")]
    [HasPermission("learning.manage")]
    public async Task<IActionResult> NewVersion(Guid id, CancellationToken ct) =>
        StatusCode(201, await catalog.NewVersionAsync(await ActorAsync(ct), id, ct));

    /// <summary>POST /api/learning/courses/{id}/retire — Published → Retired. Existing enrollments continue on it.</summary>
    [HttpPost("courses/{id:guid}/retire")]
    [HasPermission("learning.manage")]
    public async Task<IActionResult> Retire(Guid id, CancellationToken ct) =>
        Ok(await catalog.RetireCourseAsync(await ActorAsync(ct), id, ct));

    // ── Paths ──

    [HttpGet("paths")]
    [HasPermission("learning.read", "learning.manage", "learning.assign")]
    public async Task<IActionResult> Paths(CancellationToken ct) => Ok(await catalog.ListPathsAsync((await ActorAsync(ct)).TenantId, ct));

    [HttpGet("paths/{id:guid}")]
    [HasPermission("learning.read", "learning.manage", "learning.assign")]
    public async Task<IActionResult> PathById(Guid id, CancellationToken ct) => Ok(await catalog.GetPathAsync((await ActorAsync(ct)).TenantId, id, ct));

    [HttpPost("paths")]
    [HasPermission("learning.manage")]
    public async Task<IActionResult> CreatePath([FromBody] PathInput input, CancellationToken ct) =>
        StatusCode(201, await catalog.CreatePathAsync(await ActorAsync(ct), input, ct));

    /// <summary>PUT /api/learning/paths/{id} — header always; items only while Draft (409 path_items_locked).</summary>
    [HttpPut("paths/{id:guid}")]
    [HasPermission("learning.manage")]
    public async Task<IActionResult> UpdatePath(Guid id, [FromBody] PathInput input, CancellationToken ct) =>
        Ok(await catalog.UpdatePathAsync(await ActorAsync(ct), id, input, ct));

    [HttpPost("paths/{id:guid}/activate")]
    [HasPermission("learning.manage")]
    public async Task<IActionResult> Activate(Guid id, CancellationToken ct) => Ok(await catalog.ActivatePathAsync(await ActorAsync(ct), id, ct));

    [HttpPost("paths/{id:guid}/archive")]
    [HasPermission("learning.manage")]
    public async Task<IActionResult> Archive(Guid id, CancellationToken ct) => Ok(await catalog.ArchivePathAsync(await ActorAsync(ct), id, ct));

    // ── Rules ──

    [HttpGet("rules")]
    [HasPermission("learning.read", "learning.manage")]
    public async Task<IActionResult> Rules(CancellationToken ct) => Ok(await catalog.ListRulesAsync((await ActorAsync(ct)).TenantId, ct));

    /// <summary>POST /api/learning/rules — saves the rule and applies it now; the sweep applies it to later matches.</summary>
    [HttpPost("rules")]
    [HasPermission("learning.manage")]
    public async Task<IActionResult> CreateRule([FromBody] RuleInput input, CancellationToken ct)
    {
        RequireGroupScope();
        var (rule, applied) = await catalog.CreateRuleAsync(await ActorAsync(ct), input, ct);
        return StatusCode(201, new { rule, applied });
    }

    [HttpPost("rules/{id:guid}/deactivate")]
    [HasPermission("learning.manage")]
    public async Task<IActionResult> DeactivateRule(Guid id, CancellationToken ct)
    {
        RequireGroupScope();
        return Ok(await catalog.DeactivateRuleAsync(await ActorAsync(ct), id, ct));
    }

    /// <summary>
    /// A saved rule is applied by the sweep with tenant-wide reach (it catches future hires in every company), so only a
    /// group-scope user may create or deactivate one. A company-scoped HR user assigns one-off instead.
    /// </summary>
    private void RequireGroupScope()
    {
        if (!this.HasGroupEntityScope())
            throw new LearningException(403, "group_scope_required",
                "Standing rules apply across every company, so only a group-level HR user can create or stop one. Assign the path directly instead.");
    }
}
