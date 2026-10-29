using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Data;
using Xunit;

namespace Zayra.Api.Tests;

/// <summary>
/// The committed model snapshot must describe exactly what the entity model builds. Hand-edited
/// migrations drift silently otherwise, and the next generated migration then re-creates (or drops)
/// whatever was skipped.
/// </summary>
public class MigrationSnapshotTests
{
    [Fact]
    public void ModelSnapshot_MatchesTheEntityModel_NoPendingChanges()
    {
        using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=u;Password=p").Options);

        var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot;
        Assert.NotNull(snapshot);

        var initializer = db.GetService<IModelRuntimeInitializer>();
        var snapshotModel = initializer.Initialize(snapshot!.Model).GetRelationalModel();
        var currentModel = db.GetService<IDesignTimeModel>().Model.GetRelationalModel();

        var differ = db.GetService<IMigrationsModelDiffer>();
        var ops = differ.GetDifferences(snapshotModel, currentModel);

        Assert.True(ops.Count == 0,
            "Model snapshot has drifted from the entity model. Pending operations: "
            + string.Join("; ", ops.Select(Describe)));
    }

    private static string Describe(MigrationOperation op) => op switch
    {
        AddColumnOperation a => $"AddColumn {a.Table}.{a.Name}",
        DropColumnOperation d => $"DropColumn {d.Table}.{d.Name}",
        AlterColumnOperation a => $"AlterColumn {a.Table}.{a.Name}",
        CreateIndexOperation i => $"CreateIndex {i.Table}.{i.Name}",
        DropIndexOperation i => $"DropIndex {i.Table}.{i.Name}",
        CreateTableOperation t => $"CreateTable {t.Name}",
        DropTableOperation t => $"DropTable {t.Name}",
        AddForeignKeyOperation f => $"AddFk {f.Table}.{f.Name}",
        DropForeignKeyOperation f => $"DropFk {f.Table}.{f.Name}",
        _ => op.GetType().Name,
    };
}
