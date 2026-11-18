using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Infrastructure.Government.Credentials;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Api;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Channels;
using static Zayra.Api.Tests.Government.AnbPayrollApi.AnbApiTestKit;

namespace Zayra.Api.Tests.Government.AnbPayrollApi;

/// <summary>
/// Fix round for the two reviews: redirects, exceptions after the send, frozen-byte copies, bounded reads,
/// credential binding, host allow-list, token lifetime and cache key, 4xx classification.
/// </summary>
public class AnbTransportHardeningTests
{
    private static readonly Batch Small = BuildBatch(3);

    private static HttpResponseMessage Accepted() => Json(HttpStatusCode.OK, new { id = PayrollId, status = "ACCEPTED", message = "" });

    // ── 1. Redirects (the transport's own guarded handler, on a real socket) ─────────────────────

    /// <summary>The transport's real guarded handler (GovernmentHttp.CreateHandler), pointed at a loopback TLS bank. Loopback is
    /// allowed ONLY here, by the fixture's ConnectCallback; the production handler refuses it (AnbTransportGuardTests).</summary>
    internal static AnbPayrollApiClient RealSocketClient(LoopbackTlsBank bank, IConfiguration? config = null, TimeSpan? timeout = null)
    {
        var settings = config ?? Config();
        var http = new GovernmentHttp(new LoopbackClientFactory(bank.Wire(GovernmentHttp.CreateHandler())), settings);
        return new AnbPayrollApiClient(http, new FixedCredentials(), new PolicyGate(settings, AllOpen), new ListLogger<AnbPayrollApiClient>(),
            null, new AnbClientLimits(timeout ?? TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task RealHandler_A307OnThePayrollPost_IsNotFollowed_ExactlyOnePost_AndUnknown()
    {
        await using var bank = new LoopbackTlsBank(seen => seen.Host switch
        {
            "anb-auth.test" => new(200, JsonSerializer.Serialize(new { access_token = AccessToken, token_type = "Bearer", expires_in = 3600 })),
            "anb-sandbox.test" => new(307, "", "https://anb-redirect.test/collect"),
            _ => new(200, JsonSerializer.Serialize(new { id = PayrollId, status = "ACCEPTED", message = "" })),
        });
        var client = RealSocketClient(bank);

        var outcome = await client.SubmitAsync(Submit(Small), CancellationToken.None);

        bank.Requests.Where(r => r.Method == "POST" && r.Host != "anb-auth.test").Should().ContainSingle("a redirect must never re-send the payroll");
        bank.Requests.Should().NotContain(r => r.Host == "anb-redirect.test", "the IBANs and IDs in the body must not reach the Location host");
        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Unknown);
        outcome.HttpStatus.Should().Be(307);
        outcome.RequestSent.Should().BeTrue();
    }

    [Fact]
    public async Task RealHandler_A307OnTheTokenPost_IsNotFollowed_TheSecretIsNotResent_AndRefused()
    {
        var config = Config();
        config[AnbPayrollApiClient.ClientAuthMethodKey] = AnbPayrollApiClient.AuthPost;
        await using var bank = new LoopbackTlsBank(seen => seen.Host switch
        {
            "anb-auth.test" => new(307, "", "https://anb-redirect.test/steal"),
            "anb-redirect.test" => new(200, JsonSerializer.Serialize(new { access_token = AccessToken, token_type = "Bearer", expires_in = 3600 })),
            _ => new(200, JsonSerializer.Serialize(new { id = PayrollId, status = "ACCEPTED", message = "" })),
        });
        var client = RealSocketClient(bank, config);

        var outcome = await client.SubmitAsync(Submit(Small), CancellationToken.None);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Refused);
        outcome.Code.Should().Be(BankChannelCodes.TokenUnavailable);
        bank.Requests.Should().ContainSingle(r => r.Host == "anb-auth.test");
        bank.Requests.Should().NotContain(r => r.Host == "anb-redirect.test");
        bank.Requests.Where(r => r.BodyText.Contains(Uri.EscapeDataString(ClientSecret))).Should().ContainSingle("the secret went to the token host once, and nowhere else");
        bank.Requests.Should().NotContain(r => r.Host == "anb-sandbox.test", "no payroll POST without a token");
    }


    // ── 2. Nothing escapes once the POST was attempted ──────────────────────────────────────────

    public static TheoryData<string> AfterSendFailures => new() { "invalid-operation", "object-disposed", "not-supported", "custom" };

    [Theory]
    [MemberData(nameof(AfterSendFailures))]
    public async Task AnyExceptionAfterTheBodyWasRead_IsUnknown_NeverThrown(string kind)
    {
        var handler = new StubAnbHandler((_, _) => throw (kind switch
        {
            "invalid-operation" => new InvalidOperationException("pipeline"),
            "object-disposed" => new ObjectDisposedException("client"),
            "not-supported" => new NotSupportedException("x"),
            _ => (Exception)new CustomHandlerBug(),
        }));

        var outcome = await Client(handler, out _).SubmitAsync(Submit(Small), CancellationToken.None);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Unknown);
        outcome.RequestSent.Should().BeTrue();
        handler.Posts.Should().ContainSingle();
    }

    private sealed class CustomHandlerBug : Exception;

    [Fact]
    public async Task A200WithAnUnknownCharset_IsStillAccepted()
    {
        var handler = new StubAnbHandler(_ =>
        {
            var r = Accepted();
            r.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json; charset=bogus-9");
            return r;
        });
        var outcome = await Client(handler, out _).SubmitAsync(Submit(Small), CancellationToken.None);
        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Accepted);
        outcome.BankPayrollId.Should().Be(PayrollId);
    }

    [Fact]
    public async Task ALoggerThatThrows_NeverTurnsAnAcceptedPayrollIntoAnException()
    {
        var handler = new StubAnbHandler(_ => Accepted());
        var client = ThrowingLoggerClient(handler);

        var outcome = await client.SubmitAsync(Submit(Small), CancellationToken.None);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Accepted);
        outcome.BankPayrollId.Should().Be(PayrollId);
    }

    [Fact]
    public async Task ALoggerThatThrows_OnATransportFailure_StillGivesUnknown()
    {
        var handler = new StubAnbHandler((_, _) => throw new HttpRequestException("reset"));
        var client = ThrowingLoggerClient(handler);
        (await client.SubmitAsync(Submit(Small), CancellationToken.None)).Kind.Should().Be(BankSubmitOutcomeKind.Unknown);
    }

    private static AnbPayrollApiClient ThrowingLoggerClient(HttpMessageHandler handler)
    {
        var config = Config();
        return new AnbPayrollApiClient(Transport(handler, config), new FixedCredentials(), new PolicyGate(config, AllOpen), new ThrowingLogger(), null,
            new AnbClientLimits(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)));
    }

    private sealed class ThrowingLogger : ILogger<AnbPayrollApiClient>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => throw new InvalidOperationException("sink");
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            throw new AggregateException(new InvalidOperationException("sink down"));
    }

    [Fact]
    public async Task ACredentialSourceThatThrows_IsRefused_NotThrown_AndNothingIsSent()
    {
        var handler = new StubAnbHandler(_ => throw new InvalidOperationException("no call"));
        var outcome = await Client(handler, out _, credentials: new ExplodingCredentials()).SubmitAsync(Submit(Small), CancellationToken.None);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Refused);
        outcome.Code.Should().Be(BankChannelCodes.CredentialSourceFailed);
        handler.Requests.Should().BeEmpty();
    }

    private sealed class ExplodingCredentials : IBankCredentialSource
    {
        public Task<UnsealedGovernmentCredential?> OpenAsync(string body, BankChannelScope scope, CancellationToken ct) => throw new InvalidOperationException("store down");
    }

    [Fact]
    public async Task CancelledBeforeThePost_IsRefused_NotUnknown_AndNoPost()
    {
        using var cts = new CancellationTokenSource();
        var handler = new StubAnbHandler(_ => throw new InvalidOperationException("no POST"), _ => { cts.Cancel(); return TokenOk(); });

        var outcome = await Client(handler, out _).SubmitAsync(Submit(Small), cts.Token);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Refused);
        outcome.Code.Should().Be(BankChannelCodes.CancelledBeforeSend);
        handler.Posts.Should().BeEmpty();
    }

    // ── 4. Frozen bytes are copied before they are checked ──────────────────────────────────────

    [Fact]
    public async Task TheBytesSentAreTheBytesHashed_EvenIfTheCallersArrayChangesDuringTheTokenCall()
    {
        var batch = BuildBatch(3);
        var original = (byte[])batch.Body.Content.Clone();
        var handler = new StubAnbHandler(_ => Accepted(), _ =>
        {
            batch.Body.Content[^3] ^= 0x01; // someone else's write lands between the check and the send
            return TokenOk();
        });

        var outcome = await Client(handler, out _).SubmitAsync(Submit(batch), CancellationToken.None);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Accepted);
        var parts = await AnbPayrollApiSubmitTests.ReadMultipart(handler.Posts.Single());
        parts["body"].Should().Equal(original);
        outcome.SentBodySha256.Should().Be(AnbConnectCsvGenerator.Sha256Hex(original));
        outcome.SentHeaderSha256.Should().Be(batch.Header.ExpectedSha256);
    }

    // ── Bounded reads ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnOversizedSubmitResponse_IsUnknown_NotBuffered(bool withLength)
    {
        var big = new string(' ', 3 * 1024 * 1024) + "{}";
        var handler = new StubAnbHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK);
            r.Content = withLength ? new StringContent(big) : new StreamContent(new NoLengthStream(Encoding.UTF8.GetBytes(big)));
            return r;
        });
        var outcome = await Client(handler, out _).SubmitAsync(Submit(Small), CancellationToken.None);
        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Unknown);
        outcome.Code.Should().Be(BankChannelCodes.AcceptedWithoutId);
    }

    [Fact]
    public async Task AnOversizedStatusPage_IsRefused()
    {
        var handler = new StubAnbHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new NoLengthStream(new byte[2 * 1024 * 1024])) });
        var act = () => Client(handler, out _).GetStatusAsync(TestScope, PayrollId, null, CancellationToken.None);
        (await act.Should().ThrowAsync<BankChannelException>()).Which.Code.Should().Be(BankChannelCodes.ResponseTooLarge);
    }

    /// <summary>A stream that does not report its length, like a chunked response.</summary>
    private sealed class NoLengthStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
    }

    // ── Credential binding and the host allow-list ───────────────────────────────────────────────

    [Theory]
    [InlineData("production", "anb-sandbox.test", "anb-auth.test", "oauth_client", "same")]
    [InlineData("sandbox", "anb-prod.test", "anb-auth.test", "oauth_client", "same")]
    [InlineData("sandbox", "anb-sandbox.test", "anb-auth-prod.test", "oauth_client", "same")]
    [InlineData("sandbox", "anb-sandbox.test", "anb-auth.test", "api_key", "same")]
    [InlineData("sandbox", "anb-sandbox.test", "anb-auth.test", "oauth_client", "other")]
    public async Task ACredentialIssuedForAnotherEnvironmentHostKindOrCompany_IsRefused(string env, string baseHost, string tokenHost, string kind, string company)
    {
        var credential = Credential(environment: env, baseHost: baseHost, tokenHost: tokenHost, kind: kind,
            companyId: company == "other" ? Guid.NewGuid() : TestScope.CompanyId);
        var handler = new StubAnbHandler(_ => throw new InvalidOperationException("no call"));
        var client = Client(handler, out _, credentials: new FixedCredentials(credential));

        var outcome = await client.SubmitAsync(Submit(Small), CancellationToken.None);
        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Refused);
        outcome.Code.Should().Be(BankChannelCodes.CredentialEndpointMismatch);

        var read = () => client.GetStatusAsync(TestScope, PayrollId, null, CancellationToken.None);
        (await read.Should().ThrowAsync<BankChannelRefusedException>()).Which.Code.Should().Be(BankChannelCodes.CredentialEndpointMismatch);
        handler.Requests.Should().BeEmpty();
    }

    // ── The operator's allow-list (GovernmentHttp), not a list of the bank client's own ──────────

    [Theory]
    [InlineData("https://10.0.0.5")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://[::1]")]
    [InlineData("https://169.254.169.254")]
    [InlineData("http://anb-sandbox.test")]
    [InlineData("https://anb-sandbox.test/api")]
    [InlineData("https://user:pw@anb-sandbox.test")]
    [InlineData("https://anb-sandbox.test?x=1")]
    [InlineData("anb-sandbox.test")]
    public async Task ABaseUrlThatIsNotAPlainHttpsOriginWithAHostName_IsRefused_BeforeAnySocket(string url)
    {
        var config = Config();
        config[GovernmentHttp.BaseUrlKey(GovernmentBodies.BankAnb, "sandbox")] = url;
        var handler = new StubAnbHandler(_ => throw new InvalidOperationException("no call"));
        var client = Client(handler, out _, config);

        var outcome = await client.SubmitAsync(Submit(Small), CancellationToken.None);
        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Refused);
        outcome.Code.Should().Be(BankChannelCodes.HostNotAllowed);
        var read = () => client.GetStatusAsync(TestScope, PayrollId, null, CancellationToken.None);
        (await read.Should().ThrowAsync<BankChannelRefusedException>()).Which.Code.Should().Be(BankChannelCodes.HostNotAllowed);
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("base")]
    [InlineData("token")]
    public async Task ASandboxUrlOnTheProductionHost_IsRefused(string which)
    {
        // Fable P2-17: the environment is not a label. A sandbox setting pointing at the production host would send real files
        // under the sandbox rung, which needs no legal clearance.
        var config = Config();
        if (which == "base") config[GovernmentHttp.BaseUrlKey(GovernmentBodies.BankAnb, "sandbox")] = ProductionBaseUrl;
        else config[GovernmentHttp.TokenUrlKey(GovernmentBodies.BankAnb, "sandbox")] = ProductionTokenOrigin;
        var handler = new StubAnbHandler(_ => throw new InvalidOperationException("no call"));

        var outcome = await Client(handler, out _, config).SubmitAsync(Submit(Small), CancellationToken.None);

        outcome.Code.Should().Be(BankChannelCodes.HostNotAllowed);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AnEnvironmentThatIsOnlyALabel_IsRefused()
    {
        var handler = new StubAnbHandler(_ => throw new InvalidOperationException("no call"));
        var uat = TestScope with { Environment = "uat" };

        var outcome = await Client(handler, out _).SubmitAsync(Submit(Small, scope: uat), CancellationToken.None);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Refused);
        outcome.Code.Should().Be(BankChannelCodes.EnvironmentMismatch);
        handler.Requests.Should().BeEmpty();
    }

    // ── Token cache key and lifetime ─────────────────────────────────────────────────────────────

    private sealed class MutableCredentials(Func<BankChannelScope, UnsealedGovernmentCredential> make) : IBankCredentialSource
    {
        public Func<BankChannelScope, UnsealedGovernmentCredential> Make = make;
        public Task<UnsealedGovernmentCredential?> OpenAsync(string body, BankChannelScope scope, CancellationToken ct) =>
            Task.FromResult<UnsealedGovernmentCredential?>(Make(scope));
    }

    [Fact]
    public async Task AnotherTenant_NeverReusesThisTenantsToken()
    {
        var handler = new StubAnbHandler(_ => Accepted());
        // Each tenant's credential carries its own tenant (a credential is never used for another tenant's call).
        var client = Client(handler, out _, credentials: new MutableCredentials(scope => Credential(tenantId: scope.TenantId)));
        await client.SubmitAsync(Submit(Small), CancellationToken.None);
        await client.SubmitAsync(Submit(Small, scope: TestScope with { TenantId = Guid.NewGuid() }), CancellationToken.None);
        handler.TokenRequests.Should().HaveCount(2);
    }

    [Fact]
    public async Task ACredentialOfAnotherTenant_IsRefused_NoTokenRequestAndNoPost()
    {
        var handler = new StubAnbHandler(_ => Accepted());
        var client = Client(handler, out _, credentials: new FixedCredentials(Credential(tenantId: Guid.NewGuid())));

        var outcome = await client.SubmitAsync(Submit(Small), CancellationToken.None);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Refused);
        outcome.Code.Should().Be(BankChannelCodes.CredentialEndpointMismatch);
        outcome.RequestSent.Should().BeFalse();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AnotherClientId_OrASecretChangedUnderTheSameVersion_GetsItsOwnToken()
    {
        var handler = new StubAnbHandler(_ => Accepted());
        var creds = new MutableCredentials(_ => Credential());
        var client = Client(handler, out _, credentials: creds);

        await client.SubmitAsync(Submit(Small), CancellationToken.None);
        creds.Make = _ => Credential(clientId: "kx-other-client");
        await client.SubmitAsync(Submit(Small), CancellationToken.None);
        creds.Make = _ => Credential(clientId: "kx-other-client", secret: "another-SYNTHETIC-secret");
        await client.SubmitAsync(Submit(Small), CancellationToken.None);

        handler.TokenRequests.Should().HaveCount(3);
    }

    [Fact]
    public async Task AnotherEnvironmentOrBaseUrl_GetsItsOwnToken()
    {
        var handler = new StubAnbHandler(_ => Accepted());
        var config = Config(productionClearance: true);
        var creds = new MutableCredentials(scope => Credential(environment: scope.Environment));
        var client = Client(handler, out _, config, creds);

        await client.SubmitAsync(Submit(Small), CancellationToken.None);
        config[GovernmentHttp.BaseUrlKey(GovernmentBodies.BankAnb, "sandbox")] = "https://anb-sandbox-2.test";
        creds.Make = scope => Credential(environment: scope.Environment, baseHost: scope.Environment == "sandbox" ? "anb-sandbox-2.test" : null);
        await client.SubmitAsync(Submit(Small), CancellationToken.None);
        await client.SubmitAsync(Submit(Small, scope: TestScope with { Environment = "production" }), CancellationToken.None);

        handler.TokenRequests.Should().HaveCount(3);
        handler.Posts.Should().HaveCount(3);
        handler.Posts[1].Uri.Host.Should().Be("anb-sandbox-2.test");
        handler.Posts[2].Uri.Host.Should().Be("anb-prod.test");
        handler.TokenRequests[2].Uri.Host.Should().Be("anb-prod-auth.test");
    }

    [Fact]
    public async Task ACachedToken_IsUsedUntilItsExpiryLessSkew_ThenRenewed()
    {
        var clock = new ManualClock();
        var handler = new StubAnbHandler(_ => Accepted(), _ => TokenOk(expiresIn: 120));
        var client = Client(handler, out _, clock: clock);

        await client.SubmitAsync(Submit(Small), CancellationToken.None);
        clock.Now = clock.Now.AddSeconds(59);
        await client.SubmitAsync(Submit(Small), CancellationToken.None);
        handler.TokenRequests.Should().HaveCount(1);

        clock.Now = clock.Now.AddSeconds(2);
        await client.SubmitAsync(Submit(Small), CancellationToken.None);
        handler.TokenRequests.Should().HaveCount(2);
    }

    [Fact]
    public async Task AHugeExpiresIn_IsClampedToAnHour()
    {
        var clock = new ManualClock();
        var handler = new StubAnbHandler(_ => Accepted(), _ => TokenOk(expiresIn: 1_000_000_000));
        var client = Client(handler, out _, clock: clock);

        await client.SubmitAsync(Submit(Small), CancellationToken.None);
        clock.Now = clock.Now.AddSeconds(3_600);
        await client.SubmitAsync(Submit(Small), CancellationToken.None);

        handler.TokenRequests.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("1e999")]
    [InlineData("-5")]
    [InlineData("1e12")]
    public async Task AnAbsurdExpiresIn_NeverThrows(string expiresIn)
    {
        var handler = new StubAnbHandler(_ => Accepted(), _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"access_token\":\"" + AccessToken + "\",\"token_type\":\"Bearer\",\"expires_in\":" + expiresIn + "}"),
        });
        var outcome = await Client(handler, out _, clock: new ManualClock()).SubmitAsync(Submit(Small), CancellationToken.None);
        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Accepted);
    }

    // ── Classification ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(404)]
    [InlineData(405)]
    [InlineData(406)]
    [InlineData(410)]
    public async Task AWrongEndpoint_IsUnknown_NeverAFileProblemToRegenerate(int status)
    {
        var handler = new StubAnbHandler(_ => Json((HttpStatusCode)status, new { message = "x" }));
        var outcome = await Client(handler, out _).SubmitAsync(Submit(Small), CancellationToken.None);
        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Unknown);
        outcome.Code.Should().Be(BankChannelCodes.EndpointWrong);
    }

    [Theory]
    [InlineData(499)]
    [InlineData(418)]
    [InlineData(451)]
    public async Task AnUnlistedClientError_IsUnknown(int status)
    {
        var handler = new StubAnbHandler(_ => Json((HttpStatusCode)status, new { message = "x" }));
        var outcome = await Client(handler, out _).SubmitAsync(Submit(Small), CancellationToken.None);
        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Unknown);
        outcome.Code.Should().Be(BankChannelCodes.OutcomeUnknown);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(422)]
    public async Task AStructuralRefusalAfterAnEarlierUnknown_IsNotARegenerateSignal(int status)
    {
        var handler = new StubAnbHandler(_ => Json((HttpStatusCode)status, new { message = "Duplicate batch number" }));
        var outcome = await Client(handler, out _).SubmitAsync(Submit(Small) with { PriorOutcomeUnknown = true }, CancellationToken.None);
        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Unknown);
        outcome.Code.Should().Be(BankChannelCodes.RejectedAfterUnknown);
        outcome.Message.Should().Contain("Duplicate");
    }

    [Theory]
    [InlineData("REJECTED")]
    [InlineData("FAILED")]
    public async Task A2xxWhoseStatusSaysRejected_IsNotAccepted_ButKeepsTheId(string status)
    {
        var handler = new StubAnbHandler(_ => Json(HttpStatusCode.OK, new { id = PayrollId, status, message = "Insufficient balance" }));
        var outcome = await Client(handler, out _).SubmitAsync(Submit(Small), CancellationToken.None);
        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Unknown);
        outcome.Code.Should().Be(BankChannelCodes.AcceptedWithFailedStatus);
        outcome.BankPayrollId.Should().Be(PayrollId, "the person checking with the bank needs the id");
    }

    [Theory]
    [InlineData("3f2b8c1e-9d4a-4e7b-8a61-0c5d2e9f1a77\n")]
    [InlineData("abc\n")]
    public async Task APayrollIdWithATrailingNewline_IsRefused(string id)
    {
        var handler = new StubAnbHandler(_ => throw new InvalidOperationException("no call"));
        var act = () => Client(handler, out _).GetStatusAsync(TestScope, id, null, CancellationToken.None);
        (await act.Should().ThrowAsync<BankChannelRefusedException>()).Which.Code.Should().Be(BankChannelCodes.PayrollIdInvalid);
    }

    [Fact]
    public async Task TheWpsDownload_AcceptsAnyContentType()
    {
        var handler = new StubAnbHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1 }) });
        await Client(handler, out _).DownloadWpsAsync(TestScope, PayrollId, null, CancellationToken.None);
        handler.ApiRequests.Single().Headers["Accept"].Should().Be("*/*");
    }

    // ── Masking that does not depend on the sent-values list ────────────────────────────────────

    [Fact]
    public async Task AnIbanOrIdTheBankEchoesThatWeDidNotSend_IsStillMasked()
    {
        var unsentIban = Iban(9_999);
        var unsentId = NationalId(9_999);
        var handler = new StubAnbHandler(_ => Json(HttpStatusCode.BadRequest, new { message = $"Account {unsentIban} for {unsentId} is blocked" }));
        var outcome = await Client(handler, out _).SubmitAsync(Submit(Small), CancellationToken.None);

        outcome.Message.Should().NotContain(unsentIban).And.NotContain(unsentId).And.Contain("blocked");
    }

    [Fact]
    public async Task ThePayrollReason_IsMasked()
    {
        var batch = BuildBatch(1);
        var handler = new StubAnbHandler(_ => Json(HttpStatusCode.OK,
            Page("REJECTED", new[] { Line(batch.Rows[0], "INITIATED") }, 1, 50, 1, reason: $"Debit account {Iban(77)} has insufficient balance")));
        var snapshot = await Client(handler, out _).GetStatusAsync(TestScope, PayrollId, null, CancellationToken.None);
        snapshot.Reason.Should().NotContain(Iban(77)).And.Contain("insufficient");
    }
}
