using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// A carried-over leave balance must be provided ONCE. The product has no year-end rollover job yet, so a customer (or a
/// future rollover) can leave last year's row open while this year's row carries the same days in
/// <c>CarriedForward</c>. The final-settlement calculator sums <c>Available</c> over every year's row, so in that
/// shape it counts the days twice. CTO decision 2026-11-12: final settlement must not pay a carried day twice, so BOTH the provision
/// and the settlement ask the calculator with <c>netCarriedForward: true</c> (see <c>FinalSettlement_CountsCarriedForwardDaysOnce_…</c>
/// for the settlement-level proof). <c>false</c> remains only as the raw per-row sum.
/// </summary>
public class LeaveProvisionCarryForwardTests
{
    private const decimal Day = 300m;   // monthly gross 9,000 / 30

    private static (ZayraDbContext db, SqliteConnection conn) NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static async Task<(Guid Tenant, Guid Company, int Employee)> SeedAsync(
        ZayraDbContext db, params (int Year, decimal Accrued, decimal Used, decimal CarriedForward, decimal Expired)[] rows)
    {
        var tenant = Guid.NewGuid();
        var companyRow = new Company
        {
            TenantId = tenant, LegalNameEn = "Carry Forward Co", RegistrationNumber = $"CR-{Guid.NewGuid():N}"[..12],
            CountryCode = "SAU", Jurisdiction = "KSA-mainland", DefaultCurrency = "SAR", IsActive = true,
        };
        db.Companies.Add(companyRow);
        await db.SaveChangesAsync();
        var company = companyRow.Id;
        var employee = new Employee
        {
            TenantId = tenant, CompanyId = company, EmployeeCode = "E-CF", FullName = "Carry Forward", Status = "Active",
            JoiningDate = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc), WorkEmail = "cf@test.com", Nationality = "SAU", ContractType = "Indefinite",
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var annual = new LeaveType { TenantId = tenant, CompanyId = company, Code = "ANNUAL", NameEn = "Annual leave", Category = "Annual", IsActive = true };
        db.LeaveTypes.Add(annual);
        foreach (var r in rows)
            db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
            {
                TenantId = tenant, EmployeeId = employee.Id, LeaveTypeId = annual.Id, LeaveTypeName = annual.NameEn, Year = r.Year,
                Accrued = r.Accrued, Used = r.Used, CarriedForward = r.CarriedForward, Expired = r.Expired,
            });
        await db.SaveChangesAsync();
        return (tenant, company, employee.Id);
    }

    private static Task<LeaveEncashmentResult> ComputeAsync(ZayraDbContext db, (Guid Tenant, Guid Company, int Employee) w, bool net) =>
        LeaveEncashmentCalculator.ComputeAsync(db, w.Tenant, w.Employee, w.Company, new DateOnly(2026, 6, 30), 9_000m, default,
            statutoryAnnualLeaveCashOut: true, netCarriedForward: net);

    [Fact]
    public async Task AnUnclosedPriorYearRow_PlusTheSameDaysCarriedForward_IsCountedOnce_WhenNetting_AndTheRawSumStillDoublesIt()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        // 2025: 21 granted, 6 used -> 15 still showing. 2026: those 15 carried forward plus 5 accrued so far.
        var w = await SeedAsync(db, (2025, 21m, 6m, 0m, 0m), (2026, 5m, 0m, 15m, 0m));

        var provision = await ComputeAsync(db, w, net: true);
        var rawSum = await ComputeAsync(db, w, net: false);

        provision.TotalDays.Should().Be(20m, "15 carried + 5 accrued, each day once");
        provision.TotalAmount.Should().Be(20m * Day);
        provision.Lines.Select(l => (l.Year, l.EncashableDays)).Should().Equal((2026, 20m));
        rawSum.TotalDays.Should().Be(35m,
            "netCarriedForward:false is the raw per-row sum (no caller uses it any more): the 15 carried days count in both rows");
    }

    [Fact]
    public async Task AClosedPriorYearRow_IsAlreadyOnce_InBothModes()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        // The rollover closed 2025 properly (the carried days were expired out of it): 21 - 6 - 15 = 0.
        var w = await SeedAsync(db, (2025, 21m, 6m, 0m, 15m), (2026, 5m, 0m, 15m, 0m));

        (await ComputeAsync(db, w, net: true)).TotalDays.Should().Be(20m);
        (await ComputeAsync(db, w, net: false)).TotalDays.Should().Be(20m);
    }

    [Fact]
    public async Task APriorYearLeftoverThatWasNotCarried_IsStillOwed()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        // Today's model: nothing rolls over, so last year's unused 15 days stay in the 2025 row and 2026 starts fresh.
        var w = await SeedAsync(db, (2025, 21m, 6m, 0m, 0m), (2026, 5m, 0m, 0m, 0m));

        (await ComputeAsync(db, w, net: true)).TotalDays.Should().Be(20m, "15 left over from 2025 + 5 accrued in 2026");
        (await ComputeAsync(db, w, net: false)).TotalDays.Should().Be(20m);
    }

    [Fact]
    public async Task APartialCarry_NetsOnlyWhatWasCarried()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        // 15 days left in 2025 (row still open); only 10 were carried into 2026, which also accrued 5.
        var w = await SeedAsync(db, (2025, 21m, 6m, 0m, 0m), (2026, 5m, 0m, 10m, 0m));

        var provision = await ComputeAsync(db, w, net: true);

        provision.TotalDays.Should().Be(20m, "2025 keeps the 5 days it did not carry; 2026 holds 10 carried + 5 accrued");
        (await ComputeAsync(db, w, net: false)).TotalDays.Should().Be(30m);
    }

    [Fact]
    public async Task ACarriedForwardWithNoPriorRow_IsJustTheOpeningBalance()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        // A migrated tenant: only the 2026 row exists and carries the opening days.
        var w = await SeedAsync(db, (2026, 5m, 0m, 15m, 0m));

        (await ComputeAsync(db, w, net: true)).TotalDays.Should().Be(20m);
    }
}
