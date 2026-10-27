using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.Employees.Onboarding;

namespace Zayra.Api.Infrastructure.Employees.Onboarding;

/// <summary>
/// Registers the hire input validation pieces. WP-B's <c>AddOnboardingServices()</c> calls this one method, so
/// Program.cs needs a single line for the whole onboarding backend.
/// </summary>
public static class HireValidationRegistration
{
    public static IServiceCollection AddHireValidation(this IServiceCollection services)
    {
        services.AddScoped<IEmployeeInputGuard, EmployeeInputGuard>();
        services.Configure<MvcOptions>(o =>
        {
            o.Filters.Add<EmployeeProblemFilter>();
            o.ModelMetadataDetailsProviders.Add(new DtoBoundsMetadataProvider());
        });
        return services;
    }
}
