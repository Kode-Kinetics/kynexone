using System.Globalization;
using System.Text.RegularExpressions;
using Zayra.Api.Application.Common;

namespace Zayra.Api.Infrastructure.Payroll.SaudiBankExports;

/// <summary>
/// Whether a rule is encoded in this repository (WpsConformance.cs, KsaWageFileRules.cs, WpsSifValidator.cs) or is
/// merely assumed. An Unverified rule never blocks anything: it is reported as a warning so a real bank file is not
/// refused on a guess.
/// </summary>
public enum WpsRuleBasis { VerifiedFromRepoFact, Unverified }

public enum WpsFindingSeverity { Error, Warning }

/// <summary>One thing the reader found. <see cref="Line"/> is null for a property of the whole file. English and Arabic.</summary>
public sealed record WpsReturnedFileFinding(
    string Code, WpsFindingSeverity Severity, WpsRuleBasis Basis, int? Line, string Message, string MessageAr);

/// <summary>One entry of the rule catalogue: what is checked, whether the repo proves it, and where.</summary>
public sealed record WpsReturnedFileRule(string Code, WpsRuleBasis Basis, string Source);

/// <summary>What the company expects of the file. A null expectation means "do not check the establishment".</summary>
public sealed record WpsReturnedFileExpectation(string? MolEstablishmentId);

public sealed record WpsReturnedFileHeader(
    string? DestId, string? EstbId, string? BankAccount, string? Currency, DateOnly? ValueDate, decimal? DeclaredTotal,
    string? FileRef, string? MolEstablishmentId, string? FileRejectCode, DateOnly? FileDate);

/// <summary>One employee line as the file states it. <see cref="Blocked"/> rows failed a format rule and are never used.</summary>
public sealed record WpsReturnedFileRow(
    int Line, string? MolId, string? Iban, string? Name, decimal? Net, decimal? Basic, decimal? Housing,
    decimal? OtherEarnings, decimal? Deductions, string? TrnRef, string? TrnStatus, DateOnly? TrnDate,
    bool Blocked, IReadOnlyList<string> BlockingCodes);

public sealed class WpsReturnedFileReadResult
{
    public WpsReturnedFileHeader? Header { get; init; }
    public IReadOnlyList<WpsReturnedFileRow> Rows { get; init; } = Array.Empty<WpsReturnedFileRow>();
    public IReadOnlyList<WpsReturnedFileFinding> Findings { get; init; } = Array.Empty<WpsReturnedFileFinding>();
    /// <summary>True when an error belongs to the file as a whole (header, totals, establishment): nothing in it may be applied.</summary>
    public bool HasFileLevelError { get; init; }
}

/// <summary>
/// READ-AND-COMPARE reader for a WPS wage file that the employer's BANK produced and digitally signed.
///
/// <para>It never writes, repairs, trims or strips anything: the input string is only read, and the bank's signature
/// (whatever lines carry it) is left exactly as received; lines it does not recognise are ignored with a warning.</para>
///
/// <para>EVERY RULE HAS A BASIS. VerifiedFromRepoFact = encoded in <c>WpsConformance</c> / <c>KsaWageFileRules</c> /
/// <c>WpsSifValidator</c> (which cite the MHRSD specification's tags: TAB-delimited, bracketed tags, comma decimal,
/// [32A-CCY] SAR, [FILE-REF] 16x, [MOL-ESTBID] 2d-15d, [59-ACC] 24-char SA IBAN, [59-NAME] 35 per line one language,
/// [MOL-ID] 10d, [32B-AMT] = [MOL-BAS] + [MOL-HAL] + [MOL-OEA] − [MOL-DED]). Unverified = not proven by the repo; it is a
/// warning, never a block. See <see cref="Rules"/>.</para>
///
/// <para>LAYOUT IS UNVERIFIED. The repo names the tags but holds no real bank file. This reader assumes: one record per
/// line, each record a run of <c>[TAG]&lt;TAB&gt;value</c> pairs; header tags before the first employee line; an employee
/// line holds the row tags. A file in any other layout is refused with errors, never guessed at.</para>
/// </summary>
public static class WpsReturnedFileValidator
{
    private static readonly HashSet<string> HeaderTags = new(StringComparer.Ordinal)
    {
        "DEST-ID", "ESTB-ID", "BANK-ACC", "32A-CCY", "32A-VAL", "32A-AMT", "D-DATE", "FILE-REF", "FILE-REJCDE", "MOL-ESTBID",
    };

    private static readonly HashSet<string> RowTags = new(StringComparer.Ordinal)
    {
        "32B-AMT", "59-ACC", "59-NAME", "MOL-ID", "MOL-BAS", "MOL-HAL", "MOL-OEA", "MOL-DED", "TRN-REF", "TRN-STATUS", "TRN-DATE",
    };

    private static readonly string[] BreakdownTags = { "MOL-BAS", "MOL-HAL", "MOL-OEA", "MOL-DED" };
    private static readonly Regex TagPattern = new(@"^\[[A-Z0-9-]{1,24}\]$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex AmountPattern = new(@"^[0-9]{1,15}(,[0-9]+)?$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public const int MaxFileReferenceLength = KsaWageFileRules.MaxFileReferenceLength;
    public const int MaxNameLength = KsaWageFileRules.MaxNameLineLength;

    private const WpsRuleBasis V = WpsRuleBasis.VerifiedFromRepoFact;
    private const WpsRuleBasis U = WpsRuleBasis.Unverified;

    private sealed record Entry(WpsRuleBasis Basis, string Source, string En, string Ar);

    private const string Conformance = "WpsConformance.cs";
    private const string Rules_ = "KsaWageFileRules.cs";
    private const string Note = "demo-notes research of the 2017 spec; not encoded in repo code";

    private static readonly IReadOnlyDictionary<string, Entry> Catalogue = new Dictionary<string, Entry>(StringComparer.Ordinal)
    {
        // ── the file as a whole ──
        ["line_layout"] = new(U, "Assumed: one record per line, [TAG]<TAB>value pairs; no real bank file is held in the repo",
            "The tab and tag layout of the file is assumed from the tag names the repository cites and has not been confirmed against a real bank file.",
            "تنسيق الملف (علامات الجدولة والوسوم) مفترض من أسماء الوسوم الواردة في المستودع ولم يتم التأكد منه على ملف بنك حقيقي."),
        ["file_empty"] = new(V, Rules_ + " NoRows", "The file is empty.", "الملف فارغ."),
        ["no_tagged_lines"] = new(V, Conformance + " (bracketed field tags)",
            "The file has no lines made of bracketed [TAG] fields, so it is not in the tab-delimited layout this reader understands.",
            "لا يحتوي الملف على أي سطر بحقول موسومة بين أقواس مربعة [TAG]، لذلك فهو ليس بالتنسيق المفصول بعلامات الجدولة الذي يقرؤه هذا القارئ."),
        ["no_rows"] = new(V, Rules_ + " NoRows", "The file has a header but no employee lines.", "يحتوي الملف على ترويسة ولكن بلا أي سطر موظف."),
        ["unrecognised_line"] = new(U, "Bank signature or trailer lines are not described in the repo",
            "A line with no [TAG] fields was ignored (for example the bank's signature). It was not changed.",
            "تم تجاهل سطر لا يحتوي على أي حقل [TAG] (مثل توقيع البنك). لم يتم تغييره."),
        ["unknown_tag"] = new(U, "Only the tags cited by the repo are read",
            "The field [{0}] is not one this reader knows, so it was ignored.", "الحقل [{0}] غير معروف لهذا القارئ لذلك تم تجاهله."),
        ["stray_value"] = new(V, Conformance + " (TAB-delimited fields; a tab inside a value shifts the fields)",
            "The line has text that does not belong to any [TAG] field (a tab inside a value shifts every field after it). The line was not used.",
            "يحتوي السطر على نص لا يتبع أي حقل [TAG] (وجود علامة جدولة داخل قيمة يزيح كل الحقول بعدها). لم يتم استخدام هذا السطر."),
        ["control_character"] = new(V, Rules_ + " NameControlCharacter",
            "The line contains a control character. The line was not used.", "يحتوي السطر على رمز تحكم. لم يتم استخدام هذا السطر."),
        // ── header ──
        ["header_tag_missing"] = new(V, Conformance + " (header carries DEST-ID, BANK-ACC, FILE-REF) and " + Rules_ + " (32A-CCY, MOL-ESTBID); 32A-AMT: header total",
            "The header field [{0}] is missing or empty. Nothing was applied.", "حقل الترويسة [{0}] غير موجود أو فارغ. لم يتم تطبيق أي شيء."),
        ["header_tag_missing_unverified"] = new(U, "ESTB-ID and 32A-VAL are cited by the repo but not shown to be mandatory",
            "The header field [{0}] is not present. It is not required here because the repository does not prove it is mandatory.",
            "حقل الترويسة [{0}] غير موجود. لا يُشترط هنا لأن المستودع لا يثبت أنه إلزامي."),
        ["header_tag_duplicated"] = new(V, Conformance + " (one header group)",
            "The header field [{0}] appears more than once. Nothing was applied.", "حقل الترويسة [{0}] مكرر. لم يتم تطبيق أي شيء."),
        ["header_after_rows"] = new(V, Conformance + " (header group, then one row per employee)",
            "The header field [{0}] appears after the first employee line. Nothing was applied.",
            "حقل الترويسة [{0}] ظهر بعد أول سطر موظف. لم يتم تطبيق أي شيء."),
        ["file_reference_missing"] = new(V, Rules_ + " FileReferenceInvalid ([FILE-REF] required)",
            "The file reference [FILE-REF] is missing or empty. The bank prints it on your statement. Nothing was applied.",
            "مرجع الملف [FILE-REF] غير موجود أو فارغ. يطبعه البنك في كشف حسابك. لم يتم تطبيق أي شيء."),
        ["file_reference_too_long"] = new(V, Rules_ + " MaxFileReferenceLength ([FILE-REF] 16x)",
            "The file reference [FILE-REF] is longer than 16 characters. Nothing was applied.",
            "مرجع الملف [FILE-REF] أطول من 16 حرفاً. لم يتم تطبيق أي شيء."),
        ["file_reference_charset"] = new(U, Rules_ + " restricts OUR outbound reference; the bank chose this one",
            "The file reference [FILE-REF] has characters other than upper-case letters and digits. This is a warning only because the bank chose it.",
            "مرجع الملف [FILE-REF] يحتوي على أحرف غير الحروف الكبيرة والأرقام. هذا تحذير فقط لأن البنك هو من اختاره."),
        ["currency_not_sar"] = new(V, Rules_ + " CurrencyNotSar ([32A-CCY] SAR only)",
            "The file currency [32A-CCY] is {0}; Saudi wage files pay in SAR only. Nothing was applied.",
            "عملة الملف [32A-CCY] هي {0}؛ ملفات الأجور السعودية بالريال السعودي فقط. لم يتم تطبيق أي شيء."),
        ["mol_establishment_id_missing"] = new(V, Rules_ + " EstablishmentMissing",
            "The file has no establishment ID [MOL-ESTBID]. Nothing was applied.", "لا يحتوي الملف على رقم المنشأة [MOL-ESTBID]. لم يتم تطبيق أي شيء."),
        ["mol_establishment_id_invalid"] = new(V, Rules_ + " EstablishmentInvalid ([MOL-ESTBID] 2d-15d; the hyphen form is [CONFIRM] in the repo)",
            "The establishment ID [MOL-ESTBID] is not 2 to 15 characters of digits with at most one hyphen. Nothing was applied.",
            "رقم المنشأة [MOL-ESTBID] ليس من 2 إلى 15 خانة أرقام مع شرطة واحدة على الأكثر. لم يتم تطبيق أي شيء."),
        ["mol_establishment_id_placeholder"] = new(V, Rules_ + " EstablishmentPlaceholder",
            "The establishment ID [MOL-ESTBID] is all zeros, which is a placeholder. Nothing was applied.",
            "رقم المنشأة [MOL-ESTBID] كله أصفار وهو قيمة مؤقتة. لم يتم تطبيق أي شيء."),
        ["establishment_mismatch"] = new(V, "WpsEstablishmentAuthority.SameEstablishment (company establishment is the authority)",
            "The file is for establishment {0}, but this company's establishment is {1}. Nothing was applied.",
            "الملف خاص بالمنشأة {0} بينما منشأة هذه الشركة هي {1}. لم يتم تطبيق أي شيء."),
        ["establishment_not_configured"] = new(V, "WpsEstablishmentAuthority.Resolve",
            "This company has no single MOL establishment ID on record, so the file's establishment cannot be checked. Set it in the company's bank-file settings and import again. Nothing was applied.",
            "ليس لدى هذه الشركة رقم منشأة واحد مسجل، لذلك لا يمكن التحقق من منشأة الملف. أدخله في إعدادات ملف البنك للشركة ثم أعد الاستيراد. لم يتم تطبيق أي شيء."),
        ["establishment_not_checked"] = new(V, "WpsEstablishmentAuthority.Resolve",
            "The establishment in the file was not compared with the company's establishment.",
            "لم تتم مقارنة المنشأة الواردة في الملف بمنشأة الشركة."),
        ["header_total_invalid"] = new(V, Conformance + " ('1234567890123,12': digits, a comma, two decimals)",
            "The header total [32A-AMT] is not a valid amount (digits, a comma and two decimals). Nothing was applied.",
            "إجمالي الترويسة [32A-AMT] ليس مبلغاً صالحاً (أرقام وفاصلة ومنزلتان عشريتان). لم يتم تطبيق أي شيء."),
        ["header_total_mismatch"] = new(V, "WpsSifValidator run-total reconciliation; the header total equals the sum of the lines",
            "The header total [32A-AMT] is {0} but the employee lines add up to {1}. The file may be incomplete or altered. Nothing was applied.",
            "إجمالي الترويسة [32A-AMT] هو {0} بينما مجموع أسطر الموظفين {1}. قد يكون الملف ناقصاً أو معدلاً. لم يتم تطبيق أي شيء."),
        ["header_total_unverifiable"] = new(V, "WpsSifValidator run-total reconciliation",
            "The header total cannot be checked because at least one employee line has no readable [32B-AMT]. Nothing was applied.",
            "لا يمكن التحقق من إجمالي الترويسة لأن سطر موظف واحد على الأقل بلا [32B-AMT] مقروء. لم يتم تطبيق أي شيء."),
        ["file_rejected"] = new(U, "FILE-REJCDE is named only in " + Note,
            "The file carries a file-level reject code [FILE-REJCDE] {0}: the whole file was rejected. Nothing was applied. Ask the bank what this code means.",
            "يحمل الملف رمز رفض على مستوى الملف [FILE-REJCDE] {0}: تم رفض الملف بالكامل. لم يتم تطبيق أي شيء. اسأل البنك عن معنى هذا الرمز."),
        ["date_format_invalid"] = new(U, "YYYYMMDD comes from " + Note,
            "The date in [{0}] is not a valid YYYYMMDD date. This is a warning only because the date format is not proven by the repository.",
            "التاريخ في [{0}] ليس تاريخاً صالحاً بصيغة YYYYMMDD. هذا تحذير فقط لأن صيغة التاريخ غير مثبتة في المستودع."),
        // ── a row ──
        ["duplicate_tag"] = new(V, Conformance + " (one value per field)",
            "The employee line repeats the field [{0}]. The line was not used.", "يكرر سطر الموظف الحقل [{0}]. لم يتم استخدام هذا السطر."),
        ["mixed_line"] = new(V, Conformance + " (header group, then employee rows)",
            "An employee line also carries a header field [{0}]. The line was not used.", "سطر الموظف يحمل أيضاً حقل ترويسة [{0}]. لم يتم استخدام هذا السطر."),
        ["row_field_missing"] = new(V, Rules_ + " (59-ACC, 59-NAME, MOL-ID, 32B-AMT required)",
            "The employee line has no value for [{0}]. The line was not used.", "سطر الموظف بلا قيمة للحقل [{0}]. لم يتم استخدام هذا السطر."),
        ["wage_breakdown_missing"] = new(V, Conformance + " (MISSING MANDATORY FIELDS: MOL-BAS, MOL-HAL, MOL-OEA, MOL-DED)",
            "The employee line has no wage breakdown value for {0}. The line was not used.",
            "سطر الموظف بلا قيمة لتفصيل الأجر في {0}. لم يتم استخدام هذا السطر."),
        ["amount_invalid"] = new(V, Conformance + " ('1234567890123,12')",
            "The amount in [{0}] is not valid (digits, a comma and two decimals). The line was not used.",
            "المبلغ في [{0}] غير صالح (أرقام وفاصلة ومنزلتان عشريتان). لم يتم استخدام هذا السطر."),
        ["amount_point_decimal"] = new(V, Conformance + " (DECIMAL SEPARATOR is a comma)",
            "The amount in [{0}] uses a point as the decimal separator; the specification uses a comma. The line was not used.",
            "المبلغ في [{0}] يستخدم نقطة كفاصل عشري بينما المواصفة تستخدم فاصلة. لم يتم استخدام هذا السطر."),
        ["amount_precision"] = new(V, Rules_ + " AmountPrecision",
            "The amount in [{0}] has more than two decimals. The line was not used.",
            "المبلغ في [{0}] فيه أكثر من منزلتين عشريتين. لم يتم استخدام هذا السطر."),
        ["amount_no_decimals"] = new(U, "Whether the bank always writes ',00' is not proven by the repo",
            "The amount in [{0}] has no decimal part; it was read as a whole number.", "المبلغ في [{0}] بلا جزء عشري؛ تمت قراءته كعدد صحيح."),
        ["net_identity_failed"] = new(V, Rules_ + " ([32B-AMT] = [MOL-BAS] + [MOL-HAL] + [MOL-OEA] − [MOL-DED])",
            "The net amount [32B-AMT] does not equal basic + housing + other earnings − deductions. The line was not used.",
            "صافي المبلغ [32B-AMT] لا يساوي الأساسي + السكن + الإيرادات الأخرى − الخصومات. لم يتم استخدام هذا السطر."),
        ["net_not_positive"] = new(V, Rules_ + " NetNotPositive", "The net amount [32B-AMT] must be more than zero. The line was not used.",
            "صافي المبلغ [32B-AMT] يجب أن يكون أكبر من صفر. لم يتم استخدام هذا السطر."),
        ["mol_id_invalid"] = new(V, Rules_ + " MolIdInvalid ([MOL-ID] 10d)",
            "The government ID [MOL-ID] is not exactly 10 digits. The line was not used.",
            "رقم الهوية [MOL-ID] ليس 10 أرقام بالضبط. لم يتم استخدام هذا السطر."),
        ["mol_id_prefix_unexpected"] = new(U, "A Border Number may be written as the ID (WageFileGovernmentId), and its prefix is not in the repo",
            "The government ID [MOL-ID] does not start with 1 or 2.", "رقم الهوية [MOL-ID] لا يبدأ بـ 1 أو 2."),
        ["iban_invalid"] = new(V, Rules_ + " IbanLength / IbanLowerCase / IbanChecksum ([59-ACC] 24-char upper-case SA IBAN, mod-97)",
            "The account [59-ACC] is not a valid 24-character upper-case Saudi IBAN. The line was not used.",
            "الحساب [59-ACC] ليس آيبان سعودياً صالحاً من 24 خانة بأحرف كبيرة. لم يتم استخدام هذا السطر."),
        ["name_missing"] = new(V, Rules_ + " NameMissing", "The employee line has no name [59-NAME]. The line was not used.",
            "سطر الموظف بلا اسم [59-NAME]. لم يتم استخدام هذا السطر."),
        ["name_too_long"] = new(V, Rules_ + " NameTooLong ([59-NAME] 35 per line)",
            "The name [59-NAME] is longer than 35 characters. The line was not used.", "الاسم [59-NAME] أطول من 35 حرفاً. لم يتم استخدام هذا السطر."),
        ["name_mixed_script"] = new(V, Rules_ + " NameMixedScript (one language per field)",
            "The name [59-NAME] mixes Arabic and English letters. The line was not used.",
            "الاسم [59-NAME] يخلط بين الحروف العربية والإنجليزية. لم يتم استخدام هذا السطر."),
        ["name_formula_prefix"] = new(V, Rules_ + " NameFormulaPrefix (a warning here: the name is the bank's)",
            "The name [59-NAME] starts with =, +, - or @; do not open this file in a spreadsheet.",
            "الاسم [59-NAME] يبدأ بـ = أو + أو - أو @؛ لا تفتح هذا الملف في برنامج جداول."),
        ["name_contains_comma"] = new(U, "Whether a comma in a name is allowed in a TAB-delimited file is not proven by the repo",
            "The name [59-NAME] contains a comma. It was kept as written and did not affect any other field.",
            "الاسم [59-NAME] يحتوي على فاصلة. تم الإبقاء عليه كما هو ولم يؤثر على أي حقل آخر."),
        ["duplicate_mol_id"] = new(V, Rules_ + " DuplicateMolId",
            "Another line in the file has the same government ID. Both lines were not used.",
            "يوجد سطر آخر في الملف بنفس رقم الهوية. لم يتم استخدام السطرين."),
        ["duplicate_iban"] = new(V, Rules_ + " DuplicateIban",
            "Another line in the file credits the same account. Both lines were not used.",
            "يوجد سطر آخر في الملف يحوّل إلى الحساب نفسه. لم يتم استخدام السطرين."),
        ["trn_ref_too_long"] = new(U, "TRN-REF is named only in " + Note + "; our column holds 140",
            "The transaction reference [TRN-REF] is longer than 140 characters. The line was not used.",
            "مرجع العملية [TRN-REF] أطول من 140 حرفاً. لم يتم استخدام هذا السطر."),
        ["status_too_long"] = new(U, "TRN-STATUS is named only in " + Note + "; our column holds 64",
            "The status [TRN-STATUS] is longer than 64 characters. The line was not used.",
            "الحالة [TRN-STATUS] أطول من 64 حرفاً. لم يتم استخدام هذا السطر."),
    };

    /// <summary>Every rule, with whether the repository proves it and where. Both bases are present on purpose.</summary>
    public static IReadOnlyList<WpsReturnedFileRule> Rules { get; } =
        Catalogue.Select(kv => new WpsReturnedFileRule(kv.Key, kv.Value.Basis, kv.Value.Source)).ToList();

    /// <summary>Every code the reader can emit, each with an English and an Arabic message.</summary>
    public static IReadOnlyCollection<string> MessageCodes => Catalogue.Keys.ToList();

    public static (string En, string Ar) MessageFor(string code, params object[] args)
    {
        var e = Catalogue[code];
        return (Format(e.En, args), Format(e.Ar, args));
    }

    public static WpsRuleBasis BasisFor(string code) => Catalogue[code].Basis;

    private static string Format(string template, object[] args) =>
        args.Length == 0 ? template : string.Format(CultureInfo.InvariantCulture, template, args);

    // ───────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class RowBuilder
    {
        public int Line;
        public string? MolId, Iban, Name, TrnRef, TrnStatus;
        public decimal? Net, Basic, Housing, Other, Deductions;
        public DateOnly? TrnDate;
        public readonly List<string> Blocking = new();
        public bool Blocked => Blocking.Count > 0;
    }

    /// <summary>Reads <paramref name="content"/>. Pure: no I/O, no clock, no culture, and the input is never altered.</summary>
    public static WpsReturnedFileReadResult Read(string? content, WpsReturnedFileExpectation? expected)
    {
        var findings = new List<WpsReturnedFileFinding>();
        var fileBlocked = false;

        void Add(string code, WpsFindingSeverity sev, int? line, bool fileLevel, params object[] args)
        {
            var (en, ar) = MessageFor(code, args);
            findings.Add(new WpsReturnedFileFinding(code, sev, Catalogue[code].Basis, line, en, ar));
            if (sev == WpsFindingSeverity.Error && fileLevel) fileBlocked = true;
        }

        if (string.IsNullOrEmpty(content))
        {
            Add("file_empty", WpsFindingSeverity.Error, null, true);
            return new WpsReturnedFileReadResult { Findings = findings, HasFileLevelError = true };
        }

        var text = content[0] == '\uFEFF' ? content[1..] : content;
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var headerValues = new Dictionary<string, string>(StringComparer.Ordinal);
        var headerLine = new Dictionary<string, int>(StringComparer.Ordinal);
        var rows = new List<RowBuilder>();
        var unknownSeen = new HashSet<string>(StringComparer.Ordinal);
        var taggedLines = 0;

        for (var idx = 0; idx < lines.Length; idx++)
        {
            var lineNo = idx + 1;
            var line = lines[idx];
            if (string.IsNullOrWhiteSpace(line)) continue;

            var hasControl = line.Any(c => char.IsControl(c) && c != '\t');
            var pairs = new List<(string Tag, string Value)>();
            var strays = 0;
            var tokens = line.Split('\t');
            for (var i = 0; i < tokens.Length; i++)
            {
                var t = tokens[i];
                if (TagPattern.IsMatch(t))
                {
                    var tag = t[1..^1];
                    if (i + 1 < tokens.Length && !TagPattern.IsMatch(tokens[i + 1])) { pairs.Add((tag, tokens[i + 1])); i++; }
                    else pairs.Add((tag, string.Empty));
                }
                else if (t.Length > 0) strays++;
            }

            if (pairs.Count == 0)
            {
                Add("unrecognised_line", WpsFindingSeverity.Warning, lineNo, false);
                continue;
            }
            taggedLines++;

            foreach (var (tag, _) in pairs)
                if (!HeaderTags.Contains(tag) && !RowTags.Contains(tag) && unknownSeen.Add(tag))
                    Add("unknown_tag", WpsFindingSeverity.Warning, lineNo, false, tag);

            var isRow = pairs.Any(p => RowTags.Contains(p.Tag));
            if (isRow)
            {
                var rb = ReadRow(lineNo, pairs, strays, hasControl, Add);
                rows.Add(rb);
                var headerTagInRow = pairs.FirstOrDefault(p => HeaderTags.Contains(p.Tag));
                if (headerTagInRow.Tag is not null) Block(rb, "mixed_line", lineNo, Add, headerTagInRow.Tag);
                continue;
            }

            // A header line.
            if (hasControl) { Add("control_character", WpsFindingSeverity.Error, lineNo, true); }
            if (strays > 0) Add("stray_value", WpsFindingSeverity.Error, lineNo, true);
            foreach (var (tag, value) in pairs)
            {
                if (!HeaderTags.Contains(tag)) continue;
                if (rows.Count > 0) { Add("header_after_rows", WpsFindingSeverity.Error, lineNo, true, tag); continue; }
                if (headerValues.ContainsKey(tag)) { Add("header_tag_duplicated", WpsFindingSeverity.Error, lineNo, true, tag); continue; }
                headerValues[tag] = value;
                headerLine[tag] = lineNo;
            }
        }

        if (taggedLines == 0)
        {
            Add("no_tagged_lines", WpsFindingSeverity.Error, null, true);
            return new WpsReturnedFileReadResult { Findings = findings, HasFileLevelError = true };
        }

        var header = ReadHeader(headerValues, headerLine, expected, Add);
        if (rows.Count == 0) Add("no_rows", WpsFindingSeverity.Error, null, true);

        // The same person or the same account twice: both lines are blocked, never "first one wins".
        foreach (var g in rows.Where(r => !string.IsNullOrEmpty(r.MolId)).GroupBy(r => r.MolId!, StringComparer.Ordinal).Where(g => g.Count() > 1))
            foreach (var r in g) Block(r, "duplicate_mol_id", r.Line, Add);
        foreach (var g in rows.Where(r => !string.IsNullOrEmpty(r.Iban)).GroupBy(r => r.Iban!, StringComparer.Ordinal).Where(g => g.Count() > 1))
            foreach (var r in g) Block(r, "duplicate_iban", r.Line, Add);

        // The header total against the sum of EVERY line in the file (blocked or not): a line dropped or altered after
        // the bank wrote the total shows up here.
        if (header.DeclaredTotal is { } declared && rows.Count > 0)
        {
            if (rows.Any(r => r.Net is null))
                Add("header_total_unverifiable", WpsFindingSeverity.Error, null, true);
            else
            {
                var sum = rows.Sum(r => r.Net!.Value);
                if (sum != declared)
                    Add("header_total_mismatch", WpsFindingSeverity.Error, null, true, Money(declared), Money(sum));
            }
        }

        return new WpsReturnedFileReadResult
        {
            Header = header,
            Findings = findings,
            HasFileLevelError = fileBlocked,
            Rows = rows.Select(r => new WpsReturnedFileRow(
                r.Line, r.MolId, r.Iban, r.Name, r.Net, r.Basic, r.Housing, r.Other, r.Deductions, r.TrnRef, r.TrnStatus,
                r.TrnDate, r.Blocked, r.Blocking.Distinct().ToList())).ToList(),
        };
    }

    private delegate void AddFinding(string code, WpsFindingSeverity sev, int? line, bool fileLevel, params object[] args);

    private static void Block(RowBuilder rb, string code, int line, AddFinding add, params object[] args)
    {
        rb.Blocking.Add(code);
        add(code, WpsFindingSeverity.Error, line, false, args);
    }

    private static string Money(decimal d) => d.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Only letters, digits, '-', '_' and '.', at most 32 of them: safe to put in a message.</summary>
    internal static string Safe(string? value) =>
        new((value ?? string.Empty).Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').Take(32).ToArray());

    private static WpsReturnedFileHeader ReadHeader(
        Dictionary<string, string> h, Dictionary<string, int> lineOf, WpsReturnedFileExpectation? expected, AddFinding add)
    {
        string? Get(string tag) => h.TryGetValue(tag, out var v) ? v : null;
        int? L(string tag) => lineOf.TryGetValue(tag, out var l) ? l : null;

        foreach (var tag in new[] { "DEST-ID", "BANK-ACC", "32A-CCY", "32A-AMT" })
            if (string.IsNullOrEmpty(Get(tag))) add("header_tag_missing", WpsFindingSeverity.Error, L(tag), true, tag);
        foreach (var tag in new[] { "ESTB-ID", "32A-VAL" })
            if (string.IsNullOrEmpty(Get(tag))) add("header_tag_missing_unverified", WpsFindingSeverity.Warning, L(tag), false, tag);

        // [FILE-REF] 16x
        var fileRef = Get("FILE-REF");
        if (string.IsNullOrEmpty(fileRef)) add("file_reference_missing", WpsFindingSeverity.Error, L("FILE-REF"), true);
        else
        {
            if (fileRef.Length > MaxFileReferenceLength) add("file_reference_too_long", WpsFindingSeverity.Error, L("FILE-REF"), true);
            if (!fileRef.All(c => c is (>= '0' and <= '9') or (>= 'A' and <= 'Z')))
                add("file_reference_charset", WpsFindingSeverity.Warning, L("FILE-REF"), false);
        }

        // [32A-CCY] SAR only
        var ccy = Get("32A-CCY");
        if (!string.IsNullOrEmpty(ccy) && !string.Equals(ccy, "SAR", StringComparison.Ordinal))
            add("currency_not_sar", WpsFindingSeverity.Error, L("32A-CCY"), true, Safe(ccy));

        // [MOL-ESTBID]
        var estb = Get("MOL-ESTBID");
        var estbWellFormed = false;
        if (string.IsNullOrEmpty(estb)) add("header_tag_missing", WpsFindingSeverity.Error, L("MOL-ESTBID"), true, "MOL-ESTBID");
        else
        {
            var errs = new List<SaudiBankExportIssueDto>();
            KsaWageFileRules.ValidateEstablishmentId(estb, errs);
            foreach (var e in errs.Where(e => Catalogue.ContainsKey(e.Code)))
                add(e.Code, WpsFindingSeverity.Error, L("MOL-ESTBID"), true);
            estbWellFormed = errs.Count == 0;
        }

        if (expected is null) add("establishment_not_checked", WpsFindingSeverity.Warning, null, false);
        else if (string.IsNullOrWhiteSpace(expected.MolEstablishmentId)) add("establishment_not_configured", WpsFindingSeverity.Error, null, true);
        else if (estbWellFormed && !WpsEstablishmentAuthority.SameEstablishment(estb!, expected.MolEstablishmentId!))
            add("establishment_mismatch", WpsFindingSeverity.Error, L("MOL-ESTBID"), true,
                SensitiveValueMask.MaskId(estb), SensitiveValueMask.MaskId(expected.MolEstablishmentId));

        // [32A-AMT]
        decimal? declared = null;
        var amt = Get("32A-AMT");
        if (!string.IsNullOrEmpty(amt))
        {
            if (TryAmount(amt, out var d, out _, out _)) declared = d;
            else add("header_total_invalid", WpsFindingSeverity.Error, L("32A-AMT"), true);
        }

        // Dates: YYYYMMDD is UNVERIFIED, so a bad one is a warning.
        DateOnly? valueDate = ParseDate(Get("32A-VAL"), "32A-VAL", L("32A-VAL"), add);
        DateOnly? fileDate = ParseDate(Get("D-DATE"), "D-DATE", L("D-DATE"), add);

        // File-level reject code
        var rej = Get("FILE-REJCDE");
        if (!string.IsNullOrWhiteSpace(rej)) add("file_rejected", WpsFindingSeverity.Error, L("FILE-REJCDE"), true, Safe(rej));

        return new WpsReturnedFileHeader(Get("DEST-ID"), Get("ESTB-ID"), Get("BANK-ACC"), ccy, valueDate, declared, fileRef, estb,
            string.IsNullOrWhiteSpace(rej) ? null : Safe(rej), fileDate);
    }

    private static DateOnly? ParseDate(string? value, string tag, int? line, AddFinding add)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (DateOnly.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        add("date_format_invalid", WpsFindingSeverity.Warning, line, false, tag);
        return null;
    }

    /// <summary>Comma-decimal amount ("1234,56"). Never accepts a point, a thousands separator, a sign or more than two decimals.</summary>
    private static bool TryAmount(string value, out decimal amount, out string? code, out bool wholeNumber)
    {
        amount = 0m; code = null; wholeNumber = false;
        if (value.Contains('.')) { code = "amount_point_decimal"; return false; }
        if (!AmountPattern.IsMatch(value)) { code = "amount_invalid"; return false; }
        var comma = value.IndexOf(',');
        if (comma >= 0 && value.Length - comma - 1 > 2) { code = "amount_precision"; return false; }
        wholeNumber = comma < 0;
        amount = decimal.Parse(value.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        return true;
    }

    private static RowBuilder ReadRow(
        int lineNo, List<(string Tag, string Value)> pairs, int strays, bool hasControl, AddFinding add)
    {
        var rb = new RowBuilder { Line = lineNo };
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var repeated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (tag, value) in pairs)
        {
            if (!values.TryAdd(tag, value) && repeated.Add(tag) && (HeaderTags.Contains(tag) || RowTags.Contains(tag)))
                Block(rb, "duplicate_tag", lineNo, add, tag);
        }
        // A repeated field is never trusted: neither value is read (a tab-smuggled second pair must not become the ID).
        foreach (var tag in repeated) values.Remove(tag);
        if (strays > 0) Block(rb, "stray_value", lineNo, add);
        if (hasControl) Block(rb, "control_character", lineNo, add);

        string? Get(string tag) => values.TryGetValue(tag, out var v) ? v : null;
        decimal? Amount(string tag)
        {
            var v = Get(tag);
            if (string.IsNullOrEmpty(v)) return null;
            if (TryAmount(v, out var d, out var code, out var whole))
            {
                if (whole) add("amount_no_decimals", WpsFindingSeverity.Warning, lineNo, false, tag);
                return d;
            }
            Block(rb, code!, lineNo, add, tag);
            return null;
        }

        rb.Net = Amount("32B-AMT");
        rb.Basic = Amount("MOL-BAS");
        rb.Housing = Amount("MOL-HAL");
        rb.Other = Amount("MOL-OEA");
        rb.Deductions = Amount("MOL-DED");

        // Required fields
        foreach (var tag in new[] { "32B-AMT", "59-ACC", "MOL-ID" })
            if (string.IsNullOrEmpty(Get(tag))) Block(rb, "row_field_missing", lineNo, add, tag);
        var missingBreakdown = BreakdownTags.Where(t => string.IsNullOrEmpty(Get(t))).ToList();
        if (missingBreakdown.Count > 0) Block(rb, "wage_breakdown_missing", lineNo, add, string.Join(", ", missingBreakdown.Select(t => $"[{t}]")));

        if (rb.Net is { } net)
        {
            if (net <= 0m) Block(rb, "net_not_positive", lineNo, add);
            if (rb.Basic is { } b && rb.Housing is { } h && rb.Other is { } o && rb.Deductions is { } d && b + h + o - d != net)
                Block(rb, "net_identity_failed", lineNo, add);
        }

        // [MOL-ID] 10d
        rb.MolId = Get("MOL-ID");
        if (!string.IsNullOrEmpty(rb.MolId))
        {
            if (rb.MolId.Length != 10 || !rb.MolId.All(char.IsAsciiDigit)) Block(rb, "mol_id_invalid", lineNo, add);
            else if (rb.MolId[0] is not ('1' or '2')) add("mol_id_prefix_unexpected", WpsFindingSeverity.Warning, lineNo, false);
        }

        // [59-ACC] 24-char upper-case SA IBAN, mod-97
        rb.Iban = Get("59-ACC");
        if (!string.IsNullOrEmpty(rb.Iban))
        {
            var ok = rb.Iban.Length == 24 && rb.Iban.StartsWith("SA", StringComparison.Ordinal)
                     && rb.Iban.All(c => c is (>= '0' and <= '9') or (>= 'A' and <= 'Z')) && IbanValidator.IsSaudiIban(rb.Iban);
            if (!ok) Block(rb, "iban_invalid", lineNo, add);
        }

        // [59-NAME]
        rb.Name = Get("59-NAME");
        if (string.IsNullOrEmpty(rb.Name)) Block(rb, "name_missing", lineNo, add);
        else
        {
            if (rb.Name.Length > MaxNameLength) Block(rb, "name_too_long", lineNo, add);
            if (rb.Name.Any(IsArabic) && rb.Name.Any(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z'))) Block(rb, "name_mixed_script", lineNo, add);
            if (rb.Name[0] is '=' or '+' or '-' or '@') add("name_formula_prefix", WpsFindingSeverity.Warning, lineNo, false);
            if (rb.Name.Contains(',')) add("name_contains_comma", WpsFindingSeverity.Warning, lineNo, false);
        }

        // Per-line status fields: named only in the research note, so unverified.
        rb.TrnRef = NullIfEmpty(Get("TRN-REF"));
        if (rb.TrnRef is { Length: > 140 }) Block(rb, "trn_ref_too_long", lineNo, add);
        rb.TrnStatus = NullIfEmpty(Get("TRN-STATUS"));
        if (rb.TrnStatus is { Length: > 64 }) Block(rb, "status_too_long", lineNo, add);
        var trnDate = Get("TRN-DATE");
        if (!string.IsNullOrEmpty(trnDate))
        {
            if (DateOnly.TryParseExact(trnDate, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var td)) rb.TrnDate = td;
            else add("date_format_invalid", WpsFindingSeverity.Warning, lineNo, false, "TRN-DATE");
        }
        return rb;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static bool IsArabic(char c) =>
        c is (>= '\u0600' and <= '\u06FF') or (>= '\u0750' and <= '\u077F') or (>= '\u08A0' and <= '\u08FF')
            or (>= '\uFB50' and <= '\uFDFF') or (>= '\uFE70' and <= '\uFEFF');
}
