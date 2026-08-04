using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// POD-B2 FIX-SME — validation Rule 2 (GOSI_MISSING_FOR_SAUDI) under multi-run periods.
///
/// The independent test SME's C1/F2 proved that a KSA supplemental run is un-lockable: Rule 2 was gated
/// on `isKsa` alone and never on the pay BASIS, so a run that zeroes basic/housing yields zero covered
/// wage → zero GOSI EE → a blocking Error. That run is then TERMINAL — Approve/Lock 422, re-Process 400
/// ("cannot be reprocessed"), DeleteRun 400 (Draft only), and nothing in the codebase ever sets
/// PayrollValidationResult.IsResolved — so Void is the only exit.
///
/// The fix is period-aware rather than merely basis-aware, because gating on the basis alone leaves a
/// SECOND, worse instance of the same bug: POD-B2's incremental statutory base nets off what sibling runs
/// already deducted, so whichever run arrives after the 45 k covered-wage ceiling is fully consumed
/// legitimately nets to zero — and that can be the tenant's REGULAR monthly run (R3 below).
///
/// Contract now enforced by Rule 2:
///   • Error   — the employee contributed NOTHING to GOSI anywhere in the period, on a run that pays
///               the recurring monthly wage. (Identical to pre-B2 behaviour for a single-run period.)
///   • Warning — nothing anywhere in the period, but this run pays supplemental items only.
///   • silent  — this run deducted zero because a sibling run already met the period obligation.
/// </summary>
public class PayrollRunTypeGosiPeriodScopeTests
{
    private const string SalariesPayable = "2100";
    private const string GosiEePayable   = "2101";

    // ── Harness ─────────────────────────────────────────────────────────────────

    private static (ZayraDbContext db, SqliteConnection conn) NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static ClaimsPrincipal Principal(Guid tenantId, Guid userId, string name) =>
        new(new ClaimsIdentity(new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, name),
            new(ClaimTypes.Role, "Admin"),
            new("permission", "payroll.write"),
            new("permission", "payroll.lock"),
            new("permission", "payroll.approve"),
            new("permission", "payroll.export"),
            new("permission", "payroll.run_delete"),
            new("permission", "finance.gl.manage"),
            new("permission", "finance.gl.read"),
        }, "test"));

    private static PayrollController Payroll(ZayraDbContext db, Guid tenantId, Guid userId, string name = "maker")
    {
        var httpCtx = new DefaultHttpContext { User = Principal(tenantId, userId, name) };
        var ctrl = new PayrollController(
            db, new _B2FixScope(), new _B2FixHttp(httpCtx), new _B2FixNotifications(),
            new _B2FixKsaResolver(), _B2FixRules.Rules, new _B2FixLetters(), new _B2FixDocs(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(4));
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }

    private static BonusesController Bonuses(ZayraDbContext db, Guid tenantId)
    {
        var httpCtx = new DefaultHttpContext { User = Principal(tenantId, Guid.NewGuid(), "bonus-user") };
        var ctrl = new BonusesController(db, new _B2FixScope());
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }

    private static readonly Guid Maker   = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Checker = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static async Task<Company> SeedCompany(ZayraDbContext db, Guid tenantId)
    {
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "Fix KSA Co", CountryCode = "SAU",
            Jurisdiction = "KSA-mainland", IsActive = true, DefaultCurrency = "SAR",
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        return company;
    }

    private static async Task<Employee> SeedEmployee(
        ZayraDbContext db, Guid tenantId, Guid companyId, string code,
        decimal basic = 10_000m, decimal housing = 2_000m, string nationality = "SAU")
    {
        var structure = await db.SalaryStructures.FirstOrDefaultAsync(s => s.TenantId == tenantId);
        if (structure is null)
        {
            structure = new SalaryStructure
            {
                TenantId = tenantId, CompanyId = companyId, Code = "STR-BASE", Name = "Base",
                Currency = "SAR", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
            };
            db.SalaryStructures.Add(structure);
            await db.SaveChangesAsync();
        }
        var emp = new Employee
        {
            TenantId = tenantId, CompanyId = companyId, EmployeeCode = code, FullName = $"Emp {code}",
            Status = "Active", JoiningDate = new DateTime(2023, 1, 1),
            WorkEmail = $"{code}@fix.test", Nationality = nationality, ContractType = "Indefinite",
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = emp.Id, SalaryStructureId = structure.Id,
            BasicSalary = basic, HousingAllowance = housing, TransportAllowance = 1_000m,
            EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
        });
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
        {
            TenantId = tenantId, EmployeeId = emp.Id,
            Iban = "SA4420000001234567891234", MolId = $"MOL-{code}", SalaryCurrency = "SAR",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return emp;
    }

    private static async Task<PayrollRun> CreateRun(
        ZayraDbContext db, PayrollController ctrl, int year, int month, Guid companyId,
        string? runType = null, Guid? parentRunId = null, bool? includesRecurringPay = null)
    {
        var res = await ctrl.CreateRun(
            new CreatePayrollRunRequest(year, month, companyId, runType, parentRunId, includesRecurringPay, null),
            CancellationToken.None);
        res.Should().BeOfType<CreatedResult>();
        var run = (PayrollRun)((CreatedResult)res).Value!;
        db.ChangeTracker.Clear();
        return await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == run.Id);
    }

    /// <summary>Approved bonus batch (real endpoint, so the POD-B1b accrual is the production journal).</summary>
    private static async Task SeedApprovedBonus(
        ZayraDbContext db, Guid tenantId, string period, string number, bool inGosiBase,
        Employee emp, decimal gross)
    {
        var type = await db.BonusTypes.FirstOrDefaultAsync(t => t.TenantId == tenantId && t.IsIncludedInGosiBase == inGosiBase);
        if (type is null)
        {
            type = new BonusType
            {
                TenantId = tenantId, Code = inGosiBase ? "GOSI_BON" : "PERF",
                NameEn = inGosiBase ? "Contractual" : "Performance",
                IsIncludedInGosiBase = inGosiBase, IsIncludedInWps = false, IsIncludedInEosb = false,
                TaxRegion = "GCC", IsActive = true,
            };
            db.BonusTypes.Add(type);
            await db.SaveChangesAsync();
        }
        var batch = new BonusBatch
        {
            TenantId = tenantId, BonusTypeId = type.Id, BonusTypeName = type.NameEn,
            BatchNumber = number, BatchName = $"Batch {number}", PaymentPeriod = period,
            PaymentDate = new DateOnly(2026, 6, 15), Status = "PendingApproval",
            EmployeeCount = 1, TotalAmount = gross,
        };
        db.BonusBatches.Add(batch);
        await db.SaveChangesAsync();
        db.EmployeeBonuses.Add(new EmployeeBonus
        {
            TenantId = tenantId, CompanyId = emp.CompanyId, BonusBatchId = batch.Id,
            EmployeeId = Guid.NewGuid(), EmployeeIntId = emp.Id, EmployeeName = emp.FullName,
            BonusTypeId = type.Id, BonusTypeName = type.NameEn, BasicSalary = 10_000m,
            CalculationMethod = "Fixed", CalculationValue = gross,
            GrossBonusAmount = gross, TaxWithheld = 0m, BonusAmount = gross,
            PaymentPeriod = period, Status = "Draft", TaxRegion = "GCC",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        (await Bonuses(db, tenantId).ApproveBatch(batch.Id, new BatchApproveRequest("accrue"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
    }

    private static async Task Include(PayrollController ctrl, Guid runId, string reason, params int[] empIds) =>
        (await ctrl.UpsertRunSelection(runId, new PayrollRunSelectionRequest("Include", reason, empIds.ToList()),
            CancellationToken.None)).Should().BeOfType<OkObjectResult>();

    private static string Describe(IActionResult r) => r switch
    {
        ObjectResult o => $"{o.GetType().Name} {o.StatusCode}: {JsonSerializer.Serialize(o.Value)}",
        _ => r.GetType().Name,
    };

    private static async Task ProcessApproveLock(ZayraDbContext db, Guid tenantId, Guid runId)
    {
        var processed = await Payroll(db, tenantId, Maker).Process(runId, CancellationToken.None);
        processed.Should().BeOfType<OkObjectResult>($"Process must succeed — got {Describe(processed)}");
        db.ChangeTracker.Clear();
        var approved = await Payroll(db, tenantId, Checker, "checker")
            .Approve(runId, new PayrollDecisionRequest("ok", null), CancellationToken.None);
        approved.Should().BeOfType<OkObjectResult>($"Approve must succeed — got {Describe(approved)}");
        db.ChangeTracker.Clear();
        var locked = await Payroll(db, tenantId, Checker, "checker").Lock(runId, CancellationToken.None);
        locked.Should().BeOfType<OkObjectResult>($"Lock must succeed — got {Describe(locked)}");
        db.ChangeTracker.Clear();
    }

    private static async Task<List<PayrollValidationResult>> Results(ZayraDbContext db, Guid tenantId, Guid runId) =>
        await db.PayrollValidationResults.AsNoTracking()
            .Where(v => v.TenantId == tenantId && v.PayrollRunId == runId).ToListAsync();

    private static decimal GosiEe(IEnumerable<FinanceGlEntry> gl) =>
        gl.Where(l => l.CreditAccount.Contains(GosiEePayable)).Sum(l => l.Amount)
      - gl.Where(l => l.DebitAccount.Contains(GosiEePayable)).Sum(l => l.Amount);

    // ═══════════════════════════════════════════════════════════════════════════
    //  R1 — the pre-B2 Error is untouched (this is the regression fence)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A Saudi national on the period's ONLY run, with the GOSI deduction removed, must still be a
    /// blocking Error. This is the behaviour every existing tenant has, and the narrowing must not have
    /// reached it: PriorPeriodGosiEeByEmployee is empty and the run pays recurring, so periodGosiEe is
    /// exactly the run's own figure.
    /// </summary>
    [Fact]
    public async Task R1_SingleRegularRun_SaudiWithNoGosi_IsStillABlockingError()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        var emp = await SeedEmployee(db, tid, co.Id, "E001");
        var ctrl = Payroll(db, tid, Maker);

        var run = await CreateRun(db, ctrl, 2026, 6, co.Id);
        (await ctrl.Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        // Strip the statutory GOSI EE rows the run produced, exactly as a misconfigured pack would.
        await db.PayrollDeductions
            .Where(d => d.TenantId == tid && d.PayrollRunId == run.Id && d.Source == "Statutory"
                     && !d.IsEmployerContribution)
            .ExecuteDeleteAsync();
        db.ChangeTracker.Clear();

        // /validate rebuilds results from persisted data — the run now has no GOSI EE anywhere.
        (await Payroll(db, tid, Maker).Validate(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var results = await Results(db, tid, run.Id);
        results.Should().Contain(r => r.Code == "GOSI_MISSING_FOR_SAUDI" && r.Severity == "Error" && r.EmployeeId == emp.Id,
            "a Saudi national paid a full monthly wage with zero GOSI, in a period with no other run, is a real compliance failure");

        var approve = await Payroll(db, tid, Checker, "checker")
            .Approve(run.Id, new PayrollDecisionRequest("ok", null), CancellationToken.None);
        approve.Should().BeOfType<UnprocessableEntityObjectResult>("the Error must still block Approve");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  R2 — the SME's C1/F2 defect: a supplemental run must not be bricked
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The pod brief's headline use case. The Regular run already met the period's GOSI obligation; the
    /// off-cycle run pays a bonus that is NOT in the GOSI base, so it has no covered wage of its own and
    /// correctly deducts nothing. That must produce NO result at all — not an Error (which would strand
    /// the run) and not even a Warning, because the period obligation is demonstrably met.
    /// </summary>
    [Fact]
    public async Task R2_SupplementalRun_WhoseSiblingAlreadyPaidGosi_ProducesNoGosiResultAtAll()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        var emp = await SeedEmployee(db, tid, co.Id, "E001");
        var ctrl = Payroll(db, tid, Maker);

        var regular = await CreateRun(db, ctrl, 2026, 6, co.Id);
        await ProcessApproveLock(db, tid, regular.Id);
        GosiEe(await db.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.SourceEntityId == regular.Id).ToListAsync())
            .Should().BeGreaterThan(0m, "the regular run carries the period's GOSI");

        await SeedApprovedBonus(db, tid, "2026-06", "BON-A", inGosiBase: false, emp, 5_000m);

        var offCycle = await CreateRun(db, ctrl, 2026, 6, co.Id, PayrollRunTypes.OffCycle);
        await Include(Payroll(db, tid, Maker), offCycle.Id, "Mid-month performance bonus", emp.Id);
        await ProcessApproveLock(db, tid, offCycle.Id);

        (await Results(db, tid, offCycle.Id))
            .Should().NotContain(r => r.Code == "GOSI_MISSING_FOR_SAUDI",
                "the period's GOSI was already deducted by the regular run, so this run's zero is correct and silent");

        // …and /validate, which DELETES and rebuilds the stored results, must not re-raise it.
        (await Payroll(db, tid, Maker).Validate(offCycle.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await Results(db, tid, offCycle.Id))
            .Should().NotContain(r => r.Code == "GOSI_MISSING_FOR_SAUDI",
                "/validate replaces results wholesale — it must reproduce the same cross-run facts Process fed the engine");
    }

    /// <summary>
    /// The genuinely non-compliant supplemental case: NO run anywhere in the period deducted GOSI for a
    /// Saudi national. That IS worth telling the preparer — but as a Warning, because a supplemental run
    /// has no covered wage of its own to fix it with, and an Error here would leave a correct run with no
    /// exit but Void.
    /// </summary>
    [Fact]
    public async Task R3_SupplementalRun_WithNoGosiAnywhereInThePeriod_WarnsButStillLocks()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        var emp = await SeedEmployee(db, tid, co.Id, "E001");
        var ctrl = Payroll(db, tid, Maker);

        // No Regular run for the period at all — a standalone off-cycle bonus.
        await SeedApprovedBonus(db, tid, "2026-06", "BON-B", inGosiBase: false, emp, 5_000m);
        var offCycle = await CreateRun(db, ctrl, 2026, 6, co.Id, PayrollRunTypes.OffCycle);
        await Include(Payroll(db, tid, Maker), offCycle.Id, "Standalone bonus", emp.Id);
        await ProcessApproveLock(db, tid, offCycle.Id);

        var results = await Results(db, tid, offCycle.Id);
        var gosi = results.Where(r => r.Code == "GOSI_MISSING_FOR_SAUDI").ToList();
        gosi.Should().ContainSingle("the period really has no GOSI for a Saudi national — that must be surfaced");
        gosi[0].Severity.Should().Be("Warning",
            "an Error would strand a run that is arithmetically correct: Approve/Lock 422, re-Process 400, DeleteRun 400, IsResolved is never set");
        gosi[0].EmployeeId.Should().Be(emp.Id);

        var run = await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == offCycle.Id);
        run.Status.Should().Be("Locked", "the run must reach Locked, which is the whole point of the fix");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  R4 — the second instance of the same bug, which basis-gating alone misses
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The REGULAR run is the victim here. A GOSI-base bonus larger than the 45 k covered-wage ceiling is
    /// paid off-cycle first; POD-B2's incremental statutory base then applies the ceiling to the period
    /// total and nets off what the off-cycle run already deducted, so the monthly run's own GOSI delta is
    /// zero. It pays a full recurring wage, so gating Rule 2 on the pay basis alone would still raise a
    /// blocking Error — and brick the tenant's MONTHLY payroll. Period-awareness is what prevents it.
    /// </summary>
    [Fact]
    public async Task R4_RegularRun_WhoseGosiWasFullyConsumedByAPriorOffCycleRun_StillLocks()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        // Covered wage 40 k + 2 k = 42 k; a 50 k GOSI-base bonus pushes the period past the 45 k ceiling.
        var emp = await SeedEmployee(db, tid, co.Id, "E001", basic: 40_000m, housing: 2_000m);
        var ctrl = Payroll(db, tid, Maker);

        await SeedApprovedBonus(db, tid, "2026-06", "BON-C", inGosiBase: true, emp, 50_000m);

        // The off-cycle run goes FIRST and consumes the whole period ceiling.
        var offCycle = await CreateRun(db, ctrl, 2026, 6, co.Id, PayrollRunTypes.OffCycle);
        await Include(Payroll(db, tid, Maker), offCycle.Id, "Annual bonus, paid ahead of the monthly run", emp.Id);
        await ProcessApproveLock(db, tid, offCycle.Id);

        var offCycleGl = await db.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.SourceEntityId == offCycle.Id).ToListAsync();
        GosiEe(offCycleGl).Should().BeGreaterThan(0m, "the off-cycle run deducted the period's capped GOSI");

        // …now the monthly run, whose own GOSI delta is zero.
        var regular = await CreateRun(db, ctrl, 2026, 6, co.Id);
        (await Payroll(db, tid, Maker).Process(regular.Id, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        // Materialised before summing: SQLite cannot aggregate decimals server-side.
        var regularStatutoryEe = await db.PayrollDeductions.AsNoTracking()
            .Where(d => d.TenantId == tid && d.PayrollRunId == regular.Id && d.Source == "Statutory"
                     && !d.IsEmployerContribution)
            .ToListAsync();
        regularStatutoryEe.Sum(d => d.Amount).Should().Be(0m,
            "the ceiling was already fully consumed by the off-cycle run — this is the condition under test");

        (await Results(db, tid, regular.Id))
            .Should().NotContain(r => r.Code == "GOSI_MISSING_FOR_SAUDI" && r.Severity == "Error",
                "the period obligation is met; blocking the monthly run here would brick the tenant's payroll");

        var approved = await Payroll(db, tid, Checker, "checker")
            .Approve(regular.Id, new PayrollDecisionRequest("ok", null), CancellationToken.None);
        approved.Should().BeOfType<OkObjectResult>($"the monthly run must Approve — got {Describe(approved)}");
        db.ChangeTracker.Clear();
        (await Payroll(db, tid, Checker, "checker").Lock(regular.Id, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        // The period's GOSI is capped exactly once across the two runs, and both journals balance.
        var allGl = await db.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tid).ToListAsync();
        var dr = allGl.Where(l => !string.IsNullOrEmpty(l.DebitAccount)).Sum(l => l.Amount);
        var cr = allGl.Where(l => !string.IsNullOrEmpty(l.CreditAccount)).Sum(l => l.Amount);
        dr.Should().Be(cr, "the whole ledger must stay balanced across both runs");
        // 45 000 ceiling × 9.75 % (9 % annuities + 0.75 % SANED) = 4 387.50, deducted ONCE for the period.
        GosiEe(allGl).Should().Be(4_387.50m, "the covered-wage ceiling is a PERIOD concept, applied exactly once");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  R5 — the per-run GOSI report no longer presents a phantom variance as fact
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Under the incremental basis a run's ACTUAL is a period delta while the per-run report's EXPECTED is
    /// a standalone recomputation, so near the ceiling they cannot agree — "this run's share of a capped
    /// period total" is not a standalone quantity, and it cannot be reproduced after the fact because the
    /// order the period's runs were processed in is not persisted. The per-run reconciliation must
    /// therefore declare itself partial and point at the period tie-out, rather than report a variance
    /// that would send a compliance officer chasing a filing error that does not exist.
    /// </summary>
    [Fact]
    public async Task R5_PerRunGosiReconciliation_DeclaresItselfPartial_WhenThePeriodHoldsSiblingRuns()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        var emp = await SeedEmployee(db, tid, co.Id, "E001", basic: 40_000m, housing: 2_000m);
        var ctrl = Payroll(db, tid, Maker);
        var svc = new GosiReconciliationService(db, new _B2FixKsaResolver());

        var regular = await CreateRun(db, ctrl, 2026, 6, co.Id);
        await ProcessApproveLock(db, tid, regular.Id);

        // Single-run period: the per-run report is the whole truth and says so by staying silent.
        var solo = await svc.ReconcileAsync(tid, await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == regular.Id), CancellationToken.None);
        solo.SiblingRunCount.Should().Be(0);
        solo.ExpectedIsPeriodPartial.Should().BeFalse();
        solo.PeriodScopeNote.Should().BeNull("nothing changes for a period with one run — i.e. every run in every tenant before B2");
        solo.ExpectedVsActualEmployeeDelta.Should().Be(0m, "POD-A1's per-run invariant still holds outright");

        // Add a sibling and the same report must now declare itself partial.
        await SeedApprovedBonus(db, tid, "2026-06", "BON-D", inGosiBase: true, emp, 50_000m);
        var offCycle = await CreateRun(db, ctrl, 2026, 6, co.Id, PayrollRunTypes.OffCycle);
        await Include(Payroll(db, tid, Maker), offCycle.Id, "Annual bonus", emp.Id);
        await ProcessApproveLock(db, tid, offCycle.Id);

        var partial = await svc.ReconcileAsync(tid, await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == offCycle.Id), CancellationToken.None);
        partial.SiblingRunCount.Should().Be(1);
        partial.ExpectedIsPeriodPartial.Should().BeTrue();
        partial.PeriodScopeNote.Should().NotBeNull().And.Subject.As<string>()
            .Should().Contain("contribution-summary", "the note must name the endpoint that IS the filing source");

        // The period tie-out is the one that holds: actual == expected on the period-aggregated base.
        var period = await svc.ReconcilePeriodAsync(tid, co.Id, 2026, 6, CancellationToken.None);
        period.RunCount.Should().Be(2);
        period.ExpectedVsActualEmployeeDelta.Should().Be(0m,
            "the period recomputation applies the ceiling once, exactly as Process's incremental base did");
        period.ExpectedVsActualEmployerDelta.Should().Be(0m);
        period.ActualEmployeeTotal.Should().Be(4_387.50m, "45 000 ceiling × 9.75 %, once for the month");
    }
}

// ── Test doubles (file-scoped) ─────────────────────────────────────────────────

file static class _B2FixRules
{
    internal static readonly StubRuleReader Rules = new StubRuleReader()
        .Set("gosi.saudi_employee_rate",            0.09m)
        .Set("gosi.saudi_employer_rate",            0.09m)
        .Set("gosi.saned_rate",                     0.0075m)
        .Set("gosi.expat_occupational_hazard_rate", 0.02m)
        .Set("gosi.covered_wage_ceiling_sar",       45_000m)
        .Set("ot.standard_multiplier",              1.5m)
        .Set("ot.standard_monthly_hours",           240m)
        .Set("lop.monthly_day_divisor",             30m)
        .Set("lop.standard_work_minutes_per_day",   480m);
}

file sealed class _B2FixScope : Zayra.Api.Application.Common.IDataScopeService
{
    public Task<Zayra.Api.Application.Common.DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Application.Common.DataScope
        { Level = Zayra.Api.Application.Common.DataScopeLevel.Organization, AllowedEmployeeIds = null });
}

file sealed class _B2FixHttp : IHttpContextAccessor
{
    public _B2FixHttp(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _B2FixNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _B2FixKsaResolver : ICountryPackResolver
{
    private static readonly KsaDeductionCalculator _calc = new(_B2FixRules.Rules);
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => cc == "SAU" ? _calc : new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class _B2FixLetters : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class _B2FixDocs : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument(file.FileName, file.ContentType, "storage/test", "/tmp/test"));
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public string ResolvePath(string storageUrl) => "/tmp/test";
}
