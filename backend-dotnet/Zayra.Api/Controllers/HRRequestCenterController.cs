using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/hr-requests")]
[Authorize]
public class HRRequestCenterController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IDataScopeService _scopeService;

    public HRRequestCenterController(ZayraDbContext db, IDataScopeService scopeService)
    {
        _db = db;
        _scopeService = scopeService;
    }

    // ── Categories ──────────────────────────────────────────────────────────

    [HttpGet("categories")]
    public async Task<IActionResult> ListCategories(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var items = await _db.HRRequestCategories
            .Where(c => c.TenantId == tenantId && c.IsActive)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpPost("categories")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> CreateCategory([FromBody] CreateHRCategoryRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var cat = new HRRequestCategory
        {
            TenantId = tenantId.Value,
            Name = req.Name,
            Code = req.Code.ToUpperInvariant(),
            DefaultSlaHours = req.DefaultSlaHours ?? 48,
            IsActive = true
        };

        _db.HRRequestCategories.Add(cat);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/hr-requests/categories/{cat.Id}", cat);
    }

    // ── SLAs ────────────────────────────────────────────────────────────────

    [HttpGet("slas")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> ListSlas(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var items = await _db.HRRequestSLAs
            .Where(s => s.TenantId == tenantId && s.IsActive)
            .OrderBy(s => s.Priority)
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpPost("slas")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> CreateSla([FromBody] CreateHRSlaRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var sla = new HRRequestSLA
        {
            TenantId = tenantId.Value,
            CategoryId = req.CategoryId,
            Priority = req.Priority,
            SlaHours = req.SlaHours,
            IsActive = true
        };

        _db.HRRequestSLAs.Add(sla);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/hr-requests/slas/{sla.Id}", sla);
    }

    // ── Requests ────────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int? employeeId,
        [FromQuery] string? status,
        [FromQuery] string? priority,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var reader = await ReaderAsync(tenantId.Value, ct);
        var query = ReadableRequests(tenantId.Value, reader);
        if (employeeId.HasValue) query = query.Where(r => r.EmployeeId == employeeId.Value);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(r => r.Status == status);
        if (!string.IsNullOrWhiteSpace(priority)) query = query.Where(r => r.Priority == priority);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(r => r.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(new PagedResult<HrRequestDto>(items.Select(r => HrRequestDto.Project(r, reader.MaySeeDetails(r))).ToList(), total, page, pageSize));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var request = await _db.HRRequests
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct);
        if (request is null) return NotFound();

        if (request.JawazatDataJson is not null && (request.CompanyId is null || !this.GetEntityScope().CanAccessCompany(request.CompanyId)))
            return Forbid();

        var reader = await ReaderAsync(tenantId.Value, ct);
        if (!reader.MayList(request))
            return Forbid();
        if (!reader.MaySeeDetails(request))
            // Metadata only: the free text, the conversation and the attachments are for HR and the requester.
            return Ok(new { request = HrRequestDto.Project(request, false), comments = Array.Empty<HRRequestComment>(), attachments = Array.Empty<HRRequestAttachment>() });

        var comments = await _db.HRRequestComments
            .Where(c => c.TenantId == tenantId && c.HRRequestId == id)
            .OrderBy(c => c.CreatedAtUtc)
            .ToListAsync(ct);

        var attachments = await _db.HRRequestAttachments
            .Where(a => a.TenantId == tenantId && a.HRRequestId == id)
            .ToListAsync(ct);

        return Ok(new { request = HrRequestDto.Project(request, true), comments, attachments });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateHRRequestBody req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var userId = this.GetUserId();
        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        if (!scope.CanAccessEmployee(req.EmployeeId)) return Forbid();
        if (!await _db.Employees.AnyAsync(e => e.TenantId == tenantId && e.Id == req.EmployeeId && !e.IsDeleted, ct))
            return BadRequest(new { message = "Employee not found." });

        var slaHours = 48;
        if (req.CategoryId.HasValue)
        {
            var sla = await _db.HRRequestSLAs
                .Where(s => s.TenantId == tenantId && s.CategoryId == req.CategoryId
                    && s.Priority == (req.Priority ?? "Normal") && s.IsActive)
                .FirstOrDefaultAsync(ct);
            sla ??= await _db.HRRequestSLAs
                .Where(s => s.TenantId == tenantId && s.CategoryId == req.CategoryId && s.IsActive)
                .FirstOrDefaultAsync(ct);
            if (sla is not null) slaHours = sla.SlaHours;
        }

        var hrRequest = new HRRequest
        {
            TenantId = tenantId.Value,
            EmployeeId = req.EmployeeId,
            CategoryId = req.CategoryId,
            CategoryName = req.CategoryName ?? string.Empty,
            Subject = req.Subject,
            Description = req.Description,
            Priority = req.Priority ?? "Normal",
            Status = "Open",
            DueAtUtc = DateTime.UtcNow.AddHours(slaHours),
            CreatedBy = userId
        };

        _db.HRRequests.Add(hrRequest);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/hr-requests/{hrRequest.Id}", hrRequest);
    }

    [HttpPatch("{id:guid}/status")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> UpdateStatus(Guid id, [FromBody] UpdateHRStatusRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var request = await _db.HRRequests
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct);
        if (request is null) return NotFound();
        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        if (!scope.CanAccessEmployee(request.EmployeeId)) return Forbid();
        if (request.JawazatDataJson is not null)
        {
            if (request.CompanyId is null || !this.GetEntityScope().CanAccessCompany(request.CompanyId)) return Forbid();
            return Conflict(new { code = "governed_request", message = "Jawazat request states can only change through their governed workflow." });
        }

        request.Status = req.Status;
        await _db.SaveChangesAsync(ct);
        return Ok(request);
    }

    // ── Comments ────────────────────────────────────────────────────────────

    [HttpPost("{id:guid}/comments")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> AddComment(Guid id, [FromBody] AddCommentRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var userId = this.GetUserId();
        var ticket = await _db.HRRequests
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct);
        if (ticket is null) return NotFound();
        if (ticket.JawazatDataJson is not null && (ticket.CompanyId is null || !this.GetEntityScope().CanAccessCompany(ticket.CompanyId)))
            return Forbid();
        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        if (!scope.CanAccessEmployee(ticket.EmployeeId)) return Forbid();

        var comment = new HRRequestComment
        {
            TenantId = tenantId.Value,
            HRRequestId = id,
            EmployeeId = ticket.EmployeeId,
            UserId = userId,
            Comment = req.Comment,
            AuthorType = "HR",
            AuthorName = User.FindFirstValue(ClaimTypes.Name) ?? User.FindFirstValue("name") ?? "HR",
        };

        _db.HRRequestComments.Add(comment);
        // A reply from HR moves an Open ticket into "InProgress" so the SLA/response
        // indicators reflect that HR has engaged. (Canonical status token — no space —
        // matching the dashboard count, status filters and badges across the app.)
        if (ticket.JawazatDataJson is null && ticket.Status == "Open")
            ticket.Status = "InProgress";
        // Notify the employee in their self-service feed that HR replied.
        _db.EmployeeNotifications.Add(new EmployeeNotification
        {
            TenantId = tenantId.Value, EmployeeId = ticket.EmployeeId, NotificationType = "Info",
            Title = "HR replied to your request",
            Body = $"HR responded to \"{ticket.Subject}\". Open it to read the reply.",
        });
        await _db.SaveChangesAsync(ct);
        return Created($"/api/hr-requests/{id}/comments/{comment.Id}", comment);
    }

    // ── Dashboard ───────────────────────────────────────────────────────────

    // The HR desk's queue. The role list resolved to employees.read, which every staff role holds, so a
    // Recruiter, Finance or Auditor read the five latest requests org-wide with their free text and
    // Jawazat travel data. employees.write is what the named HR roles hold.
    [HttpGet("dashboard")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    [HasPermission("employees.write")]
    public async Task<IActionResult> Dashboard(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var reader = await ReaderAsync(tenantId.Value, ct);
        var dashboardQuery = ReadableRequests(tenantId.Value, reader);
        var open = await dashboardQuery.CountAsync(r => r.Status == "Open", ct);
        var inProgress = await dashboardQuery.CountAsync(r => r.Status == "InProgress", ct);
        var resolved = await dashboardQuery.CountAsync(r => r.Status == "Resolved", ct);
        var overdue = await dashboardQuery.CountAsync(r =>
            r.Status != "Resolved" && r.Status != "Closed" && r.DueAtUtc < DateTime.UtcNow, ct);

        var recentRequests = await dashboardQuery
            .OrderByDescending(r => r.CreatedAtUtc)
            .Take(5)
            .ToListAsync(ct);

        return Ok(new { open, inProgress, resolved, overdue,
            recentRequests = recentRequests.Select(r => HrRequestDto.Project(r, reader.MaySeeDetails(r))).ToList() });
    }

    /// <summary>
    /// Who is reading the request queue, and how much of it they may see.
    /// <list type="bullet">
    /// <item>HR (employees.write): the existing data and company scope, with every field.</item>
    /// <item>A line manager (a restricted, team data scope): their team's requests by the same data-scope
    /// rules, but the free text and Jawazat data only on their own requests.</item>
    /// <item>Anyone else, including roles whose data scope is org-wide only because they read employee
    /// records (Recruiter, Finance, Payroll, Compliance, Auditor...): their own requests only.</item>
    /// </list>
    /// </summary>
    private sealed record RequestReader(bool IsHr, DataScope Scope, int? OwnEmployeeId)
    {
        public bool MaySeeDetails(HRRequest r) => IsHr || (OwnEmployeeId.HasValue && r.EmployeeId == OwnEmployeeId.Value);

        public bool MayList(HRRequest r) => IsHr
            ? Scope.CanAccessEmployee(r.EmployeeId)
            : (OwnEmployeeId.HasValue && r.EmployeeId == OwnEmployeeId.Value)
              || (!Scope.IsUnrestricted && Scope.CanAccessEmployee(r.EmployeeId));
    }

    private async Task<RequestReader> ReaderAsync(Guid tenantId, CancellationToken ct)
    {
        var scope = await _scopeService.ResolveAsync(User, tenantId, ct);
        var isHr = User.HasPermission("employees.write");
        int? own = scope.CallerEmployeeId;
        if (own is null && int.TryParse(User.FindFirstValue("employee_id"), out var claimed)) own = claimed;
        if (own is null && this.GetUserId() is { } uid)
            own = await _db.Employees.AsNoTracking()
                .Where(e => e.TenantId == tenantId && e.UserAccountId == uid && !e.IsDeleted)
                .Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
        return new RequestReader(isHr, scope, own);
    }

    private IQueryable<HRRequest> ReadableRequests(Guid tenantId, RequestReader reader)
    {
        var query = ScopeGovernedRequests(_db.HRRequests.Where(r => r.TenantId == tenantId));
        var own = reader.OwnEmployeeId ?? int.MinValue;
        if (reader.IsHr)
            return reader.Scope.IsUnrestricted ? query : query.Where(r => reader.Scope.AllowedEmployeeIds!.Contains(r.EmployeeId));
        if (reader.Scope.IsUnrestricted)
            return query.Where(r => r.EmployeeId == own);
        return query.Where(r => r.EmployeeId == own || reader.Scope.AllowedEmployeeIds!.Contains(r.EmployeeId));
    }

    private IQueryable<HRRequest> ScopeGovernedRequests(IQueryable<HRRequest> query)
    {
        var companyScope = this.GetEntityScope();
        var companies = companyScope.AccessibleCompanyIds;
        return query.Where(r => r.JawazatDataJson == null || (r.CompanyId != null
            && (companyScope.IsGroupLevel || companies.Contains(r.CompanyId.Value))));
    }
}

/// <summary>
/// The HR request as the request center returns it. <see cref="Description"/> and <see cref="JawazatDataJson"/>
/// are filled only for HR and for the request's own employee; for anyone else they are empty and
/// <see cref="DetailsRedacted"/> is true.
/// </summary>
public sealed record HrRequestDto(
    Guid Id, Guid TenantId, int EmployeeId, Guid? CompanyId, Guid? CategoryId, string CategoryName, string Subject,
    string Description, string Priority, string Status, DateTime DueAtUtc, DateTime CreatedAtUtc,
    Guid? ApprovalRequestId, bool IsJawazatRequest, string? JawazatDataJson, Guid? AttachmentDocumentId, bool DetailsRedacted)
{
    public static HrRequestDto Project(HRRequest r, bool withDetails) => new(
        r.Id, r.TenantId, r.EmployeeId, r.CompanyId, r.CategoryId, r.CategoryName, r.Subject,
        withDetails ? r.Description : string.Empty, r.Priority, r.Status, r.DueAtUtc, r.CreatedAtUtc,
        r.ApprovalRequestId, r.JawazatDataJson is not null, withDetails ? r.JawazatDataJson : null,
        withDetails ? r.AttachmentDocumentId : null, !withDetails);
}

public record CreateHRCategoryRequest(string Name, string Code, int? DefaultSlaHours);
public record CreateHRSlaRequest(Guid? CategoryId, string Priority, int SlaHours);
public record CreateHRRequestBody(int EmployeeId, Guid? CategoryId, string? CategoryName, string Subject, string Description, string? Priority);
public record UpdateHRStatusRequest(string Status);
public record AddCommentRequest(int EmployeeId, string Comment);
