using Zayra.Api.Application.Employees.Onboarding;
using Zayra.Api.Models;

namespace Zayra.Api.Application.Employees.Ess;

/// <summary>
/// The six ESS profile sections: which fields each one has, how "missing" is derived, how a value is masked on read, and
/// how an accepted section is written to the employee. The field list is a WHITELIST: a key that is not here is refused
/// (<c>unknown_field</c>), so no section can ever carry status, company, grade, pay or role.
/// </summary>
public static class ProfileSectionRegistry
{
    private static readonly string[] MaritalOptions = ["Single", "Married", "Divorced", "Widowed"];
    private const string L = "ess.completion.field.";

    private static EssFieldSpec Spec(string key, int max, bool required, EssFieldKind kind, bool sensitive = false, IReadOnlyList<string>? options = null) =>
        new(key, L + key, max, required, kind, sensitive, options);

    private static readonly EssFieldSpec[] PersonFields =
    [
        Spec("preferredName", 120, false, EssFieldKind.Text),
        Spec("personalEmail", 180, true, EssFieldKind.Email),
        Spec("phone", 60, true, EssFieldKind.Phone),
        Spec("maritalStatus", 60, false, EssFieldKind.Enum, options: MaritalOptions),
    ];
    private static readonly EssFieldSpec[] EmergencyFields =
    [
        Spec("emergencyContactName", 180, true, EssFieldKind.Text),
        Spec("emergencyContactPhone", 60, true, EssFieldKind.Phone),
    ];
    private static readonly EssFieldSpec[] ArabicFields = [Spec("arabicName", 150, true, EssFieldKind.Text)];
    private static readonly EssFieldSpec[] SaudiIdFields = [Spec("nationalId", 10, true, EssFieldKind.NationalId, sensitive: true)];
    private static readonly EssFieldSpec[] ExpatIdFields =
    [
        Spec("iqamaNumber", 10, false, EssFieldKind.Iqama, sensitive: true),
        Spec("borderNumber", 10, false, EssFieldKind.Border, sensitive: true),
    ];
    private static readonly EssFieldSpec[] BankFields =
    [
        Spec("iban", 34, true, EssFieldKind.Iban, sensitive: true),
        Spec("bankName", 120, false, EssFieldKind.BankName),
    ];

    public static IReadOnlyList<EssSectionSpec> All { get; } =
    [
        new(EssSectionKeys.Person, false, PersonFields),
        new(EssSectionKeys.Emergency, false, EmergencyFields),
        new(EssSectionKeys.ArabicName, false, ArabicFields),
        new(EssSectionKeys.Id, true, ExpatIdFields),   // the field list of the id section depends on nationality: see FieldsFor
        new(EssSectionKeys.Bank, true, BankFields),
        new(EssSectionKeys.Documents, false, []),
    ];

    public static EssSectionSpec? Find(string key) => All.FirstOrDefault(s => s.Key == key);

    /// <summary>A Saudi holds a National ID; everyone else (and an employee whose nationality is not yet classified) an Iqama or a Border Number.</summary>
    public static IReadOnlyList<EssFieldSpec> FieldsFor(string key, NationalityClass? nationalityClass) =>
        key == EssSectionKeys.Id && nationalityClass == NationalityClass.Saudi ? SaudiIdFields : (Find(key)?.Fields ?? []);

    public static IReadOnlyList<string> UnknownFields(string key, NationalityClass? nationalityClass, IEnumerable<string> fields)
    {
        var known = FieldsFor(key, nationalityClass).Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        return fields.Where(f => !known.Contains(f)).ToList();
    }

    public static string CurrentValue(string key, string field, Employee e) => (key, field) switch
    {
        (EssSectionKeys.Person, "preferredName") => e.PreferredName,
        (EssSectionKeys.Person, "personalEmail") => e.PersonalEmail,
        (EssSectionKeys.Person, "phone") => e.Phone,
        (EssSectionKeys.Person, "maritalStatus") => e.MaritalStatus,
        (EssSectionKeys.Emergency, "emergencyContactName") => e.EmergencyContactName,
        (EssSectionKeys.Emergency, "emergencyContactPhone") => e.EmergencyContactPhone,
        (EssSectionKeys.ArabicName, "arabicName") => e.ArabicName,
        (EssSectionKeys.Id, "nationalId") => e.IdNumber,
        (EssSectionKeys.Id, "iqamaNumber") => e.IqamaNumber,
        (EssSectionKeys.Id, "borderNumber") => e.BorderNumber,
        (EssSectionKeys.Bank, "iban") => e.BankIban,
        (EssSectionKeys.Bank, "bankName") => e.BankName,
        _ => string.Empty,
    } ?? string.Empty;

    public static bool IsMissing(string key, Employee employee, EssEmployeeFacts facts) => key switch
    {
        EssSectionKeys.Person => Blank(employee.PersonalEmail) || Blank(employee.Phone),
        EssSectionKeys.Emergency => Blank(employee.EmergencyContactName) || Blank(employee.EmergencyContactPhone),
        EssSectionKeys.ArabicName => Blank(employee.ArabicName),
        EssSectionKeys.Id => facts.NationalityClass == NationalityClass.Saudi
            ? Blank(employee.IdNumber)
            : Blank(employee.IqamaNumber) && Blank(employee.BorderNumber),
        EssSectionKeys.Bank => Blank(employee.BankIban),
        EssSectionKeys.Documents => !facts.HasIdDocument,
        _ => false,
    };

    public static IReadOnlyList<string> MissingSections(Employee employee, EssEmployeeFacts facts) =>
        EssSectionKeys.All.Where(k => IsMissing(k, employee, facts)).ToList();

    /// <summary>Last four characters visible, the rest hidden. An IBAN keeps its country code and its 4-character grouping.</summary>
    public static string Mask(EssFieldSpec field, string? value)
    {
        var clean = (value ?? string.Empty).Replace(" ", "");
        if (clean.Length == 0) return string.Empty;
        if (!field.Sensitive) return value!;
        if (clean.Length <= 4) return new string('*', clean.Length);
        if (field.Kind == EssFieldKind.Iban)
        {
            var masked = clean[..2] + new string('*', Math.Max(0, clean.Length - 6)) + clean[^4..];
            return string.Join(' ', Enumerable.Range(0, (masked.Length + 3) / 4).Select(i => masked.Substring(i * 4, Math.Min(4, masked.Length - i * 4))));
        }
        return new string('*', clean.Length - 4) + clean[^4..];
    }

    public static IReadOnlyList<string> SectionsForLegacyReason(string? reason) =>
        string.Equals(reason, EssCompletion.LegacyReason, StringComparison.Ordinal) ? [EssSectionKeys.Person, EssSectionKeys.Emergency] : [];

    /// <summary>Writes an accepted section onto the employee. The bank section is refused here by design: it goes through Finance approval.</summary>
    public static void Apply(string key, Employee e, IReadOnlyDictionary<string, string> values)
    {
        string V(string f) => values.TryGetValue(f, out var v) ? v.Trim() : string.Empty;
        bool Has(string f) => values.ContainsKey(f);
        switch (key)
        {
            case EssSectionKeys.Bank:
                throw new InvalidOperationException("A bank change is never applied by accepting a section; it is approved by Finance.");
            case EssSectionKeys.Person:
                if (Has("preferredName")) e.PreferredName = V("preferredName");
                if (Has("personalEmail")) e.PersonalEmail = V("personalEmail");
                if (Has("phone")) e.Phone = V("phone");
                if (Has("maritalStatus")) e.MaritalStatus = V("maritalStatus");
                break;
            case EssSectionKeys.Emergency:
                if (Has("emergencyContactName")) e.EmergencyContactName = V("emergencyContactName");
                if (Has("emergencyContactPhone")) e.EmergencyContactPhone = V("emergencyContactPhone");
                break;
            case EssSectionKeys.ArabicName:
                if (Has("arabicName")) e.ArabicName = V("arabicName");
                break;
            case EssSectionKeys.Id:
                if (Has("nationalId")) { e.IdNumber = V("nationalId"); e.IdType = "NationalId"; }
                if (Has("iqamaNumber")) e.IqamaNumber = V("iqamaNumber");
                if (Has("borderNumber")) e.BorderNumber = V("borderNumber");
                break;
            case EssSectionKeys.Documents:
                break;   // the attached files are the section; nothing is written to the employee row
            default:
                throw new InvalidOperationException($"Unknown section '{key}'.");
        }
    }

    private static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);
}
