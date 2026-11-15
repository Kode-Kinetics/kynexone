using Microsoft.EntityFrameworkCore;
using K = Zayra.Api.Tests.OpeningBalanceImportKit;

namespace Zayra.Api.Tests;

/// <summary>
/// A loan is restated by the opening-balance import only when the import carried it in, it is still open, and nothing has been
/// recovered from it, exactly as for advances. A package row naming a loan disbursed in this product would otherwise overwrite it
/// and debit 1400 a second time; a loan whose leftover a final settlement moved to 1420 would be reopened while 1420 still carries
/// the same debt.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class OpeningBalanceLoanGuardsPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ALoanTheImportDidNotCarryIn_IsRefused_AndLeftAlone()
    {
        var (b, cutover, _) = await K.SeedAsync(fixture, "GD-NAT");
        string nativeNumber;
        await using (var db = fixture.CreateDb())
            nativeNumber = (await db.EmployeeLoans.AsNoTracking().SingleAsync(l => l.Id == b.LoanId)).LoanNumber;

        var errors = await K.CommitAsync(fixture, b, "loans",
            K.LoanCsv(b, nativeNumber, paid: 0, outstanding: 100m, cutover, original: 100m, instalment: 50m, total: 2), withCutover: true, expectErrors: true);

        Assert.Contains("loan_not_carried_in", Assert.Single(errors), StringComparison.Ordinal);
        var loan = await K.LoanAsync(fixture, b, nativeNumber);
        Assert.Equal((100m, 0m, "Active"), (loan.OutstandingBalance, loan.TotalRepaid, loan.Status));
        Assert.Empty(await K.OpeningJournalAsync(fixture, b, loan.Id));
    }

    [Fact]
    public async Task ACarriedInLoanClosedByASettlementMoveTo1420_IsNeverReopened()
    {
        var (b, cutover, number) = await K.SeedAsync(fixture, "GD-CL");
        await K.CommitAsync(fixture, b, "loans", K.LoanCsv(b, number, paid: 3, outstanding: 300m, cutover), withCutover: true);
        await using (var db = fixture.CreateDb())
            await db.EmployeeLoans.Where(l => l.TenantId == b.Tenant && l.LoanNumber == number)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.Status, "Closed").SetProperty(l => l.OutstandingBalance, 0m));

        var errors = await K.CommitAsync(fixture, b, "loans", K.LoanCsv(b, number, paid: 3, outstanding: 300m, cutover), expectErrors: true);

        Assert.Contains("loan_not_active", Assert.Single(errors), StringComparison.Ordinal);
        var loan = await K.LoanAsync(fixture, b, number);
        Assert.Equal((0m, "Closed"), (loan.OutstandingBalance, loan.Status));
    }

    [Fact]
    public async Task ACarriedInLoanPartlyMovedTo1420_CountsAsRecovered_DifferentFiguresAreRefused_TheSameAreANoOp()
    {
        var (b, cutover, number) = await K.SeedAsync(fixture, "GD-MV");
        await K.CommitAsync(fixture, b, "loans", K.LoanCsv(b, number, paid: 3, outstanding: 300m, cutover), withCutover: true);
        // A settlement moved 100 to 1420: the balance drops, nothing is repaid.
        await using (var db = fixture.CreateDb())
            await db.EmployeeLoans.Where(l => l.TenantId == b.Tenant && l.LoanNumber == number)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.OutstandingBalance, 200m));

        var errors = await K.CommitAsync(fixture, b, "loans", K.LoanCsv(b, number, paid: 1, outstanding: 500m, cutover), expectErrors: true);
        Assert.Contains("loan_collected_by_payroll", Assert.Single(errors), StringComparison.Ordinal);

        await K.CommitAsync(fixture, b, "loans", K.LoanCsv(b, number, paid: 3, outstanding: 300m, cutover));
        var loan = await K.LoanAsync(fixture, b, number);
        Assert.Equal((200m, 300m, "Active"), (loan.OutstandingBalance, loan.TotalRepaid, loan.Status));
    }
}
