using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// POD-B3 — RECOVERY / ROLLBACK of a bad payroll month.
///
/// The bar: an operator can fully recover from a month that is LOCKED, PAID (net settled) and REMITTED,
/// with no manual journal entries, no data loss, no dead-end states, and a complete audit trail.
///
/// Each test names the defect it closes rather than the code path it walks.
/// </summary>
public class PayrollRecoveryB3Tests
{
    // ══ Harness ═══════════════════════════════════════════════════════════════════════════════════

    private static readonly Guid Preparer = Guid.NewGuid();
    private static readonly Guid Approver = Guid.NewGuid();

    private static (ZayraDbContext db, SqliteConnection conn) NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static PayrollController Payroll(ZayraDbContext db, Guid tenantId, Guid userId)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, userId == Preparer ? "preparer" : "approver"),
            // Distinct ROLES as well as distinct identities: Approve's two-step ladder needs a Finance
            // role to finalise, and the maker-checker + override SoD are identity-based on top of that.
            new(ClaimTypes.Role, userId == Preparer ? "Payroll Officer" : "Finance Controller"),
            new("permission", "payroll.export"),
            new("permission", "payroll.lock"),
            new("permission", "payroll.write"),
            new("permission", "payroll.approve"),
        };
        var httpCtx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        var ctrl = new PayrollController(
            db, new _B3Scope(), new _B3Http(httpCtx), new _B3Notifications(),
            new _B3PackResolver(), _B3Rules.Rules, new _B3Letters(), new _B3Storage(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(4));
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }

    private static FinanceGlController Gl(ZayraDbContext db, Guid tenantId)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "finance"),
            new("permission", "finance.gl.manage"),
            new("permission", "finance.gl.read"),
        };
        var httpCtx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        return new FinanceGlController(db) { ControllerContext = new ControllerContext { HttpContext = httpCtx } };
    }

    private static MobileController Mobile(ZayraDbContext db, Guid tenantId, int employeeId)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new("employee_id", employeeId.ToString()),
        };
        var httpCtx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        return new MobileController(db) { ControllerContext = new ControllerContext { HttpContext = httpCtx } };
    }

    private static async Task<(Guid companyId, Employee emp, PayrollRun run)> SeedRun(
        ZayraDbContext db, Guid tenantId, int year = 2026, int month = 6)
    {
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "Recovery Co",
            CountryCode = "SAU", Jurisdiction = "KSA-mainland", IsActive = true, DefaultCurrency = "SAR",
        };
        db.Companies.Add(company);
        var structure = new SalaryStructure
        {
            TenantId = tenantId, Code = "STR", Name = "Base", Currency = "SAR",
            EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
        };
        db.SalaryStructures.Add(structure);
        await db.SaveChangesAsync();

        var emp = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "E001", FullName = "Ali Hassan",
            Status = "Active", JoiningDate = new DateTime(2023, 1, 1),
            WorkEmail = "ali@test.com", Nationality = "SAU", ContractType = "Indefinite",
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();

        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = emp.Id, SalaryStructureId = structure.Id,
            BasicSalary = 10_000m, HousingAllowance = 2_000m, TransportAllowance = 1_000m,
            EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
        });
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
        {
            TenantId = tenantId, EmployeeId = emp.Id,
            Iban = "SA4420000001234567891234", MolId = "MOL-E001", SalaryCurrency = "SAR",
        });
        var run = new PayrollRun
        {
            TenantId = tenantId, CompanyId = company.Id, Year = year, Month = month, Status = "Draft",
            CreatedByUserId = Preparer,
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        return (company.Id, emp, run);
    }

    /// <summary>
    /// Process → generate payslips → Approve → Lock, i.e. the real shipped order. Payslips are generated
    /// BEFORE the lock because Lock is what publishes them to ESS; generating them afterwards would leave
    /// IsPublishedToEss false and quietly hide the very leak these tests are about.
    /// The preparer processes; a DIFFERENT user approves (maker-checker).
    /// </summary>
    private static async Task ProcessApproveLock(ZayraDbContext db, Guid tenantId, Guid runId)
    {
        (await Payroll(db, tenantId, Preparer).Process(runId, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await Payroll(db, tenantId, Preparer).GeneratePayslips(runId, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        var run = await db.PayrollRuns.FirstAsync(r => r.Id == runId);
        run.Status = "Approved";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        (await Payroll(db, tenantId, Approver).Lock(runId, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
    }

    /// <summary>Locked → payment batch (Accepted) → settle → remit. The full money-out path.</summary>
    private static async Task<PayrollPaymentBatch> SettleAndRemit(
        ZayraDbContext db, Guid tenantId, Guid runId, DateOnly? paidDate = null)
    {
        var ctrl = Payroll(db, tenantId, Approver);
        (await ctrl.CreatePaymentBatch(runId, new PayrollPaymentBatchRequest("WPS", "SAR"), CancellationToken.None))
            .Should().BeOfType<CreatedResult>();
        var batch = await db.PayrollPaymentBatches.FirstAsync(b => b.PayrollRunId == runId);
        batch.WpsStatus = WpsStatuses.Accepted;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        (await ctrl.SettlePaymentBatch(batch.Id, new SettlePaymentBatchRequest("PAYREF", paidDate), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        (await ctrl.RemitStatutory(runId, new RemitStatutoryRequest("All", "GOSIREF",
            paidDate), CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        return await db.PayrollPaymentBatches.AsNoTracking().FirstAsync(b => b.Id == batch.Id);
    }

    private static Task<List<FinanceGlEntry>> RunGl(ZayraDbContext db, Guid runId) =>
        db.FinanceGlEntries.AsNoTracking()
            .Where(x => x.SourceModule == "Payroll" && x.SourceEntityId == runId).ToListAsync();

    /// <summary>Σ CR − Σ DR for an account across the whole tenant ledger — the account's true carrying
    /// balance. A contra is a real posting; IsReversed is an audit link, never a balance filter.</summary>
    private static async Task<decimal> Balance(ZayraDbContext db, Guid tenantId, string acctFragment)
    {
        var rows = await db.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenantId).ToListAsync();
        return rows.Where(l => l.CreditAccount.Contains(acctFragment)).Sum(l => l.Amount)
             - rows.Where(l => l.DebitAccount.Contains(acctFragment)).Sum(l => l.Amount);
    }

    private static (decimal dr, decimal cr) Totals(IEnumerable<FinanceGlEntry> gl)
    {
        var list = gl.ToList();
        return (list.Where(l => !string.IsNullOrEmpty(l.DebitAccount)).Sum(l => l.Amount),
                list.Where(l => !string.IsNullOrEmpty(l.CreditAccount)).Sum(l => l.Amount));
    }

    private static string Body(IActionResult r) => JsonSerializer.Serialize(r switch
    {
        ObjectResult o => o.Value,
        _ => null,
    });

    // ══ 1. D1 — the Lock idempotency guard was IsReversed-blind ═══════════════════════════════════

    /// <summary>
    /// THE HEADLINE. Before B3 the Lock guard asked "does this run have ANY Payroll GL row?" — and a
    /// void's contras are Payroll rows for the same run, persisted with IsReversed=false. So after a void
    /// ANY path that re-locked the run posted ZERO GL while setting Status=Locked, ErpPostingStatus=
    /// ReadyForErp, slips Final and payslips published: a month that accrues NOTHING while every payslip
    /// asserts a liability, with `glPosted:false` buried in audit metadata and nothing on the response.
    ///
    /// This test proves BOTH halves: the old predicate still matches (so the bug was real and would have
    /// silently fired), and the fixed predicate posts a fresh, balanced accrual.
    /// </summary>
    [Fact]
    public async Task D1_ReLockAfterVoid_PostsFreshBalancedGl_TheOldGuardWouldHavePostedNothing()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var (_, _, run) = await SeedRun(db, tid);
        await ProcessApproveLock(db, tid, run.Id);
        var accrualCount = (await RunGl(db, run.Id)).Count(l => l.EventType == GlEventTypes.Accrual);
        accrualCount.Should().BeGreaterThan(0);

        (await Payroll(db, tid, Approver).VoidRun(run.Id, new PayrollDecisionRequest("bad month"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        // THE OLD PREDICATE — "any Payroll row for this run" — is still TRUE after the void, because the
        // contras satisfy it. That is exactly the silent zero-GL lock.
        (await db.FinanceGlEntries.AnyAsync(x => x.TenantId == tid && x.SourceModule == "Payroll" && x.SourceEntityId == run.Id))
            .Should().BeTrue("the void's own contras satisfy the pre-B3 guard — this is the defect");
        // THE FIXED PREDICATE — "a LIVE accrual" — is correctly false.
        (await db.FinanceGlEntries.AnyAsync(x => x.TenantId == tid && x.SourceModule == "Payroll" && x.SourceEntityId == run.Id
                                              && x.EventType == GlEventTypes.Accrual && !x.IsReversed))
            .Should().BeFalse("the accrual was reversed, so the run has NOT accrued");

        // Force the run back to Approved (the only way to reach Lock twice) and re-lock.
        var tracked = await db.PayrollRuns.FirstAsync(r => r.Id == run.Id);
        tracked.Status = "Approved";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var res = await Payroll(db, tid, Approver).Lock(run.Id, CancellationToken.None);
        res.Should().BeOfType<OkObjectResult>();
        Body(res).Should().Contain("\"glPosted\":true",
            "glPosted is now part of the ANSWER, not something buried in audit metadata");
        db.ChangeTracker.Clear();

        var live = (await RunGl(db, run.Id)).Where(l => l.EventType == GlEventTypes.Accrual && !l.IsReversed).ToList();
        live.Should().HaveCount(accrualCount, "a fresh accrual journal must be posted, not silently skipped");
        var (dr, cr) = Totals(live);
        dr.Should().Be(cr, "the fresh accrual balances");
    }

    /// <summary>The idempotency it MUST still provide: a re-lock with a live accrual posts nothing.</summary>
    [Fact]
    public async Task D1_ReLockWithoutAVoid_IsStillIdempotent()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var (_, _, run) = await SeedRun(db, tid);
        await ProcessApproveLock(db, tid, run.Id);
        var before = (await RunGl(db, run.Id)).Count;

        var tracked = await db.PayrollRuns.FirstAsync(r => r.Id == run.Id);
        tracked.Status = "Approved";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var res = await Payroll(db, tid, Approver).Lock(run.Id, CancellationToken.None);
        res.Should().BeOfType<OkObjectResult>();
        Body(res).Should().Contain("\"glPosted\":false");
        (await RunGl(db, run.Id)).Count.Should().Be(before, "a re-lock over a live accrual must post nothing");
    }

    // ══ 2. Void completeness — every artifact the month created ════════════════════════════════════

    /// <summary>
    /// A LOCKED + PAID + REMITTED month with two loans (one of them with NO installment schedule — the
    /// case that has no witness at all without B3), an absence impact and an adjustment. After the void
    /// every operational artifact is back where it started.
    ///
    /// The two-loan / unscheduled-loan shape is the point: Process writes ONE aggregate LOAN_EMI deduction
    /// per EMPLOYEE and stamps an installment only when a schedule row exists, so neither the payslip nor
    /// the installment can attribute the decrement. Only the per-loan consumption witness can.
    /// </summary>
    [Fact]
    public async Task Void_RestoresEveryConsumedArtifact_IncludingAnUnscheduledSecondLoan()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var (companyId, emp, run) = await SeedRun(db, tid);

        var loanA = new EmployeeLoan
        {
            TenantId = tid, CompanyId = companyId, EmployeeId = Guid.NewGuid(), EmployeeIntId = emp.Id,
            LoanNumber = "LN-A", Status = "Active", ApprovedAmount = 6_000m, OutstandingBalance = 6_000m,
            InstallmentAmount = 1_000m, TotalRepaid = 0m, ApprovedInstallments = 6,
        };
        // The second loan has NO installment schedule — Process decrements it with zero installment witness.
        var loanB = new EmployeeLoan
        {
            TenantId = tid, CompanyId = companyId, EmployeeId = Guid.NewGuid(), EmployeeIntId = emp.Id,
            LoanNumber = "LN-B", Status = "Active", ApprovedAmount = 500m, OutstandingBalance = 500m,
            InstallmentAmount = 500m, TotalRepaid = 0m, ApprovedInstallments = 1,
        };
        db.EmployeeLoans.AddRange(loanA, loanB);
        await db.SaveChangesAsync();
        db.LoanInstallments.Add(new LoanInstallment
        {
            TenantId = tid, LoanId = loanA.Id, InstallmentNumber = 1,
            DueDate = new DateOnly(2026, 6, 28), AmountDue = 1_000m, Status = "Pending",
        });
        db.AttendancePayrollImpacts.Add(new AttendancePayrollImpact
        {
            TenantId = tid, EmployeeId = emp.Id, WorkDate = new DateOnly(2026, 6, 10),
            ImpactType = "Absence", Minutes = 480, Status = "PendingPayroll",
        });
        db.PayrollAdjustments.Add(new PayrollAdjustment
        {
            TenantId = tid, PayrollRunId = run.Id, EmployeeId = emp.Id,
            AdjustmentType = "Allowance", Amount = 250m, Reason = "one-off", Status = "Approved",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await ProcessApproveLock(db, tid, run.Id);
        var batch = await SettleAndRemit(db, tid, run.Id);

        // Pre-conditions: the month really consumed everything.
        (await db.EmployeeLoans.AsNoTracking().FirstAsync(l => l.Id == loanA.Id)).OutstandingBalance.Should().Be(5_000m);
        (await db.EmployeeLoans.AsNoTracking().FirstAsync(l => l.Id == loanB.Id)).Status.Should().Be("Closed");
        (await db.AttendancePayrollImpacts.AsNoTracking().FirstAsync()).Status.Should().Be("Processed");
        (await db.PayrollAdjustments.AsNoTracking().FirstAsync()).Status.Should().Be("Processed");
        (await db.Payslips.AsNoTracking().FirstAsync(p => p.PayrollRunId == run.Id)).IsPublishedToEss.Should().BeTrue();

        (await Payroll(db, tid, Approver).VoidRun(run.Id, new PayrollDecisionRequest("wrong attendance data"),
            CancellationToken.None, settlementDisposition: "FundsRecalled", settlementReference: "RECALL-1",
            remittanceDisposition: "FundsRecalled", remittanceReference: "REFUND-1"))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var a = await db.EmployeeLoans.AsNoTracking().FirstAsync(l => l.Id == loanA.Id);
        a.OutstandingBalance.Should().Be(6_000m, "loan A's EMI is given back exactly as it was taken");
        a.TotalRepaid.Should().Be(0m);
        a.Status.Should().Be("Active");

        var b = await db.EmployeeLoans.AsNoTracking().FirstAsync(l => l.Id == loanB.Id);
        b.OutstandingBalance.Should().Be(500m,
            "loan B has NO installment schedule, so only the per-loan witness can restore it — without B3 " +
            "this loan stayed silently repaid and the employee would have been charged twice");
        b.TotalRepaid.Should().Be(0m);
        b.Status.Should().Be("Active", "the RECORDED prior status is restored, not an assumed Closed → Active");

        (await db.LoanInstallments.AsNoTracking().FirstAsync()).Status.Should().Be("Pending");
        (await db.LoanInstallments.AsNoTracking().FirstAsync()).PayrollRunId.Should().BeNull();
        (await db.AttendancePayrollImpacts.AsNoTracking().FirstAsync()).Status
            .Should().Be("PendingPayroll", "an impact that is never released is DATA LOSS: the re-run drops the absence");
        (await db.PayrollAdjustments.AsNoTracking().FirstAsync()).Status.Should().Be("Approved");

        var payslip = await db.Payslips.AsNoTracking().FirstAsync(p => p.PayrollRunId == run.Id);
        payslip.IsPublishedToEss.Should().BeFalse("a voided month must leave ESS");
        payslip.PublishedAtUtc.Should().BeNull();

        var afterBatch = await db.PayrollPaymentBatches.AsNoTracking().FirstAsync(x => x.Id == batch.Id);
        afterBatch.WpsStatus.Should().Be(WpsStatuses.Voided, "terminal, not parked on the Accepted → Paid edge");
        (await db.PayrollPaymentRecords.AsNoTracking().Where(r => r.PaymentBatchId == batch.Id).ToListAsync())
            .Should().OnlyContain(r => r.Status == "Cancelled");
        (await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == run.Id)).ErpPostingStatus
            .Should().Be(ErpPostingStatuses.NotReady, "a voided run has no journal to export");
        (await db.PayrollSlips.AsNoTracking().Where(s => s.RunId == run.Id).ToListAsync())
            .Should().OnlyContain(s => s.Status == "Voided");
    }

    /// <summary>
    /// Requirement 2's actual bar: the CONTROL ACCOUNTS end exactly where they started — per (account,
    /// period), not merely cumulatively. Per-period is what catches the pre-B3 contra-period bug, where
    /// every contra was stamped with the ACCRUAL period, so voiding a run settled in July reversed the
    /// July cash into June and left both months permanently unbalanced.
    /// </summary>
    [Fact]
    public async Task Void_ControlAccountsReturnToZero_InEachPeriodSeparately()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var (_, _, run) = await SeedRun(db, tid, 2026, 6);
        await ProcessApproveLock(db, tid, run.Id);
        // Settle + remit in JULY — the accrual is June's, the cash is July's (standard accrual accounting).
        await SettleAndRemit(db, tid, run.Id, new DateOnly(2026, 7, 15));

        (await Payroll(db, tid, Approver).VoidRun(run.Id, new PayrollDecisionRequest("re-run"), CancellationToken.None,
            settlementDisposition: "FundsRecalled", settlementReference: "R1",
            remittanceDisposition: "FundsRecalled", remittanceReference: "R2"))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var gl = await RunGl(db, run.Id);
        foreach (var period in new[] { "2026-06", "2026-07" })
        {
            var inPeriod = gl.Where(l => l.Period == period).ToList();
            var (dr, cr) = Totals(inPeriod);
            dr.Should().Be(cr, $"{period} must balance ON ITS OWN — a contra dated into the wrong month " +
                               "leaves both months permanently out");
            foreach (var acct in new[] { "2100", "2101", "2106", "1000" })
            {
                var net = inPeriod.Where(l => l.CreditAccount.Contains(acct)).Sum(l => l.Amount)
                        - inPeriod.Where(l => l.DebitAccount.Contains(acct)).Sum(l => l.Amount);
                net.Should().Be(0m, $"{acct} must net to zero within {period}");
            }
        }
        // The July reversal really is dated into July, inheriting its original's period. (Reversing a
        // disbursement DEBITS Cash/Bank — the money comes back — so this is the cash leg of the contra.)
        var cashContras = gl.Where(l => l.EventType == GlEventTypes.Void && l.DebitAccount.Contains("1000")).ToList();
        cashContras.Should().NotBeEmpty();
        cashContras.Should().OnlyContain(l => l.Period == "2026-07",
            "pre-B3 EVERY contra was stamped with the ACCRUAL period, so a July settlement was reversed into June");
    }

    // ══ 3. Closed periods ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Doctrine (a): refuse, and list EVERY closed period the unwind would write into. Pre-B3 the guard
    /// tested ONE period (the accrual's) against ONE company, so a closed SETTLEMENT month was never
    /// consulted at all — the void wrote into it silently.
    /// </summary>
    [Fact]
    public async Task Void_ClosedSettlementPeriod_IsRefused_AndNothingIsWritten()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var (companyId, _, run) = await SeedRun(db, tid, 2026, 6);
        await ProcessApproveLock(db, tid, run.Id);
        await SettleAndRemit(db, tid, run.Id, new DateOnly(2026, 7, 15));

        (await Gl(db, tid).ClosePeriod("2026-07", companyId, new GlPeriodActionRequest("month-end"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        var glBefore = (await RunGl(db, run.Id)).Count;

        var refused = await Payroll(db, tid, Approver).VoidRun(run.Id, new PayrollDecisionRequest("re-run"),
            CancellationToken.None, settlementDisposition: "FundsRecalled", settlementReference: "R1",
            remittanceDisposition: "FundsRecalled", remittanceReference: "R2");
        refused.Should().BeOfType<UnprocessableEntityObjectResult>();
        var body = Body(refused);
        body.Should().Contain("gl_period_closed");
        body.Should().Contain("2026-07", "the CLOSED settlement period must be named, not the accrual period");
        db.ChangeTracker.Clear();

        (await RunGl(db, run.Id)).Count.Should().Be(glBefore, "a refused void writes nothing");
        (await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == run.Id)).Status.Should().Be("Locked");

        (await Gl(db, tid).ReopenPeriod("2026-07", companyId, new GlPeriodActionRequest("payroll correction"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await Payroll(db, tid, Approver).VoidRun(run.Id, new PayrollDecisionRequest("re-run"), CancellationToken.None,
            settlementDisposition: "FundsRecalled", settlementReference: "R1",
            remittanceDisposition: "FundsRecalled", remittanceReference: "R2"))
            .Should().BeOfType<OkObjectResult>();
    }

    /// <summary>
    /// Doctrine (b), the standard controllership answer: the closed month STAYS closed and the reversal is
    /// booked into the current open month as a PRIOR-PERIOD ADJUSTMENT, carrying the original period as the
    /// line reference. Without this option, "keep the month closed" was literally unreachable — a void has
    /// to write somewhere.
    /// </summary>
    [Fact]
    public async Task Void_IntoAClosedPeriod_CanBeBookedAsAnAuditedPriorPeriodAdjustment()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var (companyId, _, run) = await SeedRun(db, tid, 2026, 6);
        await ProcessApproveLock(db, tid, run.Id);

        (await Gl(db, tid).ClosePeriod("2026-06", companyId, new GlPeriodActionRequest("year-end"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        (await Payroll(db, tid, Approver).VoidRun(run.Id, new PayrollDecisionRequest("prior-period correction"),
            CancellationToken.None, priorPeriodAdjustment: true, adjustmentPeriod: "2026-09"))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var gl = await RunGl(db, run.Id);
        gl.Where(l => l.EventType == GlEventTypes.Void).Should().OnlyContain(l => l.Period == "2026-09",
            "the reversal lands in the open month");
        gl.Where(l => l.EventType == GlEventTypes.Void).Should().OnlyContain(l => l.SourceEntityRef == "2026-06",
            "…while still saying which closed month it corrects");
        gl.Where(l => l.Period == "2026-06").Should().OnlyContain(l => l.IsReversed,
            "the closed month is untouched — its journal stands, flagged as reversed");
        var (dr, cr) = Totals(gl.Where(l => l.Period == "2026-09"));
        dr.Should().Be(cr, "the adjustment journal balances on its own");
        // The period was never reopened.
        (await db.GlPeriodCloses.AsNoTracking().FirstAsync(p => p.Period == "2026-06")).Status
            .Should().Be(GlPeriodStatuses.Closed);
    }

    // ══ 4. Replacement lifecycle ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The end-to-end recovery: void a locked+paid+remitted month, create a Replacement, and run it all
    /// the way through to settled + remitted. The corrected month gets its OWN journal, batch and ERP
    /// document; the bad month stays frozen and attributable; every control account nets to zero across
    /// the pair; and the employee is left with exactly ONE payslip for the month.
    /// </summary>
    [Fact]
    public async Task Replacement_FullLifecycle_LeavesOneLiveMonth_AndAllControlAccountsFlat()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var (companyId, emp, run) = await SeedRun(db, tid, 2026, 6);
        await ProcessApproveLock(db, tid, run.Id);
        await SettleAndRemit(db, tid, run.Id);

        (await Payroll(db, tid, Approver).VoidRun(run.Id, new PayrollDecisionRequest("wrong salary applied"),
            CancellationToken.None, settlementDisposition: "FundsRecalled", settlementReference: "RECALL",
            remittanceDisposition: "FundsRecalled", remittanceReference: "REFUND"))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var created = await Payroll(db, tid, Preparer).CreateRun(
            new CreatePayrollRunRequest(2026, 6, companyId, PayrollRunTypes.Replacement, run.Id), CancellationToken.None);
        created.Should().BeOfType<CreatedResult>();
        var replacement = created.As<CreatedResult>().Value.As<PayrollRun>();
        db.ChangeTracker.Clear();

        // The replaced run's paid population is inherited as explicit Include rows, so a hold-out would
        // survive recovery instead of being silently re-included.
        (await db.PayrollRunEmployeeSelections.AsNoTracking().Where(s => s.PayrollRunId == replacement.Id).ToListAsync())
            .Should().ContainSingle().Which.Mode.Should().Be(PayrollRunSelectionModes.Include);

        await ProcessApproveLock(db, tid, replacement.Id);
        await SettleAndRemit(db, tid, replacement.Id);

        // The replacement's own journal balances and is LIVE.
        var newGl = await RunGl(db, replacement.Id);
        var (ndr, ncr) = Totals(newGl);
        ndr.Should().Be(ncr);
        newGl.Should().Contain(l => l.EventType == GlEventTypes.Accrual && !l.IsReversed);

        // Across BOTH runs every control account is flat, and cash carries exactly ONE month's outflow.
        foreach (var acct in new[] { "2100", "2101", "2106", "2102", "2107", "2300", "1400", "1410" })
            (await Balance(db, tid, acct)).Should().Be(0m, $"{acct} must be flat across the recovered month");
        var cashOut = await Balance(db, tid, "1000 - Cash/Bank");
        var replacementNet = (await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == replacement.Id)).TotalNetSalary;
        cashOut.Should().BeGreaterThan(0m);
        // The voided month's cash was recalled, so lifetime cash out is one month's worth.
        var replacementCash = newGl.Where(l => l.CreditAccount.Contains("1000")).Sum(l => l.Amount)
                            - newGl.Where(l => l.DebitAccount.Contains("1000")).Sum(l => l.Amount);
        cashOut.Should().Be(replacementCash, "the recalled month must not leave a second outflow behind");
        replacementCash.Should().BeGreaterThan(replacementNet, "net pay + statutory remittance both left the bank");

        // The voided run is FROZEN, not rewritten.
        var oldGl = await RunGl(db, run.Id);
        oldGl.Where(l => l.EventType == GlEventTypes.Accrual).Should().OnlyContain(l => l.IsReversed);
        oldGl.Should().Contain(l => l.EventType == GlEventTypes.Void);
        (await db.PayrollSlips.AsNoTracking().Where(s => s.RunId == run.Id).ToListAsync())
            .Should().OnlyContain(s => s.Status == "Voided");

        // T11 — the employee sees exactly ONE payslip for the month, and it is the corrected one.
        var mobile = await Mobile(db, tid, emp.Id).MyPayslips(emp.Id, CancellationToken.None);
        var rows = ((System.Collections.IEnumerable)mobile.As<OkObjectResult>().Value!).Cast<object>().ToList();
        rows.Should().ContainSingle("a voided month must disappear and a replacement must not double it");
    }

    // ══ 5. GOSI reconciliation is reversal-aware (requirement 4 / POD-A1) ══════════════════════════

    /// <summary>
    /// POD-A1's bar is "deducted == report == GL". The per-run GL tie-out read EVERY Payroll row for the
    /// run with no EventType and no !IsReversed filter, surviving only because a contra's Description is
    /// rewritten and so failed the `DED:` prefix test — a string coincidence, not a control. And
    /// ReconcileAsync reported a VOIDED run as live and tied out, which is the worst possible failure mode
    /// for a statutory filing source.
    /// </summary>
    [Fact]
    public async Task GosiReconciliation_OnAVoidedRun_ReportsItSuperseded_AndCountsNoLiveGl()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var (companyId, _, run) = await SeedRun(db, tid, 2026, 6);
        await ProcessApproveLock(db, tid, run.Id);

        var svc = new GosiReconciliationService(db, new _B3PackResolver());
        var live = await svc.ReconcileAsync(tid, await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == run.Id), CancellationToken.None);
        live.GlPosted.Should().BeTrue();
        live.GlEmployeeLiability.Should().Be(live.ActualEmployeeTotal, "POD-A1: deducted == GL");
        live.IsVoided.Should().BeFalse();

        (await Payroll(db, tid, Approver).VoidRun(run.Id, new PayrollDecisionRequest("re-run"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var voided = await svc.ReconcileAsync(tid, await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == run.Id), CancellationToken.None);
        voided.IsVoided.Should().BeTrue();
        voided.RunStatus.Should().Be("Voided");
        voided.FilingStatusNote.Should().NotBeNull("a voided run must never be silently reported as filable");
        voided.GlPosted.Should().BeFalse("the accrual is reversed — there is no live liability to tie out to");

        // …and the replacement is named as the successor.
        (await Payroll(db, tid, Preparer).CreateRun(
            new CreatePayrollRunRequest(2026, 6, companyId, PayrollRunTypes.Replacement, run.Id), CancellationToken.None))
            .Should().BeOfType<CreatedResult>();
        db.ChangeTracker.Clear();
        var superseded = await svc.ReconcileAsync(tid, await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == run.Id), CancellationToken.None);
        superseded.SupersededByRunId.Should().NotBeNull();
        superseded.FilingStatusNote.Should().Contain("SUPERSEDED");
    }

    // ══ 6. The IsResolved dead end ════════════════════════════════════════════════════════════════

    /// <summary>
    /// D2 — before B3, IsResolved was read at Approve, Lock, the overview card and the readiness wizard,
    /// and SET BY NOTHING. Any blocking Error on a Processed run was exit-only-via-Void.
    ///
    /// The override that replaces the dead end has four controls, all asserted here: a substantive reason,
    /// identity-based segregation of duties, a published default-deny code list, and durability across
    /// /validate's delete-and-rebuild.
    /// </summary>
    [Fact]
    public async Task Override_ClearsABlockingError_WithReason_Actor_AndDurabilityAcrossValidate()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var (_, emp, run) = await SeedRun(db, tid);
        // No IBAN ⇒ MISSING_IBAN is a blocking Error, and it is a genuine judgement (paid by cheque).
        db.EmployeePayrollProfiles.RemoveRange(db.EmployeePayrollProfiles.Where(p => p.TenantId == tid));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        (await Payroll(db, tid, Preparer).Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        var result = await db.PayrollValidationResults.AsNoTracking()
            .FirstAsync(r => r.PayrollRunId == run.Id && r.Code == "MISSING_IBAN");

        // Approve is blocked — the dead end.
        (await Payroll(db, tid, Approver).Approve(run.Id, new PayrollDecisionRequest("ok"), CancellationToken.None))
            .Should().BeOfType<UnprocessableEntityObjectResult>();
        db.ChangeTracker.Clear();

        // (1) A blank / trivial reason is refused.
        (await Payroll(db, tid, Approver).ResolveValidationResult(run.Id, result.Id, new PayrollReasonRequest("ok"), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
        db.ChangeTracker.Clear();

        // (2) IDENTITY-based SoD: the person who processed the run cannot clear its errors. Permission-based
        //     SoD would be theatre — AuthSeeder grants payroll.write AND payroll.approve to the same roles.
        var self = await Payroll(db, tid, Preparer).ResolveValidationResult(
            run.Id, result.Id, new PayrollReasonRequest("Paid by cheque, bank details pending"), CancellationToken.None);
        self.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
        Body(self).Should().Contain("maker_checker_violation");
        db.ChangeTracker.Clear();

        // A different approver, with a substantive reason, may clear it.
        (await Payroll(db, tid, Approver).ResolveValidationResult(
            run.Id, result.Id, new PayrollReasonRequest("Paid by cheque; IBAN collection in progress (HR-4471)"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var cleared = await db.PayrollValidationResults.AsNoTracking().FirstAsync(r => r.Id == result.Id);
        cleared.IsResolved.Should().BeTrue();
        cleared.ResolvedByUserId.Should().Be(Approver);
        cleared.ResolvedReason.Should().Contain("HR-4471");

        // (4) DURABILITY — /validate deletes and rebuilds every result row. Without re-applying the durable
        //     override the run would silently re-stick, with no trace of why the sign-off vanished.
        (await Payroll(db, tid, Preparer).Validate(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        var rebuilt = await db.PayrollValidationResults.AsNoTracking()
            .FirstAsync(r => r.PayrollRunId == run.Id && r.Code == "MISSING_IBAN");
        rebuilt.IsResolved.Should().BeTrue("an override must survive a re-validate");
        rebuilt.ResolvedReason.Should().Contain("HR-4471");

        // The approver must ACKNOWLEDGE the override by count — restating it in a body is not a control.
        var unacknowledged = await Payroll(db, tid, Approver).Approve(run.Id, new PayrollDecisionRequest("ok"), CancellationToken.None);
        unacknowledged.Should().BeOfType<ConflictObjectResult>();
        Body(unacknowledged).Should().Contain("overridden_errors_not_acknowledged");
        db.ChangeTracker.Clear();

        (await Payroll(db, tid, Approver).Approve(run.Id,
            new PayrollDecisionRequest("reviewed", ExpectedOverriddenCount: 1), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        // …and the whole thing is on the tamper-evident payroll audit chain, with actor and reason.
        var audit = await db.PayrollAuditLogs.AsNoTracking()
            .FirstAsync(a => a.TenantId == tid && a.Action == "payroll.validation.overridden");
        audit.UserId.Should().Be(Approver);
        audit.MetadataJson.Should().Contain("HR-4471").And.Contain("MISSING_IBAN");
    }

    /// <summary>
    /// C6 — ALREADY_PAID_THIS_PERIOD can NEVER be overridden. It is the only control in this codebase that
    /// looks across runs, and clearing it authorises paying one person two full salaries in one month in a
    /// product whose next step wires money via WPS. Its exit is REOPEN, which unfreezes the population
    /// selector so the operator can do what the error actually asks.
    /// </summary>
    [Fact]
    public async Task Override_OfANonOverridableCode_IsRefused_AndNamesTheRealExit()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var (companyId, emp, run) = await SeedRun(db, tid);
        await ProcessApproveLock(db, tid, run.Id);

        // A second, full-recurring run for the same period → ALREADY_PAID_THIS_PERIOD on the employee.
        var second = (await Payroll(db, tid, Preparer).CreateRun(
            new CreatePayrollRunRequest(2026, 6, companyId, PayrollRunTypes.OffCycle, IncludesRecurringPay: true),
            CancellationToken.None)).As<CreatedResult>().Value.As<PayrollRun>();
        (await Payroll(db, tid, Preparer).UpsertRunSelection(second.Id,
            new PayrollRunSelectionRequest("Include", "missed joiner", new List<int> { emp.Id }), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await Payroll(db, tid, Preparer).Process(second.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var blocking = await db.PayrollValidationResults.AsNoTracking()
            .FirstAsync(r => r.PayrollRunId == second.Id && r.Code == "ALREADY_PAID_THIS_PERIOD");

        var refused = await Payroll(db, tid, Approver).ResolveValidationResult(
            second.Id, blocking.Id, new PayrollReasonRequest("Finance director approved the second payment"), CancellationToken.None);
        refused.Should().BeOfType<UnprocessableEntityObjectResult>();
        var body = Body(refused);
        body.Should().Contain("code_not_overridable");
        body.Should().Contain("exclude the employee", "the refusal must name the exit, not just say no");
        db.ChangeTracker.Clear();
        (await db.PayrollValidationResults.AsNoTracking().FirstAsync(r => r.Id == blocking.Id))
            .IsResolved.Should().BeFalse();

        // The published split is part of the contract, not an implementation detail.
        PayrollValidationOverridePolicy.IsOverridable("ALREADY_PAID_THIS_PERIOD").Should().BeFalse();
        PayrollValidationOverridePolicy.IsOverridable("GL_WILL_NOT_BALANCE").Should().BeFalse();
        PayrollValidationOverridePolicy.IsOverridable("NEGATIVE_NET").Should().BeFalse();
        PayrollValidationOverridePolicy.IsOverridable("A_CODE_NOBODY_HAS_CLASSIFIED").Should().BeFalse(
            "the policy is DEFAULT-DENY: a code added later is non-overridable until classified");
        PayrollValidationOverridePolicy.IsOverridable("MISSING_IBAN").Should().BeTrue();
    }

    // ══ 7. Reopen — the exit for a run that never posted GL ════════════════════════════════════════

    /// <summary>
    /// The real exit for ALREADY_PAID_THIS_PERIOD (and for "my inputs were wrong" generally). Reopen
    /// releases everything the run consumed, deletes its outputs — INCLUDING the ESS payslips, without
    /// which a re-process would leave the employee looking at pre-correction numbers forever — and
    /// unfreezes the population selector.
    /// </summary>
    [Fact]
    public async Task Reopen_ReleasesConsumption_DeletesEssPayslips_AndUnfreezesTheSelector()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var (companyId, emp, run) = await SeedRun(db, tid);
        var loan = new EmployeeLoan
        {
            TenantId = tid, CompanyId = companyId, EmployeeId = Guid.NewGuid(), EmployeeIntId = emp.Id,
            LoanNumber = "LN-1", Status = "Active", ApprovedAmount = 3_000m, OutstandingBalance = 3_000m,
            InstallmentAmount = 1_000m, ApprovedInstallments = 3,
        };
        db.EmployeeLoans.Add(loan);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        (await Payroll(db, tid, Preparer).Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await Payroll(db, tid, Preparer).GeneratePayslips(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await db.Payslips.AsNoTracking().CountAsync(p => p.PayrollRunId == run.Id)).Should().Be(1);
        (await db.EmployeeLoans.AsNoTracking().FirstAsync()).OutstandingBalance.Should().Be(2_000m);

        // The selector is FROZEN on a Processed run — which is why the error's own remedy was unreachable.
        (await Payroll(db, tid, Preparer).UpsertRunSelection(run.Id,
            new PayrollRunSelectionRequest("Exclude", "paid elsewhere", new List<int> { emp.Id }), CancellationToken.None))
            .Should().NotBeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        (await Payroll(db, tid, Approver).ReopenRun(run.Id, new PayrollReasonRequest("wrong attendance import"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var reopened = await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == run.Id);
        reopened.Status.Should().Be("Draft");
        reopened.ProcessedByUserId.Should().BeNull();
        (await db.EmployeeLoans.AsNoTracking().FirstAsync()).OutstandingBalance
            .Should().Be(3_000m, "the EMI is given back — otherwise the re-run takes the NEXT installment too");
        (await db.PayrollSlips.AsNoTracking().CountAsync(s => s.RunId == run.Id)).Should().Be(0);
        (await db.Payslips.AsNoTracking().CountAsync(p => p.PayrollRunId == run.Id))
            .Should().Be(0, "GeneratePayslips SKIPS an employee who already has a payslip, so a surviving row " +
                            "would strand the pre-correction gross/net in ESS forever");
        (await db.PayslipComponents.AsNoTracking().CountAsync()).Should().Be(0);

        // …and the remedy the validation errors point at is now reachable.
        (await Payroll(db, tid, Preparer).UpsertRunSelection(run.Id,
            new PayrollRunSelectionRequest("Exclude", "paid elsewhere", new List<int> { emp.Id }), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();

        var audit = await db.PayrollAuditLogs.AsNoTracking()
            .FirstAsync(a => a.TenantId == tid && a.Action == "payroll.run.reopened");
        audit.UserId.Should().Be(Approver);
        audit.MetadataJson.Should().Contain("wrong attendance import");
    }

    /// <summary>
    /// The line between the two recovery paths. A run that has posted GL is recovered by void →
    /// Replacement, NEVER by re-processing itself: Process deletes the slips, earnings and deductions that
    /// are the supporting detail behind journals an auditor can still see in the ledger.
    /// </summary>
    [Fact]
    public async Task Reopen_IsRefusedOnceTheRunHasPostedGl_AndPointsAtTheReplacementPath()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var (_, _, run) = await SeedRun(db, tid);
        await ProcessApproveLock(db, tid, run.Id);

        // A Locked run is refused on status alone…
        var locked = await Payroll(db, tid, Approver).ReopenRun(run.Id, new PayrollReasonRequest("changed my mind"), CancellationToken.None);
        locked.Should().BeOfType<BadRequestObjectResult>();
        Body(locked).Should().Contain("not_reopenable").And.Contain("Replacement");
        db.ChangeTracker.Clear();

        // …and the GL guard is the one that matters: even at a status reopen normally allows, a run that
        // has posted a journal cannot have its supporting detail deleted out from under the ledger.
        var tracked = await db.PayrollRuns.FirstAsync(r => r.Id == run.Id);
        tracked.Status = "Approved";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var res = await Payroll(db, tid, Approver).ReopenRun(run.Id, new PayrollReasonRequest("changed my mind"), CancellationToken.None);
        res.Should().BeOfType<UnprocessableEntityObjectResult>();
        Body(res).Should().Contain("run_has_gl").And.Contain("Replacement");
        db.ChangeTracker.Clear();
        (await db.PayrollSlips.AsNoTracking().CountAsync(s => s.RunId == run.Id))
            .Should().BeGreaterThan(0, "the detail behind a posted journal must survive the refusal");
    }

    // ══ 8. The surfaces a voided run must no longer expose ═════════════════════════════════════════

    [Fact]
    public async Task VoidedRun_CannotBeDrivenToPaid_OrPostedToErp()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var (_, _, run) = await SeedRun(db, tid);
        await ProcessApproveLock(db, tid, run.Id);
        var ctrl = Payroll(db, tid, Approver);
        (await ctrl.CreatePaymentBatch(run.Id, new PayrollPaymentBatchRequest("WPS", "SAR"), CancellationToken.None))
            .Should().BeOfType<CreatedResult>();
        var batch = await db.PayrollPaymentBatches.FirstAsync(b => b.PayrollRunId == run.Id);
        batch.WpsStatus = WpsStatuses.Accepted;
        db.WPSFileBatches.Add(new WPSFileBatch { TenantId = tid, PaymentBatchId = batch.Id, SifFileName = "f.sif", FilingStatus = WpsStatuses.Accepted });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        (await Payroll(db, tid, Approver).VoidRun(run.Id, new PayrollDecisionRequest("re-run"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        // WpsTransitions allows Accepted → Paid, and pre-B3 the void left the batch sitting on that edge.
        var paid = await Payroll(db, tid, Approver).UpdateWpsStatus(
            batch.Id, new WpsStatusRequest(WpsStatuses.Paid, null, "REF"), CancellationToken.None);
        paid.Should().BeOfType<UnprocessableEntityObjectResult>();
        Body(paid).Should().Contain("run_voided");

        // UpdateErpPostingStatus only required that SOME Payroll GL row existed — and contras qualify.
        var erp = await Payroll(db, tid, Approver).UpdateErpPostingStatus(
            run.Id, new ErpPostingStatusRequest(ErpPostingStatuses.ReadyForErp), CancellationToken.None);
        erp.Should().BeOfType<UnprocessableEntityObjectResult>();
        Body(erp).Should().Contain("run_voided");
        db.ChangeTracker.Clear();

        (await db.WPSFileBatches.AsNoTracking().FirstAsync()).FilingStatus
            .Should().Be(WpsStatuses.Voided, "the statutory filing must be marked withdrawn, not left Accepted");
    }

    /// <summary>
    /// M9's recovery case. The sibling-run duplicate-SIF guard reads non-voided siblings, so it goes
    /// SILENT for a Replacement — exactly when it matters most. Voiding withdraws the filing in THIS
    /// system; it does not retrieve the file from the bank, which still treats the next SIF for the same
    /// (establishment, salary month) as a replacement of the first.
    /// </summary>
    [Fact]
    public async Task Replacement_WpsExport_WarnsThatTheReplacedRunsFileIsStillAtTheBank()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var (companyId, _, run) = await SeedRun(db, tid);
        await ProcessApproveLock(db, tid, run.Id);
        (await Payroll(db, tid, Approver).CreatePaymentBatch(run.Id, new PayrollPaymentBatchRequest("WPS", "SAR"), CancellationToken.None))
            .Should().BeOfType<CreatedResult>();
        var oldBatch = await db.PayrollPaymentBatches.FirstAsync(b => b.PayrollRunId == run.Id);
        db.WPSFileBatches.Add(new WPSFileBatch
        {
            TenantId = tid, PaymentBatchId = oldBatch.Id, SifFileName = "june.sif",
            FilingStatus = WpsStatuses.Submitted, SubmissionReference = "MUDAD-88213",
            SubmittedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        (await Payroll(db, tid, Approver).VoidRun(run.Id, new PayrollDecisionRequest("re-run"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var replacement = (await Payroll(db, tid, Preparer).CreateRun(
            new CreatePayrollRunRequest(2026, 6, companyId, PayrollRunTypes.Replacement, run.Id), CancellationToken.None))
            .As<CreatedResult>().Value.As<PayrollRun>();
        await ProcessApproveLock(db, tid, replacement.Id);
        (await Payroll(db, tid, Approver).CreatePaymentBatch(replacement.Id, new PayrollPaymentBatchRequest("WPS", "SAR"), CancellationToken.None))
            .Should().BeOfType<CreatedResult>();
        var newBatch = await db.PayrollPaymentBatches.AsNoTracking().FirstAsync(b => b.PayrollRunId == replacement.Id);
        db.ChangeTracker.Clear();

        var res = await Payroll(db, tid, Approver).GenerateWps(newBatch.Id, false, false, CancellationToken.None);
        res.Should().BeOfType<UnprocessableEntityObjectResult>();
        var body = Body(res);
        body.Should().Contain("replaced_run_wps_export_exists");
        body.Should().Contain("MUDAD-88213", "the reference the bank actually holds must be shown, which is " +
                                             "why the void PRESERVES it rather than clearing it");
    }

    // ══ 9. The recovery path must not make the operator discover it one wall at a time ════════════

    /// <summary>
    /// A settled + remitted month used to refuse the void THREE TIMES IN A ROW — remittance disposition,
    /// then settlement disposition, then the settlement recall reference — because the elections were
    /// evaluated inside the unwind loop, which returned on the FIRST journal that needed one. Each refusal
    /// looked like the last obstacle, so neither an operator nor a UI could know up front what the void
    /// would ultimately require.
    ///
    /// The elections are now resolved as a SET before any contra is built: ONE refusal that names every
    /// outstanding election with its amount, account and request field, and flags that a reference becomes
    /// mandatory if a recall is elected — so the whole decision is collectible in a single prompt, and a
    /// caller who supplies it is not refused at all. The primary error code is unchanged.
    /// </summary>
    [Fact]
    public async Task Void_NamesEveryOutstandingCashElectionAtOnce_NotOneWallAtATime()
    {
        var (db, conn) = NewDb(); using var _ = conn;
        var tid = Guid.NewGuid();
        var (_, _, run) = await SeedRun(db, tid);
        await ProcessApproveLock(db, tid, run.Id);
        await SettleAndRemit(db, tid, run.Id);
        var glBefore = (await RunGl(db, run.Id)).Count;

        // ── One call, nothing stated. Pre-fix this named ONLY the remittance. ─────────────────────
        var refused = await Payroll(db, tid, Approver).VoidRun(
            run.Id, new PayrollDecisionRequest("Wrong salary master data"), CancellationToken.None);
        refused.Should().BeOfType<UnprocessableEntityObjectResult>();
        var body = Body(refused);

        body.Should().Contain("remittance_disposition_required",
            "the primary error code is still the first deficiency in unwind order — callers pinned to it are unaffected");
        body.Should().Contain("settlement_disposition_required",
            "THE FIX: the SECOND election must be disclosed by the FIRST refusal, not discovered by a second round trip");
        body.Should().Contain("requiredElections");
        body.Should().Contain("referenceRequiredIfRecalled",
            "the caller must be told a recall will also demand evidence, so the reference can be collected in the same prompt");
        body.Should().Contain("outstandingElectionCount");

        var payload = JsonSerializer.Deserialize<JsonElement>(body);
        payload.GetProperty("detail").GetProperty("outstandingElectionCount").GetInt32()
            .Should().Be(2, "net settlement and the statutory remittance are two distinct cash decisions");

        // A refused void writes NOTHING — the guard runs before the first contra line is built.
        (await RunGl(db, run.Id)).Count.Should().Be(glBefore);
        (await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == run.Id)).Status
            .Should().Be("Locked", "a refused void must leave the month exactly as it was");
        db.ChangeTracker.Clear();

        // ── Everything the ONE refusal asked for, supplied together → the void proceeds. ──────────
        var ok = await Payroll(db, tid, Approver).VoidRun(
            run.Id, new PayrollDecisionRequest("Wrong salary master data"), CancellationToken.None,
            settlementDisposition: PayrollVoidDispositions.FundsRecalled,  settlementReference: "RECALL-NET-1",
            remittanceDisposition: PayrollVoidDispositions.FundsRecalled,  remittanceReference: "RECALL-GOSI-1");
        ok.Should().BeOfType<OkObjectResult>("a caller who answers the single prompt in full is never refused again");

        (await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == run.Id)).Status.Should().Be("Voided");
        var (dr, cr) = Totals(await RunGl(db, run.Id));
        dr.Should().Be(cr, "every journal the void wrote must still balance");
    }

    // ══ 10. A recovered month must actually reach the employee ═══════════════════════════════════

    /// <summary>
    /// Lock is the ONLY writer of <c>IsPublishedToEss</c>, and the void is the only un-publisher. So a
    /// payslip GENERATED AFTER the lock was invisible to the employee forever, with nothing reporting it.
    ///
    /// That is a live trap in precisely this pod's flow. Recovery runs void → Replacement → process → lock
    /// → payment batch (CreatePaymentBatch requires a Locked run, so "lock, then produce the documents" is
    /// the natural operator order), and the void has ALREADY un-published the bad month's slips. Generating
    /// after the lock therefore left the employee with NO visible payslip for that month at all: the
    /// corrected one never published, the original withdrawn.
    ///
    /// Publishing is gated on the run being at or past Lock, so a Draft/Processed run is never published
    /// early and a VOIDED run is never re-published — the withdrawal stays sticky.
    /// </summary>
    [Fact]
    public async Task PayslipsGeneratedAfterTheLock_StillReachEss_SoARecoveredMonthIsVisible()
    {
        var (db, conn) = NewDb(); using var _ = conn;
        var tid = Guid.NewGuid();
        var (_, emp, run) = await SeedRun(db, tid);

        // Process, then lock WITHOUT generating payslips first — the order the recovery runbook invites.
        (await Payroll(db, tid, Preparer).Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        // Control: on a run that has NOT been locked, generation must not publish anything early.
        (await Payroll(db, tid, Preparer).GeneratePayslips(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await db.Payslips.AsNoTracking().Where(p => p.PayrollRunId == run.Id).ToListAsync())
            .Should().OnlyContain(p => !p.IsPublishedToEss,
                "a Processed run is not final — publishing it early would show the employee figures that can still change");

        // Wipe them so this is genuinely a FIRST generation that happens after the lock.
        await db.PayslipComponents.Where(c => db.Payslips
            .Where(p => p.PayrollRunId == run.Id).Select(p => p.Id).Contains(c.PayslipId)).ExecuteDeleteAsync();
        await db.Payslips.Where(p => p.PayrollRunId == run.Id).ExecuteDeleteAsync();
        db.ChangeTracker.Clear();

        var toApprove = await db.PayrollRuns.FirstAsync(r => r.Id == run.Id);
        toApprove.Status = "Approved";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        (await Payroll(db, tid, Approver).Lock(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        (await db.Payslips.AsNoTracking().CountAsync(p => p.PayrollRunId == run.Id))
            .Should().Be(0, "the lock had nothing to publish — this is the exact hole being closed");

        // THE FIX: generating after the lock publishes.
        (await Payroll(db, tid, Preparer).GeneratePayslips(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var published = await db.Payslips.AsNoTracking().Where(p => p.PayrollRunId == run.Id).ToListAsync();
        published.Should().ContainSingle().Which.EmployeeId.Should().Be(emp.Id);
        published[0].IsPublishedToEss.Should().BeTrue(
            "a locked run's payslip is final by definition — generating it must not hide it from the employee");
        published[0].PublishedAtUtc.Should().NotBeNull();

        // ── And the withdrawal stays sticky: a VOIDED run must never be re-published by generation. ──
        (await Payroll(db, tid, Approver).VoidRun(run.Id, new PayrollDecisionRequest("bad month"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await db.Payslips.AsNoTracking().Where(p => p.PayrollRunId == run.Id).ToListAsync())
            .Should().OnlyContain(p => !p.IsPublishedToEss, "the void un-publishes");

        (await Payroll(db, tid, Preparer).GeneratePayslips(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await db.Payslips.AsNoTracking().Where(p => p.PayrollRunId == run.Id).ToListAsync())
            .Should().OnlyContain(p => !p.IsPublishedToEss,
                "a voided month must stay withdrawn — re-publishing it would show the employee a month that no longer exists");
    }
}

// ── Test doubles (file-scoped) ──────────────────────────────────────────────────

file static class _B3Rules
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

file sealed class _B3Scope : Zayra.Api.Application.Common.IDataScopeService
{
    public Task<Zayra.Api.Application.Common.DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Application.Common.DataScope { Level = Zayra.Api.Application.Common.DataScopeLevel.Organization, AllowedEmployeeIds = null });
}

file sealed class _B3Http : IHttpContextAccessor
{
    public _B3Http(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _B3Notifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _B3PackResolver : ICountryPackResolver
{
    private static readonly KsaDeductionCalculator _calc = new(_B3Rules.Rules);
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => cc == "SAU" ? _calc : new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new KsaWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class _B3Letters : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class _B3Storage : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument(file.FileName, file.ContentType, "storage/test", "/tmp/test"));
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public string ResolvePath(string storageUrl) => "/tmp/test";
}
