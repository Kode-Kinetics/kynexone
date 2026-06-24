using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

public static class AccessModes
{
    public const string FullPortal = "FullPortal";
    public const string EssOnly = "ESSOnly";
    public const string ManagerPortal = "ManagerPortal";
    public const string HRPortal = "HRPortal";
    public const string PayrollPortal = "PayrollPortal";
    public const string FinancePortal = "FinancePortal";
    public const string SupervisorPortal = "SupervisorPortal";
    public const string ReadOnlyAuditor = "ReadOnlyAuditor";
    public const string Mobile = "Mobile";
    public const string KioskOnly = "KioskOnly";
    public const string NoLogin = "NoLogin";
}

public class SecuritySetting : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    // Password policy
    public int PasswordMinLength { get; set; } = 10;
    public bool PasswordRequireUppercase { get; set; } = true;
    public bool PasswordRequireLowercase { get; set; } = true;
    public bool PasswordRequireDigit { get; set; } = true;
    public bool PasswordRequireSpecial { get; set; } = true;
    public int PasswordExpiryDays { get; set; } = 90;
    public int PasswordHistoryCount { get; set; } = 5;
    // Lockout policy
    public int MaxFailedLoginAttempts { get; set; } = 5;
    public int LockoutDurationMinutes { get; set; } = 30;
    // Session policy
    public int SessionTimeoutMinutes { get; set; } = 480;
    public int RefreshTokenExpiryDays { get; set; } = 30;
    public bool AllowMultipleSessions { get; set; } = true;
    // MFA policy — when true all tenant users must complete TOTP at login
    public bool MfaRequired { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? UpdatedBy { get; set; }
}

public class EmployeeUserAccount : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }
    public Guid? UserId { get; set; }
    public User? User { get; set; }
    public string AccessMode { get; set; } = AccessModes.EssOnly;
    public bool IsPrimary { get; set; } = true;
    public string Status { get; set; } = "Invited";
    public bool RequiresPasswordSetup { get; set; } = true;
    public string InvitationTokenHash { get; set; } = string.Empty;
    public DateTime? InvitationExpiresAtUtc { get; set; }
    public DateTime? InvitedAtUtc { get; set; }
    public DateTime? InvitationAcceptedAtUtc { get; set; }
    public string LoginDisabledReason { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public Guid? DeletedBy { get; set; }
}

public class UserPermissionOverride : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public string PermissionKey { get; set; } = string.Empty;
    public string Effect { get; set; } = "Allow";
    public string Reason { get; set; } = string.Empty;
    public DateTime? ExpiresAtUtc { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
}

public class ApprovalDelegation : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int FromEmployeeId { get; set; }
    public int ToEmployeeId { get; set; }
    public Guid? FromUserId { get; set; }
    public Guid? ToUserId { get; set; }
    public string Scope { get; set; } = "All";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string Status { get; set; } = "Active";
    public string Reason { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
}

public class ApprovalAuthority : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }
    public Guid? UserId { get; set; }
    public string AuthorityScope { get; set; } = string.Empty;
    public string ApproverRole { get; set; } = string.Empty;
    public decimal? AmountLimit { get; set; }
    public string Currency { get; set; } = string.Empty;
    public bool CanFinalApprove { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
}

// Tracks users who are authorised to manually grant/revoke individual permissions
public class PermissionGrantorRecord : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    // The user who is allowed to grant permissions
    public Guid GrantorUserId { get; set; }
    // "all" | module prefix e.g. "leave" | comma-separated keys e.g. "leave.read,leave.write"
    public string PermissionScope { get; set; } = "all";
    // Whether this grantor can further sub-delegate their granting authority
    public bool CanSubDelegate { get; set; }
    public Guid? GrantedByUserId { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public bool IsActive { get; set; } = true;
    public string Reason { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
}

/// <summary>
/// SAP/Workday-style Legal Entity access grant.
/// CompanyId null = group-level (all companies). CompanyId set = scoped to that company only.
/// </summary>
public class UserEntityAccess : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public Guid? CompanyId { get; set; }
    public Company? Company { get; set; }
    public string Role { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
    // Explicit grant tracking — who approved this access grant and when
    public Guid? GrantedBy { get; set; }
    public DateTime? GrantedAt { get; set; }
}
