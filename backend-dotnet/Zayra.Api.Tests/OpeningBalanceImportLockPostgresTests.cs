using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;
using Kit = Zayra.Api.Tests.DebtRacePostgresKit;

namespace Zayra.Api.Tests;

/// <summary>
/// The opening-balance import restates a carried-in salary advance (OutstandingBalance, TotalRepaid, Status). It must take the
/// advance's finance.advance lock, like every other writer of that balance, before it reads and writes it: a payroll pass that
/// is collecting an instalment from the same advance either waits for the restatement and collects from it, or (if it came
/// first) is seen by it. Without the lock the import's write overwrote the instalment payroll had just withheld.
///
/// <para>Real Postgres only; the import is held just before its UPDATE of the advance, and the payroll pass is observed waiting
/// in <c>pg_stat_activity</c>.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class OpeningBalanceImportLockPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ProcessArrivingWhileTheImportRestatesTheAdvance_WaitsAndCollectsFromTheRestatedBalance()
    {
        var b = await Kit.SeedBorrowerAsync(fixture, 100m, 2);
        var cutover = new DateOnly(b.Today.Year, b.Today.Month, 1);
        var advanceNumber = $"LEG-ADV-{Guid.NewGuid():N}"[..20];
        string companyRegistration;
        await using (var db = fixture.CreateDb())
            companyRegistration = (await db.Companies.AsNoTracking().SingleAsync(c => c.Id == b.CompanyId)).RegistrationNumber!;

        // Carried in at 300 owed (3 of 6 instalments of 100 already repaid in the previous system).
        await using (var db = fixture.CreateDb())
            AssertCommitted(await Migration(db, b.Tenant).Commit(Package($"cutover-{Guid.NewGuid():N}",
                ("companyCutover", $"CompanyRegistrationNumber,CompanyLegalName,CutoverDate,SourceSystem,Status,Notes\n{companyRegistration},,{cutover:yyyy-MM-dd},SAP,Active,Wave 1\n"),
                ("advances", AdvanceCsv(b, advanceNumber, paid: 3, outstanding: 300m, cutover))), default));
        Guid advanceId;
        await using (var db = fixture.CreateDb())
            advanceId = (await db.SalaryAdvances.AsNoTracking().SingleAsync(a => a.TenantId == b.Tenant && a.AdvanceNumber == advanceNumber)).Id;
        var runId = await Kit.SeedDraftRunAsync(fixture, b, b.Today.Year, b.Today.Month);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var gate = new HoldBeforeAdvanceUpdate();
        await using var importDb = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.EnableRetryOnFailure(5, TimeSpan.FromSeconds(5), null))
            .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance,
                Zayra.Api.Infrastructure.Data.AdvisoryXactLockGuardInterceptor.Instance, gate)
            .Options);
        await using var processDb = fixture.CreateDb();
        var processPid = await Kit.OpenAndPidAsync(processDb, timeout.Token);

        // The restatement: the previous system was wrong, 500 is owed (only 1 of 6 repaid).
        var import = Task.Run(() => Migration(importDb, b.Tenant).Commit(Package($"restate-{Guid.NewGuid():N}",
            ("advances", AdvanceCsv(b, advanceNumber, paid: 1, outstanding: 500m, cutover))), timeout.Token));
        Assert.True(await gate.ReachedAsync(), "the import must reach its write of the advance");
        var process = Task.Run(() => Kit.ProcessAsync(processDb, b, runId, null, timeout.Token));
        var waited = await Kit.WaitForLockAsync(fixture, processPid, advisoryOnly: true, process, timeout.Token);
        gate.Release();
        AssertCommitted(await import);
        var outcome = await process;

        Assert.True(waited, "the payroll pass must queue behind the advance lock the import holds");
        Assert.Equal(200, outcome.StatusCode);
        await using var verify = fixture.CreateDb();
        var advance = await verify.SalaryAdvances.AsNoTracking().SingleAsync(a => a.Id == advanceId);
        var withheld = await verify.PayrollDeductions.AsNoTracking()
            .Where(d => d.PayrollRunId == runId && d.ComponentCode == "ADVANCE_EMI").SumAsync(d => d.Amount);
        Assert.Equal(100m, withheld);
        // The restated 500, less the instalment payroll withheld from it.
        Assert.Equal((400m, 200m, "Active"), (advance.OutstandingBalance, advance.TotalRepaid, advance.Status));
    }

    private static string AdvanceCsv(Kit.Borrower b, string number, int paid, decimal outstanding, DateOnly firstUnpaid) =>
        "EmployeeCode,AdvanceNumber,OriginalAmount,InstallmentAmount,TotalInstallments,InstallmentsPaid,OutstandingBalance,FirstUnpaidDueDate,Currency,SourceSystem,SourceRecordId\n" +
        $"{b.EmployeeCode},{number},600,100,6,{paid},{outstanding},{firstUnpaid:yyyy-MM-dd},SAR,SAP,ADV-1\n";

    private static MigrationPackageRequest Package(string externalId, params (string Section, string Csv)[] sections) =>
        new(externalId, sections.ToDictionary(s => s.Section, s => s.Csv), false);

    private static void AssertCommitted(ActionResult<MigrationReconciliationDto> result)
    {
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<MigrationReconciliationDto>(ok.Value);
        Assert.True(dto.Errors.Count == 0, string.Join(" | ", dto.Errors));
    }

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

    /// <summary>Holds the first command that updates salary_advances until released (bounded, so a broken choreography fails
    /// the assertions instead of hanging the suite).</summary>
    private sealed class HoldBeforeAdvanceUpdate : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _held;

        public async Task<bool> ReachedAsync() => await Task.WhenAny(_reached.Task, Task.Delay(TimeSpan.FromSeconds(30))) == _reached.Task;
        public void Release() => _release.TrySetResult();

        private async Task HoldAsync(DbCommand command)
        {
            if (!command.CommandText.Contains("UPDATE salary_advances", StringComparison.OrdinalIgnoreCase)) return;
            if (Interlocked.Exchange(ref _held, 1) != 0) return;
            _reached.TrySetResult();
            await Task.WhenAny(_release.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await HoldAsync(command);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await HoldAsync(command);
            return result;
        }
    }
}
