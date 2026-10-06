namespace Zayra.Api.Infrastructure.Contracts;

// Release A slice R6 owns this file. R0 created it as a stub (registered in DI by ReleaseAServiceCollectionExtensions)
// so no slice has to edit Program.cs; the owning slice replaces the body. Shared contracts: Application/Entitlements,
// Application/Contracts. Gated per tenant by the release_a feature flag.

/// <summary>Apply in one transaction under the employee lock: new or promoted term, salary row, IEntitlementWriter.ApplyRenewalAsync. Slice R6.</summary>
public sealed class RenewalApplyService
{
}
