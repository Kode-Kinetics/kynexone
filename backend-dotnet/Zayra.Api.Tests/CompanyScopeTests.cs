using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// CompanyScope access-layer integration tests (Testcontainers / real Postgres).
///
/// Tests the defence-in-depth layer that sits ABOVE the global tenant filter:
/// a per-request, lazy company-scope predicate derived from JWT entity_access claims.
///
/// All tests use the same physical DbContext instance with a SwitchableHttpContextAccessor
/// to simulate AddDbContextPool reuse — proving there is no cached/stale scope state.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class CompanyScopeTests
{
    private readonly PostgresFixture _fx;
    public CompanyScopeTests(PostgresFixture fx) => _fx = fx;

    // ─────────────────────────────────────────────────────────────────────────────
    // Test (a): Pooled-context reuse — global-filter-only queries must not leak
    //           across company scopes when the accessor switches between requests.
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PooledContext_SameDbInstance_DifferentCompanyScopes_NoCrossCompanyLeak()
    {
        // ── Seed ──────────────────────────────────────────────────────────────────
        await using var seedDb = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(seedDb);

        var companyA = MakeCompany(tenantId, "Alpha Corp");
        var companyB = MakeCompany(tenantId, "Beta Corp");
        seedDb.Companies.AddRange(companyA, companyB);

        var empA = MakeEmployee(tenantId, "CS-A001", companyA.Id);
        var empB = MakeEmployee(tenantId, "CS-B001", companyB.Id);
        seedDb.Employees.AddRange(empA, empB);
        await seedDb.SaveChangesAsync();

        // ── One DbContext instance, switchable accessor (pool-reuse simulation) ──
        var accessor = new CsSwitchableAccessor();
        await using var db = _fx.CreateDbWithAccessor(accessor);

        // Request 1: User scoped to Company A only
        accessor.HttpContext = ScopedContext(tenantId, companyA.Id);
        var req1Ids = await db.Employees.Select(e => e.CompanyId).ToListAsync();
        req1Ids.Should().OnlyContain(id => id == companyA.Id,
            "global filter (company A scope) must exclude Company B employees");
        req1Ids.Should().NotContain(companyB.Id,
            "Company B employee must not leak into Company A scoped request");

        // Simulate pool reuse: same DbContext instance, accessor switches to User 2
        accessor.HttpContext = ScopedContext(tenantId, companyB.Id);
        var req2Ids = await db.Employees.Select(e => e.CompanyId).ToListAsync();
        req2Ids.Should().OnlyContain(id => id == companyB.Id,
            "after accessor switch the global filter must reflect Company B scope — no stale Company A value");
        req2Ids.Should().NotContain(companyA.Id,
            "Company A employee must not leak into Company B scoped request (pool-reuse leak path)");

        // Flip back to A — proves lazy re-resolution on every query
        accessor.HttpContext = ScopedContext(tenantId, companyA.Id);
        var req3Ids = await db.Employees.Select(e => e.CompanyId).ToListAsync();
        req3Ids.Should().OnlyContain(id => id == companyA.Id,
            "switching back to Company A scope must work cleanly — no cross-company contamination");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Test (b): Group-scope user sees all tenant companies, zero from other tenant.
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GroupScope_SeesAllTenantCompanies_ZeroFromOtherTenant()
    {
        await using var seedDb = _fx.CreateDb();
        var tenantA = await PostgresFixture.SeedMinimalTenant(seedDb);
        var tenantB = await PostgresFixture.SeedMinimalTenant(seedDb);

        var companyA1 = MakeCompany(tenantA, "TenantA-Company1");
        var companyA2 = MakeCompany(tenantA, "TenantA-Company2");
        var companyB1 = MakeCompany(tenantB, "TenantB-Company1");
        seedDb.Companies.AddRange(companyA1, companyA2, companyB1);

        seedDb.Employees.AddRange(
            MakeEmployee(tenantA, "GA-A1", companyA1.Id),
            MakeEmployee(tenantA, "GA-A2", companyA2.Id),
            MakeEmployee(tenantB, "GA-B1", companyB1.Id));
        await seedDb.SaveChangesAsync();

        var accessor = new CsSwitchableAccessor();
        await using var db = _fx.CreateDbWithAccessor(accessor);

        // Group-scope user for Tenant A: no entity_access claims → EntityScopeContext.GroupLevel
        accessor.HttpContext = GroupScopeContext(tenantA);
        var results = await db.Employees.ToListAsync();

        results.Select(e => e.CompanyId).Should().Contain(companyA1.Id,
            "group-scope user must see Company A1");
        results.Select(e => e.CompanyId).Should().Contain(companyA2.Id,
            "group-scope user must see Company A2");
        results.Select(e => e.CompanyId).Should().NotContain(companyB1.Id,
            "tenant filter must exclude Tenant B companies even under group scope");
        results.Should().OnlyContain(e => e.TenantId == tenantA,
            "tenant boundary must hold regardless of company scope");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Test (c): Scoped user sees only the granted company, not peers in same tenant.
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ScopedUser_SeesOnlyGrantedCompany_NotPeerCompanies()
    {
        await using var seedDb = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(seedDb);

        var companyA = MakeCompany(tenantId, "SC-Alpha");
        var companyB = MakeCompany(tenantId, "SC-Beta");
        seedDb.Companies.AddRange(companyA, companyB);

        var runA = new PayrollRun
        {
            TenantId = tenantId, CompanyId = companyA.Id,
            Year = 2026, Month = 3,  // distinct months to avoid IX_payroll_runs_tenant_id_year_month
            CreatedAtUtc = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var runB = new PayrollRun
        {
            TenantId = tenantId, CompanyId = companyB.Id,
            Year = 2026, Month = 4,
            CreatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var structA = new SalaryStructure
        {
            TenantId = tenantId, CompanyId = companyA.Id,
            Code = "SS-A", Name = "Alpha Structure",
            EffectiveDate = new DateOnly(2026, 1, 1),
        };
        var structB = new SalaryStructure
        {
            TenantId = tenantId, CompanyId = companyB.Id,
            Code = "SS-B", Name = "Beta Structure",
            EffectiveDate = new DateOnly(2026, 1, 1),
        };
        seedDb.PayrollRuns.AddRange(runA, runB);
        seedDb.SalaryStructures.AddRange(structA, structB);
        await seedDb.SaveChangesAsync();

        var accessor = new CsSwitchableAccessor();
        await using var db = _fx.CreateDbWithAccessor(accessor);

        // User scoped to Company A only
        accessor.HttpContext = ScopedContext(tenantId, companyA.Id);

        var runs = await db.PayrollRuns.ToListAsync();
        runs.Should().OnlyContain(r => r.CompanyId == companyA.Id,
            "scoped user must see only Company A payroll runs");
        runs.Should().NotContain(r => r.CompanyId == companyB.Id,
            "Company B payroll run must be excluded by company scope filter");

        var structs = await db.SalaryStructures.ToListAsync();
        structs.Should().OnlyContain(s => s.CompanyId == companyA.Id,
            "scoped user must see only Company A salary structures");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Test (d): Backfill preserves existing-user visibility.
    //           Users without entity_access claims (existing users who haven't
    //           re-authenticated yet) default to group scope — same as pre-migration.
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BackfilledGroupScopeUser_RetainsFullVisibility_BeforeReauth()
    {
        await using var seedDb = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(seedDb);

        var c1 = MakeCompany(tenantId, "BF-Company1");
        var c2 = MakeCompany(tenantId, "BF-Company2");
        seedDb.Companies.AddRange(c1, c2);
        seedDb.Employees.AddRange(
            MakeEmployee(tenantId, "BF-E1", c1.Id),
            MakeEmployee(tenantId, "BF-E2", c2.Id));
        await seedDb.SaveChangesAsync();

        var accessor = new CsSwitchableAccessor();
        await using var db = _fx.CreateDbWithAccessor(accessor);

        // Simulate a backfilled admin user: is_group_scope=true in DB but their existing
        // JWT has no entity_access claims (they haven't re-authenticated since migration).
        // EntityScopeContext.FromClaims returns GroupLevel for no-claims → same as before.
        accessor.HttpContext = GroupScopeContext(tenantId); // no entity_access claims

        var emps = await db.Employees.ToListAsync();
        emps.Should().HaveCount(2,
            "backfilled user (no entity_access claims) must see all tenant employees — " +
            "backward-compat group scope prevents regressions before re-authentication");
        emps.Select(e => e.CompanyId).Should().Contain(c1.Id);
        emps.Select(e => e.CompanyId).Should().Contain(c2.Id);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Test (e): Company-owned query with no matching grant returns empty, not error.
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ScopedUser_NoMatchingGrant_ReturnsEmpty_NotException()
    {
        await using var seedDb = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(seedDb);

        var companyA = MakeCompany(tenantId, "NMG-Alpha");
        var companyB = MakeCompany(tenantId, "NMG-Beta");
        seedDb.Companies.AddRange(companyA, companyB);
        seedDb.Employees.Add(MakeEmployee(tenantId, "NMG-E1", companyA.Id));
        await seedDb.SaveChangesAsync();

        var accessor = new CsSwitchableAccessor();
        await using var db = _fx.CreateDbWithAccessor(accessor);

        // User has a grant for Company B only — but all employees are in Company A
        accessor.HttpContext = ScopedContext(tenantId, companyB.Id);

        var emps = await db.Employees.ToListAsync(); // must not throw
        emps.Should().BeEmpty(
            "a user whose grant does not cover any company with matching data must get " +
            "an empty result set, not a cross-company leak and not an exception");

        var runs = await db.PayrollRuns.ToListAsync(); // also empty, no exception
        runs.Should().BeEmpty();

        var structs = await db.SalaryStructures.ToListAsync(); // also empty
        structs.Should().BeEmpty();
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────────

    private static HttpContext ScopedContext(Guid tenantId, Guid companyId)
    {
        var accessJson = JsonSerializer.Serialize(new { c = companyId, r = "Viewer" });
        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim("entity_access", accessJson),
        }, "Test"));
        return ctx;
    }

    private static HttpContext GroupScopeContext(Guid tenantId)
    {
        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            // Deliberately no entity_access claims → EntityScopeContext.FromClaims returns GroupLevel
        }, "Test"));
        return ctx;
    }

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

// ── Test-local helper: simulates DbContextPool reuse by letting a single test swap
//    the HttpContext on the same IHttpContextAccessor instance mid-test ─────────────
file sealed class CsSwitchableAccessor : IHttpContextAccessor
{
    public HttpContext? HttpContext { get; set; }
}
