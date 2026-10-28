namespace Zayra.Api.Application.Employees.Onboarding;

/// <summary>
/// CD30: whether a new hire is WPS- and end-of-service-eligible by default is a property of the employing company's
/// country, decided on the server. It is never inferred from whether an IBAN or a MOL id happened to be typed.
/// The GCC states run a wage-protection system and a statutory end-of-service gratuity; elsewhere both default off
/// until HR switches them on.
/// </summary>
public static class PayrollEligibilityPolicy
{
    private static readonly HashSet<string> Gcc = new(StringComparer.Ordinal) { "SA", "AE", "QA", "KW", "OM", "BH" };

    public static (bool Wps, bool Eosb) DefaultsFor(string? companyIso2)
    {
        var on = companyIso2 is not null && Gcc.Contains(companyIso2.Trim().ToUpperInvariant());
        return (on, on);
    }
}
