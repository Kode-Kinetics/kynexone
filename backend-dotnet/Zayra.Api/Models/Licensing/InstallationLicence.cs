namespace Zayra.Api.Models;

/// <summary>
/// Signed licence evidence for this installation (implementation prompt §4.2). The exact signed payload and its
/// signature are kept, so every load re-verifies them: editing a row by hand breaks the signature and the licence
/// stops counting. At most one row is <see cref="InstallationLicenceStatuses.Active"/>.
/// </summary>
public class InstallationLicence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string LicenceId { get; set; } = string.Empty;
    public string Kid { get; set; } = string.Empty;
    /// <summary>The signed payload exactly as received, base64url.</summary>
    public string Payload { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
    /// <summary>SHA-256 of the payload bytes, lower-case hex.</summary>
    public string Fingerprint { get; set; } = string.Empty;
    public string Licensee { get; set; } = string.Empty;
    public DateTime? ExpiresAtUtc { get; set; }
    public string Status { get; set; } = InstallationLicenceStatuses.Active;
    public DateTime ImportedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? ImportedByUserId { get; set; }
}

public static class InstallationLicenceStatuses
{
    public const string Active = "Active";
    public const string Superseded = "Superseded";
    public const string Rejected = "Rejected";
}
