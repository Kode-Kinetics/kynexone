using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Api;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Channels;
using static Zayra.Api.Tests.Government.AnbPayrollApi.AnbApiTestKit;

namespace Zayra.Api.Tests.Government.AnbPayrollApi;

/// <summary>
/// ANB publishes no servers, no token URL and no security scheme. Every one of those is configuration,
/// empty by default, and an empty or unsafe value means the client refuses: it will not be built, and a
/// built client whose settings are later cleared will not call. No socket in either case.
/// </summary>
public class AnbPayrollApiConfigRefusalTests
{
    public static TheoryData<string> Settings => new()
    {
        nameof(IAnbEndpointConfig.Environment), nameof(IAnbEndpointConfig.BaseUrl), nameof(IAnbEndpointConfig.TokenUrl),
        nameof(IAnbEndpointConfig.GrantType), nameof(IAnbEndpointConfig.Scope), nameof(IAnbEndpointConfig.ClientAuthMethod), nameof(IAnbEndpointConfig.AllowedHostSuffixes),
    };

    private static AnbPayrollApiOptions Without(string setting, string? value = null)
    {
        var c = Config();
        typeof(AnbPayrollApiOptions).GetProperty(setting)!.SetValue(c, value);
        return c;
    }

    [Fact]
    public void TheDefaultOptions_AreAllEmpty_AndRefuse()
    {
        var empty = new AnbPayrollApiOptions();
        new[] { empty.Environment, empty.BaseUrl, empty.TokenUrl, empty.GrantType, empty.Scope, empty.ClientAuthMethod, empty.AllowedHostSuffixes }
            .Should().OnlyContain(v => string.IsNullOrEmpty(v));

        var act = () => Client(new StubAnbHandler(_ => throw new InvalidOperationException()), out _, empty);
        var ex = act.Should().Throw<BankChannelRefusedException>().Which;
        ex.Code.Should().Be(BankChannelCodes.ConfigMissing);
        ex.Settings.Should().HaveCount(7);
    }

    [Fact]
    public void AnEmptyConfigurationSection_BindsToRefusingOptions()
    {
        var options = new AnbPayrollApiOptions();
        new ConfigurationBuilder().AddInMemoryCollection().Build().GetSection(AnbPayrollApiOptions.SectionName).Bind(options);
        var act = () => Client(new StubAnbHandler(_ => throw new InvalidOperationException()), out _, options);
        act.Should().Throw<BankChannelRefusedException>().Which.Code.Should().Be(BankChannelCodes.ConfigMissing);
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public void EachEmptySetting_RefusesConstruction_ByName(string setting)
    {
        foreach (var blank in new[] { null, "", "   " })
        {
            var act = () => Client(new StubAnbHandler(_ => throw new InvalidOperationException()), out _, Without(setting, blank));
            var ex = act.Should().Throw<BankChannelRefusedException>().Which;
            ex.Code.Should().Be(BankChannelCodes.ConfigMissing);
            ex.Settings.Should().Equal(setting);
        }
    }

    [Theory]
    [InlineData(nameof(IAnbEndpointConfig.BaseUrl), "http://anb-sandbox.test/api")]
    [InlineData(nameof(IAnbEndpointConfig.TokenUrl), "http://anb-auth.test/token")]
    [InlineData(nameof(IAnbEndpointConfig.BaseUrl), "anb-sandbox.test/api")]
    [InlineData(nameof(IAnbEndpointConfig.BaseUrl), "https://anb-sandbox.test/api?x=1")]
    [InlineData(nameof(IAnbEndpointConfig.BaseUrl), "https://user:pw@anb-sandbox.test/api")]
    [InlineData(nameof(IAnbEndpointConfig.GrantType), "password")]
    [InlineData(nameof(IAnbEndpointConfig.GrantType), "authorization_code")]
    [InlineData(nameof(IAnbEndpointConfig.ClientAuthMethod), "private_key_jwt")]
    public void AnUnsafeOrUnsupportedSetting_RefusesConstruction(string setting, string value)
    {
        var act = () => Client(new StubAnbHandler(_ => throw new InvalidOperationException()), out _, Without(setting, value));
        var ex = act.Should().Throw<BankChannelRefusedException>().Which;
        ex.Code.Should().Be(BankChannelCodes.ConfigInvalid);
        ex.Settings.Should().Equal(setting);
        ex.Message.Should().NotContain(value, "a refusal names the setting, never its value");
    }

    [Fact]
    public async Task SettingsClearedAfterConstruction_RefuseEveryCall_WithNoSocket()
    {
        var config = Config();
        var handler = new StubAnbHandler(_ => throw new InvalidOperationException("no call"));
        var client = Client(handler, out _, config);
        config.TokenUrl = "";

        var outcome = await client.SubmitAsync(Submit(BuildBatch(2)), CancellationToken.None);
        outcome.Kind.Should().Be(BankSubmitOutcomeKind.Refused);
        outcome.Code.Should().Be(BankChannelCodes.ConfigMissing);
        outcome.RequestSent.Should().BeFalse();

        var poll = () => client.GetStatusAsync(TestScope, PayrollId, null, CancellationToken.None);
        (await poll.Should().ThrowAsync<BankChannelRefusedException>()).Which.Code.Should().Be(BankChannelCodes.ConfigMissing);
        var wps = () => client.DownloadWpsAsync(TestScope, PayrollId, null, CancellationToken.None);
        (await wps.Should().ThrowAsync<BankChannelRefusedException>()).Which.Code.Should().Be(BankChannelCodes.ConfigMissing);

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadsForAnotherEnvironment_AreRefused()
    {
        var handler = new StubAnbHandler(_ => throw new InvalidOperationException("no call"));
        var act = () => Client(handler, out _).GetStatusAsync(TestScope with { Environment = "production" }, PayrollId, null, CancellationToken.None);
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
