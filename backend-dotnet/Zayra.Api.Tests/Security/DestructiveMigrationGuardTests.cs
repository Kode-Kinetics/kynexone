using System.Text.RegularExpressions;
using FluentAssertions;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Zero-downtime gate for migrations. During a rolling deploy the OLD code runs against the NEW
/// schema, so a migration whose <c>Up</c> drops, renames or tightens a column that old code still
/// reads takes every old instance down. Such a change is only safe as the CONTRACT phase of an
/// expand → migrate → contract sequence (docs/schema/HOW_TO_CHANGE_THE_SCHEMA.md §1), and must say
/// so with <c>[ContractPhase("reason")]</c> on the migration class.
///
/// <para>Scans the <c>Up</c> method only (a <c>Down</c> that drops what <c>Up</c> added is normal).
/// Flags DropColumn, DropTable, RenameColumn, RenameTable, an AlterColumn to non-nullable from nullable
/// with no default, and the raw-SQL forms of the same inside <c>migrationBuilder.Sql</c>.</para>
///
/// <para>Migrations that existed before this gate are grandfathered by id in <see cref="Baseline"/>.
/// The baseline only shrinks: every entry must still exist and still be destructive, so it cannot
/// rot into a list of names that no longer mean anything.</para>
/// </summary>
public sealed partial class DestructiveMigrationGuardTests
{
    /// <summary>Destructive migrations that shipped before this gate. Never add to this list.</summary>
    private static readonly HashSet<string> Baseline = new(StringComparer.Ordinal)
    {
        "20260816010419_PersistKeyRingAndEmployeePublicIdentity", // AlterColumn to NOT NULL, no default
        "20260816012031_AddRefreshTokenFamilies",                 // AlterColumn to NOT NULL, no default
        "20260923042704_OvertimeQuantityInMinutes",               // DropColumn x2 in the same release
    };

    [Fact]
    public void NoNewMigrationIsDestructiveWithoutAContractPhaseMarker()
    {
        var offenders = new List<string>();
        foreach (var migration in Migrations())
        {
            if (Baseline.Contains(migration.Id)) continue;
            var findings = Scan(migration.Code);
            if (findings.Count == 0) continue;
            var marker = ContractPhaseMarker().Match(migration.Code);
            if (marker.Success && !string.IsNullOrWhiteSpace(marker.Groups["reason"].Value)) continue;
            offenders.Add($"{migration.Id}: {string.Join("; ", findings)}");
        }

        offenders.Should().BeEmpty(
            "a migration whose Up drops, renames or tightens a column breaks the old instances still " +
            "running during a rolling deploy. Ship it as the contract phase of expand → migrate → contract " +
            "and mark the class [ContractPhase(\"<release that stopped using it>\")] — see " +
            "docs/schema/HOW_TO_CHANGE_THE_SCHEMA.md.\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void BaselineOnlyNamesMigrationsThatStillExistAndAreStillDestructive()
    {
        var byId = Migrations().ToDictionary(m => m.Id, m => m.Code, StringComparer.Ordinal);
        var stale = Baseline
            .Where(id => !byId.TryGetValue(id, out var code) || Scan(code).Count == 0)
            .ToList();
        stale.Should().BeEmpty("the grandfather list may only shrink; remove entries that no longer apply");
    }

    [Fact]
    public void ScannerFlagsEachDestructiveFormAndIgnoresSafeOnes()
    {
        static string Migration(string up, string down = "") =>
            "public partial class X : Migration {\n protected override void Up(MigrationBuilder migrationBuilder) {\n"
            + up + "\n }\n protected override void Down(MigrationBuilder migrationBuilder) {\n" + down + "\n }\n}";

        Scan(Migration("migrationBuilder.DropColumn(name: \"a\", table: \"t\");")).Should().ContainSingle();
        Scan(Migration("migrationBuilder.DropTable(name: \"t\");")).Should().ContainSingle();
        Scan(Migration("migrationBuilder.RenameColumn(name: \"a\", table: \"t\", newName: \"b\");")).Should().ContainSingle();
        Scan(Migration("migrationBuilder.RenameTable(name: \"t\", newName: \"u\");")).Should().ContainSingle();
        Scan(Migration("""
            migrationBuilder.AlterColumn<string>(name: "a", table: "t", type: "text", nullable: false,
                oldClrType: typeof(string), oldType: "text", oldNullable: true);
            """)).Should().ContainSingle();
        Scan(Migration("migrationBuilder.Sql(\"ALTER TABLE t DROP COLUMN a;\");")).Should().ContainSingle();
        Scan(Migration("migrationBuilder.Sql(@\"ALTER TABLE t RENAME COLUMN a TO b;\");")).Should().ContainSingle();
        Scan(Migration("migrationBuilder.Sql(\"ALTER TABLE t ALTER COLUMN a SET NOT NULL;\");")).Should().ContainSingle();
        Scan(Migration("migrationBuilder.Sql(\"DROP TABLE IF EXISTS t;\");")).Should().ContainSingle();

        // Safe: additive Up with the drop only in Down; tightening WITH a default; loosening; comments.
        Scan(Migration("migrationBuilder.AddColumn<string>(name: \"a\", table: \"t\", nullable: true);",
            "migrationBuilder.DropColumn(name: \"a\", table: \"t\");")).Should().BeEmpty();
        Scan(Migration("""
            migrationBuilder.AlterColumn<string>(name: "a", table: "t", nullable: false, defaultValue: "",
                oldClrType: typeof(string), oldNullable: true);
            """)).Should().BeEmpty();
        Scan(Migration("""
            migrationBuilder.AlterColumn<string>(name: "a", table: "t", nullable: true,
                oldClrType: typeof(string), oldNullable: false);
            """)).Should().BeEmpty();
        Scan(Migration("// migrationBuilder.DropColumn(name: \"a\", table: \"t\");")).Should().BeEmpty();
        Scan(Migration("migrationBuilder.Sql(\"DROP INDEX IF EXISTS ix_a;\");")).Should().BeEmpty();

        ContractPhaseMarker().IsMatch("[ContractPhase(\"Release 41 stopped reading name_en\")]").Should().BeTrue();
        ContractPhaseMarker().IsMatch("[Zayra.Api.Infrastructure.Data.ContractPhase(\"r\")]").Should().BeTrue();
    }

    [Fact]
    public void TheScanActuallyReachesTheMigrations()
    {
        var migrations = Migrations().ToList();
        migrations.Should().HaveCountGreaterThan(50, "the guard must be scanning the real Migrations folder");
        migrations.Should().OnlyContain(m => UpBody(m.Code) != null, "every migration has an Up method the scanner can find");
    }

    // ── Scanner ───────────────────────────────────────────────────────────────

    private sealed record MigrationSource(string Id, string Code);

    private static IEnumerable<MigrationSource> Migrations()
    {
        var root = Path.Combine(SourceScan.ResolveApiRoot(), "Migrations");
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.TopDirectoryOnly)
            .Where(f => !f.EndsWith(".Designer.cs", StringComparison.Ordinal)
                        && !f.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal))
            .Select(f => new MigrationSource(Path.GetFileNameWithoutExtension(f),
                SourceScan.StripCSharpComments(File.ReadAllText(f))));
        // Comments are blanked so a commented-out DropColumn or [ContractPhase] counts for nothing.
    }

    /// <summary>Text between Up's signature and Down's (or end of file).</summary>
    private static string? UpBody(string code)
    {
        var up = UpSignature().Match(code);
        if (!up.Success) return null;
        var down = DownSignature().Match(code, up.Index + up.Length);
        return down.Success ? code[up.Index..down.Index] : code[up.Index..];
    }

    private static List<string> Scan(string code)
    {
        var findings = new List<string>();
        var up = UpBody(SourceScan.StripCSharpComments(code));
        if (up is null) return findings;

        foreach (Match m in DestructiveCall().Matches(up))
            findings.Add(m.Groups["op"].Value);

        foreach (Match m in AlterColumnStart().Matches(up))
        {
            var args = BalancedArguments(up, m.Index + m.Length - 1);
            if (Regex.IsMatch(args, @"\bnullable\s*:\s*false\b")
                && Regex.IsMatch(args, @"\boldNullable\s*:\s*true\b")
                && !Regex.IsMatch(args, @"\bdefaultValue(Sql)?\s*:"))
                findings.Add("AlterColumn to NOT NULL without a default");
        }

        foreach (Match m in DestructiveSql().Matches(up))
            findings.Add($"raw SQL '{Regex.Replace(m.Value.ToUpperInvariant(), @"\s+", " ")}'");

        return findings;
    }

    /// <summary>The argument text of the call whose '(' is at <paramref name="open"/>.</summary>
    private static string BalancedArguments(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0) return text[(open + 1)..i];
        }
        return text[(open + 1)..];
    }

    [GeneratedRegex(@"override\s+void\s+Up\s*\(")]
    private static partial Regex UpSignature();

    [GeneratedRegex(@"override\s+void\s+Down\s*\(")]
    private static partial Regex DownSignature();

    [GeneratedRegex(@"\.(?<op>DropColumn|DropTable|RenameColumn|RenameTable)\s*(<[^>]*>)?\s*\(")]
    private static partial Regex DestructiveCall();

    [GeneratedRegex(@"\.AlterColumn\s*<[^>]*>\s*\(")]
    private static partial Regex AlterColumnStart();

    [GeneratedRegex(@"\bDROP\s+COLUMN\b|\bDROP\s+TABLE\b|\bRENAME\s+COLUMN\b|\bRENAME\s+TO\b|\bSET\s+NOT\s+NULL\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex DestructiveSql();

    [GeneratedRegex(@"\[\s*(?:[\w.]+\.)?ContractPhase(?:Attribute)?\s*\(\s*@?""(?<reason>[^""]*)""\s*\)\s*\]")]
    private static partial Regex ContractPhaseMarker();
}
