using System.Security.Cryptography;
using Zayra.Api.Application.Auth;

namespace Zayra.Api.Infrastructure.Auth;

/// <summary>
/// PBKDF2-HMAC-SHA256 password hashing.
///
/// <para><b>Versioned format.</b> Every stored hash is <c>PBKDF2$&lt;iterations&gt;$&lt;salt&gt;$&lt;key&gt;</c>.
/// The iteration count travels WITH the hash, so raising the work factor never invalidates an
/// existing credential: <see cref="Verify"/> uses the count written in the hash, and
/// <see cref="NeedsRehash"/> tells the login paths to re-hash at today's count once the plaintext
/// has just been proven. Hashes written before 2026-10 carry 100,000; new ones carry
/// <see cref="CurrentIterations"/> (OWASP Password Storage Cheat Sheet, PBKDF2-HMAC-SHA256: 600,000).</para>
/// </summary>
public class Pbkdf2PasswordHasher : IPasswordHasher
{
    /// <summary>Work factor for every hash written from now on.</summary>
    public const int CurrentIterations = 600_000;

    /// <summary>
    /// Upper bound accepted from a STORED hash. The count is read from the database, so without a cap
    /// a single tampered or corrupt row could make one login burn minutes of CPU.
    /// </summary>
    internal const int MaxAcceptedIterations = 10_000_000;

    private const string Scheme = "PBKDF2";
    private const int SaltSize = 16;
    private const int KeySize = 32;

    private readonly int _iterations;

    private static readonly Lazy<string> Dummy = new(
        () => new Pbkdf2PasswordHasher().Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))));

    /// <summary>
    /// A valid hash of a random, never-disclosed password at the current work factor. Login paths
    /// verify against it when there is no real credential to check (unknown email, inactive account),
    /// so a miss costs the same CPU and time as a hit and response timing does not reveal which
    /// emails exist.
    /// </summary>
    public static string DummyHash => Dummy.Value;

    public Pbkdf2PasswordHasher() : this(DefaultIterationsOverride ?? CurrentIterations) { }

    /// <summary>
    /// TEST ASSEMBLY ONLY (internal, reached through InternalsVisibleTo by a module initializer in
    /// Zayra.Api.Tests). Thousands of tests hash fixture passwords; at 600k iterations that CPU load
    /// starved the suite's timing-sensitive Postgres tests. Production never assigns it —
    /// PasswordHashUpgradeTests.ProductionNeverLowersTheWorkFactor scans the API source to keep it so —
    /// and the work-factor tests construct hashers with <see cref="CurrentIterations"/> explicitly.
    /// </summary>
    internal static int? DefaultIterationsOverride { get; set; }

    /// <summary>
    /// Explicit work factor. Production registers the parameterless constructor; this exists so the
    /// rehash path can be exercised with a deliberately weaker "legacy" hasher in tests.
    /// </summary>
    public Pbkdf2PasswordHasher(int iterations)
    {
        if (iterations < 1 || iterations > MaxAcceptedIterations)
            throw new ArgumentOutOfRangeException(nameof(iterations));
        _iterations = iterations;
    }

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, _iterations, HashAlgorithmName.SHA256, KeySize);
        return $"{Scheme}${_iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string passwordHash)
    {
        if (!TryParse(passwordHash, out var iterations, out var salt, out var expected)) return false;
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>
    /// True for a well-formed hash written with fewer iterations than this hasher uses. A malformed
    /// value is NOT reported: it cannot have verified, so there is nothing to upgrade.
    /// </summary>
    public bool NeedsRehash(string passwordHash)
        => TryParse(passwordHash, out var iterations, out _, out _) && iterations < _iterations;

    private static bool TryParse(string? passwordHash, out int iterations, out byte[] salt, out byte[] key)
    {
        iterations = 0;
        salt = key = Array.Empty<byte>();
        if (string.IsNullOrEmpty(passwordHash)) return false;
        var parts = passwordHash.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme
            || !int.TryParse(parts[1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out iterations)
            || iterations < 1 || iterations > MaxAcceptedIterations)
            return false;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            key = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }
        return salt.Length > 0 && key.Length > 0;
    }
}
