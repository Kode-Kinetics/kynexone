using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

public partial class VEmployeeCurrent
{
    public Guid? TenantId { get; set; }

    public Guid? EmployeeId { get; set; }

    public string? EmployeeNumber { get; set; }

    public string? Status { get; set; }

    public string? NameEn { get; set; }

    public string? NameAr { get; set; }

    public string? WorkEmail { get; set; }

    public DateOnly? JoiningDate { get; set; }

    public DateOnly? SeparationDate { get; set; }

    public Guid? AssignmentId { get; set; }

    public Guid? CompanyId { get; set; }

    public Guid? BranchId { get; set; }

    public Guid? DepartmentId { get; set; }

    public Guid? DesignationId { get; set; }

    public Guid? GradeId { get; set; }

    public Guid? ManagerEmployeeId { get; set; }

    public Guid? CostCenterId { get; set; }

    public string? EmploymentStatus { get; set; }

    public string? PayGroup { get; set; }

    public DateOnly? AssignmentEffectiveFrom { get; set; }

    public Guid? SalaryId { get; set; }

    public decimal? Basic { get; set; }

    public decimal? Housing { get; set; }

    public decimal? Transport { get; set; }

    public bool? HousingInKind { get; set; }

    public DateOnly? SalaryEffectiveFrom { get; set; }

    public Guid? BankAccountId { get; set; }

    public string? Iban { get; set; }

    public string? BankCode { get; set; }

    public string? PaymentMethod { get; set; }
}
