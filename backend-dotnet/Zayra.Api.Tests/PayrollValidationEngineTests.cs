using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Tests the PayrollValidationEngine (all 9 rules), the block-on-error gate in
/// Approve/Lock, and tenant isolation of validation results.
/// </summary>
[Trait("Category", "PayrollValidation")]
public class PayrollValidationEngineTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static PayrollRun MakeRun(Guid tenantId, Guid? companyId = null) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenantId, Year = 2026, Month = 6,
        Status = "Processed", TotalGrossSalary = 10_000m, TotalDeductions = 1_000m,
        TotalNetSalary = 9_000m, CompanyId = companyId,
    };

    private static PayrollSlip MakeSlip(Guid runId, Guid tenantId, int empId = 1,
        decimal gross = 10_000m, decimal ded = 1_000m, decimal net = 9_000m,
        decimal basic = 8_000m, decimal housing = 2_000m) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenantId, RunId = runId,
        EmployeeId = empId, EmployeeCode = $"EMP{empId:D3}", EmployeeName = $"Test Employee {empId}",
        GrossSalary = gross, Deductions = ded, NetSalary = net,
        BasicSalary = basic, HousingAllowance = housing,
    };

    private static Employee MakeSaudiEmp(Guid tenantId, int id = 1) => new()
    {
        Id = id, TenantId = tenantId, EmployeeCode = $"EMP{id:D3}", FullName = $"Saudi Employee {id}",
        Nationality = "Saudi", Status = "Active",
    };

    private static Employee MakeExpatEmp(Guid tenantId, int id = 1) => new()
    {
        Id = id, TenantId = tenantId, EmployeeCode = $"EMP{id:D3}", FullName = $"Expat Employee {id}",
        Nationality = "Egyptian", Status = "Active",
    };

    private static EmployeeSalaryStructure MakeSalary(Guid tenantId, int empId, decimal basic = 8_000m) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenantId, EmployeeId = empId,
        SalaryStructureId = Guid.NewGuid(), BasicSalary = basic, IsActive = true,
    };

    private static EmployeePayrollProfile MakeProfile(Guid tenantId, int empId,
        string iban = "SA4420000001234567891234", string molId = "MOL123456", string currency = "SAR") => new()
    {
        Id = Guid.NewGuid(), TenantId = tenantId, EmployeeId = empId,
        Iban = iban, MolId = molId, SalaryCurrency = currency,
    };

    private static Company MakeKsaCompany(Guid tenantId) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenantId, LegalNameEn = "Test Co", CountryCode = "SA",
        DefaultCurrency = "SAR", IsActive = true,
    };

    private static PayrollDeduction MakeGosiEeDeduction(Guid runId, Guid tenantId, int empId, decimal amount = 810m) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenantId, PayrollRunId = runId, EmployeeId = empId,
        ComponentCode = "GOSI-ANN-EE", ComponentName = "GOSI Annuities (Employee)",
        Amount = amount, Source = "Statutory",
    };

    // Minimal clean context: one Saudi employee with salary, profile, IBAN, MOL ID, GOSI deduction.
    private PayrollValidationContext MakeCleanKsaContext()
    {
        var tid     = Guid.NewGuid();
        var run     = MakeRun(tid);
        var emp     = MakeSaudiEmp(tid);
        var slip    = MakeSlip(run.Id, tid);
        var salary  = MakeSalary(tid, 1);
        var profile = MakeProfile(tid, 1);
        var gosi    = MakeGosiEeDeduction(run.Id, tid, 1);
        var company = MakeKsaCompany(tid);
        return new PayrollValidationContext(run, [slip], [emp], [salary], [profile], [gosi], [], company);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rule 1: Missing salary structure / payroll profile
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rule1_MissingSalaryStructure_ProducesError()
    {
        var tid = Guid.NewGuid();
        var run = MakeRun(tid);
        var emp = MakeSaudiEmp(tid);
        // No salary assignment
        var ctx = new PayrollValidationContext(run, [], [emp], [], [], [], [], null);

        var results = PayrollValidationEngine.Run(ctx);

        results.Should().Contain(r => r.Code == "MISSING_SALARY_STRUCTURE" && r.Severity == "Error" && r.EmployeeId == emp.Id);
    }

    [Fact]
    public void Rule1_MissingPayrollProfile_ProducesWarning()
    {
        var tid = Guid.NewGuid();
        var run = MakeRun(tid);
        var emp = MakeSaudiEmp(tid);
        var sal = MakeSalary(tid, 1);
        // No payroll profile
        var ctx = new PayrollValidationContext(run, [], [emp], [sal], [], [], [], null);

        var results = PayrollValidationEngine.Run(ctx);

        results.Should().Contain(r => r.Code == "MISSING_PAYROLL_PROFILE" && r.Severity == "Warning" && r.EmployeeId == emp.Id);
    }

    [Fact]
    public void Rule1_AllPresent_NoRule1Findings()
    {
        var ctx = MakeCleanKsaContext();
        var results = PayrollValidationEngine.Run(ctx);
        results.Should().NotContain(r => r.Code == "MISSING_SALARY_STRUCTURE" || r.Code == "MISSING_PAYROLL_PROFILE");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rule 2: GOSI check
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rule2_SaudiEmployee_NoGosiDeductions_ProducesError()
    {
        var tid     = Guid.NewGuid();
        var run     = MakeRun(tid);
        var emp     = MakeSaudiEmp(tid);
        var slip    = MakeSlip(run.Id, tid);
        var profile = MakeProfile(tid, 1);
        var company = MakeKsaCompany(tid);
        // No GOSI deductions
        var ctx = new PayrollValidationContext(run, [slip], [emp], [MakeSalary(tid, 1)], [profile], [], [], company);

        var results = PayrollValidationEngine.Run(ctx);

        results.Should().Contain(r => r.Code == "GOSI_MISSING_FOR_SAUDI" && r.Severity == "Error" && r.EmployeeId == emp.Id);
    }

    [Fact]
    public void Rule2_SaudiEmployee_HasGosiDeductions_PassesClean()
    {
        var ctx = MakeCleanKsaContext();
        var results = PayrollValidationEngine.Run(ctx);
        results.Should().NotContain(r => r.Code == "GOSI_MISSING_FOR_SAUDI");
    }

    [Fact]
    public void Rule2_ExpatEmployee_HasGosiDeductions_ProducesError()
    {
        var tid     = Guid.NewGuid();
        var run     = MakeRun(tid);
        var emp     = MakeExpatEmp(tid);
        var slip    = MakeSlip(run.Id, tid);
        var profile = MakeProfile(tid, 1);
        var company = MakeKsaCompany(tid);
        var gosi    = MakeGosiEeDeduction(run.Id, tid, 1, 500m); // Expat has GOSI — wrong
        var ctx = new PayrollValidationContext(run, [slip], [emp], [MakeSalary(tid, 1)], [profile], [gosi], [], company);

        var results = PayrollValidationEngine.Run(ctx);

        results.Should().Contain(r => r.Code == "GOSI_APPLIED_TO_EXPAT" && r.Severity == "Error" && r.EmployeeId == emp.Id);
    }

    [Fact]
    public void Rule2_ExpatEmployee_NoGosiDeductions_PassesClean()
    {
        var tid     = Guid.NewGuid();
        var run     = MakeRun(tid);
        var emp     = MakeExpatEmp(tid);
        var slip    = MakeSlip(run.Id, tid);
        var profile = MakeProfile(tid, 1);
        var company = MakeKsaCompany(tid);
        // No GOSI deductions for expat — correct
        var ctx = new PayrollValidationContext(run, [slip], [emp], [MakeSalary(tid, 1)], [profile], [], [], company);

        var results = PayrollValidationEngine.Run(ctx);

        results.Should().NotContain(r => r.Code == "GOSI_MISSING_FOR_SAUDI" || r.Code == "GOSI_APPLIED_TO_EXPAT");
    }

    [Fact]
    public void Rule2_SaudiEmployee_CoveredWageExceedsCeiling_ProducesWarning()
    {
        var tid     = Guid.NewGuid();
        var run     = MakeRun(tid);
        var emp     = MakeSaudiEmp(tid);
        // Basic 40k + Housing 10k = 50k > 45k ceiling
        var slip    = MakeSlip(run.Id, tid, basic: 40_000m, housing: 10_000m, gross: 50_000m, ded: 5_000m, net: 45_000m);
        var profile = MakeProfile(tid, 1);
        var company = MakeKsaCompany(tid);
        var gosi    = MakeGosiEeDeduction(run.Id, tid, 1, 4_050m); // 9% of 45k
        var ctx = new PayrollValidationContext(run, [slip], [emp], [MakeSalary(tid, 1)], [profile], [gosi], [], company);

        var results = PayrollValidationEngine.Run(ctx);

        results.Should().Contain(r => r.Code == "GOSI_CEILING_EXCEEDED" && r.Severity == "Warning" && r.EmployeeId == emp.Id);
    }

    [Fact]
    public void Rule2_NonKsaRun_GosiChecksSkipped()
    {
        var tid     = Guid.NewGuid();
        var run     = MakeRun(tid);
        var emp     = MakeSaudiEmp(tid);
        var slip    = MakeSlip(run.Id, tid);
        var profile = MakeProfile(tid, 1);
        var company = new Company { Id = Guid.NewGuid(), TenantId = tid, LegalNameEn = "UAE Co", CountryCode = "AE", DefaultCurrency = "AED", IsActive = true };
        // No GOSI deductions — but it's a UAE company, so no KSA rule applies
        var ctx = new PayrollValidationContext(run, [slip], [emp], [MakeSalary(tid, 1)], [profile], [], [], company);

        var results = PayrollValidationEngine.Run(ctx);

        results.Should().NotContain(r => r.Code == "GOSI_MISSING_FOR_SAUDI" || r.Code == "GOSI_APPLIED_TO_EXPAT");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rule 3: Net not negative / not zero when gross > 0
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rule3_NegativeNet_ProducesError()
    {
        var tid  = Guid.NewGuid();
        var run  = MakeRun(tid);
        var emp  = MakeSaudiEmp(tid);
        var slip = MakeSlip(run.Id, tid, gross: 5_000m, ded: 6_000m, net: -1_000m);
        var ctx  = new PayrollValidationContext(run, [slip], [emp], [], [], [], [], null);

        var results = PayrollValidationEngine.Run(ctx);

        results.Should().Contain(r => r.Code == "NEGATIVE_NET" && r.Severity == "Error" && r.EmployeeId == emp.Id);
    }

    [Fact]
    public void Rule3_ZeroNetWithPositiveGross_ProducesError()
    {
        var tid  = Guid.NewGuid();
        var run  = MakeRun(tid);
        var emp  = MakeSaudiEmp(tid);
        var slip = MakeSlip(run.Id, tid, gross: 5_000m, ded: 5_000m, net: 0m);
        var ctx  = new PayrollValidationContext(run, [slip], [emp], [], [], [], [], null);

        var results = PayrollValidationEngine.Run(ctx);

        results.Should().Contain(r => r.Code == "ZERO_NET_WITH_GROSS" && r.Severity == "Error" && r.EmployeeId == emp.Id);
    }

    [Fact]
    public void Rule3_PositiveNet_NoRule3Findings()
    {
        var ctx = MakeCleanKsaContext();
        var results = PayrollValidationEngine.Run(ctx);
        results.Should().NotContain(r => r.Code == "NEGATIVE_NET" || r.Code == "ZERO_NET_WITH_GROSS");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rule 4: Duplicate employee
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rule4_DuplicateEmployee_ProducesError()
    {
        var tid   = Guid.NewGuid();
        var run   = MakeRun(tid);
        var emp   = MakeSaudiEmp(tid);
        var slip1 = MakeSlip(run.Id, tid);
        var slip2 = MakeSlip(run.Id, tid); // same EmployeeId = 1
        var ctx   = new PayrollValidationContext(run, [slip1, slip2], [emp], [], [], [], [], null);

        var results = PayrollValidationEngine.Run(ctx);

        results.Should().Contain(r => r.Code == "DUPLICATE_EMPLOYEE" && r.Severity == "Error" && r.EmployeeId == emp.Id);
    }

    [Fact]
    public void Rule4_NoDuplicates_NoRule4Findings()
    {
        var ctx = MakeCleanKsaContext();
        var results = PayrollValidationEngine.Run(ctx);
        results.Should().NotContain(r => r.Code == "DUPLICATE_EMPLOYEE");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rule 5: WPS readiness (IBAN + MOL ID)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rule5_MissingIban_ProducesError()
    {
        var tid     = Guid.NewGuid();
        var run     = MakeRun(tid);
        var emp     = MakeSaudiEmp(tid);
        var slip    = MakeSlip(run.Id, tid);
        var profile = MakeProfile(tid, 1, iban: ""); // blank IBAN
        var company = MakeKsaCompany(tid);
        var gosi    = MakeGosiEeDeduction(run.Id, tid, 1);
        var ctx     = new PayrollValidationContext(run, [slip], [emp], [MakeSalary(tid, 1)], [profile], [gosi], [], company);

        var results = PayrollValidationEngine.Run(ctx);

        results.Should().Contain(r => r.Code == "MISSING_IBAN" && r.Severity == "Error" && r.EmployeeId == emp.Id);
    }

    [Fact]
    public void Rule5_InvalidIban_ProducesError()
    {
        var tid     = Guid.NewGuid();
        var run     = MakeRun(tid);
        var emp     = MakeSaudiEmp(tid);
        var slip    = MakeSlip(run.Id, tid);
        var profile = MakeProfile(tid, 1, iban: "SA0000000000000000000000"); // fails mod-97
        var company = MakeKsaCompany(tid);
        var gosi    = MakeGosiEeDeduction(run.Id, tid, 1);
        var ctx     = new PayrollValidationContext(run, [slip], [emp], [MakeSalary(tid, 1)], [profile], [gosi], [], company);

        var results = PayrollValidationEngine.Run(ctx);

        results.Should().Contain(r => r.Code == "INVALID_IBAN" && r.Severity == "Error" && r.EmployeeId == emp.Id);
    }

    [Fact]
    public void Rule5_ValidSaudiIban_NoIbanError()
    {
        var ctx = MakeCleanKsaContext(); // uses SA4420000001234567891234
        var results = PayrollValidationEngine.Run(ctx);
        results.Should().NotContain(r => r.Code == "MISSING_IBAN" || r.Code == "INVALID_IBAN");
    }

    [Fact]
    public void Rule5_NonSaudiIban_OnKsaRun_ProducesWarning()
    {
        var tid     = Guid.NewGuid();
        var run     = MakeRun(tid);
        var emp     = MakeSaudiEmp(tid);
        var slip    = MakeSlip(run.Id, tid);
        // Valid GB IBAN but not Saudi
        var profile = MakeProfile(tid, 1, iban: "GB29NWBK60161331926819");
        var company = MakeKsaCompany(tid);
        var gosi    = MakeGosiEeDeduction(run.Id, tid, 1);
        var ctx     = new PayrollValidationContext(run, [slip], [emp], [MakeSalary(tid, 1)], [profile], [gosi], [], company);

        var results = PayrollValidationEngine.Run(ctx);

        results.Should().Contain(r => r.Code == "NON_SAUDI_IBAN" && r.Severity == "Warning" && r.EmployeeId == emp.Id);
    }

    [Fact]
    public void Rule5_MissingMolId_OnKsaRun_ProducesWarning()
    {
        var tid     = Guid.NewGuid();
        var run     = MakeRun(tid);
        var emp     = MakeSaudiEmp(tid);
        var slip    = MakeSlip(run.Id, tid);
        var profile = MakeProfile(tid, 1, molId: ""); // no MOL ID
        var company = MakeKsaCompany(tid);
        var gosi    = MakeGosiEeDeduction(run.Id, tid, 1);
        var ctx     = new PayrollValidationContext(run, [slip], [emp], [MakeSalary(tid, 1)], [profile], [gosi], [], company);

        var results = PayrollValidationEngine.Run(ctx);

        results.Should().Contain(r => r.Code == "MISSING_MOL_ID" && r.Severity == "Warning" && r.EmployeeId == emp.Id);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rule 6: Nationality present
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rule6_MissingNationality_ProducesWarning()
    {
        var tid  = Guid.NewGuid();
        var run  = MakeRun(tid);
        var emp  = new Employee { Id = 1, TenantId = tid, EmployeeCode = "EMP001", FullName = "No Nat", Nationality = "", Status = "Active" };
        var slip = MakeSlip(run.Id, tid);
        var ctx  = new PayrollValidationContext(run, [slip], [emp], [], [], [], [], null);

        var results = PayrollValidationEngine.Run(ctx);

        results.Should().Contain(r => r.Code == "MISSING_NATIONALITY" && r.Severity == "Warning" && r.EmployeeId == emp.Id);
    }

    [Fact]
    public void Rule6_NationalityPresent_NoRule6Findings()
    {
        var ctx = MakeCleanKsaContext();
        var results = PayrollValidationEngine.Run(ctx);
        results.Should().NotContain(r => r.Code == "MISSING_NATIONALITY");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rule 7: Run totals reconcile
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rule7_GrossMismatch_ProducesError()
    {
        var tid  = Guid.NewGuid();
        var run  = MakeRun(tid); // header: gross=10k, ded=1k, net=9k
        var emp  = MakeSaudiEmp(tid);
        // Slip gross deliberately different from run header
        var slip = MakeSlip(run.Id, tid, gross: 11_000m, ded: 1_000m, net: 10_000m);
        var ctx  = new PayrollValidationContext(run, [slip], [emp], [], [], [], [], null);

        var results = PayrollValidationEngine.Run(ctx);

        results.Should().Contain(r => r.Code == "TOTALS_GROSS_MISMATCH" && r.Severity == "Error");
    }

    [Fact]
    public void Rule7_NetMismatch_ProducesError()
    {
        var tid  = Guid.NewGuid();
        var run  = MakeRun(tid); // header net=9k
        var emp  = MakeSaudiEmp(tid);
        var slip = MakeSlip(run.Id, tid, gross: 10_000m, ded: 1_000m, net: 8_000m); // slip net differs
        var ctx  = new PayrollValidationContext(run, [slip], [emp], [], [], [], [], null);

        var results = PayrollValidationEngine.Run(ctx);

        results.Should().Contain(r => r.Code == "TOTALS_NET_MISMATCH" && r.Severity == "Error");
    }

    [Fact]
    public void Rule7_TotalsMatch_NoRule7Findings()
    {
        var ctx = MakeCleanKsaContext(); // run header matches slip sums
        var results = PayrollValidationEngine.Run(ctx);
        results.Should().NotContain(r => r.Code == "TOTALS_GROSS_MISMATCH" || r.Code == "TOTALS_DEDUCTIONS_MISMATCH" || r.Code == "TOTALS_NET_MISMATCH");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rule 8: GL pre-check
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rule8_GlImbalance_ProducesError()
    {
        var tid = Guid.NewGuid();
        // Deliberately unbalanced: gross (10k) ≠ ded (1k) + net (8.5k)
        var run = new PayrollRun
        {
            Id = Guid.NewGuid(), TenantId = tid, Year = 2026, Month = 6,
            Status = "Processed", TotalGrossSalary = 10_000m, TotalDeductions = 1_000m, TotalNetSalary = 8_500m,
        };
        var slip = MakeSlip(run.Id, tid, gross: 10_000m, ded: 1_000m, net: 8_500m);
        var emp  = MakeSaudiEmp(tid);
        var ctx  = new PayrollValidationContext(run, [slip], [emp], [], [], [], [], null);

        var results = PayrollValidationEngine.Run(ctx);

        results.Should().Contain(r => r.Code == "GL_WILL_NOT_BALANCE" && r.Severity == "Error");
    }

    [Fact]
    public void Rule8_GlBalanced_NoRule8Findings()
    {
        var ctx = MakeCleanKsaContext(); // gross (10k) = ded (1k) + net (9k)
        var results = PayrollValidationEngine.Run(ctx);
        results.Should().NotContain(r => r.Code == "GL_WILL_NOT_BALANCE");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rule 9: Currency matches company default
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rule9_CurrencyMismatch_ProducesWarning()
    {
        var tid     = Guid.NewGuid();
        var run     = MakeRun(tid);
        var emp     = MakeSaudiEmp(tid);
        var slip    = MakeSlip(run.Id, tid);
        var profile = MakeProfile(tid, 1, currency: "AED"); // AED but company default is SAR
        var company = MakeKsaCompany(tid);                  // DefaultCurrency = "SAR"
        var gosi    = MakeGosiEeDeduction(run.Id, tid, 1);
        var ctx     = new PayrollValidationContext(run, [slip], [emp], [MakeSalary(tid, 1)], [profile], [gosi], [], company);

        var results = PayrollValidationEngine.Run(ctx);

        results.Should().Contain(r => r.Code == "CURRENCY_MISMATCH" && r.Severity == "Warning" && r.EmployeeId == emp.Id);
    }

    [Fact]
    public void Rule9_CurrencyMatches_NoRule9Findings()
    {
        var ctx = MakeCleanKsaContext(); // profile SAR = company SAR
        var results = PayrollValidationEngine.Run(ctx);
        results.Should().NotContain(r => r.Code == "CURRENCY_MISMATCH");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Clean-run positive: zero findings on a fully valid KSA run
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CleanRun_KsaSaudiEmployee_NoFindings()
    {
        var ctx     = MakeCleanKsaContext();
        var results = PayrollValidationEngine.Run(ctx);

        var errors = results.Where(r => r.Severity == "Error").ToList();
        errors.Should().BeEmpty($"clean KSA run with Saudi employee must have zero errors; got: {string.Join(", ", errors.Select(e => e.Code))}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Block-on-error: Approve and Lock must reject runs with unresolved errors
    // ─────────────────────────────────────────────────────────────────────────

    private static ZayraDbContext CreateSqliteDb(out SqliteConnection conn)
    {
        conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(
            new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return db;
    }

    private static PayrollController MakeCtrl(ZayraDbContext db, Guid tenantId, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "TestUser"),
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var httpCtx   = new DefaultHttpContext { User = principal };
        var ctrl = new PayrollController(
            db,
            new _ValUnrestrictedScope(),
            new _ValHttpAccessor(httpCtx),
            new _ValNullNotifications(),
            new _ValNullPackResolver(),
            new _ValNullLetterService());
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }

    [Fact]
    public async Task Approve_WithUnresolvedErrorResult_Returns422()
    {
        var db  = CreateSqliteDb(out var conn);
        await using var _ = conn;
        var tid = Guid.NewGuid();
        var run = new PayrollRun { Id = Guid.NewGuid(), TenantId = tid, Year = 2026, Month = 6, Status = "Processed" };
        db.PayrollRuns.Add(run);
        // Seed an unresolved ERROR
        db.PayrollValidationResults.Add(new PayrollValidationResult
        {
            TenantId = tid, PayrollRunId = run.Id, Severity = "Error",
            Code = "MISSING_IBAN", Message = "Employee EMP001 has no IBAN.", IsResolved = false,
        });
        await db.SaveChangesAsync();

        var ctrl   = MakeCtrl(db, tid, "Admin");
        var result = await ctrl.Approve(run.Id, new PayrollDecisionRequest(null), CancellationToken.None);

        result.Should().BeOfType<UnprocessableEntityObjectResult>("ERROR-severity results must block approval");
    }

    [Fact]
    public async Task Lock_WithUnresolvedErrorResult_Returns422()
    {
        var db  = CreateSqliteDb(out var conn);
        await using var _ = conn;
        var tid = Guid.NewGuid();
        var run = new PayrollRun { Id = Guid.NewGuid(), TenantId = tid, Year = 2026, Month = 6, Status = "Approved" };
        db.PayrollRuns.Add(run);
        db.PayrollValidationResults.Add(new PayrollValidationResult
        {
            TenantId = tid, PayrollRunId = run.Id, Severity = "Error",
            Code = "NEGATIVE_NET", Message = "Employee EMP002 net salary is negative.", IsResolved = false,
        });
        await db.SaveChangesAsync();

        var ctrl   = MakeCtrl(db, tid, "Admin");
        var result = await ctrl.Lock(run.Id, CancellationToken.None);

        result.Should().BeOfType<UnprocessableEntityObjectResult>("ERROR-severity results must block lock");
    }

    [Fact]
    public async Task Approve_WithWarningsOnly_Proceeds()
    {
        var db  = CreateSqliteDb(out var conn);
        await using var _ = conn;
        var tid = Guid.NewGuid();
        var run = new PayrollRun { Id = Guid.NewGuid(), TenantId = tid, Year = 2026, Month = 6, Status = "Processed" };
        db.PayrollRuns.Add(run);
        // Only a WARNING — must not block
        db.PayrollValidationResults.Add(new PayrollValidationResult
        {
            TenantId = tid, PayrollRunId = run.Id, Severity = "Warning",
            Code = "MISSING_MOL_ID", Message = "Employee has no MOL ID.", IsResolved = false,
        });
        await db.SaveChangesAsync();

        var ctrl   = MakeCtrl(db, tid, "Admin");
        var result = await ctrl.Approve(run.Id, new PayrollDecisionRequest(null), CancellationToken.None);

        result.Should().NotBeOfType<UnprocessableEntityObjectResult>("Warning-only runs must be approvable");
    }

    [Fact]
    public async Task Approve_AfterErrorResolved_Proceeds()
    {
        var db  = CreateSqliteDb(out var conn);
        await using var _ = conn;
        var tid = Guid.NewGuid();
        var run = new PayrollRun { Id = Guid.NewGuid(), TenantId = tid, Year = 2026, Month = 6, Status = "Processed" };
        db.PayrollRuns.Add(run);
        var err = new PayrollValidationResult
        {
            TenantId = tid, PayrollRunId = run.Id, Severity = "Error",
            Code = "MISSING_IBAN", Message = "Employee has no IBAN.", IsResolved = false,
        };
        db.PayrollValidationResults.Add(err);
        await db.SaveChangesAsync();

        // Mark resolved
        err.IsResolved = true;
        await db.SaveChangesAsync();

        var ctrl   = MakeCtrl(db, tid, "Admin");
        var result = await ctrl.Approve(run.Id, new PayrollDecisionRequest(null), CancellationToken.None);

        result.Should().NotBeOfType<UnprocessableEntityObjectResult>("resolved errors must not block approval");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Bypass-attempt: try to approve via direct API call when errors exist
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApproveBypass_WithErrors_IsRejectedServerSide()
    {
        // Proves that even a direct endpoint call (no frontend) is blocked.
        var db  = CreateSqliteDb(out var conn);
        await using var _ = conn;
        var tid = Guid.NewGuid();
        var run = new PayrollRun { Id = Guid.NewGuid(), TenantId = tid, Year = 2026, Month = 6, Status = "Processed" };
        db.PayrollRuns.Add(run);
        db.PayrollValidationResults.Add(new PayrollValidationResult
        {
            TenantId = tid, PayrollRunId = run.Id, Severity = "Error",
            Code = "GOSI_MISSING_FOR_SAUDI", Message = "Saudi employee has no GOSI.", IsResolved = false,
        });
        await db.SaveChangesAsync();

        var ctrl   = MakeCtrl(db, tid, "Admin", "Finance Controller");
        var result = await ctrl.Approve(run.Id, new PayrollDecisionRequest("bypass attempt"), CancellationToken.None);

        var unproc = result.Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        var body   = unproc.Value!.ToString();
        body.Should().Contain("validation_errors", "response body must explain why it was blocked");

        // Confirm run status did NOT advance
        var reloaded = await db.PayrollRuns.FindAsync(run.Id);
        reloaded!.Status.Should().Be("Processed", "status must not change when approval is blocked");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Tenant isolation: validation results from tenant A must not appear for B
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TenantIsolation_ValidationResultsAreScopedToTenant()
    {
        var tidA = Guid.NewGuid();
        var tidB = Guid.NewGuid();

        // Build contexts for two different tenants
        var runA  = MakeRun(tidA);
        var empA  = MakeSaudiEmp(tidA);
        var slipA = MakeSlip(runA.Id, tidA);
        // Tenant A has a missing IBAN error
        var ctxA  = new PayrollValidationContext(runA, [slipA], [empA], [], [], [], [], null);

        var runB  = MakeRun(tidB);
        var empB  = MakeExpatEmp(tidB, 2);
        var slipB = MakeSlip(runB.Id, tidB, empId: 2);
        var profB = MakeProfile(tidB, 2);
        var salB  = MakeSalary(tidB, 2);
        // Tenant B is clean (no GOSI rules since no company/KSA context)
        var ctxB  = new PayrollValidationContext(runB, [slipB], [empB], [salB], [profB], [], [], null);

        var resultsA = PayrollValidationEngine.Run(ctxA);
        var resultsB = PayrollValidationEngine.Run(ctxB);

        // All results from tenant A carry tidA
        resultsA.Should().NotBeEmpty("tenant A has a missing salary / IBAN error");
        resultsA.Should().OnlyContain(r => r.TenantId == tidA, "engine must stamp tenant A's tenant ID");

        // Tenant B is clean — its result set must be empty (not contaminated by A's run)
        resultsB.Should().NotContain(r => r.TenantId == tidA, "tenant A's errors must not appear in tenant B's results");

        // None of tenant A's ERROR codes must appear in B's results
        var aErrorCodes = resultsA.Where(r => r.Severity == "Error").Select(r => r.Code).ToHashSet();
        resultsB.Should().NotContain(r => aErrorCodes.Contains(r.Code),
            "validation findings from tenant A must not bleed into tenant B's result set");
    }
}

// ── File-scoped stubs ─────────────────────────────────────────────────────────

file sealed class _ValUnrestrictedScope : IDataScopeService
{
    public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
}

file sealed class _ValHttpAccessor : IHttpContextAccessor
{
    public _ValHttpAccessor(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _ValNullNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _ValNullPackResolver : Zayra.Api.Application.CountryPack.ICountryPackResolver
{
    public Zayra.Api.Application.CountryPack.IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => new Zayra.Api.Infrastructure.CountryPack.DefaultStatutoryDeductionCalculator();
    public Zayra.Api.Application.CountryPack.IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j)
        => new Zayra.Api.Infrastructure.CountryPack.DefaultEndOfServiceCalculator();
    public Zayra.Api.Application.CountryPack.IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j)
        => new Zayra.Api.Infrastructure.CountryPack.DefaultWageProtectionExporter();
    public Zayra.Api.Application.CountryPack.INationalizationTracker ResolveNationalizationTracker(string cc, string j)
        => new Zayra.Api.Infrastructure.CountryPack.DefaultNationalizationTracker();
    public Zayra.Api.Application.CountryPack.ILocalizationProfile ResolveLocalizationProfile(string cc, string j)
        => new Zayra.Api.Infrastructure.CountryPack.DefaultLocalizationProfile();
    public Zayra.Api.Application.CountryPack.ICountryPackDescriptor ResolveDescriptor(string cc, string j)
        => new Zayra.Api.Infrastructure.CountryPack.DefaultCountryPackDescriptor();
}

file sealed class _ValNullLetterService : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}
