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
using Zayra.Api.Infrastructure.WorkWeek;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Final product batch: X-Company-Id switcher narrowing, company
/// lifecycle (creation modes, draft approval, suspend guard), and readiness math —
/// against real Postgres.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class GroupProductFoundationTests : Platform.PlatformTestBase
{
    private readonly PostgresFixture _fx;
    public GroupProductFoundationTests(PostgresFixture fx) => _fx = fx;

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
    public async Task CreationModes_PlatformControlledBlocks_AndTheRetiredDraftModeNowCreatesAnActiveCompany()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        await db.Tenants.Where(t => t.Id == tenantId).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.AccountType, TenantAccountTypes.Group)
            .SetProperty(t => t.CompanyCreationMode, CompanyCreationModes.PlatformControlled));

        var controller = MakeCompaniesController(db, tenantId);
        var blocked = await controller.Create(NewCompanyRequest("PC Blocked"), CancellationToken.None);
        ((ObjectResult)blocked.Result!).StatusCode.Should().Be(403, "PlatformControlled tenants cannot self-create companies");

        // G-013 (owner decision): the draft-approval mode is retired. A tenant still holding it (before the data migration
        // runs) is treated as the default mode, so a new company is created ACTIVE instead of stranded as a Draft.
        await db.Tenants.Where(t => t.Id == tenantId).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.CompanyCreationMode, CompanyCreationModes.GroupDraftPlatformApproval));
        db.ChangeTracker.Clear();

        var created = await controller.Create(NewCompanyRequest("Was Draft Co"), CancellationToken.None);
        var dto = (Application.Organization.CompanyDto)((CreatedAtActionResult)created.Result!).Value!;
        dto.ApprovalStatus.Should().Be(CompanyApprovalStatuses.Active);
        dto.IsActive.Should().BeTrue("no company is created as a Draft any more");
    }

    [Fact]
    public async Task TheRetiredDraftMode_CannotBeChosen_ByThePlatformConsole()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var platform = CreateController(db);

        var result = await platform.SetCompanyCreationMode(tenantId, new SetCompanyCreationModeRequest(CompanyCreationModes.GroupDraftPlatformApproval), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        CompanyCreationModes.IsValid(CompanyCreationModes.GroupDraftPlatformApproval).Should().BeFalse();
        CompanyCreationModes.IsValid(CompanyCreationModes.GroupSelfServiceWithinLimit).Should().BeTrue();
        CompanyCreationModes.IsValid(CompanyCreationModes.PlatformControlled).Should().BeTrue();
        db.ChangeTracker.Clear();
        (await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenantId)).CompanyCreationMode
            .Should().NotBe(CompanyCreationModes.GroupDraftPlatformApproval);
    }

    [Fact]
    public async Task APlatformAdmin_CanActivateACompanyStuckInDraft_Audited_AndOnlyOnce()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var stuck = new Company { TenantId = tenantId, LegalNameEn = "Stuck Draft", RegistrationNumber = $"R-{Guid.NewGuid():N}", CountryCode = "SA", DefaultCurrency = "SAR", IsActive = false, ApprovalStatus = CompanyApprovalStatuses.Draft };
        db.Companies.Add(stuck);
        await db.SaveChangesAsync();
        var platform = CreateController(db);

        var activated = await platform.ApproveCompany(tenantId, stuck.Id, CancellationToken.None);

        activated.Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        var company = await db.Companies.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == stuck.Id);
        company.IsActive.Should().BeTrue();
        company.ApprovalStatus.Should().Be(CompanyApprovalStatuses.Active);
        var audit = await db.AdminAuditLogs.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.EntityType == nameof(Company) && a.EntityId == stuck.Id.ToString()).ToListAsync();
        audit.Should().ContainSingle(a => a.Action == "CompanyActivatedByPlatform" && a.PerformedByName == AdminEmail);

        var again = await platform.ApproveCompany(tenantId, stuck.Id, CancellationToken.None);
        again.Should().BeOfType<ConflictObjectResult>("a company that is not a Draft is never re-activated through this door");
        System.Text.Json.JsonSerializer.Serialize(((ConflictObjectResult)again).Value).Should().Contain("company_not_draft");
    }

    [Fact]
    public async Task ActivatingADraft_CannotReachAnotherTenantsCompany_AndNeverRevivesASuspendedOne()
    {
        await using var db = _fx.CreateDb();
        var tenantA = await PostgresFixture.SeedMinimalTenant(db);
        var tenantB = await PostgresFixture.SeedMinimalTenant(db);
        var draftOfB = new Company { TenantId = tenantB, LegalNameEn = "B Draft", RegistrationNumber = $"R-{Guid.NewGuid():N}", CountryCode = "SA", DefaultCurrency = "SAR", IsActive = false, ApprovalStatus = CompanyApprovalStatuses.Draft };
        var suspendedOfA = new Company { TenantId = tenantA, LegalNameEn = "A Suspended", RegistrationNumber = $"R-{Guid.NewGuid():N}", CountryCode = "SA", DefaultCurrency = "SAR", IsActive = false, ApprovalStatus = CompanyApprovalStatuses.Active };
        db.Companies.AddRange(draftOfB, suspendedOfA);
        await db.SaveChangesAsync();
        var platform = CreateController(db);

        (await platform.ApproveCompany(tenantA, draftOfB.Id, CancellationToken.None)).Should().BeOfType<NotFoundObjectResult>("the company is looked up inside the named tenant only");
        (await platform.ApproveCompany(tenantA, suspendedOfA.Id, CancellationToken.None)).Should().BeOfType<ConflictObjectResult>("suspension is not a Draft");
        db.ChangeTracker.Clear();
        (await db.Companies.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == draftOfB.Id)).IsActive.Should().BeFalse();
        (await db.Companies.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == suspendedOfA.Id)).IsActive.Should().BeFalse();
    }

    /// <summary>The data migration is idempotent and leaves a trace: every tenant on the retired mode moves to the default mode with
    /// one audit row each, a second run changes nothing, and tenants on the other modes are untouched.</summary>
    [Fact]
    public async Task TheRetirementMigration_MovesTenantsToTheDefaultMode_WithAnAuditRow_AndIsIdempotent()
    {
        await using var db = _fx.CreateDb();
        var legacy = await PostgresFixture.SeedMinimalTenant(db);
        var controlled = await PostgresFixture.SeedMinimalTenant(db);
        await db.Tenants.Where(t => t.Id == legacy).ExecuteUpdateAsync(s => s.SetProperty(t => t.CompanyCreationMode, CompanyCreationModes.GroupDraftPlatformApproval));
        await db.Tenants.Where(t => t.Id == controlled).ExecuteUpdateAsync(s => s.SetProperty(t => t.CompanyCreationMode, CompanyCreationModes.PlatformControlled));

        await db.Database.ExecuteSqlRawAsync(Zayra.Api.Migrations.RetireDraftApprovalMode.RetireSql);
        await db.Database.ExecuteSqlRawAsync(Zayra.Api.Migrations.RetireDraftApprovalMode.RetireSql);

        db.ChangeTracker.Clear();
        (await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == legacy)).CompanyCreationMode.Should().Be(CompanyCreationModes.GroupSelfServiceWithinLimit);
        (await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == controlled)).CompanyCreationMode.Should().Be(CompanyCreationModes.PlatformControlled);
        var rows = await db.AdminAuditLogs.IgnoreQueryFilters().AsNoTracking().Where(a => a.Action == "CompanyCreationModeRetired" && (a.TenantId == legacy || a.TenantId == controlled)).ToListAsync();
        rows.Should().ContainSingle("one audit row for the one tenant that moved, none on the second run").Which.TenantId.Should().Be(legacy);
        rows[0].OldValuesJson.Should().Contain(CompanyCreationModes.GroupDraftPlatformApproval);
        rows[0].NewValuesJson.Should().Contain(CompanyCreationModes.GroupSelfServiceWithinLimit);
    }

    [Fact]
    public async Task CompanyScopedAdministrator_CannotCreateASiblingCompany()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var own = new Company { TenantId = tenantId, LegalNameEn = "OWN", RegistrationNumber = $"R-{Guid.NewGuid():N}", CountryCode = "SA", DefaultCurrency = "SAR", IsActive = true };
        db.Companies.Add(own);
        await db.SaveChangesAsync();

        var before = await db.Companies.IgnoreQueryFilters().CountAsync(c => c.TenantId == tenantId);
        var result = await MakeCompaniesController(db, tenantId, own.Id).Create(NewCompanyRequest("Sibling"), CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        (await db.Companies.IgnoreQueryFilters().CountAsync(c => c.TenantId == tenantId)).Should().Be(before);
    }

    /// <summary>
    /// Org catalogue rows are PER COMPANY (owner, 2026-10-23): a company-scoped administrator who creates a grade without
    /// naming a company gets it in their own company, never as a tenant-wide shared row. What they must NOT be able to do is
    /// touch a group-shared (null-company) grade that already exists: it reads as not found and is left unchanged.
    /// </summary>
    [Fact]
    public async Task CompanyScopedAdministrator_CannotMutateGroupSharedGrades()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var own = new Company { TenantId = tenantId, LegalNameEn = "OWN", RegistrationNumber = $"R-{Guid.NewGuid():N}", CountryCode = "SA", DefaultCurrency = "SAR", IsActive = true };
        db.Companies.Add(own);
        var shared = new Grade { TenantId = tenantId, CompanyId = null, Code = "G-SHARED", Name = "Group grade", Band = "Staff", Level = 1, IsActive = true };
        db.Grades.Add(shared);
        await db.SaveChangesAsync();
        var controller = new GradesController(
            new Zayra.Api.Infrastructure.Organization.OrganizationSetupService(db, new NullAudit()), db)
        {
            ControllerContext = new ControllerContext { HttpContext = HttpCtx(ScopedPrincipal(tenantId, own.Id), null) },
        };

        var update = await controller.Update(shared.Id,
            new Application.Organization.GradeRequest("G-SHARED", "Renamed by a company admin", "Staff", 1, Currency: "SAR"),
            CancellationToken.None);
        update.Result.Should().BeOfType<NotFoundResult>();
        (await controller.Delete(shared.Id, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        var untouched = await db.Grades.IgnoreQueryFilters().AsNoTracking().SingleAsync(g => g.Id == shared.Id);
        untouched.Name.Should().Be("Group grade");
        untouched.IsDeleted.Should().BeFalse();

        var created = await controller.Create(
            new Application.Organization.GradeRequest("G-LOCAL", "Own company grade", "Staff", 1, Currency: "SAR"),
            CancellationToken.None);
        created.Result.Should().BeOfType<CreatedAtActionResult>();
        (await db.Grades.IgnoreQueryFilters().AsNoTracking().SingleAsync(g => g.TenantId == tenantId && g.Code == "G-LOCAL"))
            .CompanyId.Should().Be(own.Id, "a scoped admin's new grade belongs to their company, not to the group");
    }

    [Fact]
    public async Task GroupScopeAdministrator_CompanySelectionDoesNotRevokeSharedCatalogAuthority()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var selected = new Company { TenantId = tenantId, LegalNameEn = "SELECTED", RegistrationNumber = $"R-{Guid.NewGuid():N}", CountryCode = "SA", DefaultCurrency = "SAR", IsActive = true };
        db.Companies.Add(selected);
        await db.SaveChangesAsync();
        var controller = new GradesController(
            new Zayra.Api.Infrastructure.Organization.OrganizationSetupService(db, new NullAudit()), db)
        {
            ControllerContext = new ControllerContext { HttpContext = HttpCtx(GroupPrincipal(tenantId), selected.Id.ToString()) },
        };

        var result = await controller.Create(
            new Application.Organization.GradeRequest("G-GROUP", "Shared group grade", "Staff", 1, Currency: "SAR"),
            CancellationToken.None);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        (await db.Grades.IgnoreQueryFilters().AnyAsync(grade => grade.TenantId == tenantId && grade.Code == "G-GROUP")).Should().BeTrue();
    }

    [Fact]
    public async Task CompanyScopedAdministrator_CannotMutateTenantWideStatutoryRules()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var own = new Company { TenantId = tenantId, LegalNameEn = "OWN", RegistrationNumber = $"R-{Guid.NewGuid():N}", CountryCode = "SA", DefaultCurrency = "SAR", IsActive = true };
        db.Companies.Add(own);
        await db.SaveChangesAsync();
        var controller = new StatutoryRulesController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = HttpCtx(ScopedPrincipal(tenantId, own.Id), null) },
        };

        var result = await controller.Create(new CreateStatutoryRuleRequest(
            "SA", "KSA-mainland", "example.key", "1", "decimal", "delegated admin attempt",
            DateTime.UtcNow.Date.AddDays(1), null), CancellationToken.None);

        result.Result.Should().BeOfType<ForbidResult>();
        (await db.StatutoryRules.IgnoreQueryFilters().AnyAsync(rule => rule.TenantId == tenantId)).Should().BeFalse();
    }

    [Fact]
    public async Task CompanyStatusChange_FailsClosed_WithZeroMutation()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var only = new Company { TenantId = tenantId, LegalNameEn = "LAST-ACTIVE", RegistrationNumber = $"R-{Guid.NewGuid():N}", IsActive = true };
        db.Companies.Add(only);
        await db.SaveChangesAsync();

        var controller = MakeCompaniesController(db, tenantId);
        var conflict = await controller.SetStatus(only.Id, new CompanyStatusRequest(false), CancellationToken.None);
        conflict.Should().BeOfType<ConflictObjectResult>("a tenant must always retain one operational company");

        // Company suspend/reactivate is deliberately fail-closed pending atomic authorization
        // invalidation, even when another active company exists: 409, no mutation, no audit row.
        var second = new Company { TenantId = tenantId, LegalNameEn = "SECOND", RegistrationNumber = $"R-{Guid.NewGuid():N}", IsActive = true };
        db.Companies.Add(second);
        await db.SaveChangesAsync();
        var disabled = await controller.SetStatus(only.Id, new CompanyStatusRequest(false), CancellationToken.None);
        System.Text.Json.JsonSerializer.Serialize(disabled.Should().BeOfType<ConflictObjectResult>().Subject.Value)
            .Should().Contain("company_status_change_disabled");
        db.ChangeTracker.Clear();
        (await db.Companies.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == only.Id)).IsActive.Should().BeTrue();
        (await db.AdminAuditLogs.IgnoreQueryFilters().AnyAsync(a => a.TenantId == tenantId && a.Action == "CompanySuspended")).Should().BeFalse();
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

    private static CompaniesController MakeCompaniesController(ZayraDbContext db, Guid tenantId, Guid? scopedTo = null) => new(
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
                    new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(scopedTo.HasValue
                        ? (object)new { v = 2, m = "companies", c = new[] { scopedTo.Value } }
                        : new { v = 2, m = "group", c = Array.Empty<Guid>() })),
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
            new Claim(ClaimTypes.Role, "Admin"),
            new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
        }, "Test"));

    private static ClaimsPrincipal ScopedPrincipal(Guid tenantId, Guid companyId) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "Admin"),
            new Claim("permission", "payroll.rates.statutory_override"),
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
}
