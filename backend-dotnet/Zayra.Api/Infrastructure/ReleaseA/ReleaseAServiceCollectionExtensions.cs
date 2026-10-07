using Zayra.Api.Application.Common;
using Zayra.Api.Application.Entitlements;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Contracts;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Infrastructure.Payroll;

namespace Zayra.Api.Infrastructure.ReleaseA;

/// <summary>
/// Every Release A service registration, in one place, called once from Program.cs. Owned by the integration
/// owner (R0): slices R1–R6 fill the classes registered here and never edit Program.cs. A slice that needs a NEW
/// registration asks the integration owner, who adds it here — that is how two slices never race on Program.cs.
/// Every Release A surface is additionally gated per tenant by the release_a opt-in flag.
/// </summary>
public static class ReleaseAServiceCollectionExtensions
{
    public static IServiceCollection AddReleaseA(this IServiceCollection services)
    {
        // Shared (R0)
        services.AddScoped<ITenantClock, TenantClock>();
        services.AddScoped<IContractTermLifecycleDispatcher, ContractTermLifecycleDispatcher>();

        // R1 — benefits-by-grade matrix
        services.AddScoped<EntitlementMatrixService>();

        // R2 — package resolver, writer, freeze
        services.AddScoped<IEntitlementResolver, EntitlementResolver>();
        services.AddScoped<IEntitlementWriter, EntitlementWriter>();
        services.AddSingleton(PackageFreezeJobHandler.Descriptor);
        services.AddScoped<PackageFreezeJobHandler>();

        // R3 — deductions statement
        services.AddScoped<IDeductionStatementService, DeductionStatementService>();

        // R4 — chain, deadlines, daily renewal job
        services.AddScoped<ContractChainCensus>();
        services.AddScoped<AllowedActionsDeriver>();
        services.AddScoped<IRenewalDeadlineCalculator, RenewalDeadlineCalculator>();
        services.AddSingleton(RenewalCaseJobHandler.Descriptor);
        services.AddScoped<RenewalCaseJobHandler>();
        // R4 additions (flagged in the R4 PR for the integration owner): the opener and reminders the job and the
        // radar API share, and the hourly scheduler that enqueues one job per release_a tenant per day.
        services.AddScoped<RenewalCaseOpener>();
        services.AddScoped<RenewalReminderService>();
        services.AddHostedService<RenewalCaseScheduler>();

        // Term activation hooks, in order: R4 stamps the chain, then R2 freezes the package for that term.
        services.AddScoped<IContractTermLifecycle, ContractChainStamper>();
        services.AddScoped<IContractTermLifecycle, PackageFreezeOnActivation>();

        // R5 — offer and approvals (ContractRenewalApprovalSync is static, called from ApprovalWorkflowService)
        services.AddScoped<RenewalOfferService>();
        services.AddScoped<RenewalOfferValidator>();

        // R6 — response, Qiwa evidence, apply, holdover
        services.AddScoped<RenewalResponseService>();
        services.AddScoped<QiwaEvidenceService>();
        services.AddScoped<RenewalApplyService>();
        services.AddScoped<RenewalHoldoverStep>();

        return services;
    }
}
