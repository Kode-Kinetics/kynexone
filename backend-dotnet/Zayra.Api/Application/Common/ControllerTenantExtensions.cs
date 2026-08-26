using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;

namespace Zayra.Api.Application.Common;

public static class ControllerTenantExtensions
{
    public static Guid? GetTenantId(this ControllerBase controller)
    {
        var value = controller.User.FindFirstValue("tenant_id");
        return Guid.TryParse(value, out var id) ? id : null;
    }

    public static Guid? GetUserId(this ControllerBase controller)
    {
        var value = controller.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? controller.User.FindFirstValue("sub");
        return Guid.TryParse(value, out var id) ? id : null;
    }

    /// <summary>
    /// The request's entity scope, from the ONE authoritative resolver.
    ///
    /// <para>This used to be <c>EntityScopeContext.FromClaims(controller.User)</c> — which took
    /// <c>strictMode: false</c> whatever the deployment was configured for, and ignored the
    /// <c>X-Company-Id</c> switcher entirely. <c>ZayraDbContext</c> honoured both. So a controller
    /// could authorize against a WIDER scope than the database would serve, and on the payment-batch,
    /// GL-export and report paths — whose tables are <c>ITenantOwned</c> with no ambient company
    /// filter — the controller check is the only company control there is. There was no backstop.</para>
    ///
    /// <para>Resolution is cached on <c>HttpContext.Items</c>, so this and the DbContext cannot
    /// disagree within a request however many times either asks.</para>
    /// </summary>
    public static RequestEntityScope GetRequestScope(this ControllerBase controller)
    {
        var services = controller.HttpContext?.RequestServices;
        var resolver = services?.GetService(typeof(Zayra.Api.Infrastructure.Scope.IRequestEntityScopeResolver))
            as Zayra.Api.Infrastructure.Scope.IRequestEntityScopeResolver;

        // Resolve(), not ResolveFor(). ResolveFor neither reads nor writes the per-request cache, so an
        // earlier version of this method re-parsed the claims on every call and agreed with the
        // DbContext only by coincidence — both being deterministic over the same inputs. They would have
        // diverged the moment anything mutated the X-Company-Id header mid-request, and on the
        // payment-batch and GL-export paths the controller decision is the ONLY company control.
        if (resolver is not null) return resolver.Resolve();

        // No service provider: a unit test with a bare DefaultHttpContext. Build the resolver with the
        // SAME options the DI path would give it. Passing none silently yields strictMode=false and a
        // dead platform gate — which is exactly the strict-mode divergence this class exists to remove,
        // reintroduced in the fallback.
        var scopeOptions = services?.GetService(typeof(Microsoft.Extensions.Options.IOptions<EntityScopeOptions>))
            as Microsoft.Extensions.Options.IOptions<EntityScopeOptions>;
        var jwtOptions = services?.GetService(typeof(Microsoft.Extensions.Options.IOptions<Zayra.Api.Application.Auth.JwtOptions>))
            as Microsoft.Extensions.Options.IOptions<Zayra.Api.Application.Auth.JwtOptions>;

        return new Zayra.Api.Infrastructure.Scope.RequestEntityScopeResolver(
                http: null, scopeOptions: scopeOptions, jwtOptions: jwtOptions)
            .ResolveFor(
                controller.User,
                controller.HttpContext?.Request.Headers[Zayra.Api.Data.ZayraDbContext.CompanySelectionHeader].FirstOrDefault());
    }

    /// <summary>
    /// Legacy shape for the call sites that still take an <see cref="EntityScopeContext"/>. It now
    /// derives from the authoritative resolution, so those sites inherit strict mode and switcher
    /// narrowing without each having to remember to ask for them.
    /// </summary>
    public static EntityScopeContext GetEntityScope(this ControllerBase controller)
        => controller.GetRequestScope().ToEntityScopeContext();

    /// <summary>
    /// Resolves the tenant's base currency.
    /// Priority: Company.DefaultCurrency (most-active company) →
    ///           TenantLocalizationSettings.CurrencyCode → "USD" fallback.
    /// Call this instead of hard-coding "USD" or "AED" anywhere money is recorded.
    /// </summary>
    public static async Task<string> ResolveTenantCurrencyAsync(
        this ZayraDbContext db, Guid tenantId, CancellationToken ct = default)
    {
        var companyCurrency = await db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .OrderByDescending(c => c.IsActive)
            .Select(c => c.DefaultCurrency)
            .FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(companyCurrency)) return companyCurrency;

        var locCurrency = await db.TenantLocalizationSettings.AsNoTracking()
            .Where(l => l.TenantId == tenantId)
            .Select(l => l.CurrencyCode)
            .FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(locCurrency) ? "USD" : locCurrency;
    }
}
