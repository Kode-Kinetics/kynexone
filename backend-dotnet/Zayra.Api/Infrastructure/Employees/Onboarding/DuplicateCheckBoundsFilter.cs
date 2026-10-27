using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Zayra.Api.Controllers;

namespace Zayra.Api.Infrastructure.Employees.Onboarding;

/// <summary>
/// Bounds the advisory duplicate check so it cannot be used as a bulk identity probe: at most
/// <see cref="MaxValues"/> identity values per call, a field key of at most 40 characters and a value of at most 64.
/// A breach is a 400 problem (<c>validation_failed</c>, <c>list_too_long</c> or <c>value_too_long</c>) before any query runs.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class BoundDuplicateCheckAttribute : ActionFilterAttribute
{
    public const int MaxValues = 20;
    public const int MaxKey = 40;
    public const int MaxValue = 64;

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var values = context.ActionArguments.Values.OfType<DuplicateCheckRequest>().FirstOrDefault()?.IdentityValues;
        if (values is null) return;
        string? code = null;
        if (values.Count > MaxValues) code = "list_too_long";
        else if (values.Any(v => (v.FieldKey?.Length ?? 0) > MaxKey || (v.Value?.Length ?? 0) > MaxValue)) code = "value_too_long";
        if (code is null) return;
        var problem = new ProblemDetails { Status = 400, Title = "validation_failed", Detail = "The duplicate check input is too large." };
        problem.Extensions["code"] = "validation_failed";
        problem.Extensions["errors"] = new Dictionary<string, string[]> { ["identityValues"] = [code] };
        problem.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
        context.Result = new ObjectResult(problem) { StatusCode = 400, ContentTypes = { "application/problem+json" } };
    }
}
