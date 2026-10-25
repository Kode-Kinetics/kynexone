using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;

namespace Zayra.Api.Infrastructure.Finance;

/// <summary>
/// Cost-accounting door guards shared by every writer that can change what a pending payroll run will post
/// (AM-3, AM-9): the M-7 refusal, the M-10 "reason required" refusal, and the per-company advisory lock.
/// <para>WP-A (master-data doors), WP-C (Approve, Lock) and this package's own writers (mappings, account rules, the
/// switch, default cost centre, K) all call these, so the rule is written once.</para>
/// <para>The check "no run awaits finance review or lock" followed by a write is advisory on its own (check-then-act).
/// The control that cannot be raced is the approval hash re-checked at Lock; the lock taken by
/// <see cref="SerializeAsync{T}"/> narrows the window to nothing for every writer that uses it, and Approve / Lock must
/// take the same lock (<see cref="AcquireCompanyLockAsync"/>).</para>
/// </summary>
public static class CostAccountingGuards
{
    /// <summary>The advisory-lock scope string. Approve, Lock and every attribution writer use this same scope.</summary>
    public const string LockScope = "cost-accounting";
    public const string LockedIntoApprovedRunCode = "locked_into_approved_run";
    public const string ReasonRequiredCode = "cost_centre_reason_required";

    /// <summary>Run statuses that freeze cost attribution: level-1 approved and finally approved, not yet locked (AM-9).</summary>
    public static readonly IReadOnlyList<string> AwaitingStatuses = new[] { "PendingFinanceReview", "Approved" };

    /// <summary>True when a run of the company (or, with a null company, of ANY company of the tenant) is
    /// PendingFinanceReview or Approved-not-Locked.</summary>
    public static Task<bool> HasRunAwaitingFinanceOrLockAsync(ZayraDbContext db, Guid tenantId, Guid? companyId, CancellationToken ct)
    {
        var awaiting = AwaitingStatuses.ToArray();
        return ScopedBypass.TenantWide(db.PayrollRuns, tenantId,
                "The M-7 door guard asks whether ANY run of the company (or tenant) awaits finance review or lock, whatever the caller's company claims.")
            .AsNoTracking()
            .AnyAsync(r => (companyId == null || r.CompanyId == companyId) && awaiting.Contains(r.Status), ct);
    }

    public static object Error(string code, string message, string messageAr) => new { error = code, code, message, messageAr };

    /// <summary>M-7, corrected per AM-9: it names the real unlock paths (Lock, or Reopen) and never the run, period or amounts (S-042).</summary>
    public static object LockedIntoApprovedRunBody { get; } = Error(LockedIntoApprovedRunCode,
        "A payroll run for this company is awaiting finance review or lock, and this change would alter what it posts. Lock the run, or reopen it first.",
        "هناك مسيرة رواتب لهذه الشركة بانتظار المراجعة المالية أو القفل، وهذا التغيير سيغيّر ما ستقوم بترحيله. اقفل المسيرة أو أعد فتحها أولًا.");

    /// <summary>M-10.</summary>
    public static object ReasonRequiredBody { get; } = Error(ReasonRequiredCode, "Enter a reason for this change.", "أدخل سببًا لهذا التغيير.");

    public static bool HasReason(string? reason) => !string.IsNullOrWhiteSpace(reason);

    /// <summary>Takes the transaction-scoped advisory lock for (tenant, company). Must run inside a transaction.</summary>
    public static Task AcquireCompanyLockAsync(ZayraDbContext db, Guid tenantId, Guid companyId, CancellationToken ct)
        => FinanceDecisionSerializer.AcquireAsync(db, LockScope, tenantId, companyId, ct);

    /// <summary>
    /// Runs <paramref name="body"/> as the sole cost-attribution writer of the company (<c>pg_advisory_xact_lock</c> in a
    /// transaction). A tenant-default write (null company) takes the tenant key and then every company key in ascending
    /// key order, so it cannot deadlock against a company-level writer, which only ever takes one key.
    /// The body must (re)load everything it reads: the change tracker is cleared on entry.
    /// </summary>
    public static Task<T> SerializeAsync<T>(ZayraDbContext db, Guid tenantId, Guid? companyId, Func<Task<T>> body, CancellationToken ct)
    {
        if (companyId is { } c)
            return FinanceDecisionSerializer.SerializeAsync(db, LockScope, tenantId, c, body, ct);

        return FinanceDecisionSerializer.SerializeAsync(db, LockScope, tenantId, Guid.Empty, async () =>
        {
            if (db.Database.IsNpgsql())
            {
                var ids = await ScopedBypass.TenantWide(db.Companies, tenantId, "A tenant-default cost-accounting write serialises against every company's writers.")
                    .AsNoTracking().Select(x => x.Id).ToListAsync(ct);
                foreach (var id in ids.OrderBy(i => FinanceDecisionSerializer.ComputeLockKey(LockScope, tenantId, i)))
                    await AcquireCompanyLockAsync(db, tenantId, id, ct);
            }
            return await body();
        }, ct);
    }
}

/// <summary>
/// Identity segregation of duties for cost accounting (AM-14). A user who changed a posting input of a company since the
/// previous Lock — a mapping, an account rule, the functional class of a cost centre, the switch, the default cost centre,
/// an employee override or K — cannot Lock, or create a journal export for, that company's run, and neither can an ERP API
/// key that user created. The check is by IDENTITY, so an Admin holding every permission is refused like anyone else.
/// <para>It only applies while the company's cost-accounting switch is ON, so tenants that never use the feature keep
/// today's behaviour exactly. Tenant-default changes (null company) count for every company of the tenant.</para>
/// <para>WP-C calls <see cref="HasConflictAsync"/> from Lock; WP-D calls it (and <see cref="ApiKeyHasConflictAsync"/>) from
/// export create, and answers 403 with <see cref="RefusalBody"/>.</para>
/// </summary>
public static class CostAccountingSod
{
    public const string ErrorCode = "separation_of_duties";

    /// <summary>The audit actions that record a change to a posting input. WP-A's master-data writers must emit the last two
    /// verbatim (or add theirs here) for the check to see them.</summary>
    public static readonly IReadOnlyCollection<string> RelevantActions = new[]
    {
        "finance.gl.mapping.saved",
        "finance.gl.account.rule_changed",
        "finance.gl.account.rule_preset_applied",
        "finance.gl.cost_accounting.switch_changed",
        "finance.gl.cost_accounting.default_cost_centre_changed",
        "finance.gl.cost_accounting.small_cell_changed",
        "organization.costcenter.class_changed",
        "employee.cost_centre_override.changed",
    };

    public static object RefusalBody { get; } = CostAccountingGuards.Error(ErrorCode,
        "You changed cost-accounting settings for this company since the last lock, so someone else must lock or export this run.",
        "لقد غيّرت إعدادات محاسبة التكلفة لهذه الشركة منذ آخر قفل، لذا يجب أن يقوم مستخدم آخر بقفل هذه المسيرة أو تصديرها.");

    public static async Task<bool> HasConflictAsync(ZayraDbContext db, Guid tenantId, Guid companyId, string period, Guid userId, CancellationToken ct)
    {
        var enabled = await ScopedBypass.TenantWide(db.Companies, tenantId, "SoD reads the company's costing switch regardless of the caller's company claims.")
            .AsNoTracking().Where(c => c.Id == companyId).Select(c => (bool?)c.CostAccountingEnabled).FirstOrDefaultAsync(ct);
        if (enabled != true) return false;

        var since = await SinceAsync(db, tenantId, companyId, period, ct);
        var actions = RelevantActions.ToArray();
        return await ScopedBypass.NullableTenantWide(db.AuditLogs, tenantId, "SoD scans the tenant's audit trail for the user's own cost-accounting changes.")
            .AsNoTracking()
            .AnyAsync(a => a.UserId == userId && actions.Contains(a.Action) && (a.CompanyId == companyId || a.CompanyId == null)
                           && (since == null || a.CreatedAtUtc >= since), ct);
    }

    /// <summary>True when the key's creator has a conflict. An unknown key fails closed.</summary>
    public static async Task<bool> ApiKeyHasConflictAsync(ZayraDbContext db, Guid tenantId, Guid companyId, string period, Guid apiKeyId, CancellationToken ct)
    {
        var creator = await ScopedBypass.TenantWide(db.ErpApiKeys, tenantId, "SoD resolves the creator of the ERP key acting on the company.")
            .AsNoTracking().Where(k => k.Id == apiKeyId).Select(k => (Guid?)k.CreatedByUserId).FirstOrDefaultAsync(ct);
        if (creator is not { } userId) return true;
        return await HasConflictAsync(db, tenantId, companyId, period, userId, ct);
    }

    /// <summary>Start of the review window: the latest Lock of an EARLIER period of the company, else the beginning of time.
    /// Changes before it were already in front of a locker; changes after it have not been.</summary>
    private static async Task<DateTime?> SinceAsync(ZayraDbContext db, Guid tenantId, Guid companyId, string period, CancellationToken ct)
    {
        if (!DateTime.TryParseExact((period ?? "") + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first))
            return null;
        var index = first.Year * 12 + first.Month;
        var latest = await ScopedBypass.TenantWide(db.PayrollRuns, tenantId, "SoD finds the company's previous lock regardless of the caller's company claims.")
            .AsNoTracking()
            .Where(r => r.CompanyId == companyId && r.LockedAtUtc != null && r.Year * 12 + r.Month < index)
            .MaxAsync(r => r.LockedAtUtc, ct);
        return latest;
    }
}
