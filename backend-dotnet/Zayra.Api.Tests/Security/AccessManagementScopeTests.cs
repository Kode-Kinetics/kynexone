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
