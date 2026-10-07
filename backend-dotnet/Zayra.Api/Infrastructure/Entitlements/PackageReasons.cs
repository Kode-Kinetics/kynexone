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
    public const string RowNeverTookEffect = ReleaseABlockReasons.EntitlementRowNeverTookEffect;
    public const string TermRunningNeedsProposal = ReleaseABlockReasons.EntitlementTermRunningNeedsProposal;
    public const string ProposalOpen = ReleaseABlockReasons.EntitlementProposalOpen;
    public const string ProposalRequesterUnknown = ReleaseABlockReasons.EntitlementProposalRequesterUnknown;
    public const string PredecessorUnconfirmed = ReleaseABlockReasons.EntitlementPredecessorUnconfirmed;
    public const string EarlierServiceUnconfirmed = ReleaseABlockReasons.EntitlementEarlierServiceUnconfirmed;
    public const string JoiningDateChanged = ReleaseABlockReasons.EntitlementJoiningDateChanged;
    public const string TermHasPackage = ReleaseABlockReasons.EntitlementTermHasPackage;
    // The stable message prefixes R0's triggers raise (SQLSTATE 23514), each mapped to its own code by FromDatabase.
    public const string OutsideTerm = ReleaseABlockReasons.EntitlementOutsideTerm;
    public const string CompanyMismatch = ReleaseABlockReasons.EntitlementCompanyMismatch;
    public const string BasisNotOwnSalary = ReleaseABlockReasons.EntitlementBasisNotOwnSalary;
    public const string CarriedDiffers = ReleaseABlockReasons.EntitlementCarriedDiffers;
    public const string CarriedOverlaps = ReleaseABlockReasons.EntitlementCarriedOverlaps;
    public const string CloseOnly = ReleaseABlockReasons.EntitlementCloseOnly;

    /// <summary>Codes R2 uses that the R0 catalogue does not have yet. Empty: R0 (#189, 349a56e1) added all twelve.</summary>
    /// <summary>Codes R2 uses that the R0 catalogue does not have yet. Empty: every R2 code is in ReleaseABlockReasons (on main).</summary>
    public static readonly IReadOnlyDictionary<string, BlockReason> Pending = new Dictionary<string, BlockReason>();


    /// <summary>
    /// The reason code for a database refusal: SQLSTATE 23P01 (the no-overlap EXCLUDE) is a term overlap; 23514 carries the
    /// trigger's stable <c>ENTITLEMENT_*</c> message prefix (R0 migration), mapped to the code of the same name. NULL when the
    /// exception is not an entitlement rule.
    /// </summary>
    public static string? FromDatabase(Exception exception)
    {
        // At COMMIT the deferred containment trigger raises a bare PostgresException (no DbUpdateException around it).
        if ((exception as Npgsql.PostgresException ?? exception.InnerException as Npgsql.PostgresException) is not { } pg) return null;
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
        RowNeverTookEffect, TermRunningNeedsProposal, ProposalOpen, ProposalRequesterUnknown, PredecessorUnconfirmed,
        EarlierServiceUnconfirmed, JoiningDateChanged, TermHasPackage,
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
