using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The live-export guard and the double-post guard on REAL Postgres, where the partial unique index
/// <c>ux_gl_journal_exports_live_scope</c> (NULLS NOT DISTINCT, raw SQL) exists and the JSON columns, the
/// confirmed-entry sub-query and EF's write ordering are exercised for real.
///
/// <para>THE DEFECT. Two concurrent POST journal-exports for one scope both read "no live export", both
/// inserted, and both stayed <c>Exported</c> — two files of the same month in circulation, either of which
/// could be confirmed. Now the second insert hits the index and the endpoint answers 409.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class JournalExportConcurrencyPostgresTests
{
    private readonly PostgresFixture _fx;
    public JournalExportConcurrencyPostgresTests(PostgresFixture fx) => _fx = fx;

    private const int Rounds = 5;

    [Fact]
    public async Task TwoSimultaneousExports_OfOneScope_LeaveExactlyOneLive_AndTheLoserGets409()
    {
        var tenantId = await SeedTenantAsync();
        for (var round = 0; round < Rounds; round++)
        {
            var period = $"2025-{round + 1:D2}";
            await SeedLedgerAsync(tenantId, period);
            var maker = Principal(tenantId, "finance.gl.manage");

            var results = await RaceAsync(() => CreateAsync(tenantId, period, maker), () => CreateAsync(tenantId, period, maker));

            results.Should().NotContainNulls($"round {round}: neither contender may surface a 500");
            results.Should().OnlyContain(r => StatusOf(r!) == 201 || StatusOf(r!) == 409, $"round {round}");
            foreach (var loser in results.Where(r => StatusOf(r!) == 409))
                Body(loser!).GetProperty("error").GetString().Should().Be(JournalExportErrors.ConcurrentExport);

            await using var db = _fx.CreateDb();
            (await db.GlJournalExports.AsNoTracking()
                    .CountAsync(x => x.TenantId == tenantId && x.Period == period && x.Status == GlJournalExportStatuses.Exported))
                .Should().Be(1, $"round {round}: one scope, one live export — never two files of the same month in circulation");
        }
    }

    [Fact]
    public async Task TheLiveScopeIndex_IsUniquePartialAndNullsNotDistinct()
    {
        await using var db = _fx.CreateDb();
        await db.Database.OpenConnectionAsync();
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = "SELECT indexdef FROM pg_indexes WHERE indexname = '" + GlJournalExportsController.LiveScopeIndexName + "'";
        var def = (string?)await cmd.ExecuteScalarAsync();
        def.Should().NotBeNull("the fixture applies the migration's own DDL");
        def.Should().Contain("UNIQUE").And.Contain("NULLS NOT DISTINCT").And.Contain("WHERE").And.Contain("Exported");
    }

    [Fact]
    public async Task ASequentialReExport_SupersedesThePrior_WithoutTrippingTheLiveIndex()
    {
        var tenantId = await SeedTenantAsync();
        await SeedLedgerAsync(tenantId, "2024-03");
        var maker = Principal(tenantId, "finance.gl.manage");

        var first = await CreateAsync(tenantId, "2024-03", maker);
        var second = await CreateAsync(tenantId, "2024-03", maker);

        StatusOf(first).Should().Be(201);
        StatusOf(second).Should().Be(201, "EF writes the prior's Superseded UPDATE before the new INSERT in one save");
        await using var db = _fx.CreateDb();
        var rows = await db.GlJournalExports.AsNoTracking().Where(x => x.TenantId == tenantId).ToListAsync();
        rows.Single(x => x.Id == IdOf(first)).Status.Should().Be(GlJournalExportStatuses.Superseded);
        rows.Single(x => x.Id == IdOf(second)).Status.Should().Be(GlJournalExportStatuses.Exported);
    }

    [Fact]
    public async Task ConfirmedJournals_AreNeverResent_OnPostgres()
    {
        var tenantId = await SeedTenantAsync();
        await SeedLedgerAsync(tenantId, "2024-07");
        var maker = Principal(tenantId, "finance.gl.manage");
        var checker = Principal(tenantId, "finance.gl.read", GlJournalExportsController.ErpConfirmPermission);

        var exportId = IdOf(await CreateAsync(tenantId, "2024-07", maker));
        List<string> journals;
        await using (var db = _fx.CreateDb())
            journals = Body(await Controller(db, checker).Get(exportId, default)).GetProperty("journals")
                .EnumerateArray().Select(j => j.GetProperty("journalRef").GetString()!).ToList();
        journals.Should().HaveCount(2);

        await using (var db = _fx.CreateDb())
            StatusOf(await Controller(db, checker).ConfirmJournals(exportId,
                new ErpJournalConfirmationRequest(new() { new(journals[0], "ERP-77") }), default)).Should().Be(200);
        // A partly recorded export is ERP evidence: a re-export waits until every journal has an outcome.
        StatusOf(await CreateAsync(tenantId, "2024-07", maker)).Should().Be(409);
        await using (var db = _fx.CreateDb())
            StatusOf(await Controller(db, checker).RejectJournals(exportId,
                new ErpJournalRejectionRequest(new() { new(journals[1], "Refused by the ERP") }), default)).Should().Be(200);

        await using (var db = _fx.CreateDb())
        {
            db.FinanceGlEntries.Add(Row(tenantId, "2024-07", "Loan", "1400 - Loans", "1000 - Bank", 50m));
            await db.SaveChangesAsync();
        }

        var next = await CreateAsync(tenantId, "2024-07", maker);
        StatusOf(next).Should().Be(201);
        Body(next).GetProperty("kind").GetString().Should().Be("Supplementary");
        await using (var db = _fx.CreateDb())
        {
            var confirmedRefs = await db.GlJournalExportLines.AsNoTracking()
                .Where(l => l.TenantId == tenantId && l.GlJournalExportId == IdOf(next)).Select(l => l.JournalRef).Distinct().ToListAsync();
            confirmedRefs.Should().NotContain(journals[0], "the ERP holds that journal");
            (await db.GlJournalExports.AsNoTracking().SingleAsync(x => x.Id == exportId)).Status
                .Should().Be(GlJournalExportStatuses.PartiallyConfirmed, "its confirmed journal stays evidence; only the refused one is re-sent");
        }
    }

    // ── Harness ───────────────────────────────────────────────────────────────────────────────────

    private async Task<IActionResult> CreateAsync(Guid tenantId, string period, ClaimsPrincipal caller)
    {
        await using var db = _fx.CreateDb();
        await db.Database.OpenConnectionAsync();
        return await Controller(db, caller).Create(period, null, null, "SAR", false, false, "generic-csv", default);
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

    private async Task SeedLedgerAsync(Guid tenantId, string period)
    {
        await using var db = _fx.CreateDb();
        var run = Guid.NewGuid();
        db.FinanceGlEntries.AddRange(
            Row(tenantId, period, "Payroll", "5000 - Salaries", "", 900m, run),
            Row(tenantId, period, "Payroll", "", "2100 - Payable", 900m, run),
            Row(tenantId, period, "Loan", "1400 - Loans", "1000 - Bank", 250m));
        await db.SaveChangesAsync();
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
