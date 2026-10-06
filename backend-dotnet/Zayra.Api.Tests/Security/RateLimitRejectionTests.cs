using System.Text.Json;
using System.Threading.RateLimiting;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Zayra.Api.Infrastructure.Http;

namespace Zayra.Api.Tests.Security;

/// <summary>A request the rate limiter rejects gets JSON { error, message } and a Retry-After.</summary>
public sealed class RateLimitRejectionTests
{
    private static async Task<(HttpResponse Response, JsonElement Body)> Reject(RateLimitLease lease)
    {
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        await RateLimitRejection.WriteAsync(new OnRejectedContext { HttpContext = http, Lease = lease }, CancellationToken.None);
        http.Response.Body.Position = 0;
        return (http.Response, JsonDocument.Parse(http.Response.Body).RootElement.Clone());
    }

    [Fact]
    public async Task RejectedRequests_GetJsonAndTheLimitersRetryAfter()
    {
        using var limiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = 1, Window = TimeSpan.FromSeconds(30), QueueLimit = 0,
        });
        using var granted = limiter.AttemptAcquire();
        using var refused = limiter.AttemptAcquire();
        refused.IsAcquired.Should().BeFalse();

        var (response, body) = await Reject(refused);
        response.StatusCode.Should().Be(429);
        response.ContentType.Should().StartWith("application/json");
        body.GetProperty("error").GetString().Should().Be("rate_limited");
        body.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        var seconds = int.Parse(response.Headers.RetryAfter.ToString());
        seconds.Should().BeInRange(11, 30);
        body.GetProperty("message").GetString().Should().EndWith("in about a minute.",
            "the message's wait matches its Retry-After");
    }

    [Fact]
    public async Task WithoutLimiterMetadata_RetryAfterIsJittered()
    {
        var (response, body) = await Reject(new NoMetadataLease());
        int.Parse(response.Headers.RetryAfter.ToString()).Should().BeInRange(2, 6);
        body.GetProperty("message").GetString().Should().EndWith("in a few seconds.");
    }

    private sealed class NoMetadataLease : RateLimitLease
    {
        public override bool IsAcquired => false;
        public override IEnumerable<string> MetadataNames => [];
        public override bool TryGetMetadata(string metadataName, out object? metadata) { metadata = null; return false; }
    }
}
