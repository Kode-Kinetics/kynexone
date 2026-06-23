namespace Zayra.Api.Infrastructure.Documents;

public record StoredDocument(string FileName, string ContentType, string StorageUrl, string AbsolutePath);

public interface IDocumentStorage
{
    Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken cancellationToken);
    string ResolvePath(string storageUrl);

    // Returns raw bytes for the stored object. Implementations enforce tenant ownership via the key prefix.
    Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default);

    // Local-only: resolves a storage URL to an absolute file path. S3DocumentStorage throws NotSupportedException.
}

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
        var path = ResolvePath(storageUrl);
        if (!File.Exists(path)) throw new FileNotFoundException($"Stored document not found: {storageUrl}");
        return File.ReadAllBytesAsync(path, ct);
    }

    public string ResolvePath(string storageUrl)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_environment.ContentRootPath, storageUrl));
        var storageRoot = Path.GetFullPath(Path.Combine(_environment.ContentRootPath, "storage"));
        if (!fullPath.StartsWith(storageRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid document path.");
        return fullPath;
    }
}
