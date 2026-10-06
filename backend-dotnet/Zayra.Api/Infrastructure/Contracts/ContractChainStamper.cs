using Zayra.Api.Application.Entitlements;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Contracts;

// Release A slice R4 owns this file. R0 created it as a stub (registered in DI by ReleaseAServiceCollectionExtensions)
// so no slice has to edit Program.cs; the owning slice replaces the body. Shared contracts: Application/Entitlements,
// Application/Contracts. Gated per tenant by the release_a feature flag.

/// <summary>
/// Stamps renewed_from, renewal_number and chain_started_on when a term becomes Active (<see cref="IContractTermLifecycle"/>). Slice R4.
/// A no-op until then — never a throw — so contract activation keeps working.
/// </summary>
public sealed class ContractChainStamper : IContractTermLifecycle
{
    public Task OnActivatedAsync(EmployeeContract contract, CancellationToken ct) => Task.CompletedTask;

    public Task OnEndedAsync(EmployeeContract contract, string reason, CancellationToken ct) => Task.CompletedTask;
}
