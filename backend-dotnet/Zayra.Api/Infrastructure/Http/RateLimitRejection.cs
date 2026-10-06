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
    public static string MessageFor(int retryAfterSeconds)
        => $"Too many requests. Please try again {LoginAbuseGuard.WaitPhrase(retryAfterSeconds)}.";

    public static async ValueTask WriteAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var response = context.HttpContext.Response;
        response.StatusCode = StatusCodes.Status429TooManyRequests;
        var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
            : int.Parse(LoginAbuseGuard.JitteredRetryAfterSeconds(), CultureInfo.InvariantCulture);
        response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        // The words match the header: "in a few seconds" only when the wait really is that short.
        await response.WriteAsJsonAsync(new { error = Error, message = MessageFor(seconds) }, cancellationToken);
    }
}
