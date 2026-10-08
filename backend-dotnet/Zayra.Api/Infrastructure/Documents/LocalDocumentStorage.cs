namespace Zayra.Api.Infrastructure.Documents;

public record StoredDocument(string FileName, string ContentType, string StorageUrl, string AbsolutePath);

public interface IDocumentStorage
{
    Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken cancellationToken);
    string ResolvePath(string storageUrl);

    // Returns raw bytes for the stored object. Implementations enforce tenant ownership via the key prefix.
    Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default);

    // Local-only: resolves a storage URL to an absolute file path. S3DocumentStorage throws NotSupportedException.

    /// <summary>
    /// Best-effort removal of an object this process stored but whose database row did not commit, so a
    /// failed write leaves no orphan. Tenant ownership is enforced exactly as for reads. Returns false
    /// when nothing was removed; never throws for a missing object. Stores that cannot delete keep the
    /// default (no-op).
    /// </summary>
    Task<bool> TryDeleteAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(false);

    /// <summary>
    /// The storage key a tenant-relative object name will have, so a database row can record its key BEFORE the
    /// upload (selfie evidence: insert Pending, upload, then activate). Stores that cannot place an object at a chosen
    /// key keep the default and refuse.
    /// </summary>
    string TenantKey(Guid tenantId, string relativeName) =>
        throw new NotSupportedException($"{GetType().Name} cannot store an object at a chosen key.");

    /// <summary>Stores <paramref name="content"/> at exactly <paramref name="storageKey"/> (from <see cref="TenantKey"/>).</summary>
    Task PutAtAsync(Guid tenantId, string storageKey, byte[] content, string contentType, CancellationToken ct = default) =>
        throw new NotSupportedException($"{GetType().Name} cannot store an object at a chosen key.");

    /// <summary>
    /// STRICT delete, for anything the product promised to delete (selfie retention, consent withdrawal, tenant
    /// erasure). Returns only when the object is confirmed gone — every stored version of it, on a versioned bucket —
    /// and treats an object that is already absent as success. Throws on anything else (a 403, a network error, an
    /// object that is still there afterwards), so the caller keeps its record and retries. Unlike
    /// <see cref="TryDeleteAsync"/> it never reports failure as a value. A store that cannot confirm a deletion keeps
    /// this default, which throws: nothing is ever marked deleted on the strength of a delete nobody confirmed.
    /// </summary>
    Task DeleteStrictAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) =>
        throw new NotSupportedException($"{GetType().Name} cannot confirm a deletion, so nothing may be marked deleted through it.");
}

/// <summary>A strict delete ran but the object (or one of its versions) is still in storage.</summary>
public sealed class DocumentDeletionNotConfirmedException(string message) : IOException(message);

public class LocalDocumentStorage : IDocumentStorage
{
    private readonly IWebHostEnvironment _environment;

    public LocalDocumentStorage(IWebHostEnvironment environment)
    {
        _environment = environment;
    }

    public async Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length <= 0) throw new InvalidOperationException("Document file is empty.");
        if (file.Length > 10 * 1024 * 1024) throw new InvalidOperationException("Document file exceeds the 10MB limit.");
        var safeName = Path.GetFileName(file.FileName).Replace(' ', '_');
        var relative = Path.Combine("storage", "documents", tenantId.ToString("N"), $"{Guid.NewGuid():N}_{safeName}");
        var absolute = Path.Combine(_environment.ContentRootPath, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        await using var stream = File.Create(absolute);
        await file.CopyToAsync(stream, cancellationToken);
        return new StoredDocument(safeName, string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType, relative.Replace(Path.DirectorySeparatorChar, '/'), absolute);
    }

    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default)
    {
        var path = ResolveTenantPath(tenantId, storageUrl);
        if (!File.Exists(path)) throw new FileNotFoundException($"Stored document not found: {storageUrl}");
        return File.ReadAllBytesAsync(path, ct);
    }

    public Task<bool> TryDeleteAsync(Guid tenantId, string storageUrl, CancellationToken ct = default)
    {
        var path = ResolveTenantPath(tenantId, storageUrl);
        if (!File.Exists(path)) return Task.FromResult(false);
        File.Delete(path);
        return Task.FromResult(true);
    }

    public string TenantKey(Guid tenantId, string relativeName) =>
        $"storage/documents/{tenantId:N}/{SafeRelative(relativeName)}";

    public async Task PutAtAsync(Guid tenantId, string storageKey, byte[] content, string contentType, CancellationToken ct = default)
    {
        var path = ResolveTenantPath(tenantId, storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content, ct);
    }

    public Task DeleteStrictAsync(Guid tenantId, string storageUrl, CancellationToken ct = default)
    {
        var path = ResolveTenantPath(tenantId, storageUrl);
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path)) throw new DocumentDeletionNotConfirmedException($"Stored document still present after delete: {storageUrl}");
        return Task.CompletedTask;
    }

    internal static string SafeRelative(string relativeName)
    {
        if (string.IsNullOrWhiteSpace(relativeName) || relativeName.Contains("..", StringComparison.Ordinal)
            || relativeName.StartsWith('/') || relativeName.Contains('\\'))
            throw new InvalidOperationException("Invalid object name.");
        return relativeName;
    }

    public string ResolvePath(string storageUrl)
    {
        if (string.IsNullOrWhiteSpace(storageUrl) || Path.IsPathRooted(storageUrl))
            throw new InvalidOperationException("Invalid document path.");

        var fullPath = Path.GetFullPath(Path.Combine(_environment.ContentRootPath, storageUrl));
        var storageRoot = Path.GetFullPath(Path.Combine(_environment.ContentRootPath, "storage"));
        if (!IsChildPath(storageRoot, fullPath)) throw new InvalidOperationException("Invalid document path.");
        return fullPath;
    }

    private string ResolveTenantPath(Guid tenantId, string storageUrl)
    {
        var fullPath = ResolvePath(storageUrl);
        var tenantRoot = Path.GetFullPath(Path.Combine(
            _environment.ContentRootPath, "storage", "documents", tenantId.ToString("N")));

        if (!IsChildPath(tenantRoot, fullPath))
            throw new InvalidOperationException(
                $"Cross-tenant storage access denied: key does not belong to tenant '{tenantId}'.");

        return fullPath;
    }

    private static bool IsChildPath(string root, string candidate)
    {
        var rootedPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootedPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
