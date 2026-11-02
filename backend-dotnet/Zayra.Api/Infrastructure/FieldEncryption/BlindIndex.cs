using System.Security.Cryptography;
using System.Text;
using Zayra.Api.Infrastructure.Employees;

namespace Zayra.Api.Infrastructure.FieldEncryption;

/// <summary>
/// Equality lookup over ciphertext. <c>HMAC-SHA256(HKDF(tenantRoot, "bidx:" + table.column), canonical(value))</c>:
/// deterministic per tenant and per column (so the same iqama in two tenants, or an iqama and a passport number that
/// happen to match, produce unrelated values), and useless without the tenant's root. The canonical form is the one the
/// duplicate detector already folds to (<see cref="IdentityValueNormalizer.Canonical"/>: Arabic digits to Western,
/// upper-case, separators stripped), so it is deliberately BROADER than the old plain-equality lookups.
/// </summary>
public static class BlindIndex
{
    public const int Length = 32;

    public static string Canonical(string? value) => IdentityValueNormalizer.Canonical(value);

    public static byte[]? Compute(byte[] tenantRoot, string table, string column, string? value)
    {
        var canonical = Canonical(value);
        if (canonical.Length == 0) return null;
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, tenantRoot, 32, salt: null,
            info: Encoding.UTF8.GetBytes($"bidx:{table}.{column}"));
        return HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonical));
    }
}
