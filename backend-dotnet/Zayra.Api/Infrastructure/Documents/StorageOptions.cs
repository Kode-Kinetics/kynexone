namespace Zayra.Api.Infrastructure.Documents;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";
    public string Provider { get; set; } = "local";        // "local" | "s3"
    public string Bucket { get; set; } = string.Empty;     // S3/R2 bucket name
    public string Endpoint { get; set; } = string.Empty;   // R2: https://<acct>.r2.cloudflarestorage.com; leave empty for AWS
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string Region { get; set; } = "auto";           // AWS region; "auto" = R2
    public int SignedUrlExpiryMinutes { get; set; } = 60;

    /// <summary>
    /// Where this deploy's storage is allowed to count as resident, per jurisdiction:
    /// <c>Storage:ResidencyAllowList:KSA:0 = me-central-1</c> or an endpoint host such as
    /// <c>s3.example-ksa.com</c>. EMPTY by default — no region is hard-coded, so nothing is resident until the
    /// operator lists the location of a bucket that really is in the jurisdiction (an owner spend decision).
    /// Read by <see cref="StorageResidency"/>.
    /// </summary>
    public Dictionary<string, string[]> ResidencyAllowList { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
