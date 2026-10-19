using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

public class EmployeeDraft : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public string Status { get; set; } = "Draft";
    public string CurrentStep { get; set; } = "PersonalInformation";
    public string EnglishName { get; set; } = string.Empty;
    public string ArabicName { get; set; } = string.Empty;
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
    public string Branch { get; set; } = string.Empty;
    public string WorkLocation { get; set; } = string.Empty;
    public int? ManagerEmployeeId { get; set; }
    public DateTime? JoiningDate { get; set; }
    public string ContractType { get; set; } = string.Empty;
    public string Grade { get; set; } = string.Empty;
    public string CostCenter { get; set; } = string.Empty;
    public DateOnly? ContractStartDate { get; set; }
    public DateOnly? ContractEndDate { get; set; }
    public DateOnly? ProbationEndDate { get; set; }
    public string PayrollProfileCode { get; set; } = string.Empty;
    public decimal? Salary { get; set; }
    // Immutable acceptance witness; null JSON explicitly means no retained legacy snapshot.
    public Guid? AcceptedOfferId { get; set; }
    public Guid? AcceptedOfferCompanyId { get; set; }
    public Guid? OfferCompensationPlacementCompanyId { get; set; }
    public string? AcceptedOfferCompensationJson { get; set; }
    public long OfferCompensationVersion { get; set; }
    public string? OfferCompensationDispositionsJson { get; set; }
    public string? OfferCompensationConfirmedFingerprint { get; set; }
    public DateTime? OfferCompensationConfirmedAtUtc { get; set; }
    public Guid? OfferCompensationConfirmedByUserId { get; set; }
    public string BankName { get; set; } = string.Empty;
    public string BankIban { get; set; } = string.Empty;
    public string WpsBankDetails { get; set; } = string.Empty;
    public string ShiftPolicyCode { get; set; } = string.Empty;
    public string LeavePolicyCode { get; set; } = string.Empty;
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
    public decimal ProfileCompletenessScore { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? SubmittedAtUtc { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public DateTime? ActivatedAtUtc { get; set; }
}
