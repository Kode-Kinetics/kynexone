using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Application.Localization;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees;

// ── Contracts ─────────────────────────────────────────────────────────────────

/// <summary>
/// A candidate the detector believes may be the SAME human as the probe. Carries the raw
/// identifying columns; the CONTROLLER applies scope masking (CompanyId → canView) before it
/// leaves the process — the detector itself is scope-agnostic so it stays pure and unit-testable.
/// </summary>
public sealed record DuplicateMatch(
    int EmployeeId,
    string EmployeeCode,
    string FullName,
    string? Branch,
    System.Guid? CompanyId,
    string Status,
    /// <summary>"strong" (exact national-identity match, or passport corroborated by name/DOB) |
    /// "probable" (name+DOB fuzzy, or passport-only).</summary>
    string MatchType,
    IReadOnlyList<string> Signals);

/// <summary>The inputs a caller supplies to look for an existing person. Identity values are keyed by the
/// SAME snake_case field keys the create modal / compliance mirror use (iqama_number, emirates_id, qid,
/// civil_id, passport_number, id_number). Never trusts EmployeeCode.</summary>
public sealed record DuplicateProbe(
    string? EnglishName,
    string? ArabicName,
    DateOnly? DateOfBirth,
    string? Nationality,
    System.Guid? CompanyId,
    IReadOnlyDictionary<string, string> IdentityValues,
    int? ExcludeEmployeeId);

public interface IEmployeeDuplicateDetector
{
    /// <summary>
    /// Single/create execution mode: a handful of INDEXED, sargable point-lookups (one OR-set per
    /// non-blank identity value) plus a DOB-blocked name+DOB pass. Tenant-wide across companies,
    /// excludes soft-deleted and <see cref="DuplicateProbe.ExcludeEmployeeId"/>. Never throws.
    /// The batch importer does NOT call this per row — it uses the preloaded-dictionary path
    /// (<see cref="EmployeeDuplicateMatcher"/>) so import is not N×M queries.
    /// </summary>
    Task<IReadOnlyList<DuplicateMatch>> FindAsync(System.Guid tenantId, DuplicateProbe probe, CancellationToken ct);
}

// ── Match-type + signal vocabulary ──────────────────────────────────────────────

public static class DuplicateMatchTypes
{
    public const string Strong = "strong";
    public const string Probable = "probable";
}

/// <summary>The identity fieldKey → Employee column mapping and the STRONG/weak weighting. National IDs
/// (Iqama/EID/QID/Civil/national IdNumber) are STRONG on their own; a passport is only PROBABLE alone
/// (recycled on renewal, frequently mis-attached across GCC records) and escalates to STRONG only when
/// corroborated by a name or DOB agreement (M4).</summary>
public static class DuplicateIdentityFields
{
    /// <summary>fieldKey (lower snake) → (human label, isStrongAlone).</summary>
    public static readonly IReadOnlyDictionary<string, (string Label, bool StrongAlone)> Map =
        new Dictionary<string, (string, bool)>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["iqama_number"] = ("Iqama number", true),
            ["emirates_id"] = ("Emirates ID", true),
            ["qid"] = ("Qatar ID (QID)", true),
            ["civil_id"] = ("Civil ID", true),
            ["id_number"] = ("National ID number", true),
            ["national_id"] = ("National ID number", true),
            ["passport_number"] = ("Passport number", false),
            ["passport"] = ("Passport number", false),
        };
}

// ── Detector ────────────────────────────────────────────────────────────────────

public sealed class EmployeeDuplicateDetector : IEmployeeDuplicateDetector
{
    private readonly ZayraDbContext _db;
    private readonly ITransliterationService? _transliterator;

    public EmployeeDuplicateDetector(ZayraDbContext db, ITransliterationService? transliterator = null)
    {
        _db = db;
        _transliterator = transliterator;
    }

    public async Task<IReadOnlyList<DuplicateMatch>> FindAsync(System.Guid tenantId, DuplicateProbe probe, CancellationToken ct)
    {
        var acc = new Dictionary<int, MatchAccumulator>();

        // Normalize probe identity values by column so we can (a) run sargable point-lookups and
        // (b) label the signal. Keep BOTH the trimmed raw and the digit-folded form so a value entered
        // with Arabic-Indic digits still matches a Western-digit stored value — both are plain `==`
        // (index-assisted), never a case-folding SQL function that would defeat the b-tree index (M3).
        var national = new List<(string Column, string Label, List<string> Values)>();
        var passport = new List<(string Label, List<string> Values)>();
        foreach (var kv in probe.IdentityValues)
        {
            if (string.IsNullOrWhiteSpace(kv.Value)) continue;
            if (!DuplicateIdentityFields.Map.TryGetValue(kv.Key.Trim(), out var meta)) continue;
            var variants = IdentityValueNormalizer.QueryVariants(kv.Value);
            if (variants.Count == 0) continue;
            var column = ColumnFor(kv.Key.Trim());
            if (column is null) continue;
            if (meta.StrongAlone) national.Add((column, meta.Label, variants));
            else passport.Add((meta.Label, variants));
        }

        // ── STRONG pass: exact national-identity match, one indexed OR-set per column ──
        foreach (var (column, label, values) in national)
        {
            var hits = await QueryByColumnAsync(tenantId, column, values, probe.ExcludeEmployeeId, ct);
            foreach (var e in hits)
                Add(acc, e, DuplicateMatchTypes.Strong, $"{label} match");
        }

        // ── PROBABLE name + DOB pass (DOB-blocked, fuzzy names in memory) ──
        var probeName = probe.EnglishName;
        var probeArabic = probe.ArabicName;
        var probeNorm = NameNormalizer.Normalize(probeName);
        var dob = probe.DateOfBirth;
        var candidatesByDob = new List<Employee>();
        if (dob is DateOnly d && !IdentityValueNormalizer.IsSentinelDob(d)
            && (!string.IsNullOrWhiteSpace(probeNorm) || !string.IsNullOrWhiteSpace(probeArabic)))
        {
            candidatesByDob = await _db.Employees.AsNoTracking()
                // IgnoreQueryFilters: detection is authoritative TENANT-WIDE across every company (a scoped
                // caller's company filter must NOT hide a cross-company dup). PII is protected downstream by
                // the controller's scope masking, not by hiding the match here.
                .IgnoreQueryFilters()
                .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.DateOfBirth == d
                            && (probe.ExcludeEmployeeId == null || x.Id != probe.ExcludeEmployeeId))
                .Take(IdentityValueNormalizer.DobBlockCap + 1)
                .ToListAsync(ct);

            // M2: a huge DOB block (sentinel-ish / common) is not a "small candidate set". Above the cap,
            // require a nationality corroborator before flagging a name+DOB probable (or suppress entirely
            // when no nationality is supplied) — the STRONG identity pass is the real safety net there.
            var overCap = candidatesByDob.Count > IdentityValueNormalizer.DobBlockCap;
            foreach (var e in candidatesByDob.Take(IdentityValueNormalizer.DobBlockCap))
            {
                if (overCap)
                {
                    if (string.IsNullOrWhiteSpace(probe.Nationality)
                        || !string.Equals(e.Nationality?.Trim(), probe.Nationality.Trim(), System.StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                if (NameMatches(probeNorm, probeArabic, e))
                    Add(acc, e, DuplicateMatchTypes.Probable, "Same date of birth and a matching name");
            }
        }

        // ── PASSPORT pass: probable alone; escalates to strong when name or DOB agrees (M4) ──
        foreach (var (label, values) in passport)
        {
            var hits = await QueryByColumnAsync(tenantId, "PassportNumber", values, probe.ExcludeEmployeeId, ct);
            foreach (var e in hits)
            {
                var corroborated =
                    (dob is DateOnly pd && e.DateOfBirth == pd && !IdentityValueNormalizer.IsSentinelDob(pd))
                    || NameMatches(probeNorm, probeArabic, e);
                Add(acc, e, corroborated ? DuplicateMatchTypes.Strong : DuplicateMatchTypes.Probable,
                    corroborated ? $"{label} match corroborated by name/DOB" : $"{label} match (verify — passports are reused)");
            }
        }

        return acc.Values
            .Select(m => new DuplicateMatch(m.Employee.Id, m.Employee.EmployeeCode, m.Employee.FullName,
                m.Employee.Branch, m.Employee.CompanyId, m.Employee.Status, m.MatchType, m.Signals.ToList()))
            .OrderByDescending(m => m.MatchType == DuplicateMatchTypes.Strong)
            .ToList();
    }

    private async Task<List<Employee>> QueryByColumnAsync(
        System.Guid tenantId, string column, List<string> values, int? excludeId, CancellationToken ct)
    {
        // IgnoreQueryFilters → authoritative tenant-wide (cross-company) identity lookup; masking is applied
        // by the controller, never by hiding a cross-scope match here.
        var q = _db.Employees.AsNoTracking().IgnoreQueryFilters().Where(x => x.TenantId == tenantId && !x.IsDeleted);
        if (excludeId is int ex) q = q.Where(x => x.Id != ex);
        // Plain equality on each variant → uses the filtered b-tree index (sargable). Never ToUpper/ILike.
        q = column switch
        {
            "IqamaNumber" => q.Where(x => values.Contains(x.IqamaNumber)),
            "EmiratesId" => q.Where(x => values.Contains(x.EmiratesId)),
            "Qid" => q.Where(x => values.Contains(x.Qid)),
            "CivilId" => q.Where(x => values.Contains(x.CivilId)),
            "IdNumber" => q.Where(x => values.Contains(x.IdNumber)),
            "PassportNumber" => q.Where(x => values.Contains(x.PassportNumber)),
            _ => q.Where(_ => false),
        };
        return await q.ToListAsync(ct);
    }

    private bool NameMatches(string probeNorm, string? probeArabic, Employee e)
    {
        if (!string.IsNullOrWhiteSpace(probeNorm))
        {
            var storedNorm = NameNormalizer.Normalize(e.FullName is { Length: > 0 } ? e.FullName : e.EnglishName);
            if (NameNormalizer.Similarity(probeNorm, storedNorm, _transliterator) >= NameNormalizer.ProbableThreshold)
                return true;
        }
        // Direct Arabic-name compare when both sides carry one (S5: never route Arabic through the
        // Latin→Arabic transliterator — it would drop non-Latin letters and false-match).
        if (!string.IsNullOrWhiteSpace(probeArabic) && !string.IsNullOrWhiteSpace(e.ArabicName)
            && NameNormalizer.ArabicSimilarity(probeArabic, e.ArabicName) >= NameNormalizer.ProbableThreshold)
            return true;
        return false;
    }

    private static string? ColumnFor(string fieldKey) => fieldKey.ToLowerInvariant() switch
    {
        "iqama_number" => "IqamaNumber",
        "emirates_id" => "EmiratesId",
        "qid" => "Qid",
        "civil_id" => "CivilId",
        "id_number" or "national_id" => "IdNumber",
        "passport_number" or "passport" => "PassportNumber",
        _ => null,
    };

    private static void Add(Dictionary<int, MatchAccumulator> acc, Employee e, string matchType, string signal)
    {
        if (!acc.TryGetValue(e.Id, out var m))
        {
            m = new MatchAccumulator(e);
            acc[e.Id] = m;
        }
        // Strong always wins over probable.
        if (matchType == DuplicateMatchTypes.Strong) m.MatchType = DuplicateMatchTypes.Strong;
        if (!m.Signals.Contains(signal)) m.Signals.Add(signal);
    }

    private sealed class MatchAccumulator
    {
        public MatchAccumulator(Employee e) { Employee = e; }
        public Employee Employee { get; }
        public string MatchType { get; set; } = DuplicateMatchTypes.Probable;
        public List<string> Signals { get; } = new();
    }
}

// ── Identity-value normalization (comparison-only; stored columns keep their entered format) ──────────

/// <summary>
/// Canonicalizes national-identity values for MATCHING ONLY. Stored columns keep their entered format
/// (Emirates IDs display with dashes, etc.), so normalization happens at compare time — never mutating
/// the display scalar. <see cref="QueryVariants"/> yields the plain-equality forms a sargable index
/// lookup can use (raw-trimmed plus a digit-folded form for Arabic-Indic entry); <see cref="Canonical"/>
/// is the aggressive form used by the in-memory import matcher.
/// </summary>
public static class IdentityValueNormalizer
{
    /// <summary>Above this many rows sharing one DOB the name+DOB block is no longer "small" — require a
    /// nationality corroborator (M2). Guards both a false-positive flood and a per-row CPU blowup.</summary>
    public const int DobBlockCap = 40;

    /// <summary>The distinct plain-equality forms to point-look-up in the index: the trimmed raw value and,
    /// when different, the Arabic-Indic→Western digit-folded value. Both are sargable `==` forms.</summary>
    public static List<string> QueryVariants(string? value)
    {
        var result = new List<string>();
        var raw = value?.Trim() ?? string.Empty;
        if (raw.Length == 0) return result;
        result.Add(raw);
        var folded = FoldDigits(raw);
        if (!string.Equals(folded, raw, System.StringComparison.Ordinal)) result.Add(folded);
        return result;
    }

    /// <summary>Aggressive canonical form (upper, digit-folded, separators stripped) for the in-memory
    /// import matcher — catches format variance (spaces/dashes/case) that the per-row index lookups do not.</summary>
    public static string Canonical(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var folded = FoldDigits(value.Trim());
        var sb = new StringBuilder(folded.Length);
        foreach (var ch in folded)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToUpperInvariant(ch));
        }
        return sb.ToString();
    }

    /// <summary>GCC data-entry sentinels: blue-collar expat records overwhelmingly carry a placeholder DOB
    /// (1900-01-01, 1970-01-01, any Jan-1). A sentinel DOB must NOT drive the name+DOB probable pass (M2).</summary>
    public static bool IsSentinelDob(DateOnly d) => d.Month == 1 && d.Day == 1;

    private static string FoldDigits(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            // Arabic-Indic (U+0660–0669) and Extended/Persian (U+06F0–06F9) → Western 0-9.
            if (ch >= '٠' && ch <= '٩') sb.Append((char)('0' + (ch - '٠')));
            else if (ch >= '۰' && ch <= '۹') sb.Append((char)('0' + (ch - '۰')));
            else sb.Append(ch);
        }
        return sb.ToString();
    }
}

// ── Name normalization + fuzzy similarity ────────────────────────────────────────────────────────────

/// <summary>
/// Name folding + fuzzy similarity for the PROBABLE (name+DOB) pass. Handles case/whitespace/diacritics
/// and the pervasive GCC transliteration variance (Mohammed/Muhammad/Mohamed) via a vowel-folded
/// consonant skeleton and, for Latin input only, the existing Latin→Arabic transliterator as a phonetic
/// bridge. Token-set aware so reordered name parts still match.
/// </summary>
public static class NameNormalizer
{
    /// <summary>Similarity ≥ this ⇒ a PROBABLE name match (tuned so Mohammed/Muhammad/Mohamed clear it but
    /// genuinely different names do not; combined with an exact-DOB block so the bar is a name given a shared DOB).</summary>
    public const double ProbableThreshold = 0.82;

    /// <summary>Lowercase, strip diacritics (NFD + drop combining marks), keep letters + single spaces.</summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var decomposed = name.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var lastSpace = false;
        foreach (var ch in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue; // dropped diacritic
            if (char.IsLetter(ch)) { sb.Append(ch); lastSpace = false; }
            else if (!lastSpace && sb.Length > 0) { sb.Append(' '); lastSpace = true; }
        }
        return sb.ToString().Trim();
    }

    /// <summary>Order-independent similarity in [0,1]. Max of: token-sorted normalized Levenshtein ratio,
    /// vowel-folded consonant-skeleton ratio, and (Latin input only) an Arabic-transliteration bridge.</summary>
    public static double Similarity(string normA, string normB, ITransliterationService? transliterator = null)
    {
        if (string.IsNullOrWhiteSpace(normA) || string.IsNullOrWhiteSpace(normB)) return 0d;
        if (string.Equals(normA, normB, System.StringComparison.Ordinal)) return 1d;

        var a = TokenSort(normA);
        var b = TokenSort(normB);
        double best = Ratio(a, b);

        var skelA = Skeleton(a);
        var skelB = Skeleton(b);
        if (skelA.Length > 0 && skelB.Length > 0)
        {
            if (string.Equals(skelA, skelB, System.StringComparison.Ordinal)) best = System.Math.Max(best, 0.95d);
            else best = System.Math.Max(best, Ratio(skelA, skelB));
        }

        // Phonetic bridge — Latin ONLY (S5): the transliterator drops non-Latin letters, so Arabic input
        // would collapse to garbage/empty. Compare the Arabic renderings of two Latin names.
        if (transliterator is not null && IsLatin(a) && IsLatin(b))
        {
            var ar = transliterator.ToArabic(a);
            var br = transliterator.ToArabic(b);
            if (ar.Length > 0 && br.Length > 0)
                best = System.Math.Max(best, Ratio(ar, br));
        }

        return best;
    }

    /// <summary>Direct Arabic-name comparison (never routed through the Latin transliterator).</summary>
    public static double ArabicSimilarity(string? a, string? b)
    {
        var na = NormalizeArabic(a);
        var nb = NormalizeArabic(b);
        if (na.Length == 0 || nb.Length == 0) return 0d;
        if (string.Equals(na, nb, System.StringComparison.Ordinal)) return 1d;
        return Ratio(TokenSort(na), TokenSort(nb));
    }

    private static string NormalizeArabic(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var decomposed = name.Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var lastSpace = false;
        foreach (var ch in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue; // drop tashkeel/harakat
            if (char.IsWhiteSpace(ch)) { if (!lastSpace && sb.Length > 0) { sb.Append(' '); lastSpace = true; } }
            else { sb.Append(ch); lastSpace = false; }
        }
        // Normalize common Arabic orthographic variants (alef forms, ta marbuta, alef maqsura).
        return sb.ToString().Trim()
            .Replace('أ', 'ا').Replace('إ', 'ا').Replace('آ', 'ا')
            .Replace('ة', 'ه').Replace('ى', 'ي');
    }

    private static string TokenSort(string norm)
    {
        var tokens = norm.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length <= 1) return norm;
        System.Array.Sort(tokens, System.StringComparer.Ordinal);
        return string.Join(' ', tokens);
    }

    /// <summary>Vowel-folded consonant skeleton: collapse doubled letters then map every vowel to a single
    /// token. Mohammed→mahamad, Muhammad→mahamad, Mohamed→mahamad — the transliteration-variance collapse.</summary>
    private static string Skeleton(string norm)
    {
        var sb = new StringBuilder(norm.Length);
        char prev = '\0';
        foreach (var raw in norm)
        {
            if (raw == ' ') { if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' '); prev = '\0'; continue; }
            var ch = raw is 'e' or 'i' or 'o' or 'u' ? 'a' : raw;
            if (ch == prev) continue;               // collapse doubled letters
            sb.Append(ch);
            prev = ch;
        }
        return sb.ToString().Trim();
    }

    private static bool IsLatin(string s)
    {
        foreach (var ch in s)
            if (char.IsLetter(ch) && ch > 'z') return false;
        return true;
    }

    /// <summary>Normalized Levenshtein similarity ratio in [0,1].</summary>
    private static double Ratio(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1d;
        if (a.Length == 0 || b.Length == 0) return 0d;
        var distance = Levenshtein(a, b);
        var max = System.Math.Max(a.Length, b.Length);
        return 1d - (double)distance / max;
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = System.Math.Min(System.Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}
