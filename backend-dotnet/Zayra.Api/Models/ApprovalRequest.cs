using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

public class ApprovalRequest : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid WorkflowId { get; set; }
    public string EntityName { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public int CurrentStepOrder { get; set; } = 1;
    /// <summary>
    /// Optimistic decision version. Every workflow decision increments this value, so two
    /// relational writers that observed the same pending step cannot both advance it.
    /// </summary>
    public int DecisionVersion { get; set; }
    public Guid? RequestedByUserId { get; set; }
    public int? RequestedForEmployeeId { get; set; }
    public Guid? CompanyId { get; set; }
    public int? CurrentApproverEmployeeId { get; set; }
    public Guid? CurrentApproverUserId { get; set; }
    public string CurrentApproverName { get; set; } = string.Empty;
    public string CurrentApproverRole { get; set; } = string.Empty;
    public string CurrentApproverType { get; set; } = string.Empty;
    public string CurrentQueue { get; set; } = string.Empty;
    public int SlaHours { get; set; } = 24;
    public DateTime? DueAtUtc { get; set; }
    public DateTime? LastRoutedAtUtc { get; set; }
    public DateTime? EscalatedAtUtc { get; set; }
    public string EscalatedToRole { get; set; } = string.Empty;
    public string Priority { get; set; } = "Normal";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
    public ICollection<ApprovalDecision> Decisions { get; set; } = new List<ApprovalDecision>();
}
