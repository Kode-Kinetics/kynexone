using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Zayra.Api.Data;

namespace Zayra.Api.Tests.FieldEncryption;

/// <summary>The field-encryption migrations were assembled by hand around the generated one (NOT VALID checks, a separate VALIDATE, indexes built concurrently), so these pin that the model snapshot still equals the model and that the lock-friendly shape holds.</summary>
public class FieldEncryptionMigrationTests
{
    private static string MigrationsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Zayra.Api", "Migrations"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "Zayra.Api", "Migrations");
    }

    [Fact]
    public void TheModelSnapshot_EqualsTheModel_ForTheFieldEncryptionTables()
    {
        using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql("Host=localhost;Database=none;Username=x;Password=x").Options);
        var differ = db.GetService<IMigrationsModelDiffer>();
        var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot!.Model;
        if (snapshot is IMutableModel mutable) snapshot = mutable.FinalizeModel();
        snapshot = db.GetService<IModelRuntimeInitializer>().Initialize(snapshot);
        var ops = differ.GetDifferences(snapshot.GetRelationalModel(), db.GetService<IDesignTimeModel>().Model.GetRelationalModel());
        var mine = ops.Where(o => Describe(o).Contains("field_encryption", StringComparison.Ordinal)
                                   || Describe(o).Contains("_enc", StringComparison.Ordinal) || Describe(o).Contains("_bidx", StringComparison.Ordinal)).Select(Describe).ToList();
        mine.Should().BeEmpty("a hand-edited snapshot must match the model; otherwise the next generated migration re-adds or drops these columns");
    }

    private static string Describe(MigrationOperation o) => o switch
    {
        ColumnOperation c => $"{o.GetType().Name} {c.Table}.{c.Name}",
        TableOperation t => $"{o.GetType().Name} {t.Name}",
        AddCheckConstraintOperation a => $"{o.GetType().Name} {a.Table}.{a.Name}",
        DropCheckConstraintOperation d => $"{o.GetType().Name} {d.Table}.{d.Name}",
        CreateIndexOperation i => $"{o.GetType().Name} {i.Table}.{i.Name}",
        DropIndexOperation i => $"{o.GetType().Name} {i.Table}.{i.Name}",
        _ => o.GetType().Name,
    };

    [Fact]
    public void ExistingTableChecks_AreAddedNotValid_AndValidatedSeparately()
    {
        var foundation = File.ReadAllText(Path.Combine(MigrationsDir(), "20261026151526_AddFieldEncryptionFoundation.cs"));
        foundation.Should().NotContain("AddCheckConstraint(", "ADD CHECK on a populated table scans it under ACCESS EXCLUSIVE");
        foundation.Should().Contain("NOT VALID").And.Contain("lock_timeout");
        foundation.Should().NotContain("CreateIndex(\n                name: \"ix_employees_bidx", "indexes on employees are built concurrently in their own migration");
        var validate = File.ReadAllText(Path.Combine(MigrationsDir(), "20261026151527_ValidateFieldEncryptionConstraints.cs"));
        System.Text.RegularExpressions.Regex.Matches(foundation, "NOT VALID").Count.Should()
            .Be(System.Text.RegularExpressions.Regex.Matches(validate, "VALIDATE CONSTRAINT").Count, "every NOT VALID constraint gets its VALIDATE");
    }

    [Fact]
    public void TheIndexMigration_IsConcurrent_AndNonTransactional()
    {
        var idx = File.ReadAllText(Path.Combine(MigrationsDir(), "20261026151528_AddFieldEncryptionBlindIndexes.cs"));
        var creates = System.Text.RegularExpressions.Regex.Matches(idx, "CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_employees_bidx_").Count;
        creates.Should().Be(8);
        System.Text.RegularExpressions.Regex.Matches(idx, "suppressTransaction: true").Count.Should().Be(idx.Split("migrationBuilder.Sql(").Length - 1, "every statement in it must opt out of the migration transaction");
        idx.Should().NotContain("CreateTable").And.NotContain("AddColumn", "CONCURRENTLY cannot share a transaction with anything else");
    }
}
