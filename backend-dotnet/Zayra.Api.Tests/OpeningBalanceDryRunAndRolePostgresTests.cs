using Microsoft.EntityFrameworkCore;
using Zayra.Api.Models;
using K = Zayra.Api.Tests.OpeningBalanceImportKit;
using Kit = Zayra.Api.Tests.DebtRacePostgresKit;

namespace Zayra.Api.Tests;

/// <summary>
/// The dry run (preview) refuses a loan or advance row for exactly the reasons the commit would, so a consultant sees every
/// refusal before committing; and only Finance or an Admin may import rows that post an opening journal (an HR Manager on their
/// own is refused per row, in both languages, while the package's other sections import as before).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class OpeningBalanceDryRunAndRolePostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ThePreview_ReportsACollectedLoan_ANativeAdvance_AndAClosedPeriod_AsTheCommitWould()
    {
        var (b, cutover, number) = await K.SeedAsync(fixture, "DR-LN");
        await K.CommitAsync(fixture, b, "loans", K.LoanCsv(b, number, paid: 3, outstanding: 300m, cutover), withCutover: true);
        var runId = await Kit.SeedDraftRunAsync(fixture, b, b.Today.Year, b.Today.Month);
        await using (var db = fixture.CreateDb())
            Assert.Equal(200, (await Kit.ProcessAsync(db, b, runId, null, CancellationToken.None)).StatusCode);

        var collected = await K.CommitAsync(fixture, b, "loans", K.LoanCsv(b, number, paid: 1, outstanding: 500m, cutover), expectErrors: true, dryRun: true);
        Assert.Contains("loan_collected_by_payroll", Assert.Single(collected), StringComparison.Ordinal);

        var nativeAdvance = $"NAT-{Guid.NewGuid():N}"[..16];
        await using (var db = fixture.CreateDb())
        {
            db.SalaryAdvances.Add(new SalaryAdvance
            {
                TenantId = b.Tenant, CompanyId = b.CompanyId, EmployeeId = b.EmployeePublicId, EmployeeIntId = b.EmployeeId,
                EmployeeName = "Debt Race Borrower", AdvanceNumber = nativeAdvance, RequestedAmount = 600m, ApprovedAmount = 600m,
                RepaymentType = "Installments", Installments = 6, InstallmentAmount = 100m, OutstandingBalance = 600m, Status = "Active",
            });
            await db.SaveChangesAsync();
        }
        var native = await K.CommitAsync(fixture, b, "advances", K.AdvanceCsv(b, nativeAdvance, paid: 3, outstanding: 300m, cutover), expectErrors: true, dryRun: true);
        Assert.Contains("advance_not_carried_in", Assert.Single(native), StringComparison.Ordinal);

        var fresh = $"DR-NEW-{Guid.NewGuid():N}"[..18];
        await K.ClosePeriodAsync(fixture, b, cutover);
        var closed = await K.CommitAsync(fixture, b, "advances", K.AdvanceCsv(b, fresh, paid: 3, outstanding: 300m, cutover), expectErrors: true, dryRun: true);
        Assert.Contains("opening_journal_period_closed", Assert.Single(closed), StringComparison.Ordinal);
        await using var verify = fixture.CreateDb();
        Assert.False(await verify.SalaryAdvances.AnyAsync(a => a.TenantId == b.Tenant && a.AdvanceNumber == fresh), "a preview writes nothing");
    }
}
