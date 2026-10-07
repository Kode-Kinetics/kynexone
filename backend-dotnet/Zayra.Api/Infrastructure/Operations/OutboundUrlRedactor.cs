using System.Text;
using System.Text.RegularExpressions;

namespace Zayra.Api.Infrastructure.Operations;

/// <summary>
/// The URL an outbound HTTP span is allowed to carry: scheme, host, port and a TEMPLATED path, never the query.
/// The Qiwa status lookup is <c>/api/v1/establishments/{establishment}/employees/{national ID or iqama}</c>, and
/// an attendance device's pull URL is tenant-configured and may hold credentials in its query string, so the raw
/// <c>url.full</c> would ship personal data and secrets to the telemetry backend.
///
/// <para>Rules: the segment after <c>establishments</c> / <c>employees</c> (any host) becomes <c>{id}</c>; so does
/// any other segment containing a digit, except an API version (<c>v1</c>). No OpenTelemetry type is referenced
/// here so the policy is unit-testable and <see cref="Observability"/> stays OTel-free.</para>
/// </summary>
public static partial class OutboundUrlRedactor
{
    private static readonly HashSet<string> IdBearingParents = new(StringComparer.OrdinalIgnoreCase)
    {
        "establishments", "employees", "employee", "workers", "residents", "iqamas", "persons", "people", "contracts",
    };

    [GeneratedRegex(@"^v[0-9]+(\.[0-9]+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ApiVersion();

    public static string TemplatePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path == "/") return path ?? string.Empty;
        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var s = segments[i];
            if (s.Length == 0) continue;
            var afterIdParent = i > 0 && IdBearingParents.Contains(Uri.UnescapeDataString(segments[i - 1]));
            if (afterIdParent || s.Any(char.IsDigit) && !ApiVersion().IsMatch(s))
                segments[i] = "{id}";
        }
        return string.Join('/', segments);
    }

    /// <summary>scheme://host[:port]/templated/path — no query, no fragment, no user info.</summary>
    public static string Redact(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri) return string.Empty;
        var sb = new StringBuilder();
        sb.Append(uri.Scheme).Append("://").Append(uri.Host);
        if (!uri.IsDefaultPort) sb.Append(':').Append(uri.Port);
        sb.Append(TemplatePath(uri.AbsolutePath));
        return sb.ToString();
    }
}
