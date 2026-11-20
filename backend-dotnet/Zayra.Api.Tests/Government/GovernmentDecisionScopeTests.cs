using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Infrastructure.Government.Credentials;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Government.GovernmentLiveGateTests;

namespace Zayra.Api.Tests.Government;

/// <summary>
/// E5: a decision, a credential and a receipt each name their tenant (and company), and the transport compares them at the point of
/// use. A decision made for tenant A cannot be presented with tenant B's credential or receipt; a company's credential is used only
/// by that company's decision; a tenant-wide decision never carries a company's credential. Every refusal is before the socket.
/// </summary>
public sealed class GovernmentDecisionScopeTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");
    private static readonly Guid CompanyA1 = Guid.Parse("a1a1a1a1-0000-0000-0000-0000000000a1");
    private static readonly Guid CompanyA2 = Guid.Parse("a2a2a2a2-0000-0000-0000-0000000000a2");
    private static readonly GovernmentLiveGates AllOpen = new(true, true, true, true);

    private static IConfiguration Settings() => Config(
        (GovernmentHttp.BaseUrlKey(GovernmentBodies.BankAnb, "sandbox"), "https://bank-sandbox.example.test"),
        (GovernmentHttp.TokenUrlKey(GovernmentBodies.BankAnb, "sandbox"), "https://auth.bank-sandbox.example.test"),
        (GovernmentHttp.TokenPathKey(GovernmentBodies.BankAnb, "sandbox"), "/oauth2/token"));

    private static GovernmentLiveDecision Write(IConfiguration config, Guid tenant, Guid? company) =>
        GovernmentLivePolicy.DecideLiveWrite(config, TestScopes.For(tenant, company, "sandbox"), GovernmentBodies.BankAnb, AllOpen);

    private static UnsealedGovernmentCredential Credential(Guid tenant, Guid? company) =>
        new(tenant, GovernmentBodies.BankAnb, "sandbox", GovernmentCredentialKinds.OAuthClient, company, 1, "synthetic-secret",
            "synthetic-client", "bank-sandbox.example.test", "auth.bank-sandbox.example.test");

    private static GovernmentExchangeIntentReceipt Receipt(Guid tenant, Guid? company) =>
        new(Guid.NewGuid(), tenant, GovernmentBodies.BankAnb, "sandbox", Guid.NewGuid().ToString("N"), new string('a', 64), null, company);

    private static async Task<GovernmentTransportRefusedException> RefusedAsync(Func<Task> send, CountingHandler handler)
    {
        var refused = (await send.Should().ThrowAsync<GovernmentTransportRefusedException>()).Which;
        refused.RequestSent.Should().BeFalse("a scope mismatch is refused before the socket");
        handler.Calls.Should().Be(0);
        return refused;
    }

    private static Task<HttpResponseMessage> PostAsync(GovernmentHttp http, GovernmentLiveDecision decision, UnsealedGovernmentCredential credential, GovernmentExchangeIntentReceipt intent) =>
        http.ExchangeAsync(decision, credential, HttpMethod.Post, "/payroll/v2", new StringContent("{}"), null, default, intent);

    [Fact]
    public async Task ADecisionForTenantA_PresentedWithTenantBsCredential_IsRefused_AndNothingIsSent()
    {
        var handler = new CountingHandler();
        var config = Settings();
        var http = new GovernmentHttp(new SingleHandlerFactory(handler), config);
        var decisionA = Write(config, TenantA, null);

        var send = () => PostAsync(http, decisionA, Credential(TenantB, null), Receipt(TenantA, null));

        (await RefusedAsync(send, handler)).Code.Should().Be(GovernmentHttp.ScopeMismatchCode);
        var grant = () => http.GrantTokenAsync(decisionA, Credential(TenantB, null), new StringContent("x"), null, default);
        (await RefusedAsync(grant, handler)).Code.Should().Be(GovernmentHttp.ScopeMismatchCode);
    }

    [Fact]
    public async Task ACompanyDecision_PresentedWithAnotherCompanysCredential_IsRefused()
    {
        var handler = new CountingHandler();
        var config = Settings();
        var http = new GovernmentHttp(new SingleHandlerFactory(handler), config);

        var send = () => PostAsync(http, Write(config, TenantA, CompanyA1), Credential(TenantA, CompanyA2), Receipt(TenantA, CompanyA2));

        (await RefusedAsync(send, handler)).Code.Should().Be(GovernmentHttp.ScopeMismatchCode);
    }

    [Fact]
    public async Task ATenantWideDecision_PresentedWithACompanysCredential_IsRefused()
    {
        var handler = new CountingHandler();
        var config = Settings();
        var http = new GovernmentHttp(new SingleHandlerFactory(handler), config);

        var send = () => PostAsync(http, Write(config, TenantA, null), Credential(TenantA, CompanyA1), Receipt(TenantA, CompanyA1));

        (await RefusedAsync(send, handler)).Code.Should().Be(GovernmentHttp.ScopeMismatchCode);
    }

    [Fact]
    public async Task ACompanysDecision_MayUseTheTenantWideCredential_AndItsOwnCompanyOne()
    {
        var handler = new CountingHandler();
        var config = Settings();
        var http = new GovernmentHttp(new SingleHandlerFactory(handler), config);
        var decision = Write(config, TenantA, CompanyA1);

        // The store's order: the company's own credential, else the tenant-wide one.
        using (await PostAsync(http, decision, Credential(TenantA, null), Receipt(TenantA, CompanyA1))) { }
        using (await PostAsync(http, decision, Credential(TenantA, CompanyA1), Receipt(TenantA, CompanyA1))) { }

        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task AReceiptOfAnotherTenant_IsRefused_WithOrWithoutACredential_AndIsNotSpent()
    {
        var handler = new CountingHandler();
        var config = Settings();
        var http = new GovernmentHttp(new SingleHandlerFactory(handler), config);
        var decisionA = Write(config, TenantA, null);
        var foreign = Receipt(TenantB, null);

        var withCredential = () => PostAsync(http, decisionA, Credential(TenantA, null), foreign);
        (await RefusedAsync(withCredential, handler)).Code.Should().Be(GovernmentHttp.ScopeMismatchCode);
        var withoutCredential = () => http.ExchangeAsync(decisionA, "sandbox", HttpMethod.Post, "/payroll/v2", new StringContent("{}"), null, default, foreign);
        (await RefusedAsync(withoutCredential, handler)).Code.Should().Be(GovernmentHttp.ScopeMismatchCode);

        foreign.IsSpent.Should().BeFalse("nothing was sent, so the foreign receipt was never used");
    }

    [Fact]
    public void RequireFor_AcceptsExactlyTheScopeTheDecisionWasMadeFor()
    {
        var decision = Write(Settings(), TenantA, CompanyA1);

        GovernmentHttp.RequireFor(decision, TenantA, CompanyA1, "sandbox");

        var other = new (Guid Tenant, Guid? Company, string Environment)[]
        {
            (TenantB, CompanyA1, "sandbox"), (TenantA, CompanyA2, "sandbox"), (TenantA, null, "sandbox"), (TenantA, CompanyA1, "production"),
        };
        foreach (var (tenant, company, environment) in other)
        {
            var act = () => GovernmentHttp.RequireFor(decision, tenant, company, environment);
            act.Should().Throw<GovernmentTransportRefusedException>().Which.Code.Should().Be(GovernmentHttp.ScopeMismatchCode);
        }
    }

    [Fact]
    public void ADecision_CarriesItsTenantCompanyAndEnvironment_AndAnOpenOneNeedsAnEnvironmentThePolicyKnows()
    {
        var decision = Write(Settings(), TenantA, CompanyA1);
        decision.TenantId.Should().Be(TenantA);
        decision.CompanyId.Should().Be(CompanyA1);
        decision.Environment.Should().Be("sandbox");

        var unknownEnvironment = () => GovernmentLivePolicy.Decide(TestScopes.For(TenantA, null, "staging"), GovernmentBodies.BankAnb, AllOpen);
        var noTenant = () => GovernmentLivePolicy.Decide(TestScopes.For(Guid.Empty, null, "sandbox"), GovernmentBodies.BankAnb, AllOpen);
        unknownEnvironment.Should().Throw<ArgumentOutOfRangeException>();
        noTenant.Should().Throw<ArgumentException>();
    }
}
