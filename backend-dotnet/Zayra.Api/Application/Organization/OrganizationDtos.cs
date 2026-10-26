using System.ComponentModel.DataAnnotations;
using Zayra.Api.Models;

namespace Zayra.Api.Application.Organization;

public record CompanyDto(
    Guid Id,
    string LegalNameEn,
    string LegalNameAr,
    string TradeName,
    string CountryCode,
    string Jurisdiction,
    string RegistrationNumber,
    string TaxNumber,
    string WpsEmployerId,
    string GosiEmployerId,
    string QiwaEstablishmentId,
    string DefaultCurrency,
    bool IsActive,
    string ApprovalStatus = CompanyApprovalStatuses.Active,
    // Work-email auto-derivation config (empty EmailDomain ⇒ manual work-email entry for this company).
    string EmailDomain = "",
    string WorkEmailPattern = WorkEmailPatterns.FirstLast,
    // The legal entity's company code in the client's ERP (SAP company code, Oracle balancing segment,
    // D365 legal entity). Journal exports stamp it on every line; empty ⇒ the CR-number fallback.
    string ErpCompanyCode = "",
    // The establishment's Unified National Number (SA "700 number", 10 digits). Empty = not recorded.
    string UnifiedNationalNumber = "");

public record CompanyRequest(
    [Required, MaxLength(180)] string LegalNameEn,
    [MaxLength(180)] string? LegalNameAr,
    [MaxLength(180)] string? TradeName,
    [Required, MaxLength(10)] string CountryCode,
    [MaxLength(60)] string? Jurisdiction,
    [Required, MaxLength(80)] string RegistrationNumber,
    [MaxLength(80)] string? TaxNumber,
    [MaxLength(80)] string? WpsEmployerId,
    [MaxLength(80)] string? GosiEmployerId,
    [MaxLength(80)] string? QiwaEstablishmentId,
    [Required, MaxLength(3)] string DefaultCurrency,
    // Auto-derive work-email config (captured at company create/edit in BOTH Setup and Platform).
    // EmailDomain is format-validated server-side; empty is allowed (manual-entry fallback). Pattern is
    // one of WorkEmailPatterns (coerced to first.last if unrecognized).
    [MaxLength(255)] string? EmailDomain = null,
    [MaxLength(20)] string? WorkEmailPattern = null,
    bool IsActive = true,
    // null ⇒ leave the stored ERP company code unchanged (callers that predate the field — the CSV
    // importer, the setup assistant — must not wipe it); "" ⇒ clear it.
    [MaxLength(30), RegularExpression(@"^[A-Za-z0-9._\-]*$", ErrorMessage = "ERP company code may contain only letters, digits, '.', '_' and '-'.")] string? ErpCompanyCode = null,
    // null ⇒ leave the stored number unchanged (callers that predate the field); "" ⇒ clear it. SA: 10 digits.
    [MaxLength(20)] string? UnifiedNationalNumber = null);

public record BranchDto(
    Guid Id,
    Guid CompanyId,
    string Code,
    string NameEn,
    string NameAr,
    string CountryCode,
    string City,
    string AddressLine1,
    string AddressLine2,
    string TimeZoneId,
    string LaborOfficeCode,
    bool IsHeadOffice,
    bool IsActive,
    // The owning legal entity's English legal name, resolved server-side so a list never has to
    // fall back to showing the raw CompanyId when the company is outside the caller's company list.
    string CompanyName = "");

public record BranchRequest(
    [Required] Guid CompanyId,
    [Required, MaxLength(40)] string Code,
    [Required, MaxLength(180)] string NameEn,
    [MaxLength(180)] string? NameAr,
    [Required, MaxLength(10)] string CountryCode,
    [Required, MaxLength(120)] string City,
    [MaxLength(240)] string? AddressLine1,
    [MaxLength(240)] string? AddressLine2,
    [Required, MaxLength(80)] string TimeZoneId,
    [MaxLength(80)] string? LaborOfficeCode,
    bool IsHeadOffice = false,
    bool IsActive = true);

public record DepartmentDto(
    Guid Id,
    Guid? BranchId,
    Guid? ParentDepartmentId,
    Guid? CostCenterId,
    string Code,
    string NameEn,
    string NameAr,
    int? ManagerEmployeeId,
    bool IsActive,
    Guid? CompanyId = null);

public record DepartmentRequest(
    Guid? BranchId,
    Guid? ParentDepartmentId,
    Guid? CostCenterId,
    [Required, MaxLength(40)] string Code,
    [Required, MaxLength(180)] string NameEn,
    [MaxLength(180)] string? NameAr,
    int? ManagerEmployeeId,
    bool IsActive = true,
    Guid? CompanyId = null,
    // Cost-accounting v1: the reason for clearing a cost-centre or parent link (recorded in the audit row).
    string? Reason = null);

public record DesignationDto(
    Guid Id,
    Guid? DepartmentId,
    string Code,
    string TitleEn,
    string TitleAr,
    string JobGrade,
    Guid? GradeId,
    string JobLevel,
    string JobDescription,
    bool IsManagerRole,
    bool IsActive,
    Guid? CompanyId = null);

public record DesignationRequest(
    Guid? DepartmentId,
    [Required, MaxLength(40)] string Code,
    [Required, MaxLength(180)] string TitleEn,
    [MaxLength(180)] string? TitleAr,
    [MaxLength(40)] string? JobGrade,
    Guid? GradeId,
    [MaxLength(80)] string? JobLevel,
    [MaxLength(2000)] string? JobDescription,
    bool IsManagerRole = false,
    bool IsActive = true,
    Guid? CompanyId = null);

public record GradeDto(
    Guid Id, string Code, string Name, string Band, int Level,
    decimal MinSalary, decimal MidSalary, decimal MaxSalary, string Currency,
    bool IsActive, string? NameAr = null, Guid? CompanyId = null);

public record GradeRequest(
    [Required, MaxLength(40)] string Code,
    [Required, MaxLength(120)] string Name,
    [MaxLength(80)] string? Band,
    [Range(0, 100)] int Level,
    [Range(0, 100_000_000)] decimal MinSalary = 0,
    [Range(0, 100_000_000)] decimal MidSalary = 0,
    [Range(0, 100_000_000)] decimal MaxSalary = 0,
    [MaxLength(8)] string? Currency = "SAR",
    bool IsActive = true,
    [MaxLength(120)] string? NameAr = null,
    Guid? CompanyId = null);

public record GradePayScaleComponentDto(
    Guid Id, string ComponentCode, string ComponentName, string ComponentType,
    string CalculationType, decimal Amount, decimal Percentage, bool IsTaxable,
    string Frequency, int SortOrder, bool IsActive);

/// <summary>One line of a grade's pay package. <paramref name="ComponentCode"/> names a component of the grade company's pay
/// component catalogue; name, type and taxable are taken from that catalogue row when saved. A row with a blank code is an
/// unfinished editor row and is ignored.</summary>
public record GradePayScaleComponentRequest(
    [MaxLength(40)] string? ComponentCode,
    [MaxLength(120)] string? ComponentName = null,
    [MaxLength(20)] string ComponentType = "Earning",
    [MaxLength(20)] string CalculationType = "Fixed",
    [Range(0, 100_000_000)] decimal Amount = 0,
    [Range(0, 100)] decimal Percentage = 0,
    bool IsTaxable = false,
    [MaxLength(20)] string Frequency = "Monthly",
    int SortOrder = 0,
    bool IsActive = true);

public record CostCenterDto(Guid Id, Guid? CompanyId, string Code, string Name, bool IsActive, string? NameAr = null,
    Guid? ParentId = null, string? FunctionalClass = null, string? GlSegment = null,
    CostCentreInUse? InUse = null, int InUseTotal = 0, bool CodeLocked = false);

/// <summary>Live references to a cost centre, per referencing table (M-8), and the posted ledger lines that freeze its code (M-9; not a live reference).</summary>
public sealed record CostCentreInUse(int Departments, int Positions, int Employees, int Mappings, int CompanyDefault, int Children, int PostedLedgerLines);

public record CostCenterRequest(
    Guid? CompanyId,
    [Required, MaxLength(40)] string Code,
    [Required, MaxLength(160)] string Name,
    bool IsActive = true,
    [MaxLength(160)] string? NameAr = null,
    // Cost-accounting v1. On an update, null leaves the stored value alone; Guid.Empty / "" clears it.
    Guid? ParentId = null,
    [MaxLength(24)] string? FunctionalClass = null,
    [MaxLength(30)] string? GlSegment = null,
    // Required when the functional class changes (M-10); recorded in the audit row.
    string? Reason = null);

public static class OrganizationMappings
{
    public static CompanyDto ToDto(this Company company) => new(
        company.Id,
        company.LegalNameEn,
        company.LegalNameAr,
        company.TradeName,
        company.CountryCode,
        company.Jurisdiction,
        company.RegistrationNumber,
        company.TaxNumber,
        company.WpsEmployerId,
        company.GosiEmployerId,
        company.QiwaEstablishmentId,
        company.DefaultCurrency,
        company.IsActive,
        company.ApprovalStatus,
        company.EmailDomain,
        company.WorkEmailPattern,
        company.ErpCompanyCode ?? string.Empty,
        company.UnifiedNationalNumber ?? string.Empty);

    public static BranchDto ToDto(this Branch branch) => new(
        branch.Id,
        branch.CompanyId,
        branch.Code,
        branch.NameEn,
        branch.NameAr,
        branch.CountryCode,
        branch.City,
        branch.AddressLine1,
        branch.AddressLine2,
        branch.TimeZoneId,
        branch.LaborOfficeCode,
        branch.IsHeadOffice,
        branch.IsActive);

    public static DepartmentDto ToDto(this Department department) => new(
        department.Id,
        department.BranchId,
        department.ParentDepartmentId,
        department.CostCenterId,
        department.Code,
        department.NameEn,
        department.NameAr,
        department.ManagerEmployeeId,
        department.IsActive,
        department.CompanyId);

    public static DesignationDto ToDto(this Designation designation) => new(
        designation.Id,
        designation.DepartmentId,
        designation.Code,
        designation.TitleEn,
        designation.TitleAr,
        designation.JobGrade,
        designation.GradeId,
        designation.JobLevel,
        designation.JobDescription,
        designation.IsManagerRole,
        designation.IsActive,
        designation.CompanyId);

    public static GradeDto ToDto(this Grade grade) => new(
        grade.Id, grade.Code, grade.Name, grade.Band, grade.Level,
        grade.MinSalary, grade.MidSalary, grade.MaxSalary, grade.Currency,
        grade.IsActive, grade.NameAr, grade.CompanyId);

    public static GradePayScaleComponentDto ToDto(this GradePayScaleComponent c) => new(
        c.Id, c.ComponentCode, c.ComponentName, c.ComponentType, c.CalculationType,
        c.Amount, c.Percentage, c.IsTaxable, c.Frequency, c.SortOrder, c.IsActive);

    public static CostCenterDto ToDto(this CostCenter costCenter) => new(costCenter.Id, costCenter.CompanyId, costCenter.Code, costCenter.Name, costCenter.IsActive, costCenter.NameAr,
        costCenter.ParentId, costCenter.FunctionalClass, costCenter.GlSegment);
}
