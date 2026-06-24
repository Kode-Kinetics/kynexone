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

    public static EntityScopeContext GetEntityScope(this ControllerBase controller)
        => EntityScopeContext.FromClaims(controller.User);

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
