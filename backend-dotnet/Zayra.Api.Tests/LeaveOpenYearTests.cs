using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// "Open this year's balances": the one action an HR admin has to get a leave year going. Nothing in the product
/// scheduled the monthly accrual and nothing called the accrue endpoint, so a company that went live without
/// imported balances had no balance row for any employee and every annual-leave request was refused.
/// The action reuses the policy engine (monthly accrual for every month so far; a front-loaded policy's whole
/// entitlement) and never invents an entitlement of its own.
/// </summary>
public class LeaveOpenYearTests
{
    private static readonly DateTime Now = Utc(2026, 11, 15);

    private static DateTime Utc(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    private static DbContextOptions<ZayraDbContext> Options() =>
        new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    private sealed record World(Guid TenantId, Company Company, LeaveType Annual, LeaveType Sick, LeavePolicy AnnualPolicy, LeavePolicy SickPolicy, Employee Employee);

    private static async Task<World> SeedAsync(ZayraDbContext db, DateTime joined, string country = "AE", decimal annualDays = 24m)
    {
        var tenantId = Guid.NewGuid();
        var company = new Company { TenantId = tenantId, LegalNameEn = "Open-year fixture", CountryCode = country, RegistrationNumber = $"R-{Guid.NewGuid():N}" };
        var annual = new LeaveType { TenantId = tenantId, CompanyId = company.Id, Code = "ANNUAL", NameEn = "Annual Leave", Category = "Annual", IsActive = true };
        var sick = new LeaveType { TenantId = tenantId, CompanyId = company.Id, Code = "SICK", NameEn = "Sick Leave", Category = "Sick", IsActive = true };
        var employee = new Employee { TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "OY-1", FullName = "Open Year", Status = "Active", JoiningDate = joined };
        var annualPolicy = new LeavePolicy { TenantId = tenantId, CompanyId = company.Id, LeaveTypeId = annual.Id, Name = "Annual", Status = "Active", AccrualMethod = "Monthly", AnnualEntitlementDays = annualDays };
        var sickPolicy = new LeavePolicy { TenantId = tenantId, CompanyId = company.Id, LeaveTypeId = sick.Id, Name = "Sick", Status = "Active", AccrualMethod = "Yearly", AnnualEntitlementDays = 120m };
        db.AddRange(company, annual, sick, employee, annualPolicy, sickPolicy);
        await db.SaveChangesAsync();
        return new World(tenantId, company, annual, sick, annualPolicy, sickPolicy, employee);
    }

    private static LeaveService Service(ZayraDbContext db) => new(db, new ApprovalRouter(db));

    [Fact]
    public async Task OpenYear_CreatesTheMissingBalances_FromTheMonthlyPolicyForEveryMonthSoFar()
    {
        await using var db = new ZayraDbContext(Options());
        var w = await SeedAsync(db, Utc(2025, 1, 1));
        (await db.EmployeeLeaveBalances.CountAsync()).Should().Be(0, "precondition: the live company's situation - no balance rows at all");

        var result = await Service(db).OpenYearBalancesAsync(w.TenantId, 2026, Now);

        var annual = await db.EmployeeLeaveBalances.SingleAsync(b => b.LeaveTypeId == w.Annual.Id);
        annual.Year.Should().Be(2026);
        annual.Accrued.Should().Be(22m, "24 days a year is 2 a month, January to November");
        annual.Entitled.Should().Be(0m, "a monthly policy grows Accrued; it must not also grant the whole year");
        (await db.LeaveBalanceTransactions.CountAsync(t => t.LeaveTypeId == w.Annual.Id && t.TransactionType == "Accrual")).Should().Be(11);
        result.Year.Should().Be(2026);
        result.BalancesCreated.Should().Be(2);
        result.AccrualEntries.Should().Be(11);
    }

    [Fact]
    public async Task OpenYear_IsIdempotent_ARerunChangesNothing()
    {
        await using var db = new ZayraDbContext(Options());
        var w = await SeedAsync(db, Utc(2025, 1, 1));
        var service = Service(db);
        await service.OpenYearBalancesAsync(w.TenantId, 2026, Now);
        var firstBalances = await db.EmployeeLeaveBalances.AsNoTracking().OrderBy(b => b.LeaveTypeName).ToListAsync();
        var firstLedger = await db.LeaveBalanceTransactions.CountAsync();

        var again = await service.OpenYearBalancesAsync(w.TenantId, 2026, Now);

        again.BalancesCreated.Should().Be(0);
        again.AccrualEntries.Should().Be(0);
        again.AllocationEntries.Should().Be(0);
        (await db.LeaveBalanceTransactions.CountAsync()).Should().Be(firstLedger);
        var secondBalances = await db.EmployeeLeaveBalances.AsNoTracking().OrderBy(b => b.LeaveTypeName).ToListAsync();
        secondBalances.Select(b => (b.Entitled, b.Accrued)).Should().Equal(firstBalances.Select(b => (b.Entitled, b.Accrued)));
    }

    [Fact]
    public async Task OpenYear_FrontLoadsAYearlyPolicyIntoEntitled_Once()
    {
        await using var db = new ZayraDbContext(Options());
        var w = await SeedAsync(db, Utc(2025, 1, 1));
        var service = Service(db);

        var result = await service.OpenYearBalancesAsync(w.TenantId, 2026, Now);
        await service.OpenYearBalancesAsync(w.TenantId, 2026, Now);

        var sick = await db.EmployeeLeaveBalances.SingleAsync(b => b.LeaveTypeId == w.Sick.Id);
        sick.Entitled.Should().Be(120m, "the policy's own entitlement, front-loaded");
        sick.Accrued.Should().Be(0m);
        var ledger = await db.LeaveBalanceTransactions.Where(t => t.LeaveTypeId == w.Sick.Id).ToListAsync();
        ledger.Should().ContainSingle().Which.TransactionType.Should().Be("Allocation");
        result.AllocationEntries.Should().Be(1);
    }

    [Fact]
    public async Task OpenYear_DoesNotGrantAnEmployeeWhoHasNotJoinedYet()
    {
        await using var db = new ZayraDbContext(Options());
        var w = await SeedAsync(db, Utc(2027, 2, 1));

        var result = await Service(db).OpenYearBalancesAsync(w.TenantId, 2026, Now);

        (await db.EmployeeLeaveBalances.CountAsync()).Should().Be(0);
        result.BalancesCreated.Should().Be(0);
    }

    [Fact]
    public async Task OpenYear_LeavesABalanceThatAlreadyHoldsAGrantAlone()
    {
        // An imported / hand-entered balance is the company's own figure for the year; catching it up as well would double it.
        await using var db = new ZayraDbContext(Options());
        var w = await SeedAsync(db, Utc(2025, 1, 1));
        db.EmployeeLeaveBalances.AddRange(
            new EmployeeLeaveBalance { TenantId = w.TenantId, EmployeeId = w.Employee.Id, LeaveTypeId = w.Annual.Id, Year = 2026, Entitled = 30m, Accrued = 17.5m, Used = 10m },
            new EmployeeLeaveBalance { TenantId = w.TenantId, EmployeeId = w.Employee.Id, LeaveTypeId = w.Sick.Id, Year = 2026, Entitled = 90m });
        await db.SaveChangesAsync();

        var result = await Service(db).OpenYearBalancesAsync(w.TenantId, 2026, Now);

        var annual = await db.EmployeeLeaveBalances.SingleAsync(b => b.LeaveTypeId == w.Annual.Id);
        (annual.Entitled, annual.Accrued, annual.Used).Should().Be((30m, 17.5m, 10m));
        (await db.EmployeeLeaveBalances.SingleAsync(b => b.LeaveTypeId == w.Sick.Id)).Entitled.Should().Be(90m);
        (await db.LeaveBalanceTransactions.CountAsync()).Should().Be(0);
        result.BalancesCreated.Should().Be(0);
    }

    [Fact]
    public async Task OpenYear_AnnualLeaveFollowsTheSaudiTier_ThroughTheExistingEngine()
    {
        // Art. 109: 21 days, 30 once the worker has five years of service. The configured policy says 21; the engine floors it.
        await using var db = new ZayraDbContext(Options());
        var w = await SeedAsync(db, Utc(2019, 1, 1), country: "SA", annualDays: 21m);

        await Service(db).OpenYearBalancesAsync(w.TenantId, 2026, Now);

        (await db.EmployeeLeaveBalances.SingleAsync(b => b.LeaveTypeId == w.Annual.Id)).Accrued.Should().Be(27.5m, "30 / 12 = 2.5 a month for eleven months");
    }

    [Fact]
    public async Task OpenYear_OnlyReachesTheCompaniesTheCallerMayAccess()
    {
        var options = Options();
        Guid tenantId, companyA;
        int employeeA, employeeB;
        await using (var seed = new ZayraDbContext(options))
        {
            var w = await SeedAsync(seed, Utc(2025, 1, 1));
            tenantId = w.TenantId; companyA = w.Company.Id; employeeA = w.Employee.Id;
            var companyB = new Company { TenantId = tenantId, LegalNameEn = "Sister", CountryCode = "AE", RegistrationNumber = $"R-{Guid.NewGuid():N}" };
            var typeB = new LeaveType { TenantId = tenantId, CompanyId = companyB.Id, Code = "ANNUAL", NameEn = "Annual Leave", Category = "Annual", IsActive = true };
            var empB = new Employee { TenantId = tenantId, CompanyId = companyB.Id, EmployeeCode = "OY-2", FullName = "Sister Employee", Status = "Active", JoiningDate = Utc(2025, 1, 1) };
            var policyB = new LeavePolicy { TenantId = tenantId, CompanyId = companyB.Id, LeaveTypeId = typeB.Id, Name = "Annual B", Status = "Active", AccrualMethod = "Monthly", AnnualEntitlementDays = 24m };
            seed.AddRange(companyB, typeB, empB, policyB);
            await seed.SaveChangesAsync();
            employeeB = empB.Id;
        }

        var accessor = new SwitchableAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim("entity_access", JsonSerializer.Serialize(new { c = companyA, r = "Admin" })),
                }, "Test")),
            },
        };
        await using var db = new ZayraDbContext(options, accessor);

        await Service(db).OpenYearBalancesAsync(tenantId, 2026, Now);

        await using var check = new ZayraDbContext(options);
        (await check.EmployeeLeaveBalances.AnyAsync(b => b.EmployeeId == employeeA)).Should().BeTrue();
        (await check.EmployeeLeaveBalances.AnyAsync(b => b.EmployeeId == employeeB)).Should().BeFalse("the sister company is outside the caller's access");
    }

    [Fact]
    public void OpenYear_NeedsTheLeavePolicyManagePermission()
    {
        var method = typeof(LeaveBalancesController).GetMethod("OpenYear")!;
        method.GetCustomAttribute<HttpPostAttribute>()!.Template.Should().Be("open-year");
        method.GetCustomAttributes<HasPermissionAttribute>().Single().Permissions.Should().Equal("leave.policy_manage");
    }

    [Fact]
    public async Task OpenYear_Endpoint_OpensTheCurrentYear_AndRefusesAnyOther()
    {
        await using var db = new ZayraDbContext(Options());
        var w = await SeedAsync(db, Utc(2025, 1, 1));
        var controller = new LeaveBalancesController(db, Service(db), new OrgScope())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", w.TenantId.ToString()),
                        new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                        new Claim(ClaimTypes.Name, "HR Admin"),
                        new Claim(ClaimTypes.Role, "HR Manager"),
                    }, "Test")),
                },
            },
        };

        var past = await controller.OpenYear(new OpenYearRequest(DateTime.UtcNow.Year - 1), CancellationToken.None);
        past.Should().BeOfType<BadRequestObjectResult>();
        (await db.EmployeeLeaveBalances.CountAsync()).Should().Be(0);

        var ok = await controller.OpenYear(null, CancellationToken.None);
        ok.Should().BeOfType<OkObjectResult>();
        (await db.EmployeeLeaveBalances.CountAsync(b => b.Year == DateTime.UtcNow.Year)).Should().BeGreaterThan(0);
    }

    private sealed class SwitchableAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class OrgScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
            => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }
}
