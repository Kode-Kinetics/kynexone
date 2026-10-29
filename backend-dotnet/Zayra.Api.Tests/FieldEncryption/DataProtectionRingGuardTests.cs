using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Infrastructure.FieldEncryption;

namespace Zayra.Api.Tests.FieldEncryption;

internal sealed class FakeRing(params string[] rows) : IDataProtectionRingReader
{
    public List<string> Rows { get; } = [.. rows];
    public Task<List<string?>> ReadXmlAsync(CancellationToken ct) => Task.FromResult(Rows.Cast<string?>().ToList());
}

/// <summary>B1: the ring is never rewritten implicitly, rotation keeps old entries readable, and a missing/wrong KEK fails startup.</summary>
public class DataProtectionRingGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fe-ring-" + Guid.NewGuid().ToString("N"));
    public DataProtectionRingGuardTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private (string Cipher, string[] Rows) EncryptedRing(IKeyEncryptionKeyProvider kek)
    {
        var s = new ServiceCollection();
        s.AddSingleton(kek);
        s.AddDataProtection().SetApplicationName("Zayra.Api").PersistKeysToFileSystem(new DirectoryInfo(_dir));
        s.AddOptions<KeyManagementOptions>().Configure<IKeyEncryptionKeyProvider>((o, k) => o.XmlEncryptor = new KekXmlEncryptor(k));
        using var p = s.BuildServiceProvider();
        var cipher = p.GetRequiredService<IDataProtectionProvider>().CreateProtector("purpose").Protect("totp");
        return (cipher, Directory.GetFiles(_dir, "*.xml").Select(File.ReadAllText).ToArray());
    }

    private static ConfigKeyEncryptionKeyProvider Kek(Dictionary<int, byte[]> keks, int active) =>
        new(new FieldEncryptionOptions { Mode = FieldEncryptionMode.DualWrite, Keks = keks, ActiveKekVersion = active });

    private static FieldEncryptionStartupGuard Guard(FeKit kit, FakeRing ring, IKeyEncryptionKeyProvider kek, string env = "Production", bool flag = false) =>
        new(new FieldEncryptionOptions { Mode = FieldEncryptionMode.DualWrite, Keks = kit.Options.Keks, ActiveKekVersion = 1, EncryptDataProtectionRing = flag },
            kit.Store, ring, kek, new Env(env), NullLogger<FieldEncryptionStartupGuard>.Instance);

    [Fact]
    public async Task EncryptedRing_WithItsKek_StartsFine()
    {
        var kit = new FeKit(); var (_, rows) = EncryptedRing(kit.Kek);
        DataProtectionRingInspector.Inspect(rows, kit.Kek).Healthy.Should().BeTrue();
        await Guard(kit, new FakeRing(rows), kit.Kek).StartAsync(default);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task EncryptedRing_WithAWrongKek_FailsStartup_InEveryEnvironment(string env)
    {
        var kit = new FeKit(); var (_, rows) = EncryptedRing(kit.Kek);
        var wrong = new FeKit();
        // Same key id, different bytes: the unwrap fails the tag check.
        Func<Task> act = async () => await Guard(wrong, new FakeRing(rows), wrong.Kek, env).StartAsync(default);
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("cannot open").And.Contain("Refusing to start");
    }

    [Fact]
    public async Task EncryptedRing_WithTheKekVersionMissing_NamesTheVersion()
    {
        var kit = new FeKit(); var (_, rows) = EncryptedRing(kit.Kek);
        var onlyV2 = new FeKit(kekVersions: [2]);
        Func<Task> act = async () => await Guard(onlyV2, new FakeRing(rows), onlyV2.Kek).StartAsync(default);
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("not configured: 1").And.Contain("FieldEncryption__Kek__<n>");
    }

    [Fact]
    public async Task PlaintextRing_IsNeverTouchedOrRefused_EvenWithAKek()
    {
        var kit = new FeKit();
        var s = new ServiceCollection();
        s.AddDataProtection().SetApplicationName("Zayra.Api").PersistKeysToFileSystem(new DirectoryInfo(_dir));
        using (var p = s.BuildServiceProvider()) p.GetRequiredService<IDataProtectionProvider>().CreateProtector("x").Protect("y");
        var rows = Directory.GetFiles(_dir, "*.xml").Select(File.ReadAllText).ToArray();
        var report = DataProtectionRingInspector.Inspect(rows, kit.Kek);
        report.PlaintextRows.Should().BeGreaterThan(0);
        report.HasEncryptedEntries.Should().BeFalse();
        await Guard(kit, new FakeRing(rows), kit.Kek, flag: false).StartAsync(default);
        rows.Should().OnlyContain(r => r.Contains("<value>"), "nothing in the guard rewrites the ring");
    }

    [Fact]
    public async Task Rotation_KeepsOldEntriesReadable_AndRewrapMovesThemToTheNewKekWithoutTouchingCiphertext()
    {
        var kit = new FeKit(); var (cipher, rows) = EncryptedRing(kit.Kek);
        var keks = new Dictionary<int, byte[]> { [1] = kit.Options.Keks[1], [2] = RandomNumberGenerator.GetBytes(32) };
        var both = Kek(keks, active: 2);
        DataProtectionRingInspector.Inspect(rows, both).Healthy.Should().BeTrue("the v1 KEK is still configured, so v1 entries open");

        var rewrapped = rows.Select(r => DataProtectionKeyRingRewrite.RewrapInPlace(r, both) ?? r).ToArray();
        rewrapped.Should().NotEqual(rows);
        rewrapped.Should().OnlyContain(r => r.Contains("<kekVersion>2</kekVersion>") || r.Contains(">2</"));
        DataProtectionKeyRingRewrite.RewrapInPlace(rewrapped[0], both).Should().BeNull("a second pass finds nothing on an old KEK");
        System.Xml.Linq.XElement.Parse(rows[0]).Descendants(KekXmlEncryptor.Ns + "ct").Single().Value
            .Should().Be(System.Xml.Linq.XElement.Parse(rewrapped[0]).Descendants(KekXmlEncryptor.Ns + "ct").Single().Value, "only the wrapped key changes");

        // Now the old KEK can be dropped: the ring still opens, and the real DataProtection stack still decrypts old ciphertext.
        var onlyNew = Kek(new Dictionary<int, byte[]> { [2] = keks[2] }, 2);
        DataProtectionRingInspector.Inspect(rewrapped, onlyNew).Healthy.Should().BeTrue();
        var files = Directory.GetFiles(_dir, "*.xml");
        for (var i = 0; i < files.Length; i++) File.WriteAllText(files[i], rewrapped[i]);
        var s = new ServiceCollection();
        s.AddSingleton<IKeyEncryptionKeyProvider>(onlyNew);
        s.AddDataProtection().SetApplicationName("Zayra.Api").PersistKeysToFileSystem(new DirectoryInfo(_dir));
        using var p = s.BuildServiceProvider();
        p.GetRequiredService<IDataProtectionProvider>().CreateProtector("purpose").Unprotect(cipher).Should().Be("totp");
    }

    [Fact]
    public async Task RewrapWithoutTheOldKek_Throws_InsteadOfDroppingTheEntry()
    {
        var kit = new FeKit(); var (_, rows) = EncryptedRing(kit.Kek);
        var onlyV2 = new FeKit(kekVersions: [2]);
        Action act = () => DataProtectionKeyRingRewrite.RewrapInPlace(rows[0], onlyV2.Kek);
        act.Should().Throw<InvalidOperationException>().WithMessage("*not configured*");
        await Task.CompletedTask;
    }

    [Fact]
    public void Options_RingFlagDefaultsOff_NeedsAKek_AndCoolingOffIsBounded()
    {
        static IConfiguration Cfg(params (string, string?)[] kv) =>
            new ConfigurationBuilder().AddInMemoryCollection(kv.Select(x => new KeyValuePair<string, string?>(x.Item1, x.Item2))).Build();
        var kek = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var o = FieldEncryptionOptions.Load(Cfg(("FieldEncryption:Kek:1", kek)), true);
        o.EncryptDataProtectionRing.Should().BeFalse("having a KEK must not rewrite the ring");
        o.RevokeCoolingOff.Should().Be(TimeSpan.FromHours(24));
        FieldEncryptionOptions.Load(Cfg(("FieldEncryption:Kek:1", kek), ("FieldEncryption:EncryptDataProtectionRing", "true"), ("FieldEncryption:RevokeCoolingOffHours", "0")), true)
            .Should().Match<FieldEncryptionOptions>(x => x.EncryptDataProtectionRing && x.RevokeCoolingOff == TimeSpan.Zero);
        ((Action)(() => FieldEncryptionOptions.Load(Cfg(("FieldEncryption:EncryptDataProtectionRing", "true")), true))).Should().Throw<InvalidOperationException>();
        ((Action)(() => FieldEncryptionOptions.Load(Cfg(("FieldEncryption:RevokeCoolingOffHours", "-1")), true))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task RingJob_RefusesToEncrypt_UntilTheFlagIsExplicitlySet()
    {
        var kit = new FeKit();
        var db = new Zayra.Api.Data.ZayraDbContext(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Zayra.Api.Data.ZayraDbContext>().UseInMemoryDatabase(kit.DbName).Options);
        var job = new DataProtectionRingJob(db, kit.Kek, kit.Options, NullLogger<DataProtectionRingJob>.Instance);
        Func<Task> act = async () => await job.EncryptAsync(default);
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("EncryptDataProtectionRing");

        var on = new FieldEncryptionOptions { Mode = kit.Options.Mode, Keks = kit.Options.Keks, ActiveKekVersion = 1, EncryptDataProtectionRing = true };
        db.DataProtectionKeys.Add(new Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.DataProtectionKey
        { FriendlyName = "k", Xml = "<key id=\"" + Guid.NewGuid() + "\" version=\"1\"><descriptor><descriptor><encryption algorithm=\"AES_256_CBC\"/><masterKey p4:requiresEncryption=\"true\" xmlns:p4=\"http://schemas.asp.net/2015/03/dataProtection\"><value>AAAA</value></masterKey></descriptor></descriptor></key>" });
        await db.SaveChangesAsync();
        var result = await new DataProtectionRingJob(db, kit.Kek, on, NullLogger<DataProtectionRingJob>.Instance).EncryptAsync(default);
        result.RowsChanged.Should().Be(1);
        result.After.Healthy.Should().BeTrue();
        result.After.PlaintextRows.Should().Be(0);
    }
}
