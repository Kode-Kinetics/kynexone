using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Api;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Channels;
using Zayra.Api.Tests.Security;
using static Zayra.Api.Tests.Government.AnbPayrollApi.AnbApiTestKit;

namespace Zayra.Api.Tests.Government.AnbPayrollApi;

/// <summary>
/// CTO directive 1, E4 (and security P2-C): the ANB client itself asks the live gate before every token request and every submit,
/// for this tenant, bank and environment. A closed gate, or production without the recorded legal clearance, refuses before any
/// socket; the sandbox needs no clearance. The client has no HTTP stack of its own, so no handler (one with certificate checks off,
/// say) can be injected, and the tests cannot reach a production host.
/// </summary>
public class AnbClientLiveGateTests
{
    private static readonly Batch Small = BuildBatch(3);
    private static readonly BankChannelScope Production = TestScope with { Environment = GovernmentEnvironments.Production };

    private static StubAnbHandler AcceptingBank() =>
        new(_ => Json(HttpStatusCode.OK, new { id = PayrollId, status = "ACCEPTED", message = "" }));

    public static TheoryData<bool, bool, bool, bool, string> ClosedGates => new()
    {
        { false, true, true, true, GovernmentLivePolicy.NoPartnerAgreement },
        { true, false, true, true, GovernmentLivePolicy.TenantNotOptedIn },
        { true, true, false, true, GovernmentLivePolicy.NoCredential },
        { true, true, true, false, GovernmentLivePolicy.OperatorDisabled },
    };

    // ── submit ─────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(ClosedGates))]
    public async Task Submit_WhenTheGateIsClosed_IsRefusedBeforeTheTokenRequest_AndNothingIsSent(bool g1, bool g2, bool g3, bool g4, string reason)
    {
        var handler = AcceptingBank();
        var config = Config();
        var gate = new PolicyGate(config, new(g1, g2, g3, g4));
        var request = Submit(Small);

        var outcome = await Client(handler, out _, config, gate: gate).SubmitAsync(request, CancellationToken.None);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Refused);
        outcome.Code.Should().Be(BankChannelCodes.GateRefused);
        outcome.Message.Should().Be(reason, "the client's own gate check refused it, naming the closed gate");
        outcome.RequestSent.Should().BeFalse();
        handler.Requests.Should().BeEmpty("no token request and no POST");
        gate.Asked.Should().Equal((true, "sandbox"));
        request.Intent!.IsSpent.Should().BeFalse("nothing was sent");
    }

    [Fact]
    public async Task Submit_InProduction_WithoutTheLegalClearance_IsRefused_NoTokenNoPost()
    {
        var handler = AcceptingBank();
        var config = Config(productionClearance: false);

        var outcome = await Client(handler, out _, config, new FixedCredentials(Credential(environment: "production")))
            .SubmitAsync(Submit(Small, scope: Production), CancellationToken.None);

        outcome.Code.Should().Be(BankChannelCodes.GateRefused);
        outcome.Message.Should().Be(GovernmentLivePolicy.LiveWritesNeedLegalClearance);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Submit_InProduction_WithTheLegalClearance_IsSentOnce_ToTheProductionHosts()
    {
        var handler = AcceptingBank();
        var config = Config(productionClearance: true);

        var outcome = await Client(handler, out _, config, new FixedCredentials(Credential(environment: "production")))
            .SubmitAsync(Submit(Small, scope: Production), CancellationToken.None);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Accepted);
        handler.TokenRequests.Should().ContainSingle().Which.Uri.Host.Should().Be("anb-prod-auth.test");
        handler.Posts.Should().ContainSingle().Which.Uri.Host.Should().Be("anb-prod.test");
    }

    [Fact]
    public async Task Submit_InTheSandbox_NeedsNoClearance()
    {
        var handler = AcceptingBank();
        var config = Config(productionClearance: false);
        config[GovernmentLivePolicy.LegalClearanceKey(GovernmentBodies.BankAnb, GovernmentEnvironments.Sandbox)] = null;

        var outcome = await Client(handler, out _, config).SubmitAsync(Submit(Small), CancellationToken.None);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Accepted);
        handler.Posts.Should().ContainSingle();
    }

    [Fact]
    public async Task Submit_WithOnlyAReadDecision_IsRefused_BeforeTheTokenRequest()
    {
        var handler = AcceptingBank();
        var config = Config();
        var outcome = await Client(handler, out _, config, gate: new PolicyGate(config, AllOpen) { WriteAsRead = true })
            .SubmitAsync(Submit(Small), CancellationToken.None);

        outcome.Code.Should().Be(BankChannelCodes.GateRefused);
        outcome.Message.Should().Be(GatedBankPayrollChannel.WriteNotGranted);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Submit_WithADecisionForAnotherEnvironment_IsRefused()
    {
        var handler = AcceptingBank();
        var config = Config(productionClearance: true);
        var outcome = await Client(handler, out _, config, gate: new PolicyGate(config, AllOpen) { EnvironmentOverride = GovernmentEnvironments.Production })
            .SubmitAsync(Submit(Small), CancellationToken.None);

        outcome.Code.Should().Be(BankChannelCodes.GateRefused);
        outcome.Message.Should().Be(GatedBankPayrollChannel.DecisionNotForThisCall);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Submit_WhenTheGateThrows_FailsClosed()
    {
        var handler = AcceptingBank();
        var config = Config();
        var outcome = await Client(handler, out _, config, gate: new PolicyGate(config, AllOpen) { Throw = true })
            .SubmitAsync(Submit(Small), CancellationToken.None);

        outcome.Code.Should().Be(BankChannelCodes.GateRefused);
        outcome.Message.Should().Be(GatedBankPayrollChannel.GateUnavailable);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AKillSwitchThrownBetweenTwoSubmits_RefusesTheSecond_EvenWithACachedToken()
    {
        var handler = AcceptingBank();
        var config = Config();
        var gate = new PolicyGate(config, AllOpen);
        var client = Client(handler, out _, config, gate: gate);

        (await client.SubmitAsync(Submit(Small), CancellationToken.None)).Kind.Should().Be(BankSubmitOutcomeKind.Accepted);
        gate.Gates = AllOpen with { OperatorEnabled = false };
        var second = await client.SubmitAsync(Submit(Small), CancellationToken.None);

        second.Code.Should().Be(BankChannelCodes.GateRefused);
        second.Message.Should().Be(GovernmentLivePolicy.OperatorDisabled);
        handler.Posts.Should().ContainSingle();
        handler.TokenRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task TheGate_IsAskedForTheWriteGrade_BeforeTheTokenRequest()
    {
        var order = new List<string>();
        var config = Config();
        var gate = new RecordingGate(new PolicyGate(config, AllOpen), order);
        var handler = new StubAnbHandler(
            _ => { lock (order) order.Add("post"); return Json(HttpStatusCode.OK, new { id = PayrollId, status = "ACCEPTED", message = "" }); },
            _ => { lock (order) order.Add("token"); return TokenOk(); });

        await Client(handler, out _, config, gate: gate).SubmitAsync(Submit(Small), CancellationToken.None);

        order.Should().Equal("gate:write", "token", "post");
    }

    [Fact]
    public async Task Submit_WithoutAReceipt_IsRefused_BeforeTheGateAndAnySocket()
    {
        var handler = AcceptingBank();
        var config = Config();
        var gate = new PolicyGate(config, AllOpen);

        var outcome = await Client(handler, out _, config, gate: gate).SubmitAsync(Submit(Small) with { Intent = null }, CancellationToken.None);

        outcome.Code.Should().Be(BankChannelCodes.IntentMissing);
        handler.Requests.Should().BeEmpty();
    }

    public static TheoryData<string> ForeignReceipts => new() { "tenant", "company", "environment", "files" };

    [Theory]
    [MemberData(nameof(ForeignReceipts))]
    public async Task Submit_WithAReceiptForAnotherTenantCompanyEnvironmentOrFiles_IsRefused(string what)
    {
        var handler = AcceptingBank();
        var receipt = what switch
        {
            "tenant" => Receipt(Small, TestScope with { TenantId = Guid.NewGuid() }),
            "company" => Receipt(Small, TestScope with { CompanyId = Guid.NewGuid() }),
            "environment" => Receipt(Small, Production),
            _ => Receipt(BuildBatch(4)),
        };

        var outcome = await Client(handler, out _).SubmitAsync(Submit(Small) with { Intent = receipt }, CancellationToken.None);

        outcome.Code.Should().Be(BankChannelCodes.IntentMismatch);
        handler.Requests.Should().BeEmpty();
        receipt.IsSpent.Should().BeFalse();
    }

    [Fact]
    public async Task AReceipt_IsSpentByTheOneSend_AndASecondSubmitWithItSendsNothing()
    {
        var handler = AcceptingBank();
        var client = Client(handler, out _);
        var request = Submit(Small);

        (await client.SubmitAsync(request, CancellationToken.None)).Kind.Should().Be(BankSubmitOutcomeKind.Accepted);
        var again = await client.SubmitAsync(request, CancellationToken.None);

        again.Kind.Should().Be(BankSubmitOutcomeKind.Refused);
        again.Code.Should().Be(BankChannelCodes.IntentAlreadyUsed);
        handler.Posts.Should().ContainSingle();
    }

    // ── reads ──────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(ClosedGates))]
    public async Task Reads_WhenTheGateIsClosed_AreRefused_AndNoTokenIsMinted(bool g1, bool g2, bool g3, bool g4, string reason)
    {
        var handler = new StubAnbHandler(_ => throw new InvalidOperationException("no call"));
        var config = Config();
        var client = Client(handler, out _, config, gate: new PolicyGate(config, new(g1, g2, g3, g4)));

        var status = () => client.GetStatusAsync(TestScope, PayrollId, null, CancellationToken.None);
        var wps = () => client.DownloadWpsAsync(TestScope, PayrollId, null, CancellationToken.None);

        (await status.Should().ThrowAsync<BankChannelRefusedException>()).Which.Settings.Should().Equal(reason);
        (await wps.Should().ThrowAsync<BankChannelRefusedException>()).Which.Code.Should().Be(BankChannelCodes.GateRefused);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AProductionRead_WithoutTheClearance_MintsNoToken()
    {
        var handler = new StubAnbHandler(_ => throw new InvalidOperationException("no call"));
        var config = Config(productionClearance: false);
        var client = Client(handler, out _, config, new FixedCredentials(Credential(environment: "production")));

        var status = () => client.GetStatusAsync(Production, PayrollId, null, CancellationToken.None);

        var refused = (await status.Should().ThrowAsync<BankChannelRefusedException>()).Which;
        refused.Code.Should().Be(BankChannelCodes.GateRefused);
        refused.Settings.Should().Equal(GovernmentLivePolicy.LiveWritesNeedLegalClearance);
        handler.Requests.Should().BeEmpty("the read decision is open, but minting a token is a POST and needs the write grade");
    }

    // ── no transport of its own; no way to inject one ──────────────────────────────────────────────────

    [Fact]
    public void NoBankClientChannelOrFactory_TakesAnHttpClientHandlerOrClientFactory()
    {
        var forbidden = new[] { typeof(HttpMessageHandler), typeof(HttpClient), typeof(IHttpClientFactory), typeof(IHttpMessageHandlerFactory) };
        var types = typeof(AnbPayrollApiClient).Assembly.GetTypes()
            .Where(t => t.Namespace is "Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Api" or "Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Channels")
            .ToList();
        types.Should().Contain(typeof(AnbPayrollApiClient)).And.Contain(typeof(AnbGatedChannelFactory)).And.Contain(typeof(GatedBankPayrollChannel));

        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var takes = types
            .SelectMany(t => t.GetConstructors(all).Cast<MethodBase>().Concat(t.GetMethods(all)).Select(m => (Type: t, Member: m)))
            .Where(x => x.Member.GetParameters().Any(p => forbidden.Any(f => f.IsAssignableFrom(p.ParameterType))))
            .Select(x => $"{x.Type.Name}.{x.Member.Name}").ToList();
        takes.Should().BeEmpty("the only transport is GovernmentHttp: nothing can hand the bank client a handler with certificate checks "
            + "turned off or redirects on");
        var fields = types.SelectMany(t => t.GetFields(all).Where(f => forbidden.Any(x => x.IsAssignableFrom(f.FieldType))).Select(f => $"{t.Name}.{f.Name}"));
        fields.Should().BeEmpty("no bank type holds an HTTP client or handler");

        // (the static constructor of the class's constants is not a way to build one)
        typeof(AnbPayrollApiClient).GetConstructors(all).Where(c => !c.IsStatic).Should().ContainSingle()
            .Which.GetParameters().Select(p => p.ParameterType).Should().Contain(typeof(GovernmentHttp));
    }

    [Fact]
    public void TheTests_CannotReachAProductionHost()
    {
        // No ANB host is built into the transport: an unconfigured production call has nowhere to go.
        var bare = new GovernmentHttp(new StubClientFactory(new StubAnbHandler(_ => throw new InvalidOperationException())),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        foreach (var environment in GovernmentEnvironments.All)
        {
            var act = () => bare.ResolveBaseUrl(GovernmentBodies.BankAnb, environment);
            act.Should().Throw<GovernmentTransportRefusedException>().Which.Code.Should().Be(GovernmentHttp.HostRefusedCode);
        }

        // Every host the kit configures is a reserved .test name, and the stub refuses any other.
        var kit = Config(productionClearance: true);
        kit.AsEnumerable().Where(kv => kv.Value is not null && kv.Value.StartsWith("https://", StringComparison.Ordinal))
            .Select(kv => new Uri(kv.Value!).Host).Should().NotBeEmpty().And.OnlyContain(h => h.EndsWith(".test", StringComparison.Ordinal));

        // And no test source in this folder names a host outside the reserved test names (.test, .example, localhost).
        var folder = Path.GetFullPath(Path.Combine(SourceScan.ResolveApiRoot(), "..", "Zayra.Api.Tests", "Government", "AnbPayrollApi"));
        var files = Directory.EnumerateFiles(folder, "*.cs").ToList();
        files.Should().HaveCountGreaterThan(10);
        var hosts = files.SelectMany(f => Regex.Matches(File.ReadAllText(f), @"https?://(?:[^/@\s""]*@)?(?<host>[A-Za-z0-9.\-]+)").Select(m => (File: Path.GetFileName(f), Host: m.Groups["host"].Value)))
            .Where(x => !(x.Host.EndsWith(".test", StringComparison.Ordinal) || x.Host.EndsWith(".example", StringComparison.Ordinal)
                          || x.Host == "localhost" || x.Host is "10.0.0.5" or "127.0.0.1" or "169.254.169.254"))
            .ToList();
        hosts.Should().BeEmpty("tests name only reserved hosts, never a bank's real one");
        files.Where(f => Regex.IsMatch(File.ReadAllText(f), @"anb\.com\.sa", RegexOptions.IgnoreCase)).Should().BeEmpty("no real bank host, even bare");
    }

    /// <summary>Writes "gate:write" or "gate:read" into a shared order log, then asks the inner gate.</summary>
    private sealed class RecordingGate(IBankChannelGate inner, List<string> order) : IBankChannelGate
    {
        public Task<GovernmentLiveDecision> DecideAsync(string body, BankChannelScope scope, bool write, CancellationToken ct)
        {
            lock (order) order.Add(write ? "gate:write" : "gate:read");
            return inner.DecideAsync(body, scope, write, ct);
        }
    }
}
