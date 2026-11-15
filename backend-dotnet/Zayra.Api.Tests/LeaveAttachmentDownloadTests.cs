using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// <c>GET /api/leave/requests/{id}/attachment</c>: the approver screens can open a leave's supporting document. The
/// visibility rule is the existing one (<c>LeaveFileKeyExposure</c>): the leave's own employee and Admin / HR Manager /
/// HR Officer. Everyone else, another tenant, a leave with no attachment and a deleted document all get 404, so the
/// endpoint never confirms that a medical document exists.
/// </summary>
public sealed class LeaveAttachmentDownloadTests
{
    private static readonly byte[] Bytes = [0x25, 0x50, 0x44, 0x46, 0x2d];

    private sealed class MemoryStorage(Guid tenantId, string key) : IDocumentStorage
    {
        public Task<StoredDocument> SaveAsync(Guid t, IFormFile file, CancellationToken ct) => throw new NotSupportedException();
        public Task<byte[]> GetBytesAsync(Guid t, string storageUrl, CancellationToken ct = default)
        {
            if (t != tenantId) throw new InvalidOperationException("Cross-tenant storage access denied.");
            return storageUrl == key ? Task.FromResult(Bytes) : throw new FileNotFoundException(storageUrl);
        }
        public string ResolvePath(string storageUrl) => storageUrl;
    }

    private static async Task<(LeaveBatch1World W, LeaveRequest Leave, EmployeeDocument Doc, string Key)> SeedAsync(
        string fileName = "sick note.pdf", bool withDocument = true, bool attach = true)
    {
        var w = await LeaveBatch1World.CreateAsync();
        var key = $"{w.TenantId:N}/documents/{Guid.NewGuid():N}_note.pdf";
        var doc = new EmployeeDocument
        {
            TenantId = w.TenantId, CompanyId = w.CompanyId, EmployeeId = w.Emp.Id, DocumentType = "Leave Attachment",
            FileName = fileName, ContentType = "application/pdf", StorageUrl = key, ScanStatus = "clean",
        };
        if (withDocument) w.Db.EmployeeDocuments.Add(doc);
        var day = w.NextWorkday();
        var leave = await w.SeedAsync("PendingHrApproval", day, day, attachmentPath: attach ? key : string.Empty);
        return (w, leave, doc, key);
    }

    private static LeaveRequestsController Controller(LeaveBatch1World w, string key, ClaimsPrincipal user, Zayra.Api.Application.Common.DataScope scope,
        IAuditService? audit = null) =>
        new(w.Db, w.Service, new LeaveBatch1World.FixedScope(scope), w.Notices, new MemoryStorage(w.TenantId, key), audit ?? new AuditService(w.Db))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } },
        };

    [Fact]
    public async Task The_leaves_own_employee_gets_the_document()
    {
        var (w, leave, _, key) = await SeedAsync();
        var c = Controller(w, key, w.User(w.EmpUserId, "Employee", "leave.read", "ess.read"), w.OwnScope(w.Emp));

        var result = await c.DownloadAttachment(leave.Id, CancellationToken.None);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.FileContents.Should().Equal(Bytes);
        file.ContentType.Should().Be("application/pdf");
        c.Response.Headers.ContentDisposition.ToString().Should().StartWith("inline").And.Contain("sick_note.pdf");
        c.Response.Headers["X-Content-Type-Options"].ToString().Should().Be("nosniff");
    }

    [Theory]
    [InlineData("HR Officer")]
    [InlineData("HR Manager")]
    [InlineData("Admin")]
    public async Task Hr_and_admin_get_the_document_and_the_access_is_audited(string role)
    {
        var (w, leave, _, key) = await SeedAsync();
        var c = Controller(w, key, w.User(Guid.NewGuid(), role, "leave.read"), LeaveBatch1World.OrgScope());

        var result = await c.DownloadAttachment(leave.Id, CancellationToken.None);

        result.Should().BeOfType<FileContentResult>();
        (await w.Db.AuditLogs.AnyAsync(a => a.TenantId == w.TenantId && a.Action == "leave.attachment_downloaded" && a.EntityId == leave.Id.ToString())).Should().BeTrue();
    }

    [Fact]
    public async Task A_line_manager_without_an_hr_role_gets_404()
    {
        var (w, leave, _, key) = await SeedAsync();
        var c = Controller(w, key, w.User(w.BossUserId, "Manager", "leave.read", "manager.read"), w.TeamScope(w.Boss, w.Emp));

        (await c.DownloadAttachment(leave.Id, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        (await w.Db.AuditLogs.AnyAsync(a => a.Action == "leave.attachment_downloaded")).Should().BeFalse();
    }

    [Fact]
    public async Task A_colleague_who_is_not_the_employee_gets_404()
    {
        var (w, leave, _, key) = await SeedAsync();
        var c = Controller(w, key, w.User(w.OtherUserId, "Employee", "leave.read", "ess.read"), w.OwnScope(w.Other));

        (await c.DownloadAttachment(leave.Id, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Another_tenants_hr_gets_404()
    {
        var (w, leave, _, key) = await SeedAsync();
        var foreign = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", Guid.NewGuid().ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "Admin"), new Claim("permission", "leave.read"),
        }, "Test"));
        var c = Controller(w, key, foreign, LeaveBatch1World.OrgScope());

        (await c.DownloadAttachment(leave.Id, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task A_leave_with_no_attachment_gets_404()
    {
        var (w, leave, _, key) = await SeedAsync(attach: false);
        var c = Controller(w, key, w.User(Guid.NewGuid(), "HR Manager", "leave.read"), LeaveBatch1World.OrgScope());

        (await c.DownloadAttachment(leave.Id, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task A_deleted_document_gets_404()
    {
        var (w, leave, doc, key) = await SeedAsync();
        doc.IsDeleted = true;
        await w.Db.SaveChangesAsync();
        var c = Controller(w, key, w.User(Guid.NewGuid(), "HR Manager", "leave.read"), LeaveBatch1World.OrgScope());

        (await c.DownloadAttachment(leave.Id, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task A_key_with_no_document_row_or_no_stored_file_gets_404()
    {
        var (w, leave, _, key) = await SeedAsync(withDocument: false);
        var c = Controller(w, key, w.User(Guid.NewGuid(), "HR Manager", "leave.read"), LeaveBatch1World.OrgScope());
        (await c.DownloadAttachment(leave.Id, CancellationToken.None)).Should().BeOfType<NotFoundResult>();

        var (w2, leave2, _, _) = await SeedAsync();
        var c2 = Controller(w2, "some/other/key", w2.User(Guid.NewGuid(), "HR Manager", "leave.read"), LeaveBatch1World.OrgScope());
        (await c2.DownloadAttachment(leave2.Id, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task The_filename_in_the_header_is_sanitised()
    {
        var (w, leave, _, key) = await SeedAsync(fileName: "..\\..\\evil\"\r\nX-Injected: 1.pdf");
        var c = Controller(w, key, w.User(Guid.NewGuid(), "HR Officer", "leave.read"), LeaveBatch1World.OrgScope());

        (await c.DownloadAttachment(leave.Id, CancellationToken.None)).Should().BeOfType<FileContentResult>();

        var header = c.Response.Headers.ContentDisposition.ToString();
        header.Should().StartWith("inline");
        header.Should().NotContain("\r").And.NotContain("\n").And.NotContain("..").And.NotContain("\\");
    }
}
