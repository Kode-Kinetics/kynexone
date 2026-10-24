using System.Text.RegularExpressions;

namespace Zayra.Api.Infrastructure.CompanySetup;

/// <summary>
/// The Arabic of a plan row's <c>reason</c> (<c>PlanItem.ReasonAr</c>), so the Arabic review shows why a row is what it
/// is. Every reason the planner writes is one of the forms below; the planner's collector derives the Arabic from the
/// English when a call site does not pass its own, and <c>CompanySetupUnitTests</c> pins each form, so a new English
/// reason without Arabic fails a test instead of reaching the screen as a blank.
/// </summary>
public static partial class CompanySetupReasons
{
    public const string Kept = "Kept as it is: the wizard adds and edits, it never removes.";

    /// <summary>The changed-field words of "Changes: …" reasons.</summary>
    private static readonly Dictionary<string, string> Fields = new(StringComparer.Ordinal)
    {
        ["legal name (English)"] = "الاسم القانوني (بالإنجليزية)",
        ["legal name (Arabic)"] = "الاسم القانوني (بالعربية)",
        ["trade name"] = "الاسم التجاري",
        ["CR number"] = "رقم السجل التجاري",
        ["MOL establishment number"] = "رقم المنشأة لدى وزارة الموارد البشرية",
        ["GOSI number"] = "رقم التأمينات الاجتماعية",
        ["Unified National Number"] = "الرقم الوطني الموحد",
        ["VAT number"] = "الرقم الضريبي",
        ["Qiwa establishment id"] = "رقم المنشأة في قوى",
        ["email domain"] = "نطاق البريد الإلكتروني",
        ["work-email pattern"] = "نمط البريد الإلكتروني للعمل",
        ["ERP company code"] = "رمز الشركة في نظام تخطيط الموارد",
        ["name (English)"] = "الاسم (بالإنجليزية)",
        ["name (Arabic)"] = "الاسم (بالعربية)",
        ["title (English)"] = "المسمى (بالإنجليزية)",
        ["title (Arabic)"] = "المسمى (بالعربية)",
        ["city"] = "المدينة",
        ["time zone"] = "المنطقة الزمنية",
        ["head office"] = "المقر الرئيسي",
        ["coordinates"] = "الإحداثيات",
        ["geofence radius"] = "نطاق السياج الجغرافي",
        ["level"] = "المستوى",
        ["salary band"] = "نطاق الراتب",
        ["parent department"] = "القسم الأعلى",
        ["branch"] = "الفرع",
        ["cost centre"] = "مركز التكلفة",
        ["department"] = "القسم",
        ["grade"] = "الدرجة",
        ["prefix"] = "البادئة",
        ["format"] = "التنسيق",
        ["padding"] = "عدد الأرقام",
        ["next number"] = "الرقم التالي",
        ["weekend"] = "العطلة الأسبوعية",
    };

    private static readonly Dictionary<string, string> Fixed = new(StringComparer.Ordinal)
    {
        [Kept] = "يبقى كما هو: المعالج يضيف ويعدّل ولا يحذف أبداً.",
        ["Already configured for this workspace."] = "مُعدّة مسبقاً لمساحة العمل هذه.",
        ["No settings exist for this country yet; the country pack defaults are written once and never overwritten."] =
            "لا توجد إعدادات لهذه الدولة بعد؛ تُكتب القيم الافتراضية للدولة مرة واحدة ولا يُستبدل بها ما هو موجود.",
        ["This company's own weekend, kept as it is."] = "العطلة الأسبوعية الخاصة بهذه الشركة، تبقى كما هي.",
        ["Same as the workspace setting; no override is written."] = "مطابقة لإعداد مساحة العمل؛ لا يُكتب إعداد خاص.",
        ["The workspace working week applies."] = "ينطبق أسبوع العمل الخاص بمساحة العمل.",
        ["Invited as this company's super admin (Admin, this company only)."] = "مدعو كمسؤول أعلى لهذه الشركة (مسؤول، لهذه الشركة فقط).",
        ["Already a group super admin, so they manage this company already."] = "مسؤول أعلى للمجموعة، لذا يدير هذه الشركة بالفعل.",
        ["Already a super admin of this company."] = "مسؤول أعلى لهذه الشركة بالفعل.",
        ["Gets access to this company with their Admin role; their other access is unchanged."] =
            "يحصل على صلاحية الوصول إلى هذه الشركة بدور المسؤول؛ تبقى صلاحياته الأخرى كما هي.",
    };

    [GeneratedRegex(@"^Changes: (?<list>.+)\.$")]
    private static partial Regex ChangesPattern();
    [GeneratedRegex(@"^This company already has its (?<year>\d{4}) holiday calendar; it is kept as it is\.$")]
    private static partial Regex CalendarKeptPattern();
    [GeneratedRegex(@"^Statutory default for (?<c>[A-Z]{2}), created for this company\.$")]
    private static partial Regex LeaveCreatePattern();
    [GeneratedRegex(@"^This company already has its (?<c>[A-Z]{2}) default; it is kept as it is\.$")]
    private static partial Regex LeaveReusePattern();
    [GeneratedRegex(@"^Overrides the workspace weekend \((?<w>[^)]*)\) for this company only\.$")]
    private static partial Regex WeekendOverridePattern();

    /// <summary>The Arabic of a planner reason; null when the text is not one the planner writes (a conflict passes its own).</summary>
    public static string? Arabic(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        if (Fixed.TryGetValue(reason, out var fixedAr)) return fixedAr;
        if (ChangesPattern().Match(reason) is { Success: true } changes)
        {
            var parts = new List<string>();
            foreach (var raw in changes.Groups["list"].Value.Split(", ", StringSplitOptions.RemoveEmptyEntries))
            {
                if (raw.StartsWith("from ", StringComparison.Ordinal)) parts.Add($"كانت {raw[5..]}");
                else if (Fields.TryGetValue(raw, out var word)) parts.Add(word);
                else return null;
            }
            return $"التغييرات: {string.Join("، ", parts)}.";
        }
        if (CalendarKeptPattern().Match(reason) is { Success: true } cal)
            return $"لدى هذه الشركة تقويم العطل الرسمية لعام {cal.Groups["year"].Value}؛ يبقى كما هو.";
        if (LeaveCreatePattern().Match(reason) is { Success: true } lc)
            return $"إعداد نظامي افتراضي للدولة {lc.Groups["c"].Value}، يُنشأ لهذه الشركة.";
        if (LeaveReusePattern().Match(reason) is { Success: true } lr)
            return $"لدى هذه الشركة الإعداد الافتراضي للدولة {lr.Groups["c"].Value} بالفعل؛ يبقى كما هو.";
        if (WeekendOverridePattern().Match(reason) is { Success: true } wo)
            return $"يستبدل العطلة الأسبوعية لمساحة العمل ({wo.Groups["w"].Value}) لهذه الشركة فقط.";
        return null;
    }
}
