using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Buffers;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Auth;

public class AccessManagementService : IAccessManagementService
{
    private readonly ZayraDbContext _db;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IAuditService _auditService;
    private readonly ITokenService _tokenService;
    private readonly string _appUrl;

    public AccessManagementService(ZayraDbContext db, IPasswordHasher passwordHasher, IAuditService auditService, ITokenService tokenService, IConfiguration? configuration = null)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _auditService = auditService;
        _tokenService = tokenService;
        _appUrl = AuthLinkBuilder.ResolvePublicAppUrl(
            configuration?["APP_URL"] ?? Environment.GetEnvironmentVariable("APP_URL"));
    }

    public async Task<IReadOnlyCollection<RoleDto>> GetRolesAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var roles = await _db.Roles
            .Include(x => x.RolePermissions).ThenInclude(x => x.Permission)
            .Where(x => (x.TenantId == tenantId || x.TenantId == null) && x.IsActive && !x.IsDeleted)
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
        var normalizedEmail = AuthService.Normalize(request.Email);
        var canonicalEmail = request.Email.Trim().ToLowerInvariant();
        var fullName = request.FullName.Trim();
        var normalizedRoleNames = request.Roles
            .Select(AuthService.Normalize)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        var userId = Guid.NewGuid();
        var auditId = Guid.NewGuid();
        var createdAtUtc = ToDatabasePrecisionUtc(DateTime.UtcNow);
        var passwordHash = _passwordHasher.Hash(request.Password);
        var auditMetadata = System.Text.Json.JsonSerializer.Serialize(new
        {
            email = canonicalEmail,
            roles = request.Roles.Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal).ToList()
        });
        var isAdminUser = normalizedRoleNames.Contains("ADMIN", StringComparer.Ordinal);

        Guid[]? expectedRoleIds = null;

        async Task<AuthUserDto?> ReconcileCommittedUserAsync(CancellationToken ct)
        {
            if (expectedRoleIds is null) return null;
            _db.ChangeTracker.Clear();
            // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
            var committed = await _db.Users.IgnoreQueryFilters().AsNoTracking()
                .Include(x => x.Tenant)
                .Include(x => x.UserRoles).ThenInclude(x => x.Role)
                    .ThenInclude(x => x!.RolePermissions).ThenInclude(x => x.Permission)
                .SingleOrDefaultAsync(x => x.Id == userId && x.TenantId == tenantId, ct);
            if (committed?.Tenant is null
                || committed.IsDeleted
                || !committed.IsActive
                || !committed.IsEmailConfirmed
                || committed.IsLocked
                || committed.LockoutEnd is not null
                || committed.MustChangePassword
                || !string.Equals(committed.Status, "Active", StringComparison.Ordinal)
                || !string.Equals(committed.AccessMode, AccessModes.FullPortal, StringComparison.Ordinal)
                || !string.Equals(committed.IdentityProvider, "Local", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(committed.ProvisioningSource, "Local", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(committed.Email, canonicalEmail, StringComparison.Ordinal)
                || !string.Equals(committed.NormalizedEmail, normalizedEmail, StringComparison.Ordinal)
                || !string.Equals(committed.FullName, fullName, StringComparison.Ordinal)
                || !string.Equals(committed.PasswordHash, passwordHash, StringComparison.Ordinal)
                || committed.IsGroupScope != isAdminUser)
                return null;

            var committedRoleIds = committed.UserRoles.Select(x => x.RoleId).OrderBy(x => x).ToArray();
            if (!committedRoleIds.SequenceEqual(expectedRoleIds)) return null;
            if (committed.UserRoles.Any(x => x.Role is not { IsActive: true, IsDeleted: false })) return null;

            // IgnoreQueryFilters is intentional: commit verification of this command's own audit marker by its server-generated id; no tenant data is read (register §6).
            var markerMetadata = await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.Id == auditId
                    && x.TenantId == tenantId
                    && x.Action == "access.user_created"
                    && x.EntityName == "User"
                    && x.EntityId == userId.ToString())
                .Select(x => x.Metadata)
                .SingleOrDefaultAsync(ct);
            if (!string.Equals(markerMetadata, auditMetadata, StringComparison.Ordinal)) return null;

            return ToUserDto(
                committed,
                committed.Tenant,
                committed.UserRoles.Select(x => x.Role!).ToList());
        }

        async Task<bool> CreateOnceAsync(CancellationToken ct)
        {
            _db.ChangeTracker.Clear();
            await AcquireAdminSeatLockAsync(tenantId, isAdminUser, ct);
            var tenant = await _db.Tenants.TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == tenantId && x.IsActive, ct)
                ?? throw new InvalidOperationException("Tenant not found.");

            // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
            var policy = await _db.SecuritySettings.IgnoreQueryFilters()
                .TagWith(RowLockingInterceptor.ForShareTag)
                .SingleOrDefaultAsync(x => x.TenantId == tenantId, ct);
            ValidatePasswordAgainstPolicy(request.Password, policy);

            var conflictingUsers = await _db.Users.IgnoreQueryFilters()
                .TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => x.TenantId == tenantId && x.NormalizedEmail == normalizedEmail)
                .OrderBy(x => x.Id)
                .Take(2)
                .Select(x => x.Id)
                .ToListAsync(ct);
            if (conflictingUsers.Count != 0)
                throw new InvalidOperationException("A user with this email already exists in this tenant.");

            // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
            var roles = await _db.Roles.IgnoreQueryFilters()
                .TagWith(RowLockingInterceptor.ForShareTag)
                .Where(x => normalizedRoleNames.Contains(x.NormalizedName)
                    && (x.TenantId == tenantId || x.TenantId == null)
                    && x.IsActive
                    && !x.IsDeleted)
                .OrderBy(x => x.Id)
                .ToListAsync(ct);
            if (roles.Count != normalizedRoleNames.Count)
                throw new InvalidOperationException("One or more roles are invalid for this tenant.");
            expectedRoleIds = roles.Select(x => x.Id).OrderBy(x => x).ToArray();

            // PRIVILEGE CEILING: the creator sets this account's password, so every role it is created with must
            // sit inside the creator's own access — otherwise "create a user" is "mint myself a bigger login".
            var caller = await LoadCallerCeilingAsync(tenantId, context, ct);
            var rolePermissions = await _db.RolePermissions.AsNoTracking()
                .Where(x => expectedRoleIds.Contains(x.RoleId) && x.Permission != null)
                .Select(x => new { x.RoleId, x.Permission!.Key })
                .ToListAsync(ct);
            foreach (var role in roles.OrderBy(x => x.Name, StringComparer.Ordinal))
                ThrowIfRefused(PrivilegeCeiling.AssignRefusal(caller, new PrivilegeCeiling.RoleFacts(
                    role.Id, role.Name, role.NormalizedName, role.TenantId, role.IsSystem, role.IsEditable,
                    rolePermissions.Where(x => x.RoleId == role.Id).Select(x => x.Key).ToList())));

            if (isAdminUser) await EnsureAdminCapacityAsync(tenantId, ct);

            var user = new User
            {
                Id = userId,
                TenantId = tenantId,
                Tenant = tenant,
                Email = canonicalEmail,
                NormalizedEmail = normalizedEmail,
                FullName = fullName,
                PasswordHash = passwordHash,
                Status = "Active",
                AccessMode = AccessModes.FullPortal,
                IdentityProvider = "Local",
                ProvisioningSource = "Local",
                IsGroupScope = isAdminUser,
                IsActive = true,
                IsEmailConfirmed = true,
                CreatedAtUtc = createdAtUtc
            };
            _db.Users.Add(user);
            foreach (var role in roles)
                _db.UserRoles.Add(new UserRole { UserId = userId, RoleId = role.Id, User = user, Role = role });
            _db.AuditLogs.Add(AuthAuditEntry.Create(
                auditId,
                createdAtUtc,
                "access.user_created",
                "User",
                userId.ToString(),
                context with { TenantId = tenantId },
                auditMetadata));
            await _db.SaveChangesAsync(ct);
            return true;
        }

        if (_db.Database.IsRelational())
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteInTransactionAsync(
                CreateOnceAsync,
                async ct => await ReconcileCommittedUserAsync(ct) is not null,
                IsolationLevel.ReadCommitted,
                cancellationToken);
        }
        else
        {
            await CreateOnceAsync(cancellationToken);
        }

        return await ReconcileCommittedUserAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                "The user-creation commit could not be reconciled; the operation result was not disclosed.");
    }

    public async Task<EmployeeLoginInvitationDto> InviteEmployeeLoginAsync(
        Guid tenantId,
        InviteEmployeeLoginRequest request,
        EntityScopeContext entityScope,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        var accessMode = NormalizeAccessMode(request.AccessMode);
        var issuedAtUtc = ToDatabasePrecisionUtc(DateTime.UtcNow);
        var expiresAtUtc = accessMode == AccessModes.NoLogin
            ? (DateTime?)null
            : issuedAtUtc.AddHours(Math.Clamp(request.InvitationHours, 1, 720));
        var expectedLinkStatus = accessMode == AccessModes.NoLogin ? "NoLogin" : "Invited";
        var expectedUserStatus = accessMode == AccessModes.NoLogin ? "PendingPasswordSetup" : "Invited";
        var invitationToken = accessMode == AccessModes.NoLogin ? string.Empty : _tokenService.CreateSecureToken();
        var invitationHash = string.IsNullOrEmpty(invitationToken) ? string.Empty : _tokenService.HashToken(invitationToken);
        var unreachablePasswordHash = _passwordHasher.Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)));
        var newUserId = Guid.NewGuid();
        var newLinkId = Guid.NewGuid();
        var newEntityGrantId = Guid.NewGuid();
        var auditId = Guid.NewGuid();
        var auditMetadata = System.Text.Json.JsonSerializer.Serialize(new
        {
            employeeId = request.EmployeeId,
            accessMode,
            roles = (request.Roles is { Count: > 0 } ? request.Roles : DefaultRoles(accessMode))
                .Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal).ToList()
        });
        Guid? issuedUserId = null;
        Guid? issuedLinkId = null;
        string? issuedEmail = null;
        string? issuedNormalizedEmail = null;
        Guid[]? issuedRoleIds = null;

        async Task<EmployeeLoginInvitationDto?> ReconcileCommittedInvitationAsync(CancellationToken ct)
        {
            if (!issuedUserId.HasValue
                || !issuedLinkId.HasValue
                || issuedEmail is null
                || issuedNormalizedEmail is null
                || issuedRoleIds is null)
                return null;

            _db.ChangeTracker.Clear();
            // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
            var committed = await _db.EmployeeUserAccounts.IgnoreQueryFilters().AsNoTracking()
                .Include(x => x.User).ThenInclude(x => x!.Tenant)
                .Include(x => x.User).ThenInclude(x => x!.UserRoles)
                .SingleOrDefaultAsync(x => x.Id == issuedLinkId.Value
                    && x.TenantId == tenantId
                    && x.EmployeeId == request.EmployeeId
                    && !x.IsDeleted, ct);
            var user = committed?.User;
            if (committed is null
                || user?.Tenant is null
                || committed.UserId != issuedUserId.Value
                || !string.Equals(committed.AccessMode, accessMode, StringComparison.Ordinal)
                || !string.Equals(committed.Status, expectedLinkStatus, StringComparison.Ordinal)
                || committed.RequiresPasswordSetup != (accessMode != AccessModes.NoLogin)
                || !string.Equals(committed.InvitationTokenHash, invitationHash, StringComparison.Ordinal)
                || committed.InvitedAtUtc != issuedAtUtc
                || committed.InvitationExpiresAtUtc != expiresAtUtc
                || committed.InvitationAcceptedAtUtc is not null
                || user.Id != issuedUserId.Value
                || user.TenantId != tenantId
                || user.IsDeleted
                || user.IsActive
                || user.IsEmailConfirmed
                || user.IsLocked
                || user.LockoutEnd is not null
                || user.MustChangePassword
                || user.IsGroupScope
                || user.FailedLoginCount != 0
                || user.MFAEnabled
                || user.MfaSecretEncrypted is not null
                || user.MfaConfiguredAtUtc is not null
                || user.MfaLastVerifiedAtUtc is not null
                || user.MfaFailedCount != 0
                || !string.Equals(user.Email, issuedEmail, StringComparison.Ordinal)
                || !string.Equals(user.NormalizedEmail, issuedNormalizedEmail, StringComparison.Ordinal)
                || !string.Equals(user.PasswordHash, unreachablePasswordHash, StringComparison.Ordinal)
                || !string.Equals(user.Status, expectedUserStatus, StringComparison.Ordinal)
                || !string.Equals(user.AccessMode, AccessModes.NoLogin, StringComparison.Ordinal)
                || !string.Equals(user.IdentityProvider, "Local", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(user.ProvisioningSource, "Local", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(user.ExternalId)
                || user.LastProvisionedAtUtc is not null)
                return null;

            var committedRoleIds = user.UserRoles.Select(x => x.RoleId).OrderBy(x => x).ToArray();
            if (!committedRoleIds.SequenceEqual(issuedRoleIds)) return null;

            var expectedEmployeePointer = accessMode == AccessModes.NoLogin ? (Guid?)null : issuedUserId.Value;
            // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
            var employeeMatches = await _db.Employees.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(x => x.TenantId == tenantId
                    && x.Id == request.EmployeeId
                    && !x.IsDeleted
                    && x.UserAccountId == expectedEmployeePointer, ct);
            if (!employeeMatches) return null;

            var markerMetadata = await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.Id == auditId
                    && x.TenantId == tenantId
                    && x.Action == "access.employee_invited"
                    && x.EntityName == "EmployeeUserAccount"
                    && x.EntityId == issuedLinkId.Value.ToString())
                .Select(x => x.Metadata)
                .SingleOrDefaultAsync(ct);
            if (!string.Equals(markerMetadata, auditMetadata, StringComparison.Ordinal)) return null;

            return new EmployeeLoginInvitationDto(
                user.Id,
                request.EmployeeId,
                user.Email,
                accessMode,
                committed.Status,
                invitationToken,
                committed.InvitationExpiresAtUtc,
                string.IsNullOrEmpty(invitationToken)
                    ? string.Empty
                    : AuthLinkBuilder.AcceptInvitation(_appUrl, user.Tenant.Slug, invitationToken));
        }

        async Task<bool> IssueOnceAsync(CancellationToken ct)
        {
            _db.ChangeTracker.Clear();
            var tenant = await _db.Tenants.TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == tenantId && x.IsActive, ct)
                ?? throw new InvalidOperationException("Tenant not found.");
            var employee = await _db.Employees.TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == request.EmployeeId && !x.IsDeleted, ct)
                ?? throw new InvalidOperationException("Employee not found.");
            if (!AuthCurrentEligibility.IsEmployeeLifecycleEligible(employee.Status))
                throw new InvalidOperationException(
                    "Login invitations are available only for active or invited employees.");
            // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
            var employeeLinks = await _db.EmployeeUserAccounts.IgnoreQueryFilters()
                .TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => x.TenantId == tenantId && x.EmployeeId == employee.Id && !x.IsDeleted)
                .OrderBy(x => x.Id)
                .ToListAsync(ct);
            if (employeeLinks.Count > 1)
                throw new InvalidOperationException(
                    "The employee has more than one login mapping. Resolve the identity explicitly before issuing an invitation.");
            if (!entityScope.IsGroupLevel
                && (!employee.CompanyId.HasValue || !entityScope.CanAccessCompany(employee.CompanyId.Value)))
                throw new InvalidOperationException("Employee not found.");

            var email = (request.Email ?? employee.WorkEmail).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(email))
                throw new InvalidOperationException("Employee login requires a work email or explicit email.");
            var normalizedEmail = AuthService.Normalize(email);

            // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
            var matchingIds = await _db.Users.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.NormalizedEmail == normalizedEmail)
                .Select(x => x.Id).Take(2).ToListAsync(ct);
            if (matchingIds.Count > 1)
                throw new InvalidOperationException(
                    "This email resolves to more than one identity. Resolve the identity explicitly before issuing an invitation.");

            User user;
            if (matchingIds.Count == 0)
            {
                if (employee.UserAccountId.HasValue || employeeLinks.Count != 0)
                    throw new InvalidOperationException("The employee is already linked to a different login identity.");
                user = new User
                {
                    Id = newUserId,
                    TenantId = tenantId,
                    Email = email,
                    NormalizedEmail = normalizedEmail,
                    FullName = employee.FullName,
                    PasswordHash = unreachablePasswordHash,
                    Status = "PendingPasswordSetup",
                    AccessMode = AccessModes.NoLogin,
                    IsActive = false,
                    IsEmailConfirmed = false,
                    MustChangePassword = false
                };
                _db.Users.Add(user);
            }
            else
            {
                var existingId = matchingIds[0];
                if (employeeLinks.Any(x => x.UserId != existingId))
                    throw new InvalidOperationException(
                        "The employee is already linked to a different login identity.");
                // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
                await _db.Users.IgnoreQueryFilters().TagWith(RowLockingInterceptor.ForUpdateTag)
                    .Where(x => x.Id == existingId && x.TenantId == tenantId)
                    .Select(x => x.Id)
                    .SingleAsync(ct);
                await _db.EmployeeUserAccounts.IgnoreQueryFilters().TagWith(RowLockingInterceptor.ForUpdateTag)
                    .Where(x => x.TenantId == tenantId && x.UserId == existingId)
                    .OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(ct);
                await _db.UserRoles.TagWith(RowLockingInterceptor.ForUpdateTag)
                    .Where(x => x.UserId == existingId)
                    .OrderBy(x => x.RoleId).Select(x => x.RoleId).ToListAsync(ct);
                // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
                await _db.UserEntityAccesses.IgnoreQueryFilters().TagWith(RowLockingInterceptor.ForUpdateTag)
                    .Where(x => x.TenantId == tenantId && x.UserId == existingId)
                    .OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(ct);
                await _db.UserPermissionOverrides.IgnoreQueryFilters().TagWith(RowLockingInterceptor.ForUpdateTag)
                    .Where(x => x.TenantId == tenantId && x.UserId == existingId)
                    .OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(ct);
                var staleGrantorRecords = await _db.PermissionGrantorRecords.IgnoreQueryFilters()
                    .TagWith(RowLockingInterceptor.ForUpdateTag)
                    .Where(x => x.TenantId == tenantId && x.GrantorUserId == existingId && x.IsActive)
                    .OrderBy(x => x.Id)
                    .ToListAsync(ct);
                foreach (var grantor in staleGrantorRecords)
                    grantor.IsActive = false;

                // Split: primary-key filter inside this anchored transaction (AuthGraphSnapshot).
                // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
                user = await _db.Users.IgnoreQueryFilters().AsSplitQuery()
                    .Include(x => x.Tenant)
                    .Include(x => x.UserRoles).ThenInclude(x => x.Role)
                    .Include(x => x.EmployeeUserAccounts)
                    .Include(x => x.EntityAccesses)
                    .Include(x => x.PermissionOverrides)
                    .SingleAsync(x => x.Id == existingId && x.TenantId == tenantId, ct);
                if (!await AuthTenantGraphIntegrity.IsValidAsync(user, _db, ct))
                    throw new InvalidOperationException(
                        "The existing identity graph is inconsistent. Resolve it explicitly before issuing an invitation.");

                var sameEmployeeLink = user.EmployeeUserAccounts.Any(x =>
                    !x.IsDeleted && x.TenantId == tenantId && x.EmployeeId == employee.Id);
                var hasOtherEmployeeLink = user.EmployeeUserAccounts.Any(x =>
                    !x.IsDeleted && x.TenantId == tenantId && x.EmployeeId != employee.Id);
                var contradictoryPointer = employee.UserAccountId.HasValue && employee.UserAccountId != user.Id;
                var explicitlyLinked = employee.UserAccountId == user.Id || sameEmployeeLink;
                var safelyStaged = explicitlyLinked
                    && !contradictoryPointer
                    && !hasOtherEmployeeLink
                    && !user.IsDeleted
                    && !user.IsActive
                    && !user.IsLocked
                    && (!user.LockoutEnd.HasValue || user.LockoutEnd.Value <= issuedAtUtc)
                    && !user.MustChangePassword
                    && string.Equals(user.AccessMode, AccessModes.NoLogin, StringComparison.Ordinal)
                    && string.Equals(user.IdentityProvider, "Local", StringComparison.OrdinalIgnoreCase)
                    && user.Status is "Invited" or "PendingPasswordSetup";
                if (!safelyStaged)
                    throw new InvalidOperationException(
                        "This email belongs to an existing identity. Resolve the identity explicitly before issuing an invitation.");
                user.FullName = employee.FullName;
            }

            var roles = await LoadRoles(
                tenantId,
                request.Roles is { Count: > 0 } ? request.Roles : DefaultRoles(accessMode),
                ct);
            if (roles.Any(x => x.NormalizedName == "ADMIN"
                    || x.RolePermissions.Any(rp => string.Equals(
                        rp.Permission?.Key,
                        "security.manage",
                        StringComparison.OrdinalIgnoreCase))))
                throw new InvalidOperationException(
                    "Employee invitation cannot grant privileged security administration; use the dedicated privileged-user workflow.");
            // PRIVILEGE CEILING: with no mail transport the invitation link comes back to the inviter, so an
            // invitation may carry only roles inside the inviter's own access.
            var inviter = await LoadCallerCeilingAsync(tenantId, context, ct);
            foreach (var role in roles.OrderBy(x => x.Name, StringComparer.Ordinal))
                ThrowIfRefused(PrivilegeCeiling.AssignRefusal(inviter, Facts(role)));
            issuedRoleIds = roles.Select(x => x.Id).OrderBy(x => x).ToArray();

            _db.UserRoles.RemoveRange(user.UserRoles);
            user.UserRoles.Clear();
            _db.UserPermissionOverrides.RemoveRange(user.PermissionOverrides);
            user.PermissionOverrides.Clear();
            user.IsGroupScope = false;
            foreach (var role in roles)
            {
                var userRole = new UserRole { User = user, Role = role };
                _db.UserRoles.Add(userRole);
            }

            var link = user.EmployeeUserAccounts.FirstOrDefault(x =>
                x.TenantId == tenantId && x.EmployeeId == employee.Id && !x.IsDeleted);
            if (link is null)
            {
                link = new EmployeeUserAccount
                {
                    Id = newLinkId,
                    TenantId = tenantId,
                    EmployeeId = employee.Id,
                    User = user,
                    CreatedBy = context.UserId
                };
                _db.EmployeeUserAccounts.Add(link);
            }
            issuedUserId = user.Id;
            issuedLinkId = link.Id;
            issuedEmail = user.Email;
            issuedNormalizedEmail = user.NormalizedEmail;
            link.AccessMode = accessMode;
            link.Status = expectedLinkStatus;
            link.RequiresPasswordSetup = accessMode != AccessModes.NoLogin;
            link.InvitedAtUtc = issuedAtUtc;
            link.InvitationExpiresAtUtc = expiresAtUtc;
            link.InvitationTokenHash = invitationHash;
            link.InvitationAcceptedAtUtc = null;
            link.UpdatedAtUtc = issuedAtUtc;
            link.UpdatedBy = context.UserId;

            user.Status = expectedUserStatus;
            user.AccessMode = AccessModes.NoLogin;
            user.PasswordHash = unreachablePasswordHash;
            user.IdentityProvider = "Local";
            user.ProvisioningSource = "Local";
            user.ExternalId = string.Empty;
            user.LastProvisionedAtUtc = null;
            user.IsActive = false;
            user.IsEmailConfirmed = false;
            user.MustChangePassword = false;
            user.IsLocked = false;
            user.LockoutEnd = null;
            user.FailedLoginCount = 0;
            user.MFAEnabled = false;
            user.MfaSecretEncrypted = null;
            user.MfaConfiguredAtUtc = null;
            user.MfaLastVerifiedAtUtc = null;
            user.MfaFailedCount = 0;
            TenantSessionSecurity.RotateStamp(user, issuedAtUtc);

            _db.UserEntityAccesses.RemoveRange(user.EntityAccesses);
            user.EntityAccesses.Clear();
            if (employee.CompanyId.HasValue)
            {
                var grant = new UserEntityAccess
                {
                    Id = newEntityGrantId,
                    TenantId = tenantId,
                    User = user,
                    CompanyId = employee.CompanyId.Value,
                    GrantMode = EntityGrantModes.SelectedCompanies,
                    Role = string.Join(",", roles.Select(r => r.Name)),
                    CreatedBy = context.UserId,
                    GrantedBy = context.UserId,
                    GrantedAt = issuedAtUtc
                };
                _db.UserEntityAccesses.Add(grant);
            }

            employee.UserAccountId = accessMode == AccessModes.NoLogin ? null : user.Id;

            await _db.PasswordResetTokens.TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => x.UserId == user.Id && x.UsedAtUtc == null)
                .OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(ct);
            await _db.MfaChallengeTokens.TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => x.UserId == user.Id && x.UsedAtUtc == null)
                .OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(ct);
            await _db.RefreshTokens.TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => x.UserId == user.Id && x.RevokedAtUtc == null)
                .OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(ct);
            if (_db.Database.IsRelational())
            {
                await _db.PasswordResetTokens.Where(x => x.UserId == user.Id && x.UsedAtUtc == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedAtUtc, issuedAtUtc), ct);
                await _db.MfaChallengeTokens.Where(x => x.UserId == user.Id && x.UsedAtUtc == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedAtUtc, issuedAtUtc), ct);
                await _db.RefreshTokens.Where(x => x.UserId == user.Id && x.RevokedAtUtc == null)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.RevokedAtUtc, issuedAtUtc)
                        .SetProperty(x => x.RevokedByIp, context.IpAddress), ct);
            }
            else
            {
                foreach (var reset in await _db.PasswordResetTokens
                    .Where(x => x.UserId == user.Id && x.UsedAtUtc == null).ToListAsync(ct))
                    reset.UsedAtUtc = issuedAtUtc;
                foreach (var challenge in await _db.MfaChallengeTokens
                    .Where(x => x.UserId == user.Id && x.UsedAtUtc == null).ToListAsync(ct))
                    challenge.UsedAtUtc = issuedAtUtc;
                foreach (var refresh in await _db.RefreshTokens
                    .Where(x => x.UserId == user.Id && x.RevokedAtUtc == null).ToListAsync(ct))
                {
                    refresh.RevokedAtUtc = issuedAtUtc;
                    refresh.RevokedByIp = context.IpAddress;
                }
            }

            _db.AuditLogs.Add(AuthAuditEntry.Create(
                auditId,
                issuedAtUtc,
                "access.employee_invited",
                "EmployeeUserAccount",
                link.Id.ToString(),
                context with { TenantId = tenantId },
                auditMetadata));
            await _db.SaveChangesAsync(ct);
            return true;
        }

        if (_db.Database.IsRelational())
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteInTransactionAsync(
                IssueOnceAsync,
                async ct => await ReconcileCommittedInvitationAsync(ct) is not null,
                IsolationLevel.ReadCommitted,
                cancellationToken);
        }
        else
        {
            await IssueOnceAsync(cancellationToken);
        }

        return await ReconcileCommittedInvitationAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                "The invitation commit could not be reconciled; the invitation token was not disclosed.");
    }

    public async Task<AuthUserDto> AssignRolesAsync(Guid tenantId, Guid userId, AssignRolesRequest request, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken)
    {
        var changedAtUtc = DateTime.UtcNow;
        var auditId = Guid.NewGuid();
        var requestedAdmin = request.Roles.Any(x => AuthService.Normalize(x) == "ADMIN");

        async Task<bool> AssignOnceAsync(CancellationToken ct)
        {
            _db.ChangeTracker.Clear();
            await AcquireAdminSeatLockAsync(tenantId, requestedAdmin, ct);
            var tenant = await _db.Tenants.TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == tenantId, ct)
                ?? throw new InvalidOperationException("Tenant not found.");
            var adminCohort = await LockAdminCohortAsync(tenantId, ct);
            var user = await LockAccessUserAsync(tenantId, userId, entityScope, ct)
                ?? throw new InvalidOperationException("User not found.");
            var roles = await LoadRoles(tenantId, request.Roles, ct);

            // ── PRIVILEGE CEILING (PrivilegeCeiling) ──────────────────────────────────────────────
            // security.manage opens this endpoint; it does not let the caller hand out more than they hold.
            // The subject never decides (no self-change), nobody reaches up to a user holding more than them,
            // and every role given OR taken away must sit inside the caller's own effective permissions.
            var caller = await LoadCallerCeilingAsync(tenantId, context, ct);
            var previousRoles = user.UserRoles.Where(x => x.Role is { IsDeleted: false }).Select(x => x.Role!).ToList();
            ThrowIfRefused(PrivilegeCeiling.TargetRefusal(
                caller,
                user.Id,
                previousRoles.Any(x => x.IsActive && x.NormalizedName == PrivilegeCeiling.AdminRoleNormalizedName),
                AuthService.GetPermissions(user)));
            var previousRoleIds = previousRoles.Select(x => x.Id).ToHashSet();
            var nextRoleIds = roles.Select(x => x.Id).ToHashSet();
            foreach (var changed in roles.Where(x => !previousRoleIds.Contains(x.Id))
                         .Concat(previousRoles.Where(x => !nextRoleIds.Contains(x.Id)))
                         .OrderBy(x => x.Name, StringComparer.Ordinal))
                ThrowIfRefused(PrivilegeCeiling.AssignRefusal(caller, Facts(changed)));

            var wasOperationalAdmin = IsOperationalAdmin(user, changedAtUtc);
            var willBeOperationalAdmin = IsOperationalIdentity(user, changedAtUtc)
                && roles.Any(x => x.NormalizedName == "ADMIN" && x.IsActive && !x.IsDeleted);
            if (wasOperationalAdmin && !willBeOperationalAdmin)
            {
                if (user.Id == context.UserId)
                    throw new InvalidOperationException("You cannot remove your own administrator access.");
                EnsureAnotherOperationalAdmin(adminCohort, user.Id, changedAtUtc);
            }
            var alreadyActiveAdmin = user.IsActive
                && user.UserRoles.Any(x => x.Role is { NormalizedName: "ADMIN", IsActive: true, IsDeleted: false });
            var willBeActiveAdmin = user.IsActive && roles.Any(x => x.NormalizedName == "ADMIN");
            if (willBeActiveAdmin && !alreadyActiveAdmin)
                await EnsureAdminCapacityAsync(tenantId, ct);

            _db.UserRoles.RemoveRange(user.UserRoles);
            user.UserRoles.Clear();
            foreach (var role in roles)
                user.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id, Role = role });

            await InvalidateAuthorizationSessionsAsync(new[] { user }, changedAtUtc, context, ct);
            var metadata = System.Text.Json.JsonSerializer.Serialize(new
            {
                roles = roles.Select(x => x.Name).OrderBy(x => x).ToList(),
                previousRoles = previousRoles.Select(x => x.Name).OrderBy(x => x).ToList(),
                actorIsAdmin = caller.IsAdmin
            });
            _db.AuditLogs.Add(AuthAuditEntry.Create(
                auditId,
                changedAtUtc,
                "access.roles_assigned",
                "User",
                user.Id.ToString(),
                context with { TenantId = tenant.Id },
                metadata));
            await _db.SaveChangesAsync(ct);
            return true;
        }

        await ExecuteAuthorizationTransactionAsync(auditId, "access.roles_assigned", AssignOnceAsync, cancellationToken);
        _db.ChangeTracker.Clear();
        var committed = await _db.Users.AsNoTracking()
            .Include(x => x.Tenant)
            .Include(x => x.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x!.RolePermissions).ThenInclude(x => x.Permission)
            .ApplyEntityScope(_db, tenantId, entityScope)
            .SingleAsync(x => x.Id == userId && x.TenantId == tenantId, cancellationToken);
        var committedRoles = committed.UserRoles
            .Where(x => x.Role is { IsActive: true, IsDeleted: false })
            .Select(x => x.Role!)
            .ToList();
        return ToUserDto(committed, committed.Tenant!, committedRoles);
    }

    public async Task<UserAccessDto?> GetUserAccessAsync(Guid tenantId, Guid userId, EntityScopeContext entityScope, CancellationToken cancellationToken)
    {
        var user = await LoadAccessUser(tenantId, userId, entityScope, cancellationToken);
        return user is null ? null : ToAccessDto(user);
    }

    public async Task<UserAccessDto?> SetAccessModeAsync(Guid tenantId, Guid userId, AccessModeRequest request, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken)
    {
        if (!await _db.Users.ApplyEntityScope(_db, tenantId, entityScope)
                .AnyAsync(x => x.TenantId == tenantId && x.Id == userId && !x.IsDeleted, cancellationToken))
            return null;

        var accessMode = NormalizeAccessMode(request.AccessMode);
        var changedAtUtc = DateTime.UtcNow;
        var auditId = Guid.NewGuid();
        UserAccessDto? result = null;

        async Task<bool> ChangeOnceAsync(CancellationToken ct)
        {
            _db.ChangeTracker.Clear();
            var tenant = await _db.Tenants.TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == tenantId, ct);
            var adminCohort = tenant is null
                ? Array.Empty<User>()
                : await LockAdminCohortAsync(tenantId, ct);
            var anchor = await _db.Users.TagWith(RowLockingInterceptor.ForUpdateTag)
                .ApplyEntityScope(_db, tenantId, entityScope)
                .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == userId && !x.IsDeleted, ct);
            if (tenant is null || anchor is null)
                throw new InvalidOperationException("User not found.");

            await _db.EmployeeUserAccounts.TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => x.TenantId == tenantId && x.UserId == userId && !x.IsDeleted)
                .OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(ct);
            var user = await LoadAccessUser(tenantId, userId, entityScope, ct)
                ?? throw new InvalidOperationException("User not found.");
            var link = user.EmployeeUserAccounts.Where(x => !x.IsDeleted)
                .OrderByDescending(x => x.IsPrimary).ThenByDescending(x => x.CreatedAtUtc).FirstOrDefault();
            var disablesLogin = accessMode == AccessModes.NoLogin;
            if (disablesLogin && user.Id == context.UserId)
                throw new InvalidOperationException("You cannot disable your own account.");
            // PRIVILEGE CEILING: an access mode carries permissions of its own (AuthService.AccessModePermissions),
            // so changing it is a grant: never on yourself, never on someone above you, and only a mode whose
            // permissions you hold. Platform callers (no user) are outside the tenant ceiling.
            if (context.UserId is not null)
            {
                var caller = await LoadCallerCeilingAsync(tenantId, context, ct);
                ThrowIfRefused(PrivilegeCeiling.TargetRefusal(caller, user.Id, HoldsAdmin(user), AuthService.GetPermissions(user)));
                ThrowIfRefused(PrivilegeCeiling.GrantRefusal(caller, AuthService.AccessModePermissions(accessMode)));
            }
            var accessModeBefore = user.AccessMode;
            if (disablesLogin && IsOperationalAdmin(user, changedAtUtc))
                EnsureAnotherOperationalAdmin(adminCohort, user.Id, changedAtUtc);
            if (accessMode != AccessModes.NoLogin
                && (string.Equals(user.AccessMode, AccessModes.NoLogin, StringComparison.Ordinal)
                    || !user.IsActive
                    || link?.RequiresPasswordSetup == true))
                throw new InvalidOperationException(
                    "A disabled or setup-pending identity must be activated through a controlled invitation; access mode cannot revive its credential.");

            if (link is not null)
            {
                link.AccessMode = accessMode;
                link.Status = accessMode == AccessModes.NoLogin ? "NoLogin" : "Active";
                link.LoginDisabledReason = accessMode == AccessModes.NoLogin ? request.Reason ?? "No login access" : string.Empty;
                link.UpdatedAtUtc = changedAtUtc;
                link.UpdatedBy = context.UserId;
            }
            user.AccessMode = accessMode;
            user.IsActive = accessMode != AccessModes.NoLogin;
            TenantSessionSecurity.RotateStamp(user, changedAtUtc);

            await _db.MfaChallengeTokens.TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => x.UserId == user.Id && x.UsedAtUtc == null)
                .OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(ct);
            await _db.RefreshTokens.TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => x.UserId == user.Id && x.RevokedAtUtc == null)
                .OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(ct);
            if (_db.Database.IsRelational())
            {
                await _db.MfaChallengeTokens
                    .Where(x => x.UserId == user.Id && x.UsedAtUtc == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedAtUtc, changedAtUtc), ct);
                await _db.RefreshTokens
                    .Where(x => x.UserId == user.Id && x.RevokedAtUtc == null)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.RevokedAtUtc, changedAtUtc)
                        .SetProperty(x => x.RevokedByIp, context.IpAddress), ct);
            }
            else
            {
                foreach (var challenge in await _db.MfaChallengeTokens
                    .Where(x => x.UserId == user.Id && x.UsedAtUtc == null).ToListAsync(ct))
                    challenge.UsedAtUtc = changedAtUtc;
                foreach (var refresh in await _db.RefreshTokens
                    .Where(x => x.UserId == user.Id && x.RevokedAtUtc == null).ToListAsync(ct))
                {
                    refresh.RevokedAtUtc = changedAtUtc;
                    refresh.RevokedByIp = context.IpAddress;
                }
            }

            _db.AuditLogs.Add(AuthAuditEntry.Create(
                auditId,
                changedAtUtc,
                "access.mode_changed",
                "User",
                user.Id.ToString(),
                context with { TenantId = tenantId },
                System.Text.Json.JsonSerializer.Serialize(new { accessMode, accessModeBefore, reason = request.Reason ?? string.Empty })));
            await _db.SaveChangesAsync(ct);
            result = ToAccessDto(user);
            return true;
        }

        if (_db.Database.IsRelational())
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteInTransactionAsync(
                ChangeOnceAsync,
                // IgnoreQueryFilters is intentional: commit verification of this command's own audit marker by its server-generated id; no tenant data is read (register §6).
                async ct => await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                    .AnyAsync(x => x.Id == auditId && x.Action == "access.mode_changed", ct),
                IsolationLevel.ReadCommitted,
                cancellationToken);
        }
        else
        {
            await ChangeOnceAsync(cancellationToken);
        }

        return result ?? await GetUserAccessAsync(tenantId, userId, entityScope, cancellationToken);
    }

    public async Task<UserAccessDto?> SetPermissionOverrideAsync(Guid tenantId, Guid userId, PermissionOverrideRequest request, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken)
    {
        if (!await _db.Users.ApplyEntityScope(_db, tenantId, entityScope)
                .AnyAsync(x => x.TenantId == tenantId && x.Id == userId && !x.IsDeleted, cancellationToken))
            return null;

        var effect = request.Effect.Equals("Deny", StringComparison.OrdinalIgnoreCase) ? "Deny" : "Allow";
        var changedAtUtc = DateTime.UtcNow;
        var newOverrideId = Guid.NewGuid();
        var auditId = Guid.NewGuid();

        async Task<bool> SetOnceAsync(CancellationToken ct)
        {
            _db.ChangeTracker.Clear();
            var tenant = await _db.Tenants.TagWith(RowLockingInterceptor.ForShareTag)
                .SingleOrDefaultAsync(x => x.Id == tenantId, ct)
                ?? throw new InvalidOperationException("Tenant not found.");
            var user = await LockAccessUserAsync(tenantId, userId, entityScope, ct)
                ?? throw new InvalidOperationException("User not found.");
            if (!await _db.Permissions.AnyAsync(x => x.Key == request.PermissionKey, ct))
                throw new InvalidOperationException("Permission does not exist.");

            // PRIVILEGE CEILING: never on yourself, never on someone above you, and Allow only what you hold.
            var caller = await LoadCallerCeilingAsync(tenantId, context, ct);
            ThrowIfRefused(PrivilegeCeiling.TargetRefusal(
                caller,
                user.Id,
                user.UserRoles.Any(x => x.Role is { NormalizedName: PrivilegeCeiling.AdminRoleNormalizedName, IsActive: true, IsDeleted: false }),
                AuthService.GetPermissions(user)));
            if (effect == "Allow")
                ThrowIfRefused(PrivilegeCeiling.GrantRefusal(caller, new[] { request.PermissionKey }));

            var ov = user.PermissionOverrides.FirstOrDefault(x => x.PermissionKey == request.PermissionKey);
            var previousEffect = ov is { IsActive: true } ? ov.Effect : null;
            if (ov is null)
            {
                ov = new UserPermissionOverride
                {
                    Id = newOverrideId,
                    TenantId = tenantId,
                    UserId = userId,
                    PermissionKey = request.PermissionKey,
                    CreatedAtUtc = changedAtUtc,
                    CreatedBy = context.UserId
                };
                _db.UserPermissionOverrides.Add(ov);
            }
            ov.Effect = effect;
            ov.Reason = request.Reason ?? string.Empty;
            ov.ExpiresAtUtc = request.ExpiresAtUtc;
            ov.IsActive = true;
            ov.UpdatedAtUtc = changedAtUtc;
            ov.UpdatedBy = context.UserId;

            await InvalidateAuthorizationSessionsAsync(new[] { user }, changedAtUtc, context, ct);
            var metadata = System.Text.Json.JsonSerializer.Serialize(new
            {
                userId,
                permission = request.PermissionKey,
                effect,
                previousEffect
            });
            _db.AuditLogs.Add(AuthAuditEntry.Create(
                auditId,
                changedAtUtc,
                "access.permission_override",
                "UserPermissionOverride",
                ov.Id.ToString(),
                context with { TenantId = tenant.Id },
                metadata));
            await _db.SaveChangesAsync(ct);
            return true;
        }

        await ExecuteAuthorizationTransactionAsync(auditId, "access.permission_override", SetOnceAsync, cancellationToken);
        _db.ChangeTracker.Clear();
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
        var target = await LoadAccessUser(tenantId, userId, entityScope, cancellationToken)
            ?? throw new InvalidOperationException("User not found.");
        await EnsureMayManageAccountAsync(tenantId, context, target, cancellationToken);
        var before = new { user.FullName, user.PhoneNumber, user.PreferredLanguage, user.Timezone };
        if (!string.IsNullOrWhiteSpace(request.FullName)) user.FullName = request.FullName.Trim();
        if (request.PhoneNumber is not null) user.PhoneNumber = request.PhoneNumber.Trim();
        if (!string.IsNullOrWhiteSpace(request.PreferredLanguage)) user.PreferredLanguage = request.PreferredLanguage.Trim();
        if (!string.IsNullOrWhiteSpace(request.Timezone)) user.Timezone = request.Timezone.Trim();
        user.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("access.user_updated", "User", user.Id.ToString(), context,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                before,
                after = new { user.FullName, user.PhoneNumber, user.PreferredLanguage, user.Timezone }
            }), cancellationToken);
        return ToUserListDto(user);
    }

    public async Task ActivateUserAsync(Guid tenantId, Guid userId, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken)
    {
        await MutateEligibilityStateAsync(
            tenantId,
            userId,
            entityScope,
            context,
            "access.user_activated",
            null,
            (user, _) =>
            {
                if (!user.IsActive
                    || string.Equals(user.AccessMode, AccessModes.NoLogin, StringComparison.Ordinal)
                    || user.Status is "Suspended" or "Deactivated" or "PendingPasswordSetup" or "PasswordResetRequired")
                    throw new InvalidOperationException(
                        "A disabled identity cannot be reactivated with its old credential; issue a controlled invitation.");
                user.IsLocked = false;
                user.LockoutEnd = null;
                user.FailedLoginCount = 0;
                user.Status = "Active";
            },
            cancellationToken);
    }

    public async Task SuspendUserAsync(Guid tenantId, Guid userId, string reason, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken)
    {
        await MutateEligibilityStateAsync(
            tenantId,
            userId,
            entityScope,
            context,
            "access.user_suspended",
            $"{{\"reason\":\"{reason}\"}}",
            (user, _) =>
            {
                user.IsActive = false;
                user.Status = "Suspended";
            },
            cancellationToken);
    }

    public async Task LockUserAsync(Guid tenantId, Guid userId, string reason, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken)
    {
        await MutateEligibilityStateAsync(
            tenantId,
            userId,
            entityScope,
            context,
            "access.user_locked",
            $"{{\"reason\":\"{reason}\"}}",
            (user, changedAtUtc) =>
            {
                user.IsLocked = true;
                user.LockoutEnd = changedAtUtc.AddDays(1);
                user.Status = "Locked";
            },
            cancellationToken);
    }

    public async Task UnlockUserAsync(Guid tenantId, Guid userId, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken)
    {
        await MutateEligibilityStateAsync(
            tenantId,
            userId,
            entityScope,
            context,
            "access.user_unlocked",
            null,
            (user, _) =>
            {
                user.IsLocked = false;
                user.LockoutEnd = null;
                user.FailedLoginCount = 0;
                user.Status = user.IsActive ? "Active" : "Suspended";
            },
            cancellationToken);
    }

    public async Task AdminResetPasswordAsync(Guid tenantId, Guid userId, AdminResetPasswordRequest request, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken)
    {
        var user = await _db.Users
            .Include(x => x.EmployeeUserAccounts)
            .ApplyEntityScope(_db, tenantId, entityScope)
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == userId && !x.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("User not found.");
        // The endpoint is disabled (AccessController answers 409), but the method must not be a ceiling bypass.
        await EnsureMayManageAccountAsync(tenantId, context,
            await LoadAccessUser(tenantId, userId, entityScope, cancellationToken) ?? throw new InvalidOperationException("User not found."),
            cancellationToken);
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

    /// <summary>How long an administrator-issued reset link stays redeemable. Matches the
    /// self-service forgot-password link (AuthService.ForgotPasswordAsync) exactly — an
    /// admin-initiated reset must not be the longer-lived door.</summary>
    internal const int AdminResetLinkLifetimeHours = 1;

    /// <inheritdoc />
    public async Task<AdminPasswordResetLinkDto> IssuePasswordResetLinkAsync(
        Guid tenantId,
        Guid userId,
        EntityScopeContext entityScope,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        var tenant = await _db.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == tenantId && x.IsActive, cancellationToken)
            ?? throw new InvalidOperationException("Workspace not found.");

        var user = await _db.Users
            .ApplyEntityScope(_db, tenantId, entityScope)
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == userId && !x.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("User not found.");

        // These three are exactly the conditions AuthService.ResetPasswordAsync re-checks when the
        // link is redeemed. Refusing here means an administrator is never handed a link that is
        // guaranteed to fail in the user's hands — the alternative is a silent dead end, which is
        // the class of defect this endpoint exists to remove.
        if (string.Equals(user.AccessMode, AccessModes.NoLogin, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "This person has no portal login, so there is no password to reset. Invite them to the portal first.");
        if (!user.IsActive)
            throw new InvalidOperationException(
                "This account is not active, so a reset link could not be redeemed. Reactivate the account, or re-issue the invitation if it was never accepted.");
        if (user.Status is "Deactivated" or "Suspended")
            throw new InvalidOperationException(
                $"This account is {user.Status.ToLowerInvariant()}. Restore access first, then send a reset link.");

        // PRIVILEGE CEILING: with no mail transport the link comes back to the caller, so a reset link for a user
        // above you is a takeover of their account.
        await EnsureMayManageAccountAsync(tenantId, context,
            await LoadAccessUser(tenantId, userId, entityScope, cancellationToken) ?? throw new InvalidOperationException("User not found."),
            cancellationToken);

        var issuedAtUtc = DateTime.UtcNow;
        var expiresAtUtc = issuedAtUtc.AddHours(AdminResetLinkLifetimeHours);
        var resetToken = _tokenService.CreateSecureToken();

        // Supersede every still-live link for this user. Issuing a second link must not leave the
        // first one redeemable: an administrator who re-issues because the first went astray has to
        // be able to assume the first one is dead.
        var superseded = await _db.PasswordResetTokens
            .Where(x => x.UserId == user.Id && x.UsedAtUtc == null && x.ExpiresAtUtc > issuedAtUtc)
            .ToListAsync(cancellationToken);
        foreach (var stale in superseded) stale.ExpiresAtUtc = issuedAtUtc;

        // Only the hash is persisted; the raw token leaves in the return value and is unrecoverable.
        _db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = user.Id,
            TokenHash = _tokenService.HashToken(resetToken),
            ExpiresAtUtc = expiresAtUtc,
            CreatedAtUtc = issuedAtUtc,
            CreatedByIp = context.IpAddress
        });
        _db.LoginActivities.Add(new LoginActivity
        {
            TenantId = user.TenantId,
            UserId = user.Id,
            EmailAttempted = user.Email,
            EventType = LoginEventTypes.PasswordResetRequested,
            IpAddress = context.IpAddress,
            UserAgent = context.UserAgent,
        });
        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.WriteAsync(
            "access.password_reset_link_issued",
            "User",
            user.Id.ToString(),
            context,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                initiatedBy = "tenant_admin",
                supersededLinks = superseded.Count,
                expiresAtUtc
            }),
            cancellationToken);

        return new AdminPasswordResetLinkDto(
            user.Id,
            user.Email,
            user.FullName,
            resetToken,
            AuthLinkBuilder.ResetPassword(_appUrl, tenant.Slug, resetToken),
            expiresAtUtc);
    }

    public async Task<bool> DeleteUserAsync(Guid tenantId, Guid userId, EntityScopeContext entityScope, RequestContext context, CancellationToken cancellationToken)
    {
        var changedAtUtc = DateTime.UtcNow;
        var auditId = Guid.NewGuid();

        async Task<bool> DeleteOnceAsync(CancellationToken ct)
        {
            _db.ChangeTracker.Clear();
            // An exclusive tenant anchor makes the last-admin decision one serializable
            // critical section and also orders this write against session issuance.
            var tenant = await _db.Tenants.TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == tenantId, ct)
                ?? throw new InvalidOperationException("Tenant not found.");
            var adminCohort = await LockAdminCohortAsync(tenantId, ct);
            var user = await LockAccessUserAsync(tenantId, userId, entityScope, ct)
                ?? throw new InvalidOperationException("User not found.");
            if (user.Id == context.UserId)
                throw new InvalidOperationException("You cannot delete your own account.");
            await EnsureMayManageAccountAsync(tenantId, context, user, ct);

            if (IsOperationalAdmin(user, changedAtUtc))
                EnsureAnotherOperationalAdmin(adminCohort, user.Id, changedAtUtc);

            user.IsDeleted = true;
            user.DeletedAtUtc = changedAtUtc;
            user.DeletedBy = context.UserId;
            user.IsActive = false;
            user.Status = "Deactivated";
            await InvalidateAuthorizationSessionsAsync(new[] { user }, changedAtUtc, context, ct);
            _db.AuditLogs.Add(AuthAuditEntry.Create(
                auditId,
                changedAtUtc,
                "access.user_deleted",
                "User",
                user.Id.ToString(),
                context with { TenantId = tenant.Id }));
            await _db.SaveChangesAsync(ct);
            return true;
        }

        await ExecuteAuthorizationTransactionAsync(auditId, "access.user_deleted", DeleteOnceAsync, cancellationToken);
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
        // A GET must not race the locked settings command by inserting a default row.  The
        // command persists defaults under the tenant lock when the first real change is made.
        setting ??= new Models.SecuritySetting { TenantId = tenantId };
        return ToSecuritySettingDto(setting);
    }

    public async Task<SecuritySettingDto> UpdateSecuritySettingsAsync(Guid tenantId, UpdateSecuritySettingRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var changedAtUtc = DateTime.UtcNow;
        var auditId = Guid.NewGuid();
        SecuritySettingDto? result = null;

        async Task<bool> UpdateOnceAsync(CancellationToken ct)
        {
            _db.ChangeTracker.Clear();
            var tenant = await _db.Tenants.TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == tenantId, ct)
                ?? throw new InvalidOperationException("Tenant not found.");
            var setting = await _db.SecuritySettings.TagWith(RowLockingInterceptor.ForUpdateTag)
                .FirstOrDefaultAsync(x => x.TenantId == tenantId, ct);
            if (setting is null)
            {
                setting = new Models.SecuritySetting { TenantId = tenantId };
                _db.SecuritySettings.Add(setting);
            }

            var mfaWasRequired = setting.MfaRequired;
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
            setting.UpdatedAtUtc = changedAtUtc;
            setting.UpdatedBy = context.UserId;

            if (!mfaWasRequired && setting.MfaRequired)
            {
                var affectedUsers = await _db.Users.TagWith(RowLockingInterceptor.ForUpdateTag)
                    .Where(x => x.TenantId == tenantId
                        && !x.IsDeleted
                        && (!x.MFAEnabled || x.MfaSecretEncrypted == null))
                    .OrderBy(x => x.Id)
                    .ToListAsync(ct);
                var affectedIds = affectedUsers.Select(x => x.Id).ToList();
                foreach (var user in affectedUsers)
                    TenantSessionSecurity.RotateStamp(user, changedAtUtc);

                if (affectedIds.Count > 0)
                {
                    await _db.MfaChallengeTokens.TagWith(RowLockingInterceptor.ForUpdateTag)
                        .Where(x => x.UserId.HasValue && affectedIds.Contains(x.UserId.Value) && x.UsedAtUtc == null)
                        .OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(ct);
                    await _db.RefreshTokens.TagWith(RowLockingInterceptor.ForUpdateTag)
                        .Where(x => affectedIds.Contains(x.UserId) && x.RevokedAtUtc == null)
                        .OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(ct);
                    if (_db.Database.IsRelational())
                    {
                        await _db.MfaChallengeTokens
                            .Where(x => x.UserId.HasValue && affectedIds.Contains(x.UserId.Value) && x.UsedAtUtc == null)
                            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedAtUtc, changedAtUtc), ct);
                        await _db.RefreshTokens
                            .Where(x => affectedIds.Contains(x.UserId) && x.RevokedAtUtc == null)
                            .ExecuteUpdateAsync(s => s
                                .SetProperty(x => x.RevokedAtUtc, changedAtUtc)
                                .SetProperty(x => x.RevokedByIp, context.IpAddress), ct);
                    }
                    else
                    {
                        foreach (var challenge in await _db.MfaChallengeTokens
                            .Where(x => x.UserId.HasValue && affectedIds.Contains(x.UserId.Value) && x.UsedAtUtc == null)
                            .ToListAsync(ct))
                            challenge.UsedAtUtc = changedAtUtc;
                        foreach (var refresh in await _db.RefreshTokens
                            .Where(x => affectedIds.Contains(x.UserId) && x.RevokedAtUtc == null)
                            .ToListAsync(ct))
                        {
                            refresh.RevokedAtUtc = changedAtUtc;
                            refresh.RevokedByIp = context.IpAddress;
                        }
                    }
                }
            }

            _db.AuditLogs.Add(AuthAuditEntry.Create(
                auditId,
                changedAtUtc,
                "access.security_settings_updated",
                "SecuritySetting",
                setting.Id.ToString(),
                context with { TenantId = tenant.Id }));
            await _db.SaveChangesAsync(ct);
            result = ToSecuritySettingDto(setting);
            return true;
        }

        if (_db.Database.IsRelational())
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteInTransactionAsync(
                UpdateOnceAsync,
                // IgnoreQueryFilters is intentional: commit verification of this command's own audit marker by its server-generated id; no tenant data is read (register §6).
                async ct => await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                    .AnyAsync(x => x.Id == auditId && x.Action == "access.security_settings_updated", ct),
                IsolationLevel.ReadCommitted,
                cancellationToken);
        }
        else
        {
            await UpdateOnceAsync(cancellationToken);
        }

        if (result is not null) return result;
        var committed = await _db.SecuritySettings.AsNoTracking()
            .SingleAsync(x => x.TenantId == tenantId, cancellationToken);
        return ToSecuritySettingDto(committed);
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
        var changedAtUtc = DateTime.UtcNow;
        var recordId = Guid.NewGuid();
        var auditId = Guid.NewGuid();

        async Task<bool> AddOnceAsync(CancellationToken ct)
        {
            _db.ChangeTracker.Clear();
            var tenant = await _db.Tenants.TagWith(RowLockingInterceptor.ForShareTag)
                .SingleOrDefaultAsync(x => x.Id == tenantId, ct)
                ?? throw new InvalidOperationException("Tenant not found.");
            var idsToLock = context.UserId.HasValue
                ? new[] { request.GrantorUserId, context.UserId.Value }
                : new[] { request.GrantorUserId };
            var lockedUsers = await LockUsersAsync(tenantId, idsToLock, ct);
            var grantorUser = lockedUsers.SingleOrDefault(x => x.Id == request.GrantorUserId && !x.IsDeleted)
                ?? throw new InvalidOperationException("User not found.");

            // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
            await _db.PermissionGrantorRecords.IgnoreQueryFilters().TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => x.TenantId == tenantId && idsToLock.Contains(x.GrantorUserId))
                .OrderBy(x => x.Id)
                .Select(x => x.Id)
                .ToListAsync(ct);

            // If the caller is not Admin, they must have a current, sub-delegable grant
            // which covers the requested scope.  This check is made after locking the caller.
            if (context.UserId is not null)
            {
                var caller = lockedUsers.SingleOrDefault(x => x.Id == context.UserId.Value && !x.IsDeleted);
                var callerIsAdmin = caller is not null && await _db.UserRoles
                    .Where(x => x.UserId == caller.Id)
                    .AnyAsync(x => x.Role != null
                        && x.Role.NormalizedName == "ADMIN"
                        && x.Role.IsActive
                        && !x.Role.IsDeleted, ct);
                if (!callerIsAdmin)
                {
                    var callerGrant = await _db.PermissionGrantorRecords
                        .Where(x => x.TenantId == tenantId
                            && x.GrantorUserId == context.UserId.Value
                            && x.IsActive
                            && x.CanSubDelegate
                            && (x.ExpiresAtUtc == null || x.ExpiresAtUtc > changedAtUtc))
                        .ToListAsync(ct);
                    if (!callerGrant.Any(x => ScopeCoversScope(x.PermissionScope, request.PermissionScope)))
                        throw new InvalidOperationException("You are not authorised to delegate this permission scope.");
                }
            }

            // PRIVILEGE CEILING: a grantor record is authority to grant, so delegating a scope is granting every
            // permission it covers. Never to yourself, never to someone above you, and only a scope whose every
            // catalogue permission you hold ("all" therefore needs all of them). Fails closed without a caller.
            var catalogue = await _db.Permissions.AsNoTracking().Select(x => x.Key).ToListAsync(ct);
            await EnsureGrantWithinCeilingAsync(tenantId, EntityScopeContext.GroupLevel, context, request.GrantorUserId,
                catalogue.Where(k => PermissionMatchesScope(k, request.PermissionScope.Trim())).ToList(), ct);

            var record = new Models.PermissionGrantorRecord
            {
                Id = recordId,
                TenantId = tenantId,
                GrantorUserId = request.GrantorUserId,
                PermissionScope = request.PermissionScope.Trim(),
                CanSubDelegate = request.CanSubDelegate,
                GrantedByUserId = context.UserId,
                ExpiresAtUtc = request.ExpiresAtUtc,
                Reason = request.Reason ?? string.Empty,
                CreatedAtUtc = changedAtUtc,
                CreatedBy = context.UserId
            };
            _db.PermissionGrantorRecords.Add(record);
            await InvalidateAuthorizationSessionsAsync(new[] { grantorUser }, changedAtUtc, context, ct);
            var metadata = System.Text.Json.JsonSerializer.Serialize(new
            {
                grantorUserId = request.GrantorUserId,
                scope = request.PermissionScope,
                canSubDelegate = request.CanSubDelegate
            });
            _db.AuditLogs.Add(AuthAuditEntry.Create(
                auditId,
                changedAtUtc,
                "access.grantor_added",
                "PermissionGrantorRecord",
                recordId.ToString(),
                context with { TenantId = tenant.Id },
                metadata));
            await _db.SaveChangesAsync(ct);
            return true;
        }

        await ExecuteAuthorizationTransactionAsync(auditId, "access.grantor_added", AddOnceAsync, cancellationToken);
        _db.ChangeTracker.Clear();
        var committed = await _db.PermissionGrantorRecords.AsNoTracking()
            .SingleAsync(x => x.TenantId == tenantId && x.Id == recordId, cancellationToken);
        // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
        var committedUser = await _db.Users.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.TenantId == tenantId && x.Id == request.GrantorUserId, cancellationToken);
        return new PermissionGrantorDto(
            committed.Id,
            committed.GrantorUserId,
            committedUser.Email,
            committedUser.FullName,
            committed.PermissionScope,
            committed.CanSubDelegate,
            committed.GrantedByUserId,
            committed.ExpiresAtUtc,
            committed.IsActive,
            committed.Reason,
            committed.CreatedAtUtc);
    }

    public async Task<bool> RevokeGrantorAsync(Guid tenantId, Guid recordId, RequestContext context, CancellationToken cancellationToken)
    {
        var targetUserId = await _db.PermissionGrantorRecords.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Id == recordId)
            .Select(x => (Guid?)x.GrantorUserId)
            .SingleOrDefaultAsync(cancellationToken);
        if (!targetUserId.HasValue) return false;

        var changedAtUtc = DateTime.UtcNow;
        var auditId = Guid.NewGuid();

        async Task<bool> RevokeOnceAsync(CancellationToken ct)
        {
            _db.ChangeTracker.Clear();
            var tenant = await _db.Tenants.TagWith(RowLockingInterceptor.ForShareTag)
                .SingleOrDefaultAsync(x => x.Id == tenantId, ct)
                ?? throw new InvalidOperationException("Tenant not found.");
            var users = await LockUsersAsync(tenantId, new[] { targetUserId.Value }, ct);
            var user = users.SingleOrDefault()
                ?? throw new InvalidOperationException("User not found.");
            // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
            var record = await _db.PermissionGrantorRecords.IgnoreQueryFilters()
                .TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == recordId, ct)
                ?? throw new InvalidOperationException("Permission grantor record not found.");
            // PRIVILEGE CEILING: nobody changes the grantor records of a user above them.
            await EnsureMayManageAccountAsync(tenantId, context,
                await LoadAccessUser(tenantId, user.Id, EntityScopeContext.GroupLevel, ct) ?? throw new InvalidOperationException("User not found."),
                ct);
            record.IsActive = false;
            await InvalidateAuthorizationSessionsAsync(new[] { user }, changedAtUtc, context, ct);
            _db.AuditLogs.Add(AuthAuditEntry.Create(
                auditId,
                changedAtUtc,
                "access.grantor_revoked",
                "PermissionGrantorRecord",
                recordId.ToString(),
                context with { TenantId = tenant.Id },
                System.Text.Json.JsonSerializer.Serialize(new { grantorUserId = user.Id, scope = record.PermissionScope })));
            await _db.SaveChangesAsync(ct);
            return true;
        }

        await ExecuteAuthorizationTransactionAsync(auditId, "access.grantor_revoked", RevokeOnceAsync, cancellationToken);
        return true;
    }

    public async Task<UserAccessDto?> GrantPermissionAsync(Guid tenantId, Guid targetUserId, GrantPermissionRequest request, EntityScopeContext entityScope, Guid? callerUserId, bool isAdmin, CancellationToken cancellationToken)
    {
        if (!isAdmin && callerUserId is null)
            throw new UnauthorizedAccessException("Authentication required.");
        if (!await _db.Users.ApplyEntityScope(_db, tenantId, entityScope)
                .AnyAsync(x => x.TenantId == tenantId && x.Id == targetUserId && !x.IsDeleted, cancellationToken))
            return null;

        var changedAtUtc = DateTime.UtcNow;
        var newOverrideId = Guid.NewGuid();
        var auditId = Guid.NewGuid();
        var mutationContext = new RequestContext(null, null, callerUserId, tenantId);

        async Task<bool> GrantOnceAsync(CancellationToken ct)
        {
            _db.ChangeTracker.Clear();
            var tenant = await _db.Tenants.TagWith(RowLockingInterceptor.ForShareTag)
                .SingleOrDefaultAsync(x => x.Id == tenantId, ct)
                ?? throw new InvalidOperationException("Tenant not found.");
            var idsToLock = !isAdmin && callerUserId.HasValue
                ? new[] { targetUserId, callerUserId.Value }
                : new[] { targetUserId };
            var lockedUsers = await LockUsersAsync(tenantId, idsToLock, ct);
            if (!await _db.Users.ApplyEntityScope(_db, tenantId, entityScope)
                    .AnyAsync(x => x.TenantId == tenantId && x.Id == targetUserId && !x.IsDeleted, ct))
                throw new InvalidOperationException("User not found.");
            var target = lockedUsers.SingleOrDefault(x => x.Id == targetUserId && !x.IsDeleted)
                ?? throw new InvalidOperationException("User not found.");

            // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
            await _db.UserPermissionOverrides.IgnoreQueryFilters().TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => x.TenantId == tenantId && x.UserId == targetUserId)
                .OrderBy(x => x.Id)
                .Select(x => x.Id)
                .ToListAsync(ct);
            if (!isAdmin)
            {
                await _db.PermissionGrantorRecords.IgnoreQueryFilters().TagWith(RowLockingInterceptor.ForUpdateTag)
                    .Where(x => x.TenantId == tenantId && x.GrantorUserId == callerUserId!.Value)
                    .OrderBy(x => x.Id)
                    .Select(x => x.Id)
                    .ToListAsync(ct);
                var grantor = await _db.PermissionGrantorRecords
                    .Where(x => x.TenantId == tenantId
                        && x.GrantorUserId == callerUserId.Value
                        && x.IsActive
                        && (x.ExpiresAtUtc == null || x.ExpiresAtUtc > changedAtUtc))
                    .ToListAsync(ct);
                if (!grantor.Any(x => PermissionMatchesScope(request.PermissionKey, x.PermissionScope)))
                    throw new InvalidOperationException("You are not authorised to grant or revoke this permission.");
            }

            // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
            var existing = await _db.UserPermissionOverrides.IgnoreQueryFilters()
                .SingleOrDefaultAsync(x => x.TenantId == tenantId
                    && x.UserId == targetUserId
                    && x.PermissionKey == request.PermissionKey, ct);

            // PRIVILEGE CEILING, on top of the grantor scope: a grantor (or an Admin) never acts on themselves or on
            // someone above them, and an Allow — or removing a Deny, which gives the permission back — needs the
            // permission held. Fails closed without a caller.
            await EnsureGrantWithinCeilingAsync(tenantId, entityScope, mutationContext, targetUserId,
                request.Effect.Equals("Remove", StringComparison.OrdinalIgnoreCase)
                    ? (existing is { IsActive: true } && existing.Effect.Equals("Deny", StringComparison.OrdinalIgnoreCase) ? new[] { request.PermissionKey } : Array.Empty<string>())
                    : request.Effect.Equals("Deny", StringComparison.OrdinalIgnoreCase) ? Array.Empty<string>() : new[] { request.PermissionKey },
                ct);
            var previousEffect = existing is { IsActive: true } ? existing.Effect : null;
            if (request.Effect.Equals("Remove", StringComparison.OrdinalIgnoreCase))
            {
                if (existing is not null)
                {
                    existing.IsActive = false;
                    existing.UpdatedAtUtc = changedAtUtc;
                    existing.UpdatedBy = callerUserId;
                }
            }
            else
            {
                if (!await _db.Permissions.AnyAsync(x => x.Key == request.PermissionKey, ct))
                    throw new InvalidOperationException("Permission key does not exist.");
                var effect = request.Effect.Equals("Deny", StringComparison.OrdinalIgnoreCase) ? "Deny" : "Allow";
                if (existing is null)
                {
                    existing = new Models.UserPermissionOverride
                    {
                        Id = newOverrideId,
                        TenantId = tenantId,
                        UserId = targetUserId,
                        PermissionKey = request.PermissionKey,
                        CreatedAtUtc = changedAtUtc,
                        CreatedBy = callerUserId
                    };
                    _db.UserPermissionOverrides.Add(existing);
                }
                existing.Effect = effect;
                existing.Reason = request.Reason ?? string.Empty;
                existing.ExpiresAtUtc = request.ExpiresAtUtc;
                existing.IsActive = true;
                existing.UpdatedAtUtc = changedAtUtc;
                existing.UpdatedBy = callerUserId;
            }

            await InvalidateAuthorizationSessionsAsync(new[] { target }, changedAtUtc, mutationContext, ct);
            var metadata = System.Text.Json.JsonSerializer.Serialize(new
            {
                permission = request.PermissionKey,
                effect = request.Effect,
                previousEffect
            });
            _db.AuditLogs.Add(AuthAuditEntry.Create(
                auditId,
                changedAtUtc,
                "access.permission_granted",
                "UserPermissionOverride",
                targetUserId.ToString(),
                mutationContext with { TenantId = tenant.Id },
                metadata));
            await _db.SaveChangesAsync(ct);
            return true;
        }

        await ExecuteAuthorizationTransactionAsync(auditId, "access.permission_granted", GrantOnceAsync, cancellationToken);
        _db.ChangeTracker.Clear();
        return await GetUserAccessAsync(tenantId, targetUserId, entityScope, cancellationToken);
    }

    public async Task<UserAccessDto?> GrantPermissionsBulkAsync(Guid tenantId, Guid targetUserId, BulkGrantPermissionsRequest request, EntityScopeContext entityScope, Guid? callerUserId, bool isAdmin, CancellationToken cancellationToken)
    {
        var items = request.Items
            .Where(i => !string.IsNullOrWhiteSpace(i.PermissionKey) && !string.IsNullOrWhiteSpace(i.Effect))
            .GroupBy(i => i.PermissionKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToList();
        if (items.Count == 0) throw new InvalidOperationException("No permission changes supplied.");
        if (items.Count > 1000) throw new InvalidOperationException("Too many permission changes in one request.");
        if (!isAdmin && callerUserId is null) throw new UnauthorizedAccessException("Authentication required.");

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
        if (!await _db.Users.ApplyEntityScope(_db, tenantId, entityScope)
                .AnyAsync(x => x.TenantId == tenantId && x.Id == targetUserId && !x.IsDeleted, cancellationToken))
            return null;

        var changedAtUtc = DateTime.UtcNow;
        var auditId = Guid.NewGuid();
        var overrideIds = items.ToDictionary(x => x.PermissionKey, _ => Guid.NewGuid(), StringComparer.OrdinalIgnoreCase);
        var itemAuditIds = items.ToDictionary(x => x.PermissionKey, _ => Guid.NewGuid(), StringComparer.OrdinalIgnoreCase);
        var mutationContext = new RequestContext(null, null, callerUserId, tenantId);

        async Task<bool> GrantBulkOnceAsync(CancellationToken ct)
        {
            _db.ChangeTracker.Clear();
            var tenant = await _db.Tenants.TagWith(RowLockingInterceptor.ForShareTag)
                .SingleOrDefaultAsync(x => x.Id == tenantId, ct)
                ?? throw new InvalidOperationException("Tenant not found.");
            var idsToLock = !isAdmin && callerUserId.HasValue
                ? new[] { targetUserId, callerUserId.Value }
                : new[] { targetUserId };
            var lockedUsers = await LockUsersAsync(tenantId, idsToLock, ct);
            if (!await _db.Users.ApplyEntityScope(_db, tenantId, entityScope)
                    .AnyAsync(x => x.TenantId == tenantId && x.Id == targetUserId && !x.IsDeleted, ct))
                throw new InvalidOperationException("User not found.");
            var target = lockedUsers.SingleOrDefault(x => x.Id == targetUserId && !x.IsDeleted)
                ?? throw new InvalidOperationException("User not found.");

            // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
            await _db.UserPermissionOverrides.IgnoreQueryFilters().TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => x.TenantId == tenantId && x.UserId == targetUserId)
                .OrderBy(x => x.Id)
                .Select(x => x.Id)
                .ToListAsync(ct);
            if (!isAdmin)
            {
                await _db.PermissionGrantorRecords.IgnoreQueryFilters().TagWith(RowLockingInterceptor.ForUpdateTag)
                    .Where(x => x.TenantId == tenantId && x.GrantorUserId == callerUserId!.Value)
                    .OrderBy(x => x.Id)
                    .Select(x => x.Id)
                    .ToListAsync(ct);
                var grantor = await _db.PermissionGrantorRecords
                    .Where(x => x.TenantId == tenantId
                        && x.GrantorUserId == callerUserId.Value
                        && x.IsActive
                        && (x.ExpiresAtUtc == null || x.ExpiresAtUtc > changedAtUtc))
                    .ToListAsync(ct);
                var outOfScope = items
                    .Where(item => !grantor.Any(x => PermissionMatchesScope(item.PermissionKey, x.PermissionScope)))
                    .ToList();
                if (outOfScope.Count > 0)
                    throw new InvalidOperationException("You are not authorised to grant or revoke one or more of the selected permissions.");
            }

            var nonRemoveKeys = items
                .Where(x => !x.Effect.Equals("Remove", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.PermissionKey)
                .ToList();
            if (nonRemoveKeys.Count > 0)
            {
                var known = await _db.Permissions
                    .Where(x => nonRemoveKeys.Contains(x.Key))
                    .Select(x => x.Key)
                    .ToListAsync(ct);
                var knownSet = known.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var unknown = nonRemoveKeys.Where(x => !knownSet.Contains(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (unknown.Count > 0)
                    throw new InvalidOperationException($"Unknown permission key(s): {string.Join(", ", unknown.Take(5))}.");
            }

            // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
            var existingOverrides = await _db.UserPermissionOverrides.IgnoreQueryFilters()
                .Where(x => x.TenantId == tenantId && x.UserId == targetUserId)
                .ToListAsync(ct);

            // PRIVILEGE CEILING for the whole batch, before any row changes: what the batch would give (every Allow,
            // and every Remove of an active Deny) must all be held by the caller; never on yourself or someone above you.
            var activeDenies = existingOverrides
                .Where(x => x.IsActive && x.Effect.Equals("Deny", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.PermissionKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            await EnsureGrantWithinCeilingAsync(tenantId, entityScope, mutationContext, targetUserId,
                items.Where(i => i.Effect.Equals("Remove", StringComparison.OrdinalIgnoreCase)
                        ? activeDenies.Contains(i.PermissionKey)
                        : !i.Effect.Equals("Deny", StringComparison.OrdinalIgnoreCase))
                    .Select(i => i.PermissionKey)
                    .ToList(),
                ct);
            var changed = new List<(string Key, string Effect)>();
            var allowed = 0;
            var denied = 0;
            var removed = 0;
            var noop = 0;
            foreach (var item in items)
            {
                var existing = existingOverrides.FirstOrDefault(x =>
                    string.Equals(x.PermissionKey, item.PermissionKey, StringComparison.OrdinalIgnoreCase));
                if (item.Effect.Equals("Remove", StringComparison.OrdinalIgnoreCase))
                {
                    if (existing is null || !existing.IsActive)
                    {
                        noop++;
                        continue;
                    }
                    existing.IsActive = false;
                    existing.UpdatedAtUtc = changedAtUtc;
                    existing.UpdatedBy = callerUserId;
                    removed++;
                    changed.Add((item.PermissionKey, "Remove"));
                    continue;
                }

                var effect = item.Effect.Equals("Deny", StringComparison.OrdinalIgnoreCase) ? "Deny" : "Allow";
                if (existing is null)
                {
                    existing = new Models.UserPermissionOverride
                    {
                        Id = overrideIds[item.PermissionKey],
                        TenantId = tenantId,
                        UserId = targetUserId,
                        PermissionKey = item.PermissionKey,
                        CreatedAtUtc = changedAtUtc,
                        CreatedBy = callerUserId
                    };
                    _db.UserPermissionOverrides.Add(existing);
                    existingOverrides.Add(existing);
                }
                existing.Effect = effect;
                existing.Reason = request.Reason ?? string.Empty;
                existing.ExpiresAtUtc = null;
                existing.IsActive = true;
                existing.UpdatedAtUtc = changedAtUtc;
                existing.UpdatedBy = callerUserId;
                if (effect == "Allow") allowed++; else denied++;
                changed.Add((item.PermissionKey, effect));
            }

            if (changed.Count > 0)
                await InvalidateAuthorizationSessionsAsync(new[] { target }, changedAtUtc, mutationContext, ct);
            foreach (var item in changed)
            {
                _db.AuditLogs.Add(AuthAuditEntry.Create(
                    itemAuditIds[item.Key],
                    changedAtUtc,
                    "access.permission_granted",
                    "UserPermissionOverride",
                    targetUserId.ToString(),
                    mutationContext with { TenantId = tenant.Id },
                    System.Text.Json.JsonSerializer.Serialize(new { permission = item.Key, effect = item.Effect })));
            }
            var summary = System.Text.Json.JsonSerializer.Serialize(new
            {
                userId = targetUserId,
                allowed,
                denied,
                removed,
                noop,
                total = changed.Count,
                reason = request.Reason ?? string.Empty,
                keys = new
                {
                    allowed = changed.Where(x => x.Effect == "Allow").Select(x => x.Key).ToList(),
                    denied = changed.Where(x => x.Effect == "Deny").Select(x => x.Key).ToList(),
                    removed = changed.Where(x => x.Effect == "Remove").Select(x => x.Key).ToList()
                }
            });
            _db.AuditLogs.Add(AuthAuditEntry.Create(
                auditId,
                changedAtUtc,
                "access.permission_bulk_grant",
                "UserPermissionOverride",
                targetUserId.ToString(),
                mutationContext with { TenantId = tenant.Id },
                summary));
            await _db.SaveChangesAsync(ct);
            return true;
        }

        await ExecuteAuthorizationTransactionAsync(auditId, "access.permission_bulk_grant", GrantBulkOnceAsync, cancellationToken);
        _db.ChangeTracker.Clear();
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

    // ── Privilege ceiling ─────────────────────────────────────────────────────

    /// <summary>
    /// The caller as <see cref="PrivilegeCeiling"/> sees them: their effective permissions computed exactly as a
    /// sign-in computes them (<see cref="AuthService.GetPermissions"/>, from the database, not from a token that
    /// may be minutes old), whether they hold the Admin role, and which roles they hold. Fails CLOSED: a change
    /// with no caller, or a caller who is not an active user of this tenant, is refused.
    /// </summary>
    private async Task<PrivilegeCeiling.Caller> LoadCallerCeilingAsync(Guid tenantId, RequestContext context, CancellationToken cancellationToken)
    {
        if (context.UserId is not Guid callerId)
            throw new PrivilegeCeilingException(PrivilegeCeiling.CallerUnknown());
        var caller = await Zayra.Api.Infrastructure.Data.ScopedBypass.TenantWide(_db.Users, tenantId,
                "The caller's own auth graph by primary key: the company filter must not hide a company-scoped caller from their own privilege ceiling.")
            .AsNoTracking().AsSplitQuery()
            .Include(x => x.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x!.RolePermissions).ThenInclude(x => x.Permission)
            .Include(x => x.PermissionOverrides)
            .Include(x => x.EmployeeUserAccounts)
            .SingleOrDefaultAsync(x => x.Id == callerId && x.TenantId == tenantId && !x.IsDeleted && x.IsActive, cancellationToken);
        if (caller is null)
            throw new PrivilegeCeilingException(PrivilegeCeiling.CallerUnknown());
        var activeRoles = caller.UserRoles
            .Where(x => x.Role is { IsActive: true, IsDeleted: false }
                && (x.Role.TenantId == tenantId || x.Role.TenantId == null))
            .Select(x => x.Role!)
            .ToList();
        return PrivilegeCeiling.ForCaller(
            caller.Id,
            activeRoles.Any(x => x.NormalizedName == PrivilegeCeiling.AdminRoleNormalizedName),
            AuthService.GetPermissions(caller),
            activeRoles.Select(x => x.Id));
    }

    private async Task<PrivilegeCeiling.RoleFacts> RoleFactsAsync(Role role, CancellationToken cancellationToken)
    {
        var keys = await _db.RolePermissions.AsNoTracking()
            .Where(x => x.RoleId == role.Id && x.Permission != null)
            .Select(x => x.Permission!.Key)
            .ToListAsync(cancellationToken);
        return new PrivilegeCeiling.RoleFacts(role.Id, role.Name, role.NormalizedName, role.TenantId, role.IsSystem, role.IsEditable, keys);
    }

    /// <summary>
    /// The ceiling for permission grants made outside a role (overrides, grantor paths, grantor records): the
    /// subject never decides, nobody reaches up, and <paramref name="granted"/> must all be held by the caller.
    /// </summary>
    private async Task EnsureGrantWithinCeilingAsync(
        Guid tenantId,
        EntityScopeContext entityScope,
        RequestContext context,
        Guid targetUserId,
        IReadOnlyCollection<string> granted,
        CancellationToken cancellationToken)
    {
        var caller = await LoadCallerCeilingAsync(tenantId, context, cancellationToken);
        var target = await LoadAccessUser(tenantId, targetUserId, entityScope, cancellationToken)
            ?? throw new InvalidOperationException("User not found.");
        ThrowIfRefused(PrivilegeCeiling.TargetRefusal(caller, target.Id, HoldsAdmin(target), AuthService.GetPermissions(target)));
        ThrowIfRefused(PrivilegeCeiling.GrantRefusal(caller, granted));
    }

    private static bool HoldsAdmin(User user) =>
        user.UserRoles.Any(x => x.Role is { NormalizedName: PrivilegeCeiling.AdminRoleNormalizedName, IsActive: true, IsDeleted: false });

    /// <summary>
    /// Account actions on another user (suspend, lock, unlock, activate, delete, password-reset link, profile):
    /// refused when the target is an Admin and the caller is not, or holds access the caller lacks.
    /// <paramref name="target"/> must carry its roles, role permissions, overrides and employee links
    /// (<see cref="LoadAccessUser"/>). Self is left to each action's own rule.
    ///
    /// <para>A context with NO user is the platform operator path: PlatformController calls Delete and Unlock
    /// with <c>UserId: null</c> behind <c>RequirePlatformRole</c>, outside any tenant's ceiling. The tenant Access
    /// API never does: AccessController answers 401 before calling an account action without a user.</para>
    /// </summary>
    private async Task EnsureMayManageAccountAsync(Guid tenantId, RequestContext context, User target, CancellationToken cancellationToken)
    {
        if (context.UserId is null) return;
        var caller = await LoadCallerCeilingAsync(tenantId, context, cancellationToken);
        if (target.Id == caller.UserId) return;
        ThrowIfRefused(PrivilegeCeiling.AboveCallerRefusal(caller, HoldsAdmin(target), AuthService.GetPermissions(target)));
    }

    /// <inheritdoc />
    public async Task AssertMayChangeUserAccessAsync(Guid tenantId, Guid targetUserId, RequestContext context, CancellationToken cancellationToken)
    {
        var caller = await LoadCallerCeilingAsync(tenantId, context, cancellationToken);
        var target = await LoadAccessUser(tenantId, targetUserId, EntityScopeContext.GroupLevel, cancellationToken);
        // A user who no longer exists holds nothing to protect; the self rule still applies by id.
        if (target is null)
        {
            if (targetUserId == caller.UserId) ThrowIfRefused(PrivilegeCeiling.Refuse(PrivilegeCeiling.Codes.SelfChange, null));
            return;
        }
        ThrowIfRefused(PrivilegeCeiling.TargetRefusal(caller, target.Id, HoldsAdmin(target), AuthService.GetPermissions(target)));
    }

    private static PrivilegeCeiling.RoleFacts Facts(Role role) =>
        new(role.Id, role.Name, role.NormalizedName, role.TenantId, role.IsSystem, role.IsEditable,
            role.RolePermissions.Select(x => x.Permission?.Key).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList());

    private static void ThrowIfRefused(PrivilegeCeiling.Refusal? refusal)
    {
        if (refusal is not null) throw new PrivilegeCeilingException(refusal);
    }

    /// <summary>The Access screen's view of the ceiling: for each role, can the caller assign it, can they edit it, and why not.</summary>
    public async Task<AccessCeilingDto> GetAccessCeilingAsync(Guid tenantId, RequestContext context, CancellationToken cancellationToken)
    {
        var caller = await LoadCallerCeilingAsync(tenantId, context, cancellationToken);
        var roles = await _db.Roles.AsNoTracking()
            .Include(x => x.RolePermissions).ThenInclude(x => x.Permission)
            .Where(x => (x.TenantId == tenantId || x.TenantId == null) && !x.IsDeleted)
            .OrderBy(x => x.AuthorityLevel).ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);
        return new AccessCeilingDto(
            caller.UserId,
            caller.IsAdmin,
            caller.Held.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            roles.Select(role =>
            {
                var facts = Facts(role);
                var assign = PrivilegeCeiling.AssignRefusal(caller, facts);
                var edit = PrivilegeCeiling.EditRefusal(caller, facts, null);
                return new RoleCeilingDto(
                    role.Id, role.Name,
                    assign is null, assign?.Code, assign?.MessageEn, assign?.MessageAr,
                    edit is null, edit?.Code, edit?.MessageEn, edit?.MessageAr);
            }).ToList());
    }

    private async Task<IReadOnlyCollection<Role>> LoadRoles(Guid tenantId, IReadOnlyCollection<string> roleNames, CancellationToken cancellationToken)
    {
        var normalizedRoles = roleNames.Select(AuthService.Normalize).Distinct().ToList();
        var roles = await _db.Roles
            .Include(x => x.RolePermissions).ThenInclude(x => x.Permission)
            .Where(x => normalizedRoles.Contains(x.NormalizedName)
                && (x.TenantId == tenantId || x.TenantId == null)
                && x.IsActive
                && !x.IsDeleted)
            .ToListAsync(cancellationToken);
        if (roles.Count != normalizedRoles.Count) throw new InvalidOperationException("One or more roles are invalid for this tenant.");
        return roles;
    }

    private static DateTime ToDatabasePrecisionUtc(DateTime value)
    {
        var utc = value.ToUniversalTime();
        return new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc);
    }

    private static void ValidatePasswordAgainstPolicy(string password, Models.SecuritySetting? policy)
    {
        var scalarCount = 0;
        var hasUpper = false;
        var hasLower = false;
        var hasDigit = false;
        var hasSpecial = false;
        var offset = 0;
        while (offset < password.Length)
        {
            var status = Rune.DecodeFromUtf16(password.AsSpan(offset), out var rune, out var consumed);
            if (status != OperationStatus.Done)
                throw new InvalidOperationException("Password does not meet the workspace security policy.");

            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate)
                throw new InvalidOperationException("Password does not meet the workspace security policy.");

            scalarCount++;
            hasUpper |= Rune.IsUpper(rune);
            hasLower |= Rune.IsLower(rune);
            hasDigit |= Rune.IsDigit(rune);
            hasSpecial |= category is
                UnicodeCategory.ConnectorPunctuation or
                UnicodeCategory.DashPunctuation or
                UnicodeCategory.OpenPunctuation or
                UnicodeCategory.ClosePunctuation or
                UnicodeCategory.InitialQuotePunctuation or
                UnicodeCategory.FinalQuotePunctuation or
                UnicodeCategory.OtherPunctuation or
                UnicodeCategory.MathSymbol or
                UnicodeCategory.CurrencySymbol or
                UnicodeCategory.ModifierSymbol or
                UnicodeCategory.OtherSymbol;
            offset += consumed;
        }

        var minimumLength = Math.Max(10, policy?.PasswordMinLength ?? 10);
        var valid = scalarCount >= minimumLength
            && (!(policy?.PasswordRequireUppercase ?? true) || hasUpper)
            && (!(policy?.PasswordRequireLowercase ?? true) || hasLower)
            && (!(policy?.PasswordRequireDigit ?? true) || hasDigit)
            && (!(policy?.PasswordRequireSpecial ?? true) || hasSpecial);
        if (!valid)
            throw new InvalidOperationException("Password does not meet the workspace security policy.");
    }

    private async Task EnsureAdminCapacityAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var limit = await _db.TenantSubscriptions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Status == SubscriptionStatuses.Active
                && (x.ExpiresAtUtc == null || x.ExpiresAtUtc > DateTime.UtcNow))
            .OrderByDescending(x => x.StartedAtUtc)
            .Select(x => (int?)x.MaxAdminUsers)
            .FirstOrDefaultAsync(cancellationToken) ?? 10;
        if (limit == 0) return; // Enterprise/unlimited convention used by platform provisioning.

        var activeAdmins = await _db.Users.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.IsActive && !x.IsDeleted
                && x.UserRoles.Any(ur => ur.Role != null && ur.Role.NormalizedName == "ADMIN"))
            .CountAsync(cancellationToken);
        if (activeAdmins >= limit)
            throw new InvalidOperationException($"The tenant subscription allows at most {limit} active administrator user(s).");
    }

    /// <summary>
    /// Serialises admin-seat consumption for one tenant. Transaction-scoped on purpose: it must be
    /// called inside the execution-strategy transaction, and Postgres releases it at commit or
    /// rollback (including each retry's rollback), so there is no unlock to forget.
    ///
    /// <para>It used to be a SESSION lock (<c>pg_advisory_lock</c> ... <c>pg_advisory_unlock</c>)
    /// taken outside the transaction. Production reaches Neon through its PgBouncer pooler in
    /// transaction mode, which hands each autocommit statement and each transaction to whichever
    /// server connection is free. The lock, the work and the unlock could therefore land on three
    /// different server sessions: the unlock became a no-op, the lock stayed held on a pooled
    /// session, a later request that drew that session re-acquired it re-entrantly (no exclusion),
    /// and one that drew another session blocked until the command timeout. Inside a transaction
    /// PgBouncer pins one server session from BEGIN to COMMIT, so an xact lock is exact.</para>
    /// </summary>
    private async Task AcquireAdminSeatLockAsync(Guid tenantId, bool required, CancellationToken cancellationToken)
    {
        if (!required || !_db.Database.IsNpgsql()) return;
        if (_db.Database.CurrentTransaction is null)
            throw new InvalidOperationException(
                "The admin-seat lock is transaction-scoped and must be taken inside the operation's transaction.");
        var key = AdminSeatLockKey(tenantId);
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})", cancellationToken);
    }

    internal static long AdminSeatLockKey(Guid tenantId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"admin-seat:{tenantId:D}"));
        return System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(digest.AsSpan(0, 8));
    }

    // Split on (tenant, primary key), inside the caller's anchored transaction when there is one,
    // otherwise inside a snapshot (AuthGraphSnapshot), so every split statement sees one graph.
    private async Task<User?> LoadAccessUser(Guid tenantId, Guid userId, EntityScopeContext entityScope, CancellationToken cancellationToken) =>
        await AuthGraphSnapshot.ReadAsync(_db, ct => _db.Users.AsSplitQuery()
            .Include(x => x.Tenant)
            .Include(x => x.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x!.RolePermissions).ThenInclude(x => x.Permission)
            .Include(x => x.EmployeeUserAccounts)
            .Include(x => x.PermissionOverrides)
            .ApplyEntityScope(_db, tenantId, entityScope)
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == userId, ct), cancellationToken);

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
        var changedAtUtc = DateTime.UtcNow;
        var roleId = Guid.NewGuid();
        var auditId = Guid.NewGuid();

        async Task<bool> CreateOnceAsync(CancellationToken ct)
        {
            _db.ChangeTracker.Clear();
            var tenant = await _db.Tenants.TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == tenantId, ct)
                ?? throw new InvalidOperationException("Tenant not found.");
            if (await _db.Roles.AnyAsync(x => x.TenantId == tenantId
                    && x.NormalizedName == normalized
                    && !x.IsDeleted, ct))
                throw new InvalidOperationException($"A role named '{request.Name}' already exists.");

            var requestedPermissions = request.Permissions?.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                ?? new List<string>();
            var permissions = requestedPermissions.Count == 0
                ? new List<Domain.Entities.Permission>()
                : await _db.Permissions.Where(x => requestedPermissions.Contains(x.Key)).ToListAsync(ct);
            if (permissions.Count != requestedPermissions.Count)
                throw new InvalidOperationException("One or more permission keys do not exist.");
            // PRIVILEGE CEILING: a new role may carry only permissions its creator holds.
            var caller = await LoadCallerCeilingAsync(tenantId, context, ct);
            ThrowIfRefused(PrivilegeCeiling.GrantRefusal(caller, permissions.Select(x => x.Key)));

            var role = new Role
            {
                Id = roleId,
                TenantId = tenantId,
                Name = request.Name.Trim(),
                NormalizedName = normalized,
                Description = request.Description?.Trim() ?? string.Empty,
                AuthorityLevel = request.AuthorityLevel,
                IsSystem = false,
                IsActive = true,
                IsEditable = true,
                CreatedAtUtc = changedAtUtc
            };
            foreach (var permission in permissions)
                role.RolePermissions.Add(new RolePermission
                {
                    RoleId = roleId,
                    PermissionId = permission.Id,
                    Permission = permission
                });
            _db.Roles.Add(role);
            _db.AuditLogs.Add(AuthAuditEntry.Create(
                auditId,
                changedAtUtc,
                "access.role_created",
                "Role",
                roleId.ToString(),
                context with { TenantId = tenant.Id },
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    name = role.Name,
                    permissions = permissions.Select(x => x.Key).OrderBy(x => x, StringComparer.Ordinal).ToList()
                })));
            await _db.SaveChangesAsync(ct);
            return true;
        }

        await ExecuteAuthorizationTransactionAsync(auditId, "access.role_created", CreateOnceAsync, cancellationToken);
        _db.ChangeTracker.Clear();
        var committed = await _db.Roles.AsNoTracking()
            .Include(x => x.RolePermissions).ThenInclude(x => x.Permission)
            .SingleAsync(x => x.TenantId == tenantId && x.Id == roleId, cancellationToken);
        return ToRoleDto(committed);
    }

    public async Task<RoleDto?> UpdateRoleAsync(Guid tenantId, Guid roleId, UpdateRoleRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        if (!await _db.Roles.AnyAsync(x => x.TenantId == tenantId && x.Id == roleId && !x.IsDeleted, cancellationToken))
            return null;
        var changedAtUtc = DateTime.UtcNow;
        var auditId = Guid.NewGuid();

        async Task<bool> UpdateOnceAsync(CancellationToken ct)
        {
            _db.ChangeTracker.Clear();
            var tenant = await _db.Tenants.TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == tenantId, ct)
                ?? throw new InvalidOperationException("Tenant not found.");
            // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
            var role = await _db.Roles.IgnoreQueryFilters().TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == roleId && !x.IsDeleted, ct)
                ?? throw new InvalidOperationException("Role not found.");
            // PRIVILEGE CEILING: not an Admin-only role, not a role you hold, not a role above you.
            var caller = await LoadCallerCeilingAsync(tenantId, context, ct);
            ThrowIfRefused(PrivilegeCeiling.EditRefusal(caller, await RoleFactsAsync(role, ct), null));
            if (!role.IsEditable) throw new InvalidOperationException("This role is not editable.");
            var previousName = role.Name;

            await _db.RolePermissions.TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => x.RoleId == roleId)
                .OrderBy(x => x.PermissionId)
                .Select(x => x.PermissionId)
                .ToListAsync(ct);
            var affectedIds = await _db.UserRoles
                .Where(x => x.RoleId == roleId)
                .Select(x => x.UserId)
                .Distinct()
                .OrderBy(x => x)
                .ToListAsync(ct);
            var affectedUsers = await LockUsersAsync(tenantId, affectedIds, ct);

            if (!string.IsNullOrWhiteSpace(request.Name))
            {
                var normalized = AuthService.Normalize(request.Name);
                if (await _db.Roles.AnyAsync(x => x.TenantId == tenantId
                        && x.NormalizedName == normalized
                        && x.Id != roleId
                        && !x.IsDeleted, ct))
                    throw new InvalidOperationException($"A role named '{request.Name}' already exists.");
                role.Name = request.Name.Trim();
                role.NormalizedName = normalized;
            }
            if (request.Description is not null) role.Description = request.Description.Trim();
            if (request.AuthorityLevel.HasValue) role.AuthorityLevel = request.AuthorityLevel.Value;
            role.UpdatedAtUtc = changedAtUtc;
            role.UpdatedBy = context.UserId;
            await InvalidateAuthorizationSessionsAsync(affectedUsers, changedAtUtc, context, ct);
            _db.AuditLogs.Add(AuthAuditEntry.Create(
                auditId,
                changedAtUtc,
                "access.role_updated",
                "Role",
                roleId.ToString(),
                context with { TenantId = tenant.Id },
                System.Text.Json.JsonSerializer.Serialize(new { name = role.Name, previousName })));
            await _db.SaveChangesAsync(ct);
            return true;
        }

        await ExecuteAuthorizationTransactionAsync(auditId, "access.role_updated", UpdateOnceAsync, cancellationToken);
        _db.ChangeTracker.Clear();
        var committed = await _db.Roles.AsNoTracking()
            .Include(x => x.RolePermissions).ThenInclude(x => x.Permission)
            .SingleAsync(x => x.TenantId == tenantId && x.Id == roleId && !x.IsDeleted, cancellationToken);
        return ToRoleDto(committed);
    }

    public async Task<bool> ActivateRoleAsync(Guid tenantId, Guid roleId, RequestContext context, CancellationToken cancellationToken)
    {
        if (!await _db.Roles.AnyAsync(x => x.TenantId == tenantId && x.Id == roleId && !x.IsDeleted, cancellationToken))
            return false;
        var changedAtUtc = DateTime.UtcNow;
        var auditId = Guid.NewGuid();

        async Task<bool> ActivateOnceAsync(CancellationToken ct)
        {
            _db.ChangeTracker.Clear();
            var tenant = await _db.Tenants.TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == tenantId, ct)
                ?? throw new InvalidOperationException("Tenant not found.");
            // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
            var role = await _db.Roles.IgnoreQueryFilters().TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == roleId && !x.IsDeleted, ct)
                ?? throw new InvalidOperationException("Role not found.");
            // PRIVILEGE CEILING: switching a role back on hands its permissions back to everyone holding it.
            var caller = await LoadCallerCeilingAsync(tenantId, context, ct);
            ThrowIfRefused(PrivilegeCeiling.EditRefusal(caller, await RoleFactsAsync(role, ct), null));
            var affectedIds = await _db.UserRoles.Where(x => x.RoleId == roleId)
                .Select(x => x.UserId).Distinct().OrderBy(x => x).ToListAsync(ct);
            var affectedUsers = await LockUsersAsync(tenantId, affectedIds, ct);
            role.IsActive = true;
            role.UpdatedAtUtc = changedAtUtc;
            role.UpdatedBy = context.UserId;
            await InvalidateAuthorizationSessionsAsync(affectedUsers, changedAtUtc, context, ct);
            _db.AuditLogs.Add(AuthAuditEntry.Create(
                auditId,
                changedAtUtc,
                "access.role_activated",
                "Role",
                roleId.ToString(),
                context with { TenantId = tenant.Id }));
            await _db.SaveChangesAsync(ct);
            return true;
        }

        await ExecuteAuthorizationTransactionAsync(auditId, "access.role_activated", ActivateOnceAsync, cancellationToken);
        return true;
    }

    public async Task<bool> DeactivateRoleAsync(Guid tenantId, Guid roleId, RequestContext context, CancellationToken cancellationToken)
    {
        if (!await _db.Roles.AnyAsync(x => x.TenantId == tenantId && x.Id == roleId && !x.IsDeleted, cancellationToken))
            return false;
        var changedAtUtc = DateTime.UtcNow;
        var auditId = Guid.NewGuid();

        async Task<bool> DeactivateOnceAsync(CancellationToken ct)
        {
            _db.ChangeTracker.Clear();
            var tenant = await _db.Tenants.TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == tenantId, ct)
                ?? throw new InvalidOperationException("Tenant not found.");
            // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
            var role = await _db.Roles.IgnoreQueryFilters().TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == roleId && !x.IsDeleted, ct)
                ?? throw new InvalidOperationException("Role not found.");
            var caller = await LoadCallerCeilingAsync(tenantId, context, ct);
            ThrowIfRefused(PrivilegeCeiling.EditRefusal(caller, await RoleFactsAsync(role, ct), null));
            if (role.IsSystem) throw new InvalidOperationException("System roles cannot be deactivated.");
            var affectedIds = await _db.UserRoles.Where(x => x.RoleId == roleId)
                .Select(x => x.UserId).Distinct().OrderBy(x => x).ToListAsync(ct);
            var affectedUsers = await LockUsersAsync(tenantId, affectedIds, ct);
            role.IsActive = false;
            role.UpdatedAtUtc = changedAtUtc;
            role.UpdatedBy = context.UserId;
            await InvalidateAuthorizationSessionsAsync(affectedUsers, changedAtUtc, context, ct);
            _db.AuditLogs.Add(AuthAuditEntry.Create(
                auditId,
                changedAtUtc,
                "access.role_deactivated",
                "Role",
                roleId.ToString(),
                context with { TenantId = tenant.Id }));
            await _db.SaveChangesAsync(ct);
            return true;
        }

        await ExecuteAuthorizationTransactionAsync(auditId, "access.role_deactivated", DeactivateOnceAsync, cancellationToken);
        return true;
    }

    public async Task<RoleDto?> SetRolePermissionsAsync(Guid tenantId, Guid roleId, BulkRolePermissionsRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        if (!await _db.Roles.AnyAsync(x => x.TenantId == tenantId && x.Id == roleId && !x.IsDeleted, cancellationToken))
            return null;
        var changedAtUtc = DateTime.UtcNow;
        var auditId = Guid.NewGuid();
        var requestedKeys = request.Permissions.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        async Task<bool> SetOnceAsync(CancellationToken ct)
        {
            _db.ChangeTracker.Clear();
            var tenant = await _db.Tenants.TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == tenantId, ct)
                ?? throw new InvalidOperationException("Tenant not found.");
            // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
            var role = await _db.Roles.IgnoreQueryFilters().TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == roleId && !x.IsDeleted, ct)
                ?? throw new InvalidOperationException("Role not found.");
            await _db.RolePermissions.TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => x.RoleId == roleId)
                .OrderBy(x => x.PermissionId)
                .Select(x => x.PermissionId)
                .ToListAsync(ct);
            var affectedIds = await _db.UserRoles.Where(x => x.RoleId == roleId)
                .Select(x => x.UserId).Distinct().OrderBy(x => x).ToListAsync(ct);
            var affectedUsers = await LockUsersAsync(tenantId, affectedIds, ct);
            var permissions = requestedKeys.Count == 0
                ? new List<Domain.Entities.Permission>()
                : await _db.Permissions.Where(x => requestedKeys.Contains(x.Key)).ToListAsync(ct);
            if (permissions.Count != requestedKeys.Count)
                throw new InvalidOperationException("One or more permission keys do not exist.");

            // PRIVILEGE CEILING: not an Admin-only role, not your own role, and both the role's current and its
            // new permissions inside your own access.
            var caller = await LoadCallerCeilingAsync(tenantId, context, ct);
            var facts = await RoleFactsAsync(role, ct);
            ThrowIfRefused(PrivilegeCeiling.EditRefusal(caller, facts, permissions.Select(x => x.Key)));

            var existing = await _db.RolePermissions.Where(x => x.RoleId == roleId).ToListAsync(ct);
            _db.RolePermissions.RemoveRange(existing);
            foreach (var permission in permissions)
                _db.RolePermissions.Add(new RolePermission
                {
                    RoleId = roleId,
                    PermissionId = permission.Id,
                    Permission = permission
                });
            role.UpdatedAtUtc = changedAtUtc;
            role.UpdatedBy = context.UserId;
            await InvalidateAuthorizationSessionsAsync(affectedUsers, changedAtUtc, context, ct);
            _db.AuditLogs.Add(AuthAuditEntry.Create(
                auditId,
                changedAtUtc,
                "access.role_permissions_set",
                "Role",
                roleId.ToString(),
                context with { TenantId = tenant.Id },
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    name = role.Name,
                    count = permissions.Count,
                    added = permissions.Select(x => x.Key).Except(facts.Permissions, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                    removed = facts.Permissions.Except(permissions.Select(x => x.Key), StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal).ToList()
                })));
            await _db.SaveChangesAsync(ct);
            return true;
        }

        await ExecuteAuthorizationTransactionAsync(auditId, "access.role_permissions_set", SetOnceAsync, cancellationToken);
        _db.ChangeTracker.Clear();
        var committed = await _db.Roles.AsNoTracking()
            .Include(x => x.RolePermissions).ThenInclude(x => x.Permission)
            .SingleAsync(x => x.TenantId == tenantId && x.Id == roleId && !x.IsDeleted, cancellationToken);
        return ToRoleDto(committed);
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
        // STRICTLY tenant-owned roles. Global (TenantId == null) roles are shared across every tenant —
        // letting a tenant admin's matrix save load them turned this into a cross-tenant
        // privilege-escalation write path (rewrite a null-tenant role's permissions once, affect all
        // tenants). Global roles are platform-managed; they may be viewable but are never writable here.
        var roles = await _db.Roles.Include(x => x.RolePermissions)
            .Where(x => x.TenantId == tenantId && roleIds.Contains(x.Id) && !x.IsDeleted)
            .ToListAsync(cancellationToken);
        var allPermissions = await _db.Permissions.ToListAsync(cancellationToken);
        var permMap = allPermissions.ToDictionary(p => p.Key, p => p, StringComparer.OrdinalIgnoreCase);
        var keyById = allPermissions.ToDictionary(p => p.Id, p => p.Key);

        // PRIVILEGE CEILING, judged for the whole save BEFORE anything is written: every role whose permissions
        // this save would actually change must be one the caller may edit, and its new permissions must all be
        // held by the caller. Roles sent back unchanged (the matrix posts every column) are not judged.
        var caller = await LoadCallerCeilingAsync(tenantId, context, cancellationToken);
        var changes = new List<object>();
        var changedRoleIds = new HashSet<Guid>();
        foreach (var role in roles.OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            if (!request.RolePermissions.TryGetValue(role.Id.ToString(), out var requested)) continue;
            var current = role.RolePermissions.Select(rp => keyById.GetValueOrDefault(rp.PermissionId))
                .Where(k => k is not null).Select(k => k!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var next = requested.Where(k => permMap.ContainsKey(k)).Select(k => permMap[k].Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (current.SetEquals(next)) continue;
            ThrowIfRefused(PrivilegeCeiling.EditRefusal(caller,
                new PrivilegeCeiling.RoleFacts(role.Id, role.Name, role.NormalizedName, role.TenantId, role.IsSystem, role.IsEditable, current.ToList()),
                next));
            changedRoleIds.Add(role.Id);
            changes.Add(new
            {
                roleId = role.Id,
                name = role.Name,
                added = next.Except(current, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                removed = current.Except(next, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal).ToList()
            });
        }

        roles = roles.Where(r => changedRoleIds.Contains(r.Id)).ToList();
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
        await _auditService.WriteAsync("access.permission_matrix_saved", "Tenant", tenantId.ToString(), context,
            System.Text.Json.JsonSerializer.Serialize(new { roles = changes }), cancellationToken);
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
        // PRIVILEGE CEILING: removing a Deny gives the permission back, so it is a grant like any other.
        var caller = await LoadCallerCeilingAsync(tenantId, context, cancellationToken);
        var target = await LoadAccessUser(tenantId, userId, entityScope, cancellationToken)
            ?? throw new InvalidOperationException("User not found.");
        ThrowIfRefused(PrivilegeCeiling.TargetRefusal(
            caller,
            target.Id,
            target.UserRoles.Any(x => x.Role is { NormalizedName: PrivilegeCeiling.AdminRoleNormalizedName, IsActive: true, IsDeleted: false }),
            AuthService.GetPermissions(target)));
        if (ov.IsActive && ov.Effect.Equals("Deny", StringComparison.OrdinalIgnoreCase))
            ThrowIfRefused(PrivilegeCeiling.GrantRefusal(caller, new[] { ov.PermissionKey }));
        _db.UserPermissionOverrides.Remove(ov);
        await RevokeActiveRefreshTokensAsync(userId, context, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync("access.permission_override_deleted", "UserPermissionOverride", overrideId.ToString(), context,
            System.Text.Json.JsonSerializer.Serialize(new { userId, permission = ov.PermissionKey, previousEffect = ov.Effect }), cancellationToken);
        return true;
    }

    private async Task ExecuteAuthorizationTransactionAsync(
        Guid auditId,
        string auditAction,
        Func<CancellationToken, Task<bool>> operation,
        CancellationToken cancellationToken)
    {
        if (!_db.Database.IsRelational())
        {
            await operation(cancellationToken);
            return;
        }

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteInTransactionAsync(
            operation,
            // IgnoreQueryFilters is intentional: commit verification of this command's own audit marker by its server-generated id; no tenant data is read (register §6).
            async ct => await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(x => x.Id == auditId && x.Action == auditAction, ct),
            IsolationLevel.ReadCommitted,
            cancellationToken);
    }

    private async Task<IReadOnlyList<User>> LockAdminCohortAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
        var adminIds = await _db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenantId
                && !x.IsDeleted
                && x.UserRoles.Any(ur => ur.Role != null
                    && (ur.Role.TenantId == tenantId || ur.Role.TenantId == null)
                    && ur.Role.NormalizedName == "ADMIN"
                    && ur.Role.IsActive
                    && !ur.Role.IsDeleted))
            .OrderBy(x => x.Id)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        if (adminIds.Count == 0) return Array.Empty<User>();

        // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
        await _db.Users.IgnoreQueryFilters().TagWith(RowLockingInterceptor.ForUpdateTag)
            .Where(x => x.TenantId == tenantId && adminIds.Contains(x.Id))
            .OrderBy(x => x.Id)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        await _db.UserRoles.TagWith(RowLockingInterceptor.ForUpdateTag)
            .Where(x => adminIds.Contains(x.UserId))
            .OrderBy(x => x.UserId).ThenBy(x => x.RoleId)
            .Select(x => new { x.UserId, x.RoleId })
            .ToListAsync(cancellationToken);
        // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
        await _db.EmployeeUserAccounts.IgnoreQueryFilters().TagWith(RowLockingInterceptor.ForUpdateTag)
            .Where(x => x.TenantId == tenantId && x.UserId.HasValue && adminIds.Contains(x.UserId.Value))
            .OrderBy(x => x.UserId).ThenBy(x => x.Id)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        return await _db.Users.IgnoreQueryFilters()
            .Include(x => x.UserRoles).ThenInclude(x => x.Role)
            .Include(x => x.EmployeeUserAccounts)
            .Where(x => x.TenantId == tenantId && adminIds.Contains(x.Id))
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
    }

    private static bool IsOperationalAdmin(User user, DateTime atUtc) =>
        IsOperationalIdentity(user, atUtc)
        && user.UserRoles.Any(x => x.Role is
        {
            NormalizedName: "ADMIN",
            IsActive: true,
            IsDeleted: false
        });

    private static bool IsOperationalIdentity(User user, DateTime atUtc)
    {
        if (user.IsDeleted
            || !user.IsActive
            || !user.IsEmailConfirmed
            || !string.Equals(user.Status, "Active", StringComparison.Ordinal)
            || user.MustChangePassword
            || string.Equals(user.AccessMode, AccessModes.NoLogin, StringComparison.Ordinal)
            || (user.IsLocked && (!user.LockoutEnd.HasValue || user.LockoutEnd > atUtc))
            || (user.LockoutEnd.HasValue && user.LockoutEnd > atUtc))
            return false;

        var primary = AuthCurrentEligibility.PrimaryAccess(user);
        return !string.Equals(primary?.AccessMode, AccessModes.NoLogin, StringComparison.Ordinal)
            && primary?.RequiresPasswordSetup != true;
    }

    private static void EnsureAnotherOperationalAdmin(
        IEnumerable<User> adminCohort,
        Guid excludedUserId,
        DateTime atUtc)
    {
        if (!adminCohort.Any(x => x.Id != excludedUserId && IsOperationalAdmin(x, atUtc)))
            throw new InvalidOperationException("Cannot remove or block the last administrator. Add another active admin first.");
    }

    private async Task<User?> LockAccessUserAsync(
        Guid tenantId,
        Guid userId,
        EntityScopeContext entityScope,
        CancellationToken cancellationToken)
    {
        var anchor = await _db.Users.TagWith(RowLockingInterceptor.ForUpdateTag)
            .ApplyEntityScope(_db, tenantId, entityScope)
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == userId && !x.IsDeleted, cancellationToken);
        if (anchor is null) return null;

        await _db.UserRoles.TagWith(RowLockingInterceptor.ForUpdateTag)
            .Where(x => x.UserId == userId)
            .OrderBy(x => x.RoleId)
            .Select(x => x.RoleId)
            .ToListAsync(cancellationToken);
        // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
        await _db.UserPermissionOverrides.IgnoreQueryFilters().TagWith(RowLockingInterceptor.ForUpdateTag)
            .Where(x => x.TenantId == tenantId && x.UserId == userId)
            .OrderBy(x => x.Id)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        return await LoadAccessUser(tenantId, userId, entityScope, cancellationToken);
    }

    private async Task<IReadOnlyList<User>> LockUsersAsync(
        Guid tenantId,
        IEnumerable<Guid> userIds,
        CancellationToken cancellationToken)
    {
        var ids = userIds.Distinct().OrderBy(x => x).ToList();
        if (ids.Count == 0) return Array.Empty<User>();
        // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
        return await _db.Users.IgnoreQueryFilters().TagWith(RowLockingInterceptor.ForUpdateTag)
            .Where(x => x.TenantId == tenantId && ids.Contains(x.Id))
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
    }

    private async Task InvalidateAuthorizationSessionsAsync(
        IEnumerable<User> users,
        DateTime changedAtUtc,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        var principals = users.GroupBy(x => x.Id).Select(x => x.First()).OrderBy(x => x.Id).ToList();
        if (principals.Count == 0) return;
        var ids = principals.Select(x => x.Id).ToList();
        foreach (var user in principals)
            TenantSessionSecurity.RotateStamp(user, changedAtUtc);

        await _db.MfaChallengeTokens.TagWith(RowLockingInterceptor.ForUpdateTag)
            .Where(x => x.UserId.HasValue && ids.Contains(x.UserId.Value) && x.UsedAtUtc == null)
            .OrderBy(x => x.Id)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        await _db.RefreshTokens.TagWith(RowLockingInterceptor.ForUpdateTag)
            .Where(x => ids.Contains(x.UserId) && x.RevokedAtUtc == null)
            .OrderBy(x => x.Id)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        if (_db.Database.IsRelational())
        {
            await _db.MfaChallengeTokens
                .Where(x => x.UserId.HasValue && ids.Contains(x.UserId.Value) && x.UsedAtUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedAtUtc, changedAtUtc), cancellationToken);
            await _db.RefreshTokens
                .Where(x => ids.Contains(x.UserId) && x.RevokedAtUtc == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.RevokedAtUtc, changedAtUtc)
                    .SetProperty(x => x.RevokedByIp, context.IpAddress), cancellationToken);
            return;
        }

        foreach (var challenge in await _db.MfaChallengeTokens
            .Where(x => x.UserId.HasValue && ids.Contains(x.UserId.Value) && x.UsedAtUtc == null)
            .ToListAsync(cancellationToken))
            challenge.UsedAtUtc = changedAtUtc;
        foreach (var refresh in await _db.RefreshTokens
            .Where(x => ids.Contains(x.UserId) && x.RevokedAtUtc == null)
            .ToListAsync(cancellationToken))
        {
            refresh.RevokedAtUtc = changedAtUtc;
            refresh.RevokedByIp = context.IpAddress;
        }
    }

    private async Task MutateEligibilityStateAsync(
        Guid tenantId,
        Guid userId,
        EntityScopeContext entityScope,
        RequestContext context,
        string auditAction,
        string? auditMetadata,
        Action<User, DateTime> mutate,
        CancellationToken cancellationToken)
    {
        var changedAtUtc = DateTime.UtcNow;
        var auditId = Guid.NewGuid();

        async Task<bool> MutateOnceAsync(CancellationToken ct)
        {
            _db.ChangeTracker.Clear();
            var tenant = await _db.Tenants.TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == tenantId, ct);
            if (tenant is null)
                throw new InvalidOperationException("User not found.");
            var adminCohort = await LockAdminCohortAsync(tenantId, ct);
            var user = await LockAccessUserAsync(tenantId, userId, entityScope, ct)
                ?? throw new InvalidOperationException("User not found.");

            var blocksLogin = auditAction is "access.user_suspended" or "access.user_locked";
            if (blocksLogin && user.Id == context.UserId)
                throw new InvalidOperationException("You cannot suspend or lock your own account.");
            await EnsureMayManageAccountAsync(tenantId, context, user, ct);
            var wasOperationalAdmin = IsOperationalAdmin(user, changedAtUtc);
            var statusBefore = user.Status;

            mutate(user, changedAtUtc);
            if (wasOperationalAdmin && !IsOperationalAdmin(user, changedAtUtc))
                EnsureAnotherOperationalAdmin(adminCohort, user.Id, changedAtUtc);
            TenantSessionSecurity.RotateStamp(user, changedAtUtc);

            await _db.MfaChallengeTokens.TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => x.UserId == user.Id && x.UsedAtUtc == null)
                .OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(ct);
            await _db.RefreshTokens.TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => x.UserId == user.Id && x.RevokedAtUtc == null)
                .OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(ct);
            if (_db.Database.IsRelational())
            {
                await _db.MfaChallengeTokens
                    .Where(x => x.UserId == user.Id && x.UsedAtUtc == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedAtUtc, changedAtUtc), ct);
                await _db.RefreshTokens
                    .Where(x => x.UserId == user.Id && x.RevokedAtUtc == null)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.RevokedAtUtc, changedAtUtc)
                        .SetProperty(x => x.RevokedByIp, context.IpAddress), ct);
            }
            else
            {
                foreach (var challenge in await _db.MfaChallengeTokens
                    .Where(x => x.UserId == user.Id && x.UsedAtUtc == null).ToListAsync(ct))
                    challenge.UsedAtUtc = changedAtUtc;
                foreach (var refresh in await _db.RefreshTokens
                    .Where(x => x.UserId == user.Id && x.RevokedAtUtc == null).ToListAsync(ct))
                {
                    refresh.RevokedAtUtc = changedAtUtc;
                    refresh.RevokedByIp = context.IpAddress;
                }
            }

            _db.AuditLogs.Add(AuthAuditEntry.Create(
                auditId,
                changedAtUtc,
                auditAction,
                "User",
                user.Id.ToString(),
                context with { TenantId = tenantId },
                AddStatusChange(auditMetadata, statusBefore, user.Status)));
            await _db.SaveChangesAsync(ct);
            return true;
        }

        if (!_db.Database.IsRelational())
        {
            await MutateOnceAsync(cancellationToken);
            return;
        }

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteInTransactionAsync(
            MutateOnceAsync,
            // IgnoreQueryFilters is intentional: commit verification of this command's own audit marker by its server-generated id; no tenant data is read (register §6).
            async ct => await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(x => x.Id == auditId && x.Action == auditAction, ct),
            IsolationLevel.ReadCommitted,
            cancellationToken);
    }

    /// <summary>Adds the before/after account status to an eligibility audit row's metadata.</summary>
    private static string AddStatusChange(string? metadata, string before, string after)
    {
        var node = string.IsNullOrWhiteSpace(metadata)
            ? new System.Text.Json.Nodes.JsonObject()
            : System.Text.Json.Nodes.JsonNode.Parse(metadata) as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
        node["statusBefore"] = before;
        node["statusAfter"] = after;
        return node.ToJsonString();
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
