using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

public class EmployeeChangeRequest : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }
    public Guid? RequestedByUserId { get; set; }
    public string Status { get; set; } = "PendingApproval";
    public bool RequiresApproval { get; set; } = true;
    public DateOnly EffectiveDate { get; set; }
    public string SensitiveFields { get; set; } = string.Empty;
    public string ProposedChangesJson { get; set; } = "{}";
    public Guid? ApprovalRequestId { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ApprovedAtUtc { get; set; }
    public DateTime? AppliedAtUtc { get; set; }
    public string RejectionReason { get; set; } = string.Empty;
}
