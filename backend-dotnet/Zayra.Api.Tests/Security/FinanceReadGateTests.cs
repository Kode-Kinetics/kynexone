using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// F10 — the loan book, salary advances and bonus batches are read behind their own permission.
///
/// <para>These GETs had no permission gate. Their employee-scope filter narrowed a manager to their team
/// and an employee to themselves, but anyone with organisation-wide employee access — a Compliance
/// Officer, a Recruiter, an HR Assistant — read every loan and advance in the tenant, and any signed-in
/// user read every bonus batch's totals. The report catalog cannot be stricter than the module it
/// summarises, so the module is gated here: loans and advances on loans.read or loans.write (the Loans
/// page's own navigation rule; HR Manager holds loans.write), bonus batches on payroll.read.</para>
/// </summary>
public sealed class FinanceReadGateTests
{
    [Fact]
    public async Task LoansAndAdvances_AreRefused_ToAnOrganisationReaderWithoutALoansPermission()
    {
        await using var db = Db();
        var (tid, loanId, advanceId) = await SeedAsync(db);
        var reader = new[] { "employees.read", "compliance.read" };   // a Compliance Officer's shape

        var loans = Loans(db, tid, reader);
        AssertRefused(await loans.ListLoans(null, null, 1, 30, default));
        AssertRefused(await loans.GetLoan(loanId, default));
        AssertRefused(await loans.GetInstallments(loanId, default));

        var advances = Advances(db, tid, reader);
        AssertRefused(await advances.List(null, null, 1, 30, default));
        AssertRefused(await advances.Get(advanceId, default));
    }

    [Theory]
    [InlineData("loans.read")]
    [InlineData("loans.write")]
    public async Task LoansAndAdvances_AreServed_ToAHolderOfEitherLoansPermission(string permission)
    {
        await using var db = Db();
        var (tid, loanId, advanceId) = await SeedAsync(db);

        Assert.IsType<OkObjectResult>(await Loans(db, tid, "employees.read", permission).ListLoans(null, null, 1, 30, default));
        Assert.IsType<OkObjectResult>(await Loans(db, tid, "employees.read", permission).GetLoan(loanId, default));
        Assert.IsType<OkObjectResult>(await Advances(db, tid, "employees.read", permission).List(null, null, 1, 30, default));
    }

    [Fact]
    public async Task BonusBatches_AreRefused_WithoutPayrollRead_AndServedWithIt()
    {
        await using var db = Db();
        var (tid, _, _) = await SeedAsync(db);
        var batch = new BonusBatch { TenantId = tid, BatchNumber = "BON-1", BatchName = "Year end", PaymentPeriod = "2026-12", TotalAmount = 90000, EmployeeCount = 30 };
        db.BonusBatches.Add(batch);
        await db.SaveChangesAsync();

        var reader = Bonuses(db, tid, "employees.read");
        AssertRefused(await reader.ListBatches(null, 1, 20, default));
        AssertRefused(await reader.GetBatch(batch.Id, default));

        Assert.IsType<OkObjectResult>(await Bonuses(db, tid, "employees.read", "payroll.read").ListBatches(null, 1, 20, default));
    }

    private static void AssertRefused(IActionResult result) =>
        ReportDataAuthorizationTests.AssertForbiddenWithReason(result, "permission");

    private static ZayraDbContext Db() => new(
        new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(Guid Tenant, Guid Loan, Guid Advance)> SeedAsync(ZayraDbContext db)
    {
        var tid = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tid, Name = "Finance", Slug = $"fin-{tid:N}" });
        var loan = new EmployeeLoan { TenantId = tid, EmployeeName = "Borrower", LoanNumber = "LN-1", Status = "Active", OutstandingBalance = 5000 };
        var advance = new SalaryAdvance { TenantId = tid, EmployeeName = "Borrower", AdvanceNumber = "AD-1", Status = "Active", OutstandingBalance = 800 };
        db.EmployeeLoans.Add(loan);
        db.SalaryAdvances.Add(advance);
        await db.SaveChangesAsync();
        return (tid, loan.Id, advance.Id);
    }

    private static ControllerContext Context(Guid tid, string[] permissions)
    {
        var claims = new List<Claim> { new("tenant_id", tid.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        return new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } };
    }

    private static LoansController Loans(ZayraDbContext db, Guid tid, params string[] permissions) =>
        new(db, new DataScopeService(db)) { ControllerContext = Context(tid, permissions) };

    private static AdvancesController Advances(ZayraDbContext db, Guid tid, params string[] permissions) =>
        new(db, new DataScopeService(db)) { ControllerContext = Context(tid, permissions) };

    private static BonusesController Bonuses(ZayraDbContext db, Guid tid, params string[] permissions) =>
        new(db, new DataScopeService(db)) { ControllerContext = Context(tid, permissions) };
}
