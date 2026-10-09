using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Benefits;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

[Trait("Category", "Integration")]
[Collection("Integration")]
public class GradeBenefitDraftControllerTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EditScreen_ResolvesDraftGradeAndDate_AndPreservesSalaryApproval(bool mixedSalary)
    {
        await using var db = fixture.CreateDb();
        var seed = await Seed(db);
        var prior = await db.BenefitEnrollments.SingleAsync(x => x.EmployeeId == seed.Employee.Id);
        var snapshot = prior.EligibilitySnapshotJson;
        var changes = Changes(("grade", "G2"), ("joiningDate", "2026-11-01"));
        if (mixedSalary) changes["salary"] = JsonSerializer.SerializeToElement(9000m);
        var result = await Controller(db, seed.Tenant).UpdateEmployee(seed.Employee.Id,
            new EmployeeUpdateRequest(new DateOnly(2026, 10, 9), changes), default);
        if (mixedSalary) Assert.IsType<AcceptedResult>(result); else Assert.IsType<OkObjectResult>(result);
        db.ChangeTracker.Clear();
        var employee = await db.Employees.SingleAsync(x => x.Id == seed.Employee.Id);
        Assert.Equal(seed.Grade.Id, employee.GradeId);
        Assert.Equal("G2", employee.Grade);
        Assert.Equal(new DateTime(2026, 11, 1), employee.JoiningDate.Date);
        Assert.Equal(0m, employee.Salary);
        var rows = await db.BenefitEnrollments.Where(x => x.EmployeeId == employee.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        var old = Assert.Single(rows.Where(x => x.Status == GradeBenefitDefaults.SupersededStatus));
        Assert.Equal(prior.Id, old.Id);
        Assert.Equal(snapshot, old.EligibilitySnapshotJson);
        Assert.Equal(new DateOnly(2026, 10, 8), old.EffectiveFrom);
        var current = Assert.Single(rows.Where(x => x.Status == "Active"));
        Assert.Equal(seed.Plan.Id, current.BenefitPlanId);
        Assert.Equal(new DateOnly(2026, 11, 1), current.EffectiveFrom);
        Assert.Equal(1, await db.AuditLogs.CountAsync(x => x.TenantId == seed.Tenant && x.Action == "employee.draft_benefits.update_committed"));
        Assert.Equal(mixedSalary ? 1 : 0, await db.EmployeeChangeRequests.CountAsync(x => x.TenantId == seed.Tenant));
    }

    [Fact]
    public async Task ProtectedDraftException_RefusesTheWholeEditWithoutChangingPersistedPlacement()
    {
        await using var db = fixture.CreateDb();
        var seed = await Seed(db);
        var benefit = await db.BenefitEnrollments.SingleAsync(x => x.EmployeeId == seed.Employee.Id);
        benefit.HasException = true;
        benefit.ExceptionReason = "Agreed individual terms";
        await db.SaveChangesAsync();
        var result = await Controller(db, seed.Tenant).UpdateEmployee(seed.Employee.Id,
            new EmployeeUpdateRequest(new DateOnly(2026, 10, 9), Changes(("grade", "G2"), ("joiningDate", "2026-11-01"))), default);
        Assert.IsType<UnprocessableEntityObjectResult>(result);
        await using var verify = fixture.CreateDb();
        var saved = await verify.Employees.SingleAsync(x => x.Id == seed.Employee.Id);
        Assert.Equal(seed.Employee.GradeId, saved.GradeId);
        Assert.Equal(new DateTime(2026, 10, 8), saved.JoiningDate.Date);
        var savedBenefit = await verify.BenefitEnrollments.SingleAsync(x => x.EmployeeId == saved.Id);
        Assert.Equal("Active", savedBenefit.Status);
        Assert.True(savedBenefit.HasException);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedGradeAndSalary_CommitRetryDoesNotDuplicateBenefitsOrApprovals(bool afterCommit)
    {
        await using var setup = fixture.CreateDb();
        var seed = await Seed(setup);
        var fault = new DraftBenefitEditCommitFault(afterCommit);
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(fixture.ConnectionString, options => options.EnableRetryOnFailure())
            .AddInterceptors(RowLockingInterceptor.Instance, fault).Options);
        var changes = Changes(("grade", "G2"));
        changes["salary"] = JsonSerializer.SerializeToElement(9000m);
        Assert.IsType<AcceptedResult>(await Controller(db, seed.Tenant).UpdateEmployee(seed.Employee.Id,
            new EmployeeUpdateRequest(new DateOnly(2026, 10, 9), changes), default));
        Assert.Equal(1, fault.InjectedFaults);
        await using var verify = fixture.CreateDb();
        Assert.Equal(2, await verify.BenefitEnrollments.CountAsync(x => x.EmployeeId == seed.Employee.Id));
        Assert.Equal(1, await verify.BenefitEnrollments.CountAsync(x => x.EmployeeId == seed.Employee.Id && x.Status == "Active"));
        Assert.Equal(1, await verify.EmployeeChangeRequests.CountAsync(x => x.TenantId == seed.Tenant));
        Assert.Equal(1, await verify.ApprovalRequests.CountAsync(x => x.TenantId == seed.Tenant));
        Assert.Equal(1, await verify.AuditLogs.CountAsync(x => x.TenantId == seed.Tenant && x.Action == "employee.draft_benefits.update_committed"));
    }

    private static Dictionary<string, JsonElement> Changes(params (string Key, string Value)[] values) =>
        values.ToDictionary(x => x.Key, x => JsonSerializer.SerializeToElement(x.Value));

    private static EmployeesController Controller(ZayraDbContext db, Guid tenant)
    {
        var controller = HrmHierarchyTests.BuildImportControllerInternal(db, tenant);
        var identity = (ClaimsIdentity)controller.User.Identity!;
        identity.AddClaims([new Claim("permission", "employees.write"), new Claim("permission", "employees.sensitive"),
            new Claim("is_group_scope", "true")]);
        return controller;
    }

    private static async Task<(Guid Tenant, Employee Employee, Grade Grade, BenefitPlan Plan)> Seed(ZayraDbContext db)
    {
        var tenant = Guid.NewGuid();
        var company = new Company { TenantId = tenant, LegalNameEn = "Draft benefit company", CountryCode = "SA", IsActive = true };
        var first = new Grade { TenantId = tenant, Code = "G1", Name = "Grade One", Level = 1, IsActive = true };
        var second = new Grade { TenantId = tenant, Code = "G2", Name = "Grade Two", Level = 2, IsActive = true };
        var oldPlan = new BenefitPlan { TenantId = tenant, CompanyId = company.Id, Code = "DRAFT-A", Name = "Draft A", EffectiveFrom = new(2026, 1, 1) };
        var newPlan = new BenefitPlan { TenantId = tenant, CompanyId = company.Id, Code = "DRAFT-B", Name = "Draft B", EffectiveFrom = new(2026, 1, 1) };
        db.AddRange(new Tenant { Id = tenant, Name = "Draft benefit controller", Slug = $"draft-benefit-{tenant:N}" }, company, first, second, oldPlan, newPlan);
        db.BenefitEligibilityRules.AddRange(
            new BenefitEligibilityRule { TenantId = tenant, BenefitPlanId = oldPlan.Id, GradeId = first.Id, EffectiveFrom = new(2026, 1, 1), MaxBenefitAmount = 1000 },
            new BenefitEligibilityRule { TenantId = tenant, BenefitPlanId = newPlan.Id, GradeId = second.Id, EffectiveFrom = new(2026, 1, 1), MaxBenefitAmount = 2000 });
        var employee = new Employee { TenantId = tenant, CompanyId = company.Id, GradeId = first.Id, Grade = first.Code,
            EmployeeCode = "DRAFT-TEST", FullName = "Draft benefits test", Status = "Draft", JoiningDate = new(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc) };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        await GradeBenefitDefaults.StageDefaultsAsync(db, employee, null, default);
        await db.SaveChangesAsync();
        return (tenant, employee, second, newPlan);
    }
}

file sealed class DraftBenefitEditCommitFault(bool afterCommit) : DbTransactionInterceptor
{
    public int InjectedFaults { get; private set; }
    private void FailOnce(DbContext? db)
    {
        if (InjectedFaults > 0 || !db!.ChangeTracker.Entries<AuditLog>()
            .Any(x => x.Entity.Action == "employee.draft_benefits.update_committed")) return;
        InjectedFaults++;
        throw new NpgsqlException("Synthetic draft benefit COMMIT connection loss", new IOException("connection reset by peer"));
    }
    public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
        InterceptionResult result, CancellationToken cancellationToken = default)
    {
        if (!afterCommit) FailOnce(eventData.Context);
        return ValueTask.FromResult(result);
    }
    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (afterCommit) FailOnce(eventData.Context);
        return Task.CompletedTask;
    }
}
