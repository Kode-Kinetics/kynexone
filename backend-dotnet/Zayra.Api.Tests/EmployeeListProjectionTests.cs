using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// VALUE-LEVEL coverage for the People list projection (<c>GET /api/employees</c>).
///
/// <para>The list had no test at all: <see cref="EmployeeListItemDto"/> was only ever touched by
/// reflection (Security/SensitiveFieldMaskingTests), so nothing in the suite had ever asserted what a
/// user actually sees in a row — Department, Designation, Branch, Status, readiness badge. Every
/// production symptom on that screen (all rows Draft / "Incomplete · 1" / blank Branch) lives in code
/// these tests are the first to execute for its output values.</para>
///
/// <para>There are TWO implementations of the same list. The group-level path
/// (<see cref="EmployeeManagementService.SearchAsync"/>) and the hand-duplicated restricted-scope path
/// (<see cref="EmployeesController.Search"/>) project the same DTO from the same table, so for a row
/// visible to both they must return identical values AND identical order. These tests assert that
/// agreement rather than testing each branch in isolation.</para>
///
/// <para>Provider caveat: the fixture is EF InMemory (the convention across this project), which
/// enforces no FK, unique-index or collation semantics. So nothing here proves referential integrity or
/// case-insensitive ordering; what it does prove is the projection's column mapping and the two
/// branches' agreement, which are provider-independent.</para>
/// </summary>
public class EmployeeListProjectionTests
{
    // Deliberate: EmployeeCode order and FullName order DISAGREE for this pair, because the two list
    // implementations order by different columns. A row that sorts the same way under both orderings
    // would hide that.
    private const string ZainabCode = "EMP-001";   // FullName sorts LAST
    private const string AhmedCode = "EMP-002";    // FullName sorts FIRST

    [Fact]
    public async Task GroupLevelList_ReturnsStoredColumnValuesForEveryVisibleField()
    {
        await using var db = CreateDb();
        var fixture = await SeedAsync(db);

        var page = await Service(db).SearchAsync(fixture.TenantId, null, null, null, null, null, null, 1, 25, CancellationToken.None);

        page.Total.Should().Be(2);
        var zainab = page.Items.Single(x => x.EmployeeCode == ZainabCode);
        zainab.FullName.Should().Be("Zainab Al-Otaibi");
        zainab.Department.Should().Be("Finance");
        zainab.Designation.Should().Be("Accountant");
        zainab.Branch.Should().Be("Riyadh Tower");
        zainab.Status.Should().Be("Active");
        zainab.ReadinessState.Should().Be("Ready");
        zainab.ActivationBlockersCount.Should().Be(0);

        var ahmed = page.Items.Single(x => x.EmployeeCode == AhmedCode);
        ahmed.FullName.Should().Be("Ahmed Bakr");
        ahmed.Department.Should().Be("Operations");
        ahmed.Designation.Should().Be("Site Supervisor");
        ahmed.Branch.Should().Be("Jeddah HQ");
        ahmed.Status.Should().Be("Draft");
        ahmed.ReadinessState.Should().Be("NeedsAttention");
        ahmed.ActivationBlockersCount.Should().Be(3);
    }

    [Fact]
    public async Task RestrictedScopeList_ReturnsTheSameFieldValuesAsTheGroupLevelList()
    {
        await using var db = CreateDb();
        var fixture = await SeedAsync(db);

        var groupRows = await ListAsync(db, fixture, companyScoped: false);
        var restrictedRows = await ListAsync(db, fixture, companyScoped: true);

        restrictedRows.Should().HaveCount(groupRows.Count);
        foreach (var expected in groupRows)
        {
            var actual = restrictedRows.Single(x => x.Id == expected.Id);
            // Field-by-field rather than record equality so a failure names the offending column.
            actual.EmployeeCode.Should().Be(expected.EmployeeCode);
            actual.FullName.Should().Be(expected.FullName);
            actual.Department.Should().Be(expected.Department);
            actual.Designation.Should().Be(expected.Designation);
            actual.Branch.Should().Be(expected.Branch);
            actual.Status.Should().Be(expected.Status);
            actual.ReadinessState.Should().Be(expected.ReadinessState);
            actual.ActivationBlockersCount.Should().Be(expected.ActivationBlockersCount);
        }

        // Anchored to literals too, so both branches agreeing on a WRONG value cannot pass.
        var ahmed = restrictedRows.Single(x => x.EmployeeCode == AhmedCode);
        ahmed.Department.Should().Be("Operations");
        ahmed.Branch.Should().Be("Jeddah HQ");
        ahmed.ReadinessState.Should().Be("NeedsAttention");
        ahmed.ActivationBlockersCount.Should().Be(3);
    }

    [Fact]
    public async Task GroupLevelAndRestrictedScopeLists_ReturnRowsInTheSameOrder()
    {
        // Same page of the same list, same page size, same filters — a user who is company-scoped must
        // not see the People list sorted differently from a group user. The service orders by
        // EmployeeCode; the controller's duplicated branch orders by FullName.
        await using var db = CreateDb();
        var fixture = await SeedAsync(db);

        var groupRows = await ListAsync(db, fixture, companyScoped: false);
        var restrictedRows = await ListAsync(db, fixture, companyScoped: true);

        restrictedRows.Select(x => x.EmployeeCode).Should()
            .Equal(groupRows.Select(x => x.EmployeeCode));
    }

    [Fact]
    public async Task CreateWithBranchId_ShowsTheBranchNameInBothListBranches()
    {
        await using var db = CreateDb();
        var fixture = await SeedAsync(db);

        var created = await Service(db).CreateAsync(fixture.TenantId, NewHire(fixture), Ctx(fixture.TenantId), CancellationToken.None);

        var groupRow = (await ListAsync(db, fixture, companyScoped: false)).Single(x => x.Id == created.Id);
        groupRow.EmployeeCode.Should().Be("EMP-BRANCH-001");
        groupRow.FullName.Should().Be("Noura Saleh");
        groupRow.Branch.Should().Be("Jeddah HQ");
        groupRow.Department.Should().Be("Operations");
        groupRow.Designation.Should().Be("Site Supervisor");
        groupRow.Status.Should().Be("Draft");

        var restrictedRow = (await ListAsync(db, fixture, companyScoped: true)).Single(x => x.Id == created.Id);
        restrictedRow.Branch.Should().Be("Jeddah HQ");
        restrictedRow.Department.Should().Be("Operations");
        restrictedRow.Designation.Should().Be("Site Supervisor");
        restrictedRow.Status.Should().Be("Draft");
    }

    [Fact]
    public async Task EmployeeLinkedToABranch_ShowsANonEmptyBranchInTheList()
    {
        // The row is linked to a real Branch (BranchId resolves to "Jeddah HQ") but its denormalized
        // Branch string was never stamped — the state any writer that sets the link without the label
        // leaves behind. The list projection reads ONLY the denormalized column and never follows the
        // link, so the Branch column renders blank for a person who demonstrably has a branch.
        await using var db = CreateDb();
        var fixture = await SeedAsync(db);
        db.Employees.Add(new Employee
        {
            TenantId = fixture.TenantId,
            CompanyId = fixture.CompanyId,
            EmployeeCode = "EMP-LINKONLY",
            FullName = "Linked Only",
            EnglishName = "Linked Only",
            Department = "Operations",
            Designation = "Site Supervisor",
            BranchId = fixture.JeddahBranchId,   // real, seeded branch
            Branch = string.Empty,               // label never stamped
            Status = "Active",
            ReadinessState = "Ready",
            ActivationBlockersCount = 0,
            JoiningDate = DateTime.SpecifyKind(new DateTime(2025, 1, 1), DateTimeKind.Utc)
        });
        await db.SaveChangesAsync();

        var row = (await ListAsync(db, fixture, companyScoped: false)).Single(x => x.EmployeeCode == "EMP-LINKONLY");

        (await db.Branches.SingleAsync(b => b.Id == fixture.JeddahBranchId)).NameEn.Should().Be("Jeddah HQ");
        row.Branch.Should().NotBeNullOrWhiteSpace("an employee with a BranchId belongs to a branch and the People list must show it");
    }

    // ── fixture ───────────────────────────────────────────────────────────────────────────────────

    private sealed record Fixture(Guid TenantId, Guid CompanyId, Guid JeddahBranchId, Guid RiyadhBranchId, Guid DepartmentId, Guid DesignationId);

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static RequestContext Ctx(Guid tenantId) => new(null, "tests", Guid.NewGuid(), tenantId);

    private static EmployeeManagementService Service(ZayraDbContext db) =>
        new(db, new AuditService(db), new NullDocumentStorage(), TestNotifications.For(db));

    private static async Task<Fixture> SeedAsync(ZayraDbContext db)
    {
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Zayra HQ", Slug = $"z-{tenantId:N}" });
        db.Roles.Add(new Role { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Employee", NormalizedName = "EMPLOYEE", Description = "Employee" });
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = tenantId, MaxEmployees = 1000, Plan = "Enterprise", Status = "Active" });
        var company = new Company
        {
            TenantId = tenantId,
            LegalNameEn = "Zayra Industrial",
            CountryCode = "SA",
            Jurisdiction = "test",
            RegistrationNumber = $"RC-{Guid.NewGuid():N}",
            DefaultCurrency = "SAR",
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.Companies.Add(company);
        var jeddah = new Branch { TenantId = tenantId, CompanyId = company.Id, Code = "JED", NameEn = "Jeddah HQ", IsActive = true };
        var riyadh = new Branch { TenantId = tenantId, CompanyId = company.Id, Code = "RUH", NameEn = "Riyadh Tower", IsActive = true };
        db.Branches.AddRange(jeddah, riyadh);
        var department = new Department { TenantId = tenantId, Code = "OPS", NameEn = "Operations", IsActive = true };
        var designation = new Designation { TenantId = tenantId, Code = "SSV", TitleEn = "Site Supervisor", IsActive = true };
        db.Departments.Add(department);
        db.Designations.Add(designation);
        await db.SaveChangesAsync();

        db.Employees.AddRange(
            new Employee
            {
                TenantId = tenantId,
                CompanyId = company.Id,
                EmployeeCode = ZainabCode,
                FullName = "Zainab Al-Otaibi",
                EnglishName = "Zainab Al-Otaibi",
                ArabicName = "زينب العتيبي",
                Department = "Finance",
                Designation = "Accountant",
                Branch = "Riyadh Tower",
                BranchId = riyadh.Id,
                Status = "Active",
                ReadinessState = "Ready",
                ActivationBlockersCount = 0,
                ProfileCompletenessScore = 100m,
                JoiningDate = DateTime.SpecifyKind(new DateTime(2024, 3, 1), DateTimeKind.Utc)
            },
            new Employee
            {
                TenantId = tenantId,
                CompanyId = company.Id,
                EmployeeCode = AhmedCode,
                FullName = "Ahmed Bakr",
                EnglishName = "Ahmed Bakr",
                ArabicName = "أحمد بكر",
                Department = "Operations",
                Designation = "Site Supervisor",
                Branch = "Jeddah HQ",
                BranchId = jeddah.Id,
                Status = "Draft",
                ReadinessState = "NeedsAttention",
                ActivationBlockersCount = 3,
                ProfileCompletenessScore = 40m,
                JoiningDate = DateTime.SpecifyKind(new DateTime(2025, 6, 1), DateTimeKind.Utc)
            });
        await db.SaveChangesAsync();

        return new Fixture(tenantId, company.Id, jeddah.Id, riyadh.Id, department.Id, designation.Id);
    }

    private static EmployeeCreateRequest NewHire(Fixture fixture) => new(
        EmployeeCode: "EMP-BRANCH-001", ManualEmployeeCode: true, EnglishName: "Noura Saleh", ArabicName: null,
        PreferredName: null, Gender: "Female", DateOfBirth: null, Nationality: "Saudi", MaritalStatus: null,
        PersonalEmail: null, WorkEmail: null, MobileNumber: null, ProfilePhotoUrl: null,
        CompanyId: fixture.CompanyId, BranchId: fixture.JeddahBranchId, DepartmentId: fixture.DepartmentId,
        DesignationId: fixture.DesignationId, GradeId: null, CostCenterId: null, JobTitle: null,
        ReportingManagerEmployeeId: null, SecondLevelManagerEmployeeId: null, EmploymentType: "Full-time",
        ContractType: "Unlimited", JoiningDate: new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Unspecified),
        ConfirmationDate: null, ProbationStartDate: null, ProbationEndDate: null, NoticePeriodDays: null,
        WorkLocation: null, PayrollGroup: null, ShiftPolicyCode: null, LeavePolicyCode: null,
        AttendancePolicyCode: null, PayrollProfile: null, SalaryBreakdown: null, ComplianceRecords: null);

    /// <summary>
    /// Drives the real endpoint. <paramref name="companyScoped"/> false → group-level branch (delegates to
    /// the service); true → the controller's hand-duplicated restricted-scope branch.
    /// </summary>
    private static async Task<IReadOnlyList<EmployeeListItemDto>> ListAsync(ZayraDbContext db, Fixture fixture, bool companyScoped)
    {
        var controller = CreateController(db, fixture, companyScoped);
        var result = await controller.Search(Service(db), null, null, null, null, null, null, 1, 25, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var page = Assert.IsType<PagedResult<EmployeeListItemDto>>(ok.Value);
        return page.Items.ToList();
    }

    private static EmployeesController CreateController(ZayraDbContext db, Fixture fixture, bool companyScoped)
    {
        var audit = new AuditService(db);
        var controller = new EmployeesController(
            db,
            new Pbkdf2PasswordHasher(),
            audit,
            new NullDocumentStorage(),
            TestNotifications.For(db),
            new ListProjectionHijriDateService(),
            new Zayra.Api.Infrastructure.Common.DataScopeService(db),
            new ListProjectionLetterService());

        var claims = new List<Claim>
        {
            new("tenant_id", fixture.TenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, "Admin"),
            new("permission", "employees.read"),
            new("permission", "employees.write"),
        };
        if (companyScoped)
            claims.Add(new Claim(EntityScopeContext.V2ClaimType,
                System.Text.Json.JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { fixture.CompanyId.ToString() } })));
        else
            claims.Add(new Claim("is_group_scope", "true"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
        return controller;
    }
}

file sealed class ListProjectionHijriDateService : Zayra.Api.Infrastructure.Localization.IHijriDateService
{
    public Zayra.Api.Infrastructure.Localization.DateConversionDto FromGregorian(DateOnly date) =>
        new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
}

file sealed class ListProjectionLetterService : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<byte>());

    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<byte>());

    public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<byte>());

    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<byte>());
}
