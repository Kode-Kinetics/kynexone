using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Zayra.Api.Application.Entitlements;
using Zayra.Api.Controllers.Compliance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Contracts;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Modules;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// Review round 1, P1: a term's package across its life, on PostgreSQL with the R0 triggers and EXCLUDE — the reviewer's
/// probe (ReviewProbeR2Tests) turned into assertions. Activation must never fail because of the freeze: an amendment
/// carries the package forward, a 1-day overlap is skipped with a reason, and ending a term closes its rows.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class PackageLifecyclePostgresTests(PostgresFixture fixture)
{
    private static readonly DateOnly Today = new(2026, 10, 6);

    private static EntitlementWriter Writer(ZayraDbContext db, DateOnly today) => new(db, new FixedTenantClock(today), new EntitlementResolver(db));

    private async Task<PackageSeed> SeedAndFreezeAsync()
    {
        var s = new PackageSeed();
        await using (var db = fixture.CreateDb())
        {
            await s.SaveAsync(db);
            db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = s.TenantId, FeatureKey = FeatureKeys.ReleaseA, IsEnabled = true });
            await db.SaveChangesAsync();
        }
        await using (var db = fixture.CreateDb())
            await FinanceDecisionSerializer.SerializeAsync(db, FinanceDecisionSerializer.ScopeEmployeePackage, s.TenantId, s.Mohammed.PublicId, async () =>
            {
                var r = await Writer(db, PackageSeed.FreezeDay).FreezeTermAsync(s.TenantId, s.Term.Id, default);
                await db.SaveChangesAsync();
                return r;
            });
        return s;
    }

    /// <summary>The contract screen's own activation path: status → Active, the lifecycle hooks, one SaveChanges, no transaction.</summary>
    private static ContractsController Contracts(ZayraDbContext db, PackageSeed s, DateOnly today)
    {
        var hook = new PackageFreezeOnActivation(Writer(db, today));
        var dispatcher = new ContractTermLifecycleDispatcher([hook], new TenantModuleService(db, new MemoryCache(new MemoryCacheOptions())));
        return new ContractsController(db, dispatcher)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("tenant_id", s.TenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, s.UserId.ToString()),
                 new Claim(ClaimTypes.Role, "HR Manager"), new Claim("is_group_scope", "true")], "Test")) } },
        };
    }

    [Fact]
    public async Task ActivatingAnAmendment_AfterTheYearWasFrozen_SavesAndCarriesThePackageForward()
    {
        var s = await SeedAndFreezeAsync();
        // An amendment inside the term: the old version Superseded, v2 Draft from 1 Nov (what Supersede does).
        var v2 = new EmployeeContract { TenantId = s.TenantId, CompanyId = s.Company.Id, EmployeeId = s.Mohammed.PublicId, EmployeeName = "Mohammed",
            ContractNumber = "CON-1b", Status = "PendingApproval", StartDate = new DateOnly(2026, 11, 1), EndDate = PackageSeed.TermEnd, BasicSalary = 9000m,
            CurrencyCode = "SAR", Version = 2, PreviousVersionId = s.Term.Id, WorkerNationalityClass = WorkerNationalityClasses.NonSaudi };
        await using (var db = fixture.CreateDb())
        {
            (await db.EmployeeContracts.SingleAsync(x => x.Id == s.Term.Id)).Status = "Superseded";
            db.EmployeeContracts.Add(v2);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDb())
        {
            var result = await Contracts(db, s, new DateOnly(2026, 10, 20)).UpdateStatus(v2.Id, new UpdateContractStatusRequest("Active", "HR Lead"), default);
            Assert.IsType<OkObjectResult>(result);
        }

        await using (var verify = fixture.CreateDb())
        {
            Assert.Equal("Active", await verify.EmployeeContracts.Where(x => x.Id == v2.Id).Select(x => x.Status).SingleAsync());
            var old = await verify.EmployeeEntitlements.Where(x => x.ContractId == s.Term.Id).ToListAsync();
            Assert.All(old, r => Assert.Equal(new DateOnly(2026, 10, 31), r.EffectiveTo));
            var carried = await verify.EmployeeEntitlements.Where(x => x.ContractId == v2.Id).ToListAsync();
            Assert.Equal(new[] { "AIR_TICKET", "MEDICAL" }, carried.Select(r => r.PayComponentCode).OrderBy(c => c));
            Assert.All(carried, r => Assert.Equal((EntitlementSources.Carried, new DateOnly(2026, 11, 1), (DateOnly?)PackageSeed.TermEnd),
                (r.Source, r.EffectiveFrom, r.EffectiveTo)));
            Assert.All(carried, r => Assert.Contains(old, o => o.Id == r.CarriedFromEntitlementId));
        }
    }

    [Fact]
    public async Task ActivatingATermThatOverlapsTheFrozenYearByOneDay_SavesAndSkipsTheOverlap()
    {
        var s = await SeedAndFreezeAsync();
        var next = new EmployeeContract { TenantId = s.TenantId, CompanyId = s.Company.Id, EmployeeId = s.Mohammed.PublicId, EmployeeName = "Mohammed",
            ContractNumber = "CON-1-RENEWAL", Status = "PendingApproval", StartDate = PackageSeed.TermEnd, EndDate = new DateOnly(2028, 1, 30),
            BasicSalary = 8000m, CurrencyCode = "SAR", WorkerNationalityClass = WorkerNationalityClasses.NonSaudi };
        await using (var db = fixture.CreateDb())
        {
            db.EmployeeContracts.Add(next);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDb())
            Assert.IsType<OkObjectResult>(await Contracts(db, s, Today).UpdateStatus(next.Id, new UpdateContractStatusRequest("Active", "HR Lead"), default));

        await using var verify = fixture.CreateDb();
        Assert.Equal("Active", await verify.EmployeeContracts.Where(x => x.Id == next.Id).Select(x => x.Status).SingleAsync());
        Assert.False(await verify.EmployeeEntitlements.AnyAsync(x => x.ContractId == next.Id), "nothing that would collide is staged");
        Assert.All(await verify.EmployeeEntitlements.Where(x => x.ContractId == s.Term.Id).ToListAsync(),
            r => Assert.Equal((DateOnly?)PackageSeed.TermEnd, r.EffectiveTo));

        // The writer says why, so HR can see it on the panel's freeze action.
        await using var db2 = fixture.CreateDb();
        var writer = Writer(db2, Today);
        (await db2.EmployeeContracts.SingleAsync(x => x.Id == next.Id)).Status = "Active";
        await writer.FreezeTermAsync(s.TenantId, next.Id, default);
        Assert.Equal(new[] { "AIR_TICKET", "MEDICAL" },
            writer.LastSkips.Where(x => x.Code == PackageReasons.TermOverlap).Select(x => x.ComponentCode).OrderBy(c => c));
        Assert.Contains(writer.LastSkips, x => x.ComponentCode == "EDUCATION" && x.Code == PackageReasons.NotInGrade);
    }

    [Fact]
    public async Task TerminatingATerm_ClosesItsPackage_OnTheDay()
    {
        var s = await SeedAndFreezeAsync();
        await using (var db = fixture.CreateDb())
            Assert.IsType<OkObjectResult>(await Contracts(db, s, new DateOnly(2026, 12, 15))
                .UpdateStatus(s.Term.Id, new UpdateContractStatusRequest("Terminated", null), default));

        await using var verify = fixture.CreateDb();
        Assert.All(await verify.EmployeeEntitlements.Where(x => x.ContractId == s.Term.Id).ToListAsync(),
            r => Assert.Equal((DateOnly?)new DateOnly(2026, 12, 15), r.EffectiveTo));
    }

    [Fact]
    public async Task EndingASupersededTerm_WithItsSuccessorKnown_ClosesAtTheSuccessorsStart()
    {
        var s = await SeedAndFreezeAsync();
        await using (var db = fixture.CreateDb())
        {
            db.EmployeeContracts.Add(new EmployeeContract { TenantId = s.TenantId, CompanyId = s.Company.Id, EmployeeId = s.Mohammed.PublicId,
                ContractNumber = "CON-1c", Status = "Draft", StartDate = new DateOnly(2026, 12, 1), EndDate = PackageSeed.TermEnd, PreviousVersionId = s.Term.Id });
            var term = await db.EmployeeContracts.SingleAsync(x => x.Id == s.Term.Id);
            term.Status = "Superseded";
            await new PackageFreezeOnActivation(Writer(db, Today)).OnEndedAsync(term, ContractEndReasons.Superseded, default);
            await db.SaveChangesAsync();
        }
        await using var verify = fixture.CreateDb();
        Assert.All(await verify.EmployeeEntitlements.Where(x => x.ContractId == s.Term.Id).ToListAsync(),
            r => Assert.Equal((DateOnly?)new DateOnly(2026, 11, 30), r.EffectiveTo));
    }
}
