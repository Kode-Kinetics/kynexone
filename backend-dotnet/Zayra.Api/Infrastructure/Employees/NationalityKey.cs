using Zayra.Api.Infrastructure.Leave;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// Compares two recorded nationalities as the same country, not the same spelling: the hire wizard now stores the
/// catalog's country name ("Pakistan") where staff and imports used to store a demonym ("Pakistani"), and the duplicate
/// detector must see those as one signal (CD24). Unrecognised text falls back to a trimmed, case-insensitive compare.
/// </summary>
public static class NationalityKey
{
    public static string Of(string? nationality)
    {
        var text = (nationality ?? string.Empty).Trim();
        if (text.Length == 0) return string.Empty;
        return LeavePolicyScope.NationalityIso2(text) ?? text.ToUpperInvariant();
    }

    public static bool Same(string? a, string? b)
    {
        var ka = Of(a);
        return ka.Length > 0 && string.Equals(ka, Of(b), StringComparison.Ordinal);
    }
}
