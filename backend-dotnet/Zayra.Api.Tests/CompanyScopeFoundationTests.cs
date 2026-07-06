using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Organization;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Boot;
using Zayra.Api.Infrastructure.Governance;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Phase 1B foundation tests: default-company backfill, account types, operational vs
/// config company scoping (null-CompanyId poison-default prevention), company governance
/// tables (tax policy / compliance profile), and country-code validation — all against
/// real Postgres so the EF global filters and ExecuteUpdate paths are exercised for real.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class CompanyScopeFoundationTests
{
    private readonly PostgresFixture _fx;
    public CompanyScopeFoundationTests(PostgresFixture fx) => _fx = fx;

    // ── K1 + K2: default-company backfill, idempotent ──────────────────────────

    [Fact]
    public async Task Backfill_CreatesDefaultCompany_AssignsEmployeesAndOperationalRows_Idempotently()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);

        // Legacy-shaped data: no company, employees and operational rows unassigned.
        var e1 = MakeEmployee(tenantId, "BF1B-E1", null);
        var e2 = MakeEmployee(tenantId, "BF1B-E2", null);
        db.Employees.AddRange(e1, e2);
        await db.SaveChangesAsync();
        db.LeaveRequests.Add(new LeaveRequest { TenantId = tenantId, EmployeeId = e1.Id, StartDate = new DateOnly(2026, 1, 5), EndDate = new DateOnly(2026, 1, 7), Status = "Approved" });
        db.AttendanceRecords.Add(new AttendanceRecord { TenantId = tenantId, EmployeeId = e2.Id, WorkDate = new DateOnly(2026, 1, 5) });
        await db.SaveChangesAsync();

        var first = await CompanyScopeBackfill.RunAsync(db, NullLogger.Instance);
        db.ChangeTracker.Clear(); // ExecuteUpdate bypasses the tracker — drop stale seeded instances

        var defaultCompany = await db.Companies.IgnoreQueryFilters()
            .SingleAsync(c => c.TenantId == tenantId);
        first.CompaniesCreated.Should().BeGreaterThan(0);

        (await db.Employees.IgnoreQueryFilters().Where(e => e.TenantId == tenantId).ToListAsync())
            .Should().OnlyContain(e => e.CompanyId == defaultCompany.Id,
                "every employee must resolve to the tenant's default legal entity (K6)");
        (await db.LeaveRequests.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ToListAsync())
            .Should().OnlyContain(x => x.CompanyId == defaultCompany.Id);
        (await db.AttendanceRecords.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ToListAsync())
            .Should().OnlyContain(x => x.CompanyId == defaultCompany.Id);

        // Idempotence: a second run creates nothing and touches nothing for this tenant.
        var second = await CompanyScopeBackfill.RunAsync(db, NullLogger.Instance);
        (await db.Companies.IgnoreQueryFilters().CountAsync(c => c.TenantId == tenantId)).Should().Be(1);

        // SingleCompany tenant stays SingleCompany (K3).
        (await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenantId)).AccountType
            .Should().Be(TenantAccountTypes.SingleCompany);
    }

    // ── K2 + K4 + A: multi-company accuracy and Group promotion ────────────────

    [Fact]
    public async Task Backfill_MultiCompanyTenant_RowsFollowOwningEmployee_AndTenantPromotesToGroup()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);

        var alpha = MakeCompany(tenantId, "1B Alpha LLC", createdYear: 2023);
        var beta = MakeCompany(tenantId, "1B Beta LLC", createdYear: 2024);
        db.Companies.AddRange(alpha, beta);
        var eAlpha = MakeEmployee(tenantId, "1B-EA", alpha.Id);
        var eBeta = MakeEmployee(tenantId, "1B-EB", beta.Id);
        db.Employees.AddRange(eAlpha, eBeta);
        await db.SaveChangesAsync();

        // Unassigned operational rows belonging to employees of DIFFERENT companies.
        db.OvertimeRequests.Add(new OvertimeRequest { TenantId = tenantId, EmployeeId = eAlpha.Id, WorkDate = new DateOnly(2026, 2, 1) });
        db.OvertimeRequests.Add(new OvertimeRequest { TenantId = tenantId, EmployeeId = eBeta.Id, WorkDate = new DateOnly(2026, 2, 1) });
        await db.SaveChangesAsync();

        await CompanyScopeBackfill.RunAsync(db, NullLogger.Instance);
        db.ChangeTracker.Clear(); // ExecuteUpdate bypasses the tracker — drop stale seeded instances

        var rows = await db.OvertimeRequests.IgnoreQueryFilters()
            .Where(x => x.TenantId == tenantId).ToListAsync();
        rows.Single(x => x.EmployeeId == eAlpha.Id).CompanyId.Should().Be(alpha.Id,
            "rows must follow their OWNING employee's company, not the tenant default");
        rows.Single(x => x.EmployeeId == eBeta.Id).CompanyId.Should().Be(beta.Id);

        (await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenantId)).AccountType
            .Should().Be(TenantAccountTypes.Group,
                "a tenant already operating multiple active companies clearly behaves as a group (K4)");
    }

    // ── K7: operational entities honour company scope ──────────────────────────

    [Fact]
    public async Task OperationalEntities_ScopedUser_SeesOnlyGrantedCompanyRows()
    {
        await using var seedDb = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(seedDb);
        var a = MakeCompany(tenantId, "1B Scope A");
        var b = MakeCompany(tenantId, "1B Scope B");
        seedDb.Companies.AddRange(a, b);
        var ea = MakeEmployee(tenantId, "1BS-A", a.Id);
        var eb = MakeEmployee(tenantId, "1BS-B", b.Id);
        seedDb.Employees.AddRange(ea, eb);
        await seedDb.SaveChangesAsync();
        seedDb.LeaveRequests.AddRange(
            new LeaveRequest { TenantId = tenantId, CompanyId = a.Id, EmployeeId = ea.Id, StartDate = new DateOnly(2026, 3, 1), EndDate = new DateOnly(2026, 3, 2), Status = "Approved" },
            new LeaveRequest { TenantId = tenantId, CompanyId = b.Id, EmployeeId = eb.Id, StartDate = new DateOnly(2026, 3, 1), EndDate = new DateOnly(2026, 3, 2), Status = "Approved" });
        seedDb.EmployeeLoans.AddRange(
            new EmployeeLoan { TenantId = tenantId, CompanyId = a.Id, EmployeeIntId = ea.Id, EmployeeId = Guid.NewGuid(), LoanTypeId = Guid.NewGuid(), LoanNumber = $"LN-{Guid.NewGuid():N}" },
            new EmployeeLoan { TenantId = tenantId, CompanyId = b.Id, EmployeeIntId = eb.Id, EmployeeId = Guid.NewGuid(), LoanTypeId = Guid.NewGuid(), LoanNumber = $"LN-{Guid.NewGuid():N}" });
        await seedDb.SaveChangesAsync();

        var accessor = new FixedAccessor { HttpContext = ScopedContext(tenantId, a.Id) };
        await using var db = _fx.CreateDbWithAccessor(accessor);

        (await db.LeaveRequests.Where(x => x.TenantId == tenantId).ToListAsync())
            .Should().OnlyContain(x => x.CompanyId == a.Id, "leave is operational company data (K7)");
        (await db.EmployeeLoans.Where(x => x.TenantId == tenantId).ToListAsync())
            .Should().OnlyContain(x => x.CompanyId == a.Id, "loans are operational company data (K7)");
    }

    // ── K8 / J: null CompanyId is NOT universal visibility on operational tables ─

    [Fact]
    public async Task NullCompanyOperationalRows_HiddenFromScopedUsers_VisibleToGroupScope()
    {
        await using var seedDb = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(seedDb);
        var a = MakeCompany(tenantId, "1B Null A");
        seedDb.Companies.Add(a);
        var e = MakeEmployee(tenantId, "1BN-E", a.Id);
        seedDb.Employees.Add(e);
        await seedDb.SaveChangesAsync();
        // A poison-default row: operational record whose insert path forgot CompanyId.
        seedDb.LeaveRequests.Add(new LeaveRequest { TenantId = tenantId, CompanyId = null, EmployeeId = e.Id, StartDate = new DateOnly(2026, 4, 1), EndDate = new DateOnly(2026, 4, 2), Status = "Draft" });
        await seedDb.SaveChangesAsync();

        var accessor = new FixedAccessor { HttpContext = ScopedContext(tenantId, a.Id) };
        await using var db = _fx.CreateDbWithAccessor(accessor);
        (await db.LeaveRequests.Where(x => x.TenantId == tenantId).ToListAsync())
            .Should().BeEmpty("a scoped user must NEVER receive unassigned operational rows (J/K8)");

        accessor.HttpContext = GroupScopeContext(tenantId);
        (await db.LeaveRequests.Where(x => x.TenantId == tenantId).ToListAsync())
            .Should().HaveCount(1, "group scope still sees unassigned rows so they stay repairable");
    }

    [Fact]
    public async Task ConfigEntities_NullCompany_RemainsTenantWideVisible()
    {
        await using var seedDb = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(seedDb);
        var a = MakeCompany(tenantId, "1B Cfg A");
        seedDb.Companies.Add(a);
        // Tenant-wide policy template (CompanyId null) + company-specific one.
        seedDb.LeavePolicies.AddRange(
            new LeavePolicy { TenantId = tenantId, CompanyId = null, LeaveTypeId = Guid.NewGuid(), Name = "Tenant-wide annual" },
            new LeavePolicy { TenantId = tenantId, CompanyId = Guid.NewGuid(), LeaveTypeId = Guid.NewGuid(), Name = "Other company policy" });
        await seedDb.SaveChangesAsync();

        var accessor = new FixedAccessor { HttpContext = ScopedContext(tenantId, a.Id) };
        await using var db = _fx.CreateDbWithAccessor(accessor);

        var visible = await db.LeavePolicies.Where(x => x.TenantId == tenantId).ToListAsync();
        visible.Should().ContainSingle(p => p.CompanyId == null,
            "config templates with null CompanyId are tenant-wide and stay visible to scoped users");
        visible.Should().NotContain(p => p.CompanyId != null,
            "another company's specific config must not leak");
    }

    // ── K3 + K4 + A: account-type gate on company creation ─────────────────────

    [Fact]
    public async Task SingleCompanyTenant_SecondCompanyBlocked_GroupTenantAllowed()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var controller = MakeCompaniesController(db, tenantId);

        var first = await controller.Create(MakeCompanyRequest("1B Gate First"), CancellationToken.None);
        first.Result.Should().BeOfType<CreatedAtActionResult>();

        var second = await controller.Create(MakeCompanyRequest("1B Gate Second"), CancellationToken.None);
        var conflict = second.Result.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.Value!.ToString().Should().Contain("account_type_single_company");

        // Platform admin flips the account type → Group; second company now allowed.
        await db.Tenants.Where(t => t.Id == tenantId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.AccountType, TenantAccountTypes.Group));
        var third = await controller.Create(MakeCompanyRequest("1B Gate Third"), CancellationToken.None);
        third.Result.Should().BeOfType<CreatedAtActionResult>();

        (await db.Companies.IgnoreQueryFilters().CountAsync(c => c.TenantId == tenantId)).Should().Be(2);
    }

    // ── K11 (validation surface): company country codes are no longer free text ─

    [Fact]
    public async Task CompanyCreate_RejectsFreeTextCountryCode_AcceptsIso2AndIso3()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        await db.Tenants.Where(t => t.Id == tenantId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.AccountType, TenantAccountTypes.Group));
        var controller = MakeCompaniesController(db, tenantId);

        var bad = await controller.Create(MakeCompanyRequest("1B CC Bad") with { CountryCode = "KSA" }, CancellationToken.None);
        bad.Result.Should().BeOfType<BadRequestObjectResult>("'KSA' is not an ISO 3166-1 code");

        (await controller.Create(MakeCompanyRequest("1B CC Iso2") with { CountryCode = "SA" }, CancellationToken.None))
            .Result.Should().BeOfType<CreatedAtActionResult>();
        (await controller.Create(MakeCompanyRequest("1B CC Iso3") with { CountryCode = "ARE" }, CancellationToken.None))
            .Result.Should().BeOfType<CreatedAtActionResult>();
    }

    // ── K5: branches are structural children of a company ──────────────────────

    [Fact]
    public void Branch_BelongsToCompany_NotDirectlyToGroup()
    {
        var prop = typeof(Branch).GetProperty("CompanyId")!;
        prop.PropertyType.Should().Be(typeof(Guid),
            "a branch cannot exist outside a legal entity — CompanyId is non-nullable by design (K5)");
    }

    // ── K9: CompanyTaxPolicy — company-specific and effective-dated ─────────────

    [Fact]
    public async Task CompanyTaxPolicy_CompanyOverrideBeatsTenantDefault_AndEffectiveDatingWins()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var companyA = Guid.NewGuid();
        var companyB = Guid.NewGuid();

        db.CompanyTaxPolicies.AddRange(
            // Tenant-wide default, effective all of 2025+.
            new CompanyTaxPolicy { TenantId = tenantId, CompanyId = null, CountryCode = "SA", EffectiveFrom = new DateOnly(2025, 1, 1), IncomeTaxRatePercent = 0m },
            // Company A override, superseded then current (newest EffectiveFrom wins).
            new CompanyTaxPolicy { TenantId = tenantId, CompanyId = companyA, CountryCode = "SA", EffectiveFrom = new DateOnly(2025, 1, 1), IncomeTaxRatePercent = 2m },
            new CompanyTaxPolicy { TenantId = tenantId, CompanyId = companyA, CountryCode = "SA", EffectiveFrom = new DateOnly(2026, 1, 1), IncomeTaxRatePercent = 2.5m },
            // Draft rows never resolve.
            new CompanyTaxPolicy { TenantId = tenantId, CompanyId = companyA, CountryCode = "SA", EffectiveFrom = new DateOnly(2026, 6, 1), IncomeTaxRatePercent = 9m, Status = CompanyPolicyStatuses.Draft });
        await db.SaveChangesAsync();

        var resolver = new CompanyTaxPolicyResolver(db);

        (await resolver.ResolveAsync(tenantId, companyA, new DateOnly(2026, 7, 1)))!
            .IncomeTaxRatePercent.Should().Be(2.5m, "company override + newest effective row wins; Draft ignored");
        (await resolver.ResolveAsync(tenantId, companyA, new DateOnly(2025, 6, 1)))!
            .IncomeTaxRatePercent.Should().Be(2m, "effective dating selects the row covering the date");
        (await resolver.ResolveAsync(tenantId, companyB, new DateOnly(2026, 7, 1)))!
            .IncomeTaxRatePercent.Should().Be(0m, "companies without an override fall back to the tenant default (K9)");
        (await resolver.ResolveAsync(tenantId, companyB, new DateOnly(2024, 6, 1)))
            .Should().BeNull("nothing is effective before the first policy");
    }

    // ── K10: CompanyComplianceProfile — company-specific ────────────────────────

    [Fact]
    public async Task CompanyComplianceProfile_IsCompanySpecific_AndScopedUsersSeeOnlyTheirs()
    {
        await using var seedDb = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(seedDb);
        var a = MakeCompany(tenantId, "1B CP A");
        var b = MakeCompany(tenantId, "1B CP B");
        seedDb.Companies.AddRange(a, b);
        seedDb.CompanyComplianceProfiles.AddRange(
            new CompanyComplianceProfile { TenantId = tenantId, CompanyId = a.Id, CountryCode = "SA", Jurisdiction = "KSA-mainland", CompliancePack = "SAU", EffectiveFrom = new DateOnly(2026, 1, 1), RequiredFieldsJson = """[{"field":"IqamaNumber","failClosed":true}]""" },
            new CompanyComplianceProfile { TenantId = tenantId, CompanyId = b.Id, CountryCode = "AE", Jurisdiction = "UAE-DIFC", CompliancePack = "ARE:UAE-DIFC", EffectiveFrom = new DateOnly(2026, 1, 1) });
        await seedDb.SaveChangesAsync();

        var accessor = new FixedAccessor { HttpContext = ScopedContext(tenantId, a.Id) };
        await using var db = _fx.CreateDbWithAccessor(accessor);

        var visible = await db.CompanyComplianceProfiles.Where(x => x.TenantId == tenantId).ToListAsync();
        visible.Should().ContainSingle(p => p.CompanyId == a.Id, "compliance profiles are the per-entity enforcement boundary (K10)");
        visible.Should().NotContain(p => p.CompanyId == b.Id);
    }

    // ── Harness ────────────────────────────────────────────────────────────────

    private static CompaniesController MakeCompaniesController(ZayraDbContext db, Guid tenantId)
    {
        var controller = new CompaniesController(new OrganizationSetupService(db, new NullFoundationAuditService()), db)
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
        return controller;
    }

    private static CompanyRequest MakeCompanyRequest(string name) => new(
        LegalNameEn: name, LegalNameAr: null, TradeName: null, CountryCode: "SA",
        Jurisdiction: "KSA-mainland", RegistrationNumber: $"REG-{Guid.NewGuid():N}",
        TaxNumber: null, WpsEmployerId: null, GosiEmployerId: null, QiwaEstablishmentId: null,
        DefaultCurrency: "SAR");

    private static Company MakeCompany(Guid tenantId, string name, int createdYear = 2024) => new()
    {
        TenantId = tenantId,
        LegalNameEn = name,
        RegistrationNumber = $"REG-{Guid.NewGuid():N}",
        IsActive = true,
        CreatedAtUtc = new DateTime(createdYear, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static Employee MakeEmployee(Guid tenantId, string code, Guid? companyId) => new()
    {
        TenantId = tenantId,
        EmployeeCode = code,
        FullName = $"Employee {code}",
        CompanyId = companyId,
        Status = "Active",
        JoiningDate = DateTime.UtcNow.AddYears(-1),
    };

    private static HttpContext ScopedContext(Guid tenantId, Guid companyId)
    {
        var accessJson = JsonSerializer.Serialize(new { c = companyId, r = "Viewer" });
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("tenant_id", tenantId.ToString()),
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim("entity_access", accessJson),
            }, "Test"))
        };
    }

    private static HttpContext GroupScopeContext(Guid tenantId) => new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        }, "Test"))
    };

    private sealed class FixedAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class NullFoundationAuditService : IAuditService
    {
        public Task WriteAsync(string action, string entityName, string? entityId, RequestContext context, string? metadata, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
