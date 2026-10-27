namespace Zayra.Api.Infrastructure.Operations;

/// <summary>
/// Blanks the free-text reasons some endpoints still accept in the query string, for anything that records a request URL (traces). The reasons are
/// kept for the audit trail only, never in logs.
/// </summary>
public static class QueryRedactor
{
    private static readonly string[] Names = ["reason", "costCenterReason"];

    public static string Redact(string? query)
    {
        if (string.IsNullOrEmpty(query)) return query ?? string.Empty;
        var lead = query[0] == '?' ? "?" : string.Empty;
        var pairs = query[lead.Length..].Split('&').Select(pair =>
        {
            var eq = pair.IndexOf('=');
            return eq > 0 && Names.Any(n => string.Equals(n, pair[..eq], StringComparison.OrdinalIgnoreCase)) ? pair[..eq] + "=[redacted]" : pair;
        });
        return lead + string.Join('&', pairs);
    }
}
