using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;
using Kit = Zayra.Api.Tests.DebtRacePostgresKit;

namespace Zayra.Api.Tests;

/// <summary>
/// The opening-balance import carries a salary advance in at its outstanding balance. It must put that balance on the
/// ledger (DR 1410 Employee Salary Advances / CR 3900 Opening Balance Equity, once per carried-in advance, keyed on its origin
/// row), build the advance's instalment schedule as loans get theirs, and only ever restate an advance it carried in itself
/// and payroll has not yet collected from — never a native advance, never one payroll has taken an instalment from, and never
/// one that is no longer Active.
///
/// <para>Real Postgres: the opening journal is a ledger writer, driven here against the append-only triggers.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class OpeningBalanceAdvanceImportPostgresTests(PostgresFixture fixture)
{
    /// <summary>The event type of the opening journal (GlEventTypes.OpeningBalance).</summary>
    private const string OpeningBalanceEvent = "OpeningBalance";

    [Fact]
    public async Task CarriedInAdvance_PostsItsOpeningJournalOnce_BuildsItsSchedule_AndARestatementPostsTheDifference()
    {
        var (b, cutover, number) = await SeedAsync();
        await CommitAsync(b, AdvanceCsv(b, number, paid: 3, outstanding: 300m, cutover), withCutover: true);
        var advance = await AdvanceAsync(b, number);
        Assert.Equal((300m, 300m, "Active"), (advance.OutstandingBalance, advance.TotalRepaid, advance.Status));
        Assert.Equal(300m, await Gl1410Async(b, advance.Id));
        Assert.Equal(-300m, await Gl3900Async(b));
        var schedule = await ScheduleAsync(advance.Id);
        Assert.Equal(6, schedule.Count);
        Assert.Equal(3, schedule.Count(i => i.Status == "Paid" && i.PayrollRunId == null));
        Assert.Equal(300m, schedule.Where(i => i.Status == "Pending").Sum(i => i.AmountDue - i.AmountPaid));
        Assert.Equal(cutover, schedule.Where(i => i.Status == "Pending").Min(i => i.DueDate));

        // The same figures again (a resumed or repeated package): nothing posts twice.
        await CommitAsync(b, AdvanceCsv(b, number, paid: 3, outstanding: 300m, cutover));
        Assert.Equal(300m, await Gl1410Async(b, advance.Id));
        Assert.Single(await OpeningJournalAsync(b, advance.Id));

        // A restatement: the ledger follows the advance by the difference, and the schedule is rebuilt.
        await CommitAsync(b, AdvanceCsv(b, number, paid: 1, outstanding: 500m, cutover));
        advance = await AdvanceAsync(b, number);
        Assert.Equal((500m, 100m), (advance.OutstandingBalance, advance.TotalRepaid));
        Assert.Equal(500m, await Gl1410Async(b, advance.Id));
        Assert.Equal(-500m, await Gl3900Async(b));
        schedule = await ScheduleAsync(advance.Id);
        Assert.Equal(500m, schedule.Where(i => i.Status == "Pending").Sum(i => i.AmountDue - i.AmountPaid));
        Assert.Equal(1, schedule.Count(i => i.Status == "Paid"));
    }

    /// <summary>
    /// The opening journal is what makes the carried-in advance a receivable on this company's ledger: the 1410 tie-out (sub-ledger
    /// against GL) holds straight after the import, and still holds once payroll has collected an instalment and the run's Lock
    /// journal has credited 1410 with it.
    /// </summary>
    [Fact]
    public async Task CarriedInAdvance_TiesOutWithTheLedger_AfterTheImport_AndAfterOnePayrollDeduction()
    {
        var (b, cutover, number) = await SeedAsync();
        await CommitAsync(b, AdvanceCsv(b, number, paid: 3, outstanding: 300m, cutover), withCutover: true);
        Assert.Equal((300m, 300m, true), await TieOutAsync(b));

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

        var advance = await AdvanceAsync(b, number);
        Assert.Equal(200m, advance.OutstandingBalance);
        Assert.Equal((200m, 200m, true), await TieOutAsync(b));
    }

    /// <summary>
    /// The journal export has no rule for opening-balance entries: it sends every ledger row of the period, as it already does
    /// for the provision catch-up booked to 3900. So the advance opening journal is selected like any other row, and carries
    /// EventType "OpeningBalance" so the ERP side can tell it apart. Pinned as it stands, not as a decision: a client whose ERP
    /// already holds the receivable from the old system would book it twice if it imported this line. Whether opening entries
    /// should be excluded or flagged in the export is an open product decision.
    /// </summary>
    [Fact]
    public async Task CarriedInAdvance_OpeningJournal_IsInThePeriodsJournalExport_TaggedOpeningBalance()
    {
        var (b, cutover, number) = await SeedAsync();
        await CommitAsync(b, AdvanceCsv(b, number, paid: 3, outstanding: 300m, cutover), withCutover: true);
        var advance = await AdvanceAsync(b, number);

        await using var db = fixture.CreateDb();
        var selection = await Zayra.Api.Infrastructure.Finance.JournalExportBuilder.SelectAsync(db, b.Tenant,
            new Zayra.Api.Infrastructure.Finance.JournalExportFilter(Period: $"{cutover:yyyy-MM}", CompanyId: b.CompanyId));
        var opening = Assert.Single(selection.Entries, e => e.SourceEntityId == advance.Id);
        Assert.Equal((OpeningBalanceEvent, 300m, cutover), (opening.EventType, opening.Amount, opening.EntryDate));
        Assert.StartsWith("1410", opening.DebitAccount, StringComparison.Ordinal);
        Assert.StartsWith("3900", opening.CreditAccount, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAdvanceTheImportDidNotCarryIn_IsRefused_AndLeftAlone()
    {
        var (b, cutover, number) = await SeedAsync();
        Guid nativeId;
        await using (var db = fixture.CreateDb())
        {
            var native = new SalaryAdvance
            {
                TenantId = b.Tenant, CompanyId = b.CompanyId, EmployeeId = b.EmployeePublicId, EmployeeIntId = b.EmployeeId,
                EmployeeName = "Debt Race Borrower", AdvanceNumber = number, RequestedAmount = 600m, ApprovedAmount = 600m,
                RepaymentType = "Installments", Installments = 6, InstallmentAmount = 100m, OutstandingBalance = 600m, Status = "Active",
            };
            db.SalaryAdvances.Add(native);
            await db.SaveChangesAsync();
            nativeId = native.Id;
        }

        var errors = await CommitAsync(b, AdvanceCsv(b, number, paid: 3, outstanding: 300m, cutover), withCutover: true, expectErrors: true);

        Assert.Contains(errors, e => e.Contains("advance_not_carried_in", StringComparison.Ordinal));
        await using var verify = fixture.CreateDb();
        var untouched = await verify.SalaryAdvances.AsNoTracking().SingleAsync(a => a.Id == nativeId);
        Assert.Equal((600m, 0m, "Active"), (untouched.OutstandingBalance, untouched.TotalRepaid, untouched.Status));
        Assert.Equal(0m, await Gl1410Async(b, nativeId));
    }

    [Fact]
    public async Task ACarriedInAdvancePayrollHasCollectedFrom_IsNotRestated()
    {
        var (b, cutover, number) = await SeedAsync();
        await CommitAsync(b, AdvanceCsv(b, number, paid: 3, outstanding: 300m, cutover), withCutover: true);
        var runId = await Kit.SeedDraftRunAsync(fixture, b, b.Today.Year, b.Today.Month);
        await using (var db = fixture.CreateDb())
            Assert.Equal(200, (await Kit.ProcessAsync(db, b, runId, null, CancellationToken.None)).StatusCode);

        var errors = await CommitAsync(b, AdvanceCsv(b, number, paid: 1, outstanding: 500m, cutover), expectErrors: true);

        Assert.Contains(errors, e => e.Contains("advance_collected_by_payroll", StringComparison.Ordinal));
        var advance = await AdvanceAsync(b, number);
        Assert.Equal((200m, 400m), (advance.OutstandingBalance, advance.TotalRepaid));
    }

    [Fact]
    public async Task ACarriedInAdvanceNoLongerActive_IsNeverReopened()
    {
        var (b, cutover, number) = await SeedAsync();
        await CommitAsync(b, AdvanceCsv(b, number, paid: 3, outstanding: 300m, cutover), withCutover: true);
        await using (var db = fixture.CreateDb())
            await db.SalaryAdvances.Where(a => a.TenantId == b.Tenant && a.AdvanceNumber == number)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, "Closed").SetProperty(a => a.OutstandingBalance, 0m));

        var errors = await CommitAsync(b, AdvanceCsv(b, number, paid: 3, outstanding: 300m, cutover), expectErrors: true);

        Assert.Contains(errors, e => e.Contains("advance_not_active", StringComparison.Ordinal));
        var advance = await AdvanceAsync(b, number);
        Assert.Equal((0m, "Closed"), (advance.OutstandingBalance, advance.Status));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private async Task<(Kit.Borrower B, DateOnly Cutover, string Number)> SeedAsync()
    {
        var b = await Kit.SeedBorrowerAsync(fixture, 100m, 2);
        return (b, new DateOnly(b.Today.Year, b.Today.Month, 1), $"LEG-ADV-{Guid.NewGuid():N}"[..20]);
    }

    private async Task<IReadOnlyList<string>> CommitAsync(Kit.Borrower b, string advancesCsv, bool withCutover = false, bool expectErrors = false)
    {
        var sections = new Dictionary<string, string> { ["advances"] = advancesCsv };
        if (withCutover)
        {
            await using var read = fixture.CreateDb();
            var registration = (await read.Companies.AsNoTracking().SingleAsync(c => c.Id == b.CompanyId)).RegistrationNumber;
            var cutover = new DateOnly(b.Today.Year, b.Today.Month, 1);
            sections["companyCutover"] = $"CompanyRegistrationNumber,CompanyLegalName,CutoverDate,SourceSystem,Status,Notes\n{registration},,{cutover:yyyy-MM-dd},SAP,Active,Wave 1\n";
        }
        await using var db = fixture.CreateDb();
        var result = await Migration(db, b.Tenant).Commit(new MigrationPackageRequest($"pkg-{Guid.NewGuid():N}", sections, false), default);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<MigrationReconciliationDto>(ok.Value);
        if (!expectErrors) Assert.True(dto.Errors.Count == 0, string.Join(" | ", dto.Errors));
        return dto.Errors.ToList();
    }

    /// <summary>The company's 1410 tie-out row: (sub-ledger, GL, tied out).</summary>
    private async Task<(decimal SubLedger, decimal Gl, bool Tied)> TieOutAsync(Kit.Borrower b)
    {
        await using var db = fixture.CreateDb();
        var row = (await Zayra.Api.Infrastructure.Finance.AdvanceReceivableTieOut.ComputeAsync(db, b.Tenant, new[] { b.CompanyId }, CancellationToken.None)).Single();
        return (row.SubLedgerOutstanding, row.GlBalance, row.IsTiedOut);
    }

    private static string Describe(IActionResult result) =>
        result is ObjectResult o ? $"{o.StatusCode}: {System.Text.Json.JsonSerializer.Serialize(o.Value)}" : result.GetType().Name;

    private async Task<SalaryAdvance> AdvanceAsync(Kit.Borrower b, string number)
    {
        await using var db = fixture.CreateDb();
        return await db.SalaryAdvances.AsNoTracking().SingleAsync(a => a.TenantId == b.Tenant && a.AdvanceNumber == number);
    }

    private async Task<List<AdvanceInstallment>> ScheduleAsync(Guid advanceId)
    {
        await using var db = fixture.CreateDb();
        return await db.AdvanceInstallments.AsNoTracking().Where(i => i.AdvanceId == advanceId).OrderBy(i => i.InstallmentNumber).ToListAsync();
    }

    private async Task<List<FinanceGlEntry>> OpeningJournalAsync(Kit.Borrower b, Guid advanceId)
    {
        await using var db = fixture.CreateDb();
        return await db.FinanceGlEntries.AsNoTracking()
            .Where(g => g.TenantId == b.Tenant && g.SourceEntityId == advanceId && g.EventType == OpeningBalanceEvent && !g.IsReversed)
            .ToListAsync();
    }

    /// <summary>1410 as this advance's own journal lines carry it (debits less credits).</summary>
    private async Task<decimal> Gl1410Async(Kit.Borrower b, Guid advanceId)
    {
        await using var db = fixture.CreateDb();
        var lines = await db.FinanceGlEntries.AsNoTracking()
            .Where(g => g.TenantId == b.Tenant && g.SourceEntityId == advanceId)
            .Select(g => new { g.DebitAccount, g.CreditAccount, g.Amount }).ToListAsync();
        return lines.Sum(l => (l.DebitAccount.StartsWith("1410", StringComparison.Ordinal) ? l.Amount : 0m)
                            - (l.CreditAccount.StartsWith("1410", StringComparison.Ordinal) ? l.Amount : 0m));
    }

    /// <summary>3900 Opening Balance Equity across the tenant (debits less credits; a credit balance is negative).</summary>
    private async Task<decimal> Gl3900Async(Kit.Borrower b)
    {
        await using var db = fixture.CreateDb();
        var lines = await db.FinanceGlEntries.AsNoTracking()
            .Where(g => g.TenantId == b.Tenant && (g.DebitAccount.StartsWith("3900") || g.CreditAccount.StartsWith("3900")))
            .Select(g => new { g.DebitAccount, g.Amount }).ToListAsync();
        return lines.Sum(l => l.DebitAccount.StartsWith("3900", StringComparison.Ordinal) ? l.Amount : -l.Amount);
    }

    private static string AdvanceCsv(Kit.Borrower b, string number, int paid, decimal outstanding, DateOnly firstUnpaid) =>
        "EmployeeCode,AdvanceNumber,OriginalAmount,InstallmentAmount,TotalInstallments,InstallmentsPaid,OutstandingBalance,FirstUnpaidDueDate,Currency,SourceSystem,SourceRecordId\n" +
        $"{b.EmployeeCode},{number},600,100,6,{paid},{outstanding},{firstUnpaid:yyyy-MM-dd},SAR,SAP,ADV-1\n";

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
