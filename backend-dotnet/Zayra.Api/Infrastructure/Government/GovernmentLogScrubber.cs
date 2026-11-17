using System.Text.Json;
using System.Text.Json.Nodes;
using Zayra.Api.Application.Common;

namespace Zayra.Api.Infrastructure.Government;

/// <summary>
/// The one scrubber every government and bank adapter uses before anything it received or sent is stored or logged
/// (security R6). Extended from <c>QiwaResponseScrubber</c>, which now delegates here with its own allow-list.
/// <list type="bullet">
/// <item><see cref="ScrubJson"/>: keeps only allow-listed top-level scalar keys, masks IBAN- and ID-shaped text inside them,
/// and counts what it withheld. A non-JSON or non-object body is withheld entirely.</item>
/// <item><see cref="Mask"/> and <see cref="Preview"/>: free text with IBAN- and ID-shaped runs masked, the preview cut short.</item>
/// </list>
/// </summary>
public static class GovernmentLogScrubber
{
    public const int DefaultPreviewLength = 256;

    /// <summary>Status-like keys that are safe to keep from any body's response.</summary>
    public static readonly IReadOnlySet<string> CommonKeptKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "status", "state", "simulated", "adapter", "note", "reason",
        "error", "error_code", "error_description", "code", "message",
        "request_id", "reference", "reference_number", "transaction_id", "correlation_id", "id",
    };

    public static string? ScrubJson(string? raw, IReadOnlySet<string> keptKeys)
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
            if (!keptKeys.Contains(key) || value is not JsonValue scalar) continue;
            kept[key] = scalar.TryGetValue<string>(out var text)
                ? JsonValue.Create(SensitiveFieldClassifier.MaskEmbedded(text))
                : scalar.DeepClone();
        }
        var withheld = obj.Count - kept.Count;
        if (withheld > 0) kept["withheld_fields"] = withheld;
        return kept.ToJsonString();
    }

    /// <summary>Free text with IBAN- and national-ID-shaped runs masked.</summary>
    public static string Mask(string? text) => SensitiveFieldClassifier.MaskEmbedded(text);

    /// <summary>A masked, single-line preview of at most <paramref name="maxLength"/> characters, or null for empty text.</summary>
    public static string? Preview(string? text, int maxLength = DefaultPreviewLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var oneLine = string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        var masked = Mask(oneLine);
        return masked.Length <= maxLength ? masked : masked[..maxLength];
    }
}
