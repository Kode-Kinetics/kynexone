using Microsoft.AspNetCore.Http;
using Zayra.Api.Infrastructure.Documents;

namespace Zayra.Api.Tests;

// No-op IDocumentStorage for controller unit/integration tests that don't exercise document upload.
internal sealed class NullDocumentStorage : IDocumentStorage
{
    public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken cancellationToken)
        => Task.FromResult(new StoredDocument(file.FileName, file.ContentType ?? "application/octet-stream", $"{tenantId:N}/{file.FileName}", string.Empty));

    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default)
        => Task.FromResult(Array.Empty<byte>());

    public string ResolvePath(string storageUrl) => storageUrl;
}
