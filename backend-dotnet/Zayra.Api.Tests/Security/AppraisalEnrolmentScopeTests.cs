using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Performance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Performance;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Security.SeededRoleBundles;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Who a launched cycle enrols: the active employees of the company it was created for (and of its department,
/// when one is chosen) who had joined by the end of the review period. It used to enrol every active employee
/// of every company in the tenant.
/// </summary>
public class AppraisalEnrolmentScopeTests
{
    [Fact]
    public async Task Launch_EnrolsOnlyTheChosenCompany_AndOnlyPeopleWhoJoinedByThePeriodEnd()
    {
        var (db, tenantId) = await NewTenantAsync("enrol-co");
        var w = await SeedAsync(db, tenantId);
        var hr = await CallerAsync(db, tenantId, "HR Director");
        var cycles = Cycles(db, hr);

        var created = await cycles.Create(Request(w.Template, companyId: w.CompanyA), default);
        var cycleId = ((PerformanceCycle)((CreatedResult)created).Value!).Id;
        (await db.PerformanceCycles.AsNoTracking().SingleAsync(c => c.Id == cycleId)).CompanyId.Should().Be(w.CompanyA);

        var launched = await cycles.Launch(cycleId, default);
        StatusOf(launched).Should().Be(200);
        var enrolled = await db.AppraisalReviews.AsNoTracking().Where(r => r.CycleId == cycleId).Select(r => r.EmployeeId).ToListAsync();
        enrolled.Should().BeEquivalentTo(new[] { w.AOps, w.ASales },
            "company B's employee, a joiner after the period end and a terminated employee are not appraised in this cycle");
    }

    [Fact]
    public async Task Launch_WithADepartment_EnrolsOnlyThatDepartment()
    {
        var (db, tenantId) = await NewTenantAsync("enrol-dept");
        var w = await SeedAsync(db, tenantId);
        var hr = await CallerAsync(db, tenantId, "HR Director");
        var cycles = Cycles(db, hr);

        var cycleId = ((PerformanceCycle)((CreatedResult)await cycles.Create(Request(w.Template, w.CompanyA, w.Ops), default)).Value!).Id;
        StatusOf(await cycles.Launch(cycleId, default)).Should().Be(200);

        (await db.AppraisalReviews.AsNoTracking().Where(r => r.CycleId == cycleId).Select(r => r.EmployeeId).ToListAsync())
            .Should().BeEquivalentTo(new[] { w.AOps });
    }

    [Fact]
    public async Task Launch_InAMultiCompanyTenant_WithNoCompanyChosen_IsRefused_AndEnrolsNobody()
    {
        var (db, tenantId) = await NewTenantAsync("enrol-none");
        var w = await SeedAsync(db, tenantId);
        var hr = await CallerAsync(db, tenantId, "HR Director");
        var cycles = Cycles(db, hr);

        var cycleId = ((PerformanceCycle)((CreatedResult)await cycles.Create(Request(w.Template, null), default)).Value!).Id;
        var refused = await cycles.Launch(cycleId, default);

        StatusOf(refused).Should().Be(400);
        JsonSerializer.Serialize(((ObjectResult)refused).Value).Should().Contain("cycle_company_required");
        (await db.AppraisalReviews.CountAsync(r => r.CycleId == cycleId)).Should().Be(0);
        (await db.PerformanceCycles.AsNoTracking().SingleAsync(c => c.Id == cycleId)).Status.Should().Be("Draft");
    }

    [Fact]
    public async Task ADraftCycle_CanBeEditedToChooseItsCompanyAndTemplate_ThenLaunched()
    {
        var (db, tenantId) = await NewTenantAsync("enrol-edit");
        var w = await SeedAsync(db, tenantId);
        var hr = await CallerAsync(db, tenantId, "HR Director");
        var cycles = Cycles(db, hr);
        var draft = Request(null, null);
        var cycleId = ((PerformanceCycle)((CreatedResult)await cycles.Create(draft, default)).Value!).Id;

        StatusOf(await cycles.Update(cycleId, Request(w.Template, w.CompanyA), default)).Should().Be(200);
        StatusOf(await cycles.Launch(cycleId, default)).Should().Be(200);
        (await db.AppraisalReviews.CountAsync(r => r.CycleId == cycleId)).Should().Be(2);
    }

    [Fact]
    public async Task ACycleCannotNameAnotherTenantsCompany_OrADepartmentOutsideItsCompany()
    {
        var (db, tenantId) = await NewTenantAsync("enrol-foreign");
        var w = await SeedAsync(db, tenantId);
        var hr = await CallerAsync(db, tenantId, "HR Director");
        var cycles = Cycles(db, hr);

        StatusOf(await cycles.Create(Request(w.Template, Guid.NewGuid()), default)).Should().Be(400);
        StatusOf(await cycles.Create(Request(w.Template, w.CompanyB, w.Ops), default)).Should().Be(400,
            "Operations belongs to company A");
    }

    // ── Harness ────────────────────────────────────────────────────────────────────────────────

    private sealed record World(Guid CompanyA, Guid CompanyB, Guid Ops, Guid Sales, Guid Template, int AOps, int ASales, int B, int Late, int Gone);

    private static CreateCycleRequest Request(Guid? template, Guid? companyId, Guid? departmentId = null) =>
        new("FY26 Annual", "Annual", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            false, false, true, false, null, null, null, template, null, companyId, departmentId);

    private static CyclesController Cycles(ZayraDbContext db, System.Security.Claims.ClaimsPrincipal caller) =>
        Bind(new CyclesController(db, new PerformanceService(db), new DataScopeService(db)), caller);

    private static async Task<World> SeedAsync(ZayraDbContext db, Guid tenantId)
    {
        var a = new Company { TenantId = tenantId, LegalNameEn = "Alpha", RegistrationNumber = $"A-{Guid.NewGuid():N}"[..20], IsActive = true };
        var b = new Company { TenantId = tenantId, LegalNameEn = "Beta", RegistrationNumber = $"B-{Guid.NewGuid():N}"[..20], IsActive = true };
        db.Companies.AddRange(a, b);
        var ops = new Department { TenantId = tenantId, CompanyId = a.Id, Code = "OPS", NameEn = "Operations", NameAr = "العمليات" };
        var sales = new Department { TenantId = tenantId, CompanyId = a.Id, Code = "SAL", NameEn = "Sales", NameAr = "المبيعات" };
        db.Departments.AddRange(ops, sales);
        var template = new PerformanceScorecardTemplate { TenantId = tenantId, Name = "Std", IsActive = true };
        db.PerformanceScorecardTemplates.Add(template);
        Employee E(Guid company, Guid? dept, string name, DateTime joined, string status = "Active") => new()
        {
            TenantId = tenantId, CompanyId = company, DepartmentId = dept, EmployeeCode = $"EN{Guid.NewGuid():N}"[..12],
            FullName = name, Status = status, JoiningDate = joined,
        };
        var aOps = E(a.Id, ops.Id, "Alpha Ops", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var aSales = E(a.Id, sales.Id, "Alpha Sales", new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc));
        var bEmp = E(b.Id, null, "Beta Person", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var late = E(a.Id, ops.Id, "Late Joiner", new DateTime(2027, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        var gone = E(a.Id, ops.Id, "Gone Person", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), "Terminated");
        db.Employees.AddRange(aOps, aSales, bEmp, late, gone);
        await db.SaveChangesAsync();
        return new World(a.Id, b.Id, ops.Id, sales.Id, template.Id, aOps.Id, aSales.Id, bEmp.Id, late.Id, gone.Id);
    }
}
