using Zayra.Api.Models;
using Zayra.Api.Infrastructure.Jobs;

namespace Zayra.Api.Infrastructure.Entitlements;

// Release A slice R2 owns this file. R0 created it as a stub (registered in DI by ReleaseAServiceCollectionExtensions)
// so no slice has to edit Program.cs; the owning slice replaces the body. Shared contracts: Application/Entitlements,
// Application/Contracts. Gated per tenant by the release_a feature flag.

/// <summary>
/// Bulk freeze for existing employees (source Migrated, Unverified, HR confirm list keyed by batch). Slice R2.
/// Until R2 lands, a job of this type fails permanently with a plain reason instead of retrying.
/// </summary>
public sealed class PackageFreezeJobHandler : IBackgroundJobHandler
{
    public const string JobType = "entitlements.package-freeze";

    public static readonly BackgroundJobTypeDescriptor Descriptor = new(
        JobType,
        typeof(PackageFreezeJobHandler),
        ViewPermissions: ["entitlements.manage"],
        CancelPermissions: ["entitlements.manage"],
        KeyRetention: BackgroundJobKeyRetention.WhileActive,
        MaxAttempts: 5);

    public Task ExecuteAsync(JobExecutionContext context) =>
        throw new BackgroundJobPermanentFailureException("Freezing existing employees' packages is not available yet.");
}
