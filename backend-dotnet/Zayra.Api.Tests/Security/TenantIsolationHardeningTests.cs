using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Scope;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Proves the tenant-isolation hardening fixes actually close the holes they target, against real
/// Postgres (the same fixture the company-scope write tests use).
///
/// #1a — the read filter no longer fails OPEN on a null ambient tenant. The null bypass is gated on
///        a genuine system scope (no principal / explicit SystemScopeContext / platform super-admin);
///        any OTHER authenticated principal is filtered to its own tenant and, when it carries no
///        tenant_id, matches ZERO rows — including Guid.Empty platform-default rows and null-TenantId
///        orphans (it fails CLOSED, not to the Guid.Empty scope).
/// #2  — the write path now enforces the tenant dimension (stamp-on-null, reject foreign tenant,
///        block reassignment) in a user context, while leaving system/seeder writes untouched.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class TenantIsolationHardeningTests
{
    private const string PlatformAudience = "kynexone-platform";
    private const string TenantAudience   = "kynexone-tenant";

    private readonly PostgresFixture _fx;
    public TenantIsolationHardeningTests(PostgresFixture fx) => _fx = fx;

    // ── #1a: tenantless non-platform principal fails CLOSED (incl. Guid.Empty defaults) ──

    [Fact]
    public async Task Read_TenantlessNonPlatformPrincipal_SeesZeroRows_IncludingGuidEmptyDefaults()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        await using (var seed = _fx.CreateDb()) // system context — bypass, writes any tenant
        {
            seed.GosiContributionRules.AddRange(
                GosiRule(Guid.Empty),   // platform default — the SME-flagged trap row
                GosiRule(tenantA),
                GosiRule(tenantB));
            await seed.SaveChangesAsync();
        }

        // Authenticated principal, NO tenant_id, NOT platform — the "trap for the next endpoint".
        // Its filter is `false`, so it sees ZERO rows of the whole table — including the Guid.Empty
        // platform default (the SME-flagged trap) and any other tenant's rows.
        await using var db = _fx.CreateDbWithAccessor(Accessor(TenantlessPrincipal()));
        var all = await db.GosiContributionRules.AsNoTracking().ToListAsync();

        all.Should().BeEmpty(
            "a tenantless non-platform principal must fail CLOSED to zero rows — the null bypass is gone");
        all.Select(r => r.TenantId).Should().NotContain(Guid.Empty,
            "fail-closed means Guid.Empty platform-default rows are excluded too, not treated as an in-scope tenant");
    }

    [Fact]
    public async Task Read_TenantlessNonPlatformPrincipal_ExcludesNullTenantOrphanEmployees()
    {
        // The nullable-tenant filter variant must NOT let `TenantId == null` match null-TenantId
        // orphan rows for a tenantless principal (the specific asymmetry the guard closes).
        var codeOrphan = $"ORPH-{Guid.NewGuid():N}"[..12];
        var codeOwned  = $"OWND-{Guid.NewGuid():N}"[..12];
        var tenantA = Guid.NewGuid();

        await using (var seed = _fx.CreateDb())
        {
            seed.Employees.Add(NewEmployee(null, codeOrphan));     // orphan: TenantId == null
            seed.Employees.Add(NewEmployee(tenantA, codeOwned));   // owned by a real tenant
            await seed.SaveChangesAsync();
        }

        await using var db = _fx.CreateDbWithAccessor(Accessor(TenantlessPrincipal()));
        var visible = await db.Employees.AsNoTracking()
            .Where(e => e.EmployeeCode == codeOrphan || e.EmployeeCode == codeOwned).ToListAsync();

        visible.Should().BeEmpty(
            "a tenantless non-platform principal must see neither owned nor null-TenantId orphan employees");
    }

    // ── #1a: platform super-admin bypass PRESERVED (PlatformController depends on it) ──

    [Fact]
    public async Task Read_PlatformSuperAdmin_SeesAllTenants()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        await using (var seed = _fx.CreateDb())
        {
            seed.GosiContributionRules.AddRange(GosiRule(tenantA), GosiRule(tenantB));
            await seed.SaveChangesAsync();
        }

        // Platform token: is_platform_admin + platform audience + IOptions<JwtOptions> so the
        // DbContext can evaluate the platform-bypass branch.
        await using var db = _fx.CreateDbWithAccessor(Accessor(PlatformPrincipal()), JwtOpts());
        var mine = await db.GosiContributionRules.AsNoTracking()
            .Where(r => r.TenantId == tenantA || r.TenantId == tenantB).ToListAsync();

        mine.Select(r => r.TenantId).Should().Contain(tenantA).And.Contain(tenantB,
            "the platform super-admin is the by-design omnipotent cross-tenant reader PlatformController relies on");
    }

    // ── #1a: no regression — no-accessor system context still bypasses (seed/boot/worker) ──

    [Fact]
    public async Task Read_NoAccessorSystemContext_BypassesFilter()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        await using var db = _fx.CreateDb(); // no accessor — seeder/worker/backfill context
        db.GosiContributionRules.AddRange(GosiRule(tenantA), GosiRule(tenantB));
        await db.SaveChangesAsync();

        var mine = await db.GosiContributionRules.AsNoTracking()
            .Where(r => r.TenantId == tenantA || r.TenantId == tenantB).ToListAsync();

        mine.Select(r => r.TenantId).Should().Contain(tenantA).And.Contain(tenantB,
            "system contexts (no HttpContext) must keep the documented cross-tenant bypass for seeders/boot");
    }

    // ── #1a: no regression — a real tenant principal is scoped to its own tenant ──

    [Fact]
    public async Task Read_TenantAPrincipal_SeesOnlyTenantA()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        await using (var seed = _fx.CreateDb())
        {
            seed.GosiContributionRules.AddRange(GosiRule(tenantA), GosiRule(tenantB));
            await seed.SaveChangesAsync();
        }

        await using var db = _fx.CreateDbWithAccessor(Accessor(TenantPrincipal(tenantA)));
        var mine = await db.GosiContributionRules.AsNoTracking()
            .Where(r => r.TenantId == tenantA || r.TenantId == tenantB).ToListAsync();

        mine.Select(r => r.TenantId).Should().OnlyContain(t => t == tenantA,
            "a tenant principal sees exactly its own tenant — never tenant B");
        mine.Select(r => r.TenantId).Should().NotContain(tenantB);
    }

    // ── #1a: explicit SystemScopeContext flag toggles the bypass ──

    [Fact]
    public async Task Read_SystemScopeContextFlag_TogglesBypass()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        await using (var seed = _fx.CreateDb())
        {
            seed.GosiContributionRules.AddRange(GosiRule(tenantA), GosiRule(tenantB));
            await seed.SaveChangesAsync();
        }

        // A tenantless authenticated principal: closed by default, opened only inside Begin().
        await using var db = _fx.CreateDbWithAccessor(Accessor(TenantlessPrincipal()));

        var outside = await db.GosiContributionRules.AsNoTracking()
            .Where(r => r.TenantId == tenantA || r.TenantId == tenantB).ToListAsync();
        outside.Should().BeEmpty("outside a system scope, a tenantless principal fails closed");

        using (SystemScopeContext.Begin())
        {
            var inside = await db.GosiContributionRules.AsNoTracking()
                .Where(r => r.TenantId == tenantA || r.TenantId == tenantB).ToListAsync();
            inside.Select(r => r.TenantId).Should().Contain(tenantA).And.Contain(tenantB,
                "an explicit SystemScopeContext.Begin() opens the cross-tenant bypass for legitimate system work");
        }

        var afterDispose = await db.GosiContributionRules.AsNoTracking()
            .Where(r => r.TenantId == tenantA || r.TenantId == tenantB).ToListAsync();
        afterDispose.Should().BeEmpty("the bypass must close again once the scope is disposed");
    }

    // ── #2: write guard — stamp on null, reject foreign, block reassignment ──

    [Fact]
    public async Task Write_TenantAUser_NullTenantId_IsStamped()
    {
        var tenantA = Guid.NewGuid();
        var code = $"STMP-{Guid.NewGuid():N}"[..12];

        await using var db = _fx.CreateDbWithAccessor(Accessor(TenantPrincipal(tenantA)));
        var emp = NewEmployee(null, code); // TenantId omitted — the forgotten-stamp case
        db.Employees.Add(emp);
        await db.SaveChangesAsync();

        emp.TenantId.Should().Be(tenantA, "a missing TenantId is stamped from the ambient tenant — no orphan row");
    }

    [Fact]
    public async Task Write_TenantAUser_ForeignTenantId_IsBlocked()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        await using var db = _fx.CreateDbWithAccessor(Accessor(TenantPrincipal(tenantA)));
        db.Employees.Add(NewEmployee(tenantB, $"XTN-{Guid.NewGuid():N}"[..12])); // explicit foreign tenant

        var act = () => db.SaveChangesAsync();
        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*cross_tenant_write_blocked*",
                "a tenant-A actor must not create a row owned by tenant B");
    }

    [Fact]
    public async Task Write_TenantAUser_ModifyingForeignRow_IsBlocked()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var code = $"BMOD-{Guid.NewGuid():N}"[..12];

        await using (var seed = _fx.CreateDb())
        {
            seed.Employees.Add(NewEmployee(tenantB, code)); // owned by tenant B
            await seed.SaveChangesAsync();
        }

        await using var db = _fx.CreateDbWithAccessor(Accessor(TenantPrincipal(tenantA)));
        // Loaded via IgnoreQueryFilters (as a leaky code path might) — the write guard is the backstop.
        var bEmp = await db.Employees.IgnoreQueryFilters().FirstAsync(e => e.EmployeeCode == code);
        bEmp.FullName = "Tampered";

        var act = () => db.SaveChangesAsync();
        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*cross_tenant_write_blocked*",
                "modifying a row owned by another tenant must be blocked even when loaded past the read filter");
    }

    [Fact]
    public async Task Write_TenantAUser_ReassigningTenantId_IsBlocked()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var code = $"REAS-{Guid.NewGuid():N}"[..12];

        await using (var seed = _fx.CreateDb())
        {
            seed.Employees.Add(NewEmployee(tenantA, code)); // owned by tenant A
            await seed.SaveChangesAsync();
        }

        await using var db = _fx.CreateDbWithAccessor(Accessor(TenantPrincipal(tenantA)));
        var own = await db.Employees.FirstAsync(e => e.EmployeeCode == code);
        own.TenantId = tenantB; // attempt to hand the row to another tenant

        var act = () => db.SaveChangesAsync();
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*tenant_reassignment_blocked*",
                "reassigning a row's tenant is never allowed in a user context");
    }

    [Fact]
    public async Task Write_SystemContext_NoHttpUser_IsNotStampedOrBlocked()
    {
        var tenantB = Guid.NewGuid();
        var code = $"SYS-{Guid.NewGuid():N}"[..12];

        await using var db = _fx.CreateDb(); // no accessor — seeder/worker context
        var orphan = NewEmployee(null, code); // null tenant in a system context stays null
        db.Employees.Add(orphan);
        var foreignForTenantB = NewEmployee(tenantB, $"SYS2-{Guid.NewGuid():N}"[..12]);
        db.Employees.Add(foreignForTenantB); // arbitrary tenant allowed in system context

        await db.SaveChangesAsync(); // must not throw or stamp

        orphan.TenantId.Should().BeNull("system contexts are trusted — the tenant guard does not stamp them");
        foreignForTenantB.TenantId.Should().Be(tenantB, "system contexts may write any tenant (seeders/backfill)");
    }

    // ── Harness ────────────────────────────────────────────────────────────────

    // Rows are isolated in the shared Postgres db by their unique per-test tenant Guids
    // (CountryCode is varchar(5), too short for a marker), so queries filter on TenantId.
    private static GosiContributionRule GosiRule(Guid tenantId) => new()
    {
        Id             = Guid.NewGuid(),
        TenantId       = tenantId,
        CountryCode    = "SA",
        Classification = "Saudi",
        Branch         = "Annuities",
        Payer          = "Employee",
        Rate           = 9.75m,
        EffectiveFrom  = new DateOnly(2016, 6, 1),
        IsActive       = true,
    };

    private static Employee NewEmployee(Guid? tenantId, string code) => new()
    {
        TenantId     = tenantId,
        EmployeeCode = code,
        FullName     = $"Employee {code}",
        Status       = "Active",
        JoiningDate  = DateTime.UtcNow,
    };

    private static ClaimsPrincipal TenantlessPrincipal() =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "HR Manager"),
        }, authenticationType: "Test")); // authenticated, but NO tenant_id and NOT platform

    private static ClaimsPrincipal TenantPrincipal(Guid tenantId) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        }, authenticationType: "Test"));

    private static ClaimsPrincipal PlatformPrincipal() =>
        new(new ClaimsIdentity(new[]
        {
            new Claim("is_platform_admin", "true"),
            new Claim("aud", PlatformAudience),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        }, authenticationType: "Test")); // platform token: no tenant_id

    private static IOptions<JwtOptions> JwtOpts() =>
        Options.Create(new JwtOptions { PlatformAudience = PlatformAudience, TenantAudience = TenantAudience });

    private static IHttpContextAccessor Accessor(ClaimsPrincipal principal) =>
        new FixedAccessor { HttpContext = new DefaultHttpContext { User = principal } };

    private sealed class FixedAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
