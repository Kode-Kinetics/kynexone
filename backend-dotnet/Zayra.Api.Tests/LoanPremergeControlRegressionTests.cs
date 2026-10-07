using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class LoanPremergeControlRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HrBorrowerCannotApproveApplicationEnteredBySomeoneElse(bool publicIdentityOnly)
    {
        await using var h = await Fixture.Create();
        var loan = h.Loan(h.Company.Id, "Pending");
        if (publicIdentityOnly) loan.EmployeeIntId = null;
        var approval = new LoanApproval { TenantId = h.Tid, LoanId = loan.Id, ApproverRole = "HR Manager", StepOrder = 1 };
        h.Db.AddRange(loan, approval); await h.Db.SaveChangesAsync();

        var result = await h.Loans(h.Employee.UserAccountId!.Value).DecideApproval(loan.Id, approval.Id, new("Approved", null, null, null, null), default);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Pending", (await h.Db.EmployeeLoans.AsNoTracking().SingleAsync()).Status);
        Assert.Equal("Pending", (await h.Db.LoanApprovals.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await h.Db.LoanAuditLogs.ToListAsync());
    }

    [Fact]
    public async Task IndependentHrReviewerCanApproveApplicationEnteredBySomeoneElse()
    {
        await using var h = await Fixture.Create();
        var loan = h.Loan(h.Company.Id, "Pending");
        var approval = new LoanApproval { TenantId = h.Tid, LoanId = loan.Id, ApproverRole = "HR Manager", StepOrder = 1 };
        h.Db.AddRange(loan, approval); await h.Db.SaveChangesAsync();
        Assert.IsType<OkObjectResult>(await h.Loans(Guid.NewGuid()).DecideApproval(loan.Id, approval.Id, new("Approved", null, null, null, null), default));
        Assert.Equal("Approved", (await h.Db.EmployeeLoans.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task TransferredHrBorrowerCannotApproveWhenEmployeeIsHiddenByCompanyScope()
    {
        await using var h = await Fixture.Create();
        var loan = h.Loan(h.PriorCompany.Id, "Pending");
        var approval = new LoanApproval { TenantId = h.Tid, LoanId = loan.Id, ApproverRole = "HR Manager", StepOrder = 1 };
        h.Db.AddRange(loan, approval); await h.Db.SaveChangesAsync();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] {
            new Claim("tenant_id", h.Tid.ToString()), new Claim(ClaimTypes.NameIdentifier, h.Employee.UserAccountId!.Value.ToString()),
            new Claim(ClaimTypes.Role, "HR Manager"), new Claim("entity_access", JsonSerializer.Serialize(new { c = h.PriorCompany.Id, r = "HR Manager" })) }, "test")) };
        await using var scopedDb = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(h.Db.Database.GetDbConnection()).Options,
            new HttpContextAccessor { HttpContext = http });
        Assert.False(await scopedDb.Employees.AnyAsync(x => x.Id == h.Employee.Id));
        var controller = new LoansController(scopedDb, new Scope()) { ControllerContext = new() { HttpContext = http } };
        Assert.IsType<BadRequestObjectResult>(await controller.DecideApproval(loan.Id, approval.Id, new("Approved", null, null, null, null), default));
        Assert.Equal("Pending", (await h.Db.LoanApprovals.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await h.Db.LoanAuditLogs.ToListAsync());
    }

    [Theory]
    [InlineData("Weekly", "Monthly", false)]
    [InlineData("Monthly", "Weekly", true)]
    public async Task LaterEligibilityUsesFrozenFrequencyForAffordability(string frozen, string edited, bool eligible)
    {
        await using var h = await Fixture.Create();
        var loan = h.Loan(h.Company.Id, "Pending"); loan.RepaymentFrequency = frozen;
        var policy = h.Policy(); policy.MaxInstallmentPercentOfSalary = 10;
        loan.PolicySnapshotJson = JsonSerializer.Serialize(policy);
        h.Type.RepaymentFrequency = edited;
        h.Db.Add(loan); await h.Db.SaveChangesAsync();
        var assessment = await h.Assess(loan);
        Assert.Equal(eligible, assessment.Eligible);
        Assert.Equal(!eligible, assessment.Codes.Contains("SalaryAffordability"));
    }

    [Theory]
    [InlineData("Weekly", "Monthly")]
    [InlineData("Monthly", "Weekly")]
    public async Task LaterEligibilityUsesFrozenFrequencyForAllowedFrequencies(string frozen, string edited)
    {
        await using var h = await Fixture.Create();
        var loan = h.Loan(h.Company.Id, "Pending"); loan.RepaymentFrequency = frozen;
        var policy = h.Policy(); policy.AllowedRepaymentFrequenciesJson = JsonSerializer.Serialize(new[] { frozen });
        loan.PolicySnapshotJson = JsonSerializer.Serialize(policy);
        h.Type.RepaymentFrequency = edited;
        h.Db.Add(loan); await h.Db.SaveChangesAsync();
        Assert.True((await h.Assess(loan)).Eligible);
        var fresh = await new LoanEligibilityService(h.Db).EvaluateAsync(h.Tid, h.Employee, h.Type, 1500, 3, "BankTransfer", policySnapshotJson: loan.PolicySnapshotJson);
        Assert.Contains("RepaymentFrequency", fresh.Codes);
    }

    [Theory]
    [InlineData("Monthly", "Weekly", true)]
    [InlineData("Weekly", "Monthly", false)]
    public async Task LaterEligibilityUsesFrozenFrequencyForPayrollCompatibility(string frozen, string edited, bool eligible)
    {
        await using var h = await Fixture.Create();
        var loan = h.Loan(h.Company.Id, "Pending"); loan.RepaymentFrequency = frozen; loan.RepaymentMethod = "PayrollDeduction";
        loan.PolicySnapshotJson = JsonSerializer.Serialize(h.Policy()); h.Type.RepaymentFrequency = edited;
        h.Db.Add(loan); await h.Db.SaveChangesAsync();
        var assessment = await h.Assess(loan);
        Assert.Equal(eligible, assessment.Eligible);
        Assert.Equal(!eligible, assessment.Codes.Contains("RepaymentMethod"));
    }

    [Fact]
    public async Task RegularPayrollRecoversOnlyDebtOwnedByRunCompanyAfterEmployeeTransfer()
    {
        await using var h = await Fixture.Create();
        var prior = h.Loan(h.PriorCompany.Id); var current = h.Loan(h.Company.Id);
        h.Db.AddRange(prior, current);
        foreach (var loan in new[] { prior, current }) h.Db.Add(new LoanInstallment { TenantId = h.Tid, LoanId = loan.Id, InstallmentNumber = 1, DueDate = new(2026, 1, 1), AmountDue = 500 });
        var run = new PayrollRun { TenantId = h.Tid, CompanyId = h.Company.Id, Year = 2026, Month = 1 };
        h.Db.Add(run); await h.Db.SaveChangesAsync();
        Assert.IsType<OkObjectResult>(await h.Payroll().Process(run.Id, default));
        Assert.Equal(500m, (await h.Db.PayrollSlips.SingleAsync()).LoanDeductions);
        var untouched = await h.Db.EmployeeLoans.AsNoTracking().SingleAsync(x => x.Id == prior.Id);
        Assert.Equal(1500m, untouched.OutstandingBalance); Assert.Equal(0m, untouched.TotalRepaid); Assert.False(untouched.IsLockedByPayroll);
        var installment = await h.Db.LoanInstallments.AsNoTracking().SingleAsync(x => x.LoanId == prior.Id);
        Assert.Equal(0m, installment.AmountPaid); Assert.Null(installment.PayrollRunId);
        Assert.Equal(1000m, (await h.Db.EmployeeLoans.AsNoTracking().SingleAsync(x => x.Id == current.Id)).OutstandingBalance);
    }

    [Fact]
    public async Task FinalSettlementPlansOnlyDebtOwnedBySettlementCompanyAfterEmployeeTransfer()
    {
        await using var h = await Fixture.Create();
        var prior = h.Loan(h.PriorCompany.Id); var current = h.Loan(h.Company.Id);
        h.Db.AddRange(prior, current, new GCCComplianceSetting { TenantId = h.Tid, CountryCode = "ARE", EosbEnabled = true },
            new EmployeeOffboarding { TenantId = h.Tid, EmployeeId = h.Employee.Id, SeparationType = "Termination", Status = "InProgress", LastWorkingDay = new(2026, 1, 31) });
        await h.Db.SaveChangesAsync();
        Assert.IsType<OkObjectResult>(await h.Payroll().FinalSettlement(new(h.Employee.Id, new(2026, 1, 31), OtherDuesAmount: 5000), default));
        Assert.Equal(1500m, (await h.Db.EmployeeFinalSettlements.SingleAsync()).PlannedLoanRecovery);
        Assert.Equal(1500m, (await h.Db.EmployeeLoans.AsNoTracking().SingleAsync(x => x.Id == prior.Id)).OutstandingBalance);
    }

    [Fact]
    public async Task PaidFinalSettlementDoesNotReclassifyPriorCompanyDebt()
    {
        await using var h = await Fixture.Create();
        var prior = h.Loan(h.PriorCompany.Id);
        var run = new PayrollRun { TenantId = h.Tid, CompanyId = h.Company.Id, Year = 2026, Month = 1, Status = "Locked" };
        var batch = new PayrollPaymentBatch { TenantId = h.Tid, PayrollRunId = run.Id, BatchNumber = "TRANSFER-SETTLEMENT", WpsStatus = WpsStatuses.Accepted };
        var offboarding = new EmployeeOffboarding { TenantId = h.Tid, EmployeeId = h.Employee.Id, Status = "InProgress", LastWorkingDay = new(2026, 1, 31) };
        var settlement = new EmployeeFinalSettlement { TenantId = h.Tid, CompanyId = h.Company.Id, EmployeeId = h.Employee.Id,
            OffboardingId = offboarding.Id, PayrollRunId = run.Id, Currency = "AED", Status = FinalSettlementStatuses.Disbursing };
        h.Db.AddRange(prior, run, batch, offboarding, settlement,
            new FinanceGlEntry { TenantId = h.Tid, CompanyId = h.Company.Id, SourceModule = "Payroll", SourceEntityId = run.Id,
                EventType = GlEventTypes.Accrual, CreditAccount = "2100 - Salaries Payable", Description = PayrollGlDescriptions.NetPayable, Amount = 1000, Currency = "AED" },
            new FinanceGlEntry { TenantId = h.Tid, CompanyId = h.Company.Id, DebitAccount = "1400 - Employee Loans Receivable", Amount = 1500, Currency = "AED" });
        await h.Db.SaveChangesAsync();
        Assert.IsType<OkObjectResult>(await h.Payroll().SettlePaymentBatch(batch.Id, new(), default));
        Assert.Equal(1500m, (await h.Db.EmployeeLoans.AsNoTracking().SingleAsync()).OutstandingBalance);
        Assert.Equal(0m, settlement.ResidualDebtReclassed);
        Assert.Equal(0m, settlement.ResidualDebtUnbooked);
        Assert.Empty(await h.Db.PayrollEmployeeReceivables.ToListAsync());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection = new("DataSource=:memory:");
        public ZayraDbContext Db { get; private set; } = null!;
        public Guid Tid { get; } = Guid.NewGuid();
        public Company Company { get; private set; } = null!;
        public Company PriorCompany { get; private set; } = null!;
        public Employee Employee { get; private set; } = null!;
        public LoanType Type { get; private set; } = null!;
        public static async Task<Fixture> Create()
        {
            var h = new Fixture(); await h.connection.OpenAsync();
            h.Db = new(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(h.connection).Options);
            await h.Db.Database.EnsureCreatedAsync();
            h.Company = new() { TenantId = h.Tid, LegalNameEn = "Current employer", CountryCode = "ARE", Jurisdiction = "UAE-mainland", DefaultCurrency = "AED", RegistrationNumber = "CURRENT" };
            h.PriorCompany = new() { TenantId = h.Tid, LegalNameEn = "Prior employer", CountryCode = "ARE", DefaultCurrency = "AED", RegistrationNumber = "PRIOR" };
            h.Employee = new() { TenantId = h.Tid, CompanyId = h.Company.Id, EmployeeCode = "TRANSFER", FullName = "HR borrower", UserAccountId = Guid.NewGuid(), Nationality = "Indian", Status = "Active", JoiningDate = new(2024, 1, 1) };
            h.Type = new() { TenantId = h.Tid, Code = "PERSONAL", NameEn = "Personal", MaxAmount = 10000, MaxInstallments = 12 };
            var structure = new SalaryStructure { TenantId = h.Tid, CompanyId = h.Company.Id, Code = "STD", Name = "Standard", Currency = "AED" };
            h.Db.AddRange(h.Company, h.PriorCompany, h.Employee, h.Type, structure); await h.Db.SaveChangesAsync();
            h.Db.Add(new EmployeeSalaryStructure { TenantId = h.Tid, EmployeeId = h.Employee.Id, SalaryStructureId = structure.Id, BasicSalary = 8000, Currency = "AED", EffectiveDate = new(2025, 1, 1), IsActive = true });
            await h.Db.SaveChangesAsync(); return h;
        }
        public EmployeeLoan Loan(Guid companyId, string status = "Active") => new() { TenantId = Tid, CompanyId = companyId, EmployeeIntId = Employee.Id, EmployeeId = Employee.PublicId, EmployeeName = Employee.FullName, LoanTypeId = Type.Id, LoanNumber = Guid.NewGuid().ToString(), Status = status, RequestedAmount = 1500, RequestedInstallments = 3, ApprovedAmount = status == "Pending" ? 0 : 1500, ApprovedInstallments = 3, InstallmentAmount = 500, OutstandingBalance = status == "Pending" ? 0 : 1500, Currency = "AED", RepaymentMethod = status == "Pending" ? "BankTransfer" : "PayrollDeduction", RepaymentFrequency = "Monthly", CreatedBy = Guid.NewGuid(), RepaymentStartDate = new(2026, 1, 1) };
        public LoanPolicy Policy() => new() { TenantId = Tid, CompanyId = Company.Id, LoanTypeId = Type.Id, MaxAmount = 10000, MaxInstallments = 12, MaxConcurrentLoans = 10, AllowedRepaymentFrequenciesJson = "[\"Monthly\",\"Weekly\"]", AllowedRepaymentMethodsJson = "[\"BankTransfer\",\"PayrollDeduction\"]" };
        public Task<LoanEligibilityAssessment> Assess(EmployeeLoan loan) => new LoanEligibilityService(Db).EvaluateAsync(Tid, Employee, Type, loan.RequestedAmount, loan.RequestedInstallments, loan.RepaymentMethod, loan.Id, loan.PolicySnapshotJson);
        private DefaultHttpContext Context(Guid userId, string role) => new() { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("tenant_id", Tid.ToString()), new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Role, role), new Claim("permission", "payroll.write"), new Claim("permission", "payroll.export") }, "test")) };
        public LoansController Loans(Guid userId) => new(Db, new Scope()) { ControllerContext = new() { HttpContext = Context(userId, "HR Manager") } };
        public PayrollController Payroll()
        {
            var http = Context(Guid.NewGuid(), "Admin");
            return new(Db, new Scope(), new HttpContextAccessor { HttpContext = http }, new Notifications(), new Pack(), new StubRuleReader(), new Letters(), new NullDocumentStorage(), new Zayra.Api.Infrastructure.Documents.PdfRenderGate(4)) { ControllerContext = new() { HttpContext = http } };
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }
    private sealed class Scope : IDataScopeService
    { public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization }); }
    private sealed class Notifications : Zayra.Api.Infrastructure.Notifications.INotificationService
    {
        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
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
    { public Task<StatutoryDeductionResult> CalculateAsync(StatutoryDeductionInput input, CancellationToken ct = default) => Task.FromResult(new StatutoryDeductionResult(0, 0, Array.Empty<StatutoryDeductionLine>())); }
    private sealed class Letters : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
    {
        public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    }
}
