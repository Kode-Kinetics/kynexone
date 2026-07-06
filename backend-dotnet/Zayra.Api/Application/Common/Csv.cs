using System.Text;

namespace Zayra.Api.Application.Common;

/// <summary>
/// Minimal, dependency-free CSV writer/reader used for the configurable
/// export / import / shareable-template features across data sections.
/// </summary>
public static class Csv
{
    public static string Build(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<object?>> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", headers.Select(Escape))).Append('\n');
        foreach (var row in rows)
            sb.Append(string.Join(",", row.Select(c => Escape(c?.ToString() ?? string.Empty)))).Append('\n');
        return sb.ToString();
    }

    /// <summary>A blank template: header row only — the shareable "data format".</summary>
    public static string Template(IReadOnlyList<string> headers) =>
        string.Join(",", headers.Select(Escape)) + "\n";

    /// <summary>Parse CSV text into a list of column maps keyed by header name.</summary>
    public static List<Dictionary<string, string>> Parse(string content)
    {
        var rows = new List<Dictionary<string, string>>();
        var lines = SplitLines(content);
        if (lines.Count == 0) return rows;
        var headers = ParseLine(lines[0]);
        for (var i = 1; i < lines.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var cells = ParseLine(lines[i]);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < headers.Count; c++)
                map[headers[c]] = c < cells.Count ? cells[c] : string.Empty;
            rows.Add(map);
        }
        return rows;
    }

    private static List<string> SplitLines(string content) =>
        content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();

    private static List<string> ParseLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuotes)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (ch == '"') inQuotes = false;
                else sb.Append(ch);
            }
            else if (ch == '"') inQuotes = true;
            else if (ch == ',') { result.Add(sb.ToString().Trim()); sb.Clear(); }
            else sb.Append(ch);
        }
        result.Add(sb.ToString().Trim());
        return result;
    }

    // Characters that make a spreadsheet treat a cell as a formula. A cell that begins
    // with any of these is prefixed with a leading apostrophe so Excel/Sheets/LibreOffice
    // render it as literal text instead of evaluating it (CSV / formula injection, aka
    // "CSV Excel macro injection", CWE-1236). Tab and CR are included because some parsers
    // strip a leading one and re-expose the following '='/'+'/etc.
    private static readonly char[] FormulaTriggers = { '=', '+', '-', '@', '\t', '\r' };

    /// <summary>
    /// Escapes a single cell for safe CSV output: neutralizes formula-injection lead
    /// characters, then applies RFC-4180 quoting when the value contains a delimiter,
    /// quote, or newline. Public so alternative writers can reuse the identical rule.
    /// </summary>
    public static string Escape(string s)
    {
        s ??= string.Empty;
        // Neutralize formula injection: a value starting with a trigger char becomes text.
        if (s.Length > 0 && Array.IndexOf(FormulaTriggers, s[0]) >= 0)
            s = "'" + s;
        return s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;
    }
}
