using Zayra.Api.Application.Onboarding;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// PAY-08 — advisory only. A Saudi national ID starts with 1; a resident (iqama) number starts with 2. A prefix that
/// contradicts the recorded nationality is worth a second look but is never a reason to refuse the save, so this
/// returns warning texts and nothing else. The number itself is never echoed.
/// </summary>
public static class IdentityNationalityConsistency
{
    public static IReadOnlyList<string> Warnings(string? nationality, string? idNumber, string? iqamaNumber)
    {
        var warnings = new List<string>();
        var id = (idNumber ?? string.Empty).Trim();
        var iqama = (iqamaNumber ?? string.Empty).Trim();
        var nationalityClass = NationalityClasses.Of(nationality);

        if (id.Length > 0)
        {
            if (nationalityClass == NationalityClasses.Saudi && id[0] == '2')
                warnings.Add("The national ID starts with 2 (a resident number), but a Saudi national's ID starts with 1. Check the nationality and the ID number.");
            else if (nationalityClass == NationalityClasses.Expat && id[0] == '1')
                warnings.Add("The national ID starts with 1 (a Saudi national ID), but the nationality is not Saudi and a resident number starts with 2. Check the nationality and the ID number.");
        }
        if (iqama.Length > 0 && iqama[0] == '1')
            warnings.Add("The iqama number starts with 1, but a resident (iqama) number starts with 2. Check the number.");
        return warnings;
    }
}
