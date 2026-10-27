using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;
using static Zayra.Api.Tests.CostAccountingKit;

namespace Zayra.Api.Tests;

/// <summary>
/// WP-B slice 5b — the readiness dry-run (AM-1 blocker detail, AM-2, AM-13; S-033, S-034, S-037).
/// </summary>
public class CostAccountingReadinessTests
{
    private static readonly string[] Keys =
    {
        "employees-unresolved", "invalid-reference", "accounts-no-rule", "no-company", "rounding-default", "mapping-invisible",
        "small-cell", "rules-not-enforced", "currency-scale", "employees-no-company",
    };

    private static async Task<Employee> Emp(ZayraDbContext db, Guid tid, Guid co, string code, Action<Employee>? cfg = null, string status = "Active")
    {
        var e = new Employee { TenantId = tid, CompanyId = co, EmployeeCode = code, FullName = "Secret Name " + code, Status = status, JoiningDate = new DateTime(2024, 1, 1), Salary = 98765.43m, BankIban = "SA0380000000608010167519" };
        cfg?.Invoke(e);
        db.Employees.Add(e);
        await db.SaveChangesAsync();
        return e;
    }

    private static async Task<Department> Dept(ZayraDbContext db, Guid tid, Guid co, string code, Guid? cc = null, Guid? parent = null)
    {
        var d = new Department { TenantId = tid, CompanyId = co, Code = code, NameEn = "Dept " + code, CostCenterId = cc, ParentDepartmentId = parent };
        db.Departments.Add(d);
        await db.SaveChangesAsync();
        return d;
    }

    private static async Task<Position> Pos(ZayraDbContext db, Guid tid, Guid co, string code, Guid? cc = null)
    {
        var p = new Position { TenantId = tid, CompanyId = co, Code = code, Title = "Title " + code, CostCenterId = cc };
        db.Positions.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    private static JsonElement Check(JsonElement body, string key) => body.P("checks").EnumerateArray().Single(c => c.P("key").GetString() == key);

    private static async Task<JsonElement> Run(ZayraDbContext db, Guid tid, Guid co, IEnumerable<string>? perms = null, string? scope = null)
    {
        var res = await Gl(db, tid, perms ?? ReadPerms, scope).CostAccountingReadiness(co, default);
        Status(res).Should().Be(200);
        return Body(res);
    }

    // ── shape ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task EmptyCompany_ReturnsEveryCheck_AllOk_WithTheSettings()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);

        var body = await Run(db, tid, co.Id);

        body.P("checks").EnumerateArray().Select(c => c.P("key").GetString()).Should().BeEquivalentTo(Keys);
        foreach (var c in body.P("checks").EnumerateArray())
        {
            c.P("severity").GetString().Should().Be("ok", c.P("key").GetString());
            c.P("count").GetInt32().Should().Be(0);
            c.P("sample").GetArrayLength().Should().Be(0);
        }
        var s = body.P("settings");
        (s.P("enabled").GetBoolean(), s.P("smallCellMin").GetInt32()).Should().Be((false, 0));
        s.P("defaultCostCenterId").ValueKind.Should().Be(JsonValueKind.Null);
        s.P("awaitingRun").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task SettingsReport_WhetherARunIsAwaitingFinanceOrLock()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        await AddRun(db, tid, co.Id, "PendingFinanceReview");

        (await Run(db, tid, co.Id)).P("settings").P("awaitingRun").GetBoolean().Should().BeTrue();
    }

    // ── employees with no cost centre (AM-1 detail) ─────────────────────────

    [Fact]
    public async Task EmployeesWithNoCostCentreAtAnyLevel_AreCounted_SampleCappedAtTen_WithTheAm1Detail()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        var dept = await Dept(db, tid, co.Id, "OPS");
        var pos = await Pos(db, tid, co.Id, "P1");
        for (var i = 1; i <= 12; i++) await Emp(db, tid, co.Id, $"E{i:00}", e => { e.DepartmentId = dept.Id; e.PositionId = pos.Id; });

        var c = Check(await Run(db, tid, co.Id), "employees-unresolved");

        c.P("count").GetInt32().Should().Be(12);
        c.P("severity").GetString().Should().Be("warning");
        var sample = c.P("sample").EnumerateArray().ToList();
        sample.Should().HaveCount(10);
        var first = sample[0];
        first.P("code").GetString().Should().StartWith("E");
        first.P("employeeCode").GetString().Should().Be(first.P("code").GetString());
        first.P("employeeId").GetInt32().Should().Be(first.P("id").GetInt32());
        first.P("level").GetString().Should().Be("company");
        first.P("reason").GetString().Should().Be("none");
        first.P("departmentId").GetGuid().Should().Be(dept.Id);
        first.P("positionId").GetGuid().Should().Be(pos.Id);
        first.P("parentDepartmentId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task EmployeeSample_NeverCarriesNamesPayOrBankDetails()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        await Emp(db, tid, co.Id, "E01");

        var json = (await Run(db, tid, co.Id)).GetRawText();

        json.Should().NotContain("Secret Name").And.NotContain("98765").And.NotContain("SA0380000000608010167519");
    }

    [Fact]
    public async Task EmployeesResolvedAtAnyLevel_AreNotCounted()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        var ccEmp = await AddCostCentre(db, tid, co.Id, "EMP");
        var ccPos = await AddCostCentre(db, tid, co.Id, "POS");
        var ccDept = await AddCostCentre(db, tid, co.Id, "DEPT");
        var ccParent = await AddCostCentre(db, tid, co.Id, "PARENT");
        var parent = await Dept(db, tid, co.Id, "PAR", ccParent.Id);
        var child = await Dept(db, tid, co.Id, "CHILD", null, parent.Id);
        var withCc = await Dept(db, tid, co.Id, "WITH", ccDept.Id);
        var pos = await Pos(db, tid, co.Id, "P", ccPos.Id);
        await Emp(db, tid, co.Id, "OVR", e => { e.CostCenterId = ccEmp.Id; e.CostCenterIsOverride = true; });
        await Emp(db, tid, co.Id, "BYPOS", e => e.PositionId = pos.Id);
        await Emp(db, tid, co.Id, "BYDEPT", e => e.DepartmentId = withCc.Id);
        await Emp(db, tid, co.Id, "BYPARENT", e => e.DepartmentId = child.Id);

        (await Run(db, tid, co.Id)).Pipe(b => Check(b, "employees-unresolved").P("count").GetInt32()).Should().Be(0);
    }

    [Fact]
    public async Task AStaleDepartmentCopyOnTheEmployee_IsNotAnOverride_AndDoesNotResolve()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        var old = await AddCostCentre(db, tid, co.Id, "OLD");
        await Emp(db, tid, co.Id, "STALE", e => { e.CostCenterId = old.Id; e.CostCenterIsOverride = false; });

        Check(await Run(db, tid, co.Id), "employees-unresolved").P("count").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task CompanyDefault_ResolvesEveryone()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        var dflt = await AddCostCentre(db, tid, co.Id, "DEFAULT");
        co.DefaultCostCenterId = dflt.Id; await db.SaveChangesAsync();
        await Emp(db, tid, co.Id, "E1");

        Check(await Run(db, tid, co.Id), "employees-unresolved").P("count").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task OnlyActiveEmployeesOfThisCompany_AreChecked()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        var other = await AddCompany(db, tid);
        await Emp(db, tid, co.Id, "GONE", status: "Terminated");
        await Emp(db, tid, other.Id, "OTHER");

        Check(await Run(db, tid, co.Id), "employees-unresolved").P("count").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task UnresolvedEmployees_BecomeABlocker_WhenARequiredProfitAndLossRuleExists()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        await AddAccount(db, tid, co.Id, "5001", "Expense", "Required");
        await Emp(db, tid, co.Id, "E1");

        Check(await Run(db, tid, co.Id), "employees-unresolved").P("severity").GetString().Should().Be("blocker");
    }

    // ── invalid references ──────────────────────────────────────────────────

    [Fact]
    public async Task InvalidCostCentreAtAnyLevel_IsABlocker_ListedByHolder_AndLeaksNoForeignRow()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        var other = await AddCompany(db, tid);
        var foreign = await AddCostCentre(db, tid, other.Id, "TOPSECRETOTHERCO");
        var inactive = await AddCostCentre(db, tid, co.Id, "INACTIVE1", active: false);
        var deleted = await AddCostCentre(db, tid, co.Id, "DELETED1", deleted: true);
        var d = await Dept(db, tid, co.Id, "DEP", foreign.Id);
        var p = await Pos(db, tid, co.Id, "POS", deleted.Id);
        var e = await Emp(db, tid, co.Id, "EMP", x => { x.CostCenterId = inactive.Id; x.CostCenterIsOverride = true; });

        var body = await Run(db, tid, co.Id);

        var c = Check(body, "invalid-reference");
        c.P("severity").GetString().Should().Be("blocker");
        c.P("count").GetInt32().Should().Be(3);
        c.P("sample").EnumerateArray().Select(s => (s.P("kind").GetString(), s.P("code").GetString()))
            .Should().BeEquivalentTo(new[] { ("department", "DEP"), ("position", "POS"), ("employee", "EMP") });
        // S-033: not one character of the other company's cost centre is returned.
        body.GetRawText().Should().NotContain("TOPSECRETOTHERCO").And.NotContain(foreign.Id.ToString());
    }

    [Fact]
    public async Task AnEmployeeWhoseResolutionFailsOnAnInvalidLevel_GetsTheBlockerDetailWithTheRightLevel()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        var dead = await AddCostCentre(db, tid, co.Id, "DEAD", active: false);
        var parent = await Dept(db, tid, co.Id, "PAR", dead.Id);
        var child = await Dept(db, tid, co.Id, "CHILD", null, parent.Id);
        var e = await Emp(db, tid, co.Id, "E1", x => x.DepartmentId = child.Id);

        var body = await Run(db, tid, co.Id);

        var blocker = body.P("blockers").EnumerateArray().Single();
        blocker.P("employeeId").GetInt32().Should().Be(e.Id);
        blocker.P("employeeCode").GetString().Should().Be("E1");
        blocker.P("level").GetString().Should().Be("parentDepartment");
        blocker.P("reason").GetString().Should().Be("parent_department_cost_centre_invalid");
        blocker.P("departmentId").GetGuid().Should().Be(child.Id);
        blocker.P("parentDepartmentId").GetGuid().Should().Be(parent.Id);
        blocker.P("errorCode").GetString().Should().Be("cost_centre_invalid");
    }

    [Fact]
    public async Task APositionWithAnInvalidCostCentre_DoesNotFallThroughToTheDepartment()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        var ok = await AddCostCentre(db, tid, co.Id, "OK");
        var dead = await AddCostCentre(db, tid, co.Id, "DEAD", deleted: true);
        var dept = await Dept(db, tid, co.Id, "DEP", ok.Id);
        var pos = await Pos(db, tid, co.Id, "POS", dead.Id);
        await Emp(db, tid, co.Id, "E1", x => { x.DepartmentId = dept.Id; x.PositionId = pos.Id; });

        var body = await Run(db, tid, co.Id);

        var b = body.P("blockers").EnumerateArray().Single();
        (b.P("level").GetString(), b.P("reason").GetString()).Should().Be(("position", "position_cost_centre_invalid"));
        Check(body, "employees-unresolved").P("count").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task BlockerList_IsCappedAtTenPerKind()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        for (var i = 0; i < 14; i++) await Emp(db, tid, co.Id, $"N{i:00}");

        var body = await Run(db, tid, co.Id);

        body.P("blockers").GetArrayLength().Should().Be(10);
        Check(body, "employees-unresolved").P("count").GetInt32().Should().Be(14);
    }

    // ── accounts, groups, rounding, mappings ────────────────────────────────

    [Fact]
    public async Task ExpenseAndIncomeAccountsOnOptional_AreCounted_AsAWarning()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        var other = await AddCompany(db, tid);
        await AddAccount(db, tid, co.Id, "5001", "Expense");
        await AddAccount(db, tid, null, "4001", "Income");
        await AddAccount(db, tid, co.Id, "5002", "Expense", "Required");
        await AddAccount(db, tid, co.Id, "2101", "Liability");
        await AddAccount(db, tid, co.Id, "5003", "Expense", active: false);
        await AddAccount(db, tid, other.Id, "5009", "Expense");

        var c = Check(await Run(db, tid, co.Id), "accounts-no-rule");

        c.P("count").GetInt32().Should().Be(2);
        c.P("severity").GetString().Should().Be("warning");
        c.P("sample").EnumerateArray().Select(s => s.P("code").GetString()).Should().BeEquivalentTo(new[] { "5001", "4001" });
    }

    [Fact]
    public async Task CostCentresWithNoCompany_AreAnAggregateCount_NeverListed()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        await AddCostCentre(db, tid, null, "GRP1");
        await AddCostCentre(db, tid, null, "GRP2");
        await AddCostCentre(db, tid, null, "GRP3", deleted: true);

        var body = await Run(db, tid, co.Id);

        var c = Check(body, "no-company");
        (c.P("count").GetInt32(), c.P("severity").GetString(), c.P("sample").GetArrayLength()).Should().Be((2, "blocker", 0));
        body.GetRawText().Should().NotContain("GRP1");
    }

    [Fact]
    public async Task RoundingAccountRequiringACostCentre_WithNoDefault_IsABlocker()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        await GlDriverSeeder.SeedTenantDefaultsAsync(db, tid, default);
        await db.SaveChangesAsync();
        var co = await AddCompany(db, tid);
        await AddAccount(db, tid, co.Id, "5199", "Expense", "Required");

        Check(await Run(db, tid, co.Id), "rounding-default").Pipe(c => (c.P("count").GetInt32(), c.P("severity").GetString())).Should().Be((1, "blocker"));
    }

    [Fact]
    public async Task RoundingAccount_IsFine_WithAValidDefault_OrWhenOptional()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        await GlDriverSeeder.SeedTenantDefaultsAsync(db, tid, default);
        await db.SaveChangesAsync();
        var co = await AddCompany(db, tid);
        var acct = await AddAccount(db, tid, co.Id, "5199", "Expense", "Required");
        var dflt = await AddCostCentre(db, tid, co.Id, "DEFAULT");
        co.DefaultCostCenterId = dflt.Id; await db.SaveChangesAsync();
        Check(await Run(db, tid, co.Id), "rounding-default").P("count").GetInt32().Should().Be(0);

        co.DefaultCostCenterId = null; acct.CostCenterRule = "Optional"; await db.SaveChangesAsync();
        Check(await Run(db, tid, co.Id), "rounding-default").P("count").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task AMappingWhoseFixedCostCentreTheCompanyCannotSee_IsABlocker_WithoutNamingTheForeignRow()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        await GlDriverSeeder.SeedTenantDefaultsAsync(db, tid, default);
        await db.SaveChangesAsync();
        var co = await AddCompany(db, tid);
        var other = await AddCompany(db, tid);
        var acct = await AddAccount(db, tid, co.Id, "5001", "Expense");
        var foreign = await AddCostCentre(db, tid, other.Id, "FOREIGNFIXED");
        var mine = await AddCostCentre(db, tid, co.Id, "MINE");
        db.GlAccountMappings.Add(new GlAccountMapping { TenantId = tid, CompanyId = co.Id, DriverKey = "EARN:BASIC", AccountId = acct.Id, SegmentCostCenterId = foreign.Id });
        db.GlAccountMappings.Add(new GlAccountMapping { TenantId = tid, CompanyId = co.Id, DriverKey = "EARN:HOUSING", AccountId = acct.Id, SegmentCostCenterId = mine.Id });
        await db.SaveChangesAsync();

        var body = await Run(db, tid, co.Id);

        var c = Check(body, "mapping-invisible");
        (c.P("count").GetInt32(), c.P("severity").GetString()).Should().Be((1, "blocker"));
        c.P("sample").EnumerateArray().Single().P("code").GetString().Should().Be("EARN:BASIC");
        body.GetRawText().Should().NotContain("FOREIGNFIXED").And.NotContain(foreign.Id.ToString());
    }

    [Fact]
    public async Task RequiredRulesWhileCostingIsOff_AreFlagged_SoTheUiCanSayNothingIsEnforced()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid, enabled: false);
        await AddAccount(db, tid, co.Id, "5001", "Expense", "Required");
        await AddAccount(db, tid, null, "5002", "Expense", "Required");

        Check(await Run(db, tid, co.Id), "rules-not-enforced").Pipe(c => (c.P("count").GetInt32(), c.P("severity").GetString())).Should().Be((2, "warning"));

        co.CostAccountingEnabled = true; await db.SaveChangesAsync();
        Check(await Run(db, tid, co.Id), "rules-not-enforced").P("count").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task SmallCells_AreCounted_WhenKIsBelowThree()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        var solo = await AddCostCentre(db, tid, co.Id, "SOLO");
        var pair = await AddCostCentre(db, tid, co.Id, "PAIR");
        var crowd = await AddCostCentre(db, tid, co.Id, "CROWD");
        await Emp(db, tid, co.Id, "S1", e => { e.CostCenterId = solo.Id; e.CostCenterIsOverride = true; });
        foreach (var n in new[] { "P1", "P2" }) await Emp(db, tid, co.Id, n, e => { e.CostCenterId = pair.Id; e.CostCenterIsOverride = true; });
        foreach (var n in new[] { "C1", "C2", "C3" }) await Emp(db, tid, co.Id, n, e => { e.CostCenterId = crowd.Id; e.CostCenterIsOverride = true; });

        var body = await Run(db, tid, co.Id);

        var c = Check(body, "small-cell");
        (c.P("count").GetInt32(), c.P("severity").GetString()).Should().Be((2, "warning"));
        c.P("sample").EnumerateArray().Select(s => s.P("code").GetString()).Should().BeEquivalentTo(new[] { "SOLO", "PAIR" });
        body.P("smallCellExposure").P("count").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task ThreeDecimalCurrency_IsAHardBlocker()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid, currency: "KWD");

        Check(await Run(db, tid, co.Id), "currency-scale").Pipe(c => (c.P("count").GetInt32(), c.P("severity").GetString())).Should().Be((1, "blocker"));
    }

    // ── employees with no company (they cannot be posted once any company costs payroll) ──

    [Fact]
    public async Task ActiveEmployeesWithNoCompany_AreCountedOnly_AsABlocker()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid, enabled: true);
        var other = await AddCompany(db, tid);
        await Emp(db, tid, co.Id, "OK1");
        for (var i = 0; i < 3; i++)
            db.Employees.Add(new Employee { TenantId = tid, CompanyId = null, EmployeeCode = $"ORPHAN{i}", FullName = "Nameless", Status = "Active", JoiningDate = new DateTime(2024, 1, 1) });
        db.Employees.Add(new Employee { TenantId = tid, CompanyId = null, EmployeeCode = "GONE", FullName = "x", Status = "Terminated", JoiningDate = new DateTime(2024, 1, 1) });
        db.Employees.Add(new Employee { TenantId = Guid.NewGuid(), CompanyId = null, EmployeeCode = "OTHERTENANT", FullName = "x", Status = "Active", JoiningDate = new DateTime(2024, 1, 1) });
        await db.SaveChangesAsync();

        var body = await Run(db, tid, co.Id);

        var c = Check(body, "employees-no-company");
        (c.P("count").GetInt32(), c.P("severity").GetString(), c.P("sample").GetArrayLength()).Should().Be((3, "blocker", 0));
        body.GetRawText().Should().NotContain("ORPHAN");
        Check(await Run(db, tid, other.Id), "employees-no-company").P("count").GetInt32().Should().Be(3, "the unattributed pool is tenant-wide");
    }

    // ── S-033: permission, scope, tenant ────────────────────────────────────

    [Fact]
    public async Task WithoutFinanceGlRead_Is403()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);

        foreach (var perms in new[] { Array.Empty<string>(), new[] { "organization.read", "employees.read" }, new[] { "organization.costcenter.manage" } })
            (await Gl(db, tid, perms).CostAccountingReadiness(co.Id, default)).Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task ACompanyScopedCaller_CannotReadAnotherCompany_AndOtherTenantsGet404()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var x = await AddCompany(db, tid);
        var y = await AddCompany(db, tid);
        var foreign = await AddCompany(db, Guid.NewGuid());

        (await Gl(db, tid, ReadPerms, CompanyScope(x.Id)).CostAccountingReadiness(y.Id, default)).Should().BeOfType<ForbidResult>();
        Status(await Gl(db, tid, ReadPerms, CompanyScope(x.Id)).CostAccountingReadiness(x.Id, default)).Should().Be(200);
        Status(await Gl(db, tid, ReadPerms).CostAccountingReadiness(foreign.Id, default)).Should().Be(404);
    }

    [Fact]
    public async Task CompanyX_NeverSeesCompanyYsRows()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var x = await AddCompany(db, tid);
        var y = await AddCompany(db, tid);
        await Emp(db, tid, y.Id, "YONLY1");
        var deadY = await AddCostCentre(db, tid, y.Id, "YDEAD", deleted: true);
        await Dept(db, tid, y.Id, "YDEPT", deadY.Id);
        await AddAccount(db, tid, y.Id, "5777", "Expense");

        var body = await Run(db, tid, x.Id, scope: CompanyScope(x.Id));

        body.GetRawText().Should().NotContain("YONLY1").And.NotContain("YDEPT").And.NotContain("YDEAD").And.NotContain("5777");
        body.P("checks").EnumerateArray().Sum(c => c.P("count").GetInt32()).Should().Be(0);
    }

    // ── S-034: read-only ────────────────────────────────────────────────────

    [Fact]
    public async Task Readiness_IsAGetThatWritesNothing()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        await Emp(db, tid, co.Id, "E1");
        var before = await db.AuditLogs.CountAsync();

        await Run(db, tid, co.Id);

        db.ChangeTracker.HasChanges().Should().BeFalse();
        (await db.AuditLogs.CountAsync()).Should().Be(before);
        var method = typeof(FinanceGlController).GetMethod(nameof(FinanceGlController.CostAccountingReadiness))!;
        method.GetCustomAttributes().OfType<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>().Single().HttpMethods.Should().Equal("GET");
    }
}

internal static class FuncExtensions
{
    public static TOut Pipe<TIn, TOut>(this TIn x, Func<TIn, TOut> f) => f(x);
}
