using System.Globalization;
using System.Text;

namespace Zayra.Api.Infrastructure.Documents.Letters;

/// <summary>
/// The wording of the default Saudi offer letter, in English and Arabic (launch spec item 7). The letter is an OFFER, not the employment
/// contract. The clauses that depend on the hire (the GOSI and residency sentences on whether the candidate is Saudi, the notice periods on
/// the company's own rule, the compensation lines on what the offer actually pays) are merge values, so the letter never shows a sentence
/// that contradicts the offer. Final Arabic text, Art. 53 probation mechanics and Art. 75 notice defaults are for a lawyer to confirm.
/// </summary>
public static class OfferLetterClauses
{
    /// <summary>The clause tokens this class fills; every one is also a built-in merge field (<see cref="OfferLetterDesign.Tokens"/>).</summary>
    public static readonly string[] Tokens =
        ["work_location", "contract_term_clause", "notice_period_employer", "notice_period_employee", "gosi_clause", "health_residency_clause", "compensation_lines"];

    public static bool IsSaudi(string? nationality)
    {
        var n = (nationality ?? string.Empty).Trim().ToUpperInvariant();
        return n.Length > 0 && (n.Contains("SAUDI") || n.Contains("سعود") || n is "SA" or "SAU" or "KSA");
    }

    /// <param name="noticeDays">The company's own notice period in days, or null when none is set (the letter then refers to the Labor Law instead of inventing a duration).</param>
    /// <param name="contractTerm">A stated fixed term (for example "12 months"), or null when the contract's term is left to the contract.</param>
    public static Dictionary<string, string> Values(bool arabic, bool saudi, int? noticeDays, string? workLocation, string? contractTerm)
    {
        var location = string.IsNullOrWhiteSpace(workLocation) ? (arabic ? "كما هو مبين في العقد" : "as stated in the employment contract") : workLocation.Trim();
        string notice = noticeDays is { } d
            ? (arabic ? $"({d.ToString(CultureInfo.InvariantCulture)}) يومًا" : $"{d.ToString(CultureInfo.InvariantCulture)} days")
            : (arabic ? "المدة المقررة في نظام العمل" : "the period set by the Labor Law");
        string contract = string.IsNullOrWhiteSpace(contractTerm)
            ? (arabic
                ? "يكون العقد محدد المدة أو غير محدد المدة وفق ما يتفق عليه الطرفان ويُنص عليه في عقد العمل، ويجوز تجديد العقد محدد المدة باتفاق الطرفين كتابةً."
                : "The contract will be fixed-term or indefinite as agreed and stated in the employment contract. A fixed-term contract is renewable by written agreement of both parties.")
            : (arabic
                ? $"عقد محدد المدة مدته {contractTerm.Trim()} تبدأ من تاريخ المباشرة، ويجوز تجديده باتفاق الطرفين كتابةً."
                : $"This is a fixed-term contract of {contractTerm.Trim()} starting on the start date, renewable by written agreement of both parties.");
        string gosi = (arabic, saudi) switch
        {
            (true, true) => "يُسجَّل الموظف لدى المؤسسة العامة للتأمينات الاجتماعية، وتُخصم حصته من الاشتراكات من أجره وتتحمل المنشأة حصتها وفق نظام التأمينات الاجتماعية.",
            (true, false) => "تتحمل المنشأة اشتراك الأخطار المهنية وفق نظام التأمينات الاجتماعية، ولا تُخصم أي اشتراكات من أجر الموظف.",
            (false, true) => "You will be registered with the General Organization for Social Insurance. Your share of the contributions is deducted from your salary and the company pays its share, as required by the Social Insurance Law.",
            _ => "The company pays the occupational hazards contribution under the Social Insurance Law. No contribution is deducted from your salary.",
        };
        string health = saudi ? string.Empty : arabic
            ? "التأمين الصحي والإقامة: توفر المنشأة التأمين الصحي وفق نظام الضمان الصحي التعاوني، وتتحمل رسوم الإقامة ورخصة العمل والرسوم المتصلة بها وفق ما يقرره نظام العمل."
            : "Health insurance and residency: the company provides health insurance in line with the Cooperative Health Insurance Law and bears the residence permit, work permit and related fees as required by the Labor Law.";
        return new()
        {
            ["work_location"] = location, ["contract_term_clause"] = contract, ["notice_period_employer"] = notice, ["notice_period_employee"] = notice,
            ["gosi_clause"] = gosi, ["health_residency_clause"] = health,
        };
    }

    /// <summary>One line per pay item the offer actually has: basic, housing and transport always, other allowances only when above zero, then the total.</summary>
    public static string CompensationLines(bool arabic, string currency, decimal basic, decimal housing, decimal transport, decimal other, decimal gross)
    {
        string Money(decimal v) => $"{currency} {v.ToString("N2", CultureInfo.InvariantCulture)}";
        var lines = new StringBuilder();
        void Line(string en, string ar, decimal v) => lines.Append(arabic ? ar : en).Append(arabic ? ": " : ": ").Append(Money(v)).Append('\n');
        Line("Basic salary", "الأجر الأساسي الشهري", basic);
        Line("Housing allowance", "بدل السكن", housing);
        Line("Transport allowance", "بدل النقل", transport);
        if (other > 0m) Line("Other allowances", "بدلات أخرى", other);
        lines.Append(arabic ? "إجمالي الأجر الشهري: " : "Total monthly salary: ").Append(Money(gross));
        return lines.ToString();
    }

    public static IReadOnlyDictionary<string, string> SampleValues(bool arabic) => Values(arabic, saudi: true, noticeDays: 60, workLocation: "Riyadh", contractTerm: null)
        .Concat(new Dictionary<string, string> { ["compensation_lines"] = CompensationLines(arabic, "SAR", 12000m, 3000m, 1000m, 0m, 16000m) })
        .ToDictionary(p => p.Key, p => p.Value);

    /// <param name="dynamicCompensation">True for a real offer: the pay table is the offer's own frozen components (a schema-2 compensation block), so every component the offer has is printed.
    /// False for a preview or sample, where the table is the plain basic, housing, transport and other-allowance lines.</param>
    public static OfferDesign KsaDefault(bool arabic, bool dynamicCompensation = false)
    {
        var design = new OfferDesign
        {
            SchemaVersion = 1, Direction = arabic ? "rtl" : "ltr",
            FontFamily = arabic ? "Noto Sans Arabic" : "Lato", HeadingFontFamily = arabic ? "Noto Sans Arabic" : "Lato",
            Footer = arabic ? "سري • {{company_name}}" : "Confidential • {{company_name}}",
            Signatory = arabic ? "عن {{company_name}}" : "For {{company_name}}", SignatoryTitle = arabic ? "الموارد البشرية" : "Human Resources",
            CandidateAcceptance = arabic
                ? "قبول الموظف {{candidate_name}}: ____________________    التاريخ: ______________"
                : "Accepted by {{candidate_name}}: ____________________    Date: ______________",
            Blocks = [],
        };
        var align = arabic ? "right" : "left";
        OfferDesignBlock Heading(string text) => new() { Kind = "heading", Text = text, FontSize = 14, Align = align, Spacing = 4 };
        OfferDesignBlock Text(string text) => new() { Kind = "text", Text = text, Align = align };
        if (arabic)
        {
            design.Blocks.AddRange([
                new() { Kind = "heading", Text = "عرض عمل", Align = align },
                Text("{{issue_date}}. المرجع: {{offer_reference}}.\n\nالسيد/ة {{candidate_name}}، السلام عليكم ورحمة الله وبركاته.\n\nيسر {{company_name}} أن تعرض عليكم العمل لديها وفق الشروط الآتية. هذا العرض لا يُعد عقد العمل، ويُوقَّع العقد ويُوثَّق وفق نظام العمل السعودي بعد قبولكم العرض."),
                Heading("المسمى الوظيفي ومكان العمل"),
                Text("المسمى الوظيفي: {{job_title}}. الإدارة/القسم: {{department}}. مكان العمل: {{work_location}}."),
                Heading("تاريخ المباشرة"),
                Text("تبدأ مباشرتكم للعمل بتاريخ {{start_date}}."),
                Heading("نوع العقد ومدته"),
                Text("{{contract_term_clause}}"),
                Heading("فترة التجربة"),
                Text("تخضعون لفترة تجربة مدتها (90) يومًا من تاريخ المباشرة، ويجوز تمديدها باتفاق خطي بينكم وبين المنشأة، على ألا تتجاوز فترة التجربة في مجموعها (180) يومًا في جميع الأحوال. لا تُحتسب ضمن فترة التجربة أيام عيدي الفطر والأضحى وأيام الإجازة المرضية. ويجوز لأي من الطرفين إنهاء العقد خلال فترة التجربة وفق أحكام نظام العمل."),
                Heading("ساعات العمل والراحة"),
                Text("ساعات العمل العادية ثماني (8) ساعات يوميًا أو ثمان وأربعون (48) ساعة أسبوعيًا. وتُخفَّض ساعات العمل في شهر رمضان للموظفين المسلمين إلى ست (6) ساعات يوميًا أو ست وثلاثين (36) ساعة أسبوعيًا. ويوم الجمعة هو يوم الراحة الأسبوعية المدفوعة الأجر ما لم يُحدَّد يوم آخر وفق نظام العمل. وما زاد على هذه الساعات يُعد عملًا إضافيًا يُدفع عنه أجر الساعة مضافًا إليه (50%) من الأجر الأساسي للساعة."),
                Heading("الأجر"),
                Text("{{compensation_lines}}\n\nيُصرف الأجر شهريًا بالتحويل البنكي عبر نظام حماية الأجور."),
                Heading("الإجازة السنوية"),
                Text("يستحق الموظف إجازة سنوية بأجر كامل مدتها (21) يومًا في السنة، تزداد إلى (30) يومًا بعد إتمام خمس سنوات متصلة في خدمة المنشأة، إضافةً إلى الإجازات الرسمية والإجازات الأخرى المقررة في نظام العمل."),
                Heading("التأمينات الاجتماعية"),
                Text("{{gosi_clause}}"),
                Text("{{health_residency_clause}}"),
                Heading("مكافأة نهاية الخدمة"),
                Text("يستحق الموظف عند انتهاء العقد مكافأة نهاية الخدمة وفق المادتين (84) و(85) من نظام العمل."),
                Heading("مدة الإشعار"),
                Text("في حال كان العقد غير محدد المدة، تكون مدة الإشعار {{notice_period_employer}} على المنشأة و{{notice_period_employee}} على الموظف، وفق نظام العمل."),
                Heading("النظام الواجب التطبيق"),
                Text("يخضع هذا العرض وعقد العمل لنظام العمل في المملكة العربية السعودية ولائحته التنفيذية. وتُحال أي منازعة أولًا إلى التسوية الودية لدى الجهة المختصة بالعمل، ثم إلى الجهات المختصة في المملكة."),
                Heading("شروط العرض والقبول"),
                Text("هذا العرض مشروط بسلامة وثائق الهوية أو الإقامة، واجتياز الفحص الطبي، والتحقق من مؤهلاتكم ومراجعكم، وتوثيق العقد في منصة قوى. للقبول، يرجى التوقيع أدناه وإعادة هذا الخطاب قبل {{response_deadline}}."),
                new() { Kind = "signature", Align = align },
            ]);
        }
        else
        {
            design.Blocks.AddRange([
                new() { Kind = "heading", Text = "Offer of employment" },
                Text("{{issue_date}}. Reference: {{offer_reference}}.\n\nDear {{candidate_name}},\n\n{{company_name}} is pleased to offer you employment on the terms below. This offer is not the employment contract; the contract will be signed and documented in line with the Saudi Labor Law once you accept."),
                Heading("Job title and place of work"),
                Text("Job title: {{job_title}}. Department: {{department}}. Place of work: {{work_location}}."),
                Heading("Start date"),
                Text("Your employment starts on {{start_date}}."),
                Heading("Contract type and term"),
                Text("{{contract_term_clause}}"),
                Heading("Probation"),
                Text("You will serve a probation period of 90 days from the start date. The period may be extended by written agreement between you and the company, and in all cases the total probation period will not exceed 180 days. Days of Eid al-Fitr, Eid al-Adha and sick leave are not counted within the probation period. During probation either party may end the contract as provided by the Labor Law."),
                Heading("Working hours and rest"),
                Text("Normal working hours are eight hours a day or 48 hours a week. During Ramadan, working hours for Muslim employees are reduced to six hours a day or 36 hours a week. Friday is the paid weekly rest day unless another day is assigned in line with the Labor Law. Work beyond these hours is overtime and is paid as the hourly wage plus 50% of the basic hourly wage."),
                Heading("Remuneration"),
                Text("{{compensation_lines}}\n\nSalary is paid monthly by bank transfer through the Wage Protection System."),
                Heading("Annual leave"),
                Text("You are entitled to paid annual leave of 21 days a year, increasing to 30 days after five consecutive years of service with the company, in addition to official holidays and the other leaves provided by the Labor Law."),
                Heading("Social insurance (GOSI)"),
                Text("{{gosi_clause}}"),
                Text("{{health_residency_clause}}"),
                Heading("End-of-service award"),
                Text("On the end of the contract you are entitled to an end-of-service award calculated under Articles 84 and 85 of the Labor Law."),
                Heading("Notice period"),
                Text("Where the contract is of indefinite term, the notice period is {{notice_period_employer}} for the company and {{notice_period_employee}} for you, in line with the Labor Law."),
                Heading("Governing law and disputes"),
                Text("This offer and the employment contract are governed by the Labor Law of the Kingdom of Saudi Arabia and its implementing regulations. Any dispute is first referred to amicable settlement through the competent labor office and then to the competent authorities in the Kingdom."),
                Heading("Conditions and acceptance"),
                Text("This offer is subject to valid identity or residence documents, a satisfactory medical examination, verification of your qualifications and references, and documentation of the contract on the Qiwa platform. To accept, sign below and return this letter by {{response_deadline}}."),
                new() { Kind = "signature" },
            ]);
        }
        if (dynamicCompensation)
        {
            // The pay table becomes the offer's own components; the sentence about how salary is paid stays as its own paragraph.
            var at = design.Blocks.FindIndex(b => b.Kind == "text" && b.Text.StartsWith("{{compensation_lines}}", StringComparison.Ordinal));
            design.SchemaVersion = 2;
            design.Blocks[at] = new OfferDesignBlock { Kind = "compensation" };
            design.Blocks.Insert(at + 1, Text(arabic ? "يُصرف الأجر شهريًا بالتحويل البنكي عبر نظام حماية الأجور." : "Salary is paid monthly by bank transfer through the Wage Protection System."));
        }
        return design;
    }
}
