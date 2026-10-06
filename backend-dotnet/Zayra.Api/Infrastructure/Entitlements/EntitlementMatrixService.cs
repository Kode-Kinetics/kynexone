namespace Zayra.Api.Infrastructure.Entitlements;

// Release A slice R1 owns this file. R0 created it as a stub (registered in DI by ReleaseAServiceCollectionExtensions)
// so no slice has to edit Program.cs; the owning slice replaces the body. Shared contracts: Application/Entitlements,
// Application/Contracts. Gated per tenant by the release_a feature flag.

/// <summary>Benefits-by-grade matrix: read, publish (close and insert per changed cell), offerings, pay-scale import. Slice R1.</summary>
public sealed class EntitlementMatrixService
{
}
