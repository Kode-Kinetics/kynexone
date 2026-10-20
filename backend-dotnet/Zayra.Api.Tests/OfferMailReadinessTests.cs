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
    public async Task PlatformFallbackAndTenantSenderUseSameResolverAndNoCredentialFingerprint()
    {
        await using var db = OfferSmtpOutcomeTests.Db(); var config = OfferSmtpOutcomeTests.Config(); var tenant = Guid.NewGuid(); var service = OfferSmtpOutcomeTests.Service(db, config, new());
        var platform = await service.GetEffectiveSenderAsync(tenant);
        platform.FromAddress.Should().Be("hr@example.invalid"); platform.Source.Should().StartWith("Platform");
        config["Smtp:Password"] = "secret-1"; config["Smtp:Username"] = "secret-account";
        (await service.GetEffectiveSenderAsync(tenant)).ConfigurationFingerprint.Should().Be(platform.ConfigurationFingerprint);
        foreach(var (key,value) in new Dictionary<string,string> { ["Smtp.Host"] = "tenant.invalid", ["Smtp.FromAddress"] = "tenant@example.invalid", ["Smtp.FromName"] = "Tenant HR" })
            db.SystemSettings.Add(new() { TenantId = tenant, Category = "Email", SettingKey = key, SettingValue = value });
        await db.SaveChangesAsync(); var own = await service.GetEffectiveSenderAsync(tenant);
        own.Source.Should().Be("Tenant"); own.FromAddress.Should().Be("tenant@example.invalid"); own.ConfigurationFingerprint.Should().NotBe(platform.ConfigurationFingerprint);
        (await service.GetEffectiveSenderAsync(Guid.NewGuid())).FromAddress.Should().Be("hr@example.invalid");
    }
}
