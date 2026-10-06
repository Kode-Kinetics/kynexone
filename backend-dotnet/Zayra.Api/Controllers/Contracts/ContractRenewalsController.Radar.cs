using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Contracts;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Contracts;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Contracts;

/// <summary>POST hold body. <paramref name="Reason"/> is one of <see cref="RenewalHoldReasons"/>.</summary>
public sealed record RenewalHoldRequest(string Reason, string? Note);

/// <summary>POST cancel body: a plain sentence for the audit trail.</summary>
public sealed record RenewalCancelRequest(string Reason);

/// <summary>POST open-now body.</summary>
public sealed record RenewalOpenNowRequest(Guid? CompanyId);

/// <summary>
/// POST chain/confirm body: HR states the term's place in the chain (T2). <paramref name="RenewedFromContractId"/> is the
/// term this one renews when it is on file; <paramref name="RenewalNumber"/> counts renewals before this term (0 = the
/// original), including any made before the system held the contracts.
/// </summary>
public sealed record ChainConfirmRequest(
    Guid? RenewedFromContractId,
    DateOnly ChainStartedOn,
    string WorkerNationalityClass,
    bool AutoRenew,
    short? NonRenewalNoticeDays,
    short RenewalNumber);

/// <summary>One term in an employee's contract history, as the chain drawer lists it.</summary>
public sealed record ChainTermDto(
    Guid ContractId, string ContractNumber, string Status, DateOnly StartDate, DateOnly? EndDate, DateOnly? SignedOn,
    short? RenewalNumber, string LinkKind, Guid? LinkedToContractId, string? GapReason, bool IsCurrent);

/// <summary>GET ~/api/contracts/{id}/chain.</summary>
public sealed record ContractChainDto(
    Guid ContractId,
    IReadOnlyList<ChainTermDto> Terms,
    short? RenewalNumber,
    DateOnly? ChainStartedOn,
    bool Confirmed,
    string? NationalityClass,
    bool AutoRenew,
    short? NonRenewalNoticeDays,
    Art55Meter Art55,
    IReadOnlyList<string> NextAllowedActions,
    IReadOnlyList<BlockReason> BlockReasons,
    Guid? CaseId,
    string? CaseState,
    DateOnly? OpensOn);

// Release A slice R4 owns this file: the renewal dashboard (radar), the shared case DTO, the contract chain, open-now and
// hold / release / cancel (T19–T21). Every action carries an explicit [HasPermission]; the /api/contracts prefix is
// closed unless the tenant has release_a on (FeatureFlagGuardFilter). Reads run under the request's tenant and
// company-scope filters, and every query also pins the tenant explicitly.
public sealed partial class ContractRenewalsController
{
    private const int MaxRadarDays = 366;

    /// <summary>GET /api/contracts/renewals/radar?companyId=&amp;days=120 — buckets, exceptions and rows with their Next line.</summary>
    [HttpGet("radar")]
    [HasPermission("contracts.renewal.read")]
    public async Task<ActionResult<RenewalRadarDto>> Radar([FromServices] RenewalCaseOpener opener, [FromQuery] Guid? companyId,
        [FromQuery] int days = 120, CancellationToken ct = default)
    {
        if (days is < 1 or > MaxRadarDays)
            return BadRequest(new { error = "invalid_days", message = $"Days must be between 1 and {MaxRadarDays}." });
        var tenantId = RequireTenant();
        return Ok(await RenewalCaseReadModel.RadarAsync(_db, opener, tenantId, companyId, days, await TodayAsync(ct), ct));
    }

    /// <summary>GET /api/contracts/renewals/{caseId} — the shared case DTO (R4 drawer, R5 offer editor, R6).</summary>
    [HttpGet("{caseId:guid}")]
    [HasPermission("contracts.renewal.read")]
    public async Task<ActionResult<RenewalCaseDto>> GetCase(Guid caseId, CancellationToken ct)
    {
        var dto = await RenewalCaseReadModel.CaseAsync(_db, RequireTenant(), caseId, await TodayAsync(ct), ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>GET ~/api/contracts/{contractId}/chain — the employee's terms, the Art. 55 meter and what can be done next.</summary>
    [HttpGet("~/api/contracts/{contractId:guid}/chain")]
    [HasPermission("contracts.renewal.read")]
    public async Task<ActionResult<ContractChainDto>> Chain(Guid contractId, CancellationToken ct)
    {
        var dto = await ChainAsync(RequireTenant(), contractId, await TodayAsync(ct), ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>
    /// POST ~/api/contracts/{contractId}/chain/confirm — HR records the term's history (T2). Audited with before and
    /// after. A NeedsConfirmation case is re-derived and moves to Open; a due term with no case gets one now.
    /// </summary>
    [HttpPost("~/api/contracts/{contractId:guid}/chain/confirm")]
    [HasPermission("contracts.renewal.manage")]
    public async Task<ActionResult<ContractChainDto>> ConfirmChain(Guid contractId, [FromBody] ChainConfirmRequest req,
        [FromServices] RenewalCaseOpener opener, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var today = await TodayAsync(ct);
        if (!WorkerNationalityClasses.All.Contains(req.WorkerNationalityClass))
            return BadRequest(new { error = "invalid_nationality_class", message = "Choose Saudi or Non-Saudi." });
        if (req.RenewalNumber < 0 || (req.RenewedFromContractId is not null && req.RenewalNumber < 1))
            return BadRequest(new { error = "invalid_renewal_number", message = "A term that renews an earlier one is renewal 1 or later; the first term is 0." });
        if (req.NonRenewalNoticeDays is < 1 or > 365)
            return BadRequest(new { error = "invalid_notice_days", message = "Notice must be between 1 and 365 days, or left empty for the statutory default." });

        var target = await _db.EmployeeContracts.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.Id == contractId && !c.IsDeleted)
            .Select(c => new { c.EmployeeId }).FirstOrDefaultAsync(ct);
        if (target is null) return NotFound();

        try
        {
            var refusal = await FinanceDecisionSerializer.SerializeAsync<ActionResult?>(_db, FinanceDecisionSerializer.ScopeEmployeePackage, tenantId,
                target.EmployeeId, async () =>
                {
                    // Lock order: case → contract.
                    var renewal = await _db.ContractRenewalCases
                        .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.ExpiringContractId == contractId && c.ClosedAt == null, ct);
                    var contract = await _db.EmployeeContracts.FirstAsync(c => c.TenantId == tenantId && c.Id == contractId, ct);
                    if (contract.ProvisionalBasis is not null)
                        return Conflict(new { error = "provisional_term", message = "This term continues by law and is confirmed when its renewal is applied." });
                    if (req.ChainStartedOn > contract.StartDate)
                        return BadRequest(new { error = "invalid_chain_start", message = "The chain cannot start after this term starts." });
                    if (renewal is not null && !(renewal.State == RenewalStates.NeedsConfirmation
                                                 || (renewal.ContractAction is null && renewal.AllowedActions.Length == 0
                                                     && renewal.State is RenewalStates.Open or RenewalStates.OnHold)))
                        return Conflict(new
                        {
                            error = "renewal_case_in_progress",
                            message = "This term's renewal review already started from confirmed history. Cancel the review before correcting the history.",
                        });
                    if (req.RenewedFromContractId is { } previousId)
                    {
                        var previous = await _db.EmployeeContracts.AsNoTracking()
                            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == previousId && !c.IsDeleted, ct);
                        if (previous is null || previous.EmployeeId != contract.EmployeeId || previous.Id == contract.Id
                            || previous.StartDate >= contract.StartDate)
                            return BadRequest(new { error = "invalid_previous_term", message = "The earlier term must be another, earlier contract of the same employee." });
                        if (await _db.EmployeeContracts.AnyAsync(c => c.TenantId == tenantId && c.RenewedFromContractId == previousId && c.Id != contractId, ct))
                            return Conflict(new { error = "previous_term_already_renewed", message = "Another term is already recorded as the renewal of that contract." });
                    }

                    var before = new { contract.RenewedFromContractId, contract.RenewalNumber, contract.ChainStartedOn, contract.WorkerNationalityClass,
                        contract.AutoRenew, contract.NonRenewalNoticeDays };
                    contract.RenewedFromContractId = req.RenewedFromContractId;
                    contract.RenewalNumber = req.RenewalNumber;
                    contract.ChainStartedOn = req.ChainStartedOn;
                    contract.WorkerNationalityClass = req.WorkerNationalityClass;
                    contract.AutoRenew = req.AutoRenew;
                    contract.NonRenewalNoticeDays = req.NonRenewalNoticeDays;
                    contract.UpdatedAtUtc = DateTime.UtcNow;
                    _db.ComplianceAuditLogs.Add(new ComplianceAuditLog
                    {
                        TenantId = tenantId, EntityType = "Contract", EntityId = contract.Id.ToString(), EmployeeId = contract.EmployeeId,
                        Action = "ChainConfirmed", PerformedByUserId = CurrentUserId(), PerformedByName = ActorName(),
                        MetadataJson = JsonSerializer.Serialize(new { before, after = req }),
                    });
                    if (renewal is not null)
                    {
                        var rules = await RenewalRuleSet.LoadAsync(_db, tenantId, today, ct);
                        RenewalCaseOpener.Rebaseline(_db, renewal, contract, rules, today, "ChainConfirmed", CurrentUserId(), ActorName());
                    }
                    await _db.SaveChangesAsync(ct);
                    return null;
                }, ct);
            if (refusal is not null) return refusal;
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { error = "renewal_case_changed", message = "Someone else changed this renewal at the same time. Reload and try again." });
        }

        // A due term that could not open (no nationality on file) gets its case now that the history is recorded.
        await opener.OpenOneAsync(tenantId, contractId, today, CurrentUserId(), ActorName(), ct);
        return Ok(await ChainAsync(tenantId, contractId, today, ct));
    }

    /// <summary>POST /api/contracts/renewals/open-now {companyId?} — runs the same opener as the daily job, now, for this tenant.</summary>
    [HttpPost("open-now")]
    [HasPermission("contracts.renewal.manage")]
    public async Task<IActionResult> OpenNow([FromBody] RenewalOpenNowRequest? req, [FromServices] RenewalCaseOpener opener, CancellationToken ct)
    {
        var outcomes = await opener.OpenDueAsync(RequireTenant(), req?.CompanyId, await TodayAsync(ct), CurrentUserId(), ActorName(), ct);
        return Ok(new
        {
            opened = outcomes.Count(o => o.Result == RenewalOpenOutcome.Opened),
            alreadyOpen = outcomes.Count(o => o.Result == RenewalOpenOutcome.AlreadyOpen),
            notOpened = outcomes.Where(o => o.Result == RenewalOpenOutcome.Skipped)
                .Select(o => new { o.ContractId, reason = o.SkipReason }).ToList(),
        });
    }

    /// <summary>POST /api/contracts/renewals/{caseId}/hold {reason} — T19.</summary>
    [HttpPost("{caseId:guid}/hold")]
    [HasPermission("contracts.renewal.manage")]
    public Task<IActionResult> Hold(Guid caseId, [FromBody] RenewalHoldRequest req, CancellationToken ct)
    {
        if (!RenewalHoldReasons.All.Contains(req.Reason))
            return Task.FromResult<IActionResult>(BadRequest(new { error = "invalid_hold_reason", message = "Choose why the renewal is on hold." }));
        return MoveAsync(caseId, RenewalStates.OnHold, "Held", ct, c => c.HoldReason = req.Reason, new { reason = req.Reason, note = req.Note });
    }

    /// <summary>POST /api/contracts/renewals/{caseId}/release — T20.</summary>
    [HttpPost("{caseId:guid}/release")]
    [HasPermission("contracts.renewal.manage")]
    public Task<IActionResult> Release(Guid caseId, CancellationToken ct) =>
        MoveAsync(caseId, RenewalStates.Open, "Released", ct, c => c.HoldReason = null, new { });

    /// <summary>POST /api/contracts/renewals/{caseId}/cancel {reason} — T21.</summary>
    [HttpPost("{caseId:guid}/cancel")]
    [HasPermission("contracts.renewal.manage")]
    public Task<IActionResult> Cancel(Guid caseId, [FromBody] RenewalCancelRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Reason) || req.Reason.Length > 500)
            return Task.FromResult<IActionResult>(BadRequest(new { error = "cancel_reason_required", message = "Say why the renewal review is cancelled." }));
        return MoveAsync(caseId, RenewalStates.Cancelled, "Cancelled", ct, c =>
        {
            c.HoldReason = null;
            c.ClosedAt = DateTime.UtcNow;
        }, new { reason = req.Reason.Trim() });
    }

    /// <summary>
    /// One state change: the per-employee lock, the state machine (<see cref="RenewalStateMachine.EnsureCanTransition"/>)
    /// before anything is written, the xmin compare-and-set, and an audit row — then the updated case DTO.
    /// </summary>
    private async Task<IActionResult> MoveAsync(Guid caseId, string to, string action, CancellationToken ct, Action<ContractRenewalCase> apply,
        object metadata)
    {
        var tenantId = RequireTenant();
        var head = await _db.ContractRenewalCases.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.Id == caseId).Select(c => new { c.EmployeeId }).FirstOrDefaultAsync(ct);
        if (head is null) return NotFound();
        try
        {
            var refusal = await FinanceDecisionSerializer.SerializeAsync<IActionResult?>(_db, FinanceDecisionSerializer.ScopeEmployeePackage, tenantId,
                head.EmployeeId, async () =>
                {
                    var c = await _db.ContractRenewalCases.FirstAsync(x => x.TenantId == tenantId && x.Id == caseId, ct);
                    var from = c.State;
                    RenewalStateMachine.Transition transition;
                    try { transition = RenewalStateMachine.EnsureCanTransition(from, to); }
                    catch (RenewalTransitionException)
                    {
                        return Conflict(new
                        {
                            error = "renewal_transition_not_allowed",
                            message = RenewalStates.IsTerminal(from)
                                ? "This renewal review is already closed."
                                : $"A renewal review that is {from} cannot be moved to {to}.",
                            from,
                            to,
                        });
                    }
                    apply(c);
                    c.State = to;
                    _db.ComplianceAuditLogs.Add(RenewalCaseOpener.Audit(tenantId, c, action, CurrentUserId(), ActorName(),
                        new { transition = transition.Id, from, to, detail = metadata }));
                    await _db.SaveChangesAsync(ct);
                    return null;
                }, ct);
            if (refusal is not null) return refusal;
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { error = "renewal_case_changed", message = "Someone else changed this renewal at the same time. Reload and try again." });
        }
        return Ok(await RenewalCaseReadModel.CaseAsync(_db, tenantId, caseId, await TodayAsync(ct), ct));
    }

    private async Task<ContractChainDto?> ChainAsync(Guid tenantId, Guid contractId, DateOnly today, CancellationToken ct)
    {
        var contract = await _db.EmployeeContracts.AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == contractId && !c.IsDeleted, ct);
        if (contract is null) return null;
        var siblings = await _db.EmployeeContracts.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.EmployeeId == contract.EmployeeId && !c.IsDeleted)
            .ToListAsync(ct);
        var employee = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.PublicId == contract.EmployeeId)
            .Select(e => new { e.JoiningDate, e.SaudiOrNonSaudi, e.Nationality })
            .FirstOrDefaultAsync(ct);
        var employeeClass = WorkerNationality.ClassOf(employee?.SaudiOrNonSaudi, employee?.Nationality);
        var stamps = ContractChainLinker.Link(siblings.Select(ContractChainFacts.Of).ToList(),
            ContractChainCensus.JoiningDateOf(employee?.JoiningDate), employeeClass);

        var terms = siblings.Where(c => ContractChainLinker.IsTerm(c.Status) || c.Id == contractId)
            .OrderBy(c => c.StartDate).ThenBy(c => c.Version)
            .Select(c =>
            {
                stamps.TryGetValue(c.Id, out var s);
                return new ChainTermDto(c.Id, c.ContractNumber, c.Status, c.StartDate, c.EndDate, RenewalCaseReadModel.SignedOn(c),
                    c.RenewalNumber ?? s?.RenewalNumber, s?.LinkKind ?? ChainLinkKinds.Unconfirmed, s?.LinkedToContractId, s?.GapReason,
                    c.Id == contractId);
            })
            .ToList();

        var renewal = await _db.ContractRenewalCases.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.ExpiringContractId == contractId)
            .OrderBy(c => c.ClosedAt != null).FirstOrDefaultAsync(ct);
        var rules = await RenewalRuleSet.LoadAsync(_db, tenantId, today, ct);
        var view = RenewalCaseReadModel.Clone(contract, renewal?.WorkerNationalityClass ?? contract.WorkerNationalityClass ?? employeeClass);

        IReadOnlyList<string> actions = [];
        IReadOnlyList<string> blocks = [];
        AllowedActionsResult derived = new([], false, [], view.RenewalNumber, null);
        DateOnly? opensOn = null;
        if (view.EndDate is not null && view.ProvisionalBasis is null)
        {
            var deadlines = RenewalDeadlineCalculator.Compute(view, rules);
            opensOn = deadlines.OpensOn;
            derived = view.WorkerNationalityClass is null
                ? new AllowedActionsResult([], false, [ReleaseABlockReasons.RenewalChainUnconfirmed], view.RenewalNumber, null)
                : AllowedActionsDeriver.Derive(view, renewal?.NoticeDueOn ?? deadlines.NoticeDueOn, today, rules);
            actions = renewal is not null && !RenewalStates.IsTerminal(renewal.State) ? renewal.AllowedActions : derived.Actions;
            blocks = derived.BlockCodes;
        }
        return new ContractChainDto(contract.Id, terms, contract.RenewalNumber, contract.ChainStartedOn,
            AllowedActionsDeriver.IsChainConfirmed(contract), contract.WorkerNationalityClass ?? employeeClass, contract.AutoRenew,
            contract.NonRenewalNoticeDays, AllowedActionsDeriver.Meter(view, derived, rules), actions,
            blocks.Distinct().Select(code => ReleaseABlockReasons.All[code]).ToList(), renewal?.Id, renewal?.State, opensOn);
    }

    private string ActorName() => User.FindFirst("name")?.Value ?? User.Identity?.Name ?? "HR user";
}
