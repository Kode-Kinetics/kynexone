using Zayra.Api.Application.Entitlements;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Contracts;

// Release A slice R4 owns this file. R0 created it as a stub (registered in DI by ReleaseAServiceCollectionExtensions)
// so no slice has to edit Program.cs; the owning slice replaces the body. Shared contracts: Application/Entitlements,
// Application/Contracts. Gated per tenant by the release_a feature flag.

/// <summary>
/// Reads the renewal lead times from statutory_rules (tenant row overrides platform) and calls
/// Application.Contracts.RenewalDeadlineFormulas (<see cref="IRenewalDeadlineCalculator"/>). Slice R4.
/// </summary>
public sealed class RenewalDeadlineCalculator : IRenewalDeadlineCalculator
{
    public Task<RenewalDeadlines> ComputeAsync(Guid tenantId, EmployeeContract expiring, CancellationToken ct) =>
        throw new NotImplementedException("Renewal deadlines arrive with Release A slice R4.");
}
