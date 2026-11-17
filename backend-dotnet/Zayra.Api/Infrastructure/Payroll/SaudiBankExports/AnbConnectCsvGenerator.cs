using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Zayra.Api.Infrastructure.Finance;

namespace Zayra.Api.Infrastructure.Payroll.SaudiBankExports;

public sealed record AnbHeaderInput(
    string? BatchNumber, string? BatchType, string? MolEstablishmentId, string? MainAccountNumber,
    DateOnly CreditValueDate, string? OrganizationName, string? OrganizationAddress1, string? OrganizationAddress2,
    string? OrganizationAddress3, string? Narrative, string? CompanyName,
    bool AutoWpsUpload = false, string? NationalUnifiedNo = null);

/// <summary>One payment row. <see cref="EmployeeRef"/> is the internal employee id (ordering + blocker
/// attribution); <see cref="EmployeeLabel"/> is the employee code used in messages — never a national
/// ID or account number.</summary>
public sealed record AnbPaymentInput(
    int EmployeeRef, string EmployeeLabel, string? EmployeeNationalId, string? AccountNumber,
    decimal SalaryAmount, decimal BasicSalary, decimal HousingAllowance, decimal OtherEarnings, decimal SalaryDeductions,
    string? BicCode, string? EmployeeName, string? Address1, string? Address2, string? Address3);

public sealed record AnbGeneratedFile(string Name, byte[] Content, string Sha256);

public sealed record AnbGenerationResult(
    IReadOnlyList<SaudiBankExportIssueDto> Errors, IReadOnlyList<AnbGeneratedFile>? Files, int PaymentCount, decimal TotalAmount)
{
    public bool Ok => Errors.Count == 0 && Files is not null;

    /// <summary>Points for a person to check that do not block the file (for example "check bank holidays").</summary>
    public IReadOnlyList<SaudiBankExportWarningDto> Warnings { get; init; } = Array.Empty<SaudiBankExportWarningDto>();
}

/// <summary>What a Saudi bank-holiday calendar says about one date.</summary>
public enum SaudiBankHolidayAnswer
{
    /// <summary>No calendar source answers for this date: a person must check it.</summary>
    Unknown,
    BankingDay,
    BankHoliday,
}

/// <summary>
/// The seam for a Saudi bank-holiday calendar (CTO gate 8.4.5). Which source to trust (SAMA's published holidays, the
/// bank's own calendar) is an owner decision, so no real source exists yet: <see cref="NoSaudiBankHolidayCalendar"/>
/// answers <see cref="SaudiBankHolidayAnswer.Unknown"/> for every date and the generator warns "check bank holidays".
/// An implementation must be pure (no I/O at generation time): load the calendar before calling the generator.
/// </summary>
public interface ISaudiBankHolidayCalendar
{
    SaudiBankHolidayAnswer Check(DateOnly date);
}

/// <summary>The only calendar until a source is chosen: it knows no date.</summary>
public sealed class NoSaudiBankHolidayCalendar : ISaudiBankHolidayCalendar
{
    public static readonly NoSaudiBankHolidayCalendar Instance = new();
    private NoSaudiBankHolidayCalendar() { }
    public SaudiBankHolidayAnswer Check(DateOnly date) => SaudiBankHolidayAnswer.Unknown;
}

/// <summary>What the credit value date is checked against. <paramref name="TodayInRiyadh"/> is today's date in Riyadh,
/// read by the caller from its clock (never <c>DateTime.Now</c> here: the generator stays pure).</summary>
public sealed record AnbValueDateRules(DateOnly TodayInRiyadh, ISaudiBankHolidayCalendar Holidays)
{
    public static AnbValueDateRules WithoutHolidayCalendar(DateOnly todayInRiyadh) =>
        new(todayInRiyadh, NoSaudiBankHolidayCalendar.Instance);
}

/// <summary>
/// PURE generator for <c>anb-connect-csv-v1</c> (ANB Connect payroll-payment, spec reviewed 2026-09-26).
/// No I/O, no clock, no culture: same input ⇒ same bytes, which is what lets a repeated generate prove
/// it is the same instruction by hash. "Today" for the value-date checks is an input
/// (<see cref="AnbValueDateRules"/>), never read here.
///
/// <para>Every rule FAILS CLOSED with a structured blocker. Nothing is trimmed, rounded, truncated,
/// defaulted, apostrophised or re-cased — a value either satisfies the spec table as supplied or the
/// whole batch is refused (no partial omission).</para>
///
/// <para>Byte choices the source does not specify (UTF-8 without BOM, CRLF, a column-name first row as
/// in the source's example, RFC-4180 quoting of a comma/quote, minimal invariant decimal form as in the
/// example) are implementation choices, NOT bank-certified.</para>
/// </summary>
public static class AnbConnectCsvGenerator
{
    public const string HeaderFileName = "header.csv";
    public const string BodyFileName = "body.csv";

    public static readonly IReadOnlyList<string> HeaderColumns = new[]
    {
        "batchNumber", "batchType", "molEstablishmentId", "mainAccountNumber", "creditValueDate",
        "organizationName", "organizationAddress1", "organizationAddress2", "organizationAddress3",
        "paymentCount", "totalPayrollAmount", "narrative", "companyName",
    };

    /// <summary>Appended to the header ONLY when auto-WPS upload is enabled, so a file without it stays
    /// byte-identical to every instruction generated before the option existed. ANB documents the
    /// switch as <c>autowpsfileupload=YES</c> with <c>nationalunifiedno</c>. [CONFIRM] the exact column
    /// names and casing ANB Connect expects in the CSV header during bank onboarding.</summary>
    public static readonly IReadOnlyList<string> AutoWpsHeaderColumns = new[] { "autoWpsFileUpload", "nationalUnifiedNo" };

    public static readonly IReadOnlyList<string> BodyColumns = new[]
    {
        "employeeId", "employeeAccountNumber", "salaryAmount", "basicSalary", "housingAllowance",
        "otherEarnings", "salaryDeductions", "bicCode", "employeeName", "employeeAddress1",
        "employeeAddress2", "employeeAddress3",
    };

    public static readonly IReadOnlyList<string> BatchTypes = new[] { "PAYROLL", "BENEFIT", "BONUS", "WELFARE" };

    /// <summary>Employer settings fields validated by <see cref="ValidateEmployerField"/>.</summary>
    public static readonly IReadOnlyList<string> EmployerFields = new[]
    {
        "molEstablishmentId", "mainAccountNumber", "organizationName", "organizationAddress1",
        "organizationAddress2", "organizationAddress3", "companyName", "narrative", "batchType",
    };

    // Credit value date (CTO gate 8.4.5). Errors carry the field "paymentDate", the request field the user sets.
    public const string ValueDateWeekend = "value_date_weekend";
    public const string ValueDateInPast = "value_date_in_past";
    public const string ValueDateBankHoliday = "value_date_bank_holiday";
    /// <summary>Warning: no bank-holiday calendar source exists yet, so a person must check the date.</summary>
    public const string ValueDateCheckBankHolidays = "value_date_check_bank_holidays";

    /// <summary>Plain English and Arabic for the value-date codes. Both are sent with every issue (message and messageAr).
    /// {0} is the value date, {1} today's Riyadh date (both yyyy-MM-dd).</summary>
    public static readonly IReadOnlyDictionary<string, BankText> ValueDateTexts = new Dictionary<string, BankText>(StringComparer.Ordinal)
    {
        [ValueDateWeekend] = new(
            "The credit value date {0} falls on the Saudi bank weekend (Friday or Saturday). Choose a working day, Sunday to Thursday. If the salary is due on a weekend or holiday, choose the last working day before it so the salary is not paid late.",
            "تاريخ قيمة الإيداع {0} يقع في عطلة نهاية الأسبوع لدى البنوك السعودية (الجمعة أو السبت). اختر يوم عمل من الأحد إلى الخميس. وإذا كان الراتب مستحقًا في عطلة أسبوعية أو رسمية فاختر آخر يوم عمل قبله حتى لا يتأخر دفع الراتب."),
        [ValueDateInPast] = new(
            "The credit value date {0} is before today ({1}, Riyadh time). Choose today or a later working day.",
            "تاريخ قيمة الإيداع {0} سابق لتاريخ اليوم ({1} بتوقيت الرياض). اختر تاريخ اليوم أو يوم عمل لاحقًا."),
        [ValueDateBankHoliday] = new(
            "The credit value date {0} is a Saudi bank holiday. Choose a working day.",
            "تاريخ قيمة الإيداع {0} يوافق عطلة رسمية لدى البنوك السعودية. اختر يوم عمل."),
        [ValueDateCheckBankHolidays] = new(
            "Check bank holidays: no Saudi bank holiday calendar is set up, so the credit value date was not checked against bank holidays. Confirm with your bank that it is a banking day.",
            "تحقّق من العطل الرسمية: لم يُضبط تقويم لعطل البنوك السعودية، لذلك لم يُتحقّق من تاريخ قيمة الإيداع مقابل العطل. تأكّد من البنك أنه يوم عمل مصرفي."),
    };

    public const int MaxPaymentCount = 999_999;
    public const int MaxAmountChars = 12;

    private static readonly string[] AnbInternalAccountBics = { "ARNBSARI", "ARNBSARIXXX" };
    private static readonly Regex SaudiBic = new("^[A-Z]{4}SA[A-Z0-9]{2}([A-Z0-9]{3})?$", RegexOptions.CultureInvariant);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private const string Crlf = "\r\n";

    public static AnbGenerationResult Generate(AnbHeaderInput header, IReadOnlyList<AnbPaymentInput> rows, decimal expectedTotal,
        AnbValueDateRules valueDate)
    {
        ArgumentNullException.ThrowIfNull(valueDate);
        ArgumentNullException.ThrowIfNull(valueDate.Holidays);
        var errors = new List<SaudiBankExportIssueDto>();
        var warnings = new List<SaudiBankExportWarningDto>();
        CheckValueDate(header.CreditValueDate, valueDate, errors, warnings);

        if (!IsAsciiDigits(header.BatchNumber, 1, 20))
            errors.Add(new("batch_reference_invalid", "batchNumber must be 1 to 20 digits.", null, "batchReference"));
        ValidateEmployerField("batchType", header.BatchType, errors, allowBlank: false);
        ValidateEmployerField("molEstablishmentId", header.MolEstablishmentId, errors, allowBlank: false);
        ValidateEmployerField("mainAccountNumber", header.MainAccountNumber, errors, allowBlank: false);
        ValidateEmployerField("organizationName", header.OrganizationName, errors, allowBlank: false);
        ValidateEmployerField("organizationAddress1", header.OrganizationAddress1, errors, allowBlank: false);
        ValidateEmployerField("organizationAddress2", header.OrganizationAddress2, errors, allowBlank: false);
        ValidateEmployerField("organizationAddress3", header.OrganizationAddress3, errors, allowBlank: false);
        ValidateEmployerField("narrative", header.Narrative, errors, allowBlank: false);
        ValidateEmployerField("companyName", header.CompanyName, errors, allowBlank: false);
        if (header.AutoWpsUpload)
            KsaWageFileRules.ValidateNationalUnifiedNo(header.NationalUnifiedNo, required: true, errors);

        if (rows.Count == 0)
            errors.Add(new("batch_empty", "The batch has no payment rows."));
        if (rows.Count > MaxPaymentCount)
            errors.Add(new("payment_count_out_of_range", $"paymentCount must be 1..{MaxPaymentCount}."));

        foreach (var r in rows) ValidateRow(r, errors);
        AddDuplicates(rows, r => r.EmployeeRef.ToString(CultureInfo.InvariantCulture), "duplicate_employee", "appears more than once in the batch", null, errors);
        AddDuplicates(rows, r => r.EmployeeNationalId, "duplicate_employee_id", "shares a national ID/Iqama with another row", "employeeId", errors);
        AddDuplicates(rows, r => r.AccountNumber, "duplicate_account", "shares a credit account with another row", "employeeAccountNumber", errors);

        var total = rows.Sum(r => r.SalaryAmount);
        if (total != expectedTotal)
            errors.Add(new("total_mismatch", $"Sum of rows ({Amount(total)}) does not equal the batch total ({Amount(expectedTotal)})."));
        if (!IsTwoDecimals(total))
            errors.Add(new("total_invalid", "totalPayrollAmount has more than two decimals."));

        if (errors.Count > 0) return new AnbGenerationResult(errors, null, rows.Count, total) { Warnings = warnings };

        var headerCsv = new StringBuilder();
        var headerValues = new List<string>
        {
            header.BatchNumber!, header.BatchType!, header.MolEstablishmentId!, header.MainAccountNumber!,
            header.CreditValueDate.ToString("yyMMdd", CultureInfo.InvariantCulture),
            header.OrganizationName!, header.OrganizationAddress1!, header.OrganizationAddress2!, header.OrganizationAddress3!,
            rows.Count.ToString(CultureInfo.InvariantCulture), Amount(total), header.Narrative!, header.CompanyName!,
        };
        AppendLine(headerCsv, header.AutoWpsUpload ? HeaderColumns.Concat(AutoWpsHeaderColumns) : HeaderColumns);
        if (header.AutoWpsUpload) headerValues.AddRange(new[] { "YES", header.NationalUnifiedNo! });
        AppendLine(headerCsv, headerValues);

        var bodyCsv = new StringBuilder();
        AppendLine(bodyCsv, BodyColumns);
        foreach (var r in rows.OrderBy(r => r.EmployeeRef))
        {
            AppendLine(bodyCsv, new[]
            {
                r.EmployeeNationalId!, r.AccountNumber!, Amount(r.SalaryAmount), Amount(r.BasicSalary),
                Amount(r.HousingAllowance), Amount(r.OtherEarnings), Amount(r.SalaryDeductions), r.BicCode!,
                r.EmployeeName!, r.Address1!, r.Address2!, r.Address3!,
            });
        }

        var files = new[] { File(HeaderFileName, headerCsv), File(BodyFileName, bodyCsv) };
        return new AnbGenerationResult(errors, files, rows.Count, total) { Warnings = warnings };
    }

    /// <summary>The credit value date checks (CTO gate 8.4.5).</summary>
    private static void CheckValueDate(DateOnly date, AnbValueDateRules rules, List<SaudiBankExportIssueDto> errors,
        List<SaudiBankExportWarningDto> warnings)
    {
        string Text(string code, bool arabic) => string.Format(CultureInfo.InvariantCulture,
            arabic ? ValueDateTexts[code].Arabic : ValueDateTexts[code].English,
            date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            rules.TodayInRiyadh.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        SaudiBankExportIssueDto Issue(string code) => new(code, Text(code, false), null, "paymentDate", Text(code, true));

        if (date < rules.TodayInRiyadh)
            errors.Add(Issue(ValueDateInPast));
        if (date.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday)
            errors.Add(Issue(ValueDateWeekend));

        // Fail closed: only a calendar that positively says "banking day" removes the warning.
        switch (rules.Holidays.Check(date))
        {
            case SaudiBankHolidayAnswer.BankingDay:
                break;
            case SaudiBankHolidayAnswer.BankHoliday:
                errors.Add(Issue(ValueDateBankHoliday));
                break;
            default:
                warnings.Add(new(ValueDateCheckBankHolidays, Text(ValueDateCheckBankHolidays, false), Text(ValueDateCheckBankHolidays, true)));
                break;
        }
    }

    /// <summary>Validates one employer settings field. <paramref name="allowBlank"/> lets settings be saved
    /// incrementally; generation always passes false so a blank field blocks.</summary>
    public static void ValidateEmployerField(string field, string? value, List<SaudiBankExportIssueDto> errors, bool allowBlank)
    {
        if (string.IsNullOrEmpty(value))
        {
            if (allowBlank) return;
            if (field == "molEstablishmentId") KsaWageFileRules.ValidateEstablishmentId(value, errors);
            else errors.Add(new("field_required", $"{field} is required for the ANB header.", null, field));
            return;
        }
        switch (field)
        {
            case "batchType":
                if (!BatchTypes.Contains(value, StringComparer.Ordinal))
                    errors.Add(new("batch_type_invalid", "batchType must be PAYROLL, BENEFIT, BONUS or WELFARE.", null, field));
                break;
            case "molEstablishmentId":
                // [MOL-ESTBID] 2d-15d — never free text, never a placeholder.
                KsaWageFileRules.ValidateEstablishmentId(value, errors);
                break;
            case "mainAccountNumber":
                if (!IsAsciiDigits(value, 16, 16))
                    errors.Add(new("main_account_invalid", "mainAccountNumber must be exactly 16 digits — the ANB employer account number, not an IBAN.", null, field));
                break;
            case "organizationName":
            case "organizationAddress1":
            case "organizationAddress2":
            case "organizationAddress3":
            case "narrative":
            case "companyName":
                ValidateText(field, value, 1, 35, null, null, errors);
                break;
            default:
                errors.Add(new("field_unknown", $"{field} is not an ANB header field.", null, field));
                break;
        }
    }

    /// <summary>True for ANB's own BIC, the only one a 16-digit internal account may be credited with.</summary>
    public static bool IsAnbBic(string? bic) =>
        bic is not null && AnbInternalAccountBics.Contains(bic.Trim(), StringComparer.OrdinalIgnoreCase);

    public static string Sha256Hex(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    public static bool IsAsciiDigits(string? s, int min, int max) =>
        s is not null && s.Length >= min && s.Length <= max && s.All(c => c is >= '0' and <= '9');

    private static void ValidateRow(AnbPaymentInput r, List<SaudiBankExportIssueDto> errors)
    {
        void Err(string code, string message, string field) =>
            errors.Add(new(code, $"{r.EmployeeLabel}: {message}", r.EmployeeRef, field));

        if (!IsAsciiDigits(r.EmployeeNationalId, 10, 10))
            Err("employee_id_invalid", "national ID/Iqama must be exactly 10 digits.", "employeeId");

        var bic = r.BicCode;
        var bicOk = bic is not null && (bic.Length == 8 || bic.Length == 11) && SaudiBic.IsMatch(bic);
        if (string.IsNullOrEmpty(bic)) Err("bic_missing", "bank BIC is not recorded on the payroll profile.", "bicCode");
        else if (!bicOk) Err("bic_invalid", "BIC must be 8 or 11 characters with country SA.", "bicCode");

        var account = r.AccountNumber;
        if (string.IsNullOrEmpty(account))
            Err("account_missing", "credit account is missing.", "employeeAccountNumber");
        else if (account.Length is < 16 or > 35 || !account.All(c => c is (>= '0' and <= '9') or (>= 'A' and <= 'Z')))
            Err("account_invalid", "credit account must be 16..35 upper-case letters/digits with no spaces.", "employeeAccountNumber");
        else if (IsAsciiDigits(account, 16, 16))
        {
            if (!(bicOk && AnbInternalAccountBics.Contains(bic, StringComparer.Ordinal)))
                Err("account_internal_not_anb", "a 16-digit internal account is only allowed for ANB (ARNBSARI) credits; use the employee's SA IBAN.", "employeeAccountNumber");
        }
        else if (!IbanValidator.IsSaudiIban(account))
            Err("account_iban_invalid", "credit account is not a valid Saudi IBAN (country, length or checksum).", "employeeAccountNumber");

        ValidateText("employeeName", r.EmployeeName, 1, 35, r.EmployeeRef, r.EmployeeLabel, errors);
        if (r.Address1 is null && r.Address2 is null && r.Address3 is null)
            Err("employee_address_missing", "no employee address is recorded; ANB requires three address lines and none is invented.", "employeeAddress");
        else
        {
            ValidateText("employeeAddress1", r.Address1, 1, 30, r.EmployeeRef, r.EmployeeLabel, errors);
            ValidateText("employeeAddress2", r.Address2, 1, 30, r.EmployeeRef, r.EmployeeLabel, errors);
            ValidateText("employeeAddress3", r.Address3, 1, 30, r.EmployeeRef, r.EmployeeLabel, errors);
        }

        var components = new (string Field, decimal Value)[]
        {
            ("salaryAmount", r.SalaryAmount), ("basicSalary", r.BasicSalary), ("housingAllowance", r.HousingAllowance),
            ("otherEarnings", r.OtherEarnings), ("salaryDeductions", r.SalaryDeductions),
        };
        foreach (var (field, value) in components)
        {
            if (value < 0m) Err("amount_negative", $"{field} is negative.", field);
            if (!IsTwoDecimals(value)) Err("amount_precision", $"{field} has more than two decimals.", field);
            else if (Amount(value).Length > MaxAmountChars) Err("amount_too_long", $"{field} exceeds {MaxAmountChars} characters.", field);
        }
        if (r.SalaryAmount <= 0m) Err("net_not_positive", "net salary must be positive.", "salaryAmount");
        if (r.SalaryAmount != r.BasicSalary + r.HousingAllowance + r.OtherEarnings - r.SalaryDeductions)
            Err("net_unreconciled", "salaryAmount ≠ basic + housing + other − deductions.", "salaryAmount");
    }

    private static void ValidateText(string field, string? value, int min, int max, int? employeeRef, string? label,
        List<SaudiBankExportIssueDto> errors)
    {
        var prefix = label is null ? string.Empty : $"{label}: ";
        if (string.IsNullOrEmpty(value))
        {
            errors.Add(new("field_required", $"{prefix}{field} is required.", employeeRef, field));
            return;
        }
        if (value.Length < min || value.Length > max)
            errors.Add(new("field_length", $"{prefix}{field} must be {min}..{max} characters.", employeeRef, field));
        if (value.Any(char.IsControl))
            errors.Add(new("field_control_character", $"{prefix}{field} contains a control character.", employeeRef, field));
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
            errors.Add(new("field_surrounding_whitespace", $"{prefix}{field} has leading or trailing whitespace.", employeeRef, field));
        if (value[0] is '=' or '+' or '-' or '@')
            errors.Add(new("field_formula_prefix", $"{prefix}{field} must not start with =, +, - or @.", employeeRef, field));
    }

    private static void AddDuplicates(IReadOnlyList<AnbPaymentInput> rows, Func<AnbPaymentInput, string?> key,
        string code, string what, string? field, List<SaudiBankExportIssueDto> errors)
    {
        foreach (var group in rows.Where(r => !string.IsNullOrEmpty(key(r))).GroupBy(key, StringComparer.Ordinal).Where(g => g.Count() > 1))
            foreach (var r in group)
                errors.Add(new(code, $"{r.EmployeeLabel}: {what}.", r.EmployeeRef, field));
    }

    private static bool IsTwoDecimals(decimal v) => decimal.Round(v, 2) == v;

    // Minimal invariant form ("1500.5", "2000") as in the source's own example. Callers have already
    // proven ≤ 2 decimals, so this is lossless — it never rounds.
    private static string Amount(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static void AppendLine(StringBuilder sb, IEnumerable<string> fields)
    {
        sb.Append(string.Join(",", fields.Select(Escape)));
        sb.Append(Crlf);
    }

    // CR/LF/control characters were already rejected, so only a comma or quote needs RFC-4180 quoting.
    private static string Escape(string field) =>
        field.IndexOfAny(new[] { ',', '"' }) >= 0 ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;

    private static AnbGeneratedFile File(string name, StringBuilder sb)
    {
        var bytes = Utf8NoBom.GetBytes(sb.ToString());
        return new AnbGeneratedFile(name, bytes, Sha256Hex(bytes));
    }
}
