using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Organization;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The grade is the one place a pay package is defined (Setup → Grades). Its lines pick components from the company's pay
/// component catalogue, and the grade's salary structure (GRADE-{code}) is derived from it on save — updated in place while
/// no employee is on it, versioned once one is. Release A tenants keep Benefits by grade and are untouched.
/// </summary>
public class GradePackageTests
{
    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed record World(ZayraDbContext Db, Guid Tenant, Company A, Company B, Grade Grade, GradesController Grades);

    private static async Task<World> Seed(bool releaseA = false)
    {
        var db = Db();
        var tenant = Guid.NewGuid();
        var a = new Company { TenantId = tenant, LegalNameEn = "Alpha Trading", RegistrationNumber = "A-1", DefaultCurrency = "SAR", IsActive = true };
        var b = new Company { TenantId = tenant, LegalNameEn = "Beta Contracting", RegistrationNumber = "B-1", DefaultCurrency = "SAR", IsActive = true };
        var grade = new Grade { TenantId = tenant, CompanyId = a.Id, Code = "G5", Name = "Professional 5", Level = 5, MinSalary = 10_000m, MidSalary = 15_000m, MaxSalary = 20_000m, Currency = "SAR" };
        db.AddRange(a, b, grade);
        if (releaseA) db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = tenant, FeatureKey = FeatureKeys.ReleaseA, IsEnabled = true });
        await db.SaveChangesAsync();
        return new World(db, tenant, a, b, grade, Controller(db, tenant));
    }

    private static GradesController Controller(ZayraDbContext db, Guid tenant)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenant.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, "HR Manager"), new("is_group_scope", "true"),
        };
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        return new GradesController(new OrganizationSetupService(db, new AuditService(db)), db) { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private static GradePayScaleComponentRequest Line(string code, decimal amount = 0m, decimal percent = 0m) =>
        new(code, null, CalculationType: percent > 0 ? "PercentOfBasic" : "Fixed", Amount: amount, Percentage: percent);

    private static Task<SalaryStructure> Structure(World w, string code) =>
        w.Db.SalaryStructures.SingleAsync(s => s.TenantId == w.Tenant && s.Code == code);

    private static Task<List<SalaryComponent>> Components(World w, Guid structureId) =>
        w.Db.SalaryComponents.Where(c => c.TenantId == w.Tenant && c.SalaryStructureId == structureId).OrderBy(c => c.Code).ToListAsync();

    private static object? Prop(IActionResult result, string name)
    {
        var value = ((ObjectResult)result).Value!;
        return value.GetType().GetProperty(name)!.GetValue(value);
    }

    [Fact]
    public async Task SavingThePackage_CreatesTheGradeStructure_FromTheCatalogue_AndIgnoresBlankRows()
    {
        var w = await Seed();

        var result = await w.Grades.SetPayScale(w.Grade.Id,
            [Line("BASIC", 12_000m), Line("housing", percent: 25m), new GradePayScaleComponentRequest("  ", "unfinished row"), Line("TRANSPORT", 800m)], default);

        result.Should().BeOfType<OkObjectResult>();
        Prop(result, "count").Should().Be(3);
        var lines = await w.Db.GradePayScaleComponents.Where(c => c.GradeId == w.Grade.Id).OrderBy(c => c.SortOrder).ToListAsync();
        lines.Select(l => l.ComponentCode).Should().Equal("BASIC", "HOUSING", "TRANSPORT");
        lines.Select(l => l.ComponentName).Should().Equal("Basic salary", "Housing allowance", "Transport allowance");
        lines.Single(l => l.ComponentCode == "BASIC").IsTaxable.Should().BeTrue("taxable comes from the catalogue, not the editor");
        lines.Single(l => l.ComponentCode == "HOUSING").Should().Match<GradePayScaleComponent>(l => l.CalculationType == "PercentOfBasic" && l.Percentage == 25m && l.Amount == 0m);

        var structure = await Structure(w, "GRADE-G5");
        structure.CompanyId.Should().Be(w.A.Id);
        structure.Name.Should().Be("Professional 5 salary structure");
        structure.Currency.Should().Be("SAR");
        (structure.MinGrossSalary, structure.MaxGrossSalary).Should().Be((10_000m, 20_000m));
        structure.VersionNumber.Should().Be(1);
        var components = await Components(w, structure.Id);
        components.Select(c => c.Code).Should().Equal("BASIC", "HOUSING", "TRANSPORT");
        components.Single(c => c.Code == "HOUSING").Should().Match<SalaryComponent>(c => c.CalculationType == "Percentage" && c.Percentage == 25m,
            "payroll's taxable base reads Percentage, so the derived line says so");
        components.Single(c => c.Code == "BASIC").IsTaxable.Should().BeTrue();
    }

    [Fact]
    public async Task TheDerivedStructure_HoldsOnlyMonthlySalaryLines_SoAnAnnualOrInKindLineIsNeverTaxedMonthly()
    {
        var w = await Seed();
        GradePayScaleComponent L(string code, string type, decimal amount, string frequency = "Monthly") => new()
        {
            TenantId = w.Tenant, GradeId = w.Grade.Id, ComponentCode = code, ComponentName = code, ComponentType = type,
            CalculationType = "Fixed", Amount = amount, Frequency = frequency, IsTaxable = true, IsActive = true,
        };

        var structure = await GradeSalaryStructureSync.StageAsync(w.Db, w.Grade,
            [L("BASIC", "Earning", 12_000m), L("BONUS_ANNUAL", "Earning", 24_000m, "Annual"), L("MEDICAL", "Benefit", 900m),
             L("FIXED_DEDUCTION", "Deduction", 150m)], null, createIfMissing: true, default);
        await w.Db.SaveChangesAsync();

        // Payroll taxes every taxable component of the structure on every regular run: the yearly bonus and the in-kind
        // medical cover must not be among them.
        (await Components(w, structure!.Id)).Select(c => c.Code).Should().Equal("BASIC", "FIXED_DEDUCTION");
    }

    [Fact]
    public async Task ACodeOutsideTheCatalogue_IsRefused_AndNothingIsWritten()
    {
        var w = await Seed();

        var result = await w.Grades.SetPayScale(w.Grade.Id, [Line("BASIC", 12_000m), Line("TICKET", 2_000m)], default);

        result.Should().BeOfType<UnprocessableEntityObjectResult>();
        Prop(result, "error").Should().Be("unknown_pay_component");
        ((IEnumerable<string>)Prop(result, "codes")!).Should().Equal("TICKET");
        (await w.Db.GradePayScaleComponents.AnyAsync()).Should().BeFalse();
        (await w.Db.SalaryStructures.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task ResavingTheGrade_UpdatesAnUnusedStructureInPlace_AndVersionsOneEmployeesAreOn()
    {
        var w = await Seed();
        await w.Grades.SetPayScale(w.Grade.Id, [Line("BASIC", 12_000m)], default);
        var v1 = await Structure(w, "GRADE-G5");

        // No employee on it yet: the same row is rewritten.
        (await w.Grades.SetPayScale(w.Grade.Id, [Line("BASIC", 13_000m), Line("HOUSING", 3_000m)], default)).Should().BeOfType<OkObjectResult>();
        (await w.Db.SalaryStructures.CountAsync()).Should().Be(1);
        (await Components(w, v1.Id)).Select(c => (c.Code, c.Amount)).Should().Equal(("BASIC", 13_000m), ("HOUSING", 3_000m));

        // Saving the same package again changes nothing.
        await w.Grades.SetPayScale(w.Grade.Id, [Line("BASIC", 13_000m), Line("HOUSING", 3_000m)], default);
        (await w.Db.SalaryStructures.CountAsync()).Should().Be(1);

        // An employee is on it: the structure is never rewritten under them; a new version opens.
        var employee = new Employee { TenantId = w.Tenant, CompanyId = w.A.Id, GradeId = w.Grade.Id, EmployeeCode = "E1", FullName = "On the grade", Status = "Active" };
        w.Db.Employees.Add(employee);
        w.Db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure { TenantId = w.Tenant, EmployeeId = 1, SalaryStructureId = v1.Id, BasicSalary = 13_000m, HousingAllowance = 3_000m, EffectiveDate = new DateOnly(2026, 1, 1), Currency = "SAR" });
        await w.Db.SaveChangesAsync();

        var result = await w.Grades.SetPayScale(w.Grade.Id, [Line("BASIC", 14_000m), Line("HOUSING", 3_000m)], default);

        result.Should().BeOfType<OkObjectResult>();
        var v2 = await Structure(w, "GRADE-G5-V2");
        v2.PreviousVersionId.Should().Be(v1.Id);
        v2.VersionNumber.Should().Be(2);
        v2.IsActive.Should().BeTrue();
        (await w.Db.SalaryStructures.SingleAsync(s => s.Id == v1.Id)).IsActive.Should().BeFalse();
        (await Components(w, v1.Id)).Single(c => c.Code == "BASIC").Amount.Should().Be(13_000m, "the old version is history and is not rewritten");
        (await Components(w, v2.Id)).Single(c => c.Code == "BASIC").Amount.Should().Be(14_000m);

        // The employee salary writer's default for the grade follows the chain to the new version.
        (await EmployeeSalaryBreakdownWriter.ResolveStructureAsync(w.Db, employee, w.Grade, null, default))!.Id.Should().Be(v2.Id);
        // And the next save of that chain works from the head (V2), not the retired root.
        (await GradeSalaryStructureSync.CurrentAsync(w.Db, w.Tenant, w.A.Id, "GRADE-G5", default))!.Id.Should().Be(v2.Id);
    }

    [Fact]
    public async Task ChangingTheGradesRange_FollowsIntoItsStructure_ButDoesNotCreateOne()
    {
        var w = await Seed();
        var service = new OrganizationSetupService(w.Db, new AuditService(w.Db));
        var context = new RequestContext(null, null, Guid.NewGuid(), w.Tenant);
        GradeRequest Request(decimal min, decimal max) => new("G5", "Professional 5", "", 5, min, 15_000m, max, "SAR", true, null, w.A.Id);

        await service.UpdateGradeAsync(w.Tenant, w.Grade.Id, Request(9_000m, 21_000m), context, default);
        (await w.Db.SalaryStructures.AnyAsync()).Should().BeFalse("a grade without a package has no structure yet");

        await w.Grades.SetPayScale(w.Grade.Id, [Line("BASIC", 12_000m)], default);
        await service.UpdateGradeAsync(w.Tenant, w.Grade.Id, Request(11_000m, 22_000m), context, default);

        var structure = await Structure(w, "GRADE-G5");
        (structure.MinGrossSalary, structure.MaxGrossSalary).Should().Be((11_000m, 22_000m));
        (await Components(w, structure.Id)).Should().ContainSingle(c => c.Code == "BASIC" && c.Amount == 12_000m);
    }

    [Fact]
    public async Task NewComponent_IsAddedToTheGradesCompany_IsOfferedThere_AndIsNeverPaidByTheEngine()
    {
        var w = await Seed();

        var created = await w.Grades.CreatePackageComponent(w.A.Id, new GradePackageComponentCreateRequest(null, "Car allowance", "سيارة", "Earning", IsTaxable: true), default);

        var option = (GradePackageComponentOption)((OkObjectResult)created).Value!;
        option.Code.Should().Be("CAR_ALLOWANCE");
        option.CompanyId.Should().Be(w.A.Id);
        option.IsTaxable.Should().BeTrue();
        var row = await w.Db.PayComponents.SingleAsync(c => c.Code == "CAR_ALLOWANCE");
        row.CalcMethod.Should().Be(PayComponentCalcMethods.GradePackage);
        row.Value.Should().BeNull();
        (await w.Db.PayComponents.AnyAsync(c => c.IsSystem && c.CompanyId == null)).Should().BeTrue("the system catalogue is seeded beside the first tenant row");
        (await w.Db.PayrollAuditLogs.AnyAsync(l => l.EntityId == row.Id.ToString() && l.Action == "payroll.component.created")).Should().BeTrue();

        // Offered to company A's grades, not company B's.
        (await GradePackageCatalog.OptionsAsync(w.Db, w.Tenant, w.A.Id, default)).Should().Contain(o => o.Code == "CAR_ALLOWANCE");
        (await GradePackageCatalog.OptionsAsync(w.Db, w.Tenant, w.B.Id, default)).Should().NotContain(o => o.Code == "CAR_ALLOWANCE");

        // Payroll never pays the catalogue row itself: its amount is set per grade and arrives through the salary columns.
        var resolved = PayComponentEngine.ResolveInEffect(await w.Db.PayComponents.ToListAsync(), w.Tenant, new DateOnly(2026, 11, 1));
        var run = PayComponentEngine.Compute(resolved, new PayComponentContext { Basic = 10_000m, Gross = 12_000m });
        run.Earnings.Should().NotContain(l => l.Code == "CAR_ALLOWANCE");
        PayComponentEngine.IsConfiguredValue(row).Should().BeFalse();

        // The grade can now use it.
        (await w.Grades.SetPayScale(w.Grade.Id, [Line("BASIC", 12_000m), Line("CAR_ALLOWANCE", 1_500m)], default)).Should().BeOfType<OkObjectResult>();
        (await w.Db.GradePayScaleComponents.SingleAsync(c => c.ComponentCode == "CAR_ALLOWANCE")).Should().Match<GradePayScaleComponent>(c => c.ComponentName == "Car allowance" && c.IsTaxable);

        // Same name again: a fresh derived code. A reserved code: refused.
        var again = (GradePackageComponentOption)((OkObjectResult)await w.Grades.CreatePackageComponent(w.A.Id, new GradePackageComponentCreateRequest(null, "Car allowance", null, "Earning"), default)).Value!;
        again.Code.Should().Be("CAR_ALLOWANCE_2");
        var reserved = await w.Grades.CreatePackageComponent(w.A.Id, new GradePackageComponentCreateRequest("BASIC", "Basic", null, "Earning"), default);
        reserved.Should().BeOfType<UnprocessableEntityObjectResult>();
        var taken = await w.Grades.CreatePackageComponent(w.A.Id, new GradePackageComponentCreateRequest("CAR_ALLOWANCE", "Car", null, "Earning"), default);
        taken.Should().BeOfType<UnprocessableEntityObjectResult>();
        var badType = await w.Grades.CreatePackageComponent(w.A.Id, new GradePackageComponentCreateRequest(null, "Employer cost", null, "EmployerContribution"), default);
        badType.Should().BeOfType<UnprocessableEntityObjectResult>();

        // The payroll component API will not edit it.
        var payroll = new PayComponentsController(w.Db) { ControllerContext = w.Grades.ControllerContext };
        var edit = await payroll.Update(row.Id, new PayComponentsController.UpdateRequest("Car", null, "Fixed", 500m, "EARN:OTHER_ALLOWANCES", new DateOnly(2026, 12, 1)), default);
        ((ObjectResult)edit).StatusCode.Should().Be(409);
        Prop(edit, "error").Should().Be("grade_package_component");
    }

    [Fact]
    public async Task ATenantComponentPayrollAlreadyPaysToEveryone_IsNotOfferedOnAGrade()
    {
        var w = await Seed();
        w.Db.PayComponents.Add(new PayComponent
        {
            TenantId = w.Tenant, CompanyId = w.A.Id, Code = "UNION_DUES", NameEn = "Union dues", NameAr = "Union dues",
            ComponentType = PayComponentTypes.Deduction, CalcMethod = PayComponentCalcMethods.Fixed, Value = 50m, IsActive = true,
        });
        await w.Db.SaveChangesAsync();

        var options = await GradePackageCatalog.OptionsAsync(w.Db, w.Tenant, w.A.Id, default);

        options.Select(o => o.Code).Should().BeEquivalentTo(["BASIC", "HOUSING", "TRANSPORT", "OTHER_ALLOWANCES", "FIXED_DEDUCTION"],
            "an unseeded tenant sees the compiled salary-column components; statutory, provider and configured-value rows are not grade lines");
        (await w.Grades.SetPayScale(w.Grade.Id, [Line("UNION_DUES", 50m)], default)).Should().BeOfType<UnprocessableEntityObjectResult>();
    }

    [Fact]
    public async Task ReleaseATenants_KeepBenefitsByGrade_AndNoStructureIsDerived()
    {
        var w = await Seed(releaseA: true);

        Prop(await w.Grades.SetPayScale(w.Grade.Id, [Line("BASIC", 12_000m)], default), "error").Should().Be("moved_to_benefits_by_grade");
        Prop(await w.Grades.CreatePackageComponent(w.A.Id, new GradePackageComponentCreateRequest(null, "Car", null, "Earning"), default), "error").Should().Be("moved_to_benefits_by_grade");
        (await GradeSalaryStructureSync.StageAsync(w.Db, w.Grade, [], null, createIfMissing: true, default)).Should().BeNull();

        var service = new OrganizationSetupService(w.Db, new AuditService(w.Db));
        await service.UpdateGradeAsync(w.Tenant, w.Grade.Id, new GradeRequest("G5", "Professional 5", "", 5, 9_000m, 15_000m, 30_000m, "SAR", true, null, w.A.Id),
            new RequestContext(null, null, null, w.Tenant), default);
        (await w.Db.SalaryStructures.AnyAsync()).Should().BeFalse();
        (await w.Db.PayComponents.AnyAsync()).Should().BeFalse();
    }

    [Theory]
    [InlineData("Car allowance", "CAR_ALLOWANCE")]
    [InlineData("  Phone / internet  ", "PHONE_INTERNET")]
    [InlineData("13th month", "C_13TH_MONTH")]
    [InlineData("بدل سيارة", "COMPONENT")]
    public void CodesAreDerivedFromTheEnglishName(string name, string code) => GradePackageCatalog.DeriveCode(name).Should().Be(code);

    [Fact]
    public void PackageTotals_CountMonthlyCashEarningsOnly_AndResolvePercentagesAgainstBasic()
    {
        GradePackageLine L(string code, string type, decimal amount, decimal pct = 0m, string frequency = "Monthly") =>
            new(code, type, pct > 0 ? "PercentOfBasic" : "Fixed", amount, pct, frequency);
        var lines = new[]
        {
            L("BASIC", "Earning", 10_000m), L("HOUSING", "Earning", 0m, 25m), L("TRANSPORT", "Earning", 1_000m),
            L("TICKET", "Benefit", 3_000m), L("BONUS_ANNUAL", "Earning", 5_000m, frequency: "Annual"), L("FIXED_DEDUCTION", "Deduction", 200m),
        };
        GradePackageMath.MonthlyGross(lines).Should().Be(13_500m);
        GradePackageMath.MonthlyDeductions(lines).Should().Be(200m);
        GradePackageMath.OutsideRange(13_500m, 10_000m, 20_000m).Should().BeFalse();
        GradePackageMath.OutsideRange(13_500m, 14_000m, 0m).Should().BeTrue();
        GradePackageMath.OutsideRange(25_000m, 0m, 20_000m).Should().BeTrue();
        GradePackageMath.OutsideRange(0m, 10_000m, 20_000m).Should().BeFalse("an unpriced package is not out of range");
    }
}
