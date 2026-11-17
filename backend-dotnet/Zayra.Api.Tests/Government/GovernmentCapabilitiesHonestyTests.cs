using FluentAssertions;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Infrastructure.Government.Credentials;
using Zayra.Api.Infrastructure.Qiwa;
using Zayra.Api.Models;
using Zayra.Api.Tests.FieldEncryption;
using Microsoft.Extensions.Logging.Abstractions;
using static Zayra.Api.Tests.Government.GovernmentLiveGateTests;

namespace Zayra.Api.Tests.Government;

/// <summary>
/// CTO-GATE 8.4.1: the capabilities screen says "Connected" only for a LIVE production credential that passes the gate.
/// A credential that only exists in the sandbox reads "Sandbox connected: test data only" (with an Arabic equivalent).
/// A synthetic live GOSI adapter stands in for a real one; no network is touched.
/// </summary>
public sealed class GovernmentCapabilitiesHonestyTests
{
    private const string Secret = "synthetic-client-secret-0001";

    private sealed class LiveGosiDouble : IGosiProvider
    {
        public string Body => GovernmentBodies.Gosi;
        public string AdapterName => "synthetic-live";
        public bool IsLiveIntegration => true;
        public Task<GovernmentProviderCapabilities> GetCapabilitiesAsync(CancellationToken ct) =>
            Task.FromResult(new GovernmentProviderCapabilities(Body, "Synthetic", true, [], "synthetic"));
    }

    private static async Task<GovernmentCapabilityDto> GosiAsync(string[] credentialEnvironments, bool liveAdapter = true, bool agreement = true)
    {
        var kit = new FeKit();
        await using var db = kit.Db(interceptors: false);
        var tenant = Guid.NewGuid();
        db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = tenant, FeatureKey = FeatureKeys.GovGosiApi, IsEnabled = true, ConfigJson = GovernmentApiTests.StampedConsent() });
        await db.SaveChangesAsync();
        var pairs = new List<(string, string?)> { (GovernmentCredentialStore.AcceptLiveCredentialsKey, "true") };
        if (agreement) pairs.Add((GovernmentLivePolicy.PartnerAgreementKey(GovernmentBodies.Gosi), "AGR-SYNTHETIC"));
        var config = Config(pairs.ToArray());
        var store = new GovernmentCredentialStore(db, kit.Keys, kit.Kek, kit.Options, new AuditService(db), config);
        foreach (var environment in credentialEnvironments)
            await store.SaveAsync(tenant, null, GovernmentBodies.Gosi, environment, GovernmentCredentialKinds.PrivateKey, Secret, null,
                new Zayra.Api.Application.Auth.RequestContext("ip", "ua", Guid.NewGuid(), tenant), default,
                new GovernmentCredentialBinding(BaseUrlHost: "api.gosi.example.test"));
        IGovernmentProvider gosi = liveAdapter ? new LiveGosiDouble() : new DisabledGosiProvider();
        var service = new GovernmentCapabilitiesService(db, config, new SandboxQiwaApiAdapter(NullLogger<SandboxQiwaApiAdapter>.Instance),
            [gosi, new DisabledMuqeemProvider(), new DisabledTakamolProvider()], store);
        return (await service.GetAsync(tenant, default)).Single(c => c.Body == GovernmentBodies.Gosi);
    }

    [Fact]
    public async Task LiveProductionCredential_ThroughTheGate_ReadsConnected()
    {
        var c = await GosiAsync(["production"]);
        c.Mode.Should().Be(GovernmentCapabilityModes.Live);
        c.ReasonCode.Should().Be(GovernmentCapabilitiesService.ReasonConnected);
        c.ReasonText.Should().StartWith("Connected");
    }

    [Fact]
    public async Task SandboxOnlyCredential_NeverReadsConnected_ReadsSandboxConnectedTestDataOnly()
    {
        var c = await GosiAsync(["sandbox"]);
        c.Mode.Should().NotBe(GovernmentCapabilityModes.Live);
        c.Mode.Should().Be(GovernmentCapabilityModes.Sandbox);
        c.ReasonCode.Should().Be(GovernmentCapabilitiesService.ReasonSandboxConnected);
        c.ReasonText.Should().StartWith("Sandbox connected: test data only");
        c.ReasonText.Should().NotStartWith("Connected");
        c.ReasonTextAr.Should().StartWith("متصل ببيئة الاختبار: بيانات تجريبية فقط");
    }

    [Fact]
    public async Task SandboxAndProductionCredentials_ReadConnected_BecauseProductionPassesTheGate()
    {
        var c = await GosiAsync(["sandbox", "production"]);
        c.Mode.Should().Be(GovernmentCapabilityModes.Live);
        c.ReasonCode.Should().Be(GovernmentCapabilitiesService.ReasonConnected);
    }

    [Fact]
    public async Task SandboxCredential_ButNoLiveAdapter_StaysNotConnected()
    {
        var c = await GosiAsync(["sandbox"], liveAdapter: false);
        c.Mode.Should().Be(GovernmentCapabilityModes.Disabled);
        c.ReasonCode.Should().Be(GovernmentCapabilitiesService.ReasonNoLiveAdapter);
    }

    [Fact]
    public async Task ProductionCredential_WithoutThePartnerAgreement_DoesNotReadConnected()
    {
        var c = await GosiAsync(["production"], agreement: false);
        c.Mode.Should().Be(GovernmentCapabilityModes.Disabled);
        c.ReasonCode.Should().Be(GovernmentLivePolicy.NoPartnerAgreement);
    }

    [Fact]
    public async Task NoCredential_ReadsNotConnected()
    {
        var c = await GosiAsync([]);
        c.Mode.Should().Be(GovernmentCapabilityModes.Disabled);
        c.ReasonCode.Should().Be(GovernmentLivePolicy.NoCredential);
        c.ReasonText.Should().StartWith("Not connected");
    }

    [Fact]
    public void ArabicConnectedText_IsNeverUsedForTheSandboxState()
    {
        var sandbox = GovernmentCapabilitiesService.ReasonTexts[GovernmentCapabilitiesService.ReasonSandboxConnected];
        var live = GovernmentCapabilitiesService.ReasonTexts[GovernmentCapabilitiesService.ReasonConnected];
        sandbox.En.Should().NotBe(live.En);
        sandbox.Ar.Should().NotBe(live.Ar);
        sandbox.Ar.Should().Contain("ببيئة الاختبار");
    }
}
