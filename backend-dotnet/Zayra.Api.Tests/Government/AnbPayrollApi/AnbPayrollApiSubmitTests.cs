using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Zayra.Api.Infrastructure.Government.Credentials;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Api;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Channels;
using static Zayra.Api.Tests.Government.AnbPayrollApi.AnbApiTestKit;

namespace Zayra.Api.Tests.Government.AnbPayrollApi;

/// <summary>
/// The money path: <c>POST /payroll/v2</c>. Exactly one POST per call, the frozen bytes unchanged, and
/// an outcome class the caller can act on safely. A body that may have reached the bank is Unknown and
/// is never sent again by this client.
/// </summary>
public class AnbPayrollApiSubmitTests
{
    private static readonly Batch Small = BuildBatch(3);

    [Fact]
    public async Task Accepted_On2xxWithId_SendsTheFrozenBytesOnce_AsMultipartHeaderAndBody()
    {
        var handler = new StubAnbHandler(_ => Json(HttpStatusCode.OK, new { id = PayrollId, status = "ACCEPTED", message = "ok" }));
        var client = Client(handler, out _);

        var outcome = await client.SubmitAsync(Submit(Small), CancellationToken.None);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Accepted);
        outcome.Code.Should().Be(BankChannelCodes.Accepted);
        outcome.BankPayrollId.Should().Be(PayrollId);
        outcome.BankStatus.Should().Be("ACCEPTED");
        outcome.RequestSent.Should().BeTrue();
        handler.Posts.Should().ContainSingle();

        var post = handler.Posts[0];
        post.Uri.AbsoluteUri.Should().Be(BaseUrl + "/payroll/v2");
        post.Headers["Authorization"].Should().Be("Bearer " + AccessToken);
        var parts = await ReadMultipart(post);
        parts.Keys.Should().BeEquivalentTo(new[] { "header", "body" });
        parts["header"].Should().Equal(Small.Header.Content);
        parts["body"].Should().Equal(Small.Body.Content);
    }

    [Fact]
    public async Task SendsTheFrozenBytes_NotARegeneratedCopy()
    {
        // A frozen body the generator would NOT produce today (amounts written "6300.00", not "6300").
        // Any path that re-derives the file from inputs sends different bytes and fails here.
        var frozenText = System.Text.Encoding.UTF8.GetString(Small.Body.Content);
        var rewritten = string.Join("\r\n", frozenText.Split("\r\n").Select((line, i) =>
        {
            if (i == 0 || line.Length == 0) return line;
            var cells = line.Split(',');
            cells[2] = decimal.Parse(cells[2], System.Globalization.CultureInfo.InvariantCulture)
                .ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            return string.Join(",", cells);
        }));
        var bytes = Encoding.UTF8.GetBytes(rewritten);
        bytes.Should().NotEqual(Small.Body.Content);
        var frozen = Small.Body with { Content = bytes, ExpectedSha256 = AnbConnectCsvGenerator.Sha256Hex(bytes) };
        var handler = new StubAnbHandler(_ => Json(HttpStatusCode.OK, new { id = PayrollId, status = "ACCEPTED", message = "" }));

        var outcome = await Client(handler, out _).SubmitAsync(WithReceiptForItsFiles(Submit(Small) with { Body = frozen }), CancellationToken.None);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Accepted);
        var parts = await ReadMultipart(handler.Posts.Single());
        parts["body"].Should().Equal(bytes);
        AnbConnectCsvGenerator.Sha256Hex(parts["body"]).Should().Be(frozen.ExpectedSha256);
        parts["header"].Should().Equal(Small.Header.Content);
    }

    [Fact]
    public async Task Accepted_On201()
    {
        var handler = new StubAnbHandler(_ => Json(HttpStatusCode.Created, new { id = PayrollId, status = "ACCEPTED", message = "" }));
        var outcome = await Client(handler, out _).SubmitAsync(Submit(Small), CancellationToken.None);
        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Accepted);
    }

    [Theory]
    [InlineData("{\"status\":\"ACCEPTED\",\"message\":\"ok\"}")]
    [InlineData("{\"id\":\"\",\"status\":\"ACCEPTED\",\"message\":\"ok\"}")]
    [InlineData("not json")]
    public async Task Unknown_On2xxWithoutAnId_BecauseTheBankMayHaveIt(string body)
    {
        var handler = new StubAnbHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        var outcome = await Client(handler, out _).SubmitAsync(Submit(Small), CancellationToken.None);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Unknown);
        outcome.Code.Should().Be(BankChannelCodes.AcceptedWithoutId);
        handler.Posts.Should().ContainSingle();
    }

    [Theory]
    [InlineData(400)]
    [InlineData(413)]
    [InlineData(415)]
    [InlineData(422)]
    public async Task RejectedStructure_On4xx(int status)
    {
        var handler = new StubAnbHandler(_ => Json((HttpStatusCode)status, new { message = "Invalid header file" }));
        var outcome = await Client(handler, out _).SubmitAsync(Submit(Small), CancellationToken.None);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.RejectedStructure);
        outcome.Code.Should().Be(BankChannelCodes.RejectedStructure);
        outcome.HttpStatus.Should().Be(status);
        outcome.Message.Should().Be("Invalid header file");
        handler.Posts.Should().ContainSingle();
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task RejectedAuthorization_On401Or403_NoSecondPost_AndTheCachedTokenIsDropped(int status)
    {
        var handler = new StubAnbHandler(_ => Json((HttpStatusCode)status, new { message = "unauthorized" }));
        var client = Client(handler, out _);

        var outcome = await client.SubmitAsync(Submit(Small), CancellationToken.None);
        outcome.Kind.Should().Be(BankSubmitOutcomeKind.RejectedAuthorization);
        handler.Posts.Should().ContainSingle();

        await client.SubmitAsync(Submit(Small), CancellationToken.None);
        handler.TokenRequests.Should().HaveCount(2, "a token the bank refused is not reused");
    }

    [Theory]
    [InlineData(408)]
    [InlineData(409)]
    [InlineData(425)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    [InlineData(302)]
    public async Task Unknown_WhenTheBankMayHaveProcessedIt_AndExactlyOnePost(int status)
    {
        var handler = new StubAnbHandler(_ => Json((HttpStatusCode)status, new { message = "x" }));
        var outcome = await Client(handler, out _).SubmitAsync(Submit(Small), CancellationToken.None);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Unknown);
        outcome.Code.Should().Be(BankChannelCodes.OutcomeUnknown);
        outcome.HttpStatus.Should().Be(status);
        handler.Posts.Should().ContainSingle();
    }

    [Fact]
    public async Task Unknown_OnTimeoutAfterTheBodyWasSent_ExactlyOnePost()
    {
        var handler = new StubAnbHandler(async (req, ct) =>
        {
            // The body has been read in full by the stub: it reached the "bank". Then silence.
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = Client(handler, out _, timeout: TimeSpan.FromMilliseconds(300));

        var outcome = await client.SubmitAsync(Submit(Small), CancellationToken.None);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Unknown);
        outcome.RequestSent.Should().BeTrue();
        handler.Posts.Should().ContainSingle();
        handler.Posts[0].Content.Length.Should().BeGreaterThan(Small.Body.Content.Length);
    }

    [Fact]
    public async Task Unknown_OnConnectionResetAfterTheBodyWasSent_ExactlyOnePost()
    {
        var handler = new StubAnbHandler((_, _) =>
            throw new HttpRequestException("reset", new IOException("Connection reset by peer")));
        var outcome = await Client(handler, out _).SubmitAsync(Submit(Small), CancellationToken.None);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Unknown);
        handler.Posts.Should().ContainSingle();
    }

    [Fact]
    public async Task Unknown_WhenTheCallerCancelsDuringThePost()
    {
        using var cts = new CancellationTokenSource();
        var handler = new StubAnbHandler(async (_, ct) =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var outcome = await Client(handler, out _).SubmitAsync(Submit(Small), cts.Token);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Unknown);
        handler.Posts.Should().ContainSingle();
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public async Task Refused_On5xxFromTheTokenService_NoPostSocket(int status)
    {
        var handler = new StubAnbHandler(_ => throw new InvalidOperationException("must not POST"),
            _ => Json((HttpStatusCode)status, new { error = "temporarily_unavailable" }));
        var outcome = await Client(handler, out _).SubmitAsync(Submit(Small), CancellationToken.None);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Refused);
        outcome.Code.Should().Be(BankChannelCodes.TokenUnavailable);
        outcome.RequestSent.Should().BeFalse();
        handler.Posts.Should().BeEmpty();
        handler.TokenRequests.Should().ContainSingle("no retry storm against the token service");
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    public async Task Refused_WhenTheTokenServiceRejectsTheCredential(int status)
    {
        var handler = new StubAnbHandler(_ => throw new InvalidOperationException("must not POST"),
            _ => Json((HttpStatusCode)status, new { error = "invalid_client" }));
        var outcome = await Client(handler, out _).SubmitAsync(Submit(Small), CancellationToken.None);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Refused);
        outcome.Code.Should().Be(BankChannelCodes.TokenRejected);
        handler.Posts.Should().BeEmpty();
    }

    [Fact]
    public async Task Refused_WhenTheTokenServiceTimesOut_NoPostSocket()
    {
        var handler = new StubAnbHandler((_, _) => throw new InvalidOperationException("must not POST"),
            async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(HttpStatusCode.OK); });
        var outcome = await Client(handler, out _, timeout: TimeSpan.FromMilliseconds(200)).SubmitAsync(Submit(Small), CancellationToken.None);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Refused);
        outcome.Code.Should().Be(BankChannelCodes.TokenUnavailable);
        handler.Posts.Should().BeEmpty();
    }

    [Theory]
    [InlineData("{\"access_token\":\"\",\"token_type\":\"Bearer\",\"expires_in\":3600}")]
    [InlineData("{\"access_token\":\"abc\",\"token_type\":\"mac\",\"expires_in\":3600}")]
    [InlineData("<html>")]
    public async Task Refused_OnAnUnusableTokenResponse(string body)
    {
        var handler = new StubAnbHandler(_ => throw new InvalidOperationException("must not POST"),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        var outcome = await Client(handler, out _).SubmitAsync(Submit(Small), CancellationToken.None);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Refused);
        outcome.Code.Should().Be(BankChannelCodes.TokenUnavailable);
        handler.Posts.Should().BeEmpty();
    }

    [Fact]
    public async Task TokenRequest_IsClientCredentialsWithConfiguredScope_AndBasicClientAuth()
    {
        var handler = new StubAnbHandler(_ => Json(HttpStatusCode.OK, new { id = PayrollId, status = "ACCEPTED", message = "" }));
        await Client(handler, out _).SubmitAsync(Submit(Small), CancellationToken.None);

        var token = handler.TokenRequests.Should().ContainSingle().Subject;
        token.Method.Should().Be(HttpMethod.Post);
        var form = Encoding.UTF8.GetString(token.Content);
        form.Should().Contain("grant_type=client_credentials").And.Contain("scope=payroll");
        form.Should().NotContain(ClientSecret);
        token.Headers["Authorization"].Should().Be("Basic " +
            Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.EscapeDataString(ClientId) + ":" + Uri.EscapeDataString(ClientSecret))));
    }

    [Fact]
    public async Task TokenRequest_PostsTheClientCredentialInTheForm_WhenConfiguredSo()
    {
        var config = Config();
        config[AnbPayrollApiClient.ClientAuthMethodKey] = AnbPayrollApiClient.AuthPost;
        var handler = new StubAnbHandler(_ => Json(HttpStatusCode.OK, new { id = PayrollId, status = "ACCEPTED", message = "" }));
        await Client(handler, out _, config).SubmitAsync(Submit(Small), CancellationToken.None);

        var token = handler.TokenRequests.Should().ContainSingle().Subject;
        token.Headers.ContainsKey("Authorization").Should().BeFalse();
        var form = Encoding.UTF8.GetString(token.Content);
        form.Should().Contain("client_id=" + Uri.EscapeDataString(ClientId)).And.Contain("client_secret=" + Uri.EscapeDataString(ClientSecret));
    }

    [Fact]
    public async Task Refused_WhenAFrozenFileNoLongerMatchesItsRecordedHash_NothingIsSent()
    {
        foreach (var tamper in new Func<Batch, AnbSubmitRequest>[]
                 {
                     b => Submit(b) with { Body = b.Body with { Content = Flip(b.Body.Content) } },
                     b => Submit(b) with { Header = b.Header with { Content = Flip(b.Header.Content) } },
                     b => Submit(b) with { Body = b.Body with { ExpectedSha256 = new string('0', 64) } },
                     b => Submit(b) with { Body = b.Body with { ExpectedSha256 = "" } },
                 })
        {
            var handler = new StubAnbHandler(_ => throw new InvalidOperationException("must not POST"));
            var outcome = await Client(handler, out _).SubmitAsync(tamper(Small), CancellationToken.None);

            outcome.Kind.Should().Be(BankSubmitOutcomeKind.Refused);
            outcome.Code.Should().Be(BankChannelCodes.ArtifactHashMismatch);
            handler.Requests.Should().BeEmpty("not even a token is fetched for a tampered file");
        }
    }

    [Fact]
    public async Task Refused_WhenTheHeaderTotalsDoNotMatchTheBody_EvenWithMatchingHashes()
    {
        // Header from one batch, body from another: each file's own hash is intact.
        var other = BuildBatch(4);
        var request = WithReceiptForItsFiles(Submit(Small) with { Body = other.Body });
        var handler = new StubAnbHandler(_ => throw new InvalidOperationException("must not POST"));

        var outcome = await Client(handler, out _).SubmitAsync(request, CancellationToken.None);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Refused);
        outcome.Code.Should().Be(BankChannelCodes.ArtifactInconsistent);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Refused_WhenAutoWpsInTheRequestDisagreesWithTheFrozenHeader()
    {
        var plain = Small;
        var withWps = BuildBatch(3, autoWps: true);
        var handler = new StubAnbHandler(_ => throw new InvalidOperationException("must not POST"));
        var client = Client(handler, out _);

        (await client.SubmitAsync(Submit(plain, new AnbSubmitHeaders(AutoWpsFileUpload: true, NationalUnifiedNo: "7001234567")), CancellationToken.None))
            .Code.Should().Be(BankChannelCodes.ArtifactInconsistent);
        (await client.SubmitAsync(Submit(withWps, new AnbSubmitHeaders()), CancellationToken.None))
            .Code.Should().Be(BankChannelCodes.ArtifactInconsistent);
        (await client.SubmitAsync(Submit(withWps, new AnbSubmitHeaders(AutoWpsFileUpload: true, NationalUnifiedNo: "7009999999")), CancellationToken.None))
            .Code.Should().Be(BankChannelCodes.ArtifactInconsistent);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AutoWps_SendsTheSpecHeaders_WhenTheFrozenHeaderAgrees()
    {
        var withWps = BuildBatch(3, autoWps: true);
        var handler = new StubAnbHandler(_ => Json(HttpStatusCode.OK, new { id = PayrollId, status = "ACCEPTED", message = "" }));
        var outcome = await Client(handler, out _).SubmitAsync(
            Submit(withWps, new AnbSubmitHeaders(ProfileId: "P-77", AutoWpsFileUpload: true, NationalUnifiedNo: "7001234567")),
            CancellationToken.None);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Accepted);
        var post = handler.Posts.Single();
        post.Headers["autowpsfileupload"].Should().Be("YES");
        post.Headers["nationalunifiedno"].Should().Be("7001234567");
        post.Headers["profileid"].Should().Be("P-77");
        post.Headers.ContainsKey("feescategorycode").Should().BeFalse("unset optional headers are not sent");
    }

    [Theory]
    [InlineData("P-1\r\nX-Injected: 1")]
    [InlineData("P-1\n")]
    [InlineData("P\u0000")]
    public async Task Refused_OnAControlCharacterInAnOptionalHeader(string profile)
    {
        var handler = new StubAnbHandler(_ => throw new InvalidOperationException("must not POST"));
        var outcome = await Client(handler, out _).SubmitAsync(Submit(Small, new AnbSubmitHeaders(ProfileId: profile)), CancellationToken.None);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Refused);
        outcome.Code.Should().Be(BankChannelCodes.SubmitHeaderInvalid);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Refused_WhenNoCredentialIsHeld_NoSocketAtAll()
    {
        var handler = new StubAnbHandler(_ => throw new InvalidOperationException("must not POST"));
        var outcome = await Client(handler, out _, credentials: new FixedCredentials(none: true))
            .SubmitAsync(Submit(Small), CancellationToken.None);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Refused);
        outcome.Code.Should().Be(BankChannelCodes.CredentialMissing);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Refused_WhenTheCredentialIsForAnotherEnvironmentThanTheScope()
    {
        // A cleared production call holding only the sandbox credential: refused before any socket, the token request included.
        var handler = new StubAnbHandler(_ => throw new InvalidOperationException("must not POST"));
        var production = TestScope with { Environment = "production" };
        var outcome = await Client(handler, out _, Config(productionClearance: true)).SubmitAsync(Submit(Small, scope: production), CancellationToken.None);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Refused);
        outcome.Code.Should().Be(BankChannelCodes.CredentialEndpointMismatch);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Submit_A3000LineBatchWith35CharacterArabicNames_ByteForByte()
    {
        var big = BuildBatch(3000);
        big.Rows.Should().OnlyContain(r => r.EmployeeName!.Length == 35);
        var handler = new StubAnbHandler(_ => Json(HttpStatusCode.OK, new { id = PayrollId, status = "ACCEPTED", message = "" }));

        var outcome = await Client(handler, out _).SubmitAsync(Submit(big), CancellationToken.None);

        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Accepted);
        var parts = await ReadMultipart(handler.Posts.Single());
        parts["body"].Should().Equal(big.Body.Content);
        AnbConnectCsvGenerator.Sha256Hex(parts["body"]).Should().Be(big.Body.ExpectedSha256);
        Encoding.UTF8.GetString(parts["body"]).Should().Contain(ArabicName35(2999));
    }

    [Fact]
    public async Task TheTokenIsCachedPerScopeAndCredentialVersion()
    {
        var handler = new StubAnbHandler(_ => Json(HttpStatusCode.OK, new { id = PayrollId, status = "ACCEPTED", message = "" }));
        var creds = new RotatingCredentials();
        var client = Client(handler, out _, credentials: creds);

        await client.SubmitAsync(Submit(Small), CancellationToken.None);
        await client.SubmitAsync(Submit(Small), CancellationToken.None);
        handler.TokenRequests.Should().HaveCount(1);

        creds.Version = 2;
        await client.SubmitAsync(Submit(Small), CancellationToken.None);
        handler.TokenRequests.Should().HaveCount(2, "a rotated credential never reuses a token minted under the old one");

        await client.SubmitAsync(Submit(Small, scope: TestScope with { CompanyId = Guid.NewGuid() }), CancellationToken.None);
        handler.TokenRequests.Should().HaveCount(3, "another company never reuses this company's token");
    }

    private sealed class RotatingCredentials : IBankCredentialSource
    {
        public int Version = 1;
        public Task<UnsealedGovernmentCredential?> OpenAsync(string body, BankChannelScope scope, CancellationToken ct) =>
            Task.FromResult<UnsealedGovernmentCredential?>(Credential(version: Version, companyId: scope.CompanyId));
    }

    private static byte[] Flip(byte[] content)
    {
        var copy = (byte[])content.Clone();
        copy[^3] ^= 0x01;
        return copy;
    }

    internal static async Task<Dictionary<string, byte[]>> ReadMultipart(CapturedRequest request)
    {
        var mediaType = MediaTypeHeaderValue.Parse(request.ContentType);
        mediaType.MediaType.Value.Should().Be("multipart/form-data");
        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value!;
        var reader = new MultipartReader(boundary, new MemoryStream(request.Content));
        var parts = new Dictionary<string, byte[]>();
        while (await reader.ReadNextSectionAsync() is { } section)
        {
            var disposition = ContentDispositionHeaderValue.Parse(section.ContentDisposition);
            disposition.DispositionType.Value.Should().Be("form-data");
            disposition.FileName.HasValue.Should().BeTrue("ANB's schema declares both parts format: binary (files)");
            using var ms = new MemoryStream();
            await section.Body.CopyToAsync(ms);
            parts.Add(HeaderUtilities.RemoveQuotes(disposition.Name).Value!, ms.ToArray());
        }
        return parts;
    }
}
