using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Documents.Letters;

/// <summary>
/// The wording a new tenant starts with, one template per letter type, bilingual.
///
/// <para>These are defaults, not hard-coding: every row is written to
/// <c>hr_letter_templates</c> with <c>IsSystemDefault = true</c> and is editable through the
/// templates API. That distinction is the whole point of B6 — the previous four letters had
/// their prose compiled into C#, so a tenant who wanted "was employed with" to read "served
/// with" needed a release.</para>
///
/// <para><b>On the Arabic.</b> These are the standard GCC formulations. Note that
/// <c>{{designation}}</c> and <c>{{department}}</c> render the same value in both languages:
/// the employee record has <c>ArabicName</c> but no Arabic designation or department, so the
/// Arabic paragraph carries the English job title. That is honest and it is what most GCC
/// systems do; giving the Arabic body a token that resolves to English is better than
/// inventing a translation. If a tenant needs Arabic titles they edit the template.</para>
/// </summary>
public static class HrLetterTemplateDefaults
{
    /// <summary>
    /// Merge tokens the renderer can supply. Exposed so the templates UI can show the author a
    /// palette instead of making them guess, and so a test can assert the defaults only use
    /// tokens that actually resolve.
    /// </summary>
    public static readonly IReadOnlyList<string> KnownTokens =
    [
        "employee_name", "employee_name_ar", "employee_code", "designation", "department",
        "nationality", "national_id", "passport_number",
        "joining_date", "leaving_date", "joining_date_ar", "leaving_date_ar", "issue_date_ar",
        "service_duration", "basic_salary", "allowances", "gross_salary", "net_salary",
        "currency", "currency_ar", "bank_name", "bank_iban",
        "company_name", "company_name_ar", "company_registration_number",
        "issue_date", "reference_number", "purpose", "addressee",
        "issuer_name", "issuer_title",
    ];

    public static IReadOnlyList<HrLetterTemplate> Build() =>
    [
        new()
        {
            LetterType = HrLetterTypes.SalaryCertificate,
            NameEn = "Salary Certificate",
            NameAr = "شهادة راتب",
            TitleEn = "SALARY CERTIFICATE",
            TitleAr = "شهادة راتب",
            Language = HrLetterLanguages.Bilingual,
            IsSystemDefault = true,
            BodyEn = """
                     This is to certify that {{employee_name}} (Employee Code {{employee_code}}, ID/Iqama {{national_id}}) is employed with {{company_name}} as {{designation}} in the {{department}} department, and has been in continuous service since {{joining_date}}.

                     The current monthly remuneration is {{gross_salary}} {{currency}}, comprising a basic salary of {{basic_salary}} {{currency}} and allowances of {{allowances}} {{currency}}.

                     This certificate is issued at the employee's request for the purpose of {{purpose}} and is addressed to {{addressee}}. It carries no financial obligation or liability on the part of {{company_name}}.
                     """,
            BodyAr = """
                     نشهد نحن {{company_name_ar}} بأن السيد/السيدة {{employee_name_ar}} (الرقم الوظيفي {{employee_code}}، رقم الهوية/الإقامة {{national_id}}) يعمل لدينا بوظيفة {{designation}} في إدارة {{department}}، وعلى رأس العمل بشكل متصل منذ تاريخ {{joining_date_ar}}.

                     ويبلغ إجمالي الراتب الشهري {{gross_salary}} {{currency_ar}}، ويشمل راتباً أساسياً قدره {{basic_salary}} {{currency_ar}} وبدلات قدرها {{allowances}} {{currency_ar}}.

                     وقد أُعطيت له هذه الشهادة بناءً على طلبه لغرض {{purpose}} وموجهة إلى {{addressee}}، دون أدنى مسؤولية مالية على {{company_name_ar}}.
                     """,
            ClosingEn = "Issued on {{issue_date}} under reference {{reference_number}}.",
            ClosingAr = "صدرت بتاريخ {{issue_date_ar}} تحت الرقم المرجعي {{reference_number}}.",
        },
        new()
        {
            LetterType = HrLetterTypes.SalaryTransferLetter,
            NameEn = "Salary Transfer Letter",
            NameAr = "خطاب تحويل راتب",
            TitleEn = "SALARY TRANSFER LETTER",
            TitleAr = "خطاب تحويل راتب",
            Language = HrLetterLanguages.Bilingual,
            IsSystemDefault = true,
            BodyEn = """
                     To: {{addressee}}

                     We confirm that {{employee_name}} (Employee Code {{employee_code}}, ID/Iqama {{national_id}}) is employed with {{company_name}} as {{designation}}, and has been in continuous service since {{joining_date}}.

                     The current net monthly salary is {{net_salary}} {{currency}}. We confirm that this salary is transferred monthly to account IBAN {{bank_iban}} held with {{bank_name}}, and we undertake to notify you should the salary transfer arrangement change.

                     This letter is issued at the employee's request for the purpose of {{purpose}}.
                     """,
            BodyAr = """
                     السادة/ {{addressee}} المحترمين

                     نفيدكم بأن السيد/السيدة {{employee_name_ar}} (الرقم الوظيفي {{employee_code}}، رقم الهوية/الإقامة {{national_id}}) يعمل لدى {{company_name_ar}} بوظيفة {{designation}}، وعلى رأس العمل بشكل متصل منذ تاريخ {{joining_date_ar}}.

                     ويبلغ صافي راتبه الشهري {{net_salary}} {{currency_ar}}، ويتم تحويله شهرياً إلى الحساب رقم الآيبان {{bank_iban}} لدى {{bank_name}}. ونتعهد بإشعاركم في حال تغيّر ترتيب تحويل الراتب.

                     وقد صدر هذا الخطاب بناءً على طلب الموظف لغرض {{purpose}}.
                     """,
            ClosingEn = "Issued on {{issue_date}} under reference {{reference_number}}.",
            ClosingAr = "صدر بتاريخ {{issue_date_ar}} تحت الرقم المرجعي {{reference_number}}.",
        },
        new()
        {
            LetterType = HrLetterTypes.EmploymentVerification,
            NameEn = "Employment Verification Letter",
            NameAr = "خطاب تعريف بالعمل",
            TitleEn = "EMPLOYMENT VERIFICATION LETTER",
            TitleAr = "خطاب تعريف بالعمل",
            Language = HrLetterLanguages.Bilingual,
            IsSystemDefault = true,
            BodyEn = """
                     To: {{addressee}}

                     This is to verify that {{employee_name}} (Employee Code {{employee_code}}, nationality {{nationality}}, ID/Iqama {{national_id}}) is currently employed with {{company_name}} as {{designation}} in the {{department}} department.

                     The employee joined on {{joining_date}} and remains in active service as at the date of this letter, a total service period of {{service_duration}}.

                     This letter is issued at the employee's request for the purpose of {{purpose}} and confirms employment status only. It states no salary figure and creates no obligation on {{company_name}}.
                     """,
            BodyAr = """
                     السادة/ {{addressee}} المحترمين

                     نفيد بأن السيد/السيدة {{employee_name_ar}} (الرقم الوظيفي {{employee_code}}، الجنسية {{nationality}}، رقم الهوية/الإقامة {{national_id}}) يعمل حالياً لدى {{company_name_ar}} بوظيفة {{designation}} في إدارة {{department}}.

                     وقد التحق بالعمل بتاريخ {{joining_date_ar}} وما زال على رأس العمل حتى تاريخه، بمدة خدمة إجمالية قدرها {{service_duration}}.

                     وقد صدر هذا الخطاب بناءً على طلبه لغرض {{purpose}}، ويؤكد حالة التوظيف فقط دون ذكر أي مبالغ مالية ودون أدنى مسؤولية على {{company_name_ar}}.
                     """,
            ClosingEn = "Issued on {{issue_date}} under reference {{reference_number}}.",
            ClosingAr = "صدر بتاريخ {{issue_date_ar}} تحت الرقم المرجعي {{reference_number}}.",
        },
        new()
        {
            LetterType = HrLetterTypes.AppointmentLetter,
            NameEn = "Appointment Letter",
            NameAr = "خطاب تعيين",
            TitleEn = "LETTER OF APPOINTMENT",
            TitleAr = "خطاب تعيين",
            Language = HrLetterLanguages.Bilingual,
            IsSystemDefault = true,
            BodyEn = """
                     Dear {{employee_name}},

                     We are pleased to confirm your appointment with {{company_name}} as {{designation}} in the {{department}} department, effective {{joining_date}}. Your employee code is {{employee_code}}.

                     Your monthly remuneration is {{gross_salary}} {{currency}}, comprising a basic salary of {{basic_salary}} {{currency}} and allowances of {{allowances}} {{currency}}, payable in accordance with the company's payroll calendar.

                     You are required to comply with all company policies and procedures, and with the labour law of the jurisdiction in which you are employed. We welcome you to the team and wish you a successful career with us.
                     """,
            BodyAr = """
                     عزيزي/عزيزتي {{employee_name_ar}},

                     يسرنا تأكيد تعيينك لدى {{company_name_ar}} بوظيفة {{designation}} في إدارة {{department}}، اعتباراً من تاريخ {{joining_date_ar}}. رقمك الوظيفي هو {{employee_code}}.

                     ويبلغ إجمالي راتبك الشهري {{gross_salary}} {{currency_ar}}، ويشمل راتباً أساسياً قدره {{basic_salary}} {{currency_ar}} وبدلات قدرها {{allowances}} {{currency_ar}}، تُصرف وفقاً لجدول الرواتب المعتمد لدى الشركة.

                     ويتوجب عليك الالتزام بكافة سياسات وإجراءات الشركة وبأحكام نظام العمل المعمول به. نرحب بك ضمن فريق العمل ونتمنى لك التوفيق.
                     """,
            ClosingEn = "Issued on {{issue_date}} under reference {{reference_number}}.",
            ClosingAr = "صدر بتاريخ {{issue_date_ar}} تحت الرقم المرجعي {{reference_number}}.",
        },
        new()
        {
            LetterType = HrLetterTypes.ExperienceCertificate,
            NameEn = "Experience Certificate",
            NameAr = "شهادة خبرة",
            TitleEn = "EXPERIENCE CERTIFICATE",
            TitleAr = "شهادة خبرة",
            Language = HrLetterLanguages.Bilingual,
            IsSystemDefault = true,
            BodyEn = """
                     To Whom It May Concern

                     This is to certify that {{employee_name}} (Employee Code {{employee_code}}) was employed with {{company_name}} as {{designation}} in the {{department}} department from {{joining_date}} to {{leaving_date}}, a total service period of {{service_duration}}.

                     During this period the employee discharged the duties of the role professionally and met the expectations placed upon them. We wish {{employee_name}} every success in their future endeavours.
                     """,
            BodyAr = """
                     إلى من يهمه الأمر

                     نشهد نحن {{company_name_ar}} بأن السيد/السيدة {{employee_name_ar}} (الرقم الوظيفي {{employee_code}}) قد عمل لدينا بوظيفة {{designation}} في إدارة {{department}} خلال الفترة من {{joining_date_ar}} إلى {{leaving_date_ar}}، بمدة خدمة إجمالية قدرها {{service_duration}}.

                     وقد أدى خلال هذه الفترة مهام وظيفته بمهنية والتزام. ونتمنى له دوام التوفيق والنجاح في مستقبله المهني.
                     """,
            ClosingEn = "Issued on {{issue_date}} under reference {{reference_number}}.",
            ClosingAr = "صدرت بتاريخ {{issue_date_ar}} تحت الرقم المرجعي {{reference_number}}.",
        },
    ];
}
