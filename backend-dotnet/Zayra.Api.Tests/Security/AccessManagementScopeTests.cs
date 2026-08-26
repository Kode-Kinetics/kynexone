using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

[Trait("Category", "Integration")]
[Collection("Integration")]
public class AccessManagementScopeTests
{
    private readonly PostgresFixture _fx;
    public AccessManagementScopeTests(PostgresFixture fx) => _fx = fx;

    private sealed record World(Guid TenantId, Guid CompanyA, Guid CompanyB, Guid UserA, Guid UserB, Guid GrantOnlyA, Guid UnlinkedAdmin);

    [Fact]
    public async Task CompanyScopedAdmin_UserList_IsLimitedToLinkedOrGrantedCompanies()
    {
        var w = await SeedWorld();
        await using var db = _fx.CreateDb();
        var service = CreateService(db);

        var result = await service.ListUsersAsync(
            w.TenantId,
            new UserListQuery(null, null, null, 1, 50),
            EntityScopeContext.ForCompanies(new[] { w.CompanyA }),
            CancellationToken.None);

        result.Items.Select(x => x.Id).Should().BeEquivalentTo(new[] { w.UserA, w.GrantOnlyA },
            "Company A HR must see only users tied to Company A employees or explicit Company A grants");
    }

    [Fact]
    public async Task GroupScopeAdmin_UserList_CanSeeAllTenantUsers()
    {
        var w = await SeedWorld();
        await using var db = _fx.CreateDb();
        var service = CreateService(db);

        var result = await service.ListUsersAsync(
            w.TenantId,
            new UserListQuery(null, null, null, 1, 50),
            EntityScopeContext.GroupLevel,
            CancellationToken.None);

        result.Items.Select(x => x.Id).Should().Contain(new[] { w.UserA, w.UserB, w.GrantOnlyA, w.UnlinkedAdmin });
    }

    [Fact]
    public async Task CompanyScopedAdmin_CannotReadSiblingCompanyUserDetail()
    {
        var w = await SeedWorld();
        await using var db = _fx.CreateDb();
        var service = CreateService(db);

        var hidden = await service.GetUserAsync(
            w.TenantId,
            w.UserB,
            EntityScopeContext.ForCompanies(new[] { w.CompanyA }),
            CancellationToken.None);

        hidden.Should().BeNull("Company A HR must receive 404 semantics for Company B users");
    }

    [Fact]
    public async Task CompanyScopedAdmin_CannotMutateSiblingCompanyUserLifecycle()
    {
        var w = await SeedWorld();
        await using var db = _fx.CreateDb();
        var service = CreateService(db);

        var act = () => service.SuspendUserAsync(
            w.TenantId,
            w.UserB,
            "cross-company attempt",
            EntityScopeContext.ForCompanies(new[] { w.CompanyA }),
            new RequestContext(null, null, Guid.NewGuid(), w.TenantId),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("User not found.");
    }

    [Fact]
    public async Task CompanyScopedGrantor_CannotGrantPermissionToSiblingCompanyUser()
    {
        var w = await SeedWorld();
        await using var db = _fx.CreateDb();
        var service = CreateService(db);

        var result = await service.GrantPermissionAsync(
            w.TenantId,
            w.UserB,
            new GrantPermissionRequest("employees.read", "Allow", "cross-company attempt", null),
            EntityScopeContext.ForCompanies(new[] { w.CompanyA }),
            callerUserId: Guid.NewGuid(),
            isAdmin: true,
            CancellationToken.None);

        result.Should().BeNull("permission overrides are user-admin actions and must honor target population scope");
    }

    [Fact]
    public async Task AssignRoles_RevokesActiveRefreshTokens_ForTargetUser()
    {
        var w = await SeedWorld();
        await using var db = _fx.CreateDb();
        var service = CreateService(db);
        await EnsureRoleAsync(db, w.TenantId, "Employee");
        await AddActiveRefreshTokenAsync(db, w.UserA);

        await service.AssignRolesAsync(
            w.TenantId,
            w.UserA,
            new AssignRolesRequest(new[] { "Employee" }),
            EntityScopeContext.GroupLevel,
            new RequestContext("10.10.10.10", "tests", Guid.NewGuid(), w.TenantId),
            CancellationToken.None);

        var token = await db.RefreshTokens.SingleAsync(t => t.UserId == w.UserA);
        token.RevokedAtUtc.Should().NotBeNull("role assignments change access-token role claims");
        token.RevokedByIp.Should().Be("10.10.10.10");
    }

    [Fact]
    public async Task PermissionOverride_RevokesActiveRefreshTokens_ForTargetUser()
    {
        var w = await SeedWorld();
        await using var db = _fx.CreateDb();
        var service = CreateService(db);
        await AddActiveRefreshTokenAsync(db, w.UserA);

        await service.SetPermissionOverrideAsync(
            w.TenantId,
            w.UserA,
            new PermissionOverrideRequest("employees.read", "Allow", "security-test", null),
            EntityScopeContext.GroupLevel,
            new RequestContext("10.20.30.40", "tests", Guid.NewGuid(), w.TenantId),
            CancellationToken.None);

        var token = await db.RefreshTokens.SingleAsync(t => t.UserId == w.UserA);
        token.RevokedAtUtc.Should().NotBeNull("permission overrides change effective permissions in newly issued access tokens");
        token.RevokedByIp.Should().Be("10.20.30.40");
    }

    [Fact]
    public async Task ConcurrentAdminCreation_EnforcesSubscriptionSeatLimitAcrossDbContexts()
    {
        var w = await SeedWorld();
        await using (var seed = _fx.CreateDb())
        {
            await EnsureRoleAsync(seed, w.TenantId, "Admin");
            seed.TenantSubscriptions.Add(new TenantSubscription
            {
                TenantId = w.TenantId,
                Plan = "Starter",
                Status = SubscriptionStatuses.Active,
                MaxAdminUsers = 1
            });
            await seed.SaveChangesAsync();
        }

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<Exception?> Create(string email)
        {
            await using var db = _fx.CreateDb();
            var service = CreateService(db);
            await gate.Task;
            try
            {
                await service.CreateUserAsync(w.TenantId,
                    new CreateUserRequest(email, email, "StrongPassword!123", new[] { "Admin" }),
                    new RequestContext("127.0.0.1", "tests", Guid.NewGuid(), w.TenantId),
                    CancellationToken.None);
                return null;
            }
            catch (Exception ex) { return ex; }
        }

        var first = Create("admin-one@example.test");
        var second = Create("admin-two@example.test");
        gate.SetResult();
        var results = await Task.WhenAll(first, second);

        results.Count(x => x is null).Should().Be(1);
        results.Count(x => x is InvalidOperationException ioe
            && ioe.Message.Contains("at most 1 active administrator", StringComparison.Ordinal)).Should().Be(1);
        await using var verify = _fx.CreateDb();
        (await verify.Users.CountAsync(x => x.TenantId == w.TenantId && x.IsActive
            && x.UserRoles.Any(ur => ur.Role!.NormalizedName == "ADMIN"))).Should().Be(1);
    }

    private async Task<World> SeedWorld()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        if (!await db.Permissions.AnyAsync(p => p.Key == "employees.read"))
        {
            db.Permissions.Add(new Permission
            {
                Id = Guid.NewGuid(),
                Key = "employees.read",
                Module = "Employees",
                Description = "Read employees"
            });
        }

        var companyA = new Company { TenantId = tenantId, LegalNameEn = "Access A", RegistrationNumber = $"A-{Guid.NewGuid():N}", IsActive = true };
        var companyB = new Company { TenantId = tenantId, LegalNameEn = "Access B", RegistrationNumber = $"B-{Guid.NewGuid():N}", IsActive = true };
        db.Companies.AddRange(companyA, companyB);
        await db.SaveChangesAsync();

        var empA = new Employee { TenantId = tenantId, CompanyId = companyA.Id, EmployeeCode = $"UA-{Guid.NewGuid():N}"[..12], FullName = "User A", Status = "Active", JoiningDate = DateTime.UtcNow };
        var empB = new Employee { TenantId = tenantId, CompanyId = companyB.Id, EmployeeCode = $"UB-{Guid.NewGuid():N}"[..12], FullName = "User B", Status = "Active", JoiningDate = DateTime.UtcNow };
        db.Employees.AddRange(empA, empB);
        await db.SaveChangesAsync();

        var userA = User(tenantId, "a@example.test", "Company A User");
        var userB = User(tenantId, "b@example.test", "Company B User");
        var grantOnlyA = User(tenantId, "grant-a@example.test", "Grant Only A");
        var unlinkedAdmin = User(tenantId, "admin@example.test", "Unlinked Group Admin");
        db.Users.AddRange(userA, userB, grantOnlyA, unlinkedAdmin);
        db.EmployeeUserAccounts.AddRange(
            new EmployeeUserAccount { TenantId = tenantId, EmployeeId = empA.Id, User = userA, UserId = userA.Id, AccessMode = AccessModes.HRPortal, Status = "Active", RequiresPasswordSetup = false },
            new EmployeeUserAccount { TenantId = tenantId, EmployeeId = empB.Id, User = userB, UserId = userB.Id, AccessMode = AccessModes.HRPortal, Status = "Active", RequiresPasswordSetup = false });
        db.UserEntityAccesses.Add(new UserEntityAccess
        {
            TenantId = tenantId,
            User = grantOnlyA,
            UserId = grantOnlyA.Id,
            CompanyId = companyA.Id,
            GrantMode = EntityGrantModes.SelectedCompanies,
            Role = "HR"
        });
        await db.SaveChangesAsync();

        return new World(tenantId, companyA.Id, companyB.Id, userA.Id, userB.Id, grantOnlyA.Id, unlinkedAdmin.Id);
    }

    private static User User(Guid tenantId, string email, string name) => new()
    {
        TenantId = tenantId,
        Email = email,
        NormalizedEmail = AuthService.Normalize(email),
        FullName = name,
        PasswordHash = "hash",
        AccessMode = AccessModes.FullPortal,
        Status = "Active",
        IsActive = true,
        IsEmailConfirmed = true
    };

    private static async Task EnsureRoleAsync(ZayraDbContext db, Guid tenantId, string roleName)
    {
        var normalized = AuthService.Normalize(roleName);
        if (await db.Roles.AnyAsync(r => r.TenantId == tenantId && r.NormalizedName == normalized))
            return;
        db.Roles.Add(new Role
        {
            TenantId = tenantId,
            Name = roleName,
            NormalizedName = normalized,
            Description = roleName,
            IsActive = true,
            IsEditable = true
        });
        await db.SaveChangesAsync();
    }

    private static async Task AddActiveRefreshTokenAsync(ZayraDbContext db, Guid userId)
    {
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = userId,
            TokenHash = Guid.NewGuid().ToString("N"),
            ExpiresAtUtc = DateTime.UtcNow.AddDays(7),
            CreatedByIp = "seed"
        });
        await db.SaveChangesAsync();
    }

    private static AccessManagementService CreateService(ZayraDbContext db) =>
        new(db, new Pbkdf2PasswordHasher(), new NullAuditService(), new FakeTokenService());

    private sealed class FakeTokenService : ITokenService
    {
        public string CreateAccessToken(User user, IReadOnlyCollection<string> roles, IReadOnlyCollection<string> permissions, Tenant tenant, IReadOnlyCollection<EntityAccessGrant> entityAccess, EntityScopeDescriptor entityScope, out DateTime expiresAtUtc)
        {
            expiresAtUtc = DateTime.UtcNow.AddHours(1);
            return $"fake-access-{user.Id}";
        }

        public string CreateSecureToken() => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

        public string HashToken(string token)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = System.Text.Encoding.UTF8.GetBytes(token);
            return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
        }
    }

    private sealed class NullAuditService : IAuditService
    {
        public Task WriteAsync(string action, string entityName, string? entityId, RequestContext context, string? metadata, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
