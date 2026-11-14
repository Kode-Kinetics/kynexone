namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// The canonical values of <c>Employee.IdType</c>. The column reaches the database in more than one spelling: the hire wizard derives
/// <see cref="NationalId"/> (<see cref="EmployeeManagementService.PrimaryIdType"/>), while the profile screen and the CSV import keep the
/// displayed label ("National ID", "Iqama (residence permit)"). Every reader compares through <see cref="Canonical"/>, never against a
/// literal, so no spelling makes an ID disappear from a wage file.
/// </summary>
public static class GovernmentIdTypes
{
    public const string NationalId = "NationalId";
    public const string Iqama = "Iqama";
    public const string BorderNumber = "BorderNumber";
    public const string Passport = "Passport";

    /// <summary>The canonical type for any stored spelling: letters only, case-insensitive. A type this list does not name (another
    /// country's card, e.g. "CivilId") is returned trimmed, as stored. Null for an empty value.</summary>
    public static string? Canonical(string? idType)
    {
        if (string.IsNullOrWhiteSpace(idType)) return null;
        var key = new string(idType.Where(char.IsLetter).ToArray()).ToLowerInvariant();
        return key switch
        {
            "nationalid" or "nationalidentity" or "nationalidhawiyya" or "hawiyya" or "idnumber" or "saudinationalid" => NationalId,
            "iqama" or "iqamanumber" or "iqamaresidencepermit" or "iqamaresidencepermitno" => Iqama,
            "bordernumber" => BorderNumber,
            "passport" or "passportnumber" => Passport,
            _ => idType.Trim(),
        };
    }

    /// <summary>True when the stored type names a Saudi National ID or an Iqama, in any spelling.</summary>
    public static bool IsNationalIdOrIqama(string? idType) => Canonical(idType) is NationalId or Iqama;
}
