using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace Zayra.Api.Infrastructure.Auth;

/// <summary>
/// RFC 6238 TOTP implementation (HMAC-SHA1, 30-second period, 6 digits, ±1 window).
/// Secrets are encrypted at rest using IDataProtector before storage.
/// </summary>
public class TotpService
{
    private const int StepSeconds = 30;
    private const int Digits = 6;
    private const string ProtectorPurpose = "Zayra.Mfa.TotpSecret.v1";

    private readonly IDataProtector _protector;

    public TotpService(IDataProtectionProvider dpProvider)
    {
        _protector = dpProvider.CreateProtector(ProtectorPurpose);
    }

    // ── Secret lifecycle ──────────────────────────────────────────────────────

    public string GenerateBase32Secret()
    {
        Span<byte> key = stackalloc byte[20];
        RandomNumberGenerator.Fill(key);
        return ToBase32(key);
    }

    public string EncryptSecret(string base32Secret) => _protector.Protect(base32Secret);

    public string DecryptSecret(string encryptedSecret) => _protector.Unprotect(encryptedSecret);

    // ── Provisioning ──────────────────────────────────────────────────────────

    public string GenerateProvisioningUri(string label, string issuer, string base32Secret)
    {
        var encodedLabel  = Uri.EscapeDataString(label);
        var encodedIssuer = Uri.EscapeDataString(issuer);
        return $"otpauth://totp/{encodedLabel}?secret={base32Secret}&issuer={encodedIssuer}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
    }

    // ── Verification ─────────────────────────────────────────────────────────

    /// <summary>Verifies a 6-digit TOTP code using the plaintext base32 secret.
    /// Accepts one step before or after current step to accommodate clock skew.</summary>
    public bool Verify(string base32Secret, string userCode, int windowSteps = 1)
        => MatchStep(base32Secret, userCode, windowSteps) is not null;

    /// <summary>
    /// The RFC 6238 time-step the code matched (within ±<paramref name="windowSteps"/>), or null.
    /// Callers that hold a stored factor persist it and refuse any later code at or below it, so a
    /// code observed once (shoulder-surfed, phished, logged) cannot be replayed inside its window.
    /// </summary>
    public long? MatchStep(string base32Secret, string userCode, int windowSteps = 1)
    {
        if (string.IsNullOrWhiteSpace(base32Secret) || string.IsNullOrWhiteSpace(userCode))
            return null;
        if (!int.TryParse(userCode.Trim(), out var inputCode)) return null;
        byte[] keyBytes;
        try { keyBytes = FromBase32(base32Secret); }
        catch { return null; }
        var currentStep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / StepSeconds;
        for (var step = currentStep - windowSteps; step <= currentStep + windowSteps; step++)
            if (Compute(keyBytes, step) == inputCode) return step;
        return null;
    }

    /// <summary>A matched step is usable only if it is strictly after the last one accepted.</summary>
    public static bool IsFreshStep(long? matched, long? lastAccepted)
        => matched is { } step && (lastAccepted is null || step > lastAccepted.Value);

    // ── Internal RFC 6238 / RFC 4226 ─────────────────────────────────────────

    private static int Compute(byte[] keyBytes, long timeStep)
    {
        var msg = BitConverter.GetBytes(timeStep);
        if (BitConverter.IsLittleEndian) Array.Reverse(msg);
        using var hmac = new HMACSHA1(keyBytes);
        var hash = hmac.ComputeHash(msg);
        int offset = hash[^1] & 0x0F;
        int binCode = ((hash[offset]     & 0x7F) << 24)
                    | ((hash[offset + 1] & 0xFF) << 16)
                    | ((hash[offset + 2] & 0xFF) << 8)
                    |  (hash[offset + 3] & 0xFF);
        return binCode % 1_000_000;
    }

    // ── Base32 (RFC 4648, no padding required) ────────────────────────────────

    private static readonly char[] Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".ToCharArray();

    private static string ToBase32(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bitsLeft = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                sb.Append(Base32Alphabet[(buffer >> bitsLeft) & 0x1F]);
            }
        }
        if (bitsLeft > 0) sb.Append(Base32Alphabet[(buffer << (5 - bitsLeft)) & 0x1F]);
        return sb.ToString();
    }

    private static byte[] FromBase32(string base32)
    {
        var input = base32.TrimEnd('=').ToUpperInvariant();
        var output = new byte[input.Length * 5 / 8];
        int buffer = 0, bitsLeft = 0, index = 0;
        foreach (var c in input)
        {
            int val = Array.IndexOf(Base32Alphabet, c);
            if (val < 0) throw new FormatException($"Invalid base32 char '{c}'");
            buffer = (buffer << 5) | val;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                output[index++] = (byte)((buffer >> bitsLeft) & 0xFF);
            }
        }
        return output;
    }
}
