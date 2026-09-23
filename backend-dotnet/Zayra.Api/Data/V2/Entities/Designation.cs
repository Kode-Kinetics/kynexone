using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Job titles in English and Arabic with the MHRSD/GOSI occupation code that Saudization-restricted jobs and GOSI registration require. @tier:T @owner:HR
/// </summary>
public partial class Designation
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string? OccupationCode { get; set; }

    public string TitleEn { get; set; } = null!;

    public string? TitleAr { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public virtual ICollection<EmployeeAssignment> EmployeeAssignments { get; set; } = new List<EmployeeAssignment>();

    public virtual Tenant Tenant { get; set; } = null!;
}
