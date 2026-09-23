using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Single-use, expiring tokens for password reset, MFA challenge, invitation and email confirmation, for either subject kind, with an attempt counter that supports lockout. @tier:T/P @owner:Platform @retention:1-month-after-Expiry-then-Purge
/// </summary>
public partial class AuthToken
{
    public Guid Id { get; set; }

    public Guid? TenantId { get; set; }

    public Guid? UserId { get; set; }

    public Guid? PlatformUserId { get; set; }

    /// <summary>
    /// Secret column: REVOKE from kynex_ro by column privilege (§19.2). Lowercase hex; the plaintext token is never stored.
    /// </summary>
    public string TokenHash { get; set; } = null!;

    public string SubjectKind { get; set; } = null!;

    public string Purpose { get; set; } = null!;

    public int Attempts { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime? ConsumedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}
