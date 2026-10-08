using System.ComponentModel.DataAnnotations;

namespace Zayra.Api.Application.Auth;

[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
public sealed class RequiredWorkspaceAttribute : ValidationAttribute
{
    public RequiredWorkspaceAttribute() : base("Workspace is required.") { }

    public override bool IsValid(object? value) =>
        value is string workspace && !string.IsNullOrWhiteSpace(workspace);
}

/// <summary>TenantSlug is optional: without it the email's domain routes to a workspace on a unique match only,
/// otherwise the answer is 400 <c>{ code: "workspace_required" }</c> (contract §4).</summary>
public record LoginRequest(
    [Required, EmailAddress] string Email,
    [Required] string Password,
    string? TenantSlug = null);

public record RefreshTokenRequest([Required] string RefreshToken);

public record LogoutRequest([Required] string RefreshToken);

/// <summary>Without a workspace nothing is mailed: 400 <c>workspace_required</c> (F5).</summary>
public record ForgotPasswordRequest(
    [Required, EmailAddress] string Email,
    string? TenantSlug = null);

public record ResetPasswordRequest(
    [Required] string ResetToken,
    [Required, MinLength(10)] string NewPassword,
    [param: RequiredWorkspace] string TenantSlug);

public record AcceptInvitationRequest(
    [Required] string InvitationToken,
    [Required, MinLength(10)] string NewPassword,
    [param: RequiredWorkspace] string TenantSlug);

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
    int InvitationHours = 72,
    // Required (400 confirm_work_email) when the work email was changed after the record was created and the
    // employee has no activated login: the caller confirms they checked the address with the person.
    bool ConfirmedWorkEmail = false);

public record EmployeeLoginInvitationDto(
    Guid UserId,
    int EmployeeId,
    string Email,
    string AccessMode,
    string Status,
    string InvitationToken,
    DateTime? InvitationExpiresAtUtc,
    string InvitationUrl,
    // Delivery truth. The invitation link is minted whether or not a mail transport exists; these
    // three say whether anything was actually posted to the invitee, so no caller can render
    // "invited — tell them to check their inbox" over a workspace with no SMTP configured.
    bool EmailDeliveryConfigured = false,
    bool EmailSent = false,
    string DeliveryMessage = "")
{
    /// <summary>The caller set this employee's work email (WorkEmailSetterRule): the link is never emailed — it is
    /// returned to the caller to hand over in person, and that disclosure is recorded.</summary>
    public bool HandOverInPerson { get; init; }
}

/// <summary>A login as the employee-link screen shows it: who, what state, what access mode.</summary>
public record LinkedLoginDto(Guid UserId, string Email, string Status, string AccessMode, bool IsActive);

/// <summary>
/// Where one employee record stands on the way to Self-Service. <see cref="NextAction"/> is one of
/// <see cref="EmployeeLoginNextActions"/>; <see cref="Reason"/> says why in plain language when it is
/// <c>blocked</c> or <c>needs_work_email</c>.
/// </summary>
public record EmployeeLoginStatusDto(
    int EmployeeId,
    string EmployeeName,
    string WorkEmail,
    LinkedLoginDto? LinkedLogin,
    LinkedLoginDto? MatchingLogin,
    string NextAction,
    string? Reason)
{
    /// <summary>Stable code for a refusal the screen words itself (e.g. <c>login_other_company</c>).</summary>
    public string? ReasonCode { get; init; }
    /// <summary>The name a coded refusal cites: the company (<c>login_other_company</c>) or employee (<c>login_pointer_conflict</c>).</summary>
    public string? ReasonSubject { get; init; }
    /// <summary>For <c>link_existing</c>: someone other than the person has held a credential for this login, so the
    /// link will make its password unusable and invite the person to set their own.</summary>
    public bool WillResetCredential { get; init; }
    /// <summary>Who last set the employee's work email — the address every credential is sent to — and when. Null when not recorded.</summary>
    public string? WorkEmailSetBy { get; init; }
    public DateTime? WorkEmailSetAtUtc { get; init; }
    /// <summary>The work email was changed after the record was created and there is no activated login: an
    /// invitation or link must carry <c>confirmedWorkEmail: true</c>.</summary>
    public bool WorkEmailChangedAfterCreation { get; init; }
}

public static class EmployeeLoginNextActions
{
    public const string Linked = "linked";
    public const string LinkExisting = "link_existing";
    public const string Invite = "invite";
    public const string NeedsWorkEmail = "needs_work_email";
    public const string Blocked = "blocked";
}

public record LinkExistingLoginRequest(
    [Required] int EmployeeId,
    [Required] Guid UserId,
    [Required, MaxLength(500)] string Reason,
    bool ConfirmedWorkEmail = false);

public record EmployeeLoginLinkResultDto(
    int EmployeeId,
    Guid UserId,
    string Email,
    string Status,
    string AccessMode,
    bool IsActive,
    bool AlreadyLinked)
{
    /// <summary>
    /// True when someone other than the person had held a credential for the login (created it, set its password,
    /// or was shown a reset or invitation link): the link made the old password unusable, and the person sets their
    /// own from a fresh invitation to their work email.
    /// </summary>
    public bool CredentialReset { get; init; }
    /// <summary>The invitation link, returned ONLY when it could not be emailed — the linker passes it on by hand.</summary>
    public string? InvitationUrl { get; init; }
    public DateTime? InvitationExpiresAtUtc { get; init; }
    public bool EmailSent { get; init; }
    /// <summary>What happened to the invitation, in plain words. Empty when the credential was not reset.</summary>
    public string DeliveryMessage { get; init; } = string.Empty;
    /// <summary>The caller set the employee's work email: the rotation invitation was not emailed but handed back.</summary>
    public bool HandOverInPerson { get; init; }
}

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
    AuthUserDto User)
{
    /// <summary>
    /// Known-device token for the HttpOnly cookie the sign-in endpoint sets (LoginAbuseGuard). Never
    /// serialised: the browser must not be able to read it.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? KnownDeviceToken { get; init; }
}

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
    IReadOnlyCollection<CompanyAccessDto>? Companies = null)
{
    /// <summary>F1: non-null while a sign-in code HR issued for this ACTIVE login is live ("HR gave you a new sign-in code on {date}").</summary>
    public PendingResetNoticeDto? PendingResetNotice { get; init; }
}

public record PendingResetNoticeDto(DateTime Date);

/// <summary>GET api/auth/password-policy — what the welcome screen's live ticks check.</summary>
public record PasswordPolicyDto(int MinLength);

public record ForgotPasswordResponse(string Message, string? ResetToken, DateTime? ResetTokenExpiresAtUtc)
{
    /// <summary>Whether the RESOLVED workspace can send email at all (never whether the address exists). Lets the
    /// employee screens say "Ask HR for a new welcome code" when nothing can be mailed. False for an unknown workspace (no enumeration).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? EmailDeliveryConfigured { get; init; }
}

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

/// <summary>
/// The caller's privilege ceiling, so the Access screen offers only what the server will accept: the caller's own
/// effective permissions and, per role, whether they may assign it and edit it — with the coded reason when not.
/// </summary>
public record AccessCeilingDto(
    Guid UserId,
    bool IsAdmin,
    IReadOnlyCollection<string> HeldPermissions,
    IReadOnlyCollection<RoleCeilingDto> Roles);

public record RoleCeilingDto(
    Guid RoleId,
    string Name,
    bool CanAssign,
    string? AssignRefusalCode,
    string? AssignRefusalEn,
    string? AssignRefusalAr,
    bool CanEdit,
    string? EditRefusalCode,
    string? EditRefusalEn,
    string? EditRefusalAr);

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
    DateTime CreatedAtUtc,
    // The employee record this login is linked to, for the User Management row. Null when unlinked.
    string? EmployeeName = null,
    string? EmployeeCode = null);

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

/// <summary>
/// The controlled password-reset link an administrator issues on a user's behalf. The raw
/// <see cref="ResetToken"/> exists only in this object and in the link built from it — the database
/// stores nothing but its hash — so it can be shown once and never recovered afterwards.
/// </summary>
public record AdminPasswordResetLinkDto(
    Guid UserId,
    string Email,
    string FullName,
    string ResetToken,
    string ResetUrl,
    DateTime ExpiresAtUtc)
{
    /// <summary>The caller set a linked employee's work email: the link is never emailed, only handed back.</summary>
    public bool HandOverInPerson { get; init; }
}

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

/// <summary>
/// Mandatory-MFA standing for the signed-in principal. <c>RequiredBecause</c> is "privileged_role",
/// "workspace_policy" or null; <c>EnforceFromUtc</c> null with <c>Required</c> true means no date is
/// configured yet (prompt only).
/// </summary>
public record MfaStatusDto(
    bool Enabled,
    bool Required,
    string? RequiredBecause,
    DateTime? EnforceFromUtc,
    bool Enforced,
    bool PromptToEnroll,
    int? RecoveryCodesRemaining = null);

/// <summary>Result from LoginAsync — one of: Tokens (success), Challenge (MFA code needed),
/// or RequiresMfaEnrollment (tenant mandates MFA but this user hasn't set it up yet).</summary>
public record AuthLoginResult(AuthResponse? Tokens, MfaChallengeDto? Challenge, bool RequiresMfaEnrollment = false, MfaChallengeDto? EnrollmentChallenge = null)
{
    public bool RequiresMfa => Challenge is not null;
}

/// <summary>Temporary secret + provisioning URI returned during TOTP setup.
/// The provisioning URI contains the base32 secret embedded and is safe to return once.
/// After setup confirmation neither the secret nor URI is ever returned again.</summary>
public record MfaSetupInitResponse(string ProvisioningUri);

public record MfaEnrollmentSetupRequest(
    [System.ComponentModel.DataAnnotations.Required] string EnrollmentToken);

public record MfaEnrollmentVerifySetupRequest(
    [System.ComponentModel.DataAnnotations.Required] string EnrollmentToken,
    [System.ComponentModel.DataAnnotations.Required] string TempSecret,
    [System.ComponentModel.DataAnnotations.Required] string TotpCode);

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

public record BulkGrantPermissionItem(
    [System.ComponentModel.DataAnnotations.Required] string PermissionKey,
    [System.ComponentModel.DataAnnotations.Required] string Effect);  // "Allow" | "Deny" | "Remove"

public record BulkGrantPermissionsRequest(
    [System.ComponentModel.DataAnnotations.Required] IReadOnlyCollection<BulkGrantPermissionItem> Items,
    string? Reason = null);

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
