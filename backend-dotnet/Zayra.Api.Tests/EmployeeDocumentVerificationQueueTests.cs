using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// G-004: the verify / reject endpoints had no screen and there was no queue, while RequireVerified readiness policies block
/// activation and pay. HR is told when an employee uploads, a cross-employee "Documents to verify" queue lists what is waiting
/// (protected categories only for a caller holding employees.sensitive, as the verify endpoint itself requires), a rejection
/// carries a reason the employee can read, and a verified or rejected document says so.
/// </summary>
public class EmployeeDocumentVerificationQueueTests
{
    private sealed record Notice(Guid? UserId, string Title, string Message, string EntityName, string? EntityId);

    private sealed class Recorder : INotificationService
    {
        public List<Notice> Sent { get; } = [];
        public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string en, string? eid, CancellationToken ct) { Sent.Add(new Notice(u, title, msg, en, eid)); return Task.CompletedTask; }
        public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class MemoryStorage : IDocumentStorage
    {
        public Dictionary<string, byte[]> Objects { get; } = new();
        public async Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct)
        {
            using var ms = new MemoryStream(); await file.CopyToAsync(ms, ct);
            var key = $"{tenantId:N}/documents/{Guid.NewGuid():N}_{file.FileName}"; Objects[key] = ms.ToArray();
            return new StoredDocument(file.FileName, file.ContentType ?? string.Empty, key, string.Empty);
        }
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Objects[storageUrl]);
        public string ResolvePath(string storageUrl) => storageUrl;
    }

    private sealed class World
    {
        public required ZayraDbContext Db { get; init; }
        public required Guid TenantId { get; init; }
        public required Company CompanyA { get; init; }
        public required Employee Maya { get; init; }
        public required Guid MayaLogin { get; init; }
        public required Guid HrInScope { get; init; }
        public required Guid HrWithSensitive { get; init; }
        public required Guid HrOtherCompany { get; init; }
        public required Guid ManagerNoPermission { get; init; }
    }

    private static async Task<World> SeedAsync()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase($"docq-{Guid.NewGuid():N}").Options);
        var tenant = new Tenant { Name = "T", Slug = $"t-{Guid.NewGuid():N}" };
        var a = new Company { TenantId = tenant.Id, LegalNameEn = "A", RegistrationNumber = $"A-{Guid.NewGuid():N}", CountryCode = "SA", DefaultCurrency = "SAR", IsActive = true };
        var b = new Company { TenantId = tenant.Id, LegalNameEn = "B", RegistrationNumber = $"B-{Guid.NewGuid():N}", CountryCode = "SA", DefaultCurrency = "SAR", IsActive = true };
        var mayaUser = new User { TenantId = tenant.Id, Email = "maya@x.test", NormalizedEmail = "MAYA@X.TEST", FullName = "Maya", IsActive = true };
        var maya = new Employee { TenantId = tenant.Id, CompanyId = a.Id, EmployeeCode = "E-MAYA", FullName = "Maya Ahmed", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1), UserAccountId = mayaUser.Id };
        var docsRole = new Role { TenantId = tenant.Id, Name = "HR Officer", NormalizedName = "HR OFFICER", IsActive = true };
        var sensRole = new Role { TenantId = tenant.Id, Name = "HR Manager", NormalizedName = "HR MANAGER", IsActive = true };
        var plainRole = new Role { TenantId = tenant.Id, Name = "Manager", NormalizedName = "MANAGER", IsActive = true };
        var perms = new[] { "employees.documents", "employees.sensitive", "employees.read" }.Select(k => new Permission { Key = k, Module = "Employees", Description = k }).ToList();
        db.AddRange(tenant, a, b, mayaUser, maya, docsRole, sensRole, plainRole);
        db.Permissions.AddRange(perms);
        await db.SaveChangesAsync();
        db.RolePermissions.AddRange(
            new RolePermission { RoleId = docsRole.Id, PermissionId = perms[0].Id },
            new RolePermission { RoleId = sensRole.Id, PermissionId = perms[0].Id }, new RolePermission { RoleId = sensRole.Id, PermissionId = perms[1].Id },
            new RolePermission { RoleId = plainRole.Id, PermissionId = perms[2].Id });
        User U(string n) => new() { TenantId = tenant.Id, Email = $"{n}@x.test", NormalizedEmail = $"{n}@X.TEST", FullName = n, IsActive = true };
        var hr = U("hr-in"); var hrs = U("hr-sens"); var hrOther = U("hr-other"); var mgr = U("manager");
        db.Users.AddRange(hr, hrs, hrOther, mgr);
        await db.SaveChangesAsync();
        db.UserRoles.AddRange(new UserRole { UserId = hr.Id, RoleId = docsRole.Id }, new UserRole { UserId = hrs.Id, RoleId = sensRole.Id },
            new UserRole { UserId = hrOther.Id, RoleId = docsRole.Id }, new UserRole { UserId = mgr.Id, RoleId = plainRole.Id });
        db.UserEntityAccesses.AddRange(
            new UserEntityAccess { TenantId = tenant.Id, UserId = hr.Id, CompanyId = a.Id }, new UserEntityAccess { TenantId = tenant.Id, UserId = hrs.Id, CompanyId = a.Id },
            new UserEntityAccess { TenantId = tenant.Id, UserId = hrOther.Id, CompanyId = b.Id }, new UserEntityAccess { TenantId = tenant.Id, UserId = mgr.Id, CompanyId = a.Id });
        await db.SaveChangesAsync();
        return new World { Db = db, TenantId = tenant.Id, CompanyA = a, Maya = maya, MayaLogin = mayaUser.Id, HrInScope = hr.Id, HrWithSensitive = hrs.Id, HrOtherCompany = hrOther.Id, ManagerNoPermission = mgr.Id };
    }

    private static EmployeeDocument Doc(World w, Employee e, string type, string category, string status = "Pending", DateTime? uploaded = null, string scan = "clean", string? section = null, bool deleted = false) => new()
    {
        TenantId = w.TenantId, CompanyId = e.CompanyId, EmployeeId = e.Id, DocumentType = type, DocumentCategory = category, FileName = $"{type}.pdf",
        ContentType = "application/pdf", StorageUrl = $"{w.TenantId:N}/documents/{type}", ApprovalStatus = status,
        UploadedAtUtc = uploaded ?? DateTime.UtcNow, ScanStatus = scan, SourceSection = section, IsDeleted = deleted,
    };

    private static EmployeeManagementService Svc(World w, INotificationService? notifications = null) =>
        new(w.Db, new AuditService(w.Db), new MemoryStorage(), notifications ?? new Recorder());

    private static RequestContext Ctx(World w, params string[] permissions) => new("127.0.0.1", "test", w.HrInScope, w.TenantId, Permissions: permissions);

    // ── the queue ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_queue_lists_only_what_is_waiting_in_scope_oldest_first_and_hides_protected_documents_from_a_caller_without_employees_sensitive()
    {
        var w = await SeedAsync();
        var otherCompanyEmployee = new Employee { TenantId = w.TenantId, CompanyId = Guid.NewGuid(), EmployeeCode = "E-OTHER", FullName = "Other Co", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1) };
        w.Db.Employees.Add(otherCompanyEmployee);
        await w.Db.SaveChangesAsync();
        var now = DateTime.UtcNow;
        w.Db.EmployeeDocuments.AddRange(
            Doc(w, w.Maya, "Medical certificate", "General", uploaded: now.AddDays(-1)),
            Doc(w, w.Maya, "Degree", "Education", uploaded: now.AddDays(-3)),
            Doc(w, w.Maya, "Passport", EmployeeDocumentSensitivity.Identity, uploaded: now.AddDays(-2)),
            Doc(w, w.Maya, "Already verified", "General", status: "Verified"),
            Doc(w, w.Maya, "Already rejected", "General", status: "Rejected"),
            Doc(w, w.Maya, "Deleted", "General", deleted: true),
            Doc(w, w.Maya, "Quarantined", "General", scan: "quarantined"),
            Doc(w, w.Maya, "Completion upload", "General", section: "id"),
            Doc(w, otherCompanyEmployee, "Out of scope", "General"));
        await w.Db.SaveChangesAsync();
        IReadOnlyCollection<int> visible = [w.Maya.Id];

        var plain = await Svc(w).PendingDocumentsAsync(w.TenantId, visible, includeProtected: false, page: 1, pageSize: 25, default);
        plain.Total.Should().Be(2);
        plain.Items.Select(i => i.DocumentType).Should().Equal("Degree", "Medical certificate");   // oldest first

        var withSensitive = await Svc(w).PendingDocumentsAsync(w.TenantId, visible, includeProtected: true, page: 1, pageSize: 25, default);
        withSensitive.Items.Select(i => i.DocumentType).Should().Equal("Degree", "Passport", "Medical certificate");
        var row = withSensitive.Items.Single(i => i.DocumentType == "Passport");
        (row.EmployeeId, row.EmployeeName, row.EmployeeCode, row.IsProtected).Should().Be((w.Maya.Id, "Maya Ahmed", "E-MAYA", true));

        var unrestricted = await Svc(w).PendingDocumentsAsync(w.TenantId, null, includeProtected: false, page: 1, pageSize: 25, default);
        unrestricted.Items.Select(i => i.DocumentType).Should().Contain("Out of scope").And.NotContain("Passport");

        var paged = await Svc(w).PendingDocumentsAsync(w.TenantId, visible, includeProtected: true, page: 2, pageSize: 2, default);
        (paged.Total, paged.Items.Count).Should().Be((3, 1));
    }

    // ── verify / reject ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rejecting_stores_the_reason_clears_any_earlier_verification_and_tells_every_login_of_the_employee()
    {
        var w = await SeedAsync();
        var doc = Doc(w, w.Maya, "Medical certificate", "General", status: "Verified");
        doc.VerifiedAtUtc = DateTime.UtcNow; doc.VerifiedBy = w.HrInScope;
        w.Db.EmployeeDocuments.Add(doc);
        var extraLogin = new User { TenantId = w.TenantId, Email = "maya2@x.test", NormalizedEmail = "MAYA2@X.TEST", FullName = "Maya 2", IsActive = true };
        w.Db.Users.Add(extraLogin);
        await w.Db.SaveChangesAsync();
        w.Db.EmployeeUserAccounts.Add(new EmployeeUserAccount { TenantId = w.TenantId, EmployeeId = w.Maya.Id, UserId = extraLogin.Id });
        await w.Db.SaveChangesAsync();
        var notices = new Recorder();

        var rejected = await Svc(w, notices).RejectDocumentAsync(w.TenantId, w.Maya.Id, doc.Id, "  The scan is cut off on the right  ", Ctx(w, "employees.documents"), default);

        rejected!.ApprovalStatus.Should().Be("Rejected");
        rejected.RejectionReason.Should().Be("The scan is cut off on the right");
        rejected.VerifiedAtUtc.Should().BeNull(); rejected.VerifiedBy.Should().BeNull();
        notices.Sent.Select(n => n.UserId).Should().BeEquivalentTo([w.MayaLogin, extraLogin.Id]);
        notices.Sent.Should().OnlyContain(n => n.Message.Contains("The scan is cut off on the right"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rejecting_without_a_reason_is_refused_and_changes_nothing(string reason)
    {
        var w = await SeedAsync();
        var doc = Doc(w, w.Maya, "Medical certificate", "General");
        w.Db.EmployeeDocuments.Add(doc);
        await w.Db.SaveChangesAsync();

        await FluentActions.Awaiting(() => Svc(w).RejectDocumentAsync(w.TenantId, w.Maya.Id, doc.Id, reason, Ctx(w, "employees.documents"), default))
            .Should().ThrowAsync<InvalidOperationException>();

        w.Db.ChangeTracker.Clear();
        (await w.Db.EmployeeDocuments.AsNoTracking().SingleAsync(d => d.Id == doc.Id)).ApprovalStatus.Should().Be("Pending");
    }

    [Fact]
    public async Task Verifying_a_document_that_was_rejected_before_clears_the_reason()
    {
        var w = await SeedAsync();
        var doc = Doc(w, w.Maya, "Medical certificate", "General", status: "Rejected");
        doc.RejectionReason = "Blurry";
        w.Db.EmployeeDocuments.Add(doc);
        await w.Db.SaveChangesAsync();

        var verified = await Svc(w).VerifyDocumentAsync(w.TenantId, w.Maya.Id, doc.Id, null, Ctx(w, "employees.documents"), default);

        verified!.ApprovalStatus.Should().Be("Verified");
        verified.RejectionReason.Should().BeNull();
        verified.VerifiedBy.Should().Be(w.HrInScope);
    }

    [Fact]
    public async Task A_protected_document_still_needs_employees_sensitive_to_reject()
    {
        var w = await SeedAsync();
        var doc = Doc(w, w.Maya, "Passport", EmployeeDocumentSensitivity.Identity);
        w.Db.EmployeeDocuments.Add(doc);
        await w.Db.SaveChangesAsync();

        await FluentActions.Awaiting(() => Svc(w).RejectDocumentAsync(w.TenantId, w.Maya.Id, doc.Id, "Expired", Ctx(w, "employees.documents"), default))
            .Should().ThrowAsync<SensitiveDocumentAccessException>();
    }

    // ── HR is told of an upload ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_employee_upload_tells_the_verifiers_of_that_company_and_never_the_employee_or_other_companies()
    {
        var w = await SeedAsync();
        var notices = new Recorder();
        var doc = Doc(w, w.Maya, "Medical certificate", "General");
        w.Db.EmployeeDocuments.Add(doc);
        await w.Db.SaveChangesAsync();

        var told = await DocumentVerificationNotices.NotifyUploadedAsync(w.Db, notices, doc, w.Maya.FullName, default);

        told.Should().Be(2);
        notices.Sent.Select(n => n.UserId).Should().BeEquivalentTo([w.HrInScope, w.HrWithSensitive]);
        notices.Sent.Should().NotContain(n => n.UserId == w.HrOtherCompany || n.UserId == w.ManagerNoPermission || n.UserId == w.MayaLogin);
        var notice = notices.Sent.First();
        notice.Title.Should().Contain("Document to verify");
        notice.Message.Should().Contain("Maya Ahmed").And.Contain("Medical certificate");
        (notice.EntityName, notice.EntityId).Should().Be((nameof(EmployeeDocument), doc.Id.ToString()));
    }

    [Fact]
    public async Task A_protected_upload_tells_only_the_verifiers_who_also_hold_employees_sensitive()
    {
        var w = await SeedAsync();
        var notices = new Recorder();
        var doc = Doc(w, w.Maya, "Passport", EmployeeDocumentSensitivity.Identity);
        w.Db.EmployeeDocuments.Add(doc);
        await w.Db.SaveChangesAsync();

        await DocumentVerificationNotices.NotifyUploadedAsync(w.Db, notices, doc, w.Maya.FullName, default);

        notices.Sent.Select(n => n.UserId).Should().BeEquivalentTo([w.HrWithSensitive], "the verify endpoint refuses a protected document without employees.sensitive");
    }

    [Fact]
    public async Task The_employee_self_service_upload_raises_the_notice_and_the_employee_list_shows_status_and_reason()
    {
        var w = await SeedAsync();
        var storage = new MemoryStorage();
        var notices = new Recorder();
        var ess = new EmployeeSelfServiceController(
            w.Db, new StubLetters(), new PdfRenderGate(1),
            new Zayra.Api.Infrastructure.Leave.LeaveService(w.Db, new Zayra.Api.Infrastructure.Approvals.ApprovalRouter(w.Db)),
            new Zayra.Api.Infrastructure.Attendance.AttendanceService(w.Db, notices, new StubHttpClientFactory()),
            new HrLetterIssuer(w.Db, new StubLetters(), storage), storage, null, null, notices)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim("tenant_id", w.TenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, w.MayaLogin.ToString()),
                        new Claim("employee_id", w.Maya.Id.ToString()), new Claim("permission", "ess.read"), new Claim("permission", "ess.write"),
                    ], "Test")),
                },
            },
        };
        var pdf = "%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF"u8.ToArray();
        var file = new FormFile(new MemoryStream(pdf), 0, pdf.Length, "file", "cert.pdf") { Headers = new HeaderDictionary(), ContentType = "application/pdf" };

        var created = await ess.UploadDocumentFile(new EssDocumentUploadForm { File = file, DocumentType = "Medical certificate" }, default);

        created.Result.Should().BeOfType<CreatedResult>();
        notices.Sent.Where(n => n.EntityName == nameof(EmployeeDocument)).Select(n => n.UserId).Should().BeEquivalentTo([w.HrInScope, w.HrWithSensitive]);

        var stored = await w.Db.EmployeeDocuments.SingleAsync(d => d.DocumentType == "Medical certificate");
        stored.ApprovalStatus = "Rejected"; stored.RejectionReason = "Not legible";
        w.Db.EmployeeDocuments.Add(Doc(w, w.Maya, "Degree", "Education", status: "Verified"));
        await w.Db.SaveChangesAsync();

        var list = await ess.Documents(default);

        var rows = list.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeAssignableTo<IReadOnlyCollection<ESSDocumentDto>>().Subject;
        rows.Single(r => r.DocumentType == "Medical certificate").Should().Match<ESSDocumentDto>(r => r.ApprovalStatus == "Rejected" && r.RejectionReason == "Not legible");
        rows.Single(r => r.DocumentType == "Degree").Should().Match<ESSDocumentDto>(r => r.ApprovalStatus == "Verified" && r.RejectionReason == null);
    }

    // ── separation of duties: the subject never decides, and neither does the uploader ──────────

    private static RequestContext CtxAs(World w, Guid userId) => new("127.0.0.1", "test", userId, w.TenantId, Permissions: ["employees.documents"]);

    [Fact]
    public async Task The_employee_whose_document_it_is_can_neither_verify_nor_reject_it_even_holding_the_permission()
    {
        var w = await SeedAsync();
        var doc = Doc(w, w.Maya, "Medical certificate", "General");
        w.Db.EmployeeDocuments.Add(doc);
        await w.Db.SaveChangesAsync();

        // Maya's own login, with employees.documents on her token, is still the subject.
        await FluentActions.Awaiting(() => Svc(w).VerifyDocumentAsync(w.TenantId, w.Maya.Id, doc.Id, null, CtxAs(w, w.MayaLogin), default))
            .Should().ThrowAsync<DocumentSelfVerificationException>();
        await FluentActions.Awaiting(() => Svc(w).RejectDocumentAsync(w.TenantId, w.Maya.Id, doc.Id, "Blurry", CtxAs(w, w.MayaLogin), default))
            .Should().ThrowAsync<DocumentSelfVerificationException>();

        w.Db.ChangeTracker.Clear();
        (await w.Db.EmployeeDocuments.AsNoTracking().SingleAsync(d => d.Id == doc.Id)).ApprovalStatus.Should().Be("Pending");
    }

    [Fact]
    public async Task The_user_who_uploaded_the_document_can_neither_verify_nor_reject_it_but_a_third_user_can()
    {
        var w = await SeedAsync();
        var doc = Doc(w, w.Maya, "Medical certificate", "General");
        doc.UploadedBy = w.HrInScope;
        w.Db.EmployeeDocuments.Add(doc);
        await w.Db.SaveChangesAsync();

        await FluentActions.Awaiting(() => Svc(w).VerifyDocumentAsync(w.TenantId, w.Maya.Id, doc.Id, null, CtxAs(w, w.HrInScope), default))
            .Should().ThrowAsync<DocumentSelfVerificationException>();
        await FluentActions.Awaiting(() => Svc(w).RejectDocumentAsync(w.TenantId, w.Maya.Id, doc.Id, "Blurry", CtxAs(w, w.HrInScope), default))
            .Should().ThrowAsync<DocumentSelfVerificationException>();

        var verified = await Svc(w).VerifyDocumentAsync(w.TenantId, w.Maya.Id, doc.Id, null, CtxAs(w, w.HrWithSensitive), default);
        verified!.ApprovalStatus.Should().Be("Verified");
        verified.VerifiedBy.Should().Be(w.HrWithSensitive);
    }

    [Fact]
    public void The_refusal_carries_a_stable_code_the_screen_can_show()
    {
        DocumentSelfVerificationException.Code.Should().Be("document_self_verification");
        new DocumentSelfVerificationException().Message.Should().Contain("Someone else");
    }

    [Fact]
    public async Task The_document_projection_says_whether_a_document_is_protected_even_when_its_type_is_masked()
    {
        var w = await SeedAsync();
        var passport = Doc(w, w.Maya, "Passport", string.Empty);
        var neutral = Doc(w, w.Maya, "Medical certificate", "General");

        var masked = EmployeeDocumentDto.Project(passport, includeSensitive: false);

        masked.DocumentType.Should().Be("Sensitive document");
        masked.IsProtected.Should().BeTrue("the screen must not offer Verify to someone the server will refuse");
        EmployeeDocumentDto.Project(neutral, includeSensitive: false).IsProtected.Should().BeFalse();
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }

    private sealed class StubLetters : ILetterService
    {
        public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken ct = default) => Task.FromResult(new byte[] { 1 });
        public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    }
}
