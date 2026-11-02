using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// H1: a new hire's FIRST salary package is staged for a second person, never written by the one HR user who typed it.
/// POST /employees (EmployeeManagementService.CreateAsync) and the employee import stage a SalaryChangeRequest; the employee
/// cannot become Active while it waits; a Finance approver who is not the maker applies it, and only then does payroll see a row.
/// </summary>
public class FirstSalaryPackageStagingTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Guid> SeedTenantAsync(ZayraDbContext db, bool withSecondApprover = true)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "T", Slug = $"t-{Guid.NewGuid():N}" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        if (withSecondApprover) SalaryApprovalKit.EnsureApprover(db, tenant.Id);
        return tenant.Id;
    }

    private static EmployeeManagementService Service(ZayraDbContext db) =>
        new(db, new AuditService(db), new NullDocumentStorage(), TestNotifications.For(db));

    private static EmployeeCreateRequest Hire(string code, decimal basic, decimal housing) => new(
        EmployeeCode: code, ManualEmployeeCode: true, EnglishName: "New Hire", ArabicName: null, PreferredName: null,
        Gender: "Male", DateOfBirth: null, Nationality: "Indian", MaritalStatus: null, PersonalEmail: null, WorkEmail: null,
        MobileNumber: null, ProfilePhotoUrl: null, CompanyId: null, BranchId: null, DepartmentId: null, DesignationId: null,
        GradeId: null, CostCenterId: null, JobTitle: null, ReportingManagerEmployeeId: null, SecondLevelManagerEmployeeId: null,
        EmploymentType: "Full-Time", ContractType: "Unlimited", JoiningDate: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        ConfirmationDate: null, ProbationStartDate: null, ProbationEndDate: null, NoticePeriodDays: null, WorkLocation: null,
        PayrollGroup: null, ShiftPolicyCode: null, LeavePolicyCode: null, AttendancePolicyCode: null, PayrollProfile: null,
        SalaryBreakdown: new EmployeeSalaryBreakdownRequest(basic, housing, null, null, null, null, null, null, null, "SAR"),
        ComplianceRecords: null);

    private static RequestContext Ctx(Guid tenantId, Guid maker) => new("127.0.0.1", "tests", maker, tenantId);

    [Fact]
    public async Task CreateAsync_StagesTheFirstPackage_AndWritesNoSalaryRow()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db);
        var hr = Guid.NewGuid();

        var created = await Service(db).CreateAsync(tenantId, Hire("H1-001", 8_000m, 2_000m), Ctx(tenantId, hr), CancellationToken.None);

        (await db.EmployeeSalaryStructures.AnyAsync(x => x.EmployeeId == created.Id)).Should().BeFalse("payroll must not see a package only one person has seen");
        var staged = await db.SalaryChangeRequests.AsNoTracking().SingleAsync(x => x.EmployeeId == created.Id);
        staged.Status.Should().Be(SalaryChangeRequest.Pending);
        staged.Source.Should().Be(SalaryChangeSources.NewHire);
        staged.RequestedByUserId.Should().Be(hr);
        staged.BasicSalary.Should().Be(8_000m);
        staged.HousingAllowance.Should().Be(2_000m);
        (await db.Employees.AsNoTracking().SingleAsync(x => x.Id == created.Id)).Salary.GetValueOrDefault().Should().Be(0m);
    }

    [Fact]
    public async Task ActivatingDraft_IsRefused_WhileTheFirstPackageWaitsForFinance()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db);
        var service = Service(db);
        var created = await service.CreateAsync(tenantId, Hire("H1-002", 5_000m, 1_000m), Ctx(tenantId, Guid.NewGuid()), CancellationToken.None);

        var act = () => service.ChangeStatusAsync(tenantId, created.Id,
            new EmployeeStatusChangeRequest("Active", new DateOnly(2026, 1, 1), "Start"), Ctx(tenantId, Guid.NewGuid()), CancellationToken.None);

        var blocked = (await act.Should().ThrowAsync<EmployeeActivationBlockedException>()).Which;
        blocked.Readiness.Blocking.Should().Contain(i => i.Key == EmployeeReadinessEvaluator.SalaryPackageKey && i.Gate == "activate");
        (await db.Employees.AsNoTracking().SingleAsync(x => x.Id == created.Id)).Status.Should().Be("Draft");
    }

    [Fact]
    public async Task TheHireMaker_CannotApproveTheirOwnPackage_ButADifferentFinanceUserCan_ThenTheBlockerClears()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db);
        var hr = Guid.NewGuid();
        var created = await Service(db).CreateAsync(tenantId, Hire("H1-003", 6_000m, 1_500m), Ctx(tenantId, hr), CancellationToken.None);
        var change = await db.SalaryChangeRequests.AsNoTracking().SingleAsync(x => x.EmployeeId == created.Id);

        var self = await SalaryApprovalKit.FinanceController(db, tenantId, hr).ApproveSalaryChange(change.Id, null, CancellationToken.None);
        self.Should().BeOfType<BadRequestObjectResult>("the maker never approves");
        (await db.EmployeeSalaryStructures.AnyAsync(x => x.EmployeeId == created.Id)).Should().BeFalse();

        Assert.IsType<OkObjectResult>(await SalaryApprovalKit.FinanceController(db, tenantId).ApproveSalaryChange(change.Id, null, CancellationToken.None));

        var row = await db.EmployeeSalaryStructures.AsNoTracking().SingleAsync(x => x.EmployeeId == created.Id && x.IsActive);
        row.BasicSalary.Should().Be(6_000m);
        row.HousingAllowance.Should().Be(1_500m);
        var employee = await db.Employees.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        employee.Salary.Should().Be(7_500m);
        employee.PayrollProfileCode.Should().NotBeNullOrWhiteSpace();
        var readiness = await new EmployeeActivationGuard(db).EvaluateEmployeeAsync(tenantId, created.Id, CancellationToken.None);
        readiness!.Value.Readiness.Blocking.Should().NotContain(i => i.Key == EmployeeReadinessEvaluator.SalaryPackageKey);
    }

    [Fact]
    public async Task ARejectedFirstPackage_LeavesNoSalary_AndNoPendingBlocker()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db);
        var created = await Service(db).CreateAsync(tenantId, Hire("H1-004", 4_000m, 0m), Ctx(tenantId, Guid.NewGuid()), CancellationToken.None);
        var change = await db.SalaryChangeRequests.AsNoTracking().SingleAsync(x => x.EmployeeId == created.Id);

        Assert.IsType<OkObjectResult>(await SalaryApprovalKit.FinanceController(db, tenantId).RejectSalaryChange(change.Id, null, CancellationToken.None));

        (await db.EmployeeSalaryStructures.AnyAsync(x => x.EmployeeId == created.Id)).Should().BeFalse();
        var readiness = await new EmployeeActivationGuard(db).EvaluateEmployeeAsync(tenantId, created.Id, CancellationToken.None);
        readiness!.Value.Readiness.Blocking.Should().NotContain(i => i.Key == EmployeeReadinessEvaluator.SalaryPackageKey);
    }

    [Fact]
    public async Task UpdateAsync_WithABreakdown_StagesToo_ItIsNotADirectWriteDoor()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db);
        var service = Service(db);
        var created = await service.CreateAsync(tenantId, Hire("H1-005", 5_000m, 0m) with { SalaryBreakdown = null }, Ctx(tenantId, Guid.NewGuid()), CancellationToken.None);

        await service.UpdateAsync(tenantId, created.Id, Hire("H1-005", 7_000m, 0m), Ctx(tenantId, Guid.NewGuid()), CancellationToken.None);

        (await db.EmployeeSalaryStructures.AnyAsync(x => x.EmployeeId == created.Id)).Should().BeFalse();
        (await db.SalaryChangeRequests.AsNoTracking().SingleAsync(x => x.EmployeeId == created.Id)).Status.Should().Be(SalaryChangeRequest.Pending);
    }

    [Fact]
    public async Task Hire_WithASalary_IsRefusedUpFront_WhenNobodyElseCouldApproveIt()
    {
        await using var db = CreateDb();
        var tenantId = await SeedTenantAsync(db, withSecondApprover: false);
        var hr = Guid.NewGuid();
        // The only login holding payroll.approve is the maker herself.
        db.Users.Add(new Zayra.Api.Domain.Entities.User { Id = hr, TenantId = tenantId, Email = "hr@test.local", NormalizedEmail = "HR@TEST.LOCAL", FullName = "HR" });
        db.UserPermissionOverrides.Add(new UserPermissionOverride { TenantId = tenantId, UserId = hr, PermissionKey = "payroll.approve", Effect = "Allow" });
        await db.SaveChangesAsync();

        var act = () => Service(db).CreateAsync(tenantId, Hire("H1-006", 5_000m, 0m), Ctx(tenantId, hr), CancellationToken.None);

        var refused = (await act.Should().ThrowAsync<Zayra.Api.Infrastructure.Approvals.SecondApproverCheck.NoEligibleApproverException>()).Which;
        System.Text.Json.JsonSerializer.Serialize(refused.Body).Should().Contain("no_eligible_approver").And.Contain("Add a second person with Finance approval rights (payroll.approve)");
        (await db.Employees.AnyAsync(x => x.EmployeeCode == "H1-006")).Should().BeFalse("nothing is saved when nobody could ever approve the package");

        // A hire with NO salary is not refused: there is nothing to approve.
        var noSalary = await Service(db).CreateAsync(tenantId, Hire("H1-007", 5_000m, 0m) with { SalaryBreakdown = null }, Ctx(tenantId, hr), CancellationToken.None);
        noSalary.Id.Should().BeGreaterThan(0);
    }
}
