using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Employees.Onboarding;
using Zayra.Api.Infrastructure.Payroll;

namespace Zayra.Api.Infrastructure.Employees.Onboarding;

/// <summary>What the pure validator needs to know that is not in the request.</summary>
public sealed record EmployeeInputValidationContext(string? CompanyCountry, DateOnly Today, EmployeeGuardMode Mode, TimeZoneInfo TimeZone);

public sealed class EmployeeInputValidationResult
{
    public Dictionary<string, List<string>> Errors { get; } = new(StringComparer.Ordinal);
    public List<EmployeeWarning> Warnings { get; } = [];
    public required EmployeeCreateRequest Normalised { get; init; }
    public NationalityClass? NationalityClass { get; init; }
}

/// <summary>
/// Pure validation of an employee write: data-integrity failures become <c>Errors</c> (hard, 400), statutory
/// floors become <c>Warnings</c> (acknowledged with a reason, 409). No database access. Returns a NORMALISED
/// copy of the request (ISO-2 nationality, E.164 phone, ASCII digits, company-derived country).
/// </summary>
public static class EmployeeInputValidator
{
    public const decimal MaxAmount = 9_999_999_999.99m;
    private const int ComplianceValueMax = 200, ComplianceRecordMax = 40;

    private static readonly Dictionary<string, int> PayrollBounds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BankName"] = 120, ["Iban"] = 34, ["AccountNumber"] = 34, ["PaymentMethod"] = 80, ["SalaryCurrency"] = 3,
        ["PayrollGroup"] = 80, ["SalaryStructureReference"] = 80, ["SocialInsuranceReference"] = 80, ["MolId"] = 40, ["BankRoutingCode"] = 40,
    };

    private static readonly (string Name, int Max)[] AnnotatedBounds = BuildAnnotatedBounds();

    private static (string, int)[] BuildAnnotatedBounds() =>
        typeof(EmployeeCreateRequest).GetConstructors().First().GetParameters()
            .Select(p => (Param: p, Attr: p.GetCustomAttribute<MaxLengthAttribute>()))
            .Where(x => x.Attr is not null && x.Param.ParameterType == typeof(string) && x.Param.Name != "NationalityCode")
            .Select(x => (x.Param.Name!, x.Attr!.Length)).ToArray();

    public static string Camel(string name) => string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];

    public static EmployeeInputValidationResult Validate(EmployeeCreateRequest request, EmployeeInputValidationContext ctx)
    {
        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var warnings = new List<EmployeeWarning>();
        void Err(string field, string code) { if (!errors.TryGetValue(field, out var l)) errors[field] = l = []; if (!l.Contains(code)) l.Add(code); }
        void Warn(string code, string field, string message, bool perm = false)
        {
            if (!warnings.Any(w => w.Code == code && w.Field == field)) warnings.Add(new EmployeeWarning(code, field, message, perm));
        }

        var draft = ctx.Mode == EmployeeGuardMode.Draft;
        var companyIso = CountryCodeStandard.NormalizeToIso2(ctx.CompanyCountry) ?? (ctx.CompanyCountry ?? "").Trim().ToUpperInvariant();
        var ksa = companyIso == "SA";
        var today = ctx.Today;

        // ── bounds from the DTO's own MaxLength attributes (direct callers skip MVC validation) ───────
        var props = typeof(EmployeeCreateRequest).GetProperties();
        foreach (var (name, max) in AnnotatedBounds)
        {
            var v = props.First(p => p.Name == name).GetValue(request) as string;
            if (v is not null && v.Length > max) Err(Camel(name), EmployeeFieldErrors.ValueTooLong);
        }

        // ── name, code ────────────────────────────────────────────────────────────────────────────────
        var englishName = request.EnglishName?.Trim() ?? string.Empty;
        if (englishName.Length == 0) Err("englishName", EmployeeFieldErrors.Required);
        else if (englishName.Length > 150) Err("englishName", EmployeeFieldErrors.ValueTooLong);
        if (draft && request.CompanyId is null) Err("companyId", EmployeeFieldErrors.Required);
        var code = request.EmployeeCode?.Trim();
        if (!string.IsNullOrEmpty(code))
        {
            if (code.Length > 50) Err("employeeCode", EmployeeFieldErrors.ValueTooLong);
            else if (!code.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')) Err("employeeCode", EmployeeFieldErrors.InvalidFormat);
        }

        // ── nationality ───────────────────────────────────────────────────────────────────────────────
        string? natCode = null; string natText = request.Nationality?.Trim() ?? string.Empty; NationalityClass? natClass = null;
        if (!string.IsNullOrWhiteSpace(request.NationalityCode))
        {
            var e = NationalityCatalog.FindByIso2(request.NationalityCode);
            if (e is null) Err("nationalityCode", EmployeeFieldErrors.NationalityUnknown);
            else { natCode = e.Iso2; natText = e.NameEn; natClass = e.Class; }
        }
        else if (natText.Length > 0 && NationalityCatalog.TryResolve(natText) is { } hit)
        {
            natCode = hit.Iso2; natText = hit.NameEn; natClass = hit.Class;
        }

        // ── dates ─────────────────────────────────────────────────────────────────────────────────────
        DateOnly? joining = request.JoiningDate is { } jd
            ? (jd.Kind == DateTimeKind.Utc ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(jd, ctx.TimeZone)) : DateOnly.FromDateTime(jd))
            : null;
        var dobOk = false;
        if (request.DateOfBirth is { } dob)
        {
            if (dob > today) Err("dateOfBirth", EmployeeFieldErrors.DobFuture);
            else if (dob < today.AddYears(-100)) Err("dateOfBirth", EmployeeFieldErrors.DobOutOfRange);
            else dobOk = true;
        }
        var joiningOk = false;
        if (joining is { } j)
        {
            if (j < today.AddYears(-50) || j > today.AddYears(2) || (dobOk && j < request.DateOfBirth!.Value))
                Err("joiningDate", EmployeeFieldErrors.JoiningOutOfRange);
            else
            {
                joiningOk = true;
                if (j < today.AddDays(-30)) Warn(EmployeeWarningCodes.JoiningBackdated, "joiningDate", "Joining date is more than 30 days in the past.");
                else if (j > today.AddDays(180)) Warn(EmployeeWarningCodes.JoiningOver180dFuture, "joiningDate", "Joining date is more than 180 days away.");
            }
        }
        if (dobOk && !(joining is not null && !joiningOk))
        {
            var reference = joining ?? today;
            var age = AgeAt(request.DateOfBirth!.Value, reference);
            if (age < 15) Warn(EmployeeWarningCodes.UnderMinimumAge, "dateOfBirth", "The employee is under 15 at the joining date.", perm: true);
            else if (age < 18) Warn(EmployeeWarningCodes.Juvenile15To17, "dateOfBirth", "The employee is under 18 at the joining date.");
            else if (age > 70) Warn(EmployeeWarningCodes.AgeOver70, "dateOfBirth", "The employee is over 70 at the joining date.");
        }
        if (request.ProbationEndDate is { } pe)
        {
            // CD31: the limit runs from the probation start, else from the joining date.
            var probationFrom = request.ProbationStartDate ?? (joiningOk ? joining : null);
            if (request.ProbationStartDate is { } ps && pe < ps) Err("probationEndDate", EmployeeFieldErrors.ProbationOrder);
            else if (probationFrom is { } pf && pe.DayNumber - pf.DayNumber > 180)
                Warn(EmployeeWarningCodes.ProbationOverLimit, "probationEndDate", "Probation is longer than 180 days.");
        }
        // CD17 / CD26: a fixed-term end is after its start; an end date needs a fixed term. Autosave (draft) never
        // fights typing (CD28); the stored value is re-checked by Create-from-Review and by activation.
        if (!draft && request.ContractEndDate is not null)
        {
            DateOnly? contractStart = joining;
            foreach (var e in ContractRules.Check(request.ContractType, contractStart, request.ContractEndDate)) Err("contractEndDate", e);
        }
        if (request.ConfirmationDate is { } cd && joining is { } jj && cd < jj) Err("confirmationDate", EmployeeFieldErrors.ProbationOrder);
        if (request.NoticePeriodDays is { } np && (np < 0 || np > 365)) Err("noticePeriodDays", EmployeeFieldErrors.InvalidFormat);

        // ── contact ───────────────────────────────────────────────────────────────────────────────────
        var mobile = request.MobileNumber;
        if (!string.IsNullOrWhiteSpace(mobile))
        {
            var norm = PhoneNormalizer.Normalize(mobile, companyIso, PhoneKind.Mobile);
            if (norm is not null) mobile = norm;
            else if (!draft) Err("mobileNumber", EmployeeFieldErrors.PhoneInvalid);
        }
        foreach (var (field, value) in new[] { ("personalEmail", request.PersonalEmail), ("workEmail", request.WorkEmail) })
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (value.Length > 254) Err(field, EmployeeFieldErrors.ValueTooLong);
            else if (!draft && !new EmailAddressAttribute().IsValid(value)) Err(field, EmployeeFieldErrors.EmailInvalid);
        }
        var photo = request.ProfilePhotoUrl?.Trim();
        if (!string.IsNullOrEmpty(photo) && !IsAllowedPhotoUrl(photo)) Err("profilePhotoUrl", EmployeeFieldErrors.PhotoUrlScheme);

        // ── money ─────────────────────────────────────────────────────────────────────────────────────
        if (request.SalaryBreakdown is { } sb)
        {
            var parts = new (string Field, decimal? V)[]
            {
                ("basicSalary", sb.BasicSalary), ("housingAllowance", sb.HousingAllowance), ("transportAllowance", sb.TransportAllowance),
                ("foodAllowance", sb.FoodAllowance), ("mobileAllowance", sb.MobileAllowance), ("otherAllowance", sb.OtherAllowance), ("fixedDeduction", sb.FixedDeduction),
            };
            foreach (var (f, v) in parts)
            {
                if (v is < 0) Err("salaryBreakdown." + f, EmployeeFieldErrors.NegativeAmount);
                else if (v > MaxAmount) Err("salaryBreakdown." + f, EmployeeFieldErrors.AmountOverflow);
            }
            if (sb.SalaryStructureCode is { Length: > 60 }) Err("salaryBreakdown.salaryStructureCode", EmployeeFieldErrors.ValueTooLong);
            if (sb.Currency is { Length: > 3 }) Err("salaryBreakdown.currency", EmployeeFieldErrors.ValueTooLong);
            var total = parts.Take(6).Sum(p => Math.Max(0, Math.Min(p.V ?? 0, MaxAmount)));
            if (sb.BasicSalary is > 0 && total > 0 && sb.BasicSalary * 2 < total)
                Warn(EmployeeWarningCodes.BasicBelow50Pct, "salaryBreakdown.basicSalary", "Basic salary is below 50% of total pay.");
        }

        var payroll = request.PayrollProfile;
        if (payroll is not null)
        {
            foreach (var pi in typeof(EmployeePayrollProfileRequest).GetProperties().Where(p => p.PropertyType == typeof(string)))
                if (PayrollBounds.TryGetValue(pi.Name, out var max) && pi.GetValue(payroll) is string s && s.Length > max)
                    Err("payrollProfile." + Camel(pi.Name), EmployeeFieldErrors.ValueTooLong);
            if (!string.IsNullOrWhiteSpace(payroll.Iban))
            {
                var cleaned = payroll.Iban.Replace(" ", "").ToUpperInvariant();
                if (cleaned.Length <= 34)
                {
                    // CD29: the Saudi rule applies to a Saudi company; elsewhere mod-97 plus the country's own length.
                    if (!draft && !(ksa ? IbanValidator.IsSaudiIban(cleaned) : IbanValidator.IsValid(cleaned))) Err("payrollProfile.iban", EmployeeFieldErrors.IbanInvalid);
                    else payroll = payroll with { Iban = cleaned };
                }
            }
        }

        // ── compliance records (identity numbers) ─────────────────────────────────────────────────────
        var records = request.ComplianceRecords;
        List<EmployeeComplianceRecordRequest>? normalisedRecords = null;
        if (records is not null)
        {
            if (records.Count > ComplianceRecordMax) Err("complianceRecords", EmployeeFieldErrors.ListTooLong);
            else
            {
                normalisedRecords = [];
                var i = 0;
                foreach (var r in records)
                {
                    var path = $"complianceRecords[{i++}]";
                    var value = r.FieldValue;
                    if (value is { Length: > ComplianceValueMax }) Err(path + ".fieldValue", EmployeeFieldErrors.ValueTooLong);
                    else if (!draft && ksa && IdentityKinds.IsKsaIdentity(r.FieldKey))
                    {
                        var kind = r.FieldKey.ToLowerInvariant();
                        var check = IdentityNumberRules.Check(value, kind, natClass);
                        foreach (var e in check.HardErrors) Err(path + ".fieldValue", e);
                        foreach (var w in check.Warnings) Warn(w, path + ".fieldValue", "The identity number class does not match the nationality.");
                        if (check.Normalised.Length > 0) value = check.Normalised;
                        if (check.Normalised.Length > 0 && r.ExpiryDate is { } exp)
                        {
                            if (exp < today) Warn(EmployeeWarningCodes.IdExpired, path + ".expiryDate", "The identity document has expired.");
                            else if (exp <= today.AddDays(90)) Warn(EmployeeWarningCodes.IdExpiring90d, path + ".expiryDate", "The identity document expires within 90 days.");
                        }
                    }
                    else if (value is not null && IdentityKinds.IsKsaIdentity(r.FieldKey)) value = IdentityNumberRules.NormalizeDigits(value);
                    // The jurisdiction is the employing company's, never a client-supplied value.
                    normalisedRecords.Add(r with { FieldValue = value, CountryCode = string.IsNullOrEmpty(companyIso) ? r.CountryCode : companyIso });
                }
            }
        }

        var normalised = request with
        {
            EnglishName = englishName.Length > 0 ? englishName : request.EnglishName,
            EmployeeCode = code ?? request.EmployeeCode,
            Nationality = natText.Length > 0 ? natText : request.Nationality,
            NationalityCode = natCode,
            MobileNumber = mobile,
            ProfilePhotoUrl = photo ?? request.ProfilePhotoUrl,
            PayrollProfile = payroll,
            ComplianceRecords = normalisedRecords ?? request.ComplianceRecords,
        };
        var result = new EmployeeInputValidationResult { Normalised = normalised, NationalityClass = natClass };
        foreach (var kv in errors) result.Errors[kv.Key] = kv.Value;
        result.Warnings.AddRange(warnings);
        return result;
    }

    public static int AgeAt(DateOnly dob, DateOnly on)
    {
        var age = on.Year - dob.Year;
        if (on < dob.AddYears(age)) age--;
        return age;
    }

    private static bool IsAllowedPhotoUrl(string url)
    {
        if (url.StartsWith("/api/", StringComparison.Ordinal)) return !url.Contains("..", StringComparison.Ordinal);
        return Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && !string.IsNullOrEmpty(u.Host);
    }
}
