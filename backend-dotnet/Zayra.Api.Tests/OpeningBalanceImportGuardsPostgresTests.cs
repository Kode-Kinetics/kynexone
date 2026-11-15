using Microsoft.EntityFrameworkCore;
using K = Zayra.Api.Tests.OpeningBalanceImportKit;

namespace Zayra.Api.Tests;

/// <summary>
/// Who may import which opening-balance debt row: nobody imports a debt of their own employee record; a restatement never
/// re-points a debt to another employee or company; a finance-only importer does not create loan types; forgiving part of a
/// carried-in debt needs an Admin; and the right to import debt rows is the finance.opening_balances.import permission, so a
/// per-user Deny of it takes the right away, Admin included. Every refusal is per row and in English and Arabic.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class OpeningBalanceImportGuardsPostgresTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("Admin", false)]
    [InlineData("Finance", false)]
    [InlineData("Admin", true)]
    [InlineData("Finance", true)]
    public async Task NobodyImportsADebtOfTheirOwnEmployeeRecord(string role, bool linkedOnlyByAccountRow)
    {
        var (b, cutover, number) = await K.SeedAsync(fixture, "OWN");
        await K.CommitAsync(fixture, b, "companyCutover", await K.CutoverCsvAsync(fixture, b));
        await K.EnsureLoanTypeAsync(fixture, b);
        var me = Guid.NewGuid();
        if (linkedOnlyByAccountRow) await K.LinkBorrowerByAccountRowOnlyAsync(fixture, b, me);
        else await K.LinkBorrowerToUserAsync(fixture, b, me);

        foreach (var dryRun in new[] { true, false })
        {
            var loan = await K.CommitAsync(fixture, b, "loans", K.LoanCsv(b, number, paid: 3, outstanding: 300m, cutover),
                expectErrors: true, dryRun: dryRun, roles: new[] { role }, userId: me);
            Assert.Contains("opening_balance_own_record", Assert.Single(loan), StringComparison.Ordinal);
            Assert.Contains("سجلك الوظيفي", loan[0], StringComparison.Ordinal);
            var advance = await K.CommitAsync(fixture, b, "advances", K.AdvanceCsv(b, $"OWN-A-{Guid.NewGuid():N}"[..16], paid: 3, outstanding: 300m, cutover),
                expectErrors: true, dryRun: dryRun, roles: new[] { role }, userId: me);
            Assert.Contains("opening_balance_own_record", Assert.Single(advance), StringComparison.Ordinal);
        }
        await using var db = fixture.CreateDb();
        Assert.False(await db.EmployeeLoans.AnyAsync(l => l.TenantId == b.Tenant && l.LoanNumber == number));
    }

    [Fact]
    public async Task ARestatementNamingAnotherEmployee_IsRefused_ForLoansAndAdvances()
    {
        var (b, cutover, number) = await K.SeedAsync(fixture, "RPT");
        var advanceNumber = $"RPT-A-{Guid.NewGuid():N}"[..16];
        await K.CommitAsync(fixture, b, "loans", K.LoanCsv(b, number, paid: 3, outstanding: 300m, cutover), withCutover: true);
        await K.CommitAsync(fixture, b, "advances", K.AdvanceCsv(b, advanceNumber, paid: 3, outstanding: 300m, cutover));
        var colleague = await K.AddColleagueAsync(fixture, b);
        var other = b with { EmployeeCode = colleague.Code };

        var loanErrors = await K.CommitAsync(fixture, b, "loans", K.LoanCsv(other, number, paid: 3, outstanding: 300m, cutover), expectErrors: true);
        Assert.Contains("opening_balance_repoint_refused", Assert.Single(loanErrors), StringComparison.Ordinal);
        var advanceErrors = await K.CommitAsync(fixture, b, "advances", K.AdvanceCsv(other, advanceNumber, paid: 3, outstanding: 300m, cutover), expectErrors: true);
        Assert.Contains("opening_balance_repoint_refused", Assert.Single(advanceErrors), StringComparison.Ordinal);

        Assert.Equal(b.EmployeeId, (await K.LoanAsync(fixture, b, number)).EmployeeIntId);
        Assert.Equal(b.EmployeeId, (await K.AdvanceAsync(fixture, b, advanceNumber)).EmployeeIntId);
    }

    [Fact]
    public async Task AFinanceOnlyImporter_DoesNotCreateALoanType_AnAdminDoes()
    {
        var (b, cutover, number) = await K.SeedAsync(fixture, "LTY");
        await K.CommitAsync(fixture, b, "companyCutover", await K.CutoverCsvAsync(fixture, b));

        var errors = await K.CommitAsync(fixture, b, "loans", K.LoanCsv(b, number, paid: 3, outstanding: 300m, cutover),
            expectErrors: true, roles: new[] { "Finance" });
        var error = Assert.Single(errors);
        Assert.Contains("loan_type_missing", error, StringComparison.Ordinal);
        Assert.Contains("LEGACY", error, StringComparison.Ordinal);
        Assert.Contains("نوع القرض", error, StringComparison.Ordinal);
        await using (var db = fixture.CreateDb())
            Assert.False(await db.LoanTypes.AnyAsync(t => t.TenantId == b.Tenant && t.Code == "LEGACY"));

        await K.CommitAsync(fixture, b, "loans", K.LoanCsv(b, number, paid: 3, outstanding: 300m, cutover));
        await using var verify = fixture.CreateDb();
        Assert.True(await verify.LoanTypes.AnyAsync(t => t.TenantId == b.Tenant && t.Code == "LEGACY"));
    }

    [Fact]
    public async Task RestatingADebtLower_NeedsAnAdmin_RestatingItHigherDoesNot()
    {
        var (b, cutover, number) = await K.SeedAsync(fixture, "RED");
        await K.EnsureLoanTypeAsync(fixture, b);
        var advanceNumber = $"RED-A-{Guid.NewGuid():N}"[..16];
        await K.CommitAsync(fixture, b, "loans", K.LoanCsv(b, number, paid: 3, outstanding: 300m, cutover), withCutover: true);
        await K.CommitAsync(fixture, b, "advances", K.AdvanceCsv(b, advanceNumber, paid: 3, outstanding: 300m, cutover));

        var loanDown = await K.CommitAsync(fixture, b, "loans", K.LoanCsv(b, number, paid: 4, outstanding: 200m, cutover), expectErrors: true, roles: new[] { "Finance" });
        Assert.Contains("opening_balance_reduction_needs_admin", Assert.Single(loanDown), StringComparison.Ordinal);
        Assert.Contains("مسؤول النظام", loanDown[0], StringComparison.Ordinal);
        var advanceDown = await K.CommitAsync(fixture, b, "advances", K.AdvanceCsv(b, advanceNumber, paid: 4, outstanding: 200m, cutover), expectErrors: true, roles: new[] { "Finance Approver" });
        Assert.Contains("opening_balance_reduction_needs_admin", Assert.Single(advanceDown), StringComparison.Ordinal);
        Assert.Equal(300m, (await K.LoanAsync(fixture, b, number)).OutstandingBalance);

        await K.CommitAsync(fixture, b, "loans", K.LoanCsv(b, number, paid: 2, outstanding: 400m, cutover), roles: new[] { "Finance" });
        Assert.Equal(400m, (await K.LoanAsync(fixture, b, number)).OutstandingBalance);

        await K.CommitAsync(fixture, b, "loans", K.LoanCsv(b, number, paid: 4, outstanding: 200m, cutover));
        Assert.Equal(200m, (await K.LoanAsync(fixture, b, number)).OutstandingBalance);
    }

    [Fact]
    public async Task ADeniedOpeningBalancePermission_RefusesDebtRows_EvenForAnAdmin_AndForbidsAFinanceUser()
    {
        var (b, cutover, number) = await K.SeedAsync(fixture, "DNY");
        await K.CommitAsync(fixture, b, "companyCutover", await K.CutoverCsvAsync(fixture, b));
        await K.EnsureLoanTypeAsync(fixture, b);
        var deny = new[] { "finance.opening_balances.import" };

        var admin = await K.CommitAsync(fixture, b, "loans", K.LoanCsv(b, number, paid: 3, outstanding: 300m, cutover),
            expectErrors: true, roles: new[] { "Admin" }, denied: deny);
        Assert.Contains("opening_balance_needs_finance", Assert.Single(admin), StringComparison.Ordinal);

        await using var db = fixture.CreateDb();
        var finance = K.Migration(db, b.Tenant, new[] { "Finance" }, null, deny);
        var result = await finance.Commit(new Zayra.Api.Models.MigrationPackageRequest($"pkg-{Guid.NewGuid():N}",
            new Dictionary<string, string> { ["loans"] = K.LoanCsv(b, number, paid: 3, outstanding: 300m, cutover) }, false), default);
        Assert.IsType<Microsoft.AspNetCore.Mvc.ForbidResult>(result.Result);
        Assert.False(await db.EmployeeLoans.AnyAsync(l => l.TenantId == b.Tenant && l.LoanNumber == number));
    }
}
