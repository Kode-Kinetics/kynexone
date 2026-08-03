using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

// ══════════════════════════════════════════════════════════════════════════════
// POD-A1 — GOSI/Statutory reconciliation unification: TIE-OUT proof.
//
// Proves the reconciliation reflects (a) what the run actually deducted, (b) what the run's OWN pack
// recomputes as expected on the SAME covered-wage base, and (c) what the GL actually posted to Social
// Insurance Payable (2101 EE / 2106 ER) — all reconciled to the halala with ZERO manual correction.
//
// The old bug: contribution-summary/variance-report filtered Source=="GOSI" (no row ever written with
// that source) and computed "expected" via GosiCalculationService on basic-only against a different rate
// store → totalGosi=0 and 100% Saudi variance. These tests lock in the fix and guard the regression.
// ══════════════════════════════════════════════════════════════════════════════

[Trait("Category", "Integration")]
[Collection("Integration")]
public class GosiReconciliationTieOutTests
{
    private readonly PostgresFixture _fx;
    public GosiReconciliationTieOutTests(PostgresFixture fx) => _fx = fx;

    // Shared KSA rates for BOTH the run and the reconciliation (so expected == actual by construction).
    private static StubRuleReader KsaRules() => new StubRuleReader()
        .Set("gosi.saudi_employee_rate",            0.09m)
        .Set("gosi.saudi_employer_rate",            0.09m)
        .Set("gosi.saned_rate",                     0.0075m)
        .Set("gosi.expat_occupational_hazard_rate", 0.02m)
        .Set("gosi.covered_wage_ceiling_sar",       45_000m)
        .Set("ot.standard_multiplier",              1.5m)
        .Set("ot.standard_monthly_hours",           240m)
        .Set("lop.monthly_day_divisor",             30m)
        .Set("lop.standard_work_minutes_per_day",   480m);

    [Fact]
    public async Task E2E_TieOut_Saudi_Expat_And_GosiBonus_ExpectedEqualsActualEqualsGl()
    {
        var ruleReader = KsaRules();
        var tenantId = await SeedTenantAsync();
        var (companyId, saudiId, expatId, runId) = await SeedRunAsync(tenantId,
            saudiBasic: 10_000m, saudiHousing: 3_000m, gosiBonusGross: 2_000m,
            expatBasic: 8_000m, expatHousing: 2_000m, year: 2026, month: 6);

        // ── Process (writes Source="Statutory" pack rows; stamps the bonus with PayrollRunId) ──
        await using (var db = _fx.CreateDb())
        {
            var ctrl = BuildPayroll(db, tenantId, ruleReader);
            Assert.IsType<OkObjectResult>(await ctrl.Process(runId, CancellationToken.None));
        }

        // Covered wage: Saudi = 10,000 + 3,000 + 2,000 bonus = 15,000 → EE 9.75% = 1,462.50; ER 11.75% = 1,762.50
        //               Expat = OH 2% of (8,000 + 2,000) = 200 (employer only, no EE)
        const decimal saudiEe = 15_000m * 0.0975m;   // 1,462.50
        const decimal saudiEr = 15_000m * 0.1175m;   // 1,762.50
        const decimal expatEr = 10_000m * 0.02m;     //   200.00
        const decimal totalEe = saudiEe;
        const decimal totalEr = saudiEr + expatEr;   // 1,962.50

        await using (var db = _fx.CreateDb())
        {
            var stat = await db.PayrollDeductions.AsNoTracking()
                .Where(d => d.TenantId == tenantId && d.PayrollRunId == runId && d.Source == "Statutory")
                .ToListAsync();
            // Persisted with hyphen pack codes and the authoritative employer flag.
            Assert.Contains(stat, d => d.ComponentCode == "GOSI-ANN-EE"   && !d.IsEmployerContribution && d.EmployeeId == saudiId);
            Assert.Contains(stat, d => d.ComponentCode == "GOSI-SANED-EE" && !d.IsEmployerContribution && d.EmployeeId == saudiId);
            Assert.Contains(stat, d => d.ComponentCode == "GOSI-ANN-ER"   &&  d.IsEmployerContribution && d.EmployeeId == saudiId);
            Assert.Contains(stat, d => d.ComponentCode == "GOSI-OH-ER"    &&  d.IsEmployerContribution && d.EmployeeId == expatId);
            Assert.Equal(totalEe, stat.Where(d => !d.IsEmployerContribution).Sum(d => d.Amount), 2);
            Assert.Equal(totalEr, stat.Where(d =>  d.IsEmployerContribution).Sum(d => d.Amount), 2);

            // Bonus was consumed & stamped with the run id (so reconciliation can re-derive the GOSI base).
            var bonus = await db.EmployeeBonuses.AsNoTracking()
                .FirstAsync(b => b.TenantId == tenantId && b.EmployeeIntId == saudiId);
            Assert.Equal(runId, bonus.PayrollRunId);
            Assert.Equal("PaidInPayroll", bonus.Status);
        }

        // ── Approve + Lock (posts the GL) ──
        await LockRunAsync(tenantId, runId, ruleReader);

        // ── Reconcile via the single-truth service (what BOTH endpoints project) ──
        await using (var db = _fx.CreateDb())
        {
            var run = await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
            var recon = await new GosiReconciliationService(db, KsaResolver(ruleReader))
                .ReconcileAsync(tenantId, run, CancellationToken.None);

            Assert.True(recon.HasStatutoryData);
            Assert.True(recon.PackResolved);
            Assert.True(recon.GlPosted);

            // actual == expected (no drift): the halala tie-out on the drift axis
            Assert.Equal(totalEe, recon.ActualEmployeeTotal, 2);
            Assert.Equal(totalEr, recon.ActualEmployerTotal, 2);
            Assert.Equal(totalEe, recon.ExpectedEmployeeTotal, 2);
            Assert.Equal(totalEr, recon.ExpectedEmployerTotal, 2);
            Assert.Equal(0m, recon.ExpectedVsActualEmployeeDelta);
            Assert.Equal(0m, recon.ExpectedVsActualEmployerDelta);

            // 4-way witness: slip totals + run.TotalEmployerStatutoryCost agree with lines
            Assert.Equal(totalEe, recon.SlipEmployeeStatutoryTotal, 2);
            Assert.Equal(totalEr, recon.SlipEmployerStatutoryTotal, 2);
            Assert.Equal(totalEr, recon.RunEmployerStatutoryCost, 2);

            // actual == GL: the financial control (2101 EE / 2106 ER)
            Assert.Equal(totalEe, recon.GlEmployeeLiability);
            Assert.Equal(totalEr, recon.GlEmployerLiability);
            Assert.Equal(0m, recon.GlEmployeeDelta);
            Assert.Equal(0m, recon.GlEmployerDelta);
            Assert.StartsWith("2101", recon.GlEmployeeAccount);
            Assert.StartsWith("2106", recon.GlEmployerAccount);

            // Per-employee variance is exactly zero on a clean run, both sides.
            Assert.Equal(0, recon.VarianceCount);
            var saudiRow = recon.Rows.First(r => r.EmployeeId == saudiId);
            Assert.Equal("Saudi", saudiRow.Classification);
            Assert.Equal(15_000m, saudiRow.CoveredWageBase, 2);    // proves covered-wage base (basic+housing+bonus)
            Assert.Equal(saudiEe, saudiRow.ExpectedEmployee, 2);
            Assert.Equal(saudiEr, saudiRow.ExpectedEmployer, 2);
            Assert.Equal(0m, saudiRow.EmployeeVariance);
            Assert.Equal(0m, saudiRow.EmployerVariance);
            var expatRow = recon.Rows.First(r => r.EmployeeId == expatId);
            Assert.Equal(0m, expatRow.ExpectedEmployee, 2);        // expat: no EE annuity
            Assert.Equal(expatEr, expatRow.ExpectedEmployer, 2);
        }

        // ── Old-convention regression guard: a stray Source="GOSI" underscore row must be IGNORED ──
        await using (var db = _fx.CreateDb())
        {
            db.PayrollDeductions.Add(new PayrollDeduction
            {
                TenantId = tenantId, CompanyId = companyId, PayrollRunId = runId, EmployeeId = saudiId,
                ComponentCode = "GOSI_ANNUITIES_EMP", ComponentName = "Legacy underscore row",
                Amount = 99_999m, Source = "GOSI", IsEmployerContribution = false,
            });
            await db.SaveChangesAsync();
        }

        // ── Endpoints surface the reconciled numbers (and never the 99,999 legacy row) ──
        await using (var db = _fx.CreateDb())
        {
            var gosi = BuildGosi(db, tenantId, ruleReader);

            var summary = ((OkObjectResult)await gosi.GetRunContributionSummary(runId, CancellationToken.None)).Value!;
            Assert.Equal(totalEe, Dec(summary, "totalEmployeeContrib"), 2);
            Assert.Equal(totalEr, Dec(summary, "totalEmployerContrib"), 2);
            Assert.Equal(totalEe + totalEr, Dec(summary, "totalGosi"), 2);   // NOT +99,999 (legacy row ignored)
            Assert.Equal(totalEe, Dec(summary, "expectedEmployeeContrib"), 2);
            Assert.Equal(totalEr, Dec(summary, "expectedEmployerContrib"), 2);
            Assert.Equal(totalEe, Dec(summary, "glEmployeeLiability"), 2);
            Assert.Equal(totalEr, Dec(summary, "glEmployerLiability"), 2);
            Assert.Equal(0m, Dec(summary, "glEmployeeDelta"));
            Assert.Equal(0m, Dec(summary, "glEmployerDelta"));
            Assert.True((bool)Prop(summary, "hasStatutoryData")!);

            var variance = ((OkObjectResult)await gosi.GetVarianceReport(runId, CancellationToken.None)).Value!;
            Assert.Equal(0, Convert.ToInt32(Prop(variance, "withVariance")));
            Assert.Equal(2, Convert.ToInt32(Prop(variance, "totalEmployees")));
            Assert.Equal(totalEe, Dec(variance, "actualEmployeeContrib"), 2);
            Assert.Equal(totalEr, Dec(variance, "expectedEmployerContrib"), 2);
        }
    }

    [Fact]
    public async Task CeilingApplied_CoveredWageCappedAt45000_TiesOut()
    {
        var ruleReader = KsaRules();
        var tenantId = await SeedTenantAsync();
        // basic 40,000 + housing 10,000 = 50,000 covered → capped at 45,000
        var (_, saudiId, _, runId) = await SeedRunAsync(tenantId,
            saudiBasic: 40_000m, saudiHousing: 10_000m, gosiBonusGross: 0m,
            expatBasic: 0m, expatHousing: 0m, year: 2026, month: 7, includeExpat: false);

        await using (var db = _fx.CreateDb())
            Assert.IsType<OkObjectResult>(await BuildPayroll(db, tenantId, ruleReader).Process(runId, CancellationToken.None));

        const decimal cappedEe = 45_000m * 0.0975m; // 4,387.50

        await using (var db = _fx.CreateDb())
        {
            var run = await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
            var recon = await new GosiReconciliationService(db, KsaResolver(ruleReader))
                .ReconcileAsync(tenantId, run, CancellationToken.None);

            var row = recon.Rows.First(r => r.EmployeeId == saudiId);
            Assert.Equal(50_000m, row.CoveredWageBase, 2);         // raw base (basic+housing), pre-ceiling
            // The 45,000 ceiling is applied inside the pack to the AMOUNTS — identical to the run.
            Assert.Equal(cappedEe, row.ExpectedEmployee, 2);
            Assert.Equal(cappedEe, recon.ActualEmployeeTotal, 2);
            Assert.Equal(0, recon.VarianceCount);
        }
    }

    [Fact]
    public async Task PerCompanyAccountOverride_TiesOutByComponentCode_AndReportsOverriddenAccount()
    {
        var ruleReader = KsaRules();
        var tenantId = await SeedTenantAsync();
        var (companyId, _, _, runId) = await SeedRunAsync(tenantId,
            saudiBasic: 10_000m, saudiHousing: 3_000m, gosiBonusGross: 0m,
            expatBasic: 0m, expatHousing: 0m, year: 2026, month: 8, includeExpat: false);

        // Remap the EE statutory driver to a NON-2101 company-specific account.
        await using (var db = _fx.CreateDb())
        {
            var acct = new GlAccount
            {
                TenantId = tenantId, CompanyId = companyId, Code = "2201",
                Name = "Custom EE Social Insurance", AccountType = "Liability", IsActive = true,
            };
            db.GlAccounts.Add(acct);
            db.GlAccountMappings.Add(new GlAccountMapping
            {
                TenantId = tenantId, CompanyId = companyId,
                DriverKey = "DED:STATUTORY_EE", AccountId = acct.Id, IsActive = true,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = _fx.CreateDb())
            Assert.IsType<OkObjectResult>(await BuildPayroll(db, tenantId, ruleReader).Process(runId, CancellationToken.None));
        await LockRunAsync(tenantId, runId, ruleReader);

        const decimal ee = 13_000m * 0.0975m; // 1,267.50

        await using (var db = _fx.CreateDb())
        {
            var run = await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
            var recon = await new GosiReconciliationService(db, KsaResolver(ruleReader))
                .ReconcileAsync(tenantId, run, CancellationToken.None);

            // Account-agnostic tie-out: matched by component code, so the delta is still zero.
            Assert.Equal(ee, recon.GlEmployeeLiability!.Value, 2);
            Assert.Equal(0m, recon.GlEmployeeDelta);
            // …and the report surfaces the OVERRIDDEN account that actually received the credit.
            Assert.StartsWith("2201", recon.GlEmployeeAccount);
        }
    }

    [Fact]
    public async Task NotYetLockedRun_ReportsGlPostedFalse_WithNoFalseDelta()
    {
        var ruleReader = KsaRules();
        var tenantId = await SeedTenantAsync();
        var (_, _, _, runId) = await SeedRunAsync(tenantId,
            saudiBasic: 10_000m, saudiHousing: 3_000m, gosiBonusGross: 0m,
            expatBasic: 0m, expatHousing: 0m, year: 2026, month: 9, includeExpat: false);

        await using (var db = _fx.CreateDb())
            Assert.IsType<OkObjectResult>(await BuildPayroll(db, tenantId, ruleReader).Process(runId, CancellationToken.None));

        await using (var db = _fx.CreateDb())
        {
            var run = await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
            var recon = await new GosiReconciliationService(db, KsaResolver(ruleReader))
                .ReconcileAsync(tenantId, run, CancellationToken.None);

            Assert.False(recon.GlPosted);
            Assert.Null(recon.GlEmployeeLiability);
            Assert.Null(recon.GlEmployerLiability);
            Assert.Null(recon.GlEmployeeDelta);
            Assert.Null(recon.GlEmployerDelta);
            // The drift axis (expected vs actual) still reconciles pre-lock.
            Assert.Equal(recon.ActualEmployeeTotal, recon.ExpectedEmployeeTotal, 2);
            Assert.Equal(0, recon.VarianceCount);
        }
    }

    [Fact]
    public async Task LopRun_CoveredWageUnreducedByLop_TiesOut()
    {
        // REQUIREMENT #5 (LOP effect on covered wage): an unpaid-absence (LOP) run cuts NET pay via a
        // separate LOP_DEDUCTION line but — per the KSA rule stamped at PayrollController.cs:822-825 —
        // does NOT reduce the GOSI covered wage. Because the reconciliation rebuilds the covered wage
        // from the slip's FULL basic+housing (PayrollController.cs:916-917), expected must still equal
        // actual equal GL. This test fails if a future change prorated the covered wage for LOP in the
        // run (or in the reconstruction) but not the other — the exact drift the single truth must catch.
        var ruleReader = KsaRules();
        var tenantId = await SeedTenantAsync();
        var (_, saudiId, _, runId) = await SeedRunAsync(tenantId,
            saudiBasic: 10_000m, saudiHousing: 3_000m, gosiBonusGross: 0m,
            expatBasic: 0m, expatHousing: 0m, year: 2026, month: 10, includeExpat: false);

        // Two unpaid-absence days → LOP = 2 × round(10,000 / 30) internally; covered wage stays 13,000.
        await SeedAbsenceDaysAsync(tenantId, saudiId,
            new DateOnly(2026, 10, 6), new DateOnly(2026, 10, 7));

        await using (var db = _fx.CreateDb())
            Assert.IsType<OkObjectResult>(await BuildPayroll(db, tenantId, ruleReader).Process(runId, CancellationToken.None));

        const decimal ee = 13_000m * 0.0975m;                 // 1,267.50 — FULL covered wage, not LOP-reduced
        const decimal expectedLop = 666.67m;                  // 2 × (10,000 / 30), rounded

        await using (var db = _fx.CreateDb())
        {
            // The run really applied an LOP deduction (so this isn't a no-op run), and the slip persisted
            // the full basic+housing the reconciliation rebuilds from.
            var lop = await db.PayrollDeductions.AsNoTracking()
                .FirstAsync(d => d.TenantId == tenantId && d.PayrollRunId == runId
                              && d.EmployeeId == saudiId && d.ComponentCode == "LOP_DEDUCTION");
            Assert.Equal(expectedLop, lop.Amount, 2);
            var slip = await db.PayrollSlips.AsNoTracking()
                .FirstAsync(s => s.TenantId == tenantId && s.RunId == runId && s.EmployeeId == saudiId);
            Assert.Equal(10_000m, slip.BasicSalary, 2);
            Assert.Equal(3_000m, slip.HousingAllowance, 2);
        }

        await LockRunAsync(tenantId, runId, ruleReader);

        await using (var db = _fx.CreateDb())
        {
            var run = await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
            var recon = await new GosiReconciliationService(db, KsaResolver(ruleReader))
                .ReconcileAsync(tenantId, run, CancellationToken.None);

            var row = recon.Rows.First(r => r.EmployeeId == saudiId);
            Assert.Equal(13_000m, row.CoveredWageBase, 2);     // LOP did NOT reduce the covered wage
            Assert.Equal(ee, row.ExpectedEmployee, 2);
            Assert.Equal(ee, recon.ExpectedEmployeeTotal, 2);
            Assert.Equal(ee, recon.ActualEmployeeTotal, 2);
            Assert.Equal(ee, recon.GlEmployeeLiability!.Value, 2);
            Assert.Equal(0m, recon.ExpectedVsActualEmployeeDelta);
            Assert.Equal(0m, recon.GlEmployeeDelta);
            Assert.Equal(0, recon.VarianceCount);
        }
    }

    [Fact]
    public async Task MidPeriodJoiner_IncludedAtFullCoveredWage_TiesOut()
    {
        // REQUIREMENT #5 (mid-period joiners): an employee who joined mid-month is INCLUDED in the run
        // (PayrollController.cs:604 selects on Status=="Active" with no join-date filter) and — in this
        // implementation — contributes on the FULL covered wage (no join-date proration at
        // PayrollController.cs:806-808). The reconciliation must reflect exactly that: a slip exists for
        // the joiner and expected==actual==GL with zero variance on the full 13,000 base. Guards against
        // a silent join-date exclusion, and against the run and reconstruction diverging on proration.
        var ruleReader = KsaRules();
        var tenantId = await SeedTenantAsync();
        var (_, saudiId, _, runId) = await SeedRunAsync(tenantId,
            saudiBasic: 10_000m, saudiHousing: 3_000m, gosiBonusGross: 0m,
            expatBasic: 0m, expatHousing: 0m, year: 2026, month: 11, includeExpat: false,
            saudiJoiningDate: new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc));

        await using (var db = _fx.CreateDb())
            Assert.IsType<OkObjectResult>(await BuildPayroll(db, tenantId, ruleReader).Process(runId, CancellationToken.None));

        const decimal ee = 13_000m * 0.0975m; // 1,267.50

        await using (var db = _fx.CreateDb())
        {
            // The joiner really was included (a slip exists), on the full covered wage.
            var slip = await db.PayrollSlips.AsNoTracking()
                .FirstAsync(s => s.TenantId == tenantId && s.RunId == runId && s.EmployeeId == saudiId);
            Assert.Equal(ee, slip.EmployeeStatutoryTotal, 2);
        }

        await LockRunAsync(tenantId, runId, ruleReader);

        await using (var db = _fx.CreateDb())
        {
            var run = await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
            var recon = await new GosiReconciliationService(db, KsaResolver(ruleReader))
                .ReconcileAsync(tenantId, run, CancellationToken.None);

            var row = recon.Rows.First(r => r.EmployeeId == saudiId);
            Assert.Equal(13_000m, row.CoveredWageBase, 2);
            Assert.Equal(ee, recon.ExpectedEmployeeTotal, 2);
            Assert.Equal(ee, recon.ActualEmployeeTotal, 2);
            Assert.Equal(ee, recon.GlEmployeeLiability!.Value, 2);
            Assert.Equal(0m, recon.ExpectedVsActualEmployeeDelta);
            Assert.Equal(0m, recon.GlEmployeeDelta);
            Assert.Equal(0, recon.VarianceCount);
        }
    }

    // ── Seeding helpers ─────────────────────────────────────────────────────────

    private async Task<Guid> SeedTenantAsync()
    {
        await using var db = _fx.CreateDb();
        return await PostgresFixture.SeedMinimalTenant(db);
    }

    /// <summary>Seeds full-day (480-min) unpaid-absence impacts so the run computes an LOP deduction.</summary>
    private async Task SeedAbsenceDaysAsync(Guid tenantId, int empId, params DateOnly[] days)
    {
        await using var db = _fx.CreateDb();
        foreach (var d in days)
            db.AttendancePayrollImpacts.Add(new AttendancePayrollImpact
            {
                TenantId = tenantId, EmployeeId = empId, WorkDate = d,
                ImpactType = "Absence", Minutes = 480, Status = "PendingPayroll",
            });
        await db.SaveChangesAsync();
    }

    private async Task<(Guid companyId, int saudiId, int expatId, Guid runId)> SeedRunAsync(
        Guid tenantId, decimal saudiBasic, decimal saudiHousing, decimal gosiBonusGross,
        decimal expatBasic, decimal expatHousing, int year, int month, bool includeExpat = true,
        DateTime? saudiJoiningDate = null)
    {
        await using var db = _fx.CreateDb();

        var company = new Company
        {
            Id = Guid.NewGuid(), TenantId = tenantId, LegalNameEn = "Tie-Out KSA Co",
            CountryCode = "SAU", Jurisdiction = "KSA-mainland",
            RegistrationNumber = $"TO-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true,
            CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Companies.Add(company);

        var saudi = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = $"SAU-{Guid.NewGuid():N}",
            FullName = "Ahmed TieOut", Nationality = "Saudi", ContractType = "Indefinite",
            Status = "Active",
            JoiningDate = saudiJoiningDate ?? new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(saudi);
        Employee? expat = null;
        if (includeExpat)
        {
            expat = new Employee
            {
                TenantId = tenantId, CompanyId = company.Id, EmployeeCode = $"IND-{Guid.NewGuid():N}",
                FullName = "Raj TieOut", Nationality = "Indian", ContractType = "Indefinite",
                Status = "Active", JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            db.Employees.Add(expat);
        }
        await db.SaveChangesAsync();

        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = saudi.Id, SalaryStructureId = Guid.NewGuid(),
            BasicSalary = saudiBasic, HousingAllowance = saudiHousing,
            EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
        });
        if (expat is not null)
            db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
            {
                TenantId = tenantId, EmployeeId = expat.Id, SalaryStructureId = Guid.NewGuid(),
                BasicSalary = expatBasic, HousingAllowance = expatHousing,
                EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
            });

        if (gosiBonusGross > 0m)
        {
            var bonusType = new BonusType
            {
                Id = Guid.NewGuid(), TenantId = tenantId, Code = "GOSI_BONUS", NameEn = "GOSI Bonus",
                IsIncludedInGosiBase = true, IsActive = true,
            };
            db.BonusTypes.Add(bonusType);
            db.EmployeeBonuses.Add(new EmployeeBonus
            {
                TenantId = tenantId, CompanyId = company.Id, BonusBatchId = Guid.NewGuid(),
                EmployeeId = Guid.NewGuid(), EmployeeIntId = saudi.Id, EmployeeName = saudi.FullName,
                BonusTypeId = bonusType.Id, BonusTypeName = bonusType.NameEn,
                GrossBonusAmount = gosiBonusGross, BonusAmount = gosiBonusGross, TaxWithheld = 0m,
                PaymentPeriod = $"{year}-{month:D2}", Status = "Approved", PayrollRunId = null,
            });
        }

        var run = new PayrollRun
        {
            TenantId = tenantId, CompanyId = company.Id, Year = year, Month = month,
            CreatedAtUtc = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();

        return (company.Id, saudi.Id, expat?.Id ?? 0, run.Id);
    }

    private async Task LockRunAsync(Guid tenantId, Guid runId, StubRuleReader ruleReader)
    {
        await using var db = _fx.CreateDb();
        // Clear any validation results and move the run to Approved so Lock proceeds.
        await db.PayrollValidationResults.Where(v => v.TenantId == tenantId && v.PayrollRunId == runId)
            .ExecuteDeleteAsync();
        await db.PayrollRuns.Where(r => r.TenantId == tenantId && r.Id == runId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "Approved"));

        var ctrl = BuildPayroll(db, tenantId, ruleReader, permissions: new[] { "payroll.lock" });
        var result = await ctrl.Lock(runId, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
    }

    // ── Controller builders ───────────────────────────────────────────────────

    private static PayrollController BuildPayroll(
        ZayraDbContext db, Guid tenantId, StubRuleReader ruleReader, string[]? permissions = null)
    {
        var ctrl = new PayrollController(
            db, new DataScopeService(db), new HttpContextAccessor(),
            new TieOutNotificationStub(), KsaResolver(ruleReader), ruleReader,
            new TieOutLetterStub(), new NullDocumentStorage(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(8));
        ctrl.ControllerContext = MakeContext(tenantId, permissions);
        return ctrl;
    }

    private GosiController BuildGosi(ZayraDbContext db, Guid tenantId, StubRuleReader ruleReader)
    {
        var ctrl = new GosiController(db, new GosiReconciliationService(db, KsaResolver(ruleReader)));
        ctrl.ControllerContext = MakeContext(tenantId, null);
        return ctrl;
    }

    private static ICountryPackResolver KsaResolver(StubRuleReader r) => new KsaTestPackResolver(r);

    private static ControllerContext MakeContext(Guid tenantId, string[]? permissions)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, "Admin"),
        };
        foreach (var p in permissions ?? Array.Empty<string>())
            claims.Add(new Claim("permission", p));
        return new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")),
            },
        };
    }

    // ── Anonymous-object reflection helpers ─────────────────────────────────────
    private static object? Prop(object o, string name) => o.GetType().GetProperty(name)!.GetValue(o);
    private static decimal Dec(object o, string name) => Convert.ToDecimal(Prop(o, name));
}

// ── File-scoped service stubs ───────────────────────────────────────────────────

file sealed class TieOutNotificationStub : INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message,
        string entityName, string? entityId, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName,
        Dictionary<string, string> variables, CancellationToken cancellationToken) => Task.CompletedTask;
}

file sealed class TieOutLetterStub : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
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
