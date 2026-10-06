using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Contracts;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Contracts;

public sealed record RenewalEmployeeDto(int Id, Guid PublicId, string Name, string? NameAr, string Code);

public sealed record RenewalDeadlinesDto(
    DateOnly? OfferDueOn, DateOnly? NoticeDueOn, DateOnly? QiwaSubmitDueOn, DateOnly? QiwaGateDueOn, DateOnly? QiwaRespondByOn,
    DateOnly? NextHardDeadline);

/// <summary>One dashboard row (and the head of the full case DTO).</summary>
/// <param name="FastLaneEligible">R5's batch "renew on current terms" may take it (<see cref="RenewalNextStep.FastLaneEligible"/>).</param>
/// <param name="AmendmentPending">An amendment of the term is drafted but not activated: nothing changes until it is.</param>
public sealed record RenewalCaseItemDto(
    Guid CaseId,
    Guid ContractId,
    string ContractNumber,
    RenewalEmployeeDto Employee,
    Guid CompanyId,
    string CompanyName,
    string? CompanyNameAr,
    string NationalityClass,
    DateOnly ExpiringEndDate,
    int DaysLeft,
    string Stage,
    string State,
    string? HoldReason,
    IReadOnlyList<string> AllowedActions,
    string? ContractAction,
    short? RenewalNumber,
    DateOnly? ChainStartedOn,
    RenewalNext? Next,
    IReadOnlyList<RenewalBadge> Badges,
    IReadOnlyList<BlockReason> BlockReasons,
    RenewalDeadlinesDto Deadlines,
    bool FastLaneEligible,
    bool AmendmentPending = false);

/// <summary>The shared case DTO (R4 dashboard drawer, R5 offer editor, R6 response / Qiwa / apply).</summary>
public sealed record RenewalCaseDto(
    RenewalCaseItemDto Summary,
    DateOnly ContractStartDate,
    DateOnly? ContractSignedOn,
    Art55Meter Art55,
    IReadOnlyList<string> NextStates,
    string? RecommendedAction,
    DateOnly? ManagerDueOn,
    short? TermMonths,
    string? FallbackIfRejected,
    short OfferVersion,
    decimal? OfferCostDeltaMonthly,
    Guid? CurrentApprovalRequestId,
    Guid? RenewalBatchId,
    string? EmployeeResponse,
    DateTime? EmployeeRespondedAt,
    string? ResponseChannel,
    bool QiwaRequired,
    string? QiwaRequestNo,
    DateOnly? QiwaSentOn,
    short QiwaAttempts,
    string? QiwaEvidenceOutcome,
    DateOnly? NonRenewalNoticeServedOn,
    string? NonRenewalNoticeChannel,
    Guid? ResultingContractId,
    DateTime OpenedAt,
    DateTime? ClosedAt,
    string Version);

/// <summary>Buckets of open cases by days to the end of the expiring term.</summary>
public sealed record RenewalBucketDto(string Key, int FromDays, int ToDays, int Count, IReadOnlyList<Guid> CaseIds);

/// <summary>A due fixed-term contract that has no case, and why.</summary>
/// <param name="Reason">A <see cref="RenewalOpenSkipReasons"/> value, or <c>AwaitingDailyRun</c>.</param>
public sealed record RenewalUnopenedDto(Guid ContractId, string ContractNumber, RenewalEmployeeDto? Employee, DateOnly EndDate, DateOnly OpensOn,
    string Reason, BlockReason? BlockReason);

/// <summary>An exception tile: how many, and exactly which records (drill-down).</summary>
public sealed record RenewalExceptionDto(int Count, IReadOnlyList<Guid> CaseIds);

/// <summary>A due Active fixed-term contract whose review is CLOSED while the contract is still in force.</summary>
public sealed record RenewalClosedReviewDto(Guid ContractId, string ContractNumber, RenewalEmployeeDto? Employee, DateOnly EndDate, Guid CaseId,
    string CaseState, DateTime? ClosedAt);

/// <summary>
/// Every due Active fixed-term contract ending in the window, counted once: with an open review (buckets), without any
/// review (<c>expiringWithoutCase</c>), or with only a closed one (<c>activeWithoutOpenReview</c>). The three always add
/// up to <see cref="DueActiveContracts"/>; contracts whose review opens later are counted apart.
/// </summary>
/// <param name="OpenReviewCaseIds">The open reviews counted in <see cref="WithOpenReview"/> (drill-down).</param>
/// <param name="NotYetDueContracts">The contracts counted in <see cref="NotYetDue"/>, with the day their review opens.</param>
public sealed record RenewalReconciliationDto(int DueActiveContracts, int WithOpenReview, int WithoutReview, int WithClosedReviewOnly,
    int NotYetDue, IReadOnlyList<Guid> OpenReviewCaseIds, IReadOnlyList<RenewalUnopenedDto> NotYetDueContracts);

public sealed record RenewalExceptionsDto(
    IReadOnlyList<RenewalUnopenedDto> ExpiringWithoutCase,
    IReadOnlyList<RenewalClosedReviewDto> ActiveWithoutOpenReview,
    RenewalExceptionDto NeedsConfirmation,
    RenewalExceptionDto NoticeDatePassed,
    RenewalExceptionDto QiwaOverdue,
    RenewalExceptionDto Art55Threshold,
    RenewalExceptionDto ExpiredNoOutcome,
    RenewalExceptionDto ExpiredHoldoverPending);

/// <param name="OpenLeadDays">How many days before its end a contract with the default notice gets its review (the
/// tenant's rules): the dashboard's default window and the number its subtitle states.</param>
public sealed record RenewalRadarDto(DateOnly Today, int Days, int OpenLeadDays, IReadOnlyList<RenewalBucketDto> Buckets, RenewalExceptionsDto Exceptions,
    IReadOnlyList<RenewalCaseItemDto> Items, RenewalReconciliationDto Reconciliation);

/// <summary>
/// The renewal read side. Runs on the request's context, so the tenant and company-scope filters apply on top of the
/// explicit tenant predicates; nothing here writes. Every number on the dashboard is a count of rows returned in the
/// same response (bucket and exception ids), so a tile always reconciles to its drill-down. Slice R4.
/// </summary>
public static class RenewalCaseReadModel
{
    public const string AwaitingDailyRun = "AwaitingDailyRun";
    public const string OverdueBucket = "overdue";

    public static readonly int[] BucketEdges = [30, 60, 90, 120];

    /// <summary>The dashboard: open cases ending within <paramref name="days"/> (and every overdue one), buckets, exceptions.</summary>
    /// <param name="days">The window; NULL = the tenant's open lead.</param>
    public static async Task<RenewalRadarDto> RadarAsync(ZayraDbContext db, RenewalCaseOpener opener, Guid tenantId, Guid? companyId, int? days,
        DateOnly today, CancellationToken ct)
    {
        var rules = await RenewalRuleSet.LoadAsync(db, tenantId, today, ct);
        var openLead = RenewalDeadlineCalculator.DefaultOpenLeadDays(rules);
        var window = days ?? openLead;
        var horizon = today.AddDays(window);
        var cases = await db.ContractRenewalCases.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.ClosedAt == null && c.ExpiringEndDate <= horizon
                        && (companyId == null || c.CompanyId == companyId))
            .ToListAsync(ct);
        var items = await ItemsAsync(db, tenantId, cases, today, rules, ct);
        items = items.OrderBy(i => i.Next?.DueOn ?? i.ExpiringEndDate).ThenBy(i => i.Employee.Name).ToList();

        // "Overdue" first: open reviews whose contract already ended, so the buckets add up to every open review listed.
        var overdueIds = items.Where(i => i.DaysLeft < 0).Select(i => i.CaseId).ToList();
        var buckets = new List<RenewalBucketDto> { new(OverdueBucket, -1, -1, overdueIds.Count, overdueIds) };
        var from = 0;
        foreach (var edge in BucketEdges.Where(e => e - 30 < window))
        {
            var to = Math.Min(edge, window);
            var ids = items.Where(i => i.DaysLeft >= from && i.DaysLeft <= to).Select(i => i.CaseId).ToList();
            buckets.Add(new RenewalBucketDto($"{from}-{to}", from, to, ids.Count, ids));
            from = edge + 1;
        }

        RenewalExceptionDto Tile(Func<RenewalCaseItemDto, bool> when)
        {
            var ids = items.Where(when).Select(i => i.CaseId).ToList();
            return new RenewalExceptionDto(ids.Count, ids);
        }
        bool Has(RenewalCaseItemDto i, string badge) => i.Badges.Any(b => b.Code == badge);

        // Every Active fixed-term contract in the window, with ALL its cases (open or closed), so each lands somewhere. A
        // case belongs to its TERM, whichever version of the term it was opened on (Supersede carries it).
        var versions = await RenewalTermVersions.LoadAsync(db, tenantId, null, ct);
        var caseRows = await db.ContractRenewalCases.AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .Select(c => new { c.Id, c.ExpiringContractId, c.State, c.ClosedAt })
            .ToListAsync(ct);
        var casesByTerm = caseRows.ToLookup(c => versions.Root(c.ExpiringContractId));
        var candidates = await opener.CandidatesAsync(tenantId, companyId, horizon, tracked: false, ct);
        var employees = await EmployeesAsync(db, tenantId, candidates.Select(c => c.Contract.EmployeeId), ct);
        var unopened = new List<RenewalUnopenedDto>();
        var closedOnly = new List<RenewalClosedReviewDto>();
        var notYet = new List<RenewalUnopenedDto>();
        var openReviewIds = new List<Guid>();
        int due = 0;
        foreach (var candidate in candidates)
        {
            var contract = candidate.Contract;
            var plan = RenewalCaseOpener.Plan(candidate, rules, today);
            var all = casesByTerm[versions.Root(contract.Id)].ToList();
            var employee = employees.GetValueOrDefault(contract.EmployeeId);
            if (all.FirstOrDefault(c => c.ClosedAt == null) is { } openCase) { due++; openReviewIds.Add(openCase.Id); continue; }
            if (plan.SkipReason == RenewalOpenSkipReasons.NotDue)
            {
                notYet.Add(new RenewalUnopenedDto(contract.Id, contract.ContractNumber, employee, contract.EndDate!.Value, plan.Deadlines!.OpensOn,
                    RenewalOpenSkipReasons.NotDue, null));
                continue;
            }
            due++;
            if (all.Count > 0)
            {
                var last = all.OrderByDescending(c => c.ClosedAt).First();
                closedOnly.Add(new RenewalClosedReviewDto(contract.Id, contract.ContractNumber, employee, contract.EndDate!.Value, last.Id, last.State,
                    last.ClosedAt));
                continue;
            }
            var block = plan.BlockCode is { } code && ReleaseABlockReasons.All.TryGetValue(code, out var reason) ? reason : null;
            unopened.Add(new RenewalUnopenedDto(contract.Id, contract.ContractNumber, employee, contract.EndDate!.Value, plan.Deadlines!.OpensOn,
                plan.SkipReason ?? AwaitingDailyRun, block));
        }

        var exceptions = new RenewalExceptionsDto(
            unopened.OrderBy(u => u.EndDate).ToList(),
            closedOnly.OrderBy(u => u.EndDate).ToList(),
            Tile(i => i.State == RenewalStates.NeedsConfirmation || Has(i, RenewalBadgeCodes.ChainUnconfirmed)),
            Tile(i => Has(i, RenewalBadgeCodes.NoticeDatePassed)),
            Tile(i => Has(i, RenewalBadgeCodes.QiwaOverdue)),
            Tile(i => Has(i, RenewalBadgeCodes.Art55Threshold)),
            Tile(i => Has(i, RenewalBadgeCodes.ExpiredNoOutcome)),
            Tile(i => Has(i, RenewalBadgeCodes.ExpiredHoldoverPending)));
        return new RenewalRadarDto(today, window, openLead, buckets, exceptions, items,
            new RenewalReconciliationDto(due, openReviewIds.Count, unopened.Count, closedOnly.Count, notYet.Count, openReviewIds,
                notYet.OrderBy(n => n.OpensOn).ToList()));
    }

    /// <summary>The full DTO of one case, or NULL when it is not visible to the caller.</summary>
    public static async Task<RenewalCaseDto?> CaseAsync(ZayraDbContext db, Guid tenantId, Guid caseId, DateOnly today, CancellationToken ct)
    {
        var c = await db.ContractRenewalCases.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == caseId, ct);
        if (c is null) return null;
        var rules = await RenewalRuleSet.LoadAsync(db, tenantId, today, ct);
        var item = (await ItemsAsync(db, tenantId, [c], today, rules, ct)).Single();
        var versions = await RenewalTermVersions.LoadAsync(db, tenantId, c.EmployeeId, ct);
        var currentId = versions.Current(c.ExpiringContractId)?.Id ?? c.ExpiringContractId;
        var contract = await ScopedBypass.TenantWide(db.EmployeeContracts, tenantId,
                "Renewal case DTO reads the reviewed term's current version (it may have been amended); tenant pinned.")
            .AsNoTracking().FirstAsync(x => x.Id == currentId, ct);
        // Anchored on the term's FIRST version: an amendment does not change the term's length or the Art. 55 count.
        var view = RenewalCaseOpener.Anchored(Clone(contract, c.WorkerNationalityClass), versions.TermStartedOn(contract.Id));
        var derived = c.NoticeDueOn is { } notice ? AllowedActionsDeriver.Derive(view, notice, today, rules)
            : new AllowedActionsResult(c.AllowedActions, RenewalNextStep.IsArt55Threshold(c), [], view.RenewalNumber, null);
        var meter = AllowedActionsDeriver.Meter(view, derived with { ThresholdReached = RenewalNextStep.IsArt55Threshold(c) }, rules);
        return new RenewalCaseDto(item, contract.StartDate, SignedOn(contract), meter, RenewalStateMachine.NextStates(c.State),
            c.RecommendedAction, c.ManagerDueOn, c.TermMonths, c.FallbackIfRejected, c.OfferVersion, c.OfferCostDeltaMonthly,
            c.CurrentApprovalRequestId, c.RenewalBatchId, c.EmployeeResponse, c.EmployeeRespondedAt, c.ResponseChannel, c.QiwaRequired,
            c.QiwaRequestNo, c.QiwaSentOn, c.QiwaAttempts, c.QiwaEvidenceOutcome, c.NonRenewalNoticeServedOn, c.NonRenewalNoticeChannel,
            c.ResultingContractId, c.OpenedAt, c.ClosedAt, c.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Dashboard rows for the given cases.</summary>
    public static async Task<List<RenewalCaseItemDto>> ItemsAsync(ZayraDbContext db, Guid tenantId, IReadOnlyCollection<ContractRenewalCase> cases,
        DateOnly today, RenewalRuleSet rules, CancellationToken ct)
    {
        if (cases.Count == 0) return [];
        // Each review shows its term's CURRENT version (an amendment carries the review onto the new version).
        var versions = await RenewalTermVersions.LoadAsync(db, tenantId, null, ct);
        var contracts = cases.Select(c => versions.Current(c.ExpiringContractId)).OfType<TermVersionRow>()
            .GroupBy(v => v.Id).Select(g => g.First()).ToDictionary(v => v.Id);
        TermVersionRow? ContractOf(ContractRenewalCase c) =>
            versions.Current(c.ExpiringContractId) is { } v ? contracts.GetValueOrDefault(v.Id) : null;
        var employees = await EmployeesAsync(db, tenantId, cases.Select(c => c.EmployeeId), ct);
        var companyIds = cases.Select(c => c.CompanyId!.Value).Distinct().ToList();
        var companies = await db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId && companyIds.Contains(c.Id))
            .Select(c => new { c.Id, c.LegalNameEn, c.LegalNameAr })
            .ToDictionaryAsync(c => c.Id, ct);
        var employeeIntIds = employees.Values.Select(e => e.Id).ToList();
        var offboarding = (await db.EmployeeOffboardings.AsNoTracking()
            .Where(o => o.TenantId == tenantId && employeeIntIds.Contains(o.EmployeeId) && o.Status == "InProgress")
            .Select(o => o.EmployeeId).ToListAsync(ct)).ToHashSet();

        var list = new List<RenewalCaseItemDto>();
        foreach (var c in cases)
        {
            var contract = ContractOf(c);
            var employee = employees.GetValueOrDefault(c.EmployeeId) ?? new RenewalEmployeeDto(0, c.EmployeeId, "", null, "");
            var leaving = offboarding.Contains(employee.Id);
            var badges = RenewalNextStep.Badges(c, today, contract?.RenewalNumber, contract?.ChainStartedOn, rules, leaving,
                contractExpired: contract?.Status == "Expired");
            var blockReasons = badges.Select(b => b.BlockCode).OfType<string>().Distinct()
                .Select(code => ReleaseABlockReasons.All[code]).ToList();
            list.Add(new RenewalCaseItemDto(
                c.Id, contract?.Id ?? c.ExpiringContractId, contract?.ContractNumber ?? "", employee, c.CompanyId!.Value,
                companies.GetValueOrDefault(c.CompanyId!.Value)?.LegalNameEn ?? "",
                companies.GetValueOrDefault(c.CompanyId!.Value) is { LegalNameAr: { Length: > 0 } ar } ? ar : null,
                c.WorkerNationalityClass, c.ExpiringEndDate,
                c.ExpiringEndDate.DayNumber - today.DayNumber, RenewalStateMachine.StageOf(c.State), c.State, c.HoldReason,
                c.AllowedActions, c.ContractAction, contract?.RenewalNumber, contract?.ChainStartedOn,
                RenewalNextStep.Next(c, today), badges, blockReasons,
                new RenewalDeadlinesDto(c.OfferDueOn, c.NoticeDueOn, c.QiwaSubmitDueOn, c.QiwaGateDueOn, c.QiwaRespondByOn, c.NextHardDeadline),
                RenewalNextStep.FastLaneEligible(c, today, leaving), versions.HasPendingAmendment(c.ExpiringContractId)));
        }
        return list;
    }

    public static async Task<Dictionary<Guid, RenewalEmployeeDto>> EmployeesAsync(ZayraDbContext db, Guid tenantId, IEnumerable<Guid> publicIds,
        CancellationToken ct)
    {
        var ids = publicIds.Distinct().ToList();
        return await db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && ids.Contains(e.PublicId))
            .Select(e => new RenewalEmployeeDto(e.Id, e.PublicId, e.FullName, e.ArabicName == "" ? null : e.ArabicName, e.EmployeeCode))
            .ToDictionaryAsync(e => e.PublicId, ct);
    }

    /// <summary>The day the term was signed (employee signature, else HR's), shown beside the start-date anchor.</summary>
    public static DateOnly? SignedOn(EmployeeContract c) =>
        (c.SignedByEmployeeAtUtc ?? c.SignedByHrAtUtc) is { } at ? DateOnly.FromDateTime(at) : null;

    /// <summary>A detached copy of a contract's chain fields with a nationality class (for derivations).</summary>
    public static EmployeeContract Clone(EmployeeContract c, string? nationality) => new()
    {
        Id = c.Id, TenantId = c.TenantId, CompanyId = c.CompanyId, EmployeeId = c.EmployeeId, StartDate = c.StartDate, EndDate = c.EndDate,
        RenewalNumber = c.RenewalNumber, ChainStartedOn = c.ChainStartedOn, NonRenewalNoticeDays = c.NonRenewalNoticeDays,
        WorkerNationalityClass = nationality ?? c.WorkerNationalityClass, RenewedFromContractId = c.RenewedFromContractId,
        AutoRenew = c.AutoRenew, ProvisionalBasis = c.ProvisionalBasis,
    };
}
