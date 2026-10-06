using Zayra.Api.Application.Contracts;

namespace Zayra.Api.Infrastructure.Entitlements;

// Release A slice R2 owns this file: the ONE place every package / freeze reason code is chosen and described.

/// <summary>Which criterion an <see cref="ReleaseABlockReasons.EntitlementNotEligibleCriteria"/> line is waiting for.</summary>
public static class PackageCriteria
{
    public const string ServiceMonths = "ServiceMonths";
    public const string AfterProbation = "AfterProbation";
    public const string Nationality = "Nationality";
    public static readonly string[] All = [ServiceMonths, AfterProbation, Nationality];
}

/// <summary>A line's reason: a block code and, for "not eligible by criteria", the criterion.</summary>
public sealed record PackageReason(string Code, string? Criterion = null);

/// <summary>
/// Every reason code R2 returns, in one place. Codes already in the R0 catalogue (<see cref="ReleaseABlockReasons"/>) are
/// used as they are. The codes in <see cref="Pending"/> are R2's request to the integration owner: R2 may not edit the
/// catalogue, so until R0 adds them they are described here with the same EN/AR shape, and <see cref="Describe"/> answers
/// for both. Once R0 adds a code, its entry here is deleted and nothing else changes.
/// </summary>
public static class PackageReasons
{
    // ── Catalogue codes (R0) ─────────────────────────────────────────────────────────────────────
    public const string NotOfferedByCompany = ReleaseABlockReasons.EntitlementNotOfferedByCompany;
    public const string NotEligibleCriteria = ReleaseABlockReasons.EntitlementNotEligibleCriteria;
    public const string CellMissing = ReleaseABlockReasons.EntitlementCellMissing;
    public const string GradeMissing = ReleaseABlockReasons.GradeMissing;
    public const string NoCompany = ReleaseABlockReasons.RenewalNoCompany;

    // ── Pending: requested from R0 (PR #192 body lists them with EN/AR) ─────────────────────────
    public const string NotInGrade = "ENTITLEMENT_NOT_IN_GRADE";
    public const string HousingInKind = "ENTITLEMENT_HOUSING_IN_KIND";
    public const string SalaryMissing = "ENTITLEMENT_SALARY_MISSING";
    public const string NationalityUnconfirmed = "ENTITLEMENT_NATIONALITY_UNCONFIRMED";
    public const string LoanPolicyBlocks = "ENTITLEMENT_LOAN_POLICY_BLOCKS";
    public const string ContractNotInForce = "ENTITLEMENT_CONTRACT_NOT_IN_FORCE";
    public const string ContractNotFound = "ENTITLEMENT_CONTRACT_NOT_FOUND";
    public const string RowInTheWay = "ENTITLEMENT_ROW_IN_THE_WAY";
    public const string TermOverlap = "ENTITLEMENT_TERM_OVERLAP";
    public const string ProposalSameUser = "ENTITLEMENT_PROPOSAL_SAME_USER";
    public const string ProposalDocumentRequired = "ENTITLEMENT_PROPOSAL_DOCUMENT_REQUIRED";
    public const string ProposalClosed = "ENTITLEMENT_PROPOSAL_CLOSED";

    private const string HrManager = "HR Manager";
    private const string HrDirector = "HR Director";

    public static readonly IReadOnlyDictionary<string, BlockReason> Pending = new[]
    {
        new BlockReason(NotInGrade,
            "Not included for this grade", "غير مشمولة في هذه الدرجة",
            "The grade table says employees in this grade do not get this benefit.",
            "يحدد جدول الدرجات أن موظفي هذه الدرجة لا يحصلون على هذه الميزة.",
            "If the grade should include it, change it in Benefits by grade.",
            "إذا كان يجب أن تشملها الدرجة، عدّلها في المزايا حسب الدرجة.", HrDirector),
        new BlockReason(HousingInKind,
            "Housing is provided in kind", "السكن مقدَّم عيناً",
            "The housing advance is a multiple of the monthly housing allowance, and this employee's housing is provided in kind, so there is no allowance to advance against.",
            "سلفة السكن مضاعف لبدل السكن الشهري، وسكن هذا الموظف مقدَّم عيناً، فلا يوجد بدل تُحسب السلفة على أساسه.",
            "No action needed, unless housing should be paid in cash — then change the salary record.",
            "لا يلزم إجراء، إلا إذا كان يجب صرف السكن نقداً فعدّل سجل الراتب.", HrManager),
        new BlockReason(SalaryMissing,
            "No salary on file", "لا يوجد راتب مسجّل",
            "No salary record is in force for this date, so the pay lines and any salary-based limit cannot be shown.",
            "لا يوجد سجل راتب ساري في هذا التاريخ، لذلك لا يمكن عرض بنود الأجر أو أي حد مرتبط بالراتب.",
            "Add the employee's salary record.", "أضف سجل راتب الموظف.", HrManager),
        new BlockReason(NationalityUnconfirmed,
            "Nationality class not confirmed", "لم تُؤكَّد فئة الجنسية",
            "This benefit depends on whether the worker is Saudi or non-Saudi, and the contract term does not record it yet. Nothing is assumed.",
            "تعتمد هذه الميزة على كون العامل سعودياً أو غير سعودي، ولم يُسجَّل ذلك في العقد بعد، ولا يُفترض أي شيء.",
            "Confirm the worker's nationality class on the contract.", "أكّد فئة جنسية العامل في العقد.", HrManager),
        new BlockReason(LoanPolicyBlocks,
            "The loan policy does not allow it now", "سياسة القروض لا تسمح بها حالياً",
            "The grade allows this facility, but the company loan policy refuses it today (for example during notice, with an overdue instalment, or at the limit of open loans).",
            "تسمح الدرجة بهذا التسهيل، لكن سياسة القروض في الشركة ترفضه اليوم (مثلاً أثناء فترة الإشعار أو مع قسط متأخر أو عند بلوغ حد القروض المفتوحة).",
            "Open the loan form to see the policy's exact reason.", "افتح نموذج القرض لمعرفة السبب الدقيق في السياسة.", HrManager),
        new BlockReason(ContractNotInForce,
            "The contract is not in force", "العقد غير ساري",
            "A package is fixed only for an active contract term. This term is a draft, awaiting approval, replaced or deleted.",
            "تُثبَّت الباقة لعقد ساري فقط، وهذا العقد مسودة أو بانتظار الاعتماد أو مستبدل أو محذوف.",
            "Activate the contract first.", "فعّل العقد أولاً.", HrManager),
        new BlockReason(ContractNotFound,
            "Contract not found", "العقد غير موجود",
            "The contract term could not be found for this employee.", "تعذّر العثور على العقد لهذا الموظف.",
            "Open the employee's contracts and choose the current term.", "افتح عقود الموظف واختر العقد الحالي.", HrManager),
        new BlockReason(RowInTheWay,
            "A later benefit is already set", "توجد ميزة لاحقة مثبتة",
            "The earlier term already has this benefit fixed from a date after the new term starts. Fixed benefits are never deleted, so they cannot be cut back before they start.",
            "للعقد السابق ميزة مثبتة تبدأ بعد بداية العقد الجديد، والمزايا المثبتة لا تُحذف فلا يمكن تقصيرها قبل بدايتها.",
            "Check the start date of the new term.", "راجع تاريخ بداية العقد الجديد.", HrManager),
        new BlockReason(TermOverlap,
            "Another contract term still covers these dates", "عقد آخر ما زال يغطي هذه التواريخ",
            "This benefit is already fixed under another contract term that overlaps the new one, so it was not fixed again for the new term.",
            "هذه الميزة مثبتة في عقد آخر يتداخل مع العقد الجديد، لذلك لم تُثبَّت مرة أخرى للعقد الجديد.",
            "End or correct the overlapping term, then fix the package again.", "أنهِ العقد المتداخل أو صحّحه ثم ثبّت الباقة مجدداً.", HrManager),
        new BlockReason(ProposalSameUser,
            "Needs a second person", "يحتاج إلى شخص آخر",
            "The person who asked for the proposed packages cannot also confirm them.",
            "لا يجوز لمن طلب الباقات المقترحة أن يؤكدها بنفسه.",
            "Ask another HR user to confirm against the signed contract.", "اطلب من مستخدم آخر في الموارد البشرية التأكيد مقابل العقد الموقّع.", HrDirector),
        new BlockReason(ProposalDocumentRequired,
            "The signed contract is required", "العقد الموقّع مطلوب",
            "A proposed package is confirmed only against the employee's signed contract on file.",
            "تُؤكَّد الباقة المقترحة فقط مقابل العقد الموقّع المحفوظ في ملف الموظف.",
            "Upload the signed contract to the employee's documents, then choose it.", "ارفع العقد الموقّع إلى مستندات الموظف ثم اختره.", HrManager),
        new BlockReason(ProposalClosed,
            "Already decided", "تم البت فيها",
            "This proposed package was already confirmed or rejected, or the term already has its package.",
            "سبق تأكيد هذه الباقة المقترحة أو رفضها، أو أن العقد مثبتة باقته بالفعل.",
            "No action needed.", "لا يلزم أي إجراء.", HrManager),
    }.ToDictionary(r => r.Code);

    /// <summary>The block reason for a code: the R0 catalogue first, then R2's pending entries. Never null for an R2 code.</summary>
    public static BlockReason? Describe(string? code) =>
        code is null ? null
        : ReleaseABlockReasons.All.TryGetValue(code, out var known) ? known
        : Pending.TryGetValue(code, out var pending) ? pending : null;

    /// <summary>Every code R2 can return. Proven by test to have a description, so a raw code never reaches a screen.</summary>
    public static readonly string[] All =
    [
        NotOfferedByCompany, NotEligibleCriteria, CellMissing, GradeMissing, NoCompany,
        ReleaseABlockReasons.EntitlementFloorHousing, ReleaseABlockReasons.EntitlementFloorTransport,
        NotInGrade, HousingInKind, SalaryMissing, NationalityUnconfirmed, LoanPolicyBlocks, ContractNotInForce, ContractNotFound,
        RowInTheWay, TermOverlap, ProposalSameUser, ProposalDocumentRequired, ProposalClosed,
    ];
}
