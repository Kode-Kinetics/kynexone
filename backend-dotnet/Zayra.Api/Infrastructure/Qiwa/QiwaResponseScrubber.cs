using Zayra.Api.Infrastructure.Government;

namespace Zayra.Api.Infrastructure.Qiwa;

/// <summary>
/// What <c>qiwa_sync_logs.response_payload_json</c> keeps of a Qiwa (or sandbox) response: the top-level status
/// fields, with any IBAN / national-ID-shaped text masked. A live response echoes the employee's record back
/// (id_number and the rest), and the log row is kept for years.
///
/// <para>The only thing read back from the column is the sandbox marker <c>"simulated":true</c>
/// (<see cref="Zayra.Api.Models.QiwaSyncLogStatuses.IsSimulated"/>); the allow-list keeps it, compact, so a
/// scrubbed sandbox body is still recognised.</para>
/// </summary>
public static class QiwaResponseScrubber
{
    private static readonly HashSet<string> KeptKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "simulated", "filed_with_qiwa", "adapter", "note", "reason", "employee_status",
        "error", "error_code", "error_description", "code", "message",
        "request_id", "reference", "reference_number", "transaction_id", "correlation_id",
    };

    /// <summary>Delegates to the shared <see cref="GovernmentLogScrubber"/> with Qiwa's allow-list.</summary>
    public static string? Scrub(string? raw) => GovernmentLogScrubber.ScrubJson(raw, KeptKeys);
}
