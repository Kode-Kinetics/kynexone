using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

public class ApprovalDecision : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ApprovalRequestId { get; set; }
    public int StepOrder { get; set; }
    /// <summary>W2-E — the <see cref="ApprovalRequest.SubmissionRound"/> this decision belongs to.</summary>
    public int SubmissionRound { get; set; } = 1;
    public string Decision { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
    public Guid? DecidedByUserId { get; set; }
    public DateTime DecidedAtUtc { get; set; } = DateTime.UtcNow;
}
