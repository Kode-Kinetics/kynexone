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
    public int MfaFailedCount { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public Guid? DeletedBy { get; set; }
    public DateTime? LastLoginAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
    public ICollection<PasswordResetToken> PasswordResetTokens { get; set; } = new List<PasswordResetToken>();
    public ICollection<Zayra.Api.Models.EmployeeUserAccount> EmployeeUserAccounts { get; set; } = new List<Zayra.Api.Models.EmployeeUserAccount>();
    public ICollection<Zayra.Api.Models.UserPermissionOverride> PermissionOverrides { get; set; } = new List<Zayra.Api.Models.UserPermissionOverride>();
    public ICollection<Zayra.Api.Models.UserEntityAccess> EntityAccesses { get; set; } = new List<Zayra.Api.Models.UserEntityAccess>();
}
