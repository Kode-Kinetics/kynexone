using System.Security.Claims;
using System.Text.Json;
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

/// <summary>Review follow-up: Attachment. See <see cref="LeaveSafetyReviewTests"/>.</summary>
public sealed partial class LeaveSafetyReviewTests
{
    // ── 3 + 4. The leave document: own document with an org-wide scope, response hardening ───────

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

    private static async Task<(LeaveBatch1World W, LeaveRequest Leave, string Key)> SeedAsync(int? employeeId = null)
    {
        var w = await LeaveBatch1World.CreateAsync();
        var owner = employeeId ?? w.Emp.Id;
        var key = $"{w.TenantId:N}/documents/{Guid.NewGuid():N}_note.pdf";
        w.Db.EmployeeDocuments.Add(new EmployeeDocument
        {
            TenantId = w.TenantId, CompanyId = w.CompanyId, EmployeeId = owner, DocumentType = "Leave Attachment",
            FileName = "sick note.pdf", ContentType = "application/pdf", StorageUrl = key, ScanStatus = "clean",
        });
        var day = w.NextWorkday();
        var leave = await w.SeedAsync("PendingHrApproval", day, day, employeeId: owner, attachmentPath: key);
        return (w, leave, key);
    }

    private static LeaveRequestsController Download(LeaveBatch1World w, string key, ClaimsPrincipal user, IAuditService? audit) =>
        new(w.Db, w.Service, new LeaveBatch1World.FixedScope(LeaveBatch1World.OrgScope()), w.Notices, new MemoryStorage(w.TenantId, key), audit)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } },
        };

    [Fact]
    public async Task An_org_wide_finance_user_opens_their_own_sick_note_but_not_a_colleagues()
    {
        var (w, own, key) = await SeedAsync();
        var finance = Caller(w, "Finance Manager", w.Emp.Id, "leave.read");

        (await Download(w, key, finance, new AuditService(w.Db)).DownloadAttachment(own.Id, CancellationToken.None))
            .Should().BeOfType<FileContentResult>();

        var (w2, theirs, key2) = await SeedAsync();
        var finance2 = Caller(w2, "Finance Manager", w2.Other.Id, "leave.read");
        (await Download(w2, key2, finance2, new AuditService(w2.Db)).DownloadAttachment(theirs.Id, CancellationToken.None))
            .Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task An_org_wide_finance_user_sees_the_document_key_on_their_own_leave_only()
    {
        var (w, own, _) = await SeedAsync();
        var finance = Caller(w, "Finance Manager", w.Emp.Id, "leave.read");
        var asSelf = w.Leave(LeaveBatch1World.OrgScope(), finance);
        var ownBody = ((OkObjectResult)await asSelf.Get(own.Id, CancellationToken.None)).Value!;
        ((LeaveRequest)ownBody.GetType().GetProperty("request")!.GetValue(ownBody)!).AttachmentPath.Should().NotBeNullOrEmpty();

        var asColleague = w.Leave(LeaveBatch1World.OrgScope(), Caller(w, "Finance Manager", w.Other.Id, "leave.read"));
        var otherBody = ((OkObjectResult)await asColleague.Get(own.Id, CancellationToken.None)).Value!;
        ((LeaveRequest)otherBody.GetType().GetProperty("request")!.GetValue(otherBody)!).AttachmentPath.Should().BeNullOrEmpty();
    }
}
