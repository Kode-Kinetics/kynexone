using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>GET /api/establishment/matrix pay-range reconciliation: per-level pay band + per-department payPlan,
/// withheld without payroll.read / employees.sensitive, scoped per company.</summary>
public class EstablishmentMatrixPayRangeTests
{
    private sealed record Fx(Guid TenantId, Company CoA, Department DeptA, StaffingLevel Level, Designation Desig, Grade Grade);

    private static async Task<Fx> Seed(ZayraDbContext db, decimal budget = 10000, int approved = 5, int? budgeted = 5)
    {
        var tenantId = Guid.NewGuid();
        var co = new Company { TenantId = tenantId, DefaultCurrency = "SAR" };
        var grade = new Grade { TenantId = tenantId, CompanyId = co.Id, Code = "G1", Name = "G1", MinSalary = 1000, MidSalary = 1500, MaxSalary = 2000, Currency = "SAR" };
        var level = new StaffingLevel { TenantId = tenantId, Code = "MGR", NameEn = "Manager", NameAr = "م", Rank = 2 };
        var desig = new Designation { TenantId = tenantId, CompanyId = co.Id, Code = "D1", TitleEn = "Mgr", IsActive = true, StaffingLevelId = level.Id, GradeId = grade.Id };
        var dept = new Department { TenantId = tenantId, CompanyId = co.Id, Code = "OPS", NameEn = "Operations", IsActive = true, ApprovedHeadcount = approved, MonthlyBudgetAmount = budget };
        db.AddRange(co, grade, level, desig, dept);
        if (budgeted is { } b)
            db.DepartmentStaffingBudgets.Add(new DepartmentStaffingBudget { TenantId = tenantId, DepartmentId = dept.Id, StaffingLevelId = level.Id, BudgetedHeadcount = b });
        await db.SaveChangesAsync();
        return new Fx(tenantId, co, dept, level, desig, grade);
    }

    private static async Task<JsonElement> Matrix(ZayraDbContext db, Guid tenantId, params string[] perms)
    {
        var ok = (OkObjectResult)await EstablishmentApiTests.CreateController(db, tenantId, perms).Matrix(CancellationToken.None);
        return JsonSerializer.SerializeToElement(ok.Value);
    }

    private static JsonElement Dept(JsonElement root, string name) =>
        root.GetProperty("departments").EnumerateArray().Single(d => d.GetProperty("departmentName").GetString() == name);

    [Fact]
    public async Task Permitted_GetsPayBandAndPlan_OwnerExample()
    {
        await using var db = EstablishmentApiTests.CreateDb();
        var fx = await Seed(db);
        var root = await Matrix(db, fx.TenantId, "organization.read", "payroll.read");

        var dept = Dept(root, "Operations");
        var plan = dept.GetProperty("payPlan");
        plan.GetProperty("avgPerHead").GetDecimal().Should().Be(2000);
        plan.GetProperty("plannedMin").GetDecimal().Should().Be(5000);
        plan.GetProperty("plannedMid").GetDecimal().Should().Be(7500);
        plan.GetProperty("plannedMax").GetDecimal().Should().Be(10000);
        plan.GetProperty("unallocatedBudget").GetDecimal().Should().Be(2500);
        plan.GetProperty("unpricedHeads").GetInt32().Should().Be(0);
        plan.GetProperty("uncontrolledLevels").GetInt32().Should().Be(0);
        plan.GetProperty("flags").EnumerateArray().Select(f => f.GetString()).Should().Equal("underBudget");

        var pay = dept.GetProperty("levels")[0].GetProperty("pay");
        pay.GetProperty("min").GetDecimal().Should().Be(1000);
        pay.GetProperty("mid").GetDecimal().Should().Be(1500);
        pay.GetProperty("max").GetDecimal().Should().Be(2000);
        pay.GetProperty("currency").GetString().Should().Be("SAR");
        root.GetProperty("withheld").GetArrayLength().Should().Be(0);
        dept.GetProperty("levels")[0].GetProperty("payStatus").GetString().Should().Be("priced");
    }

    [Fact]
    public async Task EmployeesSensitive_AlsoSeesNumbers()
    {
        await using var db = EstablishmentApiTests.CreateDb();
        var fx = await Seed(db);
        var root = await Matrix(db, fx.TenantId, "organization.read", "employees.sensitive");
        Dept(root, "Operations").GetProperty("payPlan").ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public async Task Withheld_GetsNullsAndNotes_NoFlagsLeak()
    {
        await using var db = EstablishmentApiTests.CreateDb();
        var fx = await Seed(db);
        var root = await Matrix(db, fx.TenantId, "organization.read");

        var dept = Dept(root, "Operations");
        dept.GetProperty("payPlan").ValueKind.Should().Be(JsonValueKind.Null);
        dept.GetProperty("levels")[0].GetProperty("pay").ValueKind.Should().Be(JsonValueKind.Null);
        dept.GetProperty("levels")[0].GetProperty("payStatus").ValueKind.Should().Be(JsonValueKind.Null);
        var fields = root.GetProperty("withheld").EnumerateArray()
            .SelectMany(w => w.GetProperty("fields").EnumerateArray().Select(f => f.GetString())).ToList();
        fields.Should().Contain(new[] { "departments[].currentMonthlySpend", "departments[].payPlan", "departments[].levels[].pay" });
        root.GetRawText().Should().NotContain("underBudget").And.NotContain("avgPerHead\":2000");
    }

    [Fact]
    public async Task UncontrolledLevel_LevelStillShowsBand_PlanCountsIt()
    {
        await using var db = EstablishmentApiTests.CreateDb();
        var fx = await Seed(db, budgeted: null);
        var root = await Matrix(db, fx.TenantId, "organization.read", "payroll.read");
        var dept = Dept(root, "Operations");
        dept.GetProperty("payPlan").GetProperty("uncontrolledLevels").GetInt32().Should().Be(1);
        dept.GetProperty("payPlan").GetProperty("flags").EnumerateArray().Select(f => f.GetString()).Should().Contain("notComparable");
        dept.GetProperty("levels")[0].GetProperty("pay").GetProperty("mid").GetDecimal().Should().Be(1500);
    }

    [Fact]
    public async Task CompanyScoping_SameLevel_DifferentBandsPerDepartmentCompany()
    {
        await using var db = EstablishmentApiTests.CreateDb();
        var fx = await Seed(db);
        var coB = new Company { TenantId = fx.TenantId, DefaultCurrency = "SAR" };
        var gradeB = new Grade { TenantId = fx.TenantId, CompanyId = coB.Id, Code = "GB", Name = "GB", MinSalary = 5000, MidSalary = 6000, MaxSalary = 7000, Currency = "SAR" };
        var desigB = new Designation { TenantId = fx.TenantId, CompanyId = coB.Id, Code = "DB", TitleEn = "MgrB", IsActive = true, StaffingLevelId = fx.Level.Id, GradeId = gradeB.Id };
        var deptB = new Department { TenantId = fx.TenantId, CompanyId = coB.Id, Code = "OPSB", NameEn = "OperationsB", IsActive = true, ApprovedHeadcount = 2, MonthlyBudgetAmount = 20000 };
        db.AddRange(coB, gradeB, desigB, deptB);
        db.DepartmentStaffingBudgets.Add(new DepartmentStaffingBudget { TenantId = fx.TenantId, DepartmentId = deptB.Id, StaffingLevelId = fx.Level.Id, BudgetedHeadcount = 2 });
        await db.SaveChangesAsync();

        var root = await Matrix(db, fx.TenantId, "organization.read", "payroll.read");
        Dept(root, "Operations").GetProperty("levels")[0].GetProperty("pay").GetProperty("mid").GetDecimal().Should().Be(1500);
        Dept(root, "OperationsB").GetProperty("levels")[0].GetProperty("pay").GetProperty("mid").GetDecimal().Should().Be(6000);
        Dept(root, "OperationsB").GetProperty("payPlan").GetProperty("plannedMid").GetDecimal().Should().Be(12000);
    }

    [Fact]
    public async Task OtherTenantGrades_AreNeverUsed()
    {
        await using var db = EstablishmentApiTests.CreateDb();
        var fx = await Seed(db);
        var foreign = new Grade { TenantId = Guid.NewGuid(), Code = "X", Name = "X", MinSalary = 9, MidSalary = 9, MaxSalary = 9, Currency = "SAR" };
        db.Grades.Add(foreign);
        fx.Desig.GradeId = foreign.Id;
        await db.SaveChangesAsync();
        var root = await Matrix(db, fx.TenantId, "organization.read", "payroll.read");
        Dept(root, "Operations").GetProperty("levels")[0].GetProperty("pay").ValueKind.Should().Be(JsonValueKind.Null);
        Dept(root, "Operations").GetProperty("levels")[0].GetProperty("payStatus").GetString().Should().Be("noRange");
    }

    [Fact]
    public async Task CurrencyMismatch_AgainstCompanyDefault_FlagsAndUnprices()
    {
        await using var db = EstablishmentApiTests.CreateDb();
        var fx = await Seed(db);
        fx.Grade.Currency = "USD";
        await db.SaveChangesAsync();
        var root = await Matrix(db, fx.TenantId, "organization.read", "payroll.read");
        var dept = Dept(root, "Operations");
        dept.GetProperty("levels")[0].GetProperty("pay").ValueKind.Should().Be(JsonValueKind.Null);
        dept.GetProperty("levels")[0].GetProperty("payStatus").GetString().Should().Be("currencyMismatch");
        var plan = dept.GetProperty("payPlan");
        plan.GetProperty("flags").EnumerateArray().Select(f => f.GetString()).Should().Contain("currencyMismatch");
        plan.GetProperty("unpricedHeads").GetInt32().Should().Be(5);
    }
}
