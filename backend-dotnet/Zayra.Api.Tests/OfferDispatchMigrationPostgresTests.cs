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
public sealed class OfferDispatchMigrationPostgresTests
{
    [Fact]
    public async Task DisposableUpgradePreservesExternalHistoryAndEnforcesDispatchScopeAndEvidenceRetention()
    {
        await using var container=new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();await container.StartAsync();
        await using var db=new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).AddInterceptors(RowLockingInterceptor.Instance).Options);
        var migrator=db.Database.GetInfrastructure().GetRequiredService<IMigrator>();await migrator.MigrateAsync("20261019144258_OfferLifecycle");
        var tenant=await PostgresFixture.SeedMinimalTenant(db);var company=new Company{TenantId=tenant};var other=new Company{TenantId=tenant};
        // Written as of the OLD schema: a later migration may add a companies column the current model would insert.
        await LegacyRows.InsertAsync(db,"companies",company);await LegacyRows.InsertAsync(db,"companies",other);
        var app=new JobApplication{TenantId=tenant,CompanyId=company.Id,CandidateId=Guid.NewGuid(),JobOpeningId=Guid.NewGuid()};db.JobApplications.Add(app);await db.SaveChangesAsync();
        var offer=new OfferLetter{TenantId=tenant,CompanyId=company.Id,ApplicationId=app.Id,StartDate=new(2027,1,1),CommunicationOutcome="ExternalRecorded"};
        db.OfferLetters.Add(offer);await db.SaveChangesAsync();
        var artifact=new OfferArtifact{TenantId=tenant,CompanyId=company.Id,ApplicationId=app.Id,OfferId=offer.Id,StorageKey=$"{tenant:N}/dispatch-test.pdf",State="Finalized"};
        db.OfferArtifacts.Add(artifact);db.OfferLifecycleEvents.Add(new(){TenantId=tenant,CompanyId=company.Id,ApplicationId=app.Id,OfferId=offer.Id,EventType="ExternalSent",DataJson="{\"evidence\":\"ExternalRecorded\"}"});await db.SaveChangesAsync();
        var migration=db.Database.GetMigrations().SingleOrDefault(m=>m.EndsWith("_OfferDispatch"));migration.Should().NotBeNull("an additive dispatch migration must exist");
        // Later additive migrations: the rows below are written with the CURRENT model.
        await migrator.MigrateAsync(migration!);await migrator.MigrateAsync(migration!); await migrator.MigrateAsync();db.ChangeTracker.Clear();
        (await db.OfferLetters.SingleAsync(o=>o.Id==offer.Id)).CommunicationOutcome.Should().Be("External");
        (await db.OfferLifecycleEvents.SingleAsync(e=>e.OfferId==offer.Id)).DataJson.Should().Contain("ExternalRecorded");
        var dispatch=new OfferDispatch{TenantId=tenant,CompanyId=company.Id,ApplicationId=app.Id,OfferId=offer.Id,ArtifactId=artifact.Id,IdempotencyKey="upgrade",RequestFingerprint=new string('a',64),ReservationVersion=1};
        db.OfferDispatches.Add(dispatch);await db.SaveChangesAsync();
        var attempt=new OfferDispatchAttempt{TenantId=tenant,CompanyId=company.Id,ApplicationId=app.Id,OfferId=offer.Id,DispatchId=dispatch.Id,Stage="Result",Outcome="Captured"};
        db.OfferDispatchAttempts.Add(attempt);await db.SaveChangesAsync();db.ChangeTracker.Clear();
        async Task Refused(FormattableString sql,string state)
        {var failure=await FluentActions.Awaiting(()=>db.Database.ExecuteSqlInterpolatedAsync(sql)).Should().ThrowAsync<PostgresException>();failure.Which.SqlState.Should().Be(state);}
        await Refused($"UPDATE offer_dispatches SET recipient_email='tampered@example.invalid' WHERE id={dispatch.Id}",PostgresErrorCodes.RaiseException);
        await Refused($"UPDATE offer_dispatch_attempts SET outcome='AcceptedByRelay' WHERE id={attempt.Id}",PostgresErrorCodes.RaiseException);
        await Refused($"DELETE FROM offer_dispatch_attempts WHERE id={attempt.Id}",PostgresErrorCodes.RaiseException);
        await Refused($"INSERT INTO offer_dispatches SELECT (jsonb_populate_record(NULL::offer_dispatches,to_jsonb(d)||jsonb_build_object('id',{Guid.NewGuid()},'company_id',{other.Id},'idempotency_key','wrong-company'))).* FROM offer_dispatches d WHERE id={dispatch.Id}",PostgresErrorCodes.ForeignKeyViolation);
        await Refused($"INSERT INTO offer_dispatch_attempts SELECT (jsonb_populate_record(NULL::offer_dispatch_attempts,to_jsonb(d)||jsonb_build_object('id',{Guid.NewGuid()},'lease_token',{Guid.NewGuid()},'company_id',{other.Id}))).* FROM offer_dispatch_attempts d WHERE id={attempt.Id}",PostgresErrorCodes.ForeignKeyViolation);
        var rollback=await FluentActions.Awaiting(()=>migrator.MigrateAsync("20261019144258_OfferLifecycle")).Should().ThrowAsync<PostgresException>();rollback.Which.SqlState.Should().Be(PostgresErrorCodes.RaiseException);
        var t=await db.Tenants.SingleAsync(t=>t.Id==tenant);t.IsActive=false;t.Slug+="__deleted_dispatch_migration";await db.SaveChangesAsync();
        await migrator.MigrateAsync(); // The current erasure graph includes accepted draft source references.
        await OfferArtifactErasure.EraseAsync(db,new OfferMemoryStorage(),tenant,default);
        (await db.OfferDispatches.CountAsync()).Should().Be(0);(await db.OfferDispatchAttempts.CountAsync()).Should().Be(0);
        await migrator.MigrateAsync("20261019144258_OfferLifecycle");await migrator.MigrateAsync(migration!);
    }
}
