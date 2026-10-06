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

    // ── Added to the R0 catalogue for R2 (round 2, 349a56e1) ─────────────────────────────────────
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

    // ── Pending: requested from R0 in review round 2 (PR #192 body lists them with EN/AR) ───────
    public const string RowNeverTookEffect = "ENTITLEMENT_ROW_NEVER_TOOK_EFFECT";
    public const string TermRunningNeedsProposal = "ENTITLEMENT_TERM_RUNNING_NEEDS_PROPOSAL";
    public const string ProposalOpen = "ENTITLEMENT_PROPOSAL_OPEN";
    public const string ProposalRequesterUnknown = "ENTITLEMENT_PROPOSAL_REQUESTER_UNKNOWN";
    // The stable message prefixes R0's triggers raise (SQLSTATE 23514), each mapped to its own code by FromDatabase.
    public const string OutsideTerm = "ENTITLEMENT_OUTSIDE_TERM";
    public const string CompanyMismatch = "ENTITLEMENT_COMPANY_MISMATCH";
    public const string BasisNotOwnSalary = "ENTITLEMENT_BASIS_NOT_OWN_SALARY";
    public const string CarriedDiffers = "ENTITLEMENT_CARRIED_DIFFERS";
    public const string CarriedOverlaps = "ENTITLEMENT_CARRIED_OVERLAPS";
    public const string CloseOnly = "ENTITLEMENT_CLOSE_ONLY";

    /// <summary>Codes R2 uses that the R0 catalogue does not have yet. Empty: R0 (#189, 349a56e1) added all twelve.</summary>
    public static readonly IReadOnlyDictionary<string, BlockReason> Pending = new[]
    {
        new BlockReason(RowNeverTookEffect,
            "A fixed benefit has not started yet", "توجد ميزة مثبتة لم تبدأ بعد",
            "This term has a fixed benefit that starts on or after the day of this change, so it never took effect. Fixed benefits are never removed, so the change cannot be made yet.",
            "لهذا العقد ميزة مثبتة تبدأ في يوم هذا التغيير أو بعده، فلم تُطبَّق بعد. والمزايا المثبتة لا تُحذف، لذلك لا يمكن إجراء التغيير الآن.",
            "Make the change from the date shown, or ask the system owner to remove the unused benefit.",
            "أجرِ التغيير من التاريخ الظاهر، أو اطلب من مالك النظام حذف الميزة غير المستخدمة.", HrDirector),
        new BlockReason(TermRunningNeedsProposal,
            "Needs a second person's check", "يحتاج إلى تحقق شخص آخر",
            "This contract term has already started, so its benefits are taken from the grade table only as a proposal that another HR user checks against the signed contract.",
            "بدأ هذا العقد بالفعل، لذلك تؤخذ مزاياه من جدول الدرجات كمقترح فقط يراجعه مستخدم آخر في الموارد البشرية مقابل العقد الموقّع.",
            "Propose the package, then ask another HR user to confirm it.", "اقترح الباقة ثم اطلب من مستخدم آخر في الموارد البشرية تأكيدها.", HrManager),
        new BlockReason(ProposalOpen,
            "A proposal is waiting", "يوجد مقترح بانتظار القرار",
            "A proposed package for this term is waiting for a second HR user, so it cannot also be fixed directly.",
            "توجد باقة مقترحة لهذا العقد بانتظار مستخدم آخر في الموارد البشرية، فلا يمكن تثبيتها مباشرة أيضاً.",
            "Confirm or reject the proposal first.", "أكّد المقترح أو ارفضه أولاً.", HrManager),
        new BlockReason(ProposalRequesterUnknown,
            "Who asked for it is unknown", "مقدّم الطلب غير معروف",
            "This proposal does not record who asked for it, so nobody can be shown to be a different person.",
            "لا يسجّل هذا المقترح من طلبه، لذلك لا يمكن التحقق من أن المؤكِّد شخص مختلف.",
            "Reject it and propose the package again.", "ارفضه واقترح الباقة مجدداً.", HrDirector),
        new BlockReason(OutsideTerm,
            "Outside the contract term", "خارج مدة العقد",
            "A benefit's dates would fall outside its contract term.", "ستقع تواريخ الميزة خارج مدة العقد.",
            "Check the contract's start and end dates.", "راجع تاريخي بداية العقد ونهايته.", HrManager),
        new BlockReason(CompanyMismatch,
            "Different company", "منشأة مختلفة",
            "The benefit is not for the company named on its contract.", "الميزة ليست للمنشأة المذكورة في عقدها.",
            "Check the employing company on the contract.", "راجع المنشأة صاحبة العمل في العقد.", HrManager),
        new BlockReason(BasisNotOwnSalary,
            "Salary record of someone else", "سجل راتب لموظف آخر",
            "A percentage benefit cites a salary record that is not this employee's.", "ميزة بنسبة مئوية تستند إلى سجل راتب ليس لهذا الموظف.",
            "Fix the employee's salary record.", "صحّح سجل راتب الموظف.", HrManager),
        new BlockReason(CarriedDiffers,
            "Carried benefit differs", "الميزة المنقولة مختلفة",
            "A benefit carried into a new term must equal the one it carries, and it does not.", "يجب أن تساوي الميزة المنقولة إلى عقد جديد الميزة الأصلية، وهي لا تساويها.",
            "Fix the package for the new term again.", "ثبّت باقة العقد الجديد مجدداً.", HrManager),
        new BlockReason(CarriedOverlaps,
            "Carried benefit overlaps", "الميزة المنقولة متداخلة",
            "A carried benefit must start after the one it carries ends.", "يجب أن تبدأ الميزة المنقولة بعد انتهاء الميزة الأصلية.",
            "Check the start date of the new term.", "راجع تاريخ بداية العقد الجديد.", HrManager),
        new BlockReason(CloseOnly,
            "Fixed benefits are never changed", "المزايا المثبتة لا تُعدَّل",
            "A fixed benefit can only be ended earlier, never changed, extended or removed.", "يمكن إنهاء الميزة المثبتة مبكراً فقط، ولا يجوز تعديلها أو تمديدها أو حذفها.",
            "End it and fix a new value from the next day.", "أنهِها وثبّت قيمة جديدة من اليوم التالي.", HrManager),
    }.ToDictionary(r => r.Code);

    private const string HrManager = "HR Manager";
    private const string HrDirector = "HR Director";

    /// <summary>
    /// The reason code for a database refusal: SQLSTATE 23P01 (the no-overlap EXCLUDE) is a term overlap; 23514 carries the
    /// trigger's stable <c>ENTITLEMENT_*</c> message prefix (R0 migration), mapped to the code of the same name. NULL when the
    /// exception is not an entitlement rule.
    /// </summary>
    public static string? FromDatabase(Exception exception)
    {
        if (exception.InnerException is not Npgsql.PostgresException pg) return null;
        if (pg.SqlState == Npgsql.PostgresErrorCodes.ExclusionViolation) return TermOverlap;
        if (pg.SqlState != Npgsql.PostgresErrorCodes.CheckViolation) return null;
        var match = System.Text.RegularExpressions.Regex.Match(pg.MessageText ?? string.Empty, "^(ENTITLEMENT_[A-Z_]+):");
        return match.Success && Describe(match.Groups[1].Value) is not null ? match.Groups[1].Value
            : pg.ConstraintName?.StartsWith("ck_employee_entitlements", StringComparison.Ordinal) == true ? ContractNotInForce : null;
    }

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
        RowNeverTookEffect, TermRunningNeedsProposal, ProposalOpen, ProposalRequesterUnknown,
        OutsideTerm, CompanyMismatch, BasisNotOwnSalary, CarriedDiffers, CarriedOverlaps, CloseOnly,
    ];
}

/// <summary>
/// Which employee documents count as the signed contract a proposal is confirmed against: the contract's own file (its
/// stored URL), or a document of a contract type. Mirrored by the proposal card (frontend packageFormat.isContractDocument).
/// </summary>
public static class SignedContractDocuments
{
    private static readonly HashSet<string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        "Contract", "Employment Contract", "Signed Contract", "Signed employment contract", "Labour Contract", "Labor Contract",
        "Qiwa Contract", "عقد", "عقد العمل", "عقد عمل",
    };

    public static bool IsContractType(string? documentType) =>
        documentType is not null && Types.Contains(System.Text.RegularExpressions.Regex.Replace(documentType.Trim(), @"\s+", " "));

    public static bool Matches(EmployeeDocumentView document, string? contractFileUrl) =>
        (!string.IsNullOrWhiteSpace(contractFileUrl) && string.Equals(document.StorageUrl, contractFileUrl, StringComparison.Ordinal))
        || IsContractType(document.DocumentType);
}

/// <summary>The two document fields the signed-contract rule reads.</summary>
public sealed record EmployeeDocumentView(string DocumentType, string StorageUrl);
