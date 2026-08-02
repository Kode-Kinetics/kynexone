using Zayra.Api.Application.Localization;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// One person the batch matcher knows about — either an existing DB employee (EmployeeId set) or an
/// earlier row in THIS import file (BatchKey set for back-flagging). Identity values are pre-canonicalized
/// per column so lookups are O(1) and format-variance-safe (dashes/spaces/case/Arabic-Indic digits folded).
/// </summary>
public sealed class DuplicateCandidate
{
    public int? EmployeeId { get; init; }
    /// <summary>Import-file row key (the row's final EmployeeCode) — set for in-batch rows so the earlier
    /// member of a matched pair can be back-flagged (S2). Null for existing DB rows.</summary>
    public string? BatchKey { get; init; }
    public string EmployeeCode { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;
    public string? ArabicName { get; init; }
    public string NameNorm { get; init; } = string.Empty;
    public System.Guid? CompanyId { get; init; }
    public string? Nationality { get; init; }
    public DateOnly? DateOfBirth { get; init; }
    /// <summary>column ("IqamaNumber"…"PassportNumber") → canonical identity value.</summary>
    public IReadOnlyDictionary<string, string> IdentityCanonical { get; init; }
        = new Dictionary<string, string>();
}

/// <summary>A batch-path duplicate hit: the counterpart plus the tier and human signals.</summary>
public sealed record DuplicateBatchMatch(string MatchType, DuplicateCandidate Counterpart, IReadOnlyList<string> Signals);

/// <summary>Builds a <see cref="DuplicateCandidate"/> (canonicalized identity map + normalized name) from an
/// employee's fields — used to seed the matcher with existing DB rows and to probe each import row.</summary>
public static class DuplicateCandidateBuilder
{
    public static DuplicateCandidate Build(
        int? employeeId, string? batchKey, string employeeCode, string? fullName, string? englishName,
        string? arabicName, System.Guid? companyId, string? nationality, DateOnly? dateOfBirth,
        string? iqama, string? emiratesId, string? qid, string? civilId, string? idNumber, string? passport)
    {
        var identity = new Dictionary<string, string>(System.StringComparer.Ordinal);
        void Add(string col, string? val) { var c = IdentityValueNormalizer.Canonical(val); if (c.Length > 0) identity[col] = c; }
        Add("IqamaNumber", iqama);
        Add("EmiratesId", emiratesId);
        Add("Qid", qid);
        Add("CivilId", civilId);
        Add("IdNumber", idNumber);
        Add("PassportNumber", passport);
        var displayName = !string.IsNullOrWhiteSpace(fullName) ? fullName : englishName;
        return new DuplicateCandidate
        {
            EmployeeId = employeeId,
            BatchKey = batchKey,
            EmployeeCode = employeeCode ?? string.Empty,
            FullName = displayName ?? string.Empty,
            ArabicName = arabicName,
            NameNorm = NameNormalizer.Normalize(displayName),
            CompanyId = companyId,
            Nationality = nationality,
            DateOfBirth = dateOfBirth,
            IdentityCanonical = identity,
        };
    }

    public static DuplicateCandidate FromEmployee(Employee e, int? employeeId, string? batchKey) => Build(
        employeeId, batchKey, e.EmployeeCode, e.FullName, e.EnglishName, e.ArabicName, e.CompanyId,
        e.Nationality, e.DateOfBirth, e.IqamaNumber, e.EmiratesId, e.Qid, e.CivilId, e.IdNumber, e.PassportNumber);
}

/// <summary>
/// Import-path duplicate detection using PRELOADED dictionaries (N1) — never a DB query per row. Existing
/// employees are loaded once (like the existing-code set); each imported row is matched against the
/// dictionaries and then <see cref="Register"/>ed so later rows see it (and the earlier member of a pair is
/// back-flagged by the caller). Same strong/probable rules as <see cref="EmployeeDuplicateDetector"/>:
/// national ID = strong; passport = probable alone, strong when corroborated; name+DOB = probable, with the
/// sentinel-DOB skip + DOB-block cap.
/// </summary>
public sealed class EmployeeDuplicateMatcher
{
    private static readonly string[] NationalColumns = ["IqamaNumber", "EmiratesId", "Qid", "CivilId", "IdNumber"];

    private readonly ITransliterationService? _transliterator;
    private readonly Dictionary<string, Dictionary<string, List<DuplicateCandidate>>> _byNational = new();
    private readonly Dictionary<string, List<DuplicateCandidate>> _byPassport = new(System.StringComparer.Ordinal);
    private readonly Dictionary<DateOnly, List<DuplicateCandidate>> _byDob = new();

    public EmployeeDuplicateMatcher(ITransliterationService? transliterator = null)
    {
        _transliterator = transliterator;
        foreach (var col in NationalColumns) _byNational[col] = new(System.StringComparer.Ordinal);
    }

    public void Register(DuplicateCandidate c)
    {
        foreach (var col in NationalColumns)
            if (c.IdentityCanonical.TryGetValue(col, out var v) && v.Length > 0)
                Bucket(_byNational[col], v).Add(c);
        if (c.IdentityCanonical.TryGetValue("PassportNumber", out var p) && p.Length > 0)
            Bucket(_byPassport, p).Add(c);
        if (c.DateOfBirth is DateOnly d && !IdentityValueNormalizer.IsSentinelDob(d))
            Bucket(_byDob, d).Add(c);
    }

    /// <summary>All distinct counterparts (existing or earlier-batch) the probe may duplicate, strongest first.</summary>
    public IReadOnlyList<DuplicateBatchMatch> Match(DuplicateCandidate probe)
    {
        var acc = new Dictionary<string, (string Type, DuplicateCandidate C, List<string> Sig)>();

        void Hit(DuplicateCandidate c, string type, string signal)
        {
            var key = c.EmployeeId is int id ? $"e{id}" : $"b{c.BatchKey}";
            if (!acc.TryGetValue(key, out var cur)) { acc[key] = (type, c, new List<string> { signal }); return; }
            var newType = type == DuplicateMatchTypes.Strong ? DuplicateMatchTypes.Strong : cur.Type;
            if (!cur.Sig.Contains(signal)) cur.Sig.Add(signal);
            acc[key] = (newType, cur.C, cur.Sig);
        }

        // National IDs → strong.
        foreach (var col in NationalColumns)
            if (probe.IdentityCanonical.TryGetValue(col, out var v) && v.Length > 0
                && _byNational[col].TryGetValue(v, out var list))
                foreach (var c in list) Hit(c, DuplicateMatchTypes.Strong, $"{Label(col)} match");

        // Passport → probable alone; strong when name or DOB agrees (M4).
        if (probe.IdentityCanonical.TryGetValue("PassportNumber", out var pv) && pv.Length > 0
            && _byPassport.TryGetValue(pv, out var plist))
            foreach (var c in plist)
            {
                var corroborated = (probe.DateOfBirth is DateOnly pd && c.DateOfBirth == pd && !IdentityValueNormalizer.IsSentinelDob(pd))
                                   || NameMatches(probe, c);
                Hit(c, corroborated ? DuplicateMatchTypes.Strong : DuplicateMatchTypes.Probable,
                    corroborated ? "Passport number match corroborated by name/DOB" : "Passport number match (verify — passports are reused)");
            }

        // Name + DOB → probable (DOB-blocked; sentinel skipped at Register; cap guards a common-DOB flood).
        if (probe.DateOfBirth is DateOnly d && !IdentityValueNormalizer.IsSentinelDob(d)
            && _byDob.TryGetValue(d, out var dobList))
        {
            var overCap = dobList.Count > IdentityValueNormalizer.DobBlockCap;
            foreach (var c in dobList)
            {
                if (overCap && (string.IsNullOrWhiteSpace(probe.Nationality)
                    || !string.Equals(c.Nationality?.Trim(), probe.Nationality.Trim(), System.StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (NameMatches(probe, c)) Hit(c, DuplicateMatchTypes.Probable, "Same date of birth and a matching name");
            }
        }

        return acc.Values
            .Select(v => new DuplicateBatchMatch(v.Type, v.C, v.Sig))
            .OrderByDescending(m => m.MatchType == DuplicateMatchTypes.Strong)
            .ToList();
    }

    private bool NameMatches(DuplicateCandidate a, DuplicateCandidate b)
    {
        if (!string.IsNullOrWhiteSpace(a.NameNorm) && !string.IsNullOrWhiteSpace(b.NameNorm)
            && NameNormalizer.Similarity(a.NameNorm, b.NameNorm, _transliterator) >= NameNormalizer.ProbableThreshold)
            return true;
        if (!string.IsNullOrWhiteSpace(a.ArabicName) && !string.IsNullOrWhiteSpace(b.ArabicName)
            && NameNormalizer.ArabicSimilarity(a.ArabicName, b.ArabicName) >= NameNormalizer.ProbableThreshold)
            return true;
        return false;
    }

    private static string Label(string column) => column switch
    {
        "IqamaNumber" => "Iqama number",
        "EmiratesId" => "Emirates ID",
        "Qid" => "Qatar ID (QID)",
        "CivilId" => "Civil ID",
        "IdNumber" => "National ID number",
        _ => "Identity",
    };

    private static List<DuplicateCandidate> Bucket<TKey>(Dictionary<TKey, List<DuplicateCandidate>> map, TKey key) where TKey : notnull
    {
        if (!map.TryGetValue(key, out var list)) { list = new List<DuplicateCandidate>(); map[key] = list; }
        return list;
    }
}
