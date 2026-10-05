using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Zayra.Api.Infrastructure.Auth;

namespace Zayra.Api.Infrastructure.Http;

/// <summary>
/// The body and headers of a request the ASP.NET rate limiter turned away: the same shape as every
/// other 429 from sign-in — JSON <c>{ error, message }</c> and a Retry-After (the limiter's own when
/// it reports one, otherwise jittered 2–6 s) — so clients can tell "busy" from "wrong password".
/// </summary>
public static class RateLimitRejection
{
    public const string Error = "rate_limited";
    public const string Message = "The service is busy. Please try again in a few seconds.";

    public static async ValueTask WriteAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var response = context.HttpContext.Response;
        response.StatusCode = StatusCodes.Status429TooManyRequests;
        response.Headers.RetryAfter =
            context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture)
                : LoginAbuseGuard.JitteredRetryAfterSeconds();
        await response.WriteAsJsonAsync(new { error = Error, message = Message }, cancellationToken);
    }
}
