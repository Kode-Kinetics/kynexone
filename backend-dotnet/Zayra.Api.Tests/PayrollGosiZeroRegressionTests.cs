using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

// Regression test for: payroll computes zero GOSI when run.CompanyId is null.
//
// Root cause: CreateRun did not require a CompanyId. When CompanyId is null,
// the company lookup returns null → packCc="" → CountryPackResolver falls back
// to DefaultStatutoryDeductionCalculator → 0% deductions for every employee.
//
// Fix: if run.CompanyId is null, PayrollController.Process() falls back to the
// first active company for the tenant. This test verifies that fallback on a
// real Postgres database so the EF query and deduction math are both exercised.

[Trait("Category", "Integration")]
public class PayrollGosiZeroRegressionTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fx;
    public PayrollGosiZeroRegressionTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task Process_NullCompanyId_FallsBackToTenantFirstCompany_KsaGosiApplied()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);

        // KSA company — not linked to the run (CompanyId=null on the run)
        var company = new Company
        {
            Id                 = Guid.NewGuid(),
            TenantId           = tenantId,
            LegalNameEn        = "GOSI Regression Co",
            CountryCode        = "SAU",
            Jurisdiction       = "KSA-mainland",
            RegistrationNumber = "GOSI-REG-001",
            DefaultCurrency    = "SAR",
            IsActive           = true,
            CreatedAtUtc       = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Companies.Add(company);

        // Saudi national — should attract 9.75% EE GOSI on basic+housing
        var saudi = new Employee
        {
            TenantId     = tenantId,
            EmployeeCode = $"G-SAU-{Guid.NewGuid():N}",
            FullName     = "Ahmed Regression",
            Nationality  = "Saudi",
            Status       = "Active",
            JoiningDate  = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        // Expat — should have zero EE GOSI (employer 2% OH only, not in net pay)
        var expat = new Employee
        {
            TenantId     = tenantId,
            EmployeeCode = $"G-IND-{Guid.NewGuid():N}",
            FullName     = "Raj Regression",
            Nationality  = "Indian",
            Status       = "Active",
            JoiningDate  = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.AddRange(saudi, expat);
        await db.SaveChangesAsync();

        // Salary structures: basic + housing so GosiCoveredWage = basic + housing
        var saudiSalary = new EmployeeSalaryStructure
        {
            TenantId          = tenantId,
            EmployeeId        = saudi.Id,
            SalaryStructureId = Guid.NewGuid(), // no FK constraint on this column
            BasicSalary       = 10_000m,
            HousingAllowance  = 3_000m,
            EffectiveDate     = new DateOnly(2024, 1, 1),
            IsActive          = true,
        };
        var expatSalary = new EmployeeSalaryStructure
        {
            TenantId          = tenantId,
            EmployeeId        = expat.Id,
            SalaryStructureId = Guid.NewGuid(),
            BasicSalary       = 8_000m,
            HousingAllowance  = 2_000m,
            EffectiveDate     = new DateOnly(2024, 1, 1),
            IsActive          = true,
        };
        db.EmployeeSalaryStructures.AddRange(saudiSalary, expatSalary);

        // The bug scenario: payroll run has no CompanyId
        var run = new PayrollRun
        {
            TenantId  = tenantId,
            CompanyId = null,   // ← this is what caused zero GOSI before the fix
            Year      = 2026,
            Month     = 6,
            CreatedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();

        var ctrl = BuildController(db, tenantId);
        var result = await ctrl.Process(run.Id, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);

        // ── Saudi: covered wage = 13,000 → EE GOSI 9.75% = 1,267.50 ──────────
        var saudiSlip = await db.PayrollSlips
            .FirstAsync(s => s.TenantId == tenantId && s.EmployeeId == saudi.Id);
        Assert.True(saudiSlip.Deductions > 0,
            $"Saudi employee must have GOSI deduction after fix; got {saudiSlip.Deductions}. " +
            "If this is zero the company fallback lookup is still broken.");
        // 13,000 × 9.75% = 1,267.50
        Assert.Equal(13_000m * 0.0975m, saudiSlip.Deductions, precision: 2);
        // EmployeeStatutoryTotal must equal Deductions (only GOSI here, no loans etc.)
        Assert.Equal(saudiSlip.Deductions, saudiSlip.EmployeeStatutoryTotal, precision: 2);
        // Employer GOSI (9% annuity + 0.75% SANED + 2% OH = 11.75%) must be stored but NOT in Deductions
        // 13,000 × 11.75% = 1,527.50
        Assert.Equal(13_000m * 0.1175m, saudiSlip.EmployerStatutoryTotal, precision: 2);

        // ── Expat: no employee GOSI, but employer OH (2%) is tracked ──────────
        var expatSlip = await db.PayrollSlips
            .FirstAsync(s => s.TenantId == tenantId && s.EmployeeId == expat.Id);
        Assert.Equal(0m, expatSlip.Deductions);
        Assert.Equal(0m, expatSlip.EmployeeStatutoryTotal);
        // Expat employer OH = 10,000 × 2% = 200
        Assert.Equal(10_000m * 0.02m, expatSlip.EmployerStatutoryTotal, precision: 2);

        // ── Net = gross − deductions for Saudi ────────────────────────────────
        Assert.Equal(saudiSlip.GrossSalary - saudiSlip.Deductions, saudiSlip.NetSalary, precision: 2);

        // ── PayrollRun totals include employer statutory cost ──────────────────
        var processedRun = await db.PayrollRuns.FirstAsync(r => r.Id == run.Id);
        Assert.Equal(saudiSlip.EmployerStatutoryTotal + expatSlip.EmployerStatutoryTotal,
            processedRun.TotalEmployerStatutoryCost, precision: 2);

        // ── PayrollDeduction lines persisted for breakdown ─────────────────────
        var saudiLines = await db.PayrollDeductions.AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.PayrollRunId == run.Id && d.EmployeeId == saudi.Id && d.Source == "Statutory")
            .ToListAsync();
        Assert.NotEmpty(saudiLines);
        Assert.Contains(saudiLines, l => l.ComponentCode == "GOSI-ANN-EE" && l.Amount > 0);
        Assert.Contains(saudiLines, l => l.ComponentCode == "GOSI-SANED-EE" && l.Amount > 0);
        Assert.Contains(saudiLines, l => l.ComponentCode == "GOSI-OH-ER" && l.Amount > 0);
    }

    // ── Helper: build PayrollController with KSA pack resolver ────────────────

    private static PayrollController BuildController(ZayraDbContext db, Guid tenantId)
    {
        var ruleReader = new StubRuleReader()
            .Set("gosi.saudi_employee_rate",            0.09m)
            .Set("gosi.saudi_employer_rate",            0.09m)
            .Set("gosi.saned_rate",                     0.0075m)
            .Set("gosi.expat_occupational_hazard_rate", 0.02m)
            .Set("gosi.covered_wage_ceiling_sar",       45_000m);

        var ctrl = new PayrollController(
            db,
            new DataScopeService(db),
            new HttpContextAccessor(),
            new GosiRegressionNotificationStub(),
            new GosiRegressionPackResolver(ruleReader),
            new GosiRegressionLetterStub());

        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id",                        tenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier,          Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role,                    "Admin"),
                }, "Test"))
            }
        };
        return ctrl;
    }
}

// ── StatutoryRuleReader DateTimeKind regression ────────────────────────────────
//
// Root cause: StatutoryRuleReader.FetchAsync used DateOnly.ToDateTime(TimeOnly.MinValue)
// which returns DateTimeKind.Unspecified. Npgsql (with default timestamptz behaviour)
// rejects Unspecified datetimes for 'timestamp with time zone' columns, throwing
// ArgumentException: "Cannot write DateTime with Kind=Unspecified to PostgreSQL type
// 'timestamp with time zone', only UTC is supported."
//
// Fix: use DateOnly.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) so the value
// is always UTC-flagged and accepted by Npgsql.
//
// This test exercises Process with the REAL StatutoryRuleReader (not a stub) so that
// any regression in the DateTimeKind fix is caught before hitting production.

[Trait("Category", "Integration")]
public class StatutoryRuleReaderDateTimeKindRegressionTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fx;
    public StatutoryRuleReaderDateTimeKindRegressionTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task Process_WithRealStatutoryRuleReader_DoesNotThrowDateTimeKindException()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);

        var company = new Company
        {
            Id = Guid.NewGuid(), TenantId = tenantId,
            LegalNameEn = "DateTimeKind Regression Co",
            CountryCode = "SAU", Jurisdiction = "KSA-mainland",
            RegistrationNumber = $"DTK-{Guid.NewGuid():N}",
            DefaultCurrency = "SAR", IsActive = true,
            CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Companies.Add(company);

        var emp = new Employee
        {
            TenantId = tenantId,
            EmployeeCode = $"DTK-{Guid.NewGuid():N}",
            FullName = "DateTimeKind Test Employee",
            Nationality = "Saudi",
            Status = "Active",
            JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();

        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = emp.Id,
            SalaryStructureId = Guid.NewGuid(),
            BasicSalary = 10_000m, HousingAllowance = 3_000m,
            EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
        });

        var run = new PayrollRun
        {
            TenantId = tenantId, CompanyId = company.Id,
            Year = 2026, Month = 6,
            CreatedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();

        // Use real StatutoryRuleReader backed by the Postgres container — the table is empty
        // so all reads return null and the calculator falls back to hardcoded defaults.
        // The key assertion is that no ArgumentException (DateTimeKind.Unspecified) is thrown.
        var realRuleReader = new Zayra.Api.Infrastructure.CountryPack.StatutoryRuleReader(db);
        var ctrl = BuildControllerWithRealReader(db, tenantId, realRuleReader);

        var result = await ctrl.Process(run.Id, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);

        var slip = await db.PayrollSlips.FirstAsync(s => s.TenantId == tenantId && s.EmployeeId == emp.Id);
        Assert.True(slip.EmployeeStatutoryTotal > 0,
            "Saudi employee must have non-zero EE GOSI even when statutory_rules table is empty " +
            "(calculator falls back to hardcoded 9.75%). If zero, real StatutoryRuleReader or " +
            "the KSA pack calculator is broken.");
    }

    private static PayrollController BuildControllerWithRealReader(
        ZayraDbContext db, Guid tenantId, Zayra.Api.Application.CountryPack.IStatutoryRuleReader reader)
    {
        var resolver = new GosiRegressionPackResolver(reader);
        var ctrl = new PayrollController(
            db, new DataScopeService(db), new HttpContextAccessor(),
            new GosiRegressionNotificationStub(), resolver, new GosiRegressionLetterStub());
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role, "Admin"),
                }, "Test"))
            }
        };
        return ctrl;
    }
}

// ── DbContextPool tenant-isolation regression ──────────────────────────────────
//
// Root cause: AddDbContextPool<ZayraDbContext> reuses instances across requests without
// re-running the constructor.  Before the fix, _tenantId was set once in the constructor
// from the first request's JWT claim.  On pool reuse by a different tenant the global
// query filter still carried the stale tenant ID, contradicting the explicit TenantId
// predicate → 0 rows → "Company not found or not active" (CreateRun line 105).
//
// Fix: _tenantId / _actorId are now lazy properties that read IHttpContextAccessor.HttpContext
// on every access.  IHttpContextAccessor is a singleton backed by AsyncLocal, so it always
// reflects the current request even when the DbContext is pool-reused.

[Trait("Category", "Integration")]
public class CreateRunPoolReuseRegressionTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fx;
    public CreateRunPoolReuseRegressionTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task CompanyLookup_WithSameDbContextInstance_WorksForBothTenants()
    {
        // Seed two independent tenants, each with an active SAU company
        await using var seedDb = _fx.CreateDb();
        var tenantA = await PostgresFixture.SeedMinimalTenant(seedDb);
        var tenantB = await PostgresFixture.SeedMinimalTenant(seedDb);

        var companyA = new Company
        {
            Id = Guid.NewGuid(), TenantId = tenantA,
            LegalNameEn = "Company Alpha", CountryCode = "SAU", Jurisdiction = "KSA-mainland",
            RegistrationNumber = $"A-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true,
            CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var companyB = new Company
        {
            Id = Guid.NewGuid(), TenantId = tenantB,
            LegalNameEn = "Company Beta", CountryCode = "SAU", Jurisdiction = "KSA-mainland",
            RegistrationNumber = $"B-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true,
            CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        seedDb.Companies.AddRange(companyA, companyB);
        await seedDb.SaveChangesAsync();

        // Use a SWITCHABLE accessor on the SAME DbContext instance to simulate DbContextPool
        // reuse: request A uses the context, returns it to the pool, request B gets it back.
        // Before the fix, the constructor-cached _tenantId would still be tenant A's value
        // during request B's lookup, making the global filter contradict the explicit predicate.
        var accessor = new SwitchableHttpContextAccessor();
        await using var db = _fx.CreateDbWithAccessor(accessor);

        // Request A
        accessor.HttpContext = MakeHttpContext(tenantA);
        var foundA = await db.Companies.AnyAsync(c => c.TenantId == tenantA && c.Id == companyA.Id && c.IsActive);
        Assert.True(foundA, "Tenant A's company must be found when accessor is set to tenant A.");

        // Simulate pool reuse: same DbContext, different tenant
        accessor.HttpContext = MakeHttpContext(tenantB);
        var foundB = await db.Companies.AnyAsync(c => c.TenantId == tenantB && c.Id == companyB.Id && c.IsActive);
        Assert.True(foundB,
            "Tenant B's company must be found after switching accessor (pool-reuse scenario). " +
            "Failure means _tenantId is no longer resolved lazily — the fix has regressed.");

        // ── Isolation: global-filter-only queries must not leak cross-tenant ────────
        //
        // The queries below have NO explicit WHERE tenant_id predicate — isolation
        // is provided SOLELY by the global query filter (_tenantId property).
        // This proves the leak path is closed, not just that targeted lookups work.

        // Case 1: accessor=tenantB, no explicit predicate → must NOT return company A (tenantA's data)
        // SQL: SELECT EXISTS (SELECT 1 FROM companies WHERE tenant_id=@tenantB AND NOT is_deleted AND id=@companyAId)
        // companyA.TenantId=tenantA ≠ tenantB → 0 rows.
        var leakBseesA = await db.Companies.AnyAsync(c => c.Id == companyA.Id);
        Assert.False(leakBseesA,
            "Global filter must exclude company A when accessor=tenantB (no explicit WHERE tenant_id). " +
            "If true, the global filter is not applying correctly for the current accessor context.");

        // Case 2: switch accessor to tenantA, no explicit predicate → must NOT return company B (tenantB's data)
        // SQL: SELECT EXISTS (SELECT 1 FROM companies WHERE tenant_id=@tenantA AND NOT is_deleted AND id=@companyBId)
        // companyB.TenantId=tenantB ≠ tenantA → 0 rows.
        // This is the scenario the pool-reuse bug enabled before the fix: if _tenantId were still
        // cached as tenantA after a prior request, a query on a "tenantA context" would apply the
        // global filter tenant_id=tenantA, hiding tenantB rows — which is correct. But before the
        // fix, a "tenantB context" might have _tenantId=tenantA (stale), making it see tenantA data.
        accessor.HttpContext = MakeHttpContext(tenantA);
        var leakAseesB = await db.Companies.AnyAsync(c => c.Id == companyB.Id);
        Assert.False(leakAseesB,
            "Global filter must exclude company B when accessor=tenantA (no explicit WHERE tenant_id). " +
            "This is the primary isolation proof: accessor determines the filter, not a stale cached value.");
    }

    private static HttpContext MakeHttpContext(Guid tenantId)
    {
        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("tenant_id", tenantId.ToString()) }, "Test"));
        return ctx;
    }
}

// ── Test-local helpers ─────────────────────────────────────────────────────────

// Resolver that returns the real KSA deduction calculator for "SAU" and zero
// defaults for everything else.  This proves the test's assertion: if CompanyId
// fallback works, packCc becomes "SAU" and the Saudi employee gets 9.75% GOSI;
// if the bug were still present, packCc would be "" → default → 0 deductions.
file sealed class GosiRegressionPackResolver : ICountryPackResolver
{
    private readonly IStatutoryRuleReader _r;
    public GosiRegressionPackResolver(IStatutoryRuleReader r) => _r = r;

    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => cc == "SAU"
            ? new KsaDeductionCalculator(_r)
            : new DefaultStatutoryDeductionCalculator();

    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j)
        => new DefaultEndOfServiceCalculator();

    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j)
        => new DefaultWageProtectionExporter();

    public INationalizationTracker ResolveNationalizationTracker(string cc, string j)
        => new DefaultNationalizationTracker();

    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j)
        => new DefaultLocalizationProfile();

    public ICountryPackDescriptor ResolveDescriptor(string cc, string j)
        => new DefaultCountryPackDescriptor();
}

file sealed class GosiRegressionNotificationStub : INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message,
        string entityName, string? entityId, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName,
        Dictionary<string, string> variables, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

// Allows a single test to simulate DbContextPool reuse by changing the HttpContext
// mid-test on the same IHttpContextAccessor instance that was injected at construction.
file sealed class SwitchableHttpContextAccessor : IHttpContextAccessor
{
    public HttpContext? HttpContext { get; set; }
}

file sealed class GosiRegressionLetterStub : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData data, CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData data, CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData data, CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData data, CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<byte>());
}
