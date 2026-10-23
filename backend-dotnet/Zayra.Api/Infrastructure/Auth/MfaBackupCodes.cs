using System.Security.Cryptography;
using System.Text;

namespace Zayra.Api.Infrastructure.Auth;

/// <summary>
/// One-time backup codes for TENANT users' two-step sign-in (users.mfa_recovery_code_hashes).
///
/// <para><b>The code.</b> Ten codes per set, each 10 symbols of Crockford base32 (0-9 and A-Z without I, L, O, U),
/// shown as <c>XXXXX-XXXXX</c>: 50 bits each. Input is case-insensitive, ignores spaces and hyphens, and reads the
/// Crockford look-alikes O as 0 and I/L as 1. Anything else is malformed and matches nothing.</para>
///
/// <para><b>Why not the platform recovery-code hasher.</b> Platform codes carry 100 bits, so an unsalted SHA-256 is
/// out of reach of an offline guess. 50 bits is not: with a leaked column one fast hash per guess finds a code in
/// hours. So a set is stored as PBKDF2-HMAC-SHA256 with a random per-set salt: one derivation per sign-in attempt
/// (checked against every remaining hash in constant time), and an offline search costs years per account.</para>
///
/// <para><b>The stored form.</b> <c>v1$&lt;iterations&gt;$&lt;salt base64&gt;$&lt;hash base64&gt;,&lt;hash base64&gt;,...</c>;
/// a used code's hash is removed, and the column is NULL when no code is left (or two-step is off). The codes
/// themselves are never stored and are returned exactly once, by the call that generated them.</para>
/// </summary>
public static class MfaBackupCodes
{
    public const int CodeCount = 10;
    public const int CodeLength = 10;
    internal const int Iterations = 50_000;
    private const string Version = "v1";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public sealed record Set(IReadOnlyList<string> Codes, string Stored);

    /// <summary>A fresh set of <see cref="CodeCount"/> distinct codes and the value to store for it.</summary>
    public static Set Generate()
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var raw = new HashSet<string>(StringComparer.Ordinal);
        while (raw.Count < CodeCount)
        {
            var chars = new char[CodeLength];
            for (var i = 0; i < chars.Length; i++)
                chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            raw.Add(new string(chars));
        }
        var codes = raw.ToList();
        var stored = $"{Version}${Iterations}${Convert.ToBase64String(salt)}$"
            + string.Join(',', codes.Select(c => Convert.ToBase64String(Derive(c, salt, Iterations))));
        return new Set(codes.Select(c => $"{c[..5]}-{c[5..]}").ToList(), stored);
    }

    /// <summary>
    /// The canonical form of a presented code (10 Crockford symbols, upper case), or null when it is not one we could
    /// have issued. Spaces and hyphens are ignored, case does not matter, O reads as 0 and I/L as 1.
    /// </summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 64) return null;
        var sb = new StringBuilder(CodeLength);
        foreach (var raw in input)
        {
            if (raw is ' ' or '-' or '\t') continue;
            var c = char.ToUpperInvariant(raw) switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                var other => other,
            };
            if (Alphabet.IndexOf(c) < 0) return null;
            sb.Append(c);
        }
        return sb.Length == CodeLength ? sb.ToString() : null;
    }

    /// <summary>How many unused codes <paramref name="stored"/> holds (0 for null or an unreadable value).</summary>
    public static int Remaining(string? stored) => Parse(stored) is { } p ? p.Hashes.Count : 0;

    /// <summary>
    /// Checks <paramref name="presented"/> against <paramref name="stored"/>. On a match returns true and the value to
    /// store with that code removed (null when it was the last one). A malformed code, an empty set or no match
    /// returns false. Every remaining hash is compared, in constant time, whatever matches first.
    /// </summary>
    public static bool TryConsume(string? stored, string? presented, out string? remainingStored, out int remainingCount)
    {
        remainingStored = stored;
        remainingCount = Remaining(stored);
        var code = Normalize(presented);
        if (code is null || Parse(stored) is not { } p) return false;

        var derived = Derive(code, p.Salt, p.Iterations);
        var match = -1;
        for (var i = 0; i < p.Hashes.Count; i++)
        {
            byte[] candidate;
            try { candidate = Convert.FromBase64String(p.Hashes[i]); }
            catch (FormatException) { continue; }
            if (candidate.Length == derived.Length && CryptographicOperations.FixedTimeEquals(candidate, derived) && match < 0)
                match = i;
        }
        if (match < 0) return false;

        var left = p.Hashes.Where((_, i) => i != match).ToList();
        remainingCount = left.Count;
        remainingStored = left.Count == 0 ? null : $"{Version}${p.Iterations}${Convert.ToBase64String(p.Salt)}${string.Join(',', left)}";
        return true;
    }

    private sealed record Parsed(int Iterations, byte[] Salt, IReadOnlyList<string> Hashes);

    private static Parsed? Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Version || !int.TryParse(parts[1], out var iterations) || iterations < 1)
            return null;
        byte[] salt;
        try { salt = Convert.FromBase64String(parts[2]); }
        catch (FormatException) { return null; }
        var hashes = parts[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return hashes.Length == 0 ? null : new Parsed(iterations, salt, hashes);
    }

    private static byte[] Derive(string normalizedCode, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.ASCII.GetBytes(normalizedCode), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
}
