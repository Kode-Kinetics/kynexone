using System.Globalization;
using System.Text.RegularExpressions;
using Zayra.Api.Application.Common;

namespace Zayra.Api.Infrastructure.Attendance;

/// <summary>A problem with the file as a whole (empty, no data rows, over the row cap, a required column missing): the
/// upload is refused with this plain explanation (a 400). It is an <see cref="InvalidOperationException"/> so the
/// attendance endpoints' existing handling turns it into a 400 naming the problem.</summary>
public sealed class AttendanceImportFileException(string message) : InvalidOperationException(message);

/// <summary>One data line of an attendance punch file, read once and judged once. <see cref="Error"/> is null when the
/// line parsed cleanly; <see cref="Column"/> names the column the error is about.</summary>
public sealed record ParsedPunchRow(
    int RowNumber, string RawRow, IReadOnlyList<string> Cells, string EmployeeCode, DateTime? PunchUtc, string? Direction,
    string Location, string Method, string? Column, string? Error);

public sealed record ParsedPunchFile(IReadOnlyList<ParsedPunchRow> Rows);

/// <summary>
/// THE reader of an attendance punch file, shared by the preview and the import so a file cannot be judged one way in the dry
/// run and another way for real.
///
/// <para>Columns are found by NAME (case, spaces and underscores ignored) with a few aliases; a file with no recognised header
/// is read positionally as <c>employeeCode, punchTimestamp, punchDirection, location, method</c>. The delimiter is detected
/// from the first line (comma, semicolon or tab: Excel in an Arabic or European locale saves semicolons).</para>
///
/// <para>A zone-less timestamp is read in the TENANT's time zone; an explicit <c>Z</c> or <c>+hh:mm</c> is honoured. Day-first
/// dates are accepted, month-first never (01/02/2026 is the 1st of February), so a punch can't silently move to another
/// month.</para>
/// </summary>
public static class AttendancePunchCsv
{
    public const int MaxRows = 5000;

    public static readonly IReadOnlyList<string> TemplateHeaders = new[] { "employeeCode", "punchTimestamp", "punchDirection", "location", "method" };
    public static readonly IReadOnlyList<string> TemplateExample = new[] { "EMP-001", "2026-06-16 09:00", "In", "Head office", "Manual" };

    public const string ColumnCode = "employeeCode";
    public const string ColumnTimestamp = "punchTimestamp";
    public const string ColumnDirection = "punchDirection";
    public const string ColumnLocation = "location";
    public const string ColumnMethod = "method";

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["employeecode"] = ColumnCode, ["empcode"] = ColumnCode, ["code"] = ColumnCode,
        ["punchtimestamp"] = ColumnTimestamp, ["timestamp"] = ColumnTimestamp, ["datetime"] = ColumnTimestamp, ["punchtime"] = ColumnTimestamp,
        ["punchdirection"] = ColumnDirection, ["direction"] = ColumnDirection, ["type"] = ColumnDirection, ["in/out"] = ColumnDirection,
        ["location"] = ColumnLocation,
        ["method"] = ColumnMethod, ["verificationmethod"] = ColumnMethod,
    };

    private static readonly string[] Positional = { ColumnCode, ColumnTimestamp, ColumnDirection, ColumnLocation, ColumnMethod };

    /// <summary>Lower-cased, with spaces, underscores and hyphens removed ("Date Time" and "date_time" are one name).</summary>
    private static string NormaliseName(string name) =>
        new string(name.Trim().ToLowerInvariant().Where(c => !char.IsWhiteSpace(c) && c != '_' && c != '-').ToArray());

    public static ParsedPunchFile Parse(string? csv, TimeZoneInfo timeZone)
    {
        if (string.IsNullOrWhiteSpace(csv))
            throw new AttendanceImportFileException("The file is empty. Download the template, add one row per punch and upload it again.");

        var lines = Csv.Lines(csv)
            .Select((text, index) => (Text: text, Number: index + 1))
            .Where(l => !string.IsNullOrWhiteSpace(l.Text))
            .ToList();
        if (lines.Count == 0)
            throw new AttendanceImportFileException("The file is empty. Download the template, add one row per punch and upload it again.");

        var delimiter = DetectDelimiter(lines[0].Text);
        var firstCells = Csv.SplitRow(lines[0].Text, delimiter);
        var header = ReadHeader(firstCells);

        var dataLines = header is null ? lines : lines.Skip(1).ToList();
        if (header is not null)
        {
            var missing = new[] { ColumnCode, ColumnTimestamp, ColumnDirection }.Where(c => !header.ContainsKey(c)).ToList();
            if (missing.Count > 0)
                throw new AttendanceImportFileException(
                    $"The header row has no {string.Join(" / ", missing)} column. Use the template's header: {string.Join(", ", TemplateHeaders)}.");
        }
        if (dataLines.Count == 0)
            throw new AttendanceImportFileException("The file has a header but no punch rows. Add one row per punch below the header and upload it again.");
        if (dataLines.Count > MaxRows)
            throw new AttendanceImportFileException(
                $"A file can carry at most {MaxRows.ToString("N0", CultureInfo.InvariantCulture)} punch rows and this one has {dataLines.Count.ToString("N0", CultureInfo.InvariantCulture)}. Split it into smaller files and upload them one after another.");

        var width = header is null ? Positional.Length : firstCells.Count;
        var rows = new List<ParsedPunchRow>(dataLines.Count);
        foreach (var (text, number) in dataLines)
            rows.Add(ParseLine(text, number, delimiter, header, width, timeZone));
        return new ParsedPunchFile(rows);
    }

    private static ParsedPunchRow ParseLine(string text, int number, char delimiter, Dictionary<string, int>? header, int width, TimeZoneInfo timeZone)
    {
        ParsedPunchRow Fail(string column, string message, IReadOnlyList<string>? cells = null) =>
            new(number, text, cells ?? Array.Empty<string>(), string.Empty, null, null, string.Empty, string.Empty, column, message);

        if (text.Count(c => c == '"') % 2 == 1)
            return Fail(ColumnCode, "This row has a line break inside a quoted value, which a punch file can't carry. Put each punch on one line.");
        var cells = Csv.SplitRow(text, delimiter);
        if (header is not null && cells.Count > width)
            return Fail(ColumnCode, $"This row has {cells.Count} values but the header has {width}. If a value contains a comma, put it in quotes.", cells);

        string Cell(string column)
        {
            var index = header is null ? Array.IndexOf(Positional, column) : header.GetValueOrDefault(column, -1);
            return index >= 0 && index < cells.Count ? cells[index].Trim() : string.Empty;
        }

        var code = Cell(ColumnCode);
        if (code.Length == 0) return Fail(ColumnCode, "The employee code is missing.", cells);

        var stamp = Cell(ColumnTimestamp);
        if (stamp.Length == 0) return Fail(ColumnTimestamp, "The date and time is missing.", cells);
        if (!TryParseTimestamp(stamp, timeZone, out var utc, out var stampError)) return Fail(ColumnTimestamp, stampError!, cells);

        var directionText = Cell(ColumnDirection);
        if (directionText.Length == 0) return Fail(ColumnDirection, "The punch direction is missing. Use In, Out, BreakIn or BreakOut.", cells);
        var direction = ParseDirection(directionText);
        if (direction is null)
            return Fail(ColumnDirection, $"'{directionText}' is not a punch direction. Use In, Out, BreakIn or BreakOut.", cells);

        return new ParsedPunchRow(number, text, cells, code, utc, direction, Cell(ColumnLocation), Cell(ColumnMethod), null, null);
    }

    /// <summary>The header's column positions by canonical name, or null when the first line is not a header (no cell is a
    /// recognised column name).</summary>
    private static Dictionary<string, int>? ReadHeader(IReadOnlyList<string> cells)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < cells.Count; i++)
            if (Aliases.TryGetValue(NormaliseName(cells[i]), out var column)) map.TryAdd(column, i);
        return map.Count == 0 ? null : map;
    }

    /// <summary>Comma, semicolon or tab, whichever the first line uses most outside quotes (comma on a tie).</summary>
    internal static char DetectDelimiter(string firstLine)
    {
        int comma = 0, semicolon = 0, tab = 0;
        var quoted = false;
        foreach (var c in firstLine)
        {
            if (c == '"') quoted = !quoted;
            else if (quoted) continue;
            else if (c == ',') comma++;
            else if (c == ';') semicolon++;
            else if (c == '\t') tab++;
        }
        if (semicolon > comma && semicolon >= tab) return ';';
        if (tab > comma && tab > semicolon) return '\t';
        return ',';
    }

    /// <summary>In, Out, BreakIn or BreakOut, ignoring case, spaces, hyphens and underscores ("Check-in" is In). Null for anything else.</summary>
    public static string? ParseDirection(string? text)
    {
        var key = new string((text ?? string.Empty).Where(c => !char.IsWhiteSpace(c) && c != '-' && c != '_').ToArray()).ToLowerInvariant();
        return key switch
        {
            "in" or "checkin" or "clockin" => "In",
            "out" or "checkout" or "clockout" => "Out",
            "breakin" => "BreakIn",
            "breakout" => "BreakOut",
            _ => null,
        };
    }

    // An explicit ISO zone: the value is an instant already and is taken as written.
    private static readonly Regex ExplicitZone = new(
        @"^\d{4}-\d{2}-\d{2}[T ]\d{1,2}:\d{2}(:\d{2}(\.\d+)?)?\s*(Z|z|[+-]\d{2}:\d{2})$", RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly string[] LocalFormats =
    {
        "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss",
        "yyyy-M-d H:mm", "yyyy-M-d H:mm:ss", "yyyy-M-d'T'H:mm", "yyyy-M-d'T'H:mm:ss",
        "d/M/yyyy H:mm", "d/M/yyyy H:mm:ss", "d-M-yyyy H:mm", "d-M-yyyy H:mm:ss",
    };

    // The text is the user's: a pathological value must not hang the request (it is simply not an explicit-zone timestamp).
    private static bool IsExplicitZone(string text)
    {
        try { return ExplicitZone.IsMatch(text); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    /// <summary>Reads a punch time: an explicit <c>Z</c>/<c>±hh:mm</c> is honoured; anything else is a wall-clock time in
    /// <paramref name="timeZone"/>, year-first or day-first (never month-first).</summary>
    public static bool TryParseTimestamp(string text, TimeZoneInfo timeZone, out DateTime utc, out string? error)
    {
        utc = default;
        error = null;
        text = text.Trim();
        if (IsExplicitZone(text))
        {
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant))
            {
                utc = DateTime.SpecifyKind(instant.UtcDateTime, DateTimeKind.Utc);
                return true;
            }
        }
        else if (DateTime.TryParseExact(text, LocalFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            if (timeZone.IsInvalidTime(local))
            {
                error = $"{text} does not exist in {timeZone.Id} (the clocks change at that time). Use a time just before or after it.";
                return false;
            }
            utc = DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(local, timeZone), DateTimeKind.Utc);
            return true;
        }
        error = $"'{text}' is not a date and time this file can read. Write year-month-day with a 24-hour time, for example 2026-06-16 09:00, "
              + "or day/month/year such as 16/06/2026 09:00.";
        return false;
    }
}
