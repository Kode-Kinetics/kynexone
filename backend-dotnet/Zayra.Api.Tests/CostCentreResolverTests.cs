using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Cost-centre accounting v1, WP-A: <see cref="CostCentreResolver"/> (contract 2.2). T-001..T-007.
/// Order: employee override -> position -> department -> parent department -> company default. An invalid
/// cost centre at any level is a hard Unresolved, never a fall-through to the next level.
/// </summary>
public class CostCentreResolverTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Company = Guid.NewGuid();

    private static ResolverCostCentre Cc(bool active = true, bool deleted = false, Guid? company = null, Guid? tenant = null) =>
        new(Guid.NewGuid(), tenant ?? Tenant, company ?? Company, active, deleted);

    private static CostCentreResolverSnapshot Snap(
        IEnumerable<ResolverCostCentre> ccs, IEnumerable<ResolverDepartment>? depts = null,
        IEnumerable<ResolverPosition>? positions = null, Guid? companyDefault = null) =>
        new(Tenant, Company, companyDefault, ccs, depts ?? [], positions ?? []);

    [Fact]
    public void T001_EmployeeOverrideWins_OverPositionAndDepartment()
    {
        var emp = Cc(); var pos = Cc(); var dep = Cc();
        var d = new ResolverDepartment(Guid.NewGuid(), null, dep.Id);
        var p = new ResolverPosition(Guid.NewGuid(), pos.Id);
        var r = CostCentreResolver.Resolve(new ResolverEmployee(d.Id, p.Id, emp.Id, true), Snap([emp, pos, dep], [d], [p]));
        r.CostCenterId.Should().Be(emp.Id);
        r.Source.Should().Be("Employee");
        r.IsResolved.Should().BeTrue();
    }

    [Fact]
    public void T002_WithoutTheOverrideFlag_AStaleCopyOfTheDepartmentCostCentreIsIgnored()
    {
        var oldCc = Cc(); var newCc = Cc();
        var d = new ResolverDepartment(Guid.NewGuid(), null, newCc.Id); // department was re-pointed to newCc
        // The employee still carries the old copy made when they joined the department, flag false.
        var r = CostCentreResolver.Resolve(new ResolverEmployee(d.Id, null, oldCc.Id, false), Snap([oldCc, newCc], [d]));
        r.CostCenterId.Should().Be(newCc.Id);
        r.Source.Should().Be("Department");
    }

    [Fact]
    public void T003_PositionBeatsDepartment()
    {
        var pos = Cc(); var dep = Cc();
        var d = new ResolverDepartment(Guid.NewGuid(), null, dep.Id);
        var p = new ResolverPosition(Guid.NewGuid(), pos.Id);
        var r = CostCentreResolver.Resolve(new ResolverEmployee(d.Id, p.Id, null, false), Snap([pos, dep], [d], [p]));
        (r.CostCenterId, r.Source, r.Level).Should().Be((pos.Id, "Position", "position"));
    }

    [Fact]
    public void T004_ADepartmentWithoutACostCentre_FallsBackToItsParent()
    {
        var parentCc = Cc();
        var parent = new ResolverDepartment(Guid.NewGuid(), null, parentCc.Id);
        var child = new ResolverDepartment(Guid.NewGuid(), parent.Id, null);
        var r = CostCentreResolver.Resolve(new ResolverEmployee(child.Id, null, null, false), Snap([parentCc], [parent, child]));
        (r.CostCenterId, r.Source, r.Level).Should().Be((parentCc.Id, "ParentDepartment", "parentDepartment"));
    }

    [Fact]
    public void T005_AParentDepartmentCycle_DoesNotLoop_AndReachesTheCompanyDefaultOrUnresolved()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var da = new ResolverDepartment(a, b, null); var db = new ResolverDepartment(b, a, null);
        var def = Cc();

        var withDefault = CostCentreResolver.Resolve(new ResolverEmployee(a, null, null, false), Snap([def], [da, db], companyDefault: def.Id));
        (withDefault.CostCenterId, withDefault.Source).Should().Be((def.Id, "CompanyDefault"));

        var without = CostCentreResolver.Resolve(new ResolverEmployee(a, null, null, false), Snap([def], [da, db]));
        without.IsResolved.Should().BeFalse();
        without.UnresolvedReason.Should().Be("none");
    }

    [Fact]
    public void T006_NoCostCentreAtAnyLevel_UsesTheCompanyDefault_ElseUnresolvedNone()
    {
        var def = Cc();
        var r = CostCentreResolver.Resolve(new ResolverEmployee(null, null, null, false), Snap([def], companyDefault: def.Id));
        (r.CostCenterId, r.Source, r.Level).Should().Be((def.Id, "CompanyDefault", "company"));

        var none = CostCentreResolver.Resolve(new ResolverEmployee(null, null, null, false), Snap([def]));
        (none.IsResolved, none.UnresolvedReason).Should().Be((false, "none"));
    }

    public static IEnumerable<object[]> InvalidKinds() =>
    [
        ["inactive"], ["deleted"], ["other-company"], ["other-tenant"], ["missing"],
    ];

    [Theory]
    [MemberData(nameof(InvalidKinds))]
    public void T007_AnInvalidCostCentreAtThePositionLevel_IsAHardUnresolved_NotTheDepartmentCostCentre(string kind)
    {
        var valid = Cc();
        var bad = kind switch
        {
            "inactive" => Cc(active: false),
            "deleted" => Cc(deleted: true),
            "other-company" => Cc(company: Guid.NewGuid()),
            "other-tenant" => Cc(tenant: Guid.NewGuid()),
            _ => Cc(),
        };
        var d = new ResolverDepartment(Guid.NewGuid(), null, valid.Id);
        var p = new ResolverPosition(Guid.NewGuid(), bad.Id);
        var known = kind == "missing" ? new[] { valid } : new[] { valid, bad };
        var r = CostCentreResolver.Resolve(new ResolverEmployee(d.Id, p.Id, null, false), Snap(known, [d], [p]));
        r.IsResolved.Should().BeFalse("garbage position data must never silently move cost to the department");
        r.UnresolvedReason.Should().Be("position_cost_centre_invalid");
        r.Level.Should().Be("position");
    }

    [Theory]
    [InlineData("employee", "employee_cost_centre_invalid")]
    [InlineData("department", "department_cost_centre_invalid")]
    [InlineData("parentDepartment", "parent_department_cost_centre_invalid")]
    [InlineData("company", "company_default_cost_centre_invalid")]
    public void T007b_EveryLevelHasItsOwnInvalidReason(string level, string reason)
    {
        var bad = Cc(active: false); var fallback = Cc();
        var parent = new ResolverDepartment(Guid.NewGuid(), null, level == "parentDepartment" ? bad.Id : null);
        var dept = new ResolverDepartment(Guid.NewGuid(), level == "parentDepartment" ? parent.Id : null, level == "department" ? bad.Id : null);
        var emp = new ResolverEmployee(dept.Id, null, level == "employee" ? bad.Id : null, level == "employee");
        var r = CostCentreResolver.Resolve(emp, Snap([bad, fallback], [parent, dept], companyDefault: level == "company" ? bad.Id : null));
        (r.IsResolved, r.UnresolvedReason, r.Level).Should().Be((false, reason, level));
    }

    [Fact]
    public async Task LoadSnapshot_ReadsTheCompanysDepartmentsPositionsAndCostCentres_AndResolvesFromThem()
    {
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid(); var company = new Company { TenantId = tenant, LegalNameEn = "Alpha", RegistrationNumber = "R1", IsActive = true };
        var cc = new CostCenter { TenantId = tenant, CompanyId = company.Id, Code = "OPS", Name = "Ops" };
        var dept = new Department { TenantId = tenant, CompanyId = company.Id, Code = "D", NameEn = "D", CostCenterId = cc.Id };
        company.DefaultCostCenterId = null;
        db.AddRange(company, cc, dept);
        await db.SaveChangesAsync();

        var snap = await CostCentreResolver.LoadSnapshotAsync(db, tenant, company.Id, default);
        var r = CostCentreResolver.Resolve(new ResolverEmployee(dept.Id, null, null, false), snap);
        (r.CostCenterId, r.Source).Should().Be((cc.Id, "Department"));
    }
}
