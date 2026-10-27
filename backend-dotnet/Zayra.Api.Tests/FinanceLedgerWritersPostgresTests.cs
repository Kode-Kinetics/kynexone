using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// T-052, the behavioural half: every code path that UPDATEs a <c>finance_gl_entries</c> row today, run through the REAL
/// writer against REAL PostgreSQL with migration M2's triggers installed (the shared <see cref="PostgresFixture"/> applies
/// M2's own DDL), and for each of them the matching illegitimate write refused. The writers are:
/// <list type="bullet">
///  <item>the void / contra family flagging <c>IsReversed</c> (PayrollVoidService, PayrollController settle/remit/outside-payment
///   reversal via BuildContraGl, final-settlement cancel, LoansController.ReverseLoanJournalAsync, BonusesController cancel);</item>
///  <item>the <c>erp_*</c> stampers (ErpPostingEvidence.StampAsync / StampRejectionAsync, and the hand status endpoint).</item>
/// </list>
/// Where a writer is a private method of a controller whose set-up needs a payment batch or a loan product, its exact
/// shape is driven through the same <c>ZayraDbContext</c> and the same entity code it uses, and the real controllers are
/// covered by the existing Postgres classes that now also run against M2 (see the report).
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class FinanceLedgerWritersPostgresTests
{
    private readonly PostgresFixture _fx;
    public FinanceLedgerWritersPostgresTests(PostgresFixture fx) => _fx = fx;

    private async Task<GlPostingKit> NewKitAsync()
    {
        Guid tenantId;
        await using (var db = _fx.CreateDb()) tenantId = await PostgresFixture.SeedMinimalTenant(db);
        return await GlPostingKit.CreateAsync(_fx.CreateDb, tenantId);
    }

    private static string? StateOf(Exception e)
    {
        for (Exception? x = e; x is not null; x = x.InnerException) if (x is PostgresException pg) return pg.SqlState;
        return null;
    }

    private async Task AssertRefused(string sqlState, string sql, string because)
    {
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var failure = await Record.ExceptionAsync(() => cmd.ExecuteNonQueryAsync());
        failure.Should().NotBeNull(because + " :: " + sql);
        StateOf(failure!).Should().Be(sqlState, because + " :: " + failure);
    }

    // ── the void family: IsReversed + linked contras, through the real Lock and the real PayrollVoidService ────

    [Fact]
    public async Task RealLockAndRealVoid_PassTheTriggers_AndThenTheLedgerCannotBeEdited()
    {
        await using var k = await NewKitAsync();
        var run = await k.SeedApprovedRunAsync(new[] { (1, "BASIC", "Salary", 5_000m), (2, "BASIC", "Salary", 4_000m) },
            new[] { (1, "ATTENDANCE", "Attendance", 100m) }, net: 8_900m);
        await using (var lockDb = k.NewContext())
            (await k.Payroll(lockDb).Lock(run.Id, default)).Should().BeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>();

        await using (var db = k.NewContext())
        {
            var result = await new PayrollVoidService(db).VoidAsync(run.Id, k.TenantId, null, "writer sweep", "void", default);
            result.IsVoided.Should().BeTrue("the void's IsReversed flips and linked contras commit together under M2");
        }

        await using var read = k.NewContext();
        var gl = await k.RunGlAsync(run.Id, read);
        var accrual = gl.Where(l => l.EventType == GlEventTypes.Accrual).ToList();
        accrual.Should().NotBeEmpty().And.OnlyContain(l => l.IsReversed);
        gl.Where(l => l.ReversalOfEntryId != null).Should().HaveCount(accrual.Count);

        var any = accrual[0].Id;
        await AssertRefused("23001", $"UPDATE finance_gl_entries SET amount = amount + 1 WHERE id = '{any}'", "a posted amount");
        await AssertRefused("23001", $"UPDATE finance_gl_entries SET is_reversed = false WHERE id = '{any}'", "un-reversing");
        await AssertRefused("23001", $"DELETE FROM finance_gl_entries WHERE id = '{any}'", "deleting a posted line");
        await AssertRefused("23001", $"UPDATE finance_gl_entries SET company_id = NULL WHERE id = '{any}'", "detaching the company");
    }

    [Fact]
    public async Task RealVoid_WithFundsDisbursed_FlagsTheRetainedCashLegAndWritesItsReclassRow()
    {
        await using var k = await NewKitAsync();
        var run = await k.SeedApprovedRunAsync(new[] { (1, "BASIC", "Salary", 5_000m) }, Array.Empty<(int, string, string, decimal)>(), net: 5_000m);
        await using (var lockDb = k.NewContext())
            (await k.Payroll(lockDb).Lock(run.Id, default)).Should().BeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>();

        // The settlement the Pay step writes: DR the net-pay control account, CR Cash/Bank, one-sided rows.
        await using (var seed = k.NewContext())
        {
            var netPayable = (await k.RunGlAsync(run.Id, seed)).First(l => l.CreditAccount.Contains("2100"));
            seed.FinanceGlEntries.AddRange(
                new FinanceGlEntry
                {
                    TenantId = k.TenantId, CompanyId = k.CompanyId, SourceModule = "Payroll", SourceEntityId = run.Id, SourceEntityRef = "BATCH-1",
                    EventType = GlEventTypes.NetSettlement, DebitAccount = netPayable.CreditAccount, CreditAccount = string.Empty,
                    Amount = netPayable.Amount, Currency = "SAR", Period = netPayable.Period, EntryDate = netPayable.EntryDate, Description = "Net pay settled",
                },
                new FinanceGlEntry
                {
                    TenantId = k.TenantId, CompanyId = k.CompanyId, SourceModule = "Payroll", SourceEntityId = run.Id, SourceEntityRef = "BATCH-1",
                    EventType = GlEventTypes.NetSettlement, DebitAccount = string.Empty, CreditAccount = "1000 - Cash/Bank",
                    Amount = netPayable.Amount, Currency = "SAR", Period = netPayable.Period, EntryDate = netPayable.EntryDate, Description = "Net pay settled",
                });
            await seed.SaveChangesAsync();
        }

        await using (var db = k.NewContext())
        {
            var result = await new PayrollVoidService(db).VoidAsync(run.Id, k.TenantId, null, "writer sweep", "money left",
                default, new PayrollVoidOptions { SettlementDisposition = PayrollVoidDispositions.FundsDisbursed });
            result.IsVoided.Should().BeTrue(result.BlockMessage);
        }

        await using var read = k.NewContext();
        var gl = await k.RunGlAsync(run.Id, read);
        var cash = gl.Single(l => l.EventType == GlEventTypes.NetSettlement && l.CreditAccount.Contains("1000"));
        cash.IsReversed.Should().BeTrue("the retained cash leg is flagged, with no linked contra, by design");
        gl.Where(l => l.ReversalOfEntryId == cash.Id).Should().BeEmpty();
        gl.Should().Contain(l => l.EventType == GlEventTypes.NetSettlementReclass && l.ReversalOfEntryId == null);
    }

    // ── BuildContraGl family: settle/remit/outside-payment reversal and final-settlement cancel ────────────────

    [Fact]
    public async Task BuildContraGl_ThenFlag_CommitsForEverySettlementReversalShape()
    {
        await using var k = await NewKitAsync();
        var runId = Guid.NewGuid();
        var build = typeof(PayrollController).GetMethod("BuildContraGl", BindingFlags.NonPublic | BindingFlags.Static)!;

        await using var seed = k.NewContext();
        var originals = new[]
        {
            Line(k, runId, GlEventTypes.NetSettlement, "2100 - Net pay", "", 700m),
            Line(k, runId, GlEventTypes.NetSettlement, "", "1000 - Cash/Bank", 700m),
            Line(k, runId, GlEventTypes.Remit("GOSI"), "2101 - GOSI payable", "", 90m),
            Line(k, runId, GlEventTypes.Remit("GOSI"), "", "1000 - Cash/Bank", 90m),
            Line(k, runId, GlEventTypes.NetSettlement, "2100 - Net pay", "1000 - Cash/Bank", 55m), // a legacy two-sided row
        };
        seed.FinanceGlEntries.AddRange(originals);
        await seed.SaveChangesAsync();

        // The writers' exact sequence: build the contras from the loaded originals, flag the originals, AddRange, one save.
        await using var db = k.NewContext();
        var loaded = await db.FinanceGlEntries.Where(x => x.TenantId == k.TenantId && x.SourceEntityId == runId).ToListAsync();
        var contras = (List<FinanceGlEntry>)build.Invoke(null, new object?[] { k.TenantId, runId, loaded, GlEventTypes.NetSettlementReversal, Guid.NewGuid(), "ops", "reverse" })!;
        foreach (var o in loaded) o.IsReversed = true;
        db.FinanceGlEntries.AddRange(contras);
        await db.SaveChangesAsync();

        await using var read = k.NewContext();
        (await read.FinanceGlEntries.AsNoTracking().CountAsync(x => x.SourceEntityId == runId && x.IsReversed)).Should().Be(originals.Length);
        (await read.FinanceGlEntries.AsNoTracking().CountAsync(x => x.SourceEntityId == runId && x.ReversalOfEntryId != null)).Should().Be(originals.Length);

        // And the same writer cannot reverse the same lines twice.
        await using var again = k.NewContext();
        var twice = (List<FinanceGlEntry>)build.Invoke(null, new object?[] { k.TenantId, runId, loaded, GlEventTypes.NetSettlementReversal, Guid.NewGuid(), "ops", "reverse again" })!;
        again.FinanceGlEntries.AddRange(twice);
        var failure = await Record.ExceptionAsync(() => again.SaveChangesAsync());
        // AM-35: the app guard refuses first (reversal_invalid, before anything is sent); the trigger is the second layer that would have
        // (SQLSTATE 23514 at commit). Either layer's refusal proves a double reversal cannot commit; the next test pins the trigger alone.
        failure.Should().NotBeNull("a second full contra of the same lines is a double reversal");
        var appGuard = failure is GlCostCentreRuleException { Code: "reversal_invalid" };
        (appGuard || StateOf(failure!) == "23514").Should().BeTrue($"either the app guard or the trigger must refuse :: {failure}");
        await using var after = k.NewContext();
        (await after.FinanceGlEntries.AsNoTracking().CountAsync(x => x.SourceEntityId == runId && x.ReversalOfEntryId != null))
            .Should().Be(originals.Length, "the refused second set wrote nothing");
    }

    [Fact]
    public async Task ASecondFullContra_InsertedByDirectSql_IsRefusedByTheTriggerAlone_DefenceInDepth()
    {
        // AM-35: with the app guard out of the picture (plain SQL, no EF), the M2 trigger still refuses to commit a double reversal.
        await using var k = await NewKitAsync();
        var runId = Guid.NewGuid();
        var original = Guid.NewGuid();
        var firstContra = Guid.NewGuid();
        string Insert(Guid id, Guid? reversalOf, bool reversed, string debit, string credit) =>
            "INSERT INTO finance_gl_entries (id, tenant_id, company_id, source_module, source_entity_id, source_entity_ref, event_type, debit_account, " +
            "credit_account, amount, currency, entry_date, period, description, posted_by_name, is_reversed, reversal_of_entry_id, created_at_utc, erp_posting_status) VALUES (" +
            $"'{id}', '{k.TenantId}', '{k.CompanyId}', 'Payroll', '{runId}', 'BATCH-1', '{(reversalOf is null ? GlEventTypes.NetSettlement : GlEventTypes.NetSettlementReversal)}', " +
            $"'{debit}', '{credit}', 700, 'SAR', DATE '2026-09-25', '2026-09', 'direct sql', 'poster', {(reversed ? "true" : "false")}, " +
            $"{(reversalOf is null ? "NULL" : $"'{reversalOf}'")}, now(), 'NotReady')";

        await using (var conn = new NpgsqlConnection(_fx.ConnectionString))
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            foreach (var sql in new[]
                     {
                         Insert(original, null, false, "2100 - Net pay", ""),
                         Insert(firstContra, original, false, "", "2100 - Net pay"),
                         $"UPDATE finance_gl_entries SET is_reversed = true WHERE id = '{original}'",
                     })
                await using (var cmd = new NpgsqlCommand(sql, conn, tx)) await cmd.ExecuteNonQueryAsync();
            await tx.CommitAsync();
        }

        await AssertRefused("23514", Insert(Guid.NewGuid(), original, false, "", "2100 - Net pay"),
            "a second full contra of an already reversed line is refused by the database on its own");
    }

    // ── loans and bonuses: the other two IsReversed writers ───────────────────────────────────────────────────

    [Fact]
    public async Task LoanJournalReversal_AndBonusCancel_FullAndPartial_CommitUnderTheTriggers()
    {
        await using var k = await NewKitAsync();
        var loanId = Guid.NewGuid();
        var bonusId = Guid.NewGuid();
        await using var seed = k.NewContext();
        var disbursement = new FinanceGlEntry
        {
            TenantId = k.TenantId, CompanyId = k.CompanyId, SourceModule = "Loan", SourceEntityId = loanId, SourceEntityRef = "LN-1", EventType = "Disbursement",
            DebitAccount = "1400 - Loans receivable", CreditAccount = "1000 - Cash/Bank", Amount = 3_000m, Currency = "SAR", Period = "2026-09",
            EntryDate = new DateOnly(2026, 9, 3), Description = "Loan Disbursement: LN-1",
        };
        var accrualFull = Bonus(k, bonusId, 800m);
        var accrualPartial = Bonus(k, Guid.NewGuid(), 500m);
        seed.FinanceGlEntries.AddRange(disbursement, accrualFull, accrualPartial);
        await seed.SaveChangesAsync();

        await using (var db = k.NewContext())  // LoansController.ReverseLoanJournalAsync: contra (swapped, same amount) + original.IsReversed = true
        {
            var original = await db.FinanceGlEntries.SingleAsync(x => x.Id == disbursement.Id);
            db.FinanceGlEntries.Add(new FinanceGlEntry
            {
                TenantId = original.TenantId, CompanyId = original.CompanyId, SourceModule = "Loan", SourceEntityId = original.SourceEntityId, SourceEntityRef = original.SourceEntityRef,
                EventType = "DisbursementReversal", DebitAccount = original.CreditAccount, CreditAccount = original.DebitAccount, Amount = original.Amount, Currency = original.Currency,
                EntryDate = new DateOnly(2026, 9, 4), Period = "2026-09", Description = "Loan DisbursementReversal: LN-1", ReversalOfEntryId = original.Id,
            });
            original.IsReversed = true;
            await db.SaveChangesAsync();
        }
        await using (var db = k.NewContext())  // BonusesController cancel, accrual fully outstanding: contra for the remainder, original flagged
        {
            var origin = await db.FinanceGlEntries.SingleAsync(x => x.Id == accrualFull.Id);
            db.FinanceGlEntries.Add(BonusContra(origin, origin.Amount));
            origin.IsReversed = true;
            await db.SaveChangesAsync();
        }
        await using (var db = k.NewContext())  // partially consumed: contra for the remainder only, original NOT flagged
        {
            var origin = await db.FinanceGlEntries.SingleAsync(x => x.Id == accrualPartial.Id);
            db.FinanceGlEntries.Add(BonusContra(origin, 200m));
            await db.SaveChangesAsync();
        }

        await using var read = k.NewContext();
        (await read.FinanceGlEntries.AsNoTracking().SingleAsync(x => x.Id == disbursement.Id)).IsReversed.Should().BeTrue();
        (await read.FinanceGlEntries.AsNoTracking().SingleAsync(x => x.Id == accrualFull.Id)).IsReversed.Should().BeTrue();
        (await read.FinanceGlEntries.AsNoTracking().SingleAsync(x => x.Id == accrualPartial.Id)).IsReversed.Should().BeFalse();

        // Illegitimate: reverse a loan journal with a contra for MORE than was lent, or flag with no contra at all.
        await using var bad = k.NewContext();
        var other = new FinanceGlEntry
        {
            TenantId = k.TenantId, CompanyId = k.CompanyId, SourceModule = "Loan", SourceEntityId = Guid.NewGuid(), SourceEntityRef = "LN-2", EventType = "Disbursement",
            DebitAccount = "1400 - Loans receivable", CreditAccount = "1000 - Cash/Bank", Amount = 100m, Currency = "SAR", Period = "2026-09", EntryDate = new DateOnly(2026, 9, 3),
        };
        bad.FinanceGlEntries.Add(other);
        await bad.SaveChangesAsync();
        other.IsReversed = true;
        var flagOnly = await Record.ExceptionAsync(() => bad.SaveChangesAsync());
        StateOf(flagOnly!).Should().Be("23514", "a loan journal flagged reversed without its contra");
    }

    // ── erp_*: the real stampers, then the hand endpoint's loop ───────────────────────────────────────────────

    private async Task<(Guid Tenant, Guid Run, List<Guid> Lines)> SeedRunLines(GlPostingKit k, int n)
    {
        var runId = Guid.NewGuid();
        await using var db = k.NewContext();
        var lines = Enumerable.Range(0, n).Select(i => Line(k, runId, GlEventTypes.Accrual, i % 2 == 0 ? "5001 - Salary" : "", i % 2 == 0 ? "" : "2100 - Net pay", 100m + i)).ToList();
        db.FinanceGlEntries.AddRange(lines);
        await db.SaveChangesAsync();
        return (k.TenantId, runId, lines.Select(l => l.Id).ToList());
    }

    [Fact]
    public async Task ErpStampers_ExportedThenPosted_ThenRejection_RunThroughTheEvidenceTheyAlreadyWrite()
    {
        await using var k = await NewKitAsync();
        var (tenant, run, ids) = await SeedRunLines(k, 4);
        var export = new GlJournalExport { TenantId = tenant, CompanyId = k.CompanyId, Period = "2026-09", FormatKey = "generic-csv", Currency = "SAR", Status = GlJournalExportStatuses.Exported, FileName = "j.csv", FileHash = "h" };

        // Export creation: persist the export + its lines, stamp Exported, one save (GlJournalExportsController.Create).
        await using (var db = k.NewContext())
        {
            db.GlJournalExports.Add(export);
            db.GlJournalExportLines.AddRange(ids.Select((id, i) => new GlJournalExportLine { TenantId = tenant, GlJournalExportId = export.Id, FinanceGlEntryId = id, LineNo = i + 1, Side = "DR" }));
            var stamp = await ErpPostingEvidence.StampAsync(db, tenant, ids, ErpPostingStatuses.Exported, null, DateTime.UtcNow, default);
            stamp.LinesStamped.Should().Be(4);
            await db.SaveChangesAsync();
        }

        // Confirmation: stamp Posted with the ERP's number, mark the export Confirmed, one save (Confirm).
        await using (var db = k.NewContext())
        {
            var confirming = await db.GlJournalExports.SingleAsync(x => x.Id == export.Id);
            await ErpPostingEvidence.StampAsync(db, tenant, ids.Take(3).ToList(), ErpPostingStatuses.Posted, "DOC-77", DateTime.UtcNow, default);
            confirming.Status = GlJournalExportStatuses.Confirmed;
            confirming.ErpDocumentNumber = "DOC-77";
            await db.SaveChangesAsync();
        }

        // A later re-export must not walk a Posted line back: the stamper skips it, the trigger would refuse it.
        await using (var db = k.NewContext())
        {
            var again = await ErpPostingEvidence.StampAsync(db, tenant, ids, ErpPostingStatuses.Exported, null, DateTime.UtcNow, default);
            again.LinesSkippedAlreadyPosted.Should().Be(3);
            await db.SaveChangesAsync();
        }
        // The one line not confirmed is rejected by the ERP (journal reject): the stamper leaves Posted lines alone.
        await using (var db = k.NewContext())
        {
            (await ErpPostingEvidence.StampRejectionAsync(db, tenant, ids, "unbalanced in ERP", DateTime.UtcNow, default)).Should().Be(1);
            await db.SaveChangesAsync();
        }

        await using var read = k.NewContext();
        var rows = await read.FinanceGlEntries.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync();
        rows.Count(r => r.ErpPostingStatus == ErpPostingStatuses.Posted && r.ErpDocumentNumber == "DOC-77").Should().Be(3);
        rows.Count(r => r.ErpPostingStatus == ErpPostingStatuses.Rejected && r.ErpRejectionReason == "unbalanced in ERP").Should().Be(1);

        // Illegitimate: Posted without a confirming export, a Posted line walked back, its number edited.
        var posted = rows.First(r => r.ErpPostingStatus == ErpPostingStatuses.Posted).Id;
        var rejected = rows.First(r => r.ErpPostingStatus == ErpPostingStatuses.Rejected).Id;
        await AssertRefused("23001", $"UPDATE finance_gl_entries SET erp_posting_status = 'Exported' WHERE id = '{posted}'", "walking Posted back");
        await AssertRefused("23001", $"UPDATE finance_gl_entries SET erp_document_number = NULL WHERE id = '{posted}'", "erasing the ERP's number");
        await AssertRefused("23514", $"UPDATE finance_gl_entries SET erp_posting_status = 'Posted', erp_document_number = 'MADE-UP' WHERE id = '{rejected}'", "Posted with no evidence");
    }

    [Fact]
    public async Task HandStatusEndpoint_ReadyForErpAndRejected_PassOnLiveLines_ButCannotOverwriteAPostedLine()
    {
        await using var k = await NewKitAsync();
        var (tenant, run, ids) = await SeedRunLines(k, 3);

        // PayrollController.UpdateErpPostingStatus: ReadyForErp, then Rejected with the operator's note, on every GL row of the run.
        await using (var db = k.NewContext())
        {
            foreach (var status in new[] { ErpPostingStatuses.ReadyForErp, ErpPostingStatuses.Rejected })
            {
                foreach (var entry in await db.FinanceGlEntries.Where(x => x.TenantId == tenant && x.SourceEntityId == run).ToListAsync())
                {
                    entry.ErpPostingStatus = status;
                    entry.ErpStatusChangedAtUtc = DateTime.UtcNow;
                    entry.ErpDocumentNumber = "REF-1";
                    entry.ErpRejectionReason = status == ErpPostingStatuses.Rejected ? "operator note" : null;
                }
                await db.SaveChangesAsync();
            }
        }

        // The finding: the endpoint writes the status onto ALL the run's rows, Posted ones included. With one line confirmed by
        // the ERP, a hand "Rejected" would overwrite evidence of a posting; the ledger refuses it.
        var export = new GlJournalExport { TenantId = tenant, CompanyId = k.CompanyId, Period = "2026-09", FormatKey = "generic-csv", Currency = "SAR", FileName = "j.csv", FileHash = "h" };
        await using (var db = k.NewContext())
        {
            db.GlJournalExports.Add(export);
            db.GlJournalExportLines.Add(new GlJournalExportLine { TenantId = tenant, GlJournalExportId = export.Id, FinanceGlEntryId = ids[0], LineNo = 1, Side = "DR" });
            await ErpPostingEvidence.StampAsync(db, tenant, new[] { ids[0] }, ErpPostingStatuses.Posted, "DOC-9", DateTime.UtcNow, default);
            export.Status = GlJournalExportStatuses.Confirmed;
            export.ErpDocumentNumber = "DOC-9";
            await db.SaveChangesAsync();
        }
        await using (var db = k.NewContext())
        {
            foreach (var entry in await db.FinanceGlEntries.Where(x => x.TenantId == tenant && x.SourceEntityId == run).ToListAsync())
            {
                entry.ErpPostingStatus = ErpPostingStatuses.Rejected;
                entry.ErpRejectionReason = "operator note";
            }
            var failure = await Record.ExceptionAsync(() => db.SaveChangesAsync());
            StateOf(failure!).Should().Be("23001", "the hand endpoint must skip lines the ERP already posted; the ledger will not let it overwrite them");
        }
    }

    // ── tenant erasure: the only DELETE of the ledger ─────────────────────────────────────────────────────────

    [Fact]
    public async Task TenantPurge_ErasesALedger_OnlyOnceTheTenantIsSoftDeleted()
    {
        // PlatformController.PurgeTenantRowsCoreAsync<TEntity> and SoftDeletedTenantRule: ExecuteDelete over every ITenantOwned type,
        // filtered by tenant id. The same statement against a LIVE tenant must not remove a posted line.
        await using var live = await NewKitAsync();
        var (liveTenant, _, liveIds) = await SeedRunLines(live, 2);
        await using (var db = live.NewContext())
        {
            var refused = await Record.ExceptionAsync(() => db.FinanceGlEntries.IgnoreQueryFilters().Where(e => e.TenantId == liveTenant).ExecuteDeleteAsync());
            StateOf(refused!).Should().Be("23001", "a live tenant's ledger cannot be erased");
        }

        await using var gone = await NewKitAsync();
        var (goneTenant, goneRun, goneIds) = await SeedRunLines(gone, 3);
        await using (var db = gone.NewContext())
        {
            var export = new GlJournalExport { TenantId = goneTenant, CompanyId = gone.CompanyId, Period = "2026-09", FormatKey = "generic-csv", Currency = "SAR", FileName = "j.csv", FileHash = "h" };
            db.GlJournalExports.Add(export);
            db.GlJournalExportLines.Add(new GlJournalExportLine { TenantId = goneTenant, GlJournalExportId = export.Id, FinanceGlEntryId = goneIds[0], LineNo = 1, Side = "DR" });
            var tenant = await db.Tenants.SingleAsync(t => t.Id == goneTenant);
            tenant.IsActive = false;
            tenant.Slug = $"{tenant.Slug}__deleted_{goneTenant.ToString("N")[..8]}";   // PlatformController.DeleteTenant
            await db.SaveChangesAsync();
        }
        await using (var db = gone.NewContext())
        {
            await db.GlJournalExportLines.IgnoreQueryFilters().Where(e => e.TenantId == goneTenant).ExecuteDeleteAsync();
            (await db.FinanceGlEntries.IgnoreQueryFilters().Where(e => e.TenantId == goneTenant).ExecuteDeleteAsync()).Should().Be(goneIds.Count);
        }
        await using var read = live.NewContext();
        (await read.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking().CountAsync(e => e.TenantId == liveTenant)).Should().Be(liveIds.Count);
        goneRun.Should().NotBeEmpty();
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────────

    private static FinanceGlEntry Line(GlPostingKit k, Guid runId, string eventType, string debit, string credit, decimal amount) => new()
    {
        TenantId = k.TenantId, CompanyId = k.CompanyId, SourceModule = "Payroll", SourceEntityId = runId, SourceEntityRef = "BATCH-1", EventType = eventType,
        DebitAccount = debit, CreditAccount = credit, Amount = amount, Currency = "SAR", Period = "2026-09", EntryDate = new DateOnly(2026, 9, 25), Description = "sweep",
    };

    private static FinanceGlEntry Bonus(GlPostingKit k, Guid batchId, decimal amount) => new()
    {
        TenantId = k.TenantId, CompanyId = k.CompanyId, SourceModule = BonusGlLedger.SourceModule, SourceEntityId = batchId, SourceEntityRef = "BON-1",
        EventType = GlEventTypes.BonusAccrual, DebitAccount = "5004 - Bonus expense", CreditAccount = "2310 - Bonus payable", Amount = amount, Currency = "SAR",
        Period = "2026-09", EntryDate = new DateOnly(2026, 9, 10), Description = "Bonus accrual",
    };

    private static FinanceGlEntry BonusContra(FinanceGlEntry origin, decimal amount) => new()
    {
        TenantId = origin.TenantId, CompanyId = origin.CompanyId, SourceModule = origin.SourceModule, SourceEntityId = origin.SourceEntityId, SourceEntityRef = origin.SourceEntityRef,
        EventType = GlEventTypes.BonusAccrualReversal, DebitAccount = origin.CreditAccount, CreditAccount = origin.DebitAccount, Amount = amount, Currency = origin.Currency,
        EntryDate = new DateOnly(2026, 9, 20), Period = origin.Period, Description = "Bonus cancelled", ReversalOfEntryId = origin.Id,
    };
}
