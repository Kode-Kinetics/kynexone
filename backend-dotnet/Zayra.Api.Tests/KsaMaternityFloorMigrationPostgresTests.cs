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
        Guid KsaType, Guid KsaPolicySa, Guid KsaPolicyNeutral, Guid KsaPolicyGenerous, Guid KsaPolicyArchived,
        Guid KsaExtensionType, Guid KsaExtensionPolicy, Guid KsaAnnualPolicy,
        Guid UaeType, Guid UaePolicyAe, Guid UaePolicyNeutral,
        Guid SauOnlyType, Guid SauOnlyPolicy);

    private async Task<Ids> SeedAsync()
    {
        await using var db = CreateDb();
        var ksa = Guid.NewGuid();
        var uae = Guid.NewGuid();
        var sauOnly = Guid.NewGuid();
        foreach (var t in new[] { ksa, uae, sauOnly })
            db.Tenants.Add(new Tenant { Id = t, Name = $"T {t:N}", Slug = $"t-{t:N}" });
        db.Companies.Add(new Company { TenantId = ksa, LegalNameEn = "KSA Co", CountryCode = "SA" });
        db.Companies.Add(new Company { TenantId = uae, LegalNameEn = "UAE Co", CountryCode = "AE" });

        LeaveType Type(Guid tenant, string code, string name, int max)
        {
            var t = new LeaveType { TenantId = tenant, Code = code, NameEn = name, Category = "Parental", IsPaid = true, MaxConsecutiveDays = max };
            db.LeaveTypes.Add(t);
            return t;
        }
        LeavePolicy Policy(Guid tenant, LeaveType type, string country, decimal days, decimal max, string status = "Active")
        {
            var p = new LeavePolicy
            {
                TenantId = tenant, Name = $"{type.NameEn} {country} {days}", LeaveTypeId = type.Id, CountryCode = country,
                AnnualEntitlementDays = days, MaximumDaysPerRequest = max, Status = status,
            };
            db.LeavePolicies.Add(p);
            return p;
        }

        // The shape the old setup draft produced, in a Saudi tenant.
        var ksaType = Type(ksa, "MAT", "Maternity Leave", 70);
        var ksaSa = Policy(ksa, ksaType, "SA", 70m, 70m);
        var ksaNeutral = Policy(ksa, ksaType, "", 70m, 0m);           // "0" = no cap: stays 0
        var ksaGenerous = Policy(ksa, ksaType, "SA", 98m, 98m);       // company grants more: untouched
        var ksaArchived = Policy(ksa, ksaType, "SA", 70m, 70m, "Archived");
        var extType = Type(ksa, "MATEXT", "Maternity extension (unpaid)", 30);
        var extPolicy = Policy(ksa, extType, "SA", 30m, 30m);
        var annualType = new LeaveType { TenantId = ksa, Code = "ANNUAL", NameEn = "Annual Leave", Category = "Annual", IsPaid = true, MaxConsecutiveDays = 30 };
        db.LeaveTypes.Add(annualType);
        var annualPolicy = Policy(ksa, annualType, "SA", 21m, 30m);

        // A UAE-only tenant: the Saudi floor must not reach it.
        var uaeType = Type(uae, "MAT", "Maternity Leave", 70);
        var uaeAe = Policy(uae, uaeType, "AE", 60m, 60m);
        var uaeNeutral = Policy(uae, uaeType, "", 70m, 70m);

        // No company yet, but a policy that names Saudi Arabia (ISO3) for a differently-coded type.
        var sauType = Type(sauOnly, "ML1", "Maternity Leave (KSA)", 60);
        var sauPolicy = Policy(sauOnly, sauType, "SAU", 60m, 60m);

        await db.SaveChangesAsync();
        return new Ids(ksaType.Id, ksaSa.Id, ksaNeutral.Id, ksaGenerous.Id, ksaArchived.Id,
            extType.Id, extPolicy.Id, annualPolicy.Id, uaeType.Id, uaeAe.Id, uaeNeutral.Id, sauType.Id, sauPolicy.Id);
    }

    private async Task<(Dictionary<Guid, (decimal Days, decimal Max)> Policies, Dictionary<Guid, int> Types, int Audits)> ReadAsync()
    {
        await using var db = CreateDb();
        var policies = await db.LeavePolicies.IgnoreQueryFilters().AsNoTracking()
            .ToDictionaryAsync(p => p.Id, p => (p.AnnualEntitlementDays, p.MaximumDaysPerRequest));
        var types = await db.LeaveTypes.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.MaxConsecutiveDays);
        var audits = await db.LeaveAuditLogs.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(a => a.PerformedByName == RaiseKsaMaternityLeaveToTwelveWeeks.AuditActor);
        return (policies, types, audits);
    }

    [Fact]
    public async Task RaisesOnlySaudiMaternityBelowTwelveWeeks_AuditsEachChange_AndASecondRunChangesNothing()
    {
        await using (var db = CreateDb())
            await db.Database.MigrateAsync();   // includes the migration itself, over an empty database

        var ids = await SeedAsync();

        await using (var db = CreateDb())
            await db.Database.ExecuteSqlRawAsync(RaiseKsaMaternityLeaveToTwelveWeeks.UpSql);

        var (p, t, audits) = await ReadAsync();

        // Raised to 84.
        p[ids.KsaPolicySa].Should().Be((84m, 84m));
        p[ids.KsaPolicyNeutral].Should().Be((84m, 0m), "a 0 per-request cap means no cap and is left alone");
        t[ids.KsaType].Should().Be(84);
        p[ids.SauOnlyPolicy].Should().Be((84m, 84m));
        t[ids.SauOnlyType].Should().Be(84);

        // Never lowered, never touched outside scope.
        p[ids.KsaPolicyGenerous].Should().Be((98m, 98m));
        p[ids.KsaPolicyArchived].Should().Be((70m, 70m));
        p[ids.KsaExtensionPolicy].Should().Be((30m, 30m));
        t[ids.KsaExtensionType].Should().Be(30);
        p[ids.KsaAnnualPolicy].Should().Be((21m, 30m));
        p[ids.UaePolicyAe].Should().Be((60m, 60m));
        p[ids.UaePolicyNeutral].Should().Be((70m, 70m));
        t[ids.UaeType].Should().Be(70);

        audits.Should().Be(5, "three policies and two leave types were raised, one audit row each");
        await using (var db = CreateDb())
        {
            var row = await db.LeaveAuditLogs.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(a => a.EntityId == ids.KsaPolicySa.ToString());
            row.Action.Should().Be("StatutoryFloorRaised");
            row.OldValue.Should().Contain("annual_entitlement_days=70");
            row.NewValue.Should().Contain("annual_entitlement_days=84");
            row.Reason.Should().Contain("Art. 151");
        }

        // Idempotent: a retry, a replay or a restored replica changes nothing and writes no audit.
        await using (var db = CreateDb())
            await db.Database.ExecuteSqlRawAsync(RaiseKsaMaternityLeaveToTwelveWeeks.UpSql);
        var (p2, t2, audits2) = await ReadAsync();
        p2.Should().BeEquivalentTo(p);
        t2.Should().BeEquivalentTo(t);
        audits2.Should().Be(5);
    }
}
