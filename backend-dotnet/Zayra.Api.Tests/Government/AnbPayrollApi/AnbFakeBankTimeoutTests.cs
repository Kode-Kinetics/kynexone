using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Auth;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Api;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Channels;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Government.AnbPayrollApi.AnbApiTestKit;

namespace Zayra.Api.Tests.Government.AnbPayrollApi;

/// <summary>
/// CTO-GATE 8.3g and Fable P2-19: the whole money path at once, on a REAL socket. The recorder commits the intent and hands out the
/// receipt; the gated channel checks the gate and the receipt; the ANB client asks the gate again, mints a token and sends through
/// GovernmentHttp's real guarded handler (pointed at a loopback TLS fake bank: loopback is allowed only in this fixture). The fake
/// bank reads the whole payroll POST and then never answers, or answers only after the client has given up. Exactly ONE POST reaches
/// the socket, the outcome is Unknown, and the ledger row is Unknown and not completed (a person resolves it; nothing is resent).
/// </summary>
public class AnbFakeBankTimeoutTests
{
    private static readonly TimeSpan ClientTimeout = TimeSpan.FromMilliseconds(800);

    public static TheoryData<string> Silences => new() { "never-answers", "answers-after-the-timeout" };

    [Theory]
    [MemberData(nameof(Silences))]
    public async Task ABankThatTakesThePayrollAndThenGoesSilent_GetsExactlyOnePost_AndTheLedgerSaysUnknown(string silence)
    {
        await using var bank = new LoopbackTlsBank(seen => seen.Host switch
        {
            "anb-auth.test" => new(200, JsonSerializer.Serialize(new { access_token = AccessToken, token_type = "Bearer", expires_in = 3600 })),
            _ when silence == "never-answers" => LoopbackTlsBank.Silence(),
            _ => new(200, JsonSerializer.Serialize(new { id = PayrollId, status = "ACCEPTED", message = "" }), After: ClientTimeout * 4),
        });
        var config = Config();
        var transport = new GovernmentHttp(new LoopbackClientFactory(bank.Wire(GovernmentHttp.CreateHandler())), config);

        var (recorder, db) = GovernmentExchangeRecorderTests.Create();
        await using var _ = db;
        var batch = BuildBatch(25);
        var context = new RequestContext("127.0.0.1", "test", Guid.NewGuid(), TestScope.TenantId);
        var begun = await recorder.BeginAsync(new GovernmentExchangeAttempt(TestScope.TenantId, TestScope.CompanyId, GovernmentBodies.BankAnb,
            "payroll.submit", GovernmentExchangeChannels.Api, "batch-synthetic-" + silence, GovernmentExchangeStates.SubmitIntended, context,
            Environment: "sandbox", SubjectType: "PaymentBatch", SubjectId: "42", ArtifactSha256: batch.Header.ExpectedSha256,
            ArtifactSecondarySha256: batch.Body.ExpectedSha256, AccountFingerprint: "acct-fp-synthetic", BatchReference: "B-" + silence,
            TotalAmount: batch.Total, ItemCount: batch.Rows.Count), CancellationToken.None);
        var receipt = begun.Receipt!;
        begun.Receipt.Should().NotBeNull("the recorder hands out the receipt for a committed intent");
        receipt.CompanyId.Should().Be(TestScope.CompanyId);

        var channel = AnbGatedChannelFactory.Create(transport, new FixedCredentials(), new PolicyGate(config, AllOpen), recorder,
            NullLoggerFactory.Instance, limits: new AnbClientLimits(ClientTimeout, TimeSpan.FromSeconds(10)));
        var request = new BankSubmitRequest(TestScope, new[] { batch.Header, batch.Body }, new BankSubmitOptions(),
            Intent: new BankSubmitIntent(receipt, context));

        var outcome = await channel.SubmitAsync(request, CancellationToken.None);
        // The same receipt, again, after the silence: nothing more is sent.
        var again = await channel.SubmitAsync(request, CancellationToken.None);
        await Task.Delay(ClientTimeout); // give a (wrong) second POST time to arrive

        bank.Requests.Where(r => r.Method == "POST" && r.Host == "anb-sandbox.test").Should().ContainSingle("exactly ONE payroll POST reached the socket");
        bank.Requests.Single(r => r.Host == "anb-sandbox.test").Body.Length.Should().BeGreaterThan(batch.Body.Content.Length, "the whole payroll arrived");
        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Unknown);
        outcome.RequestSent.Should().BeTrue();
        again.Code.Should().Be(BankChannelCodes.IntentAlreadyUsed);

        var row = await db.GovernmentExchanges.AsNoTracking().SingleAsync(x => x.Id == receipt.ExchangeId);
        row.State.Should().Be(GovernmentExchangeStates.Unknown);
        row.CompletedAtUtc.Should().BeNull("an Unknown is a person's to resolve, never completed and never resent");
        row.CompanyId.Should().Be(TestScope.CompanyId);
    }
}
