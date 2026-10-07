using System.Globalization;
using System.Text.RegularExpressions;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll.SaudiBankExports;

/// <summary>Employer-level facts every Saudi bank/WPS instruction carries.</summary>
public sealed record KsaWageFileHeader(
    string? MolEstablishmentId,
    string? Currency,
    string? FileReference,
    bool AutoWpsUpload,
    string? NationalUnifiedNo);

/// <summary>
/// One employee line, as the payroll run computed it. <see cref="MolId"/> is the employee's
/// government ID (Employee.IdNumber) — never the payroll profile's free-text MolId.
/// <see cref="SlipDeductions"/> is the deduction total printed on the locked payslip; the file's
/// deduction figure is derived (gross − net) and must agree with it.
/// <see cref="DebtDeductions"/> is the payslip's DEBT-TYPE deductions only (loan and advance
/// instalments, penalties/fines, damages) as classified by <see cref="WageDeductionClassification"/> —
/// the amount the Art. 92/93 cap applies to. <see cref="DebtCapOverridden"/> is true when an approver
/// recorded a reasoned override of the pre-lock cap error (e.g. a court order) for this employee.
/// </summary>
public sealed record KsaWageFileRow(
    int EmployeeRef,
    string EmployeeLabel,
    string? Nationality,
    string? MolId,
    string? Iban,
    string? BankBic,
    string? Name,
    decimal Gross,
    decimal Basic,
    decimal Housing,
    decimal Net,
    decimal SlipDeductions,
    decimal DebtDeductions = 0m,
    bool DebtCapOverridden = false,
    decimal? WageDue = null);

/// <summary>Errors block the file; warnings are shown and the file may still be generated.</summary>
public sealed record KsaWageFileValidation(
    IReadOnlyList<SaudiBankExportIssueDto> Errors, IReadOnlyList<SaudiBankExportWarningDto> Warnings);

/// <summary>
/// The Saudi wage-file rule set, applied BEFORE any KSA bank or WPS instruction is generated.
/// PURE: no I/O, no clock, no culture. Every rule fails closed with a stable code and a plain
/// English message; nothing is trimmed, padded, defaulted or guessed.
///
/// <para>Source: MHRSD "WPS Wages File Specification" (header [MOL-ESTBID] 2d-15d, [FILE-REF] 16x,
/// [32A-CCY] SAR only; content [59-ACC] 24-char upper-case SA IBAN, [59-NAME] 35 per line, one
/// language per field, [MOL-ID] 10d — Saudi ID 1********* / Iqama 2*********,
/// [32B-AMT] = [MOL-BAS] + [MOL-HAL] + [MOL-OEA] − [MOL-DED]) and Saudi Labour Law Art. 92/93
/// (deductions for debts owed to the employer may not exceed half of the wage due).</para>
///
/// <para>FILE-REF uniqueness across history is a database question, so the caller passes the set of
/// references already used by this establishment (including those of voided runs).</para>
/// </summary>
public static class KsaWageFileRules
{
    /// <summary>Stable error codes. Clients and tests key on these; never rename one.</summary>
    public static class Codes
    {
        public const string EstablishmentMissing = "mol_establishment_id_missing";
        public const string EstablishmentInvalid = "mol_establishment_id_invalid";
        public const string EstablishmentPlaceholder = "mol_establishment_id_placeholder";
        public const string CurrencyNotSar = "currency_not_sar";
        public const string FileReferenceInvalid = "file_reference_invalid";
        public const string FileReferenceReused = "file_reference_reused";
        public const string NationalUnifiedNoMissing = "national_unified_no_missing";
        public const string NationalUnifiedNoInvalid = "national_unified_no_invalid";
        public const string NoRows = "file_empty";
        public const string IbanMissing = "iban_missing";
        public const string IbanNotSaudi = "iban_not_saudi";
        public const string IbanLength = "iban_length_invalid";
        public const string IbanLowerCase = "iban_not_upper_case";
        public const string IbanChecksum = "iban_checksum_failed";
        public const string BankCodeMismatch = "bank_code_mismatch";
        public const string MolIdMissing = "mol_id_missing";
        public const string MolIdInvalid = "mol_id_invalid";
        public const string MolIdPrefixMismatch = "mol_id_prefix_mismatch";
        public const string NationalityMissing = "nationality_missing";
        public const string NationalityNotSaudi = "nationality_not_recognised_as_saudi";
        public const string NameMissing = "name_missing";
        public const string NameTooLong = "name_too_long";
        public const string NameMixedScript = "name_mixed_script";
        public const string NameControlCharacter = "name_control_character";
        public const string NameFormulaPrefix = "name_formula_prefix";
        public const string NetNotPositive = "net_not_positive";
        public const string OtherEarningsNegative = "other_earnings_negative";
        public const string DeductionsUnreconciled = "deductions_unreconciled";
        public const string AmountPrecision = "amount_precision";
        public const string DeductionsOverHalf = "deductions_exceed_half_wage";
        public const string DuplicateMolId = "duplicate_mol_id";
        public const string DuplicateIban = "duplicate_iban";
    }

    /// <summary>Stable warning codes: shown to the user, never block the file.</summary>
    public static class WarningCodes
    {
        /// <summary>The IBAN's bank code is not in <see cref="AcceptedSarieIdsByIbanBankCode"/> (e.g. a
        /// newly licensed digital bank). Not a block: the IBAN already passed its mod-97 check.</summary>
        public const string IbanBankCodeUnverified = "iban_bank_code_unverified";
    }

    public const int MaxFileReferenceLength = 16;
    public const int MaxNameLineLength = 35;

    /// <summary>
    /// Saudi IBAN bank code (characters 5–6) → the bank's CURRENT SARIE ID (the first four characters of
    /// its BIC). SARIE IDs are the MHRSD WPS spec's table 7; the two-digit IBAN codes are SAMA's.
    /// Legacy codes for banks that have since merged stay because old IBANs remain in circulation; they
    /// map to the surviving bank (40 SAMBA → SNB "NCBK", 50 Alawwal → SABB "SABB"), and
    /// <see cref="AcceptedSarieIdsByIbanBankCode"/> also accepts the retired identifier for them.
    /// Newly licensed banks (e.g. digital banks) are deliberately ABSENT until a code can be cited from
    /// SAMA or the bank itself; their IBANs produce <see cref="WarningCodes.IbanBankCodeUnverified"/>,
    /// not a block.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> SarieIdByIbanBankCode = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["05"] = "INMA", // Alinma Bank
        ["10"] = "NCBK", // Saudi National Bank (formerly NCB)
        ["15"] = "ALBI", // Bank AlBilad
        ["20"] = "RIBL", // Riyad Bank
        ["30"] = "ARNB", // Arab National Bank
        ["40"] = "NCBK", // SAMBA (merged into SNB, 2021) — legacy IBANs; SNB's identifier today
        ["45"] = "SABB", // Saudi Awwal Bank (SABB)
        ["50"] = "SABB", // Alawwal (merged into SABB, 2019) — legacy IBANs; SABB's identifier today
        ["55"] = "BSFR", // Banque Saudi Fransi
        ["60"] = "BJAZ", // Bank AlJazira
        ["65"] = "SIBC", // Saudi Investment Bank
        ["71"] = "NBOB", // National Bank of Bahrain
        ["75"] = "NBOK", // National Bank of Kuwait
        ["76"] = "BMUS", // Bank Muscat
        ["80"] = "RJHI", // Al Rajhi Bank
        ["81"] = "DEUT", // Deutsche Bank
        ["82"] = "NBPA", // National Bank of Pakistan
        ["83"] = "SBIN", // State Bank of India
        ["84"] = "TCZB", // T.C. Ziraat Bankasi
        ["85"] = "BNPA", // BNP Paribas
        ["90"] = "GULF", // Gulf International Bank
        ["95"] = "EBIL", // Emirates NBD
    };

    /// <summary>
    /// Every SARIE ID a recorded BIC may carry for an IBAN bank code: the current one, plus the retired
    /// identifier for a merged bank (an employee record captured before the merger still carries it).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> AcceptedSarieIdsByIbanBankCode =
        SarieIdByIbanBankCode.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<string>)(kv.Key switch
            {
                "40" => new[] { "NCBK", "SAMB" },
                "50" => new[] { "SABB", "AAAL" },
                _ => new[] { kv.Value },
            }),
            StringComparer.Ordinal);

    /// <summary>True when <paramref name="nationality"/> is a recognised spelling of Saudi
    /// (<see cref="Compliance.SaudiNationality"/>, shared with GOSI and Saudisation).</summary>
    public static bool IsSaudiNationality(string? nationality) => Compliance.SaudiNationality.IsSaudi(nationality);

    // [MOL-ESTBID] "Labor Office – Sequential Number", 2d-15d. Digits with at most one hyphen
    // between two digit groups (e.g. "7-1234567"). [CONFIRM] the exact form the customer's bank
    // expects (with or without the hyphen) during bank onboarding.
    private static readonly Regex EstablishmentPattern = new("^[0-9]+(-[0-9]+)?$", RegexOptions.CultureInvariant);

    /// <summary>Errors only — see <see cref="ValidateAll"/> for the warnings as well.</summary>
    public static IReadOnlyList<SaudiBankExportIssueDto> Validate(
        KsaWageFileHeader header,
        IReadOnlyList<KsaWageFileRow> rows,
        IReadOnlyCollection<string>? referencesAlreadyUsed = null) =>
        ValidateAll(header, rows, referencesAlreadyUsed).Errors;

    public static KsaWageFileValidation ValidateAll(
        KsaWageFileHeader header,
        IReadOnlyList<KsaWageFileRow> rows,
        IReadOnlyCollection<string>? referencesAlreadyUsed = null)
    {
        var errors = new List<SaudiBankExportIssueDto>();
        var warnings = new List<SaudiBankExportWarningDto>();
        ValidateHeader(header, referencesAlreadyUsed, errors);

        if (rows.Count == 0)
            errors.Add(new(Codes.NoRows, "The file has no employee lines."));

        foreach (var row in rows) ValidateRow(row, errors, warnings);

        AddDuplicates(rows, r => r.MolId, Codes.DuplicateMolId,
            "has the same national ID/Iqama as another line in this file. Each person may appear once.", "employeeId", errors);
        AddDuplicates(rows, r => r.Iban, Codes.DuplicateIban,
            "is paid into the same account as another line in this file. Each account may appear once.", "employeeAccountNumber", errors);
        return new KsaWageFileValidation(errors, warnings);
    }

    /// <summary>
    /// A 16-digit ANB internal account credited with an ANB BIC. ANB Connect accepts it in place of an
    /// IBAN for ANB-to-ANB credits; its own rules live in <see cref="AnbConnectCsvGenerator"/>.
    /// </summary>
    public static bool IsAnbInternalAccount(string? account, string? bic) =>
        account is { Length: 16 } && account.All(char.IsAsciiDigit) && AnbConnectCsvGenerator.IsAnbBic(bic);

    public static void ValidateHeader(KsaWageFileHeader header, IReadOnlyCollection<string>? referencesAlreadyUsed,
        List<SaudiBankExportIssueDto> errors)
    {
        ValidateEstablishmentId(header.MolEstablishmentId, errors);

        if (!string.Equals(header.Currency, "SAR", StringComparison.Ordinal))
            errors.Add(new(Codes.CurrencyNotSar,
                $"Saudi bank and WPS files pay in SAR only. This batch is in {(string.IsNullOrEmpty(header.Currency) ? "no currency" : header.Currency)}.",
                null, "currency"));

        var reference = header.FileReference;
        if (string.IsNullOrEmpty(reference) || reference.Length > MaxFileReferenceLength
            || !reference.All(c => c is (>= '0' and <= '9') or (>= 'A' and <= 'Z')))
            errors.Add(new(Codes.FileReferenceInvalid,
                $"The file reference must be 1 to {MaxFileReferenceLength} upper-case letters or digits. The bank prints it on your statement and rejects a file that reuses one.",
                null, "batchReference"));
        else if (referencesAlreadyUsed is not null && referencesAlreadyUsed.Contains(reference, StringComparer.Ordinal))
            errors.Add(new(Codes.FileReferenceReused,
                "This file reference was already used for this establishment (including on voided runs). The bank rejects a repeated reference — choose a new one.",
                null, "batchReference"));

        if (header.AutoWpsUpload) ValidateNationalUnifiedNo(header.NationalUnifiedNo, required: true, errors);
    }

    public static void ValidateEstablishmentId(string? value, List<SaudiBankExportIssueDto> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new(Codes.EstablishmentMissing,
                "Enter the MOL establishment ID (shown in Qiwa) before creating a bank or WPS file. No placeholder is used.",
                null, "molEstablishmentId"));
            return;
        }
        var digits = value.Count(char.IsAsciiDigit);
        if (value.Length is < 2 or > 15 || !EstablishmentPattern.IsMatch(value) || digits < 2)
            errors.Add(new(Codes.EstablishmentInvalid,
                "The MOL establishment ID must be 2 to 15 characters: the labour-office number and sequence number, digits only, optionally joined by one hyphen (for example 7-1234567).",
                null, "molEstablishmentId"));
        else if (value.Where(char.IsAsciiDigit).All(c => c == '0'))
            errors.Add(new(Codes.EstablishmentPlaceholder,
                "The MOL establishment ID is all zeros, which is a placeholder, not a real establishment. Enter the ID shown in Qiwa.",
                null, "molEstablishmentId"));
    }

    /// <summary>
    /// ANB auto-WPS needs the establishment's national unified number. 10 digits.
    /// [CONFIRM] the exact format with ANB during onboarding (the public ANB Connect page names the
    /// field <c>nationalunifiedno</c> but does not state a pattern; the Ministry's unified number is
    /// issued as 10 digits starting with 7).
    /// </summary>
    public static void ValidateNationalUnifiedNo(string? value, bool required, List<SaudiBankExportIssueDto> errors)
    {
        if (string.IsNullOrEmpty(value))
        {
            if (required)
                errors.Add(new(Codes.NationalUnifiedNoMissing,
                    "Automatic WPS upload is switched on, so the establishment's national unified number (10 digits, shown in Qiwa) is required.",
                    null, "nationalUnifiedNo"));
            return;
        }
        if (value.Length != 10 || !value.All(char.IsAsciiDigit))
            errors.Add(new(Codes.NationalUnifiedNoInvalid,
                "The national unified number must be exactly 10 digits, as shown in Qiwa.", null, "nationalUnifiedNo"));
    }

    private static void ValidateRow(KsaWageFileRow r, List<SaudiBankExportIssueDto> errors, List<SaudiBankExportWarningDto> warnings)
    {
        void Err(string code, string message, string field) =>
            errors.Add(new(code, $"{r.EmployeeLabel}: {message}", r.EmployeeRef, field));

        // ── [MOL-ID] the employee's own government ID, prefix tied to nationality ───────────────
        var isSaudi = IsSaudiNationality(r.Nationality);
        if (string.IsNullOrEmpty(r.MolId))
            Err(Codes.MolIdMissing, "has no national ID or Iqama number. Add it to the employee record.", "employeeId");
        else if (r.MolId.Length != 10 || !r.MolId.All(char.IsAsciiDigit))
            Err(Codes.MolIdInvalid, "national ID/Iqama must be exactly 10 digits.", "employeeId");
        else if (string.IsNullOrWhiteSpace(r.Nationality))
            Err(Codes.NationalityMissing, "has no nationality, so the ID cannot be checked (Saudi IDs start with 1, Iqamas with 2).", "nationality");
        else if (isSaudi && r.MolId[0] != '1')
            Err(Codes.MolIdPrefixMismatch, "is recorded as Saudi, but the ID does not start with 1. A Saudi national ID starts with 1.", "employeeId");
        // An ID starting with 1 is a Saudi national ID. If the nationality is not a spelling we know
        // as Saudi, the likelier fault is the nationality field (e.g. a typo or a local spelling), so
        // that is the field the error names — the ID itself is well-formed.
        else if (!isSaudi && r.MolId[0] == '1')
            Err(Codes.NationalityNotSaudi, $"the ID starts with 1 (a Saudi national ID), but the nationality \"{r.Nationality!.Trim()}\" is not recognised as Saudi. Record the nationality as Saudi, or correct the ID if this person holds an Iqama.", "nationality");
        // [CONFIRM] GCC nationals working in KSA may be registered without an Iqama; until confirmed
        // they are held to the non-Saudi rule and block rather than file under a guessed ID.
        else if (!isSaudi && r.MolId[0] != '2')
            Err(Codes.MolIdPrefixMismatch, "is recorded as non-Saudi, but the ID does not start with 2. An Iqama number starts with 2.", "employeeId");

        // ── [59-ACC] 24-character upper-case Saudi IBAN, mod-97, bank code consistent with BIC ───
        // An ANB internal account (16 digits, ANB BIC) is not an IBAN: it falls through to the ANB
        // generator's own account rules rather than failing as a non-Saudi IBAN.
        var iban = r.Iban;
        if (IsAnbInternalAccount(iban, r.BankBic)) { }
        else if (string.IsNullOrEmpty(iban))
            Err(Codes.IbanMissing, "has no IBAN on the payment record.", "employeeAccountNumber");
        else if (!iban.StartsWith("SA", StringComparison.OrdinalIgnoreCase))
            Err(Codes.IbanNotSaudi, "the IBAN is not a Saudi (SA) account. Saudi WPS pays local accounts only.", "employeeAccountNumber");
        else if (iban.Length != 24)
            Err(Codes.IbanLength, $"the IBAN has {iban.Length} characters; a Saudi IBAN has exactly 24, with no spaces.", "employeeAccountNumber");
        else if (!iban.All(c => c is (>= '0' and <= '9') or (>= 'A' and <= 'Z')))
            Err(Codes.IbanLowerCase, "the IBAN must use upper-case letters and digits only.", "employeeAccountNumber");
        else if (!IbanValidator.IsSaudiIban(iban))
            Err(Codes.IbanChecksum, "the IBAN fails its check-digit test (ISO 13616 mod-97). Re-check it with the employee.", "employeeAccountNumber");
        else
        {
            var bankCode = iban.Substring(4, 2);
            if (!AcceptedSarieIdsByIbanBankCode.TryGetValue(bankCode, out var accepted))
                warnings.Add(new(WarningCodes.IbanBankCodeUnverified,
                    $"{r.EmployeeLabel}: the IBAN's bank code {bankCode} is not in our verified list of Saudi banks (it may be a newly licensed bank). The IBAN itself is valid; confirm with your bank that it accepts this account."));
            else if (!string.IsNullOrEmpty(r.BankBic)
                     && !accepted.Any(id => r.BankBic.StartsWith(id, StringComparison.Ordinal)))
                Err(Codes.BankCodeMismatch, $"the IBAN belongs to bank {string.Join(" / ", accepted)}, but the recorded bank BIC is {r.BankBic}. Correct whichever is wrong.", "bicCode");
        }

        // ── [59-NAME] ≤35 per line, one script, no control characters, no formula prefix ─────────
        var name = r.Name;
        if (string.IsNullOrWhiteSpace(name))
            Err(Codes.NameMissing, "has no name for the bank file.", "employeeName");
        else
        {
            if (name.Length > MaxNameLineLength)
                Err(Codes.NameTooLong, $"the name has {name.Length} characters; the bank file allows {MaxNameLineLength}. Record a shorter registered name — nothing is cut automatically.", "employeeName");
            if (name.Any(char.IsControl))
                Err(Codes.NameControlCharacter, "the name contains a tab, line break or other control character.", "employeeName");
            if (name[0] is '=' or '+' or '-' or '@')
                Err(Codes.NameFormulaPrefix, "the name must not start with =, +, - or @.", "employeeName");
            var hasArabic = name.Any(IsArabic);
            var hasLatin = name.Any(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z'));
            if (hasArabic && hasLatin)
                Err(Codes.NameMixedScript, "the name mixes Arabic and English letters. Each field must be in one language.", "employeeName");
        }

        // ── Amounts: net = basic + housing + other − deductions, against the locked payslip ──────
        foreach (var (field, value) in new[] { ("gross", r.Gross), ("basicSalary", r.Basic), ("housingAllowance", r.Housing), ("salaryAmount", r.Net), ("salaryDeductions", r.SlipDeductions) })
            if (decimal.Round(value, 2) != value)
                Err(Codes.AmountPrecision, $"{field} has more than two decimals.", field);

        if (r.Net <= 0m)
            Err(Codes.NetNotPositive, "net pay must be more than zero.", "salaryAmount");

        var otherEarnings = r.Gross - r.Basic - r.Housing;
        var deductions = r.Gross - r.Net;
        if (otherEarnings < 0m)
            Err(Codes.OtherEarningsNegative, $"basic ({Fmt(r.Basic)}) plus housing ({Fmt(r.Housing)}) is more than gross pay ({Fmt(r.Gross)}).", "otherEarnings");
        // With OEA and DED derived this way, net = basic + housing + OEA − DED holds by construction; the
        // check that can actually fail — and the one the spec's identity depends on — is that DED is the
        // deduction total the payslip shows. This is the ONE place that identity is checked for the
        // Saudi file: the service and the ANB generator defer to it, so one fault shows one message.
        if (deductions != r.SlipDeductions)
            Err(Codes.DeductionsUnreconciled, $"net pay does not equal basic + housing + other earnings − deductions: gross minus net is {Fmt(deductions)}, but the payslip shows deductions of {Fmt(r.SlipDeductions)}. Re-process the payslip.", "salaryDeductions");

        // Saudi Labour Law Art. 92/93: deductions for DEBTS owed to the employer (loan and advance
        // instalments, penalties/fines, damages) may not exceed half of the wage due. Statutory GOSI,
        // absence/loss-of-pay and unpaid leave are not debts and are not counted — counting them blocked
        // lawful payslips. An approver override recorded against the pre-lock error (which requires the
        // reference of its written basis) is honoured here.
        // Wage due = gross minus absence/LOP and unpaid leave (WageDeductionClassification.WageDue); a row built without its
        // lines falls back to gross.
        var wageDue = r.WageDue ?? r.Gross;
        if (!r.DebtCapOverridden && WageDeductionClassification.ExceedsHalfWage(r.DebtDeductions, wageDue))
            Err(Codes.DeductionsOverHalf, $"loan, advance, penalty and damages deductions ({Fmt(r.DebtDeductions)}) are more than half of the wage due after absence ({Fmt(wageDue)}). Saudi Labour Law Art. 92/93 caps them at 50%. Reschedule the instalment or reduce the deduction, then re-process.", "salaryDeductions");
    }

    private static bool IsArabic(char c) =>
        c is (>= '؀' and <= 'ۿ') or (>= 'ݐ' and <= 'ݿ') or (>= 'ࢠ' and <= 'ࣿ')
            or (>= 'ﭐ' and <= '﷿') or (>= 'ﹰ' and <= '﻿');

    private static string Fmt(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    private static void AddDuplicates(IReadOnlyList<KsaWageFileRow> rows, Func<KsaWageFileRow, string?> key,
        string code, string what, string field, List<SaudiBankExportIssueDto> errors)
    {
        foreach (var group in rows.Where(r => !string.IsNullOrEmpty(key(r))).GroupBy(key, StringComparer.Ordinal).Where(g => g.Count() > 1))
            foreach (var r in group)
                errors.Add(new(code, $"{r.EmployeeLabel}: {what}", r.EmployeeRef, field));
    }
}
