using Zayra.Api.Application.Entitlements;
using Zayra.Api.Infrastructure.Modules;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Contracts;

/// <summary>Runs every <see cref="IContractTermLifecycle"/> when a contract term becomes Active.</summary>
public interface IContractTermLifecycleDispatcher
{
    /// <summary>
    /// Called by ContractsController.UpdateStatus → Active before its SaveChanges, so whatever the hooks add (the
    /// frozen package, the chain stamp) commits with the activation or not at all. A no-op for tenants without
    /// the release_a flag: their contract activation is exactly what it was.
    /// </summary>
    Task OnActivatedAsync(EmployeeContract contract, CancellationToken ct);

    /// <summary>An Active term ended (terminated, expired, superseded, separated). Same flag gate and unit of work.</summary>
    Task OnEndedAsync(EmployeeContract contract, string reason, CancellationToken ct);
}

/// <summary>Release A owner: R0 (integration owner). The hooks themselves belong to R2 and R4.</summary>
public sealed class ContractTermLifecycleDispatcher : IContractTermLifecycleDispatcher
{
    private readonly IEnumerable<IContractTermLifecycle> _hooks;
    private readonly ITenantModuleService _modules;

    public ContractTermLifecycleDispatcher(IEnumerable<IContractTermLifecycle> hooks, ITenantModuleService modules)
    {
        _hooks = hooks;
        _modules = modules;
    }

    public async Task OnActivatedAsync(EmployeeContract contract, CancellationToken ct)
    {
        var state = await _modules.GetStateAsync(contract.TenantId, ct);
        if (!state.IsEnabled(FeatureKeys.ReleaseA)) return;
        // Order is registration order (ReleaseAServiceCollectionExtensions): chain first, then the package freeze,
        // because the frozen package belongs to a term whose place in the chain is known.
        foreach (var hook in _hooks)
            await hook.OnActivatedAsync(contract, ct);
    }

    public async Task OnEndedAsync(EmployeeContract contract, string reason, CancellationToken ct)
    {
        if (!ContractEndReasons.All.Contains(reason))
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "Not a contract end reason.");
        var state = await _modules.GetStateAsync(contract.TenantId, ct);
        if (!state.IsEnabled(FeatureKeys.ReleaseA)) return;
        foreach (var hook in _hooks)
            await hook.OnEndedAsync(contract, reason, ct);
    }
}
