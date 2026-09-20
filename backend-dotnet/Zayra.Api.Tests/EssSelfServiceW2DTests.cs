using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// W2-D — the employee self-service endpoints the mobile app waits for.
///
/// What is pinned here, per the stream brief:
///   • uploads refuse files that are too big, of the wrong type, or whose bytes are not what they claim;
///   • nobody reaches another employee's documents, photo or payslip detail — those answer 404;
///   • a category opt-out really blocks the channel (and a mandatory category cannot be opted out);
///   • payslip detail lines add up to the header totals;
///   • push `data` carries routing codes and opaque ids only, never personal information.
/// InMemory EF + in-memory storage. No Postgres, no Docker, no network.
/// </summary>
public class EssSelfServiceW2DTests
{
    // ═════════════════════════════════════════════════════════════════════════
    // Harness
    // ═════════════════════════════════════════════════════════════════════════

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    /// <summary>A tenant-prefix-enforcing in-memory store, shaped like LocalDocumentStorage.</summary>
    private sealed class MemoryStorage : IDocumentStorage
    {
        public Dictionary<string, byte[]> Objects { get; } = new();
        public async Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var key = $"{tenantId:N}/documents/{Guid.NewGuid():N}_{file.FileName}";
            Objects[key] = ms.ToArray();
            return new StoredDocument(file.FileName, file.ContentType ?? string.Empty, key, string.Empty);
        }
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default)
        {
            if (!storageUrl.StartsWith($"{tenantId:N}/", StringComparison.Ordinal))
                throw new InvalidOperationException("Cross-tenant storage access denied.");
            return Objects.TryGetValue(storageUrl, out var b) ? Task.FromResult(b) : throw new FileNotFoundException(storageUrl);
        }
        public string ResolvePath(string storageUrl) => storageUrl;
    }

    private sealed class StubLetterService : ILetterService
    {
        public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken ct = default) => Task.FromResult(new byte[] { 1 });
        public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    }

    private sealed class StubNotificationService : INotificationService
    {
        public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string entity, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static EmployeeSelfServiceController Ess(ZayraDbContext db, IDocumentStorage storage, Guid tenantId, int employeeId, bool write = true)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new("employee_id", employeeId.ToString()),
            new("permission", "ess.read"),
        };
        if (write) claims.Add(new Claim("permission", "ess.write"));
        var controller = new EmployeeSelfServiceController(
            db, new StubLetterService(), new PdfRenderGate(1),
            new Zayra.Api.Infrastructure.Leave.LeaveService(db, new Zayra.Api.Infrastructure.Approvals.ApprovalRouter(db)),
            new Zayra.Api.Infrastructure.Attendance.AttendanceService(db, new StubNotificationService(), new StubHttpClientFactory()),
            storage);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
        };
        return controller;
    }

    private static Employee SeedEmployee(ZayraDbContext db, Guid tenantId, string code, int? managerId = null)
    {
        var e = new Employee
        {
            TenantId = tenantId, EmployeeCode = code, FullName = $"Employee {code}", Department = "Ops",
            Designation = "Officer", JobTitle = "Officer", Status = "Active", JoiningDate = DateTime.UtcNow.Date.AddYears(-1),
            ManagerEmployeeId = managerId,
        };
        db.Employees.Add(e);
        db.SaveChanges();
        return e;
    }

    private static IFormFile FormFile(byte[] bytes, string name, string contentType) =>
        new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };

    private static readonly byte[] PdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF");

    private static byte[] PngBytes(int width = 40, int height = 20)
    {
        using var bmp = new SKBitmap(width, height);
        bmp.Erase(SKColors.CornflowerBlue);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] JpegBytes(int width, int height)
    {
        using var bmp = new SKBitmap(width, height);
        bmp.Erase(SKColors.OrangeRed);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    /// <summary>
    /// A JPEG carrying an APP1 "Exif" segment with a GPS marker string, the way a phone camera writes
    /// one. Inserted straight after SOI (FF D8), which is where EXIF lives.
    /// </summary>
    private static byte[] JpegWithExif(int width, int height)
    {
        var jpeg = JpegBytes(width, height);
        var tiff = new List<byte>();
        tiff.AddRange(Encoding.ASCII.GetBytes("Exif\0\0"));
        tiff.AddRange(new byte[] { 0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00 }); // big-endian TIFF, 0 IFD entries
        tiff.AddRange(Encoding.ASCII.GetBytes("GPSLatitude=24.7136;GPSLongitude=46.6753"));
        var len = tiff.Count + 2;
        var app1 = new List<byte> { 0xFF, 0xE1, (byte)(len >> 8), (byte)(len & 0xFF) };
        app1.AddRange(tiff);
        return jpeg.Take(2).Concat(app1).Concat(jpeg.Skip(2)).ToArray();
    }

    private static int CountApp1Segments(byte[] jpeg)
    {
        var count = 0;
        for (var i = 2; i + 3 < jpeg.Length;)
        {
            if (jpeg[i] != 0xFF) break;
            var marker = jpeg[i + 1];
            if (marker == 0xDA) break;              // start of scan: headers are over
            var len = (jpeg[i + 2] << 8) | jpeg[i + 3];
            if (marker == 0xE1) count++;
            i += 2 + len;
        }
        return count;
    }

    private static T Value<T>(IActionResult result) where T : class =>
        (result as ObjectResult)?.Value as T ?? throw new InvalidOperationException($"Unexpected {result.GetType().Name}");

    // ═════════════════════════════════════════════════════════════════════════
    // S1 — upload gate
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Upload_accepts_a_real_pdf_stores_it_for_the_caller_and_never_returns_the_storage_key()
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var me = SeedEmployee(db, tenantId, "E1");
        var storage = new MemoryStorage();

        var result = await Ess(db, storage, tenantId, me.Id).UploadDocumentFile(new EssDocumentUploadForm
        {
            File = FormFile(PdfBytes, "my passport scan.pdf", "application/pdf"),
            DocumentType = "Passport",
            ExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(2)),
            DocumentNumber = "P1234567",
        }, CancellationToken.None);

        var created = result.Result.Should().BeOfType<CreatedResult>().Subject;
        var json = JsonSerializer.Serialize(created.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.Should().NotContainEquivalentOf("storageUrl");
        json.Should().NotContain(tenantId.ToString("N"), "the tenant-prefixed key must not leak through any field");

        var doc = await db.EmployeeDocuments.SingleAsync();
        doc.EmployeeId.Should().Be(me.Id);
        doc.ContentType.Should().Be("application/pdf");
        doc.FileName.Should().Be("my_passport_scan.pdf");
        doc.StorageUrl.Should().StartWith($"{tenantId:N}/documents/");
        doc.ApprovalStatus.Should().Be("Pending");
        storage.Objects[doc.StorageUrl].Should().Equal(PdfBytes);
        (await db.EmployeeSelfServiceAuditLogs.AnyAsync(a => a.Action == "ess.document.uploaded")).Should().BeTrue();
    }

    [Fact]
    public async Task Upload_rejects_a_file_over_10_MB()
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var me = SeedEmployee(db, tenantId, "E1");
        var storage = new MemoryStorage();
        var big = new byte[EssUploadPolicy.MaxDocumentBytes + 1];
        PdfBytes.CopyTo(big, 0);

        var result = await Ess(db, storage, tenantId, me.Id).UploadDocumentFile(new EssDocumentUploadForm
        {
            File = FormFile(big, "big.pdf", "application/pdf"), DocumentType = "Contract",
        }, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        storage.Objects.Should().BeEmpty();
        (await db.EmployeeDocuments.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("application/zip", "archive.zip")]                  // not on the allow-list
    [InlineData("application/x-msdownload", "setup.exe")]
    [InlineData("text/html", "page.html")]
    public async Task Upload_rejects_a_disallowed_content_type(string contentType, string name)
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var me = SeedEmployee(db, tenantId, "E1");
        var storage = new MemoryStorage();

        var result = await Ess(db, storage, tenantId, me.Id).UploadDocumentFile(new EssDocumentUploadForm
        {
            File = FormFile(Encoding.ASCII.GetBytes("MZ\0 not a document"), name, contentType), DocumentType = "Other",
        }, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        storage.Objects.Should().BeEmpty();
    }

    [Fact]
    public async Task Upload_rejects_bytes_that_do_not_match_the_claimed_type()
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var me = SeedEmployee(db, tenantId, "E1");
        var storage = new MemoryStorage();
        var controller = Ess(db, storage, tenantId, me.Id);

        // An executable renamed to .pdf and declared as a PDF.
        var exe = Encoding.ASCII.GetBytes("MZ\0\0\0\0 this program cannot be run in DOS mode");
        (await controller.UploadDocumentFile(new EssDocumentUploadForm
        { File = FormFile(exe, "invoice.pdf", "application/pdf"), DocumentType = "Other" }, CancellationToken.None))
            .Result.Should().BeOfType<BadRequestObjectResult>();

        // A PNG declared as a JPEG.
        (await controller.UploadDocumentFile(new EssDocumentUploadForm
        { File = FormFile(PngBytes(), "photo.jpg", "image/jpeg"), DocumentType = "Other" }, CancellationToken.None))
            .Result.Should().BeOfType<BadRequestObjectResult>();

        // Right bytes and type, wrong extension.
        (await controller.UploadDocumentFile(new EssDocumentUploadForm
        { File = FormFile(PdfBytes, "scan.png", "application/pdf"), DocumentType = "Other" }, CancellationToken.None))
            .Result.Should().BeOfType<BadRequestObjectResult>();

        storage.Objects.Should().BeEmpty();
        (await db.EmployeeDocuments.CountAsync()).Should().Be(0);
    }

    [Fact]
    public void Sniffer_recognises_each_allowed_signature_and_nothing_else()
    {
        EssUploadPolicy.Sniff(PdfBytes).Should().Be(EssUploadPolicy.Pdf);
        EssUploadPolicy.Sniff(JpegBytes(4, 4)).Should().Be(EssUploadPolicy.Jpeg);
        EssUploadPolicy.Sniff(PngBytes()).Should().Be(EssUploadPolicy.Png);
        var heic = new byte[] { 0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'h', (byte)'e', (byte)'i', (byte)'c', 0, 0 };
        EssUploadPolicy.Sniff(heic).Should().Be(EssUploadPolicy.Heic);
        EssUploadPolicy.Sniff(Encoding.ASCII.GetBytes("GIF89a......")).Should().BeNull();
        EssUploadPolicy.Sniff(Array.Empty<byte>()).Should().BeNull();
    }

    [Fact]
    public async Task Upload_rejects_a_past_expiry_date_and_an_empty_file()
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var me = SeedEmployee(db, tenantId, "E1");
        var controller = Ess(db, new MemoryStorage(), tenantId, me.Id);

        (await controller.UploadDocumentFile(new EssDocumentUploadForm
        {
            File = FormFile(PdfBytes, "a.pdf", "application/pdf"), DocumentType = "Visa",
            ExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)),
        }, CancellationToken.None)).Result.Should().BeOfType<BadRequestObjectResult>();

        (await controller.UploadDocumentFile(new EssDocumentUploadForm
        { File = FormFile(Array.Empty<byte>(), "a.pdf", "application/pdf"), DocumentType = "Visa" }, CancellationToken.None))
            .Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Read_only_ess_access_cannot_upload()
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var me = SeedEmployee(db, tenantId, "E1");
        var result = await Ess(db, new MemoryStorage(), tenantId, me.Id, write: false).UploadDocumentFile(new EssDocumentUploadForm
        { File = FormFile(PdfBytes, "a.pdf", "application/pdf"), DocumentType = "Visa" }, CancellationToken.None);
        result.Result.Should().BeOfType<BadRequestObjectResult>();
        (await db.EmployeeDocuments.CountAsync()).Should().Be(0);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // S2 / S3 / S6 — nobody reaches another employee's data (404, not 403)
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Document_download_returns_own_file_and_404_for_a_colleagues_document()
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var me = SeedEmployee(db, tenantId, "E1");
        var colleague = SeedEmployee(db, tenantId, "E2");
        var storage = new MemoryStorage();

        await Ess(db, storage, tenantId, me.Id).UploadDocumentFile(new EssDocumentUploadForm
        { File = FormFile(PdfBytes, "mine.pdf", "application/pdf"), DocumentType = "Contract" }, CancellationToken.None);
        await Ess(db, storage, tenantId, colleague.Id).UploadDocumentFile(new EssDocumentUploadForm
        { File = FormFile(PdfBytes, "theirs.pdf", "application/pdf"), DocumentType = "Contract" }, CancellationToken.None);
        var mine = await db.EmployeeDocuments.SingleAsync(d => d.EmployeeId == me.Id);
        var theirs = await db.EmployeeDocuments.SingleAsync(d => d.EmployeeId == colleague.Id);

        var own = await Ess(db, storage, tenantId, me.Id).DownloadDocument(mine.Id, CancellationToken.None);
        own.Should().BeOfType<FileContentResult>().Which.FileContents.Should().Equal(PdfBytes);

        (await Ess(db, storage, tenantId, me.Id).DownloadDocument(theirs.Id, CancellationToken.None))
            .Should().BeOfType<NotFoundResult>("a colleague's id must look exactly like an id that does not exist");
        (await Ess(db, storage, tenantId, me.Id).DownloadDocument(Guid.NewGuid(), CancellationToken.None))
            .Should().BeOfType<NotFoundResult>();

        // Same employee id, other tenant: still 404.
        (await Ess(db, storage, Guid.NewGuid(), me.Id).DownloadDocument(mine.Id, CancellationToken.None))
            .Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Upload_always_lands_on_the_callers_own_record_and_leaves_colleagues_untouched()
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var me = SeedEmployee(db, tenantId, "E1");
        var colleague = SeedEmployee(db, tenantId, "E2");
        db.EmployeeDocuments.Add(new EmployeeDocument
        {
            TenantId = tenantId, EmployeeId = colleague.Id, DocumentType = "Passport", FileName = "theirs.pdf",
            ContentType = "application/pdf", StorageUrl = $"{tenantId:N}/documents/theirs.pdf",
        });
        await db.SaveChangesAsync();

        await Ess(db, new MemoryStorage(), tenantId, me.Id).UploadDocumentFile(new EssDocumentUploadForm
        { File = FormFile(PdfBytes, "theirs.pdf", "application/pdf"), DocumentType = "Passport" }, CancellationToken.None);

        var colleagueDocs = await db.EmployeeDocuments.Where(d => d.EmployeeId == colleague.Id).ToListAsync();
        colleagueDocs.Should().ContainSingle().Which.StorageUrl.Should().Be($"{tenantId:N}/documents/theirs.pdf");
        (await db.EmployeeDocuments.CountAsync(d => d.EmployeeId == me.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Profile_photo_is_reencoded_to_a_small_jpeg_with_no_exif_and_served_only_to_its_owner()
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var me = SeedEmployee(db, tenantId, "E1");
        var colleague = SeedEmployee(db, tenantId, "E2");
        var storage = new MemoryStorage();
        var source = JpegWithExif(1600, 900);
        CountApp1Segments(source).Should().Be(1, "the fixture must really carry EXIF");

        var upload = await Ess(db, storage, tenantId, me.Id).UploadProfilePhoto(
            new EssPhotoUploadForm { File = FormFile(source, "IMG_0001.jpg", "image/jpeg") }, CancellationToken.None);
        upload.Should().BeOfType<OkObjectResult>();
        JsonSerializer.Serialize(((OkObjectResult)upload).Value).Should().Contain("/api/ess/profile/photo?v=");

        db.ChangeTracker.Clear();
        var employee = await db.Employees.SingleAsync(e => e.Id == me.Id);
        employee.ProfilePhotoUrl.Should().StartWith("/api/ess/profile/photo?v=", "the URL is an API route, never a storage key");
        employee.ProfilePhotoUrl.Should().NotContain(tenantId.ToString("N"));

        var served = await Ess(db, storage, tenantId, me.Id).ProfilePhoto(CancellationToken.None);
        var file = served.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().Be("image/jpeg");
        EssUploadPolicy.Sniff(file.FileContents).Should().Be(EssUploadPolicy.Jpeg);
        CountApp1Segments(file.FileContents).Should().Be(0, "EXIF (APP1) must be stripped");
        Encoding.ASCII.GetString(file.FileContents).Should().NotContain("GPSLatitude");
        using (var decoded = SKBitmap.Decode(file.FileContents))
        {
            decoded.Width.Should().Be(512);
            decoded.Height.Should().Be(288);
        }

        // The colleague has no photo: 404, and never the caller's bytes.
        (await Ess(db, storage, tenantId, colleague.Id).ProfilePhoto(CancellationToken.None))
            .Should().BeOfType<NotFoundResult>();
        (await db.Employees.SingleAsync(e => e.Id == colleague.Id)).ProfilePhotoStorageKey.Should().BeEmpty();
    }

    [Fact]
    public async Task Profile_photo_rejects_heic_oversize_and_mismatched_bytes()
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var me = SeedEmployee(db, tenantId, "E1");
        var controller = Ess(db, new MemoryStorage(), tenantId, me.Id);
        var heic = new byte[] { 0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'h', (byte)'e', (byte)'i', (byte)'c', 0, 0 };

        (await controller.UploadProfilePhoto(new EssPhotoUploadForm { File = FormFile(heic, "a.heic", "image/heic") }, CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
        (await controller.UploadProfilePhoto(new EssPhotoUploadForm { File = FormFile(PdfBytes, "a.jpg", "image/jpeg") }, CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
        var big = new byte[EssUploadPolicy.MaxPhotoBytes + 1];
        JpegBytes(8, 8).CopyTo(big, 0);
        (await controller.UploadProfilePhoto(new EssPhotoUploadForm { File = FormFile(big, "a.jpg", "image/jpeg") }, CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
        (await db.Employees.SingleAsync()).ProfilePhotoStorageKey.Should().BeEmpty();
    }

    [Fact]
    public void Photo_processor_bakes_exif_orientation_into_the_pixels()
    {
        // A landscape JPEG tagged "rotate 90 CW" (orientation 6) must come out portrait.
        var jpeg = JpegBytes(200, 100);
        var tiff = new List<byte>();
        tiff.AddRange(Encoding.ASCII.GetBytes("Exif\0\0"));
        tiff.AddRange(new byte[] { 0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08 });   // TIFF header, IFD at 8
        tiff.AddRange(new byte[] { 0x00, 0x01 });                                         // 1 entry
        tiff.AddRange(new byte[] { 0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x06, 0x00, 0x00 }); // Orientation=6
        tiff.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x00 });                             // next IFD
        var len = tiff.Count + 2;
        var withOrientation = new byte[] { 0xFF, 0xD8, 0xFF, 0xE1, (byte)(len >> 8), (byte)(len & 0xFF) }
            .Concat(tiff).Concat(jpeg.Skip(2)).ToArray();

        var output = ProfilePhotoProcessor.ToSanitisedJpeg(withOrientation);
        using var decoded = SKBitmap.Decode(output);
        decoded.Width.Should().Be(100);
        decoded.Height.Should().Be(200);
        CountApp1Segments(output).Should().Be(0);
    }

    private static (PayrollRun Run, PayrollSlip Slip) SeedFinalSlip(ZayraDbContext db, Guid tenantId, Employee e,
        int year, int month, string runStatus = "Locked", bool itemised = true)
    {
        var run = new PayrollRun { TenantId = tenantId, Year = year, Month = month, Status = runStatus };
        db.PayrollRuns.Add(run);
        var slip = new PayrollSlip
        {
            TenantId = tenantId, RunId = run.Id, EmployeeId = e.Id, EmployeeCode = e.EmployeeCode, EmployeeName = e.FullName,
            BasicSalary = 11000m, HousingAllowance = 2750m, TransportAllowance = 1100m, OtherAllowances = 0m,
            GrossSalary = 14850m, Deductions = 1336.50m, NetSalary = 13513.50m, EmployeeStatutoryTotal = 1336.50m,
            Status = "Final", YtdGross = 14850m * month, YtdNet = 13513.50m * month,
        };
        db.PayrollSlips.Add(slip);
        if (itemised)
        {
            var payslip = new Payslip { TenantId = tenantId, PayrollRunId = run.Id, EmployeeId = e.Id, IsPublishedToEss = true, PayslipNumber = $"PS-{year}{month:00}" };
            db.Payslips.Add(payslip);
            db.PayslipComponents.AddRange(
                new PayslipComponent { TenantId = tenantId, PayslipId = payslip.Id, ComponentType = "Earning", ComponentName = "Basic Salary", Amount = 11000m },
                new PayslipComponent { TenantId = tenantId, PayslipId = payslip.Id, ComponentType = "Earning", ComponentName = "Housing Allowance", Amount = 2750m },
                new PayslipComponent { TenantId = tenantId, PayslipId = payslip.Id, ComponentType = "Earning", ComponentName = "Transport Allowance", Amount = 1100m },
                new PayslipComponent { TenantId = tenantId, PayslipId = payslip.Id, ComponentType = "Deduction", ComponentName = "GOSI Employee Share", Amount = 1336.50m },
                new PayslipComponent { TenantId = tenantId, PayslipId = payslip.Id, ComponentType = "Net", ComponentName = "Net Pay", Amount = 13513.50m });
        }
        db.SaveChanges();
        return (run, slip);
    }

    [Theory]
    [InlineData(true)]    // itemised PayslipComponents
    [InlineData(false)]   // fallback: the slip's own columns (as the PDF builds them)
    public async Task Payslip_detail_lines_add_up_to_the_header_totals(bool itemised)
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var me = SeedEmployee(db, tenantId, "E1");
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = tenantId, EmployeeId = me.Id, SalaryCurrency = "SAR" });
        await db.SaveChangesAsync();
        var (_, slip) = SeedFinalSlip(db, tenantId, me, 2026, 8, itemised: itemised);

        var result = await Ess(db, new MemoryStorage(), tenantId, me.Id).PayslipDetail(slip.Id, CancellationToken.None);
        var detail = (EssPayslipDetailDto)((OkObjectResult)result.Result!).Value!;

        detail.PeriodLabel.Should().Be("August 2026");
        detail.Currency.Should().Be("SAR");
        detail.Lines.Where(l => l.Type == "Earning").Sum(l => l.Amount).Should().Be(detail.GrossSalary);
        detail.Lines.Where(l => l.Type == "Deduction").Sum(l => l.Amount).Should().Be(detail.TotalDeductions);
        detail.Lines.Where(l => l.Type == "Net").Sum(l => l.Amount).Should().Be(detail.NetSalary);
        (detail.GrossSalary - detail.TotalDeductions).Should().Be(detail.NetSalary);
        detail.Reconciled.Should().BeTrue();
        detail.GrossSalary.Should().Be(14850m);
        detail.NetSalary.Should().Be(13513.50m);
        if (itemised) detail.Lines.Should().Contain(l => l.Name == "GOSI Employee Share");
    }

    [Fact]
    public async Task Payslip_detail_is_404_for_a_colleague_a_voided_run_and_a_non_final_slip()
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var me = SeedEmployee(db, tenantId, "E1");
        var colleague = SeedEmployee(db, tenantId, "E2");
        var (_, theirs) = SeedFinalSlip(db, tenantId, colleague, 2026, 8);
        var (_, voided) = SeedFinalSlip(db, tenantId, me, 2026, 7, runStatus: "Voided");
        var (_, draft) = SeedFinalSlip(db, tenantId, me, 2026, 6);
        draft.Status = "Draft";
        await db.SaveChangesAsync();
        var controller = Ess(db, new MemoryStorage(), tenantId, me.Id);

        (await controller.PayslipDetail(theirs.Id, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
        (await controller.PayslipDetail(voided.Id, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
        (await controller.PayslipDetail(draft.Id, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
        (await controller.DownloadPayslip(theirs.Id, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        (await controller.DownloadPayslip(voided.Id, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Payslip_list_carries_period_and_currency_is_chronological_and_hides_voided_runs()
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var me = SeedEmployee(db, tenantId, "E1");
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = tenantId, EmployeeId = me.Id, SalaryCurrency = "SAR" });
        await db.SaveChangesAsync();
        // Insert out of order so a GUID/insertion ordering would be wrong.
        SeedFinalSlip(db, tenantId, me, 2026, 3);
        SeedFinalSlip(db, tenantId, me, 2025, 12);
        SeedFinalSlip(db, tenantId, me, 2026, 8);
        SeedFinalSlip(db, tenantId, me, 2026, 5, runStatus: "Voided");

        var result = await Ess(db, new MemoryStorage(), tenantId, me.Id).Payslips(CancellationToken.None);
        var rows = ((IEnumerable<EssPayslipSummaryDto>)((OkObjectResult)result.Result!).Value!).ToList();

        rows.Select(r => r.PeriodLabel).Should().Equal("August 2026", "March 2026", "December 2025");
        rows.Should().OnlyContain(r => r.Currency == "SAR");
        rows.Should().NotContain(r => r.Month == 5);
    }

    [Fact]
    public async Task Hr_request_attachment_must_be_the_callers_own_document()
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var me = SeedEmployee(db, tenantId, "E1");
        var colleague = SeedEmployee(db, tenantId, "E2");
        var theirs = new EmployeeDocument { TenantId = tenantId, EmployeeId = colleague.Id, DocumentType = "Medical Certificate", FileName = "x.pdf", StorageUrl = $"{tenantId:N}/documents/x.pdf" };
        var mine = new EmployeeDocument { TenantId = tenantId, EmployeeId = me.Id, DocumentType = "Medical Certificate", FileName = "y.pdf", StorageUrl = $"{tenantId:N}/documents/y.pdf" };
        db.EmployeeDocuments.AddRange(theirs, mine);
        await db.SaveChangesAsync();
        var controller = Ess(db, new MemoryStorage(), tenantId, me.Id);

        (await controller.CreateHrRequest(new ESSHRRequestCreateDto(null, "Payroll", "s", "d", null, theirs.Id), CancellationToken.None))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        (await db.HRRequests.CountAsync()).Should().Be(0);

        (await controller.CreateHrRequest(new ESSHRRequestCreateDto(null, "Payroll", "s", "d", null, mine.Id), CancellationToken.None))
            .Result.Should().BeOfType<CreatedResult>();
        (await db.HRRequests.SingleAsync()).AttachmentDocumentId.Should().Be(mine.Id);
    }

    private sealed class OwnScope(int employeeId) : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Own, CallerEmployeeId = employeeId, AllowedEmployeeIds = new[] { employeeId } });
    }

    [Fact]
    public async Task Leave_attachment_is_resolved_from_an_owned_document_and_never_accepts_bytes_or_a_colleagues_key()
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var me = SeedEmployee(db, tenantId, "E1");
        var colleague = SeedEmployee(db, tenantId, "E2");
        var leaveType = new LeaveType { TenantId = tenantId, NameEn = "Sick", Code = "SICK", IsActive = true };
        var theirs = new EmployeeDocument { TenantId = tenantId, EmployeeId = colleague.Id, DocumentType = "Medical Certificate", FileName = "x.pdf", StorageUrl = $"{tenantId:N}/documents/x.pdf" };
        db.AddRange(leaveType, theirs);
        await db.SaveChangesAsync();

        var controller = new LeaveRequestsController(db,
            new Zayra.Api.Infrastructure.Leave.LeaveService(db, new Zayra.Api.Infrastructure.Approvals.ApprovalRouter(db)),
            new OwnScope(me.Id), new StubNotificationService())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", tenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    }, "Test")),
                },
            },
        };
        SubmitLeaveRequestRequest Req(string? path = null, Guid? docId = null) => new(me.Id, leaveType.Id, null,
            new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 1), "Full", null, "sick", false, path, AttachmentDocumentId: docId);

        (await controller.Submit(Req(docId: theirs.Id), CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
        (await controller.Submit(Req(path: theirs.StorageUrl), CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
        (await controller.Submit(Req(path: Convert.ToBase64String(PdfBytes)), CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
        (await db.LeaveRequests.CountAsync()).Should().Be(0);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // S4 — preferences are enforced, not decorative
    // ═════════════════════════════════════════════════════════════════════════

    private sealed class CapturingSmsProvider : ISmsProvider
    {
        public List<ProviderMessage> Sent { get; } = [];
        public string Name => "fake-sms";
        public Task<bool> IsConfiguredAsync(Guid tenantId, CancellationToken ct) => Task.FromResult(true);
        public Task<ProviderSendResult> SendAsync(ProviderMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.FromResult(new ProviderSendResult(ProviderSendStatus.Sent, "ref-" + Sent.Count));
        }
    }

    private sealed class CapturingPushProvider : IPushProvider
    {
        public List<ProviderMessage> Sent { get; } = [];
        public string Name => "fake-push";
        public Task<bool> IsConfiguredAsync(Guid tenantId, CancellationToken ct) => Task.FromResult(true);
        public Task<ProviderSendResult> SendAsync(ProviderMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.FromResult(new ProviderSendResult(ProviderSendStatus.Sent, "push-" + Sent.Count));
        }
    }

    private sealed class NoEmail : IEmailService
    {
        public Task SendAsync(string toAddress, string toName, string subject, string htmlBody,
            IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class NotificationHarness : IDisposable
    {
        public ServiceProvider Provider { get; }
        public NotificationHarness(ISmsProvider sms, IPushProvider push)
        {
            var name = Guid.NewGuid().ToString();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMemoryCache();
            services.AddDataProtection();
            services.AddDbContext<ZayraDbContext>(o => o.UseInMemoryDatabase(name));
            services.AddSingleton<IEmailService>(new NoEmail());
            services.AddScoped<INotificationRecipientResolver, NotificationRecipientResolver>();
            services.AddScoped<INotificationProviderConfigReader, NotificationProviderConfigReader>();
            services.AddSingleton(sms);
            services.AddScoped<IWhatsAppProvider, NullWhatsAppProvider>();
            services.AddSingleton(push);
            services.AddScoped<INotificationChannelDispatcher, EmailChannelDispatcher>();
            services.AddScoped<INotificationChannelDispatcher, SmsChannelDispatcher>();
            services.AddScoped<INotificationChannelDispatcher, WhatsAppChannelDispatcher>();
            services.AddScoped<INotificationChannelDispatcher, PushChannelDispatcher>();
            services.AddScoped<INotificationService, NotificationService>();
            Provider = services.BuildServiceProvider();
        }
        public ZayraDbContext NewDb() => Provider.CreateScope().ServiceProvider.GetRequiredService<ZayraDbContext>();
        public INotificationService Notifications => Provider.CreateScope().ServiceProvider.GetRequiredService<INotificationService>();
        public NotificationDeliveryWorker Worker => new(Provider.GetRequiredService<IServiceScopeFactory>(),
            Provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<NotificationDeliveryWorker>>());
        public void Dispose() => Provider.Dispose();
    }

    /// <summary>
    /// Employee 1 has opted IN to SMS and push at the channel level, has a device and a phone, and the
    /// tenant has active SMS + Push templates for both events — so both channels WOULD send.
    /// </summary>
    private static Guid SeedReachableEmployee(ZayraDbContext db)
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        db.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "aisha@acme.test", FullName = "Aisha Rahman" });
        db.Employees.Add(new Employee { Id = 1, TenantId = tenantId, UserAccountId = userId, FullName = "Aisha Rahman", WorkEmail = "aisha@acme.test", Phone = "+966501234567", Status = "Active" });
        db.EmployeeMobileDevices.Add(new EmployeeMobileDevice { TenantId = tenantId, EmployeeId = 1, DeviceIdentifier = "dev-1", Platform = "ios", PushToken = "ExponentPushToken[abc]" });
        db.EmployeeNotificationPreferences.Add(new EmployeeNotificationPreference { TenantId = tenantId, EmployeeId = 1, SmsEnabled = true, PushEnabled = true });
        foreach (var code in new[] { "LeaveRequest.Notice", "PASSWORD_RESET" })
            foreach (var channel in new[] { "SMS", "Push" })
                db.NotificationTemplates.Add(new NotificationTemplate
                {
                    TenantId = tenantId, Code = code, Channel = channel, EventType = code,
                    SubjectEn = "Update", BodyEn = "You have an update in KynexOne.", IsActive = true,
                });
        db.SaveChanges();
        return tenantId;
    }

    [Fact]
    public async Task Opting_out_of_a_category_on_push_blocks_push_but_not_sms_or_in_app()
    {
        var sms = new CapturingSmsProvider();
        var push = new CapturingPushProvider();
        using var h = new NotificationHarness(sms, push);
        var db = h.NewDb();
        var tenantId = SeedReachableEmployee(db);

        // Through the real endpoint: push.leave = false.
        var put = await Ess(db, new MemoryStorage(), tenantId, 1).UpdateNotificationPreferences(
            JsonDocument.Parse("""{"push":{"leave":false}}""").RootElement, CancellationToken.None);
        put.Should().BeOfType<OkObjectResult>();

        await h.Notifications.EnqueueAsync(new NotificationRequest
        {
            TenantId = tenantId, EmployeeId = 1, EventCode = "LeaveRequest.Notice", EntityName = "LeaveRequest",
            EntityId = Guid.NewGuid().ToString(), Title = "Leave update", Message = "Your leave request was updated.",
        }, default);
        await h.Worker.DrainOnceAsync(default);

        push.Sent.Should().BeEmpty("the employee turned leave notifications off on push");
        sms.Sent.Should().ContainSingle("SMS was not opted out");
        var read = h.NewDb();
        var pushRow = read.NotificationDeliveries.IgnoreQueryFilters().Single(d => d.Channel == "Push");
        pushRow.Outcome.Should().Be(DeliveryOutcomes.Suppressed);
        pushRow.ErrorCode.Should().Be("employee_opted_out");
        read.EmployeeNotifications.IgnoreQueryFilters().Should().ContainSingle("in-app is the guaranteed fallback");
    }

    [Fact]
    public async Task Opt_out_made_after_enqueue_still_blocks_the_queued_send()
    {
        var sms = new CapturingSmsProvider();
        var push = new CapturingPushProvider();
        using var h = new NotificationHarness(sms, push);
        var db = h.NewDb();
        var tenantId = SeedReachableEmployee(db);

        await h.Notifications.EnqueueAsync(new NotificationRequest
        {
            TenantId = tenantId, EmployeeId = 1, EventCode = "LeaveRequest.Notice", EntityName = "LeaveRequest",
            EntityId = Guid.NewGuid().ToString(), Title = "Leave update", Message = "Your leave request was updated.",
        }, default);
        // The row is queued; the employee opts out of SMS for leave before the worker runs.
        await Ess(db, new MemoryStorage(), tenantId, 1).UpdateNotificationPreferences(
            JsonDocument.Parse("""{"sms":{"leave":{"enabled":false}}}""").RootElement, CancellationToken.None);
        await h.Worker.DrainOnceAsync(default);

        sms.Sent.Should().BeEmpty();
        push.Sent.Should().ContainSingle();
        h.NewDb().NotificationDeliveries.IgnoreQueryFilters().Single(d => d.Channel == "SMS")
            .ErrorCode.Should().Be("employee_opted_out");
    }

    [Fact]
    public async Task Mandatory_security_notices_ignore_an_opt_out_and_cannot_be_turned_off()
    {
        var sms = new CapturingSmsProvider();
        var push = new CapturingPushProvider();
        using var h = new NotificationHarness(sms, push);
        var db = h.NewDb();
        var tenantId = SeedReachableEmployee(db);

        (await Ess(db, new MemoryStorage(), tenantId, 1).UpdateNotificationPreferences(
            JsonDocument.Parse("""{"push":{"security":false}}""").RootElement, CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();

        // Even a row written behind the API's back does not silence a mandatory category.
        db.EmployeeNotificationCategoryPreferences.Add(new EmployeeNotificationCategoryPreference
        { TenantId = tenantId, EmployeeId = 1, Channel = "push", Category = "security", Enabled = false });
        await db.SaveChangesAsync();

        await h.Notifications.EnqueueAsync(new NotificationRequest
        {
            TenantId = tenantId, EmployeeId = 1, EventCode = "PASSWORD_RESET", EntityName = "User",
            Title = "Password reset", Message = "Your password was reset.",
        }, default);
        await h.Worker.DrainOnceAsync(default);
        push.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task Preferences_default_to_enabled_expose_locked_categories_and_reject_unknown_keys()
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var me = SeedEmployee(db, tenantId, "E1");
        var controller = Ess(db, new MemoryStorage(), tenantId, me.Id);

        var view = (Dictionary<string, Dictionary<string, EssNotificationPreferenceDto>>)((OkObjectResult)await controller.NotificationPreferences(CancellationToken.None)).Value!;
        view.Keys.Should().BeEquivalentTo("push", "email", "sms");
        view["push"]["approvals"].Should().Be(new EssNotificationPreferenceDto(true, false));
        view["email"]["security"].Should().Be(new EssNotificationPreferenceDto(true, true));

        (await controller.UpdateNotificationPreferences(JsonDocument.Parse("""{"fax":{"leave":false}}""").RootElement, CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
        (await controller.UpdateNotificationPreferences(JsonDocument.Parse("""{"push":{"gossip":false}}""").RootElement, CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
        (await controller.UpdateNotificationPreferences(JsonDocument.Parse("""{"push":{"leave":"no"}}""").RootElement, CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();

        var updated = (Dictionary<string, Dictionary<string, EssNotificationPreferenceDto>>)((OkObjectResult)await controller.UpdateNotificationPreferences(
            JsonDocument.Parse("""{"email":{"payslip":false}}""").RootElement, CancellationToken.None)).Value!;
        updated["email"]["payslip"].Enabled.Should().BeFalse();
        updated["push"]["payslip"].Enabled.Should().BeTrue("a partial update touches only what it names");
    }

    [Theory]
    [InlineData("LeaveRequest.Notice", "LeaveRequest", "leave")]
    [InlineData("PAYSLIP_READY", "PAYSLIP_READY", "payslip")]
    [InlineData("COMPLIANCE_DOCUMENT_EXPIRY", "ComplianceReminder", "documents")]
    [InlineData("AttendanceRegularizationRequest.Notice", "AttendanceRegularizationRequest", "attendance")]
    [InlineData("OvertimeRequest.Notice", "OvertimeRequest", "overtime")]
    [InlineData("ApprovalRequest.Notice", "LeaveRequest", "approvals")]
    [InlineData("HRRequest.Notice", "HRRequest", "hr_requests")]
    [InlineData("PASSWORD_RESET", "User", "security")]
    [InlineData("GENERIC_NOTICE", "", null)]
    public void Event_codes_map_onto_the_categories_the_app_shows(string code, string entity, string? expected) =>
        NotificationCategories.Classify(code, entity).Should().Be(expected);

    // ═════════════════════════════════════════════════════════════════════════
    // S7 — push data: routing codes and opaque ids only
    // ═════════════════════════════════════════════════════════════════════════

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"data":[{"status":"ok","id":"r1"}]}""", Encoding.UTF8, "application/json") };
        }
    }

    private sealed class HandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ExpoConfig : INotificationProviderConfigReader
    {
        public Task<NotificationProviderConfig> GetAsync(Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new NotificationProviderConfig(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Push.Provider"] = "expo" }));
    }

    private static async Task<JsonElement> SendAndCaptureData(ProviderMessage message)
    {
        var handler = new RecordingHandler();
        var provider = new ExpoPushProvider(new ExpoConfig(), new HandlerFactory(handler),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ExpoPushProvider>.Instance);
        (await provider.SendAsync(message, CancellationToken.None)).Status.Should().Be(ProviderSendStatus.Sent);
        using var doc = JsonDocument.Parse(handler.Body!);
        return doc.RootElement[0].GetProperty("data").Clone();
    }

    [Fact]
    public async Task Push_data_carries_type_entity_and_id_for_routing()
    {
        var entityId = Guid.NewGuid().ToString();
        var data = await SendAndCaptureData(new ProviderMessage(Guid.NewGuid(), "ExponentPushToken[abc]", "Aisha Rahman",
            "Payslip ready", "Your payslip for August is ready.", "abc123", "ios",
            EventCode: "HRRequest.Notice", EntityName: "HRRequest", EntityId: entityId));

        data.GetProperty("type").GetString().Should().Be("HRRequest.Notice");
        data.GetProperty("entityName").GetString().Should().Be("HRRequest");
        data.GetProperty("entityId").GetString().Should().Be(entityId);
        data.GetProperty("idempotencyKey").GetString().Should().Be("abc123");
    }

    [Fact]
    public async Task Push_data_contains_no_personal_information()
    {
        // Every personal/financial value a caller could have put anywhere on the message.
        var data = await SendAndCaptureData(new ProviderMessage(Guid.NewGuid(), "ExponentPushToken[abc]",
            RecipientName: "Aisha Rahman",
            Subject: "Aisha, your net pay is SAR 13,513.50",
            Body: "Paid on 2026-08-31 to IBAN SA0380000000608010167519",
            IdempotencyKey: "abc123", Platform: "ios",
            EventCode: "PAYSLIP_READY",
            EntityName: "aisha.rahman@acme.test",         // not a code: dropped
            EntityId: "Aisha Rahman 13513.50"));          // not a GUID/number: dropped

        var keys = data.EnumerateObject().Select(p => p.Name).ToList();
        keys.Should().BeSubsetOf(new[] { "idempotencyKey", "type", "entityName", "entityId" });
        keys.Should().NotContain("entityName").And.NotContain("entityId");
        var raw = data.GetRawText();
        foreach (var pii in new[] { "Aisha", "Rahman", "13,513", "13513", "2026-08-31", "IBAN", "SA0380", "@" })
            raw.Should().NotContain(pii);
        data.GetProperty("type").GetString().Should().Be("PAYSLIP_READY");
    }

    [Fact]
    public async Task Dispatcher_forwards_the_delivery_rows_routing_identity_to_the_provider()
    {
        var push = new CapturingPushProvider();
        var dispatcher = new PushChannelDispatcher(push, Microsoft.Extensions.Logging.Abstractions.NullLogger<PushChannelDispatcher>.Instance);
        var id = Guid.NewGuid().ToString();
        await dispatcher.SendAsync(new NotificationDispatchRequest(Guid.NewGuid(), NotificationChannels.Push, "LeaveRequest.Notice",
            "push", "Aisha", "s", "b", "key", PushTargets: new[] { new PushTarget(Guid.NewGuid(), "ExponentPushToken[abc]", "ios") },
            EntityName: "LeaveRequest", EntityId: id), CancellationToken.None);

        push.Sent.Should().ContainSingle();
        push.Sent[0].EventCode.Should().Be("LeaveRequest.Notice");
        push.Sent[0].EntityName.Should().Be("LeaveRequest");
        push.Sent[0].EntityId.Should().Be(id);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // S8 — team, today's attendance from raw punches, KynexOne wording
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Team_lists_only_direct_reports_with_todays_status_from_every_source()
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var manager = SeedEmployee(db, tenantId, "M1");
        var processed = SeedEmployee(db, tenantId, "R1", manager.Id);
        var punchedIn = SeedEmployee(db, tenantId, "R2", manager.Id);
        var onLeave = SeedEmployee(db, tenantId, "R3", manager.Id);
        var notIn = SeedEmployee(db, tenantId, "R4", manager.Id);
        var otherTeam = SeedEmployee(db, tenantId, "X1");
        SeedEmployee(db, tenantId, "X2", otherTeam.Id);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var morning = today.ToDateTime(new TimeOnly(0, 30), DateTimeKind.Utc);
        db.AttendanceDailyRecords.Add(new AttendanceDailyRecord { TenantId = tenantId, EmployeeId = processed.Id, WorkDate = today, Status = "Late", FirstInUtc = morning });
        db.AttendanceRawEvents.Add(new AttendanceRawEvent { TenantId = tenantId, EmployeeId = punchedIn.Id, PunchTimestampUtc = morning, PunchDirection = "In" });
        db.LeaveRequests.Add(new LeaveRequest { TenantId = tenantId, EmployeeId = onLeave.Id, Status = "Approved", StartDate = today.AddDays(-1), EndDate = today.AddDays(1) });
        await db.SaveChangesAsync();

        var result = await Ess(db, new MemoryStorage(), tenantId, manager.Id).Team(CancellationToken.None);
        var team = ((IEnumerable<EssTeamMemberDto>)((OkObjectResult)result.Result!).Value!).ToDictionary(t => t.EmployeeCode);

        team.Keys.Should().BeEquivalentTo("R1", "R2", "R3", "R4");
        team["R1"].TodayStatus.Should().Be("Late");
        team["R1"].StatusSource.Should().Be("processed");
        team["R2"].TodayStatus.Should().Be("Present");
        team["R2"].StatusSource.Should().Be("raw");
        team["R2"].CurrentlyActive.Should().BeTrue();
        team["R3"].TodayStatus.Should().Be("On leave");
        team["R4"].TodayStatus.Should().Be("Not clocked in");

        // An employee with no reports gets an empty list, not someone else's team.
        var none = await Ess(db, new MemoryStorage(), tenantId, notIn.Id).Team(CancellationToken.None);
        ((IEnumerable<EssTeamMemberDto>)((OkObjectResult)none.Result!).Value!).Should().BeEmpty();
    }

    [Fact]
    public async Task Dashboard_exposes_attendance_today_from_raw_punches_before_hr_processing()
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var me = SeedEmployee(db, tenantId, "E1");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var t0 = today.ToDateTime(new TimeOnly(0, 5), DateTimeKind.Utc);
        db.AttendanceRawEvents.AddRange(
            new AttendanceRawEvent { TenantId = tenantId, EmployeeId = me.Id, PunchTimestampUtc = t0, PunchDirection = "In" },
            new AttendanceRawEvent { TenantId = tenantId, EmployeeId = me.Id, PunchTimestampUtc = t0.AddMinutes(90), PunchDirection = "Out" });
        await db.SaveChangesAsync();

        var result = await Ess(db, new MemoryStorage(), tenantId, me.Id).Dashboard(CancellationToken.None);
        var dto = (ESSDashboardDto)((OkObjectResult)result.Result!).Value!;
        dto.AttendanceTodaySource.Should().Be("raw");
        dto.AttendanceToday.Should().NotBeNull();
        dto.AttendanceToday!.FirstInUtc.Should().Be(t0);
        dto.AttendanceToday.LastOutUtc.Should().Be(t0.AddMinutes(90));
        dto.AttendanceToday.TotalWorkedMinutes.Should().Be(90);
        (await db.AttendanceDailyRecords.CountAsync()).Should().Be(0, "the provisional record is never persisted");
    }

    private sealed class TeamScope(int self, params int[] team) : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Team, CallerEmployeeId = self, AllowedEmployeeIds = team.Append(self).ToList() });
    }

    [Fact]
    public async Task Dashboard_summary_for_a_manager_counts_their_team_not_the_tenant()
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var manager = SeedEmployee(db, tenantId, "M1");
        var reports = Enumerable.Range(1, 3).Select(i => SeedEmployee(db, tenantId, $"R{i}", manager.Id)).ToList();
        for (var i = 0; i < 8; i++) SeedEmployee(db, tenantId, $"X{i}");   // the rest of the company
        var controller = new DashboardController(db,
            new Microsoft.Extensions.Caching.Distributed.MemoryDistributedCache(
                Microsoft.Extensions.Options.Options.Create(new Microsoft.Extensions.Caching.Memory.MemoryDistributedCacheOptions())),
            new TeamScope(manager.Id, reports.Select(r => r.Id).ToArray()))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("tenant_id", tenantId.ToString()) }, "Test")),
                },
            },
        };

        var summary = (DashboardSummaryDto)((OkObjectResult)await controller.Summary(CancellationToken.None)).Value!;
        summary.TotalEmployees.Should().Be(3, "a manager's summary is their 3 reports, not the tenant's 12 employees");
    }

    [Fact]
    public async Task Ai_assistant_reply_says_KynexOne_not_Zayra()
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var me = SeedEmployee(db, tenantId, "E1");
        var result = await Ess(db, new MemoryStorage(), tenantId, me.Id).AskAi(new ESSAIQuestionDto("How much leave do I have?"), CancellationToken.None);
        var answer = ((ESSAIAnswerDto)((OkObjectResult)result.Result!).Value!).Answer;
        answer.Should().Contain("KynexOne").And.NotContain("Zayra");
    }
}
