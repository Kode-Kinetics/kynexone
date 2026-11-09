using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// <c>PATCH /api/employees/{id}/status</c> to a status that ends the employment. Terminated is the reference: it closes the linked login and its
/// refresh tokens, clears WPS eligibility and records a separation. Retired and Deceased end employment too, so they must do exactly the same
/// (a retired or deceased person's login must not keep authenticating, and they must not stay in the bank file). Real PostgreSQL, because
/// the credential closure uses bulk token updates.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class EmployeeSeparatedStatusPostgresTests
{
    private readonly PostgresFixture _fx;
    public EmployeeSeparatedStatusPostgresTests(PostgresFixture fx) => _fx = fx;

    private sealed record Seed(Guid TenantId, Guid CompanyId, Employee Employee, Guid? UserId, Guid? LinkId, Guid? RefreshId);

    private async Task<Seed> SeedAsync(bool withLogin)
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company { Id = Guid.NewGuid(), TenantId = tenantId, LegalNameEn = "Sep PG", CountryCode = "SAU", Jurisdiction = "KSA-mainland", RegistrationNumber = $"S-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true, CreatedAtUtc = DateTime.UtcNow };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var employee = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "SEP-1", FullName = "Sep One", EnglishName = "Sep One",
            Status = "Active", ReadinessState = "Ready", JoiningDate = DateTime.SpecifyKind(new DateTime(2020, 1, 1), DateTimeKind.Utc),
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = tenantId, EmployeeId = employee.Id, WpsEligible = true });
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure { TenantId = tenantId, EmployeeId = employee.Id, SalaryStructureId = Guid.NewGuid(), BasicSalary = 8000m, EffectiveDate = new DateOnly(2020, 1, 1), IsActive = true });
        await db.SaveChangesAsync();
        if (!withLogin) return new Seed(tenantId, company.Id, employee, null, null, null);

        var user = new User
        {
            TenantId = tenantId, Email = $"{Guid.NewGuid():N}@sep.test", NormalizedEmail = $"{Guid.NewGuid():N}@SEP.TEST", FullName = "Sep One",
            PasswordHash = "unimportant", Status = "Active", AccessMode = AccessModes.EssOnly, IsActive = true, IsEmailConfirmed = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var row = await db.Employees.SingleAsync(e => e.Id == employee.Id);
        row.UserAccountId = user.Id;
        var link = new EmployeeUserAccount { TenantId = tenantId, EmployeeId = employee.Id, UserId = user.Id, AccessMode = AccessModes.EssOnly, Status = "Active", InvitationTokenHash = "live", InvitationExpiresAtUtc = DateTime.UtcNow.AddDays(1) };
        var refresh = new RefreshToken { UserId = user.Id, TokenHash = "live-refresh-" + Guid.NewGuid().ToString("N"), ExpiresAtUtc = DateTime.UtcNow.AddDays(7) };
        db.AddRange(link, refresh);
        await db.SaveChangesAsync();
        return new Seed(tenantId, company.Id, employee, user.Id, link.Id, refresh.Id);
    }

    private static async Task<IActionResult?> ChangeAsync(PostgresFixture fx, Seed s, string status)
    {
        await using var db = fx.CreateDb();
        var controller = new EmployeesController(db, new Pbkdf2PasswordHasher(), new AuditService(db), new NullDocumentStorage(), TestNotifications.For(db),
            new SepHijri(), new Zayra.Api.Infrastructure.Common.DataScopeService(db), new SepLetters());
        var claims = new List<Claim>
        {
            new("tenant_id", s.TenantId.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new(ClaimTypes.Role, "Admin"),
            new("permission", "employees.read"), new("permission", "employees.write"), new("is_group_scope", "true"),
        };
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) } };
        var service = new EmployeeManagementService(db, new AuditService(db), new NullDocumentStorage(), TestNotifications.For(db));
        var result = await controller.ChangeStatus(s.Employee.Id, new EmployeeStatusChangeRequest(status, new DateOnly(2026, 11, 1), "status test"), service, CancellationToken.None);
        return result.Result;
    }

    [Theory]
    [InlineData("Terminated")]
    [InlineData("Retired")]
    [InlineData("Deceased")]
    public async Task EndingEmployment_ClosesTheLogin_AndItsRefreshTokens(string status)
    {
        var s = await SeedAsync(withLogin: true);

        Assert.IsType<OkObjectResult>(await ChangeAsync(_fx, s, status));

        await using var read = _fx.CreateDb();
        (await read.Employees.IgnoreQueryFilters().SingleAsync(e => e.Id == s.Employee.Id)).Status.Should().Be(status);
        var link = await read.EmployeeUserAccounts.IgnoreQueryFilters().SingleAsync(x => x.Id == s.LinkId);
        link.AccessMode.Should().Be(AccessModes.NoLogin, $"a {status} employee's self-service login must not authenticate");
        link.InvitationTokenHash.Should().BeEmpty();
        var user = await read.Users.IgnoreQueryFilters().SingleAsync(x => x.Id == s.UserId);
        user.IsActive.Should().BeFalse();
        (await read.RefreshTokens.SingleAsync(x => x.Id == s.RefreshId)).RevokedAtUtc.Should().NotBeNull("a live refresh token kept minting access tokens");
    }

    [Theory]
    [InlineData("Terminated")]
    [InlineData("Retired")]
    [InlineData("Deceased")]
    public async Task EndingEmployment_ClearsWpsEligibility_KeepsTheSalaryStructureForTheFinalSettlement_AndRecordsASeparation(string status)
    {
        var s = await SeedAsync(withLogin: false);

        Assert.IsType<OkObjectResult>(await ChangeAsync(_fx, s, status));

        await using var read = _fx.CreateDb();
        (await read.EmployeePayrollProfiles.IgnoreQueryFilters().SingleAsync(p => p.EmployeeId == s.Employee.Id)).WpsEligible
            .Should().BeFalse("a separated employee must not stay in the bank file");
        (await read.EmployeeSalaryStructures.IgnoreQueryFilters().SingleAsync(x => x.EmployeeId == s.Employee.Id)).IsActive
            .Should().BeTrue("the end-of-service and final-settlement calculators read the active salary row of a non-deleted leaver, exactly as for Terminated");
        var separation = await read.EmployeeOffboardings.IgnoreQueryFilters().SingleAsync(o => o.EmployeeId == s.Employee.Id);
        separation.LastWorkingDay.Should().Be(new DateOnly(2026, 11, 1));
    }

    [Theory]
    [InlineData("Retired")]
    [InlineData("Deceased")]
    public async Task EndingEmployment_OfSomeoneWithNoAccount_StillWorks(string status)
    {
        var s = await SeedAsync(withLogin: false);

        var result = await ChangeAsync(_fx, s, status);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.IsType<EmployeeDetailDto>(ok.Value).Status.Should().Be(status);
    }
}

file sealed class SepHijri : Zayra.Api.Infrastructure.Localization.IHijriDateService
{
    public Zayra.Api.Infrastructure.Localization.DateConversionDto FromGregorian(DateOnly date) =>
        new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
}

file sealed class SepLetters : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
}
