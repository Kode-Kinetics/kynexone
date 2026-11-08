namespace Zayra.Api.Infrastructure.Documents.Letters;

/// <summary>
/// The Arabic wording of the payslip: fixed statutory and structural line names, Gregorian month names (Latin
/// digits, per the screen standard; Hijri is not required) and the fixed sentences.
///
/// <para>Pay-item names reach the payslip as English strings (the stored component name). A tenant-defined item
/// carries its Arabic name in <see cref="PayslipLineItem.NameAr"/> (filled from <c>PayComponent.NameAr</c> by
/// <c>PayslipLabelBook</c>); the statutory lines have FIXED Arabic labels here, worded as the product already words
/// them elsewhere (<c>frontend/src/i18n/releaseA/deductions.ts</c>, <c>PayslipTemplateRegistry</c>). An item with no
/// Arabic name prints its English name - never blank.</para>
/// </summary>
public static class PayslipArabic
{
    private static readonly string[] Months =
    [
        "يناير", "فبراير", "مارس", "أبريل", "مايو", "يونيو",
        "يوليو", "أغسطس", "سبتمبر", "أكتوبر", "نوفمبر", "ديسمبر",
    ];

    public static string Month(int month) => Months[month - 1];

    /// <summary>"أكتوبر 2026": Arabic month, Latin digits.</summary>
    public static string MonthYear(int year, int month) => $"{Month(month)} {year}";

    /// <summary>"07 نوفمبر 2026، 09:30 UTC" as one right-to-left run (see the tests for the exact characters).</summary>
    public static string Stamp(DateTime utc) =>
        $"{utc:dd} {Month(utc.Month)} {utc:yyyy HH:mm} UTC";

    public static string Disclaimer => "هذا كشف راتب صادر من النظام وصالح دون توقيع.";

    public static string Footer => "تم إنشاؤه بواسطة KynexOne HRMS";

    private const string Employee = "(حصة الموظف)";
    private const string Employer = "(حصة صاحب العمل)";

    /// <summary>Whole names: statutory lines first, then the structural lines the payslip builds itself.</summary>
    private static readonly Dictionary<string, string> Exact = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GOSI Annuities (Employee)"] = $"المعاشات - التأمينات الاجتماعية {Employee}",
        ["GOSI Annuities (Employer)"] = $"المعاشات - التأمينات الاجتماعية {Employer}",
        ["SANED (Employee)"] = $"ساند {Employee}",
        ["SANED (Employer)"] = $"ساند {Employer}",
        ["Occupational Hazard (Employee)"] = $"الأخطار المهنية {Employee}",
        ["Occupational Hazard (Employer)"] = $"الأخطار المهنية {Employer}",
        ["GPSSA (Employee)"] = $"الهيئة العامة للمعاشات والتأمينات الاجتماعية {Employee}",
        ["GPSSA (Employer)"] = $"الهيئة العامة للمعاشات والتأمينات الاجتماعية {Employer}",
        ["GRSIA (Employee)"] = $"الهيئة العامة للتقاعد والتأمينات الاجتماعية {Employee}",
        ["GRSIA (Employer)"] = $"الهيئة العامة للتقاعد والتأمينات الاجتماعية {Employer}",
        ["Basic Salary"] = "الراتب الأساسي",
        ["Housing Allowance"] = "بدل السكن",
        ["Transport Allowance"] = "بدل النقل",
        ["Other Allowances"] = "بدلات أخرى",
        ["Other Deductions"] = "استقطاعات أخرى",
        ["Arrears"] = "متأخرات",
        ["Net Pay"] = "صافي الراتب",
    };

    /// <summary>
    /// The Arabic name of a pay item. Order: the item's own Arabic name, the fixed catalogue, the catalogue name
    /// followed by a note ("Basic Salary (prorated 15/31 days)" keeps the note), then the English name unchanged.
    /// </summary>
    public static string ItemName(string name, string? nameAr)
    {
        if (!string.IsNullOrWhiteSpace(nameAr)) return nameAr.Trim();
        if (string.IsNullOrWhiteSpace(name)) return name;
        var trimmed = name.Trim();
        if (Exact.TryGetValue(trimmed, out var fixedAr)) return fixedAr;

        foreach (var (en, ar) in Exact)
        {
            if (trimmed.StartsWith(en + " (", StringComparison.OrdinalIgnoreCase))
                return ar + trimmed[en.Length..];
        }
        const string arrearsPrefix = "Arrears — ";
        if (trimmed.StartsWith(arrearsPrefix, StringComparison.OrdinalIgnoreCase))
            return "متأخرات — " + trimmed[arrearsPrefix.Length..];
        return name;
    }
}
