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

    internal static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;
}
