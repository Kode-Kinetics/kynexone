using Microsoft.AspNetCore.Http;
using Zayra.Api.Infrastructure.Documents;

namespace Zayra.Api.Tests;

/// <summary>A tenant-prefix-enforcing in-memory store, shaped like LocalDocumentStorage. Keeps the exact
/// bytes handed to it so tests can prove hashing and tamper detection end to end.</summary>
internal sealed class MemoryDocumentStorage : IDocumentStorage
{
    public Dictionary<string, byte[]> Objects { get; } = new();

    public async Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, cancellationToken);
        var key = $"storage/documents/{tenantId:N}/{Guid.NewGuid():N}_{file.FileName}";
        Objects[key] = ms.ToArray();
        return new StoredDocument(file.FileName, file.ContentType ?? string.Empty, key, string.Empty);
    }

    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default)
    {
        if (!storageUrl.StartsWith($"storage/documents/{tenantId:N}/", StringComparison.Ordinal))
            throw new InvalidOperationException("Cross-tenant storage access denied.");
        return Objects.TryGetValue(storageUrl, out var b) ? Task.FromResult(b) : throw new FileNotFoundException(storageUrl);
    }

    public string ResolvePath(string storageUrl) => storageUrl;
}
