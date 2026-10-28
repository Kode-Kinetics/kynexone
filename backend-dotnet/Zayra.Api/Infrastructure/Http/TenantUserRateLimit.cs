using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Configuration;

namespace Zayra.Api.Infrastructure.Http;

/// <summary>
/// Per-user, then per-tenant request ceilings for the Add Employee helpers (duplicate check, work-email preview,
/// field catalog, name transliteration, create).
///
/// <para>WHY AN ACTION FILTER. <c>UseRateLimiter()</c> runs before authentication, so a named policy cannot read the
/// tenant or user claims and could only partition by IP, which is the wrong key behind a shared office address. An
/// action filter runs after authorization, so the partition key is the authenticated <c>tenant:user</c>.</para>
///
/// <para>The user window is checked first and the tenant window second (a chained limiter); a caller that is over
/// either gets 429 with a Retry-After, in the same body shape as every other rate-limit response.</para>
/// </summary>
public sealed record TenantUserRateLimitPolicy(string Name, int PerUser, int PerTenant, TimeSpan Window);

/// <summary>Singleton owning one chained limiter per named policy. Pure and clock-free apart from the BCL window timer,
/// so it is unit-testable without a host.</summary>
public sealed class TenantUserRateLimiter : IDisposable
{
    public static readonly IReadOnlyList<TenantUserRateLimitPolicy> Defaults =
    [
        new("duplicate-check", 20, 120, TimeSpan.FromMinutes(1)),
        new("derive-work-email", 30, 200, TimeSpan.FromMinutes(1)),
        new("field-catalog", 60, 300, TimeSpan.FromMinutes(1)),
        new("transliterate", 60, 300, TimeSpan.FromMinutes(1)),
        new("transliterate-batch", 10, 300, TimeSpan.FromMinutes(1)),
        new("employee-create", 10, 60, TimeSpan.FromMinutes(1)),
        // The write routes around the hire (autosave runs every few seconds while a form is open).
        new("hire-draft", 60, 300, TimeSpan.FromMinutes(1)),
        new("employee-rehire", 5, 30, TimeSpan.FromMinutes(1)),
        new("draft-discard", 10, 60, TimeSpan.FromMinutes(1)),
        new("compensation-approve", 10, 60, TimeSpan.FromMinutes(1)),
        new("nationality-confirm", 30, 200, TimeSpan.FromMinutes(1)),
        // The scoped pay approval view (OD4): reading a package shows bank details, so reads are capped too.
        new("pay-approval-read", 30, 200, TimeSpan.FromMinutes(1)),
        new("pay-approval-decide", 10, 60, TimeSpan.FromMinutes(1)),
    ];

    private readonly Dictionary<string, PartitionedRateLimiter<(string Tenant, string User)>> _limiters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TenantUserRateLimitPolicy> _policies = new(StringComparer.Ordinal);

    public TenantUserRateLimiter(IConfiguration? configuration = null, IEnumerable<TenantUserRateLimitPolicy>? policies = null)
    {
        // The container hands an optional IEnumerable parameter over as an EMPTY sequence, never null: an empty list means
        // 'no override', and treating it as 'no policies' would leave every limiter unregistered (an unknown policy never
        // blocks), so the whole feature would be silently inert in production.
        var effective = policies?.ToList() is { Count: > 0 } custom ? custom : Defaults;
        foreach (var d in effective)
        {
            // RateLimit:Employees:<policy>:PerUser / PerTenant / WindowSeconds override the defaults.
            var section = configuration?.GetSection($"RateLimit:Employees:{d.Name}");
            var perUser = Math.Max(1, section?.GetValue<int?>("PerUser") ?? d.PerUser);
            var perTenant = Math.Max(1, section?.GetValue<int?>("PerTenant") ?? d.PerTenant);
            var seconds = section?.GetValue<int?>("WindowSeconds");
            var policy = d with { PerUser = perUser, PerTenant = perTenant, Window = seconds is > 0 ? TimeSpan.FromSeconds(seconds.Value) : d.Window };
            _policies[policy.Name] = policy;
            _limiters[policy.Name] = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<(string Tenant, string User), string>(key =>
                    RateLimitPartition.GetFixedWindowLimiter($"u:{key.Tenant}:{key.User}", _ => Options(policy.PerUser, policy.Window))),
                PartitionedRateLimiter.Create<(string Tenant, string User), string>(key =>
                    RateLimitPartition.GetFixedWindowLimiter($"t:{key.Tenant}", _ => Options(policy.PerTenant, policy.Window))));
        }
    }

    private static FixedWindowRateLimiterOptions Options(int permits, TimeSpan window) => new()
    {
        PermitLimit = permits, Window = window, QueueLimit = 0, AutoReplenishment = true,
    };

    public TenantUserRateLimitPolicy PolicyFor(string name) => _policies[name];

    /// <summary>Takes one permit from the caller's user window and the tenant window. False = refuse, with the seconds to wait.</summary>
    public bool TryAcquire(string policy, string tenantId, string userId, out int retryAfterSeconds)
    {
        retryAfterSeconds = 0;
        // An unknown policy name is a wiring mistake: refuse rather than leave the route unthrottled.
        if (!_limiters.TryGetValue(policy, out var limiter)) { retryAfterSeconds = 60; return false; }
        var lease = limiter.AttemptAcquire((tenantId, userId));
        if (lease.IsAcquired) { lease.Dispose(); return true; }
        retryAfterSeconds = lease.TryGetMetadata(MetadataName.RetryAfter, out var after)
            ? Math.Max(1, (int)Math.Ceiling(after.TotalSeconds))
            : Math.Max(1, (int)_policies[policy].Window.TotalSeconds);
        lease.Dispose();
        return false;
    }

    public void Dispose()
    {
        foreach (var l in _limiters.Values) l.Dispose();
    }
}

/// <summary>The 429 response, byte-compatible with <see cref="RateLimitRejection"/> (the middleware limiter's own).</summary>
public static class RateLimitResponse
{
    public static async Task WriteAsync(HttpResponse response, int retryAfterSeconds, CancellationToken ct = default)
    {
        response.StatusCode = StatusCodes.Status429TooManyRequests;
        response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        await response.WriteAsJsonAsync(new { error = RateLimitRejection.Error, message = RateLimitRejection.MessageFor(retryAfterSeconds) }, ct);
    }
}

/// <summary>Put on an action: <c>[TenantUserRateLimit("duplicate-check")]</c>. The name selects a <see cref="TenantUserRateLimitPolicy"/>.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class TenantUserRateLimitAttribute : Attribute, IFilterFactory
{
    public TenantUserRateLimitAttribute(string policy) => Policy = policy;
    public string Policy { get; }
    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
    {
        var limiter = (TenantUserRateLimiter?)serviceProvider.GetService(typeof(TenantUserRateLimiter));
        return new TenantUserRateLimitFilter(limiter, Policy);
    }
}

public sealed class TenantUserRateLimitFilter : IAsyncActionFilter
{
    private readonly TenantUserRateLimiter? _limiter;
    private readonly string _policy;

    public TenantUserRateLimitFilter(TenantUserRateLimiter? limiter, string policy)
    {
        _limiter = limiter;
        _policy = policy;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var user = context.HttpContext.User;
        var tenant = user.FindFirstValue("tenant_id");
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        // With no limiter registered (a host that did not call AddOnboardingServices) the filter is inert. A request
        // that reaches an authorised action without a tenant or user identity is refused, never counted as nobody.
        if (_limiter is not null)
        {
            if (string.IsNullOrEmpty(tenant) || string.IsNullOrEmpty(userId))
            {
                context.Result = new UnauthorizedResult();
                return;
            }
            if (!_limiter.TryAcquire(_policy, tenant, userId, out var retryAfter))
            {
                context.Result = new RateLimitedResult(retryAfter);
                return;
            }
        }
        await next();
    }

    private sealed class RateLimitedResult : IActionResult
    {
        private readonly int _seconds;
        public RateLimitedResult(int seconds) => _seconds = seconds;
        public Task ExecuteResultAsync(ActionContext context) =>
            RateLimitResponse.WriteAsync(context.HttpContext.Response, _seconds, context.HttpContext.RequestAborted);
    }
}
