using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Security review M2. Download of an identity / payroll / contract document needs employees.sensitive, but the
/// metadata routes only needed employees.documents: that user could re-type a protected document to a neutral
/// category and then download it. Update, verify, reject and archive of a protected document, and moving a document
/// INTO or OUT OF a protected category, now need employees.sensitive too.
/// </summary>
public class EmployeeDocumentProtectedMutationTests
{
    private static readonly string[] Plain = ["employees.documents"];
    private static readonly string[] WithSensitive = ["employees.documents", "employees.sensitive"];

    private sealed record World(ZayraDbContext Db, Guid TenantId, Employee Employee, EmployeeDocument Protected, EmployeeDocument Neutral, EmployeeDocument Contract);

    private static async Task<World> SeedAsync()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase($"doc-prot-{Guid.NewGuid():N}").Options);
        var tenantId = Guid.NewGuid();
        var employee = new Employee { TenantId = tenantId, EmployeeCode = "E1", FullName = "Doc Owner", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1) };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        EmployeeDocument Doc(string type, string category) => new()
        {
            TenantId = tenantId, EmployeeId = employee.Id, DocumentType = type, DocumentCategory = category,
            FileName = "f.pdf", ContentType = "application/pdf", StorageUrl = "storage/x",
        };
        var prot = Doc("Passport", EmployeeDocumentSensitivity.Identity);
        var neutral = Doc("Misc note", "General");
        var contract = Doc("Employment Contract", EmployeeDocumentSensitivity.Contract);
        db.EmployeeDocuments.AddRange(prot, neutral, contract);
        await db.SaveChangesAsync();
        return new World(db, tenantId, employee, prot, neutral, contract);
    }

    private static EmployeeManagementService Svc(World w) =>
        new(w.Db, new AuditService(w.Db), new NullStorage(), new NullNotifications());

    private static RequestContext Ctx(World w, string[] permissions) =>
        new("127.0.0.1", "test", Guid.NewGuid(), w.TenantId, Permissions: permissions);

    private static UpdateDocumentMetadataRequest Update(string? type = null, string? category = null, string? notes = null) =>
        new(type, category, null, null, null, null, notes);

    // ── update: out of / within a protected category ────────────────────────────────────────────

    [Fact]
    public async Task Retyping_a_protected_document_to_a_neutral_category_needs_employees_sensitive()
    {
        var w = await SeedAsync();

        await FluentActions.Awaiting(() => Svc(w).UpdateDocumentAsync(w.TenantId, w.Employee.Id, w.Protected.Id,
                Update(type: "Misc note", category: "General"), Ctx(w, Plain), default))
            .Should().ThrowAsync<SensitiveDocumentAccessException>();

        w.Db.ChangeTracker.Clear();
        var stored = await w.Db.EmployeeDocuments.AsNoTracking().SingleAsync(x => x.Id == w.Protected.Id);
        stored.DocumentType.Should().Be("Passport");
        stored.DocumentCategory.Should().Be(EmployeeDocumentSensitivity.Identity);
    }

    [Fact]
    public async Task Editing_any_metadata_of_a_protected_document_needs_employees_sensitive()
    {
        var w = await SeedAsync();

        foreach (var id in new[] { w.Protected.Id, w.Contract.Id })
            await FluentActions.Awaiting(() => Svc(w).UpdateDocumentAsync(w.TenantId, w.Employee.Id, id,
                    Update(notes: "edited"), Ctx(w, Plain), default))
                .Should().ThrowAsync<SensitiveDocumentAccessException>();
    }

    [Fact]
    public async Task Retyping_a_protected_document_with_employees_sensitive_is_allowed()
    {
        var w = await SeedAsync();

        var updated = await Svc(w).UpdateDocumentAsync(w.TenantId, w.Employee.Id, w.Protected.Id,
            Update(type: "Misc note", category: "General"), Ctx(w, WithSensitive), default);

        updated!.DocumentType.Should().Be("Misc note");
    }

    // ── update: INTO a protected category ───────────────────────────────────────────────────────

    [Fact]
    public async Task Moving_a_neutral_document_into_a_protected_category_needs_employees_sensitive()
    {
        var w = await SeedAsync();

        await FluentActions.Awaiting(() => Svc(w).UpdateDocumentAsync(w.TenantId, w.Employee.Id, w.Neutral.Id,
                Update(type: "Passport"), Ctx(w, Plain), default))
            .Should().ThrowAsync<SensitiveDocumentAccessException>();
        await FluentActions.Awaiting(() => Svc(w).UpdateDocumentAsync(w.TenantId, w.Employee.Id, w.Neutral.Id,
                Update(category: EmployeeDocumentSensitivity.Payroll), Ctx(w, Plain), default))
            .Should().ThrowAsync<SensitiveDocumentAccessException>();

        w.Db.ChangeTracker.Clear();
        (await w.Db.EmployeeDocuments.AsNoTracking().SingleAsync(x => x.Id == w.Neutral.Id)).DocumentType.Should().Be("Misc note");

        var moved = await Svc(w).UpdateDocumentAsync(w.TenantId, w.Employee.Id, w.Neutral.Id,
            Update(type: "Passport"), Ctx(w, WithSensitive), default);
        moved!.DocumentCategory.Should().Be(EmployeeDocumentSensitivity.Identity);
    }

    [Fact]
    public async Task Editing_a_neutral_document_stays_open_to_employees_documents()
    {
        var w = await SeedAsync();

        var updated = await Svc(w).UpdateDocumentAsync(w.TenantId, w.Employee.Id, w.Neutral.Id,
            Update(notes: "checked"), Ctx(w, Plain), default);

        updated!.Notes.Should().Be("checked");
    }

    [Fact]
    public async Task A_caller_with_no_permission_context_is_treated_as_lacking_employees_sensitive()
    {
        var w = await SeedAsync();

        await FluentActions.Awaiting(() => Svc(w).UpdateDocumentAsync(w.TenantId, w.Employee.Id, w.Protected.Id,
                Update(notes: "x"), new RequestContext("127.0.0.1", "test", Guid.NewGuid(), w.TenantId), default))
            .Should().ThrowAsync<SensitiveDocumentAccessException>();
    }

    // ── verify / reject / archive ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Verify_reject_and_archive_of_a_protected_document_need_employees_sensitive()
    {
        var w = await SeedAsync();
        var svc = Svc(w);
        var plain = Ctx(w, Plain);

        await FluentActions.Awaiting(() => svc.VerifyDocumentAsync(w.TenantId, w.Employee.Id, w.Protected.Id, null, plain, default))
            .Should().ThrowAsync<SensitiveDocumentAccessException>();
        await FluentActions.Awaiting(() => svc.RejectDocumentAsync(w.TenantId, w.Employee.Id, w.Contract.Id, "no", plain, default))
            .Should().ThrowAsync<SensitiveDocumentAccessException>();
        await FluentActions.Awaiting(() => svc.ArchiveDocumentAsync(w.TenantId, w.Employee.Id, w.Protected.Id, plain, default))
            .Should().ThrowAsync<SensitiveDocumentAccessException>();

        w.Db.ChangeTracker.Clear();
        var stored = await w.Db.EmployeeDocuments.AsNoTracking().SingleAsync(x => x.Id == w.Protected.Id);
        stored.IsDeleted.Should().BeFalse();
        stored.ApprovalStatus.Should().Be("Pending");
    }

    [Fact]
    public async Task Verify_and_archive_of_a_protected_document_work_with_employees_sensitive()
    {
        var w = await SeedAsync();
        var svc = Svc(w);
        var full = Ctx(w, WithSensitive);

        (await svc.VerifyDocumentAsync(w.TenantId, w.Employee.Id, w.Protected.Id, null, full, default))!.ApprovalStatus.Should().Be("Verified");
        (await svc.ArchiveDocumentAsync(w.TenantId, w.Employee.Id, w.Protected.Id, full, default)).Should().BeTrue();
    }

    [Fact]
    public async Task Verify_reject_and_archive_of_a_neutral_document_stay_open_to_employees_documents()
    {
        var w = await SeedAsync();
        var svc = Svc(w);
        var plain = Ctx(w, Plain);

        (await svc.VerifyDocumentAsync(w.TenantId, w.Employee.Id, w.Neutral.Id, null, plain, default))!.ApprovalStatus.Should().Be("Verified");
        (await svc.RejectDocumentAsync(w.TenantId, w.Employee.Id, w.Neutral.Id, "blurry", plain, default))!.ApprovalStatus.Should().Be("Rejected");
        (await svc.ArchiveDocumentAsync(w.TenantId, w.Employee.Id, w.Neutral.Id, plain, default)).Should().BeTrue();
    }

    private sealed class NullStorage : Zayra.Api.Infrastructure.Documents.IDocumentStorage
    {
        public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) =>
            Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument(file.FileName, file.ContentType, "storage/test", "/tmp/test"));
        public string ResolvePath(string storageUrl) => "/tmp/test";
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    }

    private sealed class NullNotifications : INotificationService
    {
        public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string en, string? eid, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
    }
}
