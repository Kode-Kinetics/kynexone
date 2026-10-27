using Zayra.Api.Application.Employees.Onboarding;
using Zayra.Api.Models;

namespace Zayra.Api.Application.Employees.Ess;

/// <summary>The six ESS profile sections, their fields, how "missing" is derived, and how an accepted section is applied.</summary>
public static class ProfileSectionRegistry
{
    public static IReadOnlyList<EssSectionSpec> All => throw new NotImplementedException();
    public static EssSectionSpec? Find(string key) => throw new NotImplementedException();
    public static IReadOnlyList<EssFieldSpec> FieldsFor(string key, NationalityClass? nationalityClass) => throw new NotImplementedException();
    public static bool IsMissing(string key, Employee employee, EssEmployeeFacts facts) => throw new NotImplementedException();
    public static IReadOnlyList<string> MissingSections(Employee employee, EssEmployeeFacts facts) => throw new NotImplementedException();
    public static string CurrentValue(string key, string field, Employee employee) => throw new NotImplementedException();
    public static string Mask(EssFieldSpec field, string? value) => throw new NotImplementedException();
    public static IReadOnlyList<string> SectionsForLegacyReason(string? reason) => throw new NotImplementedException();
    public static IReadOnlyList<string> UnknownFields(string key, NationalityClass? nationalityClass, IEnumerable<string> fields) => throw new NotImplementedException();
    /// <summary>Writes accepted values onto the employee. NEVER handles <c>bank</c> (that goes through Finance approval).</summary>
    public static void Apply(string key, Employee employee, IReadOnlyDictionary<string, string> values) => throw new NotImplementedException();
}
