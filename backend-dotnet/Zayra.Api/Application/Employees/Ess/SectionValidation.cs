using Zayra.Api.Application.Employees.Onboarding;

namespace Zayra.Api.Application.Employees.Ess;

/// <summary>Thin adapter over the P1 validators (checksum, IBAN, E.164, e-mail) in "section mode".</summary>
public static class SectionValidation
{
    public static Dictionary<string, List<string>> Validate(string sectionKey, NationalityClass? nationalityClass,
        string? companyCountry, IReadOnlyDictionary<string, string> values, bool requireComplete = false) => throw new NotImplementedException();

    public static Dictionary<string, string> Normalise(string sectionKey, NationalityClass? nationalityClass, string? companyCountry,
        IReadOnlyDictionary<string, string> values) => throw new NotImplementedException();
}
