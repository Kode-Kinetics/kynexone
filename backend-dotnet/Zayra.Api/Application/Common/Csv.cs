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
        : this(headerCount, new[] { new CsvShapeMismatch(rowNumber, cellCount) })
    {
    }

    /// <summary>Every mis-shaped row of the file, in file order (at least one).</summary>
    public CsvShapeException(int headerCount, IReadOnlyList<CsvShapeMismatch> mismatches)
        : base(BuildMessage(headerCount, mismatches))
    {
        if (mismatches.Count == 0) throw new ArgumentException("At least one mismatched row is required.", nameof(mismatches));
        RowNumber = mismatches[0].RowNumber;
        CellCount = mismatches[0].CellCount;
        HeaderCount = headerCount;
        Mismatches = mismatches;
    }

    /// <summary>1-based line number in the file, counting the header as line 1 (the FIRST bad row).</summary>
    public int RowNumber { get; }
    public int CellCount { get; }
    public int HeaderCount { get; }

    /// <summary>Every row whose width differs from the header's, so a file with the same mistake on forty
    /// salary cells is corrected in one pass rather than forty uploads.</summary>
    public IReadOnlyList<CsvShapeMismatch> Mismatches { get; }

    private static string BuildMessage(int headerCount, IReadOnlyList<CsvShapeMismatch> mismatches)
    {
        var first = mismatches.Count > 0 ? mismatches[0] : new CsvShapeMismatch(0, 0);
        if (first.UnterminatedQuote)
            return $"CSV row {first.RowNumber} has a line break inside a quoted cell (a multi-line address or note). "
                 + "The importer reads exactly one row per line, so the rest of that cell would be read as a new, "
                 + "shifted row. Remove the line breaks inside that cell (replace them with a space or a comma) and "
                 + "import again."
                 + (mismatches.Count > 1 ? $" {mismatches.Count - 1} other row(s) of the file are also mis-shaped." : string.Empty)
                 + " Nothing in this file has been imported.";
        var others = mismatches.Skip(1).Take(10).Select(m => $"row {m.RowNumber} ({m.CellCount})").ToList();
        var more = mismatches.Count - 1 > others.Count ? $" and {mismatches.Count - 1 - others.Count} more" : string.Empty;
        return $"CSV row {first.RowNumber} has {first.CellCount} cell(s) but the header declares {headerCount} column(s). "
             + (others.Count > 0 ? $"Also wrong: {string.Join(", ", others)}{more}. " : string.Empty)
             + "CSV rows are POSITIONAL, so a row of the wrong width shifts every value after the break into "
             + "the wrong column and the file is imported as nonsense rather than refused. The usual cause is "
             + "an unquoted thousands separator — write 25000 or \"25,000\", never 25,000 — or a stray comma "
             + "inside an unquoted name or note. Nothing in this file has been imported.";
    }
}

/// <summary>One data row whose cell count differs from the header's.</summary>
/// <param name="RowNumber">1-based line number in the file, counting the header as line 1.</param>
/// <param name="CellCount">How many cells the row actually has.</param>
/// <param name="UnterminatedQuote">The line ends inside a quoted cell: the cell carried a line break, which this
/// one-row-per-line reader cannot take (the cell count is then meaningless, so the message says so instead).</param>
public sealed record CsvShapeMismatch(int RowNumber, int CellCount, bool UnterminatedQuote = false);

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
        List<CsvShapeMismatch>? mismatches = null;
        for (var i = 1; i < lines.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var cells = ParseLine(lines[i]);
            var unterminated = EndsInsideQuotes(lines[i]);
            if (cells.Count != headers.Count || unterminated)
            {
                // Keep reading: every bad row is named at once, and none of the file is returned. A line that ends
                // inside a quoted cell is the first half of a multi-line cell; its continuation lines are skipped
                // with it rather than each reported as a separate shifted row.
                (mismatches ??= new List<CsvShapeMismatch>()).Add(new CsvShapeMismatch(i + 1, cells.Count, unterminated));
                if (unterminated)
                    while (i + 1 < lines.Count && !EndsInsideQuotes(lines[i + 1])) i++;
                if (unterminated && i + 1 < lines.Count) i++; // the line that closes the quote
                continue;
            }
            if (mismatches is not null) continue;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < headers.Count; c++)
                map[headers[c]] = c < cells.Count ? cells[c] : string.Empty;
            rows.Add(map);
        }
        if (mismatches is not null) throw new CsvShapeException(headers.Count, mismatches);
        return rows;
    }

    /// <summary>
    /// Splits ONE physical CSV line into its cells, honouring RFC-4180 quoting. Public so a caller
    /// can count a row's columns with precisely the splitter the importer uses, rather than a
    /// naive <c>Split(',')</c> that would miscount any quoted cell containing a comma.
    /// </summary>
    public static IReadOnlyList<string> SplitRow(string line) => ParseLine(line);

    /// <summary>
    /// U+FEFF, the UTF-8 byte-order mark, as the decoder leaves it at the start of the text.
    ///
    /// <para>It is Unicode category Cf (format), NOT whitespace, so <see cref="string.Trim()"/> does not
    /// remove it on .NET Core — the first header of a BOM'd file arrives as "﻿EmployeeCode", matches
    /// nothing, and the header validator's near-match helper then folds the BOM away and produces the
    /// nonsense sentence "Column 'EmployeeCode' is not an employee import column. Did you mean
    /// 'EmployeeCode'?". "Save as CSV UTF-8" in Excel is the ordinary way an HR user produces a file, so
    /// the BOM is stripped here, once, on the way in.</para>
    /// </summary>
    private const char ByteOrderMark = '﻿';

    private static List<string> SplitLines(string content)
    {
        if (content.Length > 0 && content[0] == ByteOrderMark) content = content[1..];
        return content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
    }

    /// <summary>True when the line ends with a quoted cell still open (an odd number of quote characters).</summary>
    private static bool EndsInsideQuotes(string line) => line.Count(c => c == '"') % 2 == 1;

    private static List<string> ParseLine(string line)
    {
        // Also stripped per-line, not only per-file, because SplitRow is public: the employee header
        // validator slices the first physical line off the raw content itself and hands it here.
        if (line.Length > 0 && line[0] == ByteOrderMark) line = line[1..];

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
            else if (ch == ',') { result.Add(Unescape(sb.ToString().Trim())); sb.Clear(); }
            else sb.Append(ch);
        }
        result.Add(Unescape(sb.ToString().Trim()));
        return result;
    }

    /// <summary>
    /// The exact inverse of the formula-injection guard in <see cref="Escape"/>: removes the apostrophe
    /// <see cref="Escape"/> prefixes, and nothing else.
    ///
    /// <para>The guard is only ever applied when the value's first character is a
    /// <see cref="FormulaTriggers">trigger</see>, so the apostrophe is removed only when the character
    /// AFTER it is one of those same triggers. That conditional strip is deliberate, and the naive
    /// unconditional one is wrong: a value that genuinely begins with an apostrophe — an Arabic
    /// transliteration such as <c>'Abdullah</c> or <c>'Aisha</c>, a quoted nickname, a note opening with
    /// a quote mark — was never escaped on the way out and must survive the way in. (A name like
    /// <c>O'Brien</c> is unaffected either way; its apostrophe is not leading.) Restricting the strip to
    /// the trigger characters makes Escape and Parse exact inverses over every value the exporter can
    /// emit while leaving every other leading apostrophe untouched.</para>
    ///
    /// <para>The one value this cannot distinguish is a cell that genuinely starts with an apostrophe
    /// followed by a trigger (<c>'=total</c>). That ambiguity is inherent to the guard — the escaped and
    /// the literal form are byte-identical — and resolving it in favour of the exporter's own output is
    /// what makes the product's export survive the product's importer, which is the case that occurs in
    /// practice (<c>+966…</c> phone numbers, <c>-500</c> deductions).</para>
    ///
    /// <para>Exactly ONE apostrophe is removed, so a re-exported value is not eroded a character per
    /// round trip.</para>
    /// </summary>
    private static string Unescape(string cell) =>
        cell.Length >= 2 && cell[0] == '\'' && Array.IndexOf(FormulaTriggers, cell[1]) >= 0
            ? cell[1..]
            : cell;

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
