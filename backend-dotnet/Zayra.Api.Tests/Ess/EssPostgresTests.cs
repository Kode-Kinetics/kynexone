using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Zayra.Api.Data;
using Zayra.Api.Data.Ess;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Infrastructure.Retention;
using Zayra.Api.Infrastructure.Retention.Rules;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Ess;

/// <summary>P2 on real PostgreSQL: the partial unique index, the expand-only shape, and the 30-day purge of rejected submissions.</summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class EssPostgresTests(PostgresFixture fx)
{
    private static EmployeeProfileChangeRequest Request(Guid tenant, int employee, string? section, string status = "PendingHR", DateTime? created = null) => new()
    {
        TenantId = tenant, EmployeeId = employee, Section = section, Status = status, Reason = section is null ? "legacy" : "ESS section",
        RequestedChangesJson = """{"phone":"+966500000001"}""", CreatedAtUtc = created ?? DateTime.UtcNow,
    };

    [Fact]
    public async Task SectionPending_PartialUniqueIndex_ConcurrentSubmits_OneWins_OtherGets409()
    {
        await using var seed = fx.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(seed);
        var def = await fx.GetIndexDefinitionAsync(OnboardingP2EssModel.SectionPendingIndex);
        def.Should().NotBeNull(); def!.ToUpperInvariant().Should().Contain("UNIQUE").And.Contain("WHERE");
        def.Should().Contain("PendingHR");

        await using var a = fx.CreateDb(); await using var b = fx.CreateDb();
        a.EmployeeProfileChangeRequests.Add(Request(tenant, 7001, "bank"));
        b.EmployeeProfileChangeRequests.Add(Request(tenant, 7001, "bank"));
        var gate = new TaskCompletionSource();
        async Task<Exception?> Save(ZayraDbContext db) { await gate.Task; try { await db.SaveChangesAsync(); return null; } catch (Exception ex) { return ex; } }
        var t1 = Save(a); var t2 = Save(b);
        gate.SetResult();
        var results = await Task.WhenAll(t1, t2);
        results.Count(r => r is null).Should().Be(1, "exactly one concurrent submit wins");
        var loser = results.Single(r => r is not null)!;
        (loser is DbUpdateException { InnerException: PostgresException { SqlState: "23505" } }).Should().BeTrue(loser.ToString());

        // A different section, a different employee, or a decided request does not collide.
        await using var c = fx.CreateDb();
        c.EmployeeProfileChangeRequests.AddRange(Request(tenant, 7001, "person"), Request(tenant, 7002, "bank"), Request(tenant, 7001, "bank", "Rejected"), Request(tenant, 7001, "bank", "Approved"),
            Request(tenant, 7001, null), Request(tenant, 7001, null));
        await c.SaveChangesAsync();
    }

    [Fact]
    public async Task Migration_P2_ExpandOnly_BackfillNone_LegacyRequestStillApprovable()
    {
        await using var db = fx.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(db);
        // A legacy row (no section, no new columns set) stores and reads back exactly as before.
        var legacy = Request(tenant, 7100, null);
        db.EmployeeProfileChangeRequests.Add(legacy);
        await db.SaveChangesAsync();
        await using var read = fx.CreateDb();
        var loaded = await read.EmployeeProfileChangeRequests.IgnoreQueryFilters().SingleAsync(x => x.Id == legacy.Id);
        loaded.Section.Should().BeNull(); loaded.NoticeVersion.Should().BeNull(); loaded.StepUpAtUtc.Should().BeNull(); loaded.ReviewNote.Should().BeNull();
        loaded.Status = "Approved"; loaded.DecidedAtUtc = DateTime.UtcNow;   // the legacy approval writes only legacy columns
        await read.SaveChangesAsync();
        // The section vocabulary is a database rule, not only an application one.
        await using var bad = fx.CreateDb();
        bad.EmployeeProfileChangeRequests.Add(Request(tenant, 7101, "salary"));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => bad.SaveChangesAsync());
        (ex.InnerException as PostgresException)!.ConstraintName.Should().Be(OnboardingP2EssModel.SectionCheck);
        // Documents default to Clean, so every existing upload stays downloadable; the status set is closed.
        await using var docs = fx.CreateDb();
        var doc = new EmployeeDocument { TenantId = tenant, EmployeeId = 1, DocumentType = "Passport", FileName = "p.pdf" };
        docs.EmployeeDocuments.Add(doc);
        await docs.SaveChangesAsync();
        (await fx.CreateDb().EmployeeDocuments.IgnoreQueryFilters().SingleAsync(d => d.Id == doc.Id)).ScanStatus.Should().Be("Clean");
        await using var badDoc = fx.CreateDb();
        badDoc.EmployeeDocuments.Add(new EmployeeDocument { TenantId = tenant, EmployeeId = 1, DocumentType = "x", FileName = "x.pdf", ScanStatus = "Maybe" });
        var ex2 = await Assert.ThrowsAsync<DbUpdateException>(() => badDoc.SaveChangesAsync());
        (ex2.InnerException as PostgresException)!.ConstraintName.Should().Be(OnboardingP2EssModel.ScanStatusCheck);
        // The inbox index exists and is partial.
        (await fx.GetIndexDefinitionAsync(OnboardingP2EssModel.InboxIndex)).Should().Contain("WHERE");
    }

    [Fact]
    public async Task Retention_RejectedOlderThan30Days_Expired_AcceptedKept()
    {
        await using var seed = fx.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(seed);
        var now = DateTime.UtcNow;
        var oldRejected = Request(tenant, 7200, "bank", "Rejected", now.AddDays(-40)); oldRejected.DecidedAtUtc = now.AddDays(-35);
        var freshRejected = Request(tenant, 7201, "bank", "Rejected", now.AddDays(-10)); freshRejected.DecidedAtUtc = now.AddDays(-5);
        var oldAccepted = Request(tenant, 7202, "bank", "Approved", now.AddDays(-400)); oldAccepted.DecidedAtUtc = now.AddDays(-399);
        var oldLegacyRejected = Request(tenant, 7203, null, "Rejected", now.AddDays(-90)); oldLegacyRejected.DecidedAtUtc = now.AddDays(-80);
        var oldPending = Request(tenant, 7204, "person", "PendingHR", now.AddDays(-60));
        // Resubmitted after the rejection: the newer pending request supersedes it, but the rejected row is still old.
        seed.EmployeeProfileChangeRequests.AddRange(oldRejected, freshRejected, oldAccepted, oldLegacyRejected, oldPending);
        await seed.SaveChangesAsync();

        await using var sweepDb = fx.CreateDb();
        var ctx = new JobExecutionContext(new(Guid.NewGuid(), tenant, "data-retention", "{}", 1, 1, Guid.NewGuid(), null),
            sweepDb, Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(new Microsoft.Extensions.DependencyInjection.ServiceCollection()), [], TimeSpan.FromMinutes(2), default, default);
        var rule = new EssCompletionRejectedRetentionRule();
        rule.RuleKey.Should().Be(RetentionRuleKeys.EssCompletionRejectedExpired);
        RetentionRuleKeys.All.Should().Contain(RetentionRuleKeys.EssCompletionRejectedExpired);
        var candidates = await rule.EvaluateAsync(ctx, now, 100, CancellationToken.None);
        candidates.Select(c => Guid.Parse(c.EntityId)).Should().BeEquivalentTo([oldRejected.Id], "only section requests rejected more than 30 days ago");
        foreach (var c in candidates) { c.Disposition.Should().Be(RetentionDispositions.Anonymise); await rule.ApplyAsync(ctx, c, now, CancellationToken.None); }

        await using var verify = fx.CreateDb();
        var rows = await verify.EmployeeProfileChangeRequests.IgnoreQueryFilters().Where(x => x.TenantId == tenant).ToDictionaryAsync(x => x.Id);
        rows[oldRejected.Id].Status.Should().Be("Expired"); rows[oldRejected.Id].RequestedChangesJson.Should().Be("{}");
        rows[freshRejected.Id].Status.Should().Be("Rejected"); rows[freshRejected.Id].RequestedChangesJson.Should().Contain("phone");
        rows[oldAccepted.Id].Status.Should().Be("Approved"); rows[oldAccepted.Id].RequestedChangesJson.Should().Contain("phone");
        rows[oldLegacyRejected.Id].Status.Should().Be("Rejected");
        rows[oldPending.Id].Status.Should().Be("PendingHR");
    }

    [Fact]
    public async Task BankSection_ConcurrentAccepts_OnlyOneClaimsTheRequest()  // CD59
    {
        await using var seed = fx.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(seed);
        var req = Request(tenant, 7300, "bank");
        seed.EmployeeProfileChangeRequests.Add(req);
        await seed.SaveChangesAsync();
        await using var a = fx.CreateDb(); await using var b = fx.CreateDb();
        var gate = new TaskCompletionSource();
        async Task<bool> Claim(ZayraDbContext db) { await gate.Task; return await Zayra.Api.Application.Employees.Ess.EssSectionDecisionService.TryClaimForFinanceAsync(db, tenant, req.Id, default); }
        var t1 = Claim(a); var t2 = Claim(b);
        gate.SetResult();
        (await Task.WhenAll(t1, t2)).Count(x => x).Should().Be(1);
        (await fx.CreateDb().EmployeeProfileChangeRequests.IgnoreQueryFilters().SingleAsync(x => x.Id == req.Id)).Status.Should().Be("AwaitingFinance");
    }

    [Fact]
    public async Task Retention_RejectedSubmissionFilesAndStaleUploads_AreDeletedWithTheirStoredObjects()  // CD62
    {
        await using var seed = fx.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(seed);
        var now = DateTime.UtcNow;
        var storage = new Zayra.Api.Tests.Esign.InMemoryDocumentStorage();
        EmployeeDocument Doc(string name, DateTime uploaded)
        {
            var key = $"storage/documents/{tenant:N}/{name}.pdf"; storage.Objects[key] = [1, 2, 3];
            return new EmployeeDocument { TenantId = tenant, EmployeeId = 7400, DocumentType = "National ID", DocumentCategory = "national_id_copy", FileName = name + ".pdf", StorageUrl = key, SourceSection = "documents", ScanStatus = "clean", UploadedAtUtc = uploaded };
        }
        var rejectedFile = Doc("rejected", now.AddDays(-45)); var staleUpload = Doc("stale", now.AddDays(-45)); var freshUpload = Doc("fresh", now.AddDays(-2));
        var acceptedFile = Doc("accepted", now.AddDays(-45)); var freshRejectedFile = Doc("freshrejected", now.AddDays(-12));
        seed.EmployeeDocuments.AddRange(rejectedFile, staleUpload, freshUpload, acceptedFile, freshRejectedFile);
        EmployeeProfileChangeRequest WithFiles(string status, DateTime decided, params EmployeeDocument[] docs)
        {
            var r = Request(tenant, 7400 + docs.Length + status.Length, "documents", status, decided.AddDays(-1)); r.DecidedAtUtc = decided;
            r.RequestedChangesJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["_documents"] = docs.Select(d => d.Id.ToString()).ToArray() });
            return r;
        }
        var oldRejected = WithFiles("Rejected", now.AddDays(-35), rejectedFile);
        var freshRejected = WithFiles("Rejected", now.AddDays(-5), freshRejectedFile); freshRejected.EmployeeId = 7499;
        var accepted = WithFiles("Approved", now.AddDays(-40), acceptedFile); accepted.EmployeeId = 7498;
        seed.EmployeeProfileChangeRequests.AddRange(oldRejected, freshRejected, accepted);
        await seed.SaveChangesAsync();

        using var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddSingleton<Zayra.Api.Infrastructure.Documents.IDocumentStorage>(storage).BuildServiceProvider();
        await using var db = fx.CreateDb();
        var ctx = new JobExecutionContext(new(Guid.NewGuid(), tenant, "data-retention", "{}", 1, 1, Guid.NewGuid(), null), db, services, [], TimeSpan.FromMinutes(2), default, default);
        var rule = new EssCompletionRejectedRetentionRule();
        var candidates = await rule.EvaluateAsync(ctx, now, 100, CancellationToken.None);
        foreach (var c in candidates) await rule.ApplyAsync(ctx, c, now, CancellationToken.None);

        await using var verify = fx.CreateDb();
        var left = (await verify.EmployeeDocuments.IgnoreQueryFilters().Where(d => d.TenantId == tenant).Select(d => d.FileName).ToListAsync()).Order().ToList();
        left.Should().Equal("accepted.pdf", "fresh.pdf", "freshrejected.pdf");
        storage.Objects.Keys.Select(k => Path.GetFileNameWithoutExtension(k)).Order().Should().Equal("accepted", "fresh", "freshrejected");
    }
}
