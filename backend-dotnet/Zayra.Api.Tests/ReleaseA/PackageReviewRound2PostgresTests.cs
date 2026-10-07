using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Zayra.Api.Controllers.Compliance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Contracts;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Modules;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// Review round 2 of #192, on PostgreSQL with R0's triggers — the reviewer's probes (ZzReviewProbeR2bTests) as assertions:
/// a change that would need a never-effective fixed benefit removed is refused with a 409 (the database never deletes one),
/// money is rounded the way PostgreSQL rounds it, and two activations for one employee are serialised.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class PackageReviewRound2PostgresTests(PostgresFixture fixture)
{
    private static readonly DateOnly Today = new(2026, 10, 6);

    private static EntitlementWriter Writer(ZayraDbContext db, DateOnly today) => new(db, new FixedTenantClock(today), new EntitlementResolver(db));

    private static ContractsController Contracts(ZayraDbContext db, PackageSeed s, DateOnly today)
    {
        var dispatcher = new ContractTermLifecycleDispatcher([new PackageFreezeOnActivation(Writer(db, today))],
            new TenantModuleService(db, new MemoryCache(new MemoryCacheOptions())));
        return new ContractsController(db, dispatcher, new FixedTenantClock(today))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("tenant_id", s.TenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, s.UserId.ToString()),
                 new Claim(ClaimTypes.Role, "HR Manager"), new Claim("is_group_scope", "true")], "Test")) } },
        };
    }

    private async Task<PackageSeed> SeedAsync(Action<PackageSeed>? tweak = null)
    {
        var s = new PackageSeed();
        tweak?.Invoke(s);
        await using var db = fixture.CreateDb();
        await s.SaveAsync(db);
        db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = s.TenantId, FeatureKey = FeatureKeys.ReleaseA, IsEnabled = true });
        await db.SaveChangesAsync();
        return s;
    }

    private async Task<IActionResult> ActivateAsync(PackageSeed s, Guid contractId, DateOnly today)
    {
        await using (var db = fixture.CreateDb())
            await Contracts(db, s, today).UpdateStatus(contractId, new UpdateContractStatusRequest("PendingApproval", null), default);
        await using var db2 = fixture.CreateDb();
        return await Contracts(db2, s, today).UpdateStatus(contractId, new UpdateContractStatusRequest("Active", "HR Lead"), default);
    }

    private async Task<EmployeeContract> SupersedeAsync(PackageSeed s, Guid contractId, DateOnly start, DateOnly? today = null, decimal basic = 8000m)
    {
        await using var db = fixture.CreateDb();
        var result = await Contracts(db, s, today ?? Today).Supersede(contractId,
            new CreateContractRequest(s.Mohammed.PublicId, null, null, null, start, PackageSeed.TermEnd, basic, "SAR", null, null, null), default);
        return (EmployeeContract)Assert.IsType<OkObjectResult>(result).Value!;
    }

    private async Task<string> StatusAsync(Guid contractId)
    {
        await using var db = fixture.CreateDb();
        return await db.EmployeeContracts.Where(x => x.Id == contractId).Select(x => x.Status).SingleAsync();
    }

    private static string Code(IActionResult result) =>
        JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<ConflictObjectResult>(result).Value)).RootElement.GetProperty("error").GetString()!;

    [Fact]
    public async Task TheDatabaseRefusesToDeleteAFixedRow_TheCloseOnlyTriggersDeleteClause()
    {
        var s = await SeedAsync();
        await using (var db = fixture.CreateDb())
        { await Writer(db, PackageSeed.FreezeDay).FreezeTermAsync(s.TenantId, s.Term.Id, default); await db.SaveChangesAsync(); }
        await using var db2 = fixture.CreateDb();
        db2.EmployeeEntitlements.Remove(await db2.EmployeeEntitlements.FirstAsync(x => x.TenantId == s.TenantId));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
        // employee_entitlements_close_only(): IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'ENTITLEMENT_CLOSE_ONLY: … is history and is never removed'
        Assert.Contains("ENTITLEMENT_CLOSE_ONLY", ex.InnerException!.Message);
        Assert.Contains("never removed", ex.InnerException.Message);
        Assert.Equal(PackageReasons.CloseOnly, PackageReasons.FromDatabase(ex));
    }

    [Fact]
    public async Task ASameDayAmendment_IsRefusedAtSupersede_TheCurrentVersionStaysInForce_AndTheNextDayItWorks()
    {
        var s = await SeedAsync();
        // The running term's package came the four-eyes way: proposed and confirmed today, from today.
        await using (var db = fixture.CreateDb())
        {
            var writer = Writer(db, Today);
            await writer.WriteConfirmedProposalAsync(s.TenantId, (await writer.ProposeAsync(s.TenantId, s.Term.Id, default))!, default);
            await db.SaveChangesAsync();
        }
        await using (var db = fixture.CreateDb())
        {
            var refused = await Contracts(db, s, Today).Supersede(s.Term.Id,
                new CreateContractRequest(s.Mohammed.PublicId, null, null, null, Today, PackageSeed.TermEnd, 8000m, "SAR", null, null, null), default);
            Assert.Equal(PackageReasons.RowNeverTookEffect, Code(refused));
            Assert.Contains("\"possibleFrom\":\"2026-10-07\"", JsonSerializer.Serialize(((ObjectResult)refused).Value));
        }
        // Nothing changed: the current version is still the one in force, with its package.
        Assert.Equal("Active", await StatusAsync(s.Term.Id));
        await using (var db = fixture.CreateDb())
        {
            Assert.Equal(1, await db.EmployeeContracts.CountAsync(x => x.TenantId == s.TenantId && x.EmployeeId == s.Mohammed.PublicId));
            var package = await new EntitlementResolver(db).ResolveAsync(s.TenantId, s.Mohammed.Id, Today, default);
            Assert.Equal(s.Term.Id, package.ContractId);
            Assert.Contains(package.Lines, l => l.ComponentCode == "MEDICAL" && l.Source == "ContractFrozen");
        }

        // Recovery: from the next day the amendment goes through and carries the package forward.
        var v2 = await SupersedeAsync(s, s.Term.Id, Today.AddDays(1), Today.AddDays(1));
        Assert.IsType<OkObjectResult>(await ActivateAsync(s, v2.Id, Today.AddDays(1)));
        await using var verify = fixture.CreateDb();
        Assert.All(await verify.EmployeeEntitlements.Where(x => x.ContractId == s.Term.Id).ToListAsync(), r => Assert.Equal((DateOnly?)Today, r.EffectiveTo));
        var carried = await verify.EmployeeEntitlements.Where(x => x.ContractId == v2.Id).ToListAsync();
        Assert.Equal(2, carried.Count);
        Assert.All(carried, r => Assert.Equal((EntitlementSources.Carried, Today.AddDays(1)), (r.Source, r.EffectiveFrom)));
    }

    [Fact]
    public async Task CorrectingATermBeforeItStarts_WithTheSameStart_IsRefusedAtSupersede_BeforeAnythingChanges()
    {
        var s = await SeedAsync();
        await using (var db = fixture.CreateDb())
        { await Writer(db, PackageSeed.FreezeDay).FreezeTermAsync(s.TenantId, s.Term.Id, default); await db.SaveChangesAsync(); }
        await using (var db = fixture.CreateDb())
        {
            var refused = await Contracts(db, s, PackageSeed.FreezeDay).Supersede(s.Term.Id,
                new CreateContractRequest(s.Mohammed.PublicId, null, null, null, PackageSeed.TermStart, PackageSeed.TermEnd, 8000m, "SAR", null, null, null), default);
            Assert.Equal(PackageReasons.RowNeverTookEffect, Code(refused));
        }
        Assert.Equal("Active", await StatusAsync(s.Term.Id)); // refused before anything changed
    }

    [Fact]
    public async Task TerminatingATermBeforeItStarts_IsRefusedWith409_LeavingNoLivePackageBehind()
    {
        var s = await SeedAsync();
        await using (var db = fixture.CreateDb())
        { await Writer(db, PackageSeed.FreezeDay).FreezeTermAsync(s.TenantId, s.Term.Id, default); await db.SaveChangesAsync(); }
        await using (var db = fixture.CreateDb())
        {
            var refused = await Contracts(db, s, PackageSeed.FreezeDay).UpdateStatus(s.Term.Id, new UpdateContractStatusRequest("Terminated", null), default);
            Assert.Equal(PackageReasons.RowNeverTookEffect, Code(refused));
        }
        Assert.Equal("Active", await StatusAsync(s.Term.Id));
    }

    [Fact]
    public async Task APercentOfBasicBenefit_IsRoundedLikePostgres_ThroughRealActivationAndAnAmendment()
    {
        var s = await SeedAsync(seed =>
        {
            seed.Term.Status = "PendingApproval";
            seed.Cell("PCT_BENEFIT", PayEntitlementClasses.Contractual, GradeEntitlementValueTypes.PercentOfBasic, rate: 0.25m, period: EntitlementLimitPeriods.Monthly);
            seed.Salary.BasicSalary = 8000.10m;
            seed.Salary.HousingAllowance = 2000.03m; // the salary CHECK: round(8000.10 × 0.25, 2) in PostgreSQL
        });
        await using (var db = fixture.CreateDb())
            Assert.IsType<OkObjectResult>(await Contracts(db, s, PackageSeed.FreezeDay).UpdateStatus(s.Term.Id, new UpdateContractStatusRequest("Active", "HR Lead"), default));
        await using (var db = fixture.CreateDb())
            Assert.Equal(2000.03m, (await db.EmployeeEntitlements.SingleAsync(x => x.TenantId == s.TenantId && x.PayComponentCode == "PCT_BENEFIT")).ResolvedAmount);

        // The amendment re-resolves the carried row; the trigger recomputes round(basic × rate, 2) and must agree.
        var v2 = await SupersedeAsync(s, s.Term.Id, new DateOnly(2026, 11, 1));
        Assert.IsType<OkObjectResult>(await ActivateAsync(s, v2.Id, new DateOnly(2026, 10, 20)));
        await using var verify = fixture.CreateDb();
        var carried = await verify.EmployeeEntitlements.SingleAsync(x => x.ContractId == v2.Id && x.PayComponentCode == "PCT_BENEFIT");
        Assert.Equal((EntitlementSources.Carried, (decimal?)2000.03m), (carried.Source, carried.ResolvedAmount));
    }

    [Fact]
    public async Task TwoActivationsForOneEmployee_AreSerialised_NeitherFailsAndNoBenefitIsFixedTwice()
    {
        var s = await SeedAsync(seed => seed.Term.Status = "PendingApproval");
        var other = new EmployeeContract { TenantId = s.TenantId, CompanyId = s.Company.Id, EmployeeId = s.Mohammed.PublicId, EmployeeName = "Mohammed",
            ContractNumber = "CON-PARALLEL", Status = "PendingApproval", StartDate = new DateOnly(2026, 6, 1), EndDate = new DateOnly(2027, 5, 31),
            BasicSalary = 8000m, CurrencyCode = "SAR", WorkerNationalityClass = WorkerNationalityClasses.NonSaudi };
        await using (var db = fixture.CreateDb()) { db.EmployeeContracts.Add(other); await db.SaveChangesAsync(); }

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;
        var results = await Task.WhenAll(new[] { s.Term.Id, other.Id }.Select(async id =>
        {
            await using var db = fixture.CreateRetryingDb();
            if (Interlocked.Increment(ref arrived) == 2) ready.SetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            return await Contracts(db, s, PackageSeed.FreezeDay).UpdateStatus(id, new UpdateContractStatusRequest("Active", "HR Lead"), default);
        }));

        Assert.All(results, r => Assert.IsType<OkObjectResult>(r));
        await using var verify = fixture.CreateDb();
        var rows = await verify.EmployeeEntitlements.Where(x => x.TenantId == s.TenantId).ToListAsync();
        Assert.Equal(2, rows.Count); // medical and ticket once — the later activation skipped them as an overlap
        Assert.Single(rows.Select(r => r.ContractId).Distinct());
    }
}
