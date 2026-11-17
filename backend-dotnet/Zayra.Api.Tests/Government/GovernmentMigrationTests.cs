using System.Text.RegularExpressions;
using FluentAssertions;
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests.Government;

/// <summary>
/// CTO gate C14: the foundation's one migration is additive only (two new tables, their indexes and checks), touches no existing
/// table, backfills nothing, and its id sorts after every other migration.
/// </summary>
public sealed partial class GovernmentMigrationTests
{
    private const string Name = "AddGovernmentCredentialsAndExchanges";

    [GeneratedRegex(@"^(?<id>\d{14})_(?<name>\w+)\.cs$")]
    private static partial Regex MigrationFile();

    private static (string Id, string Up, string Down) Load()
    {
        var dir = Path.Combine(SourceScan.ResolveApiRoot(), "Migrations");
        var files = Directory.GetFiles(dir, "*.cs").Select(Path.GetFileName).Select(f => MigrationFile().Match(f!)).Where(m => m.Success).ToList();
        var mine = files.Where(m => m.Groups["name"].Value == Name).ToList();
        mine.Should().ContainSingle($"exactly one {Name} migration exists");
        var id = mine[0].Groups["id"].Value;
        files.Select(m => m.Groups["id"].Value).Where(other => other != id)
            .Should().OnlyContain(other => string.CompareOrdinal(other, id) < 0, "the migration id must sort after every other migration");

        var code = File.ReadAllText(Path.Combine(dir, $"{id}_{Name}.cs"));
        var upStart = code.IndexOf("protected override void Up(", StringComparison.Ordinal);
        var downStart = code.IndexOf("protected override void Down(", StringComparison.Ordinal);
        upStart.Should().BePositive();
        downStart.Should().BeGreaterThan(upStart);
        return (id, code[upStart..downStart], code[downStart..]);
    }

    [Fact]
    public void TheMigration_IsAdditive_AndSortsLast()
    {
        var (_, up, down) = Load();

        Regex.Matches(up, @"CreateTable\(\s*name: ""(?<t>[a-z_]+)""").Select(m => m.Groups["t"].Value).Distinct()
            .Should().BeEquivalentTo(new[] { "government_credentials", "government_exchanges" }, "Up creates the two tables and nothing else");
        up.Should().NotContainAny("DropTable", "DropColumn", "AlterColumn", "RenameColumn", "RenameTable", "DropIndex", "AddColumn", "migrationBuilder.Sql(",
            "UpdateData", "InsertData", "DeleteData");
        Regex.Matches(up, @"\btable: ""(?<t>[a-z_]+)""").Select(m => m.Groups["t"].Value).Distinct()
            .Should().OnlyContain(t => t == "government_credentials" || t == "government_exchanges", "indexes only on the new tables");
        Regex.Matches(down, @"DropTable\(\s*name: ""(?<t>[a-z_]+)""").Select(m => m.Groups["t"].Value)
            .Should().BeEquivalentTo(new[] { "government_credentials", "government_exchanges" });
        down.Should().NotContainAny("AddColumn", "AlterColumn", "CreateTable", "migrationBuilder.Sql(");
    }
}
