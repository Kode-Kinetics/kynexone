using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Filters;
using Zayra.Api.Infrastructure.Modules;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Proves that switching a module CHANGES WHAT THE PRODUCT DOES.
///
/// <para>Deliberately not a round-trip test. Saving a setting and reading it back is what let
/// twenty dead settings ship in this codebase; every test here asserts a downstream consumer's
/// behaviour — the API guard's verdict, the disabled-key projection the navigation reads, and the
/// write API's refusals — rather than the contents of a row.</para>
/// </summary>
public class TenantModuleBehaviourTests
{
    private static ZayraDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ZayraDbContext(opts);
    }

    private static async Task SetCountryAsync(ZayraDbContext db, Guid tenantId, string countryCode)
    {
        db.TenantLocalizationSettings.Add(new TenantLocalizationSetting
        {
            TenantId = tenantId,
            CountryCode = countryCode,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SetFlagAsync(ZayraDbContext db, Guid tenantId, string key, bool enabled)
    {
        db.TenantFeatureFlags.Add(new TenantFeatureFlag
        {
            TenantId = tenantId,
            FeatureKey = key,
            IsEnabled = enabled,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Runs the real global guard against a path and returns the result it set, if any.</summary>
    private static async Task<IActionResult?> GuardVerdictAsync(ZayraDbContext db, Guid tenantId, string path)
    {
        var httpCtx = new DefaultHttpContext();
        httpCtx.Request.Path = path;
        httpCtx.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("tenant_id", tenantId.ToString())], "Test"));

        var actionCtx = new ActionContext(httpCtx, new RouteData(), new ActionDescriptor());
        var ctx = new ActionExecutingContext(actionCtx, [], new Dictionary<string, object?>(), null!);

        var filter = new FeatureFlagGuardFilter(
            new TenantModuleService(db, new MemoryCache(new MemoryCacheOptions())),
            NullLogger<FeatureFlagGuardFilter>.Instance);

        await filter.OnActionExecutionAsync(ctx, () =>
            Task.FromResult(new ActionExecutedContext(actionCtx, [], null!)));

        return ctx.Result;
    }

    // ── 1. An optional module's switch changes the API's answer ──────────────

    [Fact]
    public async Task Recruitment_Enabled_ApiIsServed_Disabled_ApiRefuses()
    {
        var tenantId = Guid.NewGuid();

        // Enabled (no row at all — absent means enabled).
        await using (var db = CreateDb())
        {
            var verdict = await GuardVerdictAsync(db, tenantId, "/api/recruitment/candidates");
            verdict.Should().BeNull("an absent flag means the module is on");
        }

        // Disabled.
        await using (var db = CreateDb())
        {
            await SetFlagAsync(db, tenantId, ModuleKeys.Recruitment, false);
            var verdict = await GuardVerdictAsync(db, tenantId, "/api/recruitment/candidates");
            verdict.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
        }
    }

    /// <summary>
    /// The `finance` flag existed, was priced, was rendered in two admin UIs — and gated nothing,
    /// because the guard mapped `/api/loans` while the controller serves `/api/finance/loans`.
    /// </summary>
    [Fact]
    public async Task Finance_Disabled_NowActuallyBlocksTheLoansController()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        await SetFlagAsync(db, tenantId, ModuleKeys.Finance, false);

        var verdict = await GuardVerdictAsync(db, tenantId, "/api/finance/loans");

        verdict.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
    }

    /// <summary>
    /// `/api/ess/timesheets` must follow Timesheets, not the Core `/api/ess` prefix it sits under.
    /// This is what longest-prefix resolution buys.
    /// </summary>
    [Fact]
    public async Task Timesheets_Disabled_BlocksTheEssSubPath_ButNotEssItself()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        await SetFlagAsync(db, tenantId, ModuleKeys.Timesheets, false);

        (await GuardVerdictAsync(db, tenantId, "/api/ess/timesheets"))
            .Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);

        (await GuardVerdictAsync(db, tenantId, "/api/ess/profile"))
            .Should().BeNull("self-service itself is core and must stay reachable");
    }

    // ── 2. Core modules cannot be switched off, however the row is written ───

    [Theory]
    [InlineData(ModuleKeys.CoreHr, "/api/employees")]
    [InlineData(ModuleKeys.Approvals, "/api/approval-requests")]
    [InlineData(ModuleKeys.Audit, "/api/audit-logs")]
    [InlineData(ModuleKeys.LeaveAttendance, "/api/leave/requests")]
    public async Task CoreModule_StoredDisableFlag_IsIgnoredByTheGuard(string moduleKey, string path)
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        await SetFlagAsync(db, tenantId, moduleKey, false);

        var verdict = await GuardVerdictAsync(db, tenantId, path);

        verdict.Should().BeNull("{0} is load-bearing and a stored false must not take effect", moduleKey);
    }

    // ── 3. Statutory locks are conditional, and the condition is real ────────

    [Fact]
    public async Task Gosi_SaudiTenantRunningPayrollHere_CannotDisable()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        await SetCountryAsync(db, tenantId, "SA");

        var state = await new TenantModuleService(db, new MemoryCache(new MemoryCacheOptions()))
            .GetStateAsync(tenantId);
        var decision = ModuleCatalog.CanDisable(
            ModuleCatalog.TryGet(ModuleKeys.Gosi)!, state.CountryCode, k => !state.StoredDisabledKeys.Contains(k));

        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().Contain("Social Insurance Law");
    }

    [Fact]
    public async Task Gosi_SaudiTenantNotRunningPayrollHere_MayDisable()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        await SetCountryAsync(db, tenantId, "SA");
        await SetFlagAsync(db, tenantId, ModuleKeys.Payroll, false);

        var state = await new TenantModuleService(db, new MemoryCache(new MemoryCacheOptions()))
            .GetStateAsync(tenantId);
        var decision = ModuleCatalog.CanDisable(
            ModuleCatalog.TryGet(ModuleKeys.Gosi)!, state.CountryCode, k => !state.StoredDisabledKeys.Contains(k));

        decision.IsAllowed.Should().BeTrue(
            "the filing obligation follows the wages, and this tenant does not pay them here");
    }

    /// <summary>
    /// The seeded KSA tenant stores <c>country_code = 'SAU'</c> (alpha-3), not <c>'SA'</c>. A plain
    /// string comparison against the catalog's alpha-2 list would have decided that a Saudi tenant
    /// has no Saudi obligations — green unit tests, wrong answer on the only real row. The country
    /// is normalised through CountryCodeStandard before it is compared.
    /// </summary>
    [Theory]
    [InlineData("SA")]
    [InlineData("SAU")]
    [InlineData("sau")]
    [InlineData(" sa ")]
    public void Saudization_SaudiTenant_IsLocked_WhicheverCodeFormIsStored(string stored)
    {
        var decision = ModuleCatalog.CanDisable(
            ModuleCatalog.TryGet(ModuleKeys.Saudization)!, stored, _ => true);

        decision.IsAllowed.Should().BeFalse("'{0}' identifies Saudi Arabia", stored);
    }

    [Theory]
    [InlineData("GB")]
    [InlineData("GBR")]
    public void Saudization_BritishTenant_MayDisable_WhicheverCodeFormIsStored(string stored)
    {
        ModuleCatalog.CanDisable(ModuleCatalog.TryGet(ModuleKeys.Saudization)!, stored, _ => true)
            .IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void Saudization_UnrecognisedCountryCode_IsLockedClosed()
    {
        ModuleCatalog.CanDisable(ModuleCatalog.TryGet(ModuleKeys.Saudization)!, "ZZZ", _ => true)
            .IsAllowed.Should().BeFalse("an unrecognised code proves nothing about the obligation");
    }

    [Fact]
    public void Saudization_NonGccTenant_MayDisable()
    {
        var decision = ModuleCatalog.CanDisable(
            ModuleCatalog.TryGet(ModuleKeys.Saudization)!, "GB", _ => true);

        decision.IsAllowed.Should().BeTrue("a British employer has no Nitaqat obligation");
    }

    [Fact]
    public void Saudization_UnknownCountry_IsLockedClosed()
    {
        var decision = ModuleCatalog.CanDisable(
            ModuleCatalog.TryGet(ModuleKeys.Saudization)!, null, _ => true);

        decision.IsAllowed.Should().BeFalse(
            "an unproven country must not be grounds for switching a statutory module off");
    }

    // ── 4. The effective-state projection the navigation reads ───────────────

    [Fact]
    public async Task DisabledKeysProjection_OmitsLockedModules_SoNavAndApiAgree()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        await SetCountryAsync(db, tenantId, "SA");
        await SetFlagAsync(db, tenantId, ModuleKeys.Saudization, false); // locked: must be ignored
        await SetFlagAsync(db, tenantId, ModuleKeys.Recruitment, false); // optional: must be honoured

        var state = await new TenantModuleService(db, new MemoryCache(new MemoryCacheOptions()))
            .GetStateAsync(tenantId);

        state.EffectiveDisabledKeys.Should().Contain(ModuleKeys.Recruitment);
        state.EffectiveDisabledKeys.Should().NotContain(ModuleKeys.Saudization,
            "hiding the Saudization nav while the API keeps serving it is the disagreement this fixes");
        state.IsEnabled(ModuleKeys.Saudization).Should().BeTrue();
        state.IsEnabled(ModuleKeys.Recruitment).Should().BeFalse();
    }

    // ── 5. The write API refuses rather than storing dead configuration ──────

    private static TenantModulesController MakeController(ZayraDbContext db, Guid tenantId)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var controller = new TenantModulesController(db, new TenantModuleService(db, cache), cache)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim("tenant_id", tenantId.ToString())], "Test")),
                },
            },
        };
        return controller;
    }

    [Fact]
    public async Task Write_LockedModule_Returns409WithMachineReadableCode()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        await SetCountryAsync(db, tenantId, "SA");

        var result = await MakeController(db, tenantId)
            .SetModule(ModuleKeys.Gosi, new SetTenantModuleRequest(false), default);

        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.Value!.ToString().Should().Contain("module_not_disableable");
    }

    [Fact]
    public async Task Write_UnenforceableKey_Returns501_RatherThanStoringADeadFlag()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();

        var result = await MakeController(db, tenantId)
            .SetModule(FeatureKeys.RiskScores, new SetTenantModuleRequest(false), default);

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status501NotImplemented);
        obj.Value!.ToString().Should().Contain("module_not_enforceable");

        (await db.TenantFeatureFlags.CountAsync()).Should().Be(0,
            "refusing must not leave a row behind — that is the dead configuration we are avoiding");
    }

    [Fact]
    public async Task Write_UnknownKey_Returns400()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();

        var result = await MakeController(db, tenantId)
            .SetModule("not_a_module", new SetTenantModuleRequest(false), default);

        result.Should().BeOfType<BadRequestObjectResult>()
            .Which.Value!.ToString().Should().Contain("unknown_module");
    }

    /// <summary>
    /// The end-to-end point of the whole surface: an administrator switches a module off and the
    /// API stops serving it. Previously this was impossible — the write endpoint returned 403
    /// unconditionally and the UI swallowed the error.
    /// </summary>
    [Fact]
    public async Task Write_OptionalModuleOff_ThenTheApiRefusesIt()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();

        (await GuardVerdictAsync(db, tenantId, "/api/performance/cycles"))
            .Should().BeNull("precondition: the module starts enabled");

        var result = await MakeController(db, tenantId)
            .SetModule(ModuleKeys.Performance, new SetTenantModuleRequest(false), default);
        result.Should().BeOfType<OkObjectResult>();

        (await GuardVerdictAsync(db, tenantId, "/api/performance/cycles"))
            .Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task Write_ThenBackOn_RestoresService()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var controller = MakeController(db, tenantId);

        await controller.SetModule(ModuleKeys.Shifts, new SetTenantModuleRequest(false), default);
        (await GuardVerdictAsync(db, tenantId, "/api/shifts/definitions"))
            .Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);

        await controller.SetModule(ModuleKeys.Shifts, new SetTenantModuleRequest(true), default);
        (await GuardVerdictAsync(db, tenantId, "/api/shifts/definitions"))
            .Should().BeNull();
    }

    /// <summary>
    /// A read that started before a toggle must not be able to re-publish the pre-toggle state
    /// into the cache after it.
    ///
    /// <para>Found by driving the browser, not by a unit test: the switch flipped, the write
    /// succeeded, and the navigation kept showing the module for the full two-minute TTL because
    /// a concurrent refresh had re-cached the old state just after the invalidation removed it.
    /// To a user that is identical to a switch that does nothing — the exact defect this whole
    /// surface exists to remove.</para>
    /// </summary>
    [Fact]
    public async Task InFlightRead_CannotRestoreStaleStateAfterAToggle()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var cache = new MemoryCache(new MemoryCacheOptions());

        // Warm the cache with "enabled" and confirm it.
        var svc = new TenantModuleService(db, cache);
        (await svc.GetStateAsync(tenantId)).IsEnabled(ModuleKeys.Recruitment).Should().BeTrue();

        // The write lands: row saved, cache invalidated.
        await SetFlagAsync(db, tenantId, ModuleKeys.Recruitment, false);
        svc.Invalidate(tenantId);

        // A read that began BEFORE the invalidation now completes and tries to publish its old
        // view. Simulated by writing the pre-toggle state under the key that read would have used.
        cache.Set($"modules:{tenantId}:0", TenantModuleService.Build([], null), TenantModuleService.CacheTtl);

        // The next reader must still see the module as off.
        var fresh = await new TenantModuleService(db, cache).GetStateAsync(tenantId);
        fresh.IsEnabled(ModuleKeys.Recruitment).Should().BeFalse(
            "the superseded cache generation must not be read back");
    }

    // ── 6. Notification ownership ────────────────────────────────────────────

    [Fact]
    public void PayslipNotifications_BelongToPayroll_AndSecurityBelongsToNoOptionalModule()
    {
        ModuleCatalog.ResolveNotificationCategory("payslip")!.Key.Should().Be(ModuleKeys.Payroll);
        ModuleCatalog.ResolveNotificationCategory("overtime")!.Key.Should().Be(ModuleKeys.Overtime);

        var security = ModuleCatalog.ResolveNotificationCategory("security");
        security!.Lock.Should().Be(ModuleLock.Core,
            "security notices must never be suppressible by a module switch");
    }
}
