using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>Offboarding actions respect the caller's company scope: company-A admins never see or act on
/// company-B separations; group admins see both; a foreign tenant sees neither.</summary>
public class OffboardingCompanyScopeTests
{
    private static async Task<(ScopeSodWorld W, Guid OffA, Guid OffB)> SeedAsync()
    {
        var w = await ScopeSodWorld.CreateAsync();
        await using var db = w.Plain();
        var a = new EmployeeOffboarding { TenantId = w.Tenant, CompanyId = w.CompanyA, EmployeeId = w.EmpA, EmployeeName = "Alpha Emp", EmployeeCode = "A-001", Status = "InProgress", NoticeDate = new DateOnly(2026, 10, 1), LastWorkingDay = new DateOnly(2026, 11, 1) };
        var b = new EmployeeOffboarding { TenantId = w.Tenant, CompanyId = w.CompanyB, EmployeeId = w.EmpB, EmployeeName = "Beta Emp", EmployeeCode = "B-001", Status = "InProgress", NoticeDate = new DateOnly(2026, 10, 1), LastWorkingDay = new DateOnly(2026, 11, 1) };
        db.EmployeeOffboardings.AddRange(a, b);
        await db.SaveChangesAsync();
        return (w, a.Id, b.Id);
    }

    private static OffboardingController Ctl(ScopeSodWorld w, ClaimsPrincipal p)
    {
        var db = w.DbFor(p);
        return ScopeSodWorld.Wire(new OffboardingController(db, new AuditService(db)), p);
    }

    [Fact]
    public async Task WriteGuard_StampsCompanyFromEmployee_AndRefusesForeignCompanyEmployee()
    {
        var w = await ScopeSodWorld.CreateAsync();
        var admin = w.GroupAdmin();
        await using (var db = w.DbFor(admin))
        {
            var off = new EmployeeOffboarding { TenantId = w.Tenant, EmployeeId = w.EmpB, EmployeeName = "Beta Emp", EmployeeCode = "B-001", Status = "InProgress", NoticeDate = new DateOnly(2026, 10, 1), LastWorkingDay = new DateOnly(2026, 11, 1) };
            db.EmployeeOffboardings.Add(off);
            await db.SaveChangesAsync();
            off.CompanyId.Should().Be(w.CompanyB, "the central guard stamps the company from the employee");
        }

        await using var scoped = w.DbFor(w.CompanyAdmin(w.CompanyA));
        scoped.EmployeeOffboardings.Add(new EmployeeOffboarding { TenantId = w.Tenant, EmployeeId = w.EmpB, EmployeeName = "Beta Emp", EmployeeCode = "B-001", Status = "InProgress", NoticeDate = new DateOnly(2026, 10, 1), LastWorkingDay = new DateOnly(2026, 11, 1) });
        await FluentActions.Awaiting(() => scoped.SaveChangesAsync()).Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task CompanyAdmin_ListsOnlyOwnCompanySeparations()
    {
        var (w, offA, _) = await SeedAsync();
        var ok = (await Ctl(w, w.CompanyAdmin(w.CompanyA)).List(null, default)).Should().BeOfType<OkObjectResult>().Subject;
        ((IEnumerable<EmployeeOffboarding>)ok.Value!).Select(o => o.Id).Should().BeEquivalentTo(new[] { offA });
    }

    [Fact]
    public async Task CompanyAdmin_CannotGetAnotherCompanysSeparation()
    {
        var (w, offA, offB) = await SeedAsync();
        var c = Ctl(w, w.CompanyAdmin(w.CompanyA));
        (await c.Get(offB, default)).Should().BeOfType<NotFoundResult>();
        (await c.Get(offA, default)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task CompanyAdmin_CannotCompleteCancelOrRevokeAnotherCompanysSeparation()
    {
        var (w, _, offB) = await SeedAsync();
        var c = Ctl(w, w.CompanyAdmin(w.CompanyA));
        (await c.RevokeAccess(offB, default)).Should().BeOfType<NotFoundResult>();
        (await c.Complete(offB, default)).Should().BeOfType<NotFoundResult>();
        (await c.Cancel(offB, new CancelOffboardingRequest("x"), default)).Should().BeOfType<NotFoundResult>();

        await using var check = w.Plain();
        var emp = await check.Employees.AsNoTracking().SingleAsync(e => e.Id == w.EmpB);
        emp.Status.Should().Be("Active", "a refused cross-company action must change nothing");
        (await check.EmployeeOffboardings.AsNoTracking().SingleAsync(o => o.Id == offB)).Status.Should().Be("InProgress");
    }

    [Fact]
    public async Task CompanyAdmin_CannotInitiateForAnotherCompanysEmployee()
    {
        var (w, _, _) = await SeedAsync();
        await using (var seed = w.Plain())
        {
            var open = await seed.EmployeeOffboardings.Where(o => o.EmployeeId == w.EmpB).ToListAsync();
            seed.EmployeeOffboardings.RemoveRange(open);
            await seed.SaveChangesAsync();
        }
        var r = await Ctl(w, w.CompanyAdmin(w.CompanyA)).Initiate(new InitiateOffboardingRequest(w.EmpB, "Resignation", "r", new DateOnly(2026, 10, 1), 30, null), default);
        r.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GroupAdmin_SeesBothCompanies()
    {
        var (w, offA, offB) = await SeedAsync();
        var ok = (await Ctl(w, w.GroupAdmin()).List(null, default)).Should().BeOfType<OkObjectResult>().Subject;
        ((IEnumerable<EmployeeOffboarding>)ok.Value!).Select(o => o.Id).Should().BeEquivalentTo(new[] { offA, offB });
    }

    [Fact]
    public async Task ForeignTenantAdmin_SeesNothing()
    {
        var (w, _, offB) = await SeedAsync();
        var c = Ctl(w, w.ForeignTenantAdmin());
        (await c.Get(offB, default)).Should().BeOfType<NotFoundResult>();
        var ok = (await c.List(null, default)).Should().BeOfType<OkObjectResult>().Subject;
        ((IEnumerable<EmployeeOffboarding>)ok.Value!).Should().BeEmpty();
    }
}
