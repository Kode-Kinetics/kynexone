using System.Text;
using Zayra.Api.Application.Common;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// Validates an employee-import CSV's HEADER ROW against <see cref="EmployeeFieldRegistry.CsvHeaders"/>.
///
/// The registry's own header comment claimed the importer read that list. It did not: <c>CsvHeaders</c> was
/// referenced only by the export header and the downloadable template, while <c>Import</c> and
/// <c>ImportPreview</c> called <c>Csv.Parse</c> and then pulled cells by hard-coded literal string. 92
/// columns, zero checks. A customer extract with <c>Mobile</c> where the template says <c>Phone</c>, or
/// <c>Iqama No.</c> where it says <c>IqamaNumber</c>, imported with no error, no warning and no count — the
/// column simply never arrived, and nothing in the response said so. That is the amplifier under every
/// silent-drop defect in this module: a misspelled column is indistinguishable from an empty one.
///
/// The check runs on the header row ALONE, before a single data row is read, so a file with a bad column is
/// refused whole rather than half-imported. It does NOT require completeness: a partial file carrying six of
/// the 92 columns is legitimate and stays legitimate — only a column that is *present and unrecognised*, or
/// *present twice*, is an error, because both of those lose data that the operator believes they supplied.
/// </summary>
public static class EmployeeCsvHeaderValidator
{
    /// <summary>One rejected header column: what was in the file, the closest legitimate column (null when
    /// nothing is close enough to be worth guessing), and the operator-facing sentence.</summary>
    public sealed record HeaderProblem(string Column, string? Suggestion, string Message);

    /// <summary>
    /// Inspects the first physical line of <paramref name="csvContent"/> and returns every header problem.
    /// An empty list means the file's columns are safe to read. Empty content yields no problems — a file
    /// with no rows imports nothing, which is already reported as <c>received = 0</c>.
    /// </summary>
    public static IReadOnlyList<HeaderProblem> Validate(string? csvContent)
    {
        var problems = new List<HeaderProblem>();
        if (string.IsNullOrWhiteSpace(csvContent)) return problems;

        var firstLine = csvContent.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')[0];
        if (string.IsNullOrWhiteSpace(firstLine)) return problems;

        // Split with the importer's OWN RFC-4180 splitter, never Split(','), so a quoted header containing a
        // comma is counted exactly as Csv.Parse will count it.
        var headers = Csv.SplitRow(firstLine);
        var valid = EmployeeFieldRegistry.CsvHeaders;
        var validSet = new HashSet<string>(valid, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in headers)
        {
            var header = raw.Trim();
            // A wholly blank cell is a trailing comma or an exported spacer column, not a mistyped name.
            // It cannot collide with a real column and carries no intent, so it is not an operator error.
            if (header.Length == 0) continue;

            if (!validSet.Contains(header))
            {
                var suggestion = NearestHeader(header, valid);
                problems.Add(new HeaderProblem(
                    header,
                    suggestion,
                    suggestion is null
                        ? $"Column '{header}' is not an employee import column, and no valid column is close to it. "
                          + "Download the import template to see the accepted column names."
                        : $"Column '{header}' is not an employee import column. Did you mean '{suggestion}'?"));
                continue;
            }

            // A repeated column is worse than an unknown one: Csv.Parse keys its row map by header name, so
            // the LAST occurrence silently wins and every value under the earlier one is discarded.
            if (!seen.Add(header))
                problems.Add(new HeaderProblem(
                    header, header,
                    $"Column '{header}' appears more than once. Only the last copy would be read and the "
                    + "other values would be discarded — remove the duplicate column."));
        }

        return problems;
    }

    /// <summary>
    /// The legitimate column a mistyped one most likely meant, or null when nothing is close enough that a
    /// guess would help more than it misleads. Containment wins first (<c>Mobile</c> → <c>MobileAllowance</c>),
    /// then edit distance within a length-scaled budget (<c>Iqama No.</c> → <c>IqamaNumber</c>).
    /// </summary>
    public static string? NearestHeader(string header, IReadOnlyList<string> valid)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        var needle = header.Trim();

        // Containment: shortest containing column wins, so "Mobile" prefers "MobileAllowance" over a
        // coincidentally-similar unrelated column.
        string? contained = null;
        foreach (var v in valid)
        {
            if (v.Length < needle.Length) continue;
            if (v.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (contained is null || v.Length < contained.Length) contained = v;
        }
        if (contained is not null) return contained;

        // Everything below compares only letters and digits, so punctuation and spacing ("Iqama No." vs
        // "IqamaNumber", "Employee Code" vs "EmployeeCode") do not dominate the score.
        var needleKey = Fold(needle);
        if (needleKey.Length == 0) return null;

        // Shared prefix: the common way a column is abbreviated or truncated by an upstream system
        // ("Iqama No." → "IqamaNumber"). Requires a substantial prefix in absolute AND relative terms, so it
        // cannot fire on two columns that merely start with the same word fragment.
        string? byPrefix = null;
        var bestPrefix = 0;
        foreach (var v in valid)
        {
            var p = CommonPrefix(needleKey, Fold(v));
            if (p < 5 || p * 10 < Math.Min(needleKey.Length, Fold(v).Length) * 7) continue;
            if (p > bestPrefix) { bestPrefix = p; byPrefix = v; }
        }
        if (byPrefix is not null) return byPrefix;

        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var v in valid)
        {
            var d = Distance(needleKey, Fold(v));
            if (d < bestDistance) { bestDistance = d; best = v; }
        }

        // Budget: a third of the column's length, at least 2. Beyond that the "did you mean" is noise and
        // the operator is better served by the template than by a wrong guess. The column is rejected and
        // named either way — the suggestion only decides whether we also hazard a guess.
        var budget = Math.Max(2, needleKey.Length / 3);
        return bestDistance <= budget ? best : null;
    }

    private static int CommonPrefix(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < n && a[i] == b[i]) i++;
        return i;
    }

    private static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    /// <summary>Levenshtein distance over two rolling rows (O(min) memory).</summary>
    private static int Distance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), substitution);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}
