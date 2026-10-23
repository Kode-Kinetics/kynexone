namespace Zayra.Api.Domain.Entities;

public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>
    /// Stable identifier for one login session's refresh-token lineage. A newly authenticated
    /// session starts a family and every rotation inherits it. Reuse of any consumed ancestor can
    /// therefore revoke only the compromised lineage without terminating unrelated sessions.
    /// </summary>
    public Guid FamilyId { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>
    /// When the sign-in that started this lineage happened. Set at issue (login, two-step, the session re-issued after
    /// signed-in two-step setup) and copied unchanged by every rotation, so <see cref="CreatedAtUtc"/> measures idleness
    /// and this measures the session's total length against <c>security_settings.session_max_hours</c>.
    /// </summary>
    public DateTime SessionStartedAtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>
    /// The user's <c>session_epoch</c> when this lineage was issued, copied unchanged by every rotation. A refresh is
    /// refused (and the family revoked) once the user's epoch has moved on: see <c>TenantSessionSecurity.EndAllSessions</c>.
    /// </summary>
    public long SessionEpoch { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public string? ReplacedByTokenHash { get; set; }
    public string? CreatedByIp { get; set; }
    public string? RevokedByIp { get; set; }
    public bool IsActive => RevokedAtUtc is null && DateTime.UtcNow < ExpiresAtUtc;
}
