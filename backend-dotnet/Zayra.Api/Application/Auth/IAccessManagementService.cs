using Zayra.Api.Application.Common;

namespace Zayra.Api.Application.Auth;

public interface IAccessManagementService
{
    Task<IReadOnlyCollection<RoleDto>> GetRolesAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<PermissionDto>> GetPermissionsAsync(CancellationToken cancellationToken);
    Task<PagedResult<UserListDto>> ListUsersAsync(Guid tenantId, UserListQuery query, EntityScopeContext entityScope, CancellationToken cancellationToken);
    Task<UserListDto?> GetUserAsync(Guid tenantId, Guid userId, EntityScopeContext entityScope, CancellationToken cancellationToken);
    Task<AuthUserDto> CreateUserAsync(Guid tenantId, CreateUserRequest request, RequestContext context, CancellationToken cancellationToken);
    /// <summary>The one writer of a legal-entity grant (POST /api/access/entity-grants and the create-company wizard).
    /// The caller has already decided the grant is within its own scope; this applies the privilege ceiling, refuses a
    /// duplicate (returns null), revokes the user's live refresh tokens and audits. Saves.</summary>
    Task<Zayra.Api.Models.UserEntityAccess?> CreateEntityGrantAsync(Guid tenantId, Guid userId, Guid? companyId, string role, string grantMode, RequestContext context, CancellationToken cancellationToken);
    Task<UserListDto?> UpdateUserAsync(Guid tenantId, Guid userId, UpdateUserRequest request, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken);
    Task<AuthUserDto> AssignRolesAsync(Guid tenantId, Guid userId, AssignRolesRequest request, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken);
    Task<EmployeeLoginInvitationDto> InviteEmployeeLoginAsync(Guid tenantId, InviteEmployeeLoginRequest request, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken);
    /// <summary>Where an employee record stands on the way to Self-Service, and the one next step. Null = not found or outside the caller's company scope.</summary>
    Task<EmployeeLoginStatusDto?> GetEmployeeLoginStatusAsync(Guid tenantId, int employeeId, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken);
    /// <summary>Links an existing, active login to an employee record whose work email it carries.</summary>
    Task<EmployeeLoginLinkResultDto> LinkExistingLoginAsync(Guid tenantId, LinkExistingLoginRequest request, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken);
    Task<UserAccessDto?> GetUserAccessAsync(Guid tenantId, Guid userId, EntityScopeContext entityScope, CancellationToken cancellationToken);
    Task<UserAccessDto?> SetAccessModeAsync(Guid tenantId, Guid userId, AccessModeRequest request, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken);
    Task<UserAccessDto?> SetPermissionOverrideAsync(Guid tenantId, Guid userId, PermissionOverrideRequest request, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken);
    Task ActivateUserAsync(Guid tenantId, Guid userId, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken);
    Task SuspendUserAsync(Guid tenantId, Guid userId, string reason, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken);
    Task LockUserAsync(Guid tenantId, Guid userId, string reason, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken);
    Task UnlockUserAsync(Guid tenantId, Guid userId, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken);
    /// <summary>
    /// May the caller set a temporary password for this user? Throws <see cref="InvalidOperationException"/> (or the
    /// ceiling exception) when not: a reason is required, never yourself, the user must be in your scope, below your
    /// ceiling, and hold an active local login. The password itself is written by IAuthService.AdminSetTemporaryPasswordAsync.
    /// </summary>
    Task AuthorizeAdminPasswordSetAsync(Guid tenantId, Guid userId, string? reason, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Mints the controlled, single-use, one-hour password-reset link that replaced the
    /// administrator-chosen temporary password. The administrator never learns or sets the
    /// credential; only the user can, by redeeming the link.
    /// </summary>
    Task<AdminPasswordResetLinkDto> IssuePasswordResetLinkAsync(Guid tenantId, Guid userId, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken);
    Task<bool> DeleteUserAsync(Guid tenantId, Guid userId, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken);
    /// <summary>Setup's Restore for a deleted user. The account comes back DEACTIVATED, never silently active: the administrator
    /// reviews its roles and then reactivates it. False when no such deleted user is in the caller's scope; refuses (InvalidOperationException)
    /// when another account has since taken the email.</summary>
    Task<bool> RestoreUserAsync(Guid tenantId, Guid userId, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken);
    /// <summary>
    /// An administrator resets ANOTHER user's two-step sign-in (lost phone). Same permission and account ceiling as the
    /// other account actions; never yourself; a 3-500 character reason. In one transaction: factor cleared, every
    /// session and open challenge ended, every trusted browser revoked (mfa_admin_reset), backup codes deleted,
    /// audited as auth.mfa.admin_reset. A ceiling refusal throws <see cref="PrivilegeCeilingException"/>.
    /// </summary>
    Task<MfaAdminResetResult> ResetUserMfaAsync(Guid tenantId, Guid userId, string? reason, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken)
        => throw new NotSupportedException("This IAccessManagementService does not support two-step reset.");
    Task<IReadOnlyCollection<EmployeeTeamMemberDto>> GetTeamAsync(Guid tenantId, int managerEmployeeId, CancellationToken cancellationToken);
    Task<ApprovalDelegationDto> CreateDelegationAsync(Guid tenantId, ApprovalDelegationRequest request, RequestContext context, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<ApprovalDelegationDto>> GetDelegationsAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<bool> CancelDelegationAsync(Guid tenantId, Guid delegationId, RequestContext context, CancellationToken cancellationToken);
    Task<ApprovalAuthorityDto> CreateAuthorityAsync(Guid tenantId, ApprovalAuthorityRequest request, RequestContext context, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<ApprovalAuthorityDto>> GetAuthoritiesAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<ApprovalAuthorityDto?> UpdateAuthorityAsync(Guid tenantId, Guid authorityId, ApprovalAuthorityRequest request, RequestContext context, CancellationToken cancellationToken);
    Task<SecuritySettingDto> GetSecuritySettingsAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<SecuritySettingDto> UpdateSecuritySettingsAsync(Guid tenantId, UpdateSecuritySettingRequest request, RequestContext context, CancellationToken cancellationToken);

    // Permission Grantor management
    Task<IReadOnlyCollection<PermissionGrantorDto>> GetGrantorsAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<PermissionGrantorDto> AddGrantorAsync(Guid tenantId, AddGrantorRequest request, RequestContext context, CancellationToken cancellationToken);
    Task<bool> RevokeGrantorAsync(Guid tenantId, Guid recordId, RequestContext context, CancellationToken cancellationToken);
    // Grant/revoke a single permission — available to Admins and designated grantors
    Task<UserAccessDto?> GrantPermissionAsync(Guid tenantId, Guid targetUserId, GrantPermissionRequest request, EntityScopeContext entityScope, Guid? callerUserId, bool isAdmin, CancellationToken cancellationToken);
    // Grant/deny/reset many permissions atomically in one transaction — one refresh-token revocation pass, per-key + summary audit
    Task<UserAccessDto?> GrantPermissionsBulkAsync(Guid tenantId, Guid targetUserId, BulkGrantPermissionsRequest request, EntityScopeContext entityScope, Guid? callerUserId, bool isAdmin, CancellationToken cancellationToken);

    // Role CRUD
    Task<RoleDto> CreateRoleAsync(Guid tenantId, CreateRoleRequest request, RequestContext context, CancellationToken cancellationToken);
    Task<RoleDto?> UpdateRoleAsync(Guid tenantId, Guid roleId, UpdateRoleRequest request, RequestContext context, CancellationToken cancellationToken);
    Task<bool> ActivateRoleAsync(Guid tenantId, Guid roleId, RequestContext context, CancellationToken cancellationToken);
    Task<bool> DeactivateRoleAsync(Guid tenantId, Guid roleId, RequestContext context, CancellationToken cancellationToken);
    /// <summary>What changing a role would touch: how many people hold it, and whether it is a locked system role. Null when the role does not exist.</summary>
    Task<RoleImpactDto?> GetRoleImpactAsync(Guid tenantId, Guid roleId, CancellationToken cancellationToken);
    /// <summary>Setup's Remove for a role: a custom role nobody holds is deleted; a system role or one still assigned is refused
    /// (the result says why and how many). Same privilege ceiling and locks as deactivating.</summary>
    Task<RoleRemovalResult?> DeleteRoleAsync(Guid tenantId, Guid roleId, RequestContext context, CancellationToken cancellationToken);
    Task<RoleDto?> SetRolePermissionsAsync(Guid tenantId, Guid roleId, BulkRolePermissionsRequest request, RequestContext context, CancellationToken cancellationToken);

    // Permission matrix
    Task<PermissionMatrixDto> GetPermissionMatrixAsync(Guid tenantId, CancellationToken cancellationToken);
    Task SavePermissionMatrixAsync(Guid tenantId, PermissionMatrixUpdateRequest request, RequestContext context, CancellationToken cancellationToken);

    // Effective permissions for a user
    /// <summary>
    /// Throws <c>PrivilegeCeilingException</c> unless the caller may change <paramref name="targetUserId"/>'s access
    /// (not themselves, not an Admin unless the caller is one, not a user holding access the caller lacks). For
    /// access writes made outside this service (group scope, company grants).
    /// </summary>
    Task AssertMayChangeUserAccessAsync(Guid tenantId, Guid targetUserId, RequestContext context, CancellationToken cancellationToken);
    Task<AccessCeilingDto> GetAccessCeilingAsync(Guid tenantId, RequestContext context, CancellationToken cancellationToken);
    Task<EffectivePermissionsDto?> GetEffectivePermissionsAsync(Guid tenantId, Guid userId, EntityScopeContext entityScope, CancellationToken cancellationToken);

    // Permission override delete
    Task<bool> DeletePermissionOverrideAsync(Guid tenantId, Guid userId, Guid overrideId, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken);
}

public record EmployeeTeamMemberDto(int EmployeeId, string EmployeeCode, string FullName, string Department, string Designation, int? ManagerEmployeeId, int Depth);
