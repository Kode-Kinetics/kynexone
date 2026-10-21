using System.Text.Json;
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
public sealed class OfferHandoffMigrationPostgresTests
{
    [Fact]
    public async Task UpgradePreservesLegacyAndProtectsSourceScopeImmutabilityAndErasureRollback()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build(); await container.StartAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString())
            .AddInterceptors(RowLockingInterceptor.Instance).Options);
        var migrator = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrator.MigrateAsync("20261019152813_OfferDispatch");
        var tenant = await PostgresFixture.SeedMinimalTenant(db);
        var legacy = new EmployeeDraft { TenantId = tenant, EnglishName = "Legacy", Salary = 777m };
        var legacyJson = JsonSerializer.Serialize(legacy, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO employee_drafts SELECT (jsonb_populate_record(NULL::employee_drafts, {legacyJson}::jsonb)).*");
        var migration = db.Database.GetMigrations().SingleOrDefault(m => m.EndsWith("_OfferCompensationHandoff"));
        migration.Should().NotBeNull("accepted source references require their additive migration");
        await migrator.MigrateAsync(migration!); await migrator.MigrateAsync(migration!); await migrator.MigrateAsync(); /* later additive migrations: rows below use the CURRENT model */
        var old = await db.EmployeeDrafts.SingleAsync(d => d.Id == legacy.Id);
        old.Salary.Should().Be(777); old.AcceptedOfferId.Should().BeNull(); old.AcceptedOfferCompensationJson.Should().BeNull();
        var company = new Company { TenantId = tenant, DefaultCurrency = "SAR" }; var other = new Company { TenantId = tenant };
        var app = new JobApplication { TenantId = tenant, CompanyId = company.Id, CandidateId = Guid.NewGuid(), JobOpeningId = Guid.NewGuid(), Status = "Hired" };
        var offer = new OfferLetter { TenantId = tenant, CompanyId = company.Id, ApplicationId = app.Id, StartDate = new(2027, 1, 1), Status = "Accepted" };
        var draft = OfferCompensationHandoffTests.Draft(); draft.TenantId = tenant;
        offer.CompensationSnapshotJson = draft.AcceptedOfferCompensationJson;
        draft.AcceptedOfferId = offer.Id; draft.AcceptedOfferCompanyId = company.Id;
        db.AddRange(company, other, app, offer, draft); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var retained = await db.EmployeeDrafts.SingleAsync(d => d.Id == draft.Id); retained.AcceptedOfferCompensationJson = "{}";
        await FluentActions.Awaiting(() => db.SaveChangesAsync()).Should().ThrowAsync<InvalidOperationException>().WithMessage("*handoff_immutable*");
        db.ChangeTracker.Clear();
        async Task Refused(FormattableString sql)
        { await FluentActions.Awaiting(() => db.Database.ExecuteSqlInterpolatedAsync(sql)).Should().ThrowAsync<PostgresException>(); }
        await Refused($"UPDATE employee_drafts SET accepted_offer_compensation_json='{{}}' WHERE id={draft.Id}");
        await Refused($"UPDATE employee_drafts SET accepted_offer_id=NULL WHERE id={draft.Id}");
        await Refused($"UPDATE employee_drafts SET offer_compensation_placement_company_id={company.Id} WHERE id={draft.Id}");
        await Refused($"INSERT INTO employee_drafts SELECT (jsonb_populate_record(NULL::employee_drafts,to_jsonb(d)||jsonb_build_object('id',{Guid.NewGuid()},'accepted_offer_company_id',{other.Id}))).* FROM employee_drafts d WHERE id={draft.Id}");
        await Refused($"INSERT INTO employee_drafts SELECT (jsonb_populate_record(NULL::employee_drafts,to_jsonb(d)||jsonb_build_object('id',{Guid.NewGuid()},'tenant_id',{Guid.NewGuid()}))).* FROM employee_drafts d WHERE id={draft.Id}");
        var legacyApp = new JobApplication { TenantId = tenant, CandidateId = Guid.NewGuid(), JobOpeningId = Guid.NewGuid(), Status = "Hired" };
        var legacyOffer = new OfferLetter { TenantId = tenant, ApplicationId = legacyApp.Id, StartDate = new(2027, 1, 1), Status = "Accepted" };
        var legacySnapshot = await OfferComponentOptions.CalculateAsync(db, tenant, null, legacyOffer.StartDate, null, 1000, 200, 75, 25, default);
        OfferCompensation.ApplySnapshot(legacyOffer, legacySnapshot);
        var anchored = new EmployeeDraft { TenantId = tenant, AcceptedOfferId = legacyOffer.Id,
            AcceptedOfferCompensationJson = legacyOffer.CompensationSnapshotJson, Salary = 1000, OfferCompensationVersion = 1 };
        db.AddRange(legacyApp, legacyOffer, anchored); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await Refused($"UPDATE employee_drafts SET offer_compensation_placement_company_id={company.Id} WHERE id={anchored.Id}");
        await Refused($"UPDATE employee_drafts SET offer_compensation_placement_company_id={Guid.NewGuid()}, offer_compensation_confirmed_at_utc=now(), offer_compensation_confirmed_by_user_id={Guid.NewGuid()}, offer_compensation_confirmed_fingerprint='confirmed', offer_compensation_dispositions_json='[]' WHERE id={anchored.Id}");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE employee_drafts SET offer_compensation_placement_company_id={company.Id}, offer_compensation_confirmed_at_utc=now(), offer_compensation_confirmed_by_user_id={Guid.NewGuid()}, offer_compensation_confirmed_fingerprint='confirmed', offer_compensation_dispositions_json='[]' WHERE id={anchored.Id}");
        await Refused($"UPDATE employee_drafts SET offer_compensation_placement_company_id={other.Id} WHERE id={anchored.Id}");
        await Refused($"UPDATE employee_drafts SET offer_compensation_placement_company_id=NULL WHERE id={anchored.Id}");
        var immutableAnchor = await db.EmployeeDrafts.SingleAsync(d => d.Id == anchored.Id);
        immutableAnchor.OfferCompensationPlacementCompanyId = other.Id;
        await FluentActions.Awaiting(() => db.SaveChangesAsync()).Should().ThrowAsync<InvalidOperationException>().WithMessage("*handoff_immutable*");
        db.ChangeTracker.Clear();
        await FluentActions.Awaiting(() => migrator.MigrateAsync("20261019152813_OfferDispatch")).Should().ThrowAsync<PostgresException>();
        var t = await db.Tenants.SingleAsync(t => t.Id == tenant); t.IsActive = false; t.Slug += "__deleted_handoff"; await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION handoff_test_fail_delete() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected downstream erase failure'; END $$; CREATE TRIGGER handoff_test_fail AFTER DELETE ON offer_letters FOR EACH ROW EXECUTE FUNCTION handoff_test_fail_delete();");
        await FluentActions.Awaiting(() => OfferArtifactErasure.EraseAsync(db, new OfferMemoryStorage(), tenant, default)).Should().ThrowAsync<PostgresException>();
        db.ChangeTracker.Clear();
        (await db.EmployeeDrafts.SingleAsync(d => d.Id == draft.Id)).AcceptedOfferCompensationJson.Should().Be(draft.AcceptedOfferCompensationJson);
        (await db.EmployeeDrafts.SingleAsync(d => d.Id == anchored.Id)).OfferCompensationPlacementCompanyId.Should().Be(company.Id);
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER handoff_test_fail ON offer_letters; DROP FUNCTION handoff_test_fail_delete();");
        await OfferArtifactErasure.EraseAsync(db, new OfferMemoryStorage(), tenant, default);
        (await db.EmployeeDrafts.AsNoTracking().SingleAsync(d => d.Id == draft.Id)).AcceptedOfferId.Should().BeNull();
        (await db.EmployeeDrafts.AsNoTracking().SingleAsync(d => d.Id == anchored.Id)).OfferCompensationPlacementCompanyId.Should().BeNull();
        await migrator.MigrateAsync("20261019152813_OfferDispatch"); await migrator.MigrateAsync(migration!); await migrator.MigrateAsync(); /* later additive migrations: rows below use the CURRENT model */
    }
}
