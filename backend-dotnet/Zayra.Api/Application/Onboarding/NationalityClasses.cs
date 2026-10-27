using Zayra.Api.Infrastructure.Employees;

namespace Zayra.Api.Application.Onboarding;

/// <summary>
/// The hire's nationality class (CD7), used to decide which checklist tasks apply. A Saudi national needs no Iqama or
/// Muqeem work; a GCC national needs neither; an expatriate does. A blank or unrecognised nationality is UNKNOWN, never
/// "expat": only tasks that apply to everyone are generated for it, so HR is not sent after paperwork the person may not need.
/// </summary>
public static class NationalityClasses
{
    public const string Any = "any";
    public const string Saudi = "saudi";
    public const string Gcc = "gcc";
    public const string Expat = "expat";
    public const string Unknown = "unknown";

    /// <summary>The values a template task may carry in its condition.</summary>
    public static readonly string[] Conditions = { Any, Saudi, Gcc, Expat };

    private static readonly HashSet<string> OtherGccStates = new(StringComparer.Ordinal) { "AE", "KW", "BH", "OM", "QA" };

    public static bool IsValidCondition(string? condition) => condition is not null && Conditions.Contains(condition, StringComparer.Ordinal);

    /// <summary>saudi | gcc | expat | unknown, from a stored nationality (ISO code, country name or demonym).</summary>
    public static string Of(string? nationality)
    {
        var key = NationalityKey.Of(nationality);
        if (key.Length == 0) return Unknown;
        if (key == "SA") return Saudi;
        if (OtherGccStates.Contains(key)) return Gcc;
        return key.Length == 2 && key.All(c => c is >= 'A' and <= 'Z') ? Expat : Unknown;
    }

    /// <summary>Whether a task with this condition is generated for a hire of this class.</summary>
    public static bool Applies(string? condition, string nationalityClass) =>
        string.IsNullOrEmpty(condition) || condition == Any || string.Equals(condition, nationalityClass, StringComparison.Ordinal);
}
