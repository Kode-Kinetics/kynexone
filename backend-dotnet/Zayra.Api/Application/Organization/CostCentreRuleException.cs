namespace Zayra.Api.Application.Organization;

/// <summary>
/// A cost-centre master-data rule refusing a write (contract 2.7 M-8..M-10, 2.9). It IS an <see cref="InvalidOperationException"/>, so
/// every caller that already turns that into a row error or a 4xx (imports, the setup wizard) keeps working; the endpoints that know the
/// type answer with <see cref="Status"/> and <see cref="ToPayload"/>.
/// </summary>
public sealed class CostCentreRuleException(string code, int status, string message, string messageAr, IReadOnlyDictionary<string, int>? counts = null)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    public string MessageAr { get; } = messageAr;
    /// <summary>For <c>cost_centre_in_use</c>: department, employee, position, mapping, companyDefault, activeChild.</summary>
    public IReadOnlyDictionary<string, int>? Counts { get; } = counts;

    public object ToPayload() => new { error = Code, message = Message, messageAr = MessageAr, counts = Counts };

    public static CostCentreRuleException CodeInvalid() => new("cost_centre_code_invalid", 422,
        "A cost centre code starts with a letter or digit and then uses only letters, digits, dot, dash or underscore, up to 30 characters.",
        "يبدأ رمز مركز التكلفة بحرف أو رقم ثم يستخدم الحروف والأرقام والنقطة والشرطة والشرطة السفلية فقط، بحد أقصى 30 حرفًا.");

    public static CostCentreRuleException GlSegmentInvalid() => new("cost_centre_gl_segment_invalid", 422,
        "The ERP cost centre value starts with a letter or digit and then uses only letters, digits, dot, dash or underscore, up to 30 characters.",
        "تبدأ قيمة مركز التكلفة في نظام ERP بحرف أو رقم ثم تستخدم الحروف والأرقام والنقطة والشرطة والشرطة السفلية فقط، بحد أقصى 30 حرفًا.");

    public static CostCentreRuleException GlSegmentTaken() => new("cost_centre_gl_segment_taken", 409,
        "Another cost centre of this company already uses that ERP value.",
        "يوجد مركز تكلفة آخر في هذه الشركة يستخدم قيمة ERP هذه.");

    public static CostCentreRuleException FunctionalClassInvalid() => new("cost_centre_functional_class_invalid", 422,
        "The functional class must be one of COGS, SELLING, G_AND_A, R_AND_D, OPERATIONS or CAPITALISABLE.",
        "يجب أن يكون التصنيف الوظيفي أحد: COGS أو SELLING أو G_AND_A أو R_AND_D أو OPERATIONS أو CAPITALISABLE.");

    /// <summary>M-10.</summary>
    public static CostCentreRuleException ReasonRequired() => new("cost_centre_reason_required", 422,
        "Enter a reason for this change.", "أدخل سببًا لهذا التغيير.");

    public static CostCentreRuleException Parent(string code, string message) => new(code, 422, message, ParentArabic(code));

    private static string ParentArabic(string code) => code switch
    {
        "cost_centre_parent_self" => "لا يمكن أن يكون مركز التكلفة أبًا لنفسه.",
        "cost_centre_parent_cycle" => "هذا الأب سيُنشئ حلقة في شجرة مراكز التكلفة.",
        "cost_centre_parent_too_deep" => "لا يمكن أن تزيد شجرة مراكز التكلفة على عشرة مستويات.",
        "cost_centre_other_company" => "يتبع مركز التكلفة شركة أخرى. اختر أحد مراكز تكلفة هذه الشركة.",
        "cost_centre_inactive" => "مركز التكلفة غير نشط ولا يمكن تعيينه.",
        _ => "لم يُعثر على مركز التكلفة في هذا المستأجر.",
    };

    public static string ParentMessage(string code) => code switch
    {
        "cost_centre_parent_self" => "A cost centre cannot be its own parent.",
        "cost_centre_parent_cycle" => "That parent would create a loop in the cost centre tree.",
        "cost_centre_parent_too_deep" => "The cost centre tree cannot be deeper than ten levels.",
        _ => code,
    };

    /// <summary>M-8.</summary>
    public static CostCentreRuleException InUse(string costCentreCode, IReadOnlyDictionary<string, int> counts)
    {
        int n(string k) => counts.GetValueOrDefault(k);
        return new("cost_centre_in_use", 409,
            $"Cost centre {costCentreCode} is used by {n("department")} department(s), {n("employee")} employee(s), {n("position")} position(s) and {n("mapping")} posting rule(s), so it cannot be deleted, deactivated or moved to another company.",
            $"مركز التكلفة {costCentreCode} مستخدم في {n("department")} قسم/أقسام و{n("employee")} موظف/موظفين و{n("position")} منصب/مناصب و{n("mapping")} قاعدة ترحيل، لذا لا يمكن حذفه أو إيقافه أو نقله إلى شركة أخرى.",
            counts);
    }

    /// <summary>M-7 (corrected by AM-9): names the real way out, which is Reopen or Lock. SendBack exists only from PendingFinanceReview.
    /// Discloses nothing about the run.</summary>
    public static CostCentreRuleException LockedIntoApprovedRun() => new("locked_into_approved_run", 409,
        "A payroll run for this company is awaiting finance review or lock. This change would alter what it posts. Reopen the run, or lock it, first.",
        "هناك مسيرة رواتب لهذه الشركة بانتظار المراجعة المالية أو القفل. هذا التغيير سيغيّر ما ستقوم بترحيله. أعد فتح المسيرة أو اقفلها أولًا.");

    /// <summary>M-9.</summary>
    public static CostCentreRuleException CodeLocked(string costCentreCode) => new("cost_centre_code_locked", 409,
        $"The code of cost centre {costCentreCode} cannot change because journal lines already carry it. You can rename it.",
        $"لا يمكن تغيير رمز مركز التكلفة {costCentreCode} لأن قيود دفتر اليومية تحمله بالفعل. يمكنك تغيير الاسم.");
}
