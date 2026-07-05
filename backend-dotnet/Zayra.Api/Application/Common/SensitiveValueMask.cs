using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Zayra.Api.Application.Common;

/// <summary>
/// Masking primitives for sensitive values that must never be persisted or logged raw
/// (audit/history snapshots, LLM payloads). Complements EmployeeSensitiveMask, which
/// clears fields on API read paths — these helpers preserve auditability (last-4 masks,
/// deterministic change markers) without storing the raw value anywhere.
/// </summary>
public static class SensitiveValueMask
{
    public const string Redacted = "[REDACTED]";

    /// <summary>Masks an identifier keeping only the last 4 characters: "SA4420000001234" → "***1234".</summary>
    public static string MaskId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();
        return trimmed.Length <= 4 ? "***" : "***" + trimmed[^4..];
    }

    /// <summary>
    /// Environment variable holding the server-side audit marker secret. Never stored in
    /// the database; rotate via deployment config.
    /// </summary>
    public const string AuditSecretEnvVar = "ZAYRA_AUDIT_HMAC_SECRET";

    /// <summary>
    /// Change marker for low-entropy sensitive values (salary and similar): reveals
    /// nothing about the value but changes when the value changes, so audit trails can
    /// still show that a sensitive field was modified.
    ///
    /// A plain unsalted digest is NOT safe here — salaries have so little entropy that a
    /// deterministic SHA-256 can be reversed by enumerating plausible amounts. We therefore
    /// use HMAC-SHA256 keyed with a server-side secret (env var above). When no secret is
    /// configured the marker degrades to full redaction rather than emitting a crackable
    /// digest.
    /// </summary>
    public static string HashMarker(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var secret = Environment.GetEnvironmentVariable(AuditSecretEnvVar);
        if (string.IsNullOrWhiteSpace(secret)) return Redacted;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "hmac:" + Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(text)))[..12].ToLowerInvariant();
    }
}
