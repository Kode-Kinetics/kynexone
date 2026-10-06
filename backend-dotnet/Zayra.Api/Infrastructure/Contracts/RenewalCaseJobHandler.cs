using Zayra.Api.Models;
using Zayra.Api.Infrastructure.Jobs;

namespace Zayra.Api.Infrastructure.Contracts;

// Release A slice R4 owns this file. R0 created it as a stub (registered in DI by ReleaseAServiceCollectionExtensions)
// so no slice has to edit Program.cs; the owning slice replaces the body. Shared contracts: Application/Entitlements,
// Application/Contracts. Gated per tenant by the release_a feature flag.

/// <summary>
/// The daily per-tenant renewal job: opens cases (T1, idempotent through the UNIQUE), sends deadline reminders,
/// and flags expired terms with no outcome for R6's holdover. Slice R4 (holdover step: R6). Until R4 lands, a job
/// of this type fails permanently with a plain reason instead of retrying.
/// </summary>
public sealed class RenewalCaseJobHandler : IBackgroundJobHandler
{
    public const string JobType = "contracts.renewal-cases";

    public static readonly BackgroundJobTypeDescriptor Descriptor = new(
        JobType,
        typeof(RenewalCaseJobHandler),
        ViewPermissions: ["contracts.renewal.read"],
        CancelPermissions: ["contracts.renewal.manage"],
        KeyRetention: BackgroundJobKeyRetention.WhileActive,
        MaxAttempts: 5);

    public Task ExecuteAsync(JobExecutionContext context) =>
        throw new BackgroundJobPermanentFailureException("Opening contract renewal cases is not available yet.");
}
