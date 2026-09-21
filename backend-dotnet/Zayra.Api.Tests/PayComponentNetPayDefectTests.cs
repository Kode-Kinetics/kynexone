using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// F2 — THE ORIGINAL DEFECT, pinned.
///
/// <para>Before F2, <c>PayrollController.Process</c> computed gross / deductions / net from hard-coded
/// scalars (basic + housing + … − fixed − tax − statutory − …) and used <see cref="PayComponentEngine"/>
/// ONLY to emit payslip lines. A tenant-added component therefore produced a payslip LINE that no
/// aggregate knew about: a deduction was printed on the payslip but never taken off net, and at Lock
/// the journal — DR Σ earning lines, CR Σ deduction lines + CR net — was out by exactly that amount, so
/// the run failed with <c>gl_unbalanced</c>.</para>
///
/// <para>The component is inserted straight into <c>pay_components</c> (there was no write API before
/// F2) so this file runs unchanged against the pre-F2 code and the F2 code. Against the pre-F2 code
/// every Fact here fails; the failures are quoted in the F2 report.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class PayComponentNetPayDefectTests
{
    private readonly PostgresFixture _fx;
    public PayComponentNetPayDefectTests(PostgresFixture fx) => _fx = fx;

    // Saudi 10,000 basic + 3,000 housing ⇒ covered 13,000 ⇒ GOSI EE 9.75% = 1,267.50; ER 11.75% = 1,527.50.
    private const decimal Basic = 10_000m, Housing = 3_000m, GosiEe = 1_267.50m, GosiEr = 1_527.50m;

    [Fact]
    public async Task TenantAddedDeduction_IsTakenOffNetPay()
    {
        var (tenantId, runId, empId) = await SeedAsync(("UNION_DUES", PayComponentTypes.Deduction, 100m));
        await ProcessAsync(tenantId, runId);

        await using var db = _fx.CreateDb();
        var line = await db.PayrollDeductions.AsNoTracking()
            .SingleAsync(d => d.PayrollRunId == runId && d.EmployeeId == empId && d.ComponentCode == "UNION_DUES");
        Assert.Equal(100m, line.Amount); // the payslip PRINTS the deduction (true before and after F2)

        var slip = await db.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == runId && s.EmployeeId == empId);
        Assert.Equal(Basic + Housing, slip.GrossSalary);
        Assert.Equal(GosiEe + 100m, slip.Deductions);                 // pre-F2: 1,267.50 (dues ignored)
        Assert.Equal(Basic + Housing - GosiEe - 100m, slip.NetSalary); // pre-F2: 11,732.50 (dues ignored)
    }

    [Fact]
    public async Task TenantAddedDeduction_LocksWithABalancedJournal()
    {
        var (tenantId, runId, _) = await SeedAsync(("UNION_DUES", PayComponentTypes.Deduction, 100m));
        await ProcessAsync(tenantId, runId);
        await AssertLocksBalancedAsync(tenantId, runId); // pre-F2: 422 gl_unbalanced (CR exceeds DR by 100.00)
    }

    [Fact]
    public async Task TenantAddedEarning_IsPaidInGrossAndNet_AndLocksBalanced()
    {
        var (tenantId, runId, empId) = await SeedAsync(("SHIFT_ALLOWANCE", PayComponentTypes.Earning, 250m));
        await ProcessAsync(tenantId, runId);

        await using (var db = _fx.CreateDb())
        {
            var slip = await db.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == runId && s.EmployeeId == empId);
            Assert.Equal(Basic + Housing + 250m, slip.GrossSalary);        // pre-F2: 13,000.00
            Assert.Equal(Basic + Housing + 250m - GosiEe, slip.NetSalary); // pre-F2: 11,732.50
            // A tenant earning is NOT in the pack-owned covered wage: GOSI is unchanged.
            Assert.Equal(GosiEe, slip.EmployeeStatutoryTotal);
            Assert.Equal(GosiEr, slip.EmployerStatutoryTotal);
        }
        await AssertLocksBalancedAsync(tenantId, runId); // pre-F2: 422 gl_unbalanced (DR exceeds CR by 250.00)
    }

    [Fact]
    public async Task TenantAddedEarning_LocksWithABalancedJournal()
    {
        var (tenantId, runId, _) = await SeedAsync(("SHIFT_ALLOWANCE", PayComponentTypes.Earning, 250m));
        await ProcessAsync(tenantId, runId);
        await AssertLocksBalancedAsync(tenantId, runId); // pre-F2: 422 gl_unbalanced (DR exceeds CR by 250.00)
    }

    // ── harness ──────────────────────────────────────────────────────────────────────────────

    private async Task AssertLocksBalancedAsync(Guid tenantId, Guid runId)
    {
        await using (var db = _fx.CreateDb())
        {
            // Nothing in this scenario should raise a blocking error; assert it rather than wipe it.
            var errors = await db.PayrollValidationResults.AsNoTracking()
                .Where(v => v.PayrollRunId == runId && v.Severity == "Error").Select(v => v.Code).ToListAsync();
            Assert.Empty(errors);
            await db.PayrollValidationResults.Where(v => v.PayrollRunId == runId).ExecuteDeleteAsync();
            await db.PayrollRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "Approved"));
        }
        await using (var db = _fx.CreateDb())
        {
            var result = await Build(db, tenantId, "payroll.lock").Lock(runId, CancellationToken.None);
            if (result is ObjectResult { StatusCode: >= 400 } bad)
                Assert.Fail($"Lock refused: HTTP {bad.StatusCode} {System.Text.Json.JsonSerializer.Serialize(bad.Value)}");
            Assert.IsType<OkObjectResult>(result);
        }
        await using (var db = _fx.CreateDb())
        {
            var gl = await db.FinanceGlEntries.AsNoTracking()
                .Where(g => g.TenantId == tenantId && g.SourceEntityId == runId && g.EventType == GlEventTypes.Accrual)
                .ToListAsync();
            var dr = gl.Where(g => !string.IsNullOrEmpty(g.DebitAccount)).Sum(g => g.Amount);
            var cr = gl.Where(g => !string.IsNullOrEmpty(g.CreditAccount)).Sum(g => g.Amount);
            Assert.True(dr > 0m);
            Assert.Equal(dr, cr);
        }
    }

    private async Task<(Guid TenantId, Guid RunId, int EmpId)> SeedAsync(
        params (string Code, string Type, decimal Value)[] tenantComponents)
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        await PayComponentSeeder.SeedTenantDefaultsAsync(db, tenantId, CancellationToken.None);
        foreach (var (code, type, value) in tenantComponents)
            db.PayComponents.Add(new PayComponent
            {
                TenantId = tenantId, Code = code, NameEn = code, NameAr = code, ComponentType = type,
                CalcMethod = PayComponentCalcMethods.Fixed, Value = value,
                GlDriverKey = type == PayComponentTypes.Earning ? "EARN:OTHER" : "DED:OTHER",
                DisplayOrder = 500, IsActive = true,
            });

        var company = new Company
        {
            Id = Guid.NewGuid(), TenantId = tenantId, LegalNameEn = $"F2 Co {Guid.NewGuid():N}",
            CountryCode = "SAU", Jurisdiction = "KSA-mainland", RegistrationNumber = $"F2-{Guid.NewGuid():N}",
            DefaultCurrency = "SAR", IsActive = true, CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Companies.Add(company);
        var emp = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "F2-1", FullName = "F2 Saudi",
            Nationality = "Saudi", ContractType = "Indefinite", Status = "Active",
            JoiningDate = new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = emp.Id, SalaryStructureId = Guid.NewGuid(),
            BasicSalary = Basic, HousingAllowance = Housing, EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
        });
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
        {
            TenantId = tenantId, EmployeeId = emp.Id, Iban = "SA4420000001234567891234",
            MolId = $"MOL-{Guid.NewGuid():N}", SalaryCurrency = "SAR",
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

    private async Task ProcessAsync(Guid tenantId, Guid runId)
    {
        await using var db = _fx.CreateDb();
        var result = await Build(db, tenantId).Process(runId, CancellationToken.None);
        if (result is ObjectResult { StatusCode: >= 400 } bad)
            Assert.Fail($"Process refused: HTTP {bad.StatusCode} {System.Text.Json.JsonSerializer.Serialize(bad.Value)}");
        Assert.IsType<OkObjectResult>(result);
    }

    internal static StubRuleReader KsaRules() => new StubRuleReader()
        .Set("gosi.saudi_employee_rate", 0.09m)
        .Set("gosi.saudi_employer_rate", 0.09m)
        .Set("gosi.saned_rate", 0.0075m)
        .Set("gosi.expat_occupational_hazard_rate", 0.02m)
        .Set("gosi.covered_wage_ceiling_sar", 45_000m)
        .Set("ot.standard_multiplier", 1.5m)
        .Set("ot.standard_monthly_hours", 240m)
        .Set("lop.monthly_day_divisor", 30m)
        .Set("lop.standard_work_minutes_per_day", 480m);

    internal static PayrollController Build(ZayraDbContext db, Guid tenantId, params string[] permissions)
    {
        var rules = KsaRules();
        var ctrl = new PayrollController(
            db, new DataScopeService(db), new HttpContextAccessor(),
            new F2NullNotifications(), new KsaTestPackResolver(rules), rules,
            new F2NullLetters(), new NullDocumentStorage(), new Zayra.Api.Infrastructure.Documents.PdfRenderGate(8));
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, "Admin"),
        };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
        };
        return ctrl;
    }
}

internal sealed class F2NullNotifications : INotificationService
{
    public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string en, string? eid, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
}

internal sealed class F2NullLetters : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}
