using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Zayra.Api.Infrastructure.Reports;

/// <summary>One tabular block: a name, a header row, and the rows under it.</summary>
public sealed record ReportTable(string Name, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>
/// Turns a report's result — a list of anonymous objects, serialised to JSON — into tables.
///
/// <para>Report methods return anonymous types with no column metadata: the frontend derives
/// its headers from <c>Object.keys(data[0])</c>. An exporter has to do the same thing, and has
/// to do it in a way that survives the two shapes in the catalogue that are not a flat list:</para>
/// <list type="bullet">
/// <item><c>hr.nationality-mix</c> returns <c>{ byNationality: [...], byGender: [...] }</c> —
/// an object of two arrays. It becomes two tables (two sheets in XLSX, two labelled blocks in
/// CSV) rather than one unreadable smear or an empty file.</item>
/// <item>Several reports return an empty list when there is no data. That becomes a table with
/// no rows, not a zero-byte download — a spreadsheet with headers and no rows is an answer;
/// an empty file is a support ticket.</item>
/// </list>
///
/// <para>Headers are the UNION of property names across rows, in first-seen order, because a
/// projection with a nullable branch can omit a property on some rows.</para>
/// </summary>
public static class ReportTabulator
{
    public static IReadOnlyList<ReportTable> Tabulate(JsonElement data, string defaultName = "Data")
    {
        switch (data.ValueKind)
        {
            case JsonValueKind.Array:
                return [BuildTable(defaultName, data)];

            case JsonValueKind.Object:
            {
                // An object whose properties are all arrays is a multi-section report.
                var sections = data.EnumerateObject()
                    .Where(p => p.Value.ValueKind == JsonValueKind.Array)
                    .ToList();
                if (sections.Count > 0)
                    return [.. sections.Select(p => BuildTable(Humanise(p.Name), p.Value))];

                // A single object is a one-row table.
                var headers = data.EnumerateObject().Select(p => Humanise(p.Name)).ToList();
                var row = data.EnumerateObject().Select(p => Scalar(p.Value)).ToList();
                return [new ReportTable(defaultName, headers, [row])];
            }

            case JsonValueKind.Undefined or JsonValueKind.Null:
                return [new ReportTable(defaultName, [], [])];

            default:
                return [new ReportTable(defaultName, ["Value"], [[Scalar(data)]])];
        }
    }

    private static ReportTable BuildTable(string name, JsonElement array)
    {
        var headerKeys = new List<string>();
        var rows = new List<Dictionary<string, string>>();

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                var row = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var property in item.EnumerateObject())
                {
                    if (!headerKeys.Contains(property.Name, StringComparer.Ordinal)) headerKeys.Add(property.Name);
                    row[property.Name] = Scalar(property.Value);
                }
                rows.Add(row);
            }
            else
            {
                if (headerKeys.Count == 0) headerKeys.Add("Value");
                rows.Add(new Dictionary<string, string>(StringComparer.Ordinal) { [headerKeys[0]] = Scalar(item) });
            }
        }

        var materialised = rows
            .Select(IReadOnlyList<string> (row) => headerKeys.Select(k => row.GetValueOrDefault(k, string.Empty)).ToList())
            .ToList();

        return new ReportTable(name, [.. headerKeys.Select(Humanise)], materialised);
    }

    /// <summary>
    /// Flattens one cell. Nested objects and arrays are serialised rather than dropped — a
    /// column that silently disappears from an export is how a finance team reconciles against
    /// a number that is not there.
    /// </summary>
    private static string Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        _ => value.GetRawText(),
    };

    /// <summary>camelCase property name to a column heading a human reads.</summary>
    internal static string Humanise(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var builder = new StringBuilder(name.Length + 6);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1])) builder.Append(' ');
            builder.Append(i == 0 ? char.ToUpper(c, CultureInfo.InvariantCulture) : c);
        }
        return builder.ToString();
    }

    // ── CSV ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// RFC 4180 with formula-injection neutralisation. The leading-apostrophe guard is carried
    /// over verbatim from the schedule worker's private writer, which is being replaced by this
    /// one: a cell beginning = + - @ is executed by Excel on open.
    /// </summary>
    public static byte[] ToCsv(IReadOnlyList<ReportTable> tables)
    {
        var csv = new StringBuilder();
        for (var t = 0; t < tables.Count; t++)
        {
            var table = tables[t];
            if (tables.Count > 1)
            {
                if (t > 0) csv.AppendLine();
                csv.AppendLine(Cell($"# {table.Name}"));
            }
            csv.AppendLine(string.Join(',', table.Headers.Select(Cell)));
            foreach (var row in table.Rows) csv.AppendLine(string.Join(',', row.Select(Cell)));
        }
        // UTF-8 BOM, prepended explicitly. Note that `new UTF8Encoding(true).GetBytes(...)`
        // does NOT emit one — GetBytes never writes the preamble, only a StreamWriter does — so
        // the schedule worker's CSV, which used exactly that idiom, never had a BOM either.
        // Without it Excel on Windows reads an Arabic employee name as mojibake.
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var preamble = Encoding.UTF8.GetPreamble();
        var body = encoding.GetBytes(csv.ToString());
        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        return bytes;
    }

    private static string Cell(string value)
    {
        var safe = value.Length > 0 && "=+-@".Contains(value[0]) ? "'" + value : value;
        return $"\"{safe.Replace("\"", "\"\"")}\"";
    }
}
