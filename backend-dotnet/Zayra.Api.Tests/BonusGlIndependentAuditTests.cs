using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
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
/// POD-B1b — INDEPENDENT audit of bonus double-count + company-first GL routing.
///
/// Written against the requirement, not the implementation: every assertion is a hand-worked CFO
/// number derived from the seeded facts, and account matching is on the account CODE PREFIX
/// (<c>"2300 "</c>), never a substring, so a line posted to 12300 or 23000 can never satisfy a 2300
/// assertion.
///
/// Seed arithmetic used throughout (KSA pack, Saudi national, basic 10,000 / housing 2,000 /
/// transport 1,000; GOSI covered wage = basic + housing = 12,000):
///   salary earnings DR 13,000 | GOSI EE 1,170 (2101) | GOSI ER 1,410 (2106) | ER expense 1,410 (5101)
/// A 5,000 bonus with GCC tax 0 rides on top of that.
///
/// Proves:
///   (a) approve → payroll → lock → settle → remit: bonus expense DEBITED EXACTLY ONCE (total AND
///       line count), Bonus Payable back to ZERO, whole ledger balanced, cash carries the real outflow;
///   (b) approved but NOT paid through payroll: balanced, payable legitimately OUTSTANDING, and an
///       unrelated payroll run for another period does not touch it;
///   (c) voiding a run that paid a bonus contra-reverses everything and RE-OPENS the payable — proven
///       both on the raw ledger and through the production sub-ledger (BonusGlLedger);
///   (d) two-company tenant: bonus AND loan AND advance post to the COMPANY's mapped account in the
///       COMPANY's currency, stamped with the COMPANY's id;
///   (e) a closed period blocks bonus/loan/advance posting PER COMPANY (and a group close blocks all).
/// </summary>
public class BonusGlIndependentAuditTests
{
    // ── Ledger helpers ──────────────────────────────────────────────────────────
    // Account labels are persisted as "<code> - <name>". Match on the CODE token only: a prefix
    // test ("2300 ") cannot be satisfied by 23000 or 12300, which a .Contains() test would accept.

    private static bool IsAcct(string? label, string code) =>
        !string.IsNullOrEmpty(label) && label.StartsWith(code + " ", StringComparison.Ordinal);

    private static decimal Dr(IEnumerable<FinanceGlEntry> gl, string code) =>
        gl.Where(l => IsAcct(l.DebitAccount, code)).Sum(l => l.Amount);

    private static decimal Cr(IEnumerable<FinanceGlEntry> gl, string code) =>
        gl.Where(l => IsAcct(l.CreditAccount, code)).Sum(l => l.Amount);

    /// <summary>Carrying balance of a liability account: Σ CR − Σ DR. Zero ⇒ fully cleared.
    /// Contras are real postings and are included; IsReversed is an audit link, not a balance filter.</summary>
    private static decimal Liability(IEnumerable<FinanceGlEntry> gl, string code) => Cr(gl, code) - Dr(gl, code);

    /// <summary>Carrying balance of an asset account: Σ DR − Σ CR.</summary>
    private static decimal Asset(IEnumerable<FinanceGlEntry> gl, string code) => Dr(gl, code) - Cr(gl, code);

    private static void AssertBalanced(IEnumerable<FinanceGlEntry> gl, string because)
    {
        var list = gl.ToList();
        var dr = list.Where(l => !string.IsNullOrEmpty(l.DebitAccount)).Sum(l => l.Amount);
        var cr = list.Where(l => !string.IsNullOrEmpty(l.CreditAccount)).Sum(l => l.Amount);
        dr.Should().Be(cr, because);
    }

    private static Task<List<FinanceGlEntry>> Ledger(ZayraDbContext db, Guid tid) =>
        db.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking().Where(x => x.TenantId == tid).ToListAsync();

    // ── Harness ─────────────────────────────────────────────────────────────────

    private static (ZayraDbContext db, SqliteConnection conn) NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static DefaultHttpContext Ctx(Guid tid) => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(new List<Claim>
        {
            new("tenant_id", tid.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "audit-user"),
            new("permission", "payroll.lock"),
            new("permission", "payroll.export"),
            new("permission", "payroll.write"),
            new("permission", "payroll.approve"),
            new("permission", "loans.write"),
            new("permission", "finance.gl.manage"),
            new("permission", "finance.gl.read"),
        }, "test")),
    };

    private static PayrollController Payroll(ZayraDbContext db, Guid tid)
    {
        var http = Ctx(tid);
        return new PayrollController(
            db, new _AudScope(), new _AudHttp(http), new _AudNotifications(),
            new _AudKsaResolver(), _AudRules.Rules, new _AudLetters(), new _AudDocs(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(4))
        { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private static BonusesController Bonuses(ZayraDbContext db, Guid tid)
    {
        var http = Ctx(tid);
        return new BonusesController(db, new _AudScope())
        { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private static LoansController Loans(ZayraDbContext db, Guid tid)
    {
        var http = Ctx(tid);
        return new LoansController(db, new _AudScope())
        { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private static AdvancesController Advances(ZayraDbContext db, Guid tid)
    {
        var http = Ctx(tid);
        return new AdvancesController(db, new _AudScope())
        { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private static FinanceGlController Gl(ZayraDbContext db, Guid tid)
    {
        var http = Ctx(tid);
        return new FinanceGlController(db)
        { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    // ── Seed ────────────────────────────────────────────────────────────────────

    private static async Task<Company> Co(ZayraDbContext db, Guid tid, string name, string currency = "SAR")
    {
        var c = new Company
        {
            TenantId = tid, LegalNameEn = name, CountryCode = "SAU",
            Jurisdiction = "KSA-mainland", IsActive = true, DefaultCurrency = currency,
        };
        db.Companies.Add(c);
        await db.SaveChangesAsync();
        return c;
    }

    private static async Task<Employee> Emp(
        ZayraDbContext db, Guid tid, Guid companyId, string code, string status = "Active")
    {
        var structure = await db.SalaryStructures.FirstOrDefaultAsync(s => s.TenantId == tid);
        if (structure is null)
        {
            structure = new SalaryStructure
            {
                TenantId = tid, Code = "STR", Name = "Base", Currency = "SAR",
                EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
            };
            db.SalaryStructures.Add(structure);
            await db.SaveChangesAsync();
        }
        var e = new Employee
        {
            TenantId = tid, CompanyId = companyId, EmployeeCode = code, FullName = $"Emp {code}",
            Status = status, JoiningDate = new DateTime(2023, 1, 1),
            WorkEmail = $"{code}@audit.test", Nationality = "SAU", ContractType = "Indefinite",
        };
        db.Employees.Add(e);
        await db.SaveChangesAsync();
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tid, EmployeeId = e.Id, SalaryStructureId = structure.Id,
            BasicSalary = 10_000m, HousingAllowance = 2_000m, TransportAllowance = 1_000m,
            EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
        });
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
        {
            TenantId = tid, EmployeeId = e.Id,
            Iban = "SA4420000001234567891234", MolId = $"MOL-{code}", SalaryCurrency = "SAR",
        });
        await db.SaveChangesAsync();
        return e;
    }

    private static async Task<PayrollRun> Run(ZayraDbContext db, Guid tid, Guid companyId, int year, int month)
    {
        var r = new PayrollRun
        {
            TenantId = tid, CompanyId = companyId, Year = year, Month = month,
            Status = "Draft", TotalNetSalary = 0m, TotalGrossSalary = 0m,
        };
        db.PayrollRuns.Add(r);
        await db.SaveChangesAsync();
        return r;
    }

    /// <summary>PendingApproval batch whose children carry (gross, tax) — ready for the REAL
    /// ApproveBatch endpoint, so the accrual under test is the production one. CompanyId is set
    /// explicitly here because the unit-test DbContext has no HttpContext and therefore does not run
    /// the server-side company stamping (ZayraDbContext.EnforceCompanyScopeOnWritesAsync).</summary>
    private static async Task<BonusBatch> Batch(
        ZayraDbContext db, Guid tid, string period, string number,
        params (Employee Emp, decimal Gross, decimal Tax)[] children)
    {
        var type = await db.BonusTypes.FirstOrDefaultAsync(t => t.TenantId == tid);
        if (type is null)
        {
            type = new BonusType
            {
                TenantId = tid, Code = "PERF", NameEn = "Performance",
                IsIncludedInGosiBase = false, IsIncludedInWps = false, IsIncludedInEosb = false,
                TaxRegion = "GCC", IsActive = true,
            };
            db.BonusTypes.Add(type);
            await db.SaveChangesAsync();
        }
        var b = new BonusBatch
        {
            TenantId = tid, BonusTypeId = type.Id, BonusTypeName = type.NameEn,
            BatchNumber = number, BatchName = $"Batch {number}", PaymentPeriod = period,
            PaymentDate = new DateOnly(2026, 6, 25), Status = "PendingApproval",
            EmployeeCount = children.Length,
            TotalAmount = children.Sum(c => c.Gross - c.Tax),
        };
        db.BonusBatches.Add(b);
        await db.SaveChangesAsync();
        foreach (var (emp, gross, tax) in children)
            db.EmployeeBonuses.Add(new EmployeeBonus
            {
                TenantId = tid, CompanyId = emp.CompanyId, BonusBatchId = b.Id,
                EmployeeId = Guid.NewGuid(), EmployeeIntId = emp.Id, EmployeeName = emp.FullName,
                BonusTypeId = type.Id, BonusTypeName = type.NameEn, BasicSalary = 10_000m,
                CalculationMethod = "Fixed", CalculationValue = gross,
                GrossBonusAmount = gross, TaxWithheld = tax, BonusAmount = gross - tax,
                PaymentPeriod = period, Status = "Draft", TaxRegion = "GCC",
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return b;
    }

    private static async Task Lock(ZayraDbContext db, PayrollController ctrl, Guid runId)
    {
        (await ctrl.Process(runId, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        await db.PayrollRuns.Where(r => r.Id == runId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "Approved"));
        db.ChangeTracker.Clear();
        (await ctrl.Lock(runId, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
    }

    private static async Task<PayrollPaymentBatch> AcceptedPaymentBatch(
        ZayraDbContext db, PayrollController ctrl, Guid tid, Guid runId)
    {
        (await ctrl.CreatePaymentBatch(runId, new PayrollPaymentBatchRequest("WPS", "SAR"), CancellationToken.None))
            .Should().BeOfType<CreatedResult>();
        var pb = await db.PayrollPaymentBatches.FirstAsync(b => b.TenantId == tid && b.PayrollRunId == runId);
        pb.WpsStatus = WpsStatuses.Accepted;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return pb;
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // (a) Approved + paid through payroll: expense EXACTLY once, payable to ZERO
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task A_ApprovedBonusPaidThroughPayroll_ExpenseDebitedExactlyOnce_AndPayableZeroAfterSettlement()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await Co(db, tid, "Audit Co");
        var emp = await Emp(db, tid, co.Id, "E001");
        var run = await Run(db, tid, co.Id, 2026, 6);
        var batch = await Batch(db, tid, "2026-06", "BON-A", (emp, 5_000m, 0m));

        (await Bonuses(db, tid).ApproveBatch(batch.Id, new BatchApproveRequest("approved"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        // Expense is recognised at accrual — once, at GROSS.
        var afterApprove = await Ledger(db, tid);
        Dr(afterApprove, "6100").Should().Be(5_000m, "approval is where the bonus expense is recognised");
        Liability(afterApprove, "2300").Should().Be(5_000m, "…against Bonus Payable");

        var payroll = Payroll(db, tid);
        await Lock(db, payroll, run.Id);

        // ── THE headline assertion: the run PAYS the bonus, it does not re-expense it. ──
        var afterLock = await Ledger(db, tid);
        Dr(afterLock, "6100").Should().Be(5_000m,
            "bonus expense must be debited EXACTLY ONCE across the lifecycle — the pre-B1b defect debited " +
            "6100 again inside the payroll Lock journal, doubling the P&L charge");
        afterLock.Count(l => IsAcct(l.DebitAccount, "6100")).Should().Be(1,
            "exactly one journal LINE may ever debit the bonus expense account for this bonus");

        // …and it clears the accrual on the account the accrual actually credited.
        var payrollLines = afterLock.Where(l => l.SourceModule == "Payroll").ToList();
        payrollLines.Should().NotContain(l => IsAcct(l.DebitAccount, "6100"),
            "the payroll journal must never debit the bonus expense while a live accrual covers it");
        var clearing = payrollLines.Where(l => l.EventType == GlEventTypes.BonusPayrollClearing).ToList();
        clearing.Should().ContainSingle("one clearing line per batch consumed");
        clearing[0].DebitAccount.Should().Be("2300 - Bonus Payable");
        clearing[0].Amount.Should().Be(5_000m);
        clearing[0].SourceEntityRef.Should().Be(batch.Id.ToString(),
            "the clearing must be machine-traceable back to the batch it settles");

        // ── Settle the net, remit the statutory: the whole cycle. ──
        var pb = await AcceptedPaymentBatch(db, payroll, tid, run.Id);
        (await payroll.SettlePaymentBatch(pb.Id, new SettlePaymentBatchRequest("WPS-1", new DateOnly(2026, 7, 15)), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        (await payroll.RemitStatutory(run.Id, new RemitStatutoryRequest("All", "REF", new DateOnly(2026, 7, 20)), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var gl = await Ledger(db, tid);
        AssertBalanced(gl, "accrual + clearing + settlement + remittance must leave the ledger balanced");

        Dr(gl, "6100").Should().Be(5_000m, "settlement and remittance must not add any further expense");
        Liability(gl, "2300").Should().Be(0m,
            "Bonus Payable must be provably ZERO once the bonus has been paid — the pre-B1b defect orphaned it");
        Liability(gl, "2100").Should().Be(0m, "net-pay settlement clears Salaries Payable");
        Liability(gl, "2101").Should().Be(0m, "GOSI employee payable is remitted");
        Liability(gl, "2106").Should().Be(0m, "GOSI employer payable is remitted");

        // Hand-worked cash: net (13,000 salary + 5,000 bonus − 1,170 GOSI EE = 16,830)
        // plus the GOSI remittance (1,170 EE + 1,410 ER = 2,580) = 19,410.
        Cr(gl, "1000").Should().Be(19_410m, "Cash/Bank carries the real total outflow, once");
        Dr(gl, "5101").Should().Be(1_410m, "employer statutory EXPENSE stays booked (P&L is not cleared)");

        var slip = await db.PayrollSlips.AsNoTracking().FirstAsync(s => s.RunId == run.Id);
        slip.NetSalary.Should().Be(16_830m, "take-home is 13,000 + 5,000 bonus − 1,170 GOSI EE");

        // The production sub-ledger itself must agree that nothing is outstanding.
        var positions = await BonusGlLedger.LoadPositionsAsync(db, tid, new[] { batch.Id }, CancellationToken.None);
        positions.Sum(p => p.Remaining).Should().Be(0m, "the bonus payable sub-ledger must report the batch closed");
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // (b) Approved but NOT paid through payroll: balanced, payable OUTSTANDING
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task B_ApprovedBonusNotPaidThroughPayroll_Balances_AndLeavesPayableOutstanding()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await Co(db, tid, "Audit Co");
        var emp = await Emp(db, tid, co.Id, "E001");
        var run = await Run(db, tid, co.Id, 2026, 6);

        // Bonus is for JULY; the run being locked is JUNE, so the run legitimately does not pay it.
        var batch = await Batch(db, tid, "2026-07", "BON-B", (emp, 5_000m, 0m));
        (await Bonuses(db, tid).ApproveBatch(batch.Id, new BatchApproveRequest(null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        await Lock(db, Payroll(db, tid), run.Id);

        var gl = await Ledger(db, tid);
        AssertBalanced(gl, "an outstanding accrual is still a balanced journal");
        Dr(gl, "6100").Should().Be(5_000m, "the expense is recognised once, at approval");
        Liability(gl, "2300").Should().Be(5_000m,
            "an approved-but-unpaid bonus is a REAL liability and must stay outstanding at its full gross");
        gl.Should().NotContain(l => l.EventType == GlEventTypes.BonusPayrollClearing,
            "an unrelated period's payroll run must never clear this accrual");
        Liability(gl, "2100").Should().Be(13_000m - 1_170m,
            "the June run accrued only its own salary net — the bonus is not in it");

        var positions = await BonusGlLedger.LoadPositionsAsync(db, tid, new[] { batch.Id }, CancellationToken.None);
        positions.Should().ContainSingle();
        positions[0].Remaining.Should().Be(5_000m, "the sub-ledger must report the full accrual still open");
        positions[0].CompanyId.Should().Be(co.Id, "and attribute it to the legal entity that owes it");
        positions[0].AccrualAccount.Should().Be("2300 - Bonus Payable");
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // (c) Void a run that paid a bonus: contra-reverses and RE-OPENS the right accounts
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task C_VoidingRunThatPaidABonus_ContraReverses_AndReopensThePayableAndPayrollAccounts()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await Co(db, tid, "Audit Co");
        var emp = await Emp(db, tid, co.Id, "E001");
        var run = await Run(db, tid, co.Id, 2026, 6);
        var batch = await Batch(db, tid, "2026-06", "BON-V", (emp, 5_000m, 0m));

        (await Bonuses(db, tid).ApproveBatch(batch.Id, new BatchApproveRequest(null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var payroll = Payroll(db, tid);
        await Lock(db, payroll, run.Id);
        var pb = await AcceptedPaymentBatch(db, payroll, tid, run.Id);
        (await payroll.SettlePaymentBatch(pb.Id, new SettlePaymentBatchRequest(), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        (await payroll.RemitStatutory(run.Id, new RemitStatutoryRequest("All"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        Liability(await Ledger(db, tid), "2300").Should().Be(0m, "pre-condition: the bonus was paid");

        // POD-B3 — this run has DISBURSED net pay and remitted statutory cash, so the void refuses until
        // the operator states what happened to the money. The assertions below expect every account
        // (including 1000 Cash/Bank) back at zero, i.e. the funds genuinely came back: FundsRecalled with
        // the recall references that evidence it.
        (await payroll.VoidRun(run.Id, new PayrollDecisionRequest("wrong run"), CancellationToken.None,
            settlementDisposition: "FundsRecalled", settlementReference: "RECALL-BON-1",
            remittanceDisposition: "FundsRecalled", remittanceReference: "GOSI-REFUND-BON-1"))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var gl = await Ledger(db, tid);
        AssertBalanced(gl, "contras keep the ledger balanced");

        // Every payroll-owned account unwinds to zero…
        foreach (var code in new[] { "2100", "2101", "2106", "5101", "1000" })
            Liability(gl, code).Should().Be(0m, $"void must fully unwind {code}");

        // …and the bonus payable RE-OPENS at its original gross, on its original account.
        Liability(gl, "2300").Should().Be(5_000m,
            "voiding the run un-pays the bonus, so the accrual is owed again");
        Dr(gl, "6100").Should().Be(5_000m,
            "the void must not re-expense or un-expense the bonus — it was earned and remains a cost");
        gl.Count(l => IsAcct(l.DebitAccount, "6100")).Should().Be(1);

        // The reversal is a proper contra of the clearing line: mirrored legs, same amount.
        var clearing = gl.Single(l => l.EventType == GlEventTypes.BonusPayrollClearing && !string.IsNullOrEmpty(l.DebitAccount));
        clearing.IsReversed.Should().BeTrue("the original clearing must be flagged reversed");
        var contra = gl.Single(l => IsAcct(l.CreditAccount, "2300") && l.SourceModule == "Payroll");
        contra.Amount.Should().Be(clearing.Amount, "a contra mirrors the original exactly");
        contra.CompanyId.Should().Be(co.Id, "the contra carries the legal-entity dimension of the line it reverses");

        // The production sub-ledger must agree the batch is open again — this is what makes the bonus
        // re-payable after a void instead of silently lost.
        var positions = await BonusGlLedger.LoadPositionsAsync(db, tid, new[] { batch.Id }, CancellationToken.None);
        positions.Should().ContainSingle();
        positions[0].Remaining.Should().Be(5_000m, "the payable sub-ledger must re-open the full accrual");
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // (d) Two-company tenant: bonus AND loan AND advance route company-first
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task D_TwoCompanyTenant_BonusLoanAndAdvance_PostToTheCompanysAccount_InTheCompanysCurrency()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var coA = await Co(db, tid, "Co A", "SAR");   // no overrides → tenant/compiled defaults
        var coB = await Co(db, tid, "Co B", "AED");   // full per-company chart of accounts
        var empA = await Emp(db, tid, coA.Id, "A001");
        var empB = await Emp(db, tid, coB.Id, "B001");

        foreach (var (driver, code, name, type) in new[]
        {
            ("BONUS_PAYABLE",      "2390", "B Bonus Payable",   "Liability"),
            ("LOAN_RECEIVABLE",    "1490", "B Loans",           "Asset"),
            ("ADVANCE_RECEIVABLE", "1491", "B Advances",        "Asset"),
            ("CASH_BANK",          "1090", "B Bank",            "Asset"),
        })
        {
            var acct = new GlAccount { TenantId = tid, CompanyId = coB.Id, Code = code, Name = name, AccountType = type };
            db.GlAccounts.Add(acct);
            await db.SaveChangesAsync();
            db.GlAccountMappings.Add(new GlAccountMapping { TenantId = tid, CompanyId = coB.Id, DriverKey = driver, AccountId = acct.Id });
            await db.SaveChangesAsync();
        }

        var loanType = new LoanType
        {
            TenantId = tid, Code = "GEN", NameEn = "General", MaxAmount = 100_000m, MaxInstallments = 24,
            RepaymentFrequency = "Monthly", IsInterestFree = true, RequiresApproval = false, IsActive = true,
        };
        db.LoanTypes.Add(loanType);
        db.AdvancePolicies.Add(new AdvancePolicy
        {
            TenantId = tid, PolicyName = "Std", MaxPercentageOfSalary = 100m, MaxAdvancesPerYear = 12,
            MinServiceMonths = 0, AllowInstallments = true, MaxInstallments = 12, CooldownMonths = 0,
            RequiresApproval = false, IsActive = true,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // ── BONUS: one batch spanning both entities → one accrual per entity. ──
        var batch = await Batch(db, tid, "2026-06", "BON-D", (empA, 1_000m, 0m), (empB, 2_000m, 0m));
        (await Bonuses(db, tid).ApproveBatch(batch.Id, new BatchApproveRequest(null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        // ── LOAN: auto-disbursed on create (RequiresApproval = false). ──
        var loans = Loans(db, tid);
        (await loans.CreateLoan(new CreateLoanRequest(Guid.Empty, empA.FullName, loanType.Id, 6_000m, 6, null, empA.Id), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await loans.CreateLoan(new CreateLoanRequest(Guid.Empty, empB.FullName, loanType.Id, 8_000m, 8, null, empB.Id), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        // ── ADVANCE: auto-disbursed on create (policy RequiresApproval = false). ──
        var advances = Advances(db, tid);
        (await advances.Create(new CreateAdvanceRequest(Guid.Empty, empA.FullName, 900m, "Installments", 3, null, "rent", empA.Id), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await advances.Create(new CreateAdvanceRequest(Guid.Empty, empB.FullName, 1_200m, "Installments", 3, null, "rent", empB.Id), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var gl = await Ledger(db, tid);
        AssertBalanced(gl, "every module's journal balances in a multi-company tenant");

        // Company A — no overrides anywhere: tenant/compiled defaults + SAR + A's company stamp.
        var aBonus = gl.Single(l => l.SourceModule == "Bonus" && l.CompanyId == coA.Id);
        aBonus.DebitAccount.Should().Be("6100 - Employee Bonus Expense");
        aBonus.CreditAccount.Should().Be("2300 - Bonus Payable");
        aBonus.Amount.Should().Be(1_000m);
        aBonus.Currency.Should().Be("SAR");

        var aLoan = gl.Single(l => l.SourceModule == "Loan" && l.CompanyId == coA.Id);
        aLoan.DebitAccount.Should().Be("1400 - Employee Loans Receivable");
        aLoan.CreditAccount.Should().Be("1000 - Cash/Bank");
        aLoan.Amount.Should().Be(6_000m);
        aLoan.Currency.Should().Be("SAR");

        var aAdv = gl.Single(l => l.SourceModule == "Advance" && l.CompanyId == coA.Id);
        aAdv.DebitAccount.Should().Be("1410 - Employee Salary Advances");
        aAdv.CreditAccount.Should().Be("1000 - Cash/Bank");
        aAdv.Amount.Should().Be(900m);
        aAdv.Currency.Should().Be("SAR");

        // Company B — every driver remapped, and a different functional currency.
        var bBonus = gl.Single(l => l.SourceModule == "Bonus" && l.CompanyId == coB.Id);
        bBonus.CreditAccount.Should().Be("2390 - B Bonus Payable", "company B's own BONUS_PAYABLE mapping must win");
        bBonus.Amount.Should().Be(2_000m);
        bBonus.Currency.Should().Be("AED", "the line is stamped in the posting entity's DefaultCurrency");

        var bLoan = gl.Single(l => l.SourceModule == "Loan" && l.CompanyId == coB.Id);
        bLoan.DebitAccount.Should().Be("1490 - B Loans", "company B's LOAN_RECEIVABLE mapping must win");
        bLoan.CreditAccount.Should().Be("1090 - B Bank", "…and its own bank account, not the tenant default");
        bLoan.Amount.Should().Be(8_000m);
        bLoan.Currency.Should().Be("AED");

        var bAdv = gl.Single(l => l.SourceModule == "Advance" && l.CompanyId == coB.Id);
        bAdv.DebitAccount.Should().Be("1491 - B Advances", "company B's ADVANCE_RECEIVABLE mapping must win");
        bAdv.CreditAccount.Should().Be("1090 - B Bank");
        bAdv.Amount.Should().Be(1_200m);
        bAdv.Currency.Should().Be("AED");

        // Cross-contamination guard: A's money never lands on B's chart and vice versa.
        gl.Where(l => l.CompanyId == coA.Id).Should().NotContain(
            l => IsAcct(l.DebitAccount, "1490") || IsAcct(l.DebitAccount, "1491")
              || IsAcct(l.CreditAccount, "2390") || IsAcct(l.CreditAccount, "1090"),
            "company A must never post to company B's accounts");
        gl.Where(l => l.CompanyId == coB.Id).Should().NotContain(
            l => IsAcct(l.CreditAccount, "1000") || IsAcct(l.DebitAccount, "1400")
              || IsAcct(l.DebitAccount, "1410") || IsAcct(l.CreditAccount, "2300"),
            "company B must never fall back to the tenant-default accounts it has overridden");
        gl.Should().NotContain(l => l.CompanyId == null, "every B1b posting carries its legal-entity dimension");
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // (e) A closed period blocks bonus / loan / advance posting PER COMPANY
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task E_ClosedPeriod_BlocksBonusLoanAndAdvance_ForThatCompanyOnly()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var coA = await Co(db, tid, "Co A");
        var coB = await Co(db, tid, "Co B");
        var empA = await Emp(db, tid, coA.Id, "A001");
        var empB = await Emp(db, tid, coB.Id, "B001");

        // Loans/advances stamp the CURRENT calendar month, so close that month to hit all three modules.
        var period = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM");

        var loanType = new LoanType
        {
            TenantId = tid, Code = "GEN", NameEn = "General", MaxAmount = 100_000m, MaxInstallments = 24,
            RepaymentFrequency = "Monthly", IsInterestFree = true, RequiresApproval = false, IsActive = true,
        };
        db.LoanTypes.Add(loanType);
        db.AdvancePolicies.Add(new AdvancePolicy
        {
            TenantId = tid, PolicyName = "Std", MaxPercentageOfSalary = 100m, MaxAdvancesPerYear = 12,
            MinServiceMonths = 0, AllowInstallments = true, MaxInstallments = 12, CooldownMonths = 0,
            RequiresApproval = false, IsActive = true,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        (await Gl(db, tid).ClosePeriod(period, coA.Id, new GlPeriodActionRequest("A month-end"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var bonuses = Bonuses(db, tid);
        var loans = Loans(db, tid);
        var advances = Advances(db, tid);

        // ── Company A: every module refuses, and commits nothing. ──
        var batchA = await Batch(db, tid, period, "BON-EA", (empA, 1_000m, 0m));
        await FluentActions.Awaiting(() => bonuses.ApproveBatch(batchA.Id, new BatchApproveRequest(null), CancellationToken.None))
            .Should().ThrowAsync<PeriodClosedException>("company A's period is closed to bonus accrual");
        db.ChangeTracker.Clear();

        await FluentActions.Awaiting(() => loans.CreateLoan(
                new CreateLoanRequest(Guid.Empty, empA.FullName, loanType.Id, 6_000m, 6, null, empA.Id), CancellationToken.None))
            .Should().ThrowAsync<PeriodClosedException>("company A's period is closed to loan disbursement");
        db.ChangeTracker.Clear();

        await FluentActions.Awaiting(() => advances.Create(
                new CreateAdvanceRequest(Guid.Empty, empA.FullName, 900m, "Installments", 3, null, "rent", empA.Id), CancellationToken.None))
            .Should().ThrowAsync<PeriodClosedException>("company A's period is closed to advance disbursement");
        db.ChangeTracker.Clear();

        (await Ledger(db, tid)).Should().BeEmpty(
            "the guard throws BEFORE SaveChanges — a closed period must leave no partial journal behind");
        (await db.EmployeeLoans.IgnoreQueryFilters().CountAsync(x => x.TenantId == tid)).Should().Be(0,
            "and no operational row may be committed either");
        (await db.SalaryAdvances.IgnoreQueryFilters().CountAsync(x => x.TenantId == tid)).Should().Be(0);

        // ── Company B: same period, still open → all three post normally. ──
        var batchB = await Batch(db, tid, period, "BON-EB", (empB, 2_000m, 0m));
        (await bonuses.ApproveBatch(batchB.Id, new BatchApproveRequest(null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>("a per-company close must not freeze the whole group");
        db.ChangeTracker.Clear();
        (await loans.CreateLoan(new CreateLoanRequest(Guid.Empty, empB.FullName, loanType.Id, 8_000m, 8, null, empB.Id), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await advances.Create(new CreateAdvanceRequest(Guid.Empty, empB.FullName, 1_200m, "Installments", 3, null, "rent", empB.Id), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var gl = await Ledger(db, tid);
        gl.Should().HaveCount(3, "exactly company B's three journals exist");
        gl.Should().OnlyContain(l => l.CompanyId == coB.Id);
        AssertBalanced(gl, "the surviving journals are balanced");

        // ── Reopening company A restores posting. ──
        (await Gl(db, tid).ReopenPeriod(period, coA.Id, new GlPeriodActionRequest("audit correction"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await loans.CreateLoan(new CreateLoanRequest(Guid.Empty, empA.FullName, loanType.Id, 6_000m, 6, null, empA.Id), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>("reopening the period must re-enable posting");
        db.ChangeTracker.Clear();
        (await Ledger(db, tid)).Count(l => l.CompanyId == coA.Id).Should().Be(1);
    }

    [Fact]
    public async Task E2_GroupWideClose_BlocksLoanAndAdvance_ForEveryCompany()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        await Co(db, tid, "Co A");   // second entity: this is a GROUP tenant, not a single-company one
        var coB = await Co(db, tid, "Co B");
        var empB = await Emp(db, tid, coB.Id, "B001");

        var period = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM");
        var loanType = new LoanType
        {
            TenantId = tid, Code = "GEN", NameEn = "General", MaxAmount = 100_000m, MaxInstallments = 24,
            RepaymentFrequency = "Monthly", IsInterestFree = true, RequiresApproval = false, IsActive = true,
        };
        db.LoanTypes.Add(loanType);
        db.AdvancePolicies.Add(new AdvancePolicy
        {
            TenantId = tid, PolicyName = "Std", MaxPercentageOfSalary = 100m, MaxAdvancesPerYear = 12,
            MinServiceMonths = 0, AllowInstallments = true, MaxInstallments = 12, CooldownMonths = 0,
            RequiresApproval = false, IsActive = true,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        (await Gl(db, tid).ClosePeriod(period, null, new GlPeriodActionRequest("group freeze"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        await FluentActions.Awaiting(() => Loans(db, tid).CreateLoan(
                new CreateLoanRequest(Guid.Empty, empB.FullName, loanType.Id, 8_000m, 8, null, empB.Id), CancellationToken.None))
            .Should().ThrowAsync<PeriodClosedException>(
                "tightening the guard to per-company must NOT weaken a group-wide close");
        db.ChangeTracker.Clear();

        await FluentActions.Awaiting(() => Advances(db, tid).Create(
                new CreateAdvanceRequest(Guid.Empty, empB.FullName, 1_200m, "Installments", 3, null, "rent", empB.Id), CancellationToken.None))
            .Should().ThrowAsync<PeriodClosedException>();
        db.ChangeTracker.Clear();

        (await Ledger(db, tid)).Should().BeEmpty();
    }


    // ══════════════════════════════════════════════════════════════════════════════
    // Cross-cutting: the loan/advance receivable must amortise to ZERO over a full cycle
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task F_AdvanceReceivable_AmortisesToZero_ThroughPayrollWithhold_WithoutDoubleCreditingCash()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await Co(db, tid, "Audit Co");
        var emp = await Emp(db, tid, co.Id, "E001");
        var run = await Run(db, tid, co.Id, 2026, 6);

        // Advance of 1,200 disbursed (cash left the bank ONCE), repaid in a single 1,200 payroll EMI.
        db.SalaryAdvances.Add(new SalaryAdvance
        {
            TenantId = tid, CompanyId = co.Id, EmployeeId = Guid.NewGuid(), EmployeeIntId = emp.Id,
            EmployeeName = emp.FullName, AdvanceNumber = "ADV-1", RequestedAmount = 1_200m,
            ApprovedAmount = 1_200m, InstallmentAmount = 1_200m, Installments = 1,
            OutstandingBalance = 1_200m, Status = "Active", RepaymentType = "Installments",
        });
        db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = tid, CompanyId = co.Id, SourceModule = "Advance", SourceEntityId = Guid.NewGuid(),
            SourceEntityRef = "ADV-1", EventType = "Disbursement",
            DebitAccount = "1410 - Employee Salary Advances", CreditAccount = "1000 - Cash/Bank",
            Amount = 1_200m, Currency = "SAR", EntryDate = new DateOnly(2026, 5, 1), Period = "2026-05",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var payroll = Payroll(db, tid);
        await Lock(db, payroll, run.Id);
        (await payroll.RemitStatutory(run.Id, new RemitStatutoryRequest("LOAN", "EMI"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var gl = await Ledger(db, tid);
        AssertBalanced(gl, "disbursement + accrual + remittance must balance");
        Asset(gl, "1410").Should().Be(0m,
            "the advance receivable must amortise to ZERO once the instalment is withheld from pay");
        Liability(gl, "2107").Should().Be(0m, "the loan-deduction control account clears");
        Cr(gl, "1000").Should().Be(1_200m,
            "cash left the bank ONCE, at disbursement — an EMI withheld from pay is not a second outflow");
    }
}

// ── Test doubles (file-scoped) ─────────────────────────────────────────────────

file static class _AudRules
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

file sealed class _AudScope : Zayra.Api.Application.Common.IDataScopeService
{
    public Task<Zayra.Api.Application.Common.DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Application.Common.DataScope
        { Level = Zayra.Api.Application.Common.DataScopeLevel.Organization, AllowedEmployeeIds = null });
}

file sealed class _AudHttp : IHttpContextAccessor
{
    public _AudHttp(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _AudNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _AudKsaResolver : ICountryPackResolver
{
    private static readonly KsaDeductionCalculator _calc = new(_AudRules.Rules);
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => cc == "SAU" ? _calc : new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class _AudLetters : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class _AudDocs : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument(file.FileName, file.ContentType, "storage/test", "/tmp/test"));
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public string ResolvePath(string storageUrl) => "/tmp/test";
}
