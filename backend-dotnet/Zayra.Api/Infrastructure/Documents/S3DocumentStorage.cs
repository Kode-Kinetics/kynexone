using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;

namespace Zayra.Api.Infrastructure.Documents;

// Thin interface over S3 operations so tests can inject an in-memory fake
// without having to implement the entire 400-method IAmazonS3 interface.
internal interface IS3Primitives
{
    Task PutAsync(string bucket, string key, Stream content, string contentType, CancellationToken ct);
    Task<byte[]> GetBytesAsync(string bucket, string key, CancellationToken ct);
}

internal sealed class AwsS3Primitives(IAmazonS3 s3) : IS3Primitives
{
    public async Task PutAsync(string bucket, string key, Stream content, string contentType, CancellationToken ct)
    {
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = content,
            ContentType = contentType,
        }, ct);
    }

    public async Task<byte[]> GetBytesAsync(string bucket, string key, CancellationToken ct)
    {
        using var resp = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key }, ct);
        using var ms = new MemoryStream();
        await resp.ResponseStream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}

public sealed class S3DocumentStorage : IDocumentStorage
{
    private readonly IS3Primitives _s3;
    private readonly StorageOptions _opts;
    private readonly ILogger<S3DocumentStorage> _logger;

    // Production constructor (registered via DI — internal so internal IS3Primitives is not publicly exposed)
    internal S3DocumentStorage(IS3Primitives s3, StorageOptions opts, ILogger<S3DocumentStorage> logger)
    {
        _s3 = s3;
        _opts = opts;
        _logger = logger;
    }

    public async Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length <= 0) throw new InvalidOperationException("Document file is empty.");
        if (file.Length > 10 * 1024 * 1024) throw new InvalidOperationException("Document file exceeds the 10MB limit.");

        var ext = Path.GetExtension(file.FileName).TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(ext)) ext = "bin";
        var safeName = Path.GetFileNameWithoutExtension(file.FileName).Replace(' ', '_');
        var key = $"{tenantId:N}/documents/{Guid.NewGuid():N}_{safeName}.{ext}";

        using var stream = file.OpenReadStream();
        await _s3.PutAsync(_opts.Bucket, key, stream,
            string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
            cancellationToken);

        _logger.LogInformation("S3: stored {Key}", key);
        return new StoredDocument(file.FileName, file.ContentType ?? "application/octet-stream", key, string.Empty);
    }

    public async Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default)
    {
        AssertTenantOwnership(tenantId, storageUrl);
        return await _s3.GetBytesAsync(_opts.Bucket, storageUrl, ct);
    }

    public string ResolvePath(string storageUrl) =>
        throw new NotSupportedException("S3DocumentStorage does not resolve local paths. Use GetBytesAsync instead.");

    private static void AssertTenantOwnership(Guid tenantId, string storageUrl)
    {
        if (!storageUrl.StartsWith(tenantId.ToString("N") + "/", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Cross-tenant storage access denied: key does not belong to tenant '{tenantId}'.");
    }

    internal static IS3Primitives CreatePrimitives(StorageOptions opts)
    {
        var config = new AmazonS3Config
        {
            UseHttp = false,
            ForcePathStyle = !string.IsNullOrEmpty(opts.Endpoint),
        };
        if (!string.IsNullOrEmpty(opts.Endpoint))
            config.ServiceURL = opts.Endpoint;
        else if (!string.IsNullOrEmpty(opts.Region) && opts.Region != "auto")
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(opts.Region);
        else
            config.RegionEndpoint = Amazon.RegionEndpoint.USEast1;

        return new AwsS3Primitives(new AmazonS3Client(opts.AccessKey, opts.SecretKey, config));
    }
}
