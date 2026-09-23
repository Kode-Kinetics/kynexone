using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// The costing dimension shared by GL export, timesheets, payroll inputs and slip lines, carrying the segment the ERP&apos;s chart of accounts expects. @tier:C @owner:Finance
/// </summary>
public partial class CostCenter
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CompanyId { get; set; }

    public Guid? ParentId { get; set; }

    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string? GlSegment { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public virtual Company Company { get; set; } = null!;

    public virtual CostCenter? CostCenterNavigation { get; set; }

    public virtual ICollection<Department> Departments { get; set; } = new List<Department>();

    public virtual ICollection<EmployeeAssignment> EmployeeAssignments { get; set; } = new List<EmployeeAssignment>();

    public virtual ICollection<GlJournalLine> GlJournalLines { get; set; } = new List<GlJournalLine>();

    public virtual ICollection<GlMapping> GlMappings { get; set; } = new List<GlMapping>();

    public virtual ICollection<CostCenter> InverseCostCenterNavigation { get; set; } = new List<CostCenter>();

    public virtual ICollection<PayrollInput> PayrollInputs { get; set; } = new List<PayrollInput>();

    public virtual ICollection<PayrollSlipLine> PayrollSlipLines { get; set; } = new List<PayrollSlipLine>();
}
