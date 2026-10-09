using System.Data.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

[Trait("Category", "Integration")]
[Collection("Integration")]
public class GradeBenefitHireIntegrationTests
{
    private readonly PostgresFixture _fixture;
    public GradeBenefitHireIntegrationTests(PostgresFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Create_TransientCommitRetry_PersistsEmployeeDefaultsAndHistoryExactlyOnce(bool afterCommit, bool generatedCode)
    {
        await using var setup = _fixture.CreateDb();
        var (tenant, company, grade, plan) = await Seed(setup);
        var fault = new BenefitHireCommitFault(afterCommit);
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(_fixture.ConnectionString, options => options.EnableRetryOnFailure())
            .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance, fault).Options);
        var service = new EmployeeManagementService(db, new AuditService(db), new BenefitHireDocuments(), TestNotifications.For(db));
        var request = Request(company.Id, grade.Id) with { ManualEmployeeCode = !generatedCode };
        var result = await service.CreateAsync(tenant, request, new RequestContext(null, "retry-test", Guid.NewGuid(), tenant), default);

        Assert.Equal(1, fault.InjectedFaults);
        Assert.True(fault.SawSavedDefaults);
        await using var verify = _fixture.CreateDb();
        var employee = Assert.Single(await verify.Employees.Where(x => x.TenantId == tenant).ToListAsync());
        Assert.Equal(result.Id, employee.Id);
        Assert.Equal(fault.PreparedPublicId, employee.PublicId);
        if (!afterCommit) Assert.NotEqual(fault.FirstSavedEmployeeId, employee.Id);
        else Assert.Equal(fault.FirstSavedEmployeeId, employee.Id);
        var enrollment = Assert.Single(await verify.BenefitEnrollments.Where(x => x.TenantId == tenant).ToListAsync());
        Assert.Equal(employee.Id, enrollment.EmployeeId);
        Assert.Equal(plan.Id, enrollment.BenefitPlanId);
        Assert.Equal("GradeDefault", enrollment.AssignmentSource);
        Assert.Single(await verify.EmployeeHistories.Where(x => x.TenantId == tenant && x.EmployeeId == employee.Id && x.EventType == "Created").ToListAsync());
        Assert.Single(await verify.AuditLogs.Where(x => x.TenantId == tenant && x.Action == "benefits.grade_default.assigned").ToListAsync());
        Assert.Single(await verify.AuditLogs.Where(x => x.TenantId == tenant && x.Action == "employee.created").ToListAsync());
        if (generatedCode)
            Assert.Equal(2, (await verify.EmployeeIdRules.SingleAsync(x => x.TenantId == tenant)).NextSequence);
        else
            Assert.Equal("GB-CREATE", employee.EmployeeCode);
    }

    [Fact]
    public async Task FailedBenefitSave_RollsBackTheEmployeeAndEveryDefaultInPostgres()
    {
        await using var setup = _fixture.CreateDb();
        var (tenant, company, grade, _) = await Seed(setup);
        await using var failing = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(_fixture.ConnectionString, options => options.EnableRetryOnFailure())
            .AddInterceptors(new RefuseBenefitSave()).Options);
        var service = new EmployeeManagementService(failing, new AuditService(failing), new BenefitHireDocuments(), TestNotifications.For(failing));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(tenant,
            Request(company.Id, grade.Id), new RequestContext(null, "rollback-test", Guid.NewGuid(), tenant), default));
        Assert.Equal("Synthetic benefit write failure", failure.Message);
        await using var verify = _fixture.CreateDb();
        Assert.False(await verify.Employees.AnyAsync(x => x.TenantId == tenant));
        Assert.False(await verify.BenefitEnrollments.AnyAsync(x => x.TenantId == tenant));
    }

    [Fact]
    public async Task ImportPreview_RollsBackDefaults_AndCommitPersistsThemInPostgres()
    {
        await using var db = _fixture.CreateDb();
        var (tenant, _, _, _) = await Seed(db);
        var controller = HrmHierarchyTests.BuildImportControllerInternal(db, tenant);
        const string csv = "EmployeeCode,FullName,CompanyLegalName,Grade,JoiningDate\nGB-PREVIEW,Synthetic Benefits Preview,Benefit Company,G5,2026-10-08\n";
        Assert.IsType<OkObjectResult>(await controller.ImportPreview(new EmployeesController.ImportEmployeesRequest(csv), default));
        await using (var verify = _fixture.CreateDb())
        {
            Assert.False(await verify.Employees.AnyAsync(x => x.TenantId == tenant));
            Assert.False(await verify.BenefitEnrollments.AnyAsync(x => x.TenantId == tenant));
        }
        Assert.IsType<OkObjectResult>(await controller.Import(new EmployeesController.ImportEmployeesRequest(csv), default));
        await using var committed = _fixture.CreateDb();
        Assert.Single(await committed.BenefitEnrollments.Where(x => x.TenantId == tenant).ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_AssignsResolvedGradeBenefits_AndLaterProfileEditsKeepExceptions(bool inheritDesignationGrade)
    {
        await using var db = Db();
        var (tenant, company, grade, plan) = await Seed(db);
        var designation = new Designation { TenantId = tenant, Code = "SPECIALIST", TitleEn = "Specialist", GradeId = grade.Id };
        db.Designations.Add(designation);
        await db.SaveChangesAsync();
        var actor = Guid.NewGuid();
        var context = new RequestContext(null, "grade-benefit-test", actor, tenant);
        var service = new EmployeeManagementService(db, new AuditService(db), new BenefitHireDocuments(), TestNotifications.For(db));
        var request = Request(company.Id, inheritDesignationGrade ? null : grade.Id) with
        {
            DesignationId = inheritDesignationGrade ? designation.Id : null,
        };
        var employee = await service.CreateAsync(tenant, request, context, default);
        var enrollment = await db.BenefitEnrollments.SingleAsync(x => x.EmployeeId == employee.Id);
        Assert.Equal(plan.Id, enrollment.BenefitPlanId);
        Assert.Equal(company.Id, enrollment.CompanyId);
        Assert.Equal(tenant, enrollment.TenantId);
        Assert.Equal(actor, enrollment.CreatedBy);
        Assert.Equal(new DateOnly(2026, 10, 8), enrollment.EffectiveFrom);
        Assert.Equal("Gold", enrollment.EntitlementTier);
        Assert.Equal(20000m, enrollment.MaximumBenefitAmount);
        Assert.Empty(await db.BenefitContributions.ToListAsync());
        Assert.Empty(await db.BenefitPayrollDeductionLinks.ToListAsync());

        // A subsequent profile save must neither recreate nor reset an individually adjusted benefit.
        enrollment.CoverageTier = "Family";
        await db.SaveChangesAsync();
        await service.UpdateAsync(tenant, employee.Id, request with { PreferredName = "Updated hire" }, context, default);
        Assert.Equal("Family", (await db.BenefitEnrollments.SingleAsync(x => x.EmployeeId == employee.Id)).CoverageTier);
    }

    [Fact]
    public async Task Import_AssignsDefaultsToNewHires_WithoutChangingExistingEnrollmentsOnRepeat()
    {
        await using var db = Db();
        var (tenant, company, grade, plan) = await Seed(db);
        var controller = HrmHierarchyTests.BuildImportControllerInternal(db, tenant);
        var csv = "EmployeeCode,FullName,CompanyLegalName,Grade,JoiningDate\nGB-IMPORT,Synthetic Benefits Hire,Benefit Company,G5,2026-10-08\n";
        Assert.IsType<OkObjectResult>(await controller.Import(new EmployeesController.ImportEmployeesRequest(csv), default));
        var employee = await db.Employees.SingleAsync(x => x.TenantId == tenant && x.EmployeeCode == "GB-IMPORT");
        var enrollment = await db.BenefitEnrollments.SingleAsync(x => x.EmployeeId == employee.Id);
        Assert.Equal(plan.Id, enrollment.BenefitPlanId);
        Assert.Equal(company.Id, enrollment.CompanyId);
        enrollment.CoverageTier = "Family";
        await db.SaveChangesAsync();
        await controller.Import(new EmployeesController.ImportEmployeesRequest(csv), default);
        Assert.Equal("Family", (await db.BenefitEnrollments.SingleAsync(x => x.EmployeeId == employee.Id)).CoverageTier);
    }

    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(Guid, Company, Grade, BenefitPlan)> Seed(ZayraDbContext db)
    {
        var tenant = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenant, Name = "Synthetic benefits", Slug = $"benefits-{tenant:N}" });
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = tenant, Plan = "Enterprise", Status = "Active", MaxEmployees = 100 });
        var company = new Company { TenantId = tenant, LegalNameEn = "Benefit Company", TradeName = "Benefit Company", CountryCode = "SA", Jurisdiction = "SA", IsActive = true };
        var grade = new Grade { TenantId = tenant, Code = "G5", Name = "Grade Five", Level = 5, IsActive = true };
        var plan = new BenefitPlan { TenantId = tenant, CompanyId = company.Id, Code = "MED-GOLD", Name = "Medical Gold", PlanType = "Medical", Currency = "SAR", EffectiveFrom = new DateOnly(2026, 1, 1) };
        db.Companies.Add(company);
        db.Grades.Add(grade);
        db.BenefitPlans.Add(plan);
        db.BenefitEligibilityRules.Add(new BenefitEligibilityRule { TenantId = tenant, CompanyId = company.Id, BenefitPlanId = plan.Id, GradeId = grade.Id, TierName = "Gold", MaxBenefitAmount = 20000m, LimitPeriod = BenefitLimitPeriods.Annual, EffectiveFrom = new DateOnly(2026, 1, 1) });
        await db.SaveChangesAsync();
        return (tenant, company, grade, plan);
    }

    private static EmployeeCreateRequest Request(Guid company, Guid? grade) => new(
        EmployeeCode: "GB-CREATE", ManualEmployeeCode: true, EnglishName: "Synthetic Benefits Hire", ArabicName: null,
        PreferredName: null, Gender: "Male", DateOfBirth: null, Nationality: "Indian", MaritalStatus: null,
        PersonalEmail: null, WorkEmail: null, MobileNumber: null, ProfilePhotoUrl: null,
        CompanyId: company, BranchId: null, DepartmentId: null, DesignationId: null, GradeId: grade,
        CostCenterId: null, JobTitle: null, ReportingManagerEmployeeId: null, SecondLevelManagerEmployeeId: null,
        EmploymentType: "Full-time", ContractType: "Unlimited", JoiningDate: new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc),
        ConfirmationDate: null, ProbationStartDate: null, ProbationEndDate: null, NoticePeriodDays: null,
        WorkLocation: null, PayrollGroup: null, ShiftPolicyCode: null, LeavePolicyCode: null,
        AttendancePolicyCode: null, PayrollProfile: null, SalaryBreakdown: null, ComplianceRecords: null);
}

file sealed class BenefitHireCommitFault(bool afterCommit) : DbTransactionInterceptor
{
    private int _armed = 1;
    public int InjectedFaults { get; private set; }
    public bool SawSavedDefaults { get; private set; }
    public Guid PreparedPublicId { get; private set; }
    public int FirstSavedEmployeeId { get; private set; }

    private void FailOnce(DbContext? context)
    {
        if (Interlocked.Exchange(ref _armed, 0) != 1) return;
        var employee = context!.ChangeTracker.Entries<Employee>().Single().Entity;
        PreparedPublicId = employee.PublicId;
        FirstSavedEmployeeId = employee.Id;
        SawSavedDefaults = context.ChangeTracker.Entries<BenefitEnrollment>().Any(x => x.State == EntityState.Unchanged);
        InjectedFaults++;
        throw new NpgsqlException("Synthetic hire COMMIT connection loss", new IOException("connection reset by peer"));
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

file sealed class RefuseBenefitSave : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context!.ChangeTracker.Entries<BenefitEnrollment>().Any(x => x.State == EntityState.Added))
            throw new InvalidOperationException("Synthetic benefit write failure");
        return ValueTask.FromResult(result);
    }
}

file sealed class BenefitHireDocuments : IDocumentStorage
{
    public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) => throw new NotSupportedException();
    public string ResolvePath(string storageUrl) => throw new NotSupportedException();
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => throw new NotSupportedException();
}
