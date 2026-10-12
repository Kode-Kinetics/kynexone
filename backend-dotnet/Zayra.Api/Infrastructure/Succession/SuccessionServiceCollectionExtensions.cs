using Zayra.Api.Infrastructure.Learning;
using Zayra.Api.Infrastructure.Retention;

namespace Zayra.Api.Infrastructure.Succession;

/// <summary>
/// Every SUC registration in one place, called once from Program.cs (the AddLearning pattern). Every SUC surface is also
/// gated per tenant by the succession opt-in flag (OptInFeatures).
/// </summary>
public static class SuccessionServiceCollectionExtensions
{
    public static IServiceCollection AddSuccession(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SuccessionOptions>(configuration.GetSection(SuccessionOptions.Section));
        services.AddScoped<SuccessionAccess>();
        services.AddScoped<SuccessionPositionService>();
        services.AddScoped<SuccessionNominationService>();
        services.AddScoped<TalentAssessmentService>();
        services.AddScoped<SuccessionSlateService>();
        services.AddScoped<DevelopmentPlanService>();
        services.AddScoped<SuccessionReportService>();
        services.AddScoped<SuccessionSweepService>();
        // LRN → SUC: a completed learning assignment completes the development-plan action that links it.
        services.AddScoped<ILearningAssignmentCompletedListener, DevelopmentPlanLearningListener>();
        // D3 retention: 3 years after employment ends, anonymise (never delete).
        services.AddScoped<IRetentionRule, SuccessionRetentionJob>();
        services.AddHostedService<SuccessionEligibilitySweep>();
        return services;
    }
}
