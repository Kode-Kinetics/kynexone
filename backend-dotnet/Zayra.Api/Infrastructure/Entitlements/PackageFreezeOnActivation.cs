using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Entitlements;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Entitlements;

// Release A slice R2 owns this file. Registered by ReleaseAServiceCollectionExtensions after R4's ContractChainStamper,
// so the package is frozen for a term whose place in the chain is already stamped. Runs only for release_a tenants
// (ContractTermLifecycleDispatcher), inside ContractsController.UpdateStatus → Active, before its SaveChanges.

/// <summary>
/// Freezes the contract-year package when a term becomes Active (<see cref="IContractTermLifecycle"/>). It stages the
/// rows on the same context, so they commit with the activation or not at all. It never throws: a package that cannot
/// be frozen (no grade, no company on the contract, nothing in the grade table yet) must not block a signed contract
/// from becoming active — HR sees "not yet fixed" on the package panel and can freeze it there once the gap is fixed.
/// </summary>
public sealed class PackageFreezeOnActivation : IContractTermLifecycle
{
    private readonly IEntitlementWriter _writer;
    private readonly ILogger<PackageFreezeOnActivation> _log;

    public PackageFreezeOnActivation(IEntitlementWriter writer, ILogger<PackageFreezeOnActivation>? log = null)
    {
        _writer = writer;
        _log = log ?? NullLogger<PackageFreezeOnActivation>.Instance;
    }

    public async Task OnActivatedAsync(EmployeeContract contract, CancellationToken ct)
    {
        try
        {
            var result = await _writer.FreezeTermAsync(contract.TenantId, contract.Id, ct);
            _log.LogInformation("Package freeze on activation of contract {ContractId}: frozen={Frozen} already={Already} rows={Rows}",
                contract.Id, result.Frozen, result.AlreadyFrozen, result.RowsWritten);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the request itself was abandoned; activation is not happening either
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Package not frozen on activation of contract {ContractId}; activation continues", contract.Id);
        }
    }
}
