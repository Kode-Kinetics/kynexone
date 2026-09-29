using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

/// <summary>
/// D3 — the evidence row for ONE retention decision about ONE record.
///
/// <para>WHY A DEDICATED TABLE AND NOT <c>audit_logs</c>. Two reasons, both structural. First,
/// <c>audit_logs</c> is a per-tenant SHA-256 hash chain written on the request path: every insert reads
/// the tenant's current chain tail, so a background sweep writing one row per candidate would serialise
/// against live user traffic and, on a retry, risk a chain fork. Second, a retention assessment has to
/// be QUERYABLE by rule, disposition and outcome ("show me everything we retained under statute last
/// quarter, and why"); that is a table with columns, not a JSON blob in a generic metadata field.</para>
///
/// <para>WHAT IT RECORDS. One row per candidate per run — <b>including the ones that were NOT purged</b>.
/// A retention mechanism that only records deletions cannot answer the question an assessor actually
/// asks, which is "what did you decide about this person and on what basis". A dry run writes rows with
/// <see cref="DryRun"/> = true and <see cref="Outcome"/> = <c>Reported</c> and changes nothing else; that
/// is the whole point of the default configuration.</para>
///
/// <para>APPEND-ONLY BY CONVENTION, not by trigger: nothing in the product updates or deletes these rows.
/// They are deliberately NOT hash-chained — see the first paragraph — so they are evidence of a decision,
/// not a tamper-evident ledger. If an assessment later requires tamper evidence, that is a schema change
/// with a migration, not a behaviour change here.</para>
/// </summary>
public sealed class RetentionPurgeAudit : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The tenant whose data the decision was about. For a whole-tenant decision, that tenant.</summary>
    public Guid TenantId { get; set; }

    /// <summary>The sweep job that made the decision. Null only for a decision made outside the job queue.</summary>
    public Guid? JobId { get; set; }

    /// <summary>Which rule decided. See <see cref="Zayra.Api.Infrastructure.Retention.RetentionRuleKeys"/>.</summary>
    public string RuleKey { get; set; } = string.Empty;

    /// <summary>CLR entity name the decision was about, e.g. <c>Employee</c>, <c>RefreshToken</c>, <c>Tenant</c>.</summary>
    public string EntityName { get; set; } = string.Empty;

    /// <summary>Primary key of the subject record, as text (the keys are int, Guid and composite).</summary>
    public string EntityId { get; set; } = string.Empty;

    /// <summary>
    /// <see cref="Zayra.Api.Infrastructure.Retention.RetentionDispositions"/> — what the rule decided
    /// SHOULD happen: Anonymise, HardDelete or Retain. Distinct from <see cref="Outcome"/>, which is what
    /// actually happened; in a dry run they deliberately disagree.
    /// </summary>
    public string Disposition { get; set; } = string.Empty;

    /// <summary>
    /// <see cref="Zayra.Api.Infrastructure.Retention.RetentionOutcomes"/> — Reported (dry run: nothing was
    /// written to the subject), Applied (the disposition was carried out) or Retained (the rule refused).
    /// </summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>True when the run could not change anything by configuration. The honesty flag.</summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// Why, in words an assessor can read. For a retained record this is the statutory basis; for an
    /// applied one it is which deadline elapsed. Never empty.
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>The deadline that made the record a candidate, where the rule has one.</summary>
    public DateTime? RetentionUntilUtc { get; set; }

    /// <summary>Rule-specific detail: which columns were cleared, how many rows a sweep touched.</summary>
    public string? DetailsJson { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
