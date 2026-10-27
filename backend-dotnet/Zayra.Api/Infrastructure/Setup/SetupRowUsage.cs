using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Organization;

namespace Zayra.Api.Controllers;

/// <summary>
/// What still points at a Setup row (leave type, shift, scorecard template), discovered from the EF model instead of listed by hand,
/// so a table added later that references the row is counted without anyone remembering this file. A row is hard-deleted only when
/// this finds nothing, in any company of the tenant. <c>SetupRowReferencesRatchetTests</c> fails when a column that looks like a
/// reference is neither found here nor allow-listed with a reason.
/// </summary>
internal static class SetupRowUsage
{
    public sealed record Reference(Type Entity, string Property);

    /// <summary>Columns that carry the stem in their name but are not usage of the row, each with the reason. Entity.Property.</summary>
    public static readonly IReadOnlyDictionary<string, string> NotUsage = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Every Guid column of the model that references <paramref name="principal"/>: an enforced foreign key to it, or a Guid
    /// column named <c>&lt;stem&gt;Id</c> (most relations in this model are unenforced).</summary>
    public static IReadOnlyList<Reference> Discover(IModel model, Type principal, string stem)
    {
        var found = new Dictionary<string, Reference>(StringComparer.Ordinal);
        var principalType = model.FindEntityType(principal)!;
        foreach (var entity in model.GetEntityTypes())
        {
            if (entity.IsOwned() || entity.ClrType.IsAbstract || entity.GetTableName() is null || entity.ClrType == principal) continue;
            foreach (var fk in entity.GetForeignKeys().Where(f => f.PrincipalEntityType == principalType))
                foreach (var p in fk.Properties)
                    found[$"{entity.ClrType.Name}.{p.Name}"] = new Reference(entity.ClrType, p.Name);
            foreach (var p in entity.GetProperties())
            {
                var type = Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType;
                if (type == typeof(Guid) && p.Name.EndsWith(stem + "Id", StringComparison.Ordinal))
                    found[$"{entity.ClrType.Name}.{p.Name}"] = new Reference(entity.ClrType, p.Name);
            }
        }
        return found.Where(kv => !NotUsage.ContainsKey(kv.Key)).Select(kv => kv.Value)
            .OrderBy(r => r.Entity.Name, StringComparer.Ordinal).ThenBy(r => r.Property, StringComparer.Ordinal).ToList();
    }

    /// <summary>Guid columns whose name merely contains <paramref name="stem"/> (any position) that <see cref="Discover"/> did not return:
    /// the ratchet's list of things to look at.</summary>
    public static IReadOnlyList<string> Lookalikes(IModel model, Type principal, string stem, IReadOnlyList<Reference> found)
    {
        var have = found.Select(f => $"{f.Entity.Name}.{f.Property}").ToHashSet(StringComparer.Ordinal);
        var principalType = model.FindEntityType(principal)!;
        var result = new List<string>();
        foreach (var entity in model.GetEntityTypes())
        {
            if (entity.IsOwned() || entity.ClrType.IsAbstract || entity.GetTableName() is null || entity.ClrType == principal) continue;
            foreach (var p in entity.GetProperties())
            {
                var type = Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType;
                var key = $"{entity.ClrType.Name}.{p.Name}";
                if (type == typeof(Guid) && p.Name.Contains(stem, StringComparison.Ordinal) && !have.Contains(key) && !NotUsage.ContainsKey(key))
                    result.Add(key);
            }
        }
        return result;
    }

    private const string Reach =
        "Removing a Setup row must see every reference to it in the tenant, whatever company the caller's switcher shows; otherwise a sister company's records would not count and the row would be deleted under them.";

    /// <summary>One probe per referencing entity. <paramref name="names"/> maps an entity to the (kind, label) the UI translates;
    /// anything unmapped falls back to a lower-camel kind and a spaced label.</summary>
    public static IReadOnlyList<SetupRemoval.Probe> Probes(ZayraDbContext db, Guid tenantId, Guid id, Type principal, string stem,
        IReadOnlyDictionary<string, (string Kind, string Label)>? names = null)
    {
        var probes = new List<SetupRemoval.Probe>();
        foreach (var group in Discover(db.Model, principal, stem).GroupBy(r => r.Entity))
        {
            var props = group.Select(g => g.Property).ToList();
            var (kind, label) = names is not null && names.TryGetValue(group.Key.Name, out var n)
                ? n : (OrgRowReferences.KindOf(group.Key.Name), OrgRowReferences.LabelOf(group.Key.Name));
            var method = typeof(SetupRowUsage).GetMethod(nameof(CountAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.MakeGenericMethod(group.Key);
            Func<bool, CancellationToken, Task<int>> run = (wide, ct) => (Task<int>)method.Invoke(null, [db, tenantId, id, props, wide, ct])!;
            probes.Add(new SetupRemoval.Probe(kind, label, ct => run(true, ct), ct => run(false, ct)));
        }
        return probes;
    }

    // wide: every company of the tenant. Otherwise the caller's own scope (the context's query filters), which a company-scoped
    // caller's response is limited to. Soft-deleted referencing rows never count.
    private static async Task<int> CountAsync<T>(ZayraDbContext db, Guid tenantId, Guid id, List<string> properties, bool wide, CancellationToken ct) where T : class
    {
        var q = wide ? ScopedBypass.TenantWideByConvention(db.Set<T>(), tenantId, Reach).AsNoTracking() : db.Set<T>().AsNoTracking();
        if (db.Model.FindEntityType(typeof(T))!.FindProperty("IsDeleted") is not null) q = q.Where(e => !EF.Property<bool>(e, "IsDeleted"));
        var key = (Guid?)id;
        var total = 0;
        foreach (var property in properties)
            total += await q.CountAsync(e => EF.Property<Guid?>(e, property) == key, ct);
        return total;
    }
}
