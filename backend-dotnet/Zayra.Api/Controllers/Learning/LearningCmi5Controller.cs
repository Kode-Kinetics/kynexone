using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using Zayra.Api.Infrastructure.Learning;

namespace Zayra.Api.Controllers.Learning;

/// <summary>Maps <see cref="Cmi5Exception"/> to the cmi5 / xAPI error shapes and stamps the xAPI version header.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class Cmi5ErrorsAttribute : Attribute, IExceptionFilter, IResultFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not Cmi5Exception e) return;
        context.Result = e.FetchErrorCode is not null
            ? new ObjectResult(new Dictionary<string, string> { ["error-code"] = e.FetchErrorCode, ["error-text"] = e.Message }) { StatusCode = e.Status }
            : new ObjectResult(new { error = e.Code, message = e.Message }) { StatusCode = e.Status };
        context.ExceptionHandled = true;
        context.HttpContext.Response.Headers["X-Experience-API-Version"] = Cmi5Verbs.XapiVersion;
        if (e.RetryAfterSeconds is int retry) context.HttpContext.Response.Headers.RetryAfter = retry.ToString();
    }

    public void OnResultExecuting(ResultExecutingContext context) =>
        context.HttpContext.Response.Headers["X-Experience-API-Version"] = Cmi5Verbs.XapiVersion;

    public void OnResultExecuted(ResultExecutedContext context) { }
}

/// <summary>
/// The cmi5 request cap as a clear 413 before model binding: a declared Content-Length over the cap is refused here.
/// [RequestSizeLimit] stays on the controllers for bodies without a length (chunked), which fail while being read.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class Cmi5RequestSizeAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        if (context.HttpContext.Request.ContentLength is not long length || length <= LearningServiceCollectionExtensions.Cmi5MaxRequestBytes) return;
        context.HttpContext.Response.Headers["X-Experience-API-Version"] = Cmi5Verbs.XapiVersion;
        context.Result = new ObjectResult(new { error = "request_too_large", message = "The request is larger than this LRS accepts." })
        {
            StatusCode = StatusCodes.Status413PayloadTooLarge,
        };
    }

    public void OnResourceExecuted(ResourceExecutedContext context) { }
}

/// <summary>
/// cmi5 fetch (§8.2). Anonymous by design: the AU has no KynexOne login. The one-time token in the URL is the
/// credential; it is stored only as a hash and refused after first use or expiry.
/// </summary>
[ApiController]
[AllowAnonymous]
[Cmi5Errors]
[EnableRateLimiting(LearningServiceCollectionExtensions.Cmi5RateLimitPolicy)]
[RequestSizeLimit(LearningServiceCollectionExtensions.Cmi5MaxRequestBytes)]
[Cmi5RequestSize]
[Route("api/learning/cmi5")]
public sealed class LearningCmi5FetchController(LearningCmi5Service cmi5) : ControllerBase
{
    /// <summary>POST /api/learning/cmi5/fetch/{token} → {"auth-token": "..."} once; then {"error-code":"1"}.</summary>
    [HttpPost("fetch/{token}")]
    public async Task<IActionResult> Fetch(string token, CancellationToken ct)
    {
        var authToken = await cmi5.FetchAsync(token, ct, Request.Headers.Origin.ToString());
        Response.Headers.CacheControl = "no-store";
        return Ok(new Dictionary<string, string> { ["auth-token"] = authToken });
    }
}

/// <summary>
/// The minimal LRS the AU reports to (§3.1): Statements (POST/PUT, idempotent by id) and the State API for
/// LMS.LaunchData only. Anonymous at the pipeline level; every action authenticates the Basic session token from fetch
/// (LearningCmi5Service.AuthenticateAsync), requires the X-Experience-API-Version header, and only accepts the AU's origin.
/// </summary>
[ApiController]
[AllowAnonymous]
[Cmi5Errors]
[EnableRateLimiting(LearningServiceCollectionExtensions.Cmi5RateLimitPolicy)]
[RequestSizeLimit(LearningServiceCollectionExtensions.Cmi5MaxRequestBytes)]
[Cmi5RequestSize]
[Route("api/learning/xapi")]
public sealed class LearningXapiController(LearningCmi5Service cmi5) : ControllerBase
{
    [HttpGet("about")]
    public IActionResult About() => Ok(new { version = new[] { Cmi5Verbs.XapiVersion } });

    [HttpPost("statements")]
    public async Task<IActionResult> Post([FromBody] JsonElement body, CancellationToken ct)
    {
        var session = await AuthenticateAsync(ct);
        var ids = await cmi5.StoreStatementsAsync(session, body, null, ct);
        return Ok(ids.Select(id => id.ToString()));
    }

    [HttpPut("statements")]
    public async Task<IActionResult> Put([FromQuery] Guid? statementId, [FromBody] JsonElement body, CancellationToken ct)
    {
        if (statementId is null) throw new Cmi5Exception(400, "invalid_statement", "PUT needs the statementId parameter.");
        var session = await AuthenticateAsync(ct);
        await cmi5.StoreStatementsAsync(session, body, statementId, ct);
        return NoContent();
    }

    /// <summary>GET /api/learning/xapi/activities/state?stateId=LMS.LaunchData&amp;activityId&amp;agent&amp;registration.</summary>
    [HttpGet("activities/state")]
    public async Task<IActionResult> GetState([FromQuery] string? stateId, [FromQuery] string? activityId, [FromQuery] string? agent,
        [FromQuery] string? registration, CancellationToken ct)
    {
        var session = await AuthenticateAsync(ct);
        return Ok(await cmi5.LaunchDataAsync(session, stateId, activityId, agent, registration, ct));
    }

    /// <summary>The AU may not write LMS.LaunchData (cmi5 §10.2.1), and this minimal LRS stores no other state documents.</summary>
    [HttpPut("activities/state")]
    [HttpPost("activities/state")]
    public async Task<IActionResult> WriteState([FromQuery] string? stateId, CancellationToken ct)
    {
        await AuthenticateAsync(ct);
        throw stateId == Cmi5Verbs.LaunchDataStateId
            ? new Cmi5Exception(403, "launch_data_read_only", "LMS.LaunchData is written by the LMS only.")
            : new Cmi5Exception(501, "state_not_supported", "This LRS stores LMS.LaunchData only.");
    }

    /// <summary>Learner preferences (cmi5 §11): none are stored; the AU uses its defaults.</summary>
    [HttpGet("agents/profile")]
    public async Task<IActionResult> AgentProfile(CancellationToken ct)
    {
        await AuthenticateAsync(ct);
        throw new Cmi5Exception(404, "profile_not_found", "No learner preferences are stored.");
    }

    private async Task<Zayra.Api.Models.LearningLaunchSession> AuthenticateAsync(CancellationToken ct)
    {
        // Authenticate first, unconditionally; the version header is checked only on an authenticated call.
        var session = await cmi5.AuthenticateAsync(Request.Headers.Authorization.ToString(), Request.Headers.Origin.ToString(), ct);
        var version = Request.Headers["X-Experience-API-Version"].ToString();
        if (!version.StartsWith("1.0", StringComparison.Ordinal))
            throw new Cmi5Exception(400, "xapi_version_required", "Send the X-Experience-API-Version header (1.0.x).");
        return session;
    }
}
