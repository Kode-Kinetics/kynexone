using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;

namespace Zayra.Api.Infrastructure.FieldEncryption;

/// <summary>
/// Rollout state of field-level encryption. Order matters: a mode is "at least" the ones before it.
/// Off -> DualWrite (write ciphertext + blind index beside the plaintext) -> ReadEncrypted (read the ciphertext)
/// -> EncryptedOnly (plaintext scrubbed). See docs/runbooks/field-encryption.md.
/// </summary>
public enum FieldEncryptionMode { Off = 0, DualWrite = 1, ReadEncrypted = 2, EncryptedOnly = 3 }

/// <summary>Bound from <c>FieldEncryption:*</c> (env <c>FieldEncryption__Mode</c>, <c>FieldEncryption__Kek__1</c>).</summary>
public sealed class FieldEncryptionOptions
{
    public const string Section = "FieldEncryption";
    public const int KekBytes = 32;

    public FieldEncryptionMode Mode { get; init; } = FieldEncryptionMode.Off;
    /// <summary>KEK version -> 32 raw bytes. Several versions may be present so a rotation can still unwrap old rows.</summary>
    public IReadOnlyDictionary<int, byte[]> Keks { get; init; } = new Dictionary<int, byte[]>();
    public int ActiveKekVersion { get; init; }
    /// <summary>
    /// Opt-in, default false: encrypt NEW DataProtection key-ring entries with the platform KEK. Having a KEK never changes the
    /// ring by itself. Turning this on is one-way until every running image can read an encrypted ring (an image that predates
    /// <see cref="KekXmlDecryptor"/> forks the ring and locks out every MFA user), so leave it off until the release is final.
    /// Existing plaintext rows are only ever rewritten by the explicit one-off job, never at boot.
    /// </summary>
    public bool EncryptDataProtectionRing { get; init; }
    /// <summary>Delay between a client's revoke request and the destruction of the platform-wrapped keys (cancellable meanwhile).</summary>
    public TimeSpan RevokeCoolingOff { get; init; } = TimeSpan.FromHours(24);

    public bool Enabled => Mode != FieldEncryptionMode.Off;
    public bool WritesCiphertext => Mode >= FieldEncryptionMode.DualWrite;
    public bool PlaintextIsAuthoritative => Mode < FieldEncryptionMode.ReadEncrypted;

    /// <summary>
    /// Parses and validates. Throws (failed boot) on any inconsistency: an unknown mode, a KEK that is not 32
    /// base64 bytes, an enabled mode without a KEK, or a mode whose code path is not shipped yet.
    /// </summary>
    public static FieldEncryptionOptions Load(IConfiguration configuration, bool isProduction)
    {
        var section = configuration.GetSection(Section);
        var rawMode = section["Mode"];
        var mode = FieldEncryptionMode.Off;
        if (!string.IsNullOrWhiteSpace(rawMode)
            && !Enum.TryParse(rawMode.Trim(), ignoreCase: true, out mode))
            throw new InvalidOperationException(
                $"{Section}:Mode '{rawMode}' is not one of Off, DualWrite, ReadEncrypted, EncryptedOnly.");

        var keks = new Dictionary<int, byte[]>();
        foreach (var child in section.GetSection("Kek").GetChildren())
        {
            if (!int.TryParse(child.Key, out var version) || version < 1 || version > 250)
                throw new InvalidOperationException($"{Section}:Kek:{child.Key} - the key name must be a version number 1..250.");
            if (string.IsNullOrWhiteSpace(child.Value)) continue;
            byte[] bytes;
            try { bytes = Convert.FromBase64String(child.Value.Trim()); }
            catch (FormatException) { throw new InvalidOperationException($"{Section}:Kek:{version} is not valid base64."); }
            if (bytes.Length != KekBytes)
                throw new InvalidOperationException($"{Section}:Kek:{version} must decode to exactly {KekBytes} bytes.");
            keks[version] = bytes;
        }

        var active = 0;
        if (int.TryParse(section["ActiveKekVersion"], out var configured)) active = configured;
        else if (keks.Count > 0) active = keks.Keys.Max();
        if (keks.Count > 0 && !keks.ContainsKey(active))
            throw new InvalidOperationException($"{Section}:ActiveKekVersion {active} has no matching {Section}:Kek:{active}.");

        if (mode != FieldEncryptionMode.Off && keks.Count == 0)
            throw new InvalidOperationException(
                $"{Section}:Mode is {mode} but no {Section}:Kek:<n> is configured. Refusing to start: encrypting " +
                "with a missing key would silently store plaintext, and losing the KEK loses the data.");

        if (mode >= FieldEncryptionMode.ReadEncrypted)
            throw new InvalidOperationException(
                $"{Section}:Mode {mode} is not available in this build. Only Off and DualWrite ship in release 1; " +
                "ReadEncrypted and EncryptedOnly arrive with the reader and scrub releases.");

        var encryptRing = false;
        if (!string.IsNullOrWhiteSpace(section["EncryptDataProtectionRing"])
            && !bool.TryParse(section["EncryptDataProtectionRing"], out encryptRing))
            throw new InvalidOperationException($"{Section}:EncryptDataProtectionRing must be true or false.");
        if (encryptRing && keks.Count == 0)
            throw new InvalidOperationException($"{Section}:EncryptDataProtectionRing is true but no {Section}:Kek:<n> is configured.");

        var coolingOff = TimeSpan.FromHours(24);
        if (!string.IsNullOrWhiteSpace(section["RevokeCoolingOffHours"]))
        {
            if (!double.TryParse(section["RevokeCoolingOffHours"], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var hours) || hours < 0 || hours > 24 * 30)
                throw new InvalidOperationException($"{Section}:RevokeCoolingOffHours must be a number of hours between 0 and 720.");
            coolingOff = TimeSpan.FromHours(hours);
        }

        return new FieldEncryptionOptions
        {
            Mode = mode, Keks = keks, ActiveKekVersion = active,
            EncryptDataProtectionRing = encryptRing, RevokeCoolingOff = coolingOff,
        };
    }
}
