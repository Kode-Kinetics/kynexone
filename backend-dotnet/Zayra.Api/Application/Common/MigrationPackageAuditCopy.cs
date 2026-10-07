using System.Text;
using System.Text.Json;

namespace Zayra.Api.Application.Common;

/// <summary>
/// What <c>migration_import_batches.payload_json</c> keeps of a migration package: its checksum, the row count of
/// each section, and a copy of every section with identity / banking numbers masked to their last 4 and pay
/// columns redacted. Never the raw package: a legacy export carries every employee's IBAN, iqama and salary,
/// and the batch row outlives the import by years.
///
/// <para>Nothing reads this column back to run an import: Resume takes the package again from the caller and
/// matches it on <c>PackageChecksum</c>. The copy exists so a reviewer can see what was submitted.</para>
/// </summary>
public static class MigrationPackageAuditCopy
{
    public const string Policy = "masked-v1";

    public static string Serialize(string checksum, IReadOnlyDictionary<string, string> sections)
    {
        var rowCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var masked = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, csv) in sections)
        {
            var rows = ParseCsv(csv ?? string.Empty);
            rowCounts[name] = Math.Max(0, rows.Count - 1);
            masked[name] = WriteCsv(MaskRows(rows));
        }
        return JsonSerializer.Serialize(new { policy = Policy, checksum, rowCounts, sections = masked });
    }

    private static List<List<string>> MaskRows(List<List<string>> rows)
    {
        if (rows.Count == 0) return rows;
        var header = rows[0];
        var salary = header.Select(SensitiveFieldClassifier.IsSalaryName).ToArray();
        var identifier = header.Select(SensitiveFieldClassifier.IsIdentifierName).ToArray();
        // A history row names its field in a FieldName column and carries the values in OldValue/NewValue.
        var fieldNameColumn = header.FindIndex(h => SensitiveFieldClassifier.Normalise(h) == "fieldname");

        var result = new List<List<string>> { header.Select(SensitiveFieldClassifier.MaskEmbedded).ToList() };
        foreach (var row in rows.Skip(1))
        {
            var rowField = fieldNameColumn >= 0 && fieldNameColumn < row.Count ? row[fieldNameColumn] : null;
            var rowFieldIsSalary = SensitiveFieldClassifier.IsSalaryName(rowField);
            var rowFieldIsIdentifier = SensitiveFieldClassifier.IsIdentifierName(rowField);
            var masked = new List<string>(row.Count);
            for (var i = 0; i < row.Count; i++)
            {
                var cell = row[i];
                var isValueColumn = i != fieldNameColumn && rowField is not null && IsValueColumn(header, i);
                if (string.IsNullOrEmpty(cell)) masked.Add(cell);
                else if (i < salary.Length && salary[i] || isValueColumn && rowFieldIsSalary) masked.Add(SensitiveValueMask.Redacted);
                else if (i < identifier.Length && identifier[i] || isValueColumn && rowFieldIsIdentifier
                         || SensitiveFieldClassifier.LooksSensitive(cell))
                    masked.Add(SensitiveValueMask.MaskId(cell));
                else masked.Add(SensitiveFieldClassifier.MaskEmbedded(cell));
            }
            result.Add(masked);
        }
        return result;
    }

    private static bool IsValueColumn(List<string> header, int i) =>
        i < header.Count && SensitiveFieldClassifier.Normalise(header[i]) is "oldvalue" or "newvalue" or "value";

    internal static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else cell.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (ch is '\n' or '\r')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Clear();
                if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
                row = new List<string>();
            }
            else cell.Append(ch);
        }
        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
        }
        return rows;
    }

    private static string WriteCsv(List<List<string>> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            sb.Append(string.Join(',', row.Select(c => c.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + c.Replace("\"", "\"\"") + "\"" : c)));
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
