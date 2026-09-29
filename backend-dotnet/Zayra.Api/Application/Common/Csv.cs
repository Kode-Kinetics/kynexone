using System.Text;

namespace Zayra.Api.Application.Common;

/// <summary>
/// A data row whose cell count does not match the header's — the silent column shift, refused.
///
/// <para>It is an <see cref="InvalidOperationException"/> so that every import endpoint's existing
/// "a bad file is a 422 naming the problem, not a 500" handling catches it unchanged.</para>
/// </summary>
public sealed class CsvShapeException : InvalidOperationException
{
    public CsvShapeException(int rowNumber, int cellCount, int headerCount)
        : base($"CSV row {rowNumber} has {cellCount} cell(s) but the header declares {headerCount} column(s). "
             + "CSV rows are POSITIONAL, so a row of the wrong width shifts every value after the break into "
             + "the wrong column and the file is imported as nonsense rather than refused. The usual cause is "
             + "an unquoted thousands separator — write 25000 or \"25,000\", never 25,000 — or a stray comma "
             + "inside an unquoted name or note. Nothing in this file has been imported.")
    {
        RowNumber = rowNumber;
        CellCount = cellCount;
        HeaderCount = headerCount;
    }

    /// <summary>1-based line number in the file, counting the header as line 1.</summary>
    public int RowNumber { get; }
    public int CellCount { get; }
    public int HeaderCount { get; }
}

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

    /// <summary>
    /// A template carrying the header row plus ONE worked example row.
    ///
    /// The example row is written through <see cref="Build"/>, so it inherits exactly the same
    /// formula-injection neutralisation and RFC-4180 quoting the export path applies — a template
    /// can never be the one CSV surface that forgets to escape a cell.
    ///
    /// CSV rows are POSITIONAL. An example row with the wrong number of cells shifts every value
    /// into the wrong column and there is nothing in the file that says so; a spreadsheet renders
    /// the corrupted row as happily as a correct one. That silent failure is the whole reason this
    /// overload exists, so the count mismatch is refused here instead of being emitted — and
    /// <c>ImportTemplateShapeTests</c> re-checks it for every template endpoint in the build.
    /// </summary>
    public static string Template(IReadOnlyList<string> headers, IReadOnlyList<string> exampleRow)
    {
        if (exampleRow.Count != headers.Count)
            throw new ArgumentException(
                $"CSV template example row has {exampleRow.Count} cell(s) but the header declares "
                + $"{headers.Count} column(s). CSV rows are positional, so a mismatched example row "
                + "silently shifts every value into the wrong column.",
                nameof(exampleRow));
        return Build(headers, new[] { (IReadOnlyList<object?>)exampleRow });
    }

    /// <summary>
    /// Parse CSV text into a list of column maps keyed by header name.
    ///
    /// <para>A data row whose cell count differs from the header's is REFUSED by row number and by both
    /// counts, before any row is returned — so nothing downstream is written from a shifted file. CSV
    /// rows are positional: one unquoted thousands separator (<c>25,000</c>) splits one cell into two
    /// and every later value in that row lands in the WRONG FIELD. Surplus cells used to be discarded
    /// and missing ones silently filled with the empty string, which is how an amount is read as a date
    /// and a date as a currency, on any sheet in the product. The write side has refused a mismatched
    /// example row ever since the two-argument <see cref="Template(IReadOnlyList{string},
    /// IReadOnlyList{string})"/> overload shipped (<c>:39-44</c>); this is that same rule, applied to
    /// the read side, where the customer's own file arrives.</para>
    ///
    /// <para>Blank lines are still skipped — a trailing newline is not a row. A row with genuinely empty
    /// trailing columns must still spell them with commas, which is what every CSV writer emits,
    /// <see cref="Build"/> included.</para>
    /// </summary>
    /// <exception cref="CsvShapeException">A data row's cell count differs from the header's.</exception>
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
            if (cells.Count != headers.Count)
                throw new CsvShapeException(i + 1, cells.Count, headers.Count);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < headers.Count; c++)
                map[headers[c]] = c < cells.Count ? cells[c] : string.Empty;
            rows.Add(map);
        }
        return rows;
    }

    /// <summary>
    /// Splits ONE physical CSV line into its cells, honouring RFC-4180 quoting. Public so a caller
    /// can count a row's columns with precisely the splitter the importer uses, rather than a
    /// naive <c>Split(',')</c> that would miscount any quoted cell containing a comma.
    /// </summary>
    public static IReadOnlyList<string> SplitRow(string line) => ParseLine(line);

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
