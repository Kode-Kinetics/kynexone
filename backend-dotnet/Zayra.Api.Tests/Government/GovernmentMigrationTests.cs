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
        // It sorts after every migration that existed when it was added. A migration added later may sort after it, but only
        // ones named here on purpose (the benefit usage ledger and its approver grant), so an older-dated migration cannot slip in unnoticed.
        string[] addedAfter = ["20261118030000", "20261118040000"];
        files.Select(m => m.Groups["id"].Value).Where(other => other != id && !addedAfter.Contains(other))
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

    private const string BodyCheck = "body IN ('qiwa','gosi','muqeem','takamol','bank_anb','bank_file')";

    [Fact]
    public void TheExchangeBodyCheck_AllowsTheGenericBankFileChannel_TheSameInTheMigrationItsDesignerTheSnapshotAndTheModel()
    {
        var (id, up, _) = Load();
        var dir = Path.Combine(SourceScan.ResolveApiRoot(), "Migrations");
        up.Should().Contain($"\"ck_government_exchanges__body\", \"{BodyCheck}\"");
        File.ReadAllText(Path.Combine(dir, $"{id}_{Name}.Designer.cs")).Should().Contain($"\"ck_government_exchanges__body\", \"{BodyCheck}\"");
        File.ReadAllText(Path.Combine(dir, "ZayraDbContextModelSnapshot.cs")).Should().Contain($"\"ck_government_exchanges__body\", \"{BodyCheck}\"");
        File.ReadAllText(Path.Combine(SourceScan.ResolveApiRoot(), "Data", "ZayraDbContext.cs")).Should().Contain($"\"ck_government_exchanges__body\", \"{BodyCheck}\"");
    }

    [Fact]
    public void TheCredentialTable_KeepsARevokedRow_OnlyWithItsSecretWiped()
    {
        var (_, up, _) = Load();
        up.Should().Contain("revoked_at_utc").And.Contain("revoked_by");
        up.Should().Contain("\"ck_government_credentials__revoked\", \"(revoked_at_utc IS NULL) = (revoked_by IS NULL)\"");
        // An empty ciphertext is allowed only on a revoked row, and a revoked row holds no secret.
        up.Should().Contain("CASE WHEN revoked_at_utc IS NOT NULL THEN octet_length(ciphertext) = 0 WHEN octet_length(ciphertext) >= 30 "
            + "THEN get_byte(ciphertext, 0) IN (1, 2) ELSE false END");
    }
}
