using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Engagement;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Engagement;

/// <summary>
/// The respondent's side: Self-Service → Surveys. The employee is ALWAYS the one linked to the signed-in user; no
/// route or body names an employee, invitation or response, so there is no way to answer as, or read, someone else.
/// Survey ids travel in the request body (not the path) for the draft and submit calls, and the session token never
/// appears in a URL, so request logs and traces carry neither.
///
/// <para>NO LOG CALLS, NO AUDIT, NO NOTIFICATION in this controller (EngagementPrivacyRatchetTests pins it): a
/// response is the one thing in KynexOne that must leave no per-person trail.</para>
/// </summary>
[ApiController]
[Route("api/ess/surveys")]
[Authorize]
public sealed class EssSurveysController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly EngagementResponseService _responses;
    private readonly EngagementActionService _actions;

    public EssSurveysController(ZayraDbContext db, EngagementResponseService responses, EngagementActionService actions)
    {
        _db = db;
        _responses = responses;
        _actions = actions;
    }

    [HttpGet]
    [HasPermission("ess.read")]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        var (tid, employeeId, problem) = await SelfAsync(ct);
        if (problem is not null) return problem;
        return Ok(await _responses.ListMineAsync(tid, employeeId, ct));
    }

    [HttpPost("open")]
    [HasPermission("ess.write")]
    public async Task<IActionResult> Open([FromBody] EssSurveyOpenRequest req, CancellationToken ct)
    {
        var (tid, employeeId, problem) = await SelfAsync(ct);
        if (problem is not null) return problem;
        var result = await _responses.OpenAsync(tid, employeeId, req.SurveyId, ct);
        if (!result.Ok) return Error(result.Status, result.Code!, result.Message!);
        Response.Headers.CacheControl = "no-store";
        return Ok(result.Value);
    }

    [HttpPut("draft")]
    [HasPermission("ess.write")]
    public async Task<IActionResult> Draft([FromBody] EssSurveyAnswersRequest req, CancellationToken ct)
    {
        var (tid, employeeId, problem) = await SelfAsync(ct);
        if (problem is not null) return problem;
        var result = await _responses.SaveDraftAsync(tid, employeeId, req.SurveyId, req.Token, req.Answers ?? [], ct);
        return result.Ok ? Ok(new { savedOn = result.Value }) : Error(result.Status, result.Code!, result.Message!);
    }

    [HttpPost("submit")]
    [HasPermission("ess.write")]
    public async Task<IActionResult> Submit([FromBody] EssSurveyAnswersRequest req, CancellationToken ct)
    {
        var (tid, employeeId, problem) = await SelfAsync(ct);
        if (problem is not null) return problem;
        var result = await _responses.SubmitAsync(tid, employeeId, req.SurveyId, req.Token, req.Answers ?? [], ct);
        return result.Ok ? Ok(new { submittedOn = result.Value }) : Error(result.Status, result.Code!, result.Message!);
    }

    /// <summary>Follow-up actions this employee owns.</summary>
    [HttpGet("actions")]
    [HasPermission("ess.read")]
    public async Task<IActionResult> MyActions(CancellationToken ct)
    {
        var (tid, employeeId, problem) = await SelfAsync(ct);
        if (problem is not null) return problem;
        return Ok(await _actions.ListAsync(_db.EngagementActions.Where(a => a.TenantId == tid && a.OwnerEmployeeId == employeeId), ct));
    }

    [HttpPost("actions/progress")]
    [HasPermission("ess.write")]
    public async Task<IActionResult> MyActionProgress([FromBody] EssActionProgressRequest req, CancellationToken ct)
    {
        var (tid, employeeId, problem) = await SelfAsync(ct);
        if (problem is not null) return problem;
        var action = await _db.EngagementActions.FirstOrDefaultAsync(a => a.TenantId == tid && a.Id == req.ActionId && a.OwnerEmployeeId == employeeId, ct);
        if (action is null) return Error(404, EngagementErrors.NotFound, "Action not found.");
        var name = await _db.Employees.AsNoTracking().Where(e => e.Id == employeeId).Select(e => e.FullName).FirstAsync(ct);
        var result = await _actions.ProgressAsync(action, req.Version, req.Status, req.Note, name, ct);
        return result.Ok ? Ok(_actions.ToDto(result.Value!, name)) : Error(result.Status, result.Code!, result.Message!);
    }

    private async Task<(Guid TenantId, int EmployeeId, IActionResult? Problem)> SelfAsync(CancellationToken ct)
    {
        var accessMode = User.FindFirstValue("access_mode") ?? string.Empty;
        if (accessMode is "NoLogin" or "KioskOnly")
            return (default, default, Error(403, "access_mode", "This access mode cannot use self-service."));
        if (!Guid.TryParse(User.FindFirstValue("tenant_id"), out var tid)) return (default, default, Unauthorized());
        if (await CallerEmployeeResolver.ResolveAsync(_db, User, tid, ct) is int employeeId) return (tid, employeeId, null);
        return (default, default, StatusCode(409, new { error = EngagementErrors.NoEmployeeLink, message = EssLinkGuidance.En, messageAr = EssLinkGuidance.Ar }));
    }

    private ObjectResult Error(int status, string code, string message) => StatusCode(status, new { error = code, code, message });
}

public sealed record EssSurveyOpenRequest(Guid SurveyId);
public sealed record EssSurveyAnswersRequest(Guid SurveyId, string Token, IReadOnlyList<EngagementAnswerInput>? Answers);
public sealed record EssActionProgressRequest(Guid ActionId, int Version, string Status, string? Note);
