namespace Zayra.Api.Infrastructure.Reports;

/// <summary>
/// The export formats the product can actually produce, in one place, so the synchronous export
/// endpoint and the scheduled-delivery policy cannot drift apart again.
///
/// <para>They had drifted: <c>ReportSchedule.ExportFormat</c>'s own comment said
/// "Excel, CSV, PDF", <c>ReportSchedulePolicy</c> accepted "JSON, CSV, Excel", the UI offered
/// "Excel, CSV, PDF", and the worker wrote CSV bytes whichever you picked. Four answers, three
/// of them wrong.</para>
///
/// <para><b>PDF is absent on purpose.</b> A report is a wide table with an unbounded column
/// count; laying one out on A4 legibly is a feature, not a serialiser. Until that exists the
/// option is gone from the UI and refused by the API, rather than silently delivering something
/// else — hiding a field beats shipping one that lies.</para>
/// </summary>
public static class ReportExportFormats
{
    public const string Csv = "csv";
    public const string Xlsx = "xlsx";

    /// <summary>Only for scheduled delivery: the raw result, for a downstream system.</summary>
    public const string Json = "json";

    /// <summary>What the synchronous export endpoint accepts.</summary>
    public static readonly IReadOnlyList<string> All = [Csv, Xlsx];

    /// <summary>What a schedule may be created with.</summary>
    public static readonly IReadOnlyList<string> Schedulable = [Csv, Xlsx, Json];

    /// <summary>
    /// Maps a caller's spelling to the canonical one, or null. "Excel" resolves to xlsx so the
    /// schedules customers already created — whose ExportFormat column reads "Excel" — keep
    /// working, and now finally get an actual workbook.
    /// </summary>
    public static string? Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "csv" => Csv,
        "xlsx" or "excel" or "xls" => Xlsx,
        _ => null,
    };

    /// <summary>As <see cref="Normalize"/>, but also accepts JSON, which only schedules support.</summary>
    public static string? NormalizeSchedulable(string? value) =>
        (value ?? string.Empty).Trim().Equals("json", StringComparison.OrdinalIgnoreCase)
            ? Json
            : Normalize(value);

    public static string ContentTypeFor(string format) =>
        format == Xlsx ? ReportWorkbookWriter.ContentType
        : format == Json ? "application/json"
        : "text/csv";

    public static string ExtensionFor(string format) => format == Xlsx ? "xlsx" : format == Json ? "json" : "csv";
}
