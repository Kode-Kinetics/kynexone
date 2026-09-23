using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Defines the ordered approval steps, approver rules, amount thresholds and service levels for one request type, optionally narrowed to a company. @tier:T @owner:HR @retention:tenant-lifecycle
/// </summary>
public partial class ApprovalWorkflow
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid? CompanyId { get; set; }

    public string RequestType { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string Steps { get; set; } = null!;

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public virtual ICollection<ApprovalRequest> ApprovalRequests { get; set; } = new List<ApprovalRequest>();

    public virtual Company? Company { get; set; }
}
