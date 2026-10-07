using System.Text.Json;
using System.Text.Json.Nodes;
using Zayra.Api.Application.Common;

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

    public static string? Scrub(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        JsonNode? node;
        try { node = JsonNode.Parse(raw); }
        catch (JsonException) { return JsonSerializer.Serialize(new { scrubbed = "non-JSON response withheld", length = raw.Length }); }
        if (node is not JsonObject obj)
            return JsonSerializer.Serialize(new { scrubbed = "non-object response withheld", length = raw.Length });

        var kept = new JsonObject();
        foreach (var (key, value) in obj)
        {
            if (!KeptKeys.Contains(key) || value is not JsonValue scalar) continue;
            kept[key] = scalar.TryGetValue<string>(out var text)
                ? JsonValue.Create(SensitiveFieldClassifier.MaskEmbedded(text))
                : scalar.DeepClone();
        }
        var withheld = obj.Count - kept.Count;
        if (withheld > 0) kept["withheld_fields"] = withheld;
        return kept.ToJsonString();
    }
}
