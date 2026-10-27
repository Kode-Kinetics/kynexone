using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Employees.Ess;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Http;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

/// <summary>HR's side of "Complete your profile": the inbox of submitted sections with a before/after, and the attached files.</summary>
[ApiController]
[Authorize]   // the audience is exactly the holders of employees.write (HasPermission on each action); deciding stays on the HR-role-gated approve/reject routes
[NoStore]
[Route("api/hr/profile-completion")]
public sealed class HrProfileCompletionController(ZayraDbContext db, IDocumentStorage storage) : ControllerBase
{
    private const int PageSize = 50;
    private static readonly string[] Statuses = ["PendingHR", "AwaitingFinance", "Approved", "Rejected", "Expired"];

    [HttpGet("inbox")]
    [HasPermission("employees.write")]
    public async Task<IActionResult> Inbox([FromQuery] string? status, [FromQuery] string? section, [FromQuery] Guid? companyId, [FromQuery] int page = 1, CancellationToken ct = default)
    {
        var tenantId = Guid.Parse(User.FindFirstValue("tenant_id")!);
        status = string.IsNullOrWhiteSpace(status) ? "PendingHR" : status;
        if (!Statuses.Contains(status) || (!string.IsNullOrEmpty(section) && !EssSectionKeys.IsKnown(section)))
            return EssProblem.Create(HttpContext, 400, EssProblemCodes.ValidationFailed);
        var mayRevealSensitive = User.HasClaim("permission", "employees.sensitive");
        page = Math.Max(1, page);

        // The employee query carries the company scope (global filter), so an HR user limited to one company never sees another's rows.
        var employees = db.Employees.AsNoTracking().Where(e => e.TenantId == tenantId && !e.IsDeleted && (companyId == null || e.CompanyId == companyId));
        var rows = await (from r in db.EmployeeProfileChangeRequests.AsNoTracking().Where(x => x.TenantId == tenantId && x.Section != null && x.Status == status && (section == null || section == "" || x.Section == section))
                          join e in employees on r.EmployeeId equals e.Id
                          orderby r.CreatedAtUtc descending
                          select new { Request = r, Employee = e })
            .Skip((page - 1) * PageSize).Take(PageSize).ToListAsync(ct);

        var docIds = rows.SelectMany(x => EssSectionDecisionService.ReadDocumentIds(x.Request.RequestedChangesJson)).Distinct().ToList();
        var docs = docIds.Count == 0 ? [] : await db.EmployeeDocuments.AsNoTracking().Where(d => d.TenantId == tenantId && docIds.Contains(d.Id)).ToListAsync(ct);

        var items = rows.Select(x =>
        {
            var cls = EssSectionDecisionService.ClassOf(x.Employee);
            var proposed = EssSectionDecisionService.ReadValues(x.Request.RequestedChangesJson);
            var diff = ProfileSectionRegistry.FieldsFor(x.Request.Section!, cls).Select(f =>
            {
                var before = ProfileSectionRegistry.CurrentValue(x.Request.Section!, f.Key, x.Employee);
                var after = proposed.GetValueOrDefault(f.Key) ?? before;
                var shownBefore = mayRevealSensitive || !f.Sensitive ? before : ProfileSectionRegistry.Mask(f, before);
                var shownAfter = mayRevealSensitive || !f.Sensitive ? after : ProfileSectionRegistry.Mask(f, after);
                return new { field = f.Key, labelKey = f.LabelKey, before = shownBefore, after = shownAfter, changed = !string.Equals(before.Replace(" ", ""), after.Replace(" ", ""), StringComparison.Ordinal) };
            }).ToList();
            var attached = EssSectionDecisionService.ReadDocumentIds(x.Request.RequestedChangesJson)
                .Select(id => docs.FirstOrDefault(d => d.Id == id)).Where(d => d is not null)
                .Select(d => new { id = mayRevealSensitive ? (Guid?)d!.Id : null, kind = d!.DocumentCategory, scanStatus = d.ScanStatus, fileName = d.FileName }).ToList();
            return new
            {
                requestId = x.Request.Id,
                employee = new { id = x.Employee.Id, code = x.Employee.EmployeeCode, name = string.IsNullOrWhiteSpace(x.Employee.EnglishName) ? x.Employee.FullName : x.Employee.EnglishName },
                section = x.Request.Section,
                status = x.Request.Status,
                submittedAtUtc = x.Request.CreatedAtUtc,
                goesToFinance = x.Request.Section == EssSectionKeys.Bank,
                diff,
                documents = attached,
            };
        }).ToList();
        if (mayRevealSensitive && rows.Count > 0)
        {
            // Unmasked values left the server: one audit row per employee on the page, so a view is as traceable as a download.
            var viewer = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var vid) ? vid : (Guid?)null;
            foreach (var x in rows)
                db.EmployeeSelfServiceAuditLogs.Add(new EmployeeSelfServiceAuditLog { TenantId = tenantId, EmployeeId = x.Employee.Id, UserId = viewer, Action = "ess.completion.inbox_viewed_by_hr", EntityName = nameof(EmployeeProfileChangeRequest), EntityId = x.Request.Id.ToString() });
            await db.SaveChangesAsync(ct);
        }
        return Ok(items);
    }

    [HttpGet("documents/{id:guid}/download")]
    [HasPermission("employees.write")]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct)
    {
        // Same conjunction as the workspace download; HasPermission above is ANY-of, so it is enforced here.
        if (!User.HasPermission("employees.read") || !User.HasPermission("employees.documents")) return Forbid();
        var tenantId = Guid.Parse(User.FindFirstValue("tenant_id")!);
        // The document row carries the company scope filter; an HR user outside the employee's company gets the same 404 as a missing id.
        var doc = await db.EmployeeDocuments.FirstOrDefaultAsync(d => d.TenantId == tenantId && d.Id == id && d.SourceSection != null && !d.IsDeleted, ct);
        if (doc is null || doc.EmployeeId is null) return EssProblem.Create(HttpContext, 404, EssProblemCodes.NotFound);
        if (EmployeeDocumentDto.IsSensitiveDocument(doc.DocumentType, doc.DocumentCategory) && !User.HasPermission("employees.sensitive")) return Forbid();
        if (doc.ScanStatus != "Clean") return EssProblem.Create(HttpContext, 409, EssProblemCodes.DocumentQuarantined);
        byte[] bytes;
        try { bytes = await storage.GetBytesAsync(tenantId, doc.StorageUrl, ct); }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or DirectoryNotFoundException)
        { return EssProblem.Create(HttpContext, 404, EssProblemCodes.NotFound); }
        doc.LastDownloadedAtUtc = DateTime.UtcNow;
        doc.LastDownloadedBy = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var uid) ? uid : null;
        db.EmployeeSelfServiceAuditLogs.Add(new EmployeeSelfServiceAuditLog
        {
            TenantId = tenantId, EmployeeId = doc.EmployeeId.Value, UserId = doc.LastDownloadedBy, Action = "ess.completion.document_downloaded_by_hr",
            EntityName = nameof(EmployeeDocument), EntityId = doc.Id.ToString(),
        });
        await db.SaveChangesAsync(ct);
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(bytes, string.IsNullOrWhiteSpace(doc.ContentType) ? "application/octet-stream" : doc.ContentType, doc.FileName);
    }
}
