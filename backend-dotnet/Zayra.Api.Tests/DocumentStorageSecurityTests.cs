using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Localization;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class DocumentStorageSecurityTests
{
    [Fact]
    public async Task LocalStorage_GetBytesAsync_RejectsAnotherTenantsManagedKey()
    {
        using var environment = new TemporaryWebHostEnvironment();
        var storage = new LocalDocumentStorage(environment);
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var expected = new byte[] { 4, 8, 15, 16, 23, 42 };

        var stored = await storage.SaveAsync(tenantA, FormFile("evidence.pdf", expected), default);

        Assert.Equal(expected, await storage.GetBytesAsync(tenantA, stored.StorageUrl));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.GetBytesAsync(tenantB, stored.StorageUrl));
    }

    [Fact]
    public async Task LocalStorage_GetBytesAsync_RejectsTraversalIntoAnotherTenantDirectory()
    {
        using var environment = new TemporaryWebHostEnvironment();
        var storage = new LocalDocumentStorage(environment);
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var stored = await storage.SaveAsync(tenantA, FormFile("contract.pdf", new byte[] { 1 }), default);
        var fileName = Path.GetFileName(stored.StorageUrl);
        var traversalKey = $"storage/documents/{tenantB:N}/../{tenantA:N}/{fileName}";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.GetBytesAsync(tenantB, traversalKey));
    }

    [Fact]
    public async Task EmployeeDownload_UsesProviderNeutralBytes_WhenResolvePathIsUnsupported()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var storageKey = $"{tenantId:N}/documents/contract.pdf";
        var expected = new byte[] { 9, 8, 7, 6 };
        var storage = new ProviderNeutralStorage(tenantId, storageKey, expected);
        var document = new EmployeeDocument
        {
            TenantId = tenantId,
            DocumentType = "Contract",
            FileName = "contract.pdf",
            ContentType = "application/pdf",
            StorageUrl = storageKey,
        };
        db.EmployeeDocuments.Add(document);
        await db.SaveChangesAsync();
        var controller = EmployeeController(db, storage, tenantId, Guid.NewGuid());

        var result = await controller.DownloadDocument(document.Id, default);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal(expected, file.FileContents);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.Equal("contract.pdf", file.FileDownloadName);
        Assert.False(storage.ResolvePathCalled);
    }

    [Fact]
    public async Task AddDraftDocument_RejectsUncheckedClientStorageKey()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var draft = new EmployeeDraft { TenantId = tenantId, CreatedByUserId = actorId };
        db.EmployeeDrafts.Add(draft);
        await db.SaveChangesAsync();
        var otherTenant = Guid.NewGuid();
        var storage = new ProviderNeutralStorage(
            tenantId, $"{tenantId:N}/documents/owned.pdf", new byte[] { 1 });
        var controller = EmployeeController(db, storage, tenantId, actorId);

        var result = await controller.AddDraftDocument(draft.Id,
            new Zayra.Api.Controllers.EmployeeDocumentRequest("Contract", "stolen.pdf", "application/pdf",
                $"{otherTenant:N}/documents/stolen.pdf", false, null), default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(await db.EmployeeDocuments.ToListAsync());
    }

    [Fact]
    public async Task PayslipPreview_LoadsLogoThroughProviderNeutralStorage()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var storageKey = $"{tenantId:N}/documents/logo.png";
        var logo = new byte[] { 137, 80, 78, 71 };
        var storage = new ProviderNeutralStorage(tenantId, storageKey, logo);
        var letters = new CapturingLetterService();
        var branding = new PayslipBrandingConfig(LogoStorageUrl: storageKey);
        var layout = new PayslipLayoutConfig("en", new[]
        {
            new PayslipSectionConfig("earnings", true, 1, new[] { "basic_salary" }),
            new PayslipSectionConfig("deductions", true, 2, new[] { "gosi_annuities_ee", "gosi_saned_ee" }),
        });
        var template = new PayslipTemplate
        {
            TenantId = tenantId,
            Name = "Provider-neutral preview",
            BrandingJson = JsonSerializer.Serialize(branding),
            LayoutJson = JsonSerializer.Serialize(layout),
        };
        db.PayslipTemplates.Add(template);
        await db.SaveChangesAsync();
        var controller = new PayslipTemplatesController(db, storage, letters)
        {
            ControllerContext = ControllerContext(tenantId, Guid.NewGuid()),
        };

        var result = await controller.Preview(template.Id, default);

        Assert.IsType<FileContentResult>(result);
        Assert.Equal(logo, letters.LastData!.Branding!.LogoBytes);
        Assert.False(storage.ResolvePathCalled);
    }

    private static EmployeesController EmployeeController(
        ZayraDbContext db, IDocumentStorage storage, Guid tenantId, Guid actorId)
    {
        var controller = new EmployeesController(
            db,
            new Pbkdf2PasswordHasher(),
            new NoopAuditService(),
            storage,
            new NoopNotificationService(),
            new FakeHijriDateService(),
            new DataScopeService(db),
            new CapturingLetterService());
        controller.ControllerContext = ControllerContext(tenantId, actorId);
        return controller;
    }

    private static ControllerContext ControllerContext(Guid tenantId, Guid actorId) => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("tenant_id", tenantId.ToString()),
                new Claim(ClaimTypes.NameIdentifier, actorId.ToString()),
                new Claim(ClaimTypes.Role, "Admin"),
                new Claim("is_group_scope", "true"),
            }, "Test")),
        },
    };

    private static ZayraDbContext CreateDb() => new(
        new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static IFormFile FormFile(string fileName, byte[] contents)
    {
        var stream = new MemoryStream(contents);
        return new FormFile(stream, 0, contents.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/pdf",
        };
    }

    private sealed class ProviderNeutralStorage(Guid tenantId, string storageKey, byte[] contents) : IDocumentStorage
    {
        public bool ResolvePathCalled { get; private set; }

        public Task<StoredDocument> SaveAsync(Guid requestedTenantId, IFormFile file, CancellationToken ct) =>
            Task.FromResult(new StoredDocument(file.FileName, file.ContentType, storageKey, string.Empty));

        public Task<byte[]> GetBytesAsync(Guid requestedTenantId, string requestedKey, CancellationToken ct = default)
        {
            if (requestedTenantId != tenantId || requestedKey != storageKey)
                throw new InvalidOperationException("Cross-tenant storage access denied.");
            return Task.FromResult(contents);
        }

        public string ResolvePath(string storageUrl)
        {
            ResolvePathCalled = true;
            throw new NotSupportedException("This provider has no local filesystem path.");
        }
    }

    private sealed class TemporaryWebHostEnvironment : IWebHostEnvironment, IDisposable
    {
        public TemporaryWebHostEnvironment()
        {
            ContentRootPath = Directory.CreateTempSubdirectory("zayra-doc-storage-").FullName;
            ContentRootFileProvider = new PhysicalFileProvider(ContentRootPath);
            WebRootPath = ContentRootPath;
            WebRootFileProvider = ContentRootFileProvider;
        }

        public string ApplicationName { get; set; } = "Zayra.Api.Tests";
        public IFileProvider WebRootFileProvider { get; set; }
        public string WebRootPath { get; set; }
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }

        public void Dispose()
        {
            (ContentRootFileProvider as IDisposable)?.Dispose();
            Directory.Delete(ContentRootPath, recursive: true);
        }
    }

    private sealed class NoopAuditService : IAuditService
    {
        public Task WriteAsync(string action, string entityName, string? entityId, RequestContext context,
            string? metadata, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoopNotificationService : INotificationService
    {
        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName,
            string? entityId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName,
            Dictionary<string, string> variables, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeHijriDateService : IHijriDateService
    {
        public DateConversionDto FromGregorian(DateOnly date) =>
            new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
    }

    private sealed class CapturingLetterService : ILetterService
    {
        public PayslipData? LastData { get; private set; }

        public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken cancellationToken = default)
        {
            LastData = data;
            return Task.FromResult(new byte[] { (byte)'%', (byte)'P', (byte)'D', (byte)'F' });
        }

        public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken cancellationToken = default) =>
            Task.FromResult(Array.Empty<byte>());

        public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken cancellationToken = default) =>
            Task.FromResult(Array.Empty<byte>());

        public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken cancellationToken = default) =>
            Task.FromResult(Array.Empty<byte>());
    }
}
