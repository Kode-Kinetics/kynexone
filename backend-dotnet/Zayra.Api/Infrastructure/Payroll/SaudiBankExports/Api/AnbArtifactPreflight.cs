using System.Globalization;
using System.Text;
using Zayra.Api.Application.Common;

namespace Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Api;

/// <summary>
/// Last look at the frozen files immediately before they are sent. READ ONLY: nothing is re-derived or
/// rewritten. The header's paymentCount and totalPayrollAmount must equal the body's rows and the sum of
/// salaryAmount, and the auto-WPS request headers must agree with what the frozen header says. A pair of
/// files that each still match their own hash but do not belong together is refused here.
/// </summary>
internal static class AnbArtifactPreflight
{
    public sealed record Result(string? Code, IReadOnlyList<string> SentValues);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static Result Check(byte[] header, byte[] body, AnbSubmitHeaders headers)
    {
        List<Dictionary<string, string>> headerRows, bodyRows;
        try
        {
            headerRows = Csv.Parse(StrictUtf8.GetString(header));
            bodyRows = Csv.Parse(StrictUtf8.GetString(body));
        }
        catch (Exception ex) when (ex is DecoderFallbackException or CsvShapeException or ArgumentException)
        {
            return Fail();
        }

        if (headerRows.Count != 1 || bodyRows.Count == 0) return Fail();
        var h = headerRows[0];

        var headerColumns = headers.AutoWpsFileUpload
            ? AnbConnectCsvGenerator.HeaderColumns.Concat(AnbConnectCsvGenerator.AutoWpsHeaderColumns)
            : AnbConnectCsvGenerator.HeaderColumns;
        if (!h.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(headerColumns)) return Fail();
        if (!bodyRows[0].Keys.ToHashSet(StringComparer.Ordinal).SetEquals(AnbConnectCsvGenerator.BodyColumns)) return Fail();

        if (!int.TryParse(h["paymentCount"], NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count != bodyRows.Count)
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

    private static Result Fail() => new(AnbApiCodes.ArtifactInconsistent, Array.Empty<string>());

    private static bool TryAmount(string? s, out decimal value) =>
        decimal.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) && value >= 0m;
}
