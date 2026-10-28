namespace Zayra.Api.Domain.Entities;

public class User : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }
    public string Email { get; set; } = string.Empty;
    public string NormalizedEmail { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public string PreferredLanguage { get; set; } = "en";
    public string Timezone { get; set; } = "UTC";
    public string ExternalId { get; set; } = string.Empty;
    public string IdentityProvider { get; set; } = "Local";
    public string ProvisioningSource { get; set; } = "Local";
    public DateTime? LastProvisionedAtUtc { get; set; }
    // Status: Active | Invited | Suspended | Locked | Deactivated | PendingPasswordSetup | PasswordResetRequired
    public string Status { get; set; } = "Active";
    // AccessMode for direct (non-employee) users — employee-linked users use EmployeeUserAccount.AccessMode
    public string AccessMode { get; set; } = "FullPortal";
    public bool IsActive { get; set; } = true;
    public bool IsEmailConfirmed { get; set; } = true;
    public bool IsLocked { get; set; }
    public DateTime? LockoutEnd { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTime? LastPasswordChangedAt { get; set; }
    public bool MustChangePassword { get; set; }

    /// <summary>
    /// The workspace OWNER: at most one per tenant (ux_users__one_workspace_owner). No other Admin may suspend, lock,
    /// delete, demote, set to no-login, or reset the password or two-step sign-in of the owner (access_owner_protected).
    /// Only the owner hands it on (AccessManagementService.TransferOwnershipAsync); KynexOne support sets it when the
    /// owner is gone. No row set means no owner designated, and nobody is protected.
    /// </summary>
    public bool IsWorkspaceOwner { get; set; }
    // True when this user has group-level (cross-company) access within their tenant.
    // Derives from explicit is_group_scope=true JWT claim (post-migration) or is used
    // to elevate scoped users by an Admin via the AccessController.SetGroupScope endpoint.
    public bool IsGroupScope { get; set; }
    public bool MFAEnabled { get; set; }
    // TOTP-specific fields — secrets are always stored encrypted via IDataProtector.
    // These are never returned in any API response; read them only inside MfaService.
    public string? MfaSecretEncrypted { get; set; }
    public DateTime? MfaConfiguredAtUtc { get; set; }
    public DateTime? MfaLastVerifiedAtUtc { get; set; }
    /// <summary>Last accepted TOTP time-step; any code at or below it is refused (replay protection).</summary>
    public long? MfaLastTotpStep { get; set; }
    public int MfaFailedCount { get; set; }
    /// <summary>
    /// The unused one-time backup codes of the current factor, as one salted PBKDF2 set (<see cref="Zayra.Api.Infrastructure.Auth.MfaBackupCodes"/>);
    /// never the codes. NULL when none is left or two-step is off (CHECK: never without a factor). Cleared with the factor
    /// by every path (the ZayraDbContext save hook catches the ones that do not do it themselves).
    /// </summary>
    public string? MfaRecoveryCodeHashes { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public Guid? DeletedBy { get; set; }
    public DateTime? LastLoginAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
    /// <summary>
    /// Every session of this login was issued at some epoch; a refresh is refused once this has moved past it. Raised
    /// (never set) by the ZayraDbContext save hook when the password, the factor or the login's active state changes,
    /// and by <c>TenantSessionSecurity.EndAllSessions</c>; a database trigger makes every raise exactly one step above
    /// the stored row. Unlike <see cref="UpdatedAtUtc"/> (the access-token stamp) nothing routine moves it.
    /// </summary>
    public long SessionEpoch { get; set; }
    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
    public ICollection<PasswordResetToken> PasswordResetTokens { get; set; } = new List<PasswordResetToken>();
    public ICollection<Zayra.Api.Models.EmployeeUserAccount> EmployeeUserAccounts { get; set; } = new List<Zayra.Api.Models.EmployeeUserAccount>();
    public ICollection<Zayra.Api.Models.UserPermissionOverride> PermissionOverrides { get; set; } = new List<Zayra.Api.Models.UserPermissionOverride>();
    public ICollection<Zayra.Api.Models.UserEntityAccess> EntityAccesses { get; set; } = new List<Zayra.Api.Models.UserEntityAccess>();
}
