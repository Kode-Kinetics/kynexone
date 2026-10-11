using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Privacy;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Engagement;

/// <summary>Who is looking: their companies (null = every company) and, for a line manager, the managers whose
/// teams they may see (null = no team restriction).</summary>
public sealed record EngagementResultsScope(IReadOnlyCollection<Guid>? CompanyIds, IReadOnlyCollection<int>? TeamManagerIds)
{
    public static readonly EngagementResultsScope Everything = new(null, null);
}

/// <summary>Cohort dimensions a result can be filtered or broken down by.</summary>
public static class EngagementDimensions
{
    public const string Company = "company";
    public const string Department = "department";
    public const string Designation = "designation";
    public const string Grade = "grade";
    public const string Tenure = "tenure";
    /// <summary>The line manager at submission. Used only to scope a manager's view; never offered as a breakdown.</summary>
    public const string Team = "team";
    public static readonly string[] Filterable = [Company, Department, Designation, Grade, Tenure];
    /// <summary>Every dimension of a survey's view family — the filterable ones plus the team scope — most important
    /// first: when views must be hidden to protect a small group, tenure goes before grade, designation, team,
    /// department and company (the scopes readers work in).</summary>
    public static readonly string[] Family = [Company, Department, Team, Designation, Grade, Tenure];
}

/// <summary>A request combined more filters (a breakdown counting as one) than the view family permits.</summary>
public sealed class EngagementViewRefusedException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public sealed record EngagementFilterDto(string Dimension, string Value, string Label);

public sealed record EngagementParticipationDto(bool Visible, int? Invited, int? Submitted, decimal? ResponseRatePct, string Definition);

public sealed record EngagementDistributionDto(string Key, string LabelEn, string LabelAr, int Count, decimal Pct);

public sealed record EngagementEnpsDto(decimal Score, decimal PromotersPct, decimal PassivesPct, decimal DetractorsPct, int Answered);

public sealed record EngagementItemDto(
    string Key, string Type, string TextEn, string TextAr, string? Driver,
    bool Visible, string? SuppressionReason, int? Answered, decimal? Mean, decimal? FavourablePct,
    EngagementEnpsDto? Enps, IReadOnlyList<EngagementDistributionDto> Distribution, int? CommentCount);

public sealed record EngagementDriverDto(string Key, string LabelEn, string LabelAr, bool Visible, string? SuppressionReason,
    int? Respondents, decimal? Mean, decimal? FavourablePct);

public sealed record EngagementCellDto(string Value, string Label, bool Visible, string? SuppressionReason,
    int? Respondents, decimal? FavourablePct, decimal? Enps, IReadOnlyDictionary<string, decimal?> DriverFavourablePct);

public sealed record EngagementBreakdownDto(string Dimension, IReadOnlyList<EngagementCellDto> Cells);

public sealed record EngagementResultsDto(
    Guid SurveyId, string TitleEn, string TitleAr, string Mode, string Status, int CycleNumber, int MinCohortSize,
    EngagementParticipationDto Participation,
    IReadOnlyList<EngagementFilterDto> Filters,
    bool Visible, string? SuppressionReason, int? Respondents,
    decimal? FavourablePct, EngagementEnpsDto? Enps,
    IReadOnlyList<EngagementDriverDto> Drivers,
    IReadOnlyList<EngagementItemDto> Items,
    EngagementBreakdownDto? Breakdown,
    string SuppressedMessage,
    DateTime GeneratedAtUtc,
    bool ResultsAvailable = true,
    DateTime? ResultsAvailableFromUtc = null,
    string CommentDimension = "department",
    string? ScopeDimension = null);

public sealed record EngagementTrendPointDto(Guid SurveyId, int CycleNumber, DateTime OpensAtUtc, string Status,
    bool Visible, int? Respondents, decimal? ResponseRatePct, decimal? FavourablePct, decimal? Enps);

/// <summary>
/// Every engagement number on every surface — dashboard, drill-down, comments, export, scheduled email, trend — is
/// computed here and suppressed by <see cref="CohortViewFamily"/>. A second computation anywhere else is a defect.
///
/// <para><b>Definitions</b> (REGISTER §3.2.6; ENG_DESIGN.md §5): a respondent is a submitted response (drafts never
/// reach these tables). Response rate = submitted ÷ invitations not cancelled. Item mean = mean of answered values; a
/// skipped item leaves that item's denominator. Favourable = answers of 4 or 5 on the 5-point scale ÷ answered.
/// eNPS = %promoters (9–10) − %detractors (0–6) over those who answered the eNPS item. Driver = the same over all
/// answered 5-point items in the driver. Percentages are rounded half away from zero to 1 dp, means to 2 dp.</para>
///
/// <para><b>When.</b> Confidential and Anonymous results exist only once the survey is CLOSED: a live view would let a
/// reader difference the results before and after one person submits. While open, only the whole-survey response rate
/// is shown (with k applied). Identified surveys stay live — their answers are linked to people by design.</para>
///
/// <para><b>Populations.</b> Each metric is decided on ITS OWN population (the people who answered that item / driver)
/// by ONE <see cref="CohortViewFamily"/> over the whole survey, in which every reader's scope (company, team) is a
/// dimension: no two requests — by one reader or by readers with different scopes — can be combined to isolate a small
/// group. A driver or the overall favourable score is shown in a view only if every item it is made of is.</para>
/// </summary>
public sealed class EngagementResultsService
{
    private readonly ZayraDbContext _db;
    private readonly TimeProvider _clock;

    private const string WholeSurveyRead =
        "Suppression must count the whole survey: rows outside the caller's companies are used only as complement " +
        "denominators and are never shown; the caller's scope is re-applied as a cohort filter.";

    public EngagementResultsService(ZayraDbContext db, TimeProvider? clock = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Confidential and Anonymous results only after the survey closes; Identified results are live.</summary>
    public static bool ResultsAvailable(EngagementSurvey survey) =>
        survey.Mode == EngagementSurveyModes.Identified
            ? survey.Status is EngagementSurveyStatuses.Open or EngagementSurveyStatuses.Closed
            : survey.Status == EngagementSurveyStatuses.Closed;

    public static string NotYetMessage(EngagementSurvey survey) =>
        $"Results appear when the survey closes on {survey.ClosesAtUtc:yyyy-MM-dd}.";

    // ── Loading ──────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record ResponseRow(Guid Id, Guid? CompanyId, Guid? DepartmentId, Guid? DesignationId, string GradeBand, string TenureBand, int? Manager);
    private sealed record AnswerRow(Guid ResponseId, string QuestionKey, int? NumericValue, string? ChoiceKeys, bool HasText);

    private sealed class Loaded
    {
        public required EngagementSurvey Survey { get; init; }
        public required EngagementTemplateContent Content { get; init; }
        public required List<ResponseRow> Responses { get; init; }
        public required Dictionary<Guid, CohortRecord> Records { get; init; }
        public required ILookup<string, AnswerRow> AnswersByQuestion { get; init; }
        /// <summary>The caller's scope as family components (null = the whole survey).</summary>
        public required CohortScope? Scope { get; init; }
        /// <summary>The k the survey's results were frozen at (close) and today's k (≥ it); see <see cref="ResultsKAsync"/>.</summary>
        public required int FrozenK { get; init; }
        public required int CurrentK { get; init; }
        public int K => Math.Max(FrozenK, CurrentK);
        /// <summary>The survey's invitees (cancelled excluded) with their org data — what scope nesting and breakdown
        /// cells are decided on, never the respondents.</summary>
        public required List<CohortRecord> Invitees { get; init; }
        public Dictionary<string, (CohortViewFamily Family, CohortPopulation Population)> Families { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, (CohortViewFamily Family, CohortPopulation Population)> FamiliesByPopulation { get; } = new(StringComparer.Ordinal);
        /// <summary>Each record's position, to key a population by its members.</summary>
        public required Dictionary<CohortRecord, int> RecordIndex { get; init; }
    }

    private async Task<Loaded> LoadAsync(EngagementSurvey survey, EngagementResultsScope scope, CancellationToken ct)
    {
        var template = await _db.EngagementSurveyTemplates.AsNoTracking()
            .FirstAsync(t => t.TenantId == survey.TenantId && t.Id == survey.TemplateId, ct);
        var responses = await ScopedBypass.TenantWide(_db.EngagementSurveyResponses, survey.TenantId, WholeSurveyRead)
            .AsNoTracking().Where(r => r.SurveyId == survey.Id)
            .Select(r => new ResponseRow(r.Id, r.CompanyId, r.DepartmentId, r.DesignationId, r.GradeBand, r.TenureBand, r.CohortManagerEmployeeId))
            .ToListAsync(ct);
        var answers = await ScopedBypass.TenantWide(_db.EngagementSurveyAnswers, survey.TenantId, WholeSurveyRead)
            .AsNoTracking().Where(a => a.SurveyId == survey.Id)
            .Select(a => new AnswerRow(a.ResponseId, a.QuestionKey, a.NumericValue, a.ChoiceKeys, a.TextValue != null && a.TextValue != ""))
            .ToListAsync(ct);
        var records = responses.ToDictionary(r => r.Id, ToRecord);
        var invitees = (await InviteesAsync(survey, ct)).Select(i => i.Record).ToList();
        var (frozenK, currentK) = await ResultsKAsync(survey, ct);
        return new Loaded
        {
            Survey = survey,
            Content = EngagementTemplateContent.Parse(template.ContentJson),
            Responses = responses,
            Records = records,
            AnswersByQuestion = answers.ToLookup(a => a.QuestionKey, StringComparer.Ordinal),
            Scope = ScopeOf(scope, invitees),
            RecordIndex = records.Values.Select((r, i) => (r, i)).ToDictionary(x => x.r, x => x.i),
            FrozenK = frozenK,
            CurrentK = currentK,
            Invitees = invitees,
        };
    }

    private static CohortRecord ToRecord(ResponseRow r) => new(new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        [EngagementDimensions.Company] = r.CompanyId?.ToString(),
        [EngagementDimensions.Department] = r.DepartmentId?.ToString(),
        [EngagementDimensions.Designation] = r.DesignationId?.ToString(),
        [EngagementDimensions.Grade] = r.GradeBand,
        [EngagementDimensions.Tenure] = r.TenureBand,
        [EngagementDimensions.Team] = r.Manager?.ToString(),
    });

    /// <summary>
    /// The caller's scope as family components. A line manager who is also cut by company is scoped by team alone when
    /// every team member in the survey lies inside their companies (the company cut then adds nothing), and by
    /// (company, team) pairs otherwise.
    /// </summary>
    public static CohortScope? ScopeOf(EngagementResultsScope scope, IEnumerable<CohortRecord> invitees)
    {
        var company = scope.CompanyIds is { } companies
            ? new CohortFilter(EngagementDimensions.Company, companies.Select(c => c.ToString()).ToHashSet(StringComparer.Ordinal)) : null;
        var team = scope.TeamManagerIds is { } managers
            ? new CohortFilter(EngagementDimensions.Team, managers.Select(m => m.ToString()).ToHashSet(StringComparer.Ordinal)) : null;
        if (company is not null && team is not null && invitees.Where(team.Matches).All(company.Matches)) company = null;
        return CohortScope.From(new[] { company, team }.OfType<CohortFilter>().ToList());
    }

    /// <summary>The scope a request is evaluated under — decided once, on the respondents' family.</summary>
    private CohortScope? Resolve(Loaded data, IReadOnlyList<CohortFilter> filters, int extraDimensions)
    {
        try { return Respondents(data).Family.Resolve(data.Scope, filters, extraDimensions, nestingRecords: data.Invitees); }
        catch (CohortViewNotPermittedException)
        {
            if (data.Scope is null)
                throw new EngagementViewRefusedException(EngagementErrors.TooManyFilters,
                    $"Combine at most {CohortViewFamily.MaxFilters} of: filters and a breakdown. Smaller intersections could identify people.");
            throw new EngagementViewRefusedException(EngagementErrors.ScopeCountsAsFilter,
                $"Your view is limited to your {ScopeDimensionOf(data.Scope)}, which counts as one of the {CohortViewFamily.MaxFilters}: add one filter or one breakdown, not both.");
        }
    }

    /// <summary>"team" or "company" — what a scoped reader's view is limited to (null: the whole survey).</summary>
    public static string? ScopeDimensionOf(CohortScope? scope) =>
        scope is null ? null
        : scope.Components.Any(c => c.Any(p => p.Dim == EngagementDimensions.Team)) ? EngagementDimensions.Team : EngagementDimensions.Company;

    private static string? ScopeDimensionOf(EngagementResultsScope scope) =>
        scope.TeamManagerIds is not null ? EngagementDimensions.Team : scope.CompanyIds is not null ? EngagementDimensions.Company : null;

    /// <summary>Filters from the request: only the filterable dimensions, one value each.</summary>
    public static List<CohortFilter> RequestFilters(IReadOnlyDictionary<string, string?> requested) =>
        EngagementDimensions.Filterable
            .Where(d => requested.TryGetValue(d, out var v) && !string.IsNullOrWhiteSpace(v))
            .Select(d => new CohortFilter(d, new HashSet<string>(StringComparer.Ordinal) { requested[d]!.Trim() }))
            .ToList();

    /// <summary>Refuses a request that combines more than two of: filters and a breakdown.</summary>
    public static void EnsurePermitted(IReadOnlyDictionary<string, string?> requested, string? breakdown)
    {
        var count = RequestFilters(requested).Count + (string.IsNullOrWhiteSpace(breakdown) ? 0 : 1);
        if (count > CohortViewFamily.MaxFilters)
            throw new EngagementViewRefusedException(EngagementErrors.TooManyFilters,
                $"Combine at most {CohortViewFamily.MaxFilters} of: filters and a breakdown. Smaller intersections could identify people.");
        if (breakdown is { Length: > 0 } b && !EngagementDimensions.Filterable.Contains(b, StringComparer.Ordinal))
            throw new EngagementViewRefusedException(EngagementErrors.InvalidRequest, "Break down by company, department, designation, grade or tenure.");
        if (breakdown is { Length: > 0 } && RequestFilters(requested).Any(f => f.Dimension == breakdown))
            throw new EngagementViewRefusedException(EngagementErrors.InvalidRequest, "A filtered dimension cannot also be the breakdown.");
    }

    /// <summary>The view family for one metric population (cached per load; metrics answered by exactly the same
    /// people — every required item — share one family).</summary>
    private (CohortViewFamily Family, CohortPopulation Population) FamilyFor(Loaded data, string key, Func<IEnumerable<CohortRecord>> records)
    {
        if (data.Families.TryGetValue(key, out var hit)) return hit;
        var members = records().ToList();
        var populationKey = string.Join(",", members.Select(r => data.RecordIndex[r]).Order());
        if (!data.FamiliesByPopulation.TryGetValue(populationKey, out var built))
        {
            var population = new CohortPopulation(members);
            built = (CohortViewFamily.BuildFrozen(population, EngagementDimensions.Family, data.FrozenK, data.CurrentK), population);
            data.FamiliesByPopulation[populationKey] = built;
        }
        data.Families[key] = built;
        return built;
    }

    private (CohortViewFamily Family, CohortPopulation Population) Respondents(Loaded data) =>
        FamilyFor(data, "respondents", () => data.Records.Values);

    private (CohortViewFamily Family, CohortPopulation Population) AnsweredFamily(Loaded data, string key, IReadOnlyCollection<string> questionKeys, bool text = false) =>
        FamilyFor(data, key, () =>
        {
            var ids = new HashSet<Guid>();
            foreach (var q in questionKeys)
                foreach (var a in data.AnswersByQuestion[q])
                    if (text ? a.HasText : (a.NumericValue is not null || a.ChoiceKeys is not null)) ids.Add(a.ResponseId);
            return ids.Where(data.Records.ContainsKey).Select(id => data.Records[id]);
        });

    // ── Results ──────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<EngagementResultsDto> ComputeAsync(
        EngagementSurvey survey, EngagementResultsScope scope, IReadOnlyDictionary<string, string?> requestedFilters,
        string? breakdownDimension, CancellationToken ct)
    {
        EnsurePermitted(requestedFilters, breakdownDimension);
        var k = await EffectiveKAsync(survey, ct);
        var message = string.Format(CohortSuppression.SuppressedMessage, k);
        var labels = await LabelsAsync(survey.TenantId, ct);
        var userFilters = RequestFilters(requestedFilters);
        var filterDtos = userFilters.Select(f => new EngagementFilterDto(f.Dimension, f.Values.First(), labels.Label(f.Dimension, f.Values.First()))).ToList();

        if (!ResultsAvailable(survey))
        {
            // While open: the response rate of the caller's own scope only (k applied), nothing that changes per answer.
            var live = await ParticipationAsync(survey, scope, k, ct);
            return new EngagementResultsDto(survey.Id, survey.TitleEn, survey.TitleAr, survey.Mode, survey.Status, survey.CycleNumber, k,
                live, filterDtos, false, EngagementErrors.ResultsAfterClose, null, null, null, [], [], null, message, Now,
                ResultsAvailable: false, ResultsAvailableFromUtc: survey.ClosesAtUtc, CommentDimension: survey.CommentDimension,
                ScopeDimension: ScopeDimensionOf(scope));
        }

        var data = await LoadAsync(survey, scope, ct);
        var hasBreakdown = breakdownDimension is { Length: > 0 };
        var resolved = Resolve(data, userFilters, hasBreakdown ? 1 : 0);
        var participation = await ParticipationAsync(survey, scope, k, ct);
        var viewDecision = Respondents(data).Family.Evaluate(resolved, userFilters);

        if (!viewDecision.IsVisible)
        {
            return new EngagementResultsDto(survey.Id, survey.TitleEn, survey.TitleAr, survey.Mode, survey.Status, survey.CycleNumber, k,
                participation, filterDtos, false, SuppressionReasons.Suppressed, null, null, null,
                data.Content.Drivers.Select(d => new EngagementDriverDto(d.Key, d.LabelEn, d.LabelAr, false, SuppressionReasons.Suppressed, null, null, null)).ToList(),
                data.Content.Questions.Select(q => HiddenItem(q, SuppressionReasons.Suppressed)).ToList(),
                null, message, Now, CommentDimension: survey.CommentDimension, ScopeDimension: ScopeDimensionOf(data.Scope));
        }

        var ids = MatchingIds(data, resolved, userFilters);
        var items = data.Content.Questions.Select(q => Item(data, q, resolved, userFilters, ids)).ToList();
        // Items hidden in this view that hold at least one answer in it (an item nobody here answered adds nothing).
        var hiddenItems = items.Where(i => !i.Visible && AnsweredIn(data, i.Key, ids)).Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        var drivers = data.Content.Drivers.Select(d => Driver(data, d, resolved, userFilters, ids, hiddenItems)).ToList();
        var likert = LikertKeys(data.Content, null);
        decimal? overallFav = null;
        // The overall score contains every 5-point item: one hidden item hides it (else overall − the rest = that item).
        if (likert.Count > 0 && !likert.Any(hiddenItems.Contains)
            && AnsweredFamily(data, "likert:*", likert).Family.Evaluate(resolved, userFilters).IsVisible)
        {
            var values = LikertValues(data, likert, ids);
            overallFav = Pct(values.Count(v => v >= 4), values.Count);
        }
        var enps = EnpsFor(data, resolved, userFilters, ids);

        EngagementBreakdownDto? breakdown = null;
        if (hasBreakdown)
            breakdown = Breakdown(data, resolved, userFilters, breakdownDimension!, labels);

        return new EngagementResultsDto(survey.Id, survey.TitleEn, survey.TitleAr, survey.Mode, survey.Status, survey.CycleNumber, k,
            participation, filterDtos, true, null, viewDecision.Count, overallFav, enps, drivers, items, breakdown, message, Now,
            CommentDimension: survey.CommentDimension, ScopeDimension: ScopeDimensionOf(data.Scope));
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Today's k for a survey: the larger of the survey's own k and the tenant's current default, never below the
    /// floor. On its own it decides nothing for a closed survey — see <see cref="ResultsKAsync"/>.
    /// </summary>
    public async Task<int> CurrentKAsync(EngagementSurvey survey, CancellationToken ct) =>
        CurrentK(survey, await ScopedBypass.TenantWide(_db.TenantHrConfigs, survey.TenantId,
                "The tenant's own engagement minimum applies to every survey of that tenant, whoever is asking.")
            .AsNoTracking().Select(c => (int?)c.EngagementMinCohortSize).FirstOrDefaultAsync(ct) ?? CohortSuppression.DefaultK);

    public static int CurrentK(EngagementSurvey survey, int tenantK) =>
        Math.Max(CohortSuppression.Floor, Math.Max(survey.MinCohortSize, tenantK));

    /// <summary>
    /// The k shown to readers: for a closed survey the larger of its frozen k and today's; otherwise today's.
    /// </summary>
    public async Task<int> EffectiveKAsync(EngagementSurvey survey, CancellationToken ct)
    {
        var (frozen, current) = await ResultsKAsync(survey, ct);
        return Math.Max(frozen, current);
    }

    /// <summary>
    /// (frozen, current) k for results. A CLOSED survey's results are decided at the k frozen when it closed — the
    /// family's visible views at that k — and a later, larger k can only hide more of them
    /// (<see cref="CohortViewFamily.BuildFrozen"/>). Without the freeze, raising the tenant's k, re-reading and lowering
    /// it again would show two different families whose union isolates small groups. A survey closed before the
    /// column existed is frozen here, on its first results computation. An open (Identified) survey is live by design
    /// and uses today's k.
    /// </summary>
    public async Task<(int Frozen, int Current)> ResultsKAsync(EngagementSurvey survey, CancellationToken ct)
    {
        var current = await CurrentKAsync(survey, ct);
        if (survey.Status != EngagementSurveyStatuses.Closed) return (current, current);
        if (survey.FrozenMinCohortSize is int frozen) return (frozen, current);
        await ScopedBypass.TenantWide(_db.EngagementSurveys, survey.TenantId, "Freezing a closed survey's k is a survey-level write.")
            .Where(s => s.Id == survey.Id && s.FrozenMinCohortSize == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.FrozenMinCohortSize, current), ct);
        frozen = await ScopedBypass.TenantWide(_db.EngagementSurveys, survey.TenantId, "Reading the frozen k back (another request may have frozen it first).")
            .AsNoTracking().Where(s => s.Id == survey.Id).Select(s => s.FrozenMinCohortSize).FirstAsync(ct) ?? current;
        survey.FrozenMinCohortSize = frozen;
        return (frozen, current);
    }

    private static EngagementItemDto HiddenItem(EngagementQuestion q, string reason) =>
        new(q.Key, q.Type, q.TextEn, q.TextAr, q.Driver, false, reason, null, null, null, null, [], null);

    private static HashSet<Guid> MatchingIds(Loaded data, CohortScope? scope, IReadOnlyList<CohortFilter> filters) =>
        data.Responses.Where(r =>
        {
            var record = data.Records[r.Id];
            return (scope is null || scope.Matches(record)) && filters.All(f => f.Matches(record));
        }).Select(r => r.Id).ToHashSet();

    private static bool AnsweredIn(Loaded data, string questionKey, HashSet<Guid> ids) =>
        data.AnswersByQuestion[questionKey].Any(a => ids.Contains(a.ResponseId) && (a.NumericValue is not null || a.ChoiceKeys is not null || a.HasText));

    private static List<string> LikertKeys(EngagementTemplateContent content, string? driver) =>
        content.Questions.Where(q => q.Type == EngagementQuestionTypes.Likert5 && (driver is null || q.Driver == driver)).Select(q => q.Key).ToList();

    private (CohortViewFamily Family, CohortPopulation Population) ItemFamily(Loaded data, EngagementQuestion q) =>
        q.Type == EngagementQuestionTypes.FreeText
            ? AnsweredFamily(data, "text:" + q.Key, [q.Key], text: true)
            : AnsweredFamily(data, "item:" + q.Key, [q.Key]);

    private EngagementItemDto Item(Loaded data, EngagementQuestion q, CohortScope? scope, IReadOnlyList<CohortFilter> userFilters, HashSet<Guid> ids)
    {
        var decision = ItemFamily(data, q).Family.Evaluate(scope, userFilters);
        if (!decision.IsVisible) return HiddenItem(q, SuppressionReasons.Suppressed);

        var answers = data.AnswersByQuestion[q.Key].Where(a => ids.Contains(a.ResponseId)).ToList();
        switch (q.Type)
        {
            case EngagementQuestionTypes.Likert5:
            {
                var values = answers.Where(a => a.NumericValue is not null).Select(a => a.NumericValue!.Value).ToList();
                var n = values.Count;
                var dist = Enumerable.Range(1, 5).Select(v => new EngagementDistributionDto(v.ToString(), LikertLabels[v - 1].En, LikertLabels[v - 1].Ar,
                    values.Count(x => x == v), Pct(values.Count(x => x == v), n))).ToList();
                return new EngagementItemDto(q.Key, q.Type, q.TextEn, q.TextAr, q.Driver, true, null, n,
                    Mean(values), Pct(values.Count(v => v >= 4), n), null, dist, null);
            }
            case EngagementQuestionTypes.Enps:
            {
                var values = answers.Where(a => a.NumericValue is not null).Select(a => a.NumericValue!.Value).ToList();
                var dist = Enumerable.Range(0, 11).Select(v => new EngagementDistributionDto(v.ToString(), v.ToString(), v.ToString(),
                    values.Count(x => x == v), Pct(values.Count(x => x == v), values.Count))).ToList();
                return new EngagementItemDto(q.Key, q.Type, q.TextEn, q.TextAr, q.Driver, true, null, values.Count,
                    Mean(values), null, Enps(values), dist, null);
            }
            case EngagementQuestionTypes.SingleChoice:
            case EngagementQuestionTypes.MultiChoice:
            {
                var picks = answers.Where(a => a.ChoiceKeys is not null).Select(a => a.ChoiceKeys!.Split(',')).ToList();
                var n = picks.Count;
                var dist = (q.Options ?? []).Select(o => new EngagementDistributionDto(o.Key, o.LabelEn, o.LabelAr,
                    picks.Count(p => p.Contains(o.Key)), Pct(picks.Count(p => p.Contains(o.Key)), n))).ToList();
                return new EngagementItemDto(q.Key, q.Type, q.TextEn, q.TextAr, q.Driver, true, null, n, null, null, null, dist, null);
            }
            default:
                return new EngagementItemDto(q.Key, q.Type, q.TextEn, q.TextAr, q.Driver, true, null,
                    answers.Count(a => a.HasText), null, null, null, [], answers.Count(a => a.HasText));
        }
    }

    private EngagementDriverDto Driver(Loaded data, EngagementDriver d, CohortScope? scope, IReadOnlyList<CohortFilter> userFilters,
        HashSet<Guid> ids, IReadOnlySet<string> hiddenItems)
    {
        var keys = LikertKeys(data.Content, d.Key);
        if (keys.Count == 0) return new EngagementDriverDto(d.Key, d.LabelEn, d.LabelAr, false, null, null, null, null);
        // A driver contains its items: one hidden item hides the driver (else driver − the rest = that item).
        if (keys.Any(hiddenItems.Contains)) return new EngagementDriverDto(d.Key, d.LabelEn, d.LabelAr, false, SuppressionReasons.Suppressed, null, null, null);
        var decision = AnsweredFamily(data, "driver:" + d.Key, keys).Family.Evaluate(scope, userFilters);
        if (!decision.IsVisible) return new EngagementDriverDto(d.Key, d.LabelEn, d.LabelAr, false, SuppressionReasons.Suppressed, null, null, null);
        var values = LikertValues(data, keys, ids);
        return new EngagementDriverDto(d.Key, d.LabelEn, d.LabelAr, true, null, decision.Count, Mean(values), Pct(values.Count(v => v >= 4), values.Count));
    }

    private static List<int> LikertValues(Loaded data, IEnumerable<string> keys, HashSet<Guid> ids) =>
        keys.SelectMany(k => data.AnswersByQuestion[k])
            .Where(a => a.NumericValue is not null && ids.Contains(a.ResponseId))
            .Select(a => a.NumericValue!.Value).ToList();

    private EngagementEnpsDto? EnpsFor(Loaded data, CohortScope? scope, IReadOnlyList<CohortFilter> userFilters, HashSet<Guid> ids)
    {
        var q = data.Content.Questions.FirstOrDefault(x => x.Type == EngagementQuestionTypes.Enps);
        if (q is null) return null;
        if (!AnsweredFamily(data, "item:" + q.Key, [q.Key]).Family.Evaluate(scope, userFilters).IsVisible) return null;
        return Enps(data.AnswersByQuestion[q.Key].Where(a => a.NumericValue is not null && ids.Contains(a.ResponseId)).Select(a => a.NumericValue!.Value).ToList());
    }

    private EngagementBreakdownDto Breakdown(Loaded data, CohortScope? scope, IReadOnlyList<CohortFilter> userFilters, string dim, OrgLabels labels)
    {
        Dictionary<string, SuppressionDecision> Cells(CohortViewFamily family) =>
            family.Breakdown(scope, userFilters, dim, data.Invitees).ToDictionary(c => c.Value, c => c.Decision, StringComparer.Ordinal);
        bool CellVisible(Dictionary<string, SuppressionDecision> cells, string value) => cells.TryGetValue(value, out var d) && d.IsVisible;

        // Cells are the values among the INVITEES in view, so a hidden cell's presence never says someone responded.
        var respondentCells = Respondents(data).Family.Breakdown(scope, userFilters, dim, data.Invitees);
        var likert = LikertKeys(data.Content, null);
        // Per-item cells: a cell's favourable score or driver is shown only if every item it contains is shown there.
        var itemCells = likert.ToDictionary(key => key, key => Cells(AnsweredFamily(data, "item:" + key, [key]).Family), StringComparer.Ordinal);
        var favCells = likert.Count == 0 ? null : Cells(AnsweredFamily(data, "likert:*", likert).Family);
        var enpsKey = data.Content.Questions.FirstOrDefault(x => x.Type == EngagementQuestionTypes.Enps)?.Key;
        var enpsCells = enpsKey is null ? null : Cells(AnsweredFamily(data, "item:" + enpsKey, [enpsKey]).Family);
        var driverCells = data.Content.Drivers.ToDictionary(d => d.Key, d =>
        {
            var keys = LikertKeys(data.Content, d.Key);
            return keys.Count == 0 ? new Dictionary<string, SuppressionDecision>() : Cells(AnsweredFamily(data, "driver:" + d.Key, keys).Family);
        });

        var cells = new List<EngagementCellDto>();
        foreach (var cell in respondentCells)
        {
            var label = labels.Label(dim, cell.Value);
            if (!cell.Decision.IsVisible)
            {
                cells.Add(new EngagementCellDto(cell.Value, label, false, SuppressionReasons.Suppressed, null, null, null,
                    data.Content.Drivers.ToDictionary(d => d.Key, _ => (decimal?)null)));
                continue;
            }
            var ids = MatchingIds(data, scope, userFilters.Append(new CohortFilter(dim, [cell.Value])).ToList());
            bool ItemsVisible(IEnumerable<string> keys) => keys.All(key => CellVisible(itemCells[key], cell.Value) || !AnsweredIn(data, key, ids));
            decimal? fav = null;
            if (favCells is not null && CellVisible(favCells, cell.Value) && ItemsVisible(likert))
            {
                var values = LikertValues(data, likert, ids);
                fav = Pct(values.Count(v => v >= 4), values.Count);
            }
            decimal? enps = null;
            if (enpsCells is not null && CellVisible(enpsCells, cell.Value))
                enps = Enps(data.AnswersByQuestion[enpsKey!].Where(a => a.NumericValue is not null && ids.Contains(a.ResponseId)).Select(a => a.NumericValue!.Value).ToList())?.Score;
            var driverValues = data.Content.Drivers.ToDictionary(d => d.Key, d =>
            {
                var keys = LikertKeys(data.Content, d.Key);
                if (keys.Count == 0 || !CellVisible(driverCells[d.Key], cell.Value) || !ItemsVisible(keys)) return (decimal?)null;
                var values = LikertValues(data, keys, ids);
                return Pct(values.Count(v => v >= 4), values.Count);
            });
            cells.Add(new EngagementCellDto(cell.Value, label, true, null, cell.Decision.Count, fav, enps, driverValues));
        }
        return new EngagementBreakdownDto(dim, cells);
    }

    // ── Participation ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Submitted ÷ invited (cancelled excluded) for the caller's scope. Decided by the same kind of view family as the
    /// results, over the survey's INVITEES with the scope dimensions (company; team = current line manager), so no two
    /// readers' response rates can be subtracted to show whether fewer than k people submitted. The survey list and
    /// the live (open) view both come through here.
    /// </summary>
    public async Task<EngagementParticipationDto> ParticipationAsync(EngagementSurvey survey, EngagementResultsScope scope, int k, CancellationToken ct)
    {
        const string definition = "Response rate = submitted ÷ invited (cancelled invitations excluded), for everyone in your view. Drafts are not counted.";
        var records = await InviteesAsync(survey, ct);
        // Decided at the survey's own k (fixed at launch, what its respondents were promised), restricted by today's:
        // whatever the tenant's k does over time, the visible views stay a subset of one family.
        var family = CohortViewFamily.BuildFrozen(new CohortPopulation(records.Select(r => r.Record)),
            [EngagementDimensions.Company, EngagementDimensions.Team], survey.MinCohortSize, Math.Max(k, survey.MinCohortSize));
        var cohortScope = ScopeOf(scope, records.Select(r => r.Record));
        var decision = family.Evaluate(family.Resolve(cohortScope, []), []);
        if (!decision.IsVisible) return new EngagementParticipationDto(false, null, null, null, definition);
        var inView = records.Where(r => cohortScope is null || cohortScope.Matches(r.Record)).ToList();
        var submitted = inView.Count(r => r.Submitted);
        return new EngagementParticipationDto(true, inView.Count, submitted, Pct(submitted, inView.Count), definition);
    }

    /// <summary>
    /// The survey's invitees (cancelled excluded) as cohort records on the family's dimensions: company at launch,
    /// and today's department, designation, grade band, tenure band and line manager (team). Who is invited is known
    /// to HR anyway; nothing here depends on who responded.
    /// </summary>
    private async Task<List<(CohortRecord Record, bool Submitted)>> InviteesAsync(EngagementSurvey survey, CancellationToken ct)
    {
        var rows = await (
                from i in ScopedBypass.TenantWide(_db.EngagementSurveyInvitations, survey.TenantId, WholeSurveyRead).AsNoTracking()
                where i.SurveyId == survey.Id && i.Status != EngagementInvitationStatuses.Cancelled
                join e in ScopedBypass.TenantWideByConvention(_db.Employees, survey.TenantId, WholeSurveyRead).AsNoTracking() on i.EmployeeId equals e.Id into es
                from e in es.DefaultIfEmpty()
                select new
                {
                    i.CompanyId,
                    DepartmentId = e == null ? null : e.DepartmentId,
                    DesignationId = e == null ? null : e.DesignationId,
                    GradeId = e == null ? null : e.GradeId,
                    JoiningDate = e == null ? (DateTime?)null : e.JoiningDate,
                    Manager = e == null ? null : e.ManagerEmployeeId,
                    Submitted = i.Status == EngagementInvitationStatuses.Submitted,
                })
            .ToListAsync(ct);
        var gradeIds = rows.Where(r => r.GradeId != null).Select(r => r.GradeId!.Value).Distinct().ToList();
        var grades = await ScopedBypass.TenantWideByConvention(_db.Grades, survey.TenantId, WholeSurveyRead).AsNoTracking()
            .Where(g => gradeIds.Contains(g.Id)).Select(g => new { g.Id, g.Band, g.Code }).ToListAsync(ct);
        var bandOf = grades.ToDictionary(g => g.Id, g =>
        {
            var band = !string.IsNullOrWhiteSpace(g.Band) ? g.Band : g.Code;
            return band.Length > 60 ? band[..60] : band;
        });
        var today = DateOnly.FromDateTime(Now);
        return rows.Select(r => (new CohortRecord(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [EngagementDimensions.Company] = r.CompanyId?.ToString(),
            [EngagementDimensions.Department] = r.DepartmentId?.ToString(),
            [EngagementDimensions.Designation] = r.DesignationId?.ToString(),
            [EngagementDimensions.Grade] = r.GradeId is Guid g ? bandOf.GetValueOrDefault(g) : null,
            [EngagementDimensions.Tenure] = r.JoiningDate is DateTime j ? EngagementTenureBands.For(j, today) : null,
            [EngagementDimensions.Team] = r.Manager?.ToString(),
        }), r.Submitted)).ToList();
    }

    // ── Comments ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Free-text answers to one question, for the whole survey or ONE value of the survey's declared comment dimension
    /// — never under any other filter. Comments are LISTS, which a reader can intersect and subtract, so they are
    /// decided by a set-mode view family over the commenters of the whole survey with the comment dimension and the
    /// scope dimensions: every group any combination of visible comment lists could single out has 0 or ≥ k people.
    /// Shuffled, with no name, id, date or cohort.
    /// </summary>
    public async Task<(bool Visible, string? Reason, IReadOnlyList<string> Comments)> CommentsAsync(
        EngagementSurvey survey, EngagementResultsScope scope, IReadOnlyDictionary<string, string?> requestedFilters,
        string questionKey, CancellationToken ct)
    {
        if (!ResultsAvailable(survey)) throw new EngagementViewRefusedException(EngagementErrors.ResultsAfterClose, NotYetMessage(survey));
        var filters = RequestFilters(requestedFilters);
        if (filters.Count > 1 || (filters.Count == 1 && filters[0].Dimension != survey.CommentDimension))
            throw new EngagementViewRefusedException(EngagementErrors.CommentFilterNotAllowed,
                survey.CommentDimension == EngagementCommentDimensions.None
                    ? "Comments on this survey are shown for the whole survey only."
                    : $"Comments on this survey are shown for the whole survey or by {survey.CommentDimension} only.");
        var data = await LoadAsync(survey, scope, ct);
        var question = data.Content.Questions.FirstOrDefault(q => q.Key == questionKey && q.Type == EngagementQuestionTypes.FreeText);
        if (question is null) return (false, "not_a_text_question", []);

        var resolved = Resolve(data, filters, 0);
        if (!Respondents(data).Family.Evaluate(resolved, filters).IsVisible) return (false, SuppressionReasons.Suppressed, []);
        var dims = new List<string> { EngagementDimensions.Company };
        if (survey.CommentDimension != EngagementCommentDimensions.None) dims.Add(survey.CommentDimension);
        dims.Add(EngagementDimensions.Team);
        var commenters = new CohortPopulation(data.AnswersByQuestion[questionKey].Where(a => a.HasText && data.Records.ContainsKey(a.ResponseId))
            .Select(a => data.Records[a.ResponseId]));
        var family = CohortViewFamily.BuildFrozen(commenters, dims, data.FrozenK, data.CurrentK, setMode: true);
        SuppressionDecision decision;
        try { decision = family.Evaluate(resolved, filters); }
        catch (CohortViewNotPermittedException) { return (false, SuppressionReasons.Suppressed, []); }
        if (!decision.IsVisible) return (false, SuppressionReasons.Suppressed, []);
        var ids = MatchingIds(data, resolved, filters);
        var texts = await ScopedBypass.TenantWide(_db.EngagementSurveyAnswers, survey.TenantId, WholeSurveyRead)
            .AsNoTracking()
            .Where(a => a.SurveyId == survey.Id && a.QuestionKey == questionKey && a.TextValue != null && a.TextValue != "")
            .Select(a => new { a.ResponseId, a.TextValue })
            .ToListAsync(ct);
        var shown = texts.Where(t => ids.Contains(t.ResponseId)).Select(t => t.TextValue!).ToList();
        Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(shown));
        return (true, null, shown);
    }

    // ── Trend ────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<EngagementTrendPointDto>> TrendAsync(EngagementSurvey survey, EngagementResultsScope scope, CancellationToken ct)
    {
        var cycles = await _db.EngagementSurveys.AsNoTracking()
            .Where(s => s.TenantId == survey.TenantId && s.SeriesId == survey.SeriesId
                        && (s.Status == EngagementSurveyStatuses.Open || s.Status == EngagementSurveyStatuses.Closed))
            .OrderBy(s => s.CycleNumber).Take(24).ToListAsync(ct);
        var points = new List<EngagementTrendPointDto>();
        foreach (var cycle in cycles)
        {
            // An open Confidential/Anonymous cycle contributes its whole-survey response rate only.
            var r = await ComputeAsync(cycle, scope, new Dictionary<string, string?>(), null, ct);
            points.Add(new EngagementTrendPointDto(cycle.Id, cycle.CycleNumber, cycle.OpensAtUtc, cycle.Status, r.Visible,
                r.Respondents, r.Participation.ResponseRatePct, r.FavourablePct, r.Enps?.Score));
        }
        return points;
    }

    // ── Export / scheduled delivery ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The results as report sections (each array becomes a sheet / CSV block via ReportTabulator). The export endpoint
    /// and the scheduled report worker both serialise THIS object, so a file and the dashboard cannot disagree.
    /// Comments are included only when <paramref name="comments"/> is passed (the caller checked the permission).
    /// </summary>
    public static object ToReportSections(EngagementResultsDto r, IReadOnlyList<string>? comments = null)
    {
        if (!r.ResultsAvailable)
            return new { summary = new[] { new { metric = "Note", value = $"Results appear when the survey closes on {r.ResultsAvailableFromUtc:yyyy-MM-dd}." } } };
        string Cell(decimal? value, bool visible) => visible && value is not null ? value.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "Suppressed";
        var suppressed = string.Format("Suppressed: {0}", r.SuppressedMessage);
        var summary = new List<object>
        {
            new { metric = "Survey", value = r.TitleEn },
            new { metric = "Mode", value = r.Mode },
            new { metric = "Cycle", value = r.CycleNumber.ToString() },
            new { metric = "Minimum group size (k)", value = r.MinCohortSize.ToString() },
            new { metric = "Filters", value = r.Filters.Count == 0 ? "None" : string.Join("; ", r.Filters.Select(f => $"{f.Dimension} = {f.Label}")) },
            new { metric = "Invited", value = r.Participation.Visible ? r.Participation.Invited!.Value.ToString() : "Suppressed" },
            new { metric = "Submitted", value = r.Participation.Visible ? r.Participation.Submitted!.Value.ToString() : "Suppressed" },
            new { metric = "Response rate %", value = Cell(r.Participation.ResponseRatePct, r.Participation.Visible) },
            new { metric = "Respondents in view", value = r.Visible ? r.Respondents!.Value.ToString() : "Suppressed" },
            new { metric = "Favourable %", value = Cell(r.FavourablePct, r.Visible) },
            new { metric = "eNPS", value = Cell(r.Enps?.Score, r.Visible && r.Enps is not null) },
            new { metric = "Generated (UTC)", value = r.GeneratedAtUtc.ToString("yyyy-MM-dd HH:mm") },
        };
        if (!r.Visible) summary.Add(new { metric = "Note", value = suppressed });
        var items = r.Items.Select(i => new
        {
            question = i.TextEn,
            type = i.Type,
            driver = i.Driver ?? "",
            answered = i.Visible && i.Answered is not null ? i.Answered.Value.ToString() : "Suppressed",
            mean = Cell(i.Mean, i.Visible),
            favourablePct = Cell(i.FavourablePct, i.Visible),
            enps = Cell(i.Enps?.Score, i.Visible && i.Enps is not null),
            distribution = i.Visible ? string.Join("; ", i.Distribution.Select(d => $"{d.LabelEn}: {d.Count}")) : "Suppressed",
        }).ToList();
        var drivers = r.Drivers.Select(d => new
        {
            driver = d.LabelEn,
            respondents = d.Visible && d.Respondents is not null ? d.Respondents.Value.ToString() : "Suppressed",
            mean = Cell(d.Mean, d.Visible),
            favourablePct = Cell(d.FavourablePct, d.Visible),
        }).ToList();
        var breakdown = (r.Breakdown?.Cells ?? []).Select(c => new
        {
            dimension = r.Breakdown!.Dimension,
            group = c.Label,
            respondents = c.Visible && c.Respondents is not null ? c.Respondents.Value.ToString() : "Suppressed",
            favourablePct = Cell(c.FavourablePct, c.Visible),
            enps = Cell(c.Enps, c.Visible && c.Enps is not null),
        }).ToList();
        if (comments is null)
            return new { summary, items, drivers, breakdown };
        return new { summary, items, drivers, breakdown, comments = comments.Select(c => new { comment = c }).ToList() };
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly (string En, string Ar)[] LikertLabels =
    [
        ("Strongly disagree", "أعارض بشدة"), ("Disagree", "أعارض"), ("Neutral", "محايد"), ("Agree", "أوافق"), ("Strongly agree", "أوافق بشدة"),
    ];

    public static decimal Pct(int part, int whole) =>
        whole == 0 ? 0m : Math.Round(100m * part / whole, 1, MidpointRounding.AwayFromZero);

    private static decimal? Mean(IReadOnlyCollection<int> values) =>
        values.Count == 0 ? null : Math.Round((decimal)values.Sum() / values.Count, 2, MidpointRounding.AwayFromZero);

    public static EngagementEnpsDto? Enps(IReadOnlyCollection<int> values)
    {
        if (values.Count == 0) return null;
        var promoters = values.Count(v => v >= 9);
        var detractors = values.Count(v => v <= 6);
        var passives = values.Count - promoters - detractors;
        return new EngagementEnpsDto(
            Math.Round(100m * (promoters - detractors) / values.Count, 1, MidpointRounding.AwayFromZero),
            Pct(promoters, values.Count), Pct(passives, values.Count), Pct(detractors, values.Count), values.Count);
    }

    private sealed class OrgLabels
    {
        public Dictionary<string, string> Companies { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Departments { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Designations { get; } = new(StringComparer.Ordinal);

        public string Label(string dimension, string value)
        {
            if (value == CohortSuppression.Unknown) return "Not recorded";
            return dimension switch
            {
                EngagementDimensions.Company => Companies.GetValueOrDefault(value, value),
                EngagementDimensions.Department => Departments.GetValueOrDefault(value, value),
                EngagementDimensions.Designation => Designations.GetValueOrDefault(value, value),
                EngagementDimensions.Tenure => value switch
                {
                    EngagementTenureBands.Under1Year => "Under 1 year",
                    EngagementTenureBands.OneToThree => "1–3 years",
                    EngagementTenureBands.ThreeToFive => "3–5 years",
                    EngagementTenureBands.FivePlus => "5 years or more",
                    _ => value,
                },
                _ => value,
            };
        }
    }

    private async Task<OrgLabels> LabelsAsync(Guid tenantId, CancellationToken ct)
    {
        var labels = new OrgLabels();
        foreach (var c in await _db.Companies.AsNoTracking().Where(x => x.TenantId == tenantId).Select(x => new { x.Id, x.LegalNameEn }).ToListAsync(ct))
            labels.Companies[c.Id.ToString()] = c.LegalNameEn;
        foreach (var d in await _db.Departments.AsNoTracking().Where(x => x.TenantId == tenantId).Select(x => new { x.Id, x.NameEn }).ToListAsync(ct))
            labels.Departments[d.Id.ToString()] = d.NameEn;
        foreach (var d in await _db.Designations.AsNoTracking().Where(x => x.TenantId == tenantId).Select(x => new { x.Id, x.TitleEn }).ToListAsync(ct))
            labels.Designations[d.Id.ToString()] = d.TitleEn;
        return labels;
    }
}
