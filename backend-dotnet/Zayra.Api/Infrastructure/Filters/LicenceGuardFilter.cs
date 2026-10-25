using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Zayra.Api.Infrastructure.Licensing;

namespace Zayra.Api.Infrastructure.Filters;

/// <summary>
/// Refuses work when the installation has no usable licence (402). A resource filter: it runs after
/// authentication and authorization and BEFORE model binding, so a refused request's body is never read. It stays
/// registered before <see cref="FeatureFlagGuardFilter"/>, so a request both would refuse gets the licence answer.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>No valid licence → 402 <c>licence_missing</c>.</item>
/// <item>Past the grace days → 402 <c>licence_expired</c>.</item>
/// <item>Inside the grace days → allowed, with <c>X-Licence-Status: Grace</c> so the web app shows a banner.</item>
/// </list>
/// Sign-in, the public endpoints, health and the licence endpoint itself always pass, so an administrator can sign in
/// and import a licence on an installation that has none.
/// </remarks>
public class LicenceGuardFilter : IAsyncResourceFilter
{
    public const string StatusHeader = "X-Licence-Status";

    private static readonly string[] SkippedPrefixes =
    {
        "/api/auth",
        "/api/public",
        "/api/organisation-admin/licence",
        "/api/tenant-admin/localization",
        "/api/health",
        "/api/version",
    };

    private readonly ILicenceService _licence;
    private readonly ILogger<LicenceGuardFilter> _log;

    public LicenceGuardFilter(ILicenceService licence, ILogger<LicenceGuardFilter> log)
    {
        _licence = licence;
        _log = log;
    }

    public static bool IsSkipped(string path) =>
        SkippedPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var path = context.HttpContext.Request.Path.Value ?? string.Empty;
        if (IsSkipped(path))
        {
            await next();
            return;
        }

        switch (_licence.State)
        {
            case LicenceState.Missing:
                Refuse(context, path, "licence_missing", "No valid licence is installed. An administrator can import one under Organisation Admin → Licence.");
                return;
            case LicenceState.Expired:
                Refuse(context, path, "licence_expired", "Your KynexOne licence has expired. Contact your supplier for a renewed licence.");
                return;
            case LicenceState.Grace:
                context.HttpContext.Response.Headers[StatusHeader] = nameof(LicenceState.Grace);
                break;
        }

        await next();
    }

    private void Refuse(ResourceExecutingContext context, string path, string error, string message)
    {
        _log.LogWarning("LicenceGuard refused a request: {Error}. Path={Path}", error, path);
        context.Result = new ObjectResult(new { error, message }) { StatusCode = StatusCodes.Status402PaymentRequired };
    }
}
