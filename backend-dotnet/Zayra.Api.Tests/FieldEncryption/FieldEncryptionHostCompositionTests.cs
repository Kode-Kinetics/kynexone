using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Zayra.Api.Infrastructure.FieldEncryption;
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests.FieldEncryption;

/// <summary>
/// The real Program.cs composition, booted in-process (Development: scope validation and validate-on-build are on, so a
/// captive or missing dependency in the field-encryption wiring fails here rather than at the first request in production).
/// Tagged Integration: it boots the whole host, which the sandboxed local lane cannot do (it times out waiting for the entry point).
/// </summary>
[Trait("Category", "Integration")]
public class FieldEncryptionHostCompositionTests
{
    private static string Conn() => $"Data Source=file:fe-host-{Guid.NewGuid():N}?mode=memory&cache=shared";

    [Fact]
    public async Task WithAKekAndDualWrite_TheWholeGraphResolves_AndTheKeyRingIsEncrypted()
    {
        var kek = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await using var host = new AuthorizationPipelineHost(Conn(), hostSettings: new Dictionary<string, string?>
        {
            ["FieldEncryption:Mode"] = "DualWrite",
            ["FieldEncryption:Kek:1"] = kek,
            ["FieldEncryption:EncryptDataProtectionRing"] = "true",
        });
        var sp = host.Services;
        sp.GetRequiredService<FieldEncryptionOptions>().Mode.Should().Be(FieldEncryptionMode.DualWrite);
        sp.GetRequiredService<IKeyEncryptionKeyProvider>().IsConfigured.Should().BeTrue();
        sp.GetRequiredService<FieldKeyService>().Should().NotBeNull();
        sp.GetRequiredService<FieldEncryptionSaveInterceptor>().Should().NotBeNull();
        sp.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlEncryptor.Should().BeOfType<KekXmlEncryptor>();
        using var scope = sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<FieldEncryptionAdminService>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<FieldEncryptionBackfill>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<DataProtectionSecretReprotector>().Should().NotBeNull();
    }

    [Fact]
    public async Task AKekAloneNeverEncryptsTheRing_TheFlagIsExplicit()
    {
        await using var host = new AuthorizationPipelineHost(Conn(), hostSettings: new Dictionary<string, string?>
        {
            ["FieldEncryption:Kek:1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        });
        host.Services.GetRequiredService<FieldEncryptionOptions>().EncryptDataProtectionRing.Should().BeFalse();
        host.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlEncryptor.Should().BeNull();
    }

    [Fact]
    public async Task WithoutAKek_ModeIsOff_AndTheKeyRingIsLeftAsItWas()
    {
        await using var host = new AuthorizationPipelineHost(Conn());
        host.Services.GetRequiredService<FieldEncryptionOptions>().Mode.Should().Be(FieldEncryptionMode.Off);
        host.Services.GetRequiredService<IKeyEncryptionKeyProvider>().IsConfigured.Should().BeFalse();
        host.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlEncryptor.Should().BeNull();
    }

    [Fact]
    public void ADualWriteConfigurationWithoutAKek_RefusesToBoot()
    {
        Func<Task> act = async () =>
        {
            await using var host = new AuthorizationPipelineHost(Conn(), hostSettings: new Dictionary<string, string?> { ["FieldEncryption:Mode"] = "DualWrite" });
            _ = host.Services;
        };
        act.Should().ThrowAsync<Exception>().Result.Which.ToString().Should().Contain("no FieldEncryption:Kek");
    }

    [Fact]
    public async Task TheCustodyRoutes_AreMapped()
    {
        await using var host = new AuthorizationPipelineHost(Conn());
        var routes = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText).ToList();
        routes.Should().Contain(r => r!.StartsWith("api/security/field-encryption/revoke"));
        routes.Should().Contain(r => r!.StartsWith("api/platform/field-encryption/{tenantId:guid}/backfill"));
    }
}
