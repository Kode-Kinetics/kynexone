using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Contracts;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Contracts;

public sealed record RenewalEmployeeDto(int Id, Guid PublicId, string Name, string? NameAr, string Code);

public sealed record RenewalDeadlinesDto(
    DateOnly? OfferDueOn, DateOnly? NoticeDueOn, DateOnly? QiwaSubmitDueOn, DateOnly? QiwaGateDueOn, DateOnly? QiwaRespondByOn,
    DateOnly? NextHardDeadline);

/// <summary>One dashboard row (and the head of the full case DTO).</summary>
/// <param name="FastLaneEligible">R5's batch "renew on current terms" may take it (<see cref="RenewalNextStep.FastLaneEligible"/>).</param>
public sealed record RenewalCaseItemDto(
    Guid CaseId,
    Guid ContractId,
    string ContractNumber,
    RenewalEmployeeDto Employee,
    Guid CompanyId,
    string CompanyName,
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
    bool FastLaneEligible);

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

public sealed record RenewalExceptionsDto(
    IReadOnlyList<RenewalUnopenedDto> ExpiringWithoutCase,
    RenewalExceptionDto NeedsConfirmation,
    RenewalExceptionDto NoticeDatePassed,
    RenewalExceptionDto QiwaOverdue,
    RenewalExceptionDto Art55Threshold,
    RenewalExceptionDto ExpiredNoOutcome);

public sealed record RenewalRadarDto(DateOnly Today, int Days, IReadOnlyList<RenewalBucketDto> Buckets, RenewalExceptionsDto Exceptions,
    IReadOnlyList<RenewalCaseItemDto> Items);

/// <summary>
/// The renewal read side. Runs on the request's context, so the tenant and company-scope filters apply on top of the
/// explicit tenant predicates; nothing here writes. Every number on the dashboard is a count of rows returned in the
/// same response (bucket and exception ids), so a tile always reconciles to its drill-down. Slice R4.
/// </summary>
public static class RenewalCaseReadModel
{
    public const string AwaitingDailyRun = "AwaitingDailyRun";

    public static readonly int[] BucketEdges = [30, 60, 90, 120];

    /// <summary>The dashboard: open cases ending within <paramref name="days"/> (and every overdue one), buckets, exceptions.</summary>
    public static async Task<RenewalRadarDto> RadarAsync(ZayraDbContext db, RenewalCaseOpener opener, Guid tenantId, Guid? companyId, int days,
        DateOnly today, CancellationToken ct)
    {
        var rules = await RenewalRuleSet.LoadAsync(db, tenantId, today, ct);
        var horizon = today.AddDays(days);
        var cases = await db.ContractRenewalCases.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.ClosedAt == null && c.ExpiringEndDate <= horizon
                        && (companyId == null || c.CompanyId == companyId))
            .ToListAsync(ct);
        var items = await ItemsAsync(db, tenantId, cases, today, rules, ct);
        items = items.OrderBy(i => i.Next?.DueOn ?? i.ExpiringEndDate).ThenBy(i => i.Employee.Name).ToList();

        var buckets = new List<RenewalBucketDto>();
        var from = 0;
        foreach (var edge in BucketEdges.Where(e => e - 30 < days))
        {
            var to = Math.Min(edge, days);
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

        var withCase = (await db.ContractRenewalCases.AsNoTracking()
            .Where(c => c.TenantId == tenantId).Select(c => c.ExpiringContractId).ToListAsync(ct)).ToHashSet();
        var unopened = new List<RenewalUnopenedDto>();
        var candidates = (await opener.CandidatesAsync(tenantId, companyId, horizon, tracked: false, ct))
            .Where(c => !withCase.Contains(c.Contract.Id)).ToList();
        var employees = await EmployeesAsync(db, tenantId, candidates.Select(c => c.Contract.EmployeeId), ct);
        foreach (var candidate in candidates)
        {
            var plan = RenewalCaseOpener.Plan(candidate, rules, today);
            if (plan.SkipReason is RenewalOpenSkipReasons.NotDue or RenewalOpenSkipReasons.SuccessorOnFile) continue;
            var block = plan.BlockCode is { } code && ReleaseABlockReasons.All.TryGetValue(code, out var reason) ? reason : null;
            unopened.Add(new RenewalUnopenedDto(candidate.Contract.Id, candidate.Contract.ContractNumber,
                employees.GetValueOrDefault(candidate.Contract.EmployeeId), candidate.Contract.EndDate!.Value, plan.Deadlines!.OpensOn,
                plan.SkipReason ?? AwaitingDailyRun, block));
        }

        var exceptions = new RenewalExceptionsDto(
            unopened.OrderBy(u => u.EndDate).ToList(),
            Tile(i => i.State == RenewalStates.NeedsConfirmation || Has(i, RenewalBadgeCodes.ChainUnconfirmed)),
            Tile(i => Has(i, RenewalBadgeCodes.NoticeDatePassed)),
            Tile(i => Has(i, RenewalBadgeCodes.QiwaOverdue)),
            Tile(i => Has(i, RenewalBadgeCodes.Art55Threshold)),
            Tile(i => Has(i, RenewalBadgeCodes.ExpiredNoOutcome)));
        return new RenewalRadarDto(today, days, buckets, exceptions, items);
    }

    /// <summary>The full DTO of one case, or NULL when it is not visible to the caller.</summary>
    public static async Task<RenewalCaseDto?> CaseAsync(ZayraDbContext db, Guid tenantId, Guid caseId, DateOnly today, CancellationToken ct)
    {
        var c = await db.ContractRenewalCases.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == caseId, ct);
        if (c is null) return null;
        var rules = await RenewalRuleSet.LoadAsync(db, tenantId, today, ct);
        var item = (await ItemsAsync(db, tenantId, [c], today, rules, ct)).Single();
        var contract = await db.EmployeeContracts.AsNoTracking()
            .FirstAsync(x => x.TenantId == tenantId && x.Id == c.ExpiringContractId, ct);
        var view = Clone(contract, c.WorkerNationalityClass);
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
        var contractIds = cases.Select(c => c.ExpiringContractId).ToList();
        var contracts = await db.EmployeeContracts.AsNoTracking()
            .Where(c => c.TenantId == tenantId && contractIds.Contains(c.Id))
            .Select(c => new { c.Id, c.ContractNumber, c.RenewalNumber, c.ChainStartedOn })
            .ToDictionaryAsync(c => c.Id, ct);
        var employees = await EmployeesAsync(db, tenantId, cases.Select(c => c.EmployeeId), ct);
        var companyIds = cases.Select(c => c.CompanyId!.Value).Distinct().ToList();
        var companies = await db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId && companyIds.Contains(c.Id))
            .Select(c => new { c.Id, c.LegalNameEn })
            .ToDictionaryAsync(c => c.Id, c => c.LegalNameEn, ct);
        var employeeIntIds = employees.Values.Select(e => e.Id).ToList();
        var offboarding = (await db.EmployeeOffboardings.AsNoTracking()
            .Where(o => o.TenantId == tenantId && employeeIntIds.Contains(o.EmployeeId) && o.Status == "InProgress")
            .Select(o => o.EmployeeId).ToListAsync(ct)).ToHashSet();

        var list = new List<RenewalCaseItemDto>();
        foreach (var c in cases)
        {
            contracts.TryGetValue(c.ExpiringContractId, out var contract);
            var employee = employees.GetValueOrDefault(c.EmployeeId) ?? new RenewalEmployeeDto(0, c.EmployeeId, "", null, "");
            var leaving = offboarding.Contains(employee.Id);
            var badges = RenewalNextStep.Badges(c, today, contract?.RenewalNumber, contract?.ChainStartedOn, rules, leaving);
            var blockReasons = badges.Select(b => b.BlockCode).OfType<string>().Distinct()
                .Select(code => ReleaseABlockReasons.All[code]).ToList();
            list.Add(new RenewalCaseItemDto(
                c.Id, c.ExpiringContractId, contract?.ContractNumber ?? "", employee, c.CompanyId!.Value,
                companies.GetValueOrDefault(c.CompanyId!.Value) ?? "", c.WorkerNationalityClass, c.ExpiringEndDate,
                c.ExpiringEndDate.DayNumber - today.DayNumber, RenewalStateMachine.StageOf(c.State), c.State, c.HoldReason,
                c.AllowedActions, c.ContractAction, contract?.RenewalNumber, contract?.ChainStartedOn,
                RenewalNextStep.Next(c, today), badges, blockReasons,
                new RenewalDeadlinesDto(c.OfferDueOn, c.NoticeDueOn, c.QiwaSubmitDueOn, c.QiwaGateDueOn, c.QiwaRespondByOn, c.NextHardDeadline),
                RenewalNextStep.FastLaneEligible(c, today, leaving)));
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
