using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Zayra.Api.Infrastructure.FieldEncryption;

namespace Zayra.Api.Tests.FieldEncryption;

/// <summary>The recovery copy is attacker-reachable input at restore time: its declared parameters are fixed and authenticated.</summary>
public class RecoveryCopyHardeningTests
{
    private const string Pass = "correct horse battery staple 42";
    private static readonly Guid T = Guid.NewGuid();
    private static RecoveryKeyEntry[] Keys() => [new(1, RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32))];

    private static string Edit(string blob, Action<JsonObject> change)
    {
        var node = JsonNode.Parse(blob)!.AsObject(); change(node); return node.ToJsonString();
    }

    [Fact]
    public void RoundTrips_AndTheFormatIsV2()
    {
        var keys = Keys();
        var blob = RecoveryCopy.SealWithPassphrase(T, keys, Pass);
        blob.Should().Contain("recovery-v2");
        RecoveryCopy.OpenWithPassphrase(T, blob, Pass).Single().Dek.Should().Equal(keys[0].Dek);
    }

    [Theory]
    [InlineData(1_000)]
    [InlineData(599_999)]
    [InlineData(600_001)]
    [InlineData(int.MaxValue)]
    public void AnyDeclaredIterationCountOtherThanTheFixedOne_IsRefused_BeforeAnyKeyDerivation(int iterations)
    {
        var blob = Edit(RecoveryCopy.SealWithPassphrase(T, Keys(), Pass), n => n["Iterations"] = iterations);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ((Action)(() => RecoveryCopy.OpenWithPassphrase(T, blob, Pass))).Should().Throw<CryptographicException>().WithMessage("*key-derivation cost*");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "a huge declared cost must not be computed (CPU DoS)");
    }

    [Fact]
    public void MissingIterations_SaltOfTheWrongSize_AndTamperedSalt_AreRefused()
    {
        var sealedBlob = RecoveryCopy.SealWithPassphrase(T, Keys(), Pass);
        ((Action)(() => RecoveryCopy.OpenWithPassphrase(T, Edit(sealedBlob, n => n.Remove("Iterations")), Pass))).Should().Throw<CryptographicException>();
        ((Action)(() => RecoveryCopy.OpenWithPassphrase(T, Edit(sealedBlob, n => n["Salt"] = Convert.ToBase64String(new byte[4])), Pass))).Should().Throw<CryptographicException>();
        ((Action)(() => RecoveryCopy.OpenWithPassphrase(T, Edit(sealedBlob, n => n["Salt"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))), Pass))).Should().Throw<CryptographicException>();
        ((Action)(() => RecoveryCopy.OpenWithPassphrase(T, Edit(sealedBlob, n => n["Salt"] = "not base64 !!"), Pass))).Should().Throw<CryptographicException>();
    }

    [Fact]
    public void AnOversizedOrMalformedBlob_IsRefusedWithoutWork()
    {
        ((Action)(() => RecoveryCopy.OpenWithPassphrase(T, new string(' ', RecoveryCopy.MaxBlobChars + 1), Pass))).Should().Throw<CryptographicException>();
        ((Action)(() => RecoveryCopy.OpenWithPassphrase(T, "not json", Pass))).Should().Throw<CryptographicException>();
        ((Action)(() => RecoveryCopy.OpenWithPassphrase(T, Edit(RecoveryCopy.SealWithPassphrase(T, Keys(), Pass), n => n["Nonce"] = "AA=="), Pass))).Should().Throw<CryptographicException>();
    }

    [Fact]
    public void TheFormatIsAuthenticated_SoAPublicKeyCopyCannotBePassedOffAsAPassphraseOne()
    {
        using var rsa = RSA.Create(3072);
        var pk = RecoveryCopy.SealToPublicKey(T, Keys(), rsa.ExportSubjectPublicKeyInfoPem());
        pk.Should().Contain("rsa-oaep");
        ((Action)(() => RecoveryCopy.OpenWithPassphrase(T, pk, Pass))).Should().Throw<CryptographicException>();
        RecoveryCopy.OpenWithPrivateKey(T, pk, rsa.ExportPkcs8PrivateKeyPem()).Should().HaveCount(1);
        var relabelled = Edit(pk, n => n["Format"] = RecoveryCopy.FormatPassphrase);
        ((Action)(() => RecoveryCopy.OpenWithPrivateKey(T, relabelled, rsa.ExportPkcs8PrivateKeyPem()))).Should().Throw<CryptographicException>();
    }

    [Fact]
    public void TheFingerprint_IdentifiesTheSealedParts_NotTheWhitespace()
    {
        var blob = RecoveryCopy.SealWithPassphrase(T, Keys(), Pass);
        var pretty = JsonSerializer.Serialize(JsonNode.Parse(blob), new JsonSerializerOptions { WriteIndented = true });
        RecoveryCopy.Fingerprint(pretty).Should().Be(RecoveryCopy.Fingerprint(blob), "a client that re-saves the file must still match the audited export");
        RecoveryCopy.Fingerprint(RecoveryCopy.SealWithPassphrase(T, Keys(), Pass)).Should().NotBe(RecoveryCopy.Fingerprint(blob));
    }

    [Fact]
    public void Zero_WipesTheOpenedKeyMaterial()
    {
        var opened = RecoveryCopy.OpenWithPassphrase(T, RecoveryCopy.SealWithPassphrase(T, Keys(), Pass), Pass);
        opened.Single().Dek.Should().NotEqual(new byte[32]);
        RecoveryCopy.Zero(opened);
        opened.Single().Dek.Should().Equal(new byte[32]);
        opened.Single().BlindIndexRoot.Should().Equal(new byte[32]);
    }
}
