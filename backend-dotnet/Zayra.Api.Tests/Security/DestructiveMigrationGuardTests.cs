using System.Text.RegularExpressions;
using FluentAssertions;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Zero-downtime gate for migrations. During a rolling deploy the OLD code runs against the NEW
/// schema, so a migration whose <c>Up</c> drops, renames, retypes or tightens a column that old code
/// still reads takes every old instance down. Such a change is only safe as the CONTRACT phase of an
/// expand → migrate → contract sequence (docs/schema/HOW_TO_CHANGE_THE_SCHEMA.md §1), and must say
/// so with <c>[ContractPhase("reason")]</c> on the migration class.
///
/// <para>Scans the WHOLE migration file except the body of <c>Down</c> (a <c>Down</c> that drops what
/// <c>Up</c> added is normal). Scanning the file rather than only the text between <c>Up(</c> and
/// <c>Down(</c> means a SQL string held in a class-level <c>const</c>, or a helper method placed after
/// <c>Down</c>, is scanned too. A <c>migrationBuilder.Sql(…)</c> argument that names something not
/// declared in the file (a constant in another class) cannot be read, and is flagged as such.</para>
///
/// <para>Flags: DropColumn, DropTable, RenameColumn, RenameTable; an AlterColumn that goes from
/// nullable to NOT NULL (with or without a default — old code may still write an explicit NULL),
/// that shrinks <c>maxLength</c>, or that changes the store or CLR type other than widening a
/// varchar; and the raw SQL forms DROP COLUMN, DROP TABLE, RENAME [COLUMN] x TO, RENAME TO,
/// ALTER COLUMN x [SET DATA] TYPE, SET NOT NULL.</para>
///
/// <para>Migrations that existed before this gate are grandfathered by id in <see cref="Baseline"/>,
/// each with the reason it trips. The baseline only shrinks: every entry must still exist and still be
/// destructive, so it cannot rot into a list of names that no longer mean anything. A NEW migration is
/// never added to it; it carries <c>[ContractPhase]</c> instead.</para>
/// </summary>
public sealed partial class DestructiveMigrationGuardTests
{
    /// <summary>
    /// Migrations that shipped before this gate (or before it was tightened on 2026-10-05), with what
    /// they trip. Never add to this list: a new destructive migration carries [ContractPhase].
    /// </summary>
    private static readonly Dictionary<string, string> Baseline = new(StringComparer.Ordinal)
    {
        ["20260816010419_PersistKeyRingAndEmployeePublicIdentity"] = "AlterColumn to NOT NULL, no default",
        ["20260816012031_AddRefreshTokenFamilies"] = "AlterColumn to NOT NULL, no default",
        ["20260923042704_OvertimeQuantityInMinutes"] = "DropColumn x2 in the same release",
        // ── Found when the gate was tightened (whole-file scan, RENAME x TO / ALTER COLUMN TYPE in SQL,
        //    maxLength/type changes, NOT NULL with a default no longer exempt). Already applied in production.
        ["20260624000003_AddStatusCheckConstraints"] = "AlterColumn text → varchar(40) on two status columns",
        ["20260624000004_AddYtdColumnPrecision"] = "AlterColumn numeric → numeric(14,2) on four YTD columns",
        ["20260713023035_AddMigrationImportBatchLedger"] = "AlterColumn text → varchar(80) and text → varchar(1000)",
        ["20260816013100_RepairMigrationModelParity"] = "raw SQL ALTER COLUMN wps_status TYPE",
        ["20260923045709_GosiContributionRuleRateToFraction"] = "AlterColumn numeric(7,4) → numeric(9,6) (a widening, but a type change)",
    };

    [Fact]
    public void NoNewMigrationIsDestructiveWithoutAContractPhaseMarker()
    {
        var offenders = new List<string>();
        foreach (var migration in Migrations())
        {
            if (Baseline.ContainsKey(migration.Id)) continue;
            var findings = Scan(migration.Code);
            if (findings.Count == 0) continue;
            if (HasContractPhaseMarker(migration.Code)) continue;
            offenders.Add($"{migration.Id}: {string.Join("; ", findings)}");
        }

        offenders.Should().BeEmpty(
            "a migration whose Up drops, renames, retypes or tightens a column breaks the old instances still " +
            "running during a rolling deploy. Ship it as the contract phase of expand → migrate → contract " +
            "and mark the class [ContractPhase(\"<release that stopped using it>\")] — see " +
            "docs/schema/HOW_TO_CHANGE_THE_SCHEMA.md.\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void BaselineOnlyNamesMigrationsThatStillExistAndAreStillDestructive()
    {
        var byId = Migrations().ToDictionary(m => m.Id, m => m.Code, StringComparer.Ordinal);
        var stale = Baseline.Keys
            .Where(id => !byId.TryGetValue(id, out var code) || Scan(code).Count == 0)
            .ToList();
        stale.Should().BeEmpty("the grandfather list may only shrink; remove entries that no longer apply");
        Baseline.Values.Should().OnlyContain(reason => !string.IsNullOrWhiteSpace(reason),
            "every grandfathered migration records why it trips the gate");
    }

    private static string Migration(string up, string down = "", string extra = "") =>
        "public partial class X : Migration {\n protected override void Up(MigrationBuilder migrationBuilder) {\n"
        + up + "\n }\n protected override void Down(MigrationBuilder migrationBuilder) {\n" + down + "\n }\n" + extra + "\n}";

    [Fact]
    public void ScannerFlagsEachDestructiveFormAndIgnoresSafeOnes()
    {
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
        Scan(Migration("migrationBuilder.Sql(\"ALTER TABLE t RENAME TO u;\");")).Should().ContainSingle();

        // Safe: additive Up with the drop only in Down; loosening; comments; dropping an index;
        // widening a varchar; a safe raw-SQL string held in a constant.
        Scan(Migration("migrationBuilder.AddColumn<string>(name: \"a\", table: \"t\", nullable: true);",
            "migrationBuilder.DropColumn(name: \"a\", table: \"t\");")).Should().BeEmpty();
        Scan(Migration("""
            migrationBuilder.AlterColumn<string>(name: "a", table: "t", nullable: true,
                oldClrType: typeof(string), oldNullable: false);
            """)).Should().BeEmpty();
        Scan(Migration("// migrationBuilder.DropColumn(name: \"a\", table: \"t\");")).Should().BeEmpty();
        Scan(Migration("migrationBuilder.Sql(\"DROP INDEX IF EXISTS ix_a;\");")).Should().BeEmpty();
        Scan(Migration("migrationBuilder.Sql(\"ALTER TABLE t RENAME CONSTRAINT a TO b;\");")).Should().BeEmpty();
        Scan(Migration("""
            migrationBuilder.AlterColumn<string>(name: "a", table: "t", type: "character varying(200)", maxLength: 200,
                nullable: true, oldClrType: typeof(string), oldType: "character varying(100)", oldMaxLength: 100, oldNullable: true);
            """)).Should().BeEmpty();
        Scan(Migration("""
            migrationBuilder.AlterColumn<string>(name: "a", table: "t", type: "text", nullable: true,
                oldClrType: typeof(string), oldType: "character varying(100)", oldMaxLength: 100, oldNullable: true);
            """)).Should().BeEmpty();
        Scan(Migration("migrationBuilder.Sql(UpSql);", extra:
            "private const string UpSql = \"CREATE INDEX ix_a ON t (a);\";")).Should().BeEmpty();
        Scan(Migration("migrationBuilder.Sql(\"SELECT 1\", suppressTransaction: true);")).Should().BeEmpty();

        ContractPhaseMarker().IsMatch("[ContractPhase(\"Release 41 stopped reading name_en\")]").Should().BeTrue();
        ContractPhaseMarker().IsMatch("[Zayra.Api.Infrastructure.Data.ContractPhase(\"r\")]").Should().BeTrue();
    }

    /// <summary>
    /// Each bypass the independent review of PR #172 found, as synthetic migration text. Every one of
    /// these passed the first version of the scanner.
    /// </summary>
    [Fact]
    public void ScannerCatchesEveryBypassTheReviewFound()
    {
        // 1. Destructive SQL in a class-level const, outside the Up..Down window.
        Scan(Migration("migrationBuilder.Sql(DropSql);", extra:
                "private const string DropSql = \"ALTER TABLE t DROP COLUMN a;\";"))
            .Should().ContainSingle(f => f.Contains("DROP COLUMN"));
        // 1b. A const declared above Up, and a helper method placed after Down.
        Scan("public partial class X : Migration {\n private const string S = @\"ALTER TABLE t RENAME COLUMN a TO b\";\n"
             + " protected override void Up(MigrationBuilder migrationBuilder) { migrationBuilder.Sql(S); Helper(migrationBuilder); }\n"
             + " protected override void Down(MigrationBuilder migrationBuilder) { }\n"
             + " private static void Helper(MigrationBuilder b) => b.DropTable(name: \"t\");\n}")
            .Should().HaveCount(2);
        // 1c. A SQL constant from another class: not readable here, so not trusted.
        Scan(Migration("migrationBuilder.Sql(SharedSql.DropLegacyColumns);"))
            .Should().ContainSingle(f => f.Contains("not declared in this migration"));

        // 2. RENAME without COLUMN, and ALTER COLUMN … TYPE (with and without SET DATA).
        Scan(Migration("migrationBuilder.Sql(\"ALTER TABLE t RENAME a TO b;\");")).Should().ContainSingle();
        Scan(Migration("migrationBuilder.Sql(\"ALTER TABLE \\\"t\\\" RENAME \\\"a\\\" TO \\\"b\\\";\");")).Should().ContainSingle();
        Scan(Migration("migrationBuilder.Sql(@\"ALTER TABLE \"\"t\"\" RENAME COLUMN \"\"a\"\" TO \"\"b\"\";\");")).Should().ContainSingle();
        Scan(Migration("migrationBuilder.Sql(\"ALTER TABLE t ALTER COLUMN a TYPE integer USING a::integer;\");")).Should().ContainSingle();
        Scan(Migration("migrationBuilder.Sql(\"ALTER TABLE t ALTER COLUMN a SET DATA TYPE bigint;\");")).Should().ContainSingle();

        // 3. AlterColumn that shrinks maxLength, or changes the store / CLR type.
        Scan(Migration("""
            migrationBuilder.AlterColumn<string>(name: "a", table: "t", type: "character varying(50)", maxLength: 50,
                nullable: true, oldClrType: typeof(string), oldType: "character varying(200)", oldMaxLength: 200, oldNullable: true);
            """)).Should().Contain(f => f.Contains("maxLength"));
        Scan(Migration("""
            migrationBuilder.AlterColumn<string>(name: "a", table: "t", type: "character varying(50)", maxLength: 50,
                nullable: true, oldClrType: typeof(string), oldType: "text", oldNullable: true);
            """)).Should().NotBeEmpty("text → varchar(50) truncates what old code writes");
        Scan(Migration("""
            migrationBuilder.AlterColumn<int>(name: "a", table: "t", type: "integer", nullable: false,
                oldClrType: typeof(long), oldType: "bigint");
            """)).Should().NotBeEmpty("bigint → integer is a type change old code cannot read");
        Scan(Migration("""
            migrationBuilder.AlterColumn<decimal>(name: "a", table: "t", type: "numeric(18,2)", nullable: false,
                oldClrType: typeof(decimal), oldType: "numeric(18,4)");
            """)).Should().NotBeEmpty("a precision/scale change is a type change");

        // 4. NOT NULL WITH a default is no longer exempt: old code can still write an explicit NULL.
        Scan(Migration("""
            migrationBuilder.AlterColumn<string>(name: "a", table: "t", nullable: false, defaultValue: "",
                oldClrType: typeof(string), oldNullable: true);
            """)).Should().ContainSingle(f => f.Contains("NOT NULL"));
        Scan(Migration("""
            migrationBuilder.AlterColumn<bool>(name: "a", table: "t", nullable: false, defaultValueSql: "false",
                oldClrType: typeof(bool), oldNullable: true);
            """)).Should().ContainSingle();

        // …and each is accepted only once the class is marked as the contract phase, with a reason.
        HasContractPhaseMarker("[ContractPhase(\"Release 2026.10.2 stopped writing NULL to t.a\")]\n"
            + Migration("migrationBuilder.Sql(\"ALTER TABLE t ALTER COLUMN a SET NOT NULL;\");")).Should().BeTrue();
        HasContractPhaseMarker("[ContractPhase(\"\")]\n" + Migration("")).Should().BeFalse("the reason is mandatory");
        HasContractPhaseMarker("// [ContractPhase(\"r\")]\n" + Migration("")).Should().BeFalse("a commented-out marker counts for nothing");
    }

    [Fact]
    public void AConstantUsedOnlyByDownIsStillScanned_ConservativeByDesign()
    {
        // The scanner cannot tell which method reads a class-level constant, so a destructive SQL
        // constant anywhere outside Down's body is flagged. Inline it into Down to make the intent clear.
        Scan(Migration("", "migrationBuilder.Sql(DownSql);",
                "private const string DownSql = \"ALTER TABLE t DROP COLUMN a;\";"))
            .Should().ContainSingle();
        Scan(Migration("", "migrationBuilder.Sql(\"ALTER TABLE t DROP COLUMN a;\");")).Should().BeEmpty();
        // Expression-bodied Down is removed too.
        Scan("public partial class X : Migration {\n protected override void Up(MigrationBuilder m) { }\n"
             + " protected override void Down(MigrationBuilder m) => m.DropTable(name: \"t\");\n}").Should().BeEmpty();
    }

    [Fact]
    public void TheScanActuallyReachesTheMigrations()
    {
        var migrations = Migrations().ToList();
        migrations.Should().HaveCountGreaterThan(50, "the guard must be scanning the real Migrations folder");
        migrations.Should().OnlyContain(m => UpSignature().IsMatch(m.Code), "every migration has an Up method the scanner can find");
        migrations.Should().OnlyContain(m => !DownSignature().IsMatch(m.Code) || WithoutDownBody(m.Code).Length < m.Code.Length,
            "the Down body was located and removed wherever a Down exists");
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

    private static bool HasContractPhaseMarker(string code)
    {
        var marker = ContractPhaseMarker().Match(SourceScan.StripCSharpComments(code));
        return marker.Success && !string.IsNullOrWhiteSpace(marker.Groups["reason"].Value);
    }

    /// <summary>The file with the body of <c>Down</c> (block or expression body) removed.</summary>
    private static string WithoutDownBody(string code)
    {
        var down = DownSignature().Match(code);
        if (!down.Success) return code;
        var paramsEnd = SourceScan.MatchingClose(code, down.Index + down.Length - 1, '(', ')');
        var i = paramsEnd + 1;
        while (i < code.Length && char.IsWhiteSpace(code[i])) i++;
        int end;
        if (i < code.Length && code[i] == '{') end = SourceScan.MatchingClose(code, i, '{', '}');
        else if (i + 1 < code.Length && code[i] == '=' && code[i + 1] == '>') end = SourceScan.NextTopLevel(code, i + 2, ';');
        else return code;
        return code[..down.Index] + code[Math.Min(code.Length, end + 1)..];
    }

    private static List<string> Scan(string code)
    {
        var findings = new List<string>();
        var stripped = SourceScan.StripCSharpComments(code);
        if (!UpSignature().IsMatch(stripped)) return findings;
        var scope = WithoutDownBody(stripped);

        foreach (Match m in DestructiveCall().Matches(scope))
            findings.Add(m.Groups["op"].Value);

        foreach (Match m in AlterColumnStart().Matches(scope))
        {
            var close = SourceScan.MatchingClose(scope, m.Index + m.Length - 1, '(', ')');
            findings.AddRange(AlterColumnFindings(m.Groups["clr"].Value, scope[(m.Index + m.Length)..close]));
        }

        foreach (Match m in DestructiveSql().Matches(scope))
            findings.Add($"raw SQL '{Regex.Replace(m.Value.ToUpperInvariant(), @"\s+", " ")}'");

        foreach (Match m in SqlCall().Matches(scope))
        {
            var close = SourceScan.MatchingClose(scope, m.Index + m.Length - 1, '(', ')');
            foreach (var name in UnresolvedSqlArguments(scope[(m.Index + m.Length)..close], scope))
                findings.Add($"Sql({name}): not declared in this migration, so the SQL it runs cannot be checked");
        }

        return findings;
    }

    private static IEnumerable<string> AlterColumnFindings(string clrType, string args)
    {
        if (Regex.IsMatch(args, @"\bnullable\s*:\s*false\b") && Regex.IsMatch(args, @"\boldNullable\s*:\s*true\b"))
            yield return "AlterColumn to NOT NULL (old code may still write NULL; a default does not stop an explicit NULL)";

        var type = StringArg(args, "type");
        var oldType = StringArg(args, "oldType");
        var maxLength = IntArg(args, "maxLength");
        var oldMaxLength = IntArg(args, "oldMaxLength");
        if (maxLength is { } max && oldMaxLength is { } oldMax && max < oldMax)
            yield return $"AlterColumn shrinks maxLength {oldMax} → {max}";
        else if (maxLength is { } bounded && oldMaxLength is null && (oldType is null || IsUnboundedText(oldType)))
            yield return $"AlterColumn bounds an unbounded column to maxLength {bounded}";

        if (type is not null && oldType is not null && !IsSameOrWidened(oldType, type))
            yield return $"AlterColumn changes type {oldType} → {type}";

        var oldClr = Regex.Match(args, @"\boldClrType\s*:\s*typeof\s*\(\s*(?<t>[^)]+?)\s*\)");
        if (clrType.Length > 0 && oldClr.Success && Normalise(oldClr.Groups["t"].Value) != Normalise(clrType))
            yield return $"AlterColumn changes CLR type {oldClr.Groups["t"].Value} → {clrType}";
    }

    /// <summary>True when <paramref name="to"/> holds every value <paramref name="from"/> could.</summary>
    private static bool IsSameOrWidened(string from, string to)
    {
        var a = ParseType(from);
        var b = ParseType(to);
        if (a == b) return true;
        var aIsVarchar = a.Base is "character varying" or "varchar";
        if (aIsVarchar && b.Base == "text") return true;
        if (aIsVarchar && b.Base == a.Base && int.TryParse(a.Args, out var oldLen)
            && (b.Args.Length == 0 || (int.TryParse(b.Args, out var newLen) && newLen >= oldLen)))
            return true;
        return false;
    }

    private static (string Base, string Args) ParseType(string type)
    {
        var t = Regex.Replace(type.Trim().ToLowerInvariant(), @"\s+", " ");
        var m = Regex.Match(t, @"^(?<base>[^(]+?)\s*(?:\((?<args>[^)]*)\))?$");
        return (m.Groups["base"].Value, Regex.Replace(m.Groups["args"].Value, @"\s", ""));
    }

    private static bool IsUnboundedText(string type) =>
        ParseType(type) is { Base: "text" } or { Base: "character varying" or "varchar", Args: "" };

    private static string Normalise(string clr) => Regex.Replace(clr, @"\s|System\.", "");

    private static int? IntArg(string args, string name)
    {
        var m = Regex.Match(args, $@"\b{name}\s*:\s*(?<v>\d+)");
        return m.Success ? int.Parse(m.Groups["v"].Value) : null;
    }

    private static string? StringArg(string args, string name)
    {
        var m = Regex.Match(args, $@"\b{name}\s*:\s*""(?<v>[^""]*)""");
        return m.Success ? m.Groups["v"].Value : null;
    }

    /// <summary>Identifiers in a Sql(...) argument that are not declared in this file.</summary>
    private static IEnumerable<string> UnresolvedSqlArguments(string args, string file)
    {
        var bare = SourceScan.BlankLiterals(args);
        bare = Regex.Replace(bare, @"\bsuppressTransaction\s*:\s*\w+", " ");
        foreach (Match id in Regex.Matches(bare, @"(?<![\w.])(?<name>[A-Za-z_]\w*)(?:\s*\.\s*[A-Za-z_]\w*)*"))
        {
            var first = id.Groups["name"].Value;
            if (first is "string" or "String" or "true" or "false" or "null") continue;
            if (Regex.IsMatch(file, $@"\b(?:const\s+string|readonly\s+string|string|var|class|static\s+\w+)\s+{Regex.Escape(first)}\b")) continue;
            yield return Regex.Replace(id.Value, @"\s", "");
        }
    }

    [GeneratedRegex(@"override\s+void\s+Up\s*\(")]
    private static partial Regex UpSignature();

    [GeneratedRegex(@"override\s+void\s+Down\s*\(")]
    private static partial Regex DownSignature();

    [GeneratedRegex(@"\.(?<op>DropColumn|DropTable|RenameColumn|RenameTable)\s*(<[^>]*>)?\s*\(")]
    private static partial Regex DestructiveCall();

    [GeneratedRegex(@"\.AlterColumn\s*<\s*(?<clr>[^>]*?)\s*>\s*\(")]
    private static partial Regex AlterColumnStart();

    [GeneratedRegex(@"\.Sql\s*\(")]
    private static partial Regex SqlCall();

    /// <summary>
    /// The identifier inside SQL held in a C# literal is plain, or quoted with \" or "" escapes.
    /// "RENAME CONSTRAINT a TO b" and "RENAME TO" are excluded from the "RENAME x TO" branch (the
    /// latter has its own).
    /// </summary>
    [GeneratedRegex(@"\bDROP\s+COLUMN\b|\bDROP\s+TABLE\b|\bRENAME\s+TO\b"
        + @"|\bRENAME\s+(?:COLUMN\s+)?(?!CONSTRAINT\b|TO\b)(?:\\?""{1,2}[^""\\]+\\?""{1,2}|\w+)\s+TO\b"
        + @"|\bALTER\s+COLUMN\s+(?:\\?""{1,2}[^""\\]+\\?""{1,2}|\w+)\s+(?:SET\s+DATA\s+)?TYPE\b"
        + @"|\bSET\s+NOT\s+NULL\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex DestructiveSql();

    [GeneratedRegex(@"\[\s*(?:[\w.]+\.)?ContractPhase(?:Attribute)?\s*\(\s*@?""(?<reason>[^""]*)""\s*\)\s*\]")]
    private static partial Regex ContractPhaseMarker();
}
