using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Entitlements;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Entitlements;

// Release A slice R2 owns this file. Registered by ReleaseAServiceCollectionExtensions after R4's ContractChainStamper.
// Runs only for release_a tenants (ContractTermLifecycleDispatcher), inside the caller's unit of work, before its SaveChanges.

/// <summary>
/// The package side of a contract term's life (<see cref="IContractTermLifecycle"/>):
/// <list type="bullet">
/// <item><b>Activated</b> — freeze the contract-year package (an amendment carries its predecessor's rows forward).</item>
/// <item><b>Ended</b> — close the term's open rows (<see cref="EntitlementWriter.CloseForEndAsync"/>).</item>
/// </list>
/// Both stage changes on the same context so they commit with the status change or not at all. Neither throws, with one
/// deliberate exception: <see cref="EntitlementLifecycleBlockedException"/>, when the change would need a fixed benefit that
/// never took effect to be removed (the database never removes one). The status change is then refused with a 409 and a
/// reason, never left half-done or silently skipped. Anything else is logged and shown on the package panel.
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
            var skips = (_writer as EntitlementWriter)?.LastSkips ?? [];
            _log.LogInformation("Package freeze on activation of contract {ContractId}: frozen={Frozen} already={Already} rows={Rows} skipped={Skipped}",
                contract.Id, result.Frozen, result.AlreadyFrozen, result.RowsWritten, string.Join(",", skips.Select(s => $"{s.ComponentCode}:{s.Code}")));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the request itself was abandoned; activation is not happening either
        }
        catch (EntitlementLifecycleBlockedException)
        {
            throw; // deliberate: the change would need a fixed row removed, which the database never does — the caller answers 409
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Package not frozen on activation of contract {ContractId}; activation continues", contract.Id);
        }
    }

    public async Task OnEndedAsync(EmployeeContract contract, string reason, CancellationToken ct)
    {
        try
        {
            if (_writer is not EntitlementWriter writer) return;
            var closed = await writer.CloseForEndAsync(contract.TenantId, contract, reason, ct);
            _log.LogInformation("Package closed for contract {ContractId} ({Reason}): {Rows} rows", contract.Id, reason, closed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (EntitlementLifecycleBlockedException)
        {
            throw; // deliberate: ending the term would leave a benefit that never took effect live — the caller answers 409
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Package not closed for contract {ContractId} ({Reason}); the status change continues", contract.Id, reason);
        }
    }
}
