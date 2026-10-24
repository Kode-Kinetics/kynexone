using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

// ── Tenant AI Usage ───────────────────────────────────────────────────────────

public class TenantAiUsage : ITenantOwned
{
    public Guid TenantId { get; set; }
    public int YearMonth { get; set; } // YYYYMM
    public long TokensUsed { get; set; }
    public int RequestCount { get; set; }
    public int BlockedCount { get; set; }
    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
}
