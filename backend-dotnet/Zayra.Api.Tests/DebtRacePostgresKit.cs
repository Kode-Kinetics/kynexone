using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zayra.Api.Application.Common;
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
/// Shared seed and observers for the Postgres races between the writers of a payroll-collected loan or advance balance:
/// payroll Process, the leaver-settlement payment, the run void and the opening-balance import. Each side runs on its own
/// context and connection; the waiting side is observed in <c>pg_stat_activity</c>, never inferred from timing.
/// </summary>
internal static class DebtRacePostgresKit
{
    internal sealed record Borrower(Guid Tenant, Guid CompanyId, int EmployeeId, Guid EmployeePublicId, string EmployeeCode,
        Guid LoanId, DateOnly Today);

    /// <summary>A KSA company, one salaried Saudi employee, and a loan of <paramref name="amount"/> disbursed through the real payout
    /// path (so 1400 carries it) and collected by payroll in <paramref name="installments"/> equal instalments from this month.</summary>
    internal static async Task<Borrower> SeedBorrowerAsync(PostgresFixture fixture, decimal amount, int installments)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Guid tid, loanId, companyId, publicId;
        int employeeId;
        string code;
        await using (var seed = fixture.CreateDb())
        {
            tid = await PostgresFixture.SeedMinimalTenant(seed);
            var company = new Company
            {
                TenantId = tid, LegalNameEn = "Debt Race Co", CountryCode = "SAU", Jurisdiction = "KSA-mainland",
                RegistrationNumber = $"DR-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true,
                CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            var employee = new Employee
            {
                TenantId = tid, CompanyId = company.Id, EmployeeCode = $"DR-{Guid.NewGuid():N}", FullName = "Debt Race Borrower",
                Nationality = "Saudi", Status = "Active", JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            var type = new LoanType { TenantId = tid, Code = "DEBTRACE", NameEn = "Personal", IsActive = true, MaxAmount = 10_000m, MaxInstallments = 12 };
            seed.AddRange(company, employee, type);
            await seed.SaveChangesAsync();
            seed.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
            {
                TenantId = tid, EmployeeId = employee.Id, SalaryStructureId = Guid.NewGuid(),
                BasicSalary = 10_000m, HousingAllowance = 2_000m, EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
            });
            seed.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
            {
                TenantId = tid, EmployeeId = employee.Id, Iban = "SA4420000001234567891234", BankName = "Debt Race Bank", SalaryCurrency = "SAR",
            });
            var loan = new EmployeeLoan
            {
                TenantId = tid, CompanyId = company.Id, EmployeeIntId = employee.Id, EmployeeId = employee.PublicId,
                EmployeeName = employee.FullName, LoanNumber = $"DR-{Guid.NewGuid():N}", Status = "Approved", LoanTypeId = type.Id,
                LoanTypeName = type.NameEn, RepaymentMethod = "BankTransfer", Currency = "SAR", RequestedAmount = amount, ApprovedAmount = amount,
                RequestedInstallments = installments, ApprovedInstallments = installments, InstallmentAmount = amount / installments,
                RepaymentStartDate = today,
            };
            seed.EmployeeLoans.Add(loan);
            await seed.SaveChangesAsync();
            (loanId, companyId, employeeId, publicId, code) = (loan.Id, company.Id, employee.Id, employee.PublicId, employee.EmployeeCode);
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
            await db.SaveChangesAsync();
            Assert.Equal("Active", loan.Status);
            Assert.Equal(amount, loan.OutstandingBalance);
        }
        return new Borrower(tid, companyId, employeeId, publicId, code, loanId, today);
    }

    /// <summary>An Active salary advance collected by payroll from this month, with the 1410 debit its disbursement booked.</summary>
    internal static async Task<Guid> SeedAdvanceAsync(PostgresFixture fixture, Borrower b, decimal amount, int installments)
    {
        await using var db = fixture.CreateDb();
        var advance = new SalaryAdvance
        {
            TenantId = b.Tenant, CompanyId = b.CompanyId, EmployeeId = b.EmployeePublicId, EmployeeIntId = b.EmployeeId,
            EmployeeName = "Debt Race Borrower", AdvanceNumber = $"ADV-{Guid.NewGuid():N}", RequestedAmount = amount, ApprovedAmount = amount,
            RepaymentType = "Installments", Installments = installments, InstallmentAmount = amount / installments,
            RepaymentStartDate = b.Today, OutstandingBalance = amount, Status = "Active", Reason = "Debt race",
        };
        db.SalaryAdvances.Add(advance);
        db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = b.Tenant, CompanyId = b.CompanyId, SourceModule = "SalaryAdvance", SourceEntityId = advance.Id,
            EventType = "Disbursement", DebitAccount = "1410 - Employee Salary Advances", CreditAccount = "1000 - Cash at Bank",
            Amount = amount, Currency = "SAR", EntryDate = b.Today, Period = $"{b.Today.Year}-{b.Today.Month:D2}", Description = "Advance disbursement",
        });
        await db.SaveChangesAsync();
        return advance.Id;
    }

    internal static async Task<Guid> SeedDraftRunAsync(PostgresFixture fixture, Borrower b, int year, int month)
    {
        await using var db = fixture.CreateDb();
        var run = new PayrollRun { TenantId = b.Tenant, CompanyId = b.CompanyId, Year = year, Month = month, Status = "Draft" };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    /// <summary>Polls <c>pg_stat_activity</c> until the backend waits on a lock (or the contender finishes first).</summary>
    internal static async Task<bool> WaitForLockAsync(PostgresFixture fixture, int pid, bool advisoryOnly, Task contender, CancellationToken ct)
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

    internal static async Task<int> OpenAndPidAsync(ZayraDbContext db, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        return ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID;
    }

    internal static PayrollRunProcessor Processor(ZayraDbContext db)
    {
        var rules = Rules();
        return new PayrollRunProcessor(db, new Packs(rules), rules,
            new Zayra.Api.Infrastructure.Governance.CompanyTaxPolicyResolver(db),
            new Zayra.Api.Infrastructure.Employees.EmployeeActivationGuard(db),
            new ProrationPolicyResolver(new CompanyRatePolicyResolver(db)),
            new Zayra.Api.Infrastructure.WorkWeek.WorkWeekService(db));
    }

    internal static Task<PayrollProcessOutcome> ProcessAsync(ZayraDbContext db, Borrower b, Guid runId, PayrollProcessHooks? hooks, CancellationToken ct) =>
        Processor(db).ProcessAsync(new PayrollProcessRequest(runId, b.Tenant, null, null), hooks, ct);

    internal static LoansController Loans(ZayraDbContext db, Guid tid) => new(db, new Scope())
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

    /// <summary>A payroll controller for a Finance approver who may settle payment batches (payroll.export).</summary>
    internal static PayrollController Payroll(ZayraDbContext db, Guid tid)
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] {
                new Claim("tenant_id", tid.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Name, "Finance Approver"), new Claim(ClaimTypes.Role, "Admin"),
                new Claim("permission", "payroll.read"), new Claim("permission", "payroll.write"), new Claim("permission", "payroll.export") }, "test")),
        };
        var rules = Rules();
        return new PayrollController(db, new Scope(), new HttpContextAccessor { HttpContext = http }, new F2NullNotifications(),
            new Packs(rules), rules, new F2NullLetters(), new NullDocumentStorage(), new Zayra.Api.Infrastructure.Documents.PdfRenderGate(4))
        { ControllerContext = new() { HttpContext = http } };
    }

    internal static StubRuleReader Rules() => new StubRuleReader()
        .Set("gosi.saudi_employee_rate", 0.09m).Set("gosi.saudi_employer_rate", 0.09m)
        .Set("gosi.saned_rate", 0.0075m).Set("gosi.expat_occupational_hazard_rate", 0.02m)
        .Set("gosi.covered_wage_ceiling_sar", 45_000m)
        .Set("ot.standard_multiplier", 1.5m).Set("ot.standard_monthly_hours", 240m)
        .Set("lop.monthly_day_divisor", 30m).Set("lop.standard_work_minutes_per_day", 480m);

    private sealed class Scope : IDataScopeService
    { public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization }); }

    private sealed class Packs(IStatutoryRuleReader rules) : ICountryPackResolver
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
