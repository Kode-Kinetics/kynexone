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
        // The structure exists (Payroll › Salary structures, group-wide). Naming it is a choice, not a request to create one.
        var named = new SalaryStructure { TenantId = tenantId, Code = "EXEC-BAND-A", Name = "Executive band A", Currency = "AED", EffectiveDate = new DateOnly(2025, 1, 1) };
        db.SalaryStructures.Add(named);
        await db.SaveChangesAsync();

        var created = await Service(db).CreateAsync(
            tenantId,
            Request("Named Structure", new EmployeeSalaryBreakdownRequest(
                6_000m, 1_500m, 500m, null, null, null, null,
                SalaryStructureCode: "EXEC-BAND-A", EffectiveDate: new DateOnly(2026, 1, 1), Currency: "AED")) with
            {
                PayrollProfile = new EmployeePayrollProfileRequest(null, null, null, "Cash", "AED", null, null, true, true, null, null, null),
            },
            Ctx(tenantId), default);

        var assignment = await db.EmployeeSalaryStructures.AsNoTracking()
            .SingleAsync(x => x.TenantId == tenantId && x.EmployeeId == created.Id && x.IsActive);
        assignment.EffectiveDate.Should().Be(new DateOnly(2026, 1, 1));
        assignment.TransportAllowance.Should().Be(500m);
        assignment.SalaryStructureId.Should().Be(named.Id, "an explicitly named structure code was ignored as well, for want of a grade");
        (await db.SalaryStructures.CountAsync(x => x.TenantId == tenantId)).Should().Be(1, "the existing structure is used, never duplicated");
        (await db.EmployeePayrollProfiles.AsNoTracking().SingleAsync(x => x.EmployeeId == created.Id)).SalaryStructureReference
            .Should().Be("EXEC-BAND-A", "the payroll profile's structure reference is derived from the chosen structure");
    }

    /// <summary>
    /// DATA SAFETY: an explicit structure code that matches nothing used to be silently CREATED as a new, empty
    /// structure — a typo ("EXEC-BNAD-A") or another company's code became a real structure the employee was paid on.
    /// It is now refused before anything is saved; only the blank → DIRECT / GRADE-x default may still be created.
    /// </summary>
    [Fact]
    public async Task UnknownExplicitSalaryStructureCode_IsRefused_AndNothingIsCreated()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var name = $"Typo {Guid.NewGuid():N}";

        var act = () => Service(db).CreateAsync(
            tenantId,
            Request(name, new EmployeeSalaryBreakdownRequest(
                6_000m, 1_500m, null, null, null, null, null,
                SalaryStructureCode: "EXEC-BNAD-A", EffectiveDate: new DateOnly(2026, 1, 1), Currency: "AED")),
            Ctx(tenantId), default);

        (await act.Should().ThrowAsync<UnknownSalaryStructureException>()).Which.StructureCode.Should().Be("EXEC-BNAD-A");
        await using var verify = _fx.CreateDb();
        (await verify.SalaryStructures.CountAsync(x => x.TenantId == tenantId)).Should().Be(0, "no structure may be invented from a typo");
        (await verify.Employees.CountAsync(x => x.TenantId == tenantId && x.EnglishName == name)).Should().Be(0, "the refusal happens before the employee is saved");
        (await verify.EmployeeSalaryStructures.CountAsync(x => x.TenantId == tenantId)).Should().Be(0);
    }

    [Fact]
    public async Task AnotherCompanysStructure_IsNotKnownToThisEmployee()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        Company Co(string name) => new()
        {
            TenantId = tenantId, LegalNameEn = name, CountryCode = "SAU", Jurisdiction = "KSA-mainland",
            RegistrationNumber = $"SB-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true,
        };
        var (companyA, companyB) = (Co("Company A"), Co("Company B"));
        db.Companies.AddRange(companyA, companyB);
        await db.SaveChangesAsync();
        db.SalaryStructures.Add(new SalaryStructure { TenantId = tenantId, CompanyId = companyA.Id, Code = "A-ONLY", Name = "Company A band", Currency = "SAR", EffectiveDate = new DateOnly(2025, 1, 1) });
        await db.SaveChangesAsync();

        var act = () => Service(db).CreateAsync(tenantId,
            Request($"Company B hire {Guid.NewGuid():N}", Breakdown(5_000m, 1_000m) with { SalaryStructureCode = "A-ONLY" }, companyId: companyB.Id),
            Ctx(tenantId), default);

        await act.Should().ThrowAsync<UnknownSalaryStructureException>();
        (await db.SalaryStructures.CountAsync(x => x.TenantId == tenantId)).Should().Be(1, "company B's hire must not clone company A's structure");
    }

    /// <summary>
    /// The pre-save structure check used to run before the designation filled in the grade, so for a hire whose grade
    /// comes from the designation the "default" was computed as DIRECT and the grade's own default code was refused.
    /// </summary>
    [Fact]
    public async Task ADesignationSuppliedGrade_DecidesTheDefaultStructure()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var grade = new Grade { TenantId = tenantId, Code = "G7", Name = "Grade 7", Level = 7, MinSalary = 1_000m, MidSalary = 10_000m, MaxSalary = 50_000m, Currency = "SAR", IsActive = true };
        db.Grades.Add(grade);
        await db.SaveChangesAsync();
        var designation = new Designation { TenantId = tenantId, Code = "ANALYST", TitleEn = "Analyst", GradeId = grade.Id, IsActive = true };
        db.Designations.Add(designation);
        await db.SaveChangesAsync();

        var created = await Service(db).CreateAsync(tenantId,
            Request("Designation Grade", Breakdown(6_000m, 1_000m) with { SalaryStructureCode = "GRADE-G7" }) with { DesignationId = designation.Id },
            Ctx(tenantId), default);

        var assignment = await db.EmployeeSalaryStructures.AsNoTracking().SingleAsync(x => x.EmployeeId == created.Id && x.IsActive);
        (await db.SalaryStructures.AsNoTracking().SingleAsync(x => x.Id == assignment.SalaryStructureId)).Code.Should().Be("GRADE-G7");
    }

    [Fact]
    public async Task AnInactiveNamedStructure_IsRefusedOnCreate_LikeOnAChangeRequest()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        db.SalaryStructures.Add(new SalaryStructure { TenantId = tenantId, Code = "RETIRED-BAND", Name = "Retired band", Currency = "SAR", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = false });
        await db.SaveChangesAsync();
        var name = $"Retired band hire {Guid.NewGuid():N}";

        var act = () => Service(db).CreateAsync(tenantId, Request(name, Breakdown(5_000m, 1_000m) with { SalaryStructureCode = "RETIRED-BAND" }), Ctx(tenantId), default);

        (await act.Should().ThrowAsync<InactiveSalaryStructureException>()).Which.ErrorCode.Should().Be("inactive_salary_structure");
        await using var verify = _fx.CreateDb();
        (await verify.Employees.CountAsync(x => x.TenantId == tenantId && x.EnglishName == name)).Should().Be(0);
        (await verify.EmployeeSalaryStructures.CountAsync(x => x.TenantId == tenantId)).Should().Be(0);
    }

    /// <summary>Editing a structure that employees are on supersedes it (old row inactive, new version "DIRECT-V2").
    /// The blank default follows that chain to the active version instead of refusing every gradeless hire.</summary>
    [Fact]
    public async Task TheBlankDefault_FollowsASupersededDefaultToItsActiveVersion()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var old = new SalaryStructure { TenantId = tenantId, Code = "DIRECT", Name = "Direct", Currency = "SAR", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = false };
        db.SalaryStructures.Add(old);
        await db.SaveChangesAsync();
        var current = new SalaryStructure { TenantId = tenantId, Code = "DIRECT-V2", Name = "Direct v2", Currency = "SAR", EffectiveDate = new DateOnly(2026, 1, 1), VersionNumber = 2, PreviousVersionId = old.Id, IsActive = true };
        db.SalaryStructures.Add(current);
        await db.SaveChangesAsync();

        var created = await Service(db).CreateAsync(tenantId, Request("Superseded Default", Breakdown(5_000m, 1_000m)), Ctx(tenantId), default);

        (await db.EmployeeSalaryStructures.AsNoTracking().SingleAsync(x => x.EmployeeId == created.Id && x.IsActive)).SalaryStructureId.Should().Be(current.Id);
        (await db.SalaryStructures.CountAsync(x => x.TenantId == tenantId)).Should().Be(2, "no third DIRECT structure is invented");
    }

    /// <summary>The writer is also reached from approvals, draft activation and merit: it refuses on its own,
    /// before deactivating the salary row it would have replaced.</summary>
    [Fact]
    public async Task TheWriter_RefusesAnUnknownCode_WithoutTouchingTheCurrentSalary()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var created = await Service(db).CreateAsync(tenantId, Request("Writer Backstop", Breakdown(7_000m, 1_000m)), Ctx(tenantId), default);
        var employee = await db.Employees.SingleAsync(x => x.Id == created.Id);
        var effective = DateOnly.FromDateTime(employee.JoiningDate);

        var act = () => EmployeeSalaryBreakdownWriter.ApplyAsync(db, employee,
            new EmployeeSalaryBreakdownRequest(9_000m, 1_000m, 0, 0, 0, 0, 0, "NOT-A-STRUCTURE", effective, "SAR"), Ctx(tenantId), default);
        await act.Should().ThrowAsync<UnknownSalaryStructureException>();

        db.ChangeTracker.Entries<SalaryStructure>().Should().NotContain(e => e.State == EntityState.Added);
        db.ChangeTracker.Entries<EmployeeSalaryStructure>().Should().NotContain(e => e.State == EntityState.Added || e.State == EntityState.Modified,
            "the refusal comes before the same-date row is deactivated");
        await db.SaveChangesAsync();
        (await db.EmployeeSalaryStructures.AsNoTracking().SingleAsync(x => x.EmployeeId == created.Id && x.IsActive)).BasicSalary.Should().Be(7_000m);

        // The employee's own default code is still the safe path, explicit or blank.
        await EmployeeSalaryBreakdownWriter.ApplyAsync(db, employee,
            new EmployeeSalaryBreakdownRequest(9_000m, 1_000m, 0, 0, 0, 0, 0, "DIRECT", effective, "SAR"), Ctx(tenantId), default);
        await db.SaveChangesAsync();
        (await db.EmployeeSalaryStructures.AsNoTracking().SingleAsync(x => x.EmployeeId == created.Id && x.IsActive)).BasicSalary.Should().Be(9_000m);
        (await db.SalaryStructures.CountAsync(x => x.TenantId == tenantId)).Should().Be(1);
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

    /// <summary>A grade whose default structure ("GRADE-G9") was retired with no active successor, and no pay scale.</summary>
    private static async Task<Grade> RetiredDefaultGradeAsync(ZayraDbContext db, Guid tenantId)
    {
        var grade = new Grade { TenantId = tenantId, Code = "G9", Name = "Grade 9", Level = 9, MinSalary = 0m, MidSalary = 0m, MaxSalary = 0m, Currency = "SAR", IsActive = true };
        db.Grades.Add(grade);
        db.SalaryStructures.Add(new SalaryStructure { TenantId = tenantId, Code = "GRADE-G9", Name = "Grade 9 (retired)", Currency = "SAR", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = false });
        await db.SaveChangesAsync();
        return grade;
    }

    [Fact]
    public async Task ACreateWithNoSalary_IsNotRefusedByARetiredGradeDefault()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var grade = await RetiredDefaultGradeAsync(db, tenantId);

        var created = await Service(db).CreateAsync(tenantId, Request($"No salary yet {Guid.NewGuid():N}", salary: null, gradeId: grade.Id), Ctx(tenantId), default);

        created.Id.Should().BePositive();
        (await db.EmployeeSalaryStructures.CountAsync(x => x.TenantId == tenantId)).Should().Be(0, "no salary, so no structure is used");
    }

    [Fact]
    public async Task AnUnrelatedUpdateOmittingTheSalary_IsNotRefusedByARetiredGradeDefault()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var grade = await RetiredDefaultGradeAsync(db, tenantId);
        var name = $"Unrelated edit {Guid.NewGuid():N}";
        var created = await Service(db).CreateAsync(tenantId, Request(name, salary: null, gradeId: grade.Id), Ctx(tenantId), default);

        var updated = await Service(db).UpdateAsync(tenantId, created.Id, Request(name, salary: null, gradeId: grade.Id) with { JobTitle = "Senior analyst" }, Ctx(tenantId), default);

        updated.Should().NotBeNull();
        (await db.Employees.AsNoTracking().SingleAsync(x => x.Id == created.Id)).JobTitle.Should().Be("Senior analyst");
    }

    [Fact]
    public async Task APositiveSalary_IsStillRefusedOnARetiredGradeDefault()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var grade = await RetiredDefaultGradeAsync(db, tenantId);

        var act = () => Service(db).CreateAsync(tenantId, Request($"Paid hire {Guid.NewGuid():N}", Breakdown(5_000m, 1_000m), gradeId: grade.Id), Ctx(tenantId), default);

        (await act.Should().ThrowAsync<InactiveSalaryStructureException>()).Which.ErrorCode.Should().Be("inactive_salary_structure");
    }

    /// <summary>A grade with a fixed basic, a percent housing, a fixed transport, and two lines that are not monthly cash.</summary>
    private static async Task<Grade> PackageGradeAsync(ZayraDbContext db, Guid tenantId, string code, decimal maxSalary = 0m)
    {
        var grade = new Grade { TenantId = tenantId, Code = code, Name = $"Grade {code}", Level = 3, MinSalary = 0m, MidSalary = 4_000m, MaxSalary = maxSalary, Currency = "SAR", IsActive = true };
        db.Grades.Add(grade);
        GradePayScaleComponent Line(string componentCode, int order, decimal amount = 0m, decimal percentage = 0m, string type = "Earning", string calculation = "Fixed", string frequency = "Monthly") =>
            new() { TenantId = tenantId, GradeId = grade.Id, ComponentCode = componentCode, ComponentName = componentCode, ComponentType = type,
                CalculationType = calculation, Amount = amount, Percentage = percentage, Frequency = frequency, IsActive = true, SortOrder = order };
        db.GradePayScaleComponents.AddRange(
            Line("BASIC", 1, amount: 4_000m),
            Line("HOUSING", 2, percentage: 25m, calculation: "PercentOfBasic"),
            Line("TRANSPORT", 3, amount: 500m),
            Line("MEDICAL", 4, amount: 300m, type: "Benefit"),
            Line("TICKET", 5, amount: 2_400m, frequency: "Annual"));
        await db.SaveChangesAsync();
        return grade;
    }

    /// <summary>
    /// DEFECT (client demo): typing the basic dropped the grade's whole pay scale, so the hire was paid basic only. Every
    /// blank line is now filled from the grade, the percent housing from the typed basic, and the Benefit and Annual lines
    /// stay out of the monthly salary payroll reads.
    /// </summary>
    [Fact]
    public async Task ATypedBasic_OnAGradedHire_IsPaidTheGradesWholeMonthlyPackage()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var grade = await PackageGradeAsync(db, tenantId, "G3");

        var created = await Service(db).CreateAsync(tenantId, Request("Typed Basic", new EmployeeSalaryBreakdownRequest(
            4_800m, null, null, null, null, null, null, null, null, "SAR"), gradeId: grade.Id), Ctx(tenantId), default);

        var assignment = await db.EmployeeSalaryStructures.AsNoTracking()
            .SingleAsync(x => x.TenantId == tenantId && x.EmployeeId == created.Id && x.IsActive);
        (assignment.BasicSalary, assignment.HousingAllowance, assignment.TransportAllowance).Should().Be((4_800m, 1_200m, 500m));
        (assignment.FoodAllowance, assignment.MobileAllowance, assignment.OtherAllowance, assignment.FixedDeduction)
            .Should().Be((0m, 0m, 0m, 0m), "medical cover (Benefit) and the yearly ticket (Annual) are not monthly cash");
        (await db.Employees.AsNoTracking().SingleAsync(x => x.Id == created.Id)).Salary.Should().Be(6_500m);
    }

    [Fact]
    public async Task AnExplicitZeroAllowance_IsKept_OnAGradedHire()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var grade = await PackageGradeAsync(db, tenantId, "G4");

        var created = await Service(db).CreateAsync(tenantId, Request("Housing In Kind", new EmployeeSalaryBreakdownRequest(
            4_800m, 0m, null, null, null, null, null, null, null, "SAR"), gradeId: grade.Id), Ctx(tenantId), default);

        var assignment = await db.EmployeeSalaryStructures.AsNoTracking()
            .SingleAsync(x => x.TenantId == tenantId && x.EmployeeId == created.Id && x.IsActive);
        (assignment.HousingAllowance, assignment.TransportAllowance).Should().Be((0m, 500m), "0 is an answer, a blank is not");
    }

    /// <summary>The grade's range is checked against the package that is paid — the merged one — not the typed basic alone.</summary>
    [Fact]
    public async Task TheGradeRange_IsCheckedAgainstTheMergedPackage()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var grade = await PackageGradeAsync(db, tenantId, "G5", maxSalary: 6_000m);

        var act = () => Service(db).CreateAsync(tenantId, Request("Over Range", new EmployeeSalaryBreakdownRequest(
            4_800m, null, null, null, null, null, null, null, null, "SAR"), gradeId: grade.Id), Ctx(tenantId), default);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("exceeds grade G5 maximum");
    }

    /// <summary>
    /// A grade change on an EXISTING employee must not silently rewrite their pay: an update that does not send a salary
    /// writes none (wage changes go through the approved salary-change path). It used to rebuild the salary from the new
    /// grade's pay scale on any such save.
    /// </summary>
    [Fact]
    public async Task AGradeChangeOnAnExistingEmployee_DoesNotRewriteTheirPay()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var name = $"Regraded {Guid.NewGuid():N}";
        var created = await Service(db).CreateAsync(tenantId, Request(name, Breakdown(7_000m, 1_000m)), Ctx(tenantId), default);
        var newGrade = await PackageGradeAsync(db, tenantId, "G6");

        await Service(db).UpdateAsync(tenantId, created.Id, Request(name, salary: null, gradeId: newGrade.Id), Ctx(tenantId), default);

        var active = await db.EmployeeSalaryStructures.AsNoTracking().Where(x => x.TenantId == tenantId && x.EmployeeId == created.Id && x.IsActive).ToListAsync();
        active.Should().ContainSingle().Which.BasicSalary.Should().Be(7_000m, "the pay agreed at hire stands until an approved salary change replaces it");
        (await db.Employees.AsNoTracking().SingleAsync(x => x.Id == created.Id)).Salary.Should().Be(8_000m);
    }
}
