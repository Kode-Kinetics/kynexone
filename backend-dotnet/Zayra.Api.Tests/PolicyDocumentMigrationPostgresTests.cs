using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;

namespace Zayra.Api.Tests;

[Trait("Category", "Integration")]
public sealed class PolicyDocumentMigrationPostgresTests
{
    [Fact]
    public async Task UpgradeKeepsLegacyDocumentsPrivate_AndRollbackProtectsSourceHistory()
    {
        const string migrationId = "20261009225910_AddPolicyDocumentPublication";
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        var migrator = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
        var migrations = db.Database.GetMigrations().ToList();
        migrations.Should().Contain(migrationId);
        var previous = migrations[migrations.IndexOf(migrationId) - 1];
        await migrator.MigrateAsync(previous);
        var id = Guid.NewGuid(); var tenant = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO policy_documents (id,tenant_id,file_name,original_name,mime_type,file_size_bytes,status,
                chunk_count,created_at_utc,updated_at_utc,is_deleted)
            VALUES ({id},{tenant},'legacy.txt','legacy.txt','text/plain',10,'Ready',0,CURRENT_TIMESTAMP,CURRENT_TIMESTAMP,false)
            """);
        await migrator.MigrateAsync(migrationId);
        await migrator.MigrateAsync(migrationId);
        var legacy = await db.PolicyDocuments.SingleAsync(d => d.Id == id);
        legacy.Status.Should().Be("Ready");
        legacy.PublicationStatus.Should().Be("Draft");
        legacy.CompanyId.Should().BeNull();
        legacy.PublishedAtUtc.Should().BeNull();
        legacy.ContentSha256.Should().BeEmpty();
        legacy.ContentSha256 = new string('A', 64);
        await db.SaveChangesAsync();
        var refused = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => migrator.MigrateAsync(previous));
        refused.SqlState.Should().Be(Npgsql.PostgresErrorCodes.RaiseException);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE policy_documents SET content_sha256 = '' WHERE id = {id}");
        await migrator.MigrateAsync(previous);
        await migrator.MigrateAsync(migrationId);
        db.ChangeTracker.Clear();
        (await db.PolicyDocuments.SingleAsync(d => d.Id == id)).PublicationStatus.Should().Be("Draft");
    }
}
