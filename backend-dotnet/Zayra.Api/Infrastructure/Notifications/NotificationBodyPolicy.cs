using System.Globalization;
using System.Text.RegularExpressions;

namespace Zayra.Api.Infrastructure.Notifications;

/// <summary>
/// POD-D5 — deterministic, template-driven message rendering and the PRIVACY guard for short
/// channels. There is deliberately NO AI in this file (product rule: AI is opt-in advisory only,
/// never in the compliance/correctness path). Every body here comes from a tenant template or a
/// compiled default, and every channel decision comes from tenant config + employee preference.
/// </summary>
public static partial class NotificationBodyPolicy
{
    // Historic bug: NotificationService.Interpolate built "{{Key}}" while every seeded template
    // (TenantProvisioningBundle NotificationSeeds) uses single braces "{Key}" — so interpolation
    // never once fired and a real phone would have received the literal text "{Period}".
    // Both forms are handled now; double-brace first so "{{X}}" cannot be half-eaten.
    [GeneratedRegex(@"\{\{([A-Za-z0-9_]+)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex DoubleBraceToken();

    [GeneratedRegex(@"\{([A-Za-z0-9_]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex SingleBraceToken();

    /// <summary>Amount-ish tokens: 1,234.00 / 12.50 / SAR 900 / ﷼ / $ — never allowed on a short channel.</summary>
    [GeneratedRegex(@"(\d[\d,]*\.\d{1,2})|(\b(SAR|AED|KWD|BHD|QAR|OMR|EGP|JOD|USD|EUR|GBP|INR|PKR)\b)|[$€£¥﷼]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MonetaryToken();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailToken();

    [GeneratedRegex(@"[+]?\d[\d\s\-()]{5,}\d", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneishToken();

    /// <summary>
    /// The ONLY variables a short-channel body may interpolate. Allow-list, not block-list —
    /// a future template variable is denied by default rather than leaking by default.
    /// Deliberately excludes every money, bank, and identity variable.
    /// </summary>
    public static readonly IReadOnlySet<string> ShortChannelVariables =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "EmployeeFirstName", "EmployeeName", "FullName",
            "Period", "Month", "Year", "StartDate", "EndDate",
            "RequestType", "Status", "Subject", "LeaveType",
            "PortalLink", "CompanyName",
        };

    /// <summary>
    /// Compiled short-channel bodies. Used when the tenant enabled the channel but wrote no body.
    /// Never contain net pay, gross, IBAN, national id, or bank details — only "there is something
    /// waiting for you, sign in to see it".
    /// </summary>
    private static readonly Dictionary<string, (string Subject, string Body)> ShortDefaults =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["PAYSLIP_READY"] = ("Payslip ready", "Your payslip for {Period} is ready. Sign in to view it."),
            ["LEAVE_APPROVED"] = ("Leave approved", "Your leave request was approved. Sign in for details."),
            ["LEAVE_REJECTED"] = ("Leave update", "Your leave request was not approved. Sign in for details."),
            ["HR_REQUEST_UPDATE"] = ("HR request update", "Your HR request status changed. Sign in for details."),
            ["APPROVAL_PENDING"] = ("Approval waiting", "An item is waiting for your approval. Sign in to review it."),
        };

    public static (string Subject, string Body)? ShortChannelDefault(string eventCode) =>
        ShortDefaults.TryGetValue(eventCode, out var v) ? v : null;

    /// <summary>
    /// Single-brace (and double-brace) interpolation. When <paramref name="htmlEncodeValues"/> is
    /// true each VALUE is HTML-encoded — not the template — so an employee-controlled name cannot
    /// inject markup into the email body (the old SendEmailAsync spliced the body in raw).
    /// Unknown tokens are LEFT IN PLACE so <see cref="HasUnresolvedPlaceholder"/> can fail closed.
    /// </summary>
    public static string Interpolate(string template, IReadOnlyDictionary<string, string>? vars,
        bool htmlEncodeValues, IReadOnlySet<string>? allowedVariables = null)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;

        string Resolve(Match m)
        {
            var key = m.Groups[1].Value;
            if (allowedVariables is not null && !allowedVariables.Contains(key))
                return string.Empty;                       // denied by allow-list: dropped, not leaked
            if (vars is null || !TryGet(vars, key, out var value))
                return m.Value;                            // unknown: keep the token so we fail closed
            return htmlEncodeValues ? System.Web.HttpUtility.HtmlEncode(value) : value;
        }

        var rendered = DoubleBraceToken().Replace(template, Resolve);
        return SingleBraceToken().Replace(rendered, Resolve);
    }

    private static bool TryGet(IReadOnlyDictionary<string, string> vars, string key, out string value)
    {
        if (vars.TryGetValue(key, out var direct)) { value = direct ?? string.Empty; return true; }
        foreach (var kv in vars)
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
            { value = kv.Value ?? string.Empty; return true; }
        value = string.Empty;
        return false;
    }

    /// <summary>True when a "{Token}" survived rendering — the body is NOT fit to send.</summary>
    public static bool HasUnresolvedPlaceholder(string text) =>
        !string.IsNullOrEmpty(text) && SingleBraceToken().IsMatch(text);

    /// <summary>Removes surviving placeholders. Used ONLY for the in-app fallback, which must stay visible.</summary>
    public static string StripPlaceholders(string text) =>
        string.IsNullOrEmpty(text) ? string.Empty
            : SingleBraceToken().Replace(text, string.Empty).Replace("  ", " ").Trim();

    /// <summary>Final backstop before a short-channel send: no money figures may leave via SMS/WhatsApp/push.</summary>
    public static bool ContainsMonetaryToken(string text) =>
        !string.IsNullOrEmpty(text) && MonetaryToken().IsMatch(text);

    // ── Masking ───────────────────────────────────────────────────────────────

    public static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return string.Empty;
        var at = email.IndexOf('@');
        if (at <= 0) return "•••";
        var local = email[..at];
        var domain = email[(at + 1)..];
        var head = local.Length <= 1 ? local : local[..1];
        var dot = domain.LastIndexOf('.');
        var maskedDomain = dot > 0 ? $"•••{domain[dot..]}" : "•••";
        return $"{head}•••@{maskedDomain}";
    }

    public static string MaskPhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return string.Empty;
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length <= 4) return "•••";
        var prefix = phone.TrimStart().StartsWith('+') ? "+" : string.Empty;
        return $"{prefix}{digits[..Math.Min(3, digits.Length)]}•••{digits[^4..]}";
    }

    public static string MaskDevices(int count) =>
        count == 1 ? "1 device" : $"{count.ToString(CultureInfo.InvariantCulture)} devices";

    /// <summary>
    /// Providers echo the destination back inside error text ("invalid number +9715...").
    /// Persisting that raw would re-introduce the PII we just masked, so scrub before storing.
    /// </summary>
    public static string ScrubProviderError(string? message, int maxLength = 400)
    {
        if (string.IsNullOrWhiteSpace(message)) return string.Empty;
        var scrubbed = EmailToken().Replace(message, "[email]");
        scrubbed = PhoneishToken().Replace(scrubbed, "[number]");
        return scrubbed.Length <= maxLength ? scrubbed : scrubbed[..maxLength];
    }
}
