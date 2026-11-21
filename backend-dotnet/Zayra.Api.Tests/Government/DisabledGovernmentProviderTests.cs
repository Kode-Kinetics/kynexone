using FluentAssertions;
using Zayra.Api.Infrastructure.Government;

namespace Zayra.Api.Tests.Government;

/// <summary>
/// A9: Muqeem, GOSI and Takamol have no published or contracted API, so each gets a provider in the Jawazat
/// <c>DisabledJawazatProvider</c> shape that says so plainly, offers no operation and is never live. No simulator: a simulator
/// written without a specification freezes a guess.
/// </summary>
public sealed class DisabledGovernmentProviderTests
{
    public static IEnumerable<object[]> Providers() =>
    [
        [new DisabledMuqeemProvider(), GovernmentBodies.Muqeem],
        [new DisabledGosiProvider(), GovernmentBodies.Gosi],
        [new DisabledTakamolProvider(), GovernmentBodies.Takamol],
    ];

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task EachProvider_IsUnavailable_OffersNothing_AndIsNeverLive(IGovernmentProvider provider, string body)
    {
        var capabilities = await provider.GetCapabilitiesAsync(CancellationToken.None);

        provider.Body.Should().Be(body);
        provider.IsLiveIntegration.Should().BeFalse();
        provider.AdapterName.Should().Be("disabled");
        capabilities.Body.Should().Be(body);
        capabilities.IsAvailable.Should().BeFalse();
        capabilities.SupportedOperations.Should().BeEmpty();
        capabilities.Provider.Should().Be("Disabled");
        capabilities.Message.Should().Contain("not connected");
        capabilities.Message.Should().NotContainAny("synced", "Synced", "submitted", "integrated", "certified", "partner");
    }
}
