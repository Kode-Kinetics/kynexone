using Zayra.Api.Infrastructure.Jobs;

namespace Zayra.Api.Infrastructure.Retention;

/// <summary>Stable rule identifiers. Persisted on every audit row — never rename one.</summary>
public static class RetentionRuleKeys
{
    /// <summary>A soft-deleted employee whose <c>RetentionUntilUtc</c> has elapsed.</summary>
    public const string EmployeeRetentionExpired = "employee.retention-expired";

    /// <summary>A refresh token whose expiry is far enough past that it can no longer be replayed.</summary>
    public const string RefreshTokenExpired = "auth.refresh-token-expired";

    /// <summary>A soft-deleted tenant held beyond the configured recovery window.</summary>
    public const string SoftDeletedTenantExpired = "tenant.soft-deleted-expired";

    public static readonly IReadOnlyList<string> All =
        [EmployeeRetentionExpired, RefreshTokenExpired, SoftDeletedTenantExpired];
}

/// <summary>
/// What a rule decides SHOULD happen to a record at expiry. These are not interchangeable and the choice
/// is per entity, not per product: erasing a payroll record to satisfy a data-subject request breaks a
/// statutory obligation that outlives the request.
/// </summary>
public static class RetentionDispositions
{
    /// <summary>Clear the identifying columns in place, keep the row and its relationships.</summary>
    public const string Anonymise = "Anonymise";

    /// <summary>Remove the row entirely. Only for records nothing else references and no statute covers.</summary>
    public const string HardDelete = "HardDelete";

    /// <summary>Keep the record and say why. The default whenever the rule is not certain.</summary>
    public const string Retain = "Retain";
}

/// <summary>What actually happened, as opposed to what the rule decided.</summary>
public static class RetentionOutcomes
{
    /// <summary>Dry run: the decision was recorded and the subject record was not touched.</summary>
    public const string Reported = "Reported";

    /// <summary>The disposition was carried out.</summary>
    public const string Applied = "Applied";

    /// <summary>The rule refused to act — statutory hold, missing evidence, or a per-rule switch.</summary>
    public const string Retained = "Retained";
}

/// <summary>
/// One record a rule has an opinion about, together with that opinion. Produced by
/// <see cref="IRetentionRule.EvaluateAsync"/> in a read-only pass, then either reported or applied.
///
/// <para><paramref name="ItemKey"/> is the job checkpoint key and must be DETERMINISTIC across attempts
/// (<c>employee:1042</c>, never a counter) — the queue's exactly-once guarantee rests on it.</para>
/// </summary>
/// <param name="Disposition">One of <see cref="RetentionDispositions"/>.</param>
/// <param name="Reason">Why, in words an assessor can read. Required.</param>
/// <param name="RetentionUntilUtc">The deadline that made this a candidate, where the rule has one.</param>
/// <param name="Details">Rule-specific detail, serialised onto the audit row.</param>
public sealed record RetentionCandidate(
    string ItemKey,
    string EntityName,
    string EntityId,
    string Disposition,
    string Reason,
    DateTime? RetentionUntilUtc = null,
    object? Details = null);

/// <summary>
/// D3 — one retention policy, expressed as code.
///
/// <para>THE SHAPE, AND WHY IT IS TWO METHODS. <see cref="EvaluateAsync"/> is READ-ONLY and must stay
/// that way: it is what a dry run calls, and a dry run that writes to the subject is not a dry run. Only
/// <see cref="ApplyAsync"/> mutates, it is only ever called when the operator has opted in, and it runs
/// inside the job's per-item transaction so its effects commit with the checkpoint or not at all.</para>
///
/// <para>A rule may return a candidate whose disposition is <see cref="RetentionDispositions.Retain"/>.
/// That is not a no-op: it is the rule saying "this record's clock expired and I am keeping it anyway,
/// here is the statutory basis", and it produces an audit row exactly like a deletion does. Silence
/// about a retained record is the failure mode this interface is shaped to prevent.</para>
/// </summary>
public interface IRetentionRule
{
    /// <summary>One of <see cref="RetentionRuleKeys"/>.</summary>
    string RuleKey { get; }

    /// <summary>
    /// READ-ONLY. Finds records of this tenant whose retention clock has run out and decides what should
    /// happen to each. Must not write. Must be bounded by <paramref name="maxCandidates"/> and ordered,
    /// so a large backlog is drained fairly over successive runs rather than starving its oldest rows.
    /// </summary>
    Task<IReadOnlyList<RetentionCandidate>> EvaluateAsync(
        JobExecutionContext ctx, DateTime nowUtc, int maxCandidates, CancellationToken ct);

    /// <summary>
    /// Carries out one candidate's disposition. Called ONLY for candidates whose disposition is not
    /// <see cref="RetentionDispositions.Retain"/>, ONLY when deletion is enabled, and ONLY from inside
    /// <see cref="JobExecutionContext.RunItemAsync"/> — so it must stage its writes on
    /// <paramref name="ctx"/>'s <c>Db</c>, open no transaction of its own, and be safe to re-run from
    /// scratch after a transient fault (the execution strategy replays the whole delegate).
    /// </summary>
    /// <returns>Detail to merge onto the audit row, e.g. which columns were cleared.</returns>
    Task<object?> ApplyAsync(JobExecutionContext ctx, RetentionCandidate candidate, DateTime nowUtc, CancellationToken ct);
}
