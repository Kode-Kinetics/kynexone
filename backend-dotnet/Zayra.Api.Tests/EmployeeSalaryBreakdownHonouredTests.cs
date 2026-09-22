using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// DEFECT (P1): <c>EmployeeCreateRequest.SalaryBreakdown</c> was silently ignored unless the
/// employee happened to resolve to a GRADE. <c>UpsertEmployeeSalaryStructure</c> returned on
/// <c>employee.GradeId is null</c> BEFORE the breakdown was ever read, so the create returned 200,
/// <c>employee.Salary</c> stayed 0 and no <c>EmployeeSalaryStructure</c> row was written. A payroll
/// run over such a population reported the gross of the one person who did have a grade.
///
/// <para>Real Postgres: the defect is data-shaped — what is asserted is the rows that exist and the
/// total they add up to, which is exactly what the payroll run reads.</para>
///
/// <para>The repo rule is refuse rather than guess. Silent partial success is neither, so the fix
/// HONOURS the operator's figures rather than rejecting the request: the numbers were explicit and
/// unambiguous, and rejecting a create that has already been validated everywhere else would fail a
/// bulk onboarding on data that is perfectly good.</para>
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class EmployeeSalaryBreakdownHonouredTests
{
    private readonly PostgresFixture _fx;
    public EmployeeSalaryBreakdownHonouredTests(PostgresFixture fx) => _fx = fx;

    private static EmployeeManagementService Service(ZayraDbContext db) =>
        new(db, new AuditService(db), new NullDocumentStorage(), TestNotifications.For(db));

    private static RequestContext Ctx(Guid tenantId) =>
        new("127.0.0.1", "tests", Guid.NewGuid(), tenantId);

    /// <summary>A create request carrying nothing but a name, a joining date and a salary — the shape
    /// a bulk onboarding produces when the tenant has not set up a grade ladder.</summary>
    private static EmployeeCreateRequest Request(
        string name, EmployeeSalaryBreakdownRequest? salary, Guid? companyId = null, Guid? gradeId = null) =>
        new(
            EmployeeCode: $"SB-{Guid.NewGuid():N}"[..14],
            ManualEmployeeCode: true,
            EnglishName: name,
            ArabicName: null, PreferredName: null,
            Gender: "Male",
            DateOfBirth: null, Nationality: "Indian", MaritalStatus: null,
            PersonalEmail: null, WorkEmail: null, MobileNumber: null, ProfilePhotoUrl: null,
            CompanyId: companyId, BranchId: null, DepartmentId: null, DesignationId: null,
            GradeId: gradeId, CostCenterId: null, JobTitle: null,
            ReportingManagerEmployeeId: null, SecondLevelManagerEmployeeId: null,
            EmploymentType: "Full-Time", ContractType: "Unlimited",
            JoiningDate: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ConfirmationDate: null, ProbationStartDate: null, ProbationEndDate: null,
            NoticePeriodDays: null, WorkLocation: null, PayrollGroup: null,
            ShiftPolicyCode: null, LeavePolicyCode: null, AttendancePolicyCode: null,
            PayrollProfile: null,
            SalaryBreakdown: salary,
            ComplianceRecords: null);

    private static EmployeeSalaryBreakdownRequest Breakdown(decimal basic, decimal housing, string? currency = "SAR") =>
        new(basic, housing, null, null, null, null, null, null, null, currency);

    [Fact]
    public async Task GradelessEmployee_SalaryBreakdown_IsPersistedNotDropped()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);

        var created = await Service(db).CreateAsync(
            tenantId, Request("Gradeless Package", Breakdown(8_000m, 2_000m)), Ctx(tenantId), default);

        var stored = await db.Employees.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        stored.Salary.Should().Be(10_000m,
            "the operator supplied a 10,000 package; before the fix this stayed 0 and the create still returned 200");

        var assignment = await db.EmployeeSalaryStructures.AsNoTracking()
            .SingleAsync(x => x.TenantId == tenantId && x.EmployeeId == created.Id && x.IsActive);
        assignment.BasicSalary.Should().Be(8_000m);
        assignment.HousingAllowance.Should().Be(2_000m);
        assignment.Currency.Should().Be("SAR");

        var structure = await db.SalaryStructures.AsNoTracking().SingleAsync(x => x.Id == assignment.SalaryStructureId);
        structure.Code.Should().Be("DIRECT", "a salary entered straight onto the employee has no grade pay scale behind it");
    }

    [Fact]
    public async Task ExplicitSalaryStructureCode_IsHonouredWithoutAGrade()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);

        var created = await Service(db).CreateAsync(
            tenantId,
            Request("Named Structure", new EmployeeSalaryBreakdownRequest(
                6_000m, 1_500m, 500m, null, null, null, null,
                SalaryStructureCode: "EXEC-BAND-A", EffectiveDate: new DateOnly(2026, 1, 1), Currency: "AED")),
            Ctx(tenantId), default);

        var assignment = await db.EmployeeSalaryStructures.AsNoTracking()
            .SingleAsync(x => x.TenantId == tenantId && x.EmployeeId == created.Id && x.IsActive);
        assignment.EffectiveDate.Should().Be(new DateOnly(2026, 1, 1));
        assignment.TransportAllowance.Should().Be(500m);

        (await db.SalaryStructures.AsNoTracking().SingleAsync(x => x.Id == assignment.SalaryStructureId))
            .Code.Should().Be("EXEC-BAND-A", "an explicitly named structure code was ignored as well, for want of a grade");
    }

    [Fact]
    public async Task PayrollPopulation_GrossTotal_ReconcilesToEveryPackageNotJustTheGradedOne()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var grade = new Grade
        {
            TenantId = tenantId, Code = "G5", Name = "Grade 5", Level = 5,
            MinSalary = 1_000m, MidSalary = 10_000m, MaxSalary = 50_000m, Currency = "SAR", IsActive = true,
        };
        db.Grades.Add(grade);
        await db.SaveChangesAsync();

        var svc = Service(db);
        // One graded employee — the only one that ever worked — and thirteen without a grade.
        await svc.CreateAsync(tenantId, Request("Graded One", Breakdown(9_000m, 3_000m), gradeId: grade.Id), Ctx(tenantId), default);
        for (var i = 0; i < 13; i++)
            await svc.CreateAsync(tenantId, Request($"Gradeless {i}", Breakdown(5_000m, 1_000m)), Ctx(tenantId), default);

        var employees = await db.Employees.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted).ToListAsync();
        employees.Should().HaveCount(14);
        employees.Sum(x => x.Salary).Should().Be(12_000m + (13 * 6_000m),
            "the run's gross must reconcile to all fourteen packages — it used to report 12,000, the graded one alone");

        (await db.EmployeeSalaryStructures.CountAsync(x => x.TenantId == tenantId && x.IsActive))
            .Should().Be(14, "every employee with a package needs an assignment the payroll engine can read");
    }

    [Fact]
    public async Task NoSalaryBreakdownAndNoGrade_StillCreatesNothing()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);

        var created = await Service(db).CreateAsync(tenantId, Request("No Package Yet", salary: null), Ctx(tenantId), default);

        (await db.Employees.AsNoTracking().SingleAsync(x => x.Id == created.Id)).Salary.GetValueOrDefault()
            .Should().Be(0m, "nothing was supplied, so nothing is invented");
        (await db.EmployeeSalaryStructures.CountAsync(x => x.TenantId == tenantId))
            .Should().Be(0, "an empty salary section is the ordinary case, not a dropped value — behaviour is unchanged");
    }

    [Fact]
    public async Task GradedEmployee_StillTakesTheGradePayScaleAndItsStructureCode()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var grade = new Grade
        {
            TenantId = tenantId, Code = "G7", Name = "Grade 7", Level = 7,
            MinSalary = 0m, MidSalary = 7_000m, MaxSalary = 0m, Currency = "SAR", IsActive = true,
        };
        db.Grades.Add(grade);
        db.GradePayScaleComponents.Add(new GradePayScaleComponent
        {
            TenantId = tenantId, GradeId = grade.Id, ComponentCode = "BASIC", ComponentName = "Basic",
            ComponentType = "Earning", CalculationType = "Fixed", Amount = 7_000m, IsActive = true, SortOrder = 1,
        });
        await db.SaveChangesAsync();

        // No breakdown supplied: the grade's pay scale is still what fills the assignment.
        var created = await Service(db).CreateAsync(tenantId, Request("Graded Default", salary: null, gradeId: grade.Id), Ctx(tenantId), default);

        var assignment = await db.EmployeeSalaryStructures.AsNoTracking()
            .SingleAsync(x => x.TenantId == tenantId && x.EmployeeId == created.Id && x.IsActive);
        assignment.BasicSalary.Should().Be(7_000m);
        (await db.SalaryStructures.AsNoTracking().SingleAsync(x => x.Id == assignment.SalaryStructureId))
            .Code.Should().Be("GRADE-G7", "the grade path is untouched");
    }
}
