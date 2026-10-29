using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.FieldEncryption;

namespace Zayra.Api.Tests.FieldEncryption;

public class FieldEncryptionStartupGuardTests
{
    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static FieldEncryptionStartupGuard Guard(FeKit kit, string env, FieldEncryptionMode mode) =>
        new(new FieldEncryptionOptions { Mode = mode, Keks = kit.Options.Keks, ActiveKekVersion = 1 }, kit.Store, new FakeRing(), kit.Kek, new Env(env),
            NullLogger<FieldEncryptionStartupGuard>.Instance);

    [Fact]
    public async Task Production_ModeOff_WithExistingKeys_RefusesToStart()
    {
        var kit = new FeKit(); await kit.Keys.GetRingAsync(Guid.NewGuid(), true, default);
        Func<Task> act = async () => await Guard(kit, "Production", FieldEncryptionMode.Off).StartAsync(default);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Mode is Off but tenant field-encryption keys exist*");
    }

    [Theory]
    [InlineData("Production", FieldEncryptionMode.Off, false)]      // never enabled: fine
    [InlineData("Production", FieldEncryptionMode.DualWrite, true)]  // enabled and on: fine
    [InlineData("Development", FieldEncryptionMode.Off, true)]       // dev may switch it off
    public async Task OtherCombinations_Start(string env, FieldEncryptionMode mode, bool keysExist)
    {
        var kit = new FeKit(); if (keysExist) await kit.Keys.GetRingAsync(Guid.NewGuid(), true, default);
        await Guard(kit, env, mode).StartAsync(default);
    }

    [Fact]
    public void CustodyEndpoints_AreGated_ToTheTenantAdminOrThePlatformOperator()
    {
        typeof(TenantFieldEncryptionController).GetCustomAttributes(typeof(HasPermissionAttribute), true).Cast<HasPermissionAttribute>()
            .Single().Permissions.Should().Equal("security.manage");
        typeof(PlatformFieldEncryptionController).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Should().Contain(a => a.Policy == "PlatformAdmin");
        foreach (var t in new[] { typeof(TenantFieldEncryptionController), typeof(PlatformFieldEncryptionController) })
            t.GetCustomAttributes(typeof(Zayra.Api.Infrastructure.Http.NoStoreAttribute), true).Should().NotBeEmpty("responses carry recovery material");
    }
}
