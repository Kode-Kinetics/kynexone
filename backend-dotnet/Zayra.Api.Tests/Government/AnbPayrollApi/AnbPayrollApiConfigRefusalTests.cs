using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Api;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Channels;
using static Zayra.Api.Tests.Government.AnbPayrollApi.AnbApiTestKit;

namespace Zayra.Api.Tests.Government.AnbPayrollApi;

/// <summary>
/// ANB publishes no servers, no token URL and no security scheme. Every one of those is operator configuration in ONE tree,
/// <c>Government:Banks:Anb</c>, empty by default: base and token URLs and the token path per environment (GovernmentHttp's
/// allow-list) and the OAuth scope and client authentication. An empty or unsafe value means the client refuses: it will not be
/// built without its OAuth settings, and a call whose environment has no allow-listed URL will not be made. No socket either way.
/// </summary>
public class AnbPayrollApiConfigRefusalTests
{
    public static TheoryData<string> OAuthSettings => new() { AnbPayrollApiClient.ScopeSetting, AnbPayrollApiClient.ClientAuthMethodSetting };

    private static IConfigurationRoot Without(string key, string? value = null)
    {
        var c = Config();
        c[key] = value;
        return c;
    }

    private static Func<AnbPayrollApiClient> Build(IConfiguration config) =>
        () => Client(new StubAnbHandler(_ => throw new InvalidOperationException("no call")), out _, config);

    [Fact]
    public void AnEmptyConfiguration_RefusesConstruction_NamingBothOAuthSettings()
    {
        var empty = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var ex = Build(empty).Should().Throw<BankChannelRefusedException>().Which;
        ex.Code.Should().Be(BankChannelCodes.ConfigMissing);
        ex.Settings.Should().BeEquivalentTo(AnbPayrollApiClient.ScopeSetting, AnbPayrollApiClient.ClientAuthMethodSetting);
    }

    [Fact]
    public void TheSettingsLiveInTheSharedBankTree_AndTheOldOwnTreeIsGone()
    {
        AnbPayrollApiClient.ScopeKey.Should().Be("Government:Banks:Anb:OAuth:Scope");
        AnbPayrollApiClient.ClientAuthMethodKey.Should().Be("Government:Banks:Anb:OAuth:ClientAuthMethod");
        GovernmentHttp.BaseUrlKey(GovernmentBodies.BankAnb, "sandbox").Should().Be("Government:Banks:Anb:BaseUrls:sandbox");
        GovernmentHttp.TokenUrlKey(GovernmentBodies.BankAnb, "sandbox").Should().Be("Government:Banks:Anb:TokenUrls:sandbox");
        GovernmentHttp.TokenPathKey(GovernmentBodies.BankAnb, "sandbox").Should().Be("Government:Banks:Anb:TokenPaths:sandbox");

        // A value under the retired tree is never read.
        var config = Config();
        config[AnbPayrollApiClient.ScopeKey] = null;
        config["Government:Anb:PayrollApi:Scope"] = "payroll";
        var ex = Build(config).Should().Throw<BankChannelRefusedException>().Which;
        ex.Settings.Should().Equal(AnbPayrollApiClient.ScopeSetting);
    }

    [Theory]
    [MemberData(nameof(OAuthSettings))]
    public void EachEmptyOAuthSetting_RefusesConstruction_ByName(string setting)
    {
        foreach (var blank in new[] { null, "", "   " })
        {
            var ex = Build(Without(GovernmentHttp.SettingKey(GovernmentBodies.BankAnb, setting), blank)).Should().Throw<BankChannelRefusedException>().Which;
            ex.Code.Should().Be(BankChannelCodes.ConfigMissing);
            ex.Settings.Should().Equal(setting);
        }
    }

    [Theory]
    [InlineData(AnbPayrollApiClient.ClientAuthMethodSetting, "private_key_jwt")]
    [InlineData(AnbPayrollApiClient.ClientAuthMethodSetting, "client_secret_jwt")]
    [InlineData(AnbPayrollApiClient.ScopeSetting, "payroll\r\nX-Injected: 1")]
    public void AnUnsafeOrUnsupportedOAuthSetting_RefusesConstruction(string setting, string value)
    {
        var ex = Build(Without(GovernmentHttp.SettingKey(GovernmentBodies.BankAnb, setting), value)).Should().Throw<BankChannelRefusedException>().Which;
        ex.Code.Should().Be(BankChannelCodes.ConfigInvalid);
        ex.Settings.Should().Equal(setting);
        ex.Message.Should().NotContain(value, "a refusal names the setting, never its value");
    }

    public static TheoryData<string> UrlKeys => new()
    {
        GovernmentHttp.BaseUrlKey(GovernmentBodies.BankAnb, "sandbox"),
        GovernmentHttp.TokenPathKey(GovernmentBodies.BankAnb, "sandbox"),
    };

    [Theory]
    [MemberData(nameof(UrlKeys))]
    public async Task AnEnvironmentWithoutItsAllowListedUrlOrTokenPath_RefusesEveryCall_WithNoSocket(string key)
    {
        var handler = new StubAnbHandler(_ => throw new InvalidOperationException("no call"));
        var client = Client(handler, out _, Without(key));

        var outcome = await client.SubmitAsync(Submit(BuildBatch(2)), CancellationToken.None);
        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Refused);
        outcome.Code.Should().Be(BankChannelCodes.HostNotAllowed);
        outcome.RequestSent.Should().BeFalse();

        var poll = () => client.GetStatusAsync(TestScope, PayrollId, null, CancellationToken.None);
        (await poll.Should().ThrowAsync<BankChannelRefusedException>()).Which.Code.Should().Be(BankChannelCodes.HostNotAllowed);
        var wps = () => client.DownloadWpsAsync(TestScope, PayrollId, null, CancellationToken.None);
        (await wps.Should().ThrowAsync<BankChannelRefusedException>()).Which.Code.Should().Be(BankChannelCodes.HostNotAllowed);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SettingsClearedAfterConstruction_RefuseEveryCall_WithNoSocket()
    {
        var config = Config();
        var handler = new StubAnbHandler(_ => throw new InvalidOperationException("no call"));
        var client = Client(handler, out _, config);
        config[AnbPayrollApiClient.ScopeKey] = "";

        var outcome = await client.SubmitAsync(Submit(BuildBatch(2)), CancellationToken.None);
        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Refused);
        outcome.Code.Should().Be(BankChannelCodes.ConfigMissing);
        outcome.RequestSent.Should().BeFalse();

        var poll = () => client.GetStatusAsync(TestScope, PayrollId, null, CancellationToken.None);
        (await poll.Should().ThrowAsync<BankChannelRefusedException>()).Which.Code.Should().Be(BankChannelCodes.ConfigMissing);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadsForAnEnvironmentThatIsOnlyALabel_AreRefused()
    {
        var handler = new StubAnbHandler(_ => throw new InvalidOperationException("no call"));
        var act = () => Client(handler, out _).GetStatusAsync(TestScope with { Environment = "uat" }, PayrollId, null, CancellationToken.None);
        (await act.Should().ThrowAsync<BankChannelRefusedException>()).Which.Code.Should().Be(BankChannelCodes.EnvironmentMismatch);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadsWithoutACredential_AreRefused()
    {
        var handler = new StubAnbHandler(_ => throw new InvalidOperationException("no call"));
        var act = () => Client(handler, out _, credentials: new FixedCredentials(none: true))
            .GetStatusAsync(TestScope, PayrollId, null, CancellationToken.None);
        (await act.Should().ThrowAsync<BankChannelRefusedException>()).Which.Code.Should().Be(BankChannelCodes.CredentialMissing);
        handler.Requests.Should().BeEmpty();
    }
}
