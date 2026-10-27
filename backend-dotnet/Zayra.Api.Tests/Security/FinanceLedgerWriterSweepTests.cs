using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// T-052 and S-020: the finance_gl_entries WRITER SWEEP, in the style of <see cref="RawSqlExecutionRatchetTests"/> and
/// <see cref="QueryFilterBypassRatchetTests"/>. Migration M2 makes the ledger append-only in the database; this ratchet makes
/// sure no writer is added, or changed, without somebody deciding which of the database's two doors it uses:
/// <list type="number">
///  <item>INSERT (a posting, or a contra), or</item>
///  <item>the only UPDATEs the triggers allow: <c>IsReversed</c> false to true together with its contra, and the
///   <c>erp_*</c> stamps along the state machine.</item>
/// </list>
/// A bulk statement (<c>ExecuteUpdate</c>, <c>ExecuteDelete</c>, raw SQL, COPY) against the ledger or against the
/// cost-centre stamp columns of the payroll tables is refused outright: the triggers would refuse it at run time, and the
/// ratchet refuses it in CI first. Each pinned number below is a writer that
/// <c>FinanceLedgerWritersPostgresTests</c> (or an existing Postgres class named beside it) drives against the real triggers.
/// Counts may go DOWN without discussion. Raising one means adding a line of reasoning here and a test there.
/// </summary>
public class FinanceLedgerWriterSweepTests
{
    // (DbSet writes, receiver.IsReversed = flips, receiver.Erp* = stamps, `new FinanceGlEntry` constructions) per file.
    // Receiver assignments are counted on any receiver: a run, an export or a loan receipt is not the ledger, and the
    // notes say which counted site is which. Verified by hand against the sources on 2026-10-25.
    private static readonly Dictionary<string, (int DbSetWrites, int IsReversedFlips, int ErpStamps, int Constructions)> Approved = new()
    {
        // Loan disbursement posting.
        ["Controllers/Finance/LoansController.cs"] = (1, 0, 0, 1),
        // AddLoanJournalAsync (post) + ReverseLoanJournalAsync (contra + original.IsReversed = true).
        ["Controllers/Finance/LoansController.Ledger.cs"] = (1, 1, 0, 1),
        // The one flip here is LoanRepayment.IsReversed (a receipt, not the ledger).
        ["Controllers/Finance/LoansController.Corrections.cs"] = (0, 1, 0, 0),
        // Advance disbursement posting.
        ["Controllers/Finance/AdvancesController.cs"] = (1, 0, 0, 1),
        // Bonus accrual, cancel contra (+ origin.IsReversed = true only when the contra closes the accrual), payment.
        ["Controllers/Finance/BonusesController.cs"] = (3, 1, 0, 2),   // accrual now built by BonusCostSplit; driven by BonusCostingPostgresTests
        // Manual GL adjustments (inserts only).
        ["Controllers/Finance/GlAdjustmentsController.cs"] = (1, 0, 0, 0),
        // The two counted stamps are GlJournalExport.ErpDocumentNumber (the artifact, not the ledger).
        ["Controllers/Finance/GlJournalExportsController.cs"] = (0, 0, 2, 0),
        // Lock accrual, settle, remit, bonus clearing, settlement discharge and their contras; four IsReversed flips
        // (outside-payment reversal, settlement reversal, remittance reversal, final-settlement cancel); the hand
        // erp-posting-status loop (4 entry stamps) plus three PayrollRun.ErpPostingStatus assignments.
        ["Controllers/PayrollController.cs"] = (10, 4, 7, 15),   // C2: lines built with At(new FinanceGlEntry ...), one construction fewer
        ["Infrastructure/Payroll/BonusCostSplit.cs"] = (0, 0, 0, 1),            // costed bonus accrual rows (inserts), BonusCostingPostgresTests + oracle
        ["Infrastructure/Payroll/EosbProvisionAccrual.cs"] = (0, 0, 0, 1),      // EOSB provision lines (inserts), inside the Lock journal
        ["Infrastructure/Payroll/FinalSettlementGlLedger.cs"] = (0, 0, 0, 1),   // settlement accrual lines (inserts), oracle ApproveFinalSettlement
        // StampAsync / StampRejectionAsync: 8 ledger-line stamps and one PayrollRun stamp.
        ["Infrastructure/Finance/ErpPostingEvidence.cs"] = (0, 0, 9, 0),
        // External-discharge journal (inserts only).
        ["Infrastructure/Payroll/FinalSettlementExternalDischarge.cs"] = (1, 0, 0, 0),
        // The void: contras (inserts), originals flagged IsReversed; one PayrollRun.ErpPostingStatus reset.
        ["Infrastructure/Payroll/PayrollVoidService.cs"] = (1, 1, 1, 0),
    };

    private static readonly Regex DbSetWrite = new(@"FinanceGlEntries\s*\.\s*(Add|AddRange|Remove|RemoveRange|Update|UpdateRange|Attach)\s*\(", RegexOptions.Compiled);
    private static readonly Regex Flip = new(@"\b[A-Za-z_][A-Za-z0-9_]*\.IsReversed\s*=\s*(true|false)\b", RegexOptions.Compiled);
    private static readonly Regex Stamp = new(@"\b[A-Za-z_][A-Za-z0-9_]*\.(ErpPostingStatus|ErpDocumentNumber|ErpStatusChangedAtUtc|ErpRejectionReason)\s*=[^=>]", RegexOptions.Compiled);
    private static readonly Regex Construction = new(@"new\s+FinanceGlEntry\b", RegexOptions.Compiled);

    /// <summary>The ledger is never removed from the context, in any file.</summary>
    private static readonly Regex Removal = new(@"FinanceGlEntries\s*\.\s*(Remove|RemoveRange)\s*\(", RegexOptions.Compiled);

    private static readonly Regex BulkApi = new(@"\b(ExecuteUpdate|ExecuteDelete|ExecuteSqlRaw|ExecuteSqlInterpolated|ExecuteSql|FromSql|FromSqlRaw|FromSqlInterpolated|SqlQuery|SqlQueryRaw|BeginBinaryImport|BeginTextImport)\w*\b", RegexOptions.Compiled);

    [Fact]
    public void EveryWriterOfTheLedger_IsPinnedWithItsCount()
    {
        var actual = new Dictionary<string, (int, int, int, int)>();
        foreach (var (relative, text) in Sources())
        {
            var counts = (DbSetWrite.Matches(text).Count, Flip.Matches(text).Count, Stamp.Matches(text).Count, Construction.Matches(text).Count);
            if (counts != (0, 0, 0, 0)) actual[relative] = counts;
        }

        var drift = new List<string>();
        foreach (var file in actual.Keys.Union(Approved.Keys).OrderBy(f => f, StringComparer.Ordinal))
        {
            var found = actual.GetValueOrDefault(file);
            var pinned = Approved.TryGetValue(file, out var p) ? (p.DbSetWrites, p.IsReversedFlips, p.ErpStamps, p.Constructions) : (0, 0, 0, 0);
            if (found != pinned) drift.Add($"{file}: found (dbset, flips, stamps, constructions) = {found}, pinned {pinned}");
        }
        drift.Should().BeEmpty(
            "a writer of finance_gl_entries was added or changed. Decide which door it uses (INSERT, or the IsReversed flip with its contra, or an " +
            "erp_* stamp along the state machine), add a test beside FinanceLedgerWritersPostgresTests that drives it against the triggers, then " +
            "move the pin. Drift: " + string.Join(" | ", drift));
    }

    [Fact]
    public void NoWriterRemovesLedgerRowsThroughTheContext()
    {
        var offenders = Sources().Where(s => Removal.IsMatch(s.Text)).Select(s => s.Relative).ToList();
        offenders.Should().BeEmpty("the ledger is append-only: nothing may Remove a FinanceGlEntry (the tenant purge deletes by tenant id, in the database)");
    }

    [Fact]
    public void NoBulkOrRawStatementTouchesTheLedger_OrThePayrollCostCentreStamps()
    {
        var offenders = new List<string>();
        foreach (var (relative, text) in Sources())
        {
            foreach (var statement in text.Split(';'))
            {
                if (!BulkApi.IsMatch(statement)) continue;
                var ledger = statement.Contains("FinanceGlEntries", StringComparison.Ordinal) || statement.Contains("finance_gl_entries", StringComparison.OrdinalIgnoreCase);
                var stamp = Regex.IsMatch(statement, @"(PayrollEarnings|PayrollDeductions|PayrollSlips|PayrollRuns|payroll_earnings|payroll_deductions|payroll_slips|payroll_runs)", RegexOptions.IgnoreCase)
                         && Regex.IsMatch(statement, @"(CostCenterId|CostCenterSource|CostCenterResolutionHash|CostAccountingApplied|cost_center_id|cost_center_source|cost_center_resolution_hash|cost_accounting_applied)");
                if (ledger || stamp) offenders.Add($"{relative}: {Regex.Replace(statement.Trim(), @"\s+", " ")[..Math.Min(140, Regex.Replace(statement.Trim(), @"\s+", " ").Length)]}");
            }
        }
        offenders.Should().BeEmpty("a set-based or raw write against the ledger, or against the stamp columns of a Locked run, is exactly what the M2 triggers refuse " +
                                   "(S-020). Allowed bulk writers of non-stamp payroll columns (CompanyScopeBackfill, the Lock's slip status) do not name these columns.");
    }

    [Fact]
    public void TheLedgerTableIsNamedInRawSqlOnlyWhereTheSchemaItselfIsDeclared()
    {
        var named = Sources()
            .Where(s => s.Text.Contains("finance_gl_entries", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Relative).OrderBy(f => f, StringComparer.Ordinal).ToList();
        named.Should().Equal(new[] { "Data/ZayraDbContext.cs", "Models/CostAccountingModelConfiguration.cs" },
            "outside the model declaration nothing may name the table in SQL; a new file doing so is a new writer");
    }

    [Fact]
    public void EveryColumnOfTheLedger_IsEitherInTheAppendOnlyTriggerOrOnTheShortListOfMutableOnes()
    {
        // The entity and the trigger cannot drift: a column added to FinanceGlEntry is frozen by default, and the trigger's own
        // text must say so, or this fails until someone decides.
        using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql("Host=localhost;Database=unused").Options);
        var table = StoreObjectIdentifier.Table("finance_gl_entries");
        var columns = db.Model.FindEntityType(typeof(FinanceGlEntry))!.GetProperties().Select(p => p.GetColumnName(table)!).ToList();
        columns.Should().Contain(new[] { "id", "amount", "posting_key", "is_reversed", "erp_posting_status" });

        var mutable = new[] { "is_reversed", "erp_posting_status", "erp_document_number", "erp_status_changed_at_utc", "erp_rejection_reason" };
        var ddl = string.Join("\n", (string[]?)typeof(ZayraDbContext).Assembly.GetType("Zayra.Api.Migrations.FinanceGlEntriesAppendOnly")
            ?.GetField("PostgresOnlyDdl")?.GetValue(null) ?? Array.Empty<string>());
        ddl.Should().NotBeNullOrEmpty("migration M2 publishes its SQL");

        var frozen = columns.Except(mutable).ToList();
        var unguarded = frozen.Where(c => !Regex.IsMatch(ddl, $@"NEW\.{c}\s+IS\s+DISTINCT\s+FROM\s+OLD\.{c}\b")).ToList();
        unguarded.Should().BeEmpty("every non-mutable column of finance_gl_entries must be compared in the append-only trigger");
        foreach (var column in mutable)
            ddl.Should().Contain($"OLD.{column}", $"{column} is on the mutable list, so the trigger must read it (state machine)");
    }

    // ── source enumeration ──────────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<(string Relative, string Text)> Sources()
    {
        var root = ResolveApiRoot();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith("Migrations/", StringComparison.Ordinal) || relative.StartsWith("Db/", StringComparison.Ordinal)
                || relative.StartsWith("obj/", StringComparison.Ordinal) || relative.StartsWith("bin/", StringComparison.Ordinal)
                || relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal)) continue;
            yield return (relative, Regex.Replace(File.ReadAllText(file), @"//.*$", string.Empty, RegexOptions.Multiline));
        }
    }

    private static string ResolveApiRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6; i++)
        {
            if (dir?.Parent is null) break;
            dir = dir.Parent;
            var candidate = Path.Combine(dir.FullName, "Zayra.Api");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException($"Could not locate the Zayra.Api source root from {AppContext.BaseDirectory}.");
    }
}
