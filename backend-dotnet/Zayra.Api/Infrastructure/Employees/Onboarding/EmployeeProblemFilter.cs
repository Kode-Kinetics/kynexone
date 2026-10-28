using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Zayra.Api.Application.Employees.Onboarding;

namespace Zayra.Api.Infrastructure.Employees.Onboarding;

/// <summary>
/// Turns the typed refusals of the employee input guard into RFC 7807 <c>application/problem+json</c>:
/// 400/422 <see cref="EmployeeValidationException"/> and 409 <see cref="EmployeeWarningsRequireAckException"/>.
/// Anything else is left to the rest of the pipeline; this filter never relies on the global
/// InvalidOperationException catch-all.
/// </summary>
public sealed class EmployeeProblemFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        var traceId = context.HttpContext.TraceIdentifier;
        switch (context.Exception)
        {
            case EmployeeValidationException v:
            {
                var body = new Dictionary<string, object?>
                {
                    ["type"] = "about:blank",
                    ["title"] = v.Status == 400 ? "One or more validation errors occurred." : "The request was refused.",
                    ["status"] = v.Status,
                    ["code"] = v.Code,
                    ["detail"] = v.Detail,
                    ["errors"] = v.Errors,
                    ["traceId"] = traceId,
                };
                if (v.Field is not null) body["field"] = v.Field;
                context.Result = Problem(v.Status, body);
                context.ExceptionHandled = true;
                break;
            }
            case EmployeeWarningsRequireAckException w:
                context.Result = Problem(409, new Dictionary<string, object?>
                {
                    ["type"] = "about:blank",
                    ["title"] = "Acknowledgement required.",
                    ["status"] = 409,
                    ["code"] = EmployeeProblemCodes.WarningsRequireAcknowledgement,
                    ["detail"] = w.Message,
                    ["warnings"] = w.Warnings,
                    ["traceId"] = traceId,
                });
                context.ExceptionHandled = true;
                break;
        }
    }

    private static ObjectResult Problem(int status, Dictionary<string, object?> body)
    {
        var result = new ObjectResult(body) { StatusCode = status };
        result.ContentTypes.Add("application/problem+json");
        return result;
    }
}
