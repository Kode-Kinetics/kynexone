using Zayra.Api.Application.Entitlements;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Entitlements;

// Release A slice R2 owns this file. R0 created it as a stub (registered in DI by ReleaseAServiceCollectionExtensions)
// so no slice has to edit Program.cs; the owning slice replaces the body. Shared contracts: Application/Entitlements,
// Application/Contracts. Gated per tenant by the release_a feature flag.

/// <summary>
/// Freezes the contract-year package when a term becomes Active (<see cref="IContractTermLifecycle"/>). Slice R2.
/// Deliberately a no-op until then — never a throw — because contract activation runs through it for every
/// release_a tenant, and activation must keep working while R2 is in flight.
/// </summary>
public sealed class PackageFreezeOnActivation : IContractTermLifecycle
{
    public Task OnActivatedAsync(EmployeeContract contract, CancellationToken ct) => Task.CompletedTask;

    public Task OnEndedAsync(EmployeeContract contract, string reason, CancellationToken ct) => Task.CompletedTask;
}
