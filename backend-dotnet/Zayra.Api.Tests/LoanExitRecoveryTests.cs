using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Every open loan is visible and recovered at exit, whatever its repayment method. A loan repaid by bank transfer (the request
/// form's default) used to be ignored by the final settlement plan, the settlement run and the settlement payment, so a leaver was
/// paid in full while the loan stayed open on an ex-employee with nothing reporting it. Now: the plan lists every open loan and
/// advance; loans not on hold are recovered from the final pay under the same cap advances use (the settlement's net before
/// debt); a held loan is listed and warned about, never recovered; what cannot be recovered is warned about by name and amount,
/// and moves to the 1420 receivable when the settlement is paid, as an advance's leftover does.
/// </summary>
public class LoanExitRecoveryTests
{
    [Fact]
    public async Task SettlementPlan_RecoversABankTransferLoan_AndListsEveryOpenDebt()
    {
        await using var h = await Fixture.Create();
        var bank = h.Loan("LN-BANK", "BankTransfer", 1_500m);
        var held = h.Loan("LN-HELD", "PayrollDeduction", 800m);
        held.CollectionStatus = "OnHold";
        var advance = h.Advance(600m);
        h.Db.AddRange(bank, held, advance);
        await h.Db.SaveChangesAsync();

        var body = await h.PlanAsync(otherDues: 5_000m);

        var settlement = await h.Db.EmployeeFinalSettlements.AsNoTracking().SingleAsync();
        Assert.Equal((1_500m, 600m), (settlement.PlannedLoanRecovery, settlement.PlannedAdvanceRecovery));
        var debts = body.GetProperty("openDebts").EnumerateArray().ToList();
        Assert.Equal(3, debts.Count);
        var bankRow = debts.Single(d => d.GetProperty("reference").GetString() == "LN-BANK");
        Assert.Equal(("Loan", "BankTransfer", false, 1_500m, 1_500m),
            (bankRow.GetProperty("kind").GetString(), bankRow.GetProperty("repaymentMethod").GetString(), bankRow.GetProperty("held").GetBoolean(),
             bankRow.GetProperty("outstandingBalance").GetDecimal(), bankRow.GetProperty("plannedRecovery").GetDecimal()));
        var heldRow = debts.Single(d => d.GetProperty("reference").GetString() == "LN-HELD");
        Assert.Equal((true, 0m), (heldRow.GetProperty("held").GetBoolean(), heldRow.GetProperty("plannedRecovery").GetDecimal()));
        var advRow = debts.Single(d => d.GetProperty("kind").GetString() == "Advance");
        Assert.Equal(600m, advRow.GetProperty("plannedRecovery").GetDecimal());
        Assert.Equal(800m, body.GetProperty("heldDebt").GetDecimal());
        Assert.Equal(0m, body.GetProperty("unrecoveredDebt").GetDecimal());
        // The held loan is named with its amount, so nobody pays the leaver out believing it was covered.
        Assert.Contains(Warnings(body), w => w.Contains("Loan Borrower") && w.Contains("800.00") && w.Contains("on hold"));
    }

    [Fact]
    public async Task SettlementPlan_ThatCannotFundTheDebt_WarnsWithTheEmployeeAndTheAmountLeft()
    {
        await using var h = await Fixture.Create();
        h.Db.Add(h.Loan("LN-BANK", "BankTransfer", 1_500m));
        await h.Db.SaveChangesAsync();

        var body = await h.PlanAsync(otherDues: 1_000m);

        var settlement = await h.Db.EmployeeFinalSettlements.AsNoTracking().SingleAsync();
        // Same cap as an advance: the settlement's net before debt, never more.
        Assert.Equal(1_000m, settlement.PlannedLoanRecovery);
        Assert.Equal(500m, body.GetProperty("unrecoveredDebt").GetDecimal());
        Assert.Contains(Warnings(body), w => w.Contains("Loan Borrower") && w.Contains("500.00") && w.Contains("cannot be funded"));
    }

    [Fact]
    public async Task SettlementRun_RecoversABankTransferLoanFromTheFinalPay()
    {
        await using var h = await Fixture.Create();
        var loan = h.Loan("LN-BANK", "BankTransfer", 1_500m);
        h.Db.Add(loan);
        h.Db.Add(new LoanInstallment { TenantId = h.Tid, LoanId = loan.Id, InstallmentNumber = 1, DueDate = new DateOnly(2026, 7, 1), AmountDue = 1_500m });
        var runId = await h.SeedApprovedSettlementRunAsync(gross: 1_000m);

        Assert.IsType<OkObjectResult>(await h.Payroll().Process(runId, CancellationToken.None));

        var slip = await h.Db.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == runId);
        Assert.Equal(1_000m, slip.LoanDeductions);
        var after = await h.Db.EmployeeLoans.AsNoTracking().SingleAsync(l => l.Id == loan.Id);
        Assert.Equal((500m, 1_000m), (after.OutstandingBalance, after.TotalRepaid));
    }

    [Fact]
    public async Task SettlementRun_LeavesAHeldLoanAlone()
    {
        await using var h = await Fixture.Create();
        var loan = h.Loan("LN-HELD", "BankTransfer", 1_500m);
        loan.CollectionStatus = "OnHold";
        h.Db.Add(loan);
        var runId = await h.SeedApprovedSettlementRunAsync(gross: 1_000m);

        Assert.IsType<OkObjectResult>(await h.Payroll().Process(runId, CancellationToken.None));

        Assert.Equal(0m, (await h.Db.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == runId)).LoanDeductions);
        Assert.Equal(1_500m, (await h.Db.EmployeeLoans.AsNoTracking().SingleAsync(l => l.Id == loan.Id)).OutstandingBalance);
    }

    [Fact]
    public async Task PaidSettlement_MovesABankTransferLoanLeftoverTo1420()
    {
        await using var h = await Fixture.Create();
        var loan = h.Loan("LN-BANK", "BankTransfer", 700m);
        h.Db.AddRange(loan, h.LoanDisbursementGl(loan.Id, 700m));
        var batch = await h.SeedPaidSettlementBatchAsync();

        Assert.IsType<OkObjectResult>(await h.Payroll().SettlePaymentBatch(batch.Id, new(), default));

        var moved = await h.Db.EmployeeLoans.AsNoTracking().SingleAsync(l => l.Id == loan.Id);
        Assert.Equal((0m, "Closed"), (moved.OutstandingBalance, moved.Status));
        Assert.Equal(700m, (await h.Db.EmployeeFinalSettlements.AsNoTracking().SingleAsync()).ResidualDebtReclassed);
        Assert.Equal(700m, (await h.Db.PayrollEmployeeReceivables.AsNoTracking().SingleAsync()).Amount);
    }

    [Fact]
    public async Task PaidSettlement_LeavesAHeldLoanOpenOnTheLoan()
    {
        await using var h = await Fixture.Create();
        var loan = h.Loan("LN-HELD", "BankTransfer", 700m);
        loan.CollectionStatus = "OnHold";
        h.Db.AddRange(loan, h.LoanDisbursementGl(loan.Id, 700m));
        var batch = await h.SeedPaidSettlementBatchAsync();

        Assert.IsType<OkObjectResult>(await h.Payroll().SettlePaymentBatch(batch.Id, new(), default));

        var kept = await h.Db.EmployeeLoans.AsNoTracking().SingleAsync(l => l.Id == loan.Id);
        Assert.Equal((700m, "Active"), (kept.OutstandingBalance, kept.Status));
        Assert.Equal(0m, (await h.Db.EmployeeFinalSettlements.AsNoTracking().SingleAsync()).ResidualDebtReclassed);
    }

    [Fact]
    public async Task OffboardingOpenDebts_ListsEveryOpenLoanAndAdvanceOfTheLeaver()
    {
        await using var h = await Fixture.Create();
        var held = h.Loan("LN-HELD", "PayrollDeduction", 800m);
        held.CollectionStatus = "OnHold";
        var closed = h.Loan("LN-CLOSED", "BankTransfer", 0m);
        closed.Status = "Closed";
        h.Db.AddRange(h.Loan("LN-BANK", "BankTransfer", 1_500m), held, closed, h.Advance(600m));
        var offboarding = new EmployeeOffboarding { TenantId = h.Tid, CompanyId = h.Company.Id, EmployeeId = h.Employee.Id, Status = "InProgress", LastWorkingDay = new(2026, 6, 30) };
        h.Db.Add(offboarding);
        await h.Db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(await h.Offboarding().OpenDebts(offboarding.Id, default));
        var body = Json(ok.Value);

        var refs = body.GetProperty("items").EnumerateArray().Select(d => d.GetProperty("reference").GetString()).OrderBy(x => x).ToList();
        Assert.Equal(3, refs.Count);
        Assert.DoesNotContain("LN-CLOSED", refs);
        Assert.Equal(2_900m, body.GetProperty("totalOutstanding").GetDecimal());
        Assert.Equal(800m, body.GetProperty("heldOutstanding").GetDecimal());
        Assert.True(body.GetProperty("items").EnumerateArray().Single(d => d.GetProperty("reference").GetString() == "LN-HELD").GetProperty("held").GetBoolean());
    }

    [Fact]
    public async Task OffboardingOpenDebts_ForAnotherTenantsOffboarding_IsNotFound()
    {
        await using var h = await Fixture.Create();
        var foreign = new EmployeeOffboarding { TenantId = Guid.NewGuid(), EmployeeId = h.Employee.Id, Status = "InProgress", LastWorkingDay = new(2026, 6, 30) };
        h.Db.Add(foreign);
        await h.Db.SaveChangesAsync();

        Assert.IsType<NotFoundResult>(await h.Offboarding().OpenDebts(foreign.Id, default));
    }

    [Fact]
    public void OffboardingOpenDebts_HasTheOffboardingReadGate()
    {
        // The same gate as reading the offboarding itself (Get): the role list is not the gate, the permission is.
        var keys = (string[] k) => k.OrderBy(x => x).ToArray();
        var gate = typeof(OffboardingController).GetMethod(nameof(OffboardingController.OpenDebts))!
            .GetCustomAttributes(typeof(HasPermissionAttribute), false).Cast<HasPermissionAttribute>().Single();
        var get = typeof(OffboardingController).GetMethod(nameof(OffboardingController.Get))!
            .GetCustomAttributes(typeof(HasPermissionAttribute), false).Cast<HasPermissionAttribute>().Single();
        Assert.Equal(keys(get.Permissions.ToArray()), keys(gate.Permissions.ToArray()));
    }

    [Fact]
    public async Task OffboardingOpenDebts_ForAReaderWhoMayNotSeeLoanBalances_IsForbidden()
    {
        await using var h = await Fixture.Create();
        h.Db.Add(h.Loan("LN-BANK", "BankTransfer", 1_500m));
        var offboarding = new EmployeeOffboarding { TenantId = h.Tid, CompanyId = h.Company.Id, EmployeeId = h.Employee.Id, Status = "InProgress", LastWorkingDay = new(2026, 6, 30) };
        h.Db.Add(offboarding);
        await h.Db.SaveChangesAsync();

        // employees.write opens the offboarding, but neither the loan book nor settlement approval.
        Assert.IsType<ForbidResult>(await h.Offboarding("HR Officer", "employees.write").OpenDebts(offboarding.Id, default));
    }

    private static List<string> Warnings(JsonElement body) =>
        body.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToList();

    private static JsonElement Json(object? value)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return doc.RootElement.Clone();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");
        public ZayraDbContext Db { get; private set; } = null!;
        public Guid Tid { get; } = Guid.NewGuid();
        public Company Company { get; private set; } = null!;
        public Employee Employee { get; private set; } = null!;

        public static async Task<Fixture> Create()
        {
            var h = new Fixture();
            await h._connection.OpenAsync();
            h.Db = new(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(h._connection).Options);
            await h.Db.Database.EnsureCreatedAsync();
            h.Company = new() { TenantId = h.Tid, LegalNameEn = "Exit Loans Co", CountryCode = "ARE", Jurisdiction = "UAE-mainland", DefaultCurrency = "AED", IsActive = true, RegistrationNumber = "EXIT-001" };
            var structure = new SalaryStructure { TenantId = h.Tid, CompanyId = h.Company.Id, Code = "STD", Name = "Standard", Currency = "AED" };
            h.Employee = new() { TenantId = h.Tid, CompanyId = h.Company.Id, EmployeeCode = "EXIT-001", FullName = "Loan Borrower", Nationality = "Indian", Status = "Active", JoiningDate = new DateTime(2025, 1, 1) };
            h.Db.AddRange(h.Company, structure, h.Employee);
            await h.Db.SaveChangesAsync();
            h.Db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
            {
                TenantId = h.Tid, EmployeeId = h.Employee.Id, SalaryStructureId = structure.Id,
                BasicSalary = 8_000m, Currency = "AED", EffectiveDate = new DateOnly(2026, 1, 1), IsActive = true,
            });
            h.Db.GCCComplianceSettings.Add(new GCCComplianceSetting { TenantId = h.Tid, CountryCode = "ARE", EosbEnabled = true });
            await h.Db.SaveChangesAsync();
            return h;
        }

        public EmployeeLoan Loan(string number, string method, decimal balance) => new()
        {
            TenantId = Tid, CompanyId = Company.Id, EmployeeIntId = Employee.Id, EmployeeId = Employee.PublicId,
            EmployeeName = Employee.FullName, LoanNumber = number, Status = "Active", RepaymentMethod = method, Currency = "AED",
            ApprovedAmount = Math.Max(balance, 1m), InstallmentAmount = 500m, OutstandingBalance = balance,
            RepaymentStartDate = new DateOnly(2026, 7, 1), DisbursementDate = new DateOnly(2026, 1, 1),
        };

        public SalaryAdvance Advance(decimal balance) => new()
        {
            TenantId = Tid, CompanyId = Company.Id, EmployeeId = Employee.PublicId, EmployeeIntId = Employee.Id, EmployeeName = Employee.FullName,
            AdvanceNumber = $"ADV-{Guid.NewGuid():N}", RequestedAmount = balance, ApprovedAmount = balance, RepaymentType = "Installments",
            Installments = 2, InstallmentAmount = balance / 2, RepaymentStartDate = new(2026, 7, 1), OutstandingBalance = balance, Status = "Active",
        };

        public FinanceGlEntry LoanDisbursementGl(Guid loanId, decimal amount) => new()
        {
            TenantId = Tid, CompanyId = Company.Id, SourceModule = "Loan", SourceEntityId = loanId, EventType = "Disbursement",
            DebitAccount = "1400 - Employee Loans Receivable", CreditAccount = "1000 - Cash/Bank", Amount = amount, Currency = "AED",
        };

        /// <summary>Calls POST /api/payroll/final-settlement for the leaver and returns the response body.</summary>
        public async Task<JsonElement> PlanAsync(decimal otherDues)
        {
            Db.EmployeeOffboardings.Add(new EmployeeOffboarding
            {
                TenantId = Tid, CompanyId = Company.Id, EmployeeId = Employee.Id, SeparationType = "Termination",
                Status = "InProgress", LastWorkingDay = new DateOnly(2026, 6, 30),
            });
            await Db.SaveChangesAsync();
            var ok = Assert.IsType<OkObjectResult>(await Payroll().FinalSettlement(
                new FinalSettlementRequest(Employee.Id, new DateOnly(2026, 6, 30), OtherDuesAmount: otherDues), CancellationToken.None));
            return Json(ok.Value);
        }

        /// <summary>An Approved settlement paying <paramref name="gross"/> and a Draft off-cycle settlement run that names the leaver.</summary>
        public async Task<Guid> SeedApprovedSettlementRunAsync(decimal gross)
        {
            Employee.Status = EmployeeStatuses.Offboarded;
            var offboarding = new EmployeeOffboarding
            {
                TenantId = Tid, CompanyId = Company.Id, EmployeeId = Employee.Id, EmployeeCode = Employee.EmployeeCode, EmployeeName = Employee.FullName,
                SeparationType = "Termination", Status = "InProgress", NoticeDate = new(2026, 6, 1), NoticePeriodDays = 30, LastWorkingDay = new(2026, 6, 30),
            };
            var settlement = new EmployeeFinalSettlement
            {
                TenantId = Tid, CompanyId = Company.Id, EmployeeId = Employee.Id, EmployeeCode = Employee.EmployeeCode, EmployeeName = Employee.FullName,
                OffboardingId = offboarding.Id, LastWorkingDay = new(2026, 6, 30), ServiceStartDate = new(2025, 1, 1), SettlementDueDate = new(2026, 7, 7),
                TerminationReason = "Termination", Currency = "AED", GratuityAmount = gross, GrossPayable = gross, NetPayable = gross,
                PlannedLoanRecovery = gross,
                Status = FinalSettlementStatuses.Approved, CreatedByUserId = Guid.NewGuid(), SubmittedByUserId = Guid.NewGuid(),
                ApprovedByUserId = Guid.NewGuid(), ApprovedByName = "Independent Approver", ApprovedAtUtc = DateTime.UtcNow, GlPostedAtUtc = DateTime.UtcNow, GlPeriod = "2026-07",
            };
            var run = new PayrollRun
            {
                TenantId = Tid, CompanyId = Company.Id, Year = 2026, Month = 7, Status = "Draft",
                RunType = PayrollRunTypes.OffCycle, IncludesRecurringPay = false, SettlesFinalSettlements = true,
            };
            Db.AddRange(offboarding, settlement, run,
                new FinalSettlementLine
                {
                    TenantId = Tid, SettlementId = settlement.Id, ComponentCode = FinalSettlementComponents.Gratuity, ComponentName = "End-of-service gratuity",
                    LineType = FinalSettlementLineTypes.Earning, Source = FinalSettlementComponents.SettlementSource, Amount = gross, SortOrder = 0,
                },
                new FinanceGlEntry
                {
                    TenantId = Tid, CompanyId = Company.Id, SourceModule = FinalSettlementGlDescriptions.SourceModule, SourceEntityId = settlement.Id,
                    SourceEntityRef = FinalSettlementGlDescriptions.SettlementRef(settlement.Id), EventType = GlEventTypes.SettlementAccrual, DebitAccount = string.Empty,
                    CreditAccount = "2320 - Final Settlement Payable", Amount = gross, Currency = "AED", EntryDate = new DateOnly(2026, 7, 1), Period = "2026-07",
                    Description = FinalSettlementGlDescriptions.AccrualPrefix + settlement.EmployeeCode,
                },
                new PayrollRunEmployeeSelection { TenantId = Tid, CompanyId = Company.Id, PayrollRunId = run.Id, EmployeeId = Employee.Id, Mode = PayrollRunSelectionModes.Include });
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return run.Id;
        }

        /// <summary>A Locked settlement run whose Accepted bank batch pays this employee's Disbursing final settlement.</summary>
        public async Task<PayrollPaymentBatch> SeedPaidSettlementBatchAsync()
        {
            var run = new PayrollRun { TenantId = Tid, CompanyId = Company.Id, Year = 2026, Month = 7, RunType = PayrollRunTypes.OffCycle, Status = "Locked" };
            var batch = new PayrollPaymentBatch { TenantId = Tid, PayrollRunId = run.Id, BatchNumber = "EXIT-SETTLEMENT", WpsStatus = WpsStatuses.Accepted, TotalAmount = 1000, Currency = "AED" };
            var offboarding = new EmployeeOffboarding { TenantId = Tid, CompanyId = Company.Id, EmployeeId = Employee.Id, Status = "InProgress", LastWorkingDay = new(2026, 6, 30) };
            var settlement = new EmployeeFinalSettlement
            {
                TenantId = Tid, CompanyId = Company.Id, EmployeeId = Employee.Id, EmployeeCode = Employee.EmployeeCode,
                OffboardingId = offboarding.Id, PayrollRunId = run.Id, Currency = "AED", Status = FinalSettlementStatuses.Disbursing,
            };
            Db.AddRange(run, batch, offboarding, settlement,
                new PayrollPaymentRecord { TenantId = Tid, PaymentBatchId = batch.Id, EmployeeId = Employee.Id, Amount = 1000, Status = "Pending" },
                new FinanceGlEntry { TenantId = Tid, CompanyId = Company.Id, SourceModule = "Payroll", SourceEntityId = run.Id,
                    EventType = GlEventTypes.Accrual, CreditAccount = "2100 - Salaries Payable", Description = PayrollGlDescriptions.NetPayable, Amount = 1000, Currency = "AED" });
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return batch;
        }

        public PayrollController Payroll()
        {
            var http = Context("Admin", "payroll.write", "payroll.export", "payroll.approve");
            return new(Db, new Scope(), new HttpContextAccessor { HttpContext = http }, new F2NullNotifications(), new Pack(), new StubRuleReader(),
                new F2NullLetters(), new NullDocumentStorage(), new Zayra.Api.Infrastructure.Documents.PdfRenderGate(4))
            { ControllerContext = new() { HttpContext = http } };
        }

        public OffboardingController Offboarding(string role = "HR Manager", params string[] permissions) =>
            new(Db) { ControllerContext = new() { HttpContext = Context(role, permissions.Length == 0 ? ["employees.write", "loans.read"] : permissions) } };

        private DefaultHttpContext Context(string role, params string[] permissions) => new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim("tenant_id", Tid.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, role) }
                    .Concat(permissions.Select(p => new Claim("permission", p))), "test")),
        };

        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await _connection.DisposeAsync(); }
    }

    private sealed class Scope : IDataScopeService
    { public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization }); }

    private sealed class Pack : ICountryPackResolver
    {
        public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) => new ZeroDeductions();
        public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
        public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
        public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
        public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
        public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
    }

    private sealed class ZeroDeductions : IStatutoryDeductionCalculator
    {
        public Task<StatutoryDeductionResult> CalculateAsync(StatutoryDeductionInput input, CancellationToken ct = default)
            => Task.FromResult(new StatutoryDeductionResult(0m, 0m, Array.Empty<StatutoryDeductionLine>()));
    }
}
