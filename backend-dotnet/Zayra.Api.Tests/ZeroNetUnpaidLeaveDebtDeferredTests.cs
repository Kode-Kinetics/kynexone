using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;
using static Zayra.Api.Tests.PayNext;

namespace Zayra.Api.Tests;

/// <summary>
/// A recurring-run employee whose unpaid leave takes the whole month nets exactly zero: the leave takes the whole earned wage
/// (the per-slice cap stops it taking more), and any loan or advance instalment, with nothing left to fund it, is carried
/// forward (never written off). That used to raise the non-overridable ZERO_NET_WITH_GROSS Error, so the run could not be
/// approved or locked until the person was excluded. It is now a DISTINCT Warning, <see cref="PayrollValidationEngine.ZeroNetUnpaidLeave"/>,
/// raised ONLY when net is 0, gross is positive, gross − deductions is exactly net (nothing hidden by the clamp at zero) and
/// the month's own unpaid-leave deduction is the whole of the employee's deductions, with or without a deferred instalment.
/// The approver must acknowledge it by count, and the count is sealed into the approval's audit entry. Every other zero net
/// keeps the Error (PayrollOvertimeLopTests "LOP exceeding salary blocks" stays unchanged).
/// </summary>
public class ZeroNetUnpaidLeaveDebtDeferredTests
{
    private const string Warning = PayrollValidationEngine.ZeroNetUnpaidLeave;

    // ══ Engine: the rule, both ways ═══════════════════════════════════════════════════════════════

    [Fact]
    public void UnpaidLeaveTakesTheWholeWage_AndTheInstalmentWasDeferred_IsAWarning_NotTheError()
    {
        var results = Engine(gross: 6_000m, deductions: 6_000m, net: 0m, deferred: true, ("LEAVE", "Leave", 6_000m));

        results.Should().ContainSingle(r => r.Code == Warning).Which.Severity.Should().Be("Warning");
        results.Single(r => r.Code == Warning).Message.Should().Contain("E1").And.Contain("6,000.00").And.Contain("instalment");
        results.Should().NotContain(r => r.Code == "ZERO_NET_WITH_GROSS");
    }

    /// <summary>Deliberately changed (CTO decision, P2-3): this used to pin the Error for a full unpaid month with no loan. The
    /// per-slice cap means a full unpaid month legitimately earns nothing; the Error was for LOP EXCEEDING salary, which the cap
    /// now prevents. So it is the same explained Warning, without the instalment sentence.</summary>
    [Fact]
    public void UnpaidLeaveTakesTheWholeWage_WithNoInstalmentAtAll_IsTheSameWarning()
    {
        var results = Engine(gross: 6_000m, deductions: 6_000m, net: 0m, deferred: false, ("LEAVE", "Leave", 6_000m));

        results.Should().ContainSingle(r => r.Code == Warning).Which.Severity.Should().Be("Warning");
        results.Single(r => r.Code == Warning).Message.Should().NotContain("instalment");
        results.Should().NotContain(r => r.Code == "ZERO_NET_WITH_GROSS");
    }

    [Fact]
    public void AnotherDeductionMakesUpPartOfTheGap_KeepsTheError_EvenWithADeferral()
    {
        var results = Engine(gross: 6_000m, deductions: 6_000m, net: 0m, deferred: true,
            ("LEAVE", "Leave", 5_000m), ("FIXED_DEDUCTION", "Manual", 1_000m));

        results.Should().Contain(r => r.Code == "ZERO_NET_WITH_GROSS" && r.Severity == "Error",
            "the unpaid leave does not account for the whole gap");
        results.Should().NotContain(r => r.Code == Warning);
    }

    [Fact]
    public void DeductionsAboveGross_ClampedToZero_KeepTheError_EvenWithADeferral()
    {
        var results = Engine(gross: 6_000m, deductions: 6_500m, net: 0m, deferred: true, ("LEAVE", "Leave", 6_500m));

        results.Should().Contain(r => r.Code == "ZERO_NET_WITH_GROSS" && r.Severity == "Error",
            "net reads zero only because of the clamp: that is an over-deduction");
        results.Should().NotContain(r => r.Code == Warning);
    }

    [Fact]
    public void TheWarningIsNotOnTheOverridableList_AndTheErrorStaysNonOverridable()
    {
        PayrollValidationOverridePolicy.IsOverridable("ZERO_NET_WITH_GROSS").Should().BeFalse();
        PayrollValidationOverridePolicy.IsOverridable(Warning).Should().BeFalse("a Warning never blocks, so there is nothing to override");
    }

    // ══ Process → validate → approve → lock → batch, on the real controller ═══════════════════════

    [Fact]
    public async Task FullUnpaidMonthPlusLoan_ProcessesToTheWarning_SurvivesValidate_Approves_Locks_AndIsLeftOutOfTheBankFile()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        var leaver = await BasicOnlyExpat(db, co);
        var colleague = await BasicOnlyExpat(db, co);
        await AddUnpaidLeave(db, co, leaver.Id, new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30),
            ("2026-06", 30m, Basic, new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30), "Pending"));
        await AddLoan(db, co, leaver.Id, outstanding: 5_000m, instalment: 1_000m);
        var run = await AddRun(db, co, 2026, 6);

        await Process(db, co, run.Id);

        var slip = (await Slip(db, run.Id, leaver.Id))!;
        (slip.GrossSalary, slip.Deductions, slip.NetSalary).Should().Be((Basic, Basic, 0m), "the leave takes exactly the whole wage");
        var findings = await Findings(db, run.Id);
        findings.Should().ContainSingle(r => r.Code == Warning && r.EmployeeId == leaver.Id).Which.Severity.Should().Be("Warning");
        findings.Should().NotContain(r => r.Code == "ZERO_NET_WITH_GROSS");
        findings.Should().Contain(r => r.Code == "WARN_EMI_DEFERRED_PRORATED_PERIOD" && r.EmployeeId == leaver.Id, "the instalment is carried forward");
        (await db.EmployeeLoans.AsNoTracking().SingleAsync(l => l.EmployeeIntId == leaver.Id)).OutstandingBalance.Should().Be(5_000m, "nothing is written off");

        // /validate rebuilds the findings wholesale: the deferral and the warning survive it, twice.
        for (var i = 0; i < 2; i++)
        {
            (await Payroll(db, co.TenantId, Maker).Validate(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
            db.ChangeTracker.Clear();
            var revalidated = await Findings(db, run.Id);
            revalidated.Should().ContainSingle(r => r.Code == Warning && r.EmployeeId == leaver.Id);
            revalidated.Should().ContainSingle(r => r.Code == "WARN_EMI_DEFERRED_PRORATED_PERIOD" && r.EmployeeId == leaver.Id);
            revalidated.Should().NotContain(r => r.Code == "ZERO_NET_WITH_GROSS");
        }

        await ReconTestSupport.SignOffAsync(db, run.Id);
        db.ChangeTracker.Clear();
        // The warning must be ACKNOWLEDGED by count, like the pay-population warnings: a bare approval is refused.
        var unacknowledged = await Payroll(db, co.TenantId, Checker).Approve(run.Id, new PayrollDecisionRequest("reviewed"), CancellationToken.None);
        Status(unacknowledged).Should().Be(409);
        Json(unacknowledged).GetProperty("error").GetString().Should().Be("zero_net_not_acknowledged");
        Json(unacknowledged).GetProperty("zeroNetCount").GetInt32().Should().Be(1);
        db.ChangeTracker.Clear();
        var approved = await Payroll(db, co.TenantId, Checker).Approve(run.Id, new PayrollDecisionRequest("reviewed", ExpectedZeroNetCount: 1), CancellationToken.None);
        approved.Should().BeOfType<OkObjectResult>(Json(approved).ToString());
        // Who and when: the approval's audit entry (actor, timestamp) carries the acknowledged count and the employees.
        var audit = await db.PayrollAuditLogs.AsNoTracking()
            .Where(a => a.EntityId == run.Id.ToString() && a.Action.StartsWith("payroll.run.") && a.Action.EndsWith("approved")).ToListAsync();
        audit.Should().ContainSingle();
        audit[0].UserId.Should().Be(Checker);
        var data = System.Text.Json.JsonDocument.Parse(audit[0].MetadataJson).RootElement.GetProperty("data");
        data.GetProperty("zeroNetAcknowledged").GetInt32().Should().Be(1);
        data.GetProperty("zeroNetEmployees").EnumerateArray().Select(x => x.GetInt32()).Should().Equal(leaver.Id);
        db.ChangeTracker.Clear();
        await SetStatus(db, run.Id, "Approved");   // the Finance step of the ladder is not what this test is about
        var locked = await Payroll(db, co.TenantId, Checker).Lock(run.Id, CancellationToken.None);
        locked.Should().BeOfType<OkObjectResult>(Json(locked).ToString());
        db.ChangeTracker.Clear();

        var payer = Payroll(db, co.TenantId, Checker);
        payer.ControllerContext.HttpContext = Ctx(co.TenantId, Checker, "payroll.read", "payroll.export");   // the payment-batch key

        // The bank pre-check agrees with the batch: a zero net has no payable line, so it is not an error there either.
        var precheck = await payer.WpsValidation(run.Id, CancellationToken.None);
        precheck.Should().BeOfType<OkObjectResult>();
        var precheckJson = Json(precheck);
        precheckJson.GetProperty("blockingErrors").EnumerateArray()
            .Where(x => x.TryGetProperty("employeeId", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.Number && id.GetInt32() == leaver.Id)
            .Select(x => x.GetProperty("code").GetString()).Should().BeEmpty("the zero-net employee is left out of the bank file, so the pre-check must not flag them");
        db.ChangeTracker.Clear();

        var batch = await payer.CreatePaymentBatch(run.Id, new PayrollPaymentBatchRequest("WPS", "SAR"), CancellationToken.None);
        batch.Should().BeOfType<CreatedResult>(batch is ObjectResult ? Json(batch).ToString() : batch.GetType().Name);
        db.ChangeTracker.Clear();
        var records = await db.PayrollPaymentRecords.AsNoTracking().ToListAsync();
        records.Should().NotContain(r => r.EmployeeId == leaver.Id, "a zero net has nothing to send to a bank");
        records.Should().ContainSingle(r => r.EmployeeId == colleague.Id).Which.Amount.Should().BeGreaterThan(0m);
    }

    /// <summary>Deliberately changed (CTO decision, P2-3): a full unpaid month with no loan is the same Warning, and approves
    /// once acknowledged. It used to pin the blocking Error.</summary>
    [Fact]
    public async Task FullUnpaidMonthWithoutAnyLoan_IsTheWarning_AndApprovesOnceAcknowledged()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        var e = await BasicOnlyExpat(db, co);
        await AddUnpaidLeave(db, co, e.Id, new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30),
            ("2026-06", 30m, Basic, new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30), "Pending"));
        var run = await AddRun(db, co, 2026, 6);

        await Process(db, co, run.Id);

        var findings = await Findings(db, run.Id);
        findings.Should().ContainSingle(r => r.Code == Warning && r.EmployeeId == e.Id).Which.Severity.Should().Be("Warning");
        findings.Should().NotContain(r => r.Code == "ZERO_NET_WITH_GROSS");
        await ReconTestSupport.SignOffAsync(db, run.Id);
        db.ChangeTracker.Clear();
        var approved = await Payroll(db, co.TenantId, Checker).Approve(run.Id, new PayrollDecisionRequest("reviewed", ExpectedZeroNetCount: 1), CancellationToken.None);
        approved.Should().BeOfType<OkObjectResult>(Json(approved).ToString());
    }

    // ══ Harness ═══════════════════════════════════════════════════════════════════════════════════

    private static List<PayrollValidationResult> Engine(decimal gross, decimal deductions, decimal net, bool deferred,
        params (string Code, string Source, decimal Amount)[] lines)
    {
        var tid = Guid.NewGuid();
        var run = new PayrollRun { TenantId = tid, Year = 2026, Month = 6, Status = "Processed", RunType = PayrollRunTypes.Regular, IncludesRecurringPay = true };
        var company = new Company { TenantId = tid, LegalNameEn = "X", CountryCode = "SAU", DefaultCurrency = "SAR" };
        var slip = new PayrollSlip
        {
            TenantId = tid, RunId = run.Id, EmployeeId = 1, EmployeeCode = "E1", EmployeeName = "E One",
            GrossSalary = gross, Deductions = deductions, NetSalary = net, BasicSalary = gross,
        };
        var dedLines = lines.Select(l => new PayrollDeduction
        {
            TenantId = tid, PayrollRunId = run.Id, EmployeeId = 1, ComponentCode = l.Code, ComponentName = l.Code, Amount = l.Amount, Source = l.Source,
        }).ToList();
        // An employer contribution is never money taken from the employee: it must not count against the rule.
        dedLines.Add(new PayrollDeduction
        {
            TenantId = tid, PayrollRunId = run.Id, EmployeeId = 1, ComponentCode = "GOSI-OH-ER", ComponentName = "GOSI OH", Amount = 120m,
            Source = "Statutory", IsEmployerContribution = true,
        });
        return PayrollValidationEngine.Run(new PayrollValidationContext(
            run, new[] { slip }, Array.Empty<Employee>(), Array.Empty<EmployeeSalaryStructure>(),
            Array.Empty<EmployeePayrollProfile>(), dedLines, Array.Empty<PayrollEarning>(), company)
        {
            DebtDeferredEmployeeIds = deferred ? new HashSet<int> { 1 } : new HashSet<int>(),
        });
    }

    /// <summary>An expatriate (no employee GOSI share) on basic salary only, so an unpaid month takes exactly the whole wage.</summary>
    private static async Task<Employee> BasicOnlyExpat(Zayra.Api.Data.ZayraDbContext db, Co co)
    {
        var e = await AddEmployee(db, co, "Active");
        var tracked = await db.Employees.SingleAsync(x => x.Id == e.Id);
        tracked.Nationality = "Indian";
        var salary = await db.EmployeeSalaryStructures.SingleAsync(x => x.EmployeeId == e.Id);
        salary.HousingAllowance = 0m;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return e;
    }

    private static async Task AddLoan(Zayra.Api.Data.ZayraDbContext db, Co co, int employeeId, decimal outstanding, decimal instalment)
    {
        db.EmployeeLoans.Add(new EmployeeLoan
        {
            TenantId = co.TenantId, CompanyId = co.CompanyId, EmployeeId = Guid.NewGuid(), EmployeeIntId = employeeId,
            LoanNumber = $"LN-{employeeId}", Status = "Active", ApprovedAmount = outstanding, OutstandingBalance = outstanding,
            InstallmentAmount = instalment, TotalRepaid = 0m, ApprovedInstallments = (int)(outstanding / instalment),
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }
}
