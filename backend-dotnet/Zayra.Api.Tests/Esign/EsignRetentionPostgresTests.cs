using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Zayra.Api.Application.Esign;
using Zayra.Api.Data;
using Zayra.Api.Data.Esign;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Infrastructure.Retention;
using Zayra.Api.Infrastructure.Retention.Rules;
using Zayra.Api.Models;
using Zayra.Api.Tests.Platform;

namespace Zayra.Api.Tests.Esign;

/// <summary>
/// The append-only trigger refuses every DELETE outside the retention purge. These prove the two things that legitimately delete acknowledgements
/// still work with the trigger installed: erasing a soft-deleted tenant (the retention job and the platform's manual purge), and the CD52 retention
/// rule (employment + 10 years) which removes only records past it. An ordinary application delete is still refused.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class EsignRetentionPostgresTests(PostgresFixture fx) : PlatformTestBase
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

    private sealed record Seeded(Guid Tenant, int EmployeeId, Guid AckId, Guid CertId);

    /// <summary>An employee with a signed acknowledgement and its certificate document. <paramref name="deletedAt"/> soft-deletes the employee.</summary>
    private async Task<Seeded> SeedAsync(Guid? tenant = null, DateTime? deletedAt = null, long seq = 1, bool recordOffboarding = true)
    {
        await using var db = fx.CreateDb();
        tenant ??= await PostgresFixture.SeedMinimalTenant(db);
        var e = new Employee { TenantId = tenant.Value, EmployeeCode = $"R{Guid.NewGuid():N}"[..12], FullName = "Layla Hassan", EnglishName = "Layla Hassan", Status = "Active", JoiningDate = DateTime.UtcNow.Date };
        if (deletedAt is not null) { e.IsDeleted = true; e.DeletedAtUtc = deletedAt; e.Status = "Terminated"; }
        db.Employees.Add(e);
        await db.SaveChangesAsync();
        // End of employment is the completed offboarding's last working day (CD64), not the moment the record was removed.
        if (deletedAt is not null && recordOffboarding)
            db.EmployeeOffboardings.Add(new EmployeeOffboarding { TenantId = tenant.Value, EmployeeId = e.Id, EmployeeName = e.FullName, EmployeeCode = e.EmployeeCode, Status = "Completed", LastWorkingDay = DateOnly.FromDateTime(deletedAt.Value) });
        var cert = new EmployeeDocument { TenantId = tenant.Value, EmployeeId = e.Id, DocumentType = EsignConstants.DocumentType, DocumentCategory = "Acknowledgement", FileName = "c.pdf", ContentType = "application/pdf", StorageUrl = $"storage/documents/{tenant.Value:N}/esign/{Guid.NewGuid():N}.pdf" };
        db.EmployeeDocuments.Add(cert);
        await db.SaveChangesAsync();
        var ack = new EmployeePolicyAcknowledgement
        {
            TenantId = tenant.Value, EmployeeId = e.Id, PolicyId = Guid.NewGuid(), UserId = Guid.NewGuid(), Method = "esign_ess", DocumentSha256 = new string('a', 64), TypedName = "Layla Hassan",
            IpAddress = "10.0.0.1", RecordHash = new string('b', 64), PrevHash = EsignConstants.ZeroHash, ChainSeq = seq, StepUpJti = Guid.NewGuid(), CertificateDocumentId = cert.Id,
        };
        db.EmployeePolicyAcknowledgements.Add(ack);
        await db.SaveChangesAsync();
        return new Seeded(tenant.Value, e.Id, ack.Id, cert.Id);
    }

    private async Task SoftDeleteTenantAsync(Guid tenantId)
    {
        await using var db = fx.CreateDb();
        var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId);
        tenant.IsActive = false; tenant.Slug += SoftDeletedTenantRule.DeletedSlugMarker + "esign-test"; tenant.SoftDeletedAtUtc = DateTime.UtcNow.AddDays(-200);
        await db.SaveChangesAsync();
    }

    private async Task<(int Acks, int Certs)> CountsAsync(Guid tenant)
    {
        await using var db = fx.CreateDb();
        return (await db.EmployeePolicyAcknowledgements.IgnoreQueryFilters().CountAsync(a => a.TenantId == tenant),
                await db.EmployeeDocuments.IgnoreQueryFilters().CountAsync(d => d.TenantId == tenant && d.DocumentType == EsignConstants.DocumentType));
    }

    [Fact]
    public async Task OrdinaryAppDelete_IsStillRefused_EvenForAnExpiredRecord()
    {
        await using var trigger = await WithTriggerAsync();
        var s = await SeedAsync(deletedAt: DateTime.UtcNow.AddYears(-20));
        await using var db = fx.CreateDb();
        await Assert.ThrowsAnyAsync<Exception>(() => db.EmployeePolicyAcknowledgements.IgnoreQueryFilters().Where(a => a.Id == s.AckId).ExecuteDeleteAsync());
        (await CountsAsync(s.Tenant)).Acks.Should().Be(1);
    }

    [Fact]
    public async Task PurgeScope_NeedsATransaction_AndOnlyLastsForIt()
    {
        await using var trigger = await WithTriggerAsync();
        var s = await SeedAsync();
        await using var db = fx.CreateDb();
        await Assert.ThrowsAsync<InvalidOperationException>(() => RetentionPurgeScope.EnableInCurrentTransactionAsync(db, default));
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await RetentionPurgeScope.EnableInCurrentTransactionAsync(db, default);
            await tx.CommitAsync();
        });
        // the flag did not outlive the transaction
        await Assert.ThrowsAnyAsync<Exception>(() => db.EmployeePolicyAcknowledgements.IgnoreQueryFilters().Where(a => a.Id == s.AckId).ExecuteDeleteAsync());
    }

    [Fact]
    public async Task TenantErasure_ByTheRetentionRule_SucceedsWithAcknowledgementsPresent()
    {
        await using var trigger = await WithTriggerAsync();
        var s = await SeedAsync();
        var other = await SeedAsync(seq: 1);   // another tenant keeps its evidence
        await SoftDeleteTenantAsync(s.Tenant);
        using var services = new ServiceCollection().BuildServiceProvider();
        await using var db = fx.CreateDb();
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var ctx = new JobExecutionContext(new(Guid.NewGuid(), s.Tenant, "data-retention", "{}", 1, 1, Guid.NewGuid(), null), db, services, [], TimeSpan.FromMinutes(2), default, default);
            var rule = new SoftDeletedTenantRule(new() { ApplyDeletions = true, AllowTenantErasure = true });
            await rule.ApplyAsync(ctx, new("esign-erasure", "Tenant", s.Tenant.ToString(), "HardDelete", "Expired recovery window"), DateTime.UtcNow, default);
            await db.SaveChangesAsync(); await tx.CommitAsync();
        });
        (await CountsAsync(s.Tenant)).Should().Be((0, 0));
        (await CountsAsync(other.Tenant)).Should().Be((1, 1));
    }

    [Fact]
    public async Task TenantErasure_ByThePlatformPurge_SucceedsWithAcknowledgementsPresent()
    {
        await using var trigger = await WithTriggerAsync();
        var s = await SeedAsync();
        await SoftDeleteTenantAsync(s.Tenant);
        using var services = new ServiceCollection().AddSingleton<IDocumentStorage>(new InMemoryDocumentStorage()).BuildServiceProvider();
        await using var db = fx.CreateDb();
        var controller = CreateController(db); controller.ControllerContext.HttpContext.RequestServices = services;
        var result = await controller.PurgeTenant(s.Tenant, "PURGE", default);
        result.Should().BeOfType<OkObjectResult>();
        ((OkObjectResult)result).Value!.ToString().Should().NotContain("incomplete");
        (await CountsAsync(s.Tenant)).Should().Be((0, 0));
    }

    [Fact]
    public async Task RetentionRule_DeletesOnlyEvidencePastEmploymentPlusTenYears()
    {
        await using var trigger = await WithTriggerAsync();
        await using var seedDb = fx.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(seedDb);
        var past = await SeedAsync(tenant, DateTime.UtcNow.AddYears(-11), 1);          // left 11 years ago: due
        var inside = await SeedAsync(tenant, DateTime.UtcNow.AddYears(-9), 2);         // left 9 years ago: kept
        var employed = await SeedAsync(tenant, null, 3);                               // still employed: kept
        EsignEvidenceRetentionRule.Years.Should().Be(EsignConstants.RetentionYearsAfterEmployment);
        using var services = new ServiceCollection().BuildServiceProvider();
        await using var db = fx.CreateDb();
        var ctx = new JobExecutionContext(new(Guid.NewGuid(), tenant, "data-retention", "{}", 1, 1, Guid.NewGuid(), null), db, services, [], TimeSpan.FromMinutes(2), default, default);
        var rule = new EsignEvidenceRetentionRule();
        rule.RuleKey.Should().Be(RetentionRuleKeys.EsignEvidenceExpired);
        RetentionRuleKeys.All.Should().Contain(RetentionRuleKeys.EsignEvidenceExpired);
        var candidates = await rule.EvaluateAsync(ctx, DateTime.UtcNow, 100, default);
        candidates.Select(c => Guid.Parse(c.EntityId)).Should().BeEquivalentTo([past.AckId], "non-vacuous: exactly the one past retention is found");
        candidates.Should().OnlyContain(c => c.Disposition == RetentionDispositions.HardDelete);
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            foreach (var c in candidates) await rule.ApplyAsync(ctx, c, DateTime.UtcNow, default);
            await tx.CommitAsync();
        });
        await using var verify = fx.CreateDb();
        var left = await verify.EmployeePolicyAcknowledgements.IgnoreQueryFilters().Where(a => a.TenantId == tenant).Select(a => a.Id).ToListAsync();
        left.Should().BeEquivalentTo([inside.AckId, employed.AckId]);
        (await verify.EmployeeDocuments.IgnoreQueryFilters().AnyAsync(d => d.Id == past.CertId)).Should().BeFalse("its certificate goes with it");
        (await verify.EmployeeDocuments.IgnoreQueryFilters().AnyAsync(d => d.Id == inside.CertId)).Should().BeTrue();
        // a record that is not due is not deleted even if a candidate for it is forged: Apply re-checks
        await using var d2 = fx.CreateDb();
        await d2.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await d2.Database.BeginTransactionAsync();
            var c2 = new JobExecutionContext(new(Guid.NewGuid(), tenant, "data-retention", "{}", 1, 1, Guid.NewGuid(), null), d2, services, [], TimeSpan.FromMinutes(2), default, default);
            await rule.ApplyAsync(c2, new($"x:{inside.AckId}", "EmployeePolicyAcknowledgement", inside.AckId.ToString(), RetentionDispositions.HardDelete, "forced"), DateTime.UtcNow, default);
            await tx.CommitAsync();
        });
        (await CountsAsync(tenant)).Acks.Should().Be(2);
    }

    [Fact]
    public async Task RetentionClock_StartsAtLastWorkingDay_NotAtSoftDelete()  // CD64
    {
        await using var trigger = await WithTriggerAsync();
        await using var seedDb = fx.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(seedDb);
        var removedLongAgoNoExitRecord = await SeedAsync(tenant, DateTime.UtcNow.AddYears(-20), 1, recordOffboarding: false);   // record removed, end of employment unknown: kept
        var leftRecently = await SeedAsync(tenant, DateTime.UtcNow.AddYears(-1), 2);
        // soft-deleted only last year, but the person's last working day was 11 years ago
        var leftLongAgo = await SeedAsync(tenant, DateTime.UtcNow.AddYears(-1), 3);
        await using (var db0 = fx.CreateDb())
        {
            var off = await db0.EmployeeOffboardings.IgnoreQueryFilters().SingleAsync(o => o.EmployeeId == leftLongAgo.EmployeeId);
            off.LastWorkingDay = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-11));
            var cancelled = await SeedAsync(tenant, DateTime.UtcNow.AddYears(-15), 4);
            (await db0.EmployeeOffboardings.IgnoreQueryFilters().SingleAsync(o => o.EmployeeId == cancelled.EmployeeId)).Status = "Cancelled";
            await db0.SaveChangesAsync();
        }
        using var services = new ServiceCollection().BuildServiceProvider();
        await using var db = fx.CreateDb();
        var ctx = new JobExecutionContext(new(Guid.NewGuid(), tenant, "data-retention", "{}", 1, 1, Guid.NewGuid(), null), db, services, [], TimeSpan.FromMinutes(2), default, default);
        var candidates = await new EsignEvidenceRetentionRule().EvaluateAsync(ctx, DateTime.UtcNow, 100, default);
        candidates.Select(c => Guid.Parse(c.EntityId)).Should().BeEquivalentTo([leftLongAgo.AckId]);
        removedLongAgoNoExitRecord.AckId.Should().NotBeEmpty(); leftRecently.AckId.Should().NotBeEmpty();
    }
}
