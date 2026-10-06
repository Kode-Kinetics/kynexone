using System.Text.Json;
using Microsoft.AspNetCore.Http;
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
/// original), including any made before the system held the contracts. <paramref name="AcknowledgeContradiction"/> is HR's
/// explicit "I've checked" when the history contradicts the nearest earlier history HR already recorded.
/// </summary>
public sealed record ChainConfirmRequest(
    Guid? RenewedFromContractId,
    DateOnly ChainStartedOn,
    string WorkerNationalityClass,
    bool AutoRenew,
    short? NonRenewalNoticeDays,
    short RenewalNumber,
    bool AcknowledgeContradiction = false);

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

    /// <summary>GET /api/contracts/renewals/radar?companyId=&amp;days= — buckets, exceptions and rows with their Next line.
    /// Without <c>days</c> the window is the lead a case opens at under the tenant's rules.</summary>
    [HttpGet("radar")]
    [HasPermission("contracts.renewal.read")]
    public async Task<ActionResult<RenewalRadarDto>> Radar([FromServices] RenewalCaseOpener opener, [FromQuery] Guid? companyId,
        [FromQuery] int? days = null, CancellationToken ct = default)
    {
        if (days is < 1 or > MaxRadarDays)
            return Invalid("invalid_days", $"Days must be between 1 and {MaxRadarDays}.", $"يجب أن يكون عدد الأيام بين 1 و{MaxRadarDays}.");
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
            return Invalid("invalid_nationality_class", "Choose Saudi or Non-Saudi.", "اختر سعودي أو غير سعودي.");
        if (req.RenewalNumber < 0 || (req.RenewedFromContractId is not null && req.RenewalNumber < 1))
            return Invalid("invalid_renewal_number", "A term that renews an earlier one is renewal 1 or later; the first term is 0.",
                "العقد الذي يجدد عقداً سابقاً هو التجديد رقم 1 أو أكثر، وأول عقد رقمه 0.");
        if (req.NonRenewalNoticeDays is < 1 or > 365)
            return Invalid("invalid_notice_days", "Notice must be between 1 and 365 days, or left empty for the statutory default.",
                "يجب أن تكون مدة الإشعار بين 1 و365 يوماً، أو اتركها فارغة لتطبيق المدة النظامية.");

        var target = await _db.EmployeeContracts.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.Id == contractId && !c.IsDeleted)
            .Select(c => new { c.EmployeeId }).FirstOrDefaultAsync(ct);
        if (target is null) return NotFound();

        try
        {
            var refusal = await FinanceDecisionSerializer.SerializeAsync<ActionResult?>(_db, FinanceDecisionSerializer.ScopeEmployeePackage, tenantId,
                target.EmployeeId, async () =>
                {
                    // Lock order: case → contract. Every term of the employee is loaded tracked: the confirmation may
                    // re-derive the later, Derived, terms of the same chain.
                    var cases = await _db.ContractRenewalCases
                        .Where(c => c.TenantId == tenantId && c.EmployeeId == target.EmployeeId && c.ClosedAt == null)
                        .ToListAsync(ct);
                    var terms = await _db.EmployeeContracts
                        .Where(c => c.TenantId == tenantId && c.EmployeeId == target.EmployeeId && !c.IsDeleted)
                        .ToListAsync(ct);
                    var contract = terms.Single(c => c.Id == contractId);
                    if (contract.ProvisionalBasis is not null)
                        return Invalid("provisional_term", "This term continues by law and is confirmed when its renewal is applied.",
                            "هذا العقد مستمر بحكم النظام ويُؤكَّد عند تطبيق تجديده.", StatusCodes.Status409Conflict);
                    if (req.ChainStartedOn > contract.StartDate)
                        return Invalid("invalid_chain_start", "The first contract cannot start after this one starts.",
                            "لا يمكن أن يبدأ أول عقد بعد بداية هذا العقد.");

                    // The term itself and every LATER term not recorded by HR (Derived, or never stamped) is re-derived from
                    // the history HR records now: none of their reviews may be past preparation.
                    var versions = await RenewalTermVersions.LoadAsync(_db, tenantId, target.EmployeeId, ct);
                    var descendants = terms.Where(t => t.Id != contract.Id && t.StartDate > contract.StartDate
                                                       && t.ChainSource != ChainSources.Recorded).ToList();
                    var affectedTerms = descendants.Select(d => versions.Root(d.Id)).Append(versions.Root(contract.Id)).ToHashSet();
                    var affectedCases = cases.Where(c => affectedTerms.Contains(versions.Root(c.ExpiringContractId))).ToList();
                    if (affectedCases.Any(c => !IsRederivable(c)))
                        return Refuse(StatusCodes.Status409Conflict, ReleaseABlockReasons.RenewalCaseInProgress);

                    if (req.RenewedFromContractId is { } previousId)
                    {
                        var previous = terms.FirstOrDefault(c => c.Id == previousId);
                        if (previous is null || previous.Id == contract.Id || previous.StartDate >= contract.StartDate)
                            return Invalid("invalid_previous_term", "The earlier contract must be another, earlier contract of the same employee.",
                                "يجب أن يكون العقد السابق عقداً آخر أقدم للموظف نفسه.");
                        if (previous.CompanyId != contract.CompanyId)
                            return Invalid("previous_term_other_employer", "The earlier contract was with another company. Article 55 counts renewals with the same employer only.",
                                "كان العقد السابق مع منشأة أخرى، والمادة 55 لا تحتسب إلا التجديدات لدى صاحب العمل نفسه.");
                        if (terms.Any(c => c.RenewedFromContractId == previousId && c.Id != contractId && c.ChainSource != ChainSources.Derived))
                            return Invalid("previous_term_already_renewed", "Another contract is already recorded as the renewal of that contract.",
                                "يوجد عقد آخر مسجل بالفعل على أنه تجديد لذلك العقد.", StatusCodes.Status409Conflict);
                    }

                    // The nearest earlier history HR already recorded must agree with this one (same chain start, a higher
                    // renewal number for a later term) — or HR says explicitly that it has checked the contradiction.
                    var recorded = terms.Where(t => t.Id != contract.Id && t.ChainSource == ChainSources.Recorded && t.StartDate < contract.StartDate
                                                    && t.RenewalNumber is not null && t.ChainStartedOn is not null)
                        .OrderByDescending(t => t.StartDate).FirstOrDefault();
                    if (recorded is not null && !req.AcknowledgeContradiction)
                    {
                        var minimum = recorded.RenewalNumber!.Value + (versions.SameTerm(recorded.Id, contract.Id) ? 0 : 1);
                        if (req.ChainStartedOn != recorded.ChainStartedOn || req.RenewalNumber < minimum)
                            return StatusCode(StatusCodes.Status409Conflict, new
                            {
                                error = "chain_contradicts_recorded_history",
                                message = $"This contradicts the history already recorded for {recorded.ContractNumber}: renewal #{recorded.RenewalNumber}, "
                                          + $"chain from {recorded.ChainStartedOn:yyyy-MM-dd}. Check the signed contracts, then confirm again with \"I've checked\".",
                                messageAr = $"يتعارض هذا مع السجل المؤكد سابقاً للعقد {recorded.ContractNumber}: التجديد رقم {recorded.RenewalNumber}، "
                                            + $"وبداية السلسلة {recorded.ChainStartedOn:yyyy-MM-dd}. راجع العقود الموقعة، ثم أكّد مرة أخرى مع اختيار \"تحققت من ذلك\".",
                                recorded = new { recorded.Id, recorded.ContractNumber, recorded.RenewalNumber, recorded.ChainStartedOn },
                            });
                    }

                    string Sig(EmployeeContract t) => string.Join("|", t.RenewedFromContractId, t.RenewalNumber, t.ChainStartedOn, t.ChainSource,
                        t.WorkerNationalityClass, t.AutoRenew, t.NonRenewalNoticeDays);
                    var before = new { contract.RenewedFromContractId, contract.RenewalNumber, contract.ChainStartedOn, contract.WorkerNationalityClass,
                        contract.AutoRenew, contract.NonRenewalNoticeDays, contract.ChainSource };
                    var signatures = terms.ToDictionary(t => t.Id, Sig);
                    // Clear the later Derived stamps first so the recorded history is what they are derived from.
                    foreach (var d in descendants) ContractChainLinker.ClearDerived(d);
                    if (req.RenewedFromContractId is { } claimedPrevious)
                        foreach (var other in terms.Where(t => t.Id != contract.Id && t.RenewedFromContractId == claimedPrevious))
                            ContractChainLinker.ClearDerived(other);
                    contract.RenewedFromContractId = req.RenewedFromContractId;
                    contract.RenewalNumber = req.RenewalNumber;
                    contract.ChainStartedOn = req.ChainStartedOn;
                    contract.ChainSource = ChainSources.Recorded;
                    contract.WorkerNationalityClass = req.WorkerNationalityClass;
                    contract.AutoRenew = req.AutoRenew;
                    contract.NonRenewalNoticeDays = req.NonRenewalNoticeDays;

                    var rules = await RenewalRuleSet.LoadAsync(_db, tenantId, today, ct);
                    var stamps = await ContractChainCensus.LinkEmployeeAsync(_db, tenantId, target.EmployeeId, terms, rules, ct);
                    var rederived = new List<object>();
                    foreach (var d in descendants)
                    {
                        if (stamps.TryGetValue(d.Id, out var stamp)) ContractChainLinker.Apply(d, stamp);
                        if (d.WorkerNationalityClass is null) d.WorkerNationalityClass = contract.WorkerNationalityClass;
                    }
                    // Only what actually changed is touched and audited: re-confirming the same history writes nothing.
                    var changed = false;
                    foreach (var t in terms.Where(t => Sig(t) != signatures[t.Id]))
                    {
                        t.UpdatedAtUtc = DateTime.UtcNow;
                        changed = true;
                        if (t.Id != contract.Id) rederived.Add(new { t.Id, t.ContractNumber, t.RenewalNumber, t.ChainStartedOn });
                    }
                    if (changed)
                        _db.ComplianceAuditLogs.Add(new ComplianceAuditLog
                        {
                            TenantId = tenantId, EntityType = "Contract", EntityId = contract.Id.ToString(), EmployeeId = contract.EmployeeId,
                            Action = "ChainConfirmed", PerformedByUserId = CurrentUserId(), PerformedByName = ActorName(),
                            MetadataJson = JsonSerializer.Serialize(new { before, after = req, rederived, acknowledgedContradiction = req.AcknowledgeContradiction }),
                        });
                    // Every open review of an affected term — including one still waiting in NeedsConfirmation — is
                    // re-baselined on its term's CURRENT version (T2 when its history is now confirmed).
                    foreach (var c in affectedCases)
                    {
                        var currentId = versions.Current(c.ExpiringContractId)?.Id ?? c.ExpiringContractId;
                        if (terms.FirstOrDefault(t => t.Id == currentId) is not { } current) continue;
                        if (RenewalCaseOpener.Rebaseline(_db, c, current, rules, today,
                                versions.SameTerm(c.ExpiringContractId, contract.Id) ? "ChainConfirmed" : "EarlierTermConfirmed", CurrentUserId(), ActorName(),
                                termStartedOn: versions.TermStartedOn(current.Id)))
                            changed = true;
                    }
                    await _db.SaveChangesAsync(ct);
                    return null;
                }, ct);
            if (refusal is not null) return refusal;
        }
        catch (DbUpdateException)
        {
            return Refuse(StatusCodes.Status409Conflict, ReleaseABlockReasons.RenewalCaseChanged);
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

    /// <summary>POST /api/contracts/renewals/{caseId}/hold {reason} — T19 (remembers the state it was held from).</summary>
    [HttpPost("{caseId:guid}/hold")]
    [HasPermission("contracts.renewal.manage")]
    public Task<IActionResult> Hold(Guid caseId, [FromBody] RenewalHoldRequest req, CancellationToken ct)
    {
        if (!RenewalHoldReasons.All.Contains(req.Reason))
            return Task.FromResult<IActionResult>(Invalid("invalid_hold_reason", "Choose why the renewal is on hold.", "اختر سبب تعليق التجديد."));
        return MoveAsync(caseId, "Held", ct, (c, _) => Task.FromResult<IActionResult?>(null),
            c => RenewalCaseTransitions.Hold(c, req.Reason), new { reason = req.Reason, note = req.Note });
    }

    /// <summary>POST /api/contracts/renewals/{caseId}/release — T20, back to exactly the state it was held from.</summary>
    [HttpPost("{caseId:guid}/release")]
    [HasPermission("contracts.renewal.manage")]
    public Task<IActionResult> Release(Guid caseId, CancellationToken ct) =>
        MoveAsync(caseId, "Released", ct, (c, _) => Task.FromResult<IActionResult?>(null), RenewalCaseTransitions.Release, new { },
            after: async (c, token) =>
            {
                // Held from NeedsConfirmation while HR confirmed the history: T2 now, as its own row change in this
                // transaction (the database checks each move separately).
                if (c.State != RenewalStates.NeedsConfirmation) return;
                var currentId = (await RenewalTermVersions.LoadAsync(_db, c.TenantId, c.EmployeeId, token)).Current(c.ExpiringContractId)?.Id
                                ?? c.ExpiringContractId;
                var contract = await _db.EmployeeContracts.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.TenantId == c.TenantId && x.Id == currentId, token);
                if (contract is null || !AllowedActionsDeriver.IsChainConfirmed(contract) || c.AllowedActions.Length == 0) return;
                var t2 = RenewalCaseTransitions.ChainConfirmed(c);
                _db.ComplianceAuditLogs.Add(RenewalCaseOpener.Audit(c.TenantId, c, "Confirmed", CurrentUserId(), ActorName(),
                    new { transition = t2, from = RenewalStates.NeedsConfirmation, to = c.State, why = "HistoryConfirmedWhileHeld" }));
                await _db.SaveChangesAsync(token);
            });

    /// <summary>
    /// POST /api/contracts/renewals/{caseId}/cancel {reason} — T21, only once the contract is no longer in force. While it
    /// is Active the review must stay visible (its deadlines keep running): refused with RENEWAL_CONTRACT_STILL_ACTIVE —
    /// put it on hold instead. An Expired contract continues by law (Art. 74(2)): refused with RENEWAL_HOLDOVER_PENDING —
    /// the holdover is R6's. Terminating the contract cancels its review automatically (OnEndedAsync).
    /// </summary>
    [HttpPost("{caseId:guid}/cancel")]
    [HasPermission("contracts.renewal.manage")]
    public Task<IActionResult> Cancel(Guid caseId, [FromBody] RenewalCancelRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Reason) || req.Reason.Length > 500)
            return Task.FromResult<IActionResult>(Invalid("cancel_reason_required", "Say why the renewal review is cancelled.", "اذكر سبب إلغاء مراجعة التجديد."));
        return MoveAsync(caseId, "Cancelled", ct,
            async (c, token) =>
            {
                // The term's CURRENT version decides (an amendment carries the review).
                var termVersions = await RenewalTermVersions.LoadAsync(_db, c.TenantId, c.EmployeeId, token);
                var current = termVersions.Current(c.ExpiringContractId);
                return current is not null && termVersions.InForce(current.Id)
                    ? Refuse(StatusCodes.Status409Conflict, ReleaseABlockReasons.RenewalContractStillActive)
                    : current?.IsDeleted == false && current.Status == "Expired"
                        ? Refuse(StatusCodes.Status409Conflict, ReleaseABlockReasons.RenewalHoldoverPending)
                        : null;
            },
            c => RenewalCaseTransitions.Cancel(c, DateTime.UtcNow), new { reason = req.Reason.Trim() });
    }

    /// <summary>
    /// One state change: the per-employee lock, a precondition, the transition helper (which calls
    /// <see cref="RenewalStateMachine.EnsureCanTransition"/> before anything is written), the xmin compare-and-set and an
    /// audit row — then the updated case DTO. A move the table does not allow, or a write that lost a race or tripped a
    /// constraint, is a 409 with a catalogue code, never a 500.
    /// </summary>
    private async Task<IActionResult> MoveAsync(Guid caseId, string action, CancellationToken ct,
        Func<ContractRenewalCase, CancellationToken, Task<IActionResult?>> precondition,
        Func<ContractRenewalCase, RenewalStateMachine.Transition> move, object metadata,
        Func<ContractRenewalCase, CancellationToken, Task>? after = null)
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
                    if (RenewalStates.IsTerminal(from))
                        return Refuse(StatusCodes.Status409Conflict, ReleaseABlockReasons.RenewalCaseChanged, new { from });
                    if (await precondition(c, ct) is { } blocked) return blocked;
                    RenewalStateMachine.Transition transition;
                    try { transition = move(c); }
                    catch (RenewalTransitionException ex)
                    {
                        return Refuse(StatusCodes.Status409Conflict, ReleaseABlockReasons.RenewalCaseChanged, new { from = ex.From, to = ex.To });
                    }
                    _db.ComplianceAuditLogs.Add(RenewalCaseOpener.Audit(tenantId, c, action, CurrentUserId(), ActorName(),
                        new { transition = transition.Id, from, to = c.State, detail = metadata }));
                    await _db.SaveChangesAsync(ct);
                    if (after is not null) await after(c, ct);
                    return null;
                }, ct);
            if (refusal is not null) return refusal;
        }
        catch (DbUpdateException)
        {
            return Refuse(StatusCodes.Status409Conflict, ReleaseABlockReasons.RenewalCaseChanged);
        }
        return Ok(await RenewalCaseReadModel.CaseAsync(_db, tenantId, caseId, await TodayAsync(ct), ct));
    }

    /// <summary>A validation error as a code with its English and Arabic sentence (no English-only text reaches an Arabic user).</summary>
    private ObjectResult Invalid(string code, string en, string ar, int status = StatusCodes.Status400BadRequest) =>
        StatusCode(status, new { error = code, message = en, messageAr = ar });

    /// <summary>A refusal the UI renders as plain sentences: the catalogue code and its EN/AR title, why and fix.</summary>
    private ObjectResult Refuse(int status, string code, object? detail = null) =>
        StatusCode(status, new
        {
            error = code, reason = ReleaseABlockReasons.All[code], message = ReleaseABlockReasons.All[code].WhyEn,
            messageAr = ReleaseABlockReasons.All[code].WhyAr, detail,
        });

    /// <summary>A review whose frozen actions may still be re-derived: nothing chosen yet, still being prepared.</summary>
    private static bool IsRederivable(ContractRenewalCase c) =>
        c.ContractAction is null
        && (c.State is RenewalStates.NeedsConfirmation or RenewalStates.Open or RenewalStates.AwaitingManager or RenewalStates.OfferInPreparation
            || (c.State == RenewalStates.OnHold && c.HeldFromState is RenewalStates.NeedsConfirmation or RenewalStates.Open
                or RenewalStates.AwaitingManager or RenewalStates.OfferInPreparation));

    private async Task<ContractChainDto?> ChainAsync(Guid tenantId, Guid contractId, DateOnly today, CancellationToken ct)
    {
        var contract = await _db.EmployeeContracts.AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == contractId && !c.IsDeleted, ct);
        if (contract is null) return null;
        var siblings = await _db.EmployeeContracts.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.EmployeeId == contract.EmployeeId && !c.IsDeleted)
            .ToListAsync(ct);
        var employeeClass = await ContractChainCensus.EmployeeClassAsync(_db, tenantId, contract.EmployeeId, ct);
        var rules = await RenewalRuleSet.LoadAsync(_db, tenantId, today, ct);
        var stamps = await ContractChainCensus.LinkEmployeeAsync(_db, tenantId, contract.EmployeeId, siblings, rules, ct);

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

        // The review of this TERM, whichever version it was opened on (Supersede carries it).
        var versions = await RenewalTermVersions.LoadAsync(_db, tenantId, contract.EmployeeId, ct);
        var renewal = (await _db.ContractRenewalCases.AsNoTracking()
                .Where(c => c.TenantId == tenantId && c.EmployeeId == contract.EmployeeId).ToListAsync(ct))
            .Where(c => versions.SameTerm(c.ExpiringContractId, contractId))
            .OrderBy(c => c.ClosedAt != null).ThenByDescending(c => c.OpenedAt).FirstOrDefault();
        // Anchored on the term's first version: an amendment does not shorten the term or reset the Art. 55 count.
        var view = RenewalCaseOpener.Anchored(
            RenewalCaseReadModel.Clone(contract, renewal?.WorkerNationalityClass ?? contract.WorkerNationalityClass ?? employeeClass),
            versions.TermStartedOn(contract.Id));

        IReadOnlyList<string> actions = [];
        IReadOnlyList<string> blocks = [];
        AllowedActionsResult derived = new([], false, [], view.RenewalNumber, null);
        DateOnly? opensOn = null;
        if (view.EndDate is not null && view.ProvisionalBasis is null)
        {
            var deadlines = RenewalDeadlineCalculator.Compute(view, rules);
            opensOn = deadlines.OpensOn;
            derived = view.WorkerNationalityClass is null
                ? new AllowedActionsResult([], false, [ReleaseABlockReasons.RenewalNationalityUnconfirmed], view.RenewalNumber, null)
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
