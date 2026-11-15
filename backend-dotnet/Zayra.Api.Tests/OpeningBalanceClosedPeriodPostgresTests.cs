using Microsoft.EntityFrameworkCore;
using Zayra.Api.Models;
using K = Zayra.Api.Tests.OpeningBalanceImportKit;

namespace Zayra.Api.Tests;

/// <summary>
/// A restatement whose opening journal cannot be posted (the cutover period is closed) is refused for its row and changes
/// nothing: not the balance, not the schedule, not the ledger. The import used to rewrite the loan or advance and its schedule
/// first and only then fail on the journal, and the row's failure detached only what it had added, so the restated balance was
/// saved with no journal behind it and the receivable stopped tying out.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class OpeningBalanceClosedPeriodPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ALoanRestatementIntoAClosedPeriod_IsRefused_AndChangesNothing()
    {
        var (b, cutover, number) = await K.SeedAsync(fixture, "CL-LN");
        await K.CommitAsync(fixture, b, "loans", K.LoanCsv(b, number, paid: 3, outstanding: 300m, cutover), withCutover: true);
        var loan = await K.LoanAsync(fixture, b, number);
        await K.ClosePeriodAsync(fixture, b, cutover);

        var errors = await K.CommitAsync(fixture, b, "loans", K.LoanCsv(b, number, paid: 4, outstanding: 200m, cutover), expectErrors: true);

        var error = Assert.Single(errors);
        Assert.Contains("opening_journal_period_closed", error, StringComparison.Ordinal);
        Assert.Contains("مقفلة", error, StringComparison.Ordinal);
        loan = await K.LoanAsync(fixture, b, number);
        Assert.Equal((300m, 300m, "Active"), (loan.OutstandingBalance, loan.TotalRepaid, loan.Status));
        await using var db = fixture.CreateDb();
        var schedule = await db.LoanInstallments.AsNoTracking().Where(i => i.LoanId == loan.Id).ToListAsync();
        Assert.Equal((3, 300m), (schedule.Count(i => i.Status == "Paid"), schedule.Where(i => i.Status == "Pending").Sum(i => i.AmountDue - i.AmountPaid)));
        Assert.Single(await K.OpeningJournalAsync(fixture, b, loan.Id));
    }

    [Fact]
    public async Task AnAdvanceRestatementIntoAClosedPeriod_IsRefused_AndChangesNothing()
    {
        var (b, cutover, number) = await K.SeedAsync(fixture, "CL-ADV");
        await K.CommitAsync(fixture, b, "advances", K.AdvanceCsv(b, number, paid: 3, outstanding: 300m, cutover), withCutover: true);
        var advance = await K.AdvanceAsync(fixture, b, number);
        await K.ClosePeriodAsync(fixture, b, cutover);

        var errors = await K.CommitAsync(fixture, b, "advances", K.AdvanceCsv(b, number, paid: 1, outstanding: 500m, cutover), expectErrors: true);

        var error = Assert.Single(errors);
        Assert.Contains("opening_journal_period_closed", error, StringComparison.Ordinal);
        advance = await K.AdvanceAsync(fixture, b, number);
        Assert.Equal((300m, 300m, "Active"), (advance.OutstandingBalance, advance.TotalRepaid, advance.Status));
        await using var db = fixture.CreateDb();
        var schedule = await db.AdvanceInstallments.AsNoTracking().Where(i => i.AdvanceId == advance.Id).ToListAsync();
        Assert.Equal((3, 300m), (schedule.Count(i => i.Status == "Paid"), schedule.Where(i => i.Status == "Pending").Sum(i => i.AmountDue - i.AmountPaid)));
        Assert.Single(await K.OpeningJournalAsync(fixture, b, advance.Id));
    }
}
