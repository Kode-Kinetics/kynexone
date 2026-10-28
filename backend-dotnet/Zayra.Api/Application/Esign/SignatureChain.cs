using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Zayra.Api.Application.Esign;

/// <summary>Everything the record hash covers. The order and the length prefixes in <see cref="SignatureChain.Canonical"/> are part of the evidence format: never reorder.</summary>
public sealed record AckEvidence(Guid TenantId, int EmployeeId, Guid PolicyId, string DocumentSha256, string TypedName, DateTime SignedAtUtc,
    string IpAddress, string? UserAgent, Guid SignerUserId, Guid? WitnessUserId, string ConsentTextSha256, string Method);

/// <summary>
/// Append-only hash chain over the acknowledgement records of one tenant:
/// <c>record_hash = SHA256(prev_hash || canonical(evidence))</c>. Changing any covered field of any row, or removing a row,
/// breaks the link that the following row holds.
/// </summary>
public static class SignatureChain
{
    /// <summary>PostgreSQL stores timestamps to the microsecond; hashing a finer value would never verify after a round trip.</summary>
    public static DateTime ToMicroseconds(DateTime utc) =>
        DateTime.SpecifyKind(new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc), DateTimeKind.Utc);

    public static string Canonical(AckEvidence e)
    {
        var sb = new StringBuilder();
        void Field(string? v) { var s = v ?? string.Empty; sb.Append(Encoding.UTF8.GetByteCount(s).ToString(CultureInfo.InvariantCulture)).Append(':').Append(s).Append('|'); }
        Field(e.TenantId.ToString("D")); Field(e.EmployeeId.ToString(CultureInfo.InvariantCulture)); Field(e.PolicyId.ToString("D"));
        Field(e.DocumentSha256.ToLowerInvariant()); Field(e.TypedName);
        Field(ToMicroseconds(e.SignedAtUtc).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture));
        Field(e.IpAddress); Field(e.UserAgent); Field(e.SignerUserId.ToString("D")); Field(e.WitnessUserId?.ToString("D"));
        Field(e.ConsentTextSha256.ToLowerInvariant()); Field(e.Method);
        return sb.ToString();
    }

    public static string ComputeHash(string prevHash, AckEvidence e) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prevHash.ToLowerInvariant() + Canonical(e)))).ToLowerInvariant();

    public static bool IsRecordValid(string prevHash, string recordHash, AckEvidence e) =>
        string.Equals(ComputeHash(prevHash, e), recordHash, StringComparison.OrdinalIgnoreCase);

    public static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
