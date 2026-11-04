using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Zayra.Api.Tests;

public sealed class OfferMailReadinessTests
{
    [Fact]
    public async Task CaptureWithoutRelayHasNoInventedSender()
    {
        await using var db = OfferSmtpOutcomeTests.Db(); var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Email:DeliveryMode"] = "capture" }).Build();
        var result = await OfferSmtpOutcomeTests.Service(db, config, new()).GetEffectiveSenderAsync(Guid.NewGuid());
        result.Mode.Should().Be("Capture"); result.Configured.Should().BeFalse(); result.FromAddress.Should().BeNull(); result.Source.Should().Be("None");
        result.ConfigurationFingerprint.Should().HaveLength(64);
    }

    [Fact]
    public async Task TenantSenderIsStrictlyItsOwn_NeverThePlatformRelay_AndNoCredentialFingerprint()
    {
        await using var db = OfferSmtpOutcomeTests.Db(); var config = OfferSmtpOutcomeTests.Config(); var tenant = Guid.NewGuid(); var service = OfferSmtpOutcomeTests.Service(db, config, new());
        // The platform relay is configured (environment), but a tenant without its own relay has no sender.
        var none = await service.GetEffectiveSenderAsync(tenant);
        none.Configured.Should().BeFalse(); none.FromAddress.Should().BeNull(); none.Source.Should().Be("None");
        OfferSmtpOutcomeTests.SeedTenantRelay(db, tenant, from: "tenant@example.invalid", host: "tenant.invalid");
        var own = await service.GetEffectiveSenderAsync(tenant);
        own.Source.Should().Be("Tenant"); own.FromAddress.Should().Be("tenant@example.invalid"); own.FromName.Should().Be("People");
        config["Smtp:Password"] = "secret-1"; config["Smtp:Username"] = "secret-account";
        (await service.GetEffectiveSenderAsync(tenant)).ConfigurationFingerprint.Should().Be(own.ConfigurationFingerprint);
        (await service.GetEffectiveSenderAsync(Guid.NewGuid())).Configured.Should().BeFalse();
    }
}
