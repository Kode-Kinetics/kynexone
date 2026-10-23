using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Compliance;
using Zayra.Api.Infrastructure.Contracts;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Setup;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Leave;

/// <summary>
/// A leave policy's Employment Type scope is a LIST: one policy can cover Full-Time and Part-Time
/// staff together. It is stored comma-separated in the existing <c>employment_type</c> column, so a
/// row written before the list existed (one value, or blank = every employment type) reads back
/// unchanged. Every reader goes through here; none splits the string itself.
/// </summary>
internal static class LeavePolicyScope
{
    /// <summary>The employment types a stored scope names; empty means "every employment type".</summary>
    public static string[] EmploymentTypes(string? stored) =>
        string.IsNullOrWhiteSpace(stored)
            ? Array.Empty<string>()
            : stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    /// <summary>The canonical stored form: trimmed, de-duplicated, comma-joined, in the order given.</summary>
    public static string NormalizeEmploymentTypes(string? value) => string.Join(",", EmploymentTypes(value));

    /// <summary>
    /// Does this scope admit an employee of <paramref name="employeeType"/>? A blank scope admits
    /// everyone; otherwise ANY listed type may match, code or label (<see cref="ReferenceList.Same"/>).
    /// </summary>
    public static bool AdmitsEmploymentType(string? stored, string? employeeType, ReferenceList list)
    {
        var types = EmploymentTypes(stored);
        return types.Length == 0 || types.Any(t => list.Same(t, employeeType));
    }

    /// <summary>Do two scopes name the same set of employment types (order and case ignored)?</summary>
    public static bool SameEmploymentTypes(string? left, string? right)
    {
        var a = EmploymentTypes(left);
        var b = EmploymentTypes(right);
        return a.Length == b.Length && a.All(t => b.Contains(t, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Does scope <paramref name="broader"/> admit everyone scope <paramref name="narrower"/> admits?</summary>
    public static bool CoversEmploymentTypes(string? broader, string? narrower)
    {
        var a = EmploymentTypes(broader);
        if (a.Length == 0) return true;
        var b = EmploymentTypes(narrower);
        return b.Length > 0 && b.All(t => a.Contains(t, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Could one employee fall under both scopes?</summary>
    public static bool EmploymentTypesOverlap(string? left, string? right)
    {
        var a = EmploymentTypes(left);
        var b = EmploymentTypes(right);
        return a.Length == 0 || b.Length == 0 || a.Intersect(b, StringComparer.OrdinalIgnoreCase).Any();
    }

    /// <summary>
    /// Does a nationality scope admit this employee? <paramref name="companyCountry"/> is the employee's company
    /// country (ISO-2 or ISO-3). An unknown nationality or company country admits only the "Any" scope.
    /// </summary>
    public static bool AdmitsNationality(string? scope, string? nationality, string? companyCountry)
    {
        if (string.IsNullOrWhiteSpace(scope) || scope == LeaveNationalityScopes.Any) return true;
        if (IsNational(nationality, companyCountry) is not { } national) return false;
        return scope == LeaveNationalityScopes.National ? national : !national;
    }

    /// <summary>
    /// A country as ISO-2 for a leave-policy comparison: an ISO-2/ISO-3 code, or the two Gulf aliases the leave policy form
    /// still offers and older companies were saved with (KSA, UAE). #258 backfills companies KSA→SA and UAE→AE, so a
    /// comparison that did not know the aliases would stop matching every "KSA"/"UAE" policy to its own company.
    /// </summary>
    internal static string? CountryIso2(string? value) =>
        value?.Trim().ToUpperInvariant() switch
        {
            "KSA" => "SA",
            "UAE" => "AE",
            _ => CountryCodeStandard.NormalizeToIso2(value),
        };

    /// <summary>True/false when the nationality can be placed against the company country; null when it cannot.</summary>
    internal static bool? IsNational(string? nationality, string? companyCountry)
    {
        if (string.IsNullOrWhiteSpace(nationality)) return null;
        var country = CountryIso2(companyCountry);
        if (country is null) return null;
        return NationalityIso2(nationality) is { } iso2 ? iso2 == country : null;
    }

    /// <summary>
    /// The country a recorded nationality names, through the product's existing readers: the Saudi spellings, the GCC
    /// spellings (Emirati, Kuwaiti, …), then an ISO code, the reference country name or its demonym (Indian, Egyptian).
    /// Null when unrecognised — a typo ("Suadi") or "-" is never read as an expat.
    /// </summary>
    internal static string? NationalityIso2(string nationality) =>
        SaudiNationality.IsSaudi(nationality) ? "SA"
        : GosiCalculationService.DeriveGccHomeState(nationality) ?? WorkerNationality.IsoOf(nationality);

    /// <summary>An order- and case-insensitive key for duplicate detection.</summary>
    public static string EmploymentTypesKey(string? stored) =>
        string.Join(",", EmploymentTypes(stored).Select(t => t.ToLowerInvariant()).Order(StringComparer.Ordinal));
}
