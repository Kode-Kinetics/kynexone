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
}
