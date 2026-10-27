namespace Zayra.Api.Application.Esign;

public sealed record AckEvidence(Guid TenantId, int EmployeeId, Guid PolicyId, string DocumentSha256, string TypedName, DateTime SignedAtUtc,
    string IpAddress, string? UserAgent, Guid SignerUserId, Guid? WitnessUserId, string ConsentTextSha256, string Method);

public static class SignatureChain
{
    public static DateTime ToMicroseconds(DateTime utc) => throw new NotImplementedException();
    public static string Canonical(AckEvidence e) => throw new NotImplementedException();
    public static string ComputeHash(string prevHash, AckEvidence e) => throw new NotImplementedException();
    public static bool IsRecordValid(string prevHash, string recordHash, AckEvidence e) => throw new NotImplementedException();
    public static string Sha256Hex(ReadOnlySpan<byte> bytes) => throw new NotImplementedException();
}
