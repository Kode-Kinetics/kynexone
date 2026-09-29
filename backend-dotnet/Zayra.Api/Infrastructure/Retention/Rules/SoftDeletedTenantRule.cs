using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Retention.Rules;

/// <summary>
/// D3 — retention for a SOFT-DELETED TENANT, and the most dangerous rule in the product.
///
/// <para>THE PROBLEM IT ADDRESSES. <c>PlatformController.DeleteTenant</c> renames the slug to
/// <c>{slug}__deleted_{id8}</c>, sets <c>IsActive = false</c>, deactivates the users and revokes their
/// tokens. It deletes nothing. Every employee record inside the tenant survives, indefinitely, with no
/// purpose. Production currently holds 48 such tenants and 496 employee rows between them.</para>
///
/// <para>THE WINDOW. Ninety days, and not invented here: the published privacy policy already promises
/// "deleted accounts — anonymised within 90 days of account closure". The number is configurable
/// (<see cref="DataRetentionOptions.SoftDeletedTenantRetentionDays"/>) because it is a commercial and
/// legal parameter, not an engineering one, and a client with a different contractual commitment must be
/// able to set it without a deploy.</para>
///
/// <para><b>THE MISSING CLOCK, AND WHY THAT MAKES THIS RULE REFUSE.</b> A retention window needs a start
/// date and there was none: <c>tenants</c> had no deletion timestamp, and no <c>TenantDeleted</c> audit
/// row exists for any of the 48 — nothing anywhere records when they were deleted. A rule that guessed
/// (from <c>CreatedAtUtc</c>, say) would be deleting real data on the strength of a fabricated date.
/// This rule instead:</para>
/// <list type="number">
///   <item>reads the new, additive <c>Tenant.SoftDeletedAtUtc</c>;</item>
///   <item>when it is null, <b>retains</b> and says so — and, only when applying is enabled, stamps it
///     with NOW as the earliest PROVABLE deletion date. That can only ever push erasure later, never
///     sooner, so the first enabled sweep over a legacy tenant starts its clock instead of ending it and
///     the tenant survives at least a further full window;</item>
///   <item>requires the <c>__deleted_</c> slug marker as well as <c>IsActive == false</c>, because a
///     merely SUSPENDED tenant is also inactive and must never be caught by this. Production has exactly
///     one such tenant today, which is precisely why the marker is checked and not the flag.</item>
/// </list>
///
/// <para><b>THREE SWITCHES, ALL OFF BY DEFAULT.</b> Erasure needs
/// <see cref="DataRetentionOptions.ScheduleEnabled"/> (the sweep runs at all),
/// <see cref="DataRetentionOptions.ApplyDeletions"/> (the sweep may change anything) AND
/// <see cref="DataRetentionOptions.AllowTenantErasure"/>. A soft-deleted tenant is the largest blast
/// radius in the system; a human-confirmed path for it already exists
/// (<c>DELETE /api/platform/tenants/{id}/purge?confirm=PURGE</c>, Owner-only), so the automatic one has
/// to earn its keep against a control that already works.</para>
///
/// <para><b>WHAT IT ERASES, AND WHAT IT KEEPS.</b> The erasure set is derived from the EF MODEL — every
/// <c>ITenantOwned</c>/<c>INullableTenantOwned</c> type — rather than a hand-maintained list, so a table
/// added next quarter is covered without anybody remembering. Kept: the <c>Tenant</c> shell (stamped
/// <c>PurgedAtUtc</c> as a tombstone), <c>AuditLog</c> and <c>AdminAuditLog</c> (the legal record, and
/// append-only at the context level anyway), <c>RetentionPurgeAudit</c> (this run's own evidence — a
/// purge that erased the proof it happened would be unauditable by construction), and
/// <c>BackgroundJob</c>/<c>BackgroundJobItem</c> (the job row being erased is the one whose lease fences
/// this very write; deleting it mid-flight would destroy the transaction's own safety mechanism).</para>
///
/// <para><b>ALL-OR-NOTHING.</b> Unlike the manual purge, which reports "unresolved tables" and leaves a
/// partially erased tenant behind, an unresolved table here throws
/// <see cref="BackgroundJobPermanentFailureException"/>: the item's transaction rolls back, NOTHING is
/// deleted, and a human looks at it. A half-erased tenant is neither recoverable nor compliant.</para>
/// </summary>
public sealed class SoftDeletedTenantRule : IRetentionRule
{
    /// <summary>The slug marker <c>PlatformController.DeleteTenant</c> writes. Never match on IsActive alone.</summary>
    public const string DeletedSlugMarker = "__deleted_";

    /// <summary>
    /// Types the erasure never touches. Each one is here for a reason stated in the class remarks;
    /// removing an entry is a deliberate decision, not a cleanup.
    /// </summary>
    private static readonly HashSet<Type> Retained =
    [
        typeof(Tenant),
        typeof(AuditLog),
        typeof(AdminAuditLog),
        typeof(RetentionPurgeAudit),
        typeof(BackgroundJob),
        typeof(BackgroundJobItem),
    ];

    private readonly DataRetentionOptions _options;

    public SoftDeletedTenantRule(DataRetentionOptions options) => _options = options;

    public string RuleKey => RetentionRuleKeys.SoftDeletedTenantExpired;

    public async Task<IReadOnlyList<RetentionCandidate>> EvaluateAsync(
        JobExecutionContext ctx, DateTime nowUtc, int maxCandidates, CancellationToken ct)
    {
        // Tenant is the tenancy root, not a tenant-owned entity: it carries no global query filter, so
        // this is an ordinary read and needs no bypass. The job's own tenant IS the subject.
        var tenant = await ctx.Db.Tenants
            .Where(t => t.Id == ctx.TenantId)
            .Select(t => new { t.Id, t.Slug, t.IsActive, t.SoftDeletedAtUtc, t.PurgedAtUtc })
            .FirstOrDefaultAsync(ct);
        if (tenant is null) return [];
        if (tenant.PurgedAtUtc is not null) return [];
        if (tenant.IsActive || !tenant.Slug.Contains(DeletedSlugMarker, StringComparison.Ordinal)) return [];

        var key = ItemKey(tenant.Id);

        if (tenant.SoftDeletedAtUtc is null)
            return
            [
                new RetentionCandidate(
                    key, nameof(Tenant), tenant.Id.ToString(),
                    RetentionDispositions.Retain,
                    "RETAINED: this tenant is soft-deleted but no deletion date was ever recorded — "
                    + "PlatformController.DeleteTenant does not stamp one and no TenantDeleted audit row "
                    + "exists — so the retention window has no start and cannot be shown to have elapsed. "
                    + "Deleting on a guessed date is not a retention policy. When applying is enabled the "
                    + "sweep stamps today as the earliest PROVABLE deletion date, which starts the clock "
                    + $"and guarantees at least a further {_options.SoftDeletedTenantRetentionDays} days.",
                    null,
                    new { reason = "unknown-deletion-date", clockStartsOnFirstEnabledSweep = true }),
            ];

        var eligibleFrom = tenant.SoftDeletedAtUtc.Value.AddDays(_options.SoftDeletedTenantRetentionDays);
        if (eligibleFrom > nowUtc)
            return
            [
                new RetentionCandidate(
                    key, nameof(Tenant), tenant.Id.ToString(),
                    RetentionDispositions.Retain,
                    $"RETAINED: still recoverable. Soft-deleted {tenant.SoftDeletedAtUtc:yyyy-MM-dd}; the "
                    + $"{_options.SoftDeletedTenantRetentionDays}-day recovery window does not close until "
                    + $"{eligibleFrom:yyyy-MM-dd}. A tenant deleted by mistake must be restorable.",
                    eligibleFrom,
                    new { softDeletedAtUtc = tenant.SoftDeletedAtUtc, eligibleFromUtc = eligibleFrom }),
            ];

        if (!_options.AllowTenantErasure)
            return
            [
                new RetentionCandidate(
                    key, nameof(Tenant), tenant.Id.ToString(),
                    RetentionDispositions.Retain,
                    $"ELIGIBLE but RETAINED: the {_options.SoftDeletedTenantRetentionDays}-day recovery "
                    + $"window closed on {eligibleFrom:yyyy-MM-dd}, and DataRetention:AllowTenantErasure "
                    + "is off. Whole-tenant erasure is the largest irreversible action in the product and "
                    + "requires its own opt-in on top of DataRetention:ApplyDeletions.",
                    eligibleFrom,
                    new { softDeletedAtUtc = tenant.SoftDeletedAtUtc, eligibleFromUtc = eligibleFrom, blockedBy = "AllowTenantErasure" }),
            ];

        var (tables, rows) = await SurveyAsync(ctx, ct);
        return
        [
            new RetentionCandidate(
                key, nameof(Tenant), tenant.Id.ToString(),
                RetentionDispositions.HardDelete,
                $"Soft-deleted {tenant.SoftDeletedAtUtc:yyyy-MM-dd}; the "
                + $"{_options.SoftDeletedTenantRetentionDays}-day recovery window closed "
                + $"{eligibleFrom:yyyy-MM-dd}. Every tenant-owned row is erased except the tenant shell "
                + "and the audit trails, which are the legal record of the erasure itself.",
                eligibleFrom,
                new { softDeletedAtUtc = tenant.SoftDeletedAtUtc, eligibleFromUtc = eligibleFrom, tables, rows }),
        ];
    }

    public async Task<object?> ApplyAsync(
        JobExecutionContext ctx, RetentionCandidate candidate, DateTime nowUtc, CancellationToken ct)
    {
        var tenant = await ctx.Db.Tenants.FirstOrDefaultAsync(t => t.Id == ctx.TenantId, ct);
        if (tenant is null || tenant.PurgedAtUtc is not null) return new { alreadyPurged = true };

        // Re-assert every precondition inside the item's own transaction. Evaluation happened earlier and
        // outside it; a tenant restored in between must not be erased on the strength of a stale read.
        if (tenant.IsActive || !tenant.Slug.Contains(DeletedSlugMarker, StringComparison.Ordinal))
            throw new BackgroundJobPermanentFailureException(
                $"Tenant {tenant.Id} is no longer soft-deleted (it was restored between evaluation and "
                + "application). Refusing to erase it.");
        if (!_options.AllowTenantErasure)
            throw new BackgroundJobPermanentFailureException(
                "DataRetention:AllowTenantErasure was turned off between evaluation and application.");
        if (tenant.SoftDeletedAtUtc is not { } deletedAt
            || deletedAt.AddDays(_options.SoftDeletedTenantRetentionDays) > nowUtc)
            throw new BackgroundJobPermanentFailureException(
                $"Tenant {tenant.Id} is not past its recovery window at application time. Refusing to erase it.");

        var erased = await EraseAsync(ctx, ct);
        tenant.PurgedAtUtc = nowUtc;
        return new { erasedRowsByTable = erased, totalRows = erased.Values.Sum(), tenantShellRetained = true };
    }

    /// <summary>
    /// Counts what an erasure WOULD remove, without removing it. This is what makes the dry run worth
    /// reading: an operator sees the blast radius per table before anything is switched on.
    /// </summary>
    private static async Task<(int Tables, int Rows)> SurveyAsync(JobExecutionContext ctx, CancellationToken ct)
    {
        var total = 0;
        var tables = 0;
        foreach (var clr in ErasableTypes(ctx.Db))
        {
            var n = await CountForTenantAsync(ctx, clr, ct);
            if (n <= 0) continue;
            tables++;
            total += n;
        }
        return (tables, total);
    }

    /// <summary>
    /// Deletes every erasable table's rows for this tenant, all inside the item transaction the runner
    /// opened. SAVEPOINTS, not try/catch alone: in PostgreSQL any failed statement aborts the whole
    /// transaction, so a foreign-key ordering failure on pass 1 would poison the transaction rather than
    /// letting pass 2 retry. Rolling back to a savepoint keeps the outer transaction usable.
    /// </summary>
    private static async Task<Dictionary<string, int>> EraseAsync(JobExecutionContext ctx, CancellationToken ct)
    {
        var erased = new Dictionary<string, int>(StringComparer.Ordinal);
        var pending = ErasableTypes(ctx.Db).ToList();
        var transaction = ctx.Db.Database.CurrentTransaction
            ?? throw new InvalidOperationException(
                "The tenant erasure must run inside the job runner's per-item transaction; none is open.");

        var failures = new List<string>();
        // Two passes: a dependent that blocks its parent on pass 1 is usually gone by pass 2. The manual
        // purge uses the same shape; the difference is what happens when pass 2 still fails.
        for (var pass = 0; pass < 2 && pending.Count > 0; pass++)
        {
            failures.Clear();
            var stillPending = new List<Type>();
            foreach (var clr in pending)
            {
                var savepoint = $"ret_{Guid.NewGuid():N}"[..24];
                await transaction.CreateSavepointAsync(savepoint, ct);
                try
                {
                    var n = await DeleteForTenantAsync(ctx, clr, ct);
                    await transaction.ReleaseSavepointAsync(savepoint, ct);
                    if (n > 0) erased[clr.Name] = erased.GetValueOrDefault(clr.Name) + n;
                }
                catch (Exception ex)
                {
                    await transaction.RollbackToSavepointAsync(savepoint, ct);
                    stillPending.Add(clr);
                    failures.Add($"{clr.Name}: {ex.GetBaseException().Message}");
                }
            }
            pending = stillPending;
        }

        if (failures.Count > 0)
            // All-or-nothing. Throwing rolls the item back, so a tenant is never left half-erased: the
            // personal data either all goes or all stays, and a human is told which tables blocked it.
            throw new BackgroundJobPermanentFailureException(
                $"Tenant erasure left {failures.Count} table(s) unresolved after two passes; the whole "
                + "erasure was rolled back rather than leaving the tenant partially erased. Unresolved: "
                + JsonSerializer.Serialize(failures.Take(10)));

        return erased;
    }

    /// <summary>
    /// The erasure set, derived from the EF model rather than a hand-maintained list — a table added
    /// later is covered without anybody remembering to add it here.
    /// </summary>
    internal static IEnumerable<Type> ErasableTypes(Zayra.Api.Data.ZayraDbContext db) =>
        db.Model.GetEntityTypes()
            .Where(t => !t.IsOwned() && t.BaseType is null)
            .Select(t => t.ClrType)
            .Where(clr => !Retained.Contains(clr)
                          && (typeof(ITenantOwned).IsAssignableFrom(clr)
                              || typeof(INullableTenantOwned).IsAssignableFrom(clr)))
            .Distinct();

    private static readonly System.Reflection.MethodInfo DeleteMethod =
        typeof(SoftDeletedTenantRule).GetMethod(nameof(DeleteForTenantCoreAsync),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

    private static readonly System.Reflection.MethodInfo CountMethod =
        typeof(SoftDeletedTenantRule).GetMethod(nameof(CountForTenantCoreAsync),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

    private static Task<int> DeleteForTenantAsync(JobExecutionContext ctx, Type clr, CancellationToken ct) =>
        (Task<int>)DeleteMethod.MakeGenericMethod(clr).Invoke(null, [ctx, ct])!;

    private static Task<int> CountForTenantAsync(JobExecutionContext ctx, Type clr, CancellationToken ct) =>
        (Task<int>)CountMethod.MakeGenericMethod(clr).Invoke(null, [ctx, ct])!;

    private static Task<int> DeleteForTenantCoreAsync<TEntity>(JobExecutionContext ctx, CancellationToken ct)
        where TEntity : class =>
        TenantRows<TEntity>(ctx).ExecuteDeleteAsync(ct);

    private static Task<int> CountForTenantCoreAsync<TEntity>(JobExecutionContext ctx, CancellationToken ct)
        where TEntity : class =>
        TenantRows<TEntity>(ctx).CountAsync(ct);

    private const string ErasureScope =
        "Whole-tenant retention erasure sweeps every tenant-owned table by open generic, so the company " +
        "and soft-delete filters must drop (an erasure has to reach soft-deleted and unattributed rows); " +
        "the tenant is re-applied inside ScopedBypass from the claimed job row.";

    private static IQueryable<TEntity> TenantRows<TEntity>(JobExecutionContext ctx) where TEntity : class =>
        ScopedBypass.TenantWideByConvention(ctx.Db.Set<TEntity>(), ctx.TenantId, ErasureScope);

    public static string ItemKey(Guid tenantId) => $"{RetentionRuleKeys.SoftDeletedTenantExpired}:tenant:{tenantId}";

    /// <summary>
    /// Records TODAY as the earliest provable soft-deletion date for a legacy tenant that has none.
    /// Called only when applying is enabled, and only for the unknown-date case: it starts the recovery
    /// clock and can therefore only ever DELAY erasure, never bring it forward.
    /// </summary>
    public static async Task<bool> StampUnknownDeletionDateAsync(JobExecutionContext ctx, DateTime nowUtc, CancellationToken ct)
    {
        var tenant = await ctx.Db.Tenants.FirstOrDefaultAsync(t => t.Id == ctx.TenantId, ct);
        if (tenant is null || tenant.SoftDeletedAtUtc is not null) return false;
        if (tenant.IsActive || !tenant.Slug.Contains(DeletedSlugMarker, StringComparison.Ordinal)) return false;
        tenant.SoftDeletedAtUtc = nowUtc;
        return true;
    }
}
