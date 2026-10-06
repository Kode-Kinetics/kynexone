using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Zayra.Api.Infrastructure.Modules;

namespace Zayra.Api.Infrastructure.Filters;

/// <summary>
/// Refuses an action with 403 <c>feature_not_enabled</c> unless the tenant has the opt-in feature switched on.
/// For Release A endpoints that live under a prefix <see cref="OptInFeatures"/> does not own — e.g. a deductions
/// statement under <c>/api/payroll</c> — so the release_a flag gates them exactly like the prefixed ones.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequireOptInFeatureAttribute : Attribute, IAsyncActionFilter
{
    public RequireOptInFeatureAttribute(string featureKey)
    {
        if (!OptInFeatures.IsOptIn(featureKey))
            throw new ArgumentException($"'{featureKey}' is not an opt-in feature (OptInFeatures).", nameof(featureKey));
        FeatureKey = featureKey;
    }

    public string FeatureKey { get; }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!Guid.TryParse(context.HttpContext.User.FindFirstValue("tenant_id"), out var tenantId))
        {
            // No tenant context: authentication and authorization decide; a feature flag has nothing to say.
            await next();
            return;
        }

        var modules = context.HttpContext.RequestServices.GetRequiredService<ITenantModuleService>();
        var state = await modules.GetStateAsync(tenantId, context.HttpContext.RequestAborted);
        if (state.IsEnabled(FeatureKey))
        {
            await next();
            return;
        }

        context.Result = new ObjectResult(new
        {
            error = "feature_not_enabled",
            feature = FeatureKey,
            message = "This feature is not enabled for your account yet.",
        })
        { StatusCode = StatusCodes.Status403Forbidden };
    }
}
