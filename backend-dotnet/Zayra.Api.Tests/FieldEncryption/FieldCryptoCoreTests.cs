using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Zayra.Api.Infrastructure.FieldEncryption;

namespace Zayra.Api.Tests.FieldEncryption;

public class FieldCryptoCoreTests
{
    private static byte[] Key() => RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void Envelope_RoundTrips_AndHasTheDocumentedShape()
    {
        var dek = Key();
        var env = FieldEnvelope.Seal(dek, 3, "SA0380000000608010167519", "employees.bank_iban");
        env[0].Should().Be(2, "new values are written in format 2");
        env[1].Should().Be(3);
        env.Length.Should().Be(2 + 12 + "SA0380000000608010167519".Length + 16);
        FieldEnvelope.IsWellFormed(env).Should().BeTrue();
        FieldEnvelope.Open(dek, env, "employees.bank_iban").Should().Be("SA0380000000608010167519");
    }

    [Fact]
    public void Envelope_IsRandomised_SameValueNeverSealsTwiceTheSame()
    {
        var dek = Key();
        FieldEnvelope.Seal(dek, 1, "x", "t.c").Should().NotEqual(FieldEnvelope.Seal(dek, 1, "x", "t.c"));
    }

    [Fact]
    public void Envelope_RefusesAnotherColumn_AWrongKey_AndAnyTamper()
    {
        var dek = Key();
        var env = FieldEnvelope.Seal(dek, 1, "2123456789", "employees.iqama_number");
        ((Action)(() => FieldEnvelope.Open(dek, env, "employees.passport_number"))).Should().Throw<CryptographicException>("AAD binds the column");
        ((Action)(() => FieldEnvelope.Open(Key(), env, "employees.iqama_number"))).Should().Throw<CryptographicException>();
        for (var i = 0; i < env.Length; i++)
        {
            var copy = env.ToArray(); copy[i] ^= 0x01;
            ((Action)(() => FieldEnvelope.Open(dek, copy, "employees.iqama_number"))).Should().Throw<CryptographicException>($"byte {i}");
        }
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 1, 0 })]
    public void Envelope_ShapeCheck_RejectsJunk(byte[] junk) => FieldEnvelope.IsWellFormed(junk).Should().BeFalse();

    [Fact]
    public void Envelope_ShapeCheck_RejectsWrongFormatByteAndZeroDekVersion()
    {
        var env = FieldEnvelope.Seal(Key(), 1, "abc", "t.c");
        var badFormat = env.ToArray(); badFormat[0] = 3;
        var zeroVer = env.ToArray(); zeroVer[1] = 0;
        FieldEnvelope.IsWellFormed(badFormat).Should().BeFalse();
        FieldEnvelope.IsWellFormed(zeroVer).Should().BeFalse();
    }

    [Fact]
    public void Format1_StillOpens_WithItsLegacyAad_ButNeverFormat2WithThatAad()
    {
        var dek = Key();
        var v1 = FieldEnvelope.Seal(dek, 1, "2123456789", "employees.iqama_number", FieldEnvelope.FormatV1);
        v1[0].Should().Be(1);
        FieldEnvelope.IsWellFormed(v1).Should().BeTrue();
        var ring = new TenantKeyRing(Guid.NewGuid(), 1, new Dictionary<int, byte[]> { [1] = dek }, Key());
        var iqama = EncryptedFieldCatalog.Find(typeof(Zayra.Api.Models.Employee), nameof(Zayra.Api.Models.Employee.IqamaNumber))!;
        FieldCipher.Decrypt(ring, "employees", iqama, "ignored-for-v1", v1).Should().Be("2123456789");
        var v2 = FieldCipher.Encrypt(ring, "employees", iqama, "", "2123456789")!;
        v2[0].Should().Be(2);
        ((Action)(() => FieldEnvelope.Open(dek, v2, "employees.iqama_number"))).Should().Throw<CryptographicException>("a v2 value does not open under the weaker v1 AAD");
    }

    [Fact]
    public void Aad_BindsTenantColumnAndRow_SoValuesCannotBeReplayedAcrossThem()
    {
        var dek = Key(); var tenantA = Guid.NewGuid(); var tenantB = Guid.NewGuid();
        var iqama = EncryptedFieldCatalog.Find(typeof(Zayra.Api.Models.Employee), nameof(Zayra.Api.Models.Employee.IqamaNumber))!;
        var passport = EncryptedFieldCatalog.Find(typeof(Zayra.Api.Models.Employee), nameof(Zayra.Api.Models.Employee.PassportNumber))!;
        var ringA = new TenantKeyRing(tenantA, 1, new Dictionary<int, byte[]> { [1] = dek }, Key());
        var ringBSameDek = new TenantKeyRing(tenantB, 1, new Dictionary<int, byte[]> { [1] = dek }, Key());
        var row1 = Guid.NewGuid().ToString("N"); var row2 = Guid.NewGuid().ToString("N");
        var env = FieldCipher.Encrypt(ringA, "employee_dependents", iqama, row1, "2123456789")!;
        FieldCipher.Decrypt(ringA, "employee_dependents", iqama, row1, env).Should().Be("2123456789");
        ((Action)(() => FieldCipher.Decrypt(ringA, "employee_dependents", iqama, row2, env))).Should().Throw<CryptographicException>("another row of the same tenant");
        ((Action)(() => FieldCipher.Decrypt(ringA, "employee_dependents", passport, row1, env))).Should().Throw<CryptographicException>("another column");
        ((Action)(() => FieldCipher.Decrypt(ringBSameDek, "employee_dependents", iqama, row1, env))).Should().Throw<CryptographicException>("another tenant, even under the same key");
    }

    [Fact]
    public void Kek_WrapsPerContext_AndVersionsRoundTrip()
    {
        var kit = new FeKit(kekVersions: [1, 2], active: 2);
        var material = Key();
        var wrapped = kit.Kek.Wrap(material, "ctx-a");
        kit.Kek.Unwrap(2, wrapped, "ctx-a").Should().Equal(material);
        ((Action)(() => kit.Kek.Unwrap(2, wrapped, "ctx-b"))).Should().Throw<CryptographicException>("context is authenticated");
        ((Action)(() => kit.Kek.Unwrap(1, wrapped, "ctx-a"))).Should().Throw<CryptographicException>("wrong KEK version holds a different key");
        ((Action)(() => kit.Kek.Unwrap(9, wrapped, "ctx-a"))).Should().Throw<KekUnavailableException>("a missing KEK is the system's state: 503, not a bug");
    }

    [Fact]
    public void Kek_WithoutAnyKey_RefusesToWrapRatherThanStoreUnprotected()
    {
        var p = new ConfigKeyEncryptionKeyProvider(new FieldEncryptionOptions());
        p.IsConfigured.Should().BeFalse();
        ((Action)(() => p.Wrap(Key(), "c"))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void BlindIndex_IsDeterministic_FoldsFormat_AndIsolatesTenantsAndColumns()
    {
        var rootA = Key(); var rootB = Key();
        var a1 = BlindIndex.Compute(rootA, "employees", "iqama_number", "2123456789");
        a1!.Length.Should().Be(BlindIndex.Length);
        a1.Should().Equal(BlindIndex.Compute(rootA, "employees", "iqama_number", " 2123456789 "));
        BlindIndex.Compute(rootA, "employees", "iqama_number", "٢١٢٣٤٥٦٧٨٩").Should().Equal(a1, "Arabic-Indic digits fold to the same value");
        BlindIndex.Compute(rootA, "employees", "emirates_id", "784-1985-1234567-1")
            .Should().Equal(BlindIndex.Compute(rootA, "employees", "emirates_id", "784198512345671"), "separators are stripped like the duplicate detector");
        BlindIndex.Compute(rootB, "employees", "iqama_number", "2123456789").Should().NotEqual(a1, "another tenant");
        BlindIndex.Compute(rootA, "employees", "passport_number", "2123456789").Should().NotEqual(a1, "another column");
        BlindIndex.Compute(rootA, "employees", "iqama_number", "2123456780").Should().NotEqual(a1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("--- ---")]
    public void BlindIndex_OfBlank_IsNull(string? blank) => BlindIndex.Compute(Key(), "t", "c", blank).Should().BeNull();

    private static IConfiguration Cfg(params (string, string?)[] kv) =>
        new ConfigurationBuilder().AddInMemoryCollection(kv.Select(x => new KeyValuePair<string, string?>(x.Item1, x.Item2))).Build();

    private static string B64() => Convert.ToBase64String(Key());

    [Fact]
    public void Options_DefaultToOff_WithNothingConfigured() =>
        FieldEncryptionOptions.Load(Cfg(), isProduction: true).Mode.Should().Be(FieldEncryptionMode.Off);

    [Fact]
    public void Options_DualWrite_NeedsAKek_ElseTheProcessRefusesToStart() =>
        ((Action)(() => FieldEncryptionOptions.Load(Cfg(("FieldEncryption:Mode", "DualWrite")), true)))
            .Should().Throw<InvalidOperationException>().WithMessage("*no FieldEncryption:Kek*");

    [Fact]
    public void Options_LoadsKeks_PicksTheHighestVersionAsActive()
    {
        var o = FieldEncryptionOptions.Load(Cfg(("FieldEncryption:Mode", "dualwrite"), ("FieldEncryption:Kek:1", B64()), ("FieldEncryption:Kek:2", B64())), true);
        o.Mode.Should().Be(FieldEncryptionMode.DualWrite);
        o.ActiveKekVersion.Should().Be(2);
        o.Keks.Keys.Should().BeEquivalentTo([1, 2]);
    }

    [Theory]
    [InlineData("not base64!!")]
    [InlineData("AAAA")]
    public void Options_RejectsMalformedKek(string value) =>
        ((Action)(() => FieldEncryptionOptions.Load(Cfg(("FieldEncryption:Kek:1", value)), true))).Should().Throw<InvalidOperationException>();

    [Fact]
    public void Options_RejectsUnknownMode_AndModesNotShippedYet()
    {
        ((Action)(() => FieldEncryptionOptions.Load(Cfg(("FieldEncryption:Mode", "Sometimes")), true))).Should().Throw<InvalidOperationException>();
        ((Action)(() => FieldEncryptionOptions.Load(Cfg(("FieldEncryption:Mode", "ReadEncrypted"), ("FieldEncryption:Kek:1", B64())), true)))
            .Should().Throw<InvalidOperationException>().WithMessage("*not available in this build*");
        ((Action)(() => FieldEncryptionOptions.Load(Cfg(("FieldEncryption:Mode", "EncryptedOnly"), ("FieldEncryption:Kek:1", B64())), true)))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Options_ActiveVersionMustExist() =>
        ((Action)(() => FieldEncryptionOptions.Load(Cfg(("FieldEncryption:Kek:1", B64()), ("FieldEncryption:ActiveKekVersion", "5")), true)))
            .Should().Throw<InvalidOperationException>();
}
