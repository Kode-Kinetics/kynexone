using Microsoft.EntityFrameworkCore;
using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Infrastructure.Data;

/// <summary>
/// Wave 0 — the APPROVED way to bypass an EF global query filter.
///
/// <para>WHY THIS EXISTS. A bare <c>.IgnoreQueryFilters()</c> removes BOTH the tenant filter and the
/// company filter, and nothing in the type system records which one the author meant to drop or what
/// replaces it. The repository has 210 such call sites; every one is a place where a reviewer must
/// reconstruct the author's intent from a comment. These helpers make the intent explicit, re-apply
/// the restriction that must survive, and give the architecture guard something to recognise.</para>
///
/// <para>CHOOSING A HELPER — the question is always "who is asking?":</para>
/// <list type="bullet">
///   <item><see cref="TenantWide{T}"/> — a user or service acting inside ONE tenant that legitimately
///     needs to see across legal entities (group-level finance, reconciliation, integrity reads).
///     Drops the company filter; the tenant is re-applied here so it cannot be forgotten.</item>
///   <item><see cref="ForCompanies{T}"/> — a USER-TRIGGERED path that spans entities but must still be
///     limited to the entities the caller may see. Tenant alone is NOT sufficient for a user path;
///     this is the only correct helper when a request principal is involved and the caller is not
///     group-level.</item>
///   <item><see cref="SystemWide{T}"/> — a background worker with NO request principal and NO ambient
///     tenant (lease reclaim, cross-tenant sweeps). Applies no restriction at all, so it is restricted
///     instead by construction: it demands a bounded batch size and refuses to be used for anything a
///     user can reach.</item>
/// </list>
///
/// <para>None of these weakens a control. Every one of them re-applies, in the predicate, whatever the
/// global filter would have applied — except the single dimension the caller has declared it must
/// cross, and stated why.</para>
/// </summary>
public static class ScopedBypass
{
    /// <summary>Tenant-pinned bypass for legacy entities whose TenantId is nullable.</summary>
    public static IQueryable<T> NullableTenantWide<T>(DbSet<T> set, Guid? tenantId, string justification)
        where T : class, INullableTenantOwned
    {
        RequireJustification(justification);
        // IgnoreQueryFilters is intentional: nullable legacy rows need an exact tenant-wide read;
        // the requested tenant (including the platform-default null tenant) is immediately re-applied.
        return set.IgnoreQueryFilters().Where(x => x.TenantId == tenantId);
    }

    /// <summary>
    /// Drops the COMPANY filter for a single tenant. The tenant predicate is applied here, so a
    /// caller cannot produce a cross-tenant query by forgetting a <c>Where</c>.
    /// </summary>
    /// <param name="justification">
    /// Why entity-level scoping must not apply. Required, and required to be non-trivial — an empty
    /// or placeholder reason throws, because an unexplained bypass is the thing this type exists to
    /// prevent.
    /// </param>
    public static IQueryable<T> TenantWide<T>(DbSet<T> set, Guid tenantId, string justification)
        where T : class, ITenantOwned
    {
        RequireJustification(justification);
        // IgnoreQueryFilters is intentional: this IS the sanctioned bypass. The company filter is
        // dropped; the tenant filter is immediately re-applied below so it cannot be forgotten.
        return set.IgnoreQueryFilters().Where(x => x.TenantId == tenantId);
    }

    /// <summary>
    /// Drops the COMPANY filter but re-applies the caller's authorised entity set. This is the
    /// correct helper for any path reachable by a request principal that is not group-level:
    /// tenant isolation alone would still let one entity's user read another entity's records.
    /// </summary>
    /// <param name="accessibleCompanyIds">
    /// The caller's authorised companies. An EMPTY set yields an empty result rather than an
    /// unrestricted one — failing closed, matching the tenant read filter's own convention.
    /// </param>
    /// <param name="includeUnattributed">
    /// Whether rows with a null CompanyId are visible. Defaults to <c>false</c>, matching
    /// <see cref="ICompanyScopedOperational"/>: on operational tables a null company is a backfill
    /// transient, not a "shared with everyone" state.
    /// </param>
    public static IQueryable<T> ForCompanies<T>(
        DbSet<T> set, Guid tenantId, IReadOnlyCollection<Guid> accessibleCompanyIds,
        string justification, bool includeUnattributed = false)
        where T : class, ITenantOwned, ICompanyScoped
    {
        RequireJustification(justification);
        var ids = accessibleCompanyIds as ICollection<Guid> ?? accessibleCompanyIds.ToList();
        // IgnoreQueryFilters is intentional: the sanctioned bypass. Both restrictions the global filters
        // would have applied are re-applied below — tenant, and the caller's authorised company set.
        return set.IgnoreQueryFilters()
            .Where(x => x.TenantId == tenantId)
            .Where(x => (includeUnattributed && x.CompanyId == null)
                     || (x.CompanyId != null && ids.Contains(x.CompanyId.Value)));
    }

    /// <summary>
    /// Drops EVERY filter, including the tenant one. Legitimate only for a background worker with no
    /// request principal — a lease reclaimer, a scheduled sweep — where there is no ambient tenant to
    /// scope to.
    ///
    /// <para>Because nothing restricts the rows, the restriction is structural: the caller must state a
    /// bounded batch size, which is enforced here. An unbounded cross-tenant query is never approved,
    /// as it turns one poisoned row into a whole-platform stall.</para>
    /// </summary>
    /// <param name="batchSize">Maximum rows this sweep may claim in one pass. Must be 1..1000.</param>
    /// <param name="predicate">Which rows the sweep is for. Taken as a parameter, NOT composed by the
    /// caller afterwards — see the ordering note below.</param>
    /// <param name="orderBy">
    /// Required. An unordered <c>Take</c> lets the database return an arbitrary window, so a sweep with
    /// a backlog larger than one batch can starve the oldest rows forever. Ordering makes the sweep
    /// deterministic and fair.
    /// </param>
    /// <remarks>
    /// WHY THE PREDICATE IS A PARAMETER. An earlier version of this helper returned
    /// <c>set.IgnoreQueryFilters().Take(batchSize)</c> and expected the caller to append its own
    /// <c>Where</c>. That composes in the WRONG ORDER: EF emits
    /// <c>SELECT * FROM (SELECT * FROM t LIMIT n) WHERE ...</c>, so an arbitrary n rows are chosen
    /// FIRST and filtered afterwards. With more than n rows in the table — i.e. immediately, in
    /// production — the sweep matches almost nothing and the bound silently becomes a correctness bug
    /// rather than a safety control. Taking the predicate here is what makes <c>Take</c> last.
    /// </remarks>
    public static IQueryable<T> SystemWide<T, TKey>(
        DbSet<T> set, int batchSize, string justification,
        System.Linq.Expressions.Expression<Func<T, bool>> predicate,
        System.Linq.Expressions.Expression<Func<T, TKey>> orderBy)
        where T : class
    {
        RequireJustification(justification);
        if (batchSize is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize,
                "A cross-tenant system sweep must be bounded (1..1000). An unbounded sweep lets one " +
                "tenant's backlog stall every other tenant's delivery.");
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(orderBy);
        // SYSTEM CONTEXT: tenant scope intentionally bypassed — worker-only sweep with no request
        // principal and no ambient tenant. Restriction is structural: filter, then order, then bound.
        return set.IgnoreQueryFilters().Where(predicate).OrderBy(orderBy).Take(batchSize);
    }

    private static void RequireJustification(string justification)
    {
        if (string.IsNullOrWhiteSpace(justification) || justification.Trim().Length < 20)
            throw new ArgumentException(
                "A query-filter bypass requires a specific justification (>= 20 chars) naming which " +
                "filter is dropped and what replaces it. See docs/QUERY_FILTER_BYPASS_REGISTER.md.",
                nameof(justification));
    }
}
