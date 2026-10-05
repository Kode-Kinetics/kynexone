using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Slice L1 on real PostgreSQL: the no-overlap EXCLUDE, the creation lock that keeps concurrent applications
/// inside the grade's outstanding cap, and the proof that a Facility component changes nothing a payroll run
/// produces.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class GradeLoanLimitPostgresTests(PostgresFixture fixture)
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    [Fact]
    public async Task Exclusion_RejectsAnOverlappingCell_ButAllowsACompanyOverrideAndAdjacentVersions()
    {
        var seed = await SeedAsync(cap: null, outstandingCap: null);
        await using var db = fixture.CreateDb();
        GradeEntitlement Cell(Guid? company, DateOnly from, DateOnly? to) => new()
        {
            TenantId = seed.TenantId, CompanyId = company, GradeId = seed.GradeId, PayComponentCode = "LOAN_PERSONAL",
            EntitlementClass = PayEntitlementClasses.Facility, Eligible = true, ValueType = GradeEntitlementValueTypes.Amount,
            Amount = 1_000m, EffectiveFrom = from, EffectiveTo = to,
        };
        // The seeded tenant-wide cell runs from Today-30 with no end. Adjacent closed history and a company
        // override on the same dates are legitimate.
        db.GradeEntitlements.Add(Cell(null, Today.AddDays(-90), Today.AddDays(-31)));
        db.GradeEntitlements.Add(Cell(seed.CompanyId, Today.AddDays(-30), null));
        await db.SaveChangesAsync();

        db.GradeEntitlements.Add(Cell(null, Today.AddDays(5), null));            // overlaps the open tenant cell
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var pg = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal(PostgresErrorCodes.ExclusionViolation, pg.SqlState);
        Assert.Equal(GradeEntitlementSql.ExclusionName, pg.ConstraintName);
    }

    [Fact]
    public async Task Publish_ClosesAndOpensInOneTransaction_UnderTheRealExclusion()
    {
        var seed = await SeedAsync(cap: 10_000m, outstandingCap: null);
        var from = Today.AddDays(1);
        await using (var db = fixture.CreateDb())
        {
            var result = await Controller(db, seed, "HR Manager").PublishGradeLimits(new PublishGradeLimitsRequest(seed.LoanTypeId, null, from,
                [new(seed.GradeId, true, GradeEntitlementValueTypes.Amount, 7_500m, MaxOutstandingAmount: 20_000m)]), default);
            Assert.IsType<OkObjectResult>(result);
        }
        await using var verify = fixture.CreateDb();
        var versions = await verify.GradeEntitlements.Where(x => x.TenantId == seed.TenantId).OrderBy(x => x.EffectiveFrom).ToListAsync();
        Assert.Equal(2, versions.Count);
        Assert.Equal((10_000m, from.AddDays(-1)), (versions[0].Amount!.Value, versions[0].EffectiveTo!.Value));
        Assert.Equal((7_500m, 20_000m, (DateOnly?)null), (versions[1].Amount!.Value, versions[1].MaxOutstandingAmount!.Value, versions[1].EffectiveTo));
        Assert.Equal(Guid.Empty, versions[1].CompanyKey);                       // generated column: tenant-wide scope
        Assert.Equal(1, await verify.AuditLogs.CountAsync(x => x.TenantId == seed.TenantId && x.Action == "loans.grade_limit.published"));
    }

    [Fact]
    public async Task ConcurrentApplications_CannotTogetherExceedTheGradeOutstandingCap()
    {
        var seed = await SeedAsync(cap: 10_000m, outstandingCap: 10_000m);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ready = new SemaphoreSlim(0, 2);
        async Task<IActionResult> Apply()
        {
            await using var db = fixture.CreateDb();
            await db.Database.OpenConnectionAsync();
            ready.Release();
            await gate.Task;
            return await Controller(db, seed, "Employee").CreateLoan(
                new CreateLoanRequest(seed.EmployeePublicId, "", seed.LoanTypeId, 6_000m, 6, null, seed.EmployeeId), default);
        }
        var first = Apply();
        var second = Apply();
        await ready.WaitAsync();
        await ready.WaitAsync();
        gate.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Single(results, r => r is OkObjectResult);
        var refused = Assert.Single(results.OfType<BadRequestObjectResult>());
        Assert.Contains(GradeLimitCodes.Outstanding, JsonSerializer.Serialize(refused.Value));
        await using var verify = fixture.CreateDb();
        var loans = await verify.EmployeeLoans.Where(x => x.TenantId == seed.TenantId).ToListAsync();
        Assert.Single(loans);
        Assert.True(loans.Sum(x => x.RequestedAmount) <= 10_000m);
    }

    /// <summary>
    /// The Facility component must be invisible to payroll. Two tenants, identical in every respect except that
    /// one carries the loan Facility component (created exactly as production creates it) and a grade cell: the
    /// payroll runs must produce identical earnings, deductions, slips and validation results.
    /// <para><c>seedCatalog: false</c> is the real first-use path: a tenant that never wrote a pay component runs
    /// on the compiled catalog, and publishing grade limits is the first write to its store (the Facility row
    /// plus the system rows seeded beside it).</para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FacilityComponent_LeavesPayrollOutputUnchanged(bool seedCatalog)
    {
        var plain = await SeedPayrollAsync(withFacility: false, seedCatalog);
        var facility = await SeedPayrollAsync(withFacility: true, seedCatalog);
        await using (var check = fixture.CreateDb())
        {
            Assert.Equal(seedCatalog, await check.PayComponents.AnyAsync(x => x.TenantId == plain.TenantId));
            Assert.True(await check.PayComponents.AnyAsync(x => x.TenantId == facility.TenantId && x.IsSystem));
        }
        await using (var check = fixture.CreateDb())
            Assert.True(await check.PayComponents.AnyAsync(x => x.TenantId == facility.TenantId
                && x.EntitlementClass == PayEntitlementClasses.Facility && x.IsActive && !x.IsDeleted));

        foreach (var (tenantId, runId, _) in new[] { plain, facility })
            await using (var db = fixture.CreateDb())
            {
                var result = await PayComponentNetPayDefectTests.Build(db, tenantId).Process(runId, CancellationToken.None);
                if (result is ObjectResult { StatusCode: >= 400 } bad)
                    Assert.Fail($"Process refused: HTTP {bad.StatusCode} {JsonSerializer.Serialize(bad.Value)}");
            }

        var expected = await PayrollOutputAsync(plain.RunId);
        var actual = await PayrollOutputAsync(facility.RunId);
        Assert.NotEmpty(expected);
        Assert.DoesNotContain("LOAN_PERSONAL", actual);
        Assert.Equal(expected, actual);
    }

    // ── harness ──────────────────────────────────────────────────────────────────────────────────

    private sealed record Seed(Guid TenantId, Guid CompanyId, Guid GradeId, Guid LoanTypeId, int EmployeeId, Guid EmployeePublicId, Guid EmployeeUserId);

    private async Task<Seed> SeedAsync(decimal? cap, decimal? outstandingCap)
    {
        await using var db = fixture.CreateDb();
        var tid = await PostgresFixture.SeedMinimalTenant(db);
        var userId = Guid.NewGuid();
        var company = new Company { TenantId = tid, LegalNameEn = $"Grade PG {Guid.NewGuid():N}", CountryCode = "SAU", DefaultCurrency = "SAR", IsActive = true };
        var grade = new Grade { TenantId = tid, Code = "G2", Name = "Grade 2", Level = 2 };
        var type = new LoanType { TenantId = tid, Code = "PERSONAL", NameEn = "Personal", MaxInstallments = 24, IsActive = true,
            GradeLimited = true, EntitlementComponentCode = "LOAN_PERSONAL" };
        var employee = new Employee { TenantId = tid, CompanyId = company.Id, EmployeeCode = "PG-GL", FullName = "Grade Borrower",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-3), GradeId = grade.Id, UserAccountId = userId };
        db.AddRange(company, grade, type, employee,
            new LoanPolicy { TenantId = tid, CompanyId = company.Id, LoanTypeId = type.Id, MaxConcurrentLoans = 10, MaxInstallments = 24, PolicyName = "PG" });
        await db.SaveChangesAsync();
        db.GradeEntitlements.Add(new GradeEntitlement
        {
            TenantId = tid, GradeId = grade.Id, PayComponentCode = "LOAN_PERSONAL", EntitlementClass = PayEntitlementClasses.Facility,
            Eligible = true, ValueType = cap.HasValue ? GradeEntitlementValueTypes.Amount : GradeEntitlementValueTypes.EligibilityOnly,
            Amount = cap, MaxOutstandingAmount = outstandingCap, EffectiveFrom = Today.AddDays(-30),
        });
        await db.SaveChangesAsync();
        return new(tid, company.Id, grade.Id, type.Id, employee.Id, employee.PublicId, userId);
    }

    private static LoansController Controller(ZayraDbContext db, Seed seed, string role) => new(db, new OrgScope())
    {
        ControllerContext = new()
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", seed.TenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, (role == "Employee" ? seed.EmployeeUserId : Guid.NewGuid()).ToString()),
                    new Claim(ClaimTypes.Role, role),
                }, "test")),
            },
        },
    };

    private async Task<(Guid TenantId, Guid RunId, int EmployeeId)> SeedPayrollAsync(bool withFacility, bool seedCatalog)
    {
        await using var db = fixture.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        if (seedCatalog) await PayComponentSeeder.SeedTenantDefaultsAsync(db, tenantId, CancellationToken.None);
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = $"Facility Co {Guid.NewGuid():N}", CountryCode = "SAU", Jurisdiction = "KSA-mainland",
            RegistrationNumber = $"FC-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true,
            CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var grade = new Grade { TenantId = tenantId, Code = "G1", Name = "Grade 1", Level = 1 };
        var emp = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "FC-1", FullName = "Facility Saudi", Nationality = "Saudi",
            ContractType = "Indefinite", Status = "Active", JoiningDate = new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc), GradeId = grade.Id,
        };
        db.AddRange(company, grade, emp);
        await db.SaveChangesAsync();
        if (withFacility)
        {
            var type = new LoanType { TenantId = tenantId, Code = "PERSONAL", NameEn = "Personal", MaxInstallments = 12 };
            db.LoanTypes.Add(type);
            var (code, error) = await GradeLoanLimitResolver.EnsureFacilityComponentAsync(db, tenantId, type, null, CancellationToken.None);
            Assert.Null(error);
            type.GradeLimited = true;
            await db.SaveChangesAsync();
            db.GradeEntitlements.Add(new GradeEntitlement
            {
                TenantId = tenantId, GradeId = grade.Id, PayComponentCode = code!, EntitlementClass = PayEntitlementClasses.Facility,
                Eligible = true, ValueType = GradeEntitlementValueTypes.Amount, Amount = 50_000m, MaxOutstandingAmount = 80_000m,
                EffectiveFrom = new DateOnly(2024, 1, 1),
            });
        }
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = emp.Id, SalaryStructureId = Guid.NewGuid(), BasicSalary = 10_000m, HousingAllowance = 3_000m,
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
        return (tenantId, run.Id, emp.Id);
    }

    /// <summary>Everything a run emits, with identifiers that legitimately differ per tenant removed.</summary>
    private async Task<string> PayrollOutputAsync(Guid runId)
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

    private sealed class OrgScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }
}
