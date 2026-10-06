namespace Zayra.Api.Infrastructure.Contracts;

// Release A slice R4 owns this file. R0 created it as a stub (registered in DI by ReleaseAServiceCollectionExtensions)
// so no slice has to edit Program.cs; the owning slice replaces the body. Shared contracts: Application/Entitlements,
// Application/Contracts. Gated per tenant by the release_a feature flag.

/// <summary>Links existing Superseded/Version contract rows into chains and derives renewal_number and chain_started_on; unlinkable chains go to NeedsConfirmation. Slice R4.</summary>
public sealed class ContractChainCensus
{
}
