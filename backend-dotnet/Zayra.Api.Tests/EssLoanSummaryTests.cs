using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class EssLoanSummaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dashboard_SeparatesCurrenciesAndOnlyCountsOwnDisbursedLoans(bool multipleCurrencies)
    {
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenantId = Guid.NewGuid();
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "ESS lender", CountryCode = "SA", DefaultCurrency = "SAR"
        };
        var employee = Employee(tenantId, "ME", company.Id);
        var colleague = Employee(tenantId, "COLLEAGUE", company.Id);
        var otherTenantEmployee = Employee(Guid.NewGuid(), "OTHER-TENANT", null);
        db.AddRange(company, employee, colleague, otherTenantEmployee);
        await db.SaveChangesAsync();

        var active = Loan(employee, "Active", "SAR", 200m);
        var overdue = Loan(employee, "Overdue", "SAR", 300m);
        var approved = Loan(employee, "Approved", "GBP", 800m);
        approved.DisbursementDate = null;
        var deleted = Loan(employee, "Active", "GBP", 900m);
        deleted.IsDeleted = true;
        db.AddRange(active, overdue, approved, deleted,
            Loan(colleague, "Active", "USD", 1200m),
            Loan(otherTenantEmployee, "Active", "KWD", 1300m));

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        db.AddRange(
            Installment(active, today.AddDays(4), "Pending", 100m, 20m),
            Installment(overdue, today.AddDays(-2), "Overdue", 90m, 30m),
            Installment(overdue, today.AddDays(-5), "Paid", 50m, 50m),
            Installment(approved, today.AddDays(-10), "Pending", 800m, 0m));
        if (multipleCurrencies)
        {
            var aedLoan = Loan(employee, "Active", "AED", 400m);
            db.AddRange(aedLoan, Installment(aedLoan, today.AddDays(1), "Pending", 120m, 10m));
        }
        await db.SaveChangesAsync();

        var result = await Controller(db, tenantId, employee.Id).Dashboard(default);

        var dashboard = Assert.IsType<ESSDashboardDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(employee.Id, dashboard.Profile.EmployeeId);
        Assert.NotNull(dashboard.LoanSummaries);
        Assert.Equal(multipleCurrencies ? 2 : 1, dashboard.LoanSummaries.Count);
        var sar = Assert.Single(dashboard.LoanSummaries, x => x.Currency == "SAR");
        Assert.Equal(500m, sar.TotalOutstanding);
        Assert.Equal(2, sar.ActiveLoanCount);
        Assert.Equal(60m, sar.NextInstallmentAmount);
        Assert.Equal(today.AddDays(-2).ToString("yyyy-MM-dd"), sar.NextInstallmentDate);
        Assert.DoesNotContain(dashboard.LoanSummaries, x => x.Currency is "GBP" or "USD" or "KWD");
        if (multipleCurrencies)
        {
            var aed = Assert.Single(dashboard.LoanSummaries, x => x.Currency == "AED");
            Assert.Equal(400m, aed.TotalOutstanding);
            Assert.Equal(1, aed.ActiveLoanCount);
            Assert.Equal(110m, aed.NextInstallmentAmount);
            Assert.Equal(today.AddDays(1).ToString("yyyy-MM-dd"), aed.NextInstallmentDate);
            Assert.Null(dashboard.LoansSummary);
        }
        else
        {
            Assert.Equal(sar, dashboard.LoansSummary);
        }
    }

    private static Employee Employee(Guid tenantId, string code, Guid? companyId) => new()
    {
        TenantId = tenantId, CompanyId = companyId, EmployeeCode = code, FullName = code,
        Status = "Active", JoiningDate = DateTime.UtcNow.Date.AddYears(-1)
    };

    private static EmployeeLoan Loan(Employee employee, string status, string currency, decimal outstanding) => new()
    {
        TenantId = employee.TenantId!.Value, CompanyId = employee.CompanyId,
        EmployeeIntId = employee.Id, EmployeeId = employee.PublicId, EmployeeName = employee.FullName,
        LoanNumber = Guid.NewGuid().ToString(), Status = status, Currency = currency,
        RepaymentMethod = "BankTransfer", OutstandingBalance = outstanding,
        ApprovedAmount = outstanding, DisbursementDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30))
    };

    private static LoanInstallment Installment(EmployeeLoan loan, DateOnly due, string status, decimal amount, decimal paid) => new()
    {
        TenantId = loan.TenantId, LoanId = loan.Id, DueDate = due, Status = status,
        AmountDue = amount, AmountPaid = paid
    };

    private static EmployeeSelfServiceController Controller(ZayraDbContext db, Guid tenantId, int employeeId)
    {
        var letters = new StubLetters();
        var storage = new UnusedStorage();
        return new EmployeeSelfServiceController(
            db, letters, new PdfRenderGate(1), new LeaveService(db, new ApprovalRouter(db)),
            new AttendanceService(db, TestNotifications.For(db), new UnusedHttpClientFactory()),
            new HrLetterIssuer(db, letters, storage), storage)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", tenantId.ToString()),
                        new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                        new Claim("employee_id", employeeId.ToString()),
                        new Claim("permission", "ess.read")
                    }, "Test"))
                }
            }
        };
    }

    private sealed class StubLetters : ILetterService
    {
        public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class UnusedStorage : IDocumentStorage
    {
        public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) => throw new NotSupportedException();
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => throw new NotSupportedException();
        public string ResolvePath(string storageUrl) => throw new NotSupportedException();
    }

    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new NotSupportedException();
    }
}
