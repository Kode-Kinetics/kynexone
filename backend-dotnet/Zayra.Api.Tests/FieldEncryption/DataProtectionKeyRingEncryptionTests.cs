using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Infrastructure.FieldEncryption;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.FieldEncryption;

public class DataProtectionKeyRingEncryptionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fe-dp-" + Guid.NewGuid().ToString("N"));
    public DataProtectionKeyRingEncryptionTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private ServiceProvider Provider(FeKit? kit, bool encrypt)
    {
        var s = new ServiceCollection();
        if (kit is not null) s.AddSingleton<IKeyEncryptionKeyProvider>(kit.Kek);
        s.AddDataProtection().SetApplicationName("Zayra.Api").PersistKeysToFileSystem(new DirectoryInfo(_dir));
        if (encrypt)
            s.AddOptions<KeyManagementOptions>().Configure<IKeyEncryptionKeyProvider>((o, k) => o.XmlEncryptor = new KekXmlEncryptor(k));
        return s.BuildServiceProvider();
    }

    private string RingText() => string.Join("\n", Directory.GetFiles(_dir, "*.xml").Select(File.ReadAllText));

    [Fact]
    public void NewKeys_AreStoredEncrypted_AndStillWork_AcrossProcessRestarts()
    {
        var kit = new FeKit();
        string cipher;
        using (var p = Provider(kit, encrypt: true)) cipher = p.GetRequiredService<IDataProtectionProvider>().CreateProtector("purpose").Protect("totp-secret");

        var xml = RingText();
        xml.Should().Contain("kekEncrypted").And.Contain("encryptedSecret");
        xml.Should().NotContain("<value>", "the master key must not sit in the ring in the clear");

        using var restarted = Provider(kit, encrypt: true);
        restarted.GetRequiredService<IDataProtectionProvider>().CreateProtector("purpose").Unprotect(cipher).Should().Be("totp-secret");
    }

    [Fact]
    public void WithoutTheKek_TheRingIsUnreadable()
    {
        var kit = new FeKit();
        string cipher;
        using (var p = Provider(kit, true)) cipher = p.GetRequiredService<IDataProtectionProvider>().CreateProtector("x").Protect("s");
        using var other = Provider(new FeKit(), true);
        Action act = () => other.GetRequiredService<IDataProtectionProvider>().CreateProtector("x").Unprotect(cipher);
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void PlaintextRing_IsEncryptedInPlace_ThenOldCiphertextStillOpens()
    {
        string cipher;
        using (var plain = Provider(null, encrypt: false)) cipher = plain.GetRequiredService<IDataProtectionProvider>().CreateProtector("purpose").Protect("legacy-secret");
        RingText().Should().Contain("<value>").And.NotContain("kekEncrypted");

        var kit = new FeKit();
        var encryptor = new KekXmlEncryptor(kit.Kek);
        foreach (var file in Directory.GetFiles(_dir, "*.xml"))
        {
            var rewritten = DataProtectionKeyRingRewrite.EncryptInPlace(File.ReadAllText(file), encryptor);
            rewritten.Should().NotBeNull();
            File.WriteAllText(file, rewritten!);
            DataProtectionKeyRingRewrite.EncryptInPlace(rewritten!, encryptor).Should().BeNull("a second pass finds nothing left to encrypt");
        }
        RingText().Should().NotContain("<value>");

        using var after = Provider(kit, encrypt: true);
        after.GetRequiredService<IDataProtectionProvider>().CreateProtector("purpose").Unprotect(cipher).Should().Be("legacy-secret");
    }

    [Fact]
    public async Task DatabaseRows_AreRewrittenOnce_AndStayReadable()
    {
        var kit = new FeKit();
        var name = "dp-" + Guid.NewGuid().ToString("N");
        var options = new DbContextOptionsBuilder<Zayra.Api.Data.ZayraDbContext>().UseInMemoryDatabase(name).Options;
        string cipher;
        {
            var s = new ServiceCollection();
            s.AddDataProtection().SetApplicationName("Zayra.Api").PersistKeysToFileSystem(new DirectoryInfo(_dir));
            using var plain = s.BuildServiceProvider();
            cipher = plain.GetRequiredService<IDataProtectionProvider>().CreateProtector("p").Protect("v");
        }
        await using var db = new Zayra.Api.Data.ZayraDbContext(options);
        foreach (var f in Directory.GetFiles(_dir, "*.xml")) db.DataProtectionKeys.Add(new Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.DataProtectionKey { FriendlyName = Path.GetFileName(f), Xml = File.ReadAllText(f) });
        await db.SaveChangesAsync();

        var log = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        (await DataProtectionKeyRingRewrite.RunAsync(db, new KekXmlEncryptor(kit.Kek), log)).Should().BeGreaterThan(0);
        (await DataProtectionKeyRingRewrite.RunAsync(db, new KekXmlEncryptor(kit.Kek), log)).Should().Be(0, "idempotent");
        (await db.DataProtectionKeys.ToListAsync()).Should().OnlyContain(k => !k.Xml!.Contains("<value>"));
    }

    [Fact]
    public async Task Reprotect_MovesLongLivedSecretsToTheNewKey_AndLeavesUnreadableOnesAlone()
    {
        var s = new ServiceCollection();
        s.AddDataProtection().SetApplicationName("Zayra.Api").PersistKeysToFileSystem(new DirectoryInfo(_dir));
        using var sp = s.BuildServiceProvider();
        var dp = sp.GetRequiredService<IDataProtectionProvider>();
        var manager = sp.GetRequiredService<IKeyManager>();

        var mfa = dp.CreateProtector("Zayra.Mfa.TotpSecret.v1");
        var oldMfa = mfa.Protect("JBSWY3DPEHPK3PXP");
        var oldKeyId = DataProtectionSecretReprotector.KeyIdOf(oldMfa);

        var db = new Zayra.Api.Data.ZayraDbContext(new DbContextOptionsBuilder<Zayra.Api.Data.ZayraDbContext>().UseInMemoryDatabase("rp-" + Guid.NewGuid().ToString("N")).Options);
        var tenant = Guid.NewGuid();
        db.Users.Add(new Zayra.Api.Domain.Entities.User { TenantId = tenant, Email = "a@x.test", FullName = "A", PasswordHash = "x", MfaSecretEncrypted = oldMfa });
        db.Users.Add(new Zayra.Api.Domain.Entities.User { TenantId = tenant, Email = "b@x.test", FullName = "B", PasswordHash = "x", MfaSecretEncrypted = "not-data-protection-output-at-all-xxxxxxxxxx" });
        db.PlatformConfigEntries.Add(new PlatformConfigEntry { Key = "smtp_password", Value = "dp:" + dp.CreateProtector("KynexOne.Platform.Smtp.Password").Protect("relay-pass") });
        db.SystemSettings.Add(new SystemSetting { TenantId = tenant, Category = "Notifications", SettingKey = "Sms.ApiKey", SettingValue = dp.CreateProtector("Zayra.Notifications.ProviderSecret.v1").Protect("sms-key") });
        await db.SaveChangesAsync();

        var job = new DataProtectionSecretReprotector(db, dp, manager);
        job.CreateNewDefaultKey();
        var report = await job.RunAsync(default);
        report.Rewritten.Should().Be(3);
        report.Unreadable.Should().Be(1, "a value that is not DataProtection output is counted and left alone");

        var users = await db.Users.OrderBy(u => u.Email).ToListAsync();
        DataProtectionSecretReprotector.KeyIdOf(users[0].MfaSecretEncrypted!).Should().NotBe(oldKeyId);
        mfa.Unprotect(users[0].MfaSecretEncrypted!).Should().Be("JBSWY3DPEHPK3PXP");
        users[1].MfaSecretEncrypted.Should().Be("not-data-protection-output-at-all-xxxxxxxxxx");
        (await db.PlatformConfigEntries.SingleAsync()).Value.Should().StartWith("dp:");

        (await job.RunAsync(default)).Rewritten.Should().Be(0, "idempotent: everything is already on the current key");
        mfa.Unprotect(oldMfa).Should().Be("JBSWY3DPEHPK3PXP", "old keys stay readable until the separate revoke step");
    }
}
