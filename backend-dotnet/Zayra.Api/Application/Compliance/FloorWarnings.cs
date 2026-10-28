using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace Zayra.Api.Application.Compliance;

// ─────────────────────────────────────────────────────────────────────────────
//  Statutory floors are WARNINGS, not blocks (owner decision 2026-10-25).
//
//  A country's statutory value is the DEFAULT. A value below the legal minimum is allowed, but the
//  admin must acknowledge the warning, with a reason, and the acknowledgement is audited. Nothing is
//  silently raised to the floor, and nothing is hard-refused.
//
//  Wire contract = the hire path's (#276): a refused save answers 409 `warnings_require_acknowledgement`
//  with `warnings: [{ code, field, message, requiresPermission, ... }]`; the caller resends the same
//  request with `acknowledgedWarnings: [{ code, reason }]` (reason 10 to 500 characters).
//  The warning also carries statutoryValue / configuredValue / citation / messageAr so one screen
//  pattern can render every module.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>One warning the caller chose to override. Same JSON as the hire path's AcknowledgedWarning.</summary>
public sealed record FloorAcknowledgement(string Code, string? Reason);

/// <summary>A below-the-legal-minimum warning. <see cref="Message"/> is English; <see cref="MessageAr"/> Arabic.</summary>
public sealed record FloorWarning(
    string Code, string Field, string Message, bool RequiresPermission = false,
    decimal? StatutoryValue = null, decimal? ConfiguredValue = null, string? Citation = null, string? MessageAr = null,
    string? CitationAr = null)
{
    /// <summary>Alias of <see cref="Message"/> for clients that name the English text explicitly.</summary>
    public string MessageEn => Message;
}

/// <summary>One floor the rule is measured against, whether or not the configured value is below it.</summary>
public sealed record FloorCheck(FloorWarning Warning, bool Below, string Label, string LabelAr);

/// <summary>An acknowledgement as kept on the configured thing (policy, country rule): who, when, value, statutory value, citation, note.</summary>
public sealed record StoredFloorAck(
    string Code, string Field, decimal? ConfiguredValue, decimal? StatutoryValue, string? Citation,
    string Reason, string AcknowledgedBy, Guid? AcknowledgedById, DateTime AcknowledgedAtUtc);

public static class FloorWarningGate
{
    public const string AckRequiredCode = "warnings_require_acknowledgement";
    public const string ReasonRequiredCode = "override_reason_required";
    public const int ReasonMin = 10;
    public const int ReasonMax = 500;

    public sealed record Outcome(
        IReadOnlyList<FloorWarning> Unacknowledged,
        IReadOnlyList<(FloorWarning Warning, FloorAcknowledgement Ack)> Consumed,
        bool ReasonInvalid)
    {
        public bool Clear => Unacknowledged.Count == 0 && !ReasonInvalid;
    }

    /// <summary>
    /// Splits <paramref name="warnings"/> into those the request answers (<c>Consumed</c>), those already
    /// answered on the stored thing with the same value (<paramref name="alreadyAcknowledged"/>, neither listed),
    /// and those still open. An answer without a 10 to 500 character reason is flagged, never accepted.
    /// </summary>
    public static Outcome Evaluate(
        IEnumerable<FloorWarning> warnings, IEnumerable<FloorAcknowledgement>? acks,
        Func<FloorWarning, bool>? alreadyAcknowledged = null)
    {
        var byCode = (acks ?? []).Where(a => !string.IsNullOrWhiteSpace(a.Code))
            .GroupBy(a => a.Code, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var open = new List<FloorWarning>();
        var consumed = new List<(FloorWarning, FloorAcknowledgement)>();
        var reasonInvalid = false;
        foreach (var w in warnings)
        {
            if (byCode.TryGetValue(w.Code, out var ack))
            {
                var len = ack.Reason?.Trim().Length ?? 0;
                if (len < ReasonMin || len > ReasonMax) { reasonInvalid = true; continue; }
                consumed.Add((w, ack));
                continue;
            }
            if (alreadyAcknowledged?.Invoke(w) == true) continue;
            open.Add(w);
        }
        return new Outcome(open, consumed, reasonInvalid);
    }

    /// <summary>409 problem+json, the same shape the hire path answers.</summary>
    public static IActionResult AckRequired(IReadOnlyList<FloorWarning> warnings, string? traceId = null)
        => Problem(409, new Dictionary<string, object?>
        {
            ["type"] = "about:blank",
            ["title"] = "Acknowledgement required.",
            ["status"] = 409,
            ["code"] = AckRequiredCode,
            ["detail"] = "This request needs an acknowledgement for: " + string.Join(", ", warnings.Select(w => w.Code)),
            ["warnings"] = warnings,
            ["traceId"] = traceId,
        });

    public static IActionResult ReasonRequired(string? traceId = null)
        => Problem(422, new Dictionary<string, object?>
        {
            ["type"] = "about:blank",
            ["title"] = "The request was refused.",
            ["status"] = 422,
            ["code"] = ReasonRequiredCode,
            ["detail"] = $"A reason of {ReasonMin} to {ReasonMax} characters is required to override this.",
            ["errors"] = new Dictionary<string, string[]> { ["acknowledgedWarnings"] = [ReasonRequiredCode] },
            ["traceId"] = traceId,
        });

    private static IActionResult Problem(int status, Dictionary<string, object?> body)
    {
        var result = new ObjectResult(body) { StatusCode = status };
        result.ContentTypes.Add("application/problem+json");
        return result;
    }
}

/// <summary>Reads, merges and writes the acknowledgements kept as JSON beside the configured value.</summary>
public static class FloorAckStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static List<StoredFloorAck> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<StoredFloorAck>>(json, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    public static string? Serialize(IReadOnlyCollection<StoredFloorAck> acks)
        => acks.Count == 0 ? null : JsonSerializer.Serialize(acks, Json);

    /// <summary>The stored answer to <paramref name="warning"/>: same code AND same configured value, else null.
    /// Changing the value again asks again.</summary>
    public static StoredFloorAck? Covering(IEnumerable<StoredFloorAck> stored, FloorWarning warning)
        => stored.FirstOrDefault(a => a.Code == warning.Code && a.ConfiguredValue == warning.ConfiguredValue);

    /// <summary>
    /// The acknowledgements to keep after a save: those from earlier that still answer a live warning, plus
    /// the ones this request just made. A warning that no longer applies drops its acknowledgement.
    /// </summary>
    public static List<StoredFloorAck> Merge(
        IEnumerable<StoredFloorAck> previous, IReadOnlyCollection<FloorWarning> liveWarnings,
        IEnumerable<(FloorWarning Warning, FloorAcknowledgement Ack)> consumed, string byName, Guid? byId, DateTime nowUtc)
    {
        var result = new List<StoredFloorAck>();
        var fresh = consumed.ToList();
        foreach (var w in liveWarnings)
        {
            var made = fresh.FirstOrDefault(c => c.Warning.Code == w.Code);
            if (made.Warning is not null)
                result.Add(new StoredFloorAck(w.Code, w.Field, w.ConfiguredValue, w.StatutoryValue, w.Citation,
                    made.Ack.Reason!.Trim(), byName, byId, nowUtc));
            else if (Covering(previous, w) is { } kept)
                result.Add(kept);
        }
        return result;
    }
}
