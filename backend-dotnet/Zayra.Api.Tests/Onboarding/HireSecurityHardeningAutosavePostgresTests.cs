using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers.Employees;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Employees.Onboarding;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Zayra.Api.Tests.Onboarding;

/// <summary>
/// Two autosaves of the same draft from the same basis, each on its own context and connection, released together on
/// real Postgres. The conditional update on the draft's stamp lets exactly one through; the other is a 409.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class HireSecurityHardeningAutosavePostgresTests
{
    private readonly PostgresFixture _fx;
    public HireSecurityHardeningAutosavePostgresTests(PostgresFixture fx) => _fx = fx;

    private async Task<(Guid tenantId, Guid companyId, int employeeId)> SeedDraftAsync()
    {
        await using var db = _fx.CreateDb();
        var tenant = new Tenant { Name = "Autosave", Slug = $"autosave-{Guid.NewGuid():N}", IsActive = true };
        db.Tenants.Add(tenant);
        var company = new Company { TenantId = tenant.Id, LegalNameEn = "Autosave Co", CountryCode = "SA", IsActive = true, DefaultCurrency = "SAR", RegistrationNumber = $"RC-{Guid.NewGuid():N}", Jurisdiction = "test" };
        db.Companies.Add(company);
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = tenant.Id, Plan = "Enterprise", Status = "Active", MaxEmployees = 10_000 });
        await db.SaveChangesAsync();
        var employee = new Employee { TenantId = tenant.Id, CompanyId = company.Id, EmployeeCode = $"AS-{Guid.NewGuid():N}"[..12], FullName = "Autosave Draft", EnglishName = "Autosave Draft", Status = "Draft", JoiningDate = DateTime.UtcNow.Date };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return (tenant.Id, company.Id, employee.Id);
    }

    /// <summary>
    /// Releases <paramref name="saves"/> autosaves together, all carrying the SAME basis (the stamp the client last saw), and
    /// returns their status codes. The basis is what defines "the same base": a save sent without one means "whatever is
    /// current", so one that merely starts after another finished is legitimately a second, sequential save.
    /// </summary>
    private async Task<int?[]> RaceAsync(Guid tenantId, Guid companyId, int employeeId, int round, int saves)
    {
        DateTime basis;
        await using (var db = _fx.CreateDb())
        {
            var row = await db.Employees.AsNoTracking().Where(e => e.TenantId == tenantId && e.Id == employeeId)
                .Select(e => new { e.UpdatedAtUtc, e.CreatedAtUtc }).FirstAsync();
            basis = row.UpdatedAtUtc ?? row.CreatedAtUtc;
        }
        var barrier = new TaskCompletionSource();
        async Task<int?> One(string name)
        {
            await using var db = _fx.CreateDb();
            await db.Database.OpenConnectionAsync();
            var controller = Controller(db, tenantId);
            await barrier.Task;
            var result = await controller.SaveDraft(employeeId, new EmployeeHireController.HireDraftRequest(basis, Request(name, companyId)), default);
            return (result as ObjectResult)?.StatusCode;
        }
        var tasks = Enumerable.Range(0, saves).Select(i => Task.Run(() => One($"Autosave {i} r{round}"))).ToArray();
        await Task.Delay(300);
        barrier.SetResult();
        return (await Task.WhenAll(tasks)).OrderBy(c => c).ToArray();
    }

    [Fact]
    public async Task Autosave_TwoSavesFromTheSameBase_OneIs200_OneIs409()
    {
        var (tenantId, companyId, employeeId) = await SeedDraftAsync();
        for (var round = 0; round < 3; round++)
            (await RaceAsync(tenantId, companyId, employeeId, round, 2)).Should().Equal([200, 409], $"round {round}: exactly one save wins");
    }

    [Fact]
    public async Task Autosave_StressFourSavesFromTheSameBase_ExactlyOneWins()
    {
        var (tenantId, companyId, employeeId) = await SeedDraftAsync();
        for (var round = 0; round < 50; round++)
            (await RaceAsync(tenantId, companyId, employeeId, round, 4)).Should().Equal([200, 409, 409, 409], $"round {round}: exactly one save wins");
    }

    [Fact]
    public async Task Autosave_WithoutBasis_IsRefusedWith400_AndChangesNothing()
    {
        // A save with no basis cannot say which version it was made from, so same-base exclusion cannot hold for it.
        var (tenantId, companyId, employeeId) = await SeedDraftAsync();
        async Task<DateTime?> Stamp() { await using var c = _fx.CreateDb(); return await c.Employees.AsNoTracking().Where(e => e.Id == employeeId).Select(e => e.UpdatedAtUtc).FirstAsync(); }
        var before = await Stamp();
        await using var db = _fx.CreateDb();
        var result = await Controller(db, tenantId).SaveDraft(employeeId, new EmployeeHireController.HireDraftRequest(null, Request("No Basis", companyId)), default);
        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(400);
        obj.Value.Should().BeOfType<ProblemDetails>().Which.Title.Should().Be("draft_basis_required");
        await using var check = _fx.CreateDb();
        var row = await check.Employees.AsNoTracking().Where(e => e.TenantId == tenantId && e.Id == employeeId)
            .Select(e => new { e.UpdatedAtUtc, e.EnglishName }).FirstAsync();
        row.UpdatedAtUtc.Should().Be(before);
        row.EnglishName.Should().Be("Autosave Draft");
    }

    private static EmployeeHireController Controller(ZayraDbContext db, Guid tenantId)
    {
        var audit = new AuditService(db);
        var management = new EmployeeManagementService(db, audit, new Docs(), TestNotifications.For(db));
        var controller = new EmployeeHireController(db, audit, management, new EmployeeActivationGuard(db), new DraftDiscardService(db, audit));
        var claims = new[]
        {
            new Claim("tenant_id", tenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim("permission", "employees.read"), new Claim("permission", "employees.write"),
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")),
                RequestServices = new ServiceCollection().BuildServiceProvider(),
            },
        };
        return controller;
    }

    private static EmployeeCreateRequest Request(string name, Guid companyId) => new(
        EmployeeCode: null, ManualEmployeeCode: false, EnglishName: name, ArabicName: null, PreferredName: null,
        Gender: "Male", DateOfBirth: null, Nationality: "Saudi", MaritalStatus: null, PersonalEmail: null,
        WorkEmail: null, MobileNumber: null, ProfilePhotoUrl: null, CompanyId: companyId, BranchId: null,
        DepartmentId: null, DesignationId: null, GradeId: null, CostCenterId: null, JobTitle: null,
        ReportingManagerEmployeeId: null, SecondLevelManagerEmployeeId: null, EmploymentType: "Full-time",
        ContractType: "Unlimited", JoiningDate: DateTime.UtcNow.Date, ConfirmationDate: null, ProbationStartDate: null,
        ProbationEndDate: null, NoticePeriodDays: null, WorkLocation: null, PayrollGroup: null, ShiftPolicyCode: null,
        LeavePolicyCode: null, AttendancePolicyCode: null, PayrollProfile: null, SalaryBreakdown: null, ComplianceRecords: null,
        PositionId: null, AcknowledgeDuplicate: false);

    private sealed class Docs : IDocumentStorage
    {
        public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) =>
            Task.FromResult(new StoredDocument(file.FileName, file.ContentType, "storage/test", "/tmp/test"));
        public string ResolvePath(string storageUrl) => "/tmp/test";
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    }
}
