using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.Employees.Onboarding;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Employees.Onboarding;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;
using Xunit;

namespace Zayra.Api.Tests.Onboarding;

/// <summary>CD28: rescinding an offboarding (Offboarded to Active) re-runs the hard rules over the stored data.</summary>
public class HireReinstatementRevalidationTests
{
    private static async Task<(OffboardingController C, ZayraDbContext Db, EmployeeOffboarding Off, Employee E)> Arrange(string iban)
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenantId = Guid.NewGuid();
        var e = new Employee { Id = 2001, TenantId = tenantId, EmployeeCode = "EMP-R1", FullName = "Reinstated Person", EnglishName = "Reinstated Person",
            Status = EmployeeStatuses.Offboarded, CountryCode = "SA", Nationality = "Saudi", JoiningDate = DateTime.UtcNow.AddYears(-1) };
        var off = new EmployeeOffboarding { TenantId = tenantId, EmployeeId = e.Id, EmployeeCode = e.EmployeeCode, EmployeeName = e.FullName,
            Status = "InProgress", LastWorkingDay = DateOnly.FromDateTime(DateTime.UtcNow) };
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "R", Slug = "r-" + tenantId.ToString("N"), IsActive = true });
        db.Employees.Add(e); db.EmployeeOffboardings.Add(off);
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = tenantId, EmployeeId = e.Id, Iban = iban });
        await db.SaveChangesAsync();
        var services = new ServiceCollection().AddSingleton<IEmployeeInputGuard>(new EmployeeInputGuard(db)).BuildServiceProvider();
        var c = new OffboardingController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services,
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id", tenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, "Admin")], "test")) } },
        };
        return (c, db, off, e);
    }

    [Fact]
    public async Task Rescind_WithAnInvalidStoredIban_IsRefused_AndTheEmployeeStaysOffboarded()
    {
        var (c, db, off, e) = await Arrange("SA0000000000000000000001");
        var ex = await Assert.ThrowsAsync<EmployeeValidationException>(() => c.Cancel(off.Id, null, default));
        ex.Errors["payrollProfile.iban"].Should().Contain("iban_invalid");
        (await db.Employees.AsNoTracking().SingleAsync(x => x.Id == e.Id)).Status.Should().Be(EmployeeStatuses.Offboarded);
    }

    [Fact]
    public async Task Rescind_WithValidStoredData_Reinstates()
    {
        var (c, db, off, e) = await Arrange(IbanValidator.WithValidCheckDigits("SA0380000000608010167519"));
        var r = await c.Cancel(off.Id, null, default);
        r.Should().BeOfType<OkObjectResult>();
        (await db.Employees.AsNoTracking().SingleAsync(x => x.Id == e.Id)).Status.Should().Be(EmployeeStatuses.Active);
    }
}
