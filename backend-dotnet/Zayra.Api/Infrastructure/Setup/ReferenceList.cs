using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Seed;

namespace Zayra.Api.Infrastructure.Setup;

/// <summary>
/// One tenant reference list (Setup → Reference lists), used to read a stored employee value as its code.
/// <para>
/// Employee rows hold either the value's code (<c>MALE</c>) — what the employee forms and import store now — or the
/// English label (<c>Male</c>) that was stored before. Nothing rewrites those older rows, so every reader that
/// compares one of these values goes through <see cref="Normalize"/>: a code and its label then compare the same,
/// and renaming a label in Setup never orphans a row that stores the code (codes cannot be edited).
/// </para>
/// Matching is case-insensitive and tries, in order: the tenant's codes, their English labels, their Arabic
/// labels, then the provisioning starter values for the type, then a few fixed aliases (<c>M</c>/<c>F</c>).
/// A value nothing recognises is kept as typed, so an unknown value is never silently turned into another one.
/// </summary>
public sealed class ReferenceList
{
    public const string GenderType = "Gender";
    public const string MaritalStatusType = "MaritalStatus";
    public const string EmploymentTypeType = "EmploymentType";
    public const string ContractTypeType = "ContractType";

    /// <summary>The seeded gender codes. A tenant that re-coded its list still matches through the labels.</summary>
    public const string Male = "MALE";
    public const string Female = "FEMALE";

    private static readonly Dictionary<string, (string Alias, string SeedCode)[]> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        [GenderType] = [("M", Male), ("F", Female)],
    };

    private readonly Dictionary<string, string> _codeByKey;
    private readonly Dictionary<string, string> _labelByCode;

    private ReferenceList(string typeCode, Dictionary<string, string> codeByKey, Dictionary<string, string> labelByCode)
    {
        TypeCode = typeCode;
        _codeByKey = codeByKey;
        _labelByCode = labelByCode;
    }

    public string TypeCode { get; }

    /// <summary>Builds the list from the tenant's own values (deleted ones excluded by the caller; retired ones
    /// included, because employees may still hold them).</summary>
    public static ReferenceList FromValues(string typeCode, IEnumerable<(string Code, string En, string Ar)> tenantValues)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var values = tenantValues.Where(v => !string.IsNullOrWhiteSpace(v.Code)).Select(v => (Code: v.Code.Trim(), En: v.En?.Trim() ?? "", Ar: v.Ar?.Trim() ?? "")).ToList();
        // Codes first, so a code always wins over another value's label that happens to read the same.
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in values) { map.TryAdd(v.Code, v.Code); if (v.En.Length > 0) labels.TryAdd(v.Code, v.En); }
        foreach (var v in values) if (v.En.Length > 0) map.TryAdd(v.En, v.Code);
        foreach (var v in values) if (v.Ar.Length > 0) map.TryAdd(v.Ar, v.Code);

        // Starter values: a seeded code or label resolves to whatever the tenant's list already calls it, so a
        // tenant that re-coded "Male" as "M" still reads a stored "MALE" as "M".
        foreach (var (code, en, ar) in TenantProvisioningBundle.SeedMasterValues(typeCode))
        {
            var resolved = map.GetValueOrDefault(en) ?? map.GetValueOrDefault(ar) ?? map.GetValueOrDefault(code) ?? code;
            map.TryAdd(code, resolved);
            map.TryAdd(en, resolved);
            map.TryAdd(ar, resolved);
            labels.TryAdd(resolved, en);
        }
        if (Aliases.TryGetValue(typeCode, out var aliases))
            foreach (var (alias, seedCode) in aliases)
                map.TryAdd(alias, map.GetValueOrDefault(seedCode) ?? seedCode);
        return new ReferenceList(typeCode, map, labels);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ReferenceList> Defaults = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The list as provisioned, for code paths that have no tenant to read (pure calculators, tests).</summary>
    public static ReferenceList Default(string typeCode) => Defaults.GetOrAdd(typeCode, t => FromValues(t, []));

    /// <summary>Loads the tenant's list: every value that is not deleted, active or retired.</summary>
    public static async Task<ReferenceList> LoadAsync(ZayraDbContext db, Guid tenantId, string typeCode, CancellationToken ct)
    {
        var values = await db.MasterDataValues.AsNoTracking()
            .Where(v => v.TenantId == tenantId && !v.IsDeleted
                && db.MasterDataTypes.Any(t => t.Id == v.TypeId && t.TenantId == tenantId && t.Code == typeCode && !t.IsDeleted))
            .Select(v => new { v.Code, v.ValueEn, v.ValueAr })
            .ToListAsync(ct);
        return FromValues(typeCode, values.Select(v => (v.Code, v.ValueEn, v.ValueAr)));
    }

    /// <summary>The code a stored value stands for, or null when it is blank or not on the list.</summary>
    public string? ToCode(string? stored)
    {
        var key = stored?.Trim();
        if (string.IsNullOrEmpty(key)) return null;
        return _codeByKey.TryGetValue(key, out var code) ? code : null;
    }

    /// <summary>The code when the value is recognised, otherwise the value as typed (trimmed); blank stays blank.</summary>
    public string Normalize(string? stored) => ToCode(stored) ?? stored?.Trim() ?? string.Empty;

    /// <summary>The English label the tenant's list gives a stored value today, or the value as typed when unknown.</summary>
    public string LabelEn(string? stored)
    {
        var code = Normalize(stored);
        return _labelByCode.GetValueOrDefault(code) ?? code;
    }

    /// <summary>True when two stored values mean the same reference value (code or label, any case).</summary>
    public bool Same(string? left, string? right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
}

/// <summary>The four employee reference lists the employee import reads, loaded once per file.</summary>
public sealed record ImportReferenceLists(ReferenceList Gender, ReferenceList MaritalStatus, ReferenceList EmploymentType, ReferenceList ContractType)
{
    public static async Task<ImportReferenceLists> LoadAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct) => new(
        await ReferenceList.LoadAsync(db, tenantId, ReferenceList.GenderType, ct),
        await ReferenceList.LoadAsync(db, tenantId, ReferenceList.MaritalStatusType, ct),
        await ReferenceList.LoadAsync(db, tenantId, ReferenceList.EmploymentTypeType, ct),
        await ReferenceList.LoadAsync(db, tenantId, ReferenceList.ContractTypeType, ct));
}
