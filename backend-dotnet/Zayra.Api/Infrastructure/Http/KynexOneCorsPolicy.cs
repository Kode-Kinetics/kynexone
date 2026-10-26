using Microsoft.AspNetCore.Cors.Infrastructure;

namespace Zayra.Api;

/// <summary>
/// The one CORS policy of the host. Response headers a browser script may read cross-origin are listed here: the department-import
/// preview's cleared-link count and the journal export's file hash are both read by the web app, and a header that is not exposed is invisible to it.
/// </summary>
public static class KynexOneCorsPolicy
{
    public static readonly string[] ExposedHeaders = ["X-Links-To-Clear", "X-Journal-Export-Hash"];

    public static void Apply(CorsPolicyBuilder policy, string[] allowedOrigins) =>
        policy.WithOrigins(allowedOrigins).AllowAnyMethod().AllowAnyHeader().WithExposedHeaders(ExposedHeaders);
}
