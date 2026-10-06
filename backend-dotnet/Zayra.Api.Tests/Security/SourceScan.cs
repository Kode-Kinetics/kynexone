using System.Text.RegularExpressions;

namespace Zayra.Api.Tests.Security;

/// <summary>Shared source walking for the advisory-lock and raw-Npgsql ratchets.</summary>
internal static partial class SourceScan
{
    internal static string ResolveApiRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir?.Parent is not null; i++)
        {
            dir = dir.Parent;
            var candidate = Path.Combine(dir.FullName, "Zayra.Api");
            if (Directory.Exists(candidate)) return candidate;
        }
        // A guard that skips when it cannot find its input is a false negative, not a safe default.
        throw new InvalidOperationException(
            $"Could not locate the Zayra.Api source root from {AppContext.BaseDirectory}; this ratchet cannot pass without scanning it.");
    }

    /// <summary>(relative path, full text with comments blanked) for every matching file outside bin/obj.</summary>
    internal static IEnumerable<(string Relative, string Code)> Files(string apiRoot, params string[] patterns)
    {
        foreach (var pattern in patterns)
        foreach (var file in Directory.EnumerateFiles(apiRoot, pattern, SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(apiRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith("obj/", StringComparison.Ordinal) || relative.StartsWith("bin/", StringComparison.Ordinal)
                || relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
                continue;
            var text = File.ReadAllText(file);
            yield return (relative, file.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) ? StripSqlComments(text) : StripCSharpComments(text));
        }
    }

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"--[^\n]*")]
    private static partial Regex SqlLineComment();

    /// <summary>
    /// Blanks C# comments (line and block) while leaving string and char literals intact, so a
    /// <c>//</c> inside a URL literal does not hide the code after it. Linear, single pass. Newlines
    /// are kept so line numbers still line up.
    /// </summary>
    internal static string StripCSharpComments(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
            }
            else if (c == '/' && next == '*')
            {
                i += 2;
                while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/'))
                {
                    if (text[i] == '\n') sb.Append('\n');
                    i++;
                }
                i = Math.Min(text.Length, i + 2);
                sb.Append(' ');
            }
            else if (c == '"' && string.CompareOrdinal(text, i, "\"\"\"", 0, 3) == 0)
            {
                var close = text.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                var stop = close < 0 ? text.Length : close + 3;
                sb.Append(text, i, stop - i);
                i = stop;
            }
            else if (c == '@' && next == '"')
            {
                var j = i + 2;
                while (j < text.Length && !(text[j] == '"' && (j + 1 >= text.Length || text[j + 1] != '"')))
                    j += text[j] == '"' ? 2 : 1;
                var stop = Math.Min(text.Length, j + 1);
                sb.Append(text, i, stop - i);
                i = stop;
            }
            else if (c == '"' || c == '\'')
            {
                var j = i + 1;
                while (j < text.Length && text[j] != c && text[j] != '\n')
                    j += text[j] == '\\' ? 2 : 1;
                var stop = Math.Min(text.Length, j + 1);
                sb.Append(text, i, stop - i);
                i = stop;
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }
        return sb.ToString();
    }

    internal static string StripSqlComments(string text) =>
        SqlLineComment().Replace(BlockComment().Replace(text, " "), string.Empty);

    // ── Literal-aware lexing ──────────────────────────────────────────────────

    /// <summary>Index just past the C# string/char literal starting at <paramref name="i"/>, or -1.</summary>
    internal static int SkipLiteral(string s, int i)
    {
        var j = i;
        while (j < s.Length && (s[j] == '$' || s[j] == '@')) j++;
        if (j >= s.Length) return -1;
        if (s[j] == '\'' && j == i)
        {
            var k = j + 1;
            while (k < s.Length && s[k] != '\'') k += s[k] == '\\' ? 2 : 1;
            return Math.Min(s.Length, k + 1);
        }
        if (s[j] != '"') return -1;
        var verbatim = s.AsSpan(i, j - i).Contains('@');
        var interpolated = s.AsSpan(i, j - i).Contains('$');
        var quotes = 0;
        while (j + quotes < s.Length && s[j + quotes] == '"') quotes++;
        if (quotes >= 3)
        {
            var close = s.IndexOf(new string('"', quotes), j + quotes, StringComparison.Ordinal);
            return close < 0 ? s.Length : close + quotes;
        }
        var p = j + 1;
        while (p < s.Length)
        {
            if (verbatim && s[p] == '"' && p + 1 < s.Length && s[p + 1] == '"') { p += 2; continue; }
            if (!verbatim && s[p] == '\\') { p += 2; continue; }
            if (interpolated && s[p] == '{')
            {
                if (p + 1 < s.Length && s[p + 1] == '{') { p += 2; continue; }
                p = InterpolationHoleEnd(s, p) + 1;   // a hole may hold literals with their own quotes
                continue;
            }
            if (s[p] == '"') return p + 1;
            p++;
        }
        return s.Length;
    }

    private static int InterpolationHoleEnd(string s, int open)
    {
        var depth = 0;
        for (var i = open; i < s.Length;)
        {
            var end = SkipLiteral(s, i);
            if (end > i) { i = end; continue; }
            if (s[i] == '{') depth++;
            else if (s[i] == '}' && --depth == 0) return i;
            i++;
        }
        return s.Length - 1;
    }

    /// <summary>
    /// The literals blanked to <c>""</c>, except that the code inside an interpolated literal's holes
    /// is kept (as <c>"" hole ""</c>), so <c>$"{user.Email}"</c> still exposes <c>user.Email</c>.
    /// </summary>
    internal static string BlankLiteralsKeepHoles(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        for (var i = 0; i < s.Length;)
        {
            var end = SkipLiteral(s, i);
            if (end <= i) { sb.Append(s[i++]); continue; }
            sb.Append("\"\"");
            var literal = s[i..end];
            var prefix = literal.IndexOf('"');
            if (literal.AsSpan(0, prefix).Contains('$'))
            {
                for (var p = prefix; p < literal.Length; p++)
                {
                    if (literal[p] != '{') continue;
                    if (p + 1 < literal.Length && literal[p + 1] == '{') { p++; continue; }
                    var close = InterpolationHoleEnd(literal, p);
                    sb.Append(' ').Append(BlankLiteralsKeepHoles(literal[(p + 1)..close])).Append(' ');
                    p = close;
                }
            }
            i = end;
        }
        return sb.ToString();
    }

    /// <summary>The comma-separated arguments of an argument list, split at depth zero, literals intact.</summary>
    internal static List<string> SplitTopLevel(string args)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < args.Length;)
        {
            var end = SkipLiteral(args, i);
            if (end > i) { i = end; continue; }
            var c = args[i];
            if (c is '(' or '{' or '[') depth++;
            else if (c is ')' or '}' or ']') depth--;
            else if (c == ',' && depth == 0) { parts.Add(args[start..i].Trim()); start = i + 1; }
            i++;
        }
        if (args[start..].Trim().Length > 0) parts.Add(args[start..].Trim());
        return parts;
    }

    internal static string BlankLiterals(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        for (var i = 0; i < s.Length;)
        {
            var end = SkipLiteral(s, i);
            if (end > i) { sb.Append("\"\""); i = end; }
            else sb.Append(s[i++]);
        }
        return sb.ToString();
    }

    /// <summary>Index of the bracket closing the one at <paramref name="open"/>, skipping literals.</summary>
    internal static int MatchingClose(string s, int open, char o, char c)
    {
        var depth = 0;
        for (var i = open; i < s.Length;)
        {
            var end = SkipLiteral(s, i);
            if (end > i) { i = end; continue; }
            if (s[i] == o) depth++;
            else if (s[i] == c && --depth == 0) return i;
            i++;
        }
        return s.Length - 1;
    }

    internal static int NextTopLevel(string s, int from, char target)
    {
        var depth = 0;
        for (var i = from; i < s.Length;)
        {
            var end = SkipLiteral(s, i);
            if (end > i) { i = end; continue; }
            if (s[i] is '(' or '{' or '[') depth++;
            else if (s[i] is ')' or '}' or ']') depth--;
            else if (s[i] == target && depth == 0) return i;
            i++;
        }
        return s.Length - 1;
    }

    internal static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;
}
