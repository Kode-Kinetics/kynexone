using Microsoft.AspNetCore.Mvc;

namespace Zayra.Api.Application.Common;

/// <summary>
/// F9 - the ONE way a controller returns CSV to a person: UTF-8 with a byte-order mark (so Excel reads Arabic correctly) and
/// <c>text/csv; charset=utf-8</c>. The text should come from <see cref="Csv.Build"/> (formula-injection guard + RFC 4180).
/// <c>CsvDownloadRatchetTests</c> fails the build if a controller returns <c>"text/csv"</c> any other way.
/// </summary>
public static class CsvDownload
{
    public const string ContentType = "text/csv; charset=utf-8";

    /// <summary>With a file name the browser downloads; without one (the caller set Content-Disposition itself) it is inline.</summary>
    public static FileContentResult Result(string csv, string? fileName = null) =>
        new(Csv.ToDownloadBytes(csv), ContentType) { FileDownloadName = fileName };
}
