using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Infrastructure.Qiwa;

namespace Zayra.Api.Tests.Government;

/// <summary>
/// The live gate (architecture 3.3): G1 deployment agreement, G2 tenant consent, G3 credential, G4 operator switch. Every one
/// of the 16 combinations is checked; only all-true opens, and a refused decision never reaches a socket.
/// </summary>
public sealed class GovernmentLiveGateTests
{
    public static IEnumerable<object[]> AllSixteen()
    {
        for (var mask = 0; mask < 16; mask++)
            yield return [(mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0, (mask & 8) != 0];
    }

    [Theory]
    [MemberData(nameof(AllSixteen))]
    public void TruthTable_OnlyAllFourTrue_Opens(bool agreement, bool consent, bool credential, bool operatorOn)
    {
        var decision = GovernmentLivePolicy.Decide(GovernmentBodies.Gosi, new GovernmentLiveGates(agreement, consent, credential, operatorOn));

        decision.IsOpen.Should().Be(agreement && consent && credential && operatorOn);
        if (decision.IsOpen)
        {
            decision.ReasonCode.Should().BeNull();
            decision.RefusalCode.Should().BeNull();
        }
        else
        {
            decision.RefusalCode.Should().Be(GovernmentLivePolicy.RefusedCode);
            decision.ReasonCode.Should().BeOneOf(GovernmentLivePolicy.OperatorDisabled, GovernmentLivePolicy.NoPartnerAgreement,
                GovernmentLivePolicy.TenantNotOptedIn, GovernmentLivePolicy.NoCredential);
        }
    }

    [Theory]
    [MemberData(nameof(AllSixteen))]
    public async Task TruthTable_ARefusedDecision_OpensZeroSockets(bool agreement, bool consent, bool credential, bool operatorOn)
    {
        var handler = new CountingHandler();
        var http = new GovernmentHttp(new SingleHandlerFactory(handler), Config((GovernmentHttp.BaseUrlKey(GovernmentBodies.Gosi, "sandbox"), "https://gosi-sandbox.example.test")));
        var decision = GovernmentLivePolicy.Decide(GovernmentBodies.Gosi, new GovernmentLiveGates(agreement, consent, credential, operatorOn));

        Func<Task> call = async () =>
        {
            using var _ = await http.ExchangeAsync(decision, "sandbox", HttpMethod.Get, "/status", null, null, CancellationToken.None);
        };

        if (decision.IsOpen) await call.Should().NotThrowAsync();
        else (await call.Should().ThrowAsync<GovernmentTransportRefusedException>()).Which.Code.Should().Be(GovernmentLivePolicy.RefusedCode);
        handler.Calls.Should().Be(decision.IsOpen ? 1 : 0);
    }

    [Theory]
    [InlineData(GovernmentBodies.Qiwa)]
    [InlineData(GovernmentBodies.Gosi)]
    [InlineData(GovernmentBodies.Muqeem)]
    [InlineData(GovernmentBodies.Takamol)]
    [InlineData(GovernmentBodies.BankAnb)]
    public void DefaultConfiguration_KeepsEveryBodyClosed_EvenWithConsentAndACredential(string body)
    {
        // An empty deployment configuration records no agreement, so nothing opens whatever the tenant has done.
        var decision = GovernmentLivePolicy.Decide(Config(), body, tenantConsent: true, credential: true);
        decision.IsOpen.Should().BeFalse();
        decision.ReasonCode.Should().Be(GovernmentLivePolicy.NoPartnerAgreement);
        var write = GovernmentLivePolicy.DecideLiveWrite(Config(), body, tenantConsent: true, credential: true, environment: "sandbox");
        write.IsOpen.Should().BeFalse();
        write.AllowsWrites.Should().BeFalse();
        write.ReasonCode.Should().Be(GovernmentLivePolicy.NoPartnerAgreement, "the agreement is read from the (empty) configuration");
    }

    [Fact]
    public async Task LiveWrites_AreRefused_WithoutTheRecordedLegalClearance_EvenWithAllFourGatesOpen()
    {
        var allOpen = new GovernmentLiveGates(true, true, true, true);
        var handler = new CountingHandler();
        var config = Config((GovernmentHttp.BaseUrlKey(GovernmentBodies.BankAnb, "sandbox"), "https://bank-sandbox.example.test"));
        var http = new GovernmentHttp(new SingleHandlerFactory(handler), config);

        GovernmentLivePolicy.Decide(GovernmentBodies.BankAnb, allOpen).IsOpen.Should().BeTrue("reads (status polls) need only the four gates");
        var write = GovernmentLivePolicy.DecideLiveWrite(config, GovernmentBodies.BankAnb, allOpen, "sandbox");

        write.IsOpen.Should().BeFalse();
        write.ReasonCode.Should().Be(GovernmentLivePolicy.LiveWritesNeedLegalClearance);
        write.RefusalCode.Should().Be(GovernmentLivePolicy.RefusedCode);
        var send = async () => { using var _ = await http.ExchangeAsync(write, "sandbox", HttpMethod.Post, "/payroll/v2", new StringContent("{}"), null, default); };
        await send.Should().ThrowAsync<GovernmentTransportRefusedException>();
        handler.Calls.Should().Be(0);

        // Nor does an open READ decision carry a write: the transport refuses a POST without the write grade.
        var read = GovernmentLivePolicy.Decide(GovernmentBodies.BankAnb, allOpen, "sandbox");
        var post = async () => { using var _ = await http.ExchangeAsync(read, "sandbox", HttpMethod.Post, "/payroll/v2", new StringContent("{}"), null, default); };
        (await post.Should().ThrowAsync<GovernmentTransportRefusedException>()).Which.Code.Should().Be(GovernmentHttp.WriteRefusedCode);
        handler.Calls.Should().Be(0);

        // Only the operator's record of the legal opinion FOR THIS BODY opens the rung; nothing else in configuration does.
        var cleared = Config((GovernmentLivePolicy.LegalClearanceKey(GovernmentBodies.BankAnb), "LEGAL-SYNTHETIC-1"),
            (GovernmentHttp.BaseUrlKey(GovernmentBodies.BankAnb, "sandbox"), "https://bank-sandbox.example.test"));
        var allowed = GovernmentLivePolicy.DecideLiveWrite(cleared, GovernmentBodies.BankAnb, allOpen, "sandbox");
        allowed.AllowsWrites.Should().BeTrue();
        using (await new GovernmentHttp(new SingleHandlerFactory(handler), cleared)
                   .ExchangeAsync(allowed, "sandbox", HttpMethod.Post, "/payroll/v2", new StringContent("{}"), null, default)) { }
        handler.Calls.Should().Be(1, "a cleared write is sent exactly once");
        GovernmentLivePolicy.DecideLiveWrite(Config((GovernmentLivePolicy.LegalClearanceKey(GovernmentBodies.Gosi), "LEGAL-SYNTHETIC-1")), GovernmentBodies.BankAnb, allOpen)
            .AllowsWrites.Should().BeFalse("one body's clearance does not open another's");
        GovernmentLivePolicy.DecideLiveWrite(Config((GovernmentLivePolicy.LegalClearanceKey(GovernmentBodies.BankAnb), "  ")), GovernmentBodies.BankAnb, allOpen)
            .IsOpen.Should().BeFalse();
        GovernmentLivePolicy.DecideLiveWrite(Config((GovernmentLivePolicy.LegalClearanceKey(GovernmentBodies.BankAnb), "LEGAL-SYNTHETIC-1")), GovernmentBodies.BankAnb,
            allOpen with { TenantConsent = false }).ReasonCode.Should().Be(GovernmentLivePolicy.TenantNotOptedIn, "the clearance never stands in for a gate");
        // A decision made for one environment is never used for another.
        var wrongEnv = async () => { using var _ = await new GovernmentHttp(new SingleHandlerFactory(handler), cleared)
            .ExchangeAsync(allowed, "production", HttpMethod.Get, "/status", null, null, default); };
        (await wrongEnv.Should().ThrowAsync<GovernmentTransportRefusedException>()).Which.Code.Should().Be(GovernmentHttp.EnvironmentMismatchCode);
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public void DeploymentGates_ReadOperatorConfig_KillSwitchOnlyWhenExplicitlyFalse()
    {
        var none = Config();
        GovernmentLivePolicy.HasPartnerAgreement(none, GovernmentBodies.Gosi).Should().BeFalse();
        GovernmentLivePolicy.OperatorEnabled(none, GovernmentBodies.Gosi).Should().BeTrue("an unset switch is not a kill");

        var killed = Config((GovernmentLivePolicy.EnabledKey(GovernmentBodies.Gosi), "false"),
            (GovernmentLivePolicy.PartnerAgreementKey(GovernmentBodies.Gosi), "AGR-1"));
        GovernmentLivePolicy.HasPartnerAgreement(killed, GovernmentBodies.Gosi).Should().BeTrue();
        GovernmentLivePolicy.OperatorEnabled(killed, GovernmentBodies.Gosi).Should().BeFalse();
        GovernmentLivePolicy.EnabledKey(GovernmentBodies.BankAnb).Should().Be("Government:Banks:Anb:Enabled", "every bank lives under one tree");
        GovernmentLivePolicy.PartnerAgreementKey(GovernmentBodies.BankAnb).Should().Be("Government:Banks:Anb:PartnerAgreementReference");
        GovernmentHttp.BaseUrlKey(GovernmentBodies.BankAnb, "sandbox").Should().Be("Government:Banks:Anb:BaseUrls:sandbox");
        GovernmentLivePolicy.LegalClearanceKey(GovernmentBodies.BankAnb).Should().Be("Government:Banks:Anb:LiveWrites:LegalClearanceReference");
        GovernmentLivePolicy.PartnerAgreementKey(GovernmentBodies.Gosi).Should().Be("Government:Gosi:PartnerAgreementReference");
    }

    [Theory]
    [InlineData(false, false, QiwaLiveAdapterPolicy.Mode.Sandbox)]
    [InlineData(false, true, QiwaLiveAdapterPolicy.Mode.Sandbox)]
    [InlineData(true, false, QiwaLiveAdapterPolicy.Mode.RefusedLive)]
    [InlineData(true, true, QiwaLiveAdapterPolicy.Mode.Live)]
    public void QiwaPolicy_KeepsItsSurface_AndItsLiveCallsUseTheRealGate(bool liveRequested, bool agreement, QiwaLiveAdapterPolicy.Mode expected)
    {
        QiwaLiveAdapterPolicy.Decide(liveRequested, agreement).Should().Be(expected, "which adapter the process registers is unchanged");
        var config = Config((QiwaLiveAdapterPolicy.LiveSwitchEnvVar, liveRequested ? "true" : "false"),
            (QiwaLiveAdapterPolicy.PartnerAgreementKey, agreement ? "AGR-SYNTHETIC" : null),
            (GovernmentLivePolicy.LegalClearanceKey(GovernmentBodies.Qiwa), "LEGAL-SYNTHETIC-1"));
        // Nothing is hard-coded open: without the tenant's consent or a credential a live Qiwa call is refused.
        QiwaLiveAdapterPolicy.DecideLiveWrite(config, tenantConsent: false, credential: true, "sandbox").IsOpen.Should().BeFalse();
        QiwaLiveAdapterPolicy.DecideLiveWrite(config, tenantConsent: true, credential: false, "sandbox").IsOpen.Should().BeFalse();
        QiwaLiveAdapterPolicy.DecideLiveWrite(config, tenantConsent: true, credential: true, "sandbox").AllowsWrites.Should().Be(liveRequested && agreement);
        QiwaLiveAdapterPolicy.DecideLiveWrite(Config((QiwaLiveAdapterPolicy.LiveSwitchEnvVar, "true"), (QiwaLiveAdapterPolicy.PartnerAgreementKey, "AGR"),
                (GovernmentLivePolicy.LegalClearanceKey(GovernmentBodies.Qiwa), "L"), (GovernmentLivePolicy.EnabledKey(GovernmentBodies.Qiwa), "false")),
            true, true, "sandbox").ReasonCode.Should().Be(GovernmentLivePolicy.OperatorDisabled, "Government:Qiwa:Enabled=false is a kill switch");
    }

    [Fact]
    public void ADecision_CanOnlyBeMadeByThePolicy() =>
        typeof(GovernmentLiveDecision).GetConstructors().Should().BeEmpty("no code outside the policy may build an open decision");

    [Fact]
    public void QiwaAdapter_IsAGovernmentAdapter_ForTheQiwaBody_AndNotLive()
    {
        IQiwaApiAdapter sandbox = new SandboxQiwaApiAdapter(Microsoft.Extensions.Logging.Abstractions.NullLogger<SandboxQiwaApiAdapter>.Instance);
        IGovernmentAdapter marker = sandbox;
        marker.Body.Should().Be(GovernmentBodies.Qiwa);
        marker.IsLiveIntegration.Should().BeFalse();
        IGovernmentAdapter refused = new RefusedLiveQiwaApiAdapter();
        refused.IsLiveIntegration.Should().BeFalse();
    }

    internal static IConfiguration Config(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();

    internal sealed class CountingHandler(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls;
        public List<Uri> Uris { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            lock (Uris) Uris.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
        }
    }

    internal sealed class SingleHandlerFactory(HttpMessageHandler handler, Uri? baseAddress = null) : IHttpClientFactory
    {
        public List<string> Names { get; } = [];
        public HttpClient CreateClient(string name)
        {
            Names.Add(name);
            return new HttpClient(handler, disposeHandler: false) { BaseAddress = baseAddress };
        }
    }
}
