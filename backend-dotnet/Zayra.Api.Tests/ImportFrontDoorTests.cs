using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common.Import;
using Zayra.Api.Application.Organization;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// CSV import used to be a MORE PRIVILEGED door than the form beside it, and a door with no locks
/// on it.
///
/// The org-setup modules each expose two ways to create the same record: a form, which posts one
/// DTO through <see cref="OrganizationSetupService"/>, and a CSV importer, which used to write
/// straight to <see cref="ZayraDbContext"/>. Writing straight to the context meant skipping
/// everything the service and the controller do on the way in — the subscription limit, the
/// company-creation governance gates, referential validation, code normalisation, and the audit
/// row. The role lists then made it worse rather than better: an HR Officer was refused at the
/// form and admitted at the importer, so the cheapest way to do something you were not allowed to
/// do was to do it a hundred rows at a time.
///
/// These four properties are the ones the fix has to hold. Each one failed before it.
/// </summary>
public sealed class ImportFrontDoorTests
{
    // ── (a) An import may never require LESS authority than the form ─────────────────────────

    /// <summary>
    /// The six People &amp; Organisation modules that carry both doors. Each entry is
    /// (controller, the form's create action, the import actions beside it).
    /// </summary>
    public static TheoryData<string> ImportControllers() =>
        new("Companies", "Branches", "Departments", "Designations", "CostCenters", "Grades");

    [Theory]
    [MemberData(nameof(ImportControllers))]
    public async Task ImportDoor_NeverAdmitsARoleTheFormDoorRefuses(string module)
    {
        var controller = ControllerFor(module);
        var createPolicy = await PolicyFor(controller, "Create");
        Assert.NotNull(createPolicy);

        var evaluator = BuildAuthorizationService();

        foreach (var action in new[] { "Import", "ImportPreview" })
        {
            var importPolicy = await PolicyFor(controller, action);
            Assert.NotNull(importPolicy);

            foreach (var role in AllRoles)
            {
                var principal = Principal(role);
                var admittedAtTheForm = (await evaluator.AuthorizeAsync(principal, null, createPolicy!)).Succeeded;
                var admittedAtTheImporter = (await evaluator.AuthorizeAsync(principal, null, importPolicy!)).Succeeded;

                if (admittedAtTheImporter && !admittedAtTheForm)
                    Assert.Fail(
                        $"{controller.Name}.{action} admits '{role}', which {controller.Name}.Create refuses. " +
                        "A bulk door must never be a wider door than the single-record door beside it.");
            }
        }
    }

    /// <summary>The specific live hole: an HR Officer could import companies it could not create.</summary>
    [Fact]
    public async Task AnHrOfficer_CannotImportCompanies_BecauseItCannotCreateOne()
    {
        var evaluator = BuildAuthorizationService();
        var create = await PolicyFor(typeof(CompaniesController), nameof(CompaniesController.Create));
        var import = await PolicyFor(typeof(CompaniesController), nameof(CompaniesController.Import));

        Assert.False((await evaluator.AuthorizeAsync(Principal("HR Officer"), null, create!)).Succeeded);
        Assert.False((await evaluator.AuthorizeAsync(Principal("HR Officer"), null, import!)).Succeeded);

        // …and the roles that MAY create must still be able to import.
        Assert.True((await evaluator.AuthorizeAsync(Principal("HR Manager"), null, import!)).Succeeded);
        Assert.True((await evaluator.AuthorizeAsync(Principal("Admin"), null, import!)).Succeeded);
    }

    // ── (b) An import may not exceed the subscription's company limit ────────────────────────

    [Fact]
    public async Task CompanyImport_CannotExceedTheSubscriptionCompanyLimit()
    {
        await using var db = CreateDb();
        var tenantId = SeedTenant(db, accountType: TenantAccountTypes.Group);
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = tenantId, MaxCompanies = 1 });
        db.Companies.Add(new Company
        {
            TenantId = tenantId, LegalNameEn = "First Company Ltd", CountryCode = "SA",
            RegistrationNumber = "REG-FIRST", DefaultCurrency = "SAR",
        });
        await db.SaveChangesAsync();

        var result = await CompaniesFor(db, tenantId).Import(
            new CompanyImportRequest(CompanyCsv(
                "Second Company Ltd,,,SA,,REG-SECOND,,,,,SAR,true")),
            CancellationToken.None);

        var commit = Commit(result);
        Assert.Equal(0, commit.Created);
        Assert.Equal(1, commit.Skipped);

        var row = Assert.Single(commit.Rows);
        Assert.Equal(ImportRowStatus.Error, row.Status);
        Assert.Contains(row.Errors, e => e.Contains("plan", StringComparison.OrdinalIgnoreCase));

        // The plan said one company. There is one company.
        Assert.Equal(1, await db.Companies.CountAsync(c => c.TenantId == tenantId));
    }

    /// <summary>The other three governance gates the form enforces and the importer used to skip.</summary>
    [Fact]
    public async Task CompanyImport_ObeysPlatformControlledCreationMode()
    {
        await using var db = CreateDb();
        var tenantId = SeedTenant(db, creationMode: CompanyCreationModes.PlatformControlled);
        await db.SaveChangesAsync();

        var result = await CompaniesFor(db, tenantId).Import(
            new CompanyImportRequest(CompanyCsv("Any Company Ltd,,,SA,,REG-ANY,,,,,SAR,true")),
            CancellationToken.None);

        var commit = Commit(result);
        Assert.Equal(0, commit.Created);
        Assert.Equal(ImportRowStatus.Error, Assert.Single(commit.Rows).Status);
        Assert.Empty(await db.Companies.Where(c => c.TenantId == tenantId).ToListAsync());
    }

    [Fact]
    public async Task CompanyImport_ObeysTheSingleCompanyAccountType()
    {
        await using var db = CreateDb();
        var tenantId = SeedTenant(db, accountType: TenantAccountTypes.SingleCompany);
        db.Companies.Add(new Company
        {
            TenantId = tenantId, LegalNameEn = "The Only Company Ltd", CountryCode = "SA",
            RegistrationNumber = "REG-ONLY", DefaultCurrency = "SAR",
        });
        await db.SaveChangesAsync();

        var result = await CompaniesFor(db, tenantId).Import(
            new CompanyImportRequest(CompanyCsv("A Second Entity Ltd,,,SA,,REG-TWO,,,,,SAR,true")),
            CancellationToken.None);

        var commit = Commit(result);
        Assert.Equal(0, commit.Created);
        Assert.Equal(ImportRowStatus.Error, Assert.Single(commit.Rows).Status);
        Assert.Equal(1, await db.Companies.CountAsync(c => c.TenantId == tenantId));
    }

    [Fact]
    public async Task CompanyImport_WritesAnAuditRow_LikeTheFormDoes()
    {
        await using var db = CreateDb();
        var tenantId = SeedTenant(db);
        await db.SaveChangesAsync();

        await CompaniesFor(db, tenantId).Import(
            new CompanyImportRequest(CompanyCsv("Audited Company Ltd,,,SA,,REG-AUD,,,,,SAR,true")),
            CancellationToken.None);

        Assert.Contains(
            await db.AuditLogs.Where(a => a.TenantId == tenantId).ToListAsync(),
            a => a.Action == "organization.company_created");
    }

    // ── (c) The case rule is ONE rule, on both doors ─────────────────────────────────────────

    /// <summary>
    /// The form upper-cased a code; the importer stored it verbatim. So `ops` (imported) and `OPS`
    /// (typed into the form) could both exist in one tenant against a case-SENSITIVE unique index.
    /// </summary>
    [Fact]
    public async Task ImportAndForm_CannotProduceTwoDepartmentsWhoseCodesDifferOnlyInCase()
    {
        await using var db = CreateDb();
        var tenantId = SeedTenant(db);
        await db.SaveChangesAsync();

        // Door 1 — the importer.
        await DepartmentsFor(db, tenantId).Import(
            new DeptImportRequest(DeptCsv("ops,Operations,,,,,true")), CancellationToken.None);

        // Door 2 — the form, with the same code in a different case.
        var formResult = await DepartmentsFor(db, tenantId).Create(
            new DepartmentRequest(null, null, null, "OPS", "Operations", null, null),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(formResult.Result);

        var departments = await db.Departments.Where(d => d.TenantId == tenantId).ToListAsync();
        var only = Assert.Single(departments);
        Assert.Equal("OPS", only.Code);
    }

    /// <summary>
    /// A tenant that ALREADY holds `ops` and `OPS` (both doors were open before the fix) used to get
    /// a 500 from import and import-preview forever: the lookup keys on ToUpperInvariant and the
    /// dictionary build threw on the duplicate key. It must refuse with an explanation instead.
    /// </summary>
    [Fact]
    public async Task LegacyCaseVariantCodes_AreRefusedWithAnExplanation_NotA500()
    {
        await using var db = CreateDb();
        var tenantId = SeedTenant(db);
        db.Departments.AddRange(
            new Department { TenantId = tenantId, Code = "ops", NameEn = "Operations (imported)" },
            new Department { TenantId = tenantId, Code = "OPS", NameEn = "Operations (typed)" });
        await db.SaveChangesAsync();

        var preview = await DepartmentsFor(db, tenantId).ImportPreview(
            new DeptImportRequest(DeptCsv("FIN,Finance,,,,,true")), CancellationToken.None);
        var commit = await DepartmentsFor(db, tenantId).Import(
            new DeptImportRequest(DeptCsv("FIN,Finance,,,,,true")), CancellationToken.None);

        foreach (var response in new[] { preview, commit })
        {
            var conflict = Assert.IsType<ConflictObjectResult>(response);
            var payload = System.Text.Json.JsonSerializer.Serialize(conflict.Value);
            Assert.Contains("OPS", payload, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── (d) An import never silently destroys a relationship it could not resolve ────────────

    [Fact]
    public async Task DesignationImport_UnresolvedDepartmentCode_DoesNotNullALiveDepartment()
    {
        await using var db = CreateDb();
        var tenantId = SeedTenant(db);
        var department = new Department { TenantId = tenantId, Code = "HR", NameEn = "Human Resources" };
        db.Departments.Add(department);
        db.Designations.Add(new Designation
        {
            TenantId = tenantId, Code = "HR-OFF", TitleEn = "HR Officer", DepartmentId = department.Id,
        });
        await db.SaveChangesAsync();

        var result = await DesignationsFor(db, tenantId).Import(
            new DesigImportRequest(DesigCsv("HR-OFF,HR Officer,,TYPO-DEPT,G3,true")),
            CancellationToken.None);

        var commit = Commit(result);
        Assert.Equal(0, commit.Updated);
        Assert.Equal(1, commit.Skipped);

        var row = Assert.Single(commit.Rows);
        Assert.Equal(ImportRowStatus.Error, row.Status);
        Assert.Contains(row.Errors, e => e.Contains("TYPO-DEPT"));

        var designation = await db.Designations.SingleAsync(d => d.TenantId == tenantId && d.Code == "HR-OFF");
        Assert.Equal(department.Id, designation.DepartmentId);
    }

    [Fact]
    public async Task BranchImport_RowNamingAnotherCompany_DoesNotStealThatCompanysBranch()
    {
        await using var db = CreateDb();
        var tenantId = SeedTenant(db, accountType: TenantAccountTypes.Group);
        var companyA = new Company
        {
            TenantId = tenantId, LegalNameEn = "Company A Ltd", CountryCode = "SA",
            RegistrationNumber = "REG-A", DefaultCurrency = "SAR",
        };
        var companyB = new Company
        {
            TenantId = tenantId, LegalNameEn = "Company B Ltd", CountryCode = "SA",
            RegistrationNumber = "REG-B", DefaultCurrency = "SAR",
        };
        db.Companies.AddRange(companyA, companyB);
        db.Branches.Add(new Branch
        {
            TenantId = tenantId, CompanyId = companyA.Id, Code = "HQ", NameEn = "A Head Office",
            CountryCode = "SA", City = "Riyadh", TimeZoneId = "Asia/Riyadh",
        });
        await db.SaveChangesAsync();

        // Branch Code is unique tenant-wide, so "HQ" under Company B collides with Company A's HQ.
        var result = await BranchesFor(db, tenantId).Import(
            new BranchImportRequest(BranchCsv(
                "Company B Ltd,HQ,B Head Office,,SA,Jeddah,,,Asia/Riyadh,,true,true")),
            CancellationToken.None);

        var commit = Commit(result);
        Assert.Equal(0, commit.Updated);
        Assert.Equal(0, commit.Created);
        Assert.Equal(1, commit.Skipped);

        var row = Assert.Single(commit.Rows);
        Assert.Equal(ImportRowStatus.Error, row.Status);
        Assert.Contains(row.Errors, e => e.Contains("Company A Ltd"));

        var branch = await db.Branches.SingleAsync(b => b.TenantId == tenantId && b.Code == "HQ");
        Assert.Equal(companyA.Id, branch.CompanyId);
        Assert.Equal("A Head Office", branch.NameEn);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    private static readonly string[] AllRoles =
    {
        "Admin", "HR Director", "HR Manager", "HR Officer", "Payroll Manager", "Payroll Officer",
        "Finance Controller", "Finance Approver", "Compliance Officer", "Auditor",
        "Manager", "Supervisor", "Employee",
    };

    private static Type ControllerFor(string module) => module switch
    {
        "Companies" => typeof(CompaniesController),
        "Branches" => typeof(BranchesController),
        "Departments" => typeof(DepartmentsController),
        "Designations" => typeof(DesignationsController),
        "CostCenters" => typeof(CostCentersController),
        "Grades" => typeof(GradesController),
        _ => throw new ArgumentOutOfRangeException(nameof(module), module, null),
    };

    private static async Task<AuthorizationPolicy?> PolicyFor(Type controller, string methodName)
    {
        var method = controller.GetMethod(methodName) ?? throw new InvalidOperationException($"{controller.Name}.{methodName}");
        IEnumerable<IAuthorizeData> data =
        [
            .. method.GetCustomAttributes<AuthorizeAttribute>(inherit: true),
            .. controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true),
        ];
        var options = new AuthorizationOptions();
        return await AuthorizationPolicy.CombineAsync(
            new DefaultAuthorizationPolicyProvider(Microsoft.Extensions.Options.Options.Create(options)), data);
    }

    private static IAuthorizationService BuildAuthorizationService()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AuthorizationOptions());
        var handlers = new DefaultAuthorizationHandlerProvider(
            new IAuthorizationHandler[] { new PassThroughAuthorizationHandler(), new RolesHandler() });
        return new DefaultAuthorizationService(
            new DefaultAuthorizationPolicyProvider(options),
            handlers,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DefaultAuthorizationService>.Instance,
            new DefaultAuthorizationHandlerContextFactory(),
            new DefaultAuthorizationEvaluator(),
            options);
    }

    private sealed class RolesHandler : AuthorizationHandler<RolesAuthorizationRequirement>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, RolesAuthorizationRequirement requirement)
        {
            if (context.User.Identity?.IsAuthenticated == true && requirement.AllowedRoles.Any(context.User.IsInRole))
                context.Succeed(requirement);
            return Task.CompletedTask;
        }
    }

    private static ClaimsPrincipal Principal(string role) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, role),
        ], "test"));

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static Guid SeedTenant(
        ZayraDbContext db,
        string? accountType = null,
        string? creationMode = null)
    {
        var tenant = new Tenant
        {
            Name = "Import Front Door",
            Slug = $"import-front-door-{Guid.NewGuid():N}",
            AccountType = accountType ?? TenantAccountTypes.Group,
            CompanyCreationMode = creationMode ?? CompanyCreationModes.GroupSelfServiceWithinLimit,
        };
        db.Tenants.Add(tenant);
        return tenant.Id;
    }

    private static ControllerContext ContextFor(Guid tenantId) => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("tenant_id", tenantId.ToString()),
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            ], "test")),
        },
    };

    private static IOrganizationSetupService Service(ZayraDbContext db) =>
        new OrganizationSetupService(db, new AuditService(db));

    private static CompaniesController CompaniesFor(ZayraDbContext db, Guid tenantId) =>
        new(Service(db), db) { ControllerContext = ContextFor(tenantId) };

    private static BranchesController BranchesFor(ZayraDbContext db, Guid tenantId) =>
        new(Service(db), db) { ControllerContext = ContextFor(tenantId) };

    private static DepartmentsController DepartmentsFor(ZayraDbContext db, Guid tenantId) =>
        new(Service(db), db) { ControllerContext = ContextFor(tenantId) };

    private static DesignationsController DesignationsFor(ZayraDbContext db, Guid tenantId) =>
        new(Service(db), db) { ControllerContext = ContextFor(tenantId) };

    private static ImportCommitResult Commit(IActionResult result) =>
        Assert.IsType<ImportCommitResult>(Assert.IsType<OkObjectResult>(result).Value);

    private static string CompanyCsv(params string[] rows) => Csv(
        "LegalNameEn,LegalNameAr,TradeName,CountryCode,Jurisdiction,RegistrationNumber,TaxNumber,WpsEmployerId,GosiEmployerId,QiwaEstablishmentId,DefaultCurrency,IsActive",
        rows);

    private static string BranchCsv(params string[] rows) => Csv(
        "CompanyLegalName,Code,NameEn,NameAr,CountryCode,City,AddressLine1,AddressLine2,TimeZoneId,LaborOfficeCode,IsHeadOffice,IsActive",
        rows);

    private static string DeptCsv(params string[] rows) => Csv(
        "Code,NameEn,NameAr,ParentDepartmentCode,ManagerEmployeeCode,CostCenterCode,IsActive", rows);

    private static string DesigCsv(params string[] rows) => Csv(
        "Code,TitleEn,TitleAr,DepartmentCode,JobGrade,IsActive", rows);

    private static string Csv(string header, IEnumerable<string> rows) =>
        string.Join('\n', new[] { header }.Concat(rows)) + '\n';
}
