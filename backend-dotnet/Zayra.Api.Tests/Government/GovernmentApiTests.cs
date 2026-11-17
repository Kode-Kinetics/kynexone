using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Data;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.FieldEncryption;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Infrastructure.Government.Credentials;
using Zayra.Api.Infrastructure.Qiwa;
using Zayra.Api.Models;
using Zayra.Api.Tests.FieldEncryption;
using static Zayra.Api.Tests.Government.GovernmentLiveGateTests;

namespace Zayra.Api.Tests.Government;

/// <summary>
/// The metadata-only credential API (PUT/GET/DELETE /api/government/credentials/{body}) and GET /api/government/capabilities.
/// </summary>
public sealed class GovernmentApiTests
{
    private const string Secret = "synthetic-client-secret-0001";

    private static T WithUser<T>(T controller, Guid tenant, Guid user) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("tenant_id", tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, user.ToString())], "test")),
            },
        };
        return controller;
    }

    /// <summary>A fresh step-up proof header: each credential change spends its own.</summary>
    private static void NewProof(ControllerBase controller) =>
        controller.ControllerContext.HttpContext.Request.Headers[Zayra.Api.Infrastructure.Auth.StepUpService.HeaderName] = $"synthetic-proof-{Guid.NewGuid():N}";

    /// <summary>A consent record as the platform endpoint stores it (server-stamped).</summary>
    internal static string StampedConsent(string category = "contributions") =>
        GovernmentOptIn.Stamp($"{{\"consent\":{{\"agreementReference\":\"AGR-2026-001\",\"dataCategories\":[\"{category}\"]}}}}",
            Guid.NewGuid(), "owner@platform.test", DateTime.UtcNow);

    private static string Json(IActionResult result) => JsonSerializer.Serialize(((ObjectResult)result).Value);

    [Fact]
    public async Task Put_WithoutAStepUp_IsRefused_AndStoresNothing()
    {
        var kit = new FeKit();
        await using var db = kit.Db(interceptors: false);
        var tenant = Guid.NewGuid();
        var controller = WithUser(new GovernmentCredentialsController(new GovernmentCredentialStore(db, kit.Keys, kit.Kek, kit.Options, new AuditService(db)),
            StepUpTestKit.Real(db)), tenant, Guid.NewGuid());

        var result = await controller.Put(GovernmentBodies.Gosi, new GovernmentCredentialSaveRequest("sandbox", GovernmentCredentialKinds.OAuthClient, Secret, null, null), default);

        ((ObjectResult)result).StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        Json(result).Should().Contain("step_up_required");
        (await db.GovernmentCredentials.CountAsync()).Should().Be(0);
        var delete = await controller.Delete(GovernmentBodies.Gosi, "sandbox", null, default);
        ((ObjectResult)delete).StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Put_Get_Delete_ReturnMetadataOnly_NeverTheSecret()
    {
        var kit = new FeKit();
        await using var db = kit.Db(interceptors: false);
        var tenant = Guid.NewGuid();
        var controller = WithUser(new GovernmentCredentialsController(new GovernmentCredentialStore(db, kit.Keys, kit.Kek, kit.Options, new AuditService(db)),
            StepUpTestKit.AlwaysConfirmed(db)), tenant, Guid.NewGuid());

        NewProof(controller);
        var put = await controller.Put(GovernmentBodies.BankAnb, new GovernmentCredentialSaveRequest("sandbox", GovernmentCredentialKinds.OAuthClient, Secret, null, null), default);
        var get = await controller.Get(GovernmentBodies.BankAnb, "sandbox", null, default);

        put.Should().BeOfType<OkObjectResult>();
        get.Should().BeOfType<OkObjectResult>();
        foreach (var json in new[] { Json(put), Json(get) })
        {
            json.Should().NotContain(Secret).And.NotContain("synthetic-client");
            json.Should().Contain("\"Fingerprint\"").And.Contain("\"Version\":1");
        }
        NewProof(controller);
        (await controller.Delete(GovernmentBodies.BankAnb, "sandbox", null, default)).Should().BeOfType<NoContentResult>();
        (await controller.Get(GovernmentBodies.BankAnb, "sandbox", null, default)).Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task WithoutAKek_TheApiAnswers503_AndStoresNothing()
    {
        var kit = new FeKit();
        await using var db = kit.Db(interceptors: false);
        var noKek = new ConfigKeyEncryptionKeyProvider(new FieldEncryptionOptions());
        var controller = WithUser(new GovernmentCredentialsController(new GovernmentCredentialStore(db, new FieldKeyService(kit.Store, noKek), noKek, new FieldEncryptionOptions(), new AuditService(db)),
            StepUpTestKit.AlwaysConfirmed(db)), Guid.NewGuid(), Guid.NewGuid());

        var put = await controller.Put(GovernmentBodies.Gosi, new GovernmentCredentialSaveRequest("sandbox", GovernmentCredentialKinds.ApiKey, Secret, null, null), default);

        ((ObjectResult)put).StatusCode.Should().Be(503);
        Json(put).Should().Contain(GovernmentCredentialStore.UnavailableCode);
        (await db.GovernmentCredentials.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("unknown_body", "sandbox", GovernmentCredentialKinds.ApiKey)]
    [InlineData(GovernmentBodies.Gosi, "staging", GovernmentCredentialKinds.ApiKey)]
    [InlineData(GovernmentBodies.Gosi, "sandbox", "password")]
    public async Task Put_RefusesAnUnknownBodyEnvironmentOrKind(string body, string environment, string kind)
    {
        var kit = new FeKit();
        await using var db = kit.Db(interceptors: false);
        var controller = WithUser(new GovernmentCredentialsController(new GovernmentCredentialStore(db, kit.Keys, kit.Kek, kit.Options, new AuditService(db)),
            StepUpTestKit.AlwaysConfirmed(db)), Guid.NewGuid(), Guid.NewGuid());

        var result = await controller.Put(body, new GovernmentCredentialSaveRequest(environment, kind, Secret, null, null), default);

        ((ObjectResult)result).StatusCode.Should().BeOneOf(400, 404);
        (await db.GovernmentCredentials.CountAsync()).Should().Be(0);
    }

    [Fact]
    public void TheCredentialEndpoints_RequireTheGovernmentPermission()
    {
        foreach (var name in new[] { nameof(GovernmentCredentialsController.Get), nameof(GovernmentCredentialsController.Put), nameof(GovernmentCredentialsController.Delete) })
        {
            var attribute = typeof(GovernmentCredentialsController).GetMethod(name)!
                .GetCustomAttributes(typeof(Zayra.Api.Infrastructure.Authorization.HasPermissionAttribute), false).Single();
            ((Zayra.Api.Infrastructure.Authorization.HasPermissionAttribute)attribute).Policy.Should().Contain(GovernmentCredentialsController.Permission);
        }
    }

    // ── Capabilities ───────────────────────────────────────────────────────────────────────────────────

    private static GovernmentCapabilitiesService Capabilities(FeKit kit, ZayraDbContext db, Microsoft.Extensions.Configuration.IConfiguration config, IQiwaApiAdapter? qiwa = null) =>
        new(db, config, qiwa ?? new SandboxQiwaApiAdapter(NullLogger<SandboxQiwaApiAdapter>.Instance),
            [new DisabledGosiProvider(), new DisabledMuqeemProvider(), new DisabledTakamolProvider()],
            new GovernmentCredentialStore(db, kit.Keys, kit.Kek, kit.Options, new AuditService(db), config));

    [Fact]
    public async Task Capabilities_OutOfTheBox_AreHonest_AndNeverLive()
    {
        var kit = new FeKit();
        await using var db = kit.Db(interceptors: false);
        var tenant = Guid.NewGuid();

        var list = await Capabilities(kit, db, Config()).GetAsync(tenant, default);

        list.Select(c => c.Body).Should().Equal(GovernmentBodies.All);
        list.Should().NotContain(c => c.Mode == GovernmentCapabilityModes.Live);
        list.Single(c => c.Body == GovernmentBodies.Qiwa).Mode.Should().Be(GovernmentCapabilityModes.Simulated);
        foreach (var body in new[] { GovernmentBodies.Gosi, GovernmentBodies.Muqeem, GovernmentBodies.Takamol, GovernmentBodies.BankAnb })
        {
            var c = list.Single(x => x.Body == body);
            c.Mode.Should().Be(GovernmentCapabilityModes.Disabled);
            c.ReasonCode.Should().Be(GovernmentLivePolicy.NoPartnerAgreement);
            c.ChannelsAvailable.Should().BeEmpty();
        }
        list.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.ReasonText) && !string.IsNullOrWhiteSpace(c.ReasonTextAr));
        list.Should().OnlyContain(c => !c.ReasonText.Contains("synced", StringComparison.OrdinalIgnoreCase)
            && !c.ReasonText.Contains("integrated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Capabilities_EveryGateOpen_ButNoLiveAdapter_StillSaysNotConnected()
    {
        var kit = new FeKit();
        await using var db = kit.Db(interceptors: false);
        var tenant = Guid.NewGuid();
        db.TenantFeatureFlags.Add(new TenantFeatureFlag
        {
            TenantId = tenant, FeatureKey = FeatureKeys.GovGosiApi, IsEnabled = true,
            ConfigJson = StampedConsent(),
        });
        await db.SaveChangesAsync();
        await new GovernmentCredentialStore(db, kit.Keys, kit.Kek, kit.Options, new AuditService(db)).SaveAsync(tenant, null, GovernmentBodies.Gosi, "sandbox",
            GovernmentCredentialKinds.OAuthClient, Secret, null, new Zayra.Api.Application.Auth.RequestContext("ip", "ua", Guid.NewGuid(), tenant), default);
        var config = Config((GovernmentLivePolicy.PartnerAgreementKey(GovernmentBodies.Gosi), "AGR-SYNTHETIC"),
            (GovernmentCredentialStore.AcceptLiveCredentialsKey, "true"));
        var service = Capabilities(kit, db, config);

        (await service.DecideAsync(tenant, null, GovernmentBodies.Gosi, "sandbox", default)).IsOpen.Should().BeTrue();
        (await service.DecideAsync(tenant, null, GovernmentBodies.Gosi, "production", default)).ReasonCode.Should().Be(GovernmentLivePolicy.NoCredential,
            "a sandbox credential never opens a production call");
        var gosi = (await service.GetAsync(tenant, default)).Single(c => c.Body == GovernmentBodies.Gosi);

        gosi.Mode.Should().Be(GovernmentCapabilityModes.Disabled, "the GOSI provider is the disabled placeholder; only a live adapter makes it live");
        gosi.ReasonCode.Should().Be(GovernmentCapabilitiesService.ReasonNoLiveAdapter);
        // A tenant without the consent record is not open even with the deployment agreement.
        (await service.DecideAsync(Guid.NewGuid(), null, GovernmentBodies.Gosi, "sandbox", default)).ReasonCode.Should().Be(GovernmentLivePolicy.TenantNotOptedIn);
    }

    [Fact]
    public async Task Capabilities_Endpoint_AnswersForTheCallersTenant_NotAnother()
    {
        var kit = new FeKit();
        await using var db = kit.Db(interceptors: false);
        var saudiTenant = Guid.NewGuid();
        db.Companies.Add(new Company { Id = Guid.NewGuid(), TenantId = saudiTenant, LegalNameEn = "Synthetic KSA", CountryCode = "SA", IsActive = true });
        await db.SaveChangesAsync();

        async Task<GovernmentCapabilityDto> BankFor(Guid tenant)
        {
            var controller = WithUser(new GovernmentCapabilitiesController(Capabilities(kit, db, Config())), tenant, Guid.NewGuid());
            var list = ((OkObjectResult)(await controller.Get(default)).Result!).Value.Should().BeAssignableTo<IReadOnlyList<GovernmentCapabilityDto>>().Subject;
            list.Should().HaveCount(GovernmentBodies.All.Count);
            return list.Single(c => c.Body == GovernmentBodies.BankAnb);
        }

        (await BankFor(saudiTenant)).Mode.Should().Be(GovernmentCapabilityModes.FileOnly, "this tenant has a Saudi company, so the bank file is open");
        (await BankFor(Guid.NewGuid())).Mode.Should().Be(GovernmentCapabilityModes.Disabled, "another tenant's company opens nothing here");
    }

    [Fact]
    public async Task Capabilities_OneTestPerMissingGate_ReportsThatGate()
    {
        var kit = new FeKit();
        await using var db = kit.Db(interceptors: false);
        var tenant = Guid.NewGuid();
        var agreed = Config((GovernmentLivePolicy.PartnerAgreementKey(GovernmentBodies.Gosi), "AGR-SYNTHETIC"));
        var service = Capabilities(kit, db, agreed);

        // No consent yet.
        (await service.DecideAsync(tenant, null, GovernmentBodies.Gosi, "sandbox", default)).ReasonCode.Should().Be(GovernmentLivePolicy.TenantNotOptedIn);
        db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = tenant, FeatureKey = FeatureKeys.GovGosiApi, IsEnabled = true, ConfigJson = StampedConsent() });
        await db.SaveChangesAsync();
        // Consent, no credential.
        (await service.DecideAsync(tenant, null, GovernmentBodies.Gosi, "sandbox", default)).ReasonCode.Should().Be(GovernmentLivePolicy.NoCredential);
        await new GovernmentCredentialStore(db, kit.Keys, kit.Kek, kit.Options, new AuditService(db)).SaveAsync(tenant, null, GovernmentBodies.Gosi, "sandbox",
            GovernmentCredentialKinds.OAuthClient, Secret, null, new Zayra.Api.Application.Auth.RequestContext("ip", "ua", Guid.NewGuid(), tenant), default);
        (await service.DecideAsync(tenant, null, GovernmentBodies.Gosi, "sandbox", default)).IsOpen.Should().BeTrue();
        // No agreement.
        (await Capabilities(kit, db, Config()).DecideAsync(tenant, null, GovernmentBodies.Gosi, "sandbox", default)).ReasonCode.Should().Be(GovernmentLivePolicy.NoPartnerAgreement);
        // Operator switch off.
        var killed = Config((GovernmentLivePolicy.PartnerAgreementKey(GovernmentBodies.Gosi), "AGR-SYNTHETIC"), (GovernmentLivePolicy.EnabledKey(GovernmentBodies.Gosi), "false"));
        (await Capabilities(kit, db, killed).DecideAsync(tenant, null, GovernmentBodies.Gosi, "sandbox", default)).ReasonCode.Should().Be(GovernmentLivePolicy.OperatorDisabled);
        // Writes need the legal clearance on top, and the screen says so.
        (await service.DecideLiveWriteAsync(tenant, null, GovernmentBodies.Gosi, "sandbox", default)).ReasonCode.Should().Be(GovernmentLivePolicy.LiveWritesNeedLegalClearance);
        (await service.GetAsync(tenant, default)).Single(c => c.Body == GovernmentBodies.Gosi).WritesAllowed.Should().BeFalse();
    }

    // ── Readiness evidence ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Readiness_ListsEveryBody_Generically_AndNoneIsLiveWithoutALiveAdapter()
    {
        var none = Zayra.Api.Infrastructure.Operations.ProductionReadinessEvidence.GovernmentDependencies(Config());
        none.Keys.Should().BeEquivalentTo(GovernmentBodies.All);
        none[GovernmentBodies.Qiwa].Simulated.Should().BeTrue("the default Qiwa adapter is the simulator");
        none[GovernmentBodies.Gosi].Mode.Should().Be("disabled");

        var agreed = Zayra.Api.Infrastructure.Operations.ProductionReadinessEvidence.GovernmentDependencies(
            Config((GovernmentLivePolicy.PartnerAgreementKey(GovernmentBodies.BankAnb), "AGR-SYNTHETIC"),
                (GovernmentLivePolicy.EnabledKey(GovernmentBodies.Gosi), "false"),
                (GovernmentLivePolicy.PartnerAgreementKey(GovernmentBodies.Gosi), "AGR-SYNTHETIC")));
        agreed[GovernmentBodies.BankAnb].Mode.Should().Be("not_connected");
        agreed[GovernmentBodies.Gosi].Mode.Should().Be("disabled", "the operator switch wins over a recorded agreement");
        agreed.Values.Should().NotContain(m => m.Mode.Contains("live") && m.Configured);
    }
}
