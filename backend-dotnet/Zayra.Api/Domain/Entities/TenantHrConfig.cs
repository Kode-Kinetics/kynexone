namespace Zayra.Api.Domain.Entities;

public class TenantHrConfig : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    // Eleven approval-routing / import / hierarchy / payroll switches were never read and are no longer mapped; their
    // columns stay in the database, with defaults, until a later release drops them.
    // Establishment matrix enforcement: Off | Advisory | Enforced. Default Enforced is
    // deploy-safe because enforcement is per-budget-row opt-in (absent row = uncontrolled);
    // changing this value is itself permission-gated + audited (never flipped by deploys).
    public string EstablishmentEnforcementMode { get; set; } = "Enforced";
    // Engagement surveys (ENG). The tenant's minimum cohort size k (floor 3, CHECK-enforced) and how long survey
    // comments are kept before the retention rule removes the text (numeric answers are kept).
    public int EngagementMinCohortSize { get; set; } = 5;
    public int EngagementFreeTextRetentionMonths { get; set; } = 24;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
