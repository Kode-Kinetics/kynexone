using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

// ── Employee status constants ─────────────────────────────────────────────────
public static class EmployeeStatuses
{
    /// <summary>Created by HR, not yet visible to the employee.</summary>
    public const string Draft      = "Draft";
    /// <summary>Login invite sent; employee has not yet accepted.</summary>
    public const string Invited    = "Invited";
    /// <summary>Fully onboarded and currently employed.</summary>
    public const string Active     = "Active";
    /// <summary>Access temporarily suspended (e.g. disciplinary hold).</summary>
    public const string Suspended  = "Suspended";
    /// <summary>Separation initiated; offboarding checklist in progress.</summary>
    public const string Offboarded = "Offboarded";
    /// <summary>Separation complete; record retained for historical / compliance purposes.</summary>
    public const string Archived   = "Archived";

    // Statuses from earlier code paths — kept for backward compat.
    public const string Inactive   = "Inactive";
    public const string Terminated = "Terminated";
    public const string Exited     = "Exited";
}

public class Employee : INullableTenantOwned, ICompanyScopedOperational
{
    public int Id { get; set; }
    /// <summary>
    /// Stable API/domain identifier used by modules whose public contracts use GUID employee references.
    /// The integer <see cref="Id"/> remains the internal relational key for backward compatibility.
    /// </summary>
    public Guid PublicId { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public Guid? UserAccountId { get; set; }
    public string EmployeeCode { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string EnglishName { get; set; } = string.Empty;
    public string ArabicName { get; set; } = string.Empty;
    public string PreferredName { get; set; } = string.Empty;
    public string ProfilePhotoUrl { get; set; } = string.Empty;
    public string PersonalEmail { get; set; } = string.Empty;
    public string WorkEmail { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Gender { get; set; } = string.Empty;
    public DateOnly? DateOfBirth { get; set; }
    public string MaritalStatus { get; set; } = string.Empty;
    public string EmergencyContactName { get; set; } = string.Empty;
    public string EmergencyContactPhone { get; set; } = string.Empty;
    public string Nationality { get; set; } = string.Empty;
    public string CountryCode { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string Designation { get; set; } = string.Empty;
    public Guid? CompanyId { get; set; }
    public Guid? BranchId { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? DesignationId { get; set; }
    public Guid? GradeId { get; set; }
    public Guid? CostCenterId { get; set; }
    public Guid? PositionId { get; set; }
    public string WorkLocation { get; set; } = string.Empty;
    public string Branch { get; set; } = string.Empty;
    public int? ManagerEmployeeId { get; set; }
    public int? SecondLevelManagerEmployeeId { get; set; }
    public int? SupervisorEmployeeId { get; set; }
    public int? HRBusinessPartnerEmployeeId { get; set; }
    public string Status { get; set; } = "Draft";
    public DateTime JoiningDate { get; set; }
    public DateOnly? ConfirmationDate { get; set; }
    public DateOnly? ProbationStartDate { get; set; }
    public string ContractType { get; set; } = string.Empty;
    public string EmploymentType { get; set; } = string.Empty;
    public string JobTitle { get; set; } = string.Empty;
    public string Grade { get; set; } = string.Empty;
    public string CostCenter { get; set; } = string.Empty;
    public int? NoticePeriodDays { get; set; }
    public DateOnly? ContractStartDate { get; set; }
    public DateOnly? ContractEndDate { get; set; }
    public DateOnly? ProbationEndDate { get; set; }
    public string PayrollProfileCode { get; set; } = string.Empty;
    public decimal? Salary { get; set; }
    public string BankName { get; set; } = string.Empty;
    public string BankIban { get; set; } = string.Empty;
    public string WpsBankDetails { get; set; } = string.Empty;
    public string ShiftPolicyCode { get; set; } = string.Empty;
    public string LeavePolicyCode { get; set; } = string.Empty;
    public string AttendancePolicyCode { get; set; } = string.Empty;
    public string SponsorName { get; set; } = string.Empty;
    public DateOnly? PassportIssueDate { get; set; }
    public DateOnly? VisaIssueDate { get; set; }
    public DateOnly? ResidencyIssueDate { get; set; }
    public DateOnly? WorkPermitIssueDate { get; set; }
    public string PassportNumber { get; set; } = string.Empty;
    public DateOnly? PassportExpiryDate { get; set; }
    public string VisaNumber { get; set; } = string.Empty;
    public DateOnly? VisaExpiryDate { get; set; }
    public string IqamaNumber { get; set; } = string.Empty;
    // GCC-ID expiry scalars — first-class columns for query/index parity with Passport/Visa expiry and
    // so the readiness pay-gate reads them directly (Δ20). Additive/nullable; mirrored from the create
    // modal's compliance records and set by the CSV importer's *Expiry columns.
    public DateOnly? IqamaExpiryDate { get; set; }
    public DateOnly? EmiratesIdExpiryDate { get; set; }
    public DateOnly? QidExpiryDate { get; set; }
    public DateOnly? CivilIdExpiryDate { get; set; }
    public string MuqeemNumber { get; set; } = string.Empty;
    public string GosiReference { get; set; } = string.Empty;
    public string QiwaContractNumber { get; set; } = string.Empty;
    public string EmiratesId { get; set; } = string.Empty;
    public string LaborCardNumber { get; set; } = string.Empty;
    public string VisaFileNumber { get; set; } = string.Empty;
    public string Qid { get; set; } = string.Empty;
    public string WorkPermitNumber { get; set; } = string.Empty;
    public string CivilId { get; set; } = string.Empty;
    public string ResidencyNumber { get; set; } = string.Empty;

    // ── Qiwa-ready fields (Saudi HR compliance integration) ───────────────────
    // These fields are stored locally and will be forwarded to Qiwa once the
    // integration is live.  No external API is called yet.

    /// <summary>"Saudi" or "NonSaudi" — required by Qiwa for all employees.</summary>
    public string SaudiOrNonSaudi { get; set; } = string.Empty;

    /// <summary>Primary government ID type: NationalId | Iqama | Passport | EmiratesId | Qid | BorderNumber.</summary>
    public string IdType { get; set; } = string.Empty;

    /// <summary>Primary government ID number matching IdType.</summary>
    public string IdNumber { get; set; } = string.Empty;

    /// <summary>ISCO-88 / Qiwa occupation code.</summary>
    public string OccupationCode { get; set; } = string.Empty;

    /// <summary>MOL establishment ID in Qiwa (e.g. "7000123456").</summary>
    public string EstablishmentId { get; set; } = string.Empty;

    /// <summary>Qiwa work location ID within the establishment.</summary>
    public string WorkLocationId { get; set; } = string.Empty;

    /// <summary>Qiwa labour contract reference number.</summary>
    public string ContractReference { get; set; } = string.Empty;

    /// <summary>Work permit reference in Qiwa (distinct from local WorkPermitNumber).</summary>
    public string WorkPermitReference { get; set; } = string.Empty;

    /// <summary>Qiwa's own employee reference once the record is registered.</summary>
    public string QiwaEmployeeReference { get; set; } = string.Empty;

    /// <summary>Current Qiwa sync state.  See QiwaSyncStatuses constants.</summary>
    public string QiwaSyncStatus { get; set; } = string.Empty;

    public string MedicalInformation { get; set; } = string.Empty;
    public string DisciplinaryRecords { get; set; } = string.Empty;
    public string TerminationReason { get; set; } = string.Empty;
    public decimal ProfileCompletenessScore { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
    public DateTime? ActivatedAtUtc { get; set; }
    // ── Readiness snapshot (denormalized for the list badge; the gate always recomputes live) ──
    /// <summary>Ready | NeedsAttention | Blocked. Fail-safe default for a new incomplete record.</summary>
    public string ReadinessState { get; set; } = "Blocked";
    /// <summary>Count of failClosed activate-gate items still missing (drives the "Incomplete · N" badge).</summary>
    public int ActivationBlockersCount { get; set; }
    public DateTime? ReadinessEvaluatedAtUtc { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public Guid? DeletedBy { get; set; }
    public DateTime? RetentionUntilUtc { get; set; }
    public string PrivacyStatus { get; set; } = string.Empty;
    public DateTime? RedactedAtUtc { get; set; }

    // ── Duplicate-person resolution (merge first slice) ────────────────────────
    /// <summary>When this record was merged into an existing one as a confirmed duplicate, the surviving
    /// employee's Id. The row itself is soft-removed (IsDeleted, PrivacyStatus="MergedDuplicate"); this link
    /// preserves the audit trail and lets a later, fuller field-by-field merge key off it. Null = not merged.
    /// Additive/nullable (migration-discipline: ships with its index in one migration).</summary>
    public int? DuplicateOfEmployeeId { get; set; }
}
