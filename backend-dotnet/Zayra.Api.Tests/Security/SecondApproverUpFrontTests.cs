using Zayra.Api.Application.Common;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Zayra.Api.Application.Auth;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Controllers.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// H2: owner decision "a single-approver tenant must add a second approver". Everything that stages work only a second person can
/// decide first asks whether such a person exists (active, holds the permission, in scope of the company, not the maker, not the
/// subject) and refuses with 422 <c>no_eligible_approver</c> and a bilingual message when not.
/// </summary>
public class SecondApproverUpFrontTests
{
    private static string Code(IActionResult r) =>
        JsonSerializer.SerializeToElement(r.Should().BeOfType<UnprocessableEntityObjectResult>().Subject.Value).GetProperty("code").GetString()!;

    private static JsonElement Body(IActionResult r) =>
        JsonSerializer.SerializeToElement(r.Should().BeOfType<UnprocessableEntityObjectResult>().Subject.Value);

    private static async Task<SalaryStructure> StructureAsync(ScopeSodWorld w)
    {
        await using var db = w.Plain();
        var s = new SalaryStructure { TenantId = w.Tenant, Code = "STD", Name = "Standard", Currency = "SAR", EffectiveDate = new DateOnly(2025, 1, 1) };
        db.SalaryStructures.Add(s);
        await db.SaveChangesAsync();
        return s;
    }

    private static EmployeeSalaryStructureRequest Package(ScopeSodWorld w, SalaryStructure s) =>
        new(w.EmpA, s.Id, 9_000m, 0m, 0m, 0m, 0m, 0m, 0m, new DateOnly(2026, 1, 1), "SAR");

    private static async Task AddHolderAsync(ScopeSodWorld w, Guid userId, bool groupScope = true, Guid? onlyCompany = null, string permission = "payroll.approve")
    {
        await using var db = w.Plain();
        db.Users.Add(new User { Id = userId, TenantId = w.Tenant, Email = $"{userId:N}@t.local", NormalizedEmail = $"{userId:N}@T.LOCAL", FullName = "Holder", IsGroupScope = groupScope });
        db.UserPermissionOverrides.Add(new UserPermissionOverride { TenantId = w.Tenant, UserId = userId, PermissionKey = permission, Effect = "Allow" });
        if (onlyCompany is Guid company)
            db.UserEntityAccesses.Add(new UserEntityAccess { TenantId = w.Tenant, UserId = userId, CompanyId = company, GrantMode = EntityGrantModes.SelectedCompanies, IsActive = true });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ProposingASalaryChange_IsRefused_WhenNobodyHoldsPayrollApprove()
    {
        var w = await ScopeSodWorld.CreateAsync(withFinanceApprovers: false);
        var s = await StructureAsync(w);
        await using var db = w.Plain();

        var result = await SalaryApprovalKit.FinanceController(db, w.Tenant, Guid.NewGuid(), "payroll.write").AssignEmployeeSalary(Package(w, s), default);

        Code(result).Should().Be("no_eligible_approver");
        var body = Body(result);
        body.GetProperty("message").GetString().Should().StartWith("Add a second person with Finance approval rights (payroll.approve) before");
        body.GetProperty("messageAr").GetString().Should().NotBeNullOrWhiteSpace();
        (await db.SalaryChangeRequests.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task ProposingASalaryChange_IsRefused_WhenTheOnlyHolderIsTheMaker()
    {
        var w = await ScopeSodWorld.CreateAsync(withFinanceApprovers: false);
        var s = await StructureAsync(w);
        var maker = Guid.NewGuid();
        await AddHolderAsync(w, maker);
        await using var db = w.Plain();

        Code(await SalaryApprovalKit.FinanceController(db, w.Tenant, maker, "payroll.write").AssignEmployeeSalary(Package(w, s), default)).Should().Be("no_eligible_approver");
    }

    [Fact]
    public async Task ProposingASalaryChange_IsRefused_WhenTheOnlyOtherHolderIsTheEmployeeThemselves()
    {
        var w = await ScopeSodWorld.CreateAsync(withFinanceApprovers: false);
        var s = await StructureAsync(w);
        await AddHolderAsync(w, w.UserA); // Alice (the subject) holds payroll.approve; nobody else does
        await using var db = w.Plain();

        Code(await SalaryApprovalKit.FinanceController(db, w.Tenant, Guid.NewGuid(), "payroll.write").AssignEmployeeSalary(Package(w, s), default)).Should().Be("no_eligible_approver");
    }

    [Fact]
    public async Task ProposingASalaryChange_IsRefused_WhenTheOnlyOtherHolderCannotSeeTheEmployeesCompany()
    {
        var w = await ScopeSodWorld.CreateAsync(withFinanceApprovers: false);
        var s = await StructureAsync(w);
        await AddHolderAsync(w, Guid.NewGuid(), groupScope: false, onlyCompany: w.CompanyB); // Alice is in company A
        await using var db = w.Plain();

        Code(await SalaryApprovalKit.FinanceController(db, w.Tenant, Guid.NewGuid(), "payroll.write").AssignEmployeeSalary(Package(w, s), default)).Should().Be("no_eligible_approver");
    }

    [Fact]
    public async Task ProposingASalaryChange_IsAccepted_WhenAnotherInScopeHolderExists()
    {
        var w = await ScopeSodWorld.CreateAsync(withFinanceApprovers: false);
        var s = await StructureAsync(w);
        await AddHolderAsync(w, Guid.NewGuid(), groupScope: false, onlyCompany: w.CompanyA);
        await using var db = w.Plain();

        (await SalaryApprovalKit.FinanceController(db, w.Tenant, Guid.NewGuid(), "payroll.write").AssignEmployeeSalary(Package(w, s), default)).Should().BeOfType<AcceptedResult>();
    }

    [Fact]
    public async Task ASalaryCsv_IsRefused_WhenNobodyElseCouldApproveIt()
    {
        var w = await ScopeSodWorld.CreateAsync(withFinanceApprovers: false);
        var s = await StructureAsync(w);
        await using var db = w.Plain();
        var csv = "EmployeeCode,SalaryStructureCode,BasicSalary,HousingAllowance,TransportAllowance,FoodAllowance,MobileAllowance,OtherAllowance,FixedDeduction,Currency,EffectiveDate\n"
                  + "A-001,STD,9000,0,0,0,0,0,0,SAR,2026-01-01\n";

        var result = await SalaryApprovalKit.FinanceController(db, w.Tenant, Guid.NewGuid(), "payroll.write")
            .ImportEmployeeSalaries(new ImportSalaryStructuresRequest(csv), default);

        Code(result).Should().Be("no_eligible_approver");
        (await db.SalaryChangeRequests.AnyAsync()).Should().BeFalse();
    }

    // ── bonus batches ──

    private static async Task<(Guid BatchId, Guid Maker)> SeedBatchAsync(ScopeSodWorld w, string status)
    {
        var maker = Guid.NewGuid();
        await using var db = w.Plain();
        var type = new BonusType { TenantId = w.Tenant, NameEn = "Performance", Code = "PERF" };
        db.BonusTypes.Add(type);
        var batch = new BonusBatch
        {
            TenantId = w.Tenant, BonusTypeId = type.Id, BonusTypeName = type.NameEn, BatchNumber = "BON-9", BatchName = "B9", PaymentPeriod = "2026-10",
            PaymentDate = new DateOnly(2026, 10, 25), Status = status, EmployeeCount = 1, TotalAmount = 100m, CreatedBy = maker, UpdatedBy = maker,
        };
        db.BonusBatches.Add(batch);
        await db.SaveChangesAsync();
        db.EmployeeBonuses.Add(new EmployeeBonus
        {
            TenantId = w.Tenant, CompanyId = w.CompanyA, BonusBatchId = batch.Id, EmployeeId = Guid.NewGuid(), EmployeeIntId = w.EmpA, EmployeeName = "Alpha Emp",
            BonusTypeId = type.Id, BonusTypeName = type.NameEn, BasicSalary = 5000m, CalculationMethod = "Fixed", CalculationValue = 100m,
            GrossBonusAmount = 100m, BonusAmount = 100m, PaymentPeriod = "2026-10", Status = "Draft",
        });
        await db.SaveChangesAsync();
        return (batch.Id, maker);
    }

    private sealed class OrgScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
    }

    private static BonusesController Bonuses(ScopeSodWorld w, Guid userId)
    {
        var p = w.GroupAdmin(userId, "payroll.write", "payroll.approve");
        return ScopeSodWorld.Wire(new BonusesController(w.DbFor(p), new OrgScope()), p);
    }

    [Fact]
    public async Task SubmittingABonusBatch_IsRefused_WhenNobodyElseCouldApproveIt()
    {
        var w = await ScopeSodWorld.CreateAsync(withFinanceApprovers: false);
        var (id, maker) = await SeedBatchAsync(w, "Draft");

        Code(await Bonuses(w, maker).SubmitBatch(id, default)).Should().Be("no_eligible_approver");
        await using var check = w.Plain();
        (await check.BonusBatches.AsNoTracking().SingleAsync(b => b.Id == id)).Status.Should().Be("Draft");
    }

    [Fact]
    public async Task ApprovingABonusBatch_IsRefused_WhenTheApproverWouldBeTheOnlyOneWhoCouldReleaseThePayment()
    {
        var w = await ScopeSodWorld.CreateAsync(withFinanceApprovers: false);
        var (id, _) = await SeedBatchAsync(w, "PendingApproval");
        var onlyFinance = Guid.NewGuid();
        await AddHolderAsync(w, onlyFinance);

        Code(await Bonuses(w, onlyFinance).ApproveBatch(id, new BatchApproveRequest(null), default)).Should().Be("no_eligible_approver");
        await using var check = w.Plain();
        (await check.BonusBatches.AsNoTracking().SingleAsync(b => b.Id == id)).Status.Should().Be("PendingApproval");
    }

    [Fact]
    public async Task ApprovingABonusBatch_Works_WhenAnotherFinanceUserCanReleaseIt()
    {
        var w = await ScopeSodWorld.CreateAsync(withFinanceApprovers: false);
        var (id, _) = await SeedBatchAsync(w, "PendingApproval");
        var first = Guid.NewGuid();
        await AddHolderAsync(w, first);
        await AddHolderAsync(w, Guid.NewGuid());

        (await Bonuses(w, first).ApproveBatch(id, new BatchApproveRequest(null), default)).Should().BeOfType<OkObjectResult>();
    }

    // ── requisitions ──

    [Fact]
    public async Task SubmittingARequisition_WithNoWorkflow_IsRefused_WhenNoOtherAdminOrHrManagerCouldDecideIt()
    {
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenantId = Guid.NewGuid();
        var maker = Guid.NewGuid();
        var req = new ManpowerRequisition { TenantId = tenantId, RequisitionNumber = "REQ-1", DepartmentName = "Ops", DesignationTitle = "Supervisor", HeadCount = 1, Status = "Draft", RequestedByUserId = maker };
        db.ManpowerRequisitions.Add(req);
        await db.SaveChangesAsync();
        var controller = new RequisitionsController(db, new RecruitmentService(db), new NoNotify(), new ApprovalWorkflowService(db, new AuditService(db)));
        var claims = new List<Claim> { new("tenant_id", tenantId.ToString()), new(ClaimTypes.NameIdentifier, maker.ToString()), new(ClaimTypes.Role, "HR Manager"), new("is_group_scope", "true") };
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) } };

        Code(await controller.Submit(req.Id, default)).Should().Be("no_eligible_approver");
        (await db.ManpowerRequisitions.AsNoTracking().SingleAsync(r => r.Id == req.Id)).Status.Should().Be("Draft");
    }

    private sealed class NoNotify : Zayra.Api.Infrastructure.Notifications.INotificationService
    {
        public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string entity, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
    }
}
