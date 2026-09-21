using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Compliance;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Tests for GosiReadinessReportService (backing the GET /api/saudi-compliance/gosi-readiness endpoint).
///
/// Coverage:
///   - Saudi employee contribution uses per-branch rules (Annuities + SANED + OccHazards), not flat rates
///   - Non-Saudi employee only receives OccupationalHazards employer contribution
///   - GCC employee carries GCC_RULES_PENDING_CONFIRMATION warning
///   - Contribution amounts match GosiCalculationService output (not controller flat math)
///   - Tenant isolation: tenant B data never appears in tenant A report
///   - Sensitive fields (GosiReference) never appear in serialised output
/// </summary>
public class GosiReadinessEndpointTests
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();

    private static ZayraDbContext MakeDb()
    {
        var opts = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ZayraDbContext(opts);
    }

    // ── Saudi employee ────────────────────────────────────────────────────────

    [Fact]
    public async Task Saudi_Employee_ContributionUsesPerBranchRules_NotFlatRates()
    {
        using var db = MakeDb();
        await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);

        db.Employees.Add(Employee(TenantA, 1, "Saudi", gosiRef: "SA001"));
        db.EmployeeSalaryStructures.Add(Salary(TenantA, 1, 10_000m));
        await db.SaveChangesAsync();

        var report = await TestReconciliation.GosiReadiness(db).BuildAsync(TenantA, CancellationToken.None);

        var emp = Assert.Single(report.Employees);
        Assert.True(emp.IsReady);
        Assert.Equal("Saudi", emp.Classification);

        // Must have per-branch lines — Annuities and SANED for both Employee and Employer.
        var branches = emp.Lines.Select(l => l.Branch).Distinct().ToHashSet();
        Assert.Contains(GosiBranches.Annuities, branches);
        Assert.Contains(GosiBranches.SANED, branches);
        Assert.Contains(GosiBranches.OccupationalHazards, branches);

        // Totals must NOT match flat-rate math (old 10%/12% = 1000/1200).
        // Real: 9% Annuities + 0.75% SANED employee = 9.75% total emp; far from 10%.
        Assert.NotEqual(1000m, emp.EmployeeContributionTotal);
        Assert.NotEqual(1200m, emp.EmployerContributionTotal);

        // Verify against the official calculator to confirm no controller-level math.
        var rules = await db.GosiContributionRules.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        var periodDate = DateOnly.FromDateTime(DateTime.UtcNow);
        var expected = GosiCalculationService.Calculate("Saudi", 10_000m, rules, periodDate, TenantA, new Zayra.Api.Infrastructure.CountryPack.Ksa.GosiWageBounds(null, Zayra.Api.Infrastructure.CountryPack.Ksa.KsaGosiWageBounds.DefaultMonthlyCeilingSar));
        Assert.Equal(expected.EmployeeTotal, emp.EmployeeContributionTotal);
        Assert.Equal(expected.EmployerTotal, emp.EmployerContributionTotal);
    }

    [Fact]
    public async Task Saudi_Employee_LineAmounts_MatchGosiCalculationService()
    {
        using var db = MakeDb();
        await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);

        db.Employees.Add(Employee(TenantA, 1, "Saudi", gosiRef: "SA002"));
        db.EmployeeSalaryStructures.Add(Salary(TenantA, 1, 15_000m));
        await db.SaveChangesAsync();

        var report = await TestReconciliation.GosiReadiness(db).BuildAsync(TenantA, CancellationToken.None);
        var emp = Assert.Single(report.Employees);

        var rules = await db.GosiContributionRules.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        var periodDate = DateOnly.FromDateTime(DateTime.UtcNow);
        var expected = GosiCalculationService.Calculate("Saudi", 15_000m, rules, periodDate, TenantA, new Zayra.Api.Infrastructure.CountryPack.Ksa.GosiWageBounds(null, Zayra.Api.Infrastructure.CountryPack.Ksa.KsaGosiWageBounds.DefaultMonthlyCeilingSar));

        // Every line amount must match the calculator's output for the same (branch, payer) pair.
        foreach (var expectedLine in expected.Lines)
        {
            var actualLine = emp.Lines.FirstOrDefault(l => l.Branch == expectedLine.Branch && l.Payer == expectedLine.Payer);
            Assert.NotNull(actualLine);
            Assert.Equal(expectedLine.Amount, actualLine.Amount);
            Assert.Equal(expectedLine.Rate, actualLine.Rate);
        }
    }

    // ── Non-Saudi employee ────────────────────────────────────────────────────

    [Fact]
    public async Task NonSaudi_Employee_OnlyReceivesOccupationalHazardsEmployerLine()
    {
        using var db = MakeDb();
        await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);

        db.Employees.Add(Employee(TenantA, 1, "Pakistan", gosiRef: "NS001"));
        db.EmployeeSalaryStructures.Add(Salary(TenantA, 1, 8_000m));
        await db.SaveChangesAsync();

        var report = await TestReconciliation.GosiReadiness(db).BuildAsync(TenantA, CancellationToken.None);
        var emp = Assert.Single(report.Employees);

        Assert.Equal("NonSaudi", emp.Classification);
        Assert.True(emp.IsReady);
        Assert.Equal(0m, emp.EmployeeContributionTotal);
        Assert.True(emp.EmployerContributionTotal > 0m, "NonSaudi employer must have OccHazards contribution");

        // Must have exactly OccupationalHazards ER and no Annuities/SANED lines.
        Assert.All(emp.Lines, l => Assert.Equal(GosiBranches.OccupationalHazards, l.Branch));
        Assert.All(emp.Lines, l => Assert.Equal(GosiPayers.Employer, l.Payer));
        Assert.DoesNotContain(emp.Lines, l => l.Branch == GosiBranches.Annuities);
        Assert.DoesNotContain(emp.Lines, l => l.Branch == GosiBranches.SANED);
    }

    [Fact]
    public async Task NonSaudi_Employee_OldFlatRate2Percent_IsNotUsed()
    {
        using var db = MakeDb();
        await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);

        db.Employees.Add(Employee(TenantA, 1, "Egypt", gosiRef: "NS002"));
        db.EmployeeSalaryStructures.Add(Salary(TenantA, 1, 10_000m));
        await db.SaveChangesAsync();

        var report = await TestReconciliation.GosiReadiness(db).BuildAsync(TenantA, CancellationToken.None);
        var emp = Assert.Single(report.Employees);

        // Old flat math would give employerRate = 2% → 200.00.
        // GosiCalculationService should also give 200.00 for NonSaudi OccHazards @ 2%,
        // but the source of truth must be the calculator, not a hard-coded rate.
        var rules = await db.GosiContributionRules.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        var periodDate = DateOnly.FromDateTime(DateTime.UtcNow);
        var expected = GosiCalculationService.Calculate("Egypt", 10_000m, rules, periodDate, TenantA, new Zayra.Api.Infrastructure.CountryPack.Ksa.GosiWageBounds(null, Zayra.Api.Infrastructure.CountryPack.Ksa.KsaGosiWageBounds.DefaultMonthlyCeilingSar));
        Assert.Equal(expected.EmployerTotal, emp.EmployerContributionTotal);
    }

    // ── GCC employee ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GccEmployee_HasPendingConfirmationWarning()
    {
        using var db = MakeDb();
        await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);

        db.Employees.Add(Employee(TenantA, 1, "UAE", gosiRef: "GCC001"));
        db.EmployeeSalaryStructures.Add(Salary(TenantA, 1, 9_000m));
        await db.SaveChangesAsync();

        var report = await TestReconciliation.GosiReadiness(db).BuildAsync(TenantA, CancellationToken.None);
        var emp = Assert.Single(report.Employees);

        Assert.Equal("GCC", emp.Classification);
        Assert.Contains(emp.Warnings, w => w.Code == "GCC_RULES_PENDING_CONFIRMATION");
    }

    [Fact]
    public async Task GccEmployee_Warnings_DoNotContainSensitiveData()
    {
        using var db = MakeDb();
        await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);

        const string sensitiveRef = "GCC-PRIVATE-9999";
        db.Employees.Add(Employee(TenantA, 1, "UAE", gosiRef: sensitiveRef));
        db.EmployeeSalaryStructures.Add(Salary(TenantA, 1, 9_000m));
        await db.SaveChangesAsync();

        var report = await TestReconciliation.GosiReadiness(db).BuildAsync(TenantA, CancellationToken.None);
        var json = System.Text.Json.JsonSerializer.Serialize(report);

        Assert.DoesNotContain(sensitiveRef, json);
    }

    // ── Sensitive field masking ───────────────────────────────────────────────

    [Fact]
    public async Task SensitiveFields_GosiReference_NeverAppearsInReport()
    {
        using var db = MakeDb();
        await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);

        const string sensitiveGosiRef = "GOSI-PRIV-SENTINEL-42";
        db.Employees.Add(Employee(TenantA, 1, "Saudi", gosiRef: sensitiveGosiRef));
        db.EmployeeSalaryStructures.Add(Salary(TenantA, 1, 10_000m));
        await db.SaveChangesAsync();

        var report = await TestReconciliation.GosiReadiness(db).BuildAsync(TenantA, CancellationToken.None);
        var json = System.Text.Json.JsonSerializer.Serialize(report);

        Assert.DoesNotContain(sensitiveGosiRef, json);
    }

    [Fact]
    public async Task SensitiveFields_BasicSalaryNotDirectlyExposed_OnlyContributionAmounts()
    {
        using var db = MakeDb();
        await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);

        const decimal sentinelSalary = 13_579.99m;
        db.Employees.Add(Employee(TenantA, 1, "Saudi", gosiRef: "SA111"));
        db.EmployeeSalaryStructures.Add(Salary(TenantA, 1, sentinelSalary));
        await db.SaveChangesAsync();

        var report = await TestReconciliation.GosiReadiness(db).BuildAsync(TenantA, CancellationToken.None);
        var emp = Assert.Single(report.Employees);

        // Report must not have a field named basicSalary / ContributoryWage.
        Assert.DoesNotContain("basicSalary", System.Text.Json.JsonSerializer.Serialize(emp));
        Assert.DoesNotContain("ContributoryWage", System.Text.Json.JsonSerializer.Serialize(emp));
        Assert.DoesNotContain("contributoryWage", System.Text.Json.JsonSerializer.Serialize(emp));
    }

    // ── Tenant isolation ──────────────────────────────────────────────────────

    [Fact]
    public async Task TenantIsolation_TenantBEmployees_NeverInTenantAReport()
    {
        using var db = MakeDb();
        await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);

        // Tenant A: 1 employee.
        db.Employees.Add(Employee(TenantA, 1, "Saudi", gosiRef: "SA_A1"));
        db.EmployeeSalaryStructures.Add(Salary(TenantA, 1, 10_000m));

        // Tenant B: 3 employees.
        for (var i = 10; i < 13; i++)
        {
            db.Employees.Add(Employee(TenantB, i, "Saudi", gosiRef: $"SA_B{i}"));
            db.EmployeeSalaryStructures.Add(Salary(TenantB, i, 5_000m));
        }
        await db.SaveChangesAsync();

        var report = await TestReconciliation.GosiReadiness(db).BuildAsync(TenantA, CancellationToken.None);

        Assert.Equal(1, report.TotalEmployees);
        Assert.Single(report.Employees);
        Assert.DoesNotContain(report.Employees, e => e.EmployeeCode.StartsWith("EMP00010")
                                                  || e.EmployeeCode.StartsWith("EMP00011")
                                                  || e.EmployeeCode.StartsWith("EMP00012"));
    }

    // ── Blocking issues ───────────────────────────────────────────────────────

    [Fact]
    public async Task BlockedEmployee_MissingGosiRef_HasCorrectBlockingIssueCode()
    {
        using var db = MakeDb();
        await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);

        db.Employees.Add(Employee(TenantA, 1, "Saudi", gosiRef: ""));
        db.EmployeeSalaryStructures.Add(Salary(TenantA, 1, 10_000m));
        await db.SaveChangesAsync();

        var report = await TestReconciliation.GosiReadiness(db).BuildAsync(TenantA, CancellationToken.None);
        var emp = Assert.Single(report.Employees);

        Assert.False(emp.IsReady);
        Assert.Contains(emp.BlockingIssues, i => i.Code == "MISSING_GOSI_REFERENCE");
        Assert.Equal(0m, emp.EmployeeContributionTotal);
        Assert.Equal(0m, emp.EmployerContributionTotal);
        Assert.Empty(emp.Lines);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Employee Employee(Guid tenantId, int id, string nationality, string gosiRef) =>
        new()
        {
            Id = id, TenantId = tenantId,
            EmployeeCode = $"EMP{id:D5}", FullName = $"Test Employee {id}",
            Nationality = nationality, Status = "Active", IsDeleted = false,
            GosiReference = gosiRef,
        };

    private static EmployeeSalaryStructure Salary(Guid tenantId, int empId, decimal basic) =>
        new()
        {
            TenantId = tenantId, EmployeeId = empId,
            BasicSalary = basic, EffectiveDate = new DateOnly(2024, 1, 1),
        };
}
