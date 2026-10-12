namespace Zayra.Api.Infrastructure.Engagement;

/// <summary>
/// ENG — every engagement survey service in one place, so Program.cs carries one line. The module is gated per tenant
/// by the opt-in <c>engagement</c> flag (OptInFeatures): off unless the platform team enables it.
/// </summary>
public static class EngagementServiceCollectionExtensions
{
    public static IServiceCollection AddEngagement(this IServiceCollection services)
    {
        services.AddScoped<EngagementSurveyService>();
        services.AddScoped<EngagementResponseService>();
        services.AddScoped<EngagementResultsService>();
        services.AddScoped<EngagementActionService>();
        services.AddScoped<Retention.IRetentionRule, Retention.Rules.EngagementFreeTextRetentionRule>();
        services.AddHostedService<EngagementSurveyWorker>();
        return services;
    }
}
