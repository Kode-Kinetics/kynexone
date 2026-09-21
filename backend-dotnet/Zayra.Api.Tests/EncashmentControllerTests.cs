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
            Jurisdiction = Jurisdictions.KsaMainland, DefaultCurrency = "SAR", IsActive = true
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

    /// <summary>
    /// <b>The expected total moved from 12 to 10, deliberately.</b> This fixture carries BOTH
    /// <c>Entitled = 20</c> and <c>Accrued = 2</c>, and <c>Available</c> used to ADD them for a granted
    /// figure of 22. Those two columns are alternative representations of ONE grant — a front-loaded
    /// policy fills <c>Entitled</c>, a monthly-accrual policy grows <c>Accrued</c> — so summing them
    /// double-counted the entitlement on every row that carried both, which is every seeded and every
    /// CSV-imported tenant. Evostel's annual-leave row read 37.50 days available against a 30-day
    /// entitlement. The grant is now <c>Math.Max(20, 2) = 20</c>, so 20 + 3 + 1 − 4 − 2 − 3 − 5 = 10.
    ///
    /// <para>The invariant this test was written for is UNCHANGED and still asserted below: Expired and
    /// Pending are each netted off exactly once, and converting a pending reservation into encashed
    /// days must not deduct the same days twice. Only the composition of the granted term moved.</para>
    /// </summary>
    [Fact]
    public void AvailableBalance_SubtractsExpiredAndPendingWithoutDoubleSubtractingEncashmentTransfer()
    {
        var balance = new EmployeeLeaveBalance
        {
            Entitled = 20m, Accrued = 2m, CarriedForward = 3m, ManualAdjustment = 1m,
            Used = 4m, Pending = 2m, Encashed = 3m, Expired = 5m
        };
        balance.Granted.Should().Be(20m,
            "Entitled and Accrued are one grant in two columns; the balance recognises the larger, never the sum");
        balance.Available.Should().Be(10m);

        var beforeApproval = balance.Available;
        balance.Pending -= 2m;
        balance.Encashed += 2m;
        balance.Available.Should().Be(beforeApproval,
            "payroll approval converts a reservation to encashed leave; it must not deduct the same days twice");
    }

    /// <summary>
    /// The shape that broke: a 30-day KSA annual entitlement with ten months of Art. 109 accrual also
    /// recorded on the same row, and ten days taken. Evostel's live figure, and the reason the balance
    /// screen showed more days available than the entitlement it was displayed against.
    /// </summary>
    [Fact]
    public void RowCarryingBothEntitledAndAccrued_CannotExceedTheEntitlement()
    {
        var balance = new EmployeeLeaveBalance { Entitled = 30m, Accrued = 17.5m, Used = 10m };

        balance.Available.Should().Be(20m,
            "30 granted less 10 taken; the pre-fix sum reported 37.50 against a 30-day entitlement");
        balance.Available.Should().BeLessThanOrEqualTo(balance.Entitled,
            "nothing carried forward or manually adjusted, so the balance cannot exceed the entitlement");
    }

    /// <summary>
    /// MAX must be a NO-OP for the two coherent single-field shapes, or the fix would silently migrate
    /// live data: an accrued-only reading would zero every CSV-imported tenant whose figure sits in
    /// Entitled, and an entitled-only reading would zero every tenant on the accrual engine.
    /// </summary>
    [Theory]
    [InlineData(21, 0, 21)]      // front-loaded policy
    [InlineData(0, 17.5, 17.5)]  // monthly-accrual policy (Art. 109 at 1.75/month, 10 months)
    [InlineData(0, 2.5, 2.5)]    // Art. 109 five-year tier, one month
    public void SingleFieldTenantsAreUnaffected(double entitled, double accrued, double expected)
    {
        new EmployeeLeaveBalance { Entitled = (decimal)entitled, Accrued = (decimal)accrued }
            .Available.Should().Be((decimal)expected);
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
