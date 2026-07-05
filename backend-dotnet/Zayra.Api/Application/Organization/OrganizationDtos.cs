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
    string ApprovalStatus = CompanyApprovalStatuses.Active);

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
    bool IsActive = true);

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
    bool IsActive);

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
    bool IsActive);

public record DepartmentRequest(
    Guid? BranchId,
    Guid? ParentDepartmentId,
    Guid? CostCenterId,
    [Required, MaxLength(40)] string Code,
    [Required, MaxLength(180)] string NameEn,
    [MaxLength(180)] string? NameAr,
    int? ManagerEmployeeId,
    bool IsActive = true);

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
    bool IsActive);

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
    bool IsActive = true);

public record GradeDto(
    Guid Id, string Code, string Name, string Band, int Level,
    decimal MinSalary, decimal MidSalary, decimal MaxSalary, string Currency,
    bool IsActive);

public record GradeRequest(
    [Required, MaxLength(40)] string Code,
    [Required, MaxLength(120)] string Name,
    [MaxLength(80)] string? Band,
    [Range(0, 100)] int Level,
    [Range(0, 100_000_000)] decimal MinSalary = 0,
    [Range(0, 100_000_000)] decimal MidSalary = 0,
    [Range(0, 100_000_000)] decimal MaxSalary = 0,
    [MaxLength(8)] string? Currency = "SAR",
    bool IsActive = true);

public record GradePayScaleComponentDto(
    Guid Id, string ComponentCode, string ComponentName, string ComponentType,
    string CalculationType, decimal Amount, decimal Percentage, bool IsTaxable,
    string Frequency, int SortOrder, bool IsActive);

public record GradePayScaleComponentRequest(
    [Required, MaxLength(40)] string ComponentCode,
    [Required, MaxLength(120)] string ComponentName,
    [MaxLength(20)] string ComponentType = "Earning",
    [MaxLength(20)] string CalculationType = "Fixed",
    [Range(0, 100_000_000)] decimal Amount = 0,
    [Range(0, 100)] decimal Percentage = 0,
    bool IsTaxable = false,
    [MaxLength(20)] string Frequency = "Monthly",
    int SortOrder = 0,
    bool IsActive = true);

public record CostCenterDto(Guid Id, Guid? CompanyId, string Code, string Name, bool IsActive);

public record CostCenterRequest(
    Guid? CompanyId,
    [Required, MaxLength(40)] string Code,
    [Required, MaxLength(160)] string Name,
    bool IsActive = true);

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
        company.ApprovalStatus);

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
        department.IsActive);

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
        designation.IsActive);

    public static GradeDto ToDto(this Grade grade) => new(
        grade.Id, grade.Code, grade.Name, grade.Band, grade.Level,
        grade.MinSalary, grade.MidSalary, grade.MaxSalary, grade.Currency,
        grade.IsActive);

    public static GradePayScaleComponentDto ToDto(this GradePayScaleComponent c) => new(
        c.Id, c.ComponentCode, c.ComponentName, c.ComponentType, c.CalculationType,
        c.Amount, c.Percentage, c.IsTaxable, c.Frequency, c.SortOrder, c.IsActive);

    public static CostCenterDto ToDto(this CostCenter costCenter) => new(costCenter.Id, costCenter.CompanyId, costCenter.Code, costCenter.Name, costCenter.IsActive);
}
