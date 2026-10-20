using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
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
        var callerId = await SeedAdminCallerAsync(w.TenantId);

        await service.AssignRolesAsync(
            w.TenantId,
            w.UserA,
            new AssignRolesRequest(new[] { "Employee" }),
            EntityScopeContext.GroupLevel,
            new RequestContext("10.10.10.10", "tests", callerId, w.TenantId),
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
        var callerId = await SeedAdminCallerAsync(w.TenantId);

        await service.SetPermissionOverrideAsync(
            w.TenantId,
            w.UserA,
            new PermissionOverrideRequest("employees.read", "Allow", "security-test", null),
            EntityScopeContext.GroupLevel,
            new RequestContext("10.20.30.40", "tests", callerId, w.TenantId),
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
            // Two seats: the acting Admin holds one (only an Admin may give the Admin role), the race is for the other.
            seed.TenantSubscriptions.Add(new TenantSubscription
            {
                TenantId = w.TenantId,
                Plan = "Starter",
                Status = SubscriptionStatuses.Active,
                MaxAdminUsers = 2
            });
            await seed.SaveChangesAsync();
        }
        var callerId = await SeedAdminCallerAsync(w.TenantId);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<Exception?> Create(string email)
        {
            await using var db = _fx.CreateDb();
            var service = CreateService(db);
            await gate.Task;
            try
            {
                await service.CreateUserAsync(w.TenantId,
                    new CreateUserRequest(email, email, "StrongPassword!123", new[] { "Admin" }, IsGroupScope: true),
                    new RequestContext("127.0.0.1", "tests", callerId, w.TenantId),
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
            && ioe.Message.Contains("at most 2 active administrator", StringComparison.Ordinal)).Should().Be(1);
        await using var verify = _fx.CreateDb();
        (await verify.Users.CountAsync(x => x.TenantId == w.TenantId && x.IsActive
            && x.UserRoles.Any(ur => ur.Role!.NormalizedName == "ADMIN"))).Should().Be(2);
    }

    [Fact]
    public async Task CreateCompanyAdministrator_CommitsRoleAndCompanyGrantAtomically()
    {
        var w = await SeedWorld();
        await using var db = _fx.CreateDb();
        await EnsureRoleAsync(db, w.TenantId, "Admin");
        var callerId = await SeedAdminCallerAsync(w.TenantId);
        var service = CreateService(db);
        var email = $"company-admin-{Guid.NewGuid():N}@example.test";

        var created = await service.CreateUserAsync(
            w.TenantId,
            new CreateUserRequest(email, "Company Administrator", "StrongPassword!123", new[] { "Admin" }, w.CompanyA),
            new RequestContext("127.0.0.1", "tests", callerId, w.TenantId),
            CancellationToken.None);

        created.IsGroupScope.Should().BeFalse();
        db.ChangeTracker.Clear();
        var persisted = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Include(user => user.UserRoles).ThenInclude(userRole => userRole.Role)
            .Include(user => user.EntityAccesses)
            .SingleAsync(user => user.Id == created.Id);
        persisted.UserRoles.Should().ContainSingle(userRole => userRole.Role!.NormalizedName == "ADMIN");
        persisted.EntityAccesses.Should().ContainSingle(grant =>
            grant.IsActive
            && grant.CompanyId == w.CompanyA
            && grant.GrantMode == EntityGrantModes.SelectedCompanies
            && grant.Role == "Company Administrator");
    }

    [Fact]
    public async Task CreateGroupAdministrator_RequiresExplicitGroupScopeAndCreatesNoCompanyGrant()
    {
        var w = await SeedWorld();
        await using var db = _fx.CreateDb();
        await EnsureRoleAsync(db, w.TenantId, "Admin");
        var callerId = await SeedAdminCallerAsync(w.TenantId);
        var service = CreateService(db);
        var email = $"group-admin-{Guid.NewGuid():N}@example.test";

        var created = await service.CreateUserAsync(
            w.TenantId,
            new CreateUserRequest(email, "Group Administrator", "StrongPassword!123", new[] { "Admin" }, IsGroupScope: true),
            new RequestContext("127.0.0.1", "tests", callerId, w.TenantId),
            CancellationToken.None);

        created.IsGroupScope.Should().BeTrue();
        db.ChangeTracker.Clear();
        (await db.UserEntityAccesses.IgnoreQueryFilters().CountAsync(grant => grant.UserId == created.Id)).Should().Be(0);
    }

    [Fact]
    public async Task CreateAdministrator_WithoutExplicitScope_IsRejectedBeforeMutation()
    {
        var w = await SeedWorld();
        await using var db = _fx.CreateDb();
        await EnsureRoleAsync(db, w.TenantId, "Admin");
        var callerId = await SeedAdminCallerAsync(w.TenantId);
        var service = CreateService(db);
        var email = $"ambiguous-admin-{Guid.NewGuid():N}@example.test";

        var act = () => service.CreateUserAsync(
            w.TenantId,
            new CreateUserRequest(email, "Ambiguous Administrator", "StrongPassword!123", new[] { "Admin" }),
            new RequestContext("127.0.0.1", "tests", callerId, w.TenantId),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*explicitly grant group-wide access*");
        (await db.Users.IgnoreQueryFilters().AnyAsync(user => user.TenantId == w.TenantId && user.Email == email)).Should().BeFalse();
    }

    // ── Controller gates with the company switcher (X-Company-Id) set ──────────────────────

    /// <summary>A security.manage caller; group-scoped when <paramref name="companyScope"/> is null,
    /// otherwise holding only those companies. <paramref name="selectedCompany"/> sets the switcher.</summary>
    private AccessController Controller(ZayraDbContext db, Guid tenantId, Guid callerId, Guid? selectedCompany, Guid[]? companyScope, bool v2 = false)
    {
        var claims = new List<System.Security.Claims.Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(System.Security.Claims.ClaimTypes.NameIdentifier, callerId.ToString()),
            new(System.Security.Claims.ClaimTypes.Role, "Admin"),
            new("permission", "security.manage"),
        };
        if (v2)
            // The current token shape: one entity_scope claim (v2), group or a company list.
            claims.Add(new(EntityScopeContext.V2ClaimType, System.Text.Json.JsonSerializer.Serialize(companyScope is null
                ? (object)new { v = 2, m = EntityScopeModes.Group }
                : new { v = 2, m = EntityScopeModes.Companies, c = companyScope })));
        else if (companyScope is null) claims.Add(new("is_group_scope", "true"));
        else claims.AddRange(companyScope.Select(c =>
            new System.Security.Claims.Claim("entity_access", System.Text.Json.JsonSerializer.Serialize(new { c, r = "Admin" }))));
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(claims, "test"))
        };
        if (selectedCompany is not null) http.Request.Headers[ZayraDbContext.CompanySelectionHeader] = selectedCompany.ToString();
        return new AccessController(CreateService(db), db, null!)
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = http }
        };
    }

    [Fact]
    public async Task GroupAdmin_WithCompanySelected_KeepsTenantSecurityAdministration()
    {
        var w = await SeedWorld();
        await using var db = _fx.CreateDb();
        var callerId = await SeedAdminCallerAsync(w.TenantId);

        (await Controller(db, w.TenantId, callerId, w.CompanyA, null).GetSecuritySettings(CancellationToken.None)).Result
            .Should().BeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>(
                "selecting a company narrows a group admin's reads; it must not revoke tenant-level security administration");
        (await Controller(db, w.TenantId, callerId, w.CompanyA, null).GetPermissionMatrix(CancellationToken.None)).Result
            .Should().BeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>();
    }

    [Fact]
    public async Task CompanyScopedAdmin_IsStillRefusedTenantSecurityAdministration()
    {
        var w = await SeedWorld();
        await using var db = _fx.CreateDb();
        var callerId = await SeedAdminCallerAsync(w.TenantId);

        foreach (var selected in new Guid?[] { null, w.CompanyA })
        {
            var scope = new[] { w.CompanyA };
            (await Controller(db, w.TenantId, callerId, selected, scope).GetSecuritySettings(CancellationToken.None)).Result
                .Should().BeOfType<Microsoft.AspNetCore.Mvc.ForbidResult>();
            (await Controller(db, w.TenantId, callerId, selected, scope).UpdateSecuritySettings(null!, CancellationToken.None)).Result
                .Should().BeOfType<Microsoft.AspNetCore.Mvc.ForbidResult>();
            (await Controller(db, w.TenantId, callerId, selected, scope).GetPermissionMatrix(CancellationToken.None)).Result
                .Should().BeOfType<Microsoft.AspNetCore.Mvc.ForbidResult>();
            (await Controller(db, w.TenantId, callerId, selected, scope).CreateRole(new CreateRoleRequest("Scoped role", null), CancellationToken.None)).Result
                .Should().BeOfType<Microsoft.AspNetCore.Mvc.ForbidResult>();
            (await Controller(db, w.TenantId, callerId, selected, scope).SetGroupScope(w.UserA, new SetGroupScopeRequest(true), CancellationToken.None))
                .Should().BeOfType<Microsoft.AspNetCore.Mvc.ForbidResult>();
        }
        (await db.Roles.AnyAsync(r => r.TenantId == w.TenantId && r.Name == "Scoped role")).Should().BeFalse();
    }

    [Fact]
    public async Task GroupAdmin_WithCompanySelected_CanCreateAdministratorForAnotherCompany()
    {
        var w = await SeedWorld();
        await using var db = _fx.CreateDb();
        await EnsureRoleAsync(db, w.TenantId, "Admin");
        var callerId = await SeedAdminCallerAsync(w.TenantId);
        var email = $"company-b-admin-{Guid.NewGuid():N}@example.test";

        var result = (await Controller(db, w.TenantId, callerId, w.CompanyA, null).CreateUser(
            new CreateUserRequest(email, "Company B Administrator", "StrongPassword!123", new[] { "Admin" }, w.CompanyB),
            CancellationToken.None)).Result;

        result.Should().BeOfType<Microsoft.AspNetCore.Mvc.CreatedAtActionResult>();
        db.ChangeTracker.Clear();
        (await db.UserEntityAccesses.IgnoreQueryFilters()
                .AnyAsync(g => g.TenantId == w.TenantId && g.User!.Email == email && g.CompanyId == w.CompanyB && g.IsActive))
            .Should().BeTrue();
    }

    [Fact]
    public async Task CompanyScopedAdmin_CannotCreateAdministratorForAnotherCompany()
    {
        var w = await SeedWorld();
        await using var db = _fx.CreateDb();
        await EnsureRoleAsync(db, w.TenantId, "Admin");
        var callerId = await SeedAdminCallerAsync(w.TenantId);
        var email = $"cross-admin-{Guid.NewGuid():N}@example.test";

        foreach (var selected in new Guid?[] { null, w.CompanyA })
        {
            (await Controller(db, w.TenantId, callerId, selected, new[] { w.CompanyA }).CreateUser(
                    new CreateUserRequest(email, "Company B Administrator", "StrongPassword!123", new[] { "Admin" }, w.CompanyB),
                    CancellationToken.None)).Result
                .Should().BeOfType<Microsoft.AspNetCore.Mvc.ForbidResult>();
            (await Controller(db, w.TenantId, callerId, selected, new[] { w.CompanyA }).CreateUser(
                    new CreateUserRequest(email, "Group Administrator", "StrongPassword!123", new[] { "Admin" }, IsGroupScope: true),
                    CancellationToken.None)).Result
                .Should().BeOfType<Microsoft.AspNetCore.Mvc.ForbidResult>();
        }
        (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.TenantId == w.TenantId && u.Email == email)).Should().BeFalse();
    }

    private async Task<Guid> SeedCompanyBGrantAsync(World w)
    {
        await using var db = _fx.CreateDb();
        var grant = new UserEntityAccess
        {
            TenantId = w.TenantId, UserId = w.UserB, CompanyId = w.CompanyB,
            GrantMode = EntityGrantModes.SelectedCompanies, Role = "HR"
        };
        db.UserEntityAccesses.Add(grant);
        await db.SaveChangesAsync();
        return grant.Id;
    }

    private static IReadOnlyCollection<EntityGrantDto> Grants(Microsoft.AspNetCore.Mvc.ActionResult<IReadOnlyCollection<EntityGrantDto>> result) =>
        (IReadOnlyCollection<EntityGrantDto>)((Microsoft.AspNetCore.Mvc.OkObjectResult)result.Result!).Value!;

    [Fact]
    public async Task GroupAdmin_WithCompanySelected_ListsAndCreatesEntityGrantsForAnyCompany()
    {
        var w = await SeedWorld();
        var grantB = await SeedCompanyBGrantAsync(w);
        await using var db = _fx.CreateDb();
        var callerId = await SeedAdminCallerAsync(w.TenantId);

        Grants(await Controller(db, w.TenantId, callerId, w.CompanyA, null).ListEntityGrants(null, CancellationToken.None))
            .Select(g => g.Id).Should().Contain(grantB, "the switcher narrows reads of data, not a group admin's view of access grants");

        (await Controller(db, w.TenantId, callerId, w.CompanyA, null).CreateEntityGrant(
                new CreateEntityGrantRequest(w.UserA, w.CompanyB, "HR"), CancellationToken.None)).Result
            .Should().BeOfType<Microsoft.AspNetCore.Mvc.CreatedAtActionResult>();
        (await Controller(db, w.TenantId, callerId, w.CompanyA, null).CreateEntityGrant(
                new CreateEntityGrantRequest(w.UnlinkedAdmin, null, "HR", EntityGrantModes.AllCurrentCompanies), CancellationToken.None)).Result
            .Should().BeOfType<Microsoft.AspNetCore.Mvc.CreatedAtActionResult>();
    }

    [Fact]
    public async Task CompanyScopedAdmin_CannotListOrGrantOutsideTheirCompanies()
    {
        var w = await SeedWorld();
        var grantB = await SeedCompanyBGrantAsync(w);
        await using var db = _fx.CreateDb();
        var callerId = await SeedAdminCallerAsync(w.TenantId);
        var scope = new[] { w.CompanyA };

        foreach (var selected in new Guid?[] { null, w.CompanyA })
        {
            var listed = Grants(await Controller(db, w.TenantId, callerId, selected, scope).ListEntityGrants(null, CancellationToken.None));
            listed.Select(g => g.Id).Should().NotContain(grantB);
            listed.Should().OnlyContain(g => g.CompanyId == w.CompanyA);

            (await Controller(db, w.TenantId, callerId, selected, scope).CreateEntityGrant(
                    new CreateEntityGrantRequest(w.UserA, w.CompanyB, "HR"), CancellationToken.None)).Result
                .Should().BeOfType<Microsoft.AspNetCore.Mvc.ForbidResult>("Company B is outside this admin's grant");
            (await Controller(db, w.TenantId, callerId, selected, scope).CreateEntityGrant(
                    new CreateEntityGrantRequest(w.UserA, null, "HR", EntityGrantModes.AllCurrentCompanies), CancellationToken.None)).Result
                .Should().BeOfType<Microsoft.AspNetCore.Mvc.ForbidResult>("an all-company grant is wider than this admin's own");
        }
        db.ChangeTracker.Clear();
        (await db.UserEntityAccesses.IgnoreQueryFilters().CountAsync(g => g.TenantId == w.TenantId && g.UserId == w.UserA))
            .Should().Be(0);

        (await Controller(db, w.TenantId, callerId, w.CompanyA, scope).CreateEntityGrant(
                new CreateEntityGrantRequest(w.UserA, w.CompanyA, "HR"), CancellationToken.None)).Result
            .Should().BeOfType<Microsoft.AspNetCore.Mvc.CreatedAtActionResult>("granting within their own company still works");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GroupAdmin_WithCompanySelected_ManagesAUserOfAnotherCompany(bool v2)
    {
        var w = await SeedWorld();
        await using var db = _fx.CreateDb();
        await EnsureRoleAsync(db, w.TenantId, "Team Viewer");
        var callerId = await SeedAdminCallerAsync(w.TenantId);
        AccessController C() => Controller(db, w.TenantId, callerId, w.CompanyA, null, v2);

        (await C().GetUser(w.UserB, CancellationToken.None)).Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>();
        (await C().UpdateUser(w.UserB, new UpdateUserRequest("Company B User (renamed)", null, null, null), CancellationToken.None)).Result
            .Should().BeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>();
        (await C().AssignRoles(w.UserB, new AssignRolesRequest(new[] { "Team Viewer" }), CancellationToken.None)).Result
            .Should().BeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompanyScopedAdmin_CannotManageAUserOfAnotherCompany(bool v2)
    {
        var w = await SeedWorld();
        await using var db = _fx.CreateDb();
        await EnsureRoleAsync(db, w.TenantId, "Team Viewer");
        await EnsureRoleAsync(db, w.TenantId, "Admin");
        var callerId = await SeedAdminCallerAsync(w.TenantId);
        var scope = new[] { w.CompanyA };

        foreach (var selected in new Guid?[] { null, w.CompanyA })
        {
            AccessController C() => Controller(db, w.TenantId, callerId, selected, scope, v2);
            (await C().GetUser(w.UserB, CancellationToken.None)).Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.NotFoundResult>();
            (await C().UpdateUser(w.UserB, new UpdateUserRequest("Taken over", null, null, null), CancellationToken.None)).Result
                .Should().BeOfType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>("the service treats a user outside the scope as not found");
            (await C().AssignRoles(w.UserB, new AssignRolesRequest(new[] { "Team Viewer" }), CancellationToken.None)).Result
                .Should().BeOfType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>();
            (await C().CreateUser(new CreateUserRequest($"x-{Guid.NewGuid():N}@example.test", "B Admin", "StrongPassword!123", new[] { "Admin" }, w.CompanyB),
                    CancellationToken.None)).Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.ForbidResult>();
            (await C().CreateEntityGrant(new CreateEntityGrantRequest(w.UserA, w.CompanyB, "HR"), CancellationToken.None)).Result
                .Should().BeOfType<Microsoft.AspNetCore.Mvc.ForbidResult>();
            (await C().GetSecuritySettings(CancellationToken.None)).Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.ForbidResult>();
            (await C().GetUser(w.UserA, CancellationToken.None)).Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>("own company's user");
        }
        db.ChangeTracker.Clear();
        var userB = await db.Users.IgnoreQueryFilters().Include(u => u.UserRoles).SingleAsync(u => u.Id == w.UserB);
        userB.FullName.Should().Be("Company B User");
        userB.UserRoles.Should().BeEmpty();
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

    /// <summary>
    /// An active Admin to act as. The privilege ceiling resolves the caller from the database, so a random
    /// caller id is refused; the Admin role here carries employees.read so an Allow override of it is in reach.
    /// </summary>
    private async Task<Guid> SeedAdminCallerAsync(Guid tenantId)
    {
        await using var db = _fx.CreateDb();
        await EnsureRoleAsync(db, tenantId, "Admin");
        var admin = await db.Roles.SingleAsync(r => r.TenantId == tenantId && r.NormalizedName == "ADMIN");
        var employeesRead = await db.Permissions.SingleAsync(p => p.Key == "employees.read");
        if (!await db.RolePermissions.AnyAsync(rp => rp.RoleId == admin.Id && rp.PermissionId == employeesRead.Id))
            db.RolePermissions.Add(new RolePermission { RoleId = admin.Id, PermissionId = employeesRead.Id });
        var caller = User(tenantId, $"caller-{Guid.NewGuid():N}@example.test", "Acting Admin");
        db.Users.Add(caller);
        db.UserRoles.Add(new UserRole { UserId = caller.Id, RoleId = admin.Id });
        await db.SaveChangesAsync();
        return caller.Id;
    }

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
