using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using System.Net;

namespace Zayra.Api.Infrastructure.Documents;

// Thin interface over S3 operations so tests can inject an in-memory fake
// without having to implement the entire 400-method IAmazonS3 interface.
internal interface IS3Primitives
{
    Task PutAsync(string bucket, string key, Stream content, string contentType, CancellationToken ct);
    Task<byte[]> GetBytesAsync(string bucket, string key, CancellationToken ct);
    Task DeleteAsync(string bucket, string key, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Every stored version id of exactly <paramref name="key"/> (delete markers included), or NULL when the store
    /// does not support listing versions. B2 and S3 keep old versions on a versioned bucket, so a delete without a
    /// version id only hides the object behind a delete marker.
    /// </summary>
    Task<IReadOnlyList<string>?> ListVersionIdsAsync(string bucket, string key, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>?>(null);

    Task DeleteVersionAsync(string bucket, string key, string versionId, CancellationToken ct) =>
        throw new NotSupportedException("This S3 adapter cannot delete a specific version.");

    /// <summary>Whether the (current version of the) object exists. Used to verify a delete when versions cannot be listed.</summary>
    Task<bool> ExistsAsync(string bucket, string key, CancellationToken ct) =>
        throw new NotSupportedException("This S3 adapter cannot check whether an object exists.");
}

internal sealed class AwsS3Primitives(IAmazonS3 s3) : IS3Primitives
{
    public async Task<IReadOnlyList<string>?> ListVersionIdsAsync(string bucket, string key, CancellationToken ct)
    {
        var ids = new List<string>();
        string? keyMarker = null, versionMarker = null;
        try
        {
            while (true)
            {
                var page = await s3.ListVersionsAsync(new ListVersionsRequest
                {
                    BucketName = bucket, Prefix = key, KeyMarker = keyMarker, VersionIdMarker = versionMarker,
                }, ct);
                // Prefix matching also returns longer keys that start with this one: keep exact matches only.
                ids.AddRange((page.Versions ?? []).Where(v => string.Equals(v.Key, key, StringComparison.Ordinal))
                    .Select(v => string.IsNullOrEmpty(v.VersionId) ? "null" : v.VersionId));
                if (page.IsTruncated != true) break;
                keyMarker = page.NextKeyMarker;
                versionMarker = page.NextVersionIdMarker;
            }
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode is HttpStatusCode.NotImplemented or HttpStatusCode.MethodNotAllowed
                                           || string.Equals(ex.ErrorCode, "NotImplemented", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return ids;
    }

    public Task DeleteVersionAsync(string bucket, string key, string versionId, CancellationToken ct) =>
        s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, VersionId = versionId }, ct);

    public async Task<bool> ExistsAsync(string bucket, string key, CancellationToken ct)
    {
        try
        {
            await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = key }, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound
                                           || string.Equals(ex.ErrorCode, "NoSuchKey", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
    }

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

    public Task DeleteAsync(string bucket, string key, CancellationToken ct) =>
        s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key }, ct);

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
        try
        {
            return await _s3.GetBytesAsync(_opts.Bucket, storageUrl, ct);
        }
        catch (AmazonS3Exception ex) when (
            ex.StatusCode == HttpStatusCode.NotFound ||
            string.Equals(ex.ErrorCode, "NoSuchKey", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException($"Stored document not found: {storageUrl}", ex);
        }
    }

    public async Task<bool> TryDeleteAsync(Guid tenantId, string storageUrl, CancellationToken ct = default)
    {
        AssertTenantOwnership(tenantId, storageUrl);
        try
        {
            await _s3.DeleteAsync(_opts.Bucket, storageUrl, ct);
            _logger.LogInformation("S3: removed uncommitted {Key}", storageUrl);
            return true;
        }
        catch (AmazonS3Exception ex)
        {
            // Best effort: the caller is already handling a failed write and must surface THAT error.
            _logger.LogWarning(ex, "S3: could not remove uncommitted {Key}", storageUrl);
            return false;
        }
    }

    public string TenantKey(Guid tenantId, string relativeName) =>
        $"{tenantId:N}/{LocalDocumentStorage.SafeRelative(relativeName)}";

    public async Task PutAtAsync(Guid tenantId, string storageKey, byte[] content, string contentType, CancellationToken ct = default)
    {
        AssertTenantOwnership(tenantId, storageKey);
        using var stream = new MemoryStream(content, writable: false);
        await _s3.PutAsync(_opts.Bucket, storageKey, stream, contentType, ct);
        _logger.LogInformation("S3: stored {Key}", storageKey);
    }

    /// <summary>
    /// Deletes every version of the object and confirms it. On a store that can list versions: list, delete each
    /// version by id, list again and fail if anything remains. On one that cannot: delete, then check that nothing is
    /// there. A missing object is success; any other S3 error (a 403 included) propagates — never a false.
    /// </summary>
    public async Task DeleteStrictAsync(Guid tenantId, string storageUrl, CancellationToken ct = default)
    {
        AssertTenantOwnership(tenantId, storageUrl);
        var versions = await _s3.ListVersionIdsAsync(_opts.Bucket, storageUrl, ct);
        if (versions is not null)
        {
            foreach (var versionId in versions)
                await IgnoreNotFound(() => _s3.DeleteVersionAsync(_opts.Bucket, storageUrl, versionId, ct));
            var remaining = await _s3.ListVersionIdsAsync(_opts.Bucket, storageUrl, ct) ?? [];
            if (remaining.Count > 0)
                throw new DocumentDeletionNotConfirmedException(
                    $"S3: {remaining.Count} version(s) of {storageUrl} remain after deleting every listed version.");
        }
        else
        {
            await IgnoreNotFound(() => _s3.DeleteAsync(_opts.Bucket, storageUrl, ct));
            if (await _s3.ExistsAsync(_opts.Bucket, storageUrl, ct))
                throw new DocumentDeletionNotConfirmedException($"S3: {storageUrl} is still present after delete.");
        }
        _logger.LogInformation("S3: deleted every version of {Key}", storageUrl);
    }

    private static async Task IgnoreNotFound(Func<Task> delete)
    {
        try { await delete(); }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound
                                           || string.Equals(ex.ErrorCode, "NoSuchKey", StringComparison.OrdinalIgnoreCase)
                                           || string.Equals(ex.ErrorCode, "NoSuchVersion", StringComparison.OrdinalIgnoreCase)) { }
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
