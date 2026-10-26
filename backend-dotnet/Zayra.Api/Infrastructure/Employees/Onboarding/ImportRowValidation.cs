using System.Globalization;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Employees.Onboarding;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees.Onboarding;

/// <summary>
/// The CSV import runs the SAME validator as the Add Employee form over each row, so the file is not a way
/// around it. Data-integrity failures (name length, impossible dates, negative or overflowing amounts, identity
/// number format/checksum/class) become <see cref="ResolvedImportRow.Errors"/> and skip that row; everything else
/// (statutory floors, unreadable phone/e-mail/IBAN, an unmapped nationality) lands the person and is flagged as a
/// warning plus a typed gap, matching the import's accept-never-block rule.
/// </summary>
public static class ImportRowValidation
{
    private static readonly HashSet<string> HardInImport = new(StringComparer.Ordinal)
    {
        EmployeeFieldErrors.ValueTooLong, EmployeeFieldErrors.DobFuture, EmployeeFieldErrors.DobOutOfRange,
        EmployeeFieldErrors.JoiningOutOfRange, EmployeeFieldErrors.NegativeAmount, EmployeeFieldErrors.AmountOverflow,
        EmployeeFieldErrors.IdFormatInvalid, EmployeeFieldErrors.IdChecksumInvalid, EmployeeFieldErrors.IdPrefixNationalityMismatch,
        EmployeeFieldErrors.Required, EmployeeFieldErrors.ProbationOrder,
    };

    public static ResolvedImportRow Apply(ResolvedImportRow r, Dictionary<string, string> row, ImportLookups? lookups = null)
    {
        string V(string k) => row.GetValueOrDefault(k, string.Empty).Trim();
        var name = V("FullName").Length > 0 ? V("FullName") : V("EnglishName");
        if (name.Length == 0) return r;   // the no-name drop is decided by the caller

        var country = r.CompanyCountryCode;
        var zone = OnboardingClock.ZoneFor(country);
        var today = OnboardingClock.TodayIn(zone);

        var records = new List<EmployeeComplianceRecordRequest>();
        void Id(string header, string key)
        {
            if (V(header).Length > 0) records.Add(new EmployeeComplianceRecordRequest(country, key, header, V(header), null, null, true, false));
        }
        Id("IdNumber", IdentityKinds.IdNumber); Id("IqamaNumber", IdentityKinds.IqamaNumber); Id("BorderNumber", IdentityKinds.BorderNumber);

        decimal? Amt(string k) => decimal.TryParse(V(k), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
        var salary = new EmployeeSalaryBreakdownRequest(Amt("BasicSalary"), Amt("HousingAllowance"), Amt("TransportAllowance"),
            Amt("FoodAllowance"), Amt("MobileAllowance"), Amt("OtherAllowance"), Amt("FixedDeduction"), null, null, null);
        DateOnly? Date(string k) => DateOnly.TryParseExact(V(k), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

        var request = new EmployeeCreateRequest(
            null, false, name, null, null, null, Date("DateOfBirth"), V("Nationality"), null, V("PersonalEmail").NullIfEmpty(), null,
            V("Phone").NullIfEmpty(), null, r.CompanyId, null, null, null, null, null, null, null, null, null, null,
            Date("JoiningDate") is { } jd ? jd.ToDateTime(TimeOnly.MinValue) : null, null, null, null, null, null, null, null, null, null,
            new EmployeePayrollProfileRequest(null, V("IBAN").NullIfEmpty(), null, null, null, null, null, false, false, null, null, null),
            salary, records);

        var result = EmployeeInputValidator.Validate(request, new EmployeeInputValidationContext(country, today, EmployeeGuardMode.Create, zone));

        foreach (var (field, codes) in result.Errors)
            foreach (var code in codes)
            {
                var header = HeaderFor(field, records);
                if (HardInImport.Contains(code)) r.Errors.Add($"{header}: {Describe(code)}");
                else
                {
                    r.Warnings.Add($"{header}: {Describe(code)} — imported and flagged for review.");
                    r.Gaps.Add(new ImportGap("data:" + code, "data", $"{header}: {Describe(code)}", null));
                }
            }
        foreach (var w in result.Warnings)
        {
            r.Warnings.Add($"{HeaderFor(w.Field, records)}: {w.Message}");
            r.Gaps.Add(new ImportGap("data:" + w.Code, "data", w.Message, null));
        }

        var nat = V("Nationality");
        if (result.Normalised.NationalityCode is { } code2)
        {
            r.NationalityCode = code2;
            r.NationalityMappingState = NationalityMappingStates.Confirmed;
        }
        else if (nat.Length > 0)
        {
            r.NationalityMappingState = NationalityMappingStates.Unresolved;
            r.Gaps.Add(new ImportGap("data:nationality_unresolved", "data",
                $"Nationality '{nat}' is not in the nationality list — kept as typed and flagged for HR review.", nat));
            r.Warnings.Add($"Nationality '{nat}' is not recognised — imported as typed, flagged for HR review.");
        }
        return r;
    }

    private static string HeaderFor(string field, List<EmployeeComplianceRecordRequest> records)
    {
        if (field.StartsWith("complianceRecords[", StringComparison.Ordinal))
        {
            var close = field.IndexOf(']');
            if (close > 0 && int.TryParse(field.AsSpan(18, close - 18), out var i) && i < records.Count) return records[i].FieldLabel;
        }
        return field switch
        {
            "englishName" => "FullName", "dateOfBirth" => "DateOfBirth", "joiningDate" => "JoiningDate", "mobileNumber" => "Phone",
            "personalEmail" => "PersonalEmail", "payrollProfile.iban" => "IBAN",
            _ when field.StartsWith("salaryBreakdown.", StringComparison.Ordinal) => char.ToUpperInvariant(field[16]) + field[17..],
            _ => field,
        };
    }

    private static string Describe(string code) => code switch
    {
        EmployeeFieldErrors.ValueTooLong => "value is too long",
        EmployeeFieldErrors.DobFuture => "date of birth is in the future",
        EmployeeFieldErrors.DobOutOfRange => "date of birth is out of range",
        EmployeeFieldErrors.JoiningOutOfRange => "joining date is out of range",
        EmployeeFieldErrors.NegativeAmount => "amount cannot be negative",
        EmployeeFieldErrors.AmountOverflow => "amount is too large",
        EmployeeFieldErrors.IdFormatInvalid => "identity number format is invalid",
        EmployeeFieldErrors.IdChecksumInvalid => "identity number check digit is invalid",
        EmployeeFieldErrors.IdPrefixNationalityMismatch => "identity number class does not match the nationality",
        EmployeeFieldErrors.Required => "value is required",
        EmployeeFieldErrors.ProbationOrder => "dates are out of order",
        EmployeeFieldErrors.PhoneInvalid => "phone number is not a valid number",
        EmployeeFieldErrors.EmailInvalid => "e-mail address is not valid",
        EmployeeFieldErrors.IbanInvalid => "IBAN is not valid",
        _ => code,
    };

    private static string? NullIfEmpty(this string s) => s.Length == 0 ? null : s;
}
