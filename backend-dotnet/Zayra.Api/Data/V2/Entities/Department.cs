using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// The org-unit hierarchy within a legal entity, optionally pointing at the cost centre its salary cost posts to. @tier:C @owner:HR
/// </summary>
public partial class Department
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CompanyId { get; set; }

    public Guid? ParentId { get; set; }

    public Guid? CostCenterId { get; set; }

    public string Name { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public virtual Company Company { get; set; } = null!;

    public virtual CostCenter? CostCenter { get; set; }

    public virtual Department? DepartmentNavigation { get; set; }

    public virtual ICollection<EmployeeAssignment> EmployeeAssignments { get; set; } = new List<EmployeeAssignment>();

    public virtual ICollection<Department> InverseDepartmentNavigation { get; set; } = new List<Department>();

    public virtual ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
}
