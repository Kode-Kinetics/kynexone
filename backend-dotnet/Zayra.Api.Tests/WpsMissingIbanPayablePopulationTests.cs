using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Compliance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The WPS "missing IBAN" count is measured over the people the run would actually pay this period (the run's own rule): an Active employee who joins
/// after the period ends is not payable yet, so a missing IBAN on their profile is not a WPS problem this period. Someone payable with no usable IBAN is.
/// </summary>
public class WpsMissingIbanPayablePopulationTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    private static ZayraDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static Employee Emp(int id, DateTime joining) => new()
    {
        Id = id, TenantId = Tenant, EmployeeCode = $"EMP{id:D4}", FullName = $"Employee {id}", Nationality = "Saudi",
        Status = "Active", JoiningDate = joining,
    };

    private static async Task<int> MissingIbanAsync(ZayraDbContext db) =>
        (await new SaudiComplianceDashboardService(db, TestReconciliation.For(db)).BuildAsync(Tenant, CancellationToken.None)).Wps.MissingIbanCount;

    [Fact]
    public async Task AnActiveEmployeeJoiningNextMonth_WithNoIban_IsNotPayableThisPeriod_AndIsNotCounted()
    {
        using var db = MakeDb();
        var nextMonth = DateTime.UtcNow.Date.AddMonths(1);
        db.PayrollRuns.Add(new PayrollRun { TenantId = Tenant, Year = 2026, Month = 5, Status = "Locked" });
        db.Employees.Add(Emp(1, new DateTime(nextMonth.Year, nextMonth.Month, 1, 0, 0, 0, DateTimeKind.Utc)));
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = Tenant, EmployeeId = 1, Iban = "" });
        await db.SaveChangesAsync();

        Assert.Equal(0, await MissingIbanAsync(db));
    }

    [Fact]
    public async Task AnActiveEmployeeWhoIsPayableNow_WithNoIban_IsCounted()
    {
        using var db = MakeDb();
        db.PayrollRuns.Add(new PayrollRun { TenantId = Tenant, Year = 2026, Month = 5, Status = "Locked" });
        db.Employees.Add(Emp(1, DateTime.UtcNow.Date.AddYears(-1)));
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = Tenant, EmployeeId = 1, Iban = "" });
        await db.SaveChangesAsync();

        Assert.Equal(1, await MissingIbanAsync(db));
    }

    [Fact]
    public async Task APayableEmployeeWithNoPayrollProfileAtAll_IsCounted_AndListedWithAClearReason()
    {
        using var db = MakeDb();
        db.PayrollRuns.Add(new PayrollRun { TenantId = Tenant, Year = 2026, Month = 5, Status = "Locked" });
        db.Employees.AddRange(Emp(1, DateTime.UtcNow.Date.AddYears(-1)), Emp(2, DateTime.UtcNow.Date.AddYears(-1)));
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = Tenant, EmployeeId = 1, Iban = "SA4420000001234567891234" });
        await db.SaveChangesAsync();

        var wps = (await new SaudiComplianceDashboardService(db, TestReconciliation.For(db)).BuildAsync(Tenant, CancellationToken.None)).Wps;

        Assert.Equal(1, wps.MissingIbanCount);
        Assert.Contains(wps.BlockingIssues, i => i.Contains("no payroll bank profile", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ANotYetPayableEmployeeWithNoPayrollProfile_IsNotCounted()
    {
        using var db = MakeDb();
        var nextMonth = DateTime.UtcNow.Date.AddMonths(1);
        db.PayrollRuns.Add(new PayrollRun { TenantId = Tenant, Year = 2026, Month = 5, Status = "Locked" });
        db.Employees.Add(Emp(1, new DateTime(nextMonth.Year, nextMonth.Month, 1, 0, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();

        Assert.Equal(0, await MissingIbanAsync(db));
    }
}
