using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Entitlements.Usage;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// Benefit usage requests on PostgreSQL 16: two Finance users racing to approve the same request produce exactly one decision,
/// two requests racing for the last ticket produce one, and the shape CHECKs refuse a request with both a count and an amount and a
/// rejection without a reason.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class BenefitUsagePostgresTests(PostgresFixture fixture)
{
    private static readonly DateOnly Today = new(2026, 10, 6);
    private readonly Guid _hr = Guid.NewGuid();

    private async Task<(PackageSeed Seed, EmployeeEntitlement Row)> SeedAsync(short tickets = 2)
    {
        var seed = new PackageSeed();
        await using var db = fixture.CreateDb();
        await seed.SaveAsync(db);
        var row = new EmployeeEntitlement
        {
            TenantId = seed.TenantId, CompanyId = seed.Company.Id, EmployeeId = seed.Mohammed.PublicId, ContractId = seed.Term.Id,
            PayComponentCode = "AIR_TICKET", EntitlementClass = PayEntitlementClasses.Contractual, ValueType = GradeEntitlementValueTypes.Quantity,
            Quantity = tickets, CoverageTier = CoverageTiers.Economy, LimitPeriod = EntitlementLimitPeriods.Annual, Source = EntitlementSources.GradeDefault,
            GradeEntitlementId = seed.Cells["AIR_TICKET"].Id, EffectiveFrom = seed.Term.StartDate, EffectiveTo = seed.Term.EndDate,
        };
        db.EmployeeEntitlements.Add(row);
        await db.SaveChangesAsync();
        return (seed, row);
    }

    private static BenefitUsageService Service(ZayraDbContext db) => new(db, new FixedTenantClock(Today));
    private BenefitUsageActor Hr(PackageSeed s) => new(s.TenantId, _hr, ["entitlements.read", "entitlements.manage"], _ => true);
    private static BenefitUsageActor Finance(PackageSeed s, Guid user) => new(s.TenantId, user, ["benefits.usage.approve"], _ => true);
    private static BenefitUsageRequestInput Ticket(PackageSeed s) =>
        new(Guid.NewGuid(), s.Mohammed.Id, "AIR_TICKET", new DateOnly(2026, 8, 10), 1, null, "BKG-1");

    [Fact]
    public async Task TwoFinanceUsersRacing_ProduceExactlyOneDecision()
    {
        var (s, _) = await SeedAsync();
        Guid id;
        await using (var db = fixture.CreateDb()) id = (await Service(db).RequestAsync(Hr(s), Ticket(s), default)).Id;

        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(async i =>
        {
            await using var db = fixture.CreateRetryingDb();
            try { await Service(db).DecideAsync(Finance(s, Guid.NewGuid()), id, i == 0 ? "Rejected" : "Approved", "reason", default); return true; }
            catch (BenefitUsageRefusedException ex) when (ex.Code == BenefitUsageErrors.AlreadyDecided) { return false; }
        }));

        Assert.Single(results, r => r);
        await using var verify = fixture.CreateDb();
        var decided = await verify.BenefitUsageRequests.SingleAsync(x => x.Id == id);
        Assert.NotEqual(BenefitUsageStatuses.Pending, decided.Status);
        Assert.Equal(1, await verify.AuditLogs.CountAsync(x => x.EntityId == id.ToString() && x.Action != "benefit_usage.requested"));
    }

    [Fact]
    public async Task TwoRequestsRacingForTheLastTicket_OnlyOneIsTaken()
    {
        var (s, _) = await SeedAsync(tickets: 1);

        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            await using var db = fixture.CreateRetryingDb();
            try { await Service(db).RequestAsync(Hr(s), Ticket(s), default); return true; }
            catch (BenefitUsageRefusedException ex) when (ex.Code == BenefitUsageErrors.OverEntitlement) { return false; }
        }));

        Assert.Single(results, r => r);
        await using var verify = fixture.CreateDb();
        Assert.Equal(1, await verify.BenefitUsageRequests.CountAsync(x => x.TenantId == s.TenantId));
    }

    [Fact]
    public async Task TheShapeChecks_RefuseBothACountAndAnAmount_AndARejectionWithoutAReason()
    {
        var (s, row) = await SeedAsync();
        await using var db = fixture.CreateDb();
        db.BenefitUsageRequests.Add(new BenefitUsageRequest
        {
            TenantId = s.TenantId, CompanyId = s.Company.Id, EmployeeId = s.Mohammed.Id, EntitlementId = row.Id, BenefitCode = "AIR_TICKET",
            PeriodStart = s.Term.StartDate, PeriodEnd = s.Term.EndDate!.Value, UsageDate = new DateOnly(2026, 8, 10), Units = 1, Amount = 100m,
            Currency = "SAR", RequestedByUserId = _hr,
        });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("ck_benefit_usage_requests__measure", (ex.InnerException as PostgresException)?.ConstraintName);

        Guid id;
        await using (var created = fixture.CreateDb()) id = (await Service(created).RequestAsync(Hr(s), Ticket(s), default)).Id;
        await using var raw = fixture.CreateDb();
        var request = await raw.BenefitUsageRequests.SingleAsync(x => x.Id == id);
        request.Status = BenefitUsageStatuses.Rejected;
        request.DecidedByUserId = Guid.NewGuid();
        request.DecidedAtUtc = DateTime.UtcNow;
        var refused = await Assert.ThrowsAsync<DbUpdateException>(() => raw.SaveChangesAsync());
        Assert.Equal("ck_benefit_usage_requests__reason", (refused.InnerException as PostgresException)?.ConstraintName);
    }
}
