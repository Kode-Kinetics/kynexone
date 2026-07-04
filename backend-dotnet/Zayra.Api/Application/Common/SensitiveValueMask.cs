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
    /// Deterministic change marker: reveals nothing about the value but changes when the
    /// value changes, so audit trails can still show that a sensitive field was modified.
    /// </summary>
    public static string HashMarker(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return "sha256:" + Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }
}
