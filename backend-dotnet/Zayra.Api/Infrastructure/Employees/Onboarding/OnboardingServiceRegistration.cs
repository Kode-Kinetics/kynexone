using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Infrastructure.Http;

namespace Zayra.Api.Infrastructure.Employees.Onboarding;

public static class OnboardingServiceRegistration
{
    /// <summary>Registers the create-time integrity services: the per-user/per-tenant limiter (singleton, so the
    /// windows are shared across requests) and the draft discard service.</summary>
    public static IServiceCollection AddOnboardingServices(this IServiceCollection services)
    {
        services.AddSingleton<TenantUserRateLimiter>();
        services.AddScoped<DraftDiscardService>();
        return services;
    }
}
