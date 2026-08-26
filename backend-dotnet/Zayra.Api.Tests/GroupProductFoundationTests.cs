using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Final product batch: enterprise seeder, X-Company-Id switcher narrowing, company
/// lifecycle (creation modes, draft approval, suspend guard), and readiness math —
/// against real Postgres.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class GroupProductFoundationTests : Platform.PlatformTestBase
{
    private readonly PostgresFixture _fx;
    public GroupProductFoundationTests(PostgresFixture fx) => _fx = fx;

    // ── Enterprise seeder: full graph + idempotence ────────────────────────────

    [Fact]
    public async Task EnterpriseSeeder_SeedsThreeGroups_Idempotently_WithoutRealPii()
    {
        await using var db = _fx.CreateDb();
        var seeder = new EnterpriseGroupSeeder(db, new Pbkdf2PasswordHasher(), new SeederAuthSeederAdapter(db), NullLogger<EnterpriseGroupSeeder>.Instance);

        await seeder.SeedAsync();
        db.ChangeTracker.Clear();

        var almarai = await db.Tenants.AsNoTracking().SingleAsync(t => t.Slug == "almarai-test");
        almarai.AccountType.Should().Be(TenantAccountTypes.Group);

        var companies = await db.Companies.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.TenantId == almarai.Id).Select(c => c.LegalNameEn).ToListAsync();
        companies.Should().BeEquivalentTo(new[] { "ALM-DAIRY-KSA", "ALM-POULTRY-KSA", "ALM-BAKERY-KSA", "ALM-DIST-KSA", "ALM-UAE-TRD" });

        (await db.Tenants.CountAsync(t => t.Slug == "tata-test" || t.Slug == "emaar-test")).Should().Be(2);

        // Users: 6 group-scope + 1 scoped + (2 per company × 5) + 2 payroll = 19
        var users = await db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => u.TenantId == almarai.Id).ToListAsync();
        users.Should().HaveCount(19);
        users.Count(u => u.IsGroupScope).Should().Be(6);

        // Scoped admin: exactly two SelectedCompanies grants (first two companies).
        var scoped = users.Single(u => u.Email == "scoped.admin@almarai-test.local");
        var grants = await db.UserEntityAccesses.IgnoreQueryFilters().AsNoTracking()
            .Where(g => g.UserId == scoped.Id).ToListAsync();
        grants.Should().HaveCount(2);
        grants.Should().OnlyContain(g => g.GrantMode == EntityGrantModes.SelectedCompanies && g.CompanyId != null);

        // Workforce + operational data per company.
        var dairy = await db.Companies.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.TenantId == almarai.Id && c.LegalNameEn == "ALM-DAIRY-KSA");
        var employees = await db.Employees.IgnoreQueryFilters().AsNoTracking().Where(e => e.CompanyId == dairy.Id).ToListAsync();
        employees.Should().HaveCount(3);
        employees.Should().OnlyContain(e => e.BankIban == "" && e.PassportNumber == "" && e.IqamaNumber == "",
            "seed data must contain NO identifier PII — missing fields drive honest readiness gaps");
        (await db.Branches.IgnoreQueryFilters().CountAsync(b => b.CompanyId == dairy.Id)).Should().Be(2);
        (await db.AttendanceRecords.IgnoreQueryFilters().CountAsync(a => a.CompanyId == dairy.Id)).Should().Be(15);
        (await db.LeaveRequests.IgnoreQueryFilters().CountAsync(l => l.CompanyId == dairy.Id)).Should().Be(2);
        (await db.PayrollRuns.IgnoreQueryFilters().CountAsync(r => r.CompanyId == dairy.Id)).Should().Be(1);
        (await db.PayrollRuns.IgnoreQueryFilters().CountAsync(r => r.TenantId == almarai.Id)).Should().Be(5,
            "every legal entity needs a real payroll artifact for cross-company authorization coverage");
        (await db.CompanyComplianceProfiles.IgnoreQueryFilters().CountAsync(p => p.CompanyId == dairy.Id)).Should().Be(1);
        (await db.CompanyTaxPolicies.IgnoreQueryFilters().CountAsync(p => p.CompanyId == dairy.Id)).Should().Be(1);

        // Idempotent: second run changes nothing.
        await seeder.SeedAsync();
        (await db.Users.IgnoreQueryFilters().CountAsync(u => u.TenantId == almarai.Id)).Should().Be(19);
        (await db.Companies.IgnoreQueryFilters().CountAsync(c => c.TenantId == almarai.Id)).Should().Be(5);
    }

    // ── X-Company-Id switcher narrowing ─────────────────────────────────────────

    [Fact]
    public async Task CompanySelectionHeader_NarrowsGroupScope_AndFailsClosedWhenInaccessible()
    {
        await using var seed = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(seed);
        var a = new Company { TenantId = tenantId, LegalNameEn = "SW-A", RegistrationNumber = $"R-{Guid.NewGuid():N}", IsActive = true };
        var b = new Company { TenantId = tenantId, LegalNameEn = "SW-B", RegistrationNumber = $"R-{Guid.NewGuid():N}", IsActive = true };
        seed.Companies.AddRange(a, b);
        seed.Employees.AddRange(
            new Employee { TenantId = tenantId, CompanyId = a.Id, EmployeeCode = $"SWA-{Guid.NewGuid():N}"[..12], FullName = "A", Status = "Active", JoiningDate = DateTime.UtcNow },
            new Employee { TenantId = tenantId, CompanyId = b.Id, EmployeeCode = $"SWB-{Guid.NewGuid():N}"[..12], FullName = "B", Status = "Active", JoiningDate = DateTime.UtcNow });
        await seed.SaveChangesAsync();

        // Group-scope user narrows to company A via header — sees only A.
        var accessor = new FixedAccessor { HttpContext = HttpCtx(GroupPrincipal(tenantId), a.Id.ToString()) };
        await using var db = _fx.CreateDbWithAccessor(accessor);
        (await db.Employees.Where(e => e.TenantId == tenantId).ToListAsync())
            .Should().OnlyContain(e => e.CompanyId == a.Id);

        // Scoped-to-A user forging a header for B fails closed — sees NOTHING.
        accessor.HttpContext = HttpCtx(ScopedPrincipal(tenantId, a.Id), b.Id.ToString());
        (await db.Employees.Where(e => e.TenantId == tenantId).ToListAsync())
            .Should().BeEmpty("the switcher header can only narrow, never widen (URL/header tampering)");

        // Malformed header fails closed too — never a 500, never a leak.
        accessor.HttpContext = HttpCtx(GroupPrincipal(tenantId), "not-a-guid");
        (await db.Employees.Where(e => e.TenantId == tenantId).ToListAsync()).Should().BeEmpty();

        // No header: unchanged behavior.
        accessor.HttpContext = HttpCtx(GroupPrincipal(tenantId), null);
        (await db.Employees.Where(e => e.TenantId == tenantId).ToListAsync()).Should().HaveCount(2);
    }

    // ── Company lifecycle: creation modes + approval + suspend guard ───────────

    [Fact]
    public async Task CreationModes_PlatformControlledBlocks_DraftModeCreatesDraft_ApproveActivates()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        await db.Tenants.Where(t => t.Id == tenantId).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.AccountType, TenantAccountTypes.Group)
            .SetProperty(t => t.CompanyCreationMode, CompanyCreationModes.PlatformControlled));

        var controller = MakeCompaniesController(db, tenantId);
        var blocked = await controller.Create(NewCompanyRequest("PC Blocked"), CancellationToken.None);
        ((ObjectResult)blocked.Result!).StatusCode.Should().Be(403, "PlatformControlled tenants cannot self-create companies");

        await db.Tenants.Where(t => t.Id == tenantId).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.CompanyCreationMode, CompanyCreationModes.GroupDraftPlatformApproval));
        db.ChangeTracker.Clear();

        var created = await controller.Create(NewCompanyRequest("Draft Co"), CancellationToken.None);
        var dto = (Application.Organization.CompanyDto)((CreatedAtActionResult)created.Result!).Value!;
        dto.ApprovalStatus.Should().Be(CompanyApprovalStatuses.Draft);
        dto.IsActive.Should().BeFalse("drafts await platform approval");

        var platform = CreateController(db);
        (await platform.ApproveCompany(tenantId, dto.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        var approved = await db.Companies.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == dto.Id);
        approved.ApprovalStatus.Should().Be(CompanyApprovalStatuses.Active);
        approved.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task SuspendLastActiveCompany_IsBlocked_OthersToggleWithAudit()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var only = new Company { TenantId = tenantId, LegalNameEn = "LAST-ACTIVE", RegistrationNumber = $"R-{Guid.NewGuid():N}", IsActive = true };
        db.Companies.Add(only);
        await db.SaveChangesAsync();

        var controller = MakeCompaniesController(db, tenantId);
        var conflict = await controller.SetStatus(only.Id, new CompanyStatusRequest(false), CancellationToken.None);
        conflict.Should().BeOfType<ConflictObjectResult>("a tenant must always retain one operational company");

        var second = new Company { TenantId = tenantId, LegalNameEn = "SECOND", RegistrationNumber = $"R-{Guid.NewGuid():N}", IsActive = true };
        db.Companies.Add(second);
        await db.SaveChangesAsync();
        (await controller.SetStatus(only.Id, new CompanyStatusRequest(false), CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        (await db.AdminAuditLogs.IgnoreQueryFilters().AnyAsync(a => a.TenantId == tenantId && a.Action == "CompanySuspended")).Should().BeTrue();
    }

    // ── Readiness math ──────────────────────────────────────────────────────────

    [Fact]
    public void ReadinessCalculator_CountsMissingFields_AndFailsClosedOnUnknownOnes()
    {
        var employees = new[]
        {
            new ComplianceReadinessCalculator.EmployeeStatutoryFields("2000001", "", "", "", "", "", "", ""),
            new ComplianceReadinessCalculator.EmployeeStatutoryFields("", "", "", "", "", "", "", ""),
        };
        var json = """[{"field":"IqamaNumber","failClosed":true},{"field":"GosiReference","failClosed":true},{"field":"NotARealField","failClosed":true}]""";

        var result = ComplianceReadinessCalculator.Evaluate(json, employees);

        result.Should().HaveCount(3);
        result.Single(f => f.Field == "IqamaNumber").MissingEmployeeCount.Should().Be(1);
        result.Single(f => f.Field == "GosiReference").MissingEmployeeCount.Should().Be(2);
        // Fail CLOSED: an unresolvable key is counted MISSING for every employee (was silently present
        // under the old `_ => "n/a"` hole) so a statutory gap can never hide behind an unknown key.
        result.Single(f => f.Field == "NotARealField").MissingEmployeeCount.Should().Be(2, "unknown/unresolvable keys fail closed");
        ComplianceReadinessCalculator.Evaluate("{broken", employees).Should().BeEmpty();
    }

    // ── Harness ────────────────────────────────────────────────────────────────

    private static CompaniesController MakeCompaniesController(ZayraDbContext db, Guid tenantId) => new(
        new Zayra.Api.Infrastructure.Organization.OrganizationSetupService(db, new NullAudit()), db)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role, "Admin"),
                }, "Test"))
            }
        }
    };

    private static Application.Organization.CompanyRequest NewCompanyRequest(string name) => new(
        LegalNameEn: name, LegalNameAr: null, TradeName: null, CountryCode: "SA",
        Jurisdiction: "KSA-mainland", RegistrationNumber: $"REG-{Guid.NewGuid():N}",
        TaxNumber: null, WpsEmployerId: null, GosiEmployerId: null, QiwaEstablishmentId: null,
        DefaultCurrency: "SAR");

    private static DefaultHttpContext HttpCtx(ClaimsPrincipal principal, string? companyHeader)
    {
        var ctx = new DefaultHttpContext { User = principal };
        if (companyHeader is not null) ctx.Request.Headers[ZayraDbContext.CompanySelectionHeader] = companyHeader;
        return ctx;
    }

    private static ClaimsPrincipal GroupPrincipal(Guid tenantId) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
        }, "Test"));

    private static ClaimsPrincipal ScopedPrincipal(Guid tenantId, Guid companyId) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { companyId } })),
        }, "Test"));

    private sealed class FixedAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class NullAudit : Zayra.Api.Application.Auth.IAuditService
    {
        public Task WriteAsync(string action, string entityName, string? entityId, Zayra.Api.Application.Auth.RequestContext context, string? metadata, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    /// <summary>Real AuthSeeder role provisioning for the enterprise seeder test.</summary>
    private sealed class SeederAuthSeederAdapter : Zayra.Api.Application.Auth.IAuthSeeder
    {
        private readonly AuthSeeder _inner;
        public SeederAuthSeederAdapter(ZayraDbContext db) =>
            _inner = new AuthSeeder(db, new Pbkdf2PasswordHasher(),
                Microsoft.Extensions.Options.Options.Create(new Zayra.Api.Application.Auth.SeedAdminOptions()));
        public Task SeedAsync(CancellationToken cancellationToken = default) => _inner.SeedAsync(cancellationToken);
        public Task<Role> EnsureTenantRolesAsync(Guid tenantId, CancellationToken cancellationToken = default) => _inner.EnsureTenantRolesAsync(tenantId, cancellationToken);
    }
}
