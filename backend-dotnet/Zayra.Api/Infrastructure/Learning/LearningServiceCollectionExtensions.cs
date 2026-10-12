using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Learning;

/// <summary>
/// Every LRN registration, in one place, called once from Program.cs (the AddReleaseA pattern), so the module never
/// edits Program.cs again. Every LRN surface is additionally gated per tenant by the learning opt-in flag.
/// </summary>
public static class LearningServiceCollectionExtensions
{
    /// <summary>Rate-limit policy on the anonymous cmi5 endpoints (fetch, statements, state), per client address.</summary>
    public const string Cmi5RateLimitPolicy = "learning_cmi5";
    /// <summary>Request body cap on the cmi5 endpoints (a batch of statements).</summary>
    public const long Cmi5MaxRequestBytes = 256 * 1024;

    public static IServiceCollection AddLearning(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<LearningOptions>(configuration.GetSection(LearningOptions.Section));
        // Registered into the app's existing rate limiter (Program.cs AddRateLimiter): partitioned by the same client
        // address the sign-in limits use (ClientIpResolver), so a proxy-asserted address is honoured identically.
        var perMinute = configuration.GetSection(LearningOptions.Section).GetValue("Cmi5RequestsPerMinute", 600);
        var clientIpSecret = configuration[Zayra.Api.Infrastructure.Http.ClientIpResolver.SecretConfigKey];
        services.Configure<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>(o => o.AddPolicy(Cmi5RateLimitPolicy, ctx =>
            System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                "lrn:" + Zayra.Api.Infrastructure.Http.ClientIpResolver.Resolve(ctx, clientIpSecret),
                _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                {
                    PermitLimit = perMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                })));
        services.AddScoped<LearningAssignmentService>();
        services.AddScoped<LearningCatalogService>();
        services.AddScoped<LearningProgressService>();
        services.AddScoped<LearningCmi5Service>();
        services.AddScoped<LearningReportService>();
        services.AddScoped<LearningSweepService>();
        services.AddHostedService<LearningSweepWorker>();

        // cmi5 units run on their own origin and call the xAPI endpoints from the browser. The app's CORS policy is a
        // fixed allow-list, so for the cmi5 routes only, an origin is allowed while it belongs to a launch session that
        // is still open. Every other route keeps the configured policy unchanged. AddCors registered the default
        // provider with TryAdd; this registration is the one resolved, and it wraps that default.
        services.AddSingleton<ICorsPolicyProvider>(sp => new LearningCorsPolicyProvider(
            new DefaultCorsPolicyProvider(sp.GetRequiredService<IOptions<CorsOptions>>())));
        return services;
    }
}

/// <summary>
/// CORS for the AU-facing routes (<c>/api/learning/xapi</c>, <c>/api/learning/cmi5</c>): the request's Origin is allowed
/// only when an open launch session names it as its AU origin. The per-session check (the origin must be THIS session's
/// AU) is enforced again after authentication in <see cref="LearningCmi5Service.AuthenticateAsync"/>. No credentials
/// (cookies) are ever allowed; the AU authenticates with its Basic token.
/// </summary>
public sealed class LearningCorsPolicyProvider(ICorsPolicyProvider inner) : ICorsPolicyProvider
{
    public static readonly string[] AuRoutePrefixes = ["/api/learning/xapi", "/api/learning/cmi5"];

    public async Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (!AuRoutePrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return await inner.GetPolicyAsync(context, policyName);

        var origin = context.Request.Headers.Origin.ToString().TrimEnd('/');
        if (string.IsNullOrEmpty(origin) || origin.Length > 300) return await inner.GetPolicyAsync(context, policyName);

        var db = context.RequestServices.GetRequiredService<ZayraDbContext>();
        var known = await db.LearningLaunchSessions.AsNoTracking()
            .AnyAsync(s => s.AuOrigin == origin && LearningSessionStatuses.Open.Contains(s.Status), context.RequestAborted);
        if (!known) return await inner.GetPolicyAsync(context, policyName);

        return new CorsPolicyBuilder()
            .WithOrigins(origin)
            .WithMethods("GET", "POST", "PUT", "OPTIONS")
            .WithHeaders("Authorization", "Content-Type", "X-Experience-API-Version")
            .WithExposedHeaders("X-Experience-API-Version")
            .SetPreflightMaxAge(TimeSpan.FromMinutes(10))
            .Build();
    }
}
