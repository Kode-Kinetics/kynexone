using System.Text.RegularExpressions;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// Cost-centre accounting v1 (contract 1.1, 2.9): the master-data format rules, enforced in code so the database CHECKs
/// that mirror them (added NOT VALID in M1, validated in M3) can later be validated against data that already obeys them.
/// A code or ERP value is printed on journal exports, so a leading <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, a space or a
/// control character is refused here rather than escaped later.
/// </summary>
public static class CostCentreCode
{
    public const int MaxLength = 30;
    // \z, not $: "$" would let a trailing newline through.
    private static readonly Regex Format = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,29}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The same expression as the <c>cost_centers.code</c> CHECK.</summary>
    public static bool IsValid(string? code) => code is not null && Format.IsMatch(code);

    /// <summary>The same expression as the <c>cost_centers.gl_segment</c> CHECK.</summary>
    public static bool IsValidGlSegment(string? segment) => IsValid(segment);

    /// <summary>
    /// Makes a proposed code (an AI draft, a template) safe: upper case, anything outside letters, digits, dot, dash and underscore becomes an
    /// underscore, runs of underscores collapse, leading non-alphanumerics and trailing underscores go, and it is cut to 30 characters.
    /// When nothing usable remains the <paramref name="fallback"/> (a name) is sanitised the same way.
    /// </summary>
    public static string Sanitise(string? raw, string fallback)
    {
        var code = Clean(raw);
        if (code.Length == 0) code = Clean(fallback);
        return code.Length == 0 ? "CC" : code;
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var chars = new System.Text.StringBuilder();
        foreach (var ch in value.Trim().ToUpperInvariant())
        {
            var c = ch is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '-' or '_' ? ch : '_';
            if (c == '_' && chars.Length > 0 && chars[^1] == '_') continue;
            chars.Append(c);
        }
        var text = chars.ToString().TrimStart(c => !char.IsAsciiLetterOrDigit(c)).TrimEnd('_');
        return text.Length > MaxLength ? text[..MaxLength].TrimEnd('_', '.', '-') : text;
    }

    private static string TrimStart(this string s, Func<char, bool> drop)
    {
        var i = 0;
        while (i < s.Length && drop(s[i])) i++;
        return s[i..];
    }

    public const string Rule = "A cost centre code starts with a letter or digit and then uses only letters, digits, dot, dash or underscore, up to 30 characters.";
}

/// <summary>The functional classes of a cost centre (contract 1.1). CAPITALISABLE is reserved: storable, refused for posting in v1.</summary>
public static class CostCentreFunctionalClasses
{
    public const string Reserved = "CAPITALISABLE";
    public static readonly IReadOnlyList<string> All = ["COGS", "SELLING", "G_AND_A", "R_AND_D", "OPERATIONS", Reserved];
    public static bool IsKnown(string? value) => value is not null && All.Contains(value, StringComparer.Ordinal);
}

/// <summary>The parent tree of cost centres (contract 1.1, 1.11 c): no self-parent, no cycle, at most ten levels.</summary>
public static class CostCentreHierarchy
{
    public const int MaxDepth = 10;

    /// <summary>
    /// Whether <paramref name="id"/> may take <paramref name="newParentId"/> as its parent, given the current parent of every
    /// cost centre in the tenant. Returns null when it may, else the refusal code. The depth check counts the levels above
    /// the new parent AND the height of the subtree that moves with <paramref name="id"/>.
    /// </summary>
    public static string? Check(Guid id, Guid? newParentId, IReadOnlyDictionary<Guid, Guid?> parentOf)
    {
        if (newParentId is not Guid parent) return null;
        if (parent == id) return "cost_centre_parent_self";

        var above = 0;
        var seen = new HashSet<Guid>();
        Guid? cursor = parent;
        while (cursor is Guid c)
        {
            if (c == id) return "cost_centre_parent_cycle";
            if (!seen.Add(c)) return "cost_centre_parent_cycle"; // a pre-existing loop above the new parent
            above++;
            cursor = parentOf.TryGetValue(c, out var next) ? next : null;
        }

        var children = parentOf.Where(kv => kv.Value is not null && kv.Key != id)
            .GroupBy(kv => kv.Value!.Value).ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToList());
        var height = Height(id, children, new HashSet<Guid>());
        return above + height > MaxDepth ? "cost_centre_parent_too_deep" : null;
    }

    private static int Height(Guid node, Dictionary<Guid, List<Guid>> children, HashSet<Guid> visiting)
    {
        if (!visiting.Add(node) || !children.TryGetValue(node, out var kids)) { visiting.Remove(node); return 1; }
        var tallest = 0;
        foreach (var kid in kids) tallest = Math.Max(tallest, Height(kid, children, visiting));
        visiting.Remove(node);
        return 1 + tallest;
    }
}
