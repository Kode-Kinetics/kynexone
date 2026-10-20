using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.AI;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Setup;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Authorization;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/setup-assistant/policy/reviews")]
// Match extraction's explicit permission gate, including authorized custom roles.
// A legacy role list here would be misleading: this app resolves it through permissions.
[Authorize]
[HasPermission("organization.write")]
public sealed class SetupPolicyReviewController(ZayraDbContext db, IPolicyDocumentService documents) : ControllerBase
{
    private const string ReviewAction = "setup.policy_review_recorded";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [HttpPost]
    [RequestSizeLimit(32768)]
    public async Task<IActionResult> Save([FromBody] PolicyReviewRequest request, CancellationToken ct)
    {
        var gate = await AuthorizeSource(request.CompanyId, request.DocumentId, ct);
        if (gate.Error is not null) return gate.Error;
        if (Invalid(request) is { } problem) return BadRequest(new { message = problem });
        if (!string.Equals(request.SourceHash, gate.Source!.ContentSha256, StringComparison.Ordinal))
            return Conflict(new { message = "The policy version changed. Extract and review the current source again." });
        if (!string.IsNullOrEmpty(request.SourceQuote))
        {
            var text = gate.Source.Text;
            if (request.SourceStart < 0 || request.SourceLength != request.SourceQuote.Length
                || request.SourceStart > text.Length - request.SourceLength
                || text.Substring(request.SourceStart, request.SourceLength) != request.SourceQuote)
                return BadRequest(new { message = "The review evidence does not match this document version." });
        }
        else if (request.SourceStart != 0 || request.SourceLength != 0)
            return BadRequest(new { message = "Source offsets require a supporting excerpt." });

        // The existing audit table stores immutable decisions; no policy or operational table is changed.
        // ZayraDbContext serializes and seals central audit rows under its shared tenant lock,
        // including ordinary audit writers. Do not introduce a separate lock or stale tail hash here.
        return await Persist(request, gate.Tenant, gate.User, ct);
    }

    [HttpGet]
    public async Task<IActionResult> History([FromQuery] Guid companyId, [FromQuery] Guid documentId, CancellationToken ct)
    {
        var gate = await AuthorizeSource(companyId, documentId, ct);
        if (gate.Error is not null) return gate.Error;
        var metadata = await db.AuditLogs.AsNoTracking()
            .Where(a => a.TenantId == gate.Tenant && a.CompanyId == companyId && a.Action == ReviewAction && a.EntityId == documentId.ToString())
            .OrderByDescending(a => a.CreatedAtUtc).ThenByDescending(a => a.Id).Take(500).Select(a => a.Metadata).ToListAsync(ct);
        var records = new List<PolicyReviewRecord>();
        foreach (var value in metadata)
        {
            if (value is null) continue;
            try { if (JsonSerializer.Deserialize<PolicyReviewRecord>(value, Json) is { } record) records.Add(record); }
            catch (JsonException) { /* Other historical formats cannot become a current review. */ }
        }
        return Ok(records);
    }

    private async Task<IActionResult> Persist(PolicyReviewRequest r, Guid tenant, Guid user, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        now = new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc); // PostgreSQL microsecond precision.
        var record = new PolicyReviewRecord(Guid.NewGuid(), r.CompanyId, r.DocumentId, r.SourceHash,
            r.ItemType, r.ItemKey, r.Summary.Trim(), r.Disposition, r.Comment.Trim(), r.Owner?.Trim(),
            r.ApprovalReference?.Trim(), r.Target, r.Field, r.SourceQuote, r.SourceStart, r.SourceLength, user, now);
        var log = new AuditLog { Id = record.Id, TenantId = tenant, CompanyId = r.CompanyId, UserId = user,
            Action = ReviewAction, EntityName = "PolicyReview", EntityId = r.DocumentId.ToString(),
            Metadata = JsonSerializer.Serialize(record, Json), CreatedAtUtc = now,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(), UserAgent = Request.Headers.UserAgent.ToString() };
        db.AuditLogs.Add(log);
        await db.SaveChangesAsync(ct);
        return Ok(record);
    }

    private async Task<(IActionResult? Error, PolicyDocumentText? Source, Guid Tenant, Guid User)> AuthorizeSource(Guid companyId, Guid documentId, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue("tenant_id"), out var tenant) || tenant == Guid.Empty
            || !Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var user) || user == Guid.Empty)
            return (Unauthorized(), null, default, default);
        if (!User.HasPermission("organization.write")) return (Forbid(), null, tenant, user);
        var scope = this.GetEntityScope();
        if (!scope.CanAccessCompany(companyId)) return (Forbid(), null, tenant, user);
        if (companyId == Guid.Empty || !await db.Companies.AnyAsync(c => c.TenantId == tenant && c.Id == companyId && !c.IsDeleted, ct))
            return (NotFound(), null, tenant, user);
        var document = await documents.FindAsync(tenant, documentId,
            new(true, scope.IsGroupLevel, scope.AccessibleCompanyIds, user, User.IsInRole("Admin")), ct);
        if (document is null) return (NotFound(), null, tenant, user);
        if (document.CompanyId.HasValue && document.CompanyId != companyId) return (Forbid(), null, tenant, user);
        var source = await documents.TextAsync(tenant, documentId,
            new(true, scope.IsGroupLevel, scope.AccessibleCompanyIds, user, User.IsInRole("Admin")), ct);
        return source is null ? (NotFound(), null, tenant, user) : (null, source, tenant, user);
    }

    private static string? Invalid(PolicyReviewRequest r)
    {
        static bool Required(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max;
        if (!Required(r.SourceHash, 64) || r.SourceHash.Length != 64 || !r.SourceHash.All(Uri.IsHexDigit)
            || !Required(r.ItemKey, 200) || !Required(r.Summary, 2000)
            || r.ItemType is not ("proposal" or "issue" or "requirement")) return "A valid document version and review item are required.";
        if (r.Disposition is not ("Reviewed" or "NeedsClarification" or "Deferred" or "ManualProcess" or "ProductGap" or "NotApplicable" or "Answered"))
            return "Choose a review disposition. A review cannot mark settings as applied.";
        if (r.Comment is null || r.Comment.Length > 4000 || r.Disposition != "Reviewed" && string.IsNullOrWhiteSpace(r.Comment))
            return "Explain this decision in a comment (up to 4,000 characters).";
        if ((r.Owner?.Length ?? 0) > 200 || (r.ApprovalReference?.Length ?? 0) > 1000
            || (r.Target?.Length ?? 0) > 40 || (r.Field?.Length ?? 0) > 100 || (r.SourceQuote?.Length ?? 0) > 4000)
            return "The review contains an overlong field.";
        if (r.Disposition is "NeedsClarification" or "Deferred" or "ManualProcess" or "ProductGap" && string.IsNullOrWhiteSpace(r.Owner))
            return "Specify a follow-up owner. This records responsibility; it does not send a task notification.";
        if (r.Disposition == "Answered" && (string.IsNullOrWhiteSpace(r.ApprovalReference) || string.IsNullOrWhiteSpace(r.SourceQuote)))
            return "An answered clarification requires a supporting source excerpt and approval reference. It does not activate a configuration.";
        return null;
    }
}
