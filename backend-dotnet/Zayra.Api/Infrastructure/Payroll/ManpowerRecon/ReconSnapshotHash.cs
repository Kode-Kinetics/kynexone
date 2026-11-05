using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zayra.Api.Infrastructure.Payroll.ManpowerRecon;

/// <summary>
/// The fingerprint of what a reviewer looked at. It covers the bridge (volume, value, final settlement, integrity, the
/// thresholds, the mode and prior run) plus the process versions of the run and of its comparison run, and nothing that
/// varies by caller or by what was written about it: not the sign-off, not the explanations, not the caller's scope.
/// Numbers are written by value in the invariant culture, so 100 and 100.00 are the same figure.
/// </summary>
public static class ReconSnapshotHash
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static string Compute(ReconDto dto, int processVersion, int? priorProcessVersion)
    {
        var node = Summary(dto);
        // Not part of what was reviewed: who is asking, the stamp itself, and whether the company requires a sign-off.
        foreach (var key in new[] { "scope", "snapshotHash", "signoffRequired", "processVersion" }) node.Remove(key);
        var canonical = new StringBuilder();
        Write(canonical, node);
        canonical.Append("|pv=").Append(processVersion.ToString(CultureInfo.InvariantCulture));
        canonical.Append("|ppv=").Append(priorProcessVersion?.ToString(CultureInfo.InvariantCulture) ?? "-");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    /// <summary>The summary stored with a sign-off: the bridge as reviewed (with its stamp), with no sign-off, no explanation text, no pay by employee and no employee names.</summary>
    public static string SummaryJson(ReconDto dto) => Summary(dto).ToJsonString(Web);

    private static JsonObject Summary(ReconDto dto)
    {
        var node = JsonSerializer.SerializeToNode(dto with { Signoff = null }, Web)!.AsObject();
        StripExplanations(node);
        StripNames(node);
        return node;
    }

    /// <summary>
    /// The kept record names nobody: a leaver without a final settlement is held by id and code, and the live reading is where
    /// the name is shown. Because the fingerprint is computed from the same summary, a name is not part of what was reviewed.
    /// </summary>
    private static void StripNames(JsonObject summary)
    {
        if (summary["finalSettlement"]?["leaversWithoutFs"] is JsonArray leavers)
            foreach (var leaver in leavers.OfType<JsonObject>()) leaver.Remove("employeeName");
    }

    private static void StripExplanations(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                o.Remove("explanation");
                foreach (var (_, v) in o.ToList()) StripExplanations(v);
                break;
            case JsonArray a:
                foreach (var v in a) StripExplanations(v);
                break;
        }
    }

    private static void Write(StringBuilder sb, JsonNode? node)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject o:
                sb.Append('{');
                var first = true;
                foreach (var (k, v) in o.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(k)).Append(':');
                    Write(sb, v);
                }
                sb.Append('}');
                break;
            case JsonArray a:
                sb.Append('[');
                for (var i = 0; i < a.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Write(sb, a[i]);
                }
                sb.Append(']');
                break;
            case JsonValue v:
                switch (v.GetValueKind())
                {
                    case JsonValueKind.Number:
                        sb.Append(v.GetValue<decimal>().ToString("0.################", CultureInfo.InvariantCulture));
                        break;
                    case JsonValueKind.True: sb.Append("true"); break;
                    case JsonValueKind.False: sb.Append("false"); break;
                    default: sb.Append(v.ToJsonString()); break;
                }
                break;
        }
    }
}
