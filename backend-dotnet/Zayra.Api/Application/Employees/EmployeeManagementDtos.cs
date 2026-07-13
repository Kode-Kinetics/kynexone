using System.ComponentModel.DataAnnotations;
using Zayra.Api.Models;

namespace Zayra.Api.Application.Employees;

public record EmployeeCreateRequest(
    string? EmployeeCode,
    bool ManualEmployeeCode,
    [Required, MaxLength(180)] string EnglishName,
    [MaxLength(180)] string? ArabicName,
    [MaxLength(120)] string? PreferredName,
    [Required, MaxLength(40)] string Gender,
    DateOnly? DateOfBirth,
    [MaxLength(80)] string? Nationality,
    [MaxLength(60)] string? MaritalStatus,
    [EmailAddress] string? PersonalEmail,
    [EmailAddress] string? WorkEmail,
    [MaxLength(60)] string? MobileNumber,
    [MaxLength(500)] string? ProfilePhotoUrl,
    Guid? CompanyId,
    Guid? BranchId,
    Guid? DepartmentId,
    Guid? DesignationId,
    Guid? GradeId,
    Guid? CostCenterId,
    [MaxLength(180)] string? JobTitle,
    int? ReportingManagerEmployeeId,
    int? SecondLevelManagerEmployeeId,
    [MaxLength(80)] string? EmploymentType,
    [MaxLength(80)] string? ContractType,
    DateTime? JoiningDate,
    DateOnly? ConfirmationDate,
    DateOnly? ProbationStartDate,
    DateOnly? ProbationEndDate,
    int? NoticePeriodDays,
    [MaxLength(120)] string? WorkLocation,
    [MaxLength(80)] string? PayrollGroup,
    [MaxLength(80)] string? ShiftPolicyCode,
    [MaxLength(80)] string? LeavePolicyCode,
    [MaxLength(80)] string? AttendancePolicyCode,
    EmployeePayrollProfileRequest? PayrollProfile,
    EmployeeSalaryBreakdownRequest? SalaryBreakdown,
    IReadOnlyCollection<EmployeeComplianceRecordRequest>? ComplianceRecords,
    Guid? PositionId = null);

public record EmployeePayrollProfileRequest(
    string? BankName,
    string? Iban,
    string? AccountNumber,
    string? PaymentMethod,
    string? SalaryCurrency,
    string? PayrollGroup,
    string? SalaryStructureReference,
    bool WpsEligible,
    bool EosbEligible,
    string? SocialInsuranceReference,
    string? MolId,
    string? BankRoutingCode);

public record EmployeeSalaryBreakdownRequest(
    decimal? BasicSalary,
    decimal? HousingAllowance,
    decimal? TransportAllowance,
    decimal? FoodAllowance,
    decimal? MobileAllowance,
    decimal? OtherAllowance,
    decimal? FixedDeduction,
    string? SalaryStructureCode,
    DateOnly? EffectiveDate,
    string? Currency);

public record EmployeeComplianceRecordRequest(
    [Required, MaxLength(10)] string CountryCode,
    [Required, MaxLength(120)] string FieldKey,
    [Required, MaxLength(180)] string FieldLabel,
    string? FieldValue,
    DateOnly? IssueDate,
    DateOnly? ExpiryDate,
    bool IsSensitive,
    bool IsRequired);

public record EmployeeStatusChangeRequest(
    [Required, MaxLength(80)] string Status,
    [Required] DateOnly EffectiveDate,
    [Required, MaxLength(500)] string Reason);

public record EmployeeDocumentUploadMetadata(
    [Required, MaxLength(80)] string DocumentType,
    [MaxLength(80)] string? DocumentCategory,
    DateOnly? IssueDate,
    DateOnly? ExpiryDate,
    DateOnly? RenewalReminderDate,
    bool IsRequired,
    [MaxLength(40)] string? ApprovalStatus,
    [MaxLength(1000)] string? Notes);

public record UpdateDocumentMetadataRequest(
    [MaxLength(80)] string? DocumentType,
    [MaxLength(80)] string? DocumentCategory,
    DateOnly? IssueDate,
    DateOnly? ExpiryDate,
    DateOnly? RenewalReminderDate,
    bool? IsRequired,
    [MaxLength(1000)] string? Notes);

public record DocumentVerifyRequest([MaxLength(1000)] string? Notes);
public record DocumentRejectRequest([Required, MaxLength(1000)] string Reason);
public record DocumentExpiryCheckResult(int MarkedExpired, int RemindersSent);

public record EmployeeTransferCreateRequest(
    string? NewBranch,
    string? NewDepartment,
    string? NewDesignation,
    int? NewManagerEmployeeId,
    [Required] DateOnly EffectiveDate,
    [Required, MaxLength(1000)] string Reason);

/// <summary>
/// Full employee detail response. All Employee entity fields are projected explicitly
/// so newly-added entity fields never auto-serialize. Sensitive fields are populated
/// only when includeSensitive=true (i.e. CanViewSensitive() returned true for the caller).
/// </summary>
public record EmployeeDetailDto
{
    // ── Identity ──────────────────────────────────────────────────────────────────
    public int Id { get; init; }
    public Guid? UserAccountId { get; init; }
    public string EmployeeCode { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;
    public string EnglishName { get; init; } = string.Empty;
    public string ArabicName { get; init; } = string.Empty;
    public string PreferredName { get; init; } = string.Empty;
    public string ProfilePhotoUrl { get; init; } = string.Empty;
    // ── Contact ───────────────────────────────────────────────────────────────────
    public string PersonalEmail { get; init; } = string.Empty;
    public string WorkEmail { get; init; } = string.Empty;
    public string Phone { get; init; } = string.Empty;
    // ── Demographics ──────────────────────────────────────────────────────────────
    public string Gender { get; init; } = string.Empty;
    public DateOnly? DateOfBirth { get; init; }
    public string MaritalStatus { get; init; } = string.Empty;
    public string Nationality { get; init; } = string.Empty;
    public string CountryCode { get; init; } = string.Empty;
    public string EmergencyContactName { get; init; } = string.Empty;
    public string EmergencyContactPhone { get; init; } = string.Empty;
    // ── Position ──────────────────────────────────────────────────────────────────
    public string Department { get; init; } = string.Empty;
    public string Designation { get; init; } = string.Empty;
    public string Branch { get; init; } = string.Empty;
    public string WorkLocation { get; init; } = string.Empty;
    public string JobTitle { get; init; } = string.Empty;
    public string Grade { get; init; } = string.Empty;
    public string CostCenter { get; init; } = string.Empty;
    public string EmploymentType { get; init; } = string.Empty;
    public string ContractType { get; init; } = string.Empty;
    public Guid? CompanyId { get; init; }
    public Guid? BranchId { get; init; }
    public Guid? DepartmentId { get; init; }
    public Guid? DesignationId { get; init; }
    public Guid? GradeId { get; init; }
    public Guid? CostCenterId { get; init; }
    public Guid? PositionId { get; init; }
    public int? ManagerEmployeeId { get; init; }
    public int? SecondLevelManagerEmployeeId { get; init; }
    public int? SupervisorEmployeeId { get; init; }
    public int? HRBusinessPartnerEmployeeId { get; init; }
    // ── Employment ────────────────────────────────────────────────────────────────
    public string Status { get; init; } = string.Empty;
    public DateTime JoiningDate { get; init; }
    public DateOnly? ConfirmationDate { get; init; }
    public DateOnly? ProbationStartDate { get; init; }
    public DateOnly? ContractStartDate { get; init; }
    public DateOnly? ContractEndDate { get; init; }
    public DateOnly? ProbationEndDate { get; init; }
    public int? NoticePeriodDays { get; init; }
    public string PayrollProfileCode { get; init; } = string.Empty;
    public string ShiftPolicyCode { get; init; } = string.Empty;
    public string LeavePolicyCode { get; init; } = string.Empty;
    public string AttendancePolicyCode { get; init; } = string.Empty;
    // ── Payroll — SENSITIVE ───────────────────────────────────────────────────────
    public decimal? Salary { get; init; }
    public string BankName { get; init; } = string.Empty;
    public string BankIban { get; init; } = string.Empty;
    public string WpsBankDetails { get; init; } = string.Empty;
    // ── Compliance — dates ────────────────────────────────────────────────────────
    public string SponsorName { get; init; } = string.Empty;
    public DateOnly? PassportIssueDate { get; init; }
    public DateOnly? PassportExpiryDate { get; init; }
    public DateOnly? VisaIssueDate { get; init; }
    public DateOnly? VisaExpiryDate { get; init; }
    public DateOnly? ResidencyIssueDate { get; init; }
    public DateOnly? WorkPermitIssueDate { get; init; }
    // ── Compliance — numbers (PassportNumber, IqamaNumber SENSITIVE) ──────────────
    public string PassportNumber { get; init; } = string.Empty;
    public string VisaNumber { get; init; } = string.Empty;
    public string VisaFileNumber { get; init; } = string.Empty;
    public string IqamaNumber { get; init; } = string.Empty;
    public string MuqeemNumber { get; init; } = string.Empty;
    public string GosiReference { get; init; } = string.Empty;
    public string QiwaContractNumber { get; init; } = string.Empty;
    public string EmiratesId { get; init; } = string.Empty;
    public string LaborCardNumber { get; init; } = string.Empty;
    public string Qid { get; init; } = string.Empty;
    public string WorkPermitNumber { get; init; } = string.Empty;
    public string CivilId { get; init; } = string.Empty;
    public string ResidencyNumber { get; init; } = string.Empty;
    // ── Qiwa integration ──────────────────────────────────────────────────────────
    public string SaudiOrNonSaudi { get; init; } = string.Empty;
    public string IdType { get; init; } = string.Empty;
    public string IdNumber { get; init; } = string.Empty;
    public string OccupationCode { get; init; } = string.Empty;
    public string EstablishmentId { get; init; } = string.Empty;
    public string WorkLocationId { get; init; } = string.Empty;
    public string ContractReference { get; init; } = string.Empty;
    public string WorkPermitReference { get; init; } = string.Empty;
    public string QiwaEmployeeReference { get; init; } = string.Empty;
    public string QiwaSyncStatus { get; init; } = string.Empty;
    // ── HR notes — SENSITIVE ──────────────────────────────────────────────────────
    public string MedicalInformation { get; init; } = string.Empty;
    public string DisciplinaryRecords { get; init; } = string.Empty;
    public string TerminationReason { get; init; } = string.Empty;
    // ── Metadata ──────────────────────────────────────────────────────────────────
    public decimal ProfileCompletenessScore { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime? UpdatedAtUtc { get; init; }
    public DateTime? ActivatedAtUtc { get; init; }
    // ── Subordinate collections (projected to DTOs — no raw EF entities) ─────────
    public EmployeePayrollProfileDto? PayrollProfile { get; init; }
    public IReadOnlyCollection<EmployeeComplianceRecord> ComplianceRecords { get; init; } = [];
    public IReadOnlyCollection<EmployeeDocumentDto> Documents { get; init; } = [];
    public IReadOnlyCollection<EmployeeHistoryDto> History { get; init; } = [];
    public IReadOnlyCollection<EmployeeTransferDto> Transfers { get; init; } = [];

    /// <summary>
    /// Projects a raw Employee entity to this DTO, applying the canonical sensitive-field
    /// gate inline. Does NOT mutate the entity.
    /// Sensitive field list must stay in sync with <see cref="EmployeeSensitiveMask"/>.
    /// </summary>
    public static EmployeeDetailDto Project(
        Employee e,
        bool includeSensitive,
        EmployeePayrollProfile? payrollProfile = null,
        IReadOnlyCollection<EmployeeComplianceRecord>? complianceRecords = null,
        IReadOnlyCollection<EmployeeDocument>? documents = null,
        IReadOnlyCollection<EmployeeHistory>? history = null,
        IReadOnlyCollection<EmployeeTransferRequest>? transfers = null,
        IReadOnlyCollection<EmployeeDocumentDto>? documentDtos = null) =>
        new()
        {
            Id                            = e.Id,
            UserAccountId                 = e.UserAccountId,
            EmployeeCode                  = e.EmployeeCode,
            FullName                      = e.FullName,
            EnglishName                   = e.EnglishName,
            ArabicName                    = e.ArabicName,
            PreferredName                 = e.PreferredName,
            ProfilePhotoUrl               = e.ProfilePhotoUrl,
            PersonalEmail                 = e.PersonalEmail,
            WorkEmail                     = e.WorkEmail,
            Phone                         = e.Phone,
            Gender                        = e.Gender,
            DateOfBirth                   = e.DateOfBirth,
            MaritalStatus                 = e.MaritalStatus,
            Nationality                   = e.Nationality,
            CountryCode                   = e.CountryCode,
            EmergencyContactName          = e.EmergencyContactName,
            EmergencyContactPhone         = e.EmergencyContactPhone,
            Department                    = e.Department,
            Designation                   = e.Designation,
            Branch                        = e.Branch,
            WorkLocation                  = e.WorkLocation,
            JobTitle                      = e.JobTitle,
            Grade                         = e.Grade,
            CostCenter                    = e.CostCenter,
            EmploymentType                = e.EmploymentType,
            ContractType                  = e.ContractType,
            CompanyId                     = e.CompanyId,
            BranchId                      = e.BranchId,
            DepartmentId                  = e.DepartmentId,
            DesignationId                 = e.DesignationId,
            GradeId                       = e.GradeId,
            CostCenterId                  = e.CostCenterId,
            PositionId                    = e.PositionId,
            ManagerEmployeeId             = e.ManagerEmployeeId,
            SecondLevelManagerEmployeeId  = e.SecondLevelManagerEmployeeId,
            SupervisorEmployeeId          = e.SupervisorEmployeeId,
            HRBusinessPartnerEmployeeId   = e.HRBusinessPartnerEmployeeId,
            Status                        = e.Status,
            JoiningDate                   = e.JoiningDate,
            ConfirmationDate              = e.ConfirmationDate,
            ProbationStartDate            = e.ProbationStartDate,
            ContractStartDate             = e.ContractStartDate,
            ContractEndDate               = e.ContractEndDate,
            ProbationEndDate              = e.ProbationEndDate,
            NoticePeriodDays              = e.NoticePeriodDays,
            PayrollProfileCode            = e.PayrollProfileCode,
            ShiftPolicyCode               = e.ShiftPolicyCode,
            LeavePolicyCode               = e.LeavePolicyCode,
            AttendancePolicyCode          = e.AttendancePolicyCode,
            // Sensitive — gated
            Salary                        = includeSensitive ? e.Salary : null,
            BankName                      = includeSensitive ? e.BankName : string.Empty,
            BankIban                      = includeSensitive ? e.BankIban : string.Empty,
            WpsBankDetails                = includeSensitive ? e.WpsBankDetails : string.Empty,
            PassportNumber                = includeSensitive ? e.PassportNumber : string.Empty,
            IqamaNumber                   = includeSensitive ? e.IqamaNumber : string.Empty,
            MedicalInformation            = includeSensitive ? e.MedicalInformation : string.Empty,
            DisciplinaryRecords           = includeSensitive ? e.DisciplinaryRecords : string.Empty,
            TerminationReason             = includeSensitive ? e.TerminationReason : string.Empty,
            // Compliance identifiers and expiry metadata are sensitive in GCC HR contexts.
            SponsorName                   = e.SponsorName,
            PassportIssueDate             = includeSensitive ? e.PassportIssueDate : null,
            PassportExpiryDate            = includeSensitive ? e.PassportExpiryDate : null,
            VisaIssueDate                 = includeSensitive ? e.VisaIssueDate : null,
            VisaExpiryDate                = includeSensitive ? e.VisaExpiryDate : null,
            ResidencyIssueDate            = includeSensitive ? e.ResidencyIssueDate : null,
            WorkPermitIssueDate           = includeSensitive ? e.WorkPermitIssueDate : null,
            VisaNumber                    = includeSensitive ? e.VisaNumber : string.Empty,
            VisaFileNumber                = includeSensitive ? e.VisaFileNumber : string.Empty,
            MuqeemNumber                  = includeSensitive ? e.MuqeemNumber : string.Empty,
            GosiReference                 = includeSensitive ? e.GosiReference : string.Empty,
            QiwaContractNumber            = includeSensitive ? e.QiwaContractNumber : string.Empty,
            EmiratesId                    = includeSensitive ? e.EmiratesId : string.Empty,
            LaborCardNumber               = includeSensitive ? e.LaborCardNumber : string.Empty,
            Qid                           = includeSensitive ? e.Qid : string.Empty,
            WorkPermitNumber              = includeSensitive ? e.WorkPermitNumber : string.Empty,
            CivilId                       = includeSensitive ? e.CivilId : string.Empty,
            ResidencyNumber               = includeSensitive ? e.ResidencyNumber : string.Empty,
            SaudiOrNonSaudi               = e.SaudiOrNonSaudi,
            IdType                        = e.IdType,
            IdNumber                      = includeSensitive ? e.IdNumber : string.Empty,
            OccupationCode                = e.OccupationCode,
            EstablishmentId               = e.EstablishmentId,
            WorkLocationId                = e.WorkLocationId,
            ContractReference             = e.ContractReference,
            WorkPermitReference           = e.WorkPermitReference,
            QiwaEmployeeReference         = e.QiwaEmployeeReference,
            QiwaSyncStatus                = e.QiwaSyncStatus,
            ProfileCompletenessScore      = e.ProfileCompletenessScore,
            CreatedAtUtc                  = e.CreatedAtUtc,
            UpdatedAtUtc                  = e.UpdatedAtUtc,
            ActivatedAtUtc                = e.ActivatedAtUtc,
            // Collections — projected to DTOs; raw entity collections accepted for backward compat
            PayrollProfile                = payrollProfile is not null ? EmployeePayrollProfileDto.Project(payrollProfile, includeSensitive) : null,
            ComplianceRecords             = complianceRecords ?? [],
            Documents                     = documentDtos ?? documents?.Select(EmployeeDocumentDto.Project).ToList() ?? [],
            History                       = history?.Select(EmployeeHistoryDto.Project).ToList() ?? [],
            Transfers                     = transfers?.Select(EmployeeTransferDto.Project).ToList() ?? [],
        };
}

/// <summary>
/// Self-service profile for an authenticated employee viewing their own record.
/// Excludes internal HR notes (DisciplinaryRecords, MedicalInformation, TerminationReason)
/// and internal processing fields (WpsBankDetails, QiwaSyncStatus, etc.).
/// The employee sees their own financial and identity data — these are not masked.
/// </summary>
public record EssEmployeeProfileDto
{
    public int Id { get; init; }
    public string EmployeeCode { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;
    public string EnglishName { get; init; } = string.Empty;
    public string ArabicName { get; init; } = string.Empty;
    public string PreferredName { get; init; } = string.Empty;
    public string ProfilePhotoUrl { get; init; } = string.Empty;
    public string PersonalEmail { get; init; } = string.Empty;
    public string WorkEmail { get; init; } = string.Empty;
    public string Phone { get; init; } = string.Empty;
    public string Gender { get; init; } = string.Empty;
    public DateOnly? DateOfBirth { get; init; }
    public string MaritalStatus { get; init; } = string.Empty;
    public string Nationality { get; init; } = string.Empty;
    public string EmergencyContactName { get; init; } = string.Empty;
    public string EmergencyContactPhone { get; init; } = string.Empty;
    public string Department { get; init; } = string.Empty;
    public string Designation { get; init; } = string.Empty;
    public string JobTitle { get; init; } = string.Empty;
    public string Branch { get; init; } = string.Empty;
    public string WorkLocation { get; init; } = string.Empty;
    public string EmploymentType { get; init; } = string.Empty;
    public string ContractType { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTime JoiningDate { get; init; }
    public DateOnly? ContractStartDate { get; init; }
    public DateOnly? ContractEndDate { get; init; }
    public DateOnly? ProbationEndDate { get; init; }
    public int? ManagerEmployeeId { get; init; }
    // Own financial data — employee can see their own salary and bank details
    public decimal? Salary { get; init; }
    public string BankName { get; init; } = string.Empty;
    public string BankIban { get; init; } = string.Empty;
    // Own identity documents
    public string PassportNumber { get; init; } = string.Empty;
    public DateOnly? PassportIssueDate { get; init; }
    public DateOnly? PassportExpiryDate { get; init; }
    public string VisaNumber { get; init; } = string.Empty;
    public DateOnly? VisaIssueDate { get; init; }
    public DateOnly? VisaExpiryDate { get; init; }
    public string IqamaNumber { get; init; } = string.Empty;
    public string EmiratesId { get; init; } = string.Empty;
    public decimal ProfileCompletenessScore { get; init; }
    public DateTime? ActivatedAtUtc { get; init; }

    public static EssEmployeeProfileDto Project(Employee e) => new()
    {
        Id                       = e.Id,
        EmployeeCode             = e.EmployeeCode,
        FullName                 = e.FullName,
        EnglishName              = e.EnglishName,
        ArabicName               = e.ArabicName,
        PreferredName            = e.PreferredName,
        ProfilePhotoUrl          = e.ProfilePhotoUrl,
        PersonalEmail            = e.PersonalEmail,
        WorkEmail                = e.WorkEmail,
        Phone                    = e.Phone,
        Gender                   = e.Gender,
        DateOfBirth              = e.DateOfBirth,
        MaritalStatus            = e.MaritalStatus,
        Nationality              = e.Nationality,
        EmergencyContactName     = e.EmergencyContactName,
        EmergencyContactPhone    = e.EmergencyContactPhone,
        Department               = e.Department,
        Designation              = e.Designation,
        JobTitle                 = e.JobTitle,
        Branch                   = e.Branch,
        WorkLocation             = e.WorkLocation,
        EmploymentType           = e.EmploymentType,
        ContractType             = e.ContractType,
        Status                   = e.Status,
        JoiningDate              = e.JoiningDate,
        ContractStartDate        = e.ContractStartDate,
        ContractEndDate          = e.ContractEndDate,
        ProbationEndDate         = e.ProbationEndDate,
        ManagerEmployeeId        = e.ManagerEmployeeId,
        Salary                   = e.Salary,
        BankName                 = e.BankName,
        BankIban                 = e.BankIban,
        PassportNumber           = e.PassportNumber,
        PassportIssueDate        = e.PassportIssueDate,
        PassportExpiryDate       = e.PassportExpiryDate,
        VisaNumber               = e.VisaNumber,
        VisaIssueDate            = e.VisaIssueDate,
        VisaExpiryDate           = e.VisaExpiryDate,
        IqamaNumber              = e.IqamaNumber,
        EmiratesId               = e.EmiratesId,
        ProfileCompletenessScore = e.ProfileCompletenessScore,
        ActivatedAtUtc           = e.ActivatedAtUtc,
    };
}

/// <summary>
/// Safe projection of <see cref="Zayra.Api.Models.EmployeeDraft"/> for API responses.
/// Sensitive fields (salary, bank, passport, Iqama) are gated by the caller's
/// CanViewSensitive() result, exactly like EmployeeDetailDto.
/// Does NOT include TenantId, IsDeleted, or CreatedByUserId (internal system fields).
/// </summary>
public record EmployeeDraftDto
{
    public Guid Id { get; init; }
    public string Status { get; init; } = string.Empty;
    public string CurrentStep { get; init; } = string.Empty;
    public string EnglishName { get; init; } = string.Empty;
    public string ArabicName { get; init; } = string.Empty;
    public string PersonalEmail { get; init; } = string.Empty;
    public string WorkEmail { get; init; } = string.Empty;
    public string Phone { get; init; } = string.Empty;
    public string Gender { get; init; } = string.Empty;
    public DateOnly? DateOfBirth { get; init; }
    public string MaritalStatus { get; init; } = string.Empty;
    public string EmergencyContactName { get; init; } = string.Empty;
    public string EmergencyContactPhone { get; init; } = string.Empty;
    public string Nationality { get; init; } = string.Empty;
    public string CountryCode { get; init; } = string.Empty;
    public string Department { get; init; } = string.Empty;
    public string Designation { get; init; } = string.Empty;
    public string Branch { get; init; } = string.Empty;
    public string WorkLocation { get; init; } = string.Empty;
    public int? ManagerEmployeeId { get; init; }
    public DateTime? JoiningDate { get; init; }
    public string ContractType { get; init; } = string.Empty;
    public string Grade { get; init; } = string.Empty;
    public string CostCenter { get; init; } = string.Empty;
    public DateOnly? ContractStartDate { get; init; }
    public DateOnly? ContractEndDate { get; init; }
    public DateOnly? ProbationEndDate { get; init; }
    public string PayrollProfileCode { get; init; } = string.Empty;
    public string ShiftPolicyCode { get; init; } = string.Empty;
    public string LeavePolicyCode { get; init; } = string.Empty;
    public string SponsorName { get; init; } = string.Empty;
    // Compliance dates (not sensitive)
    public DateOnly? PassportIssueDate { get; init; }
    public DateOnly? PassportExpiryDate { get; init; }
    public DateOnly? VisaIssueDate { get; init; }
    public DateOnly? VisaExpiryDate { get; init; }
    public DateOnly? ResidencyIssueDate { get; init; }
    public DateOnly? WorkPermitIssueDate { get; init; }
    // Sensitive — gated by CanViewSensitive()
    public decimal? Salary { get; init; }
    public string BankName { get; init; } = string.Empty;
    public string BankIban { get; init; } = string.Empty;
    public string WpsBankDetails { get; init; } = string.Empty;
    public string PassportNumber { get; init; } = string.Empty;
    public string VisaNumber { get; init; } = string.Empty;
    public string VisaFileNumber { get; init; } = string.Empty;
    public string IqamaNumber { get; init; } = string.Empty;
    public string MuqeemNumber { get; init; } = string.Empty;
    public string GosiReference { get; init; } = string.Empty;
    public string QiwaContractNumber { get; init; } = string.Empty;
    public string EmiratesId { get; init; } = string.Empty;
    public string LaborCardNumber { get; init; } = string.Empty;
    public string Qid { get; init; } = string.Empty;
    public string WorkPermitNumber { get; init; } = string.Empty;
    public string CivilId { get; init; } = string.Empty;
    public string ResidencyNumber { get; init; } = string.Empty;
    public decimal ProfileCompletenessScore { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime? SubmittedAtUtc { get; init; }
    public DateTime? ApprovedAtUtc { get; init; }

    public static EmployeeDraftDto Project(EmployeeDraft d, bool includeSensitive) => new()
    {
        Id                       = d.Id,
        Status                   = d.Status,
        CurrentStep              = d.CurrentStep,
        EnglishName              = d.EnglishName,
        ArabicName               = d.ArabicName,
        PersonalEmail            = d.PersonalEmail,
        WorkEmail                = d.WorkEmail,
        Phone                    = d.Phone,
        Gender                   = d.Gender,
        DateOfBirth              = d.DateOfBirth,
        MaritalStatus            = d.MaritalStatus,
        EmergencyContactName     = d.EmergencyContactName,
        EmergencyContactPhone    = d.EmergencyContactPhone,
        Nationality              = d.Nationality,
        CountryCode              = d.CountryCode,
        Department               = d.Department,
        Designation              = d.Designation,
        Branch                   = d.Branch,
        WorkLocation             = d.WorkLocation,
        ManagerEmployeeId        = d.ManagerEmployeeId,
        JoiningDate              = d.JoiningDate,
        ContractType             = d.ContractType,
        Grade                    = d.Grade,
        CostCenter               = d.CostCenter,
        ContractStartDate        = d.ContractStartDate,
        ContractEndDate          = d.ContractEndDate,
        ProbationEndDate         = d.ProbationEndDate,
        PayrollProfileCode       = d.PayrollProfileCode,
        ShiftPolicyCode          = d.ShiftPolicyCode,
        LeavePolicyCode          = d.LeavePolicyCode,
        SponsorName              = d.SponsorName,
        PassportIssueDate        = includeSensitive ? d.PassportIssueDate : null,
        PassportExpiryDate       = includeSensitive ? d.PassportExpiryDate : null,
        VisaIssueDate            = includeSensitive ? d.VisaIssueDate : null,
        VisaExpiryDate           = includeSensitive ? d.VisaExpiryDate : null,
        ResidencyIssueDate       = includeSensitive ? d.ResidencyIssueDate : null,
        WorkPermitIssueDate      = includeSensitive ? d.WorkPermitIssueDate : null,
        // Sensitive
        Salary                   = includeSensitive ? d.Salary : null,
        BankName                 = includeSensitive ? d.BankName : string.Empty,
        BankIban                 = includeSensitive ? d.BankIban : string.Empty,
        WpsBankDetails           = includeSensitive ? d.WpsBankDetails : string.Empty,
        PassportNumber           = includeSensitive ? d.PassportNumber : string.Empty,
        VisaNumber               = includeSensitive ? d.VisaNumber : string.Empty,
        VisaFileNumber           = includeSensitive ? d.VisaFileNumber : string.Empty,
        IqamaNumber              = includeSensitive ? d.IqamaNumber : string.Empty,
        MuqeemNumber             = includeSensitive ? d.MuqeemNumber : string.Empty,
        GosiReference            = includeSensitive ? d.GosiReference : string.Empty,
        QiwaContractNumber       = includeSensitive ? d.QiwaContractNumber : string.Empty,
        EmiratesId               = includeSensitive ? d.EmiratesId : string.Empty,
        LaborCardNumber          = includeSensitive ? d.LaborCardNumber : string.Empty,
        Qid                      = includeSensitive ? d.Qid : string.Empty,
        WorkPermitNumber         = includeSensitive ? d.WorkPermitNumber : string.Empty,
        CivilId                  = includeSensitive ? d.CivilId : string.Empty,
        ResidencyNumber          = includeSensitive ? d.ResidencyNumber : string.Empty,
        ProfileCompletenessScore = d.ProfileCompletenessScore,
        CreatedAtUtc             = d.CreatedAtUtc,
        SubmittedAtUtc           = d.SubmittedAtUtc,
        ApprovedAtUtc            = d.ApprovedAtUtc,
    };
}

/// <summary>
/// Safe projection of <see cref="Zayra.Api.Models.EmployeeDocument"/> for API responses.
/// Excludes TenantId, IsDeleted, DeletedAtUtc, DeletedBy (internal/soft-delete system fields).
/// </summary>
public record EmployeeDocumentDto
{
    public Guid Id { get; init; }
    public int? EmployeeId { get; init; }
    public Guid? DraftId { get; init; }
    public string DocumentType { get; init; } = string.Empty;
    public string DocumentCategory { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string ContentType { get; init; } = string.Empty;
    public string StorageUrl { get; init; } = string.Empty;
    public bool IsRequired { get; init; }
    public DateOnly? IssueDate { get; init; }
    public DateOnly? ExpiryDate { get; init; }
    public DateOnly? RenewalReminderDate { get; init; }
    public string ApprovalStatus { get; init; } = string.Empty;
    public int VersionNumber { get; init; }
    public Guid? UploadedBy { get; init; }
    public DateTime UploadedAtUtc { get; init; }
    public DateTime? VerifiedAtUtc { get; init; }
    public Guid? VerifiedBy { get; init; }
    public string Notes { get; init; } = string.Empty;

    public static EmployeeDocumentDto Project(EmployeeDocument d) => new()
    {
        Id                   = d.Id,
        EmployeeId           = d.EmployeeId,
        DraftId              = d.DraftId,
        DocumentType         = d.DocumentType,
        DocumentCategory     = d.DocumentCategory,
        FileName             = d.FileName,
        ContentType          = d.ContentType,
        StorageUrl           = d.StorageUrl,
        IsRequired           = d.IsRequired,
        IssueDate            = d.IssueDate,
        ExpiryDate           = d.ExpiryDate,
        RenewalReminderDate  = d.RenewalReminderDate,
        ApprovalStatus       = d.ApprovalStatus,
        VersionNumber        = d.VersionNumber,
        UploadedBy           = d.UploadedBy,
        UploadedAtUtc        = d.UploadedAtUtc,
        VerifiedAtUtc        = d.VerifiedAtUtc,
        VerifiedBy           = d.VerifiedBy,
        Notes                = d.Notes,
    };
}

/// <summary>
/// Safe projection of <see cref="Zayra.Api.Models.EmployeePayrollProfile"/> for API responses.
/// Bank IBAN, account number, routing code, and MolId are gated by <paramref name="includeSensitive"/>.
/// Excludes IsDeleted, DeletedAtUtc, DeletedBy.
/// </summary>
public record EmployeePayrollProfileDto
{
    public Guid Id { get; init; }
    public int EmployeeId { get; init; }
    public string BankName { get; init; } = string.Empty;
    public string Iban { get; init; } = string.Empty;
    public string AccountNumber { get; init; } = string.Empty;
    public string PaymentMethod { get; init; } = string.Empty;
    public string SalaryCurrency { get; init; } = string.Empty;
    public string PayrollGroup { get; init; } = string.Empty;
    public string SalaryStructureReference { get; init; } = string.Empty;
    public bool WpsEligible { get; init; }
    public bool EosbEligible { get; init; }
    public string SocialInsuranceReference { get; init; } = string.Empty;
    public string MolId { get; init; } = string.Empty;
    public string BankRoutingCode { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }
    public DateTime? UpdatedAtUtc { get; init; }

    public static EmployeePayrollProfileDto Project(EmployeePayrollProfile p, bool includeSensitive) => new()
    {
        Id                       = p.Id,
        EmployeeId               = p.EmployeeId,
        BankName                 = p.BankName,
        Iban                     = includeSensitive ? p.Iban : string.Empty,
        AccountNumber            = includeSensitive ? p.AccountNumber : string.Empty,
        PaymentMethod            = p.PaymentMethod,
        SalaryCurrency           = p.SalaryCurrency,
        PayrollGroup             = p.PayrollGroup,
        SalaryStructureReference = p.SalaryStructureReference,
        WpsEligible              = p.WpsEligible,
        EosbEligible             = p.EosbEligible,
        SocialInsuranceReference = p.SocialInsuranceReference,
        MolId                    = includeSensitive ? p.MolId : string.Empty,
        BankRoutingCode          = includeSensitive ? p.BankRoutingCode : string.Empty,
        CreatedAtUtc             = p.CreatedAtUtc,
        UpdatedAtUtc             = p.UpdatedAtUtc,
    };
}

/// <summary>
/// Safe projection of <see cref="Zayra.Api.Models.EmployeeHistory"/> for API responses.
/// Excludes TenantId. SnapshotJson may contain a full employee snapshot and is only included
/// when the caller has HR/Admin access (admin module endpoints require at least HR Manager role).
/// </summary>
public record EmployeeHistoryDto
{
    public Guid Id { get; init; }
    public int EmployeeId { get; init; }
    public string EventType { get; init; } = string.Empty;
    public string FieldName { get; init; } = string.Empty;
    public string OldValue { get; init; } = string.Empty;
    public string NewValue { get; init; } = string.Empty;
    public DateOnly EffectiveDate { get; init; }
    public string Reason { get; init; } = string.Empty;
    public Guid? ApprovedByUserId { get; init; }
    public Guid? SupportingDocumentId { get; init; }
    public string SnapshotJson { get; init; } = "{}";
    public Guid? CreatedByUserId { get; init; }
    public DateTime CreatedAtUtc { get; init; }

    public static EmployeeHistoryDto Project(EmployeeHistory h) => new()
    {
        Id                  = h.Id,
        EmployeeId          = h.EmployeeId,
        EventType           = h.EventType,
        FieldName           = h.FieldName,
        OldValue            = h.OldValue,
        NewValue            = h.NewValue,
        EffectiveDate       = h.EffectiveDate,
        Reason              = h.Reason,
        ApprovedByUserId    = h.ApprovedByUserId,
        SupportingDocumentId = h.SupportingDocumentId,
        SnapshotJson        = h.SnapshotJson,
        CreatedByUserId     = h.CreatedByUserId,
        CreatedAtUtc        = h.CreatedAtUtc,
    };
}

/// <summary>
/// Safe projection of <see cref="Zayra.Api.Models.EmployeeTransferRequest"/> for API responses.
/// Contains only org restructuring data (department, designation, branch, manager changes).
/// No salary, bank, health, or identity PII.
/// </summary>
public record EmployeeTransferDto
{
    public Guid Id { get; init; }
    public int EmployeeId { get; init; }
    public string CurrentBranch { get; init; } = string.Empty;
    public string CurrentDepartment { get; init; } = string.Empty;
    public string CurrentDesignation { get; init; } = string.Empty;
    public int? CurrentManagerEmployeeId { get; init; }
    public string NewDepartment { get; init; } = string.Empty;
    public string NewBranch { get; init; } = string.Empty;
    public string NewDesignation { get; init; } = string.Empty;
    public int? NewManagerEmployeeId { get; init; }
    public DateOnly EffectiveDate { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public Guid? RequestedByUserId { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime? CurrentManagerApprovedAtUtc { get; init; }
    public DateTime? NewManagerApprovedAtUtc { get; init; }
    public DateTime? HrApprovedAtUtc { get; init; }

    public static EmployeeTransferDto Project(EmployeeTransferRequest t) => new()
    {
        Id                           = t.Id,
        EmployeeId                   = t.EmployeeId,
        CurrentBranch                = t.CurrentBranch,
        CurrentDepartment            = t.CurrentDepartment,
        CurrentDesignation           = t.CurrentDesignation,
        CurrentManagerEmployeeId     = t.CurrentManagerEmployeeId,
        NewDepartment                = t.NewDepartment,
        NewBranch                    = t.NewBranch,
        NewDesignation               = t.NewDesignation,
        NewManagerEmployeeId         = t.NewManagerEmployeeId,
        EffectiveDate                = t.EffectiveDate,
        Reason                       = t.Reason,
        Status                       = t.Status,
        RequestedByUserId            = t.RequestedByUserId,
        CreatedAtUtc                 = t.CreatedAtUtc,
        CurrentManagerApprovedAtUtc  = t.CurrentManagerApprovedAtUtc,
        NewManagerApprovedAtUtc      = t.NewManagerApprovedAtUtc,
        HrApprovedAtUtc              = t.HrApprovedAtUtc,
    };
}

public record EmployeeGroupCountDto(string Name, int Count);
public record EmployeeHeadcountReportDto(int Total, IReadOnlyCollection<EmployeeGroupCountDto> ByCompany, IReadOnlyCollection<EmployeeGroupCountDto> ByDepartment, IReadOnlyCollection<EmployeeGroupCountDto> ByStatus);
public record EmployeeStatusSummaryDto(IReadOnlyCollection<EmployeeGroupCountDto> Statuses);
public record EmployeeMissingDocumentsReportDto(int EmployeeId, string EmployeeCode, string FullName, IReadOnlyCollection<string> MissingDocumentTypes);
public record EmployeeExpiringDocumentDto(int EmployeeId, string EmployeeCode, string FullName, string DocumentType, DateOnly? ExpiryDate);
