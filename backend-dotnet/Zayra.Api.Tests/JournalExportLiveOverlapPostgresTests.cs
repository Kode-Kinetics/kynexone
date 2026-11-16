using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// C-1 on REAL Postgres: two exports of OVERLAPPING scopes (a payroll run and its whole month) must never both go live.
///
/// <para>The live-scope unique index only covers one exact scope, so it cannot stop this. What serialises them is the lock the
/// create already takes: every ledger row its scope could select, FOR UPDATE in id order, before the build. Two overlapping
/// scopes share rows, so the second create queues behind the first and, once that commits, its build sees the first file's
/// lines and is refused (409 entries_in_live_export).</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class JournalExportLiveOverlapPostgresTests
{
    private readonly PostgresFixture _fx;
    public JournalExportLiveOverlapPostgresTests(PostgresFixture fx) => _fx = fx;

    private const int Rounds = 5;

    [Fact]
    public async Task AnOverlappingExportOfAnotherScope_QueuesBehindTheFirstsRowLocks_ThenIsRefused()
    {
        var tenantId = await SeedTenantAsync();
        const string period = "2023-04";
        var run = await SeedLedgerAsync(tenantId, period);
        var maker = Principal(tenantId, "finance.gl.manage");
        var pause = new PauseBeforeExportInsert();

        // The run export is held just before its INSERT: its ledger rows are locked, nothing is committed.
        var first = Task.Run(async () =>
        {
            await using var db = DbWith(pause);
            await db.Database.OpenConnectionAsync();
            return await Controller(db, maker).Create(period, run, null, "SAR", false, false, "generic-csv", default);
        });
        await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(60));

        var second = Task.Run(async () =>
        {
            await using var db = _fx.CreateDb();
            await db.Database.OpenConnectionAsync();
            return await Controller(db, maker).Create(period, null, null, "SAR", false, false, "generic-csv", default);
        });
        (await ABackendWaitsOnALedgerLockAsync(TimeSpan.FromSeconds(30)))
            .Should().BeTrue("the month export must queue behind the run export's ledger row locks");
        second.IsCompleted.Should().BeFalse();

        pause.Release.SetResult();
        var a = await first;
        var b = await second;

        StatusOf(a).Should().Be(201);
        StatusOf(b).Should().Be(409, "once the run export committed, the month export's rows are in a waiting file");
        Body(b).GetProperty("error").GetString().Should().Be("entries_in_live_export");
        Body(b).GetProperty("exportId").GetGuid().Should().Be(IdOf(a));
        Body(b).GetProperty("rows").GetInt32().Should().Be(2);
        await using var check = _fx.CreateDb();
        (await check.GlJournalExports.AsNoTracking().CountAsync(x => x.TenantId == tenantId && x.Status == GlJournalExportStatuses.Exported))
            .Should().Be(1);
    }

    [Fact]
    public async Task ARunExportAndAMonthExport_Racing_NeverBothGoLive()
    {
        var tenantId = await SeedTenantAsync();
        for (var round = 0; round < Rounds; round++)
        {
            var period = $"2023-{round + 5:D2}";
            var run = await SeedLedgerAsync(tenantId, period);
            var maker = Principal(tenantId, "finance.gl.manage");

            var results = await RaceAsync(
                () => CreateAsync(tenantId, period, run, maker),
                () => CreateAsync(tenantId, period, null, maker));

            results.Should().NotContainNulls($"round {round}: neither contender may surface a 500");
            results.Count(r => StatusOf(r!) == 201).Should().Be(1, $"round {round}: exactly one file may hold the run's rows");
            var loser = results.Single(r => StatusOf(r!) != 201)!;
            StatusOf(loser).Should().Be(409, $"round {round}");
            Body(loser).GetProperty("error").GetString().Should().Be("entries_in_live_export", $"round {round}");

            await using var db = _fx.CreateDb();
            var live = await db.GlJournalExports.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.Period == period && x.Status == GlJournalExportStatuses.Exported)
                .Select(x => x.Id).ToListAsync();
            live.Should().ContainSingle($"round {round}: never two waiting files over the same ledger rows");
            var runRows = await db.FinanceGlEntries.AsNoTracking()
                .Where(e => e.TenantId == tenantId && e.SourceEntityId == run).Select(e => e.Id).ToListAsync();
            (await db.GlJournalExportLines.AsNoTracking()
                    .Where(l => l.TenantId == tenantId && runRows.Contains(l.FinanceGlEntryId)).Select(l => l.GlJournalExportId).Distinct().ToListAsync())
                .Should().Equal(live, $"round {round}: the run's rows are in the one live file only");
        }
    }

    // ── Harness ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Holds the first INSERT into gl_journal_exports until released, so the export's row locks are held open.</summary>
    private sealed class PauseBeforeExportInsert : DbCommandInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _fired;

        private async Task MaybePauseAsync(DbCommand command)
        {
            if (!command.CommandText.Contains("INSERT INTO gl_journal_exports ", StringComparison.OrdinalIgnoreCase)
                && !command.CommandText.Contains("INSERT INTO \"gl_journal_exports\"", StringComparison.OrdinalIgnoreCase)) return;
            if (Interlocked.Exchange(ref _fired, 1) == 1) return;
            Reached.SetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(90));
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await MaybePauseAsync(command);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await MaybePauseAsync(command);
            return result;
        }
    }

    private ZayraDbContext DbWith(DbCommandInterceptor extra) => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseNpgsql(_fx.ConnectionString, PostgresFixture.ProductionProviderOptions)
        .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance,
            Zayra.Api.Infrastructure.Data.AdvisoryXactLockGuardInterceptor.Instance, extra)
        .Options);

    /// <summary>True once some backend is waiting on a row lock while reading the ledger.</summary>
    private async Task<bool> ABackendWaitsOnALedgerLockAsync(TimeSpan timeout)
    {
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' "
                + "AND query ILIKE '%finance_gl_entries%'", conn);
            if ((long)(await cmd.ExecuteScalarAsync())! > 0) return true;
            await Task.Delay(100);
        }
        return false;
    }

    private async Task<IActionResult> CreateAsync(Guid tenantId, string period, Guid? runId, ClaimsPrincipal caller)
    {
        await using var db = _fx.CreateDb();
        await db.Database.OpenConnectionAsync();
        return await Controller(db, caller).Create(period, runId, null, "SAR", false, false, "generic-csv", default);
    }

    private static GlJournalExportsController Controller(ZayraDbContext db, ClaimsPrincipal caller)
    {
        var exports = new JournalExportService(db, JournalExportFormatterGoldenTests.AllFormatters());
        return new GlJournalExportsController(db, exports, new PeriodHandoffReconciler(db, exports))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = caller } },
        };
    }

    private async Task<Guid> SeedTenantAsync()
    {
        await using var db = _fx.CreateDb();
        return await PostgresFixture.SeedMinimalTenant(db);
    }

    /// <summary>A payroll run's accrual (two one-sided rows) and a loan disbursement in the month. Returns the run id.</summary>
    private async Task<Guid> SeedLedgerAsync(Guid tenantId, string period)
    {
        await using var db = _fx.CreateDb();
        var run = Guid.NewGuid();
        db.FinanceGlEntries.AddRange(
            Row(tenantId, period, "Payroll", "5000 - Salaries", "", 900m, run),
            Row(tenantId, period, "Payroll", "", "2100 - Payable", 900m, run),
            Row(tenantId, period, "Loan", "1400 - Loans", "1000 - Bank", 250m));
        await db.SaveChangesAsync();
        return run;
    }

    private static FinanceGlEntry Row(Guid tenantId, string period, string module, string dr, string cr, decimal amount, Guid? source = null) => new()
    {
        TenantId = tenantId, SourceModule = module, SourceEntityId = source ?? Guid.NewGuid(), SourceEntityRef = module,
        EventType = module == "Loan" ? "Disbursement" : "Accrual", DebitAccount = dr, CreditAccount = cr, Amount = amount,
        Currency = "SAR", EntryDate = DateOnly.ParseExact(period + "-15", "yyyy-MM-dd"), Period = period, Description = module,
    };

    private static ClaimsPrincipal Principal(Guid tenantId, params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "GL Racer"),
            new("is_group_scope", "true"),
        };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static async Task<IActionResult?[]> RaceAsync(Func<Task<IActionResult>> first, Func<Task<IActionResult>> second)
    {
        var ready = new SemaphoreSlim(0, 2);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IActionResult?> Run(Func<Task<IActionResult>> contender)
        {
            await Task.Yield();
            ready.Release();
            await go.Task;
            try { return await contender(); }
            catch { return null; }
        }
        var a = Task.Run(() => Run(first));
        var b = Task.Run(() => Run(second));
        await ready.WaitAsync();
        await ready.WaitAsync();
        go.SetResult();
        return await Task.WhenAll(a, b);
    }

    private static int StatusOf(IActionResult r) => r switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        StatusCodeResult s => s.StatusCode,
        ForbidResult => 403,
        _ => throw new InvalidOperationException(r.GetType().Name),
    };

    private static JsonElement Body(IActionResult r) => JsonSerializer.SerializeToElement(((ObjectResult)r).Value);
    private static Guid IdOf(IActionResult r) => Body(r).GetProperty("exportId").GetGuid();
}
