using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Infrastructure.Localization;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// A leave's supporting document (a sick note) is stored as an ordinary employee document of type "Leave Attachment".
/// The leave rule is: the employee and Admin / HR Manager / HR Officer (LeaveFileKeyExposure). The HR document endpoints
/// were wider: the list is open to every employees.read holder in scope (managers, an auditor) and carries the storage key,
/// and the download and verify/reject/archive actions to every employees.documents holder (a compliance officer).
/// The document keeps following the leave rule wherever the document endpoints can reach it.
/// </summary>
public sealed class LeaveAttachmentDocumentVisibilityTests
{
    private const string LeaveType = "Leave Attachment";

    private sealed record World(ZayraDbContext Db, Guid TenantId, Employee Colleague, EmployeeDocument Note, EmployeeDocument Other, MemoryStorage Storage);

    private static async Task<World> SeedAsync()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase($"leave-doc-{Guid.NewGuid():N}").Options);
        var tenantId = Guid.NewGuid();
        var company = new Company { TenantId = tenantId, LegalNameEn = "Acme", RegistrationNumber = $"R-{Guid.NewGuid():N}", CountryCode = "SA", DefaultCurrency = "SAR", IsActive = true };
        var colleague = new Employee { TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "E-1", FullName = "Colleague", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-2) };
        db.AddRange(company, colleague);
        await db.SaveChangesAsync();
        var storage = new MemoryStorage();
        EmployeeDocument Doc(string type, string key) => new()
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeId = colleague.Id, DocumentType = type, FileName = $"{type}.pdf", ContentType = "application/pdf",
            StorageUrl = key, ApprovalStatus = "Pending", ScanStatus = "clean", UploadedAtUtc = DateTime.UtcNow,
        };
        var note = Doc(LeaveType, $"{tenantId:N}/documents/sicknote.pdf");
        var other = Doc("Other Document", $"{tenantId:N}/documents/other.pdf");
        storage.Objects[note.StorageUrl] = [1, 2, 3];
        storage.Objects[other.StorageUrl] = [4, 5, 6];
        db.EmployeeDocuments.AddRange(note, other);
        await db.SaveChangesAsync();
        return new World(db, tenantId, colleague, note, other, storage);
    }

    private static EmployeesController Controller(World w, string role, string[] permissions, int? ownEmployeeId = null)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", w.TenantId.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new(ClaimTypes.Role, role),
            new("is_group_scope", "true"),
        };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        if (ownEmployeeId is int own) claims.Add(new Claim("employee_id", own.ToString()));
        return new EmployeesController(w.Db, new Pbkdf2PasswordHasher(), new NoopAudit(), w.Storage, new NoopNotifications(), new FakeHijri(),
            new OrgScope(), new NoLetters())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) } },
        };
    }

    private static EmployeeManagementService Service(World w) => new(w.Db, new NoopAudit(), w.Storage, new NoopNotifications());

    private static readonly string[] Reads = ["employees.read"];
    private static readonly string[] ReadsAndDocuments = ["employees.read", "employees.documents"];

    // who, role, permissions, may see the leave attachment
    public static TheoryData<string, string, bool> Callers => new()
    {
        { "Auditor", "employees.read", false },
        { "Manager", "employees.read", false },
        { "Compliance Officer", "employees.read+employees.documents", false },
        { "Payroll Officer", "employees.read+employees.sensitive", false },
        { "HR Officer", "employees.read+employees.documents", true },
        { "HR Manager", "employees.read+employees.documents", true },
        { "Admin", "employees.read+employees.documents", true },
    };

    private static string[] Perms(string joined) => joined.Split('+');

    [Theory]
    [MemberData(nameof(Callers))]
    public async Task The_document_list_shows_the_leave_attachment_only_to_the_leave_rule_readers(string role, string permissions, bool sees)
    {
        var w = await SeedAsync();

        var result = await Controller(w, role, Perms(permissions)).EmployeeDocuments(w.Colleague.Id, Service(w), default);

        var docs = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeAssignableTo<IReadOnlyCollection<EmployeeDocumentDto>>().Subject;
        docs.Select(d => d.DocumentType).Should().Contain("Other Document", "ordinary documents are unaffected");
        docs.Any(d => d.DocumentType == LeaveType).Should().Be(sees);
        docs.Any(d => d.StorageUrl == w.Note.StorageUrl).Should().Be(sees, "the storage key is the handle to the medical note");
    }

    [Fact]
    public async Task The_employee_sees_their_own_leave_attachment_through_the_document_list()
    {
        var w = await SeedAsync();

        var result = await Controller(w, "Employee", Reads, ownEmployeeId: w.Colleague.Id).EmployeeDocuments(w.Colleague.Id, Service(w), default);

        var docs = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeAssignableTo<IReadOnlyCollection<EmployeeDocumentDto>>().Subject;
        docs.Select(d => d.DocumentType).Should().Contain(LeaveType);
    }

    [Theory]
    [MemberData(nameof(Callers))]
    public async Task The_download_serves_the_leave_attachment_only_to_the_leave_rule_readers(string role, string permissions, bool sees)
    {
        var w = await SeedAsync();

        var result = await Controller(w, role, Perms(permissions)).DownloadDocument(w.Note.Id, default);

        // Without employees.documents the download is refused outright; with it, only the leave rule's readers are served the note.
        if (sees) result.Should().BeOfType<FileContentResult>().Which.FileContents.Should().Equal(1, 2, 3);
        else result.Should().NotBeOfType<FileContentResult>();
    }

    [Fact]
    public async Task A_compliance_officer_downloading_the_leave_attachment_gets_not_found_not_forbidden()
    {
        var w = await SeedAsync();

        var result = await Controller(w, "Compliance Officer", ReadsAndDocuments).DownloadDocument(w.Note.Id, default);

        result.Should().BeOfType<NotFoundResult>("the response must not confirm that a medical note exists");
        (await Controller(w, "Compliance Officer", ReadsAndDocuments).DownloadDocument(w.Other.Id, default)).Should().BeOfType<FileContentResult>();
    }

    [Fact]
    public async Task The_employee_downloads_their_own_leave_attachment_through_the_document_endpoint()
    {
        var w = await SeedAsync();

        var result = await Controller(w, "Employee", ReadsAndDocuments, ownEmployeeId: w.Colleague.Id).DownloadDocument(w.Note.Id, default);

        result.Should().BeOfType<FileContentResult>();
    }

    [Fact]
    public async Task A_documents_holder_outside_the_leave_rule_cannot_verify_reject_update_or_archive_the_leave_attachment()
    {
        var w = await SeedAsync();
        var compliance = () => Controller(w, "Compliance Officer", ["employees.read", "employees.documents", "employees.approve"]);

        (await compliance().VerifyEmployeeDocument(w.Colleague.Id, w.Note.Id, new DocumentVerifyRequest("ok"), Service(w), default)).Result.Should().BeOfType<NotFoundResult>();
        (await compliance().RejectEmployeeDocument(w.Colleague.Id, w.Note.Id, new DocumentRejectRequest("no"), Service(w), default)).Result.Should().BeOfType<NotFoundResult>();
        (await compliance().ArchiveEmployeeDocument(w.Colleague.Id, w.Note.Id, Service(w), default)).Should().BeOfType<NotFoundResult>();

        w.Db.ChangeTracker.Clear();
        var stored = await w.Db.EmployeeDocuments.SingleAsync(d => d.Id == w.Note.Id);
        stored.ApprovalStatus.Should().Be("Pending");
        stored.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public async Task The_verification_queue_lists_the_leave_attachment_only_to_the_leave_rule_readers()
    {
        var w = await SeedAsync();

        var compliance = (await Controller(w, "Compliance Officer", ReadsAndDocuments).PendingDocumentVerification(Service(w), 1, 25, default))
            .Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<PendingDocumentsPage>().Subject;
        var hr = (await Controller(w, "HR Officer", ReadsAndDocuments).PendingDocumentVerification(Service(w), 1, 25, default))
            .Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<PendingDocumentsPage>().Subject;

        compliance.Items.Select(i => i.DocumentType).Should().Contain("Other Document").And.NotContain(LeaveType);
        hr.Items.Select(i => i.DocumentType).Should().Contain(LeaveType);
    }

    [Fact]
    public async Task The_hr_leave_form_cannot_attach_an_hr_evidence_document_as_a_leave_attachment()
    {
        var w = await SeedAsync();
        var evidence = new EmployeeDocument
        {
            TenantId = w.TenantId, CompanyId = w.Note.CompanyId, EmployeeId = w.Colleague.Id, DocumentType = RestrictedEmployeeDocumentTypes.QiwaEvidence,
            FileName = "q.pdf", ContentType = "application/pdf", StorageUrl = $"{w.TenantId:N}/documents/qiwa.pdf", ScanStatus = "clean",
        };
        var type = new LeaveType { TenantId = w.TenantId, Code = "SICK", NameEn = "Sick Leave", Category = "Sick", IsActive = true, RequiresAttachment = true };
        w.Db.AddRange(evidence, type);
        await w.Db.SaveChangesAsync();
        var controller = new LeaveRequestsController(w.Db, new LeaveService(w.Db, new ApprovalRouter(w.Db)), new OrgScope(), TestNotifications.For(w.Db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", w.TenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                        new Claim(ClaimTypes.Role, "HR Officer"), new Claim("permission", "leave.write"),
                    }, "Test")),
                },
            },
        };
        var day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(3);

        var result = await controller.Submit(new SubmitLeaveRequestRequest(w.Colleague.Id, type.Id, null, day, day, "Full", null, "flu", false, null,
            AttachmentDocumentId: evidence.Id), default);

        result.Should().BeOfType<BadRequestObjectResult>().Which.Value!.ToString().Should().Contain("attachment was not found");
    }

    private sealed class MemoryStorage : IDocumentStorage
    {
        public Dictionary<string, byte[]> Objects { get; } = new();
        public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) => throw new NotSupportedException();
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Objects[storageUrl]);
        public string ResolvePath(string storageUrl) => storageUrl;
    }

    private sealed class NoopAudit : IAuditService
    {
        public Task WriteAsync(string action, string entityName, string? entityId, RequestContext context, string? metadata, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoopNotifications : INotificationService
    {
        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeHijri : IHijriDateService
    {
        public DateConversionDto FromGregorian(DateOnly date) => new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
    }

    private sealed class NoLetters : ILetterService
    {
        public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    }

    private sealed class OrgScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
            => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }
}
