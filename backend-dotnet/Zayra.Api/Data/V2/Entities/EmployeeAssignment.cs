using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// The effective-dated record of where a person sat — company, branch, department, designation, grade, manager and cost centre — and the single authority for whether they were employed on any given date. @tier:C @owner:HR @retention:Keep
/// </summary>
public partial class EmployeeAssignment
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CompanyId { get; set; }

    public Guid EmployeeId { get; set; }

    public Guid BranchId { get; set; }

    public Guid DepartmentId { get; set; }

    public Guid DesignationId { get; set; }

    public Guid? GradeId { get; set; }

    public Guid? ManagerEmployeeId { get; set; }

    /// <summary>
    /// Optional in the schema on purpose: a company that posts GL by cost centre gets a payroll_issues BLOCK at calculation instead of a NOT NULL that stops HR saving an employee (§8 row 40).
    /// </summary>
    public Guid? CostCenterId { get; set; }

    /// <summary>
    /// The decision behind the change. The row has no status of its own: the approval is its only state (§11.1).
    /// </summary>
    public Guid? ApprovalRequestId { get; set; }

    public string? EmploymentStatus { get; set; }

    public string? PayGroup { get; set; }

    public DateOnly EffectiveFrom { get; set; }

    public DateOnly? EffectiveTo { get; set; }

    public string? ChangeReason { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}
