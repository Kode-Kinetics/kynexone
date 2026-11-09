using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Leave Batch 1, item 4 (security-sensitive; in memory, no web host). A leave request holds the STORAGE KEY of its
/// attachment (a sick note, often medical). The leave List/Get returned the whole entity, key included, to anyone who
/// could read the leave (a manager, an organisation-wide reader), and the legacy <c>POST /api/ess/documents/upload</c>
/// registered ANY tenant-prefixed key as a document of the caller's, which the caller could then download. Together:
/// read a colleague's medical certificate. Now the key is shown only to the leave's employee and HR/Admin, and the
/// legacy upload refuses a key another employee's document or leave already holds.
/// </summary>
public sealed class LeaveAttachmentKeyTests
{
    private static string Key(LeaveBatch1World w, string file = "sicknote.pdf") =>
        $"storage/documents/{w.TenantId:N}/{Guid.NewGuid():N}_{file}";

    private static string? KeyOf(object? leave) => (string?)leave!.GetType().GetProperty("AttachmentPath")!.GetValue(leave);

    // ── The leave payload ───────────────────────────────────────────────────────────────────────

    public static TheoryData<string, string, bool> Readers => new()
    {
        // who, role, sees the key
        { "employee", "Employee", true },
        { "hr", "HR Manager", true },
        { "admin", "Admin", true },
        { "hrofficer", "HR Officer", true },
        { "manager", "Manager", false },
        { "auditor", "Auditor", false },
        { "recruiter", "Recruiter", false },
    };

    private static (DataScope Scope, ClaimsPrincipal User) Reader(LeaveBatch1World w, string who, string role) => who switch
    {
        "employee" => (w.OwnScope(w.Emp), w.User(w.EmpUserId, role, "leave.read", "ess.read")),
        "manager" => (w.TeamScope(w.Boss, w.Emp), w.User(w.BossUserId, role, "leave.read", "manager.read")),
        _ => (LeaveBatch1World.OrgScope(), w.User(Guid.NewGuid(), role, "leave.read", "employees.read")),
    };

    [Theory]
    [MemberData(nameof(Readers))]
    public async Task The_leave_list_returns_the_attachment_key_only_to_the_employee_and_hr(string who, string role, bool seesKey)
    {
        var w = await LeaveBatch1World.CreateAsync();
        var key = Key(w);
        var day = w.NextWorkday();
        var leave = await w.SeedAsync("Approved", day, day, attachmentPath: key);
        var (scope, user) = Reader(w, who, role);

        var result = await w.Leave(scope, user).List(null, null, null, null, null, null, null, null, 1, 100, CancellationToken.None);

        var items = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<PagedResult<LeaveRequest>>().Subject.Items;
        var listed = items.Single(i => i.Id == leave.Id);
        listed.AttachmentPath.Should().Be(seesKey ? key : string.Empty);
        // Hiding the key from this reader must never erase it from the row.
        await w.Db.SaveChangesAsync();
        (await w.Db.LeaveRequests.AsNoTracking().SingleAsync(r => r.Id == leave.Id)).AttachmentPath.Should().Be(key);
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public async Task The_leave_detail_returns_the_attachment_key_only_to_the_employee_and_hr(string who, string role, bool seesKey)
    {
        var w = await LeaveBatch1World.CreateAsync();
        var key = Key(w);
        var day = w.NextWorkday();
        var leave = await w.SeedAsync("Approved", day, day, attachmentPath: key);
        var (scope, user) = Reader(w, who, role);

        var result = await w.Leave(scope, user).Get(leave.Id, CancellationToken.None);

        var body = result.Should().BeOfType<OkObjectResult>().Subject.Value;
        KeyOf(body!.GetType().GetProperty("request")!.GetValue(body)).Should().Be(seesKey ? key : string.Empty);
        await w.Db.SaveChangesAsync();
        (await w.Db.LeaveRequests.AsNoTracking().SingleAsync(r => r.Id == leave.Id)).AttachmentPath.Should().Be(key);
    }

    [Fact]
    public async Task The_legacy_leave_list_does_not_hand_a_manager_the_attachment_key()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var key = Key(w);
        var day = w.NextWorkday();
        var leave = await w.SeedAsync("Approved", day, day, attachmentPath: key);
        var controller = new LeaveController(w.Db, new LeaveBatch1World.FixedScope(w.TeamScope(w.Boss, w.Emp)))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = w.User(w.BossUserId, "Manager", "leave.read", "manager.read") },
            },
        };

        var result = await controller.List(null, null, null, 1, 100, CancellationToken.None);

        var items = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<PagedResult<LeaveRequest>>().Subject.Items;
        items.Single(i => i.Id == leave.Id).AttachmentPath.Should().BeEmpty();
    }

    // ── The legacy ESS upload ───────────────────────────────────────────────────────────────────

    private static EmployeeSelfServiceController Ess(LeaveBatch1World w, Employee who, Guid userId)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", w.TenantId.ToString()), new(ClaimTypes.NameIdentifier, userId.ToString()),
            new("employee_id", who.Id.ToString()), new("permission", "ess.read"), new("permission", "ess.write"),
        };
        return new EmployeeSelfServiceController(
            w.Db, new StubLetters(), new PdfRenderGate(1), w.Service,
            new Zayra.Api.Infrastructure.Attendance.AttendanceService(w.Db, new Quiet(), new StubHttpClients()),
            new HrLetterIssuer(w.Db, new StubLetters(), new NoStorage()), new NoStorage(), null)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
            },
        };
    }

    private static ESSDocumentUploadDto Upload(string key) => new("Other", "x.pdf", "application/pdf", key, null, false);

    private static async Task<IActionResult?> RegisterAsync(EmployeeSelfServiceController ess, string key) =>
        (await ess.UploadDocument(Upload(key), CancellationToken.None)).Result;

    [Fact]
    public async Task A_colleague_cannot_register_a_storage_key_a_leave_request_holds()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var key = Key(w);
        var day = w.NextWorkday();
        await w.SeedAsync("Approved", day, day, attachmentPath: key);

        var result = await RegisterAsync(Ess(w, w.Other, w.OtherUserId), key);

        result.Should().BeOfType<BadRequestObjectResult>();
        (await w.Db.EmployeeDocuments.CountAsync(d => d.EmployeeId == w.Other.Id)).Should().Be(0);
    }

    [Fact]
    public async Task A_colleague_cannot_register_a_storage_key_another_employees_document_holds()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var key = Key(w, "passport.pdf");
        w.Db.EmployeeDocuments.Add(new EmployeeDocument
        {
            TenantId = w.TenantId, CompanyId = w.CompanyId, EmployeeId = w.Emp.Id, DocumentType = "Medical", FileName = "m.pdf",
            ContentType = "application/pdf", StorageUrl = key, ApprovalStatus = "Approved",
        });
        await w.Db.SaveChangesAsync();

        var result = await RegisterAsync(Ess(w, w.Other, w.OtherUserId), key);

        result.Should().BeOfType<BadRequestObjectResult>();
        (await w.Db.EmployeeDocuments.CountAsync(d => d.EmployeeId == w.Other.Id)).Should().Be(0);
    }

    [Fact]
    public async Task A_key_held_by_a_deleted_document_or_a_document_with_no_owner_is_refused_too()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var deleted = Key(w, "old.pdf");
        var company = Key(w, "policy.pdf");
        w.Db.EmployeeDocuments.AddRange(
            new EmployeeDocument { TenantId = w.TenantId, EmployeeId = w.Emp.Id, DocumentType = "Medical", FileName = "o.pdf", StorageUrl = deleted, IsDeleted = true },
            new EmployeeDocument { TenantId = w.TenantId, EmployeeId = null, DocumentType = "Policy", FileName = "p.pdf", StorageUrl = company });
        await w.Db.SaveChangesAsync();
        var ess = Ess(w, w.Other, w.OtherUserId);

        (await RegisterAsync(ess, deleted)).Should().BeOfType<BadRequestObjectResult>();
        (await RegisterAsync(ess, company)).Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData("dot-segment")]
    [InlineData("double-slash")]
    [InlineData("upper-case")]
    [InlineData("backslash")]
    public async Task A_spelling_variant_of_a_held_key_is_refused_too(string variant)
    {
        var w = await LeaveBatch1World.CreateAsync();
        var name = $"{Guid.NewGuid():N}_variant.pdf";
        var key = $"storage/documents/{w.TenantId:N}/{name}";
        var day = w.NextWorkday();
        await w.SeedAsync("Approved", day, day, attachmentPath: key);
        var candidate = variant switch
        {
            "dot-segment" => $"storage/documents/{w.TenantId:N}/./{name}",
            "double-slash" => $"storage/documents/{w.TenantId:N}//{name}",
            "upper-case" => key.ToUpperInvariant(),
            _ => key.Replace('/', '\\'),
        };

        (await RegisterAsync(Ess(w, w.Other, w.OtherUserId), candidate)).Should().BeOfType<BadRequestObjectResult>();
        (await w.Db.EmployeeDocuments.CountAsync(d => d.EmployeeId == w.Other.Id)).Should().Be(0);
    }

    [Fact]
    public async Task A_key_nothing_else_holds_is_still_accepted_so_the_endpoint_keeps_working()
    {
        var w = await LeaveBatch1World.CreateAsync();

        var result = await RegisterAsync(Ess(w, w.Other, w.OtherUserId), Key(w, "fresh.pdf"));

        result.Should().BeOfType<CreatedResult>();
    }

    [Fact]
    public async Task The_owner_can_register_their_own_key_again()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var key = Key(w);
        var day = w.NextWorkday();
        await w.SeedAsync("Approved", day, day, attachmentPath: key);

        (await RegisterAsync(Ess(w, w.Emp, w.EmpUserId), key)).Should().BeOfType<CreatedResult>();
    }

    [Fact]
    public async Task A_key_outside_the_tenant_prefix_is_still_refused()
    {
        var w = await LeaveBatch1World.CreateAsync();

        (await RegisterAsync(Ess(w, w.Other, w.OtherUserId), $"storage/documents/{Guid.NewGuid():N}/x.pdf")).Should().BeOfType<BadRequestObjectResult>();
    }

    // ── Stubs ───────────────────────────────────────────────────────────────────────────────────

    private sealed class NoStorage : IDocumentStorage
    {
        public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) => throw new NotSupportedException();
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => throw new FileNotFoundException(storageUrl);
        public string ResolvePath(string storageUrl) => storageUrl;
    }

    private sealed class StubLetters : ILetterService
    {
        public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken ct = default) => Task.FromResult(new byte[] { 1 });
        public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    }

    private sealed class Quiet : INotificationService
    {
        public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string entity, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StubHttpClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
