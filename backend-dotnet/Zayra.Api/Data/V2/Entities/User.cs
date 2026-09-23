using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Every tenant-side login — staff and employee self-service — with its credential, MFA and lockout state, optionally bound one-to-one to an employee record. @tier:T @owner:Platform @retention:soft-delete-only
/// </summary>
public partial class User
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid? EmployeeId { get; set; }

    /// <summary>
    /// Unique per tenant, NOT globally: two tenants may legitimately share an address, which is why app.resolve_login() is keyed on (tenant, email) (§19.2 bypass surface 1).
    /// </summary>
    public string NormalizedEmail { get; set; } = null!;

    public string Status { get; set; } = null!;

    /// <summary>
    /// Secret column: REVOKE from kynex_ro by column privilege (§19.2). NULL until an invitation is consumed.
    /// </summary>
    public string? PasswordHash { get; set; }

    public bool MfaEnabled { get; set; }

    public string? MfaSecretEncrypted { get; set; }

    public string? MfaRecoveryHashes { get; set; }

    public int FailedLoginCount { get; set; }

    public DateTime? LockoutEnd { get; set; }

    public string NotificationPrefs { get; set; } = null!;

    public DateTime? LastLoginAt { get; set; }

    public DateTime? DeletedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public virtual ICollection<ApprovalAction> ApprovalActionUserNavigations { get; set; } = new List<ApprovalAction>();

    public virtual ICollection<ApprovalAction> ApprovalActionUsers { get; set; } = new List<ApprovalAction>();

    public virtual ICollection<ApprovalDelegation> ApprovalDelegationUserNavigations { get; set; } = new List<ApprovalDelegation>();

    public virtual ICollection<ApprovalDelegation> ApprovalDelegationUsers { get; set; } = new List<ApprovalDelegation>();

    public virtual ICollection<ApprovalRequest> ApprovalRequests { get; set; } = new List<ApprovalRequest>();

    public virtual ICollection<AuthSession> AuthSessions { get; set; } = new List<AuthSession>();

    public virtual ICollection<AuthToken> AuthTokens { get; set; } = new List<AuthToken>();

    public virtual Employee? Employee { get; set; }

    public virtual ICollection<File> Files { get; set; } = new List<File>();

    public virtual ICollection<GlPeriodClose> GlPeriodCloseUserNavigations { get; set; } = new List<GlPeriodClose>();

    public virtual ICollection<GlPeriodClose> GlPeriodCloseUsers { get; set; } = new List<GlPeriodClose>();

    public virtual ICollection<GosiFiling> GosiFilings { get; set; } = new List<GosiFiling>();

    public virtual ICollection<Notification> Notifications { get; set; } = new List<Notification>();

    public virtual ICollection<PayrollIssue> PayrollIssues { get; set; } = new List<PayrollIssue>();

    public virtual ICollection<PermissionGrantorRecord> PermissionGrantorRecordUser1s { get; set; } = new List<PermissionGrantorRecord>();

    public virtual ICollection<PermissionGrantorRecord> PermissionGrantorRecordUserNavigations { get; set; } = new List<PermissionGrantorRecord>();

    public virtual ICollection<PermissionGrantorRecord> PermissionGrantorRecordUsers { get; set; } = new List<PermissionGrantorRecord>();

    public virtual Tenant Tenant { get; set; } = null!;

    public virtual ICollection<UserRole> UserRoleUserNavigations { get; set; } = new List<UserRole>();

    public virtual ICollection<UserRole> UserRoleUsers { get; set; } = new List<UserRole>();

    public virtual ICollection<WpsBatch> WpsBatches { get; set; } = new List<WpsBatch>();
}
