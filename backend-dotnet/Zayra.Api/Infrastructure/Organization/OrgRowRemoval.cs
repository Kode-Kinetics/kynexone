using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Setup;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Organization;

/// <summary>
/// The one removal rule for branches, departments, designations, grades, cost centres and locations
/// (owner decision 2026-10-25): a row nothing points at is deleted; a row that is in use or has history is
/// turned OFF (IsActive = false) so every existing reference still resolves, and can be restored.
///
/// "Live reference" below means a row that still exists (not soft-deleted) and holds the FK. History rows
/// (terminated employees, closed grade-entitlement versions) count: they must keep resolving their grade.
/// </summary>
public sealed class OrgRowRemoval(ZayraDbContext db, IAuditService? audit) : IOrgRowRemoval
{
    private const string Why =
        "Removing an organisation row must see every reference to it in the tenant, whatever company the caller's switcher shows; otherwise a sister company's employees would not count and the row would be deleted under them.";

    // ── usage ───────────────────────────────────────────────────────────────────────────────────────────

    public async Task<OrgRowUsage> UsageAsync(OrgRowKind kind, Guid tenantId, Guid id, CancellationToken ct)
    {
        var map = await UsageManyAsync(kind, tenantId, [id], ct);
        return map.TryGetValue(id, out var usage) ? usage : OrgRowUsage.None;
    }

    public async Task<IReadOnlyDictionary<Guid, OrgRowUsage>> UsageManyAsync(
        OrgRowKind kind, Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var result = new Dictionary<Guid, OrgRowUsage>();
        if (ids.Count == 0) return result;
        var idList = ids.ToList();
        // entity kind -> row id -> count, summed over every column of that entity that points at the row.
        var counts = new Dictionary<Guid, Dictionary<string, int>>();
        void Add(Guid rowId, string entity, int n)
        {
            if (n <= 0) return;
            if (!counts.TryGetValue(rowId, out var per)) counts[rowId] = per = new(StringComparer.Ordinal);
            per[entity] = per.GetValueOrDefault(entity) + n;
        }
        var references = OrgRowReferences.For(db.Model, kind).References;
        foreach (var r in references)
        {
            var method = typeof(OrgRowRemoval).GetMethod(r.Shape == OrgRefShape.Guid ? nameof(CountGuidAsync) : r.Shape == OrgRefShape.Text ? nameof(CountTextAsync) : nameof(CountJsonAsync),
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.MakeGenericMethod(r.Entity);
            var task = (Task<List<(Guid, int)>>)method.Invoke(null, [db, tenantId, r.Property, idList, ct])!;
            foreach (var (rowId, n) in await task) Add(rowId, r.Entity.Name, n);
        }
        foreach (var (rowId, per) in counts)
            result[rowId] = new OrgRowUsage(per.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => new OrgRowUsageItem(OrgRowReferences.KindOf(x.Key), OrgRowReferences.LabelOf(x.Key), x.Value)).ToList());
        return result;
    }

    // Each count is pinned to the tenant (TenantWideByConvention) and ignores soft-deleted referencing rows. The company
    // filter is dropped on purpose: a sister company's records still hold the row.
    private const string Reach = "Counting what still points at an organisation row must see every company of the tenant, or a row used by a sister company would be deleted under it.";

    private static IQueryable<T> Live<T>(ZayraDbContext db, Guid tenantId) where T : class
    {
        var q = ScopedBypass.TenantWideByConvention(db.Set<T>(), tenantId, Reach).AsNoTracking();
        return db.Model.FindEntityType(typeof(T))!.FindProperty("IsDeleted") is null ? q : q.Where(e => !EF.Property<bool>(e, "IsDeleted"));
    }

    private static async Task<List<(Guid, int)>> CountGuidAsync<T>(ZayraDbContext db, Guid tenantId, string property, List<Guid> ids, CancellationToken ct) where T : class
    {
        var keys = ids.Select(i => (Guid?)i).ToList();
        var rows = await Live<T>(db, tenantId).Where(e => keys.Contains(EF.Property<Guid?>(e, property)))
            .GroupBy(e => EF.Property<Guid?>(e, property)).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        return rows.Where(r => r.Key.HasValue).Select(r => (r.Key!.Value, r.Count)).ToList();
    }

    private static async Task<List<(Guid, int)>> CountTextAsync<T>(ZayraDbContext db, Guid tenantId, string property, List<Guid> ids, CancellationToken ct) where T : class
    {
        var text = ids.ToDictionary(i => i.ToString(), i => i);
        var keys = text.Keys.ToList();
        var rows = await Live<T>(db, tenantId).Where(e => keys.Contains(EF.Property<string>(e, property)))
            .GroupBy(e => EF.Property<string>(e, property)).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        return rows.Select(r => (text[r.Key], r.Count)).ToList();
    }

    /// <summary>A JSON list of ids in a text column (eligible grades, designations…): a row holds the id when the text contains it.</summary>
    private static async Task<List<(Guid, int)>> CountJsonAsync<T>(ZayraDbContext db, Guid tenantId, string property, List<Guid> ids, CancellationToken ct) where T : class
    {
        // Read the column and match in memory: the column can be json/jsonb on Postgres, where LIKE (string Contains) does not
        // exist, and a provider-neutral projection works on every provider. Rows are pinned to the tenant and live only.
        var values = await Live<T>(db, tenantId).Select(e => EF.Property<string>(e, property)).Where(v => v != null).ToListAsync(ct);
        var found = new List<(Guid, int)>();
        foreach (var id in ids)
        {
            var needle = id.ToString();
            var n = values.Count(v => v!.Contains(needle, StringComparison.OrdinalIgnoreCase));
            if (n > 0) found.Add((id, n));
        }
        return found;
    }

    // ── remove / restore ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The usage check and the state change run as one unit under the tenant's setup lock (a transaction of its own
    /// unless the caller already has one). Writers of the referencing rows do not take that lock, so the delete path also
    /// re-reads usage after the write and turns the row off instead if a reference landed in between.</summary>
    private async Task<T> LockedAsync<T>(Guid tenantId, Func<Task<T>> body, CancellationToken ct)
    {
        if (!db.Database.IsRelational()) return await body();
        if (db.Database.CurrentTransaction is not null)
        {
            await SetupTenantLock.AcquireAsync(db, tenantId, ct);
            return await body();
        }
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await SetupTenantLock.AcquireAsync(db, tenantId, ct);
            var result = await body();
            await tx.CommitAsync(ct);
            return result;
        });
    }

    public Task<OrgRowRemovalResult?> RemoveAsync(OrgRowKind kind, Guid tenantId, Guid id, RequestContext context, CancellationToken ct, string? reason = null) =>
        LockedAsync(tenantId, async () =>
        {
            var entity = await LoadAsync(kind, tenantId, id, ct);
            if (entity is null) return null;
            // Cost-accounting M-8: a cost centre a department, position, employee, posting rule, company default or active child
            // still points at is neither deleted nor turned off (the database refuses it too); only history alone may turn it off.
            if (entity is CostCenter liveCostCentre)
                await OrganizationSetupService.EnsureCostCentreNotInUseAsync(db, tenantId, liveCostCentre, ct);
            var usage = await UsageAsync(kind, tenantId, id, ct);
            var name = Name(kind);
            var snapshot = Snapshot(entity) + (string.IsNullOrWhiteSpace(reason) ? "" : $"; reason={reason.Trim()}");
            // A cost centre's audit row keeps the cost-accounting shape (JSON with the reason), which finance's checks read.
            string Meta(string? note) => entity is CostCenter cc
                ? System.Text.Json.JsonSerializer.Serialize(
                    new { code = cc.Code, name = cc.Name, companyId = cc.CompanyId, reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(), note },
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
                : string.IsNullOrEmpty(note) ? snapshot : $"{snapshot}; {note}";

            async Task<OrgRowRemovalResult> TurnOff(OrgRowUsage why)
            {
                Set(entity, "IsActive", false);
                Touch(entity, context);
                audit?.Stage($"organization.{name}_turned_off", entity.GetType().Name, id.ToString(), context, Meta(why.Summary));
                await db.SaveChangesAsync(ct);
                return new OrgRowRemovalResult(OrgRowOutcomes.TurnedOff, why, $"Turned off. {why.Summary}. It stays in history and can be restored anytime.");
            }
            if (usage.InUse) return await TurnOff(usage);

            Set(entity, "IsDeleted", true);
            Set(entity, "DeletedAtUtc", DateTime.UtcNow, required: false);
            Set(entity, "DeletedBy", context.UserId, required: false);
            Touch(entity, context);
            await db.SaveChangesAsync(ct);
            // A reference written by a request that does not take the lock, between the check and the write, is seen here.
            var after = await UsageAsync(kind, tenantId, id, ct);
            if (after.InUse)
            {
                Set(entity, "IsDeleted", false);
                Set(entity, "DeletedAtUtc", null, required: false);
                Set(entity, "DeletedBy", null, required: false);
                return await TurnOff(after);
            }
            // The delete itself was saved above so the usage re-check could see it; the audit row joins it in the same
            // transaction (LockedAsync), so the removal cannot commit without its trail.
            if (audit is not null)
            {
                audit.Stage($"organization.{name}_deleted", entity.GetType().Name, id.ToString(), context, Meta(null));
                await db.SaveChangesAsync(ct);
            }
            return new OrgRowRemovalResult(OrgRowOutcomes.Deleted, OrgRowUsage.None, "Removed.");
        }, ct);

    public Task<bool?> RestoreAsync(OrgRowKind kind, Guid tenantId, Guid id, RequestContext context, CancellationToken ct) =>
        LockedAsync<bool?>(tenantId, async () =>
        {
            var entity = await LoadAsync(kind, tenantId, id, ct);
            if (entity is null) return null;
            if (Get<bool>(entity, "IsActive")) return true;
            Set(entity, "IsActive", true);
            Touch(entity, context);
            audit?.Stage($"organization.{Name(kind)}_restored", entity.GetType().Name, id.ToString(), context, Snapshot(entity));
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);

    public async Task RecordRefusalAsync(OrgRowKind kind, Guid tenantId, Guid id, string action, string reason, RequestContext context, CancellationToken ct)
    {
        if (audit is null) return;
        audit.Stage($"organization.{Name(kind)}_{action}_refused", kind.ToString(), id.ToString(), context, reason);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Code, name and company at the time, so a deleted row can still be told apart in the audit trail.</summary>
    private static string Snapshot(object entity)
    {
        var code = Get<string>(entity, "Code", required: false);
        var name = Get<string>(entity, "NameEn", required: false) ?? Get<string>(entity, "Name", required: false) ?? Get<string>(entity, "TitleEn", required: false);
        var company = Get<Guid?>(entity, "CompanyId", required: false);
        return $"code={code}; name={name}; company={company}";
    }

    public async Task<(bool Found, Guid? CompanyId)> CompanyOfAsync(OrgRowKind kind, Guid tenantId, Guid id, CancellationToken ct)
    {
        var entity = await LoadAsync(kind, tenantId, id, ct, tracking: false);
        if (entity is null) return (false, null);
        if (kind == OrgRowKind.Location)
        {
            var branchId = Get<Guid?>(entity, "BranchId");
            if (branchId is null) return (true, null);
            var branch = await ScopedBypass.TenantWide(db.Branches, tenantId, Why).AsNoTracking()
                .Where(b => b.Id == branchId && !b.IsDeleted).Select(b => (Guid?)b.CompanyId).FirstOrDefaultAsync(ct);
            return (true, branch);
        }
        return (true, Get<Guid?>(entity, "CompanyId"));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────

    private async Task<object?> LoadAsync(OrgRowKind kind, Guid tenantId, Guid id, CancellationToken ct, bool tracking = true)
    {
        object? Track(object? o) => o;
        return kind switch
        {
            OrgRowKind.Branch => Track(await Q(ScopedBypass.TenantWide(db.Branches, tenantId, Why).Where(x => x.Id == id && !x.IsDeleted), tracking).FirstOrDefaultAsync(ct)),
            OrgRowKind.Department => Track(await Q(ScopedBypass.TenantWide(db.Departments, tenantId, Why).Where(x => x.Id == id && !x.IsDeleted), tracking).FirstOrDefaultAsync(ct)),
            OrgRowKind.Designation => Track(await Q(ScopedBypass.TenantWide(db.Designations, tenantId, Why).Where(x => x.Id == id && !x.IsDeleted), tracking).FirstOrDefaultAsync(ct)),
            OrgRowKind.Grade => Track(await Q(ScopedBypass.TenantWide(db.Grades, tenantId, Why).Where(x => x.Id == id && !x.IsDeleted), tracking).FirstOrDefaultAsync(ct)),
            OrgRowKind.CostCenter => Track(await Q(ScopedBypass.TenantWide(db.CostCenters, tenantId, Why).Where(x => x.Id == id && !x.IsDeleted), tracking).FirstOrDefaultAsync(ct)),
            OrgRowKind.Location => Track(await Q(ScopedBypass.TenantWide(db.Locations, tenantId, Why).Where(x => x.Id == id && !x.IsDeleted), tracking).FirstOrDefaultAsync(ct)),
            _ => null,
        };
    }

    private static IQueryable<T> Q<T>(IQueryable<T> q, bool tracking) where T : class => tracking ? q : q.AsNoTracking();

    private static string Name(OrgRowKind kind) => kind switch
    {
        OrgRowKind.CostCenter => "cost_center",
        _ => kind.ToString().ToLowerInvariant(),
    };

    // A property the contract depends on must exist: a rename must fail loudly, never report "Removed." and write nothing.
    private static T? Get<T>(object entity, string property, bool required = true)
    {
        var p = entity.GetType().GetProperty(property);
        if (p is null) return required ? throw new InvalidOperationException($"{entity.GetType().Name} has no {property}.") : default;
        return (T?)p.GetValue(entity);
    }
    private static void Set(object entity, string property, object? value, bool required = true)
    {
        var p = entity.GetType().GetProperty(property);
        if (p is null) { if (required) throw new InvalidOperationException($"{entity.GetType().Name} has no {property}."); return; }
        p.SetValue(entity, value);
    }

    private static void Touch(object entity, RequestContext context)
    {
        Set(entity, "UpdatedAtUtc", DateTime.UtcNow, required: false);
        Set(entity, "UpdatedBy", context.UserId, required: false);
    }
}
