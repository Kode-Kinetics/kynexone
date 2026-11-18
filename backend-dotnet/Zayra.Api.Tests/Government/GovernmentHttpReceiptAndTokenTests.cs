using System.Net;
using System.Reflection;
using FluentAssertions;
using Zayra.Api.Application.Auth;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Infrastructure.Government.Credentials;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Government.GovernmentLiveGateTests;

namespace Zayra.Api.Tests.Government;

/// <summary>
/// Final-gate fixes in the shared transport. The intent receipt is spent by the transport itself, once, immediately before the
/// socket (security P2-A), and it is bound to its company. The token grant has one public form, bound to a credential, and it posts
/// only to the operator's fixed token path for the body and environment (security P2-B). A refusal says whether anything was sent.
/// </summary>
public sealed class GovernmentHttpReceiptAndTokenTests
{
    private const string ApiOrigin = "https://bank-sandbox.example.test";
    private const string TokenOrigin = "https://auth.bank-sandbox.example.test";
    private static readonly GovernmentLiveGates AllOpen = new(true, true, true, true);

    private static Microsoft.Extensions.Configuration.IConfiguration Settings(string? tokenPath = "/oauth2/token") => Config(
        (GovernmentHttp.BaseUrlKey(GovernmentBodies.BankAnb, "sandbox"), ApiOrigin),
        (GovernmentHttp.TokenUrlKey(GovernmentBodies.BankAnb, "sandbox"), TokenOrigin),
        (GovernmentHttp.TokenPathKey(GovernmentBodies.BankAnb, "sandbox"), tokenPath));

    /// <summary>The write decision for the synthetic tenant, tenant-wide or for one company (a decision is for exactly one scope).</summary>
    private static GovernmentLiveDecision Write(Microsoft.Extensions.Configuration.IConfiguration config, Guid? company = null) =>
        GovernmentLivePolicy.DecideLiveWrite(config, TestScopes.For(TestScopes.Tenant, company, "sandbox"), GovernmentBodies.BankAnb, AllOpen);

    /// <summary>
    /// A committed intent made the only way there is (the recorder) for one company. The recorder refuses an intent with no company
    /// (a payroll submission names one), so a tenant-wide receipt, which the transport must still handle, is built directly.
    /// </summary>
    private static async Task<GovernmentExchangeIntentReceipt> IntentAsync(Guid? companyId)
    {
        var tenant = TestScopes.Tenant;
        if (companyId is not { } company)
            return new GovernmentExchangeIntentReceipt(Guid.NewGuid(), tenant, GovernmentBodies.BankAnb, "sandbox", Guid.NewGuid().ToString("N"),
                new string('a', 64));
        var (recorder, db) = GovernmentExchangeRecorderTests.Create();
        await using var _ = db;
        var recorded = await recorder.BeginAsync(new GovernmentExchangeAttempt(tenant, company, GovernmentBodies.BankAnb, "payroll.submit",
            GovernmentExchangeChannels.Api, Guid.NewGuid().ToString("N"), GovernmentExchangeStates.SubmitIntended,
            new RequestContext("127.0.0.1", "test", Guid.NewGuid(), tenant), Environment: "sandbox", SubjectType: "PaymentBatch",
            SubjectId: Guid.NewGuid().ToString(), ArtifactSha256: new string('a', 64), AccountFingerprint: "acct-fp-synthetic",
            BatchReference: "B-" + Guid.NewGuid().ToString("N")[..8]), default);
        return recorded.Receipt!;
    }

    private static UnsealedGovernmentCredential Credential(Guid? companyId) =>
        new(TestScopes.Tenant, GovernmentBodies.BankAnb, "sandbox", GovernmentCredentialKinds.OAuthClient, companyId, 1, "synthetic-secret",
            "synthetic-client", "bank-sandbox.example.test", "auth.bank-sandbox.example.test");

    // ── P2-A: one send per receipt, spent at the transport ─────────────────────────────────────────────

    [Fact]
    public async Task AReceipt_IsSpentByTheTransport_ASecondWriteWithItIsRefusedBeforeAnySocket()
    {
        var handler = new CountingHandler();
        var config = Settings();
        var http = new GovernmentHttp(new SingleHandlerFactory(handler), config);
        var intent = await IntentAsync(null);

        using (await http.ExchangeAsync(Write(config), "sandbox", HttpMethod.Post, "/payroll/v2", new StringContent("{}"), null, default, intent)) { }
        var again = async () => { using var _ = await http.ExchangeAsync(Write(config), "sandbox", HttpMethod.Post, "/payroll/v2", new StringContent("{}"), null, default, intent); };

        var refused = (await again.Should().ThrowAsync<GovernmentTransportRefusedException>()).Which;
        refused.Code.Should().Be(GovernmentHttp.IntentAlreadyUsedCode);
        refused.RequestSent.Should().BeFalse();
        handler.Calls.Should().Be(1, "a receipt opens one socket, whoever holds it and however many transports there are");
        intent.IsSpent.Should().BeTrue();
    }

    [Fact]
    public async Task ManyConcurrentWrites_WithOneReceipt_OnSeparateTransports_SendExactlyOnce()
    {
        var handler = new CountingHandler();
        var config = Settings();
        var intent = await IntentAsync(null);

        var attempts = Enumerable.Range(0, 24).Select(async _ =>
        {
            // A transport per attempt: the single use lives on the receipt, not on any one channel or transport instance.
            var http = new GovernmentHttp(new SingleHandlerFactory(handler), config);
            try { using var __ = await http.ExchangeAsync(Write(config), "sandbox", HttpMethod.Post, "/payroll/v2", new StringContent("{}"), null, default, intent); return true; }
            catch (GovernmentTransportRefusedException ex) when (ex.Code == GovernmentHttp.IntentAlreadyUsedCode) { return false; }
        }).ToList();
        var sent = await Task.WhenAll(attempts);

        sent.Count(s => s).Should().Be(1);
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task AWriteRefusedBeforeTheSocket_DoesNotSpendTheReceipt()
    {
        var handler = new CountingHandler();
        var config = Settings();
        var http = new GovernmentHttp(new SingleHandlerFactory(handler), config);
        var intent = await IntentAsync(null);

        var badPath = async () => { using var _ = await http.ExchangeAsync(Write(config), "sandbox", HttpMethod.Post, "/../payroll", new StringContent("{}"), null, default, intent); };
        (await badPath.Should().ThrowAsync<GovernmentTransportRefusedException>()).Which.RequestSent.Should().BeFalse();
        intent.IsSpent.Should().BeFalse("nothing was sent, so the receipt is still the one committed intent's to use");

        using (await http.ExchangeAsync(Write(config), "sandbox", HttpMethod.Post, "/payroll/v2", new StringContent("{}"), null, default, intent)) { }
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task AReceipt_CarriesItsCompany_AndACompanyCredentialRefusesAnotherCompanysReceipt()
    {
        var handler = new CountingHandler();
        var config = Settings();
        var http = new GovernmentHttp(new SingleHandlerFactory(handler), config);
        var companyA = Guid.NewGuid();
        var companyB = Guid.NewGuid();
        var intentB = await IntentAsync(companyB);
        intentB.CompanyId.Should().Be(companyB);

        var crossed = async () => { using var _ = await http.ExchangeAsync(Write(config, companyA), Credential(companyA), HttpMethod.Post, "/payroll/v2", new StringContent("{}"), null, default, intentB); };

        var refused = (await crossed.Should().ThrowAsync<GovernmentTransportRefusedException>()).Which;
        refused.Code.Should().Be(GovernmentHttp.IntentCompanyMismatchCode);
        handler.Calls.Should().Be(0);
        intentB.IsSpent.Should().BeFalse();

        // The receipt's own company's credential sends it.
        using (await http.ExchangeAsync(Write(config, companyB), Credential(companyB), HttpMethod.Post, "/payroll/v2", new StringContent("{}"), null, default, intentB)) { }
        handler.Calls.Should().Be(1);
    }

    // ── P2-B: the token grant ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheOnlyPublicTokenGrant_IsBoundToACredential_AndTakesNoPath()
    {
        var grants = typeof(GovernmentHttp).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => m.Name == nameof(GovernmentHttp.GrantTokenAsync)).ToList();

        var grant = grants.Should().ContainSingle("a caller-chosen token path could POST anywhere under the host without an intent").Subject;
        grant.GetParameters().Select(p => p.ParameterType).Should().Contain(typeof(UnsealedGovernmentCredential));
        grant.GetParameters().Should().NotContain(p => p.ParameterType == typeof(string), "the path comes from the operator's configuration, not the caller");
    }

    [Fact]
    public async Task TheTokenGrant_PostsToTheOperatorsFixedTokenPath_OnTheTokenHost()
    {
        var handler = new CountingHandler();
        var config = Settings("/oauth2/token");
        var http = new GovernmentHttp(new SingleHandlerFactory(handler), config);

        using (await http.GrantTokenAsync(Write(config), Credential(null), new StringContent("grant_type=client_credentials"), null, default)) { }

        handler.Uris.Should().Equal(new Uri(TokenOrigin + "/oauth2/token"));
        http.ResolveTokenPath(GovernmentBodies.BankAnb, "sandbox").Should().Be("/oauth2/token");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("oauth/token")]
    [InlineData("//evil.example/token")]
    [InlineData("/oauth/../admin")]
    [InlineData("/oauth/token?grant=x")]
    [InlineData("/oauth/token#x")]
    [InlineData("/oauth\\token")]
    [InlineData("/oauth token")]
    public async Task AMissingOrUnsafeTokenPath_IsRefused_AndNothingIsSent(string? path)
    {
        var handler = new CountingHandler();
        var config = Settings(path);
        var http = new GovernmentHttp(new SingleHandlerFactory(handler), config);

        var grant = async () => { using var _ = await http.GrantTokenAsync(Write(config), Credential(null), new StringContent("x"), null, default); };

        var refused = (await grant.Should().ThrowAsync<GovernmentTransportRefusedException>()).Which;
        refused.Code.Should().Be(GovernmentHttp.HostRefusedCode);
        refused.RequestSent.Should().BeFalse();
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task TheTokenGrant_StillNeedsAWriteDecision()
    {
        var handler = new CountingHandler();
        var config = Settings();
        var http = new GovernmentHttp(new SingleHandlerFactory(handler), config);
        var read = GovernmentLivePolicy.Decide(TestScopes.For("sandbox"), GovernmentBodies.BankAnb, AllOpen);

        var grant = async () => { using var _ = await http.GrantTokenAsync(read, Credential(null), new StringContent("x"), null, default); };

        (await grant.Should().ThrowAsync<GovernmentTransportRefusedException>()).Which.Code.Should().Be(GovernmentHttp.WriteRefusedCode);
        handler.Calls.Should().Be(0);
    }

    // ── a refusal says whether the request left ───────────────────────────────────────────────────────

    [Fact]
    public async Task ARefusedRedirect_SaysTheRequestWasSent_AndCarriesTheStatus()
    {
        var handler = new CountingHandler(HttpStatusCode.TemporaryRedirect);
        var config = Settings();
        var http = new GovernmentHttp(new SingleHandlerFactory(handler), config);

        var post = async () => { using var _ = await http.ExchangeAsync(Write(config), "sandbox", HttpMethod.Post, "/payroll/v2", new StringContent("{}"), null, default, await IntentAsync(null)); };

        var refused = (await post.Should().ThrowAsync<GovernmentTransportRefusedException>()).Which;
        refused.Code.Should().Be(GovernmentHttp.RedirectRefusedCode);
        refused.RequestSent.Should().BeTrue("the body left once; the outcome is unknown, never a refusal before sending");
        refused.HttpStatus.Should().Be(307);
        handler.Calls.Should().Be(1);
    }
}
