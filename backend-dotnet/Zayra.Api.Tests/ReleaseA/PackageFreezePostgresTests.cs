using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Entitlements;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// Release A slice R2 on PostgreSQL 16 with the R0 DDL (EXCLUDE, close-only and deferred containment triggers): what the
/// writer stages is what the database accepts, freezing is idempotent even when two requests race, a matrix change
/// mid-year leaves the frozen year alone, and renewal / holdover rows satisfy the containment and carried-equality rules.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class PackageFreezePostgresTests(PostgresFixture fixture)
{
    private static readonly DateOnly Today = new(2026, 10, 6);

    private static EntitlementWriter Writer(ZayraDbContext db, DateOnly? today = null) =>
        new(db, new FixedTenantClock(today ?? Today), new EntitlementResolver(db));

    private async Task<PackageSeed> SeedAsync()
    {
        var seed = new PackageSeed();
        await using var db = fixture.CreateDb();
        await seed.SaveAsync(db);
        return seed;
    }

    private static Task<FreezeResult> FreezeAsync(ZayraDbContext db, PackageSeed s, Guid contractId, DateOnly? today = null) =>
        FinanceDecisionSerializer.SerializeAsync(db, FinanceDecisionSerializer.ScopeEmployeePackage, s.TenantId, s.Mohammed.PublicId, async () =>
        {
            var result = await Writer(db, today).FreezeTermAsync(s.TenantId, contractId, default);
            await db.SaveChangesAsync();
            return result;
        });

    [Fact]
    public async Task Freeze_IsAcceptedByTheTriggers_AndIsIdempotent_EvenWhenTwoRequestsRace()
    {
        var s = await SeedAsync();
        var attempts = Enumerable.Range(0, 3).Select(async _ =>
        {
            await using var db = fixture.CreateRetryingDb();
            return await FreezeAsync(db, s, s.Term.Id);
        });
        var results = await Task.WhenAll(attempts);

        Assert.Single(results, r => r.Frozen);
        Assert.Equal(2, results.Count(r => r.AlreadyFrozen));
        await using var verify = fixture.CreateDb();
        var rows = await verify.EmployeeEntitlements.Where(x => x.TenantId == s.TenantId).ToListAsync();
        Assert.Equal(new[] { "AIR_TICKET", "MEDICAL" }, rows.Select(r => r.PayComponentCode).OrderBy(c => c));
        Assert.All(rows, r => Assert.Equal((Today, (DateOnly?)PackageSeed.TermEnd), (r.EffectiveFrom, r.EffectiveTo)));
    }

    [Fact]
    public async Task AFrozenYear_SurvivesAMatrixChange_AndTheDatabaseRefusesToRewriteIt()
    {
        var s = await SeedAsync();
        await using (var db = fixture.CreateDb()) await FreezeAsync(db, s, s.Term.Id);

        await using (var db = fixture.CreateDb())
        {
            var medical = await db.GradeEntitlements.SingleAsync(x => x.Id == s.Cells["MEDICAL"].Id);
            medical.EffectiveTo = new DateOnly(2026, 10, 31);
            db.GradeEntitlements.Add(new GradeEntitlement { TenantId = s.TenantId, GradeId = s.Grade.Id, PayComponentCode = "MEDICAL",
                EntitlementClass = PayEntitlementClasses.Contractual, Eligible = true, ValueType = GradeEntitlementValueTypes.CoverageTier,
                CoverageTier = CoverageTiers.Vip, DependantScope = DependantScopes.Family, LimitPeriod = EntitlementLimitPeriods.PerTerm,
                EffectiveFrom = new DateOnly(2026, 11, 1) });
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDb())
        {
            var package = await new EntitlementResolver(db).ResolveAsync(s.TenantId, s.Mohammed.Id, new DateOnly(2026, 12, 15), default);
            var line = package.Lines.Single(l => l.ComponentCode == "MEDICAL");
            Assert.Equal((PackageLineSources.ContractFrozen, CoverageTiers.B, true), (line.Source, line.CoverageTier, line.GradeStandardDiffers));
            Assert.True((await FreezeAsync(db, s, s.Term.Id)).AlreadyFrozen);

            // Close-only: even a direct write cannot raise the frozen tier.
            var row = await db.EmployeeEntitlements.SingleAsync(x => x.TenantId == s.TenantId && x.PayComponentCode == "MEDICAL");
            row.CoverageTier = CoverageTiers.Vip;
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains("ENTITLEMENT_CLOSE_ONLY", ex.InnerException?.Message);
        }
    }

    [Fact]
    public async Task Writer_RefusesADraftTerm_BeforeTheDatabaseHasTo()
    {
        var s = await SeedAsync();
        await using var db = fixture.CreateDb();
        var draft = new EmployeeContract { TenantId = s.TenantId, CompanyId = s.Company.Id, EmployeeId = s.Mohammed.PublicId, ContractNumber = "CON-D",
            Status = "Draft", StartDate = new DateOnly(2027, 2, 1), EndDate = new DateOnly(2028, 1, 31) };
        db.EmployeeContracts.Add(draft);
        await db.SaveChangesAsync();
        var ex = await Assert.ThrowsAsync<EntitlementWriteRefusedException>(() => FreezeAsync(db, s, draft.Id));
        Assert.Equal(EntitlementWriteRefusedException.ContractNotInForce, ex.Code);
        Assert.False(await db.EmployeeEntitlements.AnyAsync(x => x.TenantId == s.TenantId));
    }

    [Fact]
    public async Task Renewal_And_Holdover_RowsSatisfyContainmentAndCarriedEquality()
    {
        var s = await SeedAsync();
        await using (var db = fixture.CreateDb()) await FreezeAsync(db, s, s.Term.Id, PackageSeed.TermStart);

        // Holdover: the term lapsed with no decision; a provisional successor carries the package by law.
        var provisional = new EmployeeContract { TenantId = s.TenantId, CompanyId = s.Company.Id, EmployeeId = s.Mohammed.PublicId, ContractNumber = "CON-P",
            Status = "Active", StartDate = new DateOnly(2027, 2, 1), EndDate = new DateOnly(2028, 1, 31), ProvisionalBasis = ProvisionalBases.DeemedRenewal };
        await using (var db = fixture.CreateDb())
        {
            db.EmployeeContracts.Add(provisional);
            await db.SaveChangesAsync();
            await FinanceDecisionSerializer.SerializeAsync(db, FinanceDecisionSerializer.ScopeEmployeePackage, s.TenantId, s.Mohammed.PublicId, async () =>
            {
                await Writer(db).CarryToProvisionalAsync(s.TenantId, s.Term.Id, provisional.Id, default);
                await db.SaveChangesAsync();
                return 0;
            });
        }

        // Apply then promotes the provisional term with medical raised to A (an approved exception) and the ticket kept.
        await using (var db = fixture.CreateDb())
        {
            var approval = new ApprovalRequest { TenantId = s.TenantId, EntityName = "ContractRenewal", EntityId = Guid.NewGuid().ToString(), Status = "Approved" };
            db.ApprovalRequests.Add(approval);
            await db.SaveChangesAsync();
            var plan = new RenewalApplyPlan(Guid.Empty, s.Mohammed.PublicId, s.Company.Id, s.Term.Id, provisional.Id, provisional.StartDate, provisional.EndDate,
                approval.Id,
            [
                new("MEDICAL", RenewalLineActions.Raise, PayEntitlementClasses.Contractual, GradeEntitlementValueTypes.CoverageTier, null, null, null,
                    CoverageTiers.A, null, DependantScopes.Family, null, EntitlementLimitPeriods.PerTerm, EntitlementSources.Exception, null, null, null),
                new("AIR_TICKET", RenewalLineActions.Keep, PayEntitlementClasses.Contractual, GradeEntitlementValueTypes.Quantity, null, null, null,
                    CoverageTiers.Economy, 1, DependantScopes.None, null, EntitlementLimitPeriods.Annual, EntitlementSources.GradeDefault,
                    s.Cells["AIR_TICKET"].Id, null, null),
            ]) { CaseId = await CaseAsync(db, s) };
            await FinanceDecisionSerializer.SerializeAsync(db, FinanceDecisionSerializer.ScopeEmployeePackage, s.TenantId, s.Mohammed.PublicId, async () =>
            {
                await Writer(db, new DateOnly(2027, 2, 10)).ApplyRenewalAsync(s.TenantId, plan, default);
                await db.SaveChangesAsync();
                return 0;
            });
        }

        await using (var db = fixture.CreateDb())
        {
            var onProvisional = await db.EmployeeEntitlements.Where(x => x.ContractId == provisional.Id).OrderBy(x => x.EffectiveFrom).ToListAsync();
            var medical = onProvisional.Where(x => x.PayComponentCode == "MEDICAL").ToList();
            Assert.Equal(2, medical.Count);
            Assert.Equal((EntitlementSources.Carried, (DateOnly?)provisional.StartDate), (medical[0].Source, medical[0].EffectiveTo));
            Assert.Equal((EntitlementSources.Exception, CoverageTiers.A, provisional.StartDate.AddDays(1)),
                (medical[1].Source, medical[1].CoverageTier, medical[1].EffectiveFrom));
            var ticket = Assert.Single(onProvisional, x => x.PayComponentCode == "AIR_TICKET");
            Assert.Equal(EntitlementSources.Carried, ticket.Source); // kept as is: the carried row simply stays
        }
    }

    /// <summary>A minimal open case for the expiring term, so the rows can cite it (FK).</summary>
    private static async Task<Guid> CaseAsync(ZayraDbContext db, PackageSeed s)
    {
        var renewal = new ContractRenewalCase
        {
            TenantId = s.TenantId, CompanyId = s.Company.Id, EmployeeId = s.Mohammed.PublicId, ExpiringContractId = s.Term.Id,
            ExpiringEndDate = PackageSeed.TermEnd, WorkerNationalityClass = WorkerNationalityClasses.NonSaudi,
            AllowedActions = [ContractActions.RenewAsIs, ContractActions.RenewWithChanges], State = RenewalStates.Open,
            NoticeDueOn = new DateOnly(2026, 12, 2), OfferDueOn = new DateOnly(2026, 11, 18),
        };
        db.ContractRenewalCases.Add(renewal);
        await db.SaveChangesAsync();
        return renewal.Id;
    }
}
