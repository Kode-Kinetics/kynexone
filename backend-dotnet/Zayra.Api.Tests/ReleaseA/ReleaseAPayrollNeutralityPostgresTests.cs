using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// Release A adds non-paying catalogue rows (air ticket, medical, education — ComponentType Benefit; per diem —
/// Facility), classifies housing/transport/other as QiwaWage, and grade cells for them. None of it may change a
/// single payroll figure. Two tenants identical in every respect except that one ran
/// <see cref="PayComponentSeeder.EnsureEntitlementCatalogAsync"/> and holds benefit cells must produce
/// byte-identical earnings, deductions, slips and validation results — on real PostgreSQL, through the real run.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class ReleaseAPayrollNeutralityPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task BenefitAndFacilityComponents_LeavePayrollOutputByteIdentical()
    {
        var plain = await SeedAsync(withReleaseA: false);
        var releaseA = await SeedAsync(withReleaseA: true);
        await using (var check = fixture.CreateDb())
        {
            Assert.Equal(4, await check.PayComponents.CountAsync(c => c.TenantId == releaseA.TenantId
                && (c.ComponentType == PayComponentTypes.Benefit || c.ComponentType == PayComponentTypes.Facility)));
            Assert.Equal(PayEntitlementClasses.QiwaWage, (await check.PayComponents.SingleAsync(c => c.TenantId == releaseA.TenantId
                && c.Code == "HOUSING")).EntitlementClass);
        }

        foreach (var (tenantId, runId) in new[] { plain, releaseA })
            await using (var db = fixture.CreateDb())
            {
                var result = await PayComponentNetPayDefectTests.Build(db, tenantId).Process(runId, CancellationToken.None);
                if (result is ObjectResult { StatusCode: >= 400 } bad)
                    Assert.Fail($"Process refused: HTTP {bad.StatusCode} {JsonSerializer.Serialize(bad.Value)}");
            }

        var expected = await OutputAsync(plain.RunId);
        var actual = await OutputAsync(releaseA.RunId);
        Assert.NotEmpty(expected);
        foreach (var code in new[] { "AIR_TICKET", "MEDICAL", "EDUCATION", "PER_DIEM" })
            Assert.DoesNotContain(code, actual);
        Assert.Equal(expected, actual);
    }

    private async Task<(Guid TenantId, Guid RunId)> SeedAsync(bool withReleaseA)
    {
        await using var db = fixture.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        await PayComponentSeeder.SeedTenantDefaultsAsync(db, tenantId, CancellationToken.None);
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = $"Masar {Guid.NewGuid():N}", CountryCode = "SAU", Jurisdiction = "KSA-mainland",
            RegistrationNumber = $"MS-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true,
            CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var grade = new Grade { TenantId = tenantId, Code = "G3", Name = "Supervisor", Level = 30 };
        var emp = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "MS-1", FullName = "Mohammed Abdelrahman", Nationality = "Egyptian",
            ContractType = "Fixed", Status = "Active", JoiningDate = new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc), GradeId = grade.Id,
        };
        db.AddRange(company, grade, emp);
        await db.SaveChangesAsync();
        if (withReleaseA)
        {
            await PayComponentSeeder.EnsureEntitlementCatalogAsync(db, tenantId, CancellationToken.None);
            db.GradeEntitlements.AddRange(
                Cell(tenantId, grade.Id, "MEDICAL", PayEntitlementClasses.Contractual, GradeEntitlementValueTypes.CoverageTier, tier: CoverageTiers.B,
                    scope: DependantScopes.Family),
                Cell(tenantId, grade.Id, "AIR_TICKET", PayEntitlementClasses.Contractual, GradeEntitlementValueTypes.Quantity, quantity: 1,
                    tier: CoverageTiers.Economy),
                Cell(tenantId, grade.Id, "PER_DIEM", PayEntitlementClasses.Facility, GradeEntitlementValueTypes.Amount, amount: 250m),
                Cell(tenantId, grade.Id, "HOUSING", PayEntitlementClasses.QiwaWage, GradeEntitlementValueTypes.PercentOfBasic, rate: 0.25m));
        }
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = emp.Id, SalaryStructureId = Guid.NewGuid(), BasicSalary = 8_000m, HousingAllowance = 2_000m,
            TransportAllowance = 800m, OtherAllowance = 200m, Currency = "SAR", EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
        });
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
        {
            TenantId = tenantId, EmployeeId = emp.Id, Iban = "SA4420000001234567891234", MolId = $"MOL-{Guid.NewGuid():N}", SalaryCurrency = "SAR",
        });
        var run = new PayrollRun
        {
            TenantId = tenantId, CompanyId = company.Id, Year = 2026, Month = 6, Status = "Draft",
            CreatedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        return (tenantId, run.Id);
    }

    private static GradeEntitlement Cell(Guid tenantId, Guid gradeId, string code, string cls, string valueType, decimal? amount = null,
        decimal? rate = null, string? tier = null, short? quantity = null, string scope = DependantScopes.None) => new()
    {
        TenantId = tenantId, GradeId = gradeId, PayComponentCode = code, EntitlementClass = cls, Eligible = true, ValueType = valueType,
        Amount = amount, Rate = rate, CoverageTier = tier, Quantity = quantity, DependantScope = scope, EffectiveFrom = new DateOnly(2024, 1, 1),
    };

    /// <summary>Everything a run emits, with identifiers that legitimately differ per tenant removed.</summary>
    private async Task<string> OutputAsync(Guid runId)
    {
        await using var db = fixture.CreateDb();
        var earnings = await db.PayrollEarnings.AsNoTracking().Where(x => x.PayrollRunId == runId)
            .OrderBy(x => x.ComponentCode).ThenBy(x => x.Amount)
            .Select(x => new { x.ComponentCode, x.ComponentName, x.Amount, x.Source, x.GlDriverKey }).ToListAsync();
        var deductions = await db.PayrollDeductions.AsNoTracking().Where(x => x.PayrollRunId == runId)
            .OrderBy(x => x.ComponentCode).ThenBy(x => x.Amount)
            .Select(x => new { x.ComponentCode, x.ComponentName, x.Amount, x.Source, x.IsEmployerContribution, x.GlDriverKey }).ToListAsync();
        var slips = await db.PayrollSlips.AsNoTracking().Where(x => x.RunId == runId)
            .Select(x => new { x.BasicSalary, x.HousingAllowance, x.TransportAllowance, x.OtherAllowances, x.GrossSalary, x.Deductions,
                x.NetSalary, x.EmployeeStatutoryTotal, x.EmployerStatutoryTotal, x.Status }).ToListAsync();
        var validation = await db.PayrollValidationResults.AsNoTracking().Where(x => x.PayrollRunId == runId)
            .OrderBy(x => x.Code).Select(x => new { x.Code, x.Severity }).ToListAsync();
        return JsonSerializer.Serialize(new { earnings, deductions, slips, validation });
    }
}
