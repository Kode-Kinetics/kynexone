using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class EncashmentControllerTests
{
    [Fact]
    public async Task Create_DerivesAmountFromScopedCompanyStatutoryDivisor_AndReservesBalance()
    {
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenantId = Guid.NewGuid();
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "Saudi Co", CountryCode = CountryCodes.Saudi,
            Jurisdiction = Jurisdictions.KsaMainland, IsActive = true
        };
        var leaveType = new LeaveType { TenantId = tenantId, Code = "AL", NameEn = "Annual", IsActive = true };
        db.AddRange(company, leaveType);
        await db.SaveChangesAsync();
        var employee = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "ENC-1", FullName = "Encash Employee",
            Salary = 2600m, Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-2)
        };
        db.Employees.Add(employee);
        db.LeavePolicies.Add(new LeavePolicy
        {
            TenantId = tenantId, CompanyId = company.Id, LeaveTypeId = leaveType.Id, Name = "Annual",
            Status = "Active", EncashmentAllowed = true, EncashmentMaxDays = 10
        });
        await db.SaveChangesAsync();
        db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
        {
            TenantId = tenantId, EmployeeId = employee.Id, LeaveTypeId = leaveType.Id,
            Year = DateTime.UtcNow.Year, Entitled = 10
        });
        await db.SaveChangesAsync();
        var rules = new FixedRules(26m);
        var controller = new EncashmentController(db, new OwnScope(employee.Id), rules)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", tenantId.ToString()),
                        new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())
                    }, "Test"))
                }
            }
        };

        var result = await controller.Create(new CreateEncashmentRequest(employee.Id, leaveType.Id, 2m, 9999m, "cash out", null), CancellationToken.None);

        result.Should().BeOfType<CreatedResult>();
        var saved = await db.LeaveEncashmentRequests.SingleAsync();
        saved.AmountPerDay.Should().Be(100m);
        saved.TotalAmount.Should().Be(200m);
        (await db.EmployeeLeaveBalances.SingleAsync()).Pending.Should().Be(2m);
        rules.LastLookup.Should().NotBeNull();
        rules.LastLookup!.Value.Country.Should().Be(CountryCodes.Saudi);
        rules.LastLookup.Value.Jurisdiction.Should().Be(Jurisdictions.KsaMainland);
        rules.LastLookup.Value.Tenant.Should().Be(tenantId);
    }

    private sealed class OwnScope(int employeeId) : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Own, CallerEmployeeId = employeeId, AllowedEmployeeIds = new[] { employeeId } });
    }

    private sealed class FixedRules(decimal divisor) : IStatutoryRuleReader
    {
        public (string Country, string Jurisdiction, Guid? Tenant)? LastLookup { get; private set; }
        public Task<decimal?> GetDecimalAsync(string countryCode, string jurisdiction, string ruleKey, DateOnly effectiveDate, Guid? tenantId = null, CancellationToken ct = default)
        {
            LastLookup = (countryCode, jurisdiction, tenantId);
            return Task.FromResult<decimal?>(divisor);
        }
        public Task<string?> GetStringAsync(string countryCode, string jurisdiction, string ruleKey, DateOnly effectiveDate, Guid? tenantId = null, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }
}
