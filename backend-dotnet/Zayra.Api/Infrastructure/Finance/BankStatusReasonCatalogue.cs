namespace Zayra.Api.Infrastructure.Finance;

/// <summary>A plain English and Arabic text pair.</summary>
public sealed record BankText(string English, string Arabic);

/// <summary>
/// What a bank's reason code means, in plain words. <see cref="Code"/> is the code as read (upper case), or null when
/// the bank gave none or sent something that is not a code. <see cref="IsKnown"/> is false for anything not in the
/// catalogue: its text then says which code it was and to ask the bank, never a guess.
/// </summary>
public sealed record BankReasonDescription(string? Code, string English, string Arabic, bool IsKnown);

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

    private static readonly IReadOnlyDictionary<string, BankText> Codes = new Dictionary<string, BankText>(StringComparer.Ordinal)
    {
        // IncorrectAccountNumber
        ["AC01"] = new("The account number (IBAN) is wrong.", "رقم الحساب (الآيبان) غير صحيح."),
        // ClosedAccountNumber
        ["AC04"] = new("The account is closed.", "الحساب مغلق."),
        // BlockedAccount
        ["AC06"] = new("The account is blocked.", "الحساب موقوف."),
        // InsufficientFunds
        ["AM04"] = new("There was not enough money in the paying account.", "الرصيد في حساب الدفع غير كافٍ."),
        // InconsistentWithEndCustomer
        ["BE01"] = new("The name or ID does not match the account holder.", "الاسم أو رقم الهوية لا يطابق صاحب الحساب."),
        // DuplicatePayment. A duplicate refusal usually means the ORIGINAL payment went through, so it never reads "not paid":
        // the line is held for a person (CTO ruling 4).
        ["DUPL"] = new(DuplicateEnglish, DuplicateArabic),
        // InvalidFileFormat
        ["FF01"] = new("The bank could not read the file format.", "تعذّر على البنك قراءة صيغة الملف."),
    };

    internal const string DuplicateEnglish =
        "The bank refused this as a duplicate. Check whether the original payment reached the employee before paying again.";
    internal const string DuplicateArabic =
        "رفض البنك هذه العملية لأنها مكررة. تحقق مما إذا كانت عملية الدفع الأصلية قد وصلت إلى الموظف قبل الدفع مرة أخرى.";

    public static IReadOnlyCollection<string> KnownCodes => Codes.Keys.ToList();

    /// <summary>True when the code is ISO DUPL (DuplicatePayment), in any letter case.</summary>
    public static bool IsDuplicate(string? code) => Normalise(code) == "DUPL";

    public static bool IsKnown(string? code) => Normalise(code) is { } c && Codes.ContainsKey(c);

    /// <summary>The plain text for a reason code. Never null and never a guess.</summary>
    public static BankReasonDescription Describe(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return new(null, "The bank gave no reason code: ask your bank", "لم يذكر البنك رمز السبب: استفسر من البنك", false);

        var normalised = Normalise(code);
        if (normalised is null)
            // Whatever sat in the code column is not a code (a formula, hidden characters, prose): never echo it back.
            return new(null, "The bank sent a reason code we cannot read: ask your bank",
                "أرسل البنك رمز سبب لا يمكن قراءته: استفسر من البنك", false);

        return Codes.TryGetValue(normalised, out var text)
            ? new(normalised, text.English, text.Arabic, true)
            : new(normalised, $"Bank reason code {normalised}: ask your bank", $"رمز السبب من البنك {normalised}: استفسر من البنك", false);
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
    /// <summary>The bank refused the instruction: the money never left. Never "Returned".</summary>
    public static readonly BankText NotPaidRejectedByBank = new("Not paid: rejected by bank", "لم يُدفع: رفضه البنك");

    /// <summary>ISO ACSC/ACCC: the bank settled it. Not proof the employee received it.</summary>
    public static readonly BankText SettledByBank = new("Settled by bank", "تمت التسوية لدى البنك");

    /// <summary>The money left and came back.</summary>
    public static readonly BankText ReturnedMoneyCameBack = new("Returned: the money came back", "مُرتجع: أعاد البنك المبلغ");

    /// <summary>ISO ACWC: the bank accepted the instruction with a change. Nothing is recorded until a person checks.</summary>
    public static readonly BankText HeldAcceptedWithChange = new(
        "Held: the bank accepted it with a change; a person must check what changed",
        "معلّق: قبل البنك التعليمات مع تعديل، ويجب أن يراجع أحد الموظفين ما الذي تغيّر");

    /// <summary>Reason code DUPL: held, never "not paid" (CTO ruling 4).</summary>
    public static readonly BankText HeldDuplicateRejection =
        new(BankStatusReasonCatalogue.DuplicateEnglish, BankStatusReasonCatalogue.DuplicateArabic);

    /// <summary>A rejection after the salary was marked Paid (CTO ruling B2).</summary>
    public static readonly BankText HeldRejectionAfterPaid = new(
        "Held: the bank reported a rejection for a salary already marked Paid; a person must check whether the money left",
        "معلّق: أبلغ البنك برفض راتب سبق تسجيله مدفوعًا، ويجب أن يتحقق أحد الموظفين مما إذا كان المبلغ قد خرج");

    /// <summary>The bank's answer moved between Returned and Rejected (CTO ruling B2).</summary>
    public static readonly BankText HeldFailedOutcomeChanged = new(
        "Held: the bank changed its answer between returned and rejected; a person must check which is right",
        "معلّق: غيّر البنك ردّه بين مرتجع ومرفوض، ويجب أن يتحقق أحد الموظفين من الرد الصحيح");

    public static IReadOnlyList<BankText> All => new[]
    {
        NotPaidRejectedByBank, SettledByBank, ReturnedMoneyCameBack, HeldAcceptedWithChange, HeldDuplicateRejection,
        HeldRejectionAfterPaid, HeldFailedOutcomeChanged,
    };
}
