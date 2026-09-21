using System.Text.Json;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Infrastructure.Retention.Rules;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Retention;

/// <summary>Payload of one tenant's retention sweep. <paramref name="AsOfUtc"/> makes a run reproducible.</summary>
/// <param name="AsOfUtc">The instant every rule evaluates against. Fixed at ENQUEUE, not read from the
/// clock inside the job, so a retry hours later reaches the same verdict as the first attempt — a
/// deletion decision must not depend on when a worker happened to pick the work up.</param>
public sealed record DataRetentionSweepPayload(DateTime AsOfUtc);

/// <summary>
/// D3 — the sweep that finally enforces the retention promise, for ONE tenant.
///
/// <para>SHAPE. One durable job per tenant per day, on the existing F3 queue: claimed with
/// <c>FOR UPDATE SKIP LOCKED</c>, held by a fenced lease, checkpointed per item. Nothing about
/// scheduling is invented here — a retention sweep is just another job type.</para>
///
/// <para><b>THE HONESTY CONTRACT.</b> Every rule's <c>EvaluateAsync</c> is read-only and runs on every
/// run. What differs is what happens next:</para>
/// <list type="bullet">
///   <item><b>Dry run</b> (the default, <c>DataRetention:ApplyDeletions=false</c>): each candidate gets a
///     <c>RetentionPurgeAudit</c> row with <c>DryRun=true</c>, <c>Outcome=Reported</c>, and the subject
///     record is not touched. The report is the deliverable; the rows are still there afterwards.</item>
///   <item><b>Enabled</b>: candidates whose disposition is Anonymise or HardDelete are applied inside the
///     item transaction, so the change and its audit row commit together or not at all. A candidate
///     whose disposition is Retain is NEVER applied, whatever the switches say — the statutory refusal
///     is not something a configuration flag can override.</item>
/// </list>
///
/// <para><b>IDEMPOTENCE UNDER RETRY.</b> <c>NpgsqlRetryingExecutionStrategy</c> re-runs an item's whole
/// delegate after a transient fault, and the queue re-runs an item after a crash. Both are safe here
/// because (a) the per-item checkpoint and the effects commit in one transaction, (b) each rule's
/// <c>ApplyAsync</c> re-reads its subject inside that transaction and no-ops if the work is already
/// done, and (c) the audit row is written inside the same item, so a retried item cannot leave a
/// deletion without its evidence or evidence without its deletion.</para>
///
/// <para><b>WHY EVERY CANDIDATE GETS AN AUDIT ROW, INCLUDING THE RETAINED ONES.</b> "What did you delete"
/// is the easy question. The one an assessment actually asks is "what did you decide about this person,
/// when, and on what basis" — and the most important answers are the refusals. A purge that logged only
/// its deletions would be silent about exactly the cases where the reasoning matters.</para>
/// </summary>
public sealed class DataRetentionSweepJobHandler : IBackgroundJobHandler
{
    public const string JobType = "retention.sweep";

    public static readonly BackgroundJobTypeDescriptor Descriptor = new(
        JobType,
        typeof(DataRetentionSweepJobHandler),
        // Reading a retention run means reading who was erased and why: the audit permission, not an HR one.
        ViewPermissions: ["audit.read"],
        CancelPermissions: ["audit.read"],
        // WhileActive, not Forever: a sweep must be able to run again tomorrow. Same-day duplicates are
        // prevented by the idempotency key carrying the date, not by permanent key retention.
        KeyRetention: BackgroundJobKeyRetention.WhileActive,
        MaxAttempts: 3);

    private readonly DataRetentionOptions _options;
    private readonly IReadOnlyList<IRetentionRule> _rules;
    private readonly ILogger<DataRetentionSweepJobHandler> _log;

    public DataRetentionSweepJobHandler(
        DataRetentionOptions options, IEnumerable<IRetentionRule> rules, ILogger<DataRetentionSweepJobHandler> log)
    {
        _options = options;
        _rules = rules.OrderBy(r => r.RuleKey, StringComparer.Ordinal).ToList();
        _log = log;
    }

    /// <summary>One sweep per tenant per calendar day. The date is IN the key, so yesterday's run does
    /// not suppress today's and a double-enqueue on the same day is deduplicated by the store.</summary>
    public static string DefaultIdempotencyKey(DateTime asOfUtc) => $"{JobType}:{asOfUtc:yyyy-MM-dd}";

    public async Task ExecuteAsync(JobExecutionContext ctx)
    {
        var payload = ctx.GetPayload<DataRetentionSweepPayload>();
        var asOf = DateTime.SpecifyKind(payload.AsOfUtc, DateTimeKind.Utc);
        var ct = ctx.AbortToken;
        var dryRun = !_options.ApplyDeletions;

        // Evaluation is read-only and re-runs on every attempt; that is allowed (and required) by the
        // handler contract, because only RunItemAsync makes durable progress.
        var planned = new List<(IRetentionRule Rule, RetentionCandidate Candidate)>();
        foreach (var rule in _rules)
        {
            var found = await rule.EvaluateAsync(ctx, asOf, _options.MaxCandidatesPerRule, ct);
            foreach (var candidate in found) planned.Add((rule, candidate));
        }

        await ctx.SetTotalAsync(planned.Count,
            planned.Count == 0
                ? $"No records past their retention deadline as of {asOf:yyyy-MM-dd}."
                : $"{planned.Count} record(s) past their retention deadline as of {asOf:yyyy-MM-dd}"
                  + (dryRun ? " — DRY RUN, nothing will be changed." : " — deletions ENABLED."));

        var applied = 0;
        var retained = 0;
        var reported = 0;

        foreach (var (rule, candidate) in planned)
        {
            if (ctx.IsItemCompleted(candidate.ItemKey)) continue;

            var willApply = !dryRun && candidate.Disposition != RetentionDispositions.Retain;
            object? details = candidate.Details;
            string outcome;

            if (candidate.Disposition == RetentionDispositions.Retain)
            {
                outcome = RetentionOutcomes.Retained;
                retained++;
            }
            else if (willApply)
            {
                outcome = RetentionOutcomes.Applied;
                applied++;
            }
            else
            {
                outcome = RetentionOutcomes.Reported;
                reported++;
            }

            await ctx.RunItemAsync(candidate.ItemKey, async itemCt =>
            {
                // Order matters and is deliberate: the effect first, then the evidence, both staged on
                // the same context and committed by the runner in one transaction with the checkpoint.
                // There is no ordering in which one can land without the other.
                if (willApply)
                    details = await rule.ApplyAsync(ctx, candidate, asOf, itemCt) ?? candidate.Details;
                else if (!dryRun
                         && candidate.Disposition == RetentionDispositions.Retain
                         && rule is SoftDeletedTenantRule)
                    // The single bookkeeping write on a non-applied path: start a legacy soft-deleted
                    // tenant's missing clock. It can only ever DELAY erasure, and it never happens in a
                    // dry run.
                    await SoftDeletedTenantRule.StampUnknownDeletionDateAsync(ctx, asOf, itemCt);

                ctx.Db.RetentionPurgeAudits.Add(new RetentionPurgeAudit
                {
                    TenantId = ctx.TenantId,
                    JobId = ctx.JobId,
                    RuleKey = rule.RuleKey,
                    EntityName = candidate.EntityName,
                    EntityId = Truncate(candidate.EntityId, 100),
                    Disposition = candidate.Disposition,
                    Outcome = outcome,
                    DryRun = dryRun,
                    Reason = Truncate(candidate.Reason, 2000),
                    RetentionUntilUtc = candidate.RetentionUntilUtc,
                    DetailsJson = details is null ? null : JsonSerializer.Serialize(details),
                    CreatedAtUtc = asOf,
                });
            }, () => new { candidate.Disposition, outcome });
        }

        _log.LogInformation(
            "Retention sweep for tenant {TenantId} as of {AsOf:yyyy-MM-dd}: {Planned} candidate(s), "
            + "{Applied} applied, {Reported} reported (dry run), {Retained} retained. DryRun={DryRun}.",
            ctx.TenantId, asOf, planned.Count, applied, reported, retained, dryRun);

        ctx.SetResult(new
        {
            asOfUtc = asOf,
            dryRun,
            tenantErasureAllowed = _options.AllowTenantErasure,
            candidates = planned.Count,
            applied,
            reported,
            retained,
            byRule = planned.GroupBy(x => x.Rule.RuleKey)
                .ToDictionary(g => g.Key, g => new
                {
                    total = g.Count(),
                    anonymise = g.Count(x => x.Candidate.Disposition == RetentionDispositions.Anonymise),
                    hardDelete = g.Count(x => x.Candidate.Disposition == RetentionDispositions.HardDelete),
                    retain = g.Count(x => x.Candidate.Disposition == RetentionDispositions.Retain),
                }),
        });
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
