using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;
using Kit = Zayra.Api.Tests.DebtRacePostgresKit;

namespace Zayra.Api.Tests;

/// <summary>
/// A loan carried in by the opening-balance import is put on the ledger like a carried-in advance: DR 1400 Employee Loans
/// Receivable / CR 3900 Opening Balance Equity for the outstanding balance on the cutover date, once (a repeated package posts
/// nothing, a restatement only the difference), so the instalments payroll credits to 1400 relieve a debit that exists and the
/// loan sub-ledger ties out with the 1400 balance. The line never goes to the client's ERP, which already holds the loan.
///
/// <para>Real Postgres: the opening journal is a ledger writer, driven here against the append-only triggers.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class OpeningBalanceLoanImportPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task CarriedInLoan_PostsItsOpeningJournalOnce_AndARestatementPostsOnlyTheDifference()
    {
        var (b, cutover, number) = await SeedAsync();
        await CommitAsync(b, LoanCsv(b, number, paid: 3, outstanding: 300m, cutover), withCutover: true);
        var loan = await LoanAsync(b, number);
        Assert.Equal((300m, "Active"), (loan.OutstandingBalance, loan.Status));
        var opening = Assert.Single(await OpeningJournalAsync(b, loan.Id));
        Assert.Equal(("Loan", 300m, cutover, b.CompanyId), (opening.SourceModule, opening.Amount, opening.EntryDate, opening.CompanyId));
        Assert.StartsWith("1400", opening.DebitAccount, StringComparison.Ordinal);
        Assert.StartsWith("3900", opening.CreditAccount, StringComparison.Ordinal);

        // The same figures again: nothing posts twice.
        await CommitAsync(b, LoanCsv(b, number, paid: 3, outstanding: 300m, cutover));
        Assert.Single(await OpeningJournalAsync(b, loan.Id));

        // A restatement down: only the difference, the other way round.
        await CommitAsync(b, LoanCsv(b, number, paid: 4, outstanding: 200m, cutover));
        var lines = await OpeningJournalAsync(b, loan.Id);
        Assert.Equal(2, lines.Count);
        var down = lines.Single(l => l.Id != opening.Id);
        Assert.Equal(100m, down.Amount);
        Assert.StartsWith("3900", down.DebitAccount, StringComparison.Ordinal);
        Assert.StartsWith("1400", down.CreditAccount, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CarriedInLoan_TiesOutWith1400_AfterTheImport_AndAfterOnePayrollRecovery()
    {
        var (b, cutover, number) = await SeedAsync();
        await CommitAsync(b, LoanCsv(b, number, paid: 3, outstanding: 300m, cutover), withCutover: true);
        // The borrower's own native loan (100, disbursed through the payout path) is in the same 1400.
        Assert.Equal((400m, 400m), await TieOutAsync(b));

        var runId = await Kit.SeedDraftRunAsync(fixture, b, b.Today.Year, b.Today.Month);
        await using (var db = fixture.CreateDb())
            Assert.Equal(200, (await Kit.ProcessAsync(db, b, runId, null, CancellationToken.None)).StatusCode);
        await using (var db = fixture.CreateDb()) await ReconTestSupport.SignOffAsync(db, runId);
        await using (var db = fixture.CreateDb())
        {
            var approved = await Kit.Payroll(db, b.Tenant).Approve(runId, new PayrollDecisionRequest("reviewed"), CancellationToken.None);
            Assert.True(approved is OkObjectResult, Describe(approved));
        }
        await using (var db = fixture.CreateDb())
        {
            var locked = await Kit.Payroll(db, b.Tenant).Lock(runId, CancellationToken.None);
            Assert.True(locked is OkObjectResult, Describe(locked));
        }

        Assert.Equal(200m, (await LoanAsync(b, number)).OutstandingBalance);
        Assert.Equal((250m, 250m), await TieOutAsync(b));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private async Task<(Kit.Borrower B, DateOnly Cutover, string Number)> SeedAsync()
    {
        var b = await Kit.SeedBorrowerAsync(fixture, 100m, 2);
        return (b, new DateOnly(b.Today.Year, b.Today.Month, 1), $"LEG-LN-{Guid.NewGuid():N}"[..20]);
    }

    private async Task CommitAsync(Kit.Borrower b, string loansCsv, bool withCutover = false)
    {
        var sections = new Dictionary<string, string> { ["loans"] = loansCsv };
        if (withCutover)
        {
            await using var read = fixture.CreateDb();
            var registration = (await read.Companies.AsNoTracking().SingleAsync(c => c.Id == b.CompanyId)).RegistrationNumber;
            var cutover = new DateOnly(b.Today.Year, b.Today.Month, 1);
            sections["companyCutover"] = $"CompanyRegistrationNumber,CompanyLegalName,CutoverDate,SourceSystem,Status,Notes\n{registration},,{cutover:yyyy-MM-dd},SAP,Active,Wave 1\n";
        }
        await using var db = fixture.CreateDb();
        var result = await Migration(db, b.Tenant).Commit(new MigrationPackageRequest($"pkg-{Guid.NewGuid():N}", sections, false), default);
        var dto = Assert.IsType<MigrationReconciliationDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(dto.Errors.Count == 0, string.Join(" | ", dto.Errors));
    }

    private async Task<EmployeeLoan> LoanAsync(Kit.Borrower b, string number)
    {
        await using var db = fixture.CreateDb();
        return await db.EmployeeLoans.AsNoTracking().SingleAsync(l => l.TenantId == b.Tenant && l.LoanNumber == number);
    }

    private async Task<List<FinanceGlEntry>> OpeningJournalAsync(Kit.Borrower b, Guid loanId)
    {
        await using var db = fixture.CreateDb();
        return await db.FinanceGlEntries.AsNoTracking()
            .Where(g => g.TenantId == b.Tenant && g.SourceEntityId == loanId && g.EventType == "OpeningBalance" && !g.IsReversed)
            .OrderBy(g => g.CreatedAtUtc).ToListAsync();
    }

    /// <summary>(Σ OutstandingBalance of the company's open loans, the company's 1400 balance on the ledger).</summary>
    private async Task<(decimal SubLedger, decimal Gl)> TieOutAsync(Kit.Borrower b)
    {
        await using var db = fixture.CreateDb();
        var subLedger = (await db.EmployeeLoans.AsNoTracking()
                .Where(l => l.TenantId == b.Tenant && l.CompanyId == b.CompanyId && !l.IsDeleted && (l.Status == "Active" || l.Status == "Overdue"))
                .Select(l => l.OutstandingBalance).ToListAsync()).Sum();
        var glCtx = await GlAccountResolver.LoadAsync(db, b.Tenant, b.CompanyId, CancellationToken.None);
        var account = GlAccountResolver.AccountLabel(GlControlAccounts.LoanReceivableDriver, glCtx);
        var gl = await GlControlAccounts.LoadAsync(db, b.Tenant, b.CompanyId, account, CancellationToken.None);
        return (subLedger, gl.Scoped);
    }

    private static string Describe(IActionResult result) =>
        result is ObjectResult o ? $"{o.StatusCode}: {System.Text.Json.JsonSerializer.Serialize(o.Value)}" : result.GetType().Name;

    private static string LoanCsv(Kit.Borrower b, string number, int paid, decimal outstanding, DateOnly firstUnpaid) =>
        "EmployeeCode,LoanNumber,LoanTypeCode,LoanTypeName,OriginalAmount,InstallmentAmount,TotalInstallments,InstallmentsPaid,OutstandingBalance,FirstUnpaidDueDate,DisbursementDate,Currency,SourceSystem,SourceRecordId\n" +
        $"{b.EmployeeCode},{number},LEGACY,Legacy loan,600,100,6,{paid},{outstanding},{firstUnpaid:yyyy-MM-dd},2025-01-01,SAR,SAP,LN-1\n";

    private static MigrationImportController Migration(ZayraDbContext db, Guid tid) =>
        new(db, new Pbkdf2PasswordHasher(), new AuditService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", tid.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                        new Claim(ClaimTypes.Name, "consultant"), new Claim(ClaimTypes.Role, "Admin"),
                        new Claim("permission", "payroll.read"), new Claim("permission", "payroll.write"),
                    }, "test")),
                },
            },
        };
}
