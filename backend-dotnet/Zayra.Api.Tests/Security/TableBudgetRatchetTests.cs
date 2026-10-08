using FluentAssertions;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// TABLE-BUDGET RATCHET — the set of tables is pinned in <c>table-budget.txt</c>, and a new one
/// fails the build until that file is edited in the same change.
///
/// <para><b>Why this exists.</b> 323 tables were not one decision. They were 323 instances of "this
/// feature needs a table", each individually reasonable, none reviewed against the whole. The result
/// was 14 audit tables, 17 approval tables, payroll results spread over 6, and 40 tables no code ever
/// reads. Nothing in the build ever asked "should this be a table?", so the answer was always yes.</para>
///
/// <para><b>What it does NOT do.</b> It does not forbid new tables. Some features genuinely need one.
/// It makes adding one a visible, reviewable act: a line in a manifest, in the diff, with a human
/// deciding. The cost is one line; the thing it buys is that nobody adds the 324th by accident.</para>
///
/// <para><b>THE RULE FOR NEW CODE.</b> Before adding a <c>DbSet</c>, answer three questions in the PR:
/// which capability needs it and why no existing table can carry the fact; who owns the data; how long
/// it is kept. If you cannot answer all three, the fact belongs on a table that already exists.
/// See <c>.claude/skills/kynexone-schema/SKILL.md</c> and <c>docs/schema/ANTI_PATTERNS.md</c>.</para>
///
/// <para><b>Removals are welcome</b> and also edit the manifest — the count is expected to fall sharply
/// when the 76-table rebuild lands. This guard is about deliberate change in either direction, not about
/// a number that may only go down.</para>
/// </summary>
public class TableBudgetRatchetTests
{
    private static readonly string ManifestPath = TestPath("Security/table-budget.txt");
    private static readonly string DbContextPath = TestPath("../Zayra.Api/Data/ZayraDbContext.cs");

    [Fact]
    public void EveryDbSet_IsInTheManifest_AndEveryManifestEntry_Exists()
    {
        var declared = DeclaredDbSets();
        var pinned = File.ReadAllLines(ManifestPath)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);

        var added = declared.Except(pinned).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var removed = pinned.Except(declared).OrderBy(n => n, StringComparer.Ordinal).ToList();

        added.Should().BeEmpty(
            "a new table is a design decision, not a detail. Before adding {0} to " +
            "Zayra.Api.Tests/Security/table-budget.txt, answer in the PR: which capability needs it and " +
            "why no existing table can carry the fact; who owns the data; how long it is kept. " +
            "323 tables arrived one reasonable-looking table at a time — see docs/schema/ANTI_PATTERNS.md",
            string.Join(", ", added));

        removed.Should().BeEmpty(
            "{0} was removed from the model but is still pinned in table-budget.txt. Deleting a table is " +
            "good; delete its line in the same change so the manifest stays the truth",
            string.Join(", ", removed));
    }

    /// <summary>
    /// The count, stated once and in the open. A test that only compares sets would let a change that
    /// adds one table and removes another pass without anyone noticing the shape shifted.
    /// </summary>
    [Fact]
    public void TheTableCount_IsWhatWeThinkItIs()
    {
        // Separate loan payments require a batch authorization record, frozen payment
        // instructions, and referenced receipts. Finance owns these monetary records;
        // retention follows the loan ledger (84 months). Payroll batches cannot carry
        // them without reintroducing the payroll dependency this workflow removes.
        // LoanChangeRequest captures maker/checker authorization for policy exceptions,
        // rescheduling and monetary reversals, not a replacement for the immutable ledger.
        // HR/Finance own it, with the same 84-month loan-record retention requirement.
        // GradeEntitlements (slice L1): per-grade entitlement cells keyed by pay component — first used for
        // loan limits by grade, and the home for later grade entitlements. No existing table carries an
        // effective-dated, per-grade, per-component value with a no-overlap guarantee. HR/Finance own it;
        // retained with the loan ledger (84 months), since loans keep a witness id into it.
        // Release A (R0, rev 8.3.2 — the only two new tables of the release):
        // EmployeeEntitlements: the Contractual package frozen per contract term (Art. 59 acquired rights); the salary
        // row may hold only QiwaWage cash and a grade cell is today's standard, not a signed term, so no existing table
        // can carry it. HR owns it; retained as labour-law evidence (class S, with employee_contracts).
        // ContractRenewalCases: one review per expiring term (precedent final_settlements) — offer, approvals, employee
        // acceptance, Qiwa evidence and Apply; no existing table holds a case with these legal deadlines. HR owns it;
        // retained as labour-law evidence (class S).
        // Selfie attendance v2 (migration 20261008000500 — the only two tables of the feature):
        // AttendanceEvidence: one uploaded selfie's envelope — opaque single-use evidence id, owner, SHA-256, expiry, the
        // punch that used it and its purge state. No existing table holds a single-use token with its own purge lifecycle
        // (employee_documents are kept HR records; attendance_raw_events.photo_reference is a free device string). HR owns
        // it; class E — blob purged by the selfie retention rule, envelope and sha256 kept.
        // BiometricConsents: per-version consent to selfie attendance with withdrawal; nothing records consent today. HR
        // owns it (DPO approves retention); kept for employment + the statutory minimum as the lawful basis.
        DeclaredDbSets().Should().HaveCount(332,
            "the live schema is 328 tables and the approved rebuild target is 76 " +
            "(TARGET_SCHEMA.md). If this number moved, say so in the PR and update it here");
    }

    private static HashSet<string> DeclaredDbSets()
    {
        var source = File.ReadAllText(DbContextPath);
        return System.Text.RegularExpressions.Regex
            .Matches(source, @"public\s+DbSet<[A-Za-z0-9_.]+>\s+(?<name>[A-Za-z0-9_]+)")
            .Select(m => m.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string TestPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Zayra.Api.Tests.csproj")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the test project root must be findable from the test binary");
        return Path.GetFullPath(Path.Combine(dir!.FullName, relative));
    }
}
