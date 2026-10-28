using System.Globalization;
using System.Text;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Compliance;

namespace Zayra.Api.Application.Employees.Onboarding;

/// <summary>Statutory class a nationality drives (GOSI, Nitaqat, identity documents).</summary>
public enum NationalityClass { Saudi, Gcc, Expat }

public sealed record NationalityEntry(string Iso2, string NameEn, string NameAr, NationalityClass Class);

/// <summary>
/// The ISO 3166 nationality list the hire flow stores (alpha-2). Free text resolves by EXACT match after
/// trim, case-fold and Arabic normalisation (alef variants, dotless ya, tatweel, diacritics) and never by
/// substring: "Suadi" does not resolve, so it stays in the HR review queue instead of being guessed into
/// a class. <see cref="LegacyClassOf"/> is the rule the legacy readers apply today, kept so the backfill
/// can prove it never reclassifies anyone.
/// </summary>
public static class NationalityCatalog
{
    private static readonly string[] GccCodes = ["AE", "QA", "KW", "OM", "BH"];

    public static IReadOnlyList<NationalityEntry> Entries { get; } = Build();

    private static readonly Dictionary<string, NationalityEntry> ByIso2 =
        Entries.ToDictionary(e => e.Iso2, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, NationalityEntry> ByText = BuildTextIndex();

    private static List<NationalityEntry> Build()
    {
        var list = new List<NationalityEntry>();
        foreach (var line in NationalityCatalogData.Rows.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = line.Split('|');
            if (p.Length != 3) continue;
            var cls = p[0] == "SA" ? NationalityClass.Saudi : GccCodes.Contains(p[0]) ? NationalityClass.Gcc : NationalityClass.Expat;
            list.Add(new NationalityEntry(p[0], p[1], p[2], cls));
        }
        return list;
    }

    private static Dictionary<string, NationalityEntry> BuildTextIndex()
    {
        var idx = new Dictionary<string, NationalityEntry>(StringComparer.Ordinal);
        foreach (var e in Entries)
        {
            idx.TryAdd(Fold(e.NameEn), e);
            idx.TryAdd(Fold(e.NameAr), e);
        }
        foreach (var line in NationalityCatalogData.Aliases.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = line.Split('|');
            if (p.Length == 2 && ByIso2.TryGetValue(p[1], out var e)) idx.TryAdd(Fold(p[0]), e);
        }
        return idx;
    }

    /// <summary>Case-fold + Arabic normalisation + whitespace collapse. Used for comparison only.</summary>
    public static string Fold(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        var lastSpace = true;
        foreach (var raw in text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormC))
        {
            var ch = raw;
            if (ch == '\u0640') continue;                                       // tatweel
            if (ch is >= '\u064B' and <= '\u065F' or '\u0670') continue;       // harakat
            ch = ch switch { '\u0623' or '\u0625' or '\u0622' or '\u0671' => '\u0627', '\u0649' => '\u064A', _ => ch };  // alef variants -> alef, alef maksura -> ya
            if (char.IsWhiteSpace(ch)) { if (!lastSpace) sb.Append(' '); lastSpace = true; continue; }
            sb.Append(ch); lastSpace = false;
        }
        return sb.ToString().TrimEnd();
    }

    public static NationalityEntry? FindByIso2(string? iso2) =>
        !string.IsNullOrWhiteSpace(iso2) && ByIso2.TryGetValue(iso2.Trim(), out var e) ? e : null;

    public static NationalityClass? ClassOf(string? iso2) => FindByIso2(iso2)?.Class;

    /// <summary>Exact resolution of a typed value (name, demonym, alias, ISO-2 or ISO-3). Null = unresolved.</summary>
    public static NationalityEntry? TryResolve(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (SaudiNationality.IsSaudi(text)) return ByIso2["SA"];
        var folded = Fold(text);
        if (ByText.TryGetValue(folded, out var hit)) return hit;
        var iso2 = CountryCodeStandard.NormalizeToIso2(text);
        if (iso2 is not null && ByIso2.TryGetValue(iso2, out var byCode)) return byCode;
        return text.Trim().Length == 2 ? FindByIso2(text) : null;
    }

    /// <summary>
    /// The class today's readers assign to this text: Saudi spellings, then the GCC names, otherwise Expat
    /// (a blank is therefore Expat, which is exactly the behaviour the backfill must not silently change).
    /// </summary>
    public static NationalityClass LegacyClassOf(string? text)
    {
        if (SaudiNationality.IsSaudi(text)) return NationalityClass.Saudi;
        var n = Zayra.Api.Infrastructure.Employees.GccReadinessFloor.NormalizeNationality(text);
        return GccCodes.Contains(n) ? NationalityClass.Gcc : NationalityClass.Expat;
    }
}
