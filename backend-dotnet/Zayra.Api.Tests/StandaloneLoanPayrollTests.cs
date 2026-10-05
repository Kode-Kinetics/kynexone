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
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class StandaloneLoanPayrollTests
{
    [Fact]
    public async Task FinalPayrollInstallment_RecoversRoundingResidual_AndLeavesStandaloneResidualUntouched()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var tid = Guid.NewGuid();
        var company = new Company
        {
            TenantId = tid, LegalNameEn = "Loan Rounding Co", CountryCode = "ARE",
            Jurisdiction = "UAE-mainland", DefaultCurrency = "AED", IsActive = true, RegistrationNumber = "ROUND-001"
        };
        var structure = new SalaryStructure { TenantId = tid, CompanyId = company.Id, Code = "STD", Name = "Standard", Currency = "AED" };
        var employee = new Employee
        {
            TenantId = tid, CompanyId = company.Id, EmployeeCode = "ROUND-001", FullName = "Loan Borrower",
            Nationality = "Indian", Status = "Active", JoiningDate = new DateTime(2025, 1, 1)
        };
        db.AddRange(company, structure, employee);
        await db.SaveChangesAsync();
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tid, EmployeeId = employee.Id, SalaryStructureId = structure.Id,
            BasicSalary = 8_000m, Currency = "AED", EffectiveDate = new DateOnly(2026, 1, 1), IsActive = true
        });
        var standalone = Loan(tid, employee, "LN-STANDALONE-ROUND", "BankTransfer");
        var payroll = Loan(tid, employee, "LN-PAYROLL-ROUND", "PayrollDeduction");
        foreach (var loan in new[] { standalone, payroll })
        {
            loan.ApprovedAmount = 100m;
            loan.ApprovedInstallments = 3;
            loan.InstallmentAmount = 33.33m;
            loan.TotalRepaid = 66.66m;
            loan.OutstandingBalance = 33.34m;
            db.EmployeeLoans.Add(loan);
            for (var number = 1; number <= 3; number++)
                db.LoanInstallments.Add(new LoanInstallment
                {
                    TenantId = tid, LoanId = loan.Id, InstallmentNumber = number,
                    DueDate = new DateOnly(2026, number, 1), AmountDue = number == 3 ? 33.34m : 33.33m,
                    AmountPaid = number == 3 ? 0m : 33.33m, Status = number == 3 ? "Pending" : "Paid"
                });
        }
        var run = new PayrollRun { TenantId = tid, CompanyId = company.Id, Year = 2026, Month = 3 };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();

        Assert.IsType<OkObjectResult>(await Controller(db, tid).Process(run.Id, CancellationToken.None));
        Assert.Equal(33.34m, (await db.PayrollSlips.SingleAsync(x => x.RunId == run.Id)).LoanDeductions);
        var recovered = await db.EmployeeLoans.AsNoTracking().SingleAsync(x => x.Id == payroll.Id);
        Assert.Equal(0m, recovered.OutstandingBalance);
        Assert.Equal(100m, recovered.TotalRepaid);
        Assert.Equal("Closed", recovered.Status);
        var finalInstallment = await db.LoanInstallments.AsNoTracking().SingleAsync(x => x.LoanId == payroll.Id && x.InstallmentNumber == 3);
        Assert.Equal(33.34m, finalInstallment.AmountPaid);
        Assert.Equal("Paid", finalInstallment.Status);
        Assert.Equal(run.Id, finalInstallment.PayrollRunId);
        var untouched = await db.EmployeeLoans.AsNoTracking().SingleAsync(x => x.Id == standalone.Id);
        Assert.Equal(33.34m, untouched.OutstandingBalance);
        Assert.Equal(66.66m, untouched.TotalRepaid);
        Assert.Equal("Active", untouched.Status);
        Assert.Equal(0m, (await db.LoanInstallments.AsNoTracking().SingleAsync(x => x.LoanId == standalone.Id && x.InstallmentNumber == 3)).AmountPaid);
    }

    [Theory]
    [InlineData("Active", false)]
    [InlineData("Overdue", false)]
    [InlineData("Active", true)]
    [InlineData("Overdue", true)]
    public async Task RegularPayroll_OnlyRecoversUnheldPayrollLoan_AndFinalSettlementHonorsHold(string payrollLoanStatus, bool onHold)
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var tid = Guid.NewGuid();
        var company = new Company
        {
            TenantId = tid, LegalNameEn = "Standalone Loans Co", CountryCode = "ARE",
            Jurisdiction = "UAE-mainland", DefaultCurrency = "AED", IsActive = true,
            RegistrationNumber = "SL-001"
        };
        var structure = new SalaryStructure { TenantId = tid, CompanyId = company.Id, Code = "STD", Name = "Standard", Currency = "AED" };
        var employee = new Employee
        {
            TenantId = tid, CompanyId = company.Id, EmployeeCode = "SL-001", FullName = "Loan Borrower",
            Nationality = "Indian", Status = "Active", JoiningDate = new DateTime(2025, 1, 1)
        };
        db.AddRange(company, structure, employee);
        await db.SaveChangesAsync();
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tid, EmployeeId = employee.Id, SalaryStructureId = structure.Id,
            BasicSalary = 8_000m, Currency = "AED", EffectiveDate = new DateOnly(2026, 1, 1), IsActive = true
        });
        var standalone = Loan(tid, employee, "LN-STANDALONE", "BankTransfer");
        var legacy = Loan(tid, employee, "LN-LEGACY", "PayrollDeduction");
        legacy.Status = payrollLoanStatus;
        legacy.CollectionStatus = onHold ? "OnHold" : "Normal";
        db.EmployeeLoans.AddRange(standalone, legacy);
        foreach (var loan in new[] { standalone, legacy })
            db.LoanInstallments.Add(new LoanInstallment
            {
                TenantId = tid, LoanId = loan.Id, InstallmentNumber = 1,
                DueDate = new DateOnly(2026, 1, 1), AmountDue = 500m
            });
        var run = new PayrollRun { TenantId = tid, CompanyId = company.Id, Year = 2026, Month = 1 };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();

        Assert.IsType<OkObjectResult>(await Controller(db, tid).Process(run.Id, CancellationToken.None));

        var slip = await db.PayrollSlips.SingleAsync(s => s.RunId == run.Id);
        Assert.Equal(onHold ? 0m : 500m, slip.LoanDeductions);
        var deductions = await db.PayrollDeductions.Where(x => x.PayrollRunId == run.Id && x.ComponentCode == "LOAN_EMI").ToListAsync();
        if (onHold) Assert.Empty(deductions);
        else Assert.Single(deductions);
        var unchanged = await db.EmployeeLoans.AsNoTracking().SingleAsync(x => x.Id == standalone.Id);
        Assert.Equal(1_500m, unchanged.OutstandingBalance);
        Assert.Equal(0m, unchanged.TotalRepaid);
        Assert.False(unchanged.IsLockedByPayroll);
        var untouchedInstallment = await db.LoanInstallments.AsNoTracking().SingleAsync(x => x.LoanId == standalone.Id);
        Assert.Equal(0m, untouchedInstallment.AmountPaid);
        Assert.Null(untouchedInstallment.PayrollRunId);
        var recovered = await db.EmployeeLoans.AsNoTracking().SingleAsync(x => x.Id == legacy.Id);
        Assert.Equal(onHold ? 1_500m : 1_000m, recovered.OutstandingBalance);
        Assert.Equal(onHold ? 0m : 500m, recovered.TotalRepaid);
        if (onHold)
        {
            var heldInstallment = await db.LoanInstallments.AsNoTracking().SingleAsync(x => x.LoanId == legacy.Id);
            Assert.Equal(0m, heldInstallment.AmountPaid);
            Assert.Null(heldInstallment.PayrollRunId);
            Assert.Equal("OnHold", recovered.CollectionStatus);
        }

        // Termination must not silently turn the standalone balance into a salary deduction.
        db.GCCComplianceSettings.Add(new GCCComplianceSetting { TenantId = tid, CountryCode = "ARE", EosbEnabled = true });
        db.EmployeeOffboardings.Add(new EmployeeOffboarding
        {
            TenantId = tid, EmployeeId = employee.Id, SeparationType = "Termination",
            Status = "InProgress", LastWorkingDay = new DateOnly(2026, 1, 31)
        });
        await db.SaveChangesAsync();
        Assert.IsType<OkObjectResult>(await Controller(db, tid).FinalSettlement(
            new FinalSettlementRequest(employee.Id, new DateOnly(2026, 1, 31), OtherDuesAmount: 5_000m), CancellationToken.None));
        var settlement = await db.EmployeeFinalSettlements.SingleAsync();
        Assert.Equal(onHold ? 0m : 1_000m, settlement.PlannedLoanRecovery);
        Assert.Equal(1_500m, (await db.EmployeeLoans.AsNoTracking().SingleAsync(x => x.Id == standalone.Id)).OutstandingBalance);
        Assert.Equal(onHold ? 1_500m : 1_000m, (await db.EmployeeLoans.AsNoTracking().SingleAsync(x => x.Id == legacy.Id)).OutstandingBalance);
    }

    private static EmployeeLoan Loan(Guid tid, Employee employee, string number, string method) => new()
    {
        TenantId = tid, CompanyId = employee.CompanyId, EmployeeIntId = employee.Id, EmployeeId = employee.PublicId,
        EmployeeName = employee.FullName, LoanNumber = number, Status = "Active", RepaymentMethod = method,
        ApprovedAmount = 1_500m, InstallmentAmount = 500m, OutstandingBalance = 1_500m,
        RepaymentStartDate = new DateOnly(2026, 1, 1)
    };

    private static PayrollController Controller(ZayraDbContext db, Guid tid)
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("tenant_id", tid.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Role, "Admin"), new Claim("permission", "payroll.write")
            }, "test"))
        };
        return new PayrollController(db, new Scope(), new HttpContextAccessor { HttpContext = http }, new Notifications(),
            new Pack(), new StubRuleReader(), new Letters(), new NullDocumentStorage(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(4))
        { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private sealed class Scope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
            => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }
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
    {
        public Task<StatutoryDeductionResult> CalculateAsync(StatutoryDeductionInput input, CancellationToken ct = default)
            => Task.FromResult(new StatutoryDeductionResult(0m, 0m, Array.Empty<StatutoryDeductionLine>()));
    }
    private sealed class Letters : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
    {
        public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    }
}
