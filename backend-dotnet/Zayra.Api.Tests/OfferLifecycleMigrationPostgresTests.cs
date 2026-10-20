using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class OfferLifecycleMigrationPostgresTests
{
    [Fact]
    public async Task DisposablePredecessorUpgradeTwicePreservesLegacyAndEnforcesScopedImmutableGraphAndErasureOrdering()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(container.GetConnectionString()).AddInterceptors(RowLockingInterceptor.Instance).Options);
        var migrator = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
        // The shared erasure graph includes dispatch children after the additive Task3 upgrade.
        var migration = db.Database.GetMigrations().Single(m => m.EndsWith("_OfferCompensationHandoff", StringComparison.Ordinal));
        await migrator.MigrateAsync("20261019043220_OfferLetterDesigner");
        var legacyTenant = Guid.NewGuid(); var legacyApplication = Guid.NewGuid(); var legacyOffer = Guid.NewGuid();
        var legacySent = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO job_applications(id,tenant_id,job_opening_id,job_title,candidate_id,candidate_name,candidate_email,
              stage,stage_order,status,rejection_reason,offered_salary,applied_at_utc)
            VALUES ({legacyApplication},{legacyTenant},{Guid.NewGuid()},'Legacy role',{Guid.NewGuid()},'Legacy Candidate',
              'legacy@example.invalid','Offer',5,'Active','',1234.56,{legacySent});
            INSERT INTO offer_letters(id,tenant_id,application_id,candidate_name,offered_job_title,offered_department,
              start_date,basic_salary,housing_allowance,transport_allowance,other_allowances,gross_salary,probation_months,
              content_html,status,generated_at_utc,sent_at_utc,decline_reason)
            VALUES ({legacyOffer},{legacyTenant},{legacyApplication},'Legacy Candidate','Legacy role','People',
              DATE '2026-10-01',1000,200,30,4.56,1234.56,3,'<p>Legacy</p>','Sent',{legacySent},{legacySent},'');
            """);
        await migrator.MigrateAsync(migration); await migrator.MigrateAsync(migration);
        var legacy = await db.OfferLetters.AsNoTracking().SingleAsync(o => o.Id == legacyOffer);
        legacy.CompanyId.Should().BeNull(); legacy.SeriesId.Should().BeNull(); legacy.ArtifactId.Should().BeNull();
        legacy.FrozenAtUtc.Should().BeNull(); legacy.CompensationSnapshotJson.Should().BeNull();
        legacy.Status.Should().Be("Sent"); legacy.SentAtUtc.Should().Be(legacySent); legacy.GrossSalary.Should().Be(1234.56m);
        var missing = new List<string>();
        if (legacy.Version != 1) missing.Add("legacy version starts at one");

        var tenant = await PostgresFixture.SeedMinimalTenant(db);
        var a = new Company { TenantId = tenant }; var b = new Company { TenantId = tenant };
        var app = new JobApplication { TenantId = tenant, CompanyId = a.Id, CandidateId = Guid.NewGuid(), JobOpeningId = Guid.NewGuid() };
        var otherApp = new JobApplication { TenantId = tenant, CompanyId = b.Id, CandidateId = Guid.NewGuid(), JobOpeningId = Guid.NewGuid() };
        var sameCompanyOtherApp = new JobApplication { TenantId = tenant, CompanyId = a.Id, CandidateId = Guid.NewGuid(), JobOpeningId = Guid.NewGuid() };
        db.Companies.AddRange(a, b); db.JobApplications.AddRange(app, otherApp, sameCompanyOtherApp); await db.SaveChangesAsync();
        var series = new OfferSeries { TenantId = tenant, CompanyId = a.Id, ApplicationId = app.Id };
        var offer = new OfferLetter { TenantId = tenant, CompanyId = a.Id, ApplicationId = app.Id,
            SeriesId = series.Id, RevisionNumber = 1, StartDate = new(2027, 1, 1) };
        var otherOffer = new OfferLetter { TenantId = tenant, CompanyId = b.Id, ApplicationId = otherApp.Id, StartDate = new(2027, 1, 1) };
        db.OfferSeries.Add(series); db.OfferLetters.AddRange(offer, otherOffer); await db.SaveChangesAsync();
        var artifact = new OfferArtifact { TenantId = tenant, CompanyId = a.Id, ApplicationId = app.Id, OfferId = offer.Id,
            StorageKey = $"{tenant:N}/migration-original.pdf", Sha256 = new string('a', 64), SizeBytes = 3 };
        db.OfferArtifacts.Add(artifact); await db.SaveChangesAsync();
        offer.ArtifactId = artifact.Id; offer.FrozenAtUtc = DateTime.UtcNow;
        artifact.State = "Finalized"; artifact.FinalizedAtUtc = DateTime.UtcNow;
        app.CurrentIssuedOfferId = offer.Id; app.OfferReservationId = Guid.NewGuid(); app.OfferReservationOfferId = offer.Id;
        app.OfferReservationState = "Unknown"; app.OfferReservationVersion = app.Version; series.LegacyRootOfferId = offer.Id;
        var evidence = new OfferLifecycleEvent { TenantId = tenant, CompanyId = a.Id, ApplicationId = app.Id, OfferId = offer.Id, EventType = "Frozen" };
        db.OfferLifecycleEvents.Add(evidence); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        async Task Refused(string label, FormattableString sql, string state)
        {
            try { await db.Database.ExecuteSqlInterpolatedAsync(sql); missing.Add(label); }
            catch (PostgresException e) { e.SqlState.Should().Be(state, label); }
        }
        await Refused("series company/application", $"INSERT INTO offer_series(id,tenant_id,company_id,application_id,next_revision_number,version) VALUES({Guid.NewGuid()},{tenant},{b.Id},{app.Id},2,1)", PostgresErrorCodes.ForeignKeyViolation);
        await Refused("artifact company/offer", $"INSERT INTO offer_artifacts(id,tenant_id,company_id,application_id,offer_id,storage_key,sha256,size_bytes,state,provenance,renderer_version,source_offer_version,pending_offer_version,created_at_utc,version) VALUES({Guid.NewGuid()},{tenant},{b.Id},{app.Id},{offer.Id},{Guid.NewGuid().ToString()},'x',1,'Pending','FrozenOriginal','v1',1,1,CURRENT_TIMESTAMP,1)", PostgresErrorCodes.ForeignKeyViolation);
        await Refused("event company/offer", $"INSERT INTO offer_lifecycle_events(id,tenant_id,company_id,application_id,offer_id,event_type,actor_user_id,actor_name,reason,data_json,occurred_at_utc,recorded_at_utc) VALUES({Guid.NewGuid()},{tenant},{b.Id},{app.Id},{offer.Id},'Invalid',{Guid.NewGuid()},'actor','','{{}}',CURRENT_TIMESTAMP,CURRENT_TIMESTAMP)", PostgresErrorCodes.ForeignKeyViolation);
        await Refused("event same-company application/offer", $"INSERT INTO offer_lifecycle_events(id,tenant_id,company_id,application_id,offer_id,event_type,actor_user_id,actor_name,reason,data_json,occurred_at_utc,recorded_at_utc) VALUES({Guid.NewGuid()},{tenant},{a.Id},{sameCompanyOtherApp.Id},{offer.Id},'Invalid',{Guid.NewGuid()},'actor','','{{}}',CURRENT_TIMESTAMP,CURRENT_TIMESTAMP)", PostgresErrorCodes.ForeignKeyViolation);
        await Refused("event cross tenant", $"INSERT INTO offer_lifecycle_events(id,tenant_id,company_id,application_id,offer_id,event_type,actor_user_id,actor_name,reason,data_json,occurred_at_utc,recorded_at_utc) VALUES({Guid.NewGuid()},{legacyTenant},{a.Id},{app.Id},{offer.Id},'Invalid',{Guid.NewGuid()},'actor','','{{}}',CURRENT_TIMESTAMP,CURRENT_TIMESTAMP)", PostgresErrorCodes.ForeignKeyViolation);
        await Refused("current issued same application", $"UPDATE job_applications SET current_issued_offer_id={otherOffer.Id} WHERE id={app.Id}", PostgresErrorCodes.ForeignKeyViolation);
        await Refused("reservation same application", $"UPDATE job_applications SET offer_reservation_offer_id={otherOffer.Id} WHERE id={app.Id}", PostgresErrorCodes.ForeignKeyViolation);
        await Refused("reservation shape", $"UPDATE job_applications SET offer_reservation_version=NULL WHERE id={app.Id}", PostgresErrorCodes.CheckViolation);
        await Refused("unique revision", $"INSERT INTO offer_letters SELECT (jsonb_populate_record(NULL::offer_letters, to_jsonb(o) || jsonb_build_object('id',{Guid.NewGuid()}))).* FROM offer_letters o WHERE id={offer.Id}", PostgresErrorCodes.UniqueViolation);
        await Refused("frozen recipient immutable", $"UPDATE offer_letters SET candidate_name='rewritten' WHERE id={offer.Id}", PostgresErrorCodes.RaiseException);
        await Refused("artifact key immutable", $"UPDATE offer_artifacts SET storage_key='rewritten' WHERE id={artifact.Id}", PostgresErrorCodes.RaiseException);
        await Refused("event append-only", $"UPDATE offer_lifecycle_events SET reason='rewritten' WHERE id={evidence.Id}", PostgresErrorCodes.RaiseException);
        await Refused("event retained", $"DELETE FROM offer_lifecycle_events WHERE id={evidence.Id}", PostgresErrorCodes.RaiseException);
        missing.Should().BeEmpty($"the migration must enforce these missing guards: {string.Join(", ", missing)}");

        var rollback = await FluentActions.Awaiting(() => migrator.MigrateAsync("20261019134047_OfferCompensationPolicy")).Should().ThrowAsync<PostgresException>();
        rollback.Which.SqlState.Should().Be(PostgresErrorCodes.RaiseException);
        // EF commits each migration independently. The empty additive dispatch migration can
        // downgrade before the lifecycle-history guard refuses; restore the current graph.
        await migrator.MigrateAsync(migration);

        var tenantRow = await db.Tenants.SingleAsync(t => t.Id == tenant); tenantRow.IsActive = false; tenantRow.Slug += "__deleted_offer_migration";
        await db.SaveChangesAsync();
        var storage = new OfferMemoryStorage();
        var unresolved = new OfferArtifact { TenantId = tenant, CompanyId = a.Id, ApplicationId = app.Id, OfferId = offer.Id,
            StorageKey = storage.TenantKey(tenant, "unresolved.pdf"), Sha256 = new string('b', 64), SizeBytes = 3 };
        db.OfferArtifacts.Add(unresolved); await db.SaveChangesAsync();
        var unresolvedPurge = await FluentActions.Awaiting(() => OfferArtifactErasure.EraseAsync(db, storage, tenant, default)).Should().ThrowAsync<IOException>();
        unresolvedPurge.Which.Message.Should().Contain("unresolved");
        (await db.OfferArtifacts.CountAsync(o => o.TenantId == tenant)).Should().Be(2);
        // Prove Uploaded is accepted on the actual migrated schema, then safely deleted. This
        // fixture explicitly completes its write; runtime never infers this from key presence.
        await storage.PutAtAsync(tenant, unresolved.StorageKey, [1, 2, 3], "application/pdf");
        unresolved.State = "Uploaded"; await db.SaveChangesAsync();
        await OfferArtifactErasure.EraseAsync(db, storage, tenant, default);
        storage.Bytes.Should().BeEmpty();
        (await db.OfferLetters.CountAsync(o => o.TenantId == tenant)).Should().Be(0);
        (await db.OfferArtifacts.CountAsync(o => o.TenantId == tenant)).Should().Be(0);
        (await db.OfferSeries.CountAsync(o => o.TenantId == tenant)).Should().Be(0);
        (await db.OfferLetters.CountAsync(o => o.Id == legacyOffer)).Should().Be(1);
        (await db.JobApplications.SingleAsync(o => o.Id == app.Id)).CurrentIssuedOfferId.Should().BeNull();
        await migrator.MigrateAsync("20261019134047_OfferCompensationPolicy");
        await migrator.MigrateAsync(migration);
        (await db.OfferLetters.AsNoTracking().SingleAsync(o => o.Id == legacyOffer)).GrossSalary.Should().Be(1234.56m);
    }
}
