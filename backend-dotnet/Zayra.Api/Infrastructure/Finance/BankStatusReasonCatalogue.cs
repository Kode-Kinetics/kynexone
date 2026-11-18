namespace Zayra.Api.Infrastructure.Finance;

/// <summary>A plain English and Arabic text pair.</summary>
public sealed record BankText(string English, string Arabic);

/// <summary>
/// What a bank's reason code means, in plain words. <see cref="Code"/> is the code as read (upper case), or null when
/// the bank gave none or sent something that is not a code. <see cref="IsKnown"/> is false for anything not in the
/// catalogue: its text then says which code it was and to ask the bank, never a guess.
/// <para><see cref="English"/> and <see cref="Arabic"/> are the cause. <see cref="NextStepEnglish"/> and
/// <see cref="NextStepArabic"/> say what the officer does about a returned or rejected line with that reason; they are
/// separate so a screen can place them, and empty for a code the catalogue does not know (its cause already says to
/// contact the bank).</para>
/// </summary>
public sealed record BankReasonDescription(
    string? Code, string English, string Arabic, bool IsKnown, string NextStepEnglish = "", string NextStepArabic = "");

/// <summary>The cause and the next step of one catalogued reason code, in both languages.</summary>
internal sealed record BankReasonText(string English, string Arabic, string NextStepEnglish, string NextStepArabic);

/// <summary>
/// The shared ISO 20022 StatusReason catalogue (ExternalStatusReason1Code), CTO gate 8.4.7. Every reader that receives
/// a bank reason code (the generic parsers, the returned-file reader, the ANB mapper) describes it through
/// <see cref="Describe"/>, so the same code always reads the same way in English and Arabic.
///
/// <para>Only codes whose ISO meaning is unambiguous are listed. Add a code with its row in
/// BankStatusReasonCatalogueTests; never map an unknown code to the nearest known one.</para>
/// </summary>
public static class BankStatusReasonCatalogue
{
    // ISO 20022 and proprietary bank codes are at most 35 characters, letters, digits, '_' and '-'.
    private const int MaxCodeLength = 35;

    private static readonly IReadOnlyDictionary<string, BankReasonText> Codes = new Dictionary<string, BankReasonText>(StringComparer.Ordinal)
    {
        // IncorrectAccountNumber
        ["AC01"] = new("The account number (IBAN) is wrong.", "رقم الحساب (الآيبان) غير صحيح.",
            "Ask HR to correct the employee's IBAN, then pay this salary in a new payment batch linked to the original batch.",
            "اطلب من الموارد البشرية تصحيح آيبان الموظف ثم ادفع هذا الراتب في دفعة دفع جديدة مرتبطة بالدفعة الأصلية."),
        // ClosedAccountNumber
        ["AC04"] = new("The account is closed.", "الحساب مغلق.",
            "Check whether it is the employee's account or your paying account. If it is the employee's, ask HR for a new IBAN, then pay it in a new payment batch linked to the original batch.",
            "تحقق هل هو حساب الموظف أم حساب الدفع لديكم. فإن كان حساب الموظف فاطلب من الموارد البشرية آيبانًا جديدًا ثم ادفعه في دفعة دفع جديدة مرتبطة بالدفعة الأصلية."),
        // BlockedAccount
        ["AC06"] = new("The account is blocked.", "الحساب موقوف.",
            "Check whether it is the employee's account or your paying account, and ask the bank what is needed to release it. Once it is resolved, pay in a new payment batch linked to the original batch.",
            "تحقق هل هو حساب الموظف أم حساب الدفع لديكم، واسأل البنك عمّا يلزم لرفع الإيقاف، وبعد المعالجة ادفع في دفعة دفع جديدة مرتبطة بالدفعة الأصلية."),
        // InsufficientFunds
        ["AM04"] = new("There was not enough money in the paying account.", "الرصيد في حساب الدفع غير كافٍ.",
            "Check the bank statement, add funds, then pay the salaries in a new payment batch linked to the original batch. Do not create the new batch without checking.",
            "راجع كشف حساب البنك وأودع المبلغ المطلوب، ثم ادفع الرواتب في دفعة دفع جديدة مرتبطة بالدفعة الأصلية. لا تنشئ الدفعة الجديدة قبل التحقق."),
        // Duplication: the same voice as DUPL, never "not paid" or "owed" (final gate P1-3).
        ["AM05"] = new(DuplicateEnglish, DuplicateArabic,
            "Do not create a new payment batch until you have checked.", "لا تنشئ دفعة دفع جديدة قبل التحقق من ذلك."),
        // InconsistentWithEndCustomer
        ["BE01"] = new("The name or ID does not match the account holder.", "الاسم أو رقم الهوية لا يطابق صاحب الحساب.",
            "Ask HR to check the employee's name, ID and IBAN against the bank's record.",
            "اطلب من الموارد البشرية مطابقة اسم الموظف ورقم هويته وآيبانه مع سجل البنك."),
        // DuplicatePayment. A duplicate refusal usually means the ORIGINAL payment went through, so it never reads "not paid":
        // the line is held for a person (CTO ruling 4). Its text never says the salary is owed.
        ["DUPL"] = new(DuplicateEnglish, DuplicateArabic,
            "Do not create a new payment batch until you have checked.", "لا تنشئ دفعة دفع جديدة قبل التحقق من ذلك."),
        // InvalidFileFormat
        ["FF01"] = new("The bank could not read the file format.", "تعذّر على البنك قراءة صيغة الملف.",
            "Salaries in this file may not have been processed: check the bank statement and ask the bank which format it expects.",
            "قد لا تكون رواتب هذا الملف قد عولجت: راجع كشف حساب البنك واسأل البنك عن الصيغة التي يطلبها."),
    };

    internal const string DuplicateEnglish =
        "The bank refused this as a duplicate. Check whether the original payment reached the employee before creating a new payment batch.";
    internal const string DuplicateArabic =
        "رفض البنك هذه العملية لأنها مكررة. تحقق مما إذا كانت عملية الدفع الأصلية قد وصلت إلى الموظف قبل إنشاء دفعة دفع جديدة.";

    public static IReadOnlyCollection<string> KnownCodes => Codes.Keys.ToList();

    /// <summary>True when the code is ISO DUPL (DuplicatePayment) or AM05 (Duplication), in any letter case.</summary>
    public static bool IsDuplicate(string? code) => Normalise(code) is "DUPL" or "AM05";

    /// <summary>
    /// True when the bank refused the line as a duplicate: the code is DUPL or AM05, or the reason text says so ("duplicat..." in
    /// any letter case, or the Arabic words for a repeat). Hidden format and control characters are removed from the text first, so
    /// a zero-width character cannot hide the word. Only ever used to HOLD a line for a person ("when in doubt, hold").
    /// </summary>
    public static bool IsDuplicate(string? code, string? reasonText)
    {
        if (IsDuplicate(code)) return true;
        if (string.IsNullOrWhiteSpace(reasonText)) return false;
        var visible = new string(reasonText.Where(c => char.GetUnicodeCategory(c) is not
            (System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.Control)).ToArray());
        return visible.Contains("DUPLICAT", StringComparison.OrdinalIgnoreCase)
               || visible.Contains("مكرر", StringComparison.Ordinal) || visible.Contains("تكرار", StringComparison.Ordinal);
    }

    public static bool IsKnown(string? code) => Normalise(code) is { } c && Codes.ContainsKey(c);

    /// <summary>The plain text for a reason code. Never null and never a guess.</summary>
    public static BankReasonDescription Describe(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return new(null, "The bank gave no reason code: ask your bank", "لم يذكر البنك رمز السبب: يرجى مراجعة البنك", false);

        var normalised = Normalise(code);
        if (normalised is null)
            // Whatever sat in the code column is not a code (a formula, hidden characters, prose): never echo it back.
            return new(null, "The bank sent a reason code we cannot read: ask your bank",
                "أرسل البنك رمز سبب لا يمكن قراءته: يرجى مراجعة البنك", false);

        return Codes.TryGetValue(normalised, out var text)
            ? new(normalised, text.English, text.Arabic, true, text.NextStepEnglish, text.NextStepArabic)
            : new(normalised, $"Bank reason code {normalised}: ask your bank", $"رمز السبب من البنك {normalised}: يرجى مراجعة البنك", false);
    }

    /// <summary>Upper-cased code, or null when it is not one: ASCII letters, digits, '_' and '-' only, at most 35, and
    /// starting with a letter or digit (a leading '-' reads as a formula in a spreadsheet export).</summary>
    private static string? Normalise(string? code)
    {
        var trimmed = (code ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxCodeLength) return null;
        if (!char.IsAsciiLetterOrDigit(trimmed[0])) return null;
        foreach (var ch in trimmed)
            if (!(ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')) return null;
        return trimmed.ToUpperInvariant();
    }
}

/// <summary>Plain EN and AR labels for what a bank line means (CTO gate 8.4.2 and 8.4.4).</summary>
public static class BankOutcomeLabels
{
    /// <summary>The bank refused the instruction; the bank statement shows whether any money moved. Never "Returned".</summary>
    public static readonly BankText NotPaidRejectedByBank = new("Not paid: rejected by bank", "لم يُدفع: رفضه البنك");

    /// <summary>ISO ACSC/ACCC: the bank settled it. Not proof the employee received it.</summary>
    public static readonly BankText SettledByBank = new("Settled by bank", "تمت التسوية لدى البنك");

    /// <summary>The money left and came back.</summary>
    public static readonly BankText ReturnedMoneyCameBack = new("Returned: the money came back", "مُرتجع: أعاد البنك المبلغ");

    /// <summary>ISO ACWC: the bank accepted the instruction with a change. Nothing is recorded until a person checks.</summary>
    public static readonly BankText HeldAcceptedWithChange = new(
        "Held: the bank accepted it with a change; a payroll or finance user must check what changed",
        "معلّق: قبل البنك التعليمات مع تعديل، ويجب أن يراجع مستخدم الرواتب أو المالية ما الذي تغيّر");

    /// <summary>The bank accepted the file (a word such as SUCCESS) but nothing proves the employee's account was credited.</summary>
    public static readonly BankText AcceptedCreditNotConfirmed = new(
        "Bank accepted the file; credit not confirmed", "قبل البنك الملف؛ لم يُؤكَّد قيد المبلغ في حساب الموظف");

    /// <summary>Reason code DUPL: held, never "not paid" (CTO ruling 4).</summary>
    public static readonly BankText HeldDuplicateRejection =
        new(BankStatusReasonCatalogue.DuplicateEnglish, BankStatusReasonCatalogue.DuplicateArabic);

    /// <summary>A rejection after the salary was marked Paid (CTO ruling B2).</summary>
    public static readonly BankText HeldRejectionAfterPaid = new(
        "Held: the bank reported a rejection for a salary already marked Paid; a payroll or finance user must check the bank statement. If the money did not reach the employee, the salary is still owed",
        "معلّق: أبلغ البنك برفض راتب سبق تسجيله مدفوعًا، ويجب أن يراجع مستخدم الرواتب أو المالية كشف حساب البنك. فإن لم يصل المبلغ إلى الموظف فالراتب ما زال مستحقًا");

    /// <summary>The bank's answer moved between Returned and Rejected (CTO ruling B2).</summary>
    public static readonly BankText HeldFailedOutcomeChanged = new(
        "Held: the bank changed its answer between returned and rejected; a payroll or finance user must check which is right",
        "معلّق: غيّر البنك ردّه بين مرتجع ومرفوض، ويجب أن يراجع مستخدم الرواتب أو المالية الرد الصحيح");

    /// <summary>The bank's amount differs from the payroll amount (CTO-GATE 8.4 ruling 11).</summary>
    public static readonly BankText HeldAmountMismatch = new(
        "Held: the bank's amount differs from the payroll amount; a payroll or finance user must check the difference",
        "معلّق: المبلغ لدى البنك يختلف عن مبلغ مسير الرواتب، ويجب أن يراجع مستخدم الرواتب أو المالية الفرق");

    public static IReadOnlyList<BankText> All => new[]
    {
        NotPaidRejectedByBank, SettledByBank, ReturnedMoneyCameBack, HeldAcceptedWithChange, AcceptedCreditNotConfirmed,
        HeldDuplicateRejection, HeldRejectionAfterPaid, HeldFailedOutcomeChanged, HeldAmountMismatch,
    };
}
