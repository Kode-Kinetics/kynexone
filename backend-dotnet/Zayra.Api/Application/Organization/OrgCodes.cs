namespace Zayra.Api.Application.Organization;

/// <summary>
/// ONE rule for an organisation code (branch, department, designation, grade, cost centre),
/// in one place, applied on every door.
///
/// <para>WHY THIS TYPE EXISTS. There used to be two rules. The setup form upper-cased the code on
/// its way to the column; the CSV importer stored whatever the spreadsheet said. The codes are
/// unique per tenant under a case-SENSITIVE index, so `ops` (imported) and `OPS` (typed) were two
/// different rows and both were accepted. The importers then keyed their own lookups on
/// <c>ToUpperInvariant</c>, so the first run after that collision built a dictionary with a
/// duplicate key and threw — and every import and import-preview for that tenant returned 500
/// from then on, with nothing in the product able to undo it.</para>
///
/// <para>Two halves, and both are needed. <see cref="Normalize"/> is the write rule, called from the
/// single place that maps a request onto an entity, so no door can store an un-normalised code
/// again. <see cref="TryBuildLookup{T}"/> is the read rule: it refuses to throw on a tenant that is
/// ALREADY in the broken state, and hands back the colliding codes so the caller can say which two
/// rows have to be reconciled by a human. Existing mixed-case rows are deliberately NOT rewritten
/// in bulk: where two of them collide, merging them would silently destroy one department's
/// identity, and only the tenant knows which one is real.</para>
/// </summary>
public static class OrgCodes
{
    /// <summary>The write rule: trim, then upper-case invariantly. Null and blank round-trip to "".</summary>
    public static string Normalize(string? code) => (code ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>
    /// Build a by-code lookup without ever throwing on a duplicate key. Returns false, and fills
    /// <paramref name="collisions"/> with the raw codes that share a normalised form, when the
    /// tenant already holds rows that differ only in case.
    /// </summary>
    public static bool TryBuildLookup<T>(
        IEnumerable<T> rows,
        Func<T, string> codeOf,
        out Dictionary<string, T> lookup,
        out IReadOnlyList<string> collisions)
    {
        lookup = new Dictionary<string, T>(StringComparer.Ordinal);
        var clashing = new List<string>();
        var rawByKey = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var raw = codeOf(row) ?? string.Empty;
            var key = Normalize(raw);
            if (!rawByKey.TryGetValue(key, out var raws))
                rawByKey[key] = raws = new List<string>();
            raws.Add(raw);
            lookup.TryAdd(key, row);
        }

        foreach (var (_, raws) in rawByKey)
        {
            if (raws.Count <= 1) continue;
            clashing.AddRange(raws.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal));
        }

        collisions = clashing;
        return clashing.Count == 0;
    }
}

/// <summary>
/// The body returned when a tenant already holds two rows whose codes differ only in case. It is a
/// refusal with the evidence in it, not a 500: it names the entity, the field and the exact values,
/// because reconciling them is a decision only the tenant can make.
/// </summary>
public static class OrgCodeCollision
{
    public const string ErrorCode = "code_case_collision";

    public static object Payload(string entity, string field, IReadOnlyList<string> codes) => new
    {
        error = ErrorCode,
        entity,
        field,
        codes,
        message =
            $"This tenant holds more than one {entity} whose {field} differs only in letter case " +
            $"({string.Join(", ", codes.Select(c => $"'{c}'"))}). Import cannot tell them apart. " +
            $"Rename or remove all but one of them in {entity} setup, then run the import again.",
    };
}
