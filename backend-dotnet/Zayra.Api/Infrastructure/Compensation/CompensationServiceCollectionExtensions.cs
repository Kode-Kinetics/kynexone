namespace Zayra.Api.Infrastructure.Compensation;

/// <summary>Every CMP registration, called once from Program.cs. CompensationApprovalSync is static and is called from
/// ApprovalWorkflowService.DecideAsync; awards are applied by the existing EffectiveChangeJobHandler.</summary>
public static class CompensationServiceCollectionExtensions
{
    public static IServiceCollection AddCompensation(this IServiceCollection services)
    {
        services.AddScoped<MeritPlanningService>();
        services.AddScoped<PayBenchmarkService>();
        services.AddScoped<PayEquityService>();
        // The effective-date job asks every gate before applying an approved change; this one re-checks merit eligibility.
        services.AddSingleton<Zayra.Api.Infrastructure.Employees.IEffectiveChangeGate, MeritEffectiveChangeGate>();
        return services;
    }
}
