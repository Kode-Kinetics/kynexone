using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Entitlements.Usage;

/// <summary>
/// Benefit utilisation, the way loans work: the employee (or HR for them) requests a use of a fixed contract benefit, Finance
/// approves or rejects it, the requester may withdraw it while Pending. The decision runs through the same
/// <see cref="ApprovalDecisionGuard"/> as a loan approval: whoever asked never approves, and the employee it is for never decides
/// (<see cref="SubjectDecisionBar"/>). Writes are serialised per employee and audited. No money moves.
///
/// <para><b>Entitlement</b> is the employee's FROZEN <see cref="EmployeeEntitlement"/> row, never today's grade grid: a Contractual
/// count (Quantity) or amount (Amount) per contract year (Annual, from the term's start) or per term (PerTerm). For a benefit that
/// covers dependants it is that value for each one covered — each child for education (up to the grade's child limit), the
/// employee and each family member in scope for a ticket. <b>Used</b> = Pending + Approved requests in the same period.</para>
/// </summary>
public sealed class BenefitUsageService(ZayraDbContext db, ITenantClock clock)
{
    public const string AuditEntity = "BenefitUsageRequest";
    public const string LockScope = "benefits.usage";
    private const int MaxRows = 1000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ── Reads ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Entitlement, pending, approved and remaining per employee per benefit, for the period containing <paramref name="asOf"/>.</summary>
    public async Task<IReadOnlyList<BenefitUtilisationDto>> UtilisationAsync(BenefitUsageActor actor, int? employeeId, DateOnly? asOf, CancellationToken ct)
    {
        EnsureReader(actor);
        var today = await clock.TodayAsync(actor.TenantId, ct);
        return await RowsAsync(actor.TenantId, employeeId, asOf ?? today, today, actor.CanAccessCompany, ct);
    }

    /// <summary>The requests in the caller's companies, newest first (status=Pending is the Finance queue).</summary>
    public async Task<IReadOnlyList<BenefitUsageRequestDto>> RequestsAsync(BenefitUsageActor actor, int? employeeId, string? status, CancellationToken ct)
    {
        EnsureReader(actor);
        var query = Requests(actor.TenantId);
        if (employeeId is int id) query = query.Where(x => x.EmployeeId == id);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        var rows = (await query.OrderByDescending(x => x.CreatedAtUtc).Take(MaxRows * 2).ToListAsync(ct))
            .Where(x => actor.CanAccessCompany(x.CompanyId)).Take(MaxRows).ToList();
        return await ToDtosAsync(actor, rows, ct);
    }

    /// <summary>The employee's own benefits and requests ("My benefits"). The employee is resolved from the login, never from input.</summary>
    public async Task<EssBenefitUsageDto> OwnAsync(Guid tenantId, int employeeId, Guid? userId, CancellationToken ct)
    {
        var today = await clock.TodayAsync(tenantId, ct);
        var rows = await RowsAsync(tenantId, employeeId, today, today, _ => true, ct);
        var requests = await Requests(tenantId).Where(x => x.EmployeeId == employeeId).OrderByDescending(x => x.CreatedAtUtc).Take(50).ToListAsync(ct);
        var labels = await LabelsAsync(tenantId, requests.Select(r => r.BenefitCode), ct);
        var dependants = await DependantNamesAsync(tenantId, requests.Select(r => r.DependantId), ct);
        return new EssBenefitUsageDto(rows, requests.Select(r =>
        {
            var label = labels.GetValueOrDefault(r.BenefitCode) ?? new ComponentLabel(r.BenefitCode, r.BenefitCode);
            return new EssBenefitUsageRequestDto(r.Id, r.BenefitCode, label.En, label.Ar, r.Status, r.UsageDate, r.Units, r.Amount, r.Currency,
                r.DependantId is Guid d ? dependants.GetValueOrDefault(d) : null, r.AcademicYear, r.ProviderName, r.DecisionReason, r.CreatedAtUtc,
                r.Status == BenefitUsageStatuses.Pending && r.RequestedByUserId == userId);
        }).ToList());
    }

    /// <summary>The employee linked to the signed-in login: the signed employee_id claim, else the user-account link (as My package).</summary>
    public async Task<int?> ResolveSelfAsync(Guid tenantId, ClaimsPrincipal user, Guid? userId, CancellationToken ct)
    {
        if (await CallerEmployeeResolver.ResolveAsync(db, user, tenantId, ct) is int linked) return linked;
        if (userId is not Guid uid) return null;
        return await db.Employees.AsNoTracking().Where(x => x.TenantId == tenantId && x.UserAccountId == uid && !x.IsDeleted)
            .Select(x => (int?)x.Id).FirstOrDefaultAsync(ct);
    }

    // ── Request ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>HR requests a use for an employee in its companies (entitlements.manage). Idempotent on the request id.</summary>
    public async Task<BenefitUsageRequestDto> RequestAsync(BenefitUsageActor actor, BenefitUsageRequestInput input, CancellationToken ct)
    {
        Require(actor, BenefitUsagePermissions.RequestForEmployee);
        var created = await CreateAsync(actor.TenantId, actor.UserId!.Value, input, actor.CanAccessCompany, ct);
        return (await ToDtosAsync(actor, [created], ct))[0];
    }

    /// <summary>The employee requests a use of their own benefit (ess.write). <paramref name="selfEmployeeId"/> comes from the login.</summary>
    public async Task<EssBenefitUsageRequestDto> RequestOwnAsync(Guid tenantId, Guid userId, int selfEmployeeId, BenefitUsageRequestInput input, CancellationToken ct)
    {
        var created = await CreateAsync(tenantId, userId, input with { EmployeeId = selfEmployeeId }, _ => true, ct);
        return (await OwnAsync(tenantId, selfEmployeeId, userId, ct)).Requests.First(r => r.Id == created.Id);
    }

    private async Task<BenefitUsageRequest> CreateAsync(Guid tenantId, Guid userId, BenefitUsageRequestInput raw, Func<Guid?, bool> canAccess, CancellationToken ct)
    {
        var input = Normalise(raw);
        if (input.RequestId == Guid.Empty) throw Refuse(BenefitUsageErrors.WrongMeasure, "Send a request id.", 400);
        if (string.IsNullOrEmpty(input.EvidenceReference))
            throw Refuse(BenefitUsageErrors.EvidenceRequired, "Give the invoice, receipt or booking reference.", 400);
        CheckLength(input.EvidenceReference, 200); CheckLength(input.ProviderName, 200); CheckLength(input.AcademicYear, 20); CheckLength(input.Note, 1000);
        if ((input.Units is null) == (input.Amount is null) || input.Units <= 0 || input.Amount <= 0
            || (input.Amount is decimal a && decimal.Round(a, 2) != a))
            throw Refuse(BenefitUsageErrors.WrongMeasure, "Request either a count or an amount (more than zero, at most two decimals), not both.", 400);

        var employee = await db.Employees.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == input.EmployeeId && !x.IsDeleted, ct)
            ?? throw Refuse(BenefitUsageErrors.NotFound, "That employee doesn't exist.", 404);
        if (!canAccess(employee.CompanyId)) throw Refuse(BenefitUsageErrors.WrongCompany, "This employee is outside your companies.", 403);

        return await FinanceDecisionSerializer.SerializeAsync(db, LockScope, tenantId, employee.PublicId, async () =>
        {
            var existing = await Requests(tenantId).FirstOrDefaultAsync(x => x.Id == input.RequestId, ct);
            if (existing is not null)
            {
                if (!SameRequest(existing, input)) throw Refuse(BenefitUsageErrors.RequestIdConflict, "This request id was already used for a different request.");
                return existing;
            }

            var (row, contract) = await FrozenRowAsync(tenantId, employee.PublicId, input.BenefitCode, input.UsageDate, ct);
            if (!IsConsumable(row))
                throw Refuse(BenefitUsageErrors.NotConsumable, "This benefit is not used up by a count or an amount, so it cannot be requested here.");
            if (row.CompanyId != employee.CompanyId || !canAccess(row.CompanyId))
                throw Refuse(BenefitUsageErrors.WrongCompany, "The fixed benefit belongs to another company than the employee's now.", 403);
            var (start, end) = PeriodOf(row, contract, input.UsageDate);
            if (end < await clock.TodayAsync(tenantId, ct))
                throw Refuse(BenefitUsageErrors.PeriodClosed, "That benefit period has ended.");
            var units = row.ValueType == GradeEntitlementValueTypes.Quantity;
            if (units != input.Units.HasValue)
                throw Refuse(BenefitUsageErrors.WrongMeasure, units ? "This benefit is counted: request a number." : "This benefit is an amount: request money.", 400);
            string? currency = null;
            if (!units)
            {
                currency = await CompanyCurrencyAsync(tenantId, row.CompanyId!.Value, ct);
                if (input.Currency is not null && !string.Equals(input.Currency, currency, StringComparison.OrdinalIgnoreCase))
                    throw Refuse(BenefitUsageErrors.CurrencyMismatch, $"Request the amount in {currency}, the company's currency.", 400);
            }
            if (IsEducation(row) && input.AcademicYear is null)
                throw Refuse(BenefitUsageErrors.AcademicYearRequired, "Give the academic year the fees are for.", 400);

            var counted = await CountedAsync(tenantId, employee.Id, row.PayComponentCode, start, ct);
            decimal Measure(BenefitUsageRequest x) => units ? x.Units ?? 0 : x.Amount ?? 0;
            var per = units ? row.Quantity!.Value : row.Amount!.Value;
            var asked = units ? input.Units!.Value : input.Amount!.Value;
            decimal used;
            if (row.DependantScope == DependantScopes.None)
            {
                if (input.DependantId is not null)
                    throw Refuse(BenefitUsageErrors.DependantInvalid, "This benefit is the employee's own; it does not cover dependants.", 400);
                used = counted.Sum(Measure);
            }
            else if (input.DependantId is not Guid dependantId)
            {
                if (IsEducation(row)) throw Refuse(BenefitUsageErrors.DependantRequired, "Choose the child this claim is for.", 400);
                used = counted.Where(x => x.DependantId is null).Sum(Measure);
            }
            else
            {
                var dependant = await db.EmployeeDependents.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == dependantId && x.EmployeeId == employee.Id && !x.IsDeleted, ct);
                if (dependant is null || !InScope(row.DependantScope, dependant.Relationship)
                    || (dependant.DateOfBirth is DateOnly born && born > input.UsageDate))
                    throw Refuse(BenefitUsageErrors.DependantInvalid,
                        IsEducation(row) ? "Choose one of the employee's children on file." : "Choose a family member this benefit covers.", 400);
                // Which dependants use the benefit this period is named by the request, up to the grade's limit.
                var named = counted.Where(x => x.DependantId is not null).Select(x => x.DependantId!.Value).ToHashSet();
                if (!named.Contains(dependantId) && row.MaxDependants is short max && named.Count >= max)
                    throw Refuse(BenefitUsageErrors.DependantCap, $"This benefit covers {max} dependants a period, and {named.Count} are already named.");
                used = counted.Where(x => x.DependantId == dependantId).Sum(Measure);
            }
            if (used + asked > per)
                throw Refuse(BenefitUsageErrors.OverEntitlement, $"Only {Math.Max(0, per - used)} of {per} is left for this period.");

            var request = new BenefitUsageRequest
            {
                Id = input.RequestId, TenantId = tenantId, CompanyId = row.CompanyId, EmployeeId = employee.Id, EntitlementId = row.Id,
                BenefitCode = row.PayComponentCode, PeriodStart = start, PeriodEnd = end, UsageDate = input.UsageDate, DependantId = input.DependantId,
                Units = input.Units, Amount = input.Amount, Currency = currency, AcademicYear = input.AcademicYear, ProviderName = input.ProviderName,
                EvidenceReference = input.EvidenceReference, Note = input.Note, Status = BenefitUsageStatuses.Pending, RequestedByUserId = userId,
            };
            db.BenefitUsageRequests.Add(request);
            Audit(request, userId, "benefit_usage.requested", new { request.Id, request.EmployeeId, request.BenefitCode, request.EntitlementId, start, end,
                request.Units, request.Amount, request.Currency, request.DependantId, request.AcademicYear, request.EvidenceReference, usedBefore = used, cap = per });
            await db.SaveChangesAsync(ct);
            return request;
        }, ct);
    }

    // ── Decide and withdraw ──────────────────────────────────────────────────────────────────────────

    /// <summary>Finance approves or rejects a Pending request, through the loan approval's own guard.</summary>
    public async Task<BenefitUsageRequestDto> DecideAsync(BenefitUsageActor actor, Guid requestId, string decision, string? reason, CancellationToken ct)
    {
        Require(actor, BenefitUsagePermissions.Decide);
        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        CheckLength(reason, 1000);
        if (decision == BenefitUsageStatuses.Rejected && reason is null) throw Refuse(BenefitUsageErrors.ReasonRequired, "Say why.", 400);
        var found = await Requests(actor.TenantId).FirstOrDefaultAsync(x => x.Id == requestId, ct)
            ?? throw Refuse(BenefitUsageErrors.NotFound, "That request doesn't exist.", 404);
        if (!actor.CanAccessCompany(found.CompanyId)) throw Refuse(BenefitUsageErrors.WrongCompany, "This request is outside your companies.", 403);
        var key = await EmployeeKeyAsync(actor.TenantId, found.EmployeeId, ct);

        await FinanceDecisionSerializer.SerializeAsync(db, LockScope, actor.TenantId, key, async () =>
        {
            var request = await db.BenefitUsageRequests.FirstAsync(x => x.TenantId == actor.TenantId && x.Id == requestId, ct);
            var subject = await SubjectDecisionBar.CallerIsSubjectAsync(db, actor.TenantId, actor.UserId, request.EmployeeId, ct);
            var verdict = ApprovalDecisionGuard.Evaluate(new ApprovalDecisionSpec
            {
                Decision = decision,
                AllowedDecisions = ApprovalDecisionGuard.ApprovedOrRejected,
                Step = null,
                ParentLabel = "benefit request",
                ParentExists = true,
                ParentStatus = request.Status,
                ParentStatusesAllowingDecision = [BenefitUsageStatuses.Pending],
                Lock = ApprovalLock.None,
                // Whoever asked never approves; they withdraw instead.
                MakerChecker = new MakerCheckerRule(request.RequestedByUserId == actor.UserId, ApprovalDecisionGuard.ApprovedOrRejected,
                    "You requested this, so another person must decide it."),
                // The employee it is for never decides it, whoever raised it.
                SubjectSeparation = new SubjectSeparationRule(subject, ApprovalDecisionGuard.ApprovedOrRejected,
                    "This is your own benefit, so another person must decide it."),
                EarlierStepSeparation = EarlierStepRule.None,
            });
            if (verdict.Outcome != ApprovalGuardOutcome.Pass)
                throw verdict.Outcome switch
                {
                    ApprovalGuardOutcome.DecisionOutsideVocabulary => Refuse(BenefitUsageErrors.NotAllowed, verdict.Message, 400),
                    ApprovalGuardOutcome.ParentStateForbidsDecision => Refuse(BenefitUsageErrors.AlreadyDecided, "This request was already decided."),
                    ApprovalGuardOutcome.MakerIsChecker => Refuse(BenefitUsageErrors.MakerCannotApprove, verdict.Message, 403),
                    ApprovalGuardOutcome.SubjectIsDecider => Refuse(SubjectDecisionBar.ErrorCode, verdict.Message, 403),
                    _ => Refuse(BenefitUsageErrors.NotAllowed, verdict.Message, 403),
                };
            request.Status = decision;
            request.DecidedByUserId = actor.UserId;
            request.DecidedAtUtc = DateTime.UtcNow;
            request.DecisionReason = reason;
            Audit(request, actor.UserId, "benefit_usage." + decision.ToLowerInvariant(), new { request.Id, decision, reason });
            await db.SaveChangesAsync(ct);
            return 0;
        }, ct);
        return (await ToDtosAsync(actor, [await Requests(actor.TenantId).FirstAsync(x => x.Id == requestId, ct)], ct))[0];
    }

    /// <summary>HR withdraws a Pending request for an employee in its companies (entitlements.manage).</summary>
    public async Task<BenefitUsageRequestDto> CancelAsync(BenefitUsageActor actor, Guid requestId, string? reason, CancellationToken ct)
    {
        Require(actor, BenefitUsagePermissions.RequestForEmployee);
        var found = await Requests(actor.TenantId).FirstOrDefaultAsync(x => x.Id == requestId, ct)
            ?? throw Refuse(BenefitUsageErrors.NotFound, "That request doesn't exist.", 404);
        if (!actor.CanAccessCompany(found.CompanyId)) throw Refuse(BenefitUsageErrors.WrongCompany, "This request is outside your companies.", 403);
        await WithdrawAsync(actor.TenantId, actor.UserId!.Value, found.EmployeeId, requestId, reason, ct);
        return (await ToDtosAsync(actor, [await Requests(actor.TenantId).FirstAsync(x => x.Id == requestId, ct)], ct))[0];
    }

    /// <summary>The employee withdraws their own Pending request (one they asked for themselves).</summary>
    public async Task<EssBenefitUsageRequestDto> CancelOwnAsync(Guid tenantId, Guid userId, int selfEmployeeId, Guid requestId, string? reason, CancellationToken ct)
    {
        var found = await Requests(tenantId).FirstOrDefaultAsync(x => x.Id == requestId && x.EmployeeId == selfEmployeeId, ct)
            ?? throw Refuse(BenefitUsageErrors.NotFound, "That request doesn't exist.", 404);
        if (found.RequestedByUserId != userId) throw Refuse(BenefitUsageErrors.NotAllowed, "HR raised this request; ask HR to withdraw it.", 403);
        await WithdrawAsync(tenantId, userId, selfEmployeeId, requestId, reason, ct);
        return (await OwnAsync(tenantId, selfEmployeeId, userId, ct)).Requests.First(r => r.Id == requestId);
    }

    private async Task WithdrawAsync(Guid tenantId, Guid userId, int employeeId, Guid requestId, string? reason, CancellationToken ct)
    {
        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        CheckLength(reason, 1000);
        if (reason is null) throw Refuse(BenefitUsageErrors.ReasonRequired, "Say why.", 400);
        await FinanceDecisionSerializer.SerializeAsync(db, LockScope, tenantId, await EmployeeKeyAsync(tenantId, employeeId, ct), async () =>
        {
            var request = await db.BenefitUsageRequests.FirstAsync(x => x.TenantId == tenantId && x.Id == requestId, ct);
            if (request.Status != BenefitUsageStatuses.Pending) throw Refuse(BenefitUsageErrors.AlreadyDecided, "Only a Pending request can be withdrawn.");
            request.Status = BenefitUsageStatuses.Cancelled;
            request.DecidedByUserId = userId;
            request.DecidedAtUtc = DateTime.UtcNow;
            request.DecisionReason = reason;
            Audit(request, userId, "benefit_usage.cancelled", new { request.Id, reason });
            await db.SaveChangesAsync(ct);
            return 0;
        }, ct);
    }

    // ── The frozen row and its period ────────────────────────────────────────────────────────────────

    /// <summary>A Contractual count or amount that resets each contract year or once per term.</summary>
    public static bool IsConsumable(EmployeeEntitlement row) =>
        row.EntitlementClass == PayEntitlementClasses.Contractual
        && ((row.ValueType == GradeEntitlementValueTypes.Quantity && row.Quantity > 0)
            || (row.ValueType == GradeEntitlementValueTypes.Amount && row.Amount > 0))
        && row.LimitPeriod is EntitlementLimitPeriods.Annual or EntitlementLimitPeriods.PerTerm;

    /// <summary>The period containing <paramref name="on"/>: Annual = the contract year from the term's start (ending with the term if
    /// it ends first); PerTerm = the whole term.</summary>
    public static (DateOnly Start, DateOnly End) PeriodOf(EmployeeEntitlement row, EmployeeContract? contract, DateOnly on)
    {
        var termStart = contract?.StartDate ?? row.EffectiveFrom;
        var termEnd = contract?.EndDate ?? row.EffectiveTo ?? DateOnly.MaxValue;
        if (row.LimitPeriod != EntitlementLimitPeriods.Annual) return (termStart, termEnd);
        var years = Math.Max(0, on.Year - termStart.Year);
        var start = termStart.AddYears(years);
        while (start > on && years > 0) start = termStart.AddYears(--years);
        var end = start.AddYears(1).AddDays(-1);
        return (start, end > termEnd ? termEnd : end);
    }

    private async Task<(EmployeeEntitlement Row, EmployeeContract? Contract)> FrozenRowAsync(Guid tenantId, Guid employeePublicId, string code,
        DateOnly on, CancellationToken ct)
    {
        var rows = await ScopedBypass.TenantWide(db.EmployeeEntitlements, tenantId,
                "Benefit utilisation: the employee's own frozen rows; the caller was authorised for the employee and the row's company is checked.")
            .AsNoTracking()
            .Where(x => x.EmployeeId == employeePublicId && x.PayComponentCode == code && x.EffectiveFrom <= on && (x.EffectiveTo == null || x.EffectiveTo >= on))
            .ToListAsync(ct);
        var row = rows.OrderByDescending(x => x.VerificationState == EntitlementVerificationStates.Verified).ThenByDescending(x => x.EffectiveFrom).FirstOrDefault()
            ?? throw Refuse(BenefitUsageErrors.NoEntitlement, "The employee has no fixed entitlement to this benefit on that date.");
        if (row.VerificationState != EntitlementVerificationStates.Verified)
            throw Refuse(BenefitUsageErrors.EntitlementUnverified, "HR must confirm this benefit against the signed contract before it is requested.");
        var contract = await ScopedBypass.TenantWide(db.EmployeeContracts, tenantId, "Benefit utilisation: the term the frozen row belongs to (its period).")
            .AsNoTracking().FirstOrDefaultAsync(x => x.Id == row.ContractId, ct);
        if (contract is { Status: "Terminated" }
            && ContractTerminationDates.LastDay(contract, await ContractTerminationDates.ForAsync(db, tenantId, [contract], ct)) is DateOnly last && last < on)
            throw Refuse(BenefitUsageErrors.NoEntitlement, "The employee's contract ended before that date.");
        return (row, contract);
    }

    // ── Utilisation rows ─────────────────────────────────────────────────────────────────────────────

    private async Task<List<BenefitUtilisationDto>> RowsAsync(Guid tenantId, int? employeeId, DateOnly on, DateOnly today, Func<Guid?, bool> canAccess,
        CancellationToken ct)
    {
        Guid? publicId = null;
        if (employeeId is int id)
        {
            publicId = await db.Employees.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted)
                .Select(x => (Guid?)x.PublicId).FirstOrDefaultAsync(ct);
            if (publicId is null) return [];
        }
        var query = ScopedBypass.TenantWide(db.EmployeeEntitlements, tenantId,
                "Benefit utilisation: frozen rows in force; each row is then kept only when the caller may see its company.")
            .AsNoTracking()
            .Where(x => x.EntitlementClass == PayEntitlementClasses.Contractual
                && (x.ValueType == GradeEntitlementValueTypes.Quantity || x.ValueType == GradeEntitlementValueTypes.Amount)
                && (x.LimitPeriod == EntitlementLimitPeriods.Annual || x.LimitPeriod == EntitlementLimitPeriods.PerTerm)
                && x.EffectiveFrom <= on && (x.EffectiveTo == null || x.EffectiveTo >= on));
        if (publicId is Guid pid) query = query.Where(x => x.EmployeeId == pid);
        var frozen = (await query.ToListAsync(ct)).Where(x => canAccess(x.CompanyId) && IsConsumable(x))
            .GroupBy(x => (x.EmployeeId, x.PayComponentCode))
            .Select(g => g.OrderByDescending(x => x.VerificationState == EntitlementVerificationStates.Verified).ThenByDescending(x => x.EffectiveFrom).First())
            .ToList();
        if (frozen.Count == 0) return [];

        var publicIds = frozen.Select(x => x.EmployeeId).Distinct().ToList();
        var employees = await ScopedBypass.NullableTenantWide(db.Employees, tenantId, "Benefit utilisation: the employees whose frozen rows the caller may see.")
            .AsNoTracking().Where(x => publicIds.Contains(x.PublicId) && !x.IsDeleted)
            .Select(x => new { x.Id, x.PublicId, x.FullName, x.EmployeeCode }).ToListAsync(ct);
        var byPublic = employees.ToDictionary(x => x.PublicId);
        var contractIds = frozen.Select(x => x.ContractId).Distinct().ToList();
        var contracts = await ScopedBypass.TenantWide(db.EmployeeContracts, tenantId, "Benefit utilisation: the terms of the frozen rows (their periods).")
            .AsNoTracking().Where(x => contractIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var terminated = await ContractTerminationDates.ForAsync(db, tenantId, contracts.Values.Where(c => c.Status == "Terminated").ToList(), ct);
        var labels = await LabelsAsync(tenantId, frozen.Select(x => x.PayComponentCode), ct);
        var currencies = new Dictionary<Guid, string>();
        foreach (var companyId in frozen.Select(x => x.CompanyId!.Value).Distinct())
            currencies[companyId] = await CompanyCurrencyAsync(tenantId, companyId, ct);
        var employeeIds = employees.Select(x => x.Id).ToList();
        var requests = await Requests(tenantId).Where(x => employeeIds.Contains(x.EmployeeId)
            && (x.Status == BenefitUsageStatuses.Pending || x.Status == BenefitUsageStatuses.Approved)).ToListAsync(ct);
        var withFamily = frozen.Where(x => x.DependantScope != DependantScopes.None).Select(x => byPublic.GetValueOrDefault(x.EmployeeId)?.Id)
            .OfType<int>().Distinct().ToList();
        List<EmployeeDependent> family = withFamily.Count == 0 ? new List<EmployeeDependent>() : (await db.EmployeeDependents.AsNoTracking()
                .Where(x => x.TenantId == tenantId && withFamily.Contains(x.EmployeeId) && !x.IsDeleted).ToListAsync(ct))
            .Where(d => d.DateOfBirth is null || d.DateOfBirth <= on).OrderBy(d => d.DateOfBirth ?? DateOnly.MinValue).ToList();

        var result = new List<BenefitUtilisationDto>();
        foreach (var row in frozen.OrderBy(x => byPublic.GetValueOrDefault(x.EmployeeId)?.FullName).ThenBy(x => x.PayComponentCode))
        {
            if (!byPublic.TryGetValue(row.EmployeeId, out var employee)) continue;
            var contract = contracts.GetValueOrDefault(row.ContractId);
            if (contract is { Status: "Terminated" } && ContractTerminationDates.LastDay(contract, terminated) is DateOnly last && last < on) continue;
            var (start, end) = PeriodOf(row, contract, on);
            var units = row.ValueType == GradeEntitlementValueTypes.Quantity;
            var per = units ? row.Quantity!.Value : row.Amount!.Value;
            var counted = requests.Where(x => x.EmployeeId == employee.Id && x.PeriodStart == start
                && string.Equals(x.BenefitCode, row.PayComponentCode, StringComparison.OrdinalIgnoreCase)).ToList();
            decimal Measure(BenefitUsageRequest c) => units ? c.Units ?? 0 : c.Amount ?? 0;
            var pending = counted.Where(c => c.Status == BenefitUsageStatuses.Pending).Sum(Measure);
            var approved = counted.Where(c => c.Status == BenefitUsageStatuses.Approved).Sum(Measure);
            var open = row.VerificationState == EntitlementVerificationStates.Verified && end >= today;
            var total = per;
            var beneficiaries = new List<BenefitUsageBeneficiaryDto>();
            bool canRequest;
            if (row.DependantScope == DependantScopes.None) canRequest = open && per - pending - approved > 0;
            else
            {
                // That value for each one covered: the employee (not for education) and the dependants in scope on file, up to the limit.
                var inScope = family.Where(d => d.EmployeeId == employee.Id && InScope(row.DependantScope, d.Relationship)).ToList();
                var named = counted.Where(c => c.DependantId is not null).Select(c => c.DependantId!.Value).ToHashSet();
                var covered = row.MaxDependants is short max ? Math.Min(inScope.Count, (int)max) : inScope.Count;
                total = per * ((IsEducation(row) ? 0 : 1) + covered);
                if (!IsEducation(row))
                {
                    var own = counted.Where(c => c.DependantId is null).Sum(Measure);
                    beneficiaries.Add(new BenefitUsageBeneficiaryDto(null, employee.FullName, per, own, per - own, own > 0));
                }
                foreach (var d in inScope)
                {
                    var theirs = counted.Where(c => c.DependantId == d.Id).Sum(Measure);
                    beneficiaries.Add(new BenefitUsageBeneficiaryDto(d.Id, d.FullName, per, theirs, per - theirs, named.Contains(d.Id)));
                }
                var room = row.MaxDependants is not short cap || named.Count < cap;
                canRequest = open && beneficiaries.Any(b => b.Remaining > 0 && (b.DependantId is null || b.Named || room));
            }
            var label = labels.GetValueOrDefault(row.PayComponentCode) ?? new ComponentLabel(row.PayComponentCode, row.PayComponentCode);
            result.Add(new BenefitUtilisationDto(employee.Id, employee.FullName, employee.EmployeeCode, row.CompanyId!.Value, row.PayComponentCode,
                label.En, label.Ar, row.VerificationState, units ? BenefitUsageMeasures.Units : BenefitUsageMeasures.Amount,
                units ? null : currencies[row.CompanyId!.Value], total, pending, approved, total - pending - approved, start, end,
                row.DependantScope, row.MaxDependants, row.DependantScope == DependantScopes.None ? null : per, beneficiaries, canRequest));
        }
        return result;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────

    private IQueryable<BenefitUsageRequest> Requests(Guid tenantId) =>
        ScopedBypass.TenantWide(db.BenefitUsageRequests, tenantId,
                "Benefit usage requests: every read is narrowed to an authorised employee or filtered on the caller's companies afterwards.")
            .AsNoTracking();

    private async Task<List<BenefitUsageRequest>> CountedAsync(Guid tenantId, int employeeId, string code, DateOnly periodStart, CancellationToken ct) =>
        await Requests(tenantId).Where(x => x.EmployeeId == employeeId && x.BenefitCode == code && x.PeriodStart == periodStart
            && (x.Status == BenefitUsageStatuses.Pending || x.Status == BenefitUsageStatuses.Approved)).ToListAsync(ct);

    private async Task<Guid> EmployeeKeyAsync(Guid tenantId, int employeeId, CancellationToken ct) =>
        await ScopedBypass.NullableTenantWide(db.Employees, tenantId, "Benefit usage: the lock key of the request's own employee.")
            .AsNoTracking().Where(x => x.Id == employeeId).Select(x => x.PublicId).FirstAsync(ct);

    private static bool SameRequest(BenefitUsageRequest r, BenefitUsageRequestInput input) =>
        r.EmployeeId == input.EmployeeId && string.Equals(r.BenefitCode, input.BenefitCode, StringComparison.OrdinalIgnoreCase)
        && r.UsageDate == input.UsageDate && r.Units == input.Units && r.Amount == input.Amount && r.DependantId == input.DependantId
        && r.EvidenceReference == input.EvidenceReference;

    /// <summary>Children's education: an amount per child; the employee is never a beneficiary.</summary>
    private static bool IsEducation(EmployeeEntitlement row) => string.Equals(row.PayComponentCode, EntitlementComponentRules.Education, StringComparison.OrdinalIgnoreCase);

    private static bool InScope(string scope, string? relationship) => scope switch
    {
        DependantScopes.Spouse => PackageRules.IsSpouse(relationship),
        DependantScopes.Children => PackageRules.IsChild(relationship),
        DependantScopes.Family => PackageRules.IsSpouse(relationship) || PackageRules.IsChild(relationship),
        _ => false,
    };

    private async Task<List<BenefitUsageRequestDto>> ToDtosAsync(BenefitUsageActor actor, List<BenefitUsageRequest> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var tenantId = actor.TenantId;
        var employeeIds = rows.Select(r => r.EmployeeId).Distinct().ToList();
        var names = await ScopedBypass.NullableTenantWide(db.Employees, tenantId, "Benefit usage: the names of the employees of requests the caller may see.")
            .AsNoTracking().Where(x => employeeIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.FullName, ct);
        var userIds = rows.Select(r => r.RequestedByUserId).Concat(rows.Select(r => r.DecidedByUserId).OfType<Guid>()).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(x => x.TenantId == tenantId && userIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.FullName, ct);
        var labels = await LabelsAsync(tenantId, rows.Select(r => r.BenefitCode), ct);
        var dependants = await DependantNamesAsync(tenantId, rows.Select(r => r.DependantId), ct);
        var self = await CallerEmployeeIdsAsync(actor, ct);
        return rows.Select(r =>
        {
            var label = labels.GetValueOrDefault(r.BenefitCode) ?? new ComponentLabel(r.BenefitCode, r.BenefitCode);
            var pending = r.Status == BenefitUsageStatuses.Pending;
            return new BenefitUsageRequestDto(r.Id, r.EmployeeId, names.GetValueOrDefault(r.EmployeeId) ?? "", r.CompanyId!.Value, r.BenefitCode, label.En,
                label.Ar, r.Status, r.PeriodStart, r.PeriodEnd, r.UsageDate, r.Units, r.Amount, r.Currency, r.DependantId,
                r.DependantId is Guid d ? dependants.GetValueOrDefault(d) : null, r.AcademicYear, r.ProviderName, r.EvidenceReference, r.Note,
                r.RequestedByUserId, users.GetValueOrDefault(r.RequestedByUserId), r.CreatedAtUtc,
                r.DecidedByUserId is Guid by ? users.GetValueOrDefault(by) : null, r.DecidedAtUtc, r.DecisionReason,
                CanDecide: pending && actor.Has(BenefitUsagePermissions.Decide) && r.RequestedByUserId != actor.UserId && !self.Contains(r.EmployeeId),
                CanCancel: pending && actor.Has(BenefitUsagePermissions.RequestForEmployee));
        }).ToList();
    }

    /// <summary>The employees the caller IS: used only to hide Approve on the caller's own benefit (the server refuses it anyway).</summary>
    private async Task<HashSet<int>> CallerEmployeeIdsAsync(BenefitUsageActor actor, CancellationToken ct)
    {
        if (actor.UserId is not Guid uid) return [];
        var own = await ScopedBypass.NullableTenantWide(db.Employees, actor.TenantId, "Benefit usage: the caller's own employee record.")
            .AsNoTracking().Where(x => x.UserAccountId == uid).Select(x => x.Id).ToListAsync(ct);
        var linked = await db.EmployeeUserAccounts.AsNoTracking().Where(x => x.TenantId == actor.TenantId && x.UserId == uid).Select(x => x.EmployeeId).ToListAsync(ct);
        return own.Concat(linked).ToHashSet();
    }

    private async Task<Dictionary<Guid, string>> DependantNamesAsync(Guid tenantId, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.OfType<Guid>().Distinct().ToList();
        return list.Count == 0 ? new Dictionary<Guid, string>() : await db.EmployeeDependents.AsNoTracking()
            .Where(x => x.TenantId == tenantId && list.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.FullName, ct);
    }

    private async Task<string> CompanyCurrencyAsync(Guid tenantId, Guid companyId, CancellationToken ct) =>
        await db.Companies.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == companyId).Select(x => x.DefaultCurrency).FirstOrDefaultAsync(ct)
        ?? "SAR";

    /// <summary>EN/AR names: the catalogue's own, else the tenant's pay component (a customer's BEN_* benefit), else the code.</summary>
    private async Task<Dictionary<string, ComponentLabel>> LabelsAsync(Guid tenantId, IEnumerable<string> codes, CancellationToken ct)
    {
        var list = codes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var labels = new Dictionary<string, ComponentLabel>(StringComparer.OrdinalIgnoreCase);
        var custom = list.Where(c => EntitlementComponentRules.For(c) is not { } r || string.Equals(r.NameEn, c, StringComparison.OrdinalIgnoreCase)).ToList();
        var components = await ScopedBypass.TenantWide(db.PayComponents, tenantId, "Component names are one identity across the tenant.")
            .AsNoTracking().Where(x => custom.Contains(x.Code) && !x.IsDeleted).Select(x => new { x.Code, x.NameEn, x.NameAr, x.CompanyId }).ToListAsync(ct);
        foreach (var code in list)
        {
            var rule = EntitlementComponentRules.For(code);
            var component = components.OrderBy(x => x.CompanyId.HasValue).FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase));
            labels[code] = rule is not null && !string.Equals(rule.NameEn, code, StringComparison.OrdinalIgnoreCase) ? new ComponentLabel(rule.NameEn, rule.NameAr)
                : component is not null ? new ComponentLabel(component.NameEn, string.IsNullOrWhiteSpace(component.NameAr) ? component.NameEn : component.NameAr)
                : new ComponentLabel(code, code);
        }
        return labels;
    }

    private static void EnsureReader(BenefitUsageActor actor)
    {
        if (!actor.Has(BenefitUsagePermissions.Read) && !actor.Has(BenefitUsagePermissions.Decide))
            throw Refuse(BenefitUsageErrors.NotAllowed, "You don't have access to benefit utilisation.", 403);
    }

    private static void Require(BenefitUsageActor actor, string permission)
    {
        if (actor.UserId is not Guid uid || uid == Guid.Empty || !actor.Has(permission))
            throw Refuse(BenefitUsageErrors.NotAllowed, permission == BenefitUsagePermissions.Decide
                ? "Only Finance (payroll approval) can decide a benefit request."
                : "Only HR (manage employee packages) can request a benefit for an employee.", 403);
    }

    private static void CheckLength(string? value, int max)
    {
        if (value is not null && value.Length > max) throw Refuse(BenefitUsageErrors.TextTooLong, $"Keep it to {max} characters.", 400);
    }

    private static BenefitUsageRequestInput Normalise(BenefitUsageRequestInput r)
    {
        static string? T(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        return r with
        {
            BenefitCode = (r.BenefitCode ?? "").Trim().ToUpperInvariant(), EvidenceReference = T(r.EvidenceReference), AcademicYear = T(r.AcademicYear),
            ProviderName = T(r.ProviderName), Note = T(r.Note), Currency = T(r.Currency)?.ToUpperInvariant(),
        };
    }

    private void Audit(BenefitUsageRequest request, Guid? actor, string action, object detail) => db.AuditLogs.Add(new AuditLog
    {
        TenantId = request.TenantId, CompanyId = request.CompanyId, UserId = actor, EntityName = AuditEntity, EntityId = request.Id.ToString(),
        Action = action, Metadata = JsonSerializer.Serialize(detail, Json),
    });

    private static BenefitUsageRefusedException Refuse(string code, string message, int status = 409) => new(code, message, status);
}
