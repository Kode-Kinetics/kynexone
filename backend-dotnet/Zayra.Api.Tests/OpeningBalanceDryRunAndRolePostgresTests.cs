using Microsoft.AspNetCore.Mvc;
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

    [Fact]
    public async Task AnHrManagerAlone_IsRefusedLoanAndAdvanceRows_InBothLanguages_TheOtherSectionsImport()
    {
        var (b, cutover, number) = await K.SeedAsync(fixture, "RL-LN");

        var errors = await K.CommitAsync(fixture, b, "loans", K.LoanCsv(b, number, paid: 3, outstanding: 300m, cutover),
            withCutover: true, expectErrors: true, roles: new[] { "HR Manager" });

        var error = Assert.Single(errors);
        Assert.Contains("opening_balance_needs_finance", error, StringComparison.Ordinal);
        Assert.Contains("المالية", error, StringComparison.Ordinal);
        await using (var db = fixture.CreateDb())
        {
            Assert.False(await db.EmployeeLoans.AnyAsync(l => l.TenantId == b.Tenant && l.LoanNumber == number));
            Assert.True(await db.CompanyCutovers.AnyAsync(c => c.TenantId == b.Tenant && c.CompanyId == b.CompanyId), "the cutover section imports as before");
        }
        var advance = await K.CommitAsync(fixture, b, "advances", K.AdvanceCsv(b, $"RL-ADV-{Guid.NewGuid():N}"[..18], paid: 3, outstanding: 300m, cutover),
            expectErrors: true, roles: new[] { "HR Manager" });
        Assert.Contains("opening_balance_needs_finance", Assert.Single(advance), StringComparison.Ordinal);

        // An HR Manager who is also a Finance approver may (the loan type exists; a finance importer does not create one).
        await K.EnsureLoanTypeAsync(fixture, b);
        await K.CommitAsync(fixture, b, "loans", K.LoanCsv(b, number, paid: 3, outstanding: 300m, cutover), roles: new[] { "HR Manager", "Finance Approver" });
        Assert.Single(await K.OpeningJournalAsync(fixture, b, (await K.LoanAsync(fixture, b, number)).Id));
    }

    /// <summary>Finance imports the opening-balance debt sections (loans, advances) and nothing else: a Finance user's package with
    /// any other section is refused before anything is read, in both languages, and the endpoints that are not package imports
    /// stay with HR and Admin.</summary>
    [Fact]
    public async Task FinanceImportsTheDebtSections_AndIsRefusedEveryOtherSection()
    {
        var (b, cutover, number) = await K.SeedAsync(fixture, "FN-LN");
        await K.CommitAsync(fixture, b, "companyCutover", await K.CutoverCsvAsync(fixture, b));   // HR's part (Admin here)
        await K.EnsureLoanTypeAsync(fixture, b);

        await K.CommitAsync(fixture, b, "loans", K.LoanCsv(b, number, paid: 3, outstanding: 300m, cutover), roles: new[] { "Finance" });
        Assert.Single(await K.OpeningJournalAsync(fixture, b, (await K.LoanAsync(fixture, b, number)).Id));
        await K.CommitAsync(fixture, b, "advances", K.AdvanceCsv(b, $"FN-ADV-{Guid.NewGuid():N}"[..18], paid: 3, outstanding: 300m, cutover),
            dryRun: true, roles: new[] { "Finance Controller" });

        await using var db = fixture.CreateDb();
        var controller = K.Migration(db, b.Tenant, "Finance Approver");
        var sections = new Dictionary<string, string>
        {
            ["companyCutover"] = await K.CutoverCsvAsync(fixture, b),
            ["loans"] = K.LoanCsv(b, $"FN-2-{Guid.NewGuid():N}"[..18], paid: 3, outstanding: 300m, cutover),
        };
        var refused = Assert.IsType<ObjectResult>((await controller.Commit(new Zayra.Api.Models.MigrationPackageRequest($"pkg-{Guid.NewGuid():N}", sections, false), default)).Result);
        Assert.Equal(403, refused.StatusCode);
        var body = System.Text.Json.JsonSerializer.Serialize(refused.Value);
        Assert.Contains("import_section_not_permitted", body, StringComparison.Ordinal);
        Assert.Contains("companyCutover", body, StringComparison.Ordinal);
        Assert.Contains("messageAr", body, StringComparison.Ordinal);
        Assert.IsType<ForbidResult>(K.Migration(db, b.Tenant, "Finance").Template());
        Assert.IsType<ForbidResult>((await K.Migration(db, b.Tenant, "Finance").Get(Guid.NewGuid(), default)).Result);
    }
}
