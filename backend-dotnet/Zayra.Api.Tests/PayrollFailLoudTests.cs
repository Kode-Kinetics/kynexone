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
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

// P0 fail-loud and data-consistency integration tests.
//
// Root cause context: IntelliFlow split-tenant bug — employees lived in tenant
// 4afd127d (no company) while the company lived in dd7c2ff9 (no employees).
// CountryPackResolver got countryCode="" → DefaultStatutoryDeductionCalculator →
// 0% deductions, net=gross for all 333,000 payslips.
//
// After this fix: Process() aborts with 422 whenever it cannot resolve a real
// statutory pack. Validation engine adds run-level ERROR rules so even manually
// constructed bad runs are blocked before Approve/Lock.

[Trait("Category", "Integration")]
[Collection("Integration")]
public class PayrollFailLoudTests
{
    private readonly PostgresFixture _fx;
    public PayrollFailLoudTests(PostgresFixture fx) => _fx = fx;

    // ── (a) No active company → Process aborts, ZERO payslips written ─────────
    //
    // This is the exact IntelliFlow scenario: employees exist in the tenant but
    // there is no active company, so CountryPackResolver cannot determine the
    // statutory jurisdiction. Process() must return 422 and write no DB rows.

    [Fact]
    public async Task Process_NoActiveCompany_Returns422_ZeroPayslipsWritten()
    {
        await using var db  = _fx.CreateDb();
        var tenantId        = await PostgresFixture.SeedMinimalTenant(db);

        // Seed employees + salary structures — deliberately NO company.
        var emp1 = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"FL-E1-{Guid.NewGuid():N}",
            FullName = "Ahmed NoCompany", Nationality = "Saudi", Status = "Active",
            JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var emp2 = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"FL-E2-{Guid.NewGuid():N}",
            FullName = "Raj NoCompany", Nationality = "Indian", Status = "Active",
            JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.AddRange(emp1, emp2);
        await db.SaveChangesAsync();

        db.EmployeeSalaryStructures.AddRange(
            new EmployeeSalaryStructure
            {
                TenantId = tenantId, EmployeeId = emp1.Id,
                SalaryStructureId = Guid.NewGuid(), BasicSalary = 10_000m,
                HousingAllowance  = 3_000m, EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
            },
            new EmployeeSalaryStructure
            {
                TenantId = tenantId, EmployeeId = emp2.Id,
                SalaryStructureId = Guid.NewGuid(), BasicSalary = 8_000m,
                HousingAllowance  = 2_000m, EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
            });

        var run = new PayrollRun
        {
            TenantId = tenantId, CompanyId = null,
            Year = 2026, Month = 12,
            CreatedAtUtc = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();

        var ctrl   = BuildController(db, tenantId, BuildKsaResolver());
        var result = await ctrl.Process(run.Id, CancellationToken.None);

        // Must return 422 — not 200 / not 500.
        var unproc = Assert.IsType<UnprocessableEntityObjectResult>(result);
        var body   = unproc.Value as dynamic;
        Assert.Equal("company_not_resolved", (string)body!.GetType().GetProperty("error")!.GetValue(body)!);

        // The run must stay in its original status (Draft / not Processed).
        var reloaded = await db.PayrollRuns.FirstAsync(r => r.Id == run.Id);
        Assert.NotEqual("Processed", reloaded.Status);

        // CRITICAL: zero payslips must be written.
        var slipCount = await db.PayrollSlips.CountAsync(s => s.TenantId == tenantId && s.RunId == run.Id);
        Assert.Equal(0, slipCount);
    }

    // ── (b) Saudi employee with 0 GOSI → validation ERROR blocks Approve ──────
    //
    // Manually insert a "bad" payslip (zero GOSI for a Saudi employee) and verify
    // the validation engine generates an ERROR that blocks Approve at the API.

    [Fact]
    public async Task Approve_SaudiZeroGosi_IsBlockedByValidationError()
    {
        await using var db  = _fx.CreateDb();
        var tenantId        = await PostgresFixture.SeedMinimalTenant(db);

        var company = new Company
        {
            Id = Guid.NewGuid(), TenantId = tenantId,
            LegalNameEn = "GOSI-Block Co", CountryCode = "SAU", Jurisdiction = "KSA-mainland",
            RegistrationNumber = $"BLK-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true,
            CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Companies.Add(company);

        var saudi = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"FL-S-{Guid.NewGuid():N}",
            FullName = "Saudi ZeroGosi", Nationality = "Saudi", Status = "Active",
            JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(saudi);
        await db.SaveChangesAsync();

        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = saudi.Id,
            SalaryStructureId = Guid.NewGuid(), BasicSalary = 10_000m,
            HousingAllowance = 3_000m, EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
        });

        // Manually craft a processed run with a payslip that has 0 GOSI (the wrong state).
        var run = new PayrollRun
        {
            TenantId = tenantId, CompanyId = company.Id,
            Year = 2026, Month = 11, Status = "Processed",
            TotalGrossSalary = 13_000m, TotalDeductions = 0m, TotalNetSalary = 13_000m,
            EmployeeCount = 1,
            CreatedAtUtc  = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.PayrollRuns.Add(run);

        // Deliberately wrong: Saudi employee, 0 GOSI deductions, net == gross.
        var badSlip = new PayrollSlip
        {
            TenantId = tenantId, RunId = run.Id, EmployeeId = saudi.Id,
            EmployeeCode = saudi.EmployeeCode, EmployeeName = saudi.FullName,
            BasicSalary = 10_000m, HousingAllowance = 3_000m, GrossSalary = 13_000m,
            Deductions = 0m, NetSalary = 13_000m,
            EmployeeStatutoryTotal = 0m, EmployerStatutoryTotal = 0m,
            Status = "Draft",
        };
        db.PayrollSlips.Add(badSlip);
        await db.SaveChangesAsync();

        // Seed the validation ERROR so Approve can block on it.
        db.PayrollValidationResults.Add(new PayrollValidationResult
        {
            TenantId = tenantId, PayrollRunId = run.Id, EmployeeId = saudi.Id,
            Severity = "Error", Code = "GOSI_MISSING_FOR_SAUDI",
            Message  = "Saudi employee has zero GOSI. Approval blocked.",
            IsResolved = false,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var ctrl   = BuildController(db, tenantId, BuildKsaResolver(), "Finance Controller");
        var result = await ctrl.Approve(run.Id, new PayrollDecisionRequest("Approve"), CancellationToken.None);

        // Approve must be blocked — 422, not 200.
        var blocked = Assert.IsType<UnprocessableEntityObjectResult>(result);

        // Run must remain in Processed status.
        var reloaded = await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == run.Id);
        Assert.Equal("Processed", reloaded.Status);
    }

    // ── (c) Split-tenant reproduced → consolidated → correct GOSI ─────────────
    //
    // Reproduce the IntelliFlow scenario exactly: employees in Tenant A, company in Tenant B.
    // Phase 1 (reproduced): Process for Tenant A (employees only, no company) → 422.
    // Phase 2 (fixed): after consolidating company into Tenant A, Process succeeds and
    // the Saudi employee gets the correct 9.75% GOSI EE deduction (not zero).

    [Fact]
    public async Task SplitTenant_Reproduced_ThenFixed_ProducesCorrectGosi()
    {
        await using var db = _fx.CreateDb();

        // Seed two separate tenants — the split-tenant scenario.
        var tenantEmployees = await PostgresFixture.SeedMinimalTenant(db);  // "4afd127d" analogue
        var tenantCompany   = await PostgresFixture.SeedMinimalTenant(db);  // "dd7c2ff9" analogue

        // Company lives in tenantCompany only (not in tenantEmployees).
        var company = new Company
        {
            Id = Guid.NewGuid(), TenantId = tenantCompany,
            LegalNameEn = "IntelliFlow Ltd", CountryCode = "SAU", Jurisdiction = "KSA-mainland",
            RegistrationNumber = $"IFL-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true,
            CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Companies.Add(company);

        // Employees live in tenantEmployees only (no company in this tenant).
        var saudi = new Employee
        {
            TenantId = tenantEmployees, EmployeeCode = $"IFL-SAU-{Guid.NewGuid():N}",
            FullName = "Ahmed IntelliFlow", Nationality = "Saudi", Status = "Active",
            JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var expat = new Employee
        {
            TenantId = tenantEmployees, EmployeeCode = $"IFL-EXP-{Guid.NewGuid():N}",
            FullName = "Bob IntelliFlow", Nationality = "Indian", Status = "Active",
            JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.AddRange(saudi, expat);
        await db.SaveChangesAsync();

        db.EmployeeSalaryStructures.AddRange(
            new EmployeeSalaryStructure
            {
                TenantId = tenantEmployees, EmployeeId = saudi.Id,
                SalaryStructureId = Guid.NewGuid(), BasicSalary = 10_000m,
                HousingAllowance = 3_000m, EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
            },
            new EmployeeSalaryStructure
            {
                TenantId = tenantEmployees, EmployeeId = expat.Id,
                SalaryStructureId = Guid.NewGuid(), BasicSalary = 8_000m,
                HousingAllowance = 2_000m, EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
            });
        await db.SaveChangesAsync();

        // ── Phase 1: split state → Process for tenantEmployees must 422 ─────────
        var splitRun = new PayrollRun
        {
            TenantId = tenantEmployees, CompanyId = null,
            Year = 2026, Month = 12,
            CreatedAtUtc = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.PayrollRuns.Add(splitRun);
        await db.SaveChangesAsync();

        var splitCtrl  = BuildController(db, tenantEmployees, BuildKsaResolver());
        var splitResult = await splitCtrl.Process(splitRun.Id, CancellationToken.None);

        Assert.IsType<UnprocessableEntityObjectResult>(splitResult);
        var zeroSlips = await db.PayrollSlips.CountAsync(s => s.TenantId == tenantEmployees && s.RunId == splitRun.Id);
        Assert.Equal(0, zeroSlips);

        // ── Phase 2: fix — move company to tenantEmployees (consolidation) ───────
        // Simulate the data migration: update the company's TenantId to tenantEmployees.
        // In production this is done via SQL UPDATE; here we do it in-test via EF.
        await db.Companies
            .Where(c => c.Id == company.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.TenantId, tenantEmployees));

        // Link the existing run to the consolidated company so Process() can find it.
        await db.PayrollRuns
            .Where(r => r.Id == splitRun.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.CompanyId, company.Id));

        // Re-process the same run — Process() is idempotent (deletes prior slips before computing).
        var fixedCtrl   = BuildController(db, tenantEmployees, BuildKsaResolver());
        var fixedResult = await fixedCtrl.Process(splitRun.Id, CancellationToken.None);

        Assert.IsType<OkObjectResult>(fixedResult);

        // Saudi: covered wage 13,000 × 9.75% = 1,267.50 EE GOSI
        var saudiSlip = await db.PayrollSlips
            .FirstAsync(s => s.TenantId == tenantEmployees && s.RunId == splitRun.Id && s.EmployeeId == saudi.Id);
        Assert.True(saudiSlip.EmployeeStatutoryTotal > 0,
            $"Saudi employee must have non-zero GOSI after consolidation; got {saudiSlip.EmployeeStatutoryTotal}");
        Assert.Equal(13_000m * 0.0975m, saudiSlip.EmployeeStatutoryTotal, precision: 2);
        Assert.True(saudiSlip.NetSalary < saudiSlip.GrossSalary,
            $"net ({saudiSlip.NetSalary}) must be less than gross ({saudiSlip.GrossSalary}) for Saudi after fix");

        // Expat: zero EE GOSI, non-zero employer OH
        var expatSlip = await db.PayrollSlips
            .FirstAsync(s => s.TenantId == tenantEmployees && s.RunId == splitRun.Id && s.EmployeeId == expat.Id);
        Assert.Equal(0m, expatSlip.EmployeeStatutoryTotal);
        // KSA OH is on covered wage (basic + housing) = 8,000 + 2,000 = 10,000 × 2% = 200
        Assert.Equal(10_000m * 0.02m, expatSlip.EmployerStatutoryTotal, precision: 2);
        Assert.Equal(expatSlip.GrossSalary, expatSlip.NetSalary, precision: 2);        // expat net == gross
    }

    // ── (d) rasalmanar Jun 2026 Locked run is NOT regressed ───────────────────
    //
    // Regression guard: a correctly-processed KSA run (company + employees in same tenant)
    // must still produce the same GOSI math after our guard changes.
    // Mirrors the rasalmanar Jun 2026 scenario: Saudi employee, 10k basic + 5k housing.
    // Covered wage 15,000 × 9.75% = 1,462.50.

    [Fact]
    public async Task ExistingCorrectRun_IsNotAffectedByFailLoudGuard()
    {
        await using var db  = _fx.CreateDb();
        var tenantId        = await PostgresFixture.SeedMinimalTenant(db);

        var company = new Company
        {
            Id = Guid.NewGuid(), TenantId = tenantId,
            LegalNameEn = "rasalmanar-analogue", CountryCode = "SAU", Jurisdiction = "KSA-mainland",
            RegistrationNumber = $"RSM-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true,
            CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Companies.Add(company);

        var saudi = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"RSM-{Guid.NewGuid():N}",
            FullName = "Ahmed rasalmanar", Nationality = "Saudi", Status = "Active",
            JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(saudi);
        await db.SaveChangesAsync();

        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = saudi.Id,
            SalaryStructureId = Guid.NewGuid(), BasicSalary = 10_000m,
            HousingAllowance = 5_000m, EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
        });

        var run = new PayrollRun
        {
            TenantId = tenantId, CompanyId = company.Id,
            Year = 2026, Month = 6,
            CreatedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();

        var ctrl   = BuildController(db, tenantId, BuildKsaResolver());
        var result = await ctrl.Process(run.Id, CancellationToken.None);

        // Must succeed — the guard must not block correctly configured tenants.
        Assert.IsType<OkObjectResult>(result);

        var slip = await db.PayrollSlips.FirstAsync(s => s.TenantId == tenantId && s.RunId == run.Id);

        // Covered wage 15,000 × 9% = 1,350 (annuities) + 15,000 × 0.75% = 112.50 (SANED) = 1,462.50
        Assert.Equal(15_000m * 0.0975m, slip.EmployeeStatutoryTotal, precision: 2);
        Assert.True(slip.NetSalary < slip.GrossSalary,
            "Correctly configured run must still deduct GOSI after guard changes");

        // Employer: 15,000 × (9% + 0.75% + 2%) = 15,000 × 11.75% = 1,762.50
        Assert.Equal(15_000m * 0.1175m, slip.EmployerStatutoryTotal, precision: 2);
    }

    // ── (e) No code path can persist a KSA run where Saudi employee has 0 GOSI ─
    //
    // Two angles:
    //   (e1) Process() guard prevents it at compute time.
    //   (e2) Validation engine generates GOSI_MISSING_FOR_SAUDI ERROR on a manually
    //        crafted bad run, which blocks Approve via the block-on-error gate.

    [Fact]
    public async Task ProcessGuard_PreventsZeroGosiRun_ForKsaTenant()
    {
        await using var db  = _fx.CreateDb();
        var tenantId        = await PostgresFixture.SeedMinimalTenant(db);

        // A tenant with no company at all — most dangerous split-tenant path.
        var saudi = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"FL-GUARD-{Guid.NewGuid():N}",
            FullName = "Guard Test Saudi", Nationality = "Saudi", Status = "Active",
            JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(saudi);
        await db.SaveChangesAsync();

        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = saudi.Id,
            SalaryStructureId = Guid.NewGuid(), BasicSalary = 15_000m,
            EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
        });

        var run = new PayrollRun
        {
            TenantId = tenantId, CompanyId = null,
            Year = 2026, Month = 12,
            CreatedAtUtc = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();

        var ctrl   = BuildController(db, tenantId, BuildKsaResolver());
        var result = await ctrl.Process(run.Id, CancellationToken.None);

        // Process must abort with 422 — not produce 0-GOSI payslips.
        Assert.IsType<UnprocessableEntityObjectResult>(result);

        // Assert ZERO payslips written — the guard fired before the per-employee loop.
        var persisted = await db.PayrollSlips
            .Where(s => s.TenantId == tenantId && s.RunId == run.Id)
            .ToListAsync();
        Assert.Empty(persisted);

        // Explicit invariant: no payslip in this run has net == gross with non-zero gross.
        Assert.DoesNotContain(persisted, s => s.NetSalary == s.GrossSalary && s.GrossSalary > 0);
    }

    [Fact]
    public async Task ValidationEngine_FlagsZeroGosiForSaudi_AsError()
    {
        // This covers e2: even if a 0-GOSI slip is seeded directly (bypassing Process),
        // the validation engine must flag GOSI_MISSING_FOR_SAUDI as an ERROR.

        var tenantId  = Guid.NewGuid();
        var runId     = Guid.NewGuid();
        var company   = new Company { Id = Guid.NewGuid(), TenantId = tenantId,
            LegalNameEn = "KSA Engine Co", CountryCode = "SAU",
            DefaultCurrency = "SAR", IsActive = true };

        var saudi  = new Employee { Id = 1, TenantId = tenantId, EmployeeCode = "SA01",
            FullName = "Saudi Test", Nationality = "Saudi", Status = "Active" };
        var run    = new PayrollRun { Id = runId, TenantId = tenantId, CompanyId = company.Id,
            Year = 2026, Month = 12, Status = "Processed",
            TotalGrossSalary = 13_000m, TotalDeductions = 0m, TotalNetSalary = 13_000m };
        var salary = new EmployeeSalaryStructure { Id = Guid.NewGuid(), TenantId = tenantId,
            EmployeeId = 1, SalaryStructureId = Guid.NewGuid(), BasicSalary = 10_000m, IsActive = true };
        var profile = new EmployeePayrollProfile { EmployeeId = 1, TenantId = tenantId,
            Iban = "SA4420000001234567891234", MolId = "MOL999", SalaryCurrency = "SAR" };

        // Payslip with 0 GOSI for Saudi — the wrong state.
        var badSlip = new PayrollSlip
        {
            Id = Guid.NewGuid(), TenantId = tenantId, RunId = runId, EmployeeId = 1,
            EmployeeCode = "SA01", EmployeeName = "Saudi Test",
            BasicSalary = 10_000m, HousingAllowance = 3_000m, GrossSalary = 13_000m,
            Deductions = 0m, NetSalary = 13_000m,
            EmployeeStatutoryTotal = 0m, EmployerStatutoryTotal = 0m,
            Status = "Draft",
        };

        // No deduction lines (= 0 GOSI).
        var ctx = new PayrollValidationContext(
            run, new[] { badSlip }, new[] { saudi }, new[] { salary },
            new[] { profile }, Array.Empty<PayrollDeduction>(),
            Array.Empty<PayrollEarning>(), company);

        var results = PayrollValidationEngine.Run(ctx);

        // Must produce GOSI_MISSING_FOR_SAUDI as an Error.
        Assert.Contains(results,
            r => r.Code == "GOSI_MISSING_FOR_SAUDI" && r.Severity == "Error");

        // Must NOT contain COMPANY_NOT_RESOLVED or COUNTRY_CODE_MISSING (company is valid here).
        Assert.DoesNotContain(results, r => r.Code == "COMPANY_NOT_RESOLVED");
        Assert.DoesNotContain(results, r => r.Code == "COUNTRY_CODE_MISSING");
    }

    [Fact]
    public async Task ValidationEngine_CompanyNull_ProducesCompanyNotResolvedError()
    {
        var tenantId = Guid.NewGuid();
        var runId    = Guid.NewGuid();

        var run = new PayrollRun
        {
            Id = runId, TenantId = tenantId, CompanyId = null,
            Year = 2026, Month = 12, Status = "Processed",
            TotalGrossSalary = 0m, TotalDeductions = 0m, TotalNetSalary = 0m,
        };

        // Company is null — the split-tenant case after Process() somehow wrote slips.
        var ctx = new PayrollValidationContext(
            run, Array.Empty<PayrollSlip>(), Array.Empty<Employee>(),
            Array.Empty<EmployeeSalaryStructure>(), Array.Empty<EmployeePayrollProfile>(),
            Array.Empty<PayrollDeduction>(), Array.Empty<PayrollEarning>(), null);

        var results = PayrollValidationEngine.Run(ctx);

        Assert.Contains(results, r => r.Code == "COMPANY_NOT_RESOLVED" && r.Severity == "Error");
    }

    [Fact]
    public async Task ValidationEngine_EmptyCountryCode_ProducesCountryCodeMissingError()
    {
        var tenantId = Guid.NewGuid();
        var runId    = Guid.NewGuid();

        var company = new Company
        {
            Id = Guid.NewGuid(), TenantId = tenantId,
            LegalNameEn = "No Country Co", CountryCode = string.Empty, IsActive = true,
        };

        var run = new PayrollRun
        {
            Id = runId, TenantId = tenantId, CompanyId = company.Id,
            Year = 2026, Month = 12, Status = "Processed",
        };

        var ctx = new PayrollValidationContext(
            run, Array.Empty<PayrollSlip>(), Array.Empty<Employee>(),
            Array.Empty<EmployeeSalaryStructure>(), Array.Empty<EmployeePayrollProfile>(),
            Array.Empty<PayrollDeduction>(), Array.Empty<PayrollEarning>(), company);

        var results = PayrollValidationEngine.Run(ctx);

        Assert.Contains(results, r => r.Code == "COUNTRY_CODE_MISSING" && r.Severity == "Error");
        Assert.DoesNotContain(results, r => r.Code == "COMPANY_NOT_RESOLVED");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static StubRuleReader BuildDefaultKsaRules() => new StubRuleReader()
        .Set("gosi.saudi_employee_rate",            0.09m)
        .Set("gosi.saudi_employer_rate",            0.09m)
        .Set("gosi.saned_rate",                     0.0075m)
        .Set("gosi.expat_occupational_hazard_rate", 0.02m)
        .Set("gosi.covered_wage_ceiling_sar",       45_000m)
        .Set("ot.standard_multiplier",              1.5m)
        .Set("ot.standard_monthly_hours",           240m)
        .Set("lop.monthly_day_divisor",             30m)
        .Set("lop.standard_work_minutes_per_day",   480m);

    private static ICountryPackResolver BuildKsaResolver()
        => new FailLoudPackResolver(BuildDefaultKsaRules());

    private static PayrollController BuildController(
        ZayraDbContext db, Guid tenantId, ICountryPackResolver resolver,
        string role = "Admin")
    {
        var ctrl = new PayrollController(
            db,
            new DataScopeService(db),
            new HttpContextAccessor(),
            new FailLoudNotificationStub(),
            resolver,
            BuildDefaultKsaRules(),
            new FailLoudLetterStub(),
            new NullDocumentStorage(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(8));

        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id",               tenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role,           role),
                }, "Test"))
            }
        };
        return ctrl;
    }
}

// ── Test-local stubs ───────────────────────────────────────────────────────────

// Resolves KSA pack for "SAU", UAE for "ARE", Qatar for "QAT".
// Returns DefaultStatutoryDeductionCalculator for any other country code — this
// is what triggers the fail-loud guard we added to Process().
file sealed class FailLoudPackResolver : ICountryPackResolver
{
    private readonly IStatutoryRuleReader _r;
    public FailLoudPackResolver(IStatutoryRuleReader r) => _r = r;

    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => cc switch
        {
            "SAU" => new KsaDeductionCalculator(_r),
            _     => new DefaultStatutoryDeductionCalculator(),
        };

    public IEndOfServiceCalculator       ResolveEndOfServiceCalculator(string cc, string j)   => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter       ResolveWageProtectionExporter(string cc, string j)   => new DefaultWageProtectionExporter();
    public INationalizationTracker       ResolveNationalizationTracker(string cc, string j)   => new DefaultNationalizationTracker();
    public ILocalizationProfile          ResolveLocalizationProfile(string cc, string j)      => new DefaultLocalizationProfile();
    public ICountryPackDescriptor        ResolveDescriptor(string cc, string j)               => new DefaultCountryPackDescriptor();
}

file sealed class FailLoudNotificationStub : INotificationService
{
    public Task NotifyAsync(Guid t, Guid? u, string title, string msg,
        string entity, string? id, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid t, string tpl, string to, string name,
        Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
}

file sealed class FailLoudLetterStub : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default)
        => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default)
        => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default)
        => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default)
        => Task.FromResult(Array.Empty<byte>());
}

