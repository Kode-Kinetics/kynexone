using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.CountryPack.Qatar;
using Zayra.Api.Infrastructure.CountryPack.Uae;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// GOLDEN-MASTER / zero-regression proof for the data-driven pay-component engine.
///
/// Every scenario is processed THREE ways and the full run output is asserted byte-identical:
///   • Legacy      — Payroll/UseComponentEngine = "false" (the original compiled inline block);
///   • EngineEmpty — engine ON, pay_components store EMPTY  ⇒ compiled PayComponentCatalog fallback;
///   • EngineSeeded— engine ON, pay_components SEEDED via PayComponentSeeder (the real config path).
///
/// The captured snapshot is exact-decimal (0.00 tolerance) and covers everything the consultant's C1
/// requires: every earning line (Code,Name,Amount,Source); every deduction line
/// (Code,Name,Amount,Source,IsEmployerContribution); NetSalary; EmployeeStatutoryTotal;
/// EmployerStatutoryTotal; LoanDeductions; the YTD accumulators; the run header totals; the set of
/// PayrollValidationResults codes; and the FULL BuildPayrollGlEntries output (each Dr/Cr account +
/// amount) with totalDebits == totalCredits. Because GL is a deterministic function of the persisted
/// earning/deduction lines + net, identical lines prove identical GL — and the GL is asserted directly
/// as well. Runs on a real Postgres container so EF + decimal math are genuinely exercised.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class PayComponentGoldenMasterTests
{
    private readonly PostgresFixture _fx;
    public PayComponentGoldenMasterTests(PostgresFixture fx) => _fx = fx;

    private enum Mode { Legacy, EngineEmpty, EngineSeeded }

    // ── (1) KSA — one run exercising every distinct pay-computation path ─────────────────────────
    [Fact]
    public async Task Ksa_AllPaths_LegacyEngineFallbackAndSeeded_AreByteIdentical()
    {
        var (legacy, fb, seeded) = await RunThreeModesAsync(SeedKsaAllPaths);
        AssertIdentical(legacy, fb, seeded);

        // ── Frozen-value anchors (absolute correctness of the baseline the engine reproduces) ──
        // K1 Saudi below ceiling: covered = basic+housing = 13,000.
        var k1 = seeded.Slip("K1");
        Assert.Equal(1170.00m, k1.Ded("GOSI-ANN-EE").Amount);      // 13,000 × 9%
        Assert.Equal(97.50m,   k1.Ded("GOSI-SANED-EE").Amount);   // 13,000 × 0.75%
        Assert.Equal(1267.50m, k1.EeStat);                        // 9.75%
        Assert.Equal(1527.50m, k1.ErStat);                        // 11.75% (incl 2% OH)
        Assert.Equal(1000.00m, k1.Earn("OTHER_ALLOWANCES").Amount); // Food+Mobile+Other lumped (500+300+200)
        Assert.Equal(15000.00m, k1.Gross);                        // basic+housing+transport+other

        // K2 Saudi ABOVE the SAR 45,000 ceiling: covered capped at 45,000.
        var k2 = seeded.Slip("K2");
        Assert.Equal(4050.00m, k2.Ded("GOSI-ANN-EE").Amount);     // 45,000 × 9% (not 50,000)
        Assert.Equal(4387.50m, k2.EeStat);

        // K3 expat: no EE line, only employer OH.
        var k3 = seeded.Slip("K3");
        Assert.Equal(0m, k3.EeStat);
        Assert.DoesNotContain(k3.Deductions, l => l.Code.EndsWith("-EE"));
        Assert.Equal(200.00m, k3.ErStat);                          // 10,000 × 2% OH

        // K4 zero-basic allowance-only: BASIC line MUST still be emitted (at 0); no statutory lines.
        var k4 = seeded.Slip("K4");
        Assert.Equal(0m, k4.Earn("BASIC").Amount);
        Assert.Equal(2000.00m, k4.Net);                            // transport only
        Assert.DoesNotContain(k4.Deductions, l => l.Source == "Statutory");

        // K5 overtime, Saudi: two impacts (10h @1.5 fallback + 4h @2.0); OT excluded from covered wage.
        var k5 = seeded.Slip("K5");
        Assert.Equal(1150.00m, k5.Earn("OVERTIME").Amount);        // 10×50×1.5 + 4×50×2.0
        Assert.Equal(1170.00m, k5.EeStat);                         // 12,000 × 9.75% (OT NOT in base)

        // K6 loan + advance, EMI capped at outstanding.
        var k6 = seeded.Slip("K6");
        Assert.Equal(1000.00m, k6.Ded("LOAN_EMI").Amount);         // min(1000, 3000)
        Assert.Equal(400.00m,  k6.Ded("ADVANCE_EMI").Amount);      // min(500, 400) capped
        Assert.Equal(1400.00m, k6.Loan);

        // K7 adjustments: +ve earning line and −ve (abs) deduction line.
        var k7 = seeded.Slip("K7");
        Assert.Equal(800.00m, k7.Earn("ADJ_BONUS_CORRECTION").Amount);
        Assert.Equal(300.00m, k7.Ded("ADJ_OVERPAYMENT").Amount);

        // K8 bonus: gosi-included bonus folds into the covered wage; line carries GROSS, net uses net.
        var k8 = seeded.Slip("K8");
        Assert.Equal(2000.00m, k8.Earn("BONUS_ANNUAL").Amount);    // gross line
        Assert.Equal(1000.00m, k8.Earn("BONUS_FESTIVAL").Amount);
        Assert.Equal(877.50m,  k8.EeStat);                         // (7,000 + 2,000 incl-bonus) × 9.75%

        // K9 attendance short + LOP + leave.
        var k9 = seeded.Slip("K9");
        Assert.Equal(75.00m,  k9.Ded("ATTENDANCE").Amount);        // 120min/60 × (9000/240)
        Assert.Equal(300.00m, k9.Ded("LOP_DEDUCTION").Amount);     // 1 day × (9000/30)
        Assert.Equal(150.00m, k9.Ded("LEAVE").Amount);

        // GL balances in every mode.
        Assert.Equal(seeded.GlDebits, seeded.GlCredits);
        Assert.True(seeded.GlDebits > 0);
    }

    // ── (2) UAE — Emirati (GPSSA 5/12.5 on basic+housing) + expat (zero) ─────────────────────────
    [Fact]
    public async Task Uae_EmiratiAndExpat_ThreeModes_AreByteIdentical()
    {
        var (legacy, fb, seeded) = await RunThreeModesAsync(t => SeedGpssaGrsia(t, "ARE", "UAE-mainland",
            nationalCode: "Emirati", nationalEe: "GPSSA-EE", nationalEr: "GPSSA-ER"));
        AssertIdentical(legacy, fb, seeded);

        var u1 = seeded.Slip("N1"); // Emirati
        Assert.Equal(650.00m,  u1.Ded("GPSSA-EE").Amount);   // 13,000 × 5%
        Assert.Equal(650.00m,  u1.EeStat);
        Assert.Equal(1625.00m, u1.ErStat);                   // 13,000 × 12.5%
        var u2 = seeded.Slip("N2"); // expat
        Assert.Equal(0m, u2.EeStat);
        Assert.Equal(0m, u2.ErStat);
        Assert.DoesNotContain(u2.Deductions, l => l.Source == "Statutory");
        Assert.Equal(seeded.GlDebits, seeded.GlCredits);
    }

    // ── (3) Qatar — Qatari (GRSIA 7/14 on BASIC ONLY) + expat (zero) ─────────────────────────────
    [Fact]
    public async Task Qatar_QatariAndExpat_ThreeModes_AreByteIdentical()
    {
        var (legacy, fb, seeded) = await RunThreeModesAsync(t => SeedGpssaGrsia(t, "QAT", "QAT-mainland",
            nationalCode: "Qatari", nationalEe: "GRSIA-EE", nationalEr: "GRSIA-ER"));
        AssertIdentical(legacy, fb, seeded);

        var q1 = seeded.Slip("N1"); // Qatari — base is BASIC ONLY (housing excluded), proving the country matrix
        Assert.Equal(700.00m,  q1.Ded("GRSIA-EE").Amount);   // 10,000 × 7% (NOT 13,000)
        Assert.Equal(1400.00m, q1.ErStat);                   // 10,000 × 14%
        var q2 = seeded.Slip("N2");
        Assert.Equal(0m, q2.EeStat);
        Assert.Equal(seeded.GlDebits, seeded.GlCredits);
    }

    // ── (4) Income tax (CompanyTaxPolicy-less → SystemSettings fallback) ─────────────────────────
    [Fact]
    public async Task Ksa_IncomeTax_ThreeModes_AreByteIdentical()
    {
        var (legacy, fb, seeded) = await RunThreeModesAsync(SeedKsaIncomeTax);
        AssertIdentical(legacy, fb, seeded);

        var t1 = seeded.Slip("T1"); // Saudi, 10% income tax on taxable base = basic (no taxable SalaryComponents)
        Assert.Equal(1000.00m, t1.Ded("INCOME_TAX").Amount);        // 10,000 × 10%
        Assert.Equal("Income tax (10%)", t1.Ded("INCOME_TAX").Name); // dynamic label byte-identical
        Assert.Equal(seeded.GlDebits, seeded.GlCredits);
    }

    // ── (5) Rounding-adversarial: awkward decimals; round-each-then-sum preserved ────────────────
    [Fact]
    public async Task Ksa_RoundingAdversarial_ThreeModes_AreByteIdentical()
    {
        var (legacy, fb, seeded) = await RunThreeModesAsync(SeedKsaRounding);
        AssertIdentical(legacy, fb, seeded);

        var r1 = seeded.Slip("R1"); // covered = 4444.44
        // Per-line rounding: ANN = round(4444.44×0.09,2)=400.00; SANED = round(4444.44×0.0075,2)=33.33.
        Assert.Equal(400.00m, r1.Ded("GOSI-ANN-EE").Amount);
        Assert.Equal(33.33m,  r1.Ded("GOSI-SANED-EE").Amount);
        // EE total is the SUM of the per-line rounded amounts (round-each-then-sum), not a re-rounded whole.
        Assert.Equal(433.33m, r1.EeStat);
        Assert.Equal(seeded.GlDebits, seeded.GlCredits);
    }

    // ── (6) The engine is genuinely LIVE and data-driven (not a dormant branch) ──────────────────
    // Byte-identity across modes could, in principle, be produced by a flag bug that runs the legacy
    // block in every mode. This test rules that out: a company-scoped PayComponent OVERRIDE relabels the
    // TRANSPORT line — output ONLY the data-driven path can produce — while the amount (and therefore the
    // GL balance and net) stays identical. So the engine is proven to (a) execute and (b) honour
    // company-scoped config precedence, without disturbing any aggregate.
    [Fact]
    public async Task Engine_HonoursCompanyOverride_ProvingLiveDataDrivenPath()
    {
        var legacy = await RunRelabelScenarioAsync(overrideLabel: false);
        var engine = await RunRelabelScenarioAsync(overrideLabel: true);

        Assert.Equal("Transport allowance", legacy.Slip("C1").Earn("TRANSPORT").Name);
        Assert.Equal("Transport (company override)", engine.Slip("C1").Earn("TRANSPORT").Name); // engine-only
        // A relabel changes the label ONLY: amount, net and GL balance are untouched.
        Assert.Equal(legacy.Slip("C1").Earn("TRANSPORT").Amount, engine.Slip("C1").Earn("TRANSPORT").Amount);
        Assert.Equal(1000.00m, engine.Slip("C1").Earn("TRANSPORT").Amount);
        Assert.Equal(legacy.TotNet, engine.TotNet);
        Assert.Equal(engine.GlDebits, engine.GlCredits);
        // And the two runs are genuinely different objects (the label diverges) — the engine is not dormant.
        Assert.NotEqual(Canonical(legacy), Canonical(engine));
    }

    private async Task<RunSnap> RunRelabelScenarioAsync(bool overrideLabel)
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var t = new GmTenantSeed(db, tenantId);
        var companyId = t.AddCompany("SAU", "KSA-mainland");
        await t.SaveAsync();

        if (overrideLabel)
        {
            await PayComponentSeeder.SeedTenantDefaultsAsync(db, tenantId, CancellationToken.None);
            // Company-scoped override of the seeded TRANSPORT default — wins per (Code, ComponentType).
            db.PayComponents.Add(new PayComponent
            {
                TenantId = tenantId, CompanyId = companyId, Code = "TRANSPORT", ComponentType = PayComponentTypes.Earning,
                NameEn = "Transport (company override)", NameAr = "بدل النقل",
                CalcMethod = PayComponentCalcMethods.StructureField, StructureField = PayComponentStructureFields.TransportAllowance,
                GlDriverKey = "EARN:TRANSPORT", DisplayOrder = 50, IsActive = true,
            });
            await db.SaveChangesAsync();
        }
        else
        {
            db.SystemSettings.Add(new SystemSetting
            {
                TenantId = tenantId, Category = "Payroll", SettingKey = "UseComponentEngine", SettingValue = "false",
            });
            await db.SaveChangesAsync();
        }

        var emp = t.AddEmp("C1", "Indian"); // expat isolates the assertion from GOSI
        await t.SaveAsync();
        t.AddSalary(emp.Id, basic: 8_000m, housing: 0m, transport: 1_000m);
        t.AddProfile(emp.Id);
        var runId = t.AddRun(companyId);
        await t.SaveAsync();

        var res = await BuildCtrl(db, tenantId).Process(runId, CancellationToken.None);
        Assert.IsType<OkObjectResult>(res);
        return await CaptureAsync(db, tenantId, runId);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  Three-mode harness
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private async Task<(RunSnap Legacy, RunSnap EngineEmpty, RunSnap EngineSeeded)> RunThreeModesAsync(
        Func<GmTenantSeed, Task<Guid>> seed)
    {
        var legacy = await RunOneModeAsync(Mode.Legacy, seed);
        var empty  = await RunOneModeAsync(Mode.EngineEmpty, seed);
        var seeded = await RunOneModeAsync(Mode.EngineSeeded, seed);
        return (legacy, empty, seeded);
    }

    private async Task<RunSnap> RunOneModeAsync(Mode mode, Func<GmTenantSeed, Task<Guid>> seed)
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);

        if (mode == Mode.Legacy)
        {
            db.SystemSettings.Add(new SystemSetting
            {
                TenantId = tenantId, Category = "Payroll", SettingKey = "UseComponentEngine", SettingValue = "false",
            });
            await db.SaveChangesAsync();
        }
        if (mode == Mode.EngineSeeded)
        {
            await PayComponentSeeder.SeedTenantDefaultsAsync(db, tenantId, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        var runId = await seed(new GmTenantSeed(db, tenantId));

        var result = await BuildCtrl(db, tenantId).Process(runId, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);

        return await CaptureAsync(db, tenantId, runId);
    }

    private static void AssertIdentical(RunSnap legacy, RunSnap engineEmpty, RunSnap engineSeeded)
    {
        var jl = Canonical(legacy);
        var je = Canonical(engineEmpty);
        var js = Canonical(engineSeeded);
        // Legacy is the golden baseline. The compiled-fallback engine AND the seeded-store engine must
        // each reproduce it byte-for-byte (exact decimals, same lines, same GL, same validation codes).
        Assert.Equal(jl, je);
        Assert.Equal(jl, js);
    }

    private static string Canonical(RunSnap s) =>
        JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = false });

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  Snapshot capture (cross-tenant comparable: keyed by employee CODE, never GUID/int id)
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private static async Task<RunSnap> CaptureAsync(ZayraDbContext db, Guid tenantId, Guid runId)
    {
        var run = await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
        var slips = await db.PayrollSlips.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.RunId == runId).ToListAsync();
        var earnings = await db.PayrollEarnings.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.PayrollRunId == runId).ToListAsync();
        var deductions = await db.PayrollDeductions.AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.PayrollRunId == runId).ToListAsync();
        var validations = await db.PayrollValidationResults.AsNoTracking()
            .Where(v => v.TenantId == tenantId && v.PayrollRunId == runId).ToListAsync();
        var empCodeById = await db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId).ToDictionaryAsync(e => e.Id, e => e.EmployeeCode);

        var slipSnaps = slips
            .OrderBy(s => s.EmployeeCode, StringComparer.Ordinal)
            .Select(s => new SlipSnap(
                s.EmployeeCode, s.BasicSalary, s.HousingAllowance, s.TransportAllowance, s.OtherAllowances,
                s.GrossSalary, s.Deductions, s.NetSalary, s.EmployeeStatutoryTotal, s.EmployerStatutoryTotal,
                s.LoanDeductions, s.YtdGross, s.YtdDeductions, s.YtdNet,
                CanonLines(earnings.Where(e => e.EmployeeId == s.EmployeeId)
                    .Select(e => new LineSnap(e.ComponentCode, e.ComponentName, e.Amount, e.Source, false))),
                CanonLines(deductions.Where(d => d.EmployeeId == s.EmployeeId)
                    .Select(d => new LineSnap(d.ComponentCode, d.ComponentName, d.Amount, d.Source, d.IsEmployerContribution)))))
            .ToList();

        var validationSnap = validations
            .Select(v => $"{v.Severity}|{v.Code}|{(v.EmployeeId.HasValue && empCodeById.TryGetValue(v.EmployeeId.Value, out var c) ? c : "RUN")}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        var period = $"{run.Year}-{run.Month:D2}";
        var (gl, debits, credits) = ComputeGl(tenantId, runId, period, earnings, deductions, run.TotalNetSalary);

        return new RunSnap(
            run.TotalGrossSalary, run.TotalDeductions, run.TotalNetSalary, run.TotalEmployerStatutoryCost,
            run.EmployeeCount, slipSnaps, validationSnap, gl, debits, credits);
    }

    private static List<LineSnap> CanonLines(IEnumerable<LineSnap> lines) => lines
        .OrderBy(l => l.Code, StringComparer.Ordinal)
        .ThenBy(l => l.Source, StringComparer.Ordinal)
        .ThenBy(l => l.IsEmployer)
        .ThenBy(l => l.Amount)
        .ThenBy(l => l.Name, StringComparer.Ordinal)
        .ToList();

    /// <summary>Invokes the REAL private BuildPayrollGlEntries so the golden master covers the exact
    /// GL journal (each Dr/Cr account + amount) and the debits==credits invariant, not a re-implementation.</summary>
    private static (List<GlSnap> Lines, decimal Debits, decimal Credits) ComputeGl(
        Guid tenantId, Guid runId, string period, List<PayrollEarning> earnings, List<PayrollDeduction> deductions, decimal net)
    {
        var mi = typeof(PayrollController).GetMethod("BuildPayrollGlEntries", BindingFlags.NonPublic | BindingFlags.Static)!;
        var boxed = mi.Invoke(null, new object?[] { tenantId, runId, period, earnings, deductions, net, null, "", "SAR", null })!;
        var tuple = (ITuple)boxed;
        var entries = (IEnumerable<FinanceGlEntry>)tuple[0]!;
        var debits = (decimal)tuple[1]!;
        var credits = (decimal)tuple[2]!;
        var lines = entries
            .Select(e => new GlSnap(e.DebitAccount, e.CreditAccount, e.Amount))
            .OrderBy(x => x.Debit, StringComparer.Ordinal).ThenBy(x => x.Credit, StringComparer.Ordinal).ThenBy(x => x.Amount)
            .ToList();
        return (lines, debits, credits);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  Scenario seeders
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private static async Task<Guid> SeedKsaAllPaths(GmTenantSeed t)
    {
        var companyId = t.AddCompany("SAU", "KSA-mainland");
        var k1 = t.AddEmp("K1", "Saudi");
        var k2 = t.AddEmp("K2", "Saudi");
        var k3 = t.AddEmp("K3", "Indian");
        var k4 = t.AddEmp("K4", "Saudi");
        var k5 = t.AddEmp("K5", "Saudi");
        var k6 = t.AddEmp("K6", "Indian");
        var k7 = t.AddEmp("K7", "Indian");
        var k8 = t.AddEmp("K8", "Saudi");
        var k9 = t.AddEmp("K9", "Indian");
        await t.SaveAsync();

        t.AddSalary(k1.Id, basic: 10_000m, housing: 3_000m, transport: 1_000m, food: 500m, mobile: 300m, other: 200m);
        t.AddSalary(k2.Id, basic: 40_000m, housing: 10_000m);               // covered 50,000 → capped at 45,000
        t.AddSalary(k3.Id, basic: 8_000m,  housing: 2_000m);               // expat
        t.AddSalary(k4.Id, basic: 0m,      housing: 0m, transport: 2_000m); // zero-basic allowance-only
        t.AddSalary(k5.Id, basic: 12_000m, housing: 0m);                    // overtime
        t.AddSalary(k6.Id, basic: 5_000m,  housing: 0m);                    // loan + advance
        t.AddSalary(k7.Id, basic: 6_000m,  housing: 0m);                    // adjustments
        t.AddSalary(k8.Id, basic: 7_000m,  housing: 0m);                    // bonus
        t.AddSalary(k9.Id, basic: 9_000m,  housing: 0m);                    // attendance + LOP + leave
        foreach (var e in new[] { k1, k2, k3, k4, k5, k6, k7, k8, k9 }) t.AddProfile(e.Id);

        // K5 overtime: 10h at statutory 1.5× fallback (ApprovedMultiplier=0) + 4h at 2.0×.
        t.AddOvertime(k5.Id, new DateOnly(2026, 6, 10), hours: 10m, approvedMultiplier: 0m);
        t.AddOvertime(k5.Id, new DateOnly(2026, 6, 11), hours: 4m, approvedMultiplier: 2.0m);
        // K6 loan (EMI 1000 of 3000) + advance (EMI 500 capped at outstanding 400).
        t.AddLoan(k6.Id, installment: 1_000m, outstanding: 3_000m);
        t.AddAdvance(k6.Id, installment: 500m, outstanding: 400m);
        // K8 two bonuses: one folded into GOSI base (gross 2000), one not (gross 1000). GCC 0-tax ⇒ gross==net.
        var btIncluded = t.AddBonusType("ANNUAL", includedInGosi: true);
        var btPlain    = t.AddBonusType("FESTIVAL", includedInGosi: false);
        t.AddBonus(k8.Id, btIncluded, "Annual", gross: 2_000m, net: 2_000m);
        t.AddBonus(k8.Id, btPlain,    "Festival", gross: 1_000m, net: 1_000m);
        // K9 attendance short-hours 120min + 1 absent day (480min) + leave deduction 150.
        t.AddAttendance(k9.Id, new DateOnly(2026, 6, 4), "deduction", 120);
        t.AddAttendance(k9.Id, new DateOnly(2026, 6, 5), "Absence", 480);
        t.AddLeave(k9.Id, amount: 150m);

        var runId = t.AddRun(companyId);
        await t.SaveAsync();

        // K7 adjustments (need the run id): +800 earning, −300 deduction.
        t.AddAdjustment(runId, k7.Id, "Bonus Correction", 800m);
        t.AddAdjustment(runId, k7.Id, "Overpayment", -300m);
        await t.SaveAsync();
        return runId;
    }

    private static async Task<Guid> SeedGpssaGrsia(
        GmTenantSeed t, string cc, string jur, string nationalCode, string nationalEe, string nationalEr)
    {
        _ = nationalEe; _ = nationalEr; // documented expected codes; asserted in the test body
        var companyId = t.AddCompany(cc, jur);
        var n1 = t.AddEmp("N1", nationalCode);
        var n2 = t.AddEmp("N2", "Indian");
        await t.SaveAsync();
        t.AddSalary(n1.Id, basic: 10_000m, housing: 3_000m);
        t.AddSalary(n2.Id, basic: 9_000m, housing: 2_000m);
        t.AddProfile(n1.Id); t.AddProfile(n2.Id);
        var runId = t.AddRun(companyId);
        await t.SaveAsync();
        return runId;
    }

    private static async Task<Guid> SeedKsaIncomeTax(GmTenantSeed t)
    {
        // No CompanyTaxPolicy ⇒ the run falls back to the legacy SystemSettings magic key.
        t.Db.SystemSettings.Add(new SystemSetting
        {
            TenantId = t.TenantId, Category = "Payroll", SettingKey = "IncomeTaxRate", SettingValue = "10",
        });
        var companyId = t.AddCompany("SAU", "KSA-mainland");
        var e = t.AddEmp("T1", "Saudi");
        await t.SaveAsync();
        t.AddSalary(e.Id, basic: 10_000m, housing: 0m);
        t.AddProfile(e.Id);
        var runId = t.AddRun(companyId);
        await t.SaveAsync();
        return runId;
    }

    private static async Task<Guid> SeedKsaRounding(GmTenantSeed t)
    {
        var companyId = t.AddCompany("SAU", "KSA-mainland");
        var e = t.AddEmp("R1", "Saudi");
        await t.SaveAsync();
        t.AddSalary(e.Id, basic: 3_333.33m, housing: 1_111.11m); // covered 4,444.44 → awkward per-line rounding
        t.AddProfile(e.Id);
        var runId = t.AddRun(companyId);
        await t.SaveAsync();
        return runId;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  Controller construction (GCC pack resolver spanning KSA / UAE / Qatar)
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private static PayrollController BuildCtrl(ZayraDbContext db, Guid tenantId)
    {
        var rules = new StubRuleReader()
            .Set("gosi.saudi_employee_rate", 0.09m)
            .Set("gosi.saudi_employer_rate", 0.09m)
            .Set("gosi.saned_rate", 0.0075m)
            .Set("gosi.expat_occupational_hazard_rate", 0.02m)
            .Set("gosi.covered_wage_ceiling_sar", 45_000m)
            .Set("gpssa.national_employee_rate", 0.05m)
            .Set("gpssa.national_employer_rate", 0.125m)
            .Set("grsia.national_employee_rate", 0.07m)
            .Set("grsia.national_employer_rate", 0.14m)
            .Set("ot.standard_multiplier", 1.5m)
            .Set("ot.standard_monthly_hours", 240m)
            .Set("lop.monthly_day_divisor", 30m)
            .Set("lop.standard_work_minutes_per_day", 480m);

        var ctrl = new PayrollController(
            db, new DataScopeService(db), new HttpContextAccessor(),
            new _GmNullNotifications(), new _GmGccPackResolver(rules), rules,
            new _GmNullLetterService(), new NullDocumentStorage(), new PdfRenderGate(8));

        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role, "Admin"),
                }, "test")),
            },
        };
        return ctrl;
    }

    // ── snapshot record types ─────────────────────────────────────────────────────
    private sealed record LineSnap(string Code, string Name, decimal Amount, string Source, bool IsEmployer);
    private sealed record GlSnap(string Debit, string Credit, decimal Amount);
    private sealed record SlipSnap(
        string EmpCode, decimal Basic, decimal Housing, decimal Transport, decimal Other,
        decimal Gross, decimal DedTotal, decimal Net, decimal EeStat, decimal ErStat, decimal Loan,
        decimal YtdGross, decimal YtdDed, decimal YtdNet,
        List<LineSnap> Earnings, List<LineSnap> Deductions)
    {
        public LineSnap Earn(string code) => Earnings.Single(l => l.Code == code);
        public LineSnap Ded(string code) => Deductions.Single(l => l.Code == code);
    }
    private sealed record RunSnap(
        decimal TotGross, decimal TotDed, decimal TotNet, decimal TotErStat, int EmpCount,
        List<SlipSnap> Slips, List<string> Validation, List<GlSnap> Gl, decimal GlDebits, decimal GlCredits)
    {
        public SlipSnap Slip(string code) => Slips.Single(s => s.EmpCode == code);
    }
}

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  Declarative per-tenant scenario seeder
// ══════════════════════════════════════════════════════════════════════════════════════════════

internal sealed class GmTenantSeed
{
    public ZayraDbContext Db { get; }
    public Guid TenantId { get; }
    public GmTenantSeed(ZayraDbContext db, Guid tenantId) { Db = db; TenantId = tenantId; }

    public Task SaveAsync() => Db.SaveChangesAsync();

    public Guid AddCompany(string cc, string jur)
    {
        var c = new Company
        {
            Id = Guid.NewGuid(), TenantId = TenantId,
            LegalNameEn = $"GM Co {cc} {Guid.NewGuid():N}", CountryCode = cc, Jurisdiction = jur,
            RegistrationNumber = $"GM-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true,
            CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        Db.Companies.Add(c);
        return c.Id;
    }

    public Employee AddEmp(string code, string nationality)
    {
        var e = new Employee
        {
            TenantId = TenantId, EmployeeCode = code, FullName = $"Employee {code}",
            Nationality = nationality, Status = "Active",
            JoiningDate = new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        Db.Employees.Add(e);
        return e;
    }

    public void AddSalary(int empId, decimal basic, decimal housing = 0m, decimal transport = 0m,
        decimal food = 0m, decimal mobile = 0m, decimal other = 0m, decimal fixedDeduction = 0m)
        => Db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = TenantId, EmployeeId = empId, SalaryStructureId = Guid.NewGuid(),
            BasicSalary = basic, HousingAllowance = housing, TransportAllowance = transport,
            FoodAllowance = food, MobileAllowance = mobile, OtherAllowance = other, FixedDeduction = fixedDeduction,
            EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
        });

    public void AddProfile(int empId)
        => Db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
        {
            TenantId = TenantId, EmployeeId = empId,
            Iban = "SA4420000001234567891234", MolId = $"MOL-{Guid.NewGuid():N}", SalaryCurrency = "SAR",
        });

    public void AddOvertime(int empId, DateOnly workDate, decimal hours, decimal approvedMultiplier)
    {
        var req = new OvertimeRequest
        {
            TenantId = TenantId, EmployeeId = empId, EmployeeName = $"E{empId}", WorkDate = workDate,
            StartTimeUtc = new DateTime(workDate.Year, workDate.Month, workDate.Day, 17, 0, 0, DateTimeKind.Utc),
            EndTimeUtc = new DateTime(workDate.Year, workDate.Month, workDate.Day, 17, 0, 0, DateTimeKind.Utc).AddHours((double)hours),
            RequestedMinutes = (int)(hours * 60), ApprovedMinutes = (int)(hours * 60), Status = "Approved",
        };
        Db.OvertimeRequests.Add(req);
        Db.OvertimePayrollImpacts.Add(new OvertimePayrollImpact
        {
            TenantId = TenantId, OvertimeRequestId = req.Id, EmployeeId = empId,
            Hours = hours, Amount = 0m, ApprovedMultiplier = approvedMultiplier, Status = "PendingPayroll",
        });
    }

    public void AddLoan(int empId, decimal installment, decimal outstanding)
        => Db.EmployeeLoans.Add(new EmployeeLoan
        {
            TenantId = TenantId, EmployeeIntId = empId, EmployeeName = $"E{empId}",
            Status = "Active", InstallmentAmount = installment, OutstandingBalance = outstanding,
            LoanNumber = $"LN-{Guid.NewGuid():N}",
        });

    public void AddAdvance(int empId, decimal installment, decimal outstanding)
        => Db.SalaryAdvances.Add(new SalaryAdvance
        {
            TenantId = TenantId, EmployeeIntId = empId, EmployeeName = $"E{empId}",
            Status = "Active", InstallmentAmount = installment, OutstandingBalance = outstanding,
            AdvanceNumber = $"ADV-{Guid.NewGuid():N}",
        });

    public Guid AddBonusType(string code, bool includedInGosi)
    {
        var bt = new BonusType
        {
            TenantId = TenantId, Code = code, NameEn = code, IsIncludedInGosiBase = includedInGosi,
            TaxRegion = "GCC", IsActive = true,
        };
        Db.BonusTypes.Add(bt);
        return bt.Id;
    }

    public void AddBonus(int empId, Guid bonusTypeId, string typeName, decimal gross, decimal net)
        => Db.EmployeeBonuses.Add(new EmployeeBonus
        {
            TenantId = TenantId, BonusBatchId = Guid.NewGuid(), EmployeeIntId = empId, EmployeeName = $"E{empId}",
            BonusTypeId = bonusTypeId, BonusTypeName = typeName,
            GrossBonusAmount = gross, BonusAmount = net, TaxRegion = "GCC",
            PaymentPeriod = "2026-06", Status = "Approved", PayrollRunId = null,
        });

    public void AddAttendance(int empId, DateOnly workDate, string impactType, int minutes)
        => Db.AttendancePayrollImpacts.Add(new AttendancePayrollImpact
        {
            TenantId = TenantId, EmployeeId = empId, WorkDate = workDate,
            ImpactType = impactType, Minutes = minutes, Status = "PendingPayroll",
        });

    public void AddLeave(int empId, decimal amount)
        => Db.LeavePayrollImpacts.Add(new LeavePayrollImpact
        {
            TenantId = TenantId, LeaveRequestId = Guid.NewGuid(), EmployeeId = empId,
            PayPeriod = "2026-06", ImpactType = "Deduction", Amount = amount, Status = "Pending",
        });

    public void AddAdjustment(Guid runId, int empId, string type, decimal amount)
        => Db.PayrollAdjustments.Add(new PayrollAdjustment
        {
            TenantId = TenantId, PayrollRunId = runId, EmployeeId = empId,
            AdjustmentType = type, Amount = amount, Status = "Approved", Reason = string.Empty,
        });

    public Guid AddRun(Guid companyId)
    {
        var run = new PayrollRun
        {
            TenantId = TenantId, CompanyId = companyId, Year = 2026, Month = 6, Status = "Draft",
            CreatedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        Db.PayrollRuns.Add(run);
        return run.Id;
    }
}

// ── File-scoped stubs ─────────────────────────────────────────────────────────
file sealed class _GmNullNotifications : INotificationService
{
    public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string en, string? eid, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _GmGccPackResolver : ICountryPackResolver
{
    private readonly StubRuleReader _rules;
    public _GmGccPackResolver(StubRuleReader rules) => _rules = rules;

    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) => cc switch
    {
        "SAU" => new KsaDeductionCalculator(_rules),
        "ARE" => new UaeDeductionCalculator(_rules),
        "QAT" => new QatarDeductionCalculator(_rules),
        _ => new DefaultStatutoryDeductionCalculator(),
    };
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class _GmNullLetterService : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}
