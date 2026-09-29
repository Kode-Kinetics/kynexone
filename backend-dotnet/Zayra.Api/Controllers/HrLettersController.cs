using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Models;


// Name collision: Controllers/EmployeesController.cs declares a DTO record called
// EmployeeDocumentRequest in this same namespace, which shadows the entity. Alias the entity
// rather than rename either — the DTO is on a public upload contract and the entity name is in
// the database schema.
using DocumentRequest = Zayra.Api.Models.EmployeeDocumentRequest;

namespace Zayra.Api.Controllers;

/// <summary>
/// HR letters: configure the template, issue the document, answer the employee's request, and
/// look up what was issued.
///
/// <para><b>Judgement call — issuance is NOT put behind an approval workflow, deliberately.</b>
/// The review noted the current letters are a direct download for anyone holding an HR role and
/// asked whether that should change. It should not, for these reasons:</para>
/// <list type="number">
/// <item>A salary certificate is a statement of fact the employer already holds. HR is not
/// deciding anything; the figures come straight out of the salary structure. An approval step
/// approves nothing.</item>
/// <item>The document is time-critical in exactly the way approvals are bad at. The employee has
/// a bank appointment on Thursday. A queue that needs a second person is a queue that gets
/// bypassed — HR will type the letter in Word, which is the status quo this feature exists to
/// end.</item>
/// <item>The real control HR and the employee need is not permission, it is <i>traceability</i>:
/// a stored unique reference, a named issuer, the figures as they stood on the day, and a
/// register a third party can be checked against. That is what this controller writes, on every
/// single issuance, with no way to opt out.</item>
/// </list>
/// <para>Where a genuine second pair of eyes does exist it is the natural one: when the employee
/// raises the request, the person who asks and the person who issues are already different
/// people, and the register records both.</para>
///
/// <para>What IS gated: an employee can request and can download their own issued letter, and
/// can do nothing else. Issuing, declining and template editing are HR-role only.</para>
/// </summary>
[ApiController]
[Route("api/hr-letters")]
[Authorize]
public class HrLettersController : ControllerBase
{
    private const string HrRoles = "Admin,HR Manager,HR Officer";
    private const string TemplateAdminRoles = "Admin,HR Manager";

    private readonly ZayraDbContext _db;
    private readonly IHrLetterIssuer _issuer;
    private readonly IAuditService _audit;
    private readonly IDataScopeService _scopeService;

    public HrLettersController(
        ZayraDbContext db, IHrLetterIssuer issuer, IAuditService audit, IDataScopeService scopeService)
    {
        _db = db;
        _issuer = issuer;
        _audit = audit;
        _scopeService = scopeService;
    }

    // ── Catalogue ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The letter types this tenant can issue, and whether each one has a template behind it.
    /// The UI uses <c>isConfigured</c> to grey out what would fail, rather than offering an
    /// option that 409s — hiding a field beats shipping one that lies.
    /// </summary>
    [HttpGet("types")]
    public async Task<IActionResult> Types(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var configured = await _db.HrLetterTemplates.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.IsActive && !x.IsDeleted)
            .Select(x => new { x.LetterType, x.Language })
            .ToListAsync(ct);

        var defaults = HrLetterTemplateDefaults.Build().ToDictionary(x => x.LetterType, StringComparer.Ordinal);

        return Ok(HrLetterTypes.All.Select(type =>
        {
            var match = configured.FirstOrDefault(x => x.LetterType == type);
            return new
            {
                letterType = type,
                referencePrefix = HrLetterTypes.Prefixes[type],
                nameEn = defaults.GetValueOrDefault(type)?.NameEn ?? type,
                nameAr = defaults.GetValueOrDefault(type)?.NameAr ?? string.Empty,
                employeeRequestable = HrLetterTypes.EmployeeRequestable.Contains(type),
                isConfigured = match is not null,
                languages = match?.Language ?? string.Empty,
            };
        }));
    }

    // ── Templates ─────────────────────────────────────────────────────────────────────────

    [HttpGet("templates")]
    [Authorize(Roles = TemplateAdminRoles)]
    public async Task<IActionResult> Templates(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var templates = await _db.HrLetterTemplates.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .OrderBy(x => x.LetterType).ThenBy(x => x.CompanyId)
            .ToListAsync(ct);

        return Ok(new
        {
            items = templates.Select(ToTemplateDto),
            knownTokens = HrLetterTemplateDefaults.KnownTokens,
        });
    }

    /// <summary>
    /// Plants the bilingual defaults for any letter type this tenant has no template for.
    /// Idempotent, and it never overwrites a tenant's edits — it only fills gaps. Exposed so an
    /// existing tenant (whose AuthSeeder run predates this feature) can self-serve.
    /// </summary>
    [HttpPost("templates/seed-defaults")]
    [Authorize(Roles = TemplateAdminRoles)]
    public async Task<IActionResult> SeedDefaults(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var added = await _issuer.EnsureDefaultTemplatesAsync(tenantId.Value, ct);
        await Audit(tenantId.Value, "hr_letter.templates_seeded", "HrLetterTemplate", null,
            JsonSerializer.Serialize(new { added }), ct);
        return Ok(new { added });
    }

    [HttpPut("templates/{id:guid}")]
    [Authorize(Roles = TemplateAdminRoles)]
    public async Task<IActionResult> UpdateTemplate(Guid id, [FromBody] UpdateLetterTemplateRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var template = await _db.HrLetterTemplates
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted, ct);
        if (template is null) return NotFound();

        if (!HrLetterLanguages.IsKnown(req.Language))
            return BadRequest(new { message = $"Language must be one of: {string.Join(", ", HrLetterLanguages.All)}." });

        // Refuse a template whose body references a token the renderer cannot supply. Catching it
        // here means the author finds out while editing, not when an employee's certificate is
        // refused at issuance.
        var referenced = LetterTemplateRenderer.ExtractTokens(
            string.Join("\n", req.TitleEn, req.TitleAr, req.BodyEn, req.BodyAr, req.ClosingEn, req.ClosingAr));
        var unknown = referenced
            .Where(t => !HrLetterTemplateDefaults.KnownTokens.Contains(t, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (unknown.Count > 0)
            return BadRequest(new
            {
                code = "unknown_merge_tokens",
                message = $"Unknown merge field(s): {string.Join(", ", unknown)}.",
                knownTokens = HrLetterTemplateDefaults.KnownTokens,
            });

        if (req.Language is HrLetterLanguages.English or HrLetterLanguages.Bilingual
            && string.IsNullOrWhiteSpace(req.BodyEn))
            return BadRequest(new { message = "An English or bilingual template needs English body text." });
        if (req.Language is HrLetterLanguages.Arabic or HrLetterLanguages.Bilingual
            && string.IsNullOrWhiteSpace(req.BodyAr))
            return BadRequest(new { message = "An Arabic or bilingual template needs Arabic body text." });

        template.NameEn = req.NameEn ?? template.NameEn;
        template.NameAr = req.NameAr ?? template.NameAr;
        template.Language = req.Language.ToLowerInvariant();
        template.TitleEn = req.TitleEn ?? string.Empty;
        template.TitleAr = req.TitleAr ?? string.Empty;
        template.BodyEn = req.BodyEn ?? string.Empty;
        template.BodyAr = req.BodyAr ?? string.Empty;
        template.ClosingEn = req.ClosingEn ?? string.Empty;
        template.ClosingAr = req.ClosingAr ?? string.Empty;
        template.IsActive = req.IsActive ?? template.IsActive;
        template.Version++;
        template.UpdatedAtUtc = DateTime.UtcNow;
        template.UpdatedByUserId = this.GetUserId();

        await _db.SaveChangesAsync(ct);
        await Audit(tenantId.Value, "hr_letter.template_updated", "HrLetterTemplate", id.ToString(),
            JsonSerializer.Serialize(new { template.LetterType, template.Version, template.Language }), ct);

        return Ok(ToTemplateDto(template));
    }

    // ── Issuance ──────────────────────────────────────────────────────────────────────────

    /// <summary>HR issues a letter directly, without a prior employee request.</summary>
    [HttpPost("issue")]
    [Authorize(Roles = HrRoles)]
    public async Task<IActionResult> Issue([FromBody] IssueLetterRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        if (!scope.CanAccessEmployee(req.EmployeeId)) return Forbid();

        var result = await _issuer.IssueAsync(await BuildCommandAsync(
            tenantId.Value, req.EmployeeId, req.LetterType, req.Language,
            req.Purpose, req.AddresseeName, null, null, ct), ct);

        return await RespondToIssuanceAsync(tenantId.Value, result, ct);
    }

    // ── Register ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The register of issued letters. This is the surface that did not exist: before it, the only
    /// evidence a letter had been produced was an audit row with no reference on it.
    /// </summary>
    [HttpGet("register")]
    [Authorize(Roles = HrRoles)]
    public async Task<IActionResult> Register(
        [FromQuery] int? employeeId,
        [FromQuery] string? letterType,
        [FromQuery] string? reference,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        var query = _db.IssuedLetters.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (!scope.IsUnrestricted)
            query = query.Where(x => scope.AllowedEmployeeIds!.Contains(x.EmployeeId));
        if (employeeId is int eid) query = query.Where(x => x.EmployeeId == eid);
        if (HrLetterTypes.Normalize(letterType) is string type) query = query.Where(x => x.LetterType == type);
        if (!string.IsNullOrWhiteSpace(reference))
            query = query.Where(x => x.ReferenceNumber == reference.Trim());
        if (from is DateTime f) query = query.Where(x => x.IssuedAtUtc >= f);
        if (to is DateTime t) query = query.Where(x => x.IssuedAtUtc <= t);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(x => x.IssuedAtUtc)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new IssuedLetterListItem(
                x.Id, x.ReferenceNumber, x.LetterType, x.EmployeeId, x.EmployeeCode, x.EmployeeName,
                x.Language, x.Purpose, x.AddresseeName, x.IssuedByName, x.IssuedByTitle,
                x.IssuedAtUtc, x.FileHash, x.FileSizeBytes, x.DocumentRequestId != null))
            .ToListAsync(ct);

        return Ok(new { total, page, pageSize, items });
    }

    /// <summary>
    /// Reprint an issued letter: same reference, same wording, marked in the audit trail as a
    /// reprint. No new register row and no new reference — a reference identifies a document,
    /// not a download.
    /// </summary>
    [HttpGet("register/{id:guid}/pdf")]
    [Authorize(Roles = HrRoles)]
    public async Task<IActionResult> Reprint(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var record = await _db.IssuedLetters.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted, ct);
        if (record is null) return NotFound();

        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        if (!scope.CanAccessEmployee(record.EmployeeId)) return Forbid();

        var pdf = await _issuer.ReprintAsync(tenantId.Value, id, ct);
        if (pdf is null)
            return Conflict(new
            {
                code = "reprint_unavailable",
                message = "This register entry has no stored content to reprint. Issue a fresh letter instead.",
            });

        await Audit(tenantId.Value, "hr_letter.reprinted", nameof(IssuedLetter), id.ToString(),
            JsonSerializer.Serialize(new { record.ReferenceNumber, record.LetterType, record.EmployeeId }), ct);

        return File(pdf, "application/pdf", $"{record.ReferenceNumber}.pdf");
    }

    // ── Employee requests (HR side) ───────────────────────────────────────────────────────

    [HttpGet("requests")]
    [Authorize(Roles = HrRoles)]
    public async Task<IActionResult> Requests(
        [FromQuery] string status = EmployeeDocumentRequestStatuses.Pending,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        var query = _db.EmployeeDocumentRequests.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (!scope.IsUnrestricted)
            query = query.Where(x => scope.AllowedEmployeeIds!.Contains(x.EmployeeId));
        if (!string.IsNullOrWhiteSpace(status) && !string.Equals(status, "All", StringComparison.OrdinalIgnoreCase))
            query = query.Where(x => x.Status == status);

        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(x => x.CreatedAtUtc)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        var employeeIds = rows.Select(x => x.EmployeeId).Distinct().ToList();
        var employees = await _db.Employees.AsNoTracking()
            .Where(x => x.TenantId == tenantId && employeeIds.Contains(x.Id))
            .Select(x => new { x.Id, x.EmployeeCode, x.FullName, x.EnglishName })
            .ToListAsync(ct);

        var items = rows.Select(r =>
        {
            var emp = employees.FirstOrDefault(e => e.Id == r.EmployeeId);
            return new DocumentRequestListItem(
                r.Id, r.EmployeeId, emp?.EmployeeCode ?? string.Empty,
                string.IsNullOrWhiteSpace(emp?.EnglishName) ? emp?.FullName ?? string.Empty : emp!.EnglishName,
                r.LetterType, r.Language, r.Purpose, r.AddresseeName, r.Status,
                r.CreatedAtUtc, r.DecidedAtUtc, r.DecisionNote, r.IssuedLetterId, r.HrRequestId);
        }).ToList();

        return Ok(new { total, page, pageSize, items });
    }

    /// <summary>HR issues the letter the employee asked for, closing the request and its ticket.</summary>
    [HttpPost("requests/{id:guid}/issue")]
    [Authorize(Roles = HrRoles)]
    public async Task<IActionResult> IssueForRequest(Guid id, [FromBody] IssueForRequestBody? body, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var request = await _db.EmployeeDocumentRequests
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
        if (request is null) return NotFound();

        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        if (!scope.CanAccessEmployee(request.EmployeeId)) return Forbid();

        if (request.Status != EmployeeDocumentRequestStatuses.Pending)
            return Conflict(new
            {
                code = "request_not_pending",
                message = $"This request is already {request.Status}.",
            });

        var command = await BuildCommandAsync(
            tenantId.Value, request.EmployeeId,
            body?.LetterType ?? request.LetterType,
            body?.Language ?? request.Language,
            request.Purpose, request.AddresseeName,
            request.Id, request.HrRequestId, ct);

        var result = await _issuer.IssueAsync(command, ct);
        if (!result.Ok) return await RespondToIssuanceAsync(tenantId.Value, result, ct);

        request.Status = EmployeeDocumentRequestStatuses.Issued;
        request.DecidedAtUtc = DateTime.UtcNow;
        request.DecidedByUserId = this.GetUserId();
        request.IssuedLetterId = result.Letter!.Id;
        await CloseTicketAsync(tenantId.Value, request, "Resolved",
            $"Issued under reference {result.Letter.ReferenceNumber}.", ct);
        await _db.SaveChangesAsync(ct);

        return await RespondToIssuanceAsync(tenantId.Value, result, ct);
    }

    [HttpPost("requests/{id:guid}/decline")]
    [Authorize(Roles = HrRoles)]
    public async Task<IActionResult> DeclineRequest(Guid id, [FromBody] DeclineRequestBody body, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        // A decline with no reason is how an employee ends up asking HR the same question four
        // times. The reason is mandatory and it is shown to them.
        if (string.IsNullOrWhiteSpace(body.Reason) || body.Reason.Trim().Length < 5)
            return BadRequest(new { message = "A reason of at least 5 characters is required to decline a document request." });

        var request = await _db.EmployeeDocumentRequests
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
        if (request is null) return NotFound();

        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        if (!scope.CanAccessEmployee(request.EmployeeId)) return Forbid();
        if (request.Status != EmployeeDocumentRequestStatuses.Pending)
            return Conflict(new { code = "request_not_pending", message = $"This request is already {request.Status}." });

        request.Status = EmployeeDocumentRequestStatuses.Declined;
        request.DecisionNote = body.Reason.Trim();
        request.DecidedAtUtc = DateTime.UtcNow;
        request.DecidedByUserId = this.GetUserId();
        await CloseTicketAsync(tenantId.Value, request, "Closed", $"Declined: {request.DecisionNote}", ct);
        await _db.SaveChangesAsync(ct);

        await Audit(tenantId.Value, "hr_letter.request_declined", nameof(DocumentRequest), id.ToString(),
            JsonSerializer.Serialize(new { request.LetterType, request.EmployeeId }), ct);

        return Ok(new { id = request.Id, status = request.Status, decisionNote = request.DecisionNote });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the signatory from the caller's own identity. The previous letters hard-coded
    /// <c>IssuedBy: "HR Department"</c>, which is not a person and cannot be asked about the
    /// document later.
    /// </summary>
    private async Task<IssueLetterCommand> BuildCommandAsync(
        Guid tenantId, int employeeId, string letterType, string language,
        string purpose, string addressee, Guid? requestId, Guid? hrRequestId, CancellationToken ct)
    {
        var userId = this.GetUserId();
        var issuerName = User.FindFirstValue("name")
                         ?? User.FindFirstValue(ClaimTypes.Name)
                         ?? User.FindFirstValue("email")
                         ?? "HR Department";

        if (userId is Guid uid)
        {
            var dbName = await _db.Users.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.Id == uid)
                .Select(x => x.FullName)
                .FirstOrDefaultAsync(ct);
            if (!string.IsNullOrWhiteSpace(dbName)) issuerName = dbName;
        }

        var issuerTitle = User.FindAll(ClaimTypes.Role)
            .Select(c => c.Value)
            .FirstOrDefault(r => r is "HR Manager" or "HR Officer" or "Admin") ?? "Human Resources";

        return new IssueLetterCommand(
            TenantId: tenantId,
            EmployeeId: employeeId,
            LetterType: letterType,
            Language: language,
            Purpose: purpose,
            AddresseeName: addressee,
            IssuedByUserId: userId,
            IssuerName: issuerName,
            IssuerTitle: issuerTitle,
            DocumentRequestId: requestId,
            HrRequestId: hrRequestId);
    }

    private async Task<IActionResult> RespondToIssuanceAsync(Guid tenantId, LetterIssueResult result, CancellationToken ct)
    {
        if (!result.Ok)
        {
            var payload = new { code = result.ErrorCode, message = result.ErrorMessage, unresolvedFields = result.UnresolvedTokens };
            return result.ErrorCode switch
            {
                "employee_not_found" => NotFound(payload),
                "unknown_letter_type" => BadRequest(payload),
                _ => Conflict(payload),
            };
        }

        var letter = result.Letter!;
        await Audit(tenantId, "hr_letter.issued", nameof(IssuedLetter), letter.Id.ToString(),
            JsonSerializer.Serialize(new
            {
                letter.ReferenceNumber, letter.LetterType, letter.EmployeeId,
                letter.Language, letter.FileHash, fromRequest = letter.DocumentRequestId != null,
            }), ct);

        // The reference travels in a header as well as the filename so a caller that streams the
        // body straight to disk can still record which document it got.
        Response.Headers["X-Letter-Reference"] = letter.ReferenceNumber;
        Response.Headers["X-Letter-Hash"] = letter.FileHash;
        return File(result.Pdf!, "application/pdf", $"{letter.ReferenceNumber}.pdf");
    }

    /// <summary>
    /// Closes the HR Request Centre ticket the ESS request raised, so HR works one queue rather
    /// than a letters queue beside the SAL-CERT queue that already existed.
    /// </summary>
    private async Task CloseTicketAsync(Guid tenantId, DocumentRequest request, string status, string note, CancellationToken ct)
    {
        if (request.HrRequestId is not Guid ticketId) return;

        var ticket = await _db.HRRequests.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == ticketId, ct);
        if (ticket is null) return;

        ticket.Status = status;
        _db.HRRequestComments.Add(new HRRequestComment
        {
            TenantId = tenantId,
            HRRequestId = ticketId,
            EmployeeId = request.EmployeeId,
            UserId = this.GetUserId(),
            Comment = note,
            AuthorType = "HR",
        });
        _db.EmployeeNotifications.Add(new EmployeeNotification
        {
            TenantId = tenantId,
            EmployeeId = request.EmployeeId,
            NotificationType = status == "Resolved" ? "Success" : "Info",
            Title = status == "Resolved" ? "Your document is ready" : "Your document request was declined",
            Body = note,
        });
    }

    private Task Audit(Guid tenantId, string action, string entity, string? entityId, string? metadata, CancellationToken ct) =>
        _audit.WriteAsync(action, entity, entityId,
            new RequestContext(HttpContext.Connection.RemoteIpAddress?.ToString(),
                Request.Headers.UserAgent.ToString(), this.GetUserId(), tenantId),
            metadata, ct);

    private static object ToTemplateDto(HrLetterTemplate t) => new
    {
        id = t.Id,
        companyId = t.CompanyId,
        letterType = t.LetterType,
        nameEn = t.NameEn,
        nameAr = t.NameAr,
        language = t.Language,
        titleEn = t.TitleEn,
        titleAr = t.TitleAr,
        bodyEn = t.BodyEn,
        bodyAr = t.BodyAr,
        closingEn = t.ClosingEn,
        closingAr = t.ClosingAr,
        isActive = t.IsActive,
        isSystemDefault = t.IsSystemDefault,
        version = t.Version,
        updatedAtUtc = t.UpdatedAtUtc,
    };
}

public record UpdateLetterTemplateRequest(
    string Language,
    string? NameEn,
    string? NameAr,
    string? TitleEn,
    string? TitleAr,
    string? BodyEn,
    string? BodyAr,
    string? ClosingEn,
    string? ClosingAr,
    bool? IsActive
);

public record IssueLetterRequest(
    int EmployeeId,
    string LetterType,
    string Language = HrLetterLanguages.Bilingual,
    string Purpose = "",
    string AddresseeName = ""
);

public record IssueForRequestBody(string? LetterType, string? Language);

public record DeclineRequestBody(string Reason);

public record IssuedLetterListItem(
    Guid Id, string ReferenceNumber, string LetterType, int EmployeeId, string EmployeeCode,
    string EmployeeName, string Language, string Purpose, string AddresseeName,
    string IssuedByName, string IssuedByTitle, DateTime IssuedAtUtc,
    string FileHash, int FileSizeBytes, bool FromEmployeeRequest
);

public record DocumentRequestListItem(
    Guid Id, int EmployeeId, string EmployeeCode, string EmployeeName,
    string LetterType, string Language, string Purpose, string AddresseeName, string Status,
    DateTime CreatedAtUtc, DateTime? DecidedAtUtc, string DecisionNote,
    Guid? IssuedLetterId, Guid? HrRequestId
);
