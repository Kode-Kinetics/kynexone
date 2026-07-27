using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Auth;

public class AccessManagementService : IAccessManagementService
{
    private readonly ZayraDbContext _db;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IAuditService _auditService;
    private readonly ITokenService _tokenService;

    public AccessManagementService(ZayraDbContext db, IPasswordHasher passwordHasher, IAuditService auditService, ITokenService tokenService)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _auditService = auditService;
        _tokenService = tokenService;
    }

    public async Task<IReadOnlyCollection<RoleDto>> GetRolesAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var roles = await _db.Roles
            .Include(x => x.RolePermissions).ThenInclude(x => x.Permission)
            .Where(x => (x.TenantId == tenantId || x.TenantId == null) && !x.IsDeleted)
            .OrderBy(x => x.AuthorityLevel).ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);
        return roles.Select(ToRoleDto).ToList();
    }

    public async Task<IReadOnlyCollection<PermissionDto>> GetPermissionsAsync(CancellationToken cancellationToken)
    {
        return await _db.Permissions
            .OrderBy(x => x.Module).ThenBy(x => x.Key)
            .Select(x => new PermissionDto(x.Id, x.Key, x.Module, x.Description))
            .ToListAsync(cancellationToken);
    }

    public async Task<AuthUserDto> CreateUserAsync(Guid tenantId, CreateUserRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(x => x.Id == tenantId && x.IsActive, cancellationToken)
            ?? throw new InvalidOperationException("Tenant not found.");
        var normalizedEmail = AuthService.Normalize(request.Email);
        var exists = await _db.Users.AnyAsync(x => x.TenantId == tenantId && x.NormalizedEmail == normalizedEmail && x.IsActive, cancellationToken);
        if (exists) throw new InvalidOperationException("A user with this email already exists in this tenant.");

        var roles = await LoadRoles(tenantId, request.Roles, cancellationToken);
        var isAdminUser = roles.Any(r => r.NormalizedName == "ADMIN");
        var user = new User
        {
            TenantId = tenantId,
            Tenant = tenant,
            Email = request.Email.Trim().ToLowerInvariant(),
            NormalizedEmail = normalizedEmail,
            FullName = request.FullName.Trim(),
            PasswordHash = _passwordHasher.Hash(request.Password),
            AccessMode = AccessModes.FullPortal,
            IsGroupScope = isAdminUser,
            IsActive = true,
            IsEmailConfirmed = true
        };
        foreach (var role in roles) user.UserRoles.Add(new UserRole { User = user, Role = role });
        _db.Users.Add(user);
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("access.user_created", "User", user.Id.ToString(), context, $"{{\"email\":\"{user.Email}\"}}", cancellationToken);
        return ToUserDto(user, tenant, roles);
    }

    public async Task<EmployeeLoginInvitationDto> InviteEmployeeLoginAsync(Guid tenantId, InviteEmployeeLoginRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var employee = await _db.Employees.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == request.EmployeeId && !x.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("Employee not found.");
        var accessMode = NormalizeAccessMode(request.AccessMode);
        var email = (request.Email ?? employee.WorkEmail).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email)) throw new InvalidOperationException("Employee login requires a work email or explicit email.");

        var normalizedEmail = AuthService.Normalize(email);
        var user = await _db.Users
            .Include(x => x.UserRoles)
            .Include(x => x.EmployeeUserAccounts)
            .Include(x => x.EntityAccesses)
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.NormalizedEmail == normalizedEmail, cancellationToken);
        if (user is null)
        {
            user = new User
            {
                TenantId = tenantId,
                Email = email,
                NormalizedEmail = normalizedEmail,
                FullName = employee.FullName,
                PasswordHash = _passwordHasher.Hash(Guid.NewGuid().ToString("N") + "!Aa1"),
                AccessMode = accessMode,
                IsActive = accessMode != AccessModes.NoLogin,
                IsEmailConfirmed = false
            };
            _db.Users.Add(user);
        }
        else if (!user.IsActive && accessMode != AccessModes.NoLogin)
        {
            user.IsActive = true;
            user.FullName = employee.FullName;
            user.AccessMode = accessMode;
        }

        var roles = await LoadRoles(tenantId, request.Roles is { Count: > 0 } ? request.Roles : DefaultRoles(accessMode), cancellationToken);
        foreach (var role in roles)
            if (!user.UserRoles.Any(x => x.RoleId == role.Id)) user.UserRoles.Add(new UserRole { User = user, Role = role });

        var invitationToken = accessMode != AccessModes.NoLogin ? _tokenService.CreateSecureToken() : string.Empty;
        var link = user.EmployeeUserAccounts.FirstOrDefault(x => x.TenantId == tenantId && x.EmployeeId == employee.Id && !x.IsDeleted);
        if (link is null)
        {
            link = new EmployeeUserAccount { TenantId = tenantId, EmployeeId = employee.Id, User = user, CreatedBy = context.UserId };
            user.EmployeeUserAccounts.Add(link);
        }
        link.AccessMode = accessMode;
        link.Status = accessMode == AccessModes.NoLogin ? "NoLogin" : "Invited";
        link.RequiresPasswordSetup = accessMode != AccessModes.NoLogin;
        link.InvitedAtUtc = DateTime.UtcNow;
        link.InvitationExpiresAtUtc = accessMode == AccessModes.NoLogin ? null : DateTime.UtcNow.AddHours(Math.Clamp(request.InvitationHours, 1, 720));
        link.InvitationTokenHash = string.IsNullOrEmpty(invitationToken) ? string.Empty : _tokenService.HashToken(invitationToken);
        link.UpdatedAtUtc = DateTime.UtcNow;
        link.UpdatedBy = context.UserId;

        if (employee.CompanyId.HasValue && !user.EntityAccesses.Any(x =>
                x.TenantId == tenantId
                && x.IsActive
                && x.CompanyId == employee.CompanyId.Value
                && x.GrantMode == EntityGrantModes.SelectedCompanies))
        {
            user.EntityAccesses.Add(new UserEntityAccess
            {
                TenantId = tenantId,
                User = user,
                CompanyId = employee.CompanyId.Value,
                GrantMode = EntityGrantModes.SelectedCompanies,
                Role = string.Join(",", roles.Select(r => r.Name)),
                CreatedBy = context.UserId,
                GrantedBy = context.UserId,
                GrantedAt = DateTime.UtcNow
            });
        }

        employee.UserAccountId = accessMode == AccessModes.NoLogin ? null : user.Id;
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("access.employee_invited", "EmployeeUserAccount", link.Id.ToString(), context, $"{{\"employeeId\":{employee.Id},\"accessMode\":\"{accessMode}\"}}", cancellationToken);
        return new EmployeeLoginInvitationDto(user.Id, employee.Id, user.Email, accessMode, link.Status, invitationToken, link.InvitationExpiresAtUtc);
    }

    public async Task<AuthUserDto> AssignRolesAsync(Guid tenantId, Guid userId, AssignRolesRequest request, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken)
    {
        var user = await _db.Users
            .Include(x => x.Tenant)
            .Include(x => x.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x!.RolePermissions).ThenInclude(x => x.Permission)
            .ApplyEntityScope(_db, tenantId, entityScope)
            .FirstOrDefaultAsync(x => x.Id == userId && x.TenantId == tenantId, cancellationToken)
            ?? throw new InvalidOperationException("User not found.");
        var roles = await LoadRoles(tenantId, request.Roles, cancellationToken);

        _db.UserRoles.RemoveRange(user.UserRoles);
        user.UserRoles.Clear();
        foreach (var role in roles) user.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id, Role = role });
        user.UpdatedAtUtc = DateTime.UtcNow;
        await RevokeActiveRefreshTokensAsync(user.Id, context, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("access.roles_assigned", "User", user.Id.ToString(), context, $"{{\"roles\":[{string.Join(',', roles.Select(r => $"\"{r.Name}\""))}]}}", cancellationToken);
        return ToUserDto(user, user.Tenant!, roles);
    }

    public async Task<UserAccessDto?> GetUserAccessAsync(Guid tenantId, Guid userId, EntityScopeContext entityScope, CancellationToken cancellationToken)
    {
        var user = await LoadAccessUser(tenantId, userId, entityScope, cancellationToken);
        return user is null ? null : ToAccessDto(user);
    }

    public async Task<UserAccessDto?> SetAccessModeAsync(Guid tenantId, Guid userId, AccessModeRequest request, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken)
    {
        var user = await LoadAccessUser(tenantId, userId, entityScope, cancellationToken);
        if (user is null) return null;
        var accessMode = NormalizeAccessMode(request.AccessMode);
        var link = user.EmployeeUserAccounts.Where(x => !x.IsDeleted).OrderByDescending(x => x.IsPrimary).FirstOrDefault();
        if (link is not null)
        {
            link.AccessMode = accessMode;
            link.Status = accessMode == AccessModes.NoLogin ? "NoLogin" : "Active";
            link.LoginDisabledReason = accessMode == AccessModes.NoLogin ? request.Reason ?? "No login access" : string.Empty;
            link.UpdatedAtUtc = DateTime.UtcNow;
            link.UpdatedBy = context.UserId;
        }
        user.AccessMode = accessMode;
        user.IsActive = accessMode != AccessModes.NoLogin;
        user.UpdatedAtUtc = DateTime.UtcNow;
        await RevokeActiveRefreshTokensAsync(user.Id, context, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("access.mode_changed", "User", user.Id.ToString(), context, $"{{\"accessMode\":\"{accessMode}\",\"reason\":\"{request.Reason ?? string.Empty}\"}}", cancellationToken);
        return ToAccessDto(user);
    }

    public async Task<UserAccessDto?> SetPermissionOverrideAsync(Guid tenantId, Guid userId, PermissionOverrideRequest request, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken)
    {
        var user = await LoadAccessUser(tenantId, userId, entityScope, cancellationToken);
        if (user is null) return null;
        var permissionExists = await _db.Permissions.AnyAsync(x => x.Key == request.PermissionKey, cancellationToken);
        if (!permissionExists) throw new InvalidOperationException("Permission does not exist.");
        var effect = request.Effect.Equals("Deny", StringComparison.OrdinalIgnoreCase) ? "Deny" : "Allow";
        var ov = user.PermissionOverrides.FirstOrDefault(x => x.PermissionKey == request.PermissionKey);
        if (ov is null)
        {
            ov = new UserPermissionOverride { TenantId = tenantId, UserId = userId, PermissionKey = request.PermissionKey, CreatedBy = context.UserId };
            _db.UserPermissionOverrides.Add(ov);
        }
        ov.Effect = effect;
        ov.Reason = request.Reason ?? string.Empty;
        ov.ExpiresAtUtc = request.ExpiresAtUtc;
        ov.IsActive = true;
        ov.UpdatedAtUtc = DateTime.UtcNow;
        ov.UpdatedBy = context.UserId;
        await RevokeActiveRefreshTokensAsync(user.Id, context, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("access.permission_override", "UserPermissionOverride", ov.Id.ToString(), context, $"{{\"userId\":\"{userId}\",\"permission\":\"{request.PermissionKey}\",\"effect\":\"{effect}\"}}", cancellationToken);
        return await GetUserAccessAsync(tenantId, userId, entityScope, cancellationToken);
    }

    public async Task<IReadOnlyCollection<EmployeeTeamMemberDto>> GetTeamAsync(Guid tenantId, int managerEmployeeId, CancellationToken cancellationToken)
    {
        // BFS level-by-level: each iteration issues one SQL IN query for exactly the next
        // level of direct reports. This is O(depth) queries with each fetching only the
        // columns needed — no full-table load regardless of org size.
        var result = new List<EmployeeTeamMemberDto>();
        var currentLevel = new List<int> { managerEmployeeId };
        var depth = 0;
        const int maxDepth = 15; // guard against circular manager relationships

        while (currentLevel.Count > 0 && depth < maxDepth)
        {
            depth++;
            var reports = await _db.Employees
                .AsNoTracking()
                .Where(x => x.TenantId == tenantId && !x.IsDeleted
                            && x.ManagerEmployeeId.HasValue
                            && currentLevel.Contains(x.ManagerEmployeeId!.Value))
                .Select(x => new { x.Id, x.EmployeeCode, x.FullName, x.Department, x.Designation, x.ManagerEmployeeId })
                .ToListAsync(cancellationToken);

            if (reports.Count == 0) break;
            result.AddRange(reports.Select(x =>
                new EmployeeTeamMemberDto(x.Id, x.EmployeeCode, x.FullName, x.Department, x.Designation, x.ManagerEmployeeId, depth)));
            currentLevel = reports.Select(x => x.Id).ToList();
        }

        return result;
    }

    public async Task<ApprovalDelegationDto> CreateDelegationAsync(Guid tenantId, ApprovalDelegationRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        if (request.FromEmployeeId == request.ToEmployeeId) throw new InvalidOperationException("Delegation must be assigned to a different employee.");
        if (request.EndDate < request.StartDate) throw new InvalidOperationException("Delegation end date must be on or after the start date.");

        var employees = await _db.Employees
            .Where(x => x.TenantId == tenantId && !x.IsDeleted && (x.Id == request.FromEmployeeId || x.Id == request.ToEmployeeId))
            .ToListAsync(cancellationToken);
        var from = employees.FirstOrDefault(x => x.Id == request.FromEmployeeId) ?? throw new InvalidOperationException("Delegating employee not found.");
        var to = employees.FirstOrDefault(x => x.Id == request.ToEmployeeId) ?? throw new InvalidOperationException("Delegate employee not found.");

        var delegation = new ApprovalDelegation
        {
            TenantId = tenantId,
            FromEmployeeId = from.Id,
            ToEmployeeId = to.Id,
            FromUserId = from.UserAccountId,
            ToUserId = to.UserAccountId,
            Scope = request.Scope.Trim(),
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            Reason = request.Reason ?? string.Empty,
            CreatedBy = context.UserId
        };
        _db.ApprovalDelegations.Add(delegation);
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("access.approval_delegation_created", "ApprovalDelegation", delegation.Id.ToString(), context, $"{{\"fromEmployeeId\":{from.Id},\"toEmployeeId\":{to.Id},\"scope\":\"{delegation.Scope}\"}}", cancellationToken);
        return ToDelegationDto(delegation);
    }

    public async Task<IReadOnlyCollection<ApprovalDelegationDto>> GetDelegationsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var delegations = await _db.ApprovalDelegations.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        return delegations.Select(ToDelegationDto).ToList();
    }

    public async Task<ApprovalAuthorityDto> CreateAuthorityAsync(Guid tenantId, ApprovalAuthorityRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var employee = await _db.Employees.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == request.EmployeeId && !x.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("Employee not found.");
        var authority = new ApprovalAuthority
        {
            TenantId = tenantId,
            EmployeeId = employee.Id,
            UserId = employee.UserAccountId,
            AuthorityScope = request.AuthorityScope.Trim(),
            ApproverRole = request.ApproverRole.Trim(),
            AmountLimit = request.AmountLimit,
            Currency = request.Currency ?? string.Empty,
            CanFinalApprove = request.CanFinalApprove,
            CreatedBy = context.UserId
        };
        _db.ApprovalAuthorities.Add(authority);
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("access.approval_authority_created", "ApprovalAuthority", authority.Id.ToString(), context, $"{{\"employeeId\":{employee.Id},\"scope\":\"{authority.AuthorityScope}\",\"final\":{authority.CanFinalApprove.ToString().ToLowerInvariant()}}}", cancellationToken);
        return ToAuthorityDto(authority);
    }

    public async Task<IReadOnlyCollection<ApprovalAuthorityDto>> GetAuthoritiesAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var authorities = await _db.ApprovalAuthorities.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        return authorities.Select(ToAuthorityDto).ToList();
    }

    public async Task<PagedResult<UserListDto>> ListUsersAsync(Guid tenantId, UserListQuery query, EntityScopeContext entityScope, CancellationToken cancellationToken)
    {
        var q = _db.Users
            .Include(x => x.UserRoles).ThenInclude(x => x.Role)
            .Include(x => x.EmployeeUserAccounts)
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .ApplyEntityScope(_db, tenantId, entityScope)
            .AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var s = query.Search.Trim().ToLowerInvariant();
            q = q.Where(x => x.Email.Contains(s) || x.FullName.ToLower().Contains(s));
        }
        if (!string.IsNullOrWhiteSpace(query.Status))
            q = q.Where(x => x.Status == query.Status);
        if (!string.IsNullOrWhiteSpace(query.Role))
            q = q.Where(x => x.UserRoles.Any(r => r.Role!.NormalizedName == AuthService.Normalize(query.Role)));

        var total = await q.CountAsync(cancellationToken);
        var items = await q.OrderByDescending(x => x.CreatedAtUtc)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<UserListDto>(items.Select(ToUserListDto).ToList(), total, query.Page, query.PageSize);
    }

    public async Task<UserListDto?> GetUserAsync(Guid tenantId, Guid userId, EntityScopeContext entityScope, CancellationToken cancellationToken)
    {
        var user = await _db.Users
            .Include(x => x.UserRoles).ThenInclude(x => x.Role)
            .Include(x => x.EmployeeUserAccounts)
            .ApplyEntityScope(_db, tenantId, entityScope)
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == userId && !x.IsDeleted, cancellationToken);
        return user is null ? null : ToUserListDto(user);
    }

    public async Task<UserListDto?> UpdateUserAsync(Guid tenantId, Guid userId, UpdateUserRequest request, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken)
    {
        var user = await _db.Users
            .Include(x => x.UserRoles).ThenInclude(x => x.Role)
            .Include(x => x.EmployeeUserAccounts)
            .ApplyEntityScope(_db, tenantId, entityScope)
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == userId && !x.IsDeleted, cancellationToken);
        if (user is null) return null;
        if (!string.IsNullOrWhiteSpace(request.FullName)) user.FullName = request.FullName.Trim();
        if (request.PhoneNumber is not null) user.PhoneNumber = request.PhoneNumber.Trim();
        if (!string.IsNullOrWhiteSpace(request.PreferredLanguage)) user.PreferredLanguage = request.PreferredLanguage.Trim();
        if (!string.IsNullOrWhiteSpace(request.Timezone)) user.Timezone = request.Timezone.Trim();
        user.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("access.user_updated", "User", user.Id.ToString(), context, null, cancellationToken);
        return ToUserListDto(user);
    }

    public async Task ActivateUserAsync(Guid tenantId, Guid userId, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken)
    {
        var user = await _db.Users.ApplyEntityScope(_db, tenantId, entityScope).FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == userId && !x.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("User not found.");
        user.IsActive = true;
        user.IsLocked = false;
        user.LockoutEnd = null;
        user.FailedLoginCount = 0;
        user.Status = "Active";
        user.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("access.user_activated", "User", user.Id.ToString(), context, null, cancellationToken);
    }

    public async Task SuspendUserAsync(Guid tenantId, Guid userId, string reason, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken)
    {
        var user = await _db.Users.ApplyEntityScope(_db, tenantId, entityScope).FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == userId && !x.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("User not found.");
        user.IsActive = false;
        user.Status = "Suspended";
        user.UpdatedAtUtc = DateTime.UtcNow;
        await RevokeActiveRefreshTokensAsync(userId, context, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("access.user_suspended", "User", user.Id.ToString(), context, $"{{\"reason\":\"{reason}\"}}", cancellationToken);
    }

    public async Task LockUserAsync(Guid tenantId, Guid userId, string reason, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken)
    {
        var user = await _db.Users.ApplyEntityScope(_db, tenantId, entityScope).FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == userId && !x.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("User not found.");
        user.IsLocked = true;
        user.LockoutEnd = DateTime.UtcNow.AddDays(1);
        user.Status = "Locked";
        user.UpdatedAtUtc = DateTime.UtcNow;
        await RevokeActiveRefreshTokensAsync(userId, context, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("access.user_locked", "User", user.Id.ToString(), context, $"{{\"reason\":\"{reason}\"}}", cancellationToken);
    }

    public async Task UnlockUserAsync(Guid tenantId, Guid userId, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken)
    {
        var user = await _db.Users.ApplyEntityScope(_db, tenantId, entityScope).FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == userId && !x.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("User not found.");
        user.IsLocked = false;
        user.LockoutEnd = null;
        user.FailedLoginCount = 0;
        user.Status = user.IsActive ? "Active" : "Suspended";
        user.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("access.user_unlocked", "User", user.Id.ToString(), context, null, cancellationToken);
    }

    public async Task AdminResetPasswordAsync(Guid tenantId, Guid userId, AdminResetPasswordRequest request, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken)
    {
        var user = await _db.Users
            .Include(x => x.EmployeeUserAccounts)
            .ApplyEntityScope(_db, tenantId, entityScope)
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == userId && !x.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("User not found.");
        user.PasswordHash = _passwordHasher.Hash(request.NewPassword);
        user.MustChangePassword = request.MustChangePassword;
        user.LastPasswordChangedAt = DateTime.UtcNow;
        user.UpdatedAtUtc = DateTime.UtcNow;
        user.IsActive = true;
        // Clear the invitation flag so the new password can be used immediately to log in
        foreach (var link in user.EmployeeUserAccounts.Where(x => !x.IsDeleted))
        {
            link.RequiresPasswordSetup = false;
            link.Status = "Active";
            link.UpdatedAtUtc = DateTime.UtcNow;
        }
        await RevokeActiveRefreshTokensAsync(userId, context, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("access.admin_password_reset", "User", user.Id.ToString(), context, null, cancellationToken);
    }

    public async Task<bool> DeleteUserAsync(Guid tenantId, Guid userId, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken)
    {
        var user = await _db.Users.ApplyEntityScope(_db, tenantId, entityScope).FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == userId && !x.IsDeleted, cancellationToken);
        if (user is null) return false;
        if (user.Id == context.UserId)
            throw new InvalidOperationException("You cannot delete your own account.");
        user.IsDeleted = true;
        user.DeletedAtUtc = DateTime.UtcNow;
        user.DeletedBy = context.UserId;
        user.IsActive = false;
        user.Status = "Deactivated";
        user.UpdatedAtUtc = DateTime.UtcNow;
        await RevokeActiveRefreshTokensAsync(userId, context, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("access.user_deleted", "User", user.Id.ToString(), context, null, cancellationToken);
        return true;
    }

    public async Task<bool> CancelDelegationAsync(Guid tenantId, Guid delegationId, RequestContext context, CancellationToken cancellationToken)
    {
        var delegation = await _db.ApprovalDelegations.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == delegationId, cancellationToken);
        if (delegation is null) return false;
        delegation.Status = "Cancelled";
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("access.delegation_cancelled", "ApprovalDelegation", delegationId.ToString(), context, null, cancellationToken);
        return true;
    }

    public async Task<ApprovalAuthorityDto?> UpdateAuthorityAsync(Guid tenantId, Guid authorityId, ApprovalAuthorityRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var authority = await _db.ApprovalAuthorities.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == authorityId, cancellationToken);
        if (authority is null) return null;
        authority.AuthorityScope = request.AuthorityScope.Trim();
        authority.ApproverRole = request.ApproverRole.Trim();
        authority.AmountLimit = request.AmountLimit;
        authority.Currency = request.Currency ?? string.Empty;
        authority.CanFinalApprove = request.CanFinalApprove;
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("access.authority_updated", "ApprovalAuthority", authorityId.ToString(), context, null, cancellationToken);
        return ToAuthorityDto(authority);
    }

    public async Task<SecuritySettingDto> GetSecuritySettingsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var setting = await _db.SecuritySettings.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);
        if (setting is null)
        {
            setting = new Models.SecuritySetting { TenantId = tenantId };
            _db.SecuritySettings.Add(setting);
            await _db.SaveChangesAsync(cancellationToken);
        }
        return ToSecuritySettingDto(setting);
    }

    public async Task<SecuritySettingDto> UpdateSecuritySettingsAsync(Guid tenantId, UpdateSecuritySettingRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var setting = await _db.SecuritySettings.FirstOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);
        if (setting is null)
        {
            setting = new Models.SecuritySetting { TenantId = tenantId };
            _db.SecuritySettings.Add(setting);
        }
        if (request.PasswordMinLength.HasValue) setting.PasswordMinLength = Math.Clamp(request.PasswordMinLength.Value, 6, 64);
        if (request.PasswordRequireUppercase.HasValue) setting.PasswordRequireUppercase = request.PasswordRequireUppercase.Value;
        if (request.PasswordRequireLowercase.HasValue) setting.PasswordRequireLowercase = request.PasswordRequireLowercase.Value;
        if (request.PasswordRequireDigit.HasValue) setting.PasswordRequireDigit = request.PasswordRequireDigit.Value;
        if (request.PasswordRequireSpecial.HasValue) setting.PasswordRequireSpecial = request.PasswordRequireSpecial.Value;
        if (request.PasswordExpiryDays.HasValue) setting.PasswordExpiryDays = Math.Clamp(request.PasswordExpiryDays.Value, 0, 365);
        if (request.PasswordHistoryCount.HasValue) setting.PasswordHistoryCount = Math.Clamp(request.PasswordHistoryCount.Value, 0, 24);
        if (request.MaxFailedLoginAttempts.HasValue) setting.MaxFailedLoginAttempts = Math.Clamp(request.MaxFailedLoginAttempts.Value, 1, 20);
        if (request.LockoutDurationMinutes.HasValue) setting.LockoutDurationMinutes = Math.Clamp(request.LockoutDurationMinutes.Value, 5, 1440);
        if (request.SessionTimeoutMinutes.HasValue) setting.SessionTimeoutMinutes = Math.Clamp(request.SessionTimeoutMinutes.Value, 15, 1440);
        if (request.RefreshTokenExpiryDays.HasValue) setting.RefreshTokenExpiryDays = Math.Clamp(request.RefreshTokenExpiryDays.Value, 1, 90);
        if (request.AllowMultipleSessions.HasValue) setting.AllowMultipleSessions = request.AllowMultipleSessions.Value;
        if (request.MfaRequired.HasValue) setting.MfaRequired = request.MfaRequired.Value;
        setting.UpdatedAtUtc = DateTime.UtcNow;
        setting.UpdatedBy = context.UserId;
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("access.security_settings_updated", "SecuritySetting", setting.Id.ToString(), context, null, cancellationToken);
        return ToSecuritySettingDto(setting);
    }

    public async Task<IReadOnlyCollection<PermissionGrantorDto>> GetGrantorsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var records = await _db.PermissionGrantorRecords
            .Where(x => x.TenantId == tenantId && x.IsActive)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        var userIds = records.Select(x => x.GrantorUserId).Distinct().ToList();
        var users = await _db.Users.Where(x => userIds.Contains(x.Id)).Select(x => new { x.Id, x.Email, x.FullName }).ToListAsync(cancellationToken);
        var userMap = users.ToDictionary(x => x.Id);

        return records.Select(r =>
        {
            userMap.TryGetValue(r.GrantorUserId, out var u);
            return new PermissionGrantorDto(r.Id, r.GrantorUserId, u?.Email ?? "unknown", u?.FullName ?? "unknown", r.PermissionScope, r.CanSubDelegate, r.GrantedByUserId, r.ExpiresAtUtc, r.IsActive, r.Reason, r.CreatedAtUtc);
        }).ToList();
    }

    public async Task<PermissionGrantorDto> AddGrantorAsync(Guid tenantId, AddGrantorRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var grantorUser = await _db.Users.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == request.GrantorUserId && !x.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("User not found.");

        // If the caller is not Admin, they must have CanSubDelegate=true and must own a grantor record that covers this scope
        if (context.UserId is not null)
        {
            var callerIsAdmin = await _db.Users
                .Include(x => x.UserRoles).ThenInclude(x => x.Role)
                .Where(x => x.Id == context.UserId && x.TenantId == tenantId)
                .AnyAsync(x => x.UserRoles.Any(r => r.Role!.NormalizedName == "ADMIN"), cancellationToken);
            if (!callerIsAdmin)
            {
                var callerGrant = await _db.PermissionGrantorRecords
                    .Where(x => x.TenantId == tenantId && x.GrantorUserId == context.UserId && x.IsActive && x.CanSubDelegate
                        && (x.ExpiresAtUtc == null || x.ExpiresAtUtc > DateTime.UtcNow))
                    .ToListAsync(cancellationToken);
                if (!callerGrant.Any(g => ScopeCoversScope(g.PermissionScope, request.PermissionScope)))
                    throw new InvalidOperationException("You are not authorised to delegate this permission scope.");
            }
        }

        var record = new Models.PermissionGrantorRecord
        {
            TenantId = tenantId,
            GrantorUserId = request.GrantorUserId,
            PermissionScope = request.PermissionScope.Trim(),
            CanSubDelegate = request.CanSubDelegate,
            GrantedByUserId = context.UserId,
            ExpiresAtUtc = request.ExpiresAtUtc,
            Reason = request.Reason ?? string.Empty,
            CreatedBy = context.UserId
        };
        _db.PermissionGrantorRecords.Add(record);
        await RevokeActiveRefreshTokensAsync(request.GrantorUserId, context, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("access.grantor_added", "PermissionGrantorRecord", record.Id.ToString(), context,
            $"{{\"grantorUserId\":\"{request.GrantorUserId}\",\"scope\":\"{request.PermissionScope}\",\"canSubDelegate\":{request.CanSubDelegate.ToString().ToLowerInvariant()}}}", cancellationToken);

        return new PermissionGrantorDto(record.Id, record.GrantorUserId, grantorUser.Email, grantorUser.FullName, record.PermissionScope, record.CanSubDelegate, record.GrantedByUserId, record.ExpiresAtUtc, record.IsActive, record.Reason, record.CreatedAtUtc);
    }

    public async Task<bool> RevokeGrantorAsync(Guid tenantId, Guid recordId, RequestContext context, CancellationToken cancellationToken)
    {
        var record = await _db.PermissionGrantorRecords.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == recordId, cancellationToken);
        if (record is null) return false;
        record.IsActive = false;
        await RevokeActiveRefreshTokensAsync(record.GrantorUserId, context, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("access.grantor_revoked", "PermissionGrantorRecord", recordId.ToString(), context, null, cancellationToken);
        return true;
    }

    public async Task<UserAccessDto?> GrantPermissionAsync(Guid tenantId, Guid targetUserId, GrantPermissionRequest request, EntityScopeContext entityScope, Guid? callerUserId, bool isAdmin, CancellationToken cancellationToken)
    {
        if (!isAdmin)
        {
            if (callerUserId is null) throw new UnauthorizedAccessException("Authentication required.");
            var grantor = await _db.PermissionGrantorRecords
                .Where(x => x.TenantId == tenantId && x.GrantorUserId == callerUserId.Value && x.IsActive
                    && (x.ExpiresAtUtc == null || x.ExpiresAtUtc > DateTime.UtcNow))
                .ToListAsync(cancellationToken);
            if (!grantor.Any(g => PermissionMatchesScope(request.PermissionKey, g.PermissionScope)))
                throw new InvalidOperationException("You are not authorised to grant or revoke this permission.");
        }

        var user = await LoadAccessUser(tenantId, targetUserId, entityScope, cancellationToken);
        if (user is null) return null;

        if (request.Effect.Equals("Remove", StringComparison.OrdinalIgnoreCase))
        {
            var existing = user.PermissionOverrides.FirstOrDefault(x => x.PermissionKey == request.PermissionKey);
            if (existing is not null) existing.IsActive = false;
        }
        else
        {
            var permissionExists = await _db.Permissions.AnyAsync(x => x.Key == request.PermissionKey, cancellationToken);
            if (!permissionExists) throw new InvalidOperationException("Permission key does not exist.");

            var effect = request.Effect.Equals("Deny", StringComparison.OrdinalIgnoreCase) ? "Deny" : "Allow";
            var ov = user.PermissionOverrides.FirstOrDefault(x => x.PermissionKey == request.PermissionKey);
            if (ov is null)
            {
                ov = new Models.UserPermissionOverride { TenantId = tenantId, UserId = targetUserId, PermissionKey = request.PermissionKey, CreatedBy = callerUserId };
                _db.UserPermissionOverrides.Add(ov);
            }
            ov.Effect = effect;
            ov.Reason = request.Reason ?? string.Empty;
            ov.ExpiresAtUtc = request.ExpiresAtUtc;
            ov.IsActive = true;
            ov.UpdatedAtUtc = DateTime.UtcNow;
            ov.UpdatedBy = callerUserId;
        }

        await RevokeActiveRefreshTokensAsync(targetUserId, new RequestContext(null, null, callerUserId, tenantId), cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("access.permission_granted", "UserPermissionOverride", targetUserId.ToString(), new RequestContext(null, null, callerUserId, tenantId),
            $"{{\"permission\":\"{request.PermissionKey}\",\"effect\":\"{request.Effect}\"}}", cancellationToken);
        return await GetUserAccessAsync(tenantId, targetUserId, entityScope, cancellationToken);
    }

    public async Task<UserAccessDto?> GrantPermissionsBulkAsync(Guid tenantId, Guid targetUserId, BulkGrantPermissionsRequest request, EntityScopeContext entityScope, Guid? callerUserId, bool isAdmin, CancellationToken cancellationToken)
    {
        // Normalize + de-dupe by key (last write wins) BEFORE any work.
        var items = request.Items
            .Where(i => !string.IsNullOrWhiteSpace(i.PermissionKey) && !string.IsNullOrWhiteSpace(i.Effect))
            .GroupBy(i => i.PermissionKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToList();
        if (items.Count == 0) throw new InvalidOperationException("No permission changes supplied.");
        if (items.Count > 1000) throw new InvalidOperationException("Too many permission changes in one request.");

        // Self-lockout guard: an admin cannot Deny their own access-management permissions in bulk.
        if (callerUserId is not null && targetUserId == callerUserId.Value)
        {
            var selfDeniedCritical = items
                .Where(i => i.Effect.Equals("Deny", StringComparison.OrdinalIgnoreCase)
                    && i.PermissionKey.StartsWith("access.", StringComparison.OrdinalIgnoreCase))
                .Select(i => i.PermissionKey)
                .ToList();
            if (selfDeniedCritical.Count > 0)
                throw new InvalidOperationException($"You cannot deny your own access-management permissions ({string.Join(", ", selfDeniedCritical.Take(5))}); this would lock you out of the console.");
        }

        // Non-admin grantors: EVERY key must be in scope, else reject the WHOLE batch (all-or-nothing, no partial escalation).
        if (!isAdmin)
        {
            if (callerUserId is null) throw new UnauthorizedAccessException("Authentication required.");
            var grantor = await _db.PermissionGrantorRecords
                .Where(x => x.TenantId == tenantId && x.GrantorUserId == callerUserId.Value && x.IsActive
                    && (x.ExpiresAtUtc == null || x.ExpiresAtUtc > DateTime.UtcNow))
                .ToListAsync(cancellationToken);
            var outOfScope = items.Where(i => !grantor.Any(g => PermissionMatchesScope(i.PermissionKey, g.PermissionScope))).ToList();
            if (outOfScope.Count > 0)
                throw new InvalidOperationException("You are not authorised to grant or revoke one or more of the selected permissions.");
        }

        var user = await LoadAccessUser(tenantId, targetUserId, entityScope, cancellationToken);
        if (user is null) return null;

        // Validate all non-Remove keys exist in ONE query (no per-key round-trips).
        var nonRemoveKeys = items
            .Where(i => !i.Effect.Equals("Remove", StringComparison.OrdinalIgnoreCase))
            .Select(i => i.PermissionKey)
            .ToList();
        if (nonRemoveKeys.Count > 0)
        {
            var known = await _db.Permissions
                .Where(p => nonRemoveKeys.Contains(p.Key))
                .Select(p => p.Key)
                .ToListAsync(cancellationToken);
            var knownSet = known.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unknown = nonRemoveKeys.Where(k => !knownSet.Contains(k)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (unknown.Count > 0) throw new InvalidOperationException($"Unknown permission key(s): {string.Join(", ", unknown.Take(5))}.");
        }

        var now = DateTime.UtcNow;
        int allowed = 0, denied = 0, removed = 0, noop = 0;
        var changed = new List<(string Key, string Effect)>();
        foreach (var item in items)
        {
            var existing = user.PermissionOverrides.FirstOrDefault(x => x.PermissionKey == item.PermissionKey);
            if (item.Effect.Equals("Remove", StringComparison.OrdinalIgnoreCase))
            {
                if (existing is not null && existing.IsActive)
                {
                    existing.IsActive = false;
                    existing.UpdatedAtUtc = now;
                    existing.UpdatedBy = callerUserId;
                    removed++;
                    changed.Add((item.PermissionKey, "Remove"));
                }
                else noop++;   // nothing to reset — skip, no audit noise, no needless revocation
                continue;
            }
            var effect = item.Effect.Equals("Deny", StringComparison.OrdinalIgnoreCase) ? "Deny" : "Allow";
            if (existing is null)
            {
                existing = new Models.UserPermissionOverride { TenantId = tenantId, UserId = targetUserId, PermissionKey = item.PermissionKey, CreatedBy = callerUserId };
                _db.UserPermissionOverrides.Add(existing);
            }
            existing.Effect = effect;
            existing.Reason = request.Reason ?? string.Empty;
            existing.ExpiresAtUtc = null;
            existing.IsActive = true;
            existing.UpdatedAtUtc = now;
            existing.UpdatedBy = callerUserId;
            if (effect == "Allow") allowed++; else denied++;
            changed.Add((item.PermissionKey, effect));
        }

        // All-noop batch (e.g. Reset-all over rows with no overrides): nothing mutated — skip revocation + audit.
        if (changed.Count > 0)
        {
            var ctx = new RequestContext(null, null, callerUserId, tenantId);
            await RevokeActiveRefreshTokensAsync(targetUserId, ctx, cancellationToken);   // ONCE, not per key
            await _db.SaveChangesAsync(cancellationToken);                                 // ONE transaction — atomic, no half-granted user

            // Per-key rows preserve exact single-override audit parity (same action + metadata shape).
            foreach (var c in changed)
            {
                await _auditService.WriteAsync("access.permission_granted", "UserPermissionOverride", targetUserId.ToString(), ctx,
                    System.Text.Json.JsonSerializer.Serialize(new { permission = c.Key, effect = c.Effect }), cancellationToken);
            }
            // Summary row for genuine batches — queryable, attributable, enumerates affected keys (forensic value for a 7-year audit).
            if (changed.Count > 1)
            {
                var metadata = System.Text.Json.JsonSerializer.Serialize(new
                {
                    userId = targetUserId,
                    allowed,
                    denied,
                    removed,
                    noop,
                    total = allowed + denied + removed,
                    reason = request.Reason ?? string.Empty,
                    keys = new
                    {
                        allowed = changed.Where(c => c.Effect == "Allow").Select(c => c.Key).ToList(),
                        denied = changed.Where(c => c.Effect == "Deny").Select(c => c.Key).ToList(),
                        removed = changed.Where(c => c.Effect == "Remove").Select(c => c.Key).ToList()
                    }
                });
                await _auditService.WriteAsync("access.permission_bulk_grant", "UserPermissionOverride", targetUserId.ToString(), ctx, metadata, cancellationToken);
            }
        }

        return await GetUserAccessAsync(tenantId, targetUserId, entityScope, cancellationToken);
    }

    private static bool PermissionMatchesScope(string permissionKey, string scope)
    {
        if (scope.Equals("all", StringComparison.OrdinalIgnoreCase)) return true;
        var parts = scope.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Any(s =>
            s.EndsWith(".*", StringComparison.OrdinalIgnoreCase)
                ? permissionKey.StartsWith(s[..^2] + ".", StringComparison.OrdinalIgnoreCase)
                : permissionKey.Equals(s, StringComparison.OrdinalIgnoreCase));
    }

    // Does parent scope cover (is superset of) child scope?
    private static bool ScopeCoversScope(string parentScope, string childScope)
    {
        if (parentScope.Equals("all", StringComparison.OrdinalIgnoreCase)) return true;
        if (childScope.Equals("all", StringComparison.OrdinalIgnoreCase)) return false;
        var childParts = childScope.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return childParts.All(c => PermissionMatchesScope(c.TrimEnd('*').TrimEnd('.'), parentScope) ||
                                   PermissionMatchesScope(c, parentScope));
    }

    private async Task<IReadOnlyCollection<Role>> LoadRoles(Guid tenantId, IReadOnlyCollection<string> roleNames, CancellationToken cancellationToken)
    {
        var normalizedRoles = roleNames.Select(AuthService.Normalize).Distinct().ToList();
        var roles = await _db.Roles
            .Include(x => x.RolePermissions).ThenInclude(x => x.Permission)
            .Where(x => normalizedRoles.Contains(x.NormalizedName) && (x.TenantId == tenantId || x.TenantId == null))
            .ToListAsync(cancellationToken);
        if (roles.Count != normalizedRoles.Count) throw new InvalidOperationException("One or more roles are invalid for this tenant.");
        return roles;
    }

    private async Task<User?> LoadAccessUser(Guid tenantId, Guid userId, EntityScopeContext entityScope, CancellationToken cancellationToken) =>
        await _db.Users
            .Include(x => x.Tenant)
            .Include(x => x.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x!.RolePermissions).ThenInclude(x => x.Permission)
            .Include(x => x.EmployeeUserAccounts)
            .Include(x => x.PermissionOverrides)
            .ApplyEntityScope(_db, tenantId, entityScope)
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == userId, cancellationToken);

    private static IReadOnlyCollection<string> DefaultRoles(string accessMode) => accessMode switch
    {
        AccessModes.ManagerPortal => new[] { "Employee", "Manager" },
        AccessModes.KioskOnly or AccessModes.NoLogin => Array.Empty<string>(),
        _ => new[] { "Employee" }
    };

    private static string NormalizeAccessMode(string accessMode) => accessMode.Trim().Replace(" ", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant() switch
    {
        "fullportal" => AccessModes.FullPortal,
        "essonly" => AccessModes.EssOnly,
        "managerportal" => AccessModes.ManagerPortal,
        "hrportal" => AccessModes.HRPortal,
        "payrollportal" => AccessModes.PayrollPortal,
        "financeportal" => AccessModes.FinancePortal,
        "supervisorportal" => AccessModes.SupervisorPortal,
        "readonlyauditor" => AccessModes.ReadOnlyAuditor,
        "mobile" => AccessModes.Mobile,
        "kioskonly" => AccessModes.KioskOnly,
        "nologin" => AccessModes.NoLogin,
        _ => throw new InvalidOperationException("Invalid access mode.")
    };

    private static UserAccessDto ToAccessDto(User user)
    {
        var link = user.EmployeeUserAccounts.Where(x => !x.IsDeleted).OrderByDescending(x => x.IsPrimary).FirstOrDefault();
        var roles = user.UserRoles.Select(x => x.Role?.Name).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct().OrderBy(x => x).ToList();
        var basePermissions = user.UserRoles.SelectMany(x => x.Role?.RolePermissions ?? Array.Empty<RolePermission>()).Select(x => x.Permission?.Key).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var ov in user.PermissionOverrides.Where(x => x.IsActive && (x.ExpiresAtUtc is null || x.ExpiresAtUtc > DateTime.UtcNow)))
        {
            if (ov.Effect == "Allow") basePermissions.Add(ov.PermissionKey);
            if (ov.Effect == "Deny") basePermissions.Remove(ov.PermissionKey);
        }
        var denied = user.PermissionOverrides
            .Where(x => x.IsActive && x.Effect == "Deny")
            .Select(x => x.PermissionKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToList();
        return new UserAccessDto(user.Id, link?.EmployeeId, user.Email, user.FullName, link?.AccessMode ?? user.AccessMode, link?.RequiresPasswordSetup ?? false, roles, basePermissions.OrderBy(x => x).ToList(), denied);
    }

    private static AuthUserDto ToUserDto(User user, Tenant tenant, IReadOnlyCollection<Role> roles)
    {
        var permissions = roles
            .SelectMany(x => x.RolePermissions)
            .Select(x => x.Permission!.Key)
            .Distinct()
            .OrderBy(x => x)
            .ToList();
        return new AuthUserDto(user.Id, user.TenantId, tenant.Slug, user.Email, user.FullName, roles.Select(x => x.Name).OrderBy(x => x).ToList(), permissions);
    }

    private static ApprovalDelegationDto ToDelegationDto(ApprovalDelegation delegation) =>
        new(delegation.Id, delegation.FromEmployeeId, delegation.ToEmployeeId, delegation.FromUserId, delegation.ToUserId, delegation.Scope, delegation.StartDate, delegation.EndDate, delegation.Status, delegation.Reason);

    private static ApprovalAuthorityDto ToAuthorityDto(ApprovalAuthority authority) =>
        new(authority.Id, authority.EmployeeId, authority.UserId, authority.AuthorityScope, authority.ApproverRole, authority.AmountLimit, authority.Currency, authority.CanFinalApprove, authority.IsActive);

    private static UserListDto ToUserListDto(User user)
    {
        var roles = user.UserRoles.Select(x => x.Role?.Name).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct().OrderBy(x => x).ToList();
        var link = user.EmployeeUserAccounts.Where(x => !x.IsDeleted).OrderByDescending(x => x.IsPrimary).FirstOrDefault();
        return new UserListDto(user.Id, user.Email, user.FullName, user.PhoneNumber, user.Status, user.IsActive, user.IsLocked, user.MustChangePassword, roles, link?.AccessMode ?? user.AccessMode, link?.EmployeeId, user.LastLoginAtUtc, user.CreatedAtUtc);
    }

    private static SecuritySettingDto ToSecuritySettingDto(Models.SecuritySetting s) =>
        new(s.Id, s.TenantId, s.PasswordMinLength, s.PasswordRequireUppercase, s.PasswordRequireLowercase, s.PasswordRequireDigit, s.PasswordRequireSpecial, s.PasswordExpiryDays, s.PasswordHistoryCount, s.MaxFailedLoginAttempts, s.LockoutDurationMinutes, s.SessionTimeoutMinutes, s.RefreshTokenExpiryDays, s.AllowMultipleSessions, s.MfaRequired, s.UpdatedAtUtc);

    private static RoleDto ToRoleDto(Role role) =>
        new(role.Id, role.Name, role.Description, role.IsSystem, role.IsActive, role.IsEditable, role.AuthorityLevel,
            role.RolePermissions.Select(rp => rp.Permission!.Key).OrderBy(p => p).ToList());

    // ── Role CRUD ─────────────────────────────────────────────────────────────

    public async Task<RoleDto> CreateRoleAsync(Guid tenantId, CreateRoleRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var normalized = AuthService.Normalize(request.Name);
        var exists = await _db.Roles.AnyAsync(x => x.TenantId == tenantId && x.NormalizedName == normalized && !x.IsDeleted, cancellationToken);
        if (exists) throw new InvalidOperationException($"A role named '{request.Name}' already exists.");

        var permissions = request.Permissions != null && request.Permissions.Count > 0
            ? await _db.Permissions.Where(x => request.Permissions.Contains(x.Key)).ToListAsync(cancellationToken)
            : new List<Domain.Entities.Permission>();

        var role = new Role
        {
            TenantId = tenantId,
            Name = request.Name.Trim(),
            NormalizedName = normalized,
            Description = request.Description?.Trim() ?? string.Empty,
            AuthorityLevel = request.AuthorityLevel,
            IsSystem = false,
            IsActive = true,
            IsEditable = true
        };
        foreach (var p in permissions) role.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = p.Id, Permission = p });
        _db.Roles.Add(role);
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("access.role_created", "Role", role.Id.ToString(), context, $"{{\"name\":\"{role.Name}\"}}", cancellationToken);
        return ToRoleDto(role);
    }

    public async Task<RoleDto?> UpdateRoleAsync(Guid tenantId, Guid roleId, UpdateRoleRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var role = await _db.Roles.Include(x => x.RolePermissions).ThenInclude(x => x.Permission)
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == roleId && !x.IsDeleted, cancellationToken);
        if (role is null) return null;
        if (!role.IsEditable) throw new InvalidOperationException("This role is not editable.");

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            var normalized = AuthService.Normalize(request.Name);
            var conflict = await _db.Roles.AnyAsync(x => x.TenantId == tenantId && x.NormalizedName == normalized && x.Id != roleId && !x.IsDeleted, cancellationToken);
            if (conflict) throw new InvalidOperationException($"A role named '{request.Name}' already exists.");
            role.Name = request.Name.Trim();
            role.NormalizedName = normalized;
        }
        if (request.Description is not null) role.Description = request.Description.Trim();
        if (request.AuthorityLevel.HasValue) role.AuthorityLevel = request.AuthorityLevel.Value;
        role.UpdatedAtUtc = DateTime.UtcNow;
        role.UpdatedBy = context.UserId;
        await RevokeActiveRefreshTokensForRoleAsync(role.Id, context, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("access.role_updated", "Role", roleId.ToString(), context, $"{{\"name\":\"{role.Name}\"}}", cancellationToken);
        return ToRoleDto(role);
    }

    public async Task<bool> ActivateRoleAsync(Guid tenantId, Guid roleId, RequestContext context, CancellationToken cancellationToken)
    {
        var role = await _db.Roles.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == roleId && !x.IsDeleted, cancellationToken);
        if (role is null) return false;
        role.IsActive = true;
        role.UpdatedAtUtc = DateTime.UtcNow;
        role.UpdatedBy = context.UserId;
        await RevokeActiveRefreshTokensForRoleAsync(role.Id, context, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("access.role_activated", "Role", roleId.ToString(), context, null, cancellationToken);
        return true;
    }

    public async Task<bool> DeactivateRoleAsync(Guid tenantId, Guid roleId, RequestContext context, CancellationToken cancellationToken)
    {
        var role = await _db.Roles.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == roleId && !x.IsDeleted, cancellationToken);
        if (role is null) return false;
        if (role.IsSystem) throw new InvalidOperationException("System roles cannot be deactivated.");
        role.IsActive = false;
        role.UpdatedAtUtc = DateTime.UtcNow;
        role.UpdatedBy = context.UserId;
        await RevokeActiveRefreshTokensForRoleAsync(role.Id, context, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("access.role_deactivated", "Role", roleId.ToString(), context, null, cancellationToken);
        return true;
    }

    public async Task<RoleDto?> SetRolePermissionsAsync(Guid tenantId, Guid roleId, BulkRolePermissionsRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var role = await _db.Roles.Include(x => x.RolePermissions).ThenInclude(x => x.Permission)
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == roleId && !x.IsDeleted, cancellationToken);
        if (role is null) return null;

        var permissions = await _db.Permissions.Where(x => request.Permissions.Contains(x.Key)).ToListAsync(cancellationToken);
        _db.RolePermissions.RemoveRange(role.RolePermissions);
        role.RolePermissions.Clear();
        foreach (var p in permissions) role.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = p.Id, Permission = p });
        role.UpdatedAtUtc = DateTime.UtcNow;
        role.UpdatedBy = context.UserId;
        await RevokeActiveRefreshTokensForRoleAsync(role.Id, context, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("access.role_permissions_set", "Role", roleId.ToString(), context, $"{{\"count\":{permissions.Count}}}", cancellationToken);
        return ToRoleDto(role);
    }

    // ── Permission Matrix ─────────────────────────────────────────────────────

    public async Task<PermissionMatrixDto> GetPermissionMatrixAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var roles = await _db.Roles
            .Include(x => x.RolePermissions).ThenInclude(x => x.Permission)
            .Where(x => (x.TenantId == tenantId || x.TenantId == null) && !x.IsDeleted && x.IsActive)
            .OrderBy(x => x.AuthorityLevel).ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);

        var permissions = await _db.Permissions.OrderBy(x => x.Module).ThenBy(x => x.Key).ToListAsync(cancellationToken);
        var roleGrantMap = roles.ToDictionary(r => r.Id, r => r.RolePermissions.Select(rp => rp.PermissionId).ToHashSet());

        var matrix = permissions.Select(p => new PermissionMatrixRow(
            p.Key, p.Module, p.Description,
            roles.ToDictionary(r => r.Id.ToString(), r => roleGrantMap[r.Id].Contains(p.Id))
        )).ToList();

        return new PermissionMatrixDto(roles.Select(ToRoleDto).ToList(), matrix);
    }

    public async Task SavePermissionMatrixAsync(Guid tenantId, PermissionMatrixUpdateRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var roleIds = request.RolePermissions.Keys.Select(k => Guid.TryParse(k, out var g) ? g : (Guid?)null).Where(x => x.HasValue).Select(x => x!.Value).ToList();
        var roles = await _db.Roles.Include(x => x.RolePermissions)
            .Where(x => (x.TenantId == tenantId || x.TenantId == null) && roleIds.Contains(x.Id) && !x.IsDeleted)
            .ToListAsync(cancellationToken);
        var allPermissions = await _db.Permissions.ToListAsync(cancellationToken);
        var permMap = allPermissions.ToDictionary(p => p.Key, p => p, StringComparer.OrdinalIgnoreCase);

        foreach (var role in roles)
        {
            if (!request.RolePermissions.TryGetValue(role.Id.ToString(), out var permKeys)) continue;
            _db.RolePermissions.RemoveRange(role.RolePermissions);
            role.RolePermissions.Clear();
            foreach (var key in permKeys.Where(k => permMap.ContainsKey(k)))
                role.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permMap[key].Id });
            role.UpdatedAtUtc = DateTime.UtcNow;
            role.UpdatedBy = context.UserId;
        }
        await RevokeActiveRefreshTokensForRolesAsync(roles.Select(r => r.Id), context, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("access.permission_matrix_saved", "Tenant", tenantId.ToString(), context, null, cancellationToken);
    }

    // ── Effective permissions ─────────────────────────────────────────────────

    public async Task<EffectivePermissionsDto?> GetEffectivePermissionsAsync(Guid tenantId, Guid userId, EntityScopeContext entityScope, CancellationToken cancellationToken)
    {
        var user = await LoadAccessUser(tenantId, userId, entityScope, cancellationToken);
        if (user is null) return null;
        var roles = user.UserRoles.Select(x => x.Role?.Name).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct().OrderBy(x => x).ToList();
        var byRole = user.UserRoles.SelectMany(x => x.Role?.RolePermissions ?? Array.Empty<RolePermission>()).Select(x => x.Permission?.Key).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        var activeOverrides = user.PermissionOverrides.Where(x => x.IsActive && (x.ExpiresAtUtc is null || x.ExpiresAtUtc > DateTime.UtcNow)).ToList();
        var allowed = activeOverrides.Where(x => x.Effect == "Allow").Select(x => x.PermissionKey).OrderBy(x => x).ToList();
        var denied = activeOverrides.Where(x => x.Effect == "Deny").Select(x => x.PermissionKey).OrderBy(x => x).ToList();
        var effective = byRole.Concat(allowed).Distinct(StringComparer.OrdinalIgnoreCase).Where(p => !denied.Contains(p, StringComparer.OrdinalIgnoreCase)).OrderBy(x => x).ToList();
        return new EffectivePermissionsDto(user.Id, user.Email, roles, byRole, allowed, denied, effective);
    }

    // ── Permission override delete ─────────────────────────────────────────────

    public async Task<bool> DeletePermissionOverrideAsync(Guid tenantId, Guid userId, Guid overrideId, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken)
    {
        if (!await _db.Users.ApplyEntityScope(_db, tenantId, entityScope).AnyAsync(x => x.TenantId == tenantId && x.Id == userId && !x.IsDeleted, cancellationToken))
            return false;
        var ov = await _db.UserPermissionOverrides.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.UserId == userId && x.Id == overrideId, cancellationToken);
        if (ov is null) return false;
        _db.UserPermissionOverrides.Remove(ov);
        await RevokeActiveRefreshTokensAsync(userId, context, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("access.permission_override_deleted", "UserPermissionOverride", overrideId.ToString(), context, null, cancellationToken);
        return true;
    }

    private async Task RevokeActiveRefreshTokensForRoleAsync(Guid roleId, RequestContext context, CancellationToken cancellationToken)
    {
        var userIds = await _db.UserRoles
            .Where(x => x.RoleId == roleId)
            .Select(x => x.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
        await RevokeActiveRefreshTokensAsync(userIds, context, cancellationToken);
    }

    private async Task RevokeActiveRefreshTokensForRolesAsync(IEnumerable<Guid> roleIds, RequestContext context, CancellationToken cancellationToken)
    {
        var ids = roleIds.Distinct().ToList();
        if (ids.Count == 0) return;
        var userIds = await _db.UserRoles
            .Where(x => ids.Contains(x.RoleId))
            .Select(x => x.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
        await RevokeActiveRefreshTokensAsync(userIds, context, cancellationToken);
    }

    private Task RevokeActiveRefreshTokensAsync(Guid userId, RequestContext context, CancellationToken cancellationToken) =>
        RevokeActiveRefreshTokensAsync(new[] { userId }, context, cancellationToken);

    private async Task RevokeActiveRefreshTokensAsync(IEnumerable<Guid> userIds, RequestContext context, CancellationToken cancellationToken)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0) return;
        var now = DateTime.UtcNow;
        var activeTokens = await _db.RefreshTokens
            .Where(x => ids.Contains(x.UserId) && x.RevokedAtUtc == null && x.ExpiresAtUtc > now)
            .ToListAsync(cancellationToken);
        foreach (var token in activeTokens)
        {
            token.RevokedAtUtc = now;
            token.RevokedByIp = context.IpAddress;
        }
    }
}

internal static class AccessManagementScopeExtensions
{
    public static IQueryable<User> ApplyEntityScope(this IQueryable<User> query, ZayraDbContext db, Guid tenantId, EntityScopeContext scope)
    {
        if (scope.IsGroupLevel) return query;
        var companyIds = scope.AccessibleCompanyIds.ToList();
        if (companyIds.Count == 0) return query.Where(_ => false);

        return query.Where(u =>
            u.EmployeeUserAccounts.Any(link => !link.IsDeleted
                && db.Employees.Any(e => e.TenantId == tenantId
                    && !e.IsDeleted
                    && e.Id == link.EmployeeId
                    && e.CompanyId.HasValue
                    && companyIds.Contains(e.CompanyId.Value)))
            || u.EntityAccesses.Any(grant => grant.TenantId == tenantId
                && grant.IsActive
                && grant.GrantMode == EntityGrantModes.SelectedCompanies
                && grant.CompanyId.HasValue
                && companyIds.Contains(grant.CompanyId.Value)));
    }
}
