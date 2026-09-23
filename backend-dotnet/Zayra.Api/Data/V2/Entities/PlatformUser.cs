using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Platform operators and their credentials, kept outside tenancy entirely so the operator surface is policed by grant rather than by a tenant filter. @tier:P @owner:Platform @retention:12-months-after-SoftDelete-then-Anonymise
/// </summary>
public partial class PlatformUser
{
    public Guid Id { get; set; }

    /// <summary>
    /// Globally unique (no tenant to scope it by). Stored normalised by the application; see 001_extensions.sql on the citext question.
    /// </summary>
    public string Email { get; set; } = null!;

    public string Status { get; set; } = null!;

    public string PlatformRole { get; set; } = null!;

    public string FullName { get; set; } = null!;

    /// <summary>
    /// Secret column: REVOKE from kynex_ro by column privilege (§19.2).
    /// </summary>
    public string? PasswordHash { get; set; }

    public bool MfaEnabled { get; set; }

    public string? MfaSecretEncrypted { get; set; }

    public string? MfaRecoveryHashes { get; set; }

    public int FailedLoginCount { get; set; }

    public DateTime? LockoutEnd { get; set; }

    public DateTime? LastLoginAt { get; set; }

    public DateTime? DeletedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public virtual ICollection<AuthSession> AuthSessions { get; set; } = new List<AuthSession>();

    public virtual ICollection<AuthToken> AuthTokens { get; set; } = new List<AuthToken>();
}
