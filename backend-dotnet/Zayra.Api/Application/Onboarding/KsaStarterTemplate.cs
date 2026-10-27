using Zayra.Api.Models;

namespace Zayra.Api.Application.Onboarding;

public sealed record StarterTask(
    string Title, string TitleAr, string Description, string DescriptionAr, string OwnerRole, int DueOffsetDays,
    string NationalityClass, bool IsStatutory, string? LinkSection = null, bool BlocksActivation = false);

/// <summary>
/// The generic Saudi starter checklist (10-KSA-COMPLIANCE-RULES section 5). It is a template a company COPIES into its own
/// setup and edits; it is never attached on its own. No government integration is implied (CD8): the Qiwa, GOSI, Muqeem and
/// Mudad steps are tasks a person does and ticks. Task TEXT states only what an official source states (HRSD, GOSI, CCHI;
/// 72-CONSULTANT-AUDIT-P2P4 section 2); due-date offsets are planning defaults and stay configurable in the copy. A Saudi national gets no Iqama, Muqeem or medical-fitness task (those apply to expatriates only), and no task
/// blocks activation unless the company turns that on (CD4).
/// </summary>
public static class KsaStarterTemplate
{
    public const string Key = "ksa";
    public const string Name = "Saudi Arabia starter checklist";
    public const string NameAr = "قائمة المهام الابتدائية للمملكة العربية السعودية";

    public static readonly IReadOnlyList<StarterTask> Tasks = new StarterTask[]
    {
        new("Offer and contract signed (Arabic and English)", "توقيع العرض والعقد (عربي وإنجليزي)",
            "Prepare the written employment contract (Arabic and English) and have both parties sign it.", "أعدّ عقد العمل المكتوب (عربي وإنجليزي) ووقّعه الطرفان.",
            OnboardingOwnerRoles.Hr, -7, NationalityClasses.Any, true),
        new("Ask the employee to complete their profile (ID or Iqama, passport, photo, IBAN)", "طلب استكمال الموظف لملفه (الهوية أو الإقامة، الجواز، الصورة، الآيبان)",
            "Send the profile-completion request from the employee record; the employee confirms their details in Self-Service.", "أرسل طلب استكمال الملف من سجل الموظف؛ يؤكد الموظف بياناته عبر الخدمة الذاتية.",
            OnboardingOwnerRoles.Hr, -5, NationalityClasses.Any, false, "id"),
        new("Qiwa contract documentation and authentication", "توثيق العقد في منصة قوى",
            "Document the contract on Qiwa. Done by a person; there is no integration. Confirm the deadline that applies to your case.", "وثّق العقد في منصة قوى. تتم بواسطة شخص ولا يوجد ربط آلي. تأكد من الموعد المطبق على حالتك.",
            OnboardingOwnerRoles.Gov, 7, NationalityClasses.Any, true),
        new("GOSI registration (add the subscriber)", "التسجيل في التأمينات الاجتماعية (إضافة مشترك)",
            "Register the employee with GOSI. The employer notifies GOSI within the first 15 days of the month after the month of joining.", "سجّل الموظف في التأمينات الاجتماعية. يبلّغ صاحب العمل المؤسسة خلال أول 15 يومًا من الشهر التالي لشهر المباشرة.",
            OnboardingOwnerRoles.Gov, 0, NationalityClasses.Any, true),
        new("CCHI medical insurance enrolment (employee and eligible dependants)", "التسجيل في التأمين الطبي (مجلس الضمان الصحي) للموظف ولتابعيه المستحقين",
            "The employer insures the employee and eligible dependants. For expatriates, cover is arranged within 10 working days of entry.", "يؤمّن صاحب العمل الموظف وتابعيه المستحقين. للوافدين يُرتَّب التأمين خلال 10 أيام عمل من الدخول.",
            OnboardingOwnerRoles.Hr, 0, NationalityClasses.Any, true),
        new("Medical fitness check (new expatriates)", "الفحص الطبي للياقة (الوافدون الجدد)",
            "Complete the medical fitness check where it applies to your case. Confirm the timing that applies.", "أكمل الفحص الطبي إن كان مطلوبًا لحالتك. تأكد من التوقيت المطبق.",
            OnboardingOwnerRoles.Hr, -14, NationalityClasses.Expat, true),
        new("Work permit and Iqama issuance through Muqeem", "إصدار رخصة العمل والإقامة عبر مقيم",
            "Issue the work permit within 90 days of entry; the Iqama follows.", "أصدر رخصة العمل خلال 90 يومًا من الدخول؛ وتليها الإقامة.",
            OnboardingOwnerRoles.Gov, 30, NationalityClasses.Expat, true, "id"),
        new("Absher and Muqeem linkage; set the Iqama expiry reminder", "ربط أبشر ومقيم وضبط تنبيه انتهاء الإقامة",
            "Once the Iqama is issued, link it and record its expiry date on the employee record.", "بعد إصدار الإقامة، اربطها وسجّل تاريخ انتهائها في سجل الموظف.",
            OnboardingOwnerRoles.Gov, 35, NationalityClasses.Expat, true, "id"),
        new("WPS and Mudad inclusion: salary account and wage file", "الإدراج في حماية الأجور ومدد: حساب الراتب وملف الأجور",
            "The salary account is on the wage file before the first pay cycle. Upload the wage file to Mudad within the Mudad window (30 days).", "يكون حساب الراتب ضمن ملف الأجور قبل أول دورة رواتب. ارفع ملف الأجور على مُدد ضمن المهلة المحددة (30 يومًا حاليًا).",
            OnboardingOwnerRoles.Payroll, 25, NationalityClasses.Any, true, "bank"),
        new("Company email, laptop and access (IT creates the mailbox)", "البريد الإلكتروني للشركة والحاسوب والصلاحيات (تنشئ تقنية المعلومات صندوق البريد)",
            "IT creates the company mailbox and accounts. KynexOne never creates mailboxes.", "تنشئ تقنية المعلومات صندوق البريد والحسابات. لا تنشئ المنصة صناديق بريد.",
            OnboardingOwnerRoles.It, -1, NationalityClasses.Any, false),
        new("Workspace, badge and equipment", "مكان العمل والبطاقة والمعدات",
            "Prepare the desk or workstation, the access badge and any equipment.", "جهّز مكان العمل وبطاقة الدخول وأي معدات.",
            OnboardingOwnerRoles.Admin, -1, NationalityClasses.Any, false),
        new("Manager 30/60/90-day plan and buddy", "خطة الـ30/60/90 يومًا من المدير وتعيين زميل مرافق",
            "The manager shares the first 90 days plan and names a buddy.", "يشارك المدير خطة أول 90 يومًا ويسمّي زميلًا مرافقًا.",
            OnboardingOwnerRoles.Manager, 0, NationalityClasses.Any, false),
        new("Probation review reminder", "تذكير بمراجعة فترة التجربة",
            "Review before the probation period ends. The default assumes a 90-day probation; change the offset to match your policy.", "راجع قبل انتهاء فترة التجربة. الافتراضي يفترض تجربة 90 يومًا؛ عدّل الإزاحة لتناسب سياستك.",
            OnboardingOwnerRoles.Hr, 76, NationalityClasses.Any, true),
        new("Policies acknowledged (Arabic and English) and data-protection notice", "إقرار السياسات (عربي وإنجليزي) وإشعار حماية البيانات",
            "The employee acknowledges the company policies and the personal-data notice.", "يقرّ الموظف بسياسات الشركة وإشعار حماية البيانات الشخصية.",
            OnboardingOwnerRoles.Hr, 0, NationalityClasses.Any, true),
        new("Salary approved (HR prepares, Finance approves)", "اعتماد الراتب (تعدّه الموارد البشرية وتعتمده المالية)",
            "The pay package is prepared by HR and approved by Finance before the first payroll.", "تعدّ الموارد البشرية حزمة الراتب وتعتمدها المالية قبل أول مسير رواتب.",
            OnboardingOwnerRoles.Finance, -3, NationalityClasses.Any, false),
    };
}
