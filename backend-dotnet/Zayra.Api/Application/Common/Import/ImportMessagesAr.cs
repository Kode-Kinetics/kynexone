using System.Text.RegularExpressions;
using Zayra.Api.Application.Organization;

namespace Zayra.Api.Application.Common.Import;

/// <summary>
/// Arabic text for the import row messages the cost-centre work introduced. Import results carry English; the Arabic arrives as a PARALLEL array
/// (<c>ErrorsAr</c> / <c>WarningsAr</c>, same length and order, an entry is null when that message has no Arabic text, the whole array is null when none has),
/// so a client that does not read it is unaffected and a message added later without a translation simply falls back to its English.
/// An optional "Row N: " or "Employee CODE: " prefix (the employee import's list form) is translated with the message.
/// </summary>
public static class ImportMessagesAr
{
    private const RegexOptions Opts = RegexOptions.CultureInvariant | RegexOptions.Singleline;

    private static readonly Regex RowPrefix = new(@"^Row (\d+): (.*)$", Opts);
    private static readonly Regex EmployeePrefix = new(@"^Employee ([^:]+): (.*)$", Opts);

    private static readonly (Regex Pattern, Func<Match, string> Arabic)[] Rules =
    [
        (new(@"^Clears the cost centre link of this existing department\.$", Opts), _ => "سيؤدي هذا إلى مسح ربط مركز التكلفة لهذا القسم الحالي."),
        (new(@"^Clears the parent department link of this existing department\.$", Opts), _ => "سيؤدي هذا إلى مسح ربط القسم الأب لهذا القسم الحالي."),
        (new(@"^CostCenterCode '(.*)' not found$", Opts), m => $"رمز مركز التكلفة '{m.Groups[1].Value}' غير موجود"),
        (new(@"^Cost center reference '(.*)' not found$", Opts), m => $"مرجع مركز التكلفة '{m.Groups[1].Value}' غير موجود"),
        (new(@"^CostCenterCode '(.*)' belongs to (.*), not '(.*)'$", Opts), m => $"رمز مركز التكلفة '{m.Groups[1].Value}' يتبع {m.Groups[2].Value} وليس '{m.Groups[3].Value}'"),
        (new(@"^DepartmentCode '(.*)' not found — will be ignored$", Opts), m => $"رمز القسم '{m.Groups[1].Value}' غير موجود وسيتم تجاهله"),
        (new(@"^CostCenterCode '(.*)' not found for company '(.*)' — imported without a cost center\.$", Opts),
            m => $"رمز مركز التكلفة '{m.Groups[1].Value}' غير موجود في الشركة '{m.Groups[2].Value}' — تم الاستيراد بدون مركز تكلفة."),
        (new(@"^CostCenterCode '(.*)' supplied but no company could be resolved — imported without a cost center\.$", Opts),
            m => $"تم تقديم رمز مركز التكلفة '{m.Groups[1].Value}' لكن تعذر تحديد الشركة — تم الاستيراد بدون مركز تكلفة."),
        (new(@"^CostCenterCode '(.*)' matches more than one cost center \((.*)\) — imported without a cost center\.$", Opts),
            m => $"رمز مركز التكلفة '{m.Groups[1].Value}' يطابق أكثر من مركز تكلفة ({m.Groups[2].Value}) — تم الاستيراد بدون مركز تكلفة."),
        (new(@"^Cost center code '(.*)': " + Regex.Escape(Zayra.Api.Infrastructure.Payroll.CostCentreCode.Rule) + "$", Opts),
            m => $"رمز مركز التكلفة '{m.Groups[1].Value}': {CostCentreRuleException.CodeInvalid().MessageAr}"),
        (new(@"^This row moves the position to '(.*)', but its saved (.*) belong to another company\. Give the new company's (.*) in this row\.$", Opts),
            m => $"ينقل هذا الصف المنصب إلى '{m.Groups[1].Value}'، لكن {Labels(m.Groups[2].Value)} المحفوظ يتبع شركة أخرى. أدخل {Labels(m.Groups[3].Value)} الخاص بالشركة الجديدة في هذا الصف."),
    ];

    private static string Labels(string english) => string.Join("، ", english.Split(", ").Select(l => l switch
    {
        "cost center" => "مركز التكلفة",
        "branch" => "الفرع",
        "department" => "القسم",
        "designation" => "المسمى الوظيفي",
        "grade" => "الدرجة",
        _ => l,
    }));

    /// <summary>The Arabic for one message, or null when there is none.</summary>
    public static string? Translate(string english)
    {
        if (string.IsNullOrEmpty(english)) return null;
        if (RowPrefix.Match(english) is { Success: true } row)
            return Translate(row.Groups[2].Value) is { } inner ? $"الصف {row.Groups[1].Value}: {inner}" : null;
        if (EmployeePrefix.Match(english) is { Success: true } emp)
            return Translate(emp.Groups[2].Value) is { } inner ? $"الموظف {emp.Groups[1].Value}: {inner}" : null;
        foreach (var (pattern, arabic) in Rules)
            if (pattern.Match(english) is { Success: true } m) return arabic(m);
        return null;
    }

    /// <summary>One entry per message, in order; null when no message has Arabic (so the array can be omitted).</summary>
    public static IReadOnlyList<string?>? Parallel(IEnumerable<string> messages)
    {
        var list = messages.Select(Translate).ToList();
        return list.Any(x => x is not null) ? list : null;
    }

    /// <summary>The row with its parallel Arabic arrays filled in.</summary>
    public static ImportRowResult WithArabic(this ImportRowResult row) =>
        row with { ErrorsAr = Parallel(row.Errors), WarningsAr = Parallel(row.Warnings) };
}
