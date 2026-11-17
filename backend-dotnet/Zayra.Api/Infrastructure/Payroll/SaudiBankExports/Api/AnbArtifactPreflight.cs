using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Channels;

namespace Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Api;

/// <summary>
/// Last look at the frozen files immediately before they are sent. READ ONLY: nothing is re-derived or
/// rewritten. The files are read as strictly as <see cref="AnbConnectCsvGenerator"/> writes them (UTF-8
/// without BOM, CRLF after every line, the exact column row in the exact order, a cell quoted only when it
/// holds a comma or a quote, amounts in plain invariant form with at most two decimals), so a file the
/// generator could not have produced is refused, not normalised.
/// Then: the header's paymentCount equals the body rows, its totalPayrollAmount equals the sum of
/// salaryAmount, and the auto-WPS request headers agree with what the frozen header says.
/// <para>A change to the generator's columns makes older frozen files unsendable: re-export, never patch.</para>
/// </summary>
internal static partial class AnbArtifactPreflight
{
    public sealed record Result(string? Code, IReadOnlyList<string> SentValues);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static Result Check(byte[] header, byte[] body, AnbSubmitHeaders headers)
    {
        var headerColumns = headers.AutoWpsFileUpload
            ? AnbConnectCsvGenerator.HeaderColumns.Concat(AnbConnectCsvGenerator.AutoWpsHeaderColumns).ToList()
            : AnbConnectCsvGenerator.HeaderColumns.ToList();

        var headerRows = Read(header, headerColumns);
        var bodyRows = Read(body, AnbConnectCsvGenerator.BodyColumns);
        if (headerRows is null || bodyRows is null || headerRows.Count != 1 || bodyRows.Count == 0) return Fail();
        var h = headerRows[0];

        if (!PaymentCount().IsMatch(h["paymentCount"])
            || !int.TryParse(h["paymentCount"], NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            || count != bodyRows.Count)
            return Fail();

        if (!TryAmount(h["totalPayrollAmount"], out var total)) return Fail();
        var sum = 0m;
        foreach (var row in bodyRows)
        {
            if (!TryAmount(row["salaryAmount"], out var amount)) return Fail();
            sum += amount;
        }
        if (sum != total) return Fail();

        if (headers.AutoWpsFileUpload)
        {
            if (!string.Equals(h["autoWpsFileUpload"], "YES", StringComparison.Ordinal)
                || string.IsNullOrEmpty(headers.NationalUnifiedNo)
                || !string.Equals(h["nationalUnifiedNo"], headers.NationalUnifiedNo, StringComparison.Ordinal))
                return Fail();
        }
        else if (!string.IsNullOrEmpty(headers.NationalUnifiedNo))
        {
            return Fail();
        }

        // Every personal value we are about to send, so a bank message echoing one can be scrubbed.
        var sent = new List<string>(bodyRows.Count * 3 + 1) { h["mainAccountNumber"] };
        foreach (var row in bodyRows)
        {
            sent.Add(row["employeeId"]);
            sent.Add(row["employeeAccountNumber"]);
            sent.Add(row["employeeName"]);
        }
        return new Result(null, sent);
    }

    private static Result Fail() => new(BankChannelCodes.ArtifactInconsistent, Array.Empty<string>());

    /// <summary>Rows keyed by column, or null when the bytes are not exactly the generator's shape.</summary>
    private static List<Dictionary<string, string>>? Read(byte[] bytes, IReadOnlyList<string> columns)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return null;
        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return null; }
        if (!text.EndsWith("\r\n", StringComparison.Ordinal)) return null;

        var lines = text[..^2].Split("\r\n");
        if (lines.Any(l => l.Contains('\r') || l.Contains('\n'))) return null;
        if (!string.Equals(lines[0], string.Join(",", columns), StringComparison.Ordinal)) return null;

        var rows = new List<Dictionary<string, string>>(lines.Length - 1);
        for (var i = 1; i < lines.Length; i++)
        {
            var cells = SplitCanonical(lines[i]);
            if (cells is null || cells.Count != columns.Count) return null;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var c = 0; c < columns.Count; c++) map[columns[c]] = cells[c];
            rows.Add(map);
        }
        return rows;
    }

    /// <summary>RFC-4180 split that accepts only the generator's own quoting: a cell is quoted if and only if
    /// it holds a comma or a quote.</summary>
    private static List<string>? SplitCanonical(string line)
    {
        var cells = new List<string>();
        var i = 0;
        while (true)
        {
            if (i < line.Length && line[i] == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (true)
                {
                    if (i >= line.Length) return null;
                    if (line[i] == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                        i++;
                        break;
                    }
                    sb.Append(line[i++]);
                }
                var value = sb.ToString();
                if (value.IndexOfAny(new[] { ',', '"' }) < 0) return null; // quoted without need: not ours
                cells.Add(value);
            }
            else
            {
                var end = line.IndexOf(',', i);
                var value = end < 0 ? line[i..] : line[i..end];
                if (value.Contains('"')) return null;
                cells.Add(value);
                i = end < 0 ? line.Length : end;
            }

            if (i == line.Length) return cells;
            if (line[i] != ',') return null;
            i++;
        }
    }

    /// <summary>A positive amount in plain invariant form with at most two decimals ("5250.5", "6300.00").</summary>
    private static bool TryAmount(string? s, out decimal value)
    {
        value = 0m;
        return s is not null && Amount().IsMatch(s)
               && decimal.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)
               && value > 0m;
    }

    [GeneratedRegex(@"\A(0|[1-9][0-9]{0,11})(\.[0-9]{1,2})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex Amount();

    [GeneratedRegex(@"\A[1-9][0-9]{0,5}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PaymentCount();
}
