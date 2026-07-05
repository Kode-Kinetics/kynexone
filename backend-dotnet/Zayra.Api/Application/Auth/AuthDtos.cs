using System.ComponentModel.DataAnnotations;

namespace Zayra.Api.Application.Auth;

public record LoginRequest(
    [Required, EmailAddress] string Email,
    [Required] string Password,
    string? TenantSlug);

public record RefreshTokenRequest([Required] string RefreshToken);

public record LogoutRequest([Required] string RefreshToken);

public record ForgotPasswordRequest([Required, EmailAddress] string Email, string? TenantSlug);

public record ResetPasswordRequest(
    [Required, EmailAddress] string Email,
    [Required] string ResetToken,
    [Required, MinLength(10)] string NewPassword,
    string? TenantSlug);

public record AcceptInvitationRequest(
    [Required, EmailAddress] string Email,
    [Required] string InvitationToken,
    [Required, MinLength(10)] string NewPassword,
    string? TenantSlug);

public record CreateUserRequest(
    [Required, EmailAddress] string Email,
    [Required] string FullName,
    [Required, MinLength(10)] string Password,
    IReadOnlyCollection<string> Roles);

public record AssignRolesRequest([Required] IReadOnlyCollection<string> Roles);

public record InviteEmployeeLoginRequest(
    [Required] int EmployeeId,
    string? Email,
    [Required] string AccessMode,
    IReadOnlyCollection<string>? Roles,
    int InvitationHours = 72);

public record EmployeeLoginInvitationDto(
    Guid UserId,
    int EmployeeId,
    string Email,
    string AccessMode,
    string Status,
    string InvitationToken,
    DateTime? InvitationExpiresAtUtc);

public record AccessModeRequest([Required] string AccessMode, string? Reason);

public record PermissionOverrideRequest([Required] string PermissionKey, [Required] string Effect, string? Reason, DateTime? ExpiresAtUtc);

public record ApprovalDelegationRequest(
    [Required] int FromEmployeeId,
    [Required] int ToEmployeeId,
    [Required] string Scope,
    DateOnly StartDate,
    DateOnly EndDate,
    string? Reason);

public record ApprovalDelegationDto(
    Guid Id,
    int FromEmployeeId,
    int ToEmployeeId,
    Guid? FromUserId,
    Guid? ToUserId,
    string Scope,
    DateOnly StartDate,
    DateOnly EndDate,
    string Status,
    string Reason);

public record ApprovalAuthorityRequest(
    [Required] int EmployeeId,
    [Required] string AuthorityScope,
    [Required] string ApproverRole,
    decimal? AmountLimit,
    string? Currency,
    bool CanFinalApprove);

public record ApprovalAuthorityDto(
    Guid Id,
    int EmployeeId,
    Guid? UserId,
    string AuthorityScope,
    string ApproverRole,
    decimal? AmountLimit,
    string Currency,
    bool CanFinalApprove,
    bool IsActive);

public record UserAccessDto(
    Guid UserId,
    int? EmployeeId,
    string Email,
    string FullName,
    string AccessMode,
    bool RequiresPasswordSetup,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<string> Permissions,
    IReadOnlyCollection<string> DeniedPermissions);

public record AuthResponse(
    string AccessToken,
    string RefreshToken,
    DateTime ExpiresAtUtc,
    AuthUserDto User);

public record CompanyAccessDto(Guid Id, string Name, string Code, string CountryCode, bool IsActive);

public record AuthUserDto(
    Guid Id,
    Guid TenantId,
    string TenantSlug,
    string Email,
    string FullName,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<string> Permissions,
    int? EmployeeId = null,
    string AccessMode = "FullPortal",
    bool RequiresPasswordSetup = false,
    // Company-scope capability payload (final batch): drives the frontend company switcher.
    string AccountType = "SingleCompany",
    bool IsGroupScope = false,
    IReadOnlyCollection<CompanyAccessDto>? Companies = null);

public record ForgotPasswordResponse(string Message, string? ResetToken, DateTime? ResetTokenExpiresAtUtc);

public record RoleDto(
    Guid Id,
    string Name,
    string Description,
    bool IsSystem,
    bool IsActive,
    bool IsEditable,
    int AuthorityLevel,
    IReadOnlyCollection<string> Permissions);

public record CreateRoleRequest(
    [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.MaxLength(80)] string Name,
    [System.ComponentModel.DataAnnotations.MaxLength(240)] string? Description,
    int AuthorityLevel = 99,
    IReadOnlyCollection<string>? Permissions = null);

public record UpdateRoleRequest(
    [System.ComponentModel.DataAnnotations.MaxLength(80)] string? Name,
    [System.ComponentModel.DataAnnotations.MaxLength(240)] string? Description,
    int? AuthorityLevel);

public record BulkRolePermissionsRequest([System.ComponentModel.DataAnnotations.Required] IReadOnlyCollection<string> Permissions);

public record PermissionMatrixRow(string PermissionKey, string Module, string Description, Dictionary<string, bool> Roles);

public record PermissionMatrixDto(IReadOnlyCollection<RoleDto> Roles, IReadOnlyCollection<PermissionMatrixRow> Matrix);

public record PermissionMatrixUpdateRequest([System.ComponentModel.DataAnnotations.Required] Dictionary<string, IReadOnlyCollection<string>> RolePermissions);

public record EffectivePermissionsDto(
    Guid UserId,
    string Email,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<string> GrantedByRole,
    IReadOnlyCollection<string> ExplicitlyAllowed,
    IReadOnlyCollection<string> ExplicitlyDenied,
    IReadOnlyCollection<string> Effective);

public record PermissionDto(Guid Id, string Key, string Module, string Description);

public record UserListDto(
    Guid Id,
    string Email,
    string FullName,
    string PhoneNumber,
    string Status,
    bool IsActive,
    bool IsLocked,
    bool MustChangePassword,
    IReadOnlyCollection<string> Roles,
    string AccessMode,
    int? EmployeeId,
    DateTime? LastLoginAtUtc,
    DateTime CreatedAtUtc);

public record UpdateUserRequest(
    string? FullName,
    string? PhoneNumber,
    string? PreferredLanguage,
    string? Timezone);

public record ChangePasswordRequest(
    [System.ComponentModel.DataAnnotations.Required] string CurrentPassword,
    [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.MinLength(10)] string NewPassword);

public record AdminResetPasswordRequest(
    [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.MinLength(10)] string NewPassword,
    bool MustChangePassword = true);

public record UserListQuery(string? Search, string? Status, string? Role, int Page = 1, int PageSize = 30);

public record SecuritySettingDto(
    Guid Id,
    Guid TenantId,
    int PasswordMinLength,
    bool PasswordRequireUppercase,
    bool PasswordRequireLowercase,
    bool PasswordRequireDigit,
    bool PasswordRequireSpecial,
    int PasswordExpiryDays,
    int PasswordHistoryCount,
    int MaxFailedLoginAttempts,
    int LockoutDurationMinutes,
    int SessionTimeoutMinutes,
    int RefreshTokenExpiryDays,
    bool AllowMultipleSessions,
    bool MfaRequired,
    DateTime UpdatedAtUtc);

// ── MFA DTOs ──────────────────────────────────────────────────────────────────

/// <summary>Issued after password validates when the user has MFA enabled.
/// The client must POST this token + TOTP code to /api/auth/mfa/challenge/verify to obtain full tokens.</summary>
public record MfaChallengeDto(string ChallengeToken, int ExpiresInSeconds);

/// <summary>Result from LoginAsync — one of: Tokens (success), Challenge (MFA code needed),
/// or RequiresMfaEnrollment (tenant mandates MFA but this user hasn't set it up yet).</summary>
public record AuthLoginResult(AuthResponse? Tokens, MfaChallengeDto? Challenge, bool RequiresMfaEnrollment = false)
{
    public bool RequiresMfa => Challenge is not null;
}

/// <summary>Temporary secret + provisioning URI returned during TOTP setup.
/// The provisioning URI contains the base32 secret embedded and is safe to return once.
/// After setup confirmation neither the secret nor URI is ever returned again.</summary>
public record MfaSetupInitResponse(string ProvisioningUri);

public record MfaVerifySetupRequest(
    [System.ComponentModel.DataAnnotations.Required] string TempSecret,
    [System.ComponentModel.DataAnnotations.Required] string TotpCode);

public record MfaChallengeVerifyRequest(
    [System.ComponentModel.DataAnnotations.Required] string ChallengeToken,
    [System.ComponentModel.DataAnnotations.Required] string TotpCode);

public record MfaDisableRequest(
    [System.ComponentModel.DataAnnotations.Required] string TotpCode);

public record ReasonRequest(string? Reason);

public record PermissionGrantorDto(
    Guid Id,
    Guid GrantorUserId,
    string GrantorEmail,
    string GrantorName,
    string PermissionScope,
    bool CanSubDelegate,
    Guid? GrantedByUserId,
    DateTime? ExpiresAtUtc,
    bool IsActive,
    string Reason,
    DateTime CreatedAtUtc);

public record AddGrantorRequest(
    [System.ComponentModel.DataAnnotations.Required] Guid GrantorUserId,
    // "all" | module e.g. "leave" | comma-separated keys
    [System.ComponentModel.DataAnnotations.Required] string PermissionScope,
    bool CanSubDelegate = false,
    DateTime? ExpiresAtUtc = null,
    string? Reason = null);

public record GrantPermissionRequest(
    [System.ComponentModel.DataAnnotations.Required] string PermissionKey,
    [System.ComponentModel.DataAnnotations.Required] string Effect,  // "Allow" | "Deny" | "Remove"
    string? Reason = null,
    DateTime? ExpiresAtUtc = null);

public record UpdateSecuritySettingRequest(
    int? PasswordMinLength,
    bool? PasswordRequireUppercase,
    bool? PasswordRequireLowercase,
    bool? PasswordRequireDigit,
    bool? PasswordRequireSpecial,
    int? PasswordExpiryDays,
    int? PasswordHistoryCount,
    int? MaxFailedLoginAttempts,
    int? LockoutDurationMinutes,
    int? SessionTimeoutMinutes,
    int? RefreshTokenExpiryDays,
    bool? AllowMultipleSessions,
    bool? MfaRequired = null);
