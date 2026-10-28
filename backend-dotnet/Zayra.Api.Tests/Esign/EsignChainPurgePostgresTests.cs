using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Zayra.Api.Application.Esign;
using Zayra.Api.Data;
using Zayra.Api.Data.Esign;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Infrastructure.Retention;
using Zayra.Api.Infrastructure.Retention.Rules;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Esign;

/// <summary>CD61: the hash chain after a lawful retention purge, and what a verify may write.</summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class EsignChainPurgePostgresTests(PostgresFixture fx)
{
    private async Task ExecAsync(string sql)
    {
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<IAsyncDisposable> WithTriggerAsync()
    {
        await ExecAsync(AckAppendOnlySql.CreateFunction);
        await ExecAsync(AckAppendOnlySql.CreateTrigger);
        return new Cleanup(() => ExecAsync(AckAppendOnlySql.Drop));
    }
    private sealed class Cleanup(Func<Task> action) : IAsyncDisposable { public async ValueTask DisposeAsync() => await action(); }

    private static byte[] Pdf(byte b) => [.. EsignFixture.PdfBytes, b];

    private async Task<(Guid Tenant, int[] Employees, Guid[] Acks, InMemoryDocumentStorage Storage)> ThreeSignedAsync()
    {
        await using var seed = fx.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(seed);
        var storage = new InMemoryDocumentStorage();
        var employees = new List<int>(); var acks = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var e = new Employee { TenantId = tenant, EmployeeCode = $"CP{i}-{Guid.NewGuid():N}"[..14], FullName = "Layla Hassan", EnglishName = "Layla Hassan", Status = "Active", JoiningDate = DateTime.UtcNow.Date };
            seed.Employees.Add(e); await seed.SaveChangesAsync(); employees.Add(e.Id);
            var bytes = Pdf((byte)i); var docId = Guid.NewGuid(); var key = $"storage/documents/{tenant:N}/{docId:N}.pdf";
            storage.Objects[key] = bytes;
            seed.EmployeeDocuments.Add(new EmployeeDocument { Id = docId, TenantId = tenant, EmployeeId = e.Id, DocumentType = "Employment contract", DocumentCategory = "Contract", FileName = "c.pdf", ContentType = "application/pdf", StorageUrl = key, ApprovalStatus = "Approved", SignatureRequiredAtUtc = DateTime.UtcNow });
            await seed.SaveChangesAsync();
            await using var db = fx.CreateDb();
            var u = Guid.NewGuid();
            var o = await new PolicySigningService(db, storage, new FakeStepUp()).SignAsync(new SignCommand(tenant, e.Id, u, u, null, docId,
                new EsignSignBody(EsignFixture.Sha(bytes), ConsentCatalog.Current, "Layla Hassan", "en", "pg"), Guid.NewGuid().ToString("N"), "10.1.1.1", "PgTest/1"), default);
            o.Status.Should().Be(201, o.Code); acks.Add(o.AckId!.Value);
        }
        return (tenant, [.. employees], [.. acks], storage);
    }

    private async Task<VerifyOutcome> VerifyAsync(Guid tenant, Guid ack, InMemoryDocumentStorage storage)
    {
        await using var db = fx.CreateDb();
        return await new PolicySigningService(db, storage, new FakeStepUp()).VerifyAsync(tenant, ack, null, default);
    }

    private async Task PurgeByRuleAsync(Guid tenant, int employee)
    {
        await using var db0 = fx.CreateDb();
        db0.EmployeeOffboardings.Add(new EmployeeOffboarding { TenantId = tenant, EmployeeId = employee, EmployeeName = "x", EmployeeCode = "x", Status = "Completed", LastWorkingDay = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-11)) });
        await db0.SaveChangesAsync();
        using var services = new ServiceCollection().BuildServiceProvider();
        await using var db = fx.CreateDb();
        var ctx = new JobExecutionContext(new(Guid.NewGuid(), tenant, "data-retention", "{}", 1, 1, Guid.NewGuid(), null), db, services, [], TimeSpan.FromMinutes(2), default, default);
        var rule = new EsignEvidenceRetentionRule();
        var candidates = await rule.EvaluateAsync(ctx, DateTime.UtcNow, 100, default);
        candidates.Should().HaveCount(1);
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            foreach (var c in candidates) await rule.ApplyAsync(ctx, c, DateTime.UtcNow, default);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        });
    }

    [Fact]
    public async Task VerifyAfterRetentionPurge_NextRecordStaysIntact_NoTamperAlarm()
    {
        await using var trigger = await WithTriggerAsync();
        var (tenant, employees, acks, storage) = await ThreeSignedAsync();
        await PurgeByRuleAsync(tenant, employees[0]);
        var second = await VerifyAsync(tenant, acks[1], storage);
        second.ChainIntact.Should().BeTrue("the missing predecessor was lawfully purged");
        second.Valid.Should().BeTrue();
        (await VerifyAsync(tenant, acks[2], storage)).Valid.Should().BeTrue();
        await using var db = fx.CreateDb();
        (await db.EmployeeSelfServiceAuditLogs.IgnoreQueryFilters().CountAsync(a => a.TenantId == tenant && a.Action.StartsWith("esign.") && a.Action.EndsWith("_failed"))).Should().Be(0);
    }

    [Fact]
    public async Task PredecessorRemovedOutsideThePurge_IsStillATamperAlarm()
    {
        var (tenant, _, acks, storage) = await ThreeSignedAsync();   // no trigger here: a privileged removal with no purge record
        await ExecAsync($"DELETE FROM employee_policy_acknowledgements WHERE id = '{acks[0]}'");
        (await VerifyAsync(tenant, acks[1], storage)).ChainIntact.Should().BeFalse();
    }

    [Fact]
    public async Task Verify_WritesAtMostOneTamperAudit_PerRecord()
    {
        var (tenant, _, acks, storage) = await ThreeSignedAsync();
        await ExecAsync($"UPDATE employee_policy_acknowledgements SET typed_name = 'Forged Name' WHERE id = '{acks[1]}'");
        for (var i = 0; i < 4; i++) (await VerifyAsync(tenant, acks[1], storage)).Valid.Should().BeFalse();
        await using var db = fx.CreateDb();
        (await db.EmployeeSelfServiceAuditLogs.IgnoreQueryFilters().CountAsync(a => a.TenantId == tenant && a.EntityId == acks[1].ToString() && a.Action == "esign.chain_failed")).Should().Be(1);
    }
}
