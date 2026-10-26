using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Employees.Onboarding;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Employees.Onboarding;
using Zayra.Api.Models;
using Xunit;

namespace Zayra.Api.Tests.Onboarding;

public class EmployeeReferenceValidatorTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class Fx
    {
        public Guid Tenant = Guid.NewGuid();
        public Guid OtherTenant = Guid.NewGuid();
        public Company Company = null!, SisterCompany = null!, ForeignCompany = null!;
        public Branch Branch = null!;
        public Department Dept = null!;
        public Designation Desig = null!;
        public Grade Grade = null!;
        public CostCenter Cc = null!;
        public Employee Manager = null!;
    }

    private static Company NewCompany(Guid tenant, string name) => new()
    {
        TenantId = tenant, LegalNameEn = name, TradeName = name, CountryCode = "SA", Jurisdiction = "t",
        RegistrationNumber = "RC-" + Guid.NewGuid().ToString("N"), DefaultCurrency = "SAR", IsActive = true, CreatedAtUtc = DateTime.UtcNow,
    };

    private static async Task<Fx> Seed(ZayraDbContext db)
    {
        var fx = new Fx();
        fx.Company = NewCompany(fx.Tenant, "Main"); fx.SisterCompany = NewCompany(fx.Tenant, "Sister"); fx.ForeignCompany = NewCompany(fx.OtherTenant, "Foreign");
        db.Companies.AddRange(fx.Company, fx.SisterCompany, fx.ForeignCompany);
        fx.Branch = new Branch { TenantId = fx.Tenant, CompanyId = fx.Company.Id, NameEn = "HQ", IsActive = true };
        fx.Dept = new Department { TenantId = fx.Tenant, CompanyId = fx.Company.Id, NameEn = "Ops", IsActive = true };
        fx.Grade = new Grade { TenantId = fx.Tenant, CompanyId = fx.Company.Id, Name = "G5", MinSalary = 5000, MaxSalary = 9000, IsActive = true };
        fx.Desig = new Designation { TenantId = fx.Tenant, CompanyId = fx.Company.Id, TitleEn = "Analyst", GradeId = fx.Grade.Id, IsActive = true };
        fx.Cc = new CostCenter { TenantId = fx.Tenant, CompanyId = fx.Company.Id, Name = "CC1", IsActive = true };
        fx.Manager = new Employee { TenantId = fx.Tenant, CompanyId = fx.Company.Id, EmployeeCode = "M1", FullName = "Boss", EnglishName = "Boss", Status = "Active" };
        db.AddRange(fx.Branch, fx.Dept, fx.Grade, fx.Desig, fx.Cc, fx.Manager);
        await db.SaveChangesAsync();
        return fx;
    }

    private static EmployeeCreateRequest Req(Fx fx, Guid? company = null) =>
        EmployeeInputValidatorTests.Req() with { CompanyId = company ?? fx.Company.Id };

    private static EntityScopeContext Group => EntityScopeContext.GroupLevel;

    private static async Task<EmployeeValidationException> Refused(Func<Task> act)
    {
        var ex = await Assert.ThrowsAsync<EmployeeValidationException>(act);
        ex.Status.Should().Be(422);
        return ex;
    }

    [Fact]
    public async Task ReferenceValidator_AcceptsValidReferences_AndReturnsCompany()
    {
        await using var db = CreateDb(); var fx = await Seed(db);
        var r = Req(fx) with { BranchId = fx.Branch.Id, DepartmentId = fx.Dept.Id, DesignationId = fx.Desig.Id, GradeId = fx.Grade.Id, CostCenterId = fx.Cc.Id, ReportingManagerEmployeeId = fx.Manager.Id };
        var res = await new EmployeeReferenceValidator(db).ValidateAsync(fx.Tenant, r, Group, null, default);
        res.Company.Id.Should().Be(fx.Company.Id);
    }

    [Fact]
    public async Task ReferenceValidator_RejectsForeignTenantCompany()
    {
        await using var db = CreateDb(); var fx = await Seed(db);
        var ex = await Refused(() => new EmployeeReferenceValidator(db).ValidateAsync(fx.Tenant, Req(fx, fx.ForeignCompany.Id), Group, null, default));
        ex.Code.Should().Be(EmployeeProblemCodes.InvalidCompany);
        // A random GUID gets the identical outcome (no existence oracle).
        var ex2 = await Refused(() => new EmployeeReferenceValidator(db).ValidateAsync(fx.Tenant, Req(fx, Guid.NewGuid()), Group, null, default));
        ex2.Code.Should().Be(ex.Code); ex2.Detail.Should().Be(ex.Detail);
    }

    [Fact]
    public async Task ReferenceValidator_RejectsMissingCompany()
    {
        await using var db = CreateDb(); var fx = await Seed(db);
        var ex = await Refused(() => new EmployeeReferenceValidator(db).ValidateAsync(fx.Tenant, Req(fx) with { CompanyId = null }, Group, null, default));
        ex.Code.Should().Be(EmployeeProblemCodes.InvalidCompany);
    }

    [Fact]
    public async Task ReferenceValidator_RejectsInactiveBranchDepartmentCostCenter()
    {
        foreach (var field in new[] { "branchId", "departmentId", "costCenterId", "designationId", "gradeId" })
        {
            await using var db = CreateDb(); var fx = await Seed(db);
            var r = Req(fx);
            switch (field)
            {
                case "branchId": fx.Branch.IsActive = false; r = r with { BranchId = fx.Branch.Id }; break;
                case "departmentId": fx.Dept.IsDeleted = true; r = r with { DepartmentId = fx.Dept.Id }; break;
                case "costCenterId": fx.Cc.IsActive = false; r = r with { CostCenterId = fx.Cc.Id }; break;
                case "designationId": fx.Desig.IsActive = false; r = r with { DesignationId = fx.Desig.Id }; break;
                default: fx.Grade.IsActive = false; r = r with { GradeId = fx.Grade.Id }; break;
            }
            await db.SaveChangesAsync();
            var ex = await Refused(() => new EmployeeReferenceValidator(db).ValidateAsync(fx.Tenant, r, Group, null, default));
            ex.Code.Should().Be(EmployeeProblemCodes.InvalidReference); ex.Field.Should().Be(field);
        }
    }

    [Fact]
    public async Task ReferenceValidator_RejectsBranchOfAnotherCompany_ButAllowsTenantWideRow()
    {
        await using var db = CreateDb(); var fx = await Seed(db);
        var sisterBranch = new Branch { TenantId = fx.Tenant, CompanyId = fx.SisterCompany.Id, NameEn = "Sis", IsActive = true };
        var tenantWideDept = new Department { TenantId = fx.Tenant, CompanyId = null, NameEn = "Shared", IsActive = true };
        db.AddRange(sisterBranch, tenantWideDept); await db.SaveChangesAsync();
        var v = new EmployeeReferenceValidator(db);
        (await Refused(() => v.ValidateAsync(fx.Tenant, Req(fx) with { BranchId = sisterBranch.Id }, Group, null, default))).Field.Should().Be("branchId");
        await v.ValidateAsync(fx.Tenant, Req(fx) with { DepartmentId = tenantWideDept.Id }, Group, null, default);
    }

    [Fact]
    public async Task ReferenceValidator_RejectsManagerFromOtherTenant()
    {
        await using var db = CreateDb(); var fx = await Seed(db);
        var foreign = new Employee { TenantId = fx.OtherTenant, CompanyId = fx.ForeignCompany.Id, EmployeeCode = "F1", FullName = "F", EnglishName = "F", Status = "Active" };
        db.Employees.Add(foreign); await db.SaveChangesAsync();
        var ex = await Refused(() => new EmployeeReferenceValidator(db).ValidateAsync(fx.Tenant, Req(fx) with { ReportingManagerEmployeeId = foreign.Id }, Group, null, default));
        ex.Field.Should().Be("reportingManagerEmployeeId");
    }

    [Fact]
    public async Task ReferenceValidator_RejectsDraftOrTerminatedManager()
    {
        foreach (var status in new[] { "Draft", "Terminated", "Exited", "Archived", "Offboarded" })
        {
            await using var db = CreateDb(); var fx = await Seed(db);
            fx.Manager.Status = status; await db.SaveChangesAsync();
            await Refused(() => new EmployeeReferenceValidator(db).ValidateAsync(fx.Tenant, Req(fx) with { SecondLevelManagerEmployeeId = fx.Manager.Id }, Group, null, default));
        }
    }

    [Fact]
    public async Task ReferenceValidator_RejectsSelfAndCycleManager_AndSameTwoManagers()
    {
        await using var db = CreateDb(); var fx = await Seed(db);
        var sub = new Employee { TenantId = fx.Tenant, CompanyId = fx.Company.Id, EmployeeCode = "S1", FullName = "Sub", EnglishName = "Sub", Status = "Active", ManagerEmployeeId = fx.Manager.Id };
        db.Employees.Add(sub); await db.SaveChangesAsync();
        var v = new EmployeeReferenceValidator(db);
        // self
        await Refused(() => v.ValidateAsync(fx.Tenant, Req(fx) with { ReportingManagerEmployeeId = sub.Id }, Group, selfEmployeeId: sub.Id, default));
        // cycle: making the manager report to his own subordinate
        await Refused(() => v.ValidateAsync(fx.Tenant, Req(fx) with { ReportingManagerEmployeeId = sub.Id }, Group, selfEmployeeId: fx.Manager.Id, default));
        // the two managers must be distinct
        await Refused(() => v.ValidateAsync(fx.Tenant, Req(fx) with { ReportingManagerEmployeeId = fx.Manager.Id, SecondLevelManagerEmployeeId = fx.Manager.Id }, Group, null, default));
    }

    [Fact]
    public async Task ReferenceValidator_SameTextForMissingAndOutOfScope()
    {
        await using var db = CreateDb(); var fx = await Seed(db);
        var narrow = EntityScopeContext.ForCompanies([fx.Company.Id]);
        var v = new EmployeeReferenceValidator(db);
        var sisterBranch = new Branch { TenantId = fx.Tenant, CompanyId = fx.SisterCompany.Id, NameEn = "Sis", IsActive = true };
        db.Branches.Add(sisterBranch); await db.SaveChangesAsync();
        var missing = await Refused(() => v.ValidateAsync(fx.Tenant, Req(fx) with { BranchId = Guid.NewGuid() }, narrow, null, default));
        var wrongCompany = await Refused(() => v.ValidateAsync(fx.Tenant, Req(fx) with { BranchId = sisterBranch.Id }, narrow, null, default));
        wrongCompany.Detail.Should().Be(missing.Detail);
        // A company outside the caller's scope reads exactly like a missing one.
        var outOfScope = await Refused(() => v.ValidateAsync(fx.Tenant, Req(fx, fx.SisterCompany.Id), narrow, null, default));
        var absent = await Refused(() => v.ValidateAsync(fx.Tenant, Req(fx, Guid.NewGuid()), narrow, null, default));
        outOfScope.Detail.Should().Be(absent.Detail);
        // Same for a manager outside the caller's employee scope.
        var scopedManager = await Refused(() => v.ValidateAsync(fx.Tenant, Req(fx) with { ReportingManagerEmployeeId = fx.Manager.Id }, narrow, null, default, canAccessEmployee: _ => false));
        var absentManager = await Refused(() => v.ValidateAsync(fx.Tenant, Req(fx) with { ReportingManagerEmployeeId = 987654 }, narrow, null, default));
        scopedManager.Detail.Should().Be(absentManager.Detail);
    }

    [Fact]
    public async Task Create_DesignationGradeMismatch_422()   // G02
    {
        await using var db = CreateDb(); var fx = await Seed(db);
        var other = new Grade { TenantId = fx.Tenant, CompanyId = fx.Company.Id, Name = "G9", MinSalary = 1, MaxSalary = 2, IsActive = true };
        db.Grades.Add(other); await db.SaveChangesAsync();
        var ex = await Refused(() => new EmployeeReferenceValidator(db).ValidateAsync(fx.Tenant,
            Req(fx) with { DesignationId = fx.Desig.Id, GradeId = other.Id }, Group, null, default));
        ex.Field.Should().Be("gradeId");
    }

    [Fact]
    public async Task SalaryOutsideGradeBand_Warns_grade_band_out_of_range()   // G08
    {
        await using var db = CreateDb(); var fx = await Seed(db);
        var v = new EmployeeReferenceValidator(db);
        var inBand = await v.ValidateAsync(fx.Tenant, Req(fx) with { GradeId = fx.Grade.Id, SalaryBreakdown = new(6000, 0, 0, 0, 0, 0, 0, null, null, "SAR") }, Group, null, default);
        inBand.Warnings.Should().BeEmpty();
        var outBand = await v.ValidateAsync(fx.Tenant, Req(fx) with { GradeId = fx.Grade.Id, SalaryBreakdown = new(12000, 0, 0, 0, 0, 0, 0, null, null, "SAR") }, Group, null, default);
        outBand.Warnings.Select(w => w.Code).Should().Contain(EmployeeWarningCodes.GradeBandOutOfRange);
    }
}
