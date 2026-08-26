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
        var resolver = controller.HttpContext?.RequestServices
            ?.GetService(typeof(Zayra.Api.Infrastructure.Scope.IRequestEntityScopeResolver))
            as Zayra.Api.Infrastructure.Scope.IRequestEntityScopeResolver;

        // Unit tests construct controllers with a bare DefaultHttpContext and no service provider.
        // Falling back to a directly-constructed resolver keeps them working AND keeps them honest:
        // it is the same class, applying the same rules, just without the per-request cache.
        resolver ??= new Zayra.Api.Infrastructure.Scope.RequestEntityScopeResolver();
        return resolver.ResolveFor(
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
