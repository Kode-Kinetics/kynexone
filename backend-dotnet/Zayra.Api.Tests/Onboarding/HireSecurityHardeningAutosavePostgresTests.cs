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

    [Fact]
    public async Task Autosave_TwoSavesFromTheSameBase_OneIs200_OneIs409()
    {
        Guid tenantId, companyId; int employeeId;
        await using (var db = _fx.CreateDb())
        {
            var tenant = new Tenant { Name = "Autosave", Slug = $"autosave-{Guid.NewGuid():N}", IsActive = true };
            db.Tenants.Add(tenant);
            var company = new Company { TenantId = tenant.Id, LegalNameEn = "Autosave Co", CountryCode = "SA", IsActive = true, DefaultCurrency = "SAR", RegistrationNumber = $"RC-{Guid.NewGuid():N}", Jurisdiction = "test" };
            db.Companies.Add(company);
            db.TenantSubscriptions.Add(new TenantSubscription { TenantId = tenant.Id, Plan = "Enterprise", Status = "Active", MaxEmployees = 10_000 });
            await db.SaveChangesAsync();
            var employee = new Employee { TenantId = tenant.Id, CompanyId = company.Id, EmployeeCode = $"AS-{Guid.NewGuid():N}"[..12], FullName = "Autosave Draft", EnglishName = "Autosave Draft", Status = "Draft", JoiningDate = DateTime.UtcNow.Date };
            db.Employees.Add(employee);
            await db.SaveChangesAsync();
            tenantId = tenant.Id; companyId = company.Id; employeeId = employee.Id;
        }

        for (var round = 0; round < 3; round++)
        {
            var barrier = new TaskCompletionSource();
            async Task<int?> One(string name)
            {
                await using var db = _fx.CreateDb();
                await db.Database.OpenConnectionAsync();
                var controller = Controller(db, tenantId);
                await barrier.Task;
                var result = await controller.SaveDraft(employeeId, new EmployeeHireController.HireDraftRequest(null, Request(name, companyId)), default);
                return (result as ObjectResult)?.StatusCode;
            }
            var t1 = Task.Run(() => One($"Autosave A {round}"));
            var t2 = Task.Run(() => One($"Autosave B {round}"));
            await Task.Delay(300);
            barrier.SetResult();
            var codes = (await Task.WhenAll(t1, t2)).OrderBy(c => c).ToArray();
            codes.Should().Equal([200, 409], $"round {round}: exactly one save wins");
        }
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
