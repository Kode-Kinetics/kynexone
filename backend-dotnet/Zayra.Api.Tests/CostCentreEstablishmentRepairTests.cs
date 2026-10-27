using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Cost-centre accounting v1, WP-A3 item 6: the establishment "reconcile departments" repair stamps a DepartmentId on people who only have a free-text
/// department. That is not "no change in meaning" for cost: a person with no department resolves to the company default (or nowhere) and with one
/// resolves to the department's cost centre. So it needs the cost-centre permission, leaves alone anyone in a run awaiting finance review or lock
/// (reported, and repaired on a later run of this idempotent job), and audits every person it moves.
/// </summary>
public class CostCentreEstablishmentRepairTests
{
    private static readonly string[] Establishment = ["organization.read", "organization.write", "organization.establishment.write"];

    private sealed record World(ZayraDbContext Db, Guid Tenant, Company A, Department Dept, CostCenter Cc, Employee Free, Employee InRun);

    private static async Task<World> Seed()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid();
        var a = new Company { TenantId = tenant, LegalNameEn = "Alpha", RegistrationNumber = "A-1", IsActive = true, CountryCode = "SA", DefaultCurrency = "SAR" };
        var cc = new CostCenter { TenantId = tenant, CompanyId = a.Id, Code = "OPS", Name = "Ops" };
        var dept = new Department { TenantId = tenant, CompanyId = a.Id, Code = "OPSD", NameEn = "Operations", CostCenterId = cc.Id, IsActive = true };
        Employee Legacy(string code) => new() { TenantId = tenant, CompanyId = a.Id, EmployeeCode = code, FullName = code, Status = "Active", JoiningDate = DateTime.UtcNow, Department = "Operations" };
        var free = Legacy("FREE");
        var inRun = Legacy("INRUN");
        db.AddRange(a, cc, dept, free, inRun);
        await db.SaveChangesAsync();
        var run = new PayrollRun { TenantId = tenant, CompanyId = a.Id, Year = 2026, Month = 6, Status = "PendingFinanceReview" };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        db.PayrollSlips.Add(new PayrollSlip { TenantId = tenant, CompanyId = a.Id, RunId = run.Id, EmployeeId = inRun.Id });
        await db.SaveChangesAsync();
        return new World(db, tenant, a, dept, cc, free, inRun);
    }

    private static Task<IActionResult> Reconcile(World w, params string[] extra) =>
        EstablishmentApiTests.CreateController(w.Db, w.Tenant, Establishment.Concat(extra).ToArray()).ReconcileDepartments(default);

    [Fact]
    public async Task S027_TheRepair_NeedsTheCostCentrePermission_AsWellAsTheEstablishmentOne()
    {
        var w = await Seed();
        (await Reconcile(w)).Should().BeOfType<ForbidResult>();
        (await w.Db.Employees.AsNoTracking().CountAsync(e => e.DepartmentId != null)).Should().Be(0);
    }

    [Fact]
    public async Task S029_TheRepair_StampsThePeopleNotInALockingRun_AndAuditsEachWithBeforeAndAfter()
    {
        var w = await Seed();
        var ok = (OkObjectResult)await Reconcile(w, "organization.costcenter.manage");
        var payload = JsonSerializer.Serialize(ok.Value);
        payload.Should().Contain("\"resolvedCount\":1").And.Contain("\"lockedIntoRun\":1");

        (await w.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == w.Free.Id)).DepartmentId.Should().Be(w.Dept.Id);
        (await w.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == w.InRun.Id)).DepartmentId.Should().BeNull("a person in a run awaiting review or lock is left for a later run of the repair");

        var row = await w.Db.AuditLogs.SingleAsync(a => a.Action == "employee.cost_attribution_changed");
        (row.CompanyId, row.EntityId).Should().Be((w.A.Id, w.Free.Id.ToString()));
        JsonDocument.Parse(row.Metadata!).RootElement.GetProperty("after").GetProperty("departmentId").GetGuid().Should().Be(w.Dept.Id);
    }

    [Fact]
    public async Task S029_OnceTheRunIsLocked_TheSameRepairStampsTheRest()
    {
        var w = await Seed();
        (await w.Db.PayrollRuns.SingleAsync()).Status = "Locked";
        await w.Db.SaveChangesAsync();
        JsonSerializer.Serialize(((OkObjectResult)await Reconcile(w, "organization.costcenter.manage")).Value).Should().Contain("\"resolvedCount\":2");
    }
}
