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
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// POD-B2 — INDEPENDENT SDE-TEST-SME pass over off-cycle / supplementary / correction run types.
///
/// Written against the REQUIREMENT, not the implementation. Where the implementer's own
/// <see cref="PayrollRunTypeTests"/> stops at Process, this suite drives the whole money path —
/// Process → Approve → Lock → payment batch → settle → remit → void — because the entire point of a
/// typed run is that everything DOWNSTREAM of it stays correct:
///
///   (a) two Regular runs for one (company, period) are still refused, but Regular + OffCycle coexist;
///   (b) a VOIDED Regular run does not block a replacement Regular run for that period;
///   (c) an off-cycle run Processes, Locks with a BALANCED journal, settles + remits (POD-B1) and is
///       still refused by the period-close guard;
///   (d) a bonus consumed by an off-cycle run clears its POD-B1b accrual EXACTLY once — 2300 nets to
///       zero, 6100 is debited once — and a later Regular run cannot clear it again;
///   (e) the hold-out selector excludes only the named employees, records the reason, is hash-chain
///       audited, and the excluded set is REPORTED at every stage (never silently dropped);
///   (f) voiding an off-cycle run contra-reverses every line and re-opens the bonus payable.
///
/// KSA pack throughout, so statutory lines are real: covered wage = basic + housing, GOSI EE 9.75 %
/// (9 % annuities + 0.75 % SANED), employer 9 % + 0.75 %.
/// </summary>
public class PayrollRunTypeSmeTests
{
    // Account-code fragments (the ledger stores "code - name").
    private const string SalariesPayable  = "2100";   // net pay
    private const string GosiEePayable    = "2101";
    private const string TaxPayable       = "2102";
    private const string GosiErPayable    = "2106";
    private const string BonusPayable     = "2300";
    private const string CashBank         = "1000";
    private const string BonusExpense     = "6100";

    // ── Harness ─────────────────────────────────────────────────────────────────

    private static (ZayraDbContext db, SqliteConnection conn) NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static ClaimsPrincipal Principal(Guid tenantId, Guid userId, string name)
    {
        var claims = new List<Claim>
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
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    /// <summary>A payroll controller acting as a NAMED user, so maker-checker is deterministic.</summary>
    private static PayrollController Payroll(ZayraDbContext db, Guid tenantId, Guid userId, string name = "maker")
    {
        var httpCtx = new DefaultHttpContext { User = Principal(tenantId, userId, name) };
        var ctrl = new PayrollController(
            db, new _B2SmeScope(), new _B2SmeHttp(httpCtx), new _B2SmeNotifications(),
            new _B2SmeKsaResolver(), _B2SmeRules.Rules, new _B2SmeLetters(), new _B2SmeDocs(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(4));
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }

    private static BonusesController Bonuses(ZayraDbContext db, Guid tenantId)
    {
        var httpCtx = new DefaultHttpContext { User = Principal(tenantId, Guid.NewGuid(), "bonus-user") };
        var ctrl = new BonusesController(db, new _B2SmeScope());
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }

    private static FinanceGlController Gl(ZayraDbContext db, Guid tenantId)
    {
        var httpCtx = new DefaultHttpContext { User = Principal(tenantId, Guid.NewGuid(), "finance-user") };
        var ctrl = new FinanceGlController(db);
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }

    private static readonly Guid Maker   = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Checker = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // ── Ledger assertions ───────────────────────────────────────────────────────

    private static async Task<List<FinanceGlEntry>> AllGl(ZayraDbContext db, Guid tenantId) =>
        await db.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenantId).ToListAsync();

    private static async Task<List<FinanceGlEntry>> RunGl(ZayraDbContext db, Guid runId) =>
        await db.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.SourceModule == "Payroll" && x.SourceEntityId == runId).ToListAsync();

    private static decimal Debits(IEnumerable<FinanceGlEntry> gl, string acct) =>
        gl.Where(l => l.DebitAccount.Contains(acct)).Sum(l => l.Amount);
    private static decimal Credits(IEnumerable<FinanceGlEntry> gl, string acct) =>
        gl.Where(l => l.CreditAccount.Contains(acct)).Sum(l => l.Amount);
    /// <summary>Carrying balance of a liability: Σ credits − Σ debits. A contra is a real posting, so
    /// IsReversed is an audit link here, never a balance exclusion.</summary>
    private static decimal NetLiability(IEnumerable<FinanceGlEntry> gl, string acct) =>
        Credits(gl, acct) - Debits(gl, acct);

    private static void AssertBalanced(IEnumerable<FinanceGlEntry> gl, string because)
    {
        var list = gl.ToList();
        var dr = list.Where(l => !string.IsNullOrEmpty(l.DebitAccount)).Sum(l => l.Amount);
        var cr = list.Where(l => !string.IsNullOrEmpty(l.CreditAccount)).Sum(l => l.Amount);
        dr.Should().Be(cr, because);
    }

    // ── Seed helpers ────────────────────────────────────────────────────────────

    private static async Task<Company> SeedCompany(ZayraDbContext db, Guid tenantId, string name = "Test KSA Co")
    {
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = name, CountryCode = "SAU",
            Jurisdiction = "KSA-mainland", IsActive = true, DefaultCurrency = "SAR",
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        return company;
    }

    private static async Task<Employee> SeedEmployee(
        ZayraDbContext db, Guid tenantId, Guid companyId, string code,
        decimal basic = 10_000m, string nationality = "SAU")
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
            WorkEmail = $"{code}@test.com", Nationality = nationality, ContractType = "Indefinite",
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = emp.Id, SalaryStructureId = structure.Id,
            BasicSalary = basic, HousingAllowance = 2_000m, TransportAllowance = 1_000m,
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

    /// <summary>Creates a run through the REAL endpoint and returns the persisted row.</summary>
    private static async Task<PayrollRun> CreateRun(
        ZayraDbContext db, PayrollController ctrl, int year, int month, Guid companyId,
        string? runType = null, Guid? parentRunId = null, bool? includesRecurringPay = null,
        string? glPostingPeriod = null)
    {
        var res = await ctrl.CreateRun(
            new CreatePayrollRunRequest(year, month, companyId, runType, parentRunId, includesRecurringPay, glPostingPeriod),
            CancellationToken.None);
        res.Should().BeOfType<CreatedResult>($"a {runType ?? "Regular"} run for {year}-{month:D2} must be creatable");
        var run = (PayrollRun)((CreatedResult)res).Value!;
        db.ChangeTracker.Clear();
        return await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == run.Id);
    }

    /// <summary>A PendingApproval batch, ready for the REAL ApproveBatch endpoint (so the accrual under
    /// test is the production DR 6100 / CR 2300 journal, not a hand-written row).</summary>
    private static async Task<BonusBatch> SeedPendingBatch(
        ZayraDbContext db, Guid tenantId, string period, string number, bool inGosiBase,
        params (Employee Emp, decimal Gross, decimal Tax)[] children)
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
            EmployeeCount = children.Length,
            TotalAmount = children.Sum(c => c.Gross - c.Tax),
        };
        db.BonusBatches.Add(batch);
        await db.SaveChangesAsync();
        foreach (var (emp, gross, tax) in children)
            db.EmployeeBonuses.Add(new EmployeeBonus
            {
                TenantId = tenantId, CompanyId = emp.CompanyId, BonusBatchId = batch.Id,
                EmployeeId = Guid.NewGuid(), EmployeeIntId = emp.Id, EmployeeName = emp.FullName,
                BonusTypeId = type.Id, BonusTypeName = type.NameEn, BasicSalary = 10_000m,
                CalculationMethod = "Fixed", CalculationValue = gross,
                GrossBonusAmount = gross, TaxWithheld = tax, BonusAmount = gross - tax,
                PaymentPeriod = period, Status = "Draft", TaxRegion = "GCC",
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return batch;
    }

    private static async Task ApproveBatch(ZayraDbContext db, Guid tenantId, Guid batchId)
    {
        (await Bonuses(db, tenantId).ApproveBatch(batchId, new BatchApproveRequest("accrue"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
    }

    private static async Task Include(PayrollController ctrl, Guid runId, string reason, params int[] empIds) =>
        (await ctrl.UpsertRunSelection(runId, new PayrollRunSelectionRequest("Include", reason, empIds.ToList()),
            CancellationToken.None)).Should().BeOfType<OkObjectResult>();

    private static async Task Exclude(PayrollController ctrl, Guid runId, string reason, params int[] empIds) =>
        (await ctrl.UpsertRunSelection(runId, new PayrollRunSelectionRequest("Exclude", reason, empIds.ToList()),
            CancellationToken.None)).Should().BeOfType<OkObjectResult>();

    /// <summary>Process (as Maker) → Approve (as Checker) → Lock. Asserts every step, so a downstream
    /// blocker surfaces exactly where it happens instead of as a mystery later.</summary>
    private static async Task ProcessApproveLock(
        ZayraDbContext db, Guid tenantId, Guid runId, int? expectedExcludedCount = null)
    {
        var processed = await Payroll(db, tenantId, Maker).Process(runId, CancellationToken.None);
        processed.Should().BeOfType<OkObjectResult>($"the run must Process — got {Describe(processed)}");
        db.ChangeTracker.Clear();

        var approved = await Payroll(db, tenantId, Checker, "checker")
            .Approve(runId, new PayrollDecisionRequest("ok", expectedExcludedCount), CancellationToken.None);
        approved.Should().BeOfType<OkObjectResult>($"the run must Approve — got {Describe(approved)}");
        db.ChangeTracker.Clear();

        var locked = await Payroll(db, tenantId, Checker, "checker").Lock(runId, CancellationToken.None);
        locked.Should().BeOfType<OkObjectResult>(
            $"the run must Lock and post a balanced accrual journal — got {Describe(locked)}");
        db.ChangeTracker.Clear();
    }

    /// <summary>Renders an IActionResult (status + body) so a failure names the refusal instead of only
    /// its CLR type.</summary>
    private static string Describe(IActionResult result) => result switch
    {
        ObjectResult o => $"{o.GetType().Name} {o.StatusCode}: {JsonSerializer.Serialize(o.Value)}",
        StatusCodeResult s => $"{s.GetType().Name} {s.StatusCode}",
        _ => result.GetType().Name,
    };

    /// <summary>Locked run → payment batch → bank Accepted, so SettlePaymentBatch may run.</summary>
    private static async Task<PayrollPaymentBatch> AcceptedBatch(ZayraDbContext db, Guid tenantId, Guid runId)
    {
        (await Payroll(db, tenantId, Checker).CreatePaymentBatch(runId, new PayrollPaymentBatchRequest("WPS", "SAR"), CancellationToken.None))
            .Should().BeOfType<CreatedResult>();
        db.ChangeTracker.Clear();
        var batch = await db.PayrollPaymentBatches.FirstAsync(b => b.TenantId == tenantId && b.PayrollRunId == runId);
        batch.WpsStatus = WpsStatuses.Accepted;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return batch;
    }

    private static async Task<List<PayrollAuditLog>> Audit(ZayraDbContext db, Guid tenantId, string action) =>
        await db.PayrollAuditLogs.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.Action == action)
            .OrderBy(a => a.Seq).ToListAsync();

    private static JsonElement Meta(PayrollAuditLog log) =>
        JsonDocument.Parse(log.MetadataJson).RootElement.GetProperty("data");

    // ═══════════════════════════════════════════════════════════════════════════
    //  (a) UNIQUENESS — one Regular run per period, unlimited non-Regular runs
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task A1_SecondRegularRunIsRefused_ButARegularAndAnOffCycleRunCoexistForTheSamePeriod()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        var ctrl = Payroll(db, tid, Maker);

        var regular = await CreateRun(db, ctrl, 2026, 6, co.Id);
        regular.RunType.Should().Be(PayrollRunTypes.Regular, "an unspecified type defaults to the monthly run");
        regular.IncludesRecurringPay.Should().BeTrue();

        // The second REGULAR run is refused — the whole point of the relaxation is that it stays refused.
        var dup = await ctrl.CreateRun(new CreatePayrollRunRequest(2026, 6, co.Id), CancellationToken.None);
        dup.Should().BeOfType<ConflictObjectResult>();
        JsonSerializer.Serialize(((ConflictObjectResult)dup).Value)
            .Should().Contain("regular_run_exists").And.Contain(regular.Id.ToString());

        // …while every non-Regular type may coexist with it, and with each other.
        var offCycle = await CreateRun(db, ctrl, 2026, 6, co.Id, PayrollRunTypes.OffCycle);
        var supp     = await CreateRun(db, ctrl, 2026, 6, co.Id, PayrollRunTypes.Supplementary);
        var offCycle2 = await CreateRun(db, ctrl, 2026, 6, co.Id, PayrollRunTypes.OffCycle);

        offCycle.IncludesRecurringPay.Should().BeFalse("an off-cycle run defaults to a supplemental basis");
        supp.IncludesRecurringPay.Should().BeFalse();

        // A Correction must name the run it amends; a Regular may not.
        var parentless = await ctrl.CreateRun(
            new CreatePayrollRunRequest(2026, 6, co.Id, PayrollRunTypes.Correction), CancellationToken.None);
        parentless.Should().BeOfType<BadRequestObjectResult>();
        JsonSerializer.Serialize(((BadRequestObjectResult)parentless).Value).Should().Contain("parent_required");

        var runs = await db.PayrollRuns.AsNoTracking().Where(r => r.TenantId == tid).ToListAsync();
        runs.Should().HaveCount(4);
        runs.Count(r => r.RunType == PayrollRunTypes.Regular).Should().Be(1,
            "exactly one Regular run may own the period");
        new[] { offCycle.Id, supp.Id, offCycle2.Id }.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task A2_TheDatabaseIndexItself_RefusesASecondRegular_AndPermitsUnlimitedNonRegularRuns()
    {
        // Belt-and-braces below the API: the CreateRun 409 is a courtesy, the partial unique index is the
        // guarantee. Asserted against the MODEL's own HasFilter (EnsureCreated), independently of the
        // migration SQL.
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var coId = Guid.NewGuid();

        db.PayrollRuns.Add(new PayrollRun { TenantId = tid, CompanyId = coId, Year = 2026, Month = 6, Status = "Locked" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        db.PayrollRuns.Add(new PayrollRun { TenantId = tid, CompanyId = coId, Year = 2026, Month = 6, Status = "Draft" });
        var act = async () => await db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>("a second non-voided Regular run must be impossible at the DB level");
        db.ChangeTracker.Clear();

        for (var i = 0; i < 4; i++)
            db.PayrollRuns.Add(new PayrollRun
            {
                TenantId = tid, CompanyId = coId, Year = 2026, Month = 6, Status = "Draft",
                RunType = i % 2 == 0 ? PayrollRunTypes.OffCycle : PayrollRunTypes.Correction,
                IncludesRecurringPay = false,
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        (await db.PayrollRuns.CountAsync(r => r.TenantId == tid)).Should().Be(5);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  (b) A VOIDED Regular run must not brick the period
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task B1_VoidedRegularRun_DoesNotBlockANewRegularRunForThatPeriod()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        var ctrl = Payroll(db, tid, Maker);

        var first = await CreateRun(db, ctrl, 2026, 6, co.Id);
        (await ctrl.VoidRun(first.Id, new PayrollDecisionRequest("created for the wrong entity"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        // The index has carried `WHERE status != 'Voided'` since 20260624000001; the API must agree with it.
        var replacement = await CreateRun(db, ctrl, 2026, 6, co.Id);
        replacement.Id.Should().NotBe(first.Id);
        replacement.RunType.Should().Be(PayrollRunTypes.Regular);

        var runs = await db.PayrollRuns.AsNoTracking().Where(r => r.TenantId == tid).ToListAsync();
        runs.Should().HaveCount(2);
        runs.Count(r => r.Status == "Voided").Should().Be(1);
        runs.Count(r => r.Status != "Voided" && r.RunType == PayrollRunTypes.Regular).Should().Be(1);
    }

    [Fact]
    public async Task B2_VoidingALOCKEDRegularRun_FreesThePeriod_AndLeavesTheLedgerFlat()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        await SeedEmployee(db, tid, co.Id, "E001");
        var ctrl = Payroll(db, tid, Maker);

        var first = await CreateRun(db, ctrl, 2026, 6, co.Id);
        await ProcessApproveLock(db, tid, first.Id);
        (await RunGl(db, first.Id)).Should().NotBeEmpty("a Locked run has posted its accrual");

        (await Payroll(db, tid, Checker).VoidRun(first.Id, new PayrollDecisionRequest("wrong pay date"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var gl = await RunGl(db, first.Id);
        AssertBalanced(gl, "accrual + contra must leave the ledger balanced");
        NetLiability(gl, SalariesPayable).Should().Be(0m, "the void reverses the net-pay liability in full");
        NetLiability(gl, GosiEePayable).Should().Be(0m);
        NetLiability(gl, GosiErPayable).Should().Be(0m);

        // …and the period is now free for a replacement Regular run — the POD-B3 seam.
        var replacement = await CreateRun(db, ctrl, 2026, 6, co.Id);
        replacement.Status.Should().Be("Draft");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  (c) An off-cycle run must run the WHOLE money path
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE canonical POD-B2 use case named in the pod brief: "a mid-month bonus".
    ///
    /// [DEFECT WITNESS — currently RED] Process succeeds and the journal is sound, but Approve and Lock
    /// are both refused with 422 validation_errors / GOSI_MISSING_FOR_SAUDI, raised as an ERROR by
    /// PayrollValidationEngine.cs:192 (Rule 2). That rule is gated on `isKsa` only — it was never gated
    /// on <c>ctx.Run.IncludesRecurringPay</c> the way Rules 1 and 3 were. A supplemental-basis run zeroes
    /// basic/housing (PayrollController.cs:1513-1519), so a bonus that is not in the GOSI base produces a
    /// covered wage of 0, zero GOSI EE lines, and therefore a blocking Error for every Saudi/GCC national.
    ///
    /// The run is then TERMINAL: Approve 422, Lock 422, re-Process 400 ("cannot be reprocessed"),
    /// DeleteRun 400 ("not_deletable" — Draft only), and no endpoint anywhere sets
    /// PayrollValidationResult.IsResolved, so the Error can never be cleared. The only exit is Void.
    ///
    /// Keep this assertion as written — the requirement is that an off-cycle run Locks with a balanced
    /// journal. See C1b, which is the same run for an expat and passes, localising the cause to Rule 2.
    /// </summary>
    [Fact]
    public async Task C1_OffCycleBonusRun_AlongsideTheRegularRun_ProcessesLocksAndPostsABalancedJournal()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        var emp = await SeedEmployee(db, tid, co.Id, "E001");
        var ctrl = Payroll(db, tid, Maker);

        // The month's Regular run is already done.
        var regular = await CreateRun(db, ctrl, 2026, 6, co.Id);
        await ProcessApproveLock(db, tid, regular.Id);

        // A mid-month performance bonus, paid out of band, injected straight into the run (no accrual).
        var batch = await SeedPendingBatch(db, tid, "2026-06", "BON-OC", inGosiBase: false, (emp, 5_000m, 0m));
        await ApproveBatch(db, tid, batch.Id);

        var offCycle = await CreateRun(db, ctrl, 2026, 6, co.Id, PayrollRunTypes.OffCycle);
        await Include(ctrl, offCycle.Id, "Mid-month performance bonus", emp.Id);
        await ProcessApproveLock(db, tid, offCycle.Id);

        var runGl = await RunGl(db, offCycle.Id);
        runGl.Should().NotBeEmpty("an off-cycle run must post its own accrual journal");
        AssertBalanced(runGl, "an off-cycle run's journal must balance exactly like a regular one");
        NetLiability(runGl, SalariesPayable).Should().Be(5_000m,
            "the bonus is owed to the employee through Salaries Payable");
        AssertBalanced(await AllGl(db, tid), "the whole ledger must stay balanced across both runs");

        // The off-cycle run paid ONLY the bonus — no second salary, no second EMI.
        var slip = await db.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == offCycle.Id);
        slip.BasicSalary.Should().Be(0m, "a supplemental run pays no recurring salary");
        slip.HousingAllowance.Should().Be(0m);
        slip.GrossSalary.Should().Be(5_000m);
        slip.NetSalary.Should().Be(5_000m);

        // …and the Regular run is untouched by it.
        var regularSlip = await db.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == regular.Id);
        regularSlip.BasicSalary.Should().Be(10_000m);
    }

    /// <summary>
    /// Isolates the cause of the C1 failure. Identical run, identical bonus, identical basis — the ONLY
    /// difference is the employee's nationality, which changes nothing about the supplemental machinery
    /// but does change PayrollValidationEngine Rule 2. Green here + red in C1 localises the defect to
    /// Rule 2 alone, and rules out the supplemental emission path, the incremental statutory base and the
    /// bonus clearing as causes.
    /// </summary>
    [Fact]
    public async Task C1b_SupplementalOffCycleRunForAnEXPAT_LocksCleanly_IsolatingTheSaudiRule2Blocker()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        var emp = await SeedEmployee(db, tid, co.Id, "E001", nationality: "IND");
        var ctrl = Payroll(db, tid, Maker);

        var regular = await CreateRun(db, ctrl, 2026, 6, co.Id);
        await ProcessApproveLock(db, tid, regular.Id);

        var batch = await SeedPendingBatch(db, tid, "2026-06", "BON-OC", inGosiBase: false, (emp, 5_000m, 0m));
        await ApproveBatch(db, tid, batch.Id);

        var offCycle = await CreateRun(db, ctrl, 2026, 6, co.Id, PayrollRunTypes.OffCycle);
        await Include(ctrl, offCycle.Id, "Mid-month performance bonus", emp.Id);
        await ProcessApproveLock(db, tid, offCycle.Id);

        var gl = await RunGl(db, offCycle.Id);
        AssertBalanced(gl, "the supplemental journal itself is sound — only Rule 2 blocks the Saudi case");
        NetLiability(gl, SalariesPayable).Should().Be(5_000m);
    }

    [Fact]
    public async Task C2_MissedJoinerOffCycleRun_LocksSettlesAndRemits_ClearingItsControlAccountsToZero()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        await SeedEmployee(db, tid, co.Id, "E001");
        var ctrl = Payroll(db, tid, Maker);

        // The month's Regular run went out without the new joiner.
        var regular = await CreateRun(db, ctrl, 2026, 6, co.Id);
        await ProcessApproveLock(db, tid, regular.Id);

        // The joiner is onboarded afterwards and paid a FULL salary by an off-cycle run — the case the
        // type-derived basis made impossible.
        var joiner = await SeedEmployee(db, tid, co.Id, "E002");
        var offCycle = await CreateRun(db, ctrl, 2026, 6, co.Id, PayrollRunTypes.OffCycle, includesRecurringPay: true);
        offCycle.IncludesRecurringPay.Should().BeTrue("an OffCycle run may opt into a full recurring basis");
        await Include(ctrl, offCycle.Id, "Missed joiner — started 03/06, omitted from the monthly run", joiner.Id);
        await ProcessApproveLock(db, tid, offCycle.Id);

        var accrual = await RunGl(db, offCycle.Id);
        AssertBalanced(accrual, "the off-cycle accrual journal must balance");
        NetLiability(accrual, SalariesPayable).Should().BeGreaterThan(0m);
        NetLiability(accrual, GosiEePayable).Should().BeGreaterThan(0m, "a full-basis KSA run deducts GOSI");
        NetLiability(accrual, GosiErPayable).Should().BeGreaterThan(0m);

        // POD-B1: settle the net pay, remit the statutory. Both must work for a NON-Regular run.
        var batch = await AcceptedBatch(db, tid, offCycle.Id);
        (await Payroll(db, tid, Checker).SettlePaymentBatch(
                batch.Id, new SettlePaymentBatchRequest("WPS-OC-1", new DateOnly(2026, 7, 5)), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await Payroll(db, tid, Checker).RemitStatutory(
                offCycle.Id, new RemitStatutoryRequest("All", "GOSI-OC-1", new DateOnly(2026, 7, 10)), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var gl = await RunGl(db, offCycle.Id);
        AssertBalanced(gl, "accrual + settlement + remittance must balance");
        NetLiability(gl, SalariesPayable).Should().Be(0m, "settlement clears net pay to zero");
        NetLiability(gl, GosiEePayable).Should().Be(0m, "remittance clears the employee GOSI liability");
        NetLiability(gl, GosiErPayable).Should().Be(0m, "remittance clears the employer GOSI liability");
        Credits(gl, CashBank).Should().BeGreaterThan(0m, "cash actually left the bank for this off-cycle run");

        // The Regular run's own controls are unaffected by the off-cycle settlement.
        var regularGl = await RunGl(db, regular.Id);
        NetLiability(regularGl, SalariesPayable).Should().BeGreaterThan(0m,
            "settling the off-cycle batch must not clear the Regular run's liability");
    }

    [Fact]
    public async Task C3_OffCycleRun_IsStillRefusedByThePeriodCloseGuard_AndPostsNothingWhenRefused()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        var emp = await SeedEmployee(db, tid, co.Id, "E001");
        var ctrl = Payroll(db, tid, Maker);

        var offCycle = await CreateRun(db, ctrl, 2026, 6, co.Id, PayrollRunTypes.OffCycle, includesRecurringPay: true);
        await Include(ctrl, offCycle.Id, "Late starter", emp.Id);
        (await Payroll(db, tid, Maker).Process(offCycle.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await Payroll(db, tid, Checker).Approve(offCycle.Id, new PayrollDecisionRequest("ok"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        (await Gl(db, tid).ClosePeriod("2026-06", co.Id, new GlPeriodActionRequest("month-end freeze"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var blocked = await Payroll(db, tid, Checker).Lock(offCycle.Id, CancellationToken.None);
        blocked.Should().BeOfType<UnprocessableEntityObjectResult>(
            "a closed period must refuse an off-cycle accrual exactly as it refuses a regular one");
        JsonSerializer.Serialize(((UnprocessableEntityObjectResult)blocked).Value).Should().Contain("gl_period_closed");
        (await RunGl(db, offCycle.Id)).Should().BeEmpty("a refused Lock must post nothing");
        (await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == offCycle.Id)).Status
            .Should().Be("Approved", "a refused Lock must not advance the run");
        db.ChangeTracker.Clear();

        (await Gl(db, tid).ReopenPeriod("2026-06", co.Id, new GlPeriodActionRequest("off-cycle correction window"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await Payroll(db, tid, Checker).Lock(offCycle.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        AssertBalanced(await RunGl(db, offCycle.Id), "the journal posted after the reopen must balance");
    }

    [Fact]
    public async Task C4_OffCycleSettlementAndRemittance_AreAlsoRefusedByAClosedPERIODOfTheirOwn()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        var emp = await SeedEmployee(db, tid, co.Id, "E001");
        var ctrl = Payroll(db, tid, Maker);

        var offCycle = await CreateRun(db, ctrl, 2026, 6, co.Id, PayrollRunTypes.OffCycle, includesRecurringPay: true);
        await Include(ctrl, offCycle.Id, "Late starter", emp.Id);
        await ProcessApproveLock(db, tid, offCycle.Id);
        var batch = await AcceptedBatch(db, tid, offCycle.Id);

        // The PAYMENT month (July) is closed — the settlement dates into it, not into the pay period.
        (await Gl(db, tid).ClosePeriod("2026-07", co.Id, new GlPeriodActionRequest("July frozen"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var settle = await Payroll(db, tid, Checker).SettlePaymentBatch(
            batch.Id, new SettlePaymentBatchRequest("REF", new DateOnly(2026, 7, 5)), CancellationToken.None);
        settle.Should().BeOfType<UnprocessableEntityObjectResult>();
        JsonSerializer.Serialize(((UnprocessableEntityObjectResult)settle).Value).Should().Contain("gl_period_closed");
        db.ChangeTracker.Clear();

        var remit = await Payroll(db, tid, Checker).RemitStatutory(
            offCycle.Id, new RemitStatutoryRequest("All", "REF", new DateOnly(2026, 7, 10)), CancellationToken.None);
        remit.Should().BeOfType<UnprocessableEntityObjectResult>();
        JsonSerializer.Serialize(((UnprocessableEntityObjectResult)remit).Value).Should().Contain("gl_period_closed");
        db.ChangeTracker.Clear();

        var gl = await RunGl(db, offCycle.Id);
        gl.Should().OnlyContain(l => l.EventType == Zayra.Api.Models.GlEventTypes.Accrual,
            "neither refused posting may have written anything");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  (d) POD-B1b — a bonus accrual clears EXACTLY once
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task D1_BonusPaidByAnOffCycleRun_ClearsTheAccrualExactlyOnce_AndTheLaterRegularRunCannotClearItAgain()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        var emp = await SeedEmployee(db, tid, co.Id, "E001");
        var ctrl = Payroll(db, tid, Maker);

        // Finance approves the bonus batch → DR 6100 expense / CR 2300 payable, at GROSS.
        var batch = await SeedPendingBatch(db, tid, "2026-06", "BON-1", inGosiBase: true, (emp, 5_000m, 0m));
        await ApproveBatch(db, tid, batch.Id);

        var afterApprove = await AllGl(db, tid);
        Debits(afterApprove, BonusExpense).Should().Be(5_000m, "approval recognises the expense once");
        NetLiability(afterApprove, BonusPayable).Should().Be(5_000m, "…against Bonus Payable");

        // An OFF-CYCLE run pays it mid-month.
        var offCycle = await CreateRun(db, ctrl, 2026, 6, co.Id, PayrollRunTypes.OffCycle);
        await Include(ctrl, offCycle.Id, "Mid-month contractual bonus", emp.Id);
        await ProcessApproveLock(db, tid, offCycle.Id);

        var afterOffCycle = await AllGl(db, tid);
        AssertBalanced(afterOffCycle, "the off-cycle journal must balance");
        Debits(afterOffCycle, BonusExpense).Should().Be(5_000m,
            "the off-cycle run PAYS the bonus — it must not expense it a second time");
        NetLiability(afterOffCycle, BonusPayable).Should().Be(0m,
            "the off-cycle run must clear the accrual it is paying");
        afterOffCycle.Count(l => l.EventType == Zayra.Api.Models.GlEventTypes.BonusPayrollClearing)
            .Should().Be(1, "exactly one clearing line per accrual");

        var consumed = await db.EmployeeBonuses.AsNoTracking().SingleAsync(b => b.BonusBatchId == batch.Id);
        consumed.PayrollRunId.Should().Be(offCycle.Id);
        consumed.Status.Should().Be("PaidInPayroll");

        // …and now the month's REGULAR run goes out. It must not touch the bonus at all.
        var regular = await CreateRun(db, ctrl, 2026, 6, co.Id);
        await ProcessApproveLock(db, tid, regular.Id);

        var regularEarnings = await db.PayrollEarnings.AsNoTracking()
            .Where(e => e.PayrollRunId == regular.Id).ToListAsync();
        regularEarnings.Should().NotContain(e => e.Source == "Bonus",
            "the bonus was already consumed by the off-cycle run and must not be paid twice");

        var final = await AllGl(db, tid);
        AssertBalanced(final, "the ledger must balance across bonus approval + both runs");
        Debits(final, BonusExpense).Should().Be(5_000m,
            "THE control: the bonus expense is recognised exactly once, whichever run pays it");
        NetLiability(final, BonusPayable).Should().Be(0m,
            "the payable is cleared exactly once — never twice, never left orphaned");
        final.Count(l => l.EventType == Zayra.Api.Models.GlEventTypes.BonusPayrollClearing)
            .Should().Be(1, "a later Regular run must not re-clear an already-cleared accrual");
    }

    [Fact]
    public async Task D2_ASecondOffCycleRun_CannotReClearAnAlreadyConsumedBonusAccrual()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        var emp = await SeedEmployee(db, tid, co.Id, "E001");
        var ctrl = Payroll(db, tid, Maker);

        var batch = await SeedPendingBatch(db, tid, "2026-06", "BON-1", inGosiBase: true, (emp, 5_000m, 0m));
        await ApproveBatch(db, tid, batch.Id);

        var first = await CreateRun(db, ctrl, 2026, 6, co.Id, PayrollRunTypes.OffCycle);
        await Include(ctrl, first.Id, "Bonus payment", emp.Id);
        await ProcessApproveLock(db, tid, first.Id);

        // A second off-cycle run for the same employee and period finds nothing left to pay.
        var second = await CreateRun(db, ctrl, 2026, 6, co.Id, PayrollRunTypes.OffCycle);
        await Include(ctrl, second.Id, "Operator repeated the off-cycle run by mistake", emp.Id);
        var res = await Payroll(db, tid, Maker).Process(second.Id, CancellationToken.None);
        db.ChangeTracker.Clear();

        // Either it refuses (nothing to pay) or it produces a zero-value slip — but under NO circumstance
        // may it consume the bonus or move the payable a second time.
        (await db.PayrollEarnings.AsNoTracking().Where(e => e.PayrollRunId == second.Id && e.Source == "Bonus")
            .ToListAsync()).Should().BeEmpty("the bonus is already stamped to the first run");
        var gl = await AllGl(db, tid);
        Debits(gl, BonusExpense).Should().Be(5_000m);
        NetLiability(gl, BonusPayable).Should().Be(0m);
        gl.Count(l => l.EventType == Zayra.Api.Models.GlEventTypes.BonusPayrollClearing).Should().Be(1);
        res.Should().NotBeNull();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  (e) The hold-out selector
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task E1_HoldOut_ExcludesOnlyTheNamedEmployee_RecordsTheReason_AndIsHashChainAudited()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        var a = await SeedEmployee(db, tid, co.Id, "E001");
        var b = await SeedEmployee(db, tid, co.Id, "E002");
        var c = await SeedEmployee(db, tid, co.Id, "E003");
        var ctrl = Payroll(db, tid, Maker);

        const string reason = "Under investigation — salary held per HR directive HR-2026-114";
        var run = await CreateRun(db, ctrl, 2026, 6, co.Id);
        await Exclude(ctrl, run.Id, reason, b.Id);

        // The reason is persisted…
        var selection = await db.PayrollRunEmployeeSelections.AsNoTracking()
            .SingleAsync(s => s.PayrollRunId == run.Id);
        selection.EmployeeId.Should().Be(b.Id);
        selection.Mode.Should().Be(PayrollRunSelectionModes.Exclude);
        selection.Reason.Should().Be(reason);
        selection.CompanyId.Should().Be(co.Id, "the selection is company-scoped like the run");

        // …and AUDITED through the tamper-evident POD-A3 chain.
        var selectionAudit = await Audit(db, tid, "payroll.run.selection.changed");
        selectionAudit.Should().ContainSingle();
        selectionAudit[0].EntityId.Should().Be(run.Id.ToString());
        selectionAudit[0].EntryHash.Should().NotBeNullOrEmpty("the hold-out audit row must be sealed");
        selectionAudit[0].Seq.Should().BeGreaterThan(0);
        var meta = Meta(selectionAudit[0]);
        meta.GetProperty("reason").GetString().Should().Be(reason);
        meta.GetProperty("mode").GetString().Should().Be("Exclude");
        meta.GetProperty("employeeIds").EnumerateArray().Select(x => x.GetInt32()).Should().Equal(b.Id);

        // The preview names exactly who is in and who is out, with the reason.
        var preview = (OkObjectResult)await ctrl.GetRunPopulation(run.Id, CancellationToken.None);
        var previewRoot = JsonDocument.Parse(JsonSerializer.Serialize(preview.Value)).RootElement;
        previewRoot.GetProperty("eligibleCount").GetInt32().Should().Be(3);
        previewRoot.GetProperty("includedCount").GetInt32().Should().Be(2);
        previewRoot.GetProperty("excludedCount").GetInt32().Should().Be(1);
        previewRoot.GetProperty("included").EnumerateArray()
            .Select(x => x.GetProperty("code").GetString()).Should().BeEquivalentTo(new[] { "E001", "E003" });
        var excludedPreview = previewRoot.GetProperty("excluded").EnumerateArray().Single();
        excludedPreview.GetProperty("employeeId").GetInt32().Should().Be(b.Id);
        excludedPreview.GetProperty("code").GetString().Should().Be("E002");
        excludedPreview.GetProperty("reason").GetString().Should().Be(reason,
            "the preview must carry the operator's exact recorded reason");
        db.ChangeTracker.Clear();

        // Process honours it: only the named employee is held out.
        (await Payroll(db, tid, Maker).Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var paid = await db.PayrollSlips.AsNoTracking().Where(s => s.RunId == run.Id).Select(s => s.EmployeeId).ToListAsync();
        paid.Should().BeEquivalentTo(new[] { a.Id, c.Id }, "only the named employee is excluded — nobody else");
        paid.Should().NotContain(b.Id);
        (await db.PayrollRunEmployeeSelections.AsNoTracking().SingleAsync(s => s.PayrollRunId == run.Id))
            .Outcome.Should().Be(PayrollRunSelectionOutcomes.Excluded, "Process must stamp the resolved outcome");

        // The exclusion is REPORTED — never silently dropped.
        var results = await db.PayrollValidationResults.AsNoTracking().Where(r => r.PayrollRunId == run.Id).ToListAsync();
        var reported = results.Should().ContainSingle(r => r.Code == "EMPLOYEE_EXCLUDED_FROM_RUN").Subject;
        reported.EmployeeId.Should().Be(b.Id);
        reported.Message.Should().Contain("E002").And.Contain(reason);

        var processAudit = await Audit(db, tid, "payroll.run.processed");
        Meta(processAudit[0]).GetProperty("excludedCount").GetInt32().Should().Be(1);
        Meta(processAudit[0]).GetProperty("eligibleCount").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task E2_HoldOut_MustBeAcknowledgedAtApprove_AndIsRestatedAtLockAndPayment()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        var a = await SeedEmployee(db, tid, co.Id, "E001");
        var b = await SeedEmployee(db, tid, co.Id, "E002");
        var ctrl = Payroll(db, tid, Maker);

        var run = await CreateRun(db, ctrl, 2026, 6, co.Id);
        await Exclude(ctrl, run.Id, "Resigned mid-month; final settlement handled separately", b.Id);
        (await Payroll(db, tid, Maker).Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        // An approver who does not state the hold-out count is refused.
        var unacknowledged = await Payroll(db, tid, Checker, "checker")
            .Approve(run.Id, new PayrollDecisionRequest("looks fine"), CancellationToken.None);
        unacknowledged.Should().BeOfType<ConflictObjectResult>(
            "a deliberate hold-out must be acknowledged like a cash count, not merely warned about");
        JsonSerializer.Serialize(((ConflictObjectResult)unacknowledged).Value)
            .Should().Contain("excluded_employees_not_acknowledged").And.Contain("\"excludedCount\":1");
        db.ChangeTracker.Clear();

        var wrongCount = await Payroll(db, tid, Checker, "checker")
            .Approve(run.Id, new PayrollDecisionRequest("ok", 2), CancellationToken.None);
        wrongCount.Should().BeOfType<ConflictObjectResult>("a mismatched count is not an acknowledgement");
        db.ChangeTracker.Clear();

        (await Payroll(db, tid, Checker, "checker").Approve(run.Id, new PayrollDecisionRequest("ok", 1), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        // Lock restates it…
        var locked = (OkObjectResult)await Payroll(db, tid, Checker).Lock(run.Id, CancellationToken.None);
        JsonSerializer.Serialize(locked.Value).Should().Contain("\"excludedCount\":1");
        db.ChangeTracker.Clear();
        Meta((await Audit(db, tid, "payroll.run.locked"))[0]).GetProperty("excludedCount").GetInt32().Should().Be(1);

        // …and so does the payment batch, which contains ONLY the paid employee.
        var created = (CreatedResult)await Payroll(db, tid, Checker)
            .CreatePaymentBatch(run.Id, new PayrollPaymentBatchRequest("WPS", "SAR"), CancellationToken.None);
        JsonSerializer.Serialize(created.Value).Should().Contain("\"excludedCount\":1");
        db.ChangeTracker.Clear();

        var batch = await db.PayrollPaymentBatches.AsNoTracking().SingleAsync(x => x.PayrollRunId == run.Id);
        var records = await db.PayrollPaymentRecords.AsNoTracking().Where(r => r.PaymentBatchId == batch.Id).ToListAsync();
        records.Should().ContainSingle().Which.EmployeeId.Should().Be(a.Id,
            "the held-out employee must never appear in the payment file");

        // The excluded employee cost the ledger nothing.
        var gl = await RunGl(db, run.Id);
        AssertBalanced(gl, "a run with a hold-out must still post a balanced journal");
        var aSlip = await db.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == run.Id);
        NetLiability(gl, SalariesPayable).Should().Be(aSlip.NetSalary,
            "net pay accrued must equal exactly the included employees' net");
    }

    [Fact]
    public async Task E3_SelectorGuards_ReasonIsMandatory_RegularRunsAreDenyListOnly_AndLockedPopulationsAreFrozen()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        var a = await SeedEmployee(db, tid, co.Id, "E001");
        var ctrl = Payroll(db, tid, Maker);

        var run = await CreateRun(db, ctrl, 2026, 6, co.Id);

        var noReason = await ctrl.UpsertRunSelection(run.Id,
            new PayrollRunSelectionRequest("Exclude", "   ", new List<int> { a.Id }), CancellationToken.None);
        noReason.Should().BeOfType<BadRequestObjectResult>("a hold-out without a documented reason is not auditable");
        JsonSerializer.Serialize(((BadRequestObjectResult)noReason).Value).Should().Contain("reason_required");

        var includeOnRegular = await ctrl.UpsertRunSelection(run.Id,
            new PayrollRunSelectionRequest("Include", "just this one", new List<int> { a.Id }), CancellationToken.None);
        includeOnRegular.Should().BeOfType<BadRequestObjectResult>(
            "an allow-list on the monthly run would silently unpay the company");

        (await db.PayrollRunEmployeeSelections.AsNoTracking().CountAsync(s => s.PayrollRunId == run.Id))
            .Should().Be(0, "a refused selector call must write nothing");

        // Once the run has produced payslips the population is frozen.
        (await Payroll(db, tid, Maker).Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        var frozen = await Payroll(db, tid, Maker).UpsertRunSelection(run.Id,
            new PayrollRunSelectionRequest("Exclude", "changed my mind", new List<int> { a.Id }), CancellationToken.None);
        frozen.Should().BeOfType<ConflictObjectResult>();
        JsonSerializer.Serialize(((ConflictObjectResult)frozen).Value).Should().Contain("population_locked");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  (f) Voiding a non-Regular run
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task F1_VoidingAnOffCycleRun_ContraReversesEveryLine_ReopensTheBonusPayable_AndCanBeRepaid()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        var emp = await SeedEmployee(db, tid, co.Id, "E001");
        var ctrl = Payroll(db, tid, Maker);

        var batch = await SeedPendingBatch(db, tid, "2026-06", "BON-1", inGosiBase: true, (emp, 5_000m, 0m));
        await ApproveBatch(db, tid, batch.Id);

        var offCycle = await CreateRun(db, ctrl, 2026, 6, co.Id, PayrollRunTypes.OffCycle);
        await Include(ctrl, offCycle.Id, "Mid-month contractual bonus", emp.Id);
        await ProcessApproveLock(db, tid, offCycle.Id);

        var beforeVoid = await RunGl(db, offCycle.Id);
        beforeVoid.Should().NotBeEmpty();
        var originalLineCount = beforeVoid.Count;

        var voided = await Payroll(db, tid, Checker)
            .VoidRun(offCycle.Id, new PayrollDecisionRequest("wrong bonus batch selected"), CancellationToken.None);
        voided.Should().BeOfType<OkObjectResult>();
        JsonSerializer.Serialize(((OkObjectResult)voided).Value)
            .Should().Contain($"\"glEntriesReversed\":{originalLineCount}");
        db.ChangeTracker.Clear();

        var afterVoid = await RunGl(db, offCycle.Id);
        afterVoid.Should().HaveCount(originalLineCount * 2, "every original line gets exactly one contra");
        afterVoid.Count(l => l.IsReversed).Should().Be(originalLineCount);
        afterVoid.Where(l => l.EventType == Zayra.Api.Models.GlEventTypes.Void)
            .Should().OnlyContain(l => l.Period == "2026-06", "the contra must land in the accrual's own period");
        AssertBalanced(afterVoid, "accrual + contra must net flat");
        NetLiability(afterVoid, SalariesPayable).Should().Be(0m, "the net-pay liability is fully reversed");

        // The bonus payable is RE-OPENED (the money was never paid) and the expense still stands once.
        var gl = await AllGl(db, tid);
        AssertBalanced(gl, "the whole ledger stays balanced through the void");
        Debits(gl, BonusExpense).Should().Be(5_000m, "the accrual's expense is untouched by the void");
        NetLiability(gl, BonusPayable).Should().Be(5_000m,
            "voiding the run that was paying the bonus must re-open the payable");

        var bonus = await db.EmployeeBonuses.AsNoTracking().SingleAsync(x => x.BonusBatchId == batch.Id);
        bonus.Status.Should().Be("Approved", "the bonus must be re-payable, not lost");
        bonus.PayrollRunId.Should().BeNull();
        (await db.PayrollSlips.AsNoTracking().Where(s => s.RunId == offCycle.Id).ToListAsync())
            .Should().OnlyContain(s => s.Status == "Voided");

        // A replacement off-cycle run can now pay it — and the payable clears exactly once again.
        var replacement = await CreateRun(db, ctrl, 2026, 6, co.Id, PayrollRunTypes.OffCycle);
        await Include(ctrl, replacement.Id, "Re-run after the void", emp.Id);
        await ProcessApproveLock(db, tid, replacement.Id);

        var finalGl = await AllGl(db, tid);
        AssertBalanced(finalGl, "void → re-run must leave the ledger balanced");
        Debits(finalGl, BonusExpense).Should().Be(5_000m, "still expensed exactly once across the whole lifecycle");
        NetLiability(finalGl, BonusPayable).Should().Be(0m, "the replacement run clears the re-opened accrual");
    }

    /// <summary>
    /// [DEFECT WITNESS — currently RED, SAME ROOT CAUSE AS C1] Blocked at Approve by
    /// GOSI_MISSING_FOR_SAUDI. Included because it proves the blocker is NOT bonus-specific: this run pays
    /// a pure ADJUSTMENT (arrears), which is the other supplemental family, and is refused identically.
    /// The M7 GlPostingPeriod behaviour it is meant to cover is therefore currently unreachable through
    /// the API for a Saudi workforce. Assertions left intact so this turns green with the Rule 2 fix.
    /// </summary>
    [Fact]
    public async Task F2_VoidingACorrectionRunBookedIntoALaterGlPeriod_ContrasIntoThatSamePeriod()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        var emp = await SeedEmployee(db, tid, co.Id, "E001");
        var ctrl = Payroll(db, tid, Maker);

        var parent = await CreateRun(db, ctrl, 2026, 6, co.Id);
        await ProcessApproveLock(db, tid, parent.Id);

        // A June correction, booked into the still-open July books.
        var correction = await CreateRun(db, ctrl, 2026, 6, co.Id, PayrollRunTypes.Correction,
            parentRunId: parent.Id, glPostingPeriod: "2026-07");
        correction.ParentRunId.Should().Be(parent.Id);
        correction.GlPostingPeriod.Should().Be("2026-07");

        db.PayrollAdjustments.Add(new PayrollAdjustment
        {
            TenantId = tid, PayrollRunId = correction.Id, EmployeeId = emp.Id,
            AdjustmentType = "Arrears", Amount = 750m, Reason = "June allowance keyed late", Status = "Approved",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Include(ctrl, correction.Id, "June arrears correction", emp.Id);
        await ProcessApproveLock(db, tid, correction.Id);

        var accrual = await RunGl(db, correction.Id);
        accrual.Should().NotBeEmpty();
        accrual.Should().OnlyContain(l => l.Period == "2026-07",
            "a correction may report under June while its journal lands in the open July period");
        AssertBalanced(accrual, "the correction journal must balance");

        (await Payroll(db, tid, Checker).VoidRun(correction.Id, new PayrollDecisionRequest("arrears figure wrong"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var afterVoid = await RunGl(db, correction.Id);
        afterVoid.Should().OnlyContain(l => l.Period == "2026-07",
            "reversing into the PAY period would leave both months permanently unbalanced");
        AssertBalanced(afterVoid, "the correction and its contra must net flat inside 2026-07");
        NetLiability(afterVoid, SalariesPayable).Should().Be(0m);

        // The parent June run is untouched.
        var parentGl = await RunGl(db, parent.Id);
        parentGl.Should().OnlyContain(l => l.Period == "2026-06" && !l.IsReversed);
        NetLiability(parentGl, SalariesPayable).Should().BeGreaterThan(0m);
    }

    [Fact]
    public async Task F3_VoidingAParentRun_ReportsTheCorrectionRunsItOrphans_WithoutCascading()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        await SeedEmployee(db, tid, co.Id, "E001");
        var ctrl = Payroll(db, tid, Maker);

        var parent = await CreateRun(db, ctrl, 2026, 6, co.Id);
        await ProcessApproveLock(db, tid, parent.Id);
        var child = await CreateRun(db, ctrl, 2026, 6, co.Id, PayrollRunTypes.Correction, parentRunId: parent.Id);

        var res = (OkObjectResult)await Payroll(db, tid, Checker)
            .VoidRun(parent.Id, new PayrollDecisionRequest("re-run required"), CancellationToken.None);
        JsonSerializer.Serialize(res.Value).Should().Contain(child.Id.ToString(),
            "the operator must be told which correction runs they have just orphaned");
        db.ChangeTracker.Clear();

        (await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == child.Id)).Status
            .Should().Be("Draft", "cascade recovery of a correction chain is POD-B3, not B2");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Guards that must NOT have been weakened
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task G1_LockIsIdempotent_OnANonRegularRun_AndNeverPostsTheJournalTwice()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        var emp = await SeedEmployee(db, tid, co.Id, "E001");
        var ctrl = Payroll(db, tid, Maker);

        var offCycle = await CreateRun(db, ctrl, 2026, 6, co.Id, PayrollRunTypes.OffCycle, includesRecurringPay: true);
        await Include(ctrl, offCycle.Id, "Missed joiner", emp.Id);
        await ProcessApproveLock(db, tid, offCycle.Id);
        var firstCount = (await RunGl(db, offCycle.Id)).Count;

        // Force the run back to Approved and re-lock: the alreadyPosted probe must suppress a second journal.
        var tracked = await db.PayrollRuns.FirstAsync(r => r.Id == offCycle.Id);
        tracked.Status = "Approved";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        (await Payroll(db, tid, Checker).Lock(offCycle.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await RunGl(db, offCycle.Id)).Count.Should().Be(firstCount, "a re-lock must post nothing new");
    }

    [Fact]
    public async Task G2_SelectorAndPopulation_AreTenantScoped()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var otherTid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        var emp = await SeedEmployee(db, tid, co.Id, "E001");
        var run = await CreateRun(db, Payroll(db, tid, Maker), 2026, 6, co.Id, PayrollRunTypes.OffCycle);

        var intruder = Payroll(db, otherTid, Guid.NewGuid(), "intruder");
        (await intruder.UpsertRunSelection(run.Id,
                new PayrollRunSelectionRequest("Include", "not mine", new List<int> { emp.Id }), CancellationToken.None))
            .Should().BeOfType<NotFoundResult>("a run in another tenant must not be reachable");
        (await intruder.GetRunPopulation(run.Id, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        (await intruder.DeleteRunSelection(run.Id, emp.Id, CancellationToken.None)).Should().BeOfType<NotFoundResult>();

        (await db.PayrollRunEmployeeSelections.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task G3_NonRegularRunWithoutAnExplicitPopulation_IsRefused_AndAVoidedRunCannotBeProcessed()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await SeedCompany(db, tid);
        await SeedEmployee(db, tid, co.Id, "E001");
        var ctrl = Payroll(db, tid, Maker);

        var offCycle = await CreateRun(db, ctrl, 2026, 6, co.Id, PayrollRunTypes.OffCycle);
        var noPopulation = await Payroll(db, tid, Maker).Process(offCycle.Id, CancellationToken.None);
        noPopulation.Should().BeOfType<UnprocessableEntityObjectResult>(
            "an off-cycle run over the whole company would consume the period's bonuses out from under the regular run");
        JsonSerializer.Serialize(((UnprocessableEntityObjectResult)noPopulation).Value).Should().Contain("run_population_required");
        (await db.PayrollSlips.AsNoTracking().CountAsync(s => s.RunId == offCycle.Id)).Should().Be(0);
        db.ChangeTracker.Clear();

        (await Payroll(db, tid, Checker).VoidRun(offCycle.Id, new PayrollDecisionRequest("abandoned"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var resurrect = await Payroll(db, tid, Maker).Process(offCycle.Id, CancellationToken.None);
        resurrect.Should().BeOfType<BadRequestObjectResult>("a voided run must never be resurrectable");
        JsonSerializer.Serialize(((BadRequestObjectResult)resurrect).Value).Should().Contain("run_voided");
    }
}

// ── Test doubles (file-scoped) ─────────────────────────────────────────────────

file static class _B2SmeRules
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

file sealed class _B2SmeScope : Zayra.Api.Application.Common.IDataScopeService
{
    public Task<Zayra.Api.Application.Common.DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Application.Common.DataScope
        { Level = Zayra.Api.Application.Common.DataScopeLevel.Organization, AllowedEmployeeIds = null });
}

file sealed class _B2SmeHttp : IHttpContextAccessor
{
    public _B2SmeHttp(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _B2SmeNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _B2SmeKsaResolver : ICountryPackResolver
{
    private static readonly KsaDeductionCalculator _calc = new(_B2SmeRules.Rules);
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => cc == "SAU" ? _calc : new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class _B2SmeLetters : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class _B2SmeDocs : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument(file.FileName, file.ContentType, "storage/test", "/tmp/test"));
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public string ResolvePath(string storageUrl) => "/tmp/test";
}
