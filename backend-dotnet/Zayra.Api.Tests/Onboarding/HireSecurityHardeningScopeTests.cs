using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees.Onboarding;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Employees.Onboarding;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Onboarding;

/// <summary>The manager lookup applies the caller's employee-level scope and does not reveal which ids exist.</summary>
public class HireSecurityHardeningScopeTests
{
    private sealed class FixedScope(DataScope scope) : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) => Task.FromResult(scope);
    }

    private static async Task<(ZayraDbContext Db, Guid Tenant, Guid Company, Employee Visible, Employee Hidden)> SeedAsync()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid();
        var company = new Company
        {
            TenantId = tenant, LegalNameEn = "Main", TradeName = "Main", CountryCode = "SA", Jurisdiction = "t",
            RegistrationNumber = "RC-" + Guid.NewGuid().ToString("N"), DefaultCurrency = "SAR", IsActive = true, CreatedAtUtc = DateTime.UtcNow,
        };
        var visible = new Employee { TenantId = tenant, CompanyId = company.Id, EmployeeCode = "V1", FullName = "Visible", EnglishName = "Visible", Status = "Active" };
        var hidden = new Employee { TenantId = tenant, CompanyId = company.Id, EmployeeCode = "H1", FullName = "Hidden", EnglishName = "Hidden", Status = "Active" };
        db.Companies.Add(company);
        db.Employees.AddRange(visible, hidden);
        await db.SaveChangesAsync();
        return (db, tenant, company.Id, visible, hidden);
    }

    private static EmployeeInputGuard Guard(ZayraDbContext db, Employee visible)
    {
        var http = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString())], "test")) },
        };
        var scope = new DataScope { Level = DataScopeLevel.Team, AllowedEmployeeIds = [visible.Id] };
        return new EmployeeInputGuard(db, dataScope: new FixedScope(scope), http: http);
    }

    private static async Task<EmployeeValidationException> Refused(Func<Task> act) => await Assert.ThrowsAsync<EmployeeValidationException>(act);

    [Fact]
    public async Task ManagerOutOfScope_AnswersExactlyLikeAMissingManager()
    {
        var (db, tenant, company, visible, hidden) = await SeedAsync();
        var ctx = new RequestContext("127.0.0.1", "test", Guid.NewGuid(), tenant);
        var request = EmployeeInputValidatorTests.Req() with { CompanyId = company };

        var outOfScope = await Refused(() => Guard(db, visible).EnforceCreateAsync(tenant, request with { ReportingManagerEmployeeId = hidden.Id }, ctx, default));
        var missing = await Refused(() => Guard(db, visible).EnforceCreateAsync(tenant, request with { ReportingManagerEmployeeId = 987_654 }, ctx, default));

        outOfScope.Status.Should().Be(missing.Status);
        outOfScope.Code.Should().Be("invalid_reference").And.Be(missing.Code);
        outOfScope.Field.Should().Be(missing.Field);
        outOfScope.Detail.Should().Be(missing.Detail);
        outOfScope.Errors.Should().BeEquivalentTo(missing.Errors);

        var ok = await Guard(db, visible).EnforceCreateAsync(tenant, request with { ReportingManagerEmployeeId = visible.Id }, ctx, default);
        ok.ReportingManagerEmployeeId.Should().Be(visible.Id);
    }
}
