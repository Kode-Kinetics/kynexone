using Zayra.Api.Models;

namespace Zayra.Api.Application.Onboarding;

public sealed record StarterTask(
    string Title, string TitleAr, string Description, string DescriptionAr, string OwnerRole, int DueOffsetDays,
    string NationalityClass, bool IsStatutory, string? LinkSection = null, bool BlocksActivation = false);

/// <summary>
/// The generic Saudi starter checklist (10-KSA-COMPLIANCE-RULES section 5). It is a template a company COPIES into its own
/// setup and edits; it is never attached on its own. No government integration is implied (CD8): the Qiwa, GOSI, Muqeem and
/// Mudad steps are tasks a person does and ticks. Deadlines the rules mark VERIFY are the defaults below and stay configurable
/// in the copy. A Saudi national gets no Iqama, Muqeem or medical-fitness task (those apply to expatriates only), and no task
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
            "The employment contract must be written and signed before the person starts.", "يجب أن يكون عقد العمل مكتوبًا وموقعًا قبل مباشرة العمل.",
            OnboardingOwnerRoles.Hr, -7, NationalityClasses.Any, true),
        new("Ask the employee to complete their profile (ID or Iqama, passport, photo, IBAN)", "طلب استكمال الموظف لملفه (الهوية أو الإقامة، الجواز، الصورة، الآيبان)",
            "Send the profile-completion request from the employee record; the employee confirms their details in Self-Service.", "أرسل طلب استكمال الملف من سجل الموظف؛ يؤكد الموظف بياناته عبر الخدمة الذاتية.",
            OnboardingOwnerRoles.Hr, -5, NationalityClasses.Any, false, "id"),
        new("Qiwa contract documentation and authentication", "توثيق العقد في منصة قوى",
            "Document the contract on Qiwa. Done by a person; there is no integration. Deadline to be verified for your case (day 0 to day 30).", "وثّق العقد في منصة قوى. تتم بواسطة شخص ولا يوجد ربط آلي. يُتحقق من الموعد لحالتك (من اليوم 0 إلى اليوم 30).",
            OnboardingOwnerRoles.Gov, 7, NationalityClasses.Any, true),
        new("GOSI registration (add the subscriber)", "التسجيل في التأمينات الاجتماعية (إضافة مشترك)",
            "Register the employee with GOSI at joining and no later than the current GOSI deadline (verify, 15 to 30 days).", "سجّل الموظف في التأمينات عند المباشرة وفي موعد لا يتجاوز المهلة الحالية (يُتحقق منها، من 15 إلى 30 يومًا).",
            OnboardingOwnerRoles.Gov, 0, NationalityClasses.Any, true),
        new("CCHI medical insurance enrolment (employee and up to 3 dependants)", "التسجيل في التأمين الطبي (مجلس الضمان الصحي) للموظف وحتى 3 من التابعين",
            "Enrol before the Iqama is issued for expatriates, and at joining for Saudi nationals (verify).", "سجّل قبل إصدار الإقامة للوافدين، وعند المباشرة للسعوديين (يُتحقق).",
            OnboardingOwnerRoles.Hr, 0, NationalityClasses.Any, true),
        new("Medical fitness check (new expatriates)", "الفحص الطبي للياقة (الوافدون الجدد)",
            "Complete the medical fitness check before the Iqama is issued.", "أكمل الفحص الطبي قبل إصدار الإقامة.",
            OnboardingOwnerRoles.Hr, -14, NationalityClasses.Expat, true),
        new("Work visa and Iqama issuance through Muqeem", "إصدار تأشيرة العمل والإقامة عبر مقيم",
            "Issue the Iqama within the allowed period after entry (verify, up to 90 days).", "أصدر الإقامة خلال المدة المسموحة بعد الدخول (يُتحقق، حتى 90 يومًا).",
            OnboardingOwnerRoles.Gov, 30, NationalityClasses.Expat, true, "id"),
        new("Absher and Muqeem linkage; set the Iqama expiry reminder", "ربط أبشر ومقيم وضبط تنبيه انتهاء الإقامة",
            "Once the Iqama is issued, link it and record its expiry date on the employee record.", "بعد إصدار الإقامة، اربطها وسجّل تاريخ انتهائها في سجل الموظف.",
            OnboardingOwnerRoles.Gov, 35, NationalityClasses.Expat, true, "id"),
        new("WPS and Mudad inclusion: salary account and wage file", "الإدراج في حماية الأجور ومدد: حساب الراتب وملف الأجور",
            "The salary account is on the wage file before the first pay cycle (wages are due within 7 days of the due date, verify).", "يكون حساب الراتب ضمن ملف الأجور قبل أول دورة رواتب (تُدفع الأجور خلال 7 أيام من الاستحقاق، يُتحقق).",
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
