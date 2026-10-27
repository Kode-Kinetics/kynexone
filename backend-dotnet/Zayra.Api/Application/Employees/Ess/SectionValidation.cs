using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees.Onboarding;
using Zayra.Api.Infrastructure.Payroll;

namespace Zayra.Api.Application.Employees.Ess;

/// <summary>
/// Thin adapter over the P1 validators (identity checksum, IBAN, E.164, e-mail) in "section mode": the same rules and the same
/// error codes the Add Employee wizard uses, applied to the whitelisted fields of one section.
/// </summary>
public static class SectionValidation
{
    private static readonly Regex ArabicName = new(@"^[\p{IsArabic}\s'’\-.]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ArabicLetter = new(@"\p{IsArabic}", RegexOptions.Compiled);

    private static string CompanyIso(string? companyCountry) =>
        CountryCodeStandard.NormalizeToIso2(companyCountry) ?? (companyCountry ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>
    /// Field key -> error codes. Empty = valid. <paramref name="values"/> is what is being submitted (already merged with the
    /// current record by the caller when a required check is wanted: <paramref name="requireComplete"/>).
    /// </summary>
    public static Dictionary<string, List<string>> Validate(string sectionKey, NationalityClass? nationalityClass, string? companyCountry,
        IReadOnlyDictionary<string, string> values, bool requireComplete = false)
    {
        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Err(string field, string code) { if (!errors.TryGetValue(field, out var l)) errors[field] = l = []; if (!l.Contains(code)) l.Add(code); }
        var specs = ProfileSectionRegistry.FieldsFor(sectionKey, nationalityClass);
        var iso = CompanyIso(companyCountry);

        foreach (var (key, raw) in values)
        {
            var spec = specs.FirstOrDefault(s => s.Key == key);
            if (spec is null) { Err(key, EssProblemCodes.UnknownField); continue; }
            var v = (raw ?? string.Empty).Trim();
            if (v.Length > spec.MaxLength) Err(key, EmployeeFieldErrors.ValueTooLong);
            if (v.Any(char.IsControl)) Err(key, EmployeeFieldErrors.InvalidFormat);
            if (v.Length == 0) continue;   // blank is a "required" matter, below
            switch (spec.Kind)
            {
                case EssFieldKind.Email:
                    if (!new EmailAddressAttribute().IsValid(v)) Err(key, EmployeeFieldErrors.EmailInvalid);
                    break;
                case EssFieldKind.Phone:
                    if (PhoneNormalizer.Normalize(v, iso, PhoneKind.Mobile) is null) Err(key, EmployeeFieldErrors.PhoneInvalid);
                    break;
                case EssFieldKind.Enum:
                    if (spec.Options is not null && !spec.Options.Contains(v, StringComparer.Ordinal)) Err(key, EmployeeFieldErrors.InvalidFormat);
                    break;
                case EssFieldKind.NationalId:
                case EssFieldKind.Iqama:
                case EssFieldKind.Border:
                {
                    var kind = spec.Kind == EssFieldKind.NationalId ? IdentityKinds.IdNumber : spec.Kind == EssFieldKind.Iqama ? IdentityKinds.IqamaNumber : IdentityKinds.BorderNumber;
                    foreach (var code in IdentityNumberRules.Check(v, kind, nationalityClass).HardErrors) Err(key, code);
                    break;
                }
                case EssFieldKind.Iban:
                {
                    var cleaned = v.Replace(" ", "").ToUpperInvariant();
                    if (!(iso == "SA" ? IbanValidator.IsSaudiIban(cleaned) : IbanValidator.IsValid(cleaned))) Err(key, EmployeeFieldErrors.IbanInvalid);
                    break;
                }
                default:
                    if (key == "arabicName" && (!ArabicName.IsMatch(v) || !ArabicLetter.IsMatch(v))) Err(key, EmployeeFieldErrors.InvalidFormat);
                    break;
            }
        }

        if (requireComplete)
        {
            string Get(string k) => values.TryGetValue(k, out var x) ? x.Trim() : string.Empty;
            if (sectionKey == EssSectionKeys.Id && nationalityClass != NationalityClass.Saudi)
            {
                if (Get("iqamaNumber").Length == 0 && Get("borderNumber").Length == 0) Err("iqamaNumber", EmployeeFieldErrors.Required);
            }
            else
            {
                foreach (var spec in specs.Where(s => s.Required && Get(s.Key).Length == 0)) Err(spec.Key, EmployeeFieldErrors.Required);
            }
        }
        return errors;
    }

    /// <summary>The values as they will be stored: trimmed, ASCII digits, E.164 phones, upper-case IBAN without spaces.</summary>
    public static Dictionary<string, string> Normalise(string sectionKey, NationalityClass? nationalityClass, string? companyCountry, IReadOnlyDictionary<string, string> values)
    {
        var specs = ProfileSectionRegistry.FieldsFor(sectionKey, nationalityClass);
        var iso = CompanyIso(companyCountry);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, raw) in values)
        {
            var v = (raw ?? string.Empty).Trim();
            var spec = specs.FirstOrDefault(s => s.Key == key);
            result[key] = spec?.Kind switch
            {
                EssFieldKind.Phone => v.Length == 0 ? v : PhoneNormalizer.Normalize(v, iso, PhoneKind.Mobile) ?? v,
                EssFieldKind.Iban => v.Replace(" ", "").ToUpperInvariant(),
                EssFieldKind.NationalId or EssFieldKind.Iqama or EssFieldKind.Border => IdentityNumberRules.NormalizeDigits(v),
                _ => v,
            };
        }
        return result;
    }
}
