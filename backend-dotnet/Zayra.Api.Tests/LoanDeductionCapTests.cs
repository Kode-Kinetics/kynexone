using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// For a Saudi legal entity the run itself keeps loan and advance instalments within the cap the validation rule checks
/// (DEDUCTIONS_EXCEED_HALF_WAGE: debt-type deductions at most half the wage due, WageDeductionClassification). The processor
/// used to take an instalment up to 100% of the net and leave the validation to refuse the run afterwards; now it takes only
/// the headroom under that cap and carries the rest forward, as it already did when the net could not fund an instalment. Other
/// countries keep the net-only cap.
/// </summary>
public class LoanDeductionCapTests
{
    [Fact]
    public async Task Ksa_InstalmentAboveHalfTheWageDue_IsCutToTheHeadroom_AndTheRestCarriesForward()
    {
        await using var h = await Fixture.Create("SAU", basic: 8_000m);
        var loan = h.Loan(installment: 5_000m, balance: 10_000m);

        var runId = await h.ProcessAsync();

        var slip = await h.Db.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == runId);
        Assert.Equal(4_000m, slip.LoanDeductions);
        var after = await h.Db.EmployeeLoans.AsNoTracking().SingleAsync(l => l.Id == loan.Id);
        Assert.Equal((6_000m, 4_000m), (after.OutstandingBalance, after.TotalRepaid));
        // The run and the rule agree: nothing for the validation to refuse.
        Assert.DoesNotContain(await h.Codes(runId), c => c == WageDeductionClassification.DeductionsExceedHalfWageCode);
    }

    [Fact]
    public async Task Ksa_InstalmentWithinHalfTheWageDue_IsTakenInFull()
    {
        await using var h = await Fixture.Create("SAU", basic: 8_000m);
        var loan = h.Loan(installment: 3_000m, balance: 10_000m);

        var runId = await h.ProcessAsync();

        Assert.Equal(3_000m, (await h.Db.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == runId)).LoanDeductions);
        Assert.Equal(7_000m, (await h.Db.EmployeeLoans.AsNoTracking().SingleAsync(l => l.Id == loan.Id)).OutstandingBalance);
    }

    [Fact]
    public async Task Ksa_ExactlyHalfTheWageDue_IsTakenInFull()
    {
        await using var h = await Fixture.Create("SAU", basic: 8_000m);
        h.Loan(installment: 4_000m, balance: 10_000m);

        var runId = await h.ProcessAsync();

        Assert.Equal(4_000m, (await h.Db.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == runId)).LoanDeductions);
        Assert.DoesNotContain(await h.Codes(runId), c => c == WageDeductionClassification.DeductionsExceedHalfWageCode);
    }

    [Fact]
    public async Task Ksa_OtherDebtTypeDeductions_UseUpTheHeadroomFirst()
    {
        await using var h = await Fixture.Create("SAU", basic: 8_000m);
        var loan = h.Loan(installment: 5_000m, balance: 10_000m);
        var runId = await h.ProcessAsync(penalty: 1_000m);

        // Half of 8,000 is 4,000; a 1,000 penalty (a debt-type adjustment) leaves 3,000 for the instalment.
        Assert.Equal(3_000m, (await h.Db.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == runId)).LoanDeductions);
        Assert.Equal(7_000m, (await h.Db.EmployeeLoans.AsNoTracking().SingleAsync(l => l.Id == loan.Id)).OutstandingBalance);
        Assert.DoesNotContain(await h.Codes(runId), c => c == WageDeductionClassification.DeductionsExceedHalfWageCode);
    }

    [Fact]
    public async Task Ksa_AdvanceInstalments_ShareTheSameHeadroom()
    {
        await using var h = await Fixture.Create("SAU", basic: 8_000m);
        h.Loan(installment: 3_000m, balance: 10_000m);
        var advance = h.Advance(installment: 2_000m, balance: 2_000m);

        var runId = await h.ProcessAsync();

        Assert.Equal(4_000m, (await h.Db.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == runId)).LoanDeductions);
        Assert.Equal(1_000m, (await h.Db.SalaryAdvances.AsNoTracking().SingleAsync(a => a.Id == advance.Id)).OutstandingBalance);
    }

    [Fact]
    public async Task Ksa_FinalSettlementPlan_PromisesNoMoreThanHalfTheSettlement()
    {
        await using var h = await Fixture.Create("SAU", basic: 8_000m);
        var loan = h.Loan(installment: 500m, balance: 10_000m);
        loan.RepaymentMethod = "BankTransfer";
        h.Db.GCCComplianceSettings.Add(new GCCComplianceSetting { TenantId = h.Tid, CountryCode = "SAU", EosbEnabled = true });
        h.Db.EmployeeOffboardings.Add(new EmployeeOffboarding
        {
            TenantId = h.Tid, CompanyId = h.Company.Id, EmployeeId = h.Employee.Id, SeparationType = "Termination", Status = "InProgress",
            LastWorkingDay = new DateOnly(2026, 6, 30),
        });
        await h.Db.SaveChangesAsync();

        Assert.IsType<OkObjectResult>(await h.Settle(otherDues: 6_000m));

        var settlement = await h.Db.EmployeeFinalSettlements.AsNoTracking().SingleAsync();
        // The run keeps the recovery within half of the wage due, so the plan does too: half of the settlement's gross.
        Assert.Equal(Math.Floor(settlement.GrossPayable / 2m * 100m) / 100m, settlement.PlannedLoanRecovery);
        Assert.True(settlement.PlannedLoanRecovery < 6_000m);
    }

    [Fact]
    public async Task NonKsa_KeepsTheNetOnlyCap()
    {
        await using var h = await Fixture.Create("ARE", basic: 8_000m);
        h.Loan(installment: 5_000m, balance: 10_000m);

        var runId = await h.ProcessAsync();

        Assert.Equal(5_000m, (await h.Db.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == runId)).LoanDeductions);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");
        public ZayraDbContext Db { get; private set; } = null!;
        public Guid Tid { get; } = Guid.NewGuid();
        public Company Company { get; private set; } = null!;
        public Employee Employee { get; private set; } = null!;

        public static async Task<Fixture> Create(string country, decimal basic)
        {
            var h = new Fixture();
            await h._connection.OpenAsync();
            h.Db = new(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(h._connection).Options);
            await h.Db.Database.EnsureCreatedAsync();
            var ksa = country == "SAU";
            h.Company = new()
            {
                TenantId = h.Tid, LegalNameEn = "Cap Co", CountryCode = country, Jurisdiction = ksa ? "KSA-mainland" : "UAE-mainland",
                DefaultCurrency = ksa ? "SAR" : "AED", IsActive = true, RegistrationNumber = "CAP-001",
            };
            var structure = new SalaryStructure { TenantId = h.Tid, CompanyId = h.Company.Id, Code = "STD", Name = "Standard", Currency = h.Company.DefaultCurrency };
            h.Employee = new() { TenantId = h.Tid, CompanyId = h.Company.Id, EmployeeCode = "CAP-001", FullName = "Cap Borrower", Nationality = "Indian", Status = "Active", JoiningDate = new DateTime(2025, 1, 1) };
            h.Db.AddRange(h.Company, structure, h.Employee);
            await h.Db.SaveChangesAsync();
            h.Db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
            {
                TenantId = h.Tid, EmployeeId = h.Employee.Id, SalaryStructureId = structure.Id,
                BasicSalary = basic, Currency = h.Company.DefaultCurrency, EffectiveDate = new DateOnly(2026, 1, 1), IsActive = true,
            });
            await h.Db.SaveChangesAsync();
            return h;
        }

        public EmployeeLoan Loan(decimal installment, decimal balance)
        {
            var loan = new EmployeeLoan
            {
                TenantId = Tid, CompanyId = Company.Id, EmployeeIntId = Employee.Id, EmployeeId = Employee.PublicId,
                EmployeeName = Employee.FullName, LoanNumber = $"LN-{Guid.NewGuid():N}"[..12], Status = "Active", RepaymentMethod = "PayrollDeduction",
                Currency = Company.DefaultCurrency, ApprovedAmount = balance, InstallmentAmount = installment, OutstandingBalance = balance,
                ApprovedInstallments = (int)Math.Ceiling(balance / installment), RepaymentStartDate = new DateOnly(2026, 1, 1),
            };
            Db.EmployeeLoans.Add(loan);
            return loan;
        }

        public SalaryAdvance Advance(decimal installment, decimal balance)
        {
            var advance = new SalaryAdvance
            {
                TenantId = Tid, CompanyId = Company.Id, EmployeeId = Employee.PublicId, EmployeeIntId = Employee.Id, EmployeeName = Employee.FullName,
                AdvanceNumber = $"ADV-{Guid.NewGuid():N}"[..12], RequestedAmount = balance, ApprovedAmount = balance, RepaymentType = "Installments",
                Installments = (int)Math.Ceiling(balance / installment), InstallmentAmount = installment, RepaymentStartDate = new(2026, 1, 1),
                OutstandingBalance = balance, Status = "Active",
            };
            Db.SalaryAdvances.Add(advance);
            return advance;
        }

        public async Task<Guid> ProcessAsync(decimal penalty = 0m)
        {
            var run = new PayrollRun { TenantId = Tid, CompanyId = Company.Id, Year = 2026, Month = 1 };
            Db.PayrollRuns.Add(run);
            if (penalty > 0m)
                Db.PayrollAdjustments.Add(new PayrollAdjustment
                {
                    TenantId = Tid, PayrollRunId = run.Id, EmployeeId = Employee.Id, AdjustmentType = "Penalty",
                    Amount = -penalty, Status = "Approved", Reason = "Damage to company property",
                });
            await Db.SaveChangesAsync();
            Assert.IsType<OkObjectResult>(await Controller().Process(run.Id, CancellationToken.None));
            return run.Id;
        }

        public Task<IActionResult> Settle(decimal otherDues) =>
            Controller().FinalSettlement(new FinalSettlementRequest(Employee.Id, new DateOnly(2026, 6, 30), OtherDuesAmount: otherDues), CancellationToken.None);

        public async Task<List<string>> Codes(Guid runId) =>
            await Db.PayrollValidationResults.AsNoTracking().Where(v => v.PayrollRunId == runId).Select(v => v.Code).ToListAsync();

        private PayrollController Controller()
        {
            var http = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", Tid.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role, "Admin"), new Claim("permission", "payroll.write"),
                }, "test")),
            };
            return new PayrollController(Db, new Scope(), new HttpContextAccessor { HttpContext = http }, new F2NullNotifications(),
                new Pack(), new StubRuleReader(), new F2NullLetters(), new NullDocumentStorage(),
                new Zayra.Api.Infrastructure.Documents.PdfRenderGate(4))
            { ControllerContext = new ControllerContext { HttpContext = http } };
        }

        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await _connection.DisposeAsync(); }
    }

    private sealed class Scope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
            => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }

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
