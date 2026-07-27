using Zayra.Api.Application.Common;

namespace Zayra.Api.Application.Auth;

public interface IAccessManagementService
{
    Task<IReadOnlyCollection<RoleDto>> GetRolesAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<PermissionDto>> GetPermissionsAsync(CancellationToken cancellationToken);
    Task<PagedResult<UserListDto>> ListUsersAsync(Guid tenantId, UserListQuery query, EntityScopeContext entityScope, CancellationToken cancellationToken);
    Task<UserListDto?> GetUserAsync(Guid tenantId, Guid userId, EntityScopeContext entityScope, CancellationToken cancellationToken);
    Task<AuthUserDto> CreateUserAsync(Guid tenantId, CreateUserRequest request, RequestContext context, CancellationToken cancellationToken);
    Task<UserListDto?> UpdateUserAsync(Guid tenantId, Guid userId, UpdateUserRequest request, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken);
    Task<AuthUserDto> AssignRolesAsync(Guid tenantId, Guid userId, AssignRolesRequest request, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken);
    Task<EmployeeLoginInvitationDto> InviteEmployeeLoginAsync(Guid tenantId, InviteEmployeeLoginRequest request, RequestContext context, CancellationToken cancellationToken);
    Task<UserAccessDto?> GetUserAccessAsync(Guid tenantId, Guid userId, EntityScopeContext entityScope, CancellationToken cancellationToken);
    Task<UserAccessDto?> SetAccessModeAsync(Guid tenantId, Guid userId, AccessModeRequest request, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken);
    Task<UserAccessDto?> SetPermissionOverrideAsync(Guid tenantId, Guid userId, PermissionOverrideRequest request, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken);
    Task ActivateUserAsync(Guid tenantId, Guid userId, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken);
    Task SuspendUserAsync(Guid tenantId, Guid userId, string reason, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken);
    Task LockUserAsync(Guid tenantId, Guid userId, string reason, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken);
    Task UnlockUserAsync(Guid tenantId, Guid userId, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken);
    Task AdminResetPasswordAsync(Guid tenantId, Guid userId, AdminResetPasswordRequest request, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken);
    Task<bool> DeleteUserAsync(Guid tenantId, Guid userId, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken);
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
    Task<RoleDto?> SetRolePermissionsAsync(Guid tenantId, Guid roleId, BulkRolePermissionsRequest request, RequestContext context, CancellationToken cancellationToken);

    // Permission matrix
    Task<PermissionMatrixDto> GetPermissionMatrixAsync(Guid tenantId, CancellationToken cancellationToken);
    Task SavePermissionMatrixAsync(Guid tenantId, PermissionMatrixUpdateRequest request, RequestContext context, CancellationToken cancellationToken);

    // Effective permissions for a user
    Task<EffectivePermissionsDto?> GetEffectivePermissionsAsync(Guid tenantId, Guid userId, EntityScopeContext entityScope, CancellationToken cancellationToken);

    // Permission override delete
    Task<bool> DeletePermissionOverrideAsync(Guid tenantId, Guid userId, Guid overrideId, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken);
}

public record EmployeeTeamMemberDto(int EmployeeId, string EmployeeCode, string FullName, string Department, string Designation, int? ManagerEmployeeId, int Depth);
