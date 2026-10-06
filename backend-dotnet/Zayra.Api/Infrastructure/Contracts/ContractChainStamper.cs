using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Entitlements;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Contracts;

/// <summary>
/// Stamps renewed_from, renewal_number, chain_started_on and the worker's nationality class when a term becomes
/// Active (<see cref="IContractTermLifecycle"/>, run FIRST by the dispatcher, before R2's package freeze, inside the
/// activation's SaveChanges). The rule is <see cref="ContractChainLinker"/>, run over all of the employee's rows, so
/// the stamp agrees with what the census would write. Only NULL fields are filled — a value HR recorded or an
/// earlier stamp is never overwritten — so activating, or re-running, is idempotent.
///
/// <para><b>Never throws</b> (the lifecycle contract): a term that cannot be linked simply stays unstamped and its
/// renewal case opens NeedsConfirmation; an unexpected failure is logged and leaves the row as it was, so contract
/// activation keeps working exactly as before Release A.</para> Slice R4.
/// </summary>
public sealed class ContractChainStamper : IContractTermLifecycle
{
    private readonly ZayraDbContext _db;
    private readonly ILogger<ContractChainStamper> _log;

    public ContractChainStamper(ZayraDbContext db, ILogger<ContractChainStamper> log)
    {
        _db = db;
        _log = log;
    }

    public async Task OnActivatedAsync(EmployeeContract contract, CancellationToken ct)
    {
        try
        {
            if (AllowedActionsDeriver.IsChainConfirmed(contract) && contract.WorkerNationalityClass is not null) return;

            var siblings = await _db.EmployeeContracts.AsNoTracking()
                .Where(c => c.TenantId == contract.TenantId && c.EmployeeId == contract.EmployeeId && !c.IsDeleted && c.Id != contract.Id)
                .ToListAsync(ct);
            var rules = await RenewalRuleSet.LoadAsync(_db, contract.TenantId, DateOnly.FromDateTime(DateTime.UtcNow), ct);
            var stamps = await ContractChainCensus.LinkEmployeeAsync(_db, contract.TenantId, contract.EmployeeId,
                siblings.Append(contract).ToList(), rules, ct);
            if (stamps.TryGetValue(contract.Id, out var stamp))
                ContractChainLinker.Apply(contract, stamp);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Contract chain stamp skipped for contract {ContractId}; its renewal case will ask HR to confirm the history.",
                contract.Id);
        }
    }

    /// <summary>
    /// The term stopped being in force by termination or separation: its open renewal review has nothing left to decide,
    /// so it is cancelled (T21) with an audit row naming why, in the caller's unit of work. <b>Expiry never
    /// cancels</b>: a term that reaches its end date with the review still open continues by law (Art. 74(2)) — that is
    /// R6's holdover (T22), and the dashboard shows it as "Expired — holdover pending". <b>Supersede never cancels</b>
    /// either: an amendment carries the review onto the new version (ContractsController.Supersede). Never throws: should it fail,
    /// the daily job's reconcile step cancels the case on its next run.
    /// </summary>
    public async Task OnEndedAsync(EmployeeContract contract, string reason, CancellationToken ct)
    {
        // Expiry: the holdover (R6). Supersede: the Supersede endpoint carries the case onto the new version itself.
        if (reason is ContractEndReasons.Expired or ContractEndReasons.Superseded) return;
        try
        {
            var versions = await RenewalTermVersions.LoadAsync(_db, contract.TenantId, contract.EmployeeId, ct);
            var open = (await _db.ContractRenewalCases
                    .Where(c => c.TenantId == contract.TenantId && c.EmployeeId == contract.EmployeeId && c.ClosedAt == null)
                    .ToListAsync(ct))
                .Where(c => c.ExpiringContractId == contract.Id || versions.SameTerm(c.ExpiringContractId, contract.Id))
                .ToList();
            foreach (var c in open)
            {
                var from = c.State;
                var transition = RenewalCaseTransitions.Cancel(c, DateTime.UtcNow);
                _db.ComplianceAuditLogs.Add(RenewalCaseOpener.Audit(contract.TenantId, c, "Cancelled", null, "kynexone:contract-ended",
                    new { transition = transition.Id, from, reason = "ContractEnded", contractEnd = reason, versionContractId = contract.Id }));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Renewal case for ended contract {ContractId} was not cancelled; the daily job will reconcile it.", contract.Id);
        }
    }
}
