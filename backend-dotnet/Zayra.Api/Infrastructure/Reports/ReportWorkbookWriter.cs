using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Zayra.Api.Infrastructure.Reports;

/// <summary>
/// Writes a genuine .xlsx — an OPC package containing SpreadsheetML — using
/// <c>DocumentFormat.OpenXml</c>, which was already a dependency of this project (it was being
/// used to READ .docx policy documents; nothing in the repo had ever written a spreadsheet).
///
/// <para>What this replaces: <c>ReportScheduleWorker.BuildArtifact</c> named the attachment
/// <c>report.csv</c>, filled it with CSV bytes, and set the content type to
/// <c>application/vnd.ms-excel</c> when the user had chosen "Excel". The user got a CSV with a
/// lie on the envelope. Excel opens it, so nobody complained loudly enough — but no formatting,
/// no typed numbers, no second sheet, and any tool that trusts the content type breaks.</para>
///
/// <para>Numbers are written as numbers, not text, so a column sums. Dates stay strings: the
/// report projections hand over already-formatted date strings in several different formats and
/// guessing which is which would corrupt some of them. That is a deliberate limit, not an
/// oversight.</para>
/// </summary>
public static class ReportWorkbookWriter
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static byte[] ToXlsx(IReadOnlyList<ReportTable> tables)
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();

            // One bold style for the header row. Index 0 is the mandatory default; index 1 is ours.
            var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
            stylesPart.Stylesheet = BuildStylesheet();
            stylesPart.Stylesheet.Save();

            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            uint sheetId = 1;
            var usedNames = new List<string>();

            // Excel refuses to open a workbook with no sheets, so an empty report still gets one.
            var effective = tables.Count > 0 ? tables : [new ReportTable("Data", [], [])];

            foreach (var table in effective)
            {
                var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
                var sheetData = new SheetData();
                worksheetPart.Worksheet = new Worksheet(sheetData);

                if (table.Headers.Count > 0)
                {
                    var headerRow = new Row();
                    foreach (var header in table.Headers) headerRow.Append(TextCell(header, styleIndex: 1));
                    sheetData.Append(headerRow);
                }

                foreach (var row in table.Rows)
                {
                    var sheetRow = new Row();
                    foreach (var value in row) sheetRow.Append(ValueCell(value));
                    sheetData.Append(sheetRow);
                }

                worksheetPart.Worksheet.Save();
                sheets.Append(new Sheet
                {
                    Id = workbookPart.GetIdOfPart(worksheetPart),
                    SheetId = sheetId++,
                    Name = UniqueSheetName(table.Name, usedNames),
                });
            }

            workbookPart.Workbook.Save();
        }
        return stream.ToArray();
    }

    /// <summary>
    /// Excel's sheet-name rules, which it enforces by refusing to open the file rather than by
    /// complaining: at most 31 characters, none of <c>: \ / ? * [ ]</c>, and unique.
    /// </summary>
    internal static string UniqueSheetName(string name, List<string> used)
    {
        var cleaned = new string((name ?? string.Empty)
            .Where(c => !":\\/?*[]".Contains(c))
            .ToArray()).Trim();
        if (cleaned.Length == 0) cleaned = "Sheet";
        if (cleaned.Length > 31) cleaned = cleaned[..31];

        var candidate = cleaned;
        var suffix = 2;
        while (used.Contains(candidate, StringComparer.OrdinalIgnoreCase))
        {
            var tail = $"_{suffix++}";
            candidate = cleaned.Length + tail.Length <= 31 ? cleaned + tail : cleaned[..(31 - tail.Length)] + tail;
        }
        used.Add(candidate);
        return candidate;
    }

    private static Cell ValueCell(string value)
    {
        // A value that round-trips as a decimal becomes a real number so the column sums.
        // Guard against strings that merely look numeric and must not be coerced: an IBAN, an
        // employee code like "0042" whose leading zero matters, a phone number.
        if (LooksNumeric(value) && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
            return new Cell
            {
                DataType = CellValues.Number,
                CellValue = new CellValue(number.ToString(CultureInfo.InvariantCulture)),
            };

        return TextCell(value, styleIndex: 0);
    }

    private static bool LooksNumeric(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var trimmed = value.Trim();
        if (trimmed.Length > 20) return false;                          // IBANs and ids
        if (trimmed.Length > 1 && trimmed[0] == '0' && trimmed[1] != '.') return false;  // "0042"
        return trimmed.All(c => char.IsDigit(c) || c is '.' or ',' or '-' or '+');
    }

    private static Cell TextCell(string value, uint styleIndex) => new()
    {
        DataType = CellValues.InlineString,
        StyleIndex = styleIndex,
        InlineString = new InlineString(new Text(value ?? string.Empty) { Space = SpaceProcessingModeValues.Preserve }),
    };

    private static Stylesheet BuildStylesheet() => new(
        new Fonts(
            new Font(),                                   // 0 — default
            new Font(new Bold())) { Count = 2 },          // 1 — header
        new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 })) { Count = 2 },
        new Borders(new Border()) { Count = 1 },
        new CellStyleFormats(new CellFormat()) { Count = 1 },
        new CellFormats(
            new CellFormat(),                                                     // 0 — default
            new CellFormat { FontId = 1, ApplyFont = true }) { Count = 2 });      // 1 — bold header
}
