using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// A cash early settlement of a payroll-collected loan (<c>LoansController.RecordRepayment</c>) and a payroll pass
/// (<see cref="PayrollRunProcessor.ProcessAsync"/>) that collects the same loan must never BOTH take the money: the
/// loan settled by cash while the payslip still withholds its instalment would collect the debt twice. Both paths hold
/// the <c>finance.loan</c> advisory lock for the loan, so whichever comes second sees what the first committed.
///
/// <para>Real Postgres only: each side has its own context and connection, and the test observes the waiting side in
/// <c>pg_stat_activity</c> instead of relying on timing.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class LoanPayoffPayrollProcessRacePostgresTests(PostgresFixture fixture)
{
    /// <summary>The payoff holds the loan and commits while the payroll pass is mid-flight (it has already read the
    /// loan as collectable). The pass must re-read the loan after the payoff and withhold nothing for it.</summary>
    [Fact]
    public async Task PayoffCommittedWhileProcessIsInFlight_PayrollDoesNotAlsoWithholdTheInstalment()
    {
        var w = await SeedAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var processDb = fixture.CreateDb();
        await processDb.Database.OpenConnectionAsync(timeout.Token);
        var processPid = ((NpgsqlConnection)processDb.Database.GetDbConnection()).ProcessID;
        await using var payoffDb = fixture.CreateDb();

        await payoffDb.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await payoffDb.Database.BeginTransactionAsync(timeout.Token);
            // The payoff runs to the end of its work (lock, guards, writes) and holds its transaction open.
            var payoff = await Loans(payoffDb, w.Tenant).RecordRepayment(w.LoanId,
                new RecordLoanRepaymentRequest(w.Outstanding, w.Today, "CASH-PAYOFF"), timeout.Token);
            Assert.IsType<OkObjectResult>(payoff);

            var process = Task.Run(() => Processor(processDb).ProcessAsync(
                new PayrollProcessRequest(w.RunId, w.Tenant, null, null), hooks: null, timeout.Token));
            var waited = await WaitForLockAsync(processPid, advisoryOnly: false, process, timeout.Token);
            await tx.CommitAsync(timeout.Token);
            var outcome = await process;

            Assert.True(waited, "the payroll pass must reach a lock the payoff holds before the payoff commits");
            Assert.Equal(200, outcome.StatusCode);
        });

        await AssertCollectedExactlyOnceAsync(w, expectCash: true);
    }

    /// <summary>The payroll pass holds the loan (it is inside its transaction and has computed the deduction) when the
    /// payoff arrives. The payoff must wait, then read what the run committed and refuse.</summary>
    [Fact]
    public async Task PayoffArrivingWhileProcessHoldsTheLoan_IsRefused_AndPayrollCollectsOnce()
    {
        var w = await SeedAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var processDb = fixture.CreateDb();
        await using var payoffDb = fixture.CreateDb();
        await payoffDb.Database.OpenConnectionAsync(timeout.Token);
        var payoffPid = ((NpgsqlConnection)payoffDb.Database.GetDbConnection()).ProcessID;

        Task<IActionResult>? payoff = null;
        var payoffWaited = false;
        var hooks = new PayrollProcessHooks
        {
            // Progress first fires inside the run's transaction, after the deductions' inputs were read.
            Progress = async (_, _, ct) =>
            {
                if (payoff is not null) return;
                payoff = Task.Run(() => Loans(payoffDb, w.Tenant).RecordRepayment(w.LoanId,
                    new RecordLoanRepaymentRequest(w.Outstanding, w.Today, "CASH-PAYOFF"), timeout.Token));
                payoffWaited = await WaitForLockAsync(payoffPid, advisoryOnly: true, payoff, ct);
            },
        };

        var outcome = await Processor(processDb).ProcessAsync(new PayrollProcessRequest(w.RunId, w.Tenant, null, null), hooks, timeout.Token);
        Assert.Equal(200, outcome.StatusCode);
        Assert.NotNull(payoff);
        var payoffResult = await payoff!;

        Assert.True(payoffWaited, "the payoff must queue behind the loan lock the payroll pass holds");
        // Refused on the balance it now reads (the full amount it asked for exceeds what payroll left), and in any case
        // by the unlocked run that holds an instalment. Either refusal records nothing.
        var refusal = Assert.IsAssignableFrom<ObjectResult>(payoffResult);
        Assert.Contains(refusal.StatusCode, new int?[] { 400, 409 });
        await AssertCollectedExactlyOnceAsync(w, expectCash: false);
    }

    // ── assertions ───────────────────────────────────────────────────────────────────────────────

    private async Task AssertCollectedExactlyOnceAsync(World w, bool expectCash)
    {
        await using var db = fixture.CreateDb();
        var loan = await db.EmployeeLoans.AsNoTracking().SingleAsync(l => l.Id == w.LoanId);
        var cash = await db.LoanRepayments.AsNoTracking().Where(r => r.LoanId == w.LoanId && !r.IsReversed).SumAsync(r => r.Amount);
        var withheld = await db.PayrollDeductions.AsNoTracking()
            .Where(d => d.PayrollRunId == w.RunId && d.ComponentCode == "LOAN_EMI").SumAsync(d => d.Amount);

        Assert.False(cash > 0 && withheld > 0,
            $"the debt was collected twice: {cash} settled by cash and {withheld} withheld by payroll");
        if (expectCash)
        {
            Assert.Equal(w.Outstanding, cash);
            Assert.Equal(0m, withheld);
            Assert.Equal("Settled", loan.Status);
            Assert.Equal(0m, loan.OutstandingBalance);
            Assert.Equal(w.Approved, loan.TotalRepaid);
            Assert.False(await db.PayrollRunConsumptions.AnyAsync(c => c.PayrollRunId == w.RunId && c.ArtifactId == w.LoanId));
        }
        else
        {
            Assert.Equal(0m, cash);
            Assert.Equal(w.Installment, withheld);
            Assert.Equal(w.Outstanding - w.Installment, loan.OutstandingBalance);
            Assert.Equal(w.Approved - w.Outstanding + w.Installment, loan.TotalRepaid);
        }
        Assert.Equal(w.Approved, loan.TotalRepaid + loan.OutstandingBalance);
    }

    /// <summary>Polls <c>pg_stat_activity</c> until the backend waits on a lock (or the contender finishes first).</summary>
    private async Task<bool> WaitForLockAsync(int pid, bool advisoryOnly, Task contender, CancellationToken ct)
    {
        await using var observer = new NpgsqlConnection(fixture.ConnectionString);
        await observer.OpenAsync(ct);
        for (var attempt = 0; attempt < 400 && !contender.IsCompleted; attempt++)
        {
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1 FROM pg_stat_activity
                    WHERE pid = @pid AND datname = current_database() AND wait_event_type = 'Lock'
                      AND (NOT @advisoryOnly OR wait_event = 'advisory')
                )
                """, observer);
            command.Parameters.AddWithValue("pid", pid);
            command.Parameters.AddWithValue("advisoryOnly", advisoryOnly);
            if ((bool)(await command.ExecuteScalarAsync(ct))!) return true;
            await Task.Delay(25, ct);
        }
        return false;
    }

    // ── seed ─────────────────────────────────────────────────────────────────────────────────────

    private sealed record World(Guid Tenant, Guid LoanId, Guid RunId, DateOnly Today, decimal Approved, decimal Outstanding, decimal Installment);

    /// <summary>A KSA company, one salaried Saudi borrower, a 100 SAR loan disbursed through the real payout path and
    /// collected by payroll in two 50 SAR instalments from this month, and a Draft run for this month.</summary>
    private async Task<World> SeedAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Guid tid, loanId;
        await using (var seed = fixture.CreateDb())
        {
            tid = await PostgresFixture.SeedMinimalTenant(seed);
            var company = new Company
            {
                TenantId = tid, LegalNameEn = "Payoff Race Co", CountryCode = "SAU", Jurisdiction = "KSA-mainland",
                RegistrationNumber = $"PR-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true,
                CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            var employee = new Employee
            {
                TenantId = tid, CompanyId = company.Id, EmployeeCode = $"PR-{Guid.NewGuid():N}", FullName = "Payoff Borrower",
                Nationality = "Saudi", Status = "Active", JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            var type = new LoanType { TenantId = tid, Code = "PAYOFF", NameEn = "Personal", IsActive = true, MaxAmount = 10_000m, MaxInstallments = 12 };
            seed.AddRange(company, employee, type);
            await seed.SaveChangesAsync();
            seed.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
            {
                TenantId = tid, EmployeeId = employee.Id, SalaryStructureId = Guid.NewGuid(),
                BasicSalary = 10_000m, HousingAllowance = 2_000m, EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
            });
            seed.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
            {
                TenantId = tid, EmployeeId = employee.Id, Iban = "SA4420000001234567891234", BankName = "Payoff Bank", SalaryCurrency = "SAR",
            });
            var loan = new EmployeeLoan
            {
                TenantId = tid, CompanyId = company.Id, EmployeeIntId = employee.Id, EmployeeId = employee.PublicId,
                EmployeeName = employee.FullName, LoanNumber = $"PR-{Guid.NewGuid():N}", Status = "Approved", LoanTypeId = type.Id,
                LoanTypeName = type.NameEn, RepaymentMethod = "BankTransfer", Currency = "SAR", RequestedAmount = 100m, ApprovedAmount = 100m,
                RequestedInstallments = 2, ApprovedInstallments = 2, InstallmentAmount = 50m, RepaymentStartDate = today,
            };
            seed.EmployeeLoans.Add(loan);
            await seed.SaveChangesAsync();
            loanId = loan.Id;
            Assert.IsType<OkObjectResult>(await Loans(seed, tid).CreatePaymentBatch(new([loanId]), default));
        }
        await using (var approve = fixture.CreateDb())
        {
            var batch = await approve.LoanDisbursementBatches.AsNoTracking().SingleAsync(x => x.TenantId == tid);
            Assert.IsType<OkObjectResult>(await Loans(approve, tid).ApprovePaymentBatch(batch.Id, default));
        }
        await using (var pay = fixture.CreateDb())
        {
            var batch = await pay.LoanDisbursementBatches.AsNoTracking().SingleAsync(x => x.TenantId == tid);
            Assert.IsType<OkObjectResult>(await Loans(pay, tid).ConfirmPaymentBatchPaid(batch.Id, new("PAYOUT", today), default));
        }
        await using (var db = fixture.CreateDb())
        {
            // Collected by payroll from now on (the payout path disburses a bank-transfer loan; the method is the borrower's choice).
            var loan = await db.EmployeeLoans.SingleAsync(x => x.Id == loanId);
            loan.RepaymentMethod = "PayrollDeduction";
            var run = new PayrollRun { TenantId = tid, CompanyId = loan.CompanyId, Year = today.Year, Month = today.Month, Status = "Draft" };
            db.PayrollRuns.Add(run);
            await db.SaveChangesAsync();
            Assert.Equal("Active", loan.Status);
            Assert.Equal(100m, loan.OutstandingBalance);
            return new World(tid, loanId, run.Id, today, 100m, loan.OutstandingBalance, 50m);
        }
    }

    private static PayrollRunProcessor Processor(ZayraDbContext db)
    {
        var rules = new StubRuleReader()
            .Set("gosi.saudi_employee_rate", 0.09m).Set("gosi.saudi_employer_rate", 0.09m)
            .Set("gosi.saned_rate", 0.0075m).Set("gosi.expat_occupational_hazard_rate", 0.02m)
            .Set("gosi.covered_wage_ceiling_sar", 45_000m)
            .Set("ot.standard_multiplier", 1.5m).Set("ot.standard_monthly_hours", 240m)
            .Set("lop.monthly_day_divisor", 30m).Set("lop.standard_work_minutes_per_day", 480m);
        return new PayrollRunProcessor(db, new PayoffRacePacks(rules), rules,
            new Zayra.Api.Infrastructure.Governance.CompanyTaxPolicyResolver(db),
            new Zayra.Api.Infrastructure.Employees.EmployeeActivationGuard(db),
            new ProrationPolicyResolver(new CompanyRatePolicyResolver(db)),
            new Zayra.Api.Infrastructure.WorkWeek.WorkWeekService(db));
    }

    private static LoansController Loans(ZayraDbContext db, Guid tid) => new(db, new Scope())
    {
        ControllerContext = new()
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] {
                    new Claim("tenant_id", tid.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Name, "Finance"), new Claim(ClaimTypes.Role, "Finance"), new Claim(ClaimTypes.Role, "HR Manager") }, "test")),
            },
        },
    };

    private sealed class Scope : IDataScopeService
    { public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization }); }

    private sealed class PayoffRacePacks(IStatutoryRuleReader rules) : ICountryPackResolver
    {
        private readonly KsaDeductionCalculator _calc = new(rules);
        public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) =>
            cc == "SAU" ? _calc : new DefaultStatutoryDeductionCalculator();
        public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
        public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
        public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
        public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
        public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
    }
}
