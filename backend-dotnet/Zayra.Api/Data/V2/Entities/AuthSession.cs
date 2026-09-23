using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// One row per signed-in device for either subject kind, carrying the rotated refresh token, the previous hash for reuse detection and the push registration, so re-login never loses a device. @tier:T/P @owner:Platform @retention:1-month-after-Expiry-then-Purge
/// </summary>
public partial class AuthSession
{
    public Guid Id { get; set; }

    /// <summary>
    /// NULL only for subject_kind=&apos;Platform&apos;. One of exactly two nullable-tenant client-adjacent tables; policed by the hand-written p_auth policy (§19.2).
    /// </summary>
    public Guid? TenantId { get; set; }

    public Guid? UserId { get; set; }

    public Guid? PlatformUserId { get; set; }

    public string DeviceId { get; set; } = null!;

    public string SubjectKind { get; set; } = null!;

    public string RefreshTokenHash { get; set; } = null!;

    /// <summary>
    /// Presented-again detection: a refresh with this hash means the token was replayed, and the whole session is revoked.
    /// </summary>
    public string? PreviousTokenHash { get; set; }

    public string? PushToken { get; set; }

    public string? PushPlatform { get; set; }

    public string? Ip { get; set; }

    public string? UserAgent { get; set; }

    public DateTime? LastSeenAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime? RevokedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public virtual PlatformUser? PlatformUser { get; set; }

    public virtual User? User { get; set; }
}
