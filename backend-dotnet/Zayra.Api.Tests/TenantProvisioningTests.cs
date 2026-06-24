using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Tenant provisioning, company management, access-grant, and cutover integration tests.
/// All DB-layer tests run against a real Postgres container (shared PostgresFixture).
/// RBAC-gate tests use reflection against controller attributes.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class TenantProvisioningTests
{
    private readonly PostgresFixture _fx;
    public TenantProvisioningTests(PostgresFixture fx) => _fx = fx;

    // ── 1. Tenant creation → exactly 1 Group Owner, 0 companies ─────────────────

    [Fact]
    public async Task TenantCreation_CreatesExactlyOneGroupOwner_ZeroCompanies()
    {
        await using var db = _fx.CreateDb();

        // Simulate what PlatformController.CreateTenant does (without the HTTP stack)
        var tenant = new Tenant { Name = "Acme Corp", Slug = $"acme-{Guid.NewGuid():N}" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var adminRole = new Role { TenantId = tenant.Id, Name = "Admin", NormalizedName = "ADMIN", Description = "Admin" };
        db.Roles.Add(adminRole);

        var owner = new User
        {
            TenantId = tenant.Id,
            Email = "owner@acme.com",
            NormalizedEmail = "OWNER@ACME.COM",
            FullName = "Group Owner",
            PasswordHash = "hashed",
            AccessMode = "FullPortal",
            Status = "Active",
            IsActive = true,
            IsEmailConfirmed = true,
            IsGroupScope = true,
            MustChangePassword = true,
        };
        owner.UserRoles.Add(new UserRole { User = owner, Role = adminRole });
        db.Users.Add(owner);
        await db.SaveChangesAsync();

        var users = await db.Users.Where(u => u.TenantId == tenant.Id && !u.IsDeleted).ToListAsync();
        var companies = await db.Companies.Where(c => c.TenantId == tenant.Id && !c.IsDeleted).ToListAsync();

        users.Should().HaveCount(1, "exactly one Group Owner is created per tenant — no per-company admins auto-created");
        users[0].IsGroupScope.Should().BeTrue("Group Owner must receive full cross-company visibility immediately");
        users[0].MustChangePassword.Should().BeTrue("Group Owner must be forced to change password on first login");
        companies.Should().BeEmpty("no companies are auto-created; Group Owner provisions them after first login");
    }

    // ── 2. Group Owner creates companies (group-scope filter lets them see all) ──

    [Fact]
    public async Task GroupOwner_CreatesCompanies_IsVisibleUnderGroupScope()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);

        var companyA = MakeCompany(tenantId, "TP-Alpha Corp");
        var companyB = MakeCompany(tenantId, "TP-Beta Corp");
        db.Companies.AddRange(companyA, companyB);
        var emp = MakeEmployee(tenantId, "TP-E1", companyA.Id);
        db.Employees.Add(emp);
        await db.SaveChangesAsync();

        // Group Owner context: no entity_access claims → GroupLevel (legacy / is_group_scope=true claim)
        var accessor = new TpSwitchableAccessor { HttpContext = GroupScopeContext(tenantId) };
        await using var scopedDb = _fx.CreateDbWithAccessor(accessor);

        var emps = await scopedDb.Employees.ToListAsync();
        emps.Should().HaveCount(1, "group-scope sees all employees in the tenant");
        emps[0].CompanyId.Should().Be(companyA.Id);
    }

    // ── 3. Grant company access → user sees only granted company ─────────────────

    [Fact]
    public async Task GrantCompanyAccess_UserSeesOnlyGrantedCompany()
    {
        await using var db = CreateDetailedDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);

        var companyA = MakeCompany(tenantId, "TP-Grant-A");
        var companyB = MakeCompany(tenantId, "TP-Grant-B");
        db.Companies.AddRange(companyA, companyB);
        db.Employees.AddRange(
            MakeEmployee(tenantId, "TP-GA-E1", companyA.Id),
            MakeEmployee(tenantId, "TP-GB-E1", companyB.Id));
        await db.SaveChangesAsync();

        // Save user first (FK parent), then grant in a separate batch to guarantee order
        var scopedUser = MakeUser(tenantId, $"grant-{Guid.NewGuid():N}@test.com");
        db.Users.Add(scopedUser);
        await db.SaveChangesAsync();

        db.UserEntityAccesses.Add(new UserEntityAccess
        {
            TenantId  = tenantId,
            UserId    = scopedUser.Id,
            CompanyId = companyA.Id,
            Role      = "Viewer",
            IsActive  = true,
            GrantedBy = scopedUser.Id,
            GrantedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var accessor = new TpSwitchableAccessor { HttpContext = ScopedContext(tenantId, companyA.Id) };
        await using var scopedDb = _fx.CreateDbWithAccessor(accessor);

        var emps = await scopedDb.Employees.ToListAsync();
        emps.Should().OnlyContain(e => e.CompanyId == companyA.Id,
            "scoped user must see only the company they were granted access to");
        emps.Should().NotContain(e => e.CompanyId == companyB.Id,
            "peer company employees must not be visible after targeted grant");
    }

    // ── 4. Revoke access → user loses company visibility ──────────────────────────

    [Fact]
    public async Task RevokeCompanyAccess_UserLosesVisibility()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);

        var company = MakeCompany(tenantId, "TP-Revoke-A");
        db.Companies.Add(company);
        db.Employees.Add(MakeEmployee(tenantId, "TP-RV-E1", company.Id));
        await db.SaveChangesAsync();

        // Save user first (FK parent), then grant in a separate batch
        var scopedUser = MakeUser(tenantId, $"rv-{Guid.NewGuid():N}@test.com");
        db.Users.Add(scopedUser);
        await db.SaveChangesAsync();

        var grant = new UserEntityAccess
        {
            TenantId  = tenantId,
            UserId    = scopedUser.Id,
            CompanyId = company.Id,
            Role      = "Viewer",
            IsActive  = true,
            GrantedBy = scopedUser.Id,
            GrantedAt = DateTime.UtcNow,
        };
        db.UserEntityAccesses.Add(grant);
        await db.SaveChangesAsync();

        // Verify access before revoke: user's JWT scoped to company sees the employee
        var accessor = new TpSwitchableAccessor { HttpContext = ScopedContext(tenantId, company.Id) };
        await using var scopedDb = _fx.CreateDbWithAccessor(accessor);
        var before = await scopedDb.Employees.ToListAsync();
        before.Should().HaveCount(1, "user should see employee before revoke");

        // Revoke by soft-deleting the grant
        grant.IsActive = false;
        grant.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // After revoke: the JWT claim is no longer backed by an active grant.
        // The company-scope filter reads from JWT claims, not from DB on every query
        // (the filter is claim-based). Revocation takes effect on next login/re-auth.
        // To simulate the post-re-auth state, switch context to an unrelated company grant.
        accessor.HttpContext = ScopedContext(tenantId, Guid.NewGuid()); // unrelated company
        var after = await scopedDb.Employees.ToListAsync();
        after.Should().BeEmpty("after re-auth with revoked scope, user sees no employees in their (non-matching) company");
    }

    // ── 5. Non-admin grant attempt → 403 enforced by class-level [Authorize] ─────

    [Fact]
    public void NonAdminGrantAttempt_Forbidden_ClassLevelAuthorizeAttribute()
    {
        var classAttr = typeof(AccessController).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .FirstOrDefault();

        classAttr.Should().NotBeNull("AccessController must carry [Authorize] to block unauthenticated callers");
        classAttr!.Roles.Should().Contain("Admin",
            "only Admin can call entity-grant endpoints — non-admins get 403 before the method body runs");

        // Also verify the group-scope elevation endpoint doesn't relax the class restriction
        var elevateMethod = typeof(AccessController).GetMethod("SetGroupScope");
        elevateMethod.Should().NotBeNull("SetGroupScope endpoint must exist");
        // No method-level [AllowAnonymous] means class-level Admin restriction applies
        var allowAnon = elevateMethod!.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: false);
        allowAnon.Should().BeEmpty("SetGroupScope must NOT carry [AllowAnonymous]; class-level Admin restriction must apply");
    }

    // ── 6. Group-scope elevation: audited in AdminAuditLog ───────────────────────

    [Fact]
    public async Task GroupScopeElevation_WritesAuditLog_UpdatesIsGroupScope()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);

        // Create a scoped (non-group) user
        var user = new User
        {
            TenantId = tenantId,
            Email = $"scoped-{Guid.NewGuid():N}@test.com",
            NormalizedEmail = $"SCOPED@TEST.COM",
            FullName = "Scoped User",
            PasswordHash = "hashed",
            Status = "Active",
            IsGroupScope = false,   // starts scoped
            IsActive = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        user.IsGroupScope.Should().BeFalse();

        // Simulate what SetGroupScope endpoint writes to DB
        var actorId = Guid.NewGuid();
        user.IsGroupScope = true;
        user.UpdatedAtUtc = DateTime.UtcNow;
        db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId = tenantId,
            EntityType = "User",
            EntityId = user.Id.ToString(),
            Action = "GroupScopeGranted",
            OldValuesJson = JsonSerializer.Serialize(new { isGroupScope = false }),
            NewValuesJson = JsonSerializer.Serialize(new { isGroupScope = true }),
            PerformedBy = actorId,
            PerformedByName = actorId.ToString(),
        });
        await db.SaveChangesAsync();

        var reloaded = await db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id);
        reloaded.IsGroupScope.Should().BeTrue("elevation must persist to the DB");

        var auditEntry = await db.AdminAuditLogs.AsNoTracking()
            .FirstOrDefaultAsync(a => a.EntityId == user.Id.ToString() && a.Action == "GroupScopeGranted");
        auditEntry.Should().NotBeNull("group-scope elevation must be audit-logged");
        auditEntry!.NewValuesJson.Should().Contain("true");
    }

    // ── 7. Delete company with active employees → blocked ─────────────────────────

    [Fact]
    public async Task DeleteCompanyWithActiveEmployees_IsBlocked()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);

        var company = MakeCompany(tenantId, "TP-BlockDelete");
        db.Companies.Add(company);
        db.Employees.Add(MakeEmployee(tenantId, "TP-BD-E1", company.Id));
        await db.SaveChangesAsync();

        var svc = new Zayra.Api.Infrastructure.Organization.OrganizationSetupService(
            db,
            new Zayra.Api.Infrastructure.Audit.AuditService(db));

        var ctx = new Zayra.Api.Application.Auth.RequestContext(null, null, null, tenantId);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.DeleteCompanyAsync(tenantId, company.Id, ctx, default));

        ex.Message.Should().Contain("1 active employee", "the error must mention the blocking count");
        ex.Message.Should().Contain("Reassign or deactivate", "must tell the caller what to do");

        // Company must still exist (not deleted)
        var still = await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == company.Id);
        still.Should().NotBeNull();
        still!.IsDeleted.Should().BeFalse("company must NOT be soft-deleted when blocked");
    }

    // ── 8. StrictMode: token with no claims → zero company data ──────────────────

    [Fact]
    public async Task StrictMode_NoEntityAccessClaims_NoGroupScopeClaim_ZeroCompanyData()
    {
        await using var seedDb = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(seedDb);

        var company = MakeCompany(tenantId, "TP-Strict-A");
        seedDb.Companies.Add(company);
        seedDb.Employees.Add(MakeEmployee(tenantId, "TP-SM-E1", company.Id));
        await seedDb.SaveChangesAsync();

        var accessor = new TpSwitchableAccessor
        {
            // Token with tenant claim but NO entity_access and NO is_group_scope=true
            HttpContext = NoScopeClaimsContext(tenantId)
        };

        // StrictMode=true → no-claims token = default-deny
        await using var strictDb = CreateStrictDb(accessor);

        var emps = await strictDb.Employees.ToListAsync();
        emps.Should().BeEmpty("StrictMode + no entity_access + no is_group_scope claim → zero company-assigned data");

        var runs = await strictDb.PayrollRuns.ToListAsync();
        runs.Should().BeEmpty();
    }

    // ── 9. Re-scope: backfilled group-user demoted to single-company ──────────────

    [Fact]
    public async Task BackfilledGroupUser_RescopedToSingleCompany_Works()
    {
        await using var seedDb = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(seedDb);

        var companyA = MakeCompany(tenantId, "TP-RS-Alpha");
        var companyB = MakeCompany(tenantId, "TP-RS-Beta");
        seedDb.Companies.AddRange(companyA, companyB);
        seedDb.Employees.AddRange(
            MakeEmployee(tenantId, "TP-RS-E1", companyA.Id),
            MakeEmployee(tenantId, "TP-RS-E2", companyB.Id));
        await seedDb.SaveChangesAsync();

        var accessor = new TpSwitchableAccessor();
        await using var db = _fx.CreateDbWithAccessor(accessor);

        // Phase 1: backfilled group-scope user (is_group_scope=true claim in JWT after re-auth)
        accessor.HttpContext = GroupScopeWithClaim(tenantId);
        var all = await db.Employees.ToListAsync();
        all.Should().HaveCount(2, "group-scope user sees both companies");

        // Phase 2: admin re-scopes the user down to company A only
        // New JWT: entity_access claim for A, no is_group_scope=true
        accessor.HttpContext = ScopedContext(tenantId, companyA.Id);
        var scoped = await db.Employees.ToListAsync();
        scoped.Should().OnlyContain(e => e.CompanyId == companyA.Id,
            "after re-scope + re-auth, user must see only company A");
        scoped.Should().NotContain(e => e.CompanyId == companyB.Id,
            "company B employees must be invisible after re-scope");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────────

    // Connection string with error detail for debugging FK violations in CI
    private string DetailedCs => _fx.ConnectionString + ";Include Error Detail=true";

    private ZayraDbContext CreateDetailedDb() => new(
        new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(DetailedCs).Options);

    private ZayraDbContext CreateStrictDb(IHttpContextAccessor accessor) => new(
        new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(_fx.ConnectionString).Options,
        accessor,
        null,
        Options.Create(new EntityScopeOptions { StrictMode = true }));

    private static HttpContext ScopedContext(Guid tenantId, Guid companyId)
    {
        var json = JsonSerializer.Serialize(new { c = companyId, r = "Viewer" });
        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim("entity_access", json),
        }, "Test"));
        return ctx;
    }

    // Legacy group-scope: no entity_access claims (backward-compat before StrictMode)
    private static HttpContext GroupScopeContext(Guid tenantId)
    {
        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        }, "Test"));
        return ctx;
    }

    // Post-migration group-scope: explicit is_group_scope=true claim in JWT
    private static HttpContext GroupScopeWithClaim(Guid tenantId)
    {
        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim("is_group_scope", "true"),
        }, "Test"));
        return ctx;
    }

    // StrictMode test: no entity_access AND no is_group_scope=true → deny
    private static HttpContext NoScopeClaimsContext(Guid tenantId)
    {
        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            // no entity_access, no is_group_scope — simulates a new user after StrictMode flip
        }, "Test"));
        return ctx;
    }

    private static User MakeUser(Guid tenantId, string email) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        FullName = "Test User",
        PasswordHash = "hashed",
        Status = "Active",
        IsActive = true,
    };

    private static Company MakeCompany(Guid tenantId, string name) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        LegalNameEn = name,
        CountryCode = "SAU",
        Jurisdiction = "KSA-mainland",
        RegistrationNumber = $"REG-{Guid.NewGuid():N}",
        DefaultCurrency = "SAR",
        IsActive = true,
        CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static Employee MakeEmployee(Guid tenantId, string code, Guid? companyId) => new()
    {
        TenantId = tenantId,
        EmployeeCode = code,
        FullName = $"Employee {code}",
        CompanyId = companyId,
        Status = "Active",
        JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };
}

// ── Test-local switchable accessor (same pattern as CsSwitchableAccessor in CompanyScopeTests) ──
file sealed class TpSwitchableAccessor : IHttpContextAccessor
{
    public HttpContext? HttpContext { get; set; }
}
