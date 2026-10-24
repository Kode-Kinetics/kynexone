using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Addendum B (docs/design/company-setup-wizard.md, B1): departments, designations and grades belong to one company.
/// The backfill runs on its own PostgreSQL container (it touches every tenant, so it must not run on the shared
/// fixture); the service rules run on the shared fixture, each scenario in its own tenant.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OrganizationPerCompanyBackfillPostgresTests
{
    internal sealed record Seeded(
        Guid SingleTenant, Guid SingleCompany, Guid SingleGrade, Guid SingleDepartment,
        Guid Tenant, Guid A, Guid B,
        Guid SharedGrade, Guid UnusedGrade, Guid ParentDept, Guid ChildDept, Guid BranchDept, Guid Designation,
        Guid CellTenantWide, Guid CellCompanyB, int EmpA1, int EmpB2);

    internal static async Task<Seeded> SeedAsync(ZayraDbContext db)
    {
        // Tenant 1: one company — unused rows go to it.
        var t1 = Guid.NewGuid();
        var c1 = new Company { TenantId = t1, LegalNameEn = "Solo Co", RegistrationNumber = "S-1", DefaultCurrency = "SAR", IsActive = true };
        var g1 = new Grade { TenantId = t1, Code = "G1", Name = "Grade 1", Currency = "SAR" };
        var d1 = new Department { TenantId = t1, Code = "D1", NameEn = "Dept 1" };

        // Tenant 2: A (older) and B.
        var t2 = Guid.NewGuid();
        var a = new Company { TenantId = t2, LegalNameEn = "Alpha", RegistrationNumber = "A-1", DefaultCurrency = "SAR", IsActive = true, CreatedAtUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        var b = new Company { TenantId = t2, LegalNameEn = "Beta", RegistrationNumber = "B-1", DefaultCurrency = "AED", IsActive = true, CreatedAtUtc = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc) };
        var branchA = new Branch { TenantId = t2, CompanyId = a.Id, Code = "HQ", NameEn = "Alpha HQ" };
        var shared = new Grade { TenantId = t2, Code = "GS", Name = "Shared", Currency = "SAR", MinSalary = 1000, MaxSalary = 5000 };
        var unused = new Grade { TenantId = t2, Code = "GU", Name = "Unused", Currency = "SAR" };
        var parent = new Department { TenantId = t2, Code = "DP", NameEn = "Parent" };
        var child = new Department { TenantId = t2, Code = "DC", NameEn = "Child", ParentDepartmentId = parent.Id };
        var byBranch = new Department { TenantId = t2, Code = "DB", NameEn = "By branch", BranchId = branchA.Id };
        var desig = new Designation { TenantId = t2, Code = "X", TitleEn = "Engineer", DepartmentId = child.Id, GradeId = shared.Id };
        db.AddRange(c1, g1, d1, a, b, branchA, shared, unused, parent, child, byBranch, desig);
        await db.SaveChangesAsync();

        Employee E(Company co, string code, Guid? grade = null, Guid? dept = null, Guid? designation = null) => new()
        {
            TenantId = t2, CompanyId = co.Id, EmployeeCode = code, FullName = code, Status = "Active",
            JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), GradeId = grade, DepartmentId = dept, DesignationId = designation,
        };
        // Grade GS: A has 2 employees, B has 1 → A keeps it, B gets a copy.
        // Department DC: A has 1 employee, B has 2 → B keeps it, A gets a copy. DP is used by both through DC (tie → A, the oldest).
        var a1 = E(a, "A1", shared.Id, child.Id);
        var a2 = E(a, "A2", shared.Id);
        var b1 = E(b, "B1", null, child.Id);
        var b2 = E(b, "B2", shared.Id, child.Id, desig.Id);
        db.AddRange(a1, a2, b1, b2);
        db.GradePayScaleComponents.Add(new GradePayScaleComponent { TenantId = t2, GradeId = shared.Id, ComponentCode = "BASIC", ComponentName = "Basic", Amount = 1000 });
        var tenantWide = new GradeEntitlement
        {
            TenantId = t2, GradeId = shared.Id, PayComponentCode = "HOUSING", EntitlementClass = PayEntitlementClasses.Contractual,
            Eligible = true, ValueType = GradeEntitlementValueTypes.Amount, Amount = 500, EffectiveFrom = new DateOnly(2026, 1, 1),
        };
        var loanLimitB = new GradeEntitlement
        {
            TenantId = t2, CompanyId = b.Id, GradeId = shared.Id, PayComponentCode = "LOAN_PERSONAL", EntitlementClass = PayEntitlementClasses.Facility,
            Eligible = true, ValueType = GradeEntitlementValueTypes.Amount, Amount = 5000, MaxOutstandingAmount = 8000, EffectiveFrom = new DateOnly(2026, 1, 1),
        };
        db.GradeEntitlements.AddRange(tenantWide, loanLimitB);
        await db.SaveChangesAsync();
        return new Seeded(t1, c1.Id, g1.Id, d1.Id, t2, a.Id, b.Id, shared.Id, unused.Id, parent.Id, child.Id, byBranch.Id, desig.Id,
            tenantWide.Id, loanLimitB.Id, a1.Id, b2.Id);
    }

    internal static async Task RunSqlAsync(ZayraDbContext db, string sql)
    {
        // A plain command, not ExecuteSqlRaw: the SQL holds braces ('{}' json paths) that would be read as format placeholders.
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Backfill_AssignsCopiesAndRepointsPerCompany_AndARerunChangesNothing()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        var options = new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options;
        await using (var setup = new ZayraDbContext(options)) await setup.Database.EnsureCreatedAsync();

        Seeded s;
        await using (var db = new ZayraDbContext(options)) s = await SeedAsync(db);
        await using (var db = new ZayraDbContext(options)) await RunSqlAsync(db, Zayra.Api.Migrations.BackfillOrganisationPerCompany.BackfillSql);

        await using (var db = new ZayraDbContext(options))
        {
            // Single-company tenant: unused rows go to the only company.
            (await db.Grades.SingleAsync(x => x.Id == s.SingleGrade)).CompanyId.Should().Be(s.SingleCompany);
            (await db.Departments.SingleAsync(x => x.Id == s.SingleDepartment)).CompanyId.Should().Be(s.SingleCompany);

            // Shared grade: kept by A (more employees), copied for B with B's currency, B's references re-pointed.
            var grades = await db.Grades.Where(x => x.TenantId == s.Tenant && x.Code == "GS").ToListAsync();
            grades.Should().HaveCount(2);
            var gradeA = grades.Single(x => x.CompanyId == s.A);
            var gradeB = grades.Single(x => x.CompanyId == s.B);
            gradeA.Id.Should().Be(s.SharedGrade);
            gradeA.Currency.Should().Be("SAR", "the keeper's currency is kept");
            gradeB.Currency.Should().Be("AED", "a copied grade takes its company's default currency");
            (gradeB.MinSalary, gradeB.MaxSalary).Should().Be((1000m, 5000m));
            (await db.Employees.SingleAsync(x => x.Id == s.EmpB2)).GradeId.Should().Be(gradeB.Id);
            (await db.Employees.Where(x => x.TenantId == s.Tenant && x.CompanyId == s.A && x.GradeId != null).Select(x => x.GradeId).Distinct().ToListAsync())
                .Should().Equal(gradeA.Id);
            (await db.Designations.SingleAsync(x => x.Id == s.Designation)).GradeId.Should().Be(gradeB.Id, "B's designation now names B's grade");
            (await db.GradePayScaleComponents.CountAsync(x => x.GradeId == gradeB.Id && x.ComponentCode == "BASIC")).Should().Be(1, "child config is copied");
            (await db.GradeEntitlements.SingleAsync(x => x.Id == s.CellCompanyB)).GradeId.Should().Be(gradeB.Id, "B's loan limit follows B's grade");
            (await db.GradeEntitlements.SingleAsync(x => x.Id == s.CellTenantWide)).GradeId.Should().Be(gradeA.Id);
            (await db.GradeEntitlements.CountAsync(x => x.GradeId == gradeB.Id && x.CompanyId == null && x.PayComponentCode == "HOUSING"))
                .Should().Be(1, "the tenant-wide cell is copied onto the copy, so B's employees keep the same grid");

            // Unused grade in a multi-company tenant stays unassigned.
            (await db.Grades.SingleAsync(x => x.Id == s.UnusedGrade)).CompanyId.Should().BeNull();

            // Department DC: kept by B (2 employees), copied for A; parents re-pointed within each company.
            var childB = await db.Departments.SingleAsync(x => x.Id == s.ChildDept);
            childB.CompanyId.Should().Be(s.B);
            var childA = await db.Departments.SingleAsync(x => x.TenantId == s.Tenant && x.Code == "DC" && x.CompanyId == s.A);
            (await db.Employees.SingleAsync(x => x.Id == s.EmpA1)).DepartmentId.Should().Be(childA.Id);
            var parentA = await db.Departments.SingleAsync(x => x.Id == s.ParentDept);
            parentA.CompanyId.Should().Be(s.A, "a tie on employees goes to the oldest company");
            var parentB = await db.Departments.SingleAsync(x => x.TenantId == s.Tenant && x.Code == "DP" && x.CompanyId == s.B);
            childA.ParentDepartmentId.Should().Be(parentA.Id);
            childB.ParentDepartmentId.Should().Be(parentB.Id, "the parent of a company's department is that company's copy");
            (await db.Designations.SingleAsync(x => x.Id == s.Designation)).CompanyId.Should().Be(s.B);
            (await db.Departments.SingleAsync(x => x.Id == s.BranchDept)).CompanyId.Should().Be(s.A, "a department is used by its branch's company");

            // One chained audit row per assigned or copied row, and the tenant's chain still verifies.
            var audits = await db.AuditLogs.Where(x => x.TenantId == s.Tenant).OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).ToListAsync();
            audits.Should().OnlyContain(x => x.Action.StartsWith("organization.company_backfill_"));
            audits.Count(x => x.Action == "organization.company_backfill_copied").Should().Be(3);
            AuditService.VerifyChain(audits).IsValid.Should().BeTrue();
        }

        // Idempotent: a second run changes nothing.
        string Snapshot(ZayraDbContext db) => JsonSerializer.Serialize(new
        {
            g = db.Grades.OrderBy(x => x.Id).Select(x => new { x.Id, x.CompanyId, x.Currency }).ToList(),
            d = db.Departments.OrderBy(x => x.Id).Select(x => new { x.Id, x.CompanyId, x.ParentDepartmentId, x.BranchId }).ToList(),
            x = db.Designations.OrderBy(x => x.Id).Select(x => new { x.Id, x.CompanyId, x.DepartmentId, x.GradeId }).ToList(),
            e = db.Employees.OrderBy(x => x.Id).Select(x => new { x.Id, x.GradeId, x.DepartmentId, x.DesignationId }).ToList(),
            ge = db.GradeEntitlements.OrderBy(x => x.Id).Select(x => new { x.Id, x.GradeId }).ToList(),
            p = db.GradePayScaleComponents.Count(),
            a = db.AuditLogs.Count(),
        });
        string before;
        await using (var db = new ZayraDbContext(options)) before = Snapshot(db);
        await using (var db = new ZayraDbContext(options)) await RunSqlAsync(db, Zayra.Api.Migrations.BackfillOrganisationPerCompany.BackfillSql);
        await using (var db = new ZayraDbContext(options)) Snapshot(db).Should().Be(before);
    }
}

[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class OrganizationPerCompanyPostgresTests(PostgresFixture fixture)
{
    private static RequestContext Ctx(Guid tenantId) => new("127.0.0.1", "test", Guid.NewGuid(), tenantId);

    private async Task<(Guid Tenant, Company A, Company B)> TwoCompaniesAsync()
    {
        await using var db = fixture.CreateDb();
        var tenant = Guid.NewGuid();
        var a = new Company { TenantId = tenant, LegalNameEn = "Alpha", RegistrationNumber = $"A-{tenant:N}", DefaultCurrency = "SAR", IsActive = true };
        var b = new Company { TenantId = tenant, LegalNameEn = "Beta", RegistrationNumber = $"B-{tenant:N}", DefaultCurrency = "AED", IsActive = true };
        db.AddRange(a, b);
        await db.SaveChangesAsync();
        return (tenant, a, b);
    }

    private static OrganizationSetupService Service(ZayraDbContext db) => new(db, new AuditService(db));

    [Fact]
    public async Task SameCode_IsAllowedInTwoCompanies_ButNotTwiceInOne()
    {
        var (tenant, a, b) = await TwoCompaniesAsync();
        await using var db = fixture.CreateDb();
        var svc = Service(db);

        await svc.CreateDepartmentAsync(tenant, new DepartmentRequest(null, null, null, "OPS", "Operations", null, null, true, a.Id), Ctx(tenant), default);
        var other = await svc.CreateDepartmentAsync(tenant, new DepartmentRequest(null, null, null, "OPS", "Operations", null, null, true, b.Id), Ctx(tenant), default);
        other.CompanyId.Should().Be(b.Id);
        var dup = () => svc.CreateDepartmentAsync(tenant, new DepartmentRequest(null, null, null, "ops", "Ops again", null, null, true, a.Id), Ctx(tenant), default);
        (await dup.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*already exists in this company*");

        var gA = await svc.CreateGradeAsync(tenant, new GradeRequest("G1", "Grade 1", null, 1, Currency: null, CompanyId: a.Id), Ctx(tenant), default);
        var gB = await svc.CreateGradeAsync(tenant, new GradeRequest("G1", "Grade 1", null, 1, Currency: null, CompanyId: b.Id), Ctx(tenant), default);
        (gA.Currency, gB.Currency).Should().Be(("SAR", "AED"), "a grade defaults to its company's currency");
        var dupGrade = () => svc.CreateGradeAsync(tenant, new GradeRequest("G1", "Again", null, 1, CompanyId: b.Id), Ctx(tenant), default);
        await dupGrade.Should().ThrowAsync<InvalidOperationException>();

        await svc.CreateDesignationAsync(tenant, new DesignationRequest(null, "ENG", "Engineer", null, null, null, null, null, CompanyId: a.Id), Ctx(tenant), default);
        await svc.CreateDesignationAsync(tenant, new DesignationRequest(null, "ENG", "Engineer", null, null, null, null, null, CompanyId: b.Id), Ctx(tenant), default);
        var dupDesig = () => svc.CreateDesignationAsync(tenant, new DesignationRequest(null, "ENG", "Other", null, null, null, null, null, CompanyId: a.Id), Ctx(tenant), default);
        await dupDesig.Should().ThrowAsync<InvalidOperationException>();

        // The database refuses it too (NULLS NOT DISTINCT unique on tenant, company, code).
        db.Departments.Add(new Department { TenantId = tenant, CompanyId = a.Id, Code = "OPS", NameEn = "Raw duplicate" });
        await FluentActions.Awaiting(() => db.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Create_RequiresACompany_WhenTheTenantHasSeveral_AndLinksMustStayInsideIt()
    {
        var (tenant, a, b) = await TwoCompaniesAsync();
        await using var db = fixture.CreateDb();
        var svc = Service(db);

        var noCompany = () => svc.CreateDepartmentAsync(tenant, new DepartmentRequest(null, null, null, "HR", "HR", null, null), Ctx(tenant), default);
        (await noCompany.Should().ThrowAsync<OrganizationRuleException>()).Which.Code.Should().Be(OrganizationRuleException.CompanyRequired);

        var ccB = await svc.CreateCostCenterAsync(tenant, new CostCenterRequest(b.Id, "CC_B", "Beta costs"), Ctx(tenant), default);
        var mismatch = () => svc.CreateDepartmentAsync(tenant, new DepartmentRequest(null, null, ccB.Id, "HR", "HR", null, null, true, a.Id), Ctx(tenant), default);
        (await mismatch.Should().ThrowAsync<OrganizationRuleException>()).Which.Code.Should().Be(OrganizationRuleException.CompanyMismatch);

        var gradeB = await svc.CreateGradeAsync(tenant, new GradeRequest("GB", "Beta grade", null, 1, CompanyId: b.Id), Ctx(tenant), default);
        var crossGrade = () => svc.CreateDesignationAsync(tenant, new DesignationRequest(null, "MGR", "Manager", null, null, gradeB.Id, null, null, CompanyId: a.Id), Ctx(tenant), default);
        (await crossGrade.Should().ThrowAsync<OrganizationRuleException>()).Which.Code.Should().Be(OrganizationRuleException.CompanyMismatch);

        var deptA = await svc.CreateDepartmentAsync(tenant, new DepartmentRequest(null, null, null, "HR", "HR", null, null, true, a.Id), Ctx(tenant), default);
        var move = () => svc.UpdateDepartmentAsync(tenant, deptA.Id, new DepartmentRequest(null, null, null, "HR", "HR", null, null, true, b.Id), Ctx(tenant), default);
        (await move.Should().ThrowAsync<OrganizationRuleException>()).Which.Code.Should().Be(OrganizationRuleException.CompanyLocked);
    }

    [Fact]
    public async Task UnassignedLegacyRow_IsAssignedFromSetup_AndOnlyCompanyIsTheDefault()
    {
        await using var db = fixture.CreateDb();
        var tenant = Guid.NewGuid();
        var only = new Company { TenantId = tenant, LegalNameEn = "Only", RegistrationNumber = $"O-{tenant:N}", DefaultCurrency = "OMR", IsActive = true };
        var legacy = new Grade { TenantId = tenant, Code = "OLD", Name = "Legacy", Currency = "SAR" };
        db.AddRange(only, legacy);
        await db.SaveChangesAsync();
        var svc = Service(db);

        var created = await svc.CreateGradeAsync(tenant, new GradeRequest("NEW", "New", null, 1, Currency: null), Ctx(tenant), default);
        created.CompanyId.Should().Be(only.Id, "the tenant's only company is the default");
        created.Currency.Should().Be("OMR");

        var assigned = await svc.UpdateGradeAsync(tenant, legacy.Id, new GradeRequest("OLD", "Legacy", null, 1, Currency: null, CompanyId: only.Id), Ctx(tenant), default);
        assigned!.CompanyId.Should().Be(only.Id);
        assigned.Currency.Should().Be("SAR", "editing never rewrites a currency the request left blank");
    }

    [Fact]
    public async Task CompanyScopedUser_CannotReadASisterCompanysDepartments()
    {
        var (tenant, a, b) = await TwoCompaniesAsync();
        Guid deptA, deptB;
        await using (var db = fixture.CreateDb())
        {
            var svc = Service(db);
            deptA = (await svc.CreateDepartmentAsync(tenant, new DepartmentRequest(null, null, null, "OPS", "Ops A", null, null, true, a.Id), Ctx(tenant), default)).Id;
            deptB = (await svc.CreateDepartmentAsync(tenant, new DepartmentRequest(null, null, null, "OPS", "Ops B", null, null, true, b.Id), Ctx(tenant), default)).Id;
        }

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("tenant_id", tenant.ToString()),
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Role, "HR Manager"),
                new Claim("entity_access", JsonSerializer.Serialize(new { c = a.Id, r = "HR Manager" })),
            }, "test")),
        };
        await using var scoped = fixture.CreateDbWithAccessor(new HttpContextAccessor { HttpContext = http });
        var scopedSvc = Service(scoped);
        var page = await scopedSvc.GetDepartmentsAsync(tenant, null, 1, 100, default);
        page.Items.Select(x => x.Id).Should().Equal(deptA);
        (await scopedSvc.GetDepartmentAsync(tenant, deptB, default)).Should().BeNull();
    }

    [Fact]
    public async Task EmployeeImport_ResolvesDepartmentDesignationAndGradeByCode_WithinTheEmployeesCompany()
    {
        var (tenant, a, b) = await TwoCompaniesAsync();
        await using var db = fixture.CreateDb();
        var svc = Service(db);
        var gA = await svc.CreateGradeAsync(tenant, new GradeRequest("G1", "Grade 1", null, 1, CompanyId: a.Id), Ctx(tenant), default);
        var gB = await svc.CreateGradeAsync(tenant, new GradeRequest("G1", "Grade 1", null, 1, CompanyId: b.Id), Ctx(tenant), default);
        var dA = await svc.CreateDepartmentAsync(tenant, new DepartmentRequest(null, null, null, "OPS", "Operations", null, null, true, a.Id), Ctx(tenant), default);
        var dB = await svc.CreateDepartmentAsync(tenant, new DepartmentRequest(null, null, null, "OPS", "Operations", null, null, true, b.Id), Ctx(tenant), default);
        var xA = await svc.CreateDesignationAsync(tenant, new DesignationRequest(dA.Id, "ENG", "Engineer", null, null, gA.Id, null, null, CompanyId: a.Id), Ctx(tenant), default);
        var xB = await svc.CreateDesignationAsync(tenant, new DesignationRequest(dB.Id, "ENG", "Engineer", null, null, gB.Id, null, null, CompanyId: b.Id), Ctx(tenant), default);

        var lookups = await EmployeeImportRowResolver.LoadImportLookupsAsync(db, tenant, default);
        Dictionary<string, string> Row(string company) => new()
        {
            ["CompanyLegalName"] = company, ["DepartmentCode"] = "OPS", ["Designation"] = "Engineer", ["Grade"] = "G1",
        };

        var rowB = EmployeeImportRowResolver.ResolveRow(Row("Beta"), lookups, new HashSet<string>());
        (rowB.CompanyId, rowB.DepartmentId, rowB.DesignationId).Should().Be((b.Id, dB.Id, xB.Id));
        rowB.Gaps.Should().NotContain(g => g.Type.StartsWith("org:department") || g.Type.StartsWith("org:designation") || g.Type.StartsWith("org:grade"));

        var rowA = EmployeeImportRowResolver.ResolveRow(Row("Alpha"), lookups, new HashSet<string>());
        (rowA.CompanyId, rowA.DepartmentId, rowA.DesignationId).Should().Be((a.Id, dA.Id, xA.Id));
    }

    [Fact]
    public async Task CostCentreCode_IsAllowedInTwoCompanies_ButNotTwiceInOne()
    {
        var (tenant, a, b) = await TwoCompaniesAsync();
        await using var db = fixture.CreateDb();
        var svc = Service(db);
        var ccA = await svc.CreateCostCenterAsync(tenant, new CostCenterRequest(a.Id, "CC_HR", "HR costs"), Ctx(tenant), default);
        var ccB = await svc.CreateCostCenterAsync(tenant, new CostCenterRequest(b.Id, "CC_HR", "HR costs"), Ctx(tenant), default);
        (ccA.CompanyId, ccB.CompanyId).Should().Be((a.Id, b.Id));

        var dup = () => svc.CreateCostCenterAsync(tenant, new CostCenterRequest(a.Id, "cc_hr", "Again"), Ctx(tenant), default);
        (await dup.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*already exists in this company*");

        // The database refuses it too (NULLS NOT DISTINCT unique on tenant, company, code).
        db.CostCenters.Add(new CostCenter { TenantId = tenant, CompanyId = b.Id, Code = "CC_HR", Name = "Raw duplicate" });
        await FluentActions.Awaiting(() => db.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task CostCentre_ArabicName_RoundTrips()
    {
        var (tenant, a, _) = await TwoCompaniesAsync();
        await using var db = fixture.CreateDb();
        var svc = Service(db);
        var created = await svc.CreateCostCenterAsync(tenant, new CostCenterRequest(a.Id, "CC_HR", "HR costs", NameAr: " تكاليف الموارد البشرية "), Ctx(tenant), default);
        created.NameAr.Should().Be("تكاليف الموارد البشرية");
        (await svc.GetCostCenterAsync(tenant, created.Id, default))!.NameAr.Should().Be("تكاليف الموارد البشرية");
        var cleared = await svc.UpdateCostCenterAsync(tenant, created.Id, new CostCenterRequest(a.Id, "CC_HR", "HR costs", NameAr: ""), Ctx(tenant), default);
        cleared!.NameAr.Should().BeNull();
    }
}
