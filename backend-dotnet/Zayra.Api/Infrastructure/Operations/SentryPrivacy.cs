using System.Text.RegularExpressions;

namespace Zayra.Api.Infrastructure.Operations;

/// <summary>
/// The text-level scrub behind error tracking. Holds no Sentry type, so it is unit-testable on its own and
/// <see cref="Observability"/> stays free of the Sentry assemblies. Used by <see cref="SentryRegistration"/>,
/// the single place that decides what an event may carry.
///
/// <para>Redacts: bearer tokens, JWTs, emails, IBANs, Saudi national / iqama ids (10 digits starting 1 or 2),
/// any other run of 6+ digits (salary and phone shapes), and query strings.</para>
/// </summary>
public static partial class SentryPrivacy
{
    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9._~+/=\-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Bearer();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]*", RegexOptions.CultureInvariant)]
    private static partial Regex Jwt();

    [GeneratedRegex(@"[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}[A-Z0-9]{10,30}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Iban();

    [GeneratedRegex(@"(?<![\w.\-])[12]\d{9}(?![\w\-])", RegexOptions.CultureInvariant)]
    private static partial Regex NationalId();

    [GeneratedRegex(@"(?<![\w.\-])\d{6,}(?![\w\-])", RegexOptions.CultureInvariant)]
    private static partial Regex LongDigits();

    // Money: any amount with decimals ("15750.50", "6,900.00"), and a whole number after a pay word ("salary 15750").
    [GeneratedRegex(@"(?<![\w.\-])\d{1,3}(?:[,\d]*\d)?[.,]\d{1,3}(?![\w\-])", RegexOptions.CultureInvariant)]
    private static partial Regex DecimalAmount();

    [GeneratedRegex(@"\b(salary|basic|housing|allowance|deduction|gross|net|pay|wage|amount|bonus|loan|راتب|الراتب|بدل|خصم)(\s*[:=]?\s*)[\d,]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PayWordAmount();

    // A '?' glued to a preceding token and followed by non-space: "/api/payroll?salary=1" but not "Why? Because".
    [GeneratedRegex(@"(?<=\S)\?[^\s""'<>#]+", RegexOptions.CultureInvariant)]
    private static partial Regex QueryString();

    public static string ScrubText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        var t = Bearer().Replace(text, "Bearer [redacted]");
        t = Jwt().Replace(t, "[token]");
        t = Email().Replace(t, "[email]");
        t = QueryString().Replace(t, "?[redacted]");
        t = Iban().Replace(t, "[iban]");
        t = NationalId().Replace(t, "[national-id]");
        t = PayWordAmount().Replace(t, "$1$2[amount]");
        t = DecimalAmount().Replace(t, "[amount]");
        return LongDigits().Replace(t, "[number]");
    }

    /// <summary>The URL without its query string or fragment, then text-scrubbed (path ids can be personal).</summary>
    public static string? StripQuery(string? url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        var cut = url.IndexOfAny(['?', '#']);
        return ScrubText(cut >= 0 ? url[..cut] : url);
    }

    /// <summary>True when any value is a URL carrying a query string or still has personal-data shapes after scrubbing.</summary>
    public static bool IsDirty(string? text) =>
        !string.IsNullOrEmpty(text) && !string.Equals(ScrubText(text), text, StringComparison.Ordinal);

    /// <summary>Only a canonical GUID may be sent as the tenant tag; anything else is refused.</summary>
    public static bool TryOpaqueTenantId(string? raw, out string tenantId)
    {
        tenantId = string.Empty;
        if (!Guid.TryParse(raw, out var guid)) return false;
        tenantId = guid.ToString("D");
        return true;
    }

    /// <summary>SENTRY_TRACES_SAMPLE_RATE as 0..1; unset, malformed or out of range means 0 (tracing off).</summary>
    public static double ParseTracesSampleRate(string? raw) =>
        double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rate)
        && rate is >= 0 and <= 1 ? rate : 0d;
}
