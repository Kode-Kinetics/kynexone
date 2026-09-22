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
        var auditMetadata = System.Text.Json.JsonSerializer.Serialize(new { email = canonicalEmail });
        var isAdminUser = normalizedRoleNames.Contains("ADMIN", StringComparer.Ordinal);
        await using var adminSeatLease = await AcquireAdminSeatLeaseAsync(
            tenantId, isAdminUser, cancellationToken);

        Guid[]? expectedRoleIds = null;

        async Task<AuthUserDto?> ReconcileCommittedUserAsync(CancellationToken ct)
        {
            if (expectedRoleIds is null) return null;
            _db.ChangeTracker.Clear();
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
            var tenant = await _db.Tenants.TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == tenantId && x.IsActive, ct)
                ?? throw new InvalidOperationException("Tenant not found.");

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
            accessMode
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
        await using var adminSeatLease = await AcquireAdminSeatLeaseAsync(tenantId, requestedAdmin, cancellationToken);

        async Task<bool> AssignOnceAsync(CancellationToken ct)
        {
            _db.ChangeTracker.Clear();
            var tenant = await _db.Tenants.TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == tenantId, ct)
                ?? throw new InvalidOperationException("Tenant not found.");
            var adminCohort = await LockAdminCohortAsync(tenantId, ct);
            var user = await LockAccessUserAsync(tenantId, userId, entityScope, ct)
                ?? throw new InvalidOperationException("User not found.");
            var roles = await LoadRoles(tenantId, request.Roles, ct);
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
                roles = roles.Select(x => x.Name).OrderBy(x => x).ToList()
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
                $"{{\"accessMode\":\"{accessMode}\",\"reason\":\"{request.Reason ?? string.Empty}\"}}"));
            await _db.SaveChangesAsync(ct);
            result = ToAccessDto(user);
            return true;
        }

        if (_db.Database.IsRelational())
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteInTransactionAsync(
                ChangeOnceAsync,
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

            var ov = user.PermissionOverrides.FirstOrDefault(x => x.PermissionKey == request.PermissionKey);
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
                effect
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
            var record = await _db.PermissionGrantorRecords.IgnoreQueryFilters()
                .TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == recordId, ct)
                ?? throw new InvalidOperationException("Permission grantor record not found.");
            record.IsActive = false;
            await InvalidateAuthorizationSessionsAsync(new[] { user }, changedAtUtc, context, ct);
            _db.AuditLogs.Add(AuthAuditEntry.Create(
                auditId,
                changedAtUtc,
                "access.grantor_revoked",
                "PermissionGrantorRecord",
                recordId.ToString(),
                context with { TenantId = tenant.Id }));
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

            var existing = await _db.UserPermissionOverrides.IgnoreQueryFilters()
                .SingleOrDefaultAsync(x => x.TenantId == tenantId
                    && x.UserId == targetUserId
                    && x.PermissionKey == request.PermissionKey, ct);
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
                effect = request.Effect
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

            var existingOverrides = await _db.UserPermissionOverrides.IgnoreQueryFilters()
                .Where(x => x.TenantId == tenantId && x.UserId == targetUserId)
                .ToListAsync(ct);
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

    private async Task<IAsyncDisposable> AcquireAdminSeatLeaseAsync(
        Guid tenantId, bool required, CancellationToken cancellationToken)
    {
        if (!required || !_db.Database.IsNpgsql()) return NoopAsyncDisposable.Instance;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"admin-seat:{tenantId:D}"));
        var key = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(digest.AsSpan(0, 8));
        await _db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_lock({key})", cancellationToken);
            return new AdvisoryLockLease(_db, key);
        }
        catch
        {
            await _db.Database.CloseConnectionAsync();
            throw;
        }
    }

    private sealed class AdvisoryLockLease(ZayraDbContext db, long key) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_unlock({key})"); }
            finally { await db.Database.CloseConnectionAsync(); }
        }
    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public static readonly NoopAsyncDisposable Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
                System.Text.Json.JsonSerializer.Serialize(new { name = role.Name })));
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
            var role = await _db.Roles.IgnoreQueryFilters().TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == roleId && !x.IsDeleted, ct)
                ?? throw new InvalidOperationException("Role not found.");
            if (!role.IsEditable) throw new InvalidOperationException("This role is not editable.");

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
                System.Text.Json.JsonSerializer.Serialize(new { name = role.Name })));
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
            var role = await _db.Roles.IgnoreQueryFilters().TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == roleId && !x.IsDeleted, ct)
                ?? throw new InvalidOperationException("Role not found.");
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
            var role = await _db.Roles.IgnoreQueryFilters().TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == roleId && !x.IsDeleted, ct)
                ?? throw new InvalidOperationException("Role not found.");
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
                System.Text.Json.JsonSerializer.Serialize(new { count = permissions.Count })));
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
            async ct => await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(x => x.Id == auditId && x.Action == auditAction, ct),
            IsolationLevel.ReadCommitted,
            cancellationToken);
    }

    private async Task<IReadOnlyList<User>> LockAdminCohortAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
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
            var wasOperationalAdmin = IsOperationalAdmin(user, changedAtUtc);

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
                auditMetadata));
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
            async ct => await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(x => x.Id == auditId && x.Action == auditAction, ct),
            IsolationLevel.ReadCommitted,
            cancellationToken);
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
