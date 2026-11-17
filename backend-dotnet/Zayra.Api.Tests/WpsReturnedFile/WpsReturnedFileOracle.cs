using System.Globalization;
using System.Text;

namespace Zayra.Api.Tests.WpsReturnedFile;

/// <summary>One synthetic employee line of a bank-returned WPS file. Invented data only.</summary>
internal sealed record SynthRow(
    string MolId, string Iban, string Name, decimal Basic, decimal Housing, decimal Other, decimal Deductions,
    string? Status = null, string? TrnRef = null, string? TrnDate = null)
{
    public decimal Net => Basic + Housing + Other - Deductions;
}

/// <summary>
/// TEST-ONLY golden oracle: writes a file in the layout the reader assumes (TAB-delimited, bracketed tags,
/// comma decimal). It exists so the reader can be tested; it is never shipped and never produces a file for
/// a customer. The layout is UNVERIFIED against a real bank sample (see WpsReturnedFileValidator.Rules).
/// </summary>
internal static class WpsReturnedFileOracle
{
    public sealed record Header(
        string DestId = "XXXX", string EstbId = "9000001", string BankAcc = "SA0380000000608010167519",
        string Currency = "SAR", string ValueDate = "20260930", string FileRef = "WPS2026090001",
        string MolEstablishmentId = "7-1234567", string? FileRejectCode = null, string? DDate = null);

    public static string Amount(decimal d) => d.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',');

    /// <summary>A valid 24-character Saudi IBAN (ISO 13616 mod-97), built from a 2-digit bank code and 18 digits.</summary>
    public static string SaIban(int bankCode, long body)
    {
        var bban = bankCode.ToString("D2", CultureInfo.InvariantCulture) + body.ToString("D18", CultureInfo.InvariantCulture);
        // Check digits: mod-97 of bban + "SA00" with letters mapped (S=28, A=10).
        var numeric = bban + "2810" + "00";
        var rem = 0;
        foreach (var c in numeric) rem = (rem * 10 + (c - '0')) % 97;
        return "SA" + (98 - rem).ToString("D2", CultureInfo.InvariantCulture) + bban;
    }

    public static IReadOnlyList<SynthRow> Rows(int count, string? arabicName = null)
    {
        var rows = new List<SynthRow>(count);
        for (var i = 0; i < count; i++)
        {
            var basic = 4000m + (i % 50) * 10m;
            var housing = 1000m + (i % 7) * 5m;
            var other = (i % 3) * 100m;
            var ded = (i % 5) * 25m;
            rows.Add(new SynthRow(
                MolId: (i % 4 == 0 ? "2" : "1") + (100_000_000 + i).ToString("D9", CultureInfo.InvariantCulture),
                Iban: SaIban(80, 100_000_000_000L + i),
                Name: arabicName ?? $"Employee {i:D5}",
                Basic: basic, Housing: housing, Other: other, Deductions: ded));
        }
        return rows;
    }

    public static string Build(
        IReadOnlyList<SynthRow> rows, Header? header = null, string newline = "\n", bool bom = false,
        decimal? declaredTotal = null, bool headerOnOneLine = false, IReadOnlyList<string>? omitHeaderTags = null,
        IReadOnlyList<string>? trailerLines = null, Func<SynthRow, string>? rowOverride = null)
    {
        header ??= new Header();
        var total = declaredTotal ?? rows.Sum(r => r.Net);
        var h = new List<(string Tag, string Value)>
        {
            ("DEST-ID", header.DestId), ("ESTB-ID", header.EstbId), ("BANK-ACC", header.BankAcc),
            ("32A-CCY", header.Currency), ("32A-VAL", header.ValueDate), ("32A-AMT", Amount(total)),
            ("FILE-REF", header.FileRef), ("MOL-ESTBID", header.MolEstablishmentId),
        };
        if (header.DDate is not null) h.Add(("D-DATE", header.DDate));
        if (header.FileRejectCode is not null) h.Add(("FILE-REJCDE", header.FileRejectCode));
        if (omitHeaderTags is not null) h.RemoveAll(x => omitHeaderTags.Contains(x.Tag));

        var sb = new StringBuilder();
        if (bom) sb.Append('\uFEFF');
        if (headerOnOneLine)
            sb.Append(string.Join('\t', h.Select(x => $"[{x.Tag}]\t{x.Value}"))).Append(newline);
        else
            foreach (var x in h) sb.Append($"[{x.Tag}]\t{x.Value}").Append(newline);

        foreach (var r in rows)
        {
            if (rowOverride is not null) { sb.Append(rowOverride(r)).Append(newline); continue; }
            sb.Append($"[32B-AMT]\t{Amount(r.Net)}\t[59-ACC]\t{r.Iban}\t[59-NAME]\t{r.Name}\t[MOL-ID]\t{r.MolId}")
              .Append($"\t[MOL-BAS]\t{Amount(r.Basic)}\t[MOL-HAL]\t{Amount(r.Housing)}\t[MOL-OEA]\t{Amount(r.Other)}\t[MOL-DED]\t{Amount(r.Deductions)}");
            if (r.TrnRef is not null) sb.Append($"\t[TRN-REF]\t{r.TrnRef}");
            if (r.Status is not null) sb.Append($"\t[TRN-STATUS]\t{r.Status}");
            if (r.TrnDate is not null) sb.Append($"\t[TRN-DATE]\t{r.TrnDate}");
            sb.Append(newline);
        }
        if (trailerLines is not null) foreach (var t in trailerLines) sb.Append(t).Append(newline);
        return sb.ToString();
    }
}
