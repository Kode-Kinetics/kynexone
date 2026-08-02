namespace Zayra.Api.Models;

/// <summary>
/// The configurable vocabulary for how an employee work-email local part is formed from the name.
/// This is the "pattern is data, not hard-coded" anchor: a company stores its SELECTION as a string
/// column (Company.WorkEmailPattern); the VOCABULARY of allowed selections lives here as constants so
/// derivation, validation, and the preview endpoint all agree on the same closed set.
/// </summary>
public static class WorkEmailPatterns
{
    /// <summary>john.smith — first name, dot, last name. The default.</summary>
    public const string FirstLast = "first.last";

    /// <summary>jsmith — first initial + last name.</summary>
    public const string Flast = "flast";

    /// <summary>john — first name only.</summary>
    public const string First = "first";

    /// <summary>john_smith — first name, underscore, last name.</summary>
    public const string FirstUnderscoreLast = "first_last";

    /// <summary>All recognized patterns (for UI selection + validation).</summary>
    public static readonly IReadOnlyList<string> All = new[] { FirstLast, Flast, First, FirstUnderscoreLast };

    /// <summary>True when <paramref name="pattern"/> is a recognized selection (case-insensitive).</summary>
    public static bool IsValid(string? pattern) =>
        pattern is not null && All.Contains(pattern.Trim().ToLowerInvariant());

    /// <summary>Coerce any input to a valid pattern: a recognized value (lowercased) or the default.
    /// Lenient-by-design so a bad stored/submitted value can never break derivation — it just falls
    /// back to first.last.</summary>
    public static string Normalize(string? pattern) =>
        IsValid(pattern) ? pattern!.Trim().ToLowerInvariant() : FirstLast;
}
