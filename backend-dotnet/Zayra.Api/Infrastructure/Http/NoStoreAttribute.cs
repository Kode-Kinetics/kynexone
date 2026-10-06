using Microsoft.AspNetCore.Mvc.Filters;

namespace Zayra.Api.Infrastructure.Http;

/// <summary>
/// Marks an action whose response carries a secret: a session or challenge token, a TOTP provisioning
/// URI (it embeds the shared secret), or one-time recovery codes. Forces
/// <c>Cache-Control: no-store</c> on every outcome of that action, so neither a browser, a proxy nor
/// a CDN keeps a copy — whatever the path-based defaults in <see cref="SecurityHeaders"/> say today.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class NoStoreAttribute : ResultFilterAttribute
{
    public override void OnResultExecuting(ResultExecutingContext context)
    {
        var headers = context.HttpContext.Response.Headers;
        headers.CacheControl = "no-store, no-cache, max-age=0";
        headers.Pragma = "no-cache";
        base.OnResultExecuting(context);
    }
}
