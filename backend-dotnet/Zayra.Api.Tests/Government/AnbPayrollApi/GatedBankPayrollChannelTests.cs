using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Auth;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Api;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Channels;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Government.AnbPayrollApi.AnbApiTestKit;

namespace Zayra.Api.Tests.Government.AnbPayrollApi;

/// <summary>
/// The gated bank channel (CTO ruling on ratchet I): before ANY outbound call to the bank, including the token POST, a submit needs
/// a live-WRITE decision from <see cref="GovernmentLivePolicy"/> for the call's environment (four gates, plus the legal clearance in
/// production) AND the recorder's committed intent receipt for this tenant, bank, environment and these exact frozen files. Reads
/// need an open decision. Each submit's outcome goes back to the ledger; a timeout after the POST was sent is Unknown, never Failed.
/// No network: a stub handler stands in for the bank.
/// </summary>
public class GatedBankPayrollChannelTests
{
    private static readonly RequestContext Ctx = new("127.0.0.1", "test", Guid.Parse("33333333-3333-3333-3333-333333333333"),
        TestScope.TenantId);

    /// <summary>One configuration for the gate and the transport: the kit's Government:Banks:Anb tree.</summary>
    private static IConfiguration Settings(bool productionClearance = false) => Config(productionClearance);

    private static GovernmentExchangeIntentReceipt Receipt(Batch batch, Guid? exchangeId = null, Guid? tenantId = null,
        string body = GovernmentBodies.BankAnb, string environment = "sandbox", string? headerSha = null, string? bodySha = null,
        Guid? companyId = null) =>
        new(exchangeId ?? Guid.NewGuid(), tenantId ?? TestScope.TenantId, body, environment, "submit-key-synthetic",
            headerSha ?? batch.Header.ExpectedSha256, bodySha ?? batch.Body.ExpectedSha256, companyId ?? TestScope.CompanyId);

    private static BankSubmitRequest Request(Batch batch, GovernmentExchangeIntentReceipt? receipt, BankChannelScope? scope = null) =>
        new(scope ?? TestScope, new[] { batch.Header, batch.Body }, new BankSubmitOptions(),
            Intent: receipt is null ? null : new BankSubmitIntent(receipt, Ctx));

    private static IBankPayrollChannel Gated(StubAnbHandler handler, IBankChannelGate gate, IGovernmentExchangeRecorder ledger, TimeSpan? timeout = null,
        IConfiguration? config = null, IBankCredentialSource? credentials = null) =>
        AnbGatedChannelFactory.Create(Transport(handler, config ?? Settings(productionClearance: true)), credentials ?? new FixedCredentials(), gate, ledger,
            NullLoggerFactory.Instance, limits: new AnbClientLimits(timeout ?? TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)));

    private static StubAnbHandler AcceptingBank() =>
        new(_ => Json(HttpStatusCode.OK, new { id = PayrollId, status = "ACCEPTED", message = "" }));

    // ── submit: the write decision ─────────────────────────────────────────────────────────────────────

    public static TheoryData<bool, bool, bool, bool, string> ClosedGates => new()
    {
        { false, true, true, true, GovernmentLivePolicy.NoPartnerAgreement },
        { true, false, true, true, GovernmentLivePolicy.TenantNotOptedIn },
        { true, true, false, true, GovernmentLivePolicy.NoCredential },
        { true, true, true, false, GovernmentLivePolicy.OperatorDisabled },
    };

    [Theory]
    [MemberData(nameof(ClosedGates))]
    public async Task Submit_WithoutAWriteDecision_OpensNoSocket_AndRefusesWithAStableCode(bool g1, bool g2, bool g3, bool g4, string reason)
    {
        var batch = BuildBatch(3);
        var handler = AcceptingBank();
        var ledger = new RecordingLedger();
        var receipt = Receipt(batch);

        var outcome = await Gated(handler, new PolicyGate(Settings(), new(g1, g2, g3, g4)), ledger)
            .SubmitAsync(Request(batch, receipt), CancellationToken.None);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Refused);
        outcome.Code.Should().Be(BankChannelCodes.GateRefused);
        outcome.Message.Should().Be(reason);
        outcome.RequestSent.Should().BeFalse();
        handler.Requests.Should().BeEmpty("no token request and no POST without a write decision");
        ledger.Updates.Should().ContainSingle().Which.Outcome.State.Should().Be(GovernmentExchangeStates.Refused);
        ledger.Updates[0].ExchangeId.Should().Be(receipt.ExchangeId);
    }

    [Fact]
    public async Task Submit_WithAReadDecisionOnly_IsRefused()
    {
        var batch = BuildBatch(2);
        var handler = AcceptingBank();
        var outcome = await Gated(handler, new PolicyGate(Settings(), AllOpen) { WriteAsRead = true }, new RecordingLedger())
            .SubmitAsync(Request(batch, Receipt(batch)), CancellationToken.None);

        outcome.Code.Should().Be(BankChannelCodes.GateRefused);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Submit_WithADecisionForAnotherEnvironment_IsRefused()
    {
        var batch = BuildBatch(2);
        var handler = AcceptingBank();
        var gate = new PolicyGate(Settings(productionClearance: true), AllOpen) { EnvironmentOverride = GovernmentEnvironments.Production };
        var outcome = await Gated(handler, gate, new RecordingLedger()).SubmitAsync(Request(batch, Receipt(batch)), CancellationToken.None);

        outcome.Code.Should().Be(BankChannelCodes.GateRefused);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Submit_WhenTheGateThrows_FailsClosed()
    {
        var batch = BuildBatch(2);
        var handler = AcceptingBank();
        var outcome = await Gated(handler, new PolicyGate(Settings(), AllOpen) { Throw = true }, new RecordingLedger())
            .SubmitAsync(Request(batch, Receipt(batch)), CancellationToken.None);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Refused);
        outcome.Code.Should().Be(BankChannelCodes.GateRefused);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ProductionSubmit_WithoutTheLegalClearance_IsRefused()
    {
        var batch = BuildBatch(2);
        var handler = AcceptingBank();
        var production = TestScope with { Environment = GovernmentEnvironments.Production };
        var outcome = await Gated(handler, new PolicyGate(Settings(productionClearance: false), AllOpen), new RecordingLedger())
            .SubmitAsync(Request(batch, Receipt(batch, environment: GovernmentEnvironments.Production), production), CancellationToken.None);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Refused);
        outcome.Code.Should().Be(BankChannelCodes.GateRefused);
        outcome.Message.Should().Be(GovernmentLivePolicy.LiveWritesNeedLegalClearance);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SandboxSubmit_WithTheFourGatesAndACommittedIntent_IsSentExactlyOnce()
    {
        var batch = BuildBatch(3);
        var handler = AcceptingBank();
        var ledger = new RecordingLedger();

        var outcome = await Gated(handler, new PolicyGate(Settings(productionClearance: false), AllOpen), ledger)
            .SubmitAsync(Request(batch, Receipt(batch)), CancellationToken.None);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Accepted);
        handler.Posts.Should().ContainSingle();
        handler.TokenRequests.Should().ContainSingle();
        // The ANB channel is not live, and only a live adapter may write Accepted: the intent stays held (SubmitIntended) until the
        // wave-2 real-endpoint split (D8) lets the ledger record it. Nothing else is written in its place.
        ledger.Updates.Should().BeEmpty();
    }

    // ── submit: the intent receipt ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Submit_WithoutAnIntentReceipt_OpensNoSocket()
    {
        var batch = BuildBatch(2);
        var handler = AcceptingBank();
        var ledger = new RecordingLedger();
        var gate = new PolicyGate(Settings(), AllOpen);

        var outcome = await Gated(handler, gate, ledger).SubmitAsync(Request(batch, receipt: null), CancellationToken.None);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Refused);
        outcome.Code.Should().Be(BankChannelCodes.IntentMissing);
        outcome.RequestSent.Should().BeFalse();
        handler.Requests.Should().BeEmpty();
        ledger.Updates.Should().BeEmpty();
    }

    public static TheoryData<string> Mismatches => new() { "tenant", "company", "body", "environment", "header", "bodyfile", "single" };

    [Theory]
    [MemberData(nameof(Mismatches))]
    public async Task Submit_WithAnIntentForSomethingElse_OpensNoSocket(string what)
    {
        var batch = BuildBatch(2);
        var other = BuildBatch(3);
        var receipt = what switch
        {
            "tenant" => Receipt(batch, tenantId: Guid.NewGuid()),
            "body" => Receipt(batch, body: GovernmentBodies.Qiwa),
            "environment" => Receipt(batch, environment: GovernmentEnvironments.Production),
            "header" => Receipt(batch, headerSha: other.Header.ExpectedSha256),
            "bodyfile" => Receipt(batch, bodySha: other.Body.ExpectedSha256),
            "company" => Receipt(batch, companyId: Guid.NewGuid()),
            _ => new GovernmentExchangeIntentReceipt(Guid.NewGuid(), TestScope.TenantId, GovernmentBodies.BankAnb, "sandbox", "k",
                batch.Header.ExpectedSha256, null, TestScope.CompanyId),
        };
        var handler = AcceptingBank();
        var ledger = new RecordingLedger();

        var outcome = await Gated(handler, new PolicyGate(Settings(), AllOpen), ledger).SubmitAsync(Request(batch, receipt), CancellationToken.None);

        outcome.Code.Should().Be(BankChannelCodes.IntentMismatch);
        handler.Requests.Should().BeEmpty();
        ledger.Updates.Should().BeEmpty("an intent for something else is not this call's to close");
    }

    [Fact]
    public async Task OneReceipt_SendsAtMostOnce()
    {
        var batch = BuildBatch(2);
        var handler = AcceptingBank();
        var channel = Gated(handler, new PolicyGate(Settings(), AllOpen), new RecordingLedger());
        var request = Request(batch, Receipt(batch));

        (await channel.SubmitAsync(request, CancellationToken.None)).Kind.Should().Be(BankSubmitOutcomeKind.Accepted);
        var second = await channel.SubmitAsync(request, CancellationToken.None);

        second.Code.Should().Be(BankChannelCodes.IntentAlreadyUsed);
        handler.Posts.Should().ContainSingle();
    }

    [Fact]
    public async Task OneReceipt_SendsAtMostOnce_EvenThroughTwoChannelInstances()
    {
        // Security P2-A: the single use is on the receipt and spent by the transport, not remembered by one channel instance.
        var batch = BuildBatch(2);
        var handler = AcceptingBank();
        var ledger = new RecordingLedger();
        var request = Request(batch, Receipt(batch));

        (await Gated(handler, new PolicyGate(Settings(), AllOpen), ledger).SubmitAsync(request, CancellationToken.None))
            .Kind.Should().Be(BankSubmitOutcomeKind.Accepted);
        var second = await Gated(handler, new PolicyGate(Settings(), AllOpen), ledger).SubmitAsync(request, CancellationToken.None);

        second.Code.Should().Be(BankChannelCodes.IntentAlreadyUsed);
        second.RequestSent.Should().BeFalse();
        handler.Posts.Should().ContainSingle();
        ledger.Updates.Should().BeEmpty("the second use writes nothing over the first use's ledger row");
    }

    [Fact]
    public async Task ManyConcurrentSubmits_OfOneReceipt_ThroughSeparateChannels_SendExactlyOnce()
    {
        var batch = BuildBatch(2);
        var handler = AcceptingBank();
        var ledger = new RecordingLedger();
        var request = Request(batch, Receipt(batch));

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            Gated(handler, new PolicyGate(Settings(), AllOpen), ledger).SubmitAsync(request, CancellationToken.None)));

        handler.Posts.Should().ContainSingle();
        outcomes.Count(o => o.Kind == BankSubmitOutcomeKind.Accepted).Should().Be(1);
        outcomes.Where(o => o.Kind != BankSubmitOutcomeKind.Accepted).Should().OnlyContain(o => o.Code == BankChannelCodes.IntentAlreadyUsed);
        ledger.Updates.Should().BeEmpty();
    }

    [Fact]
    public async Task AReceiptRefusedByAClosedGate_IsBurnt_AndCannotSendOnceTheGateOpens()
    {
        var batch = BuildBatch(2);
        var handler = AcceptingBank();
        var ledger = new RecordingLedger();
        var gate = new PolicyGate(Settings(), AllOpen with { OperatorEnabled = false });
        var channel = Gated(handler, gate, ledger);
        var request = Request(batch, Receipt(batch));

        (await channel.SubmitAsync(request, CancellationToken.None)).Code.Should().Be(BankChannelCodes.GateRefused);
        gate.Gates = AllOpen;
        var again = await channel.SubmitAsync(request, CancellationToken.None);

        again.Code.Should().Be(BankChannelCodes.IntentAlreadyUsed, "the refused attempt closed its ledger row; a new intent is needed");
        handler.Requests.Should().BeEmpty();
        ledger.Updates.Should().ContainSingle().Which.Outcome.State.Should().Be(GovernmentExchangeStates.Refused);
    }

    [Fact]
    public async Task AReceiptRefusedBeforeTheSocketByTheClient_IsBurnt_AndRecordedOnce()
    {
        var batch = BuildBatch(2);
        var handler = AcceptingBank();
        var ledger = new RecordingLedger();
        var channel = Gated(handler, new PolicyGate(Settings(), AllOpen), ledger, credentials: new FixedCredentials(none: true));
        var request = Request(batch, Receipt(batch));

        (await channel.SubmitAsync(request, CancellationToken.None)).Code.Should().Be(BankChannelCodes.CredentialMissing);
        (await channel.SubmitAsync(request, CancellationToken.None)).Code.Should().Be(BankChannelCodes.IntentAlreadyUsed);

        handler.Requests.Should().BeEmpty();
        ledger.Updates.Should().ContainSingle().Which.Outcome.State.Should().Be(GovernmentExchangeStates.Refused);
    }

    // ── submit: the outcome ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ATimeoutAfterThePostWasSent_IsRecordedAsUnknown_NeverFailed()
    {
        var batch = BuildBatch(2);
        var handler = new StubAnbHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct); // the body reached the "bank"; then silence
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var ledger = new RecordingLedger();
        var receipt = Receipt(batch);

        var outcome = await Gated(handler, new PolicyGate(Settings(), AllOpen), ledger, TimeSpan.FromMilliseconds(300))
            .SubmitAsync(Request(batch, receipt), CancellationToken.None);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Unknown);
        handler.Posts.Should().ContainSingle();
        var update = ledger.Updates.Should().ContainSingle().Subject;
        update.ExchangeId.Should().Be(receipt.ExchangeId);
        update.Outcome.State.Should().Be(GovernmentExchangeStates.Unknown);
        update.Outcome.Completed.Should().BeFalse("an Unknown is a person's to resolve");
    }

    [Fact]
    public async Task AnInnerChannelThatThrowsAfterTheGate_IsUnknown_NeverFailed()
    {
        var batch = BuildBatch(2);
        var ledger = new RecordingLedger();
        var receipt = Receipt(batch);
        var channel = new GatedBankPayrollChannel(new ThrowingChannel(), new PolicyGate(Settings(), AllOpen), ledger,
            NullLogger<GatedBankPayrollChannel>.Instance);

        var outcome = await channel.SubmitAsync(Request(batch, receipt), CancellationToken.None);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Unknown);
        outcome.RequestSent.Should().BeTrue("the request may have left");
        ledger.Updates.Should().ContainSingle().Which.Outcome.State.Should().Be(GovernmentExchangeStates.Unknown);
    }

    // ── reads ──────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(ClosedGates))]
    public async Task Poll_WithoutAReadDecision_OpensNoSocket(bool g1, bool g2, bool g3, bool g4, string reason)
    {
        _ = reason;
        var batch = BuildBatch(2);
        var handler = new StubAnbHandler(PagedStatus(batch.Rows.Select(r => Line(r, "PAID", "P")).ToList(), "COMPLETED"));
        var channel = Gated(handler, new PolicyGate(Settings(), new(g1, g2, g3, g4)), new RecordingLedger());

        var status = () => channel.GetStatusAsync(TestScope, PayrollId, Binding(batch), CancellationToken.None);
        var wps = () => channel.DownloadWpsAsync(TestScope, PayrollId, null, CancellationToken.None);

        (await status.Should().ThrowAsync<BankChannelRefusedException>()).Which.Code.Should().Be(BankChannelCodes.GateRefused);
        (await wps.Should().ThrowAsync<BankChannelRefusedException>()).Which.Code.Should().Be(BankChannelCodes.GateRefused);
        handler.Requests.Should().BeEmpty("no token request and no GET without a read decision");
    }

    [Fact]
    public async Task Poll_WithTheFourGates_Reads()
    {
        var batch = BuildBatch(2);
        var handler = new StubAnbHandler(PagedStatus(batch.Rows.Select(r => Line(r, "PAID", "P" + r.EmployeeRef)).ToList(), "COMPLETED"));
        var channel = Gated(handler, new PolicyGate(Settings(), AllOpen), new RecordingLedger());

        var snapshot = await channel.GetStatusAsync(TestScope, PayrollId, Binding(batch), CancellationToken.None);

        snapshot.Rows.Should().HaveCount(2);
        handler.ApiRequests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task ProductionPoll_WithoutTheLegalClearance_IsRefused_BecauseItWouldMintAToken()
    {
        var handler = new StubAnbHandler(_ => throw new InvalidOperationException("no call"));
        var channel = Gated(handler, new PolicyGate(Settings(productionClearance: false), AllOpen), new RecordingLedger());
        var production = TestScope with { Environment = GovernmentEnvironments.Production };

        var act = () => channel.GetStatusAsync(production, PayrollId, Binding(BuildBatch(1)), CancellationToken.None);

        (await act.Should().ThrowAsync<BankChannelRefusedException>()).Which.Code.Should().Be(BankChannelCodes.GateRefused);
        handler.Requests.Should().BeEmpty();
    }

    // ── construction ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheAnbClientAndChannel_HaveNoPublicConstructor()
    {
        typeof(AnbPayrollApiClient).GetConstructors().Should().BeEmpty("only the gated factory builds the ANB client");
        typeof(AnbPayrollChannel).GetConstructors().Should().BeEmpty("only the gated factory builds the ANB channel");
    }

    [Fact]
    public void TheFactory_ReturnsTheGatedChannel()
    {
        Gated(AcceptingBank(), new PolicyGate(Settings(), AllOpen), new RecordingLedger()).Should().BeOfType<GatedBankPayrollChannel>()
            .Which.BankId.Should().Be(AnbPayrollChannel.Id);
    }
}

/// <summary>Asks the real policy (the only maker of a decision), with switches to misbehave.</summary>
internal sealed class PolicyGate(IConfiguration config, GovernmentLiveGates gates) : IBankChannelGate
{
    public bool WriteAsRead { get; init; }
    public string? EnvironmentOverride { get; init; }
    public bool Throw { get; init; }
    /// <summary>Switch the gates while a test runs (the operator's kill switch moving between two calls).</summary>
    public GovernmentLiveGates Gates { get; set; } = gates;
    public int Calls;
    /// <summary>Every question asked, in order: (write grade?, environment).</summary>
    public readonly List<(bool Write, string Environment)> Asked = new();

    public Task<GovernmentLiveDecision> DecideAsync(string body, BankChannelScope scope, bool write, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        lock (Asked) Asked.Add((write, scope.Environment));
        if (Throw) throw new InvalidOperationException("gate store down");
        var environment = EnvironmentOverride ?? scope.Environment;
        return Task.FromResult(write && !WriteAsRead
            ? GovernmentLivePolicy.DecideLiveWrite(config, body, Gates, environment)
            : GovernmentLivePolicy.Decide(body, Gates, environment));
    }
}

/// <summary>Captures the ledger updates; the decorator never begins or resolves an attempt itself.</summary>
internal sealed class RecordingLedger : IGovernmentExchangeRecorder
{
    public readonly List<(Guid TenantId, Guid ExchangeId, GovernmentExchangeOutcome Outcome)> Updates = new();

    public Task<GovernmentExchangeRecordResult> BeginAsync(GovernmentExchangeAttempt attempt, CancellationToken ct) =>
        throw new InvalidOperationException("the decorator does not begin attempts");

    public Task<GovernmentExchange> UpdateAsync(Guid tenantId, Guid exchangeId, GovernmentExchangeOutcome outcome, CancellationToken ct)
    {
        lock (Updates) Updates.Add((tenantId, exchangeId, outcome));
        return Task.FromResult(new GovernmentExchange { Id = exchangeId, TenantId = tenantId, State = outcome.State });
    }

    public Task<GovernmentExchange> ResolveUnknownAsync(Guid tenantId, Guid exchangeId, string resolution, string? bankReference, string reason,
        RequestContext context, CancellationToken ct) => throw new InvalidOperationException("the decorator does not resolve");
}

internal sealed class ThrowingChannel : IBankPayrollChannel
{
    public string BankId => AnbPayrollChannel.Id;
    public bool IsLiveIntegration => false;
    public Task<BankSubmitOutcome> SubmitAsync(BankSubmitRequest request, CancellationToken ct) => throw new TaskCanceledException("timeout");
    public Task<BankStatusSnapshot> GetStatusAsync(BankChannelScope scope, string bankPayrollId, BankBatchBinding binding, CancellationToken ct) =>
        throw new InvalidOperationException();
    public Task<BankWpsFile> DownloadWpsAsync(BankChannelScope scope, string bankPayrollId, string? profileId, CancellationToken ct) =>
        throw new InvalidOperationException();
}
