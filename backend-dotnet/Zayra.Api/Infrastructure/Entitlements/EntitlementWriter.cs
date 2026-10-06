using Zayra.Api.Application.Entitlements;

namespace Zayra.Api.Infrastructure.Entitlements;

// Release A slice R2 owns this file. R0 created it as a stub (registered in DI by ReleaseAServiceCollectionExtensions)
// so no slice has to edit Program.cs; the owning slice replaces the body. Shared contracts: Application/Entitlements,
// Application/Contracts. Gated per tenant by the release_a feature flag.

/// <summary>The only writer of employee_entitlements (<see cref="IEntitlementWriter"/>). Slice R2.</summary>
public sealed class EntitlementWriter : IEntitlementWriter
{
    public Task<FreezeResult> FreezeTermAsync(Guid tenantId, Guid contractId, CancellationToken ct) =>
        throw new NotImplementedException("Freezing a contract-year package arrives with Release A slice R2.");

    public Task ApplyRenewalAsync(Guid tenantId, RenewalApplyPlan plan, CancellationToken ct) =>
        throw new NotImplementedException("Writing a renewed package arrives with Release A slice R2.");

    public Task CarryToProvisionalAsync(Guid tenantId, Guid fromContractId, Guid provisionalContractId, CancellationToken ct) =>
        throw new NotImplementedException("Carrying a package into a provisional term arrives with Release A slice R2.");
}
