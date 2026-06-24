using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Zayra.Api.Infrastructure.Documents;

namespace Zayra.Api.Tests;

// Unit tests for S3DocumentStorage using an in-memory IS3Primitives fake.
// No real S3/R2 bucket is required — all I/O stays in-process.

public class S3DocumentStorageTests
{
    // ── In-memory S3 fake (implements the thin IS3Primitives wrapper) ─────────

    private sealed class InMemoryS3 : IS3Primitives
    {
        private readonly Dictionary<string, (byte[] Data, string ContentType)> _store = new();

        public Task PutAsync(string bucket, string key, Stream content, string contentType, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            content.CopyTo(ms);
            _store[key] = (ms.ToArray(), contentType);
            return Task.CompletedTask;
        }

        public Task<byte[]> GetBytesAsync(string bucket, string key, CancellationToken ct)
        {
            if (!_store.TryGetValue(key, out var entry))
                throw new InvalidOperationException($"NoSuchKey: {key}");
            return Task.FromResult(entry.Data);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static S3DocumentStorage MakeStorage(IS3Primitives fake) =>
        new(fake, new StorageOptions { Bucket = "test-bucket" }, NullLogger<S3DocumentStorage>.Instance);

    private static IFormFile MakeFile(string name, byte[] content, string contentType = "image/png")
    {
        var ms = new MemoryStream(content);
        return new FormFile(ms, 0, content.Length, name, name) { Headers = new HeaderDictionary(), ContentType = contentType };
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Upload_StoresObject_KeyHasTenantPrefix()
    {
        var fake = new InMemoryS3();
        var storage = MakeStorage(fake);
        var tenantId = Guid.NewGuid();
        var file = MakeFile("logo.png", new byte[] { 1, 2, 3 });

        var result = await storage.SaveAsync(tenantId, file, default);

        Assert.StartsWith(tenantId.ToString("N") + "/", result.StorageUrl);
        Assert.Equal(string.Empty, result.AbsolutePath); // no local path for S3
        Assert.Equal("image/png", result.ContentType);
    }

    [Fact]
    public async Task GetBytesAsync_RetrievesUploadedObject()
    {
        var fake = new InMemoryS3();
        var storage = MakeStorage(fake);
        var tenantId = Guid.NewGuid();
        var original = new byte[] { 10, 20, 30, 40 };

        var doc = await storage.SaveAsync(tenantId, MakeFile("logo.png", original), default);
        var retrieved = await storage.GetBytesAsync(tenantId, doc.StorageUrl);

        Assert.Equal(original, retrieved);
    }

    [Fact]
    public async Task GetBytesAsync_CrossTenantKey_ThrowsInvalidOperation()
    {
        var fake = new InMemoryS3();
        var storage = MakeStorage(fake);
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var doc = await storage.SaveAsync(tenantA, MakeFile("logo.png", new byte[] { 1 }), default);

        // Tenant B tries to read Tenant A's storage key — must be denied.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.GetBytesAsync(tenantB, doc.StorageUrl));
    }

    [Fact]
    public async Task SaveAsync_EmptyFile_Throws()
    {
        var storage = MakeStorage(new InMemoryS3());
        var file = MakeFile("empty.png", Array.Empty<byte>());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.SaveAsync(Guid.NewGuid(), file, default));
    }

    [Fact]
    public async Task SaveAsync_FileTooLarge_Throws()
    {
        var storage = MakeStorage(new InMemoryS3());
        var big = new byte[11 * 1024 * 1024]; // 11 MB — over 10 MB limit
        var file = MakeFile("big.png", big);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.SaveAsync(Guid.NewGuid(), file, default));
    }

    [Fact]
    public void ResolvePath_ThrowsNotSupported()
    {
        var storage = MakeStorage(new InMemoryS3());
        Assert.Throws<NotSupportedException>(() => storage.ResolvePath("some/key"));
    }

    [Fact]
    public async Task Provider_SwitchBetweenLocalAndS3_HonouredByProvider()
    {
        // Local: SaveAsync returns a relative path under "storage/"
        // S3: SaveAsync returns a tenantId-prefixed key without "storage/"
        var opts = new StorageOptions { Provider = "s3", Bucket = "test" };
        Assert.Equal("s3", opts.Provider);

        var localOpts = new StorageOptions { Provider = "local" };
        Assert.Equal("local", localOpts.Provider);

        // S3 storage key uses tenant prefix, no "storage/" prefix
        var fake = new InMemoryS3();
        var s3Storage = MakeStorage(fake);
        var tenantId = Guid.NewGuid();
        var doc = await s3Storage.SaveAsync(tenantId, MakeFile("f.png", new byte[] { 1 }), default);
        Assert.DoesNotContain("storage/", doc.StorageUrl);
        Assert.StartsWith(tenantId.ToString("N") + "/", doc.StorageUrl);
    }
}
