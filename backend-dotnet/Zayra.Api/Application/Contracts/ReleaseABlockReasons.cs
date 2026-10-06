namespace Zayra.Api.Application.Contracts;

/// <summary>One block reason as a person reads it. The code is for machines and logs; the UI never shows it.</summary>
/// <param name="OwnerRole">The role that can clear it.</param>
public sealed record BlockReason(
    string Code,
    string TitleEn,
    string TitleAr,
    string WhyEn,
    string WhyAr,
    string FixEn,
    string FixAr,
    string OwnerRole);

/// <summary>
/// The Release A block-reason catalogue (plan §1.3): code → EN/AR title, why, the fix, and who can fix it. Every
/// refusal a Release A endpoint returns carries one of these codes plus its <see cref="BlockReason"/>, so the
/// frontend renders plain sentences and never a raw code. <c>ReleaseAContractTests</c> asserts each code has
/// complete EN and AR text.
/// </summary>
public static class ReleaseABlockReasons
{
    public const string RenewalArt55Threshold = "RENEWAL_ART55_THRESHOLD";
    public const string RenewalNoticeDatePassed = "RENEWAL_NOTICE_DATE_PASSED";
    public const string RenewalChainUnconfirmed = "RENEWAL_CHAIN_UNCONFIRMED";
    public const string RenewalNoCompany = "RENEWAL_NO_COMPANY";
    public const string RenewalDisputeHold = "RENEWAL_DISPUTE_HOLD";
    public const string EntitlementFloorHousing = "ENTITLEMENT_FLOOR_HOUSING";
    public const string EntitlementFloorTransport = "ENTITLEMENT_FLOOR_TRANSPORT";
    public const string EntitlementFloorMedical = "ENTITLEMENT_FLOOR_MEDICAL";
    public const string EntitlementArt40ReadOnly = "ENTITLEMENT_ART40_READONLY";
    public const string EntitlementReductionMidterm = "ENTITLEMENT_REDUCTION_MIDTERM";
    public const string EntitlementReasonRequired = "ENTITLEMENT_REASON_REQUIRED";
    public const string EntitlementBannedCriterion = "ENTITLEMENT_BANNED_CRITERION";
    public const string EntitlementCellMissing = "ENTITLEMENT_CELL_MISSING";
    public const string GradeMissing = "GRADE_MISSING";
    public const string QiwaEvidenceSameUser = "QIWA_EVIDENCE_SAME_USER";
    public const string QiwaResponseOverdue = "QIWA_RESPONSE_OVERDUE";
    public const string ApplyPayrollPeriodProcessed = "APPLY_PAYROLL_PERIOD_PROCESSED";
    public const string LoanInstalmentOver10PctNoConsent = "LOAN_INSTALMENT_OVER_10PCT_NO_CONSENT";
    public const string DeductionsOverHalfWage = "DEDUCTIONS_OVER_HALF_WAGE";

    private const string HrManager = "HR Manager";
    private const string HrDirector = "HR Director";
    private const string PayrollManager = "Payroll Manager";

    public static readonly IReadOnlyDictionary<string, BlockReason> All = new[]
    {
        new BlockReason(RenewalArt55Threshold,
            "Saudi contract limit reached", "بلغ العقد الحد النظامي للتجديد",
            "Under Article 55 a Saudi worker's fixed-term contract becomes indefinite after three renewals or four years, whichever comes first. This term can only be converted to an indefinite contract or not renewed.",
            "وفق المادة 55 يصبح العقد محدد المدة للعامل السعودي غير محدد المدة بعد ثلاث تجديدات أو أربع سنوات أيهما أقل، لذلك لا يمكن إلا تحويله إلى عقد غير محدد المدة أو عدم تجديده.",
            "Choose Convert to indefinite, or serve a non-renewal notice before the notice date.",
            "اختر التحويل إلى عقد غير محدد المدة، أو أرسل إشعار عدم التجديد قبل موعد الإشعار.",
            HrManager),
        new BlockReason(RenewalNoticeDatePassed,
            "Non-renewal notice date has passed", "فات موعد إشعار عدم التجديد",
            "A non-renewal notice had to be served by the notice date. Without it the contract renews on its current terms (Article 74(2)), so non-renewal is no longer available.",
            "كان يجب إرسال إشعار عدم التجديد قبل موعد الإشعار، وبدونه يتجدد العقد بشروطه الحالية (المادة 74 فقرة 2)، لذلك لم يعد عدم التجديد متاحاً.",
            "Renew the contract, or take legal advice before ending it early (Article 77).",
            "جدّد العقد، أو استشر الشؤون القانونية قبل إنهائه مبكراً (المادة 77).",
            HrManager),
        new BlockReason(RenewalChainUnconfirmed,
            "Contract history not confirmed", "لم يتم تأكيد سجل العقود",
            "This term could not be linked to the terms before it, so the number of renewals and the years served are unknown. Nothing is assumed.",
            "تعذّر ربط هذا العقد بالعقود السابقة، لذلك عدد التجديدات وسنوات الخدمة غير معروفة، ولا يُفترض أي شيء.",
            "Confirm the earlier terms, the date the first contract started and the worker's nationality class.",
            "أكّد العقود السابقة وتاريخ بداية أول عقد وفئة جنسية العامل.",
            HrManager),
        new BlockReason(RenewalNoCompany,
            "Contract has no employing company", "العقد غير مرتبط بمنشأة",
            "Every renewal belongs to the company that employs the worker, and this contract does not name one.",
            "كل تجديد يتبع المنشأة التي يعمل لديها العامل، وهذا العقد لا يحدد منشأة.",
            "Set the employing company on the contract.",
            "حدّد المنشأة صاحبة العمل في العقد.",
            HrManager),
        new BlockReason(RenewalDisputeHold,
            "On hold: labour dispute", "معلّق: نزاع عمالي",
            "While a labour dispute is open, benefits cannot be reduced or removed, and non-renewal needs a legal sign-off (Article 235).",
            "أثناء النزاع العمالي لا يجوز تخفيض المزايا أو إلغاؤها، ويحتاج عدم التجديد إلى موافقة قانونية (المادة 235).",
            "Keep the current terms, or obtain the legal sign-off before choosing non-renewal.",
            "حافظ على الشروط الحالية، أو احصل على الموافقة القانونية قبل اختيار عدم التجديد.",
            HrDirector),
        new BlockReason(EntitlementFloorHousing,
            "Housing must be provided", "يجب توفير السكن",
            "Article 61: the employer provides housing, in cash or in kind. It can be changed, but never removed.",
            "وفق المادة 61 يوفّر صاحب العمل السكن نقداً أو عيناً، ويمكن تعديله لكن لا يجوز إلغاؤه.",
            "Set a housing amount or percentage, or mark it as provided in kind.",
            "حدّد مبلغ أو نسبة بدل السكن، أو اختر توفيره عيناً.",
            HrDirector),
        new BlockReason(EntitlementFloorTransport,
            "Transport must be provided", "يجب توفير النقل",
            "Article 61: the employer provides transport, in cash or in kind. It can be changed, but never removed.",
            "وفق المادة 61 يوفّر صاحب العمل النقل نقداً أو عيناً، ويمكن تعديله لكن لا يجوز إلغاؤه.",
            "Set a transport amount or percentage, or mark it as provided in kind.",
            "حدّد مبلغ أو نسبة بدل النقل، أو اختر توفيره عيناً.",
            HrDirector),
        new BlockReason(EntitlementFloorMedical,
            "Medical cover is required", "التأمين الطبي إلزامي",
            "Health insurance covers every employee and their family from the first day, at no less than the basic class, whatever their nationality.",
            "يشمل التأمين الصحي كل موظف وأسرته من اليوم الأول، بفئة لا تقل عن الفئة الأساسية، أياً كانت جنسيته.",
            "Keep medical cover at the basic class or above, with no waiting period and no nationality condition.",
            "أبقِ التأمين الطبي بالفئة الأساسية أو أعلى، دون فترة انتظار ودون شرط جنسية.",
            HrDirector),
        new BlockReason(EntitlementArt40ReadOnly,
            "Paid by the employer by law", "يتحملها صاحب العمل نظاماً",
            "Article 40 costs (residence and work permits, their renewal and related fines) are the employer's. They are not a benefit and cannot be changed or charged back.",
            "رسوم المادة 40 (الإقامة ورخصة العمل وتجديدهما والغرامات المترتبة) يتحملها صاحب العمل، وليست ميزة، ولا يجوز تعديلها أو تحميلها للعامل.",
            "No action needed — this line is shown for information.",
            "لا يلزم أي إجراء، هذا البند للعلم فقط.",
            HrDirector),
        new BlockReason(EntitlementReductionMidterm,
            "Cannot reduce during the contract year", "لا يجوز التخفيض خلال سنة العقد",
            "Agreed terms change only by agreement (Articles 52 and 58–60). A reduction can only be offered at renewal.",
            "لا تتغير الشروط المتفق عليها إلا بالاتفاق (المواد 52 و58 إلى 60)، ولا يمكن عرض التخفيض إلا عند التجديد.",
            "Propose the change in the renewal offer, or raise the value instead.",
            "اقترح التعديل ضمن عرض التجديد، أو ارفع القيمة بدلاً من ذلك.",
            HrManager),
        new BlockReason(EntitlementReasonRequired,
            "Reason and evidence needed", "يلزم ذكر السبب وإرفاق المستند",
            "Removing or lowering a benefit, or not renewing, must record a lawful reason and its evidence (non-discrimination, Article 61(4)).",
            "إلغاء ميزة أو تخفيضها أو عدم التجديد يتطلب تسجيل سبب مشروع ومستند يثبته (عدم التمييز، المادة 61 فقرة 4).",
            "Pick a reason and attach the supporting document.",
            "اختر السبب وأرفق المستند الداعم.",
            HrManager),
        new BlockReason(EntitlementBannedCriterion,
            "That condition is not allowed", "هذا الشرط غير مسموح",
            "A benefit may depend only on months of service, the end of probation and — with a recorded legal basis — nationality. Age, gender, marital status and disability are never conditions.",
            "يجوز ربط الميزة بأشهر الخدمة وانتهاء فترة التجربة والجنسية مع سند نظامي مسجّل فقط، ولا يجوز اشتراط العمر أو الجنس أو الحالة الاجتماعية أو الإعاقة.",
            "Remove the condition. Cover family members through dependants instead.",
            "احذف الشرط، وغطِّ أفراد الأسرة من خلال المعالين بدلاً من ذلك.",
            HrDirector),
        new BlockReason(EntitlementCellMissing,
            "Benefit not set for this grade", "لم تُحدَّد الميزة لهذه الدرجة",
            "This benefit has no value, and no 'Not offered', for the employee's grade, so it cannot be shown or frozen.",
            "لا توجد قيمة لهذه الميزة ولا خيار «غير مقدَّمة» لدرجة الموظف، لذلك لا يمكن عرضها أو تثبيتها.",
            "Set a value, or 'Not offered', for every grade in Benefits by grade.",
            "حدّد قيمة أو «غير مقدَّمة» لكل درجة في المزايا حسب الدرجة.",
            HrDirector),
        new BlockReason(GradeMissing,
            "Employee has no grade", "لا توجد درجة للموظف",
            "Benefits are set by grade, and this employee has none, so their package cannot be worked out.",
            "تُحدَّد المزايا حسب الدرجة، ولا توجد درجة لهذا الموظف، لذلك لا يمكن احتساب باقته.",
            "Assign a grade in the employee's job details.",
            "حدّد الدرجة في بيانات وظيفة الموظف.",
            HrManager),
        new BlockReason(QiwaEvidenceSameUser,
            "A second person must verify", "يجب أن يتحقق شخص آخر",
            "Qiwa evidence is checked by a different user from the one who recorded it.",
            "يتحقق من مستند قوى مستخدم مختلف عن الذي سجّله.",
            "Ask another HR user to verify the evidence.",
            "اطلب من مستخدم آخر في الموارد البشرية التحقق من المستند.",
            HrManager),
        new BlockReason(QiwaResponseOverdue,
            "Qiwa response overdue", "تأخر الرد في قوى",
            "The employee has not approved the contract in Qiwa within the response window. Qiwa cancels requests that are not answered.",
            "لم يوافق الموظف على العقد في قوى خلال مهلة الرد، وتُلغي قوى الطلبات التي لا يُرد عليها.",
            "Remind the employee, or resend the request in Qiwa and record the new date.",
            "ذكّر الموظف، أو أعد إرسال الطلب في قوى وسجّل التاريخ الجديد.",
            HrManager),
        new BlockReason(ApplyPayrollPeriodProcessed,
            "Payroll for that month is already processed", "تمت معالجة رواتب ذلك الشهر",
            "The new terms would start inside a month whose payroll has already moved past draft.",
            "ستبدأ الشروط الجديدة في شهر تجاوزت مسيرات رواتبه مرحلة المسودة.",
            "Start the new terms in the next open month, or reopen that month's payroll first.",
            "ابدأ الشروط الجديدة في الشهر المفتوح التالي، أو أعد فتح مسير ذلك الشهر أولاً.",
            PayrollManager),
        new BlockReason(LoanInstalmentOver10PctNoConsent,
            "Employee's written consent needed", "يلزم موافقة الموظف الكتابية",
            "Article 92: an employer-loan instalment above 10% of the wage needs the employee's written consent.",
            "وفق المادة 92 يحتاج قسط قرض صاحب العمل الذي يتجاوز 10% من الأجر إلى موافقة الموظف الكتابية.",
            "Lower the instalment to 10% of the wage or less, or upload the signed consent.",
            "خفّض القسط إلى 10% من الأجر أو أقل، أو ارفع الموافقة الموقّعة.",
            HrManager),
        new BlockReason(DeductionsOverHalfWage,
            "Deductions above half the wage", "الاستقطاعات تتجاوز نصف الأجر",
            "Article 93: deductions may not exceed half of the wage due, unless a court orders otherwise.",
            "وفق المادة 93 لا يجوز أن تتجاوز الاستقطاعات نصف الأجر المستحق ما لم يصدر حكم قضائي بخلاف ذلك.",
            "Defer or reduce a deduction so the total stays within the limit.",
            "أجّل استقطاعاً أو خفّضه ليبقى المجموع ضمن الحد.",
            PayrollManager),
    }.ToDictionary(r => r.Code, StringComparer.Ordinal);

    /// <summary>The catalogue entry for a code. Throws for an unknown code: an unexplained refusal is a defect.</summary>
    public static BlockReason Get(string code) =>
        All.TryGetValue(code, out var reason)
            ? reason
            : throw new KeyNotFoundException($"'{code}' is not in the Release A block-reason catalogue.");
}
