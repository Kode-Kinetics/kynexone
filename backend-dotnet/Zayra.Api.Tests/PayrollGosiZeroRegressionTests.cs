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
