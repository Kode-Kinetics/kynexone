using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Zayra.Api.Application.Employees.Ess;

/// <summary>
/// RFC 7807 problem+json in the shape of the hire flow (<c>{type,title,status,code,traceId,errors?,titleKey}</c>).
/// <c>titleKey</c> is <c>ess.completion.error.&lt;code&gt;</c>: a dictionary key present in EN and AR, never a sentence.
/// </summary>
public static class EssProblem
{
    public static ObjectResult Create(HttpContext http, int status, string code, IReadOnlyDictionary<string, string[]>? errors = null, string? detail = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["type"] = "about:blank",
            ["title"] = code,
            ["status"] = status,
            ["code"] = code,
            ["titleKey"] = $"ess.completion.error.{code}",
            ["traceId"] = http.TraceIdentifier,
        };
        if (detail is not null) body["detail"] = detail;
        if (errors is { Count: > 0 }) body["errors"] = errors;
        var result = new ObjectResult(body) { StatusCode = status };
        result.ContentTypes.Add("application/problem+json");
        return result;
    }

    public static ObjectResult Validation(HttpContext http, int status, string code, IDictionary<string, List<string>> errors) =>
        Create(http, status, code, errors.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal));

    /// <summary>401 with the challenge the client reacts to: re-enter the password, then retry.</summary>
    public static ObjectResult StepUpRequired(HttpContext http, string purpose = StepUpPurposes.EssSensitive)
    {
        http.Response.Headers.WWWAuthenticate = $"StepUp purpose=\"{purpose}\"";
        return Create(http, StatusCodes.Status401Unauthorized, EssProblemCodes.StepUpRequired);
    }
}
