using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Migrations;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// EXECUTES the <c>RaiseKsaMaternityLeaveToTwelveWeeks</c> correction against a real Postgres built by
/// the full migration history, over every shape a live tenant can hold, then runs it a SECOND time.
/// Own container: the correction sweeps every tenant in the database.
///
/// <para>The rows are written after the schema is current and the correction is then run by hand,
/// rather than seeded at the previous migration, so the fixture does not have to spell out a historical
/// column list for five tables — the SQL under test is the very string the migration's Up executes.</para>
/// </summary>
public sealed class KsaMaternityFloorMigrationPostgresTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
    private string _cs = string.Empty;

    public async Task InitializeAsync() { await _container.StartAsync(); _cs = _container.GetConnectionString(); }
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
    private ZayraDbContext CreateDb() => new(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(_cs).Options);

    private sealed record Ids(
        Guid KsaType, Guid KsaSaCalendar, Guid KsaNeutralCalendar, Guid KsaGenerous, Guid KsaArchived, Guid KsaSaWorking, Guid KsaSaWorkingGenerous,
        Guid KsaExtensionType, Guid KsaExtensionPolicy, Guid KsaAnnualPolicy,
        Guid MixedType, Guid MixedNeutral, Guid MixedSaWorking, Guid MixedUaeCompanyPolicy,
        Guid UaeType, Guid UaePolicyAe, Guid UaePolicyNeutral,
        Guid SauOnlyType, Guid SauOnlyPolicy);

    private async Task<Ids> SeedAsync()
    {
        await using var db = CreateDb();
        var ksa = Guid.NewGuid();
        var mixed = Guid.NewGuid();
        var uae = Guid.NewGuid();
        var sauOnly = Guid.NewGuid();
        foreach (var t in new[] { ksa, mixed, uae, sauOnly })
            db.Tenants.Add(new Tenant { Id = t, Name = $"T {t:N}", Slug = $"t-{t:N}" });
        db.Companies.Add(new Company { TenantId = ksa, LegalNameEn = "KSA Co", CountryCode = "SA" });
        db.Companies.Add(new Company { TenantId = mixed, LegalNameEn = "Mixed KSA", CountryCode = "SAU" });
        var mixedUae = new Company { TenantId = mixed, LegalNameEn = "Mixed UAE", CountryCode = "AE" };
        db.Companies.Add(mixedUae);
        db.Companies.Add(new Company { TenantId = uae, LegalNameEn = "UAE Co", CountryCode = "AE" });

        LeaveType Type(Guid tenant, string code, string name, int max)
        {
            var t = new LeaveType { TenantId = tenant, Code = code, NameEn = name, Category = "Parental", IsPaid = true, MaxConsecutiveDays = max };
            db.LeaveTypes.Add(t);
            return t;
        }
        LeavePolicy Policy(Guid tenant, LeaveType type, string country, decimal days, decimal max,
            bool calendar = true, string status = "Active", Guid? companyId = null)
        {
            var p = new LeavePolicy
            {
                TenantId = tenant, Name = $"{type.NameEn} {country} {days} {calendar}", LeaveTypeId = type.Id, CountryCode = country,
                CompanyId = companyId, AnnualEntitlementDays = days, MaximumDaysPerRequest = max, Status = status,
                WeekendsIncluded = calendar, PublicHolidaysIncluded = calendar,
            };
            db.LeavePolicies.Add(p);
            return p;
        }

        // A Saudi-only tenant: both counting modes, plus the shapes that must not move.
        var ksaType = Type(ksa, "MAT", "Maternity Leave", 70);
        var ksaSa = Policy(ksa, ksaType, "SA", 70m, 70m);
        var ksaNeutral = Policy(ksa, ksaType, "", 70m, 0m);                    // "0" = no cap: stays 0
        var ksaGenerous = Policy(ksa, ksaType, "SA", 98m, 98m);                // company grants more
        var ksaArchived = Policy(ksa, ksaType, "SA", 70m, 70m, status: "Archived");
        var ksaWorking = Policy(ksa, ksaType, "SA", 70m, 70m, calendar: false); // working days: review, not raise
        var ksaWorkingGenerous = Policy(ksa, ksaType, "SA", 90m, 0m, calendar: false); // already >= 84: no review row
        var extType = Type(ksa, "MATEXT", "Maternity extension (unpaid)", 30);
        var extPolicy = Policy(ksa, extType, "SA", 30m, 30m);
        var annualType = new LeaveType { TenantId = ksa, Code = "ANNUAL", NameEn = "Annual Leave", Category = "Annual", IsPaid = true, MaxConsecutiveDays = 30 };
        db.LeaveTypes.Add(annualType);
        var annualPolicy = Policy(ksa, annualType, "SA", 21m, 30m);

        // A mixed tenant (Saudi + UAE companies): the neutral policy also governs UAE staff.
        var mixedType = Type(mixed, "MAT", "Maternity Leave", 70);
        var mixedNeutral = Policy(mixed, mixedType, "", 70m, 70m);              // never raised: review
        var mixedSaWorking = Policy(mixed, mixedType, "SA", 70m, 70m, calendar: false);
        var mixedUaePolicy = Policy(mixed, mixedType, "", 60m, 60m, companyId: mixedUae.Id);

        // A UAE-only tenant: the Saudi floor must not reach it.
        var uaeType = Type(uae, "MAT", "Maternity Leave", 70);
        var uaeAe = Policy(uae, uaeType, "AE", 60m, 60m);
        var uaeNeutral = Policy(uae, uaeType, "", 70m, 70m);

        // No company yet, but a policy that names Saudi Arabia (ISO3) for a differently-coded type.
        var sauType = Type(sauOnly, "ML1", "Maternity Leave (KSA)", 60);
        var sauPolicy = Policy(sauOnly, sauType, "SAU", 60m, 60m);

        await db.SaveChangesAsync();
        return new Ids(ksaType.Id, ksaSa.Id, ksaNeutral.Id, ksaGenerous.Id, ksaArchived.Id, ksaWorking.Id, ksaWorkingGenerous.Id,
            extType.Id, extPolicy.Id, annualPolicy.Id,
            mixedType.Id, mixedNeutral.Id, mixedSaWorking.Id, mixedUaePolicy.Id,
            uaeType.Id, uaeAe.Id, uaeNeutral.Id, sauType.Id, sauPolicy.Id);
    }

    private sealed record Snapshot(
        Dictionary<Guid, (decimal Days, decimal Max, DateTime Updated)> Policies,
        Dictionary<Guid, int> Types,
        List<LeaveAuditLog> Audits);

    private async Task<Snapshot> ReadAsync()
    {
        await using var db = CreateDb();
        var policies = await db.LeavePolicies.IgnoreQueryFilters().AsNoTracking()
            .ToDictionaryAsync(p => p.Id, p => (p.AnnualEntitlementDays, p.MaximumDaysPerRequest, p.UpdatedAtUtc));
        var types = await db.LeaveTypes.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.MaxConsecutiveDays);
        var audits = await db.LeaveAuditLogs.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.PerformedByName == RaiseKsaMaternityLeaveToTwelveWeeks.AuditActor).ToListAsync();
        return new Snapshot(policies, types, audits);
    }

    [Fact]
    public async Task RaisesOnlySaudiOnlyCalendarCountedMaternity_RecordsTheRestForReview_AndASecondRunChangesNothing()
    {
        await using (var db = CreateDb())
            await db.Database.MigrateAsync();   // includes the migration itself, over an empty database

        var ids = await SeedAsync();
        var before = await ReadAsync();

        await using (var db = CreateDb())
            await db.Database.ExecuteSqlRawAsync(RaiseKsaMaternityLeaveToTwelveWeeks.UpSql);

        var snap = await ReadAsync();
        (decimal, decimal) P(Guid id) => (snap.Policies[id].Days, snap.Policies[id].Max);

        // Raised: Saudi-only reach, counted on the calendar.
        P(ids.KsaSaCalendar).Should().Be((84m, 84m));
        P(ids.KsaNeutralCalendar).Should().Be((84m, 0m), "a 0 per-request cap means no cap and is left alone");
        P(ids.SauOnlyPolicy).Should().Be((84m, 84m));
        snap.Types[ids.KsaType].Should().Be(84);
        snap.Types[ids.SauOnlyType].Should().Be(84);
        snap.Policies[ids.KsaSaCalendar].Updated.Should().Be(before.Policies[ids.KsaSaCalendar].Updated,
            "updated_at_utc is not touched; the audit row records the change");

        // Not raised: working days, mixed reach, more generous, archived, not maternity, not Saudi.
        P(ids.KsaSaWorking).Should().Be((70m, 70m));
        P(ids.KsaSaWorkingGenerous).Should().Be((90m, 0m));
        P(ids.MixedNeutral).Should().Be((70m, 70m));
        P(ids.MixedSaWorking).Should().Be((70m, 70m));
        P(ids.MixedUaeCompanyPolicy).Should().Be((60m, 60m));
        snap.Types[ids.MixedType].Should().Be(70, "raising the type cap would lift the UAE staff too");
        P(ids.KsaGenerous).Should().Be((98m, 98m));
        P(ids.KsaArchived).Should().Be((70m, 70m));
        P(ids.KsaExtensionPolicy).Should().Be((30m, 30m));
        snap.Types[ids.KsaExtensionType].Should().Be(30);
        P(ids.KsaAnnualPolicy).Should().Be((21m, 30m));
        P(ids.UaePolicyAe).Should().Be((60m, 60m));
        P(ids.UaePolicyNeutral).Should().Be((70m, 70m));
        snap.Types[ids.UaeType].Should().Be(70);

        var raised = snap.Audits.Where(a => a.Action == "StatutoryFloorRaised").Select(a => a.EntityId).ToList();
        raised.Should().BeEquivalentTo(new[]
        {
            ids.KsaSaCalendar, ids.KsaNeutralCalendar, ids.SauOnlyPolicy, ids.KsaType, ids.SauOnlyType,
        }.Select(i => i.ToString()));
        var review = snap.Audits.Where(a => a.Action == "StatutoryReviewNeeded").ToList();
        review.Select(a => a.EntityId).Should().BeEquivalentTo(new[]
        {
            ids.KsaSaWorking, ids.MixedSaWorking, ids.MixedNeutral,
        }.Select(i => i.ToString()), "a durable HR worklist, not only a NOTICE");
        review.Single(a => a.EntityId == ids.KsaSaWorking.ToString()).Reason.Should().Contain("CALENDAR days");
        review.Single(a => a.EntityId == ids.MixedNeutral.ToString()).Reason.Should().Contain("Create a Saudi policy");

        var raisedRow = snap.Audits.Single(a => a.EntityId == ids.KsaSaCalendar.ToString());
        raisedRow.OldValue.Should().Contain("annual_entitlement_days=70");
        raisedRow.NewValue.Should().Contain("annual_entitlement_days=84");
        raisedRow.Reason.Should().Contain("Art. 151");

        // Idempotent: a retry, a replay or a restored replica changes nothing and writes no audit.
        await using (var db = CreateDb())
            await db.Database.ExecuteSqlRawAsync(RaiseKsaMaternityLeaveToTwelveWeeks.UpSql);
        var again = await ReadAsync();
        again.Policies.Should().BeEquivalentTo(snap.Policies);
        again.Types.Should().BeEquivalentTo(snap.Types);
        again.Audits.Should().HaveCount(snap.Audits.Count);
    }
}
