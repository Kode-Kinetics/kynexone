using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Models;
using K = Zayra.Api.Tests.OpeningBalanceImportKit;

namespace Zayra.Api.Tests;

/// <summary>
/// Who may import, decided by permissions, never by a role name: every section needs employees.bulk_import (or Admin), and the
/// debt rows (loans, advances), which post an opening journal, need finance.opening_balances.import, which a per-user Deny takes
/// away. And the restatement guards read their evidence tenant-wide, so the importer's own company filter cannot hide it.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class OpeningBalanceImportPermissionPostgresTests(PostgresFixture fixture)
{
    private const string FinanceKey = "finance.opening_balances.import";
    private const string BulkImportKey = "employees.bulk_import";

    /// <summary>An HR Manager whose employees.bulk_import is denied is no longer an importer: the role name alone admits them to
    /// nothing, neither the template, nor a batch, nor a package.</summary>
    [Fact]
    public async Task AnHrManagerWithBulkImportDenied_IsNotAnImporter()
    {
        var (b, _, _) = await K.SeedAsync(fixture, "PM-HR");
        await using var db = fixture.CreateDb();
        var denied = K.Migration(db, b.Tenant, new[] { "HR Manager" }, null, new[] { BulkImportKey });

        Assert.IsType<ForbidResult>(denied.Template());
        Assert.IsType<ForbidResult>((await denied.Get(Guid.NewGuid(), default)).Result);
        var sections = new Dictionary<string, string> { ["companyCutover"] = await K.CutoverCsvAsync(fixture, b) };
        Assert.IsType<ForbidResult>((await denied.Commit(new MigrationPackageRequest($"pkg-{Guid.NewGuid():N}", sections, false), default)).Result);
        Assert.False(await db.CompanyCutovers.AnyAsync(c => c.TenantId == b.Tenant), "nothing was imported");

        // The same HR Manager with the seeded bundle imports as before.
        Assert.IsType<OkObjectResult>(K.Migration(db, b.Tenant, "HR Manager").Template());
    }

    /// <summary>The debt rows follow finance.opening_balances.import for every role: an HR Manager (who does not hold it) is refused
    /// the loan row and imports the rest of the package; Finance, Finance Approver and Admin (who hold it) import debts; a Finance
    /// user whose key is denied is refused the package; an Admin whose key is denied is refused the debt rows, per row, and still
    /// imports the rest.</summary>
    [Fact]
    public async Task DebtRows_FollowTheFinanceImportPermission_ForEveryRole()
    {
        var (b, cutover, number) = await K.SeedAsync(fixture, "PM-RL");
        await K.EnsureLoanTypeAsync(fixture, b);

        // HR Manager: the cutover (HR's section) imports, the loan row is refused in both languages.
        var hr = await K.CommitAsync(fixture, b, "loans", K.LoanCsv(b, number, paid: 3, outstanding: 300m, cutover),
            withCutover: true, expectErrors: true, roles: new[] { "HR Manager" });
        Assert.Contains("opening_balance_needs_finance", Assert.Single(hr), StringComparison.Ordinal);
        Assert.Contains("المالية", Assert.Single(hr), StringComparison.Ordinal);
        await using (var db = fixture.CreateDb())
        {
            Assert.True(await db.CompanyCutovers.AnyAsync(c => c.TenantId == b.Tenant && c.CompanyId == b.CompanyId));
            Assert.False(await db.EmployeeLoans.AnyAsync(l => l.TenantId == b.Tenant && l.LoanNumber == number));
        }

        // Finance, Finance Approver and Admin hold the key through their bundles.
        await K.CommitAsync(fixture, b, "loans", K.LoanCsv(b, number, paid: 3, outstanding: 300m, cutover), roles: new[] { "Finance" });
        Assert.Single(await K.OpeningJournalAsync(fixture, b, (await K.LoanAsync(fixture, b, number)).Id));
        var advance = $"PM-ADV-{Guid.NewGuid():N}"[..18];
        await K.CommitAsync(fixture, b, "advances", K.AdvanceCsv(b, advance, paid: 3, outstanding: 300m, cutover), roles: new[] { "Finance Approver" });
        Assert.Single(await K.OpeningJournalAsync(fixture, b, (await K.AdvanceAsync(fixture, b, advance)).Id));
        var adminLoan = $"PM-AD-{Guid.NewGuid():N}"[..18];
        await K.CommitAsync(fixture, b, "loans", K.LoanCsv(b, adminLoan, paid: 3, outstanding: 300m, cutover), roles: new[] { "Admin" });
        Assert.Single(await K.OpeningJournalAsync(fixture, b, (await K.LoanAsync(fixture, b, adminLoan)).Id));

        // A Finance user whose key is denied is no importer at all.
        var deniedLoan = $"PM-DN-{Guid.NewGuid():N}"[..18];
        await using (var db = fixture.CreateDb())
        {
            var finance = K.Migration(db, b.Tenant, new[] { "Finance" }, null, new[] { FinanceKey });
            var package = new Dictionary<string, string> { ["loans"] = K.LoanCsv(b, deniedLoan, paid: 3, outstanding: 300m, cutover) };
            Assert.IsType<ForbidResult>((await finance.Commit(new MigrationPackageRequest($"pkg-{Guid.NewGuid():N}", package, false), default)).Result);
            Assert.False(await db.EmployeeLoans.AnyAsync(l => l.TenantId == b.Tenant && l.LoanNumber == deniedLoan));
        }

        // An Admin whose key is denied: the debt row is refused, HR's section still imports.
        var adminDenied = await K.CommitAsync(fixture, b, "loans", K.LoanCsv(b, deniedLoan, paid: 3, outstanding: 300m, cutover),
            withCutover: true, expectErrors: true, roles: new[] { "Admin" }, denied: new[] { FinanceKey });
        Assert.Contains("opening_balance_needs_finance", Assert.Single(adminDenied), StringComparison.Ordinal);
        await using (var db = fixture.CreateDb())
            Assert.False(await db.EmployeeLoans.AnyAsync(l => l.TenantId == b.Tenant && l.LoanNumber == deniedLoan));
    }

    /// <summary>A repayment recorded against a carried-in loan is evidence that it has moved, whatever company the repayment row
    /// names. A Finance importer scoped to the loan's company, whose company filter hides a repayment naming no company, is still
    /// refused a restatement of the loan, exactly as a group-level importer is.</summary>
    [Fact]
    public async Task TheRestatementGuard_SeesEvidenceTheImportersCompanyFilterWouldHide()
    {
        var (b, cutover, number) = await K.SeedAsync(fixture, "PM-EV");
        await K.EnsureLoanTypeAsync(fixture, b);
        await K.CommitAsync(fixture, b, "loans", K.LoanCsv(b, number, paid: 3, outstanding: 300m, cutover), withCutover: true);
        var loan = await K.LoanAsync(fixture, b, number);
        await using (var seed = fixture.CreateDb())
        {
            // A repayment row that names no company (written before the company dimension was required).
            seed.LoanRepayments.Add(new LoanRepayment
            {
                TenantId = b.Tenant, CompanyId = null, LoanId = loan.Id, Amount = 100m, PaidDate = cutover.AddDays(3),
                Reference = "CASH-1", PaymentMethod = "Cash",
            });
            await seed.SaveChangesAsync();
        }

        var restated = K.LoanCsv(b, number, paid: 2, outstanding: 400m, cutover);
        var user = K.Principal(b.Tenant, new[] { "Finance" }, companyScope: b.CompanyId);
        await using (var scoped = K.ScopedDb(fixture, user))
        {
            Assert.False(await scoped.LoanRepayments.AnyAsync(r => r.LoanId == loan.Id), "the importer's company filter hides the row");
            var result = await K.Migration(scoped, user).Preview(new MigrationPackageRequest($"pkg-{Guid.NewGuid():N}",
                new Dictionary<string, string> { ["loans"] = restated }, true), default);
            var dto = Assert.IsType<MigrationReconciliationDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
            Assert.Contains("loan_collected_by_payroll", Assert.Single(dto.Errors), StringComparison.Ordinal);
        }
        var group = await K.CommitAsync(fixture, b, "loans", restated, expectErrors: true, dryRun: true, roles: new[] { "Finance" });
        Assert.Contains("loan_collected_by_payroll", Assert.Single(group), StringComparison.Ordinal);
        Assert.Equal(300m, (await K.LoanAsync(fixture, b, number)).OutstandingBalance);
    }
}
