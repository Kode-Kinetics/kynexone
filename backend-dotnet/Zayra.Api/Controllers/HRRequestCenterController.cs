using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
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
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);

        var query = _db.HRRequests.Where(r => r.TenantId == tenantId);
        if (!scope.IsUnrestricted)
            query = query.Where(r => scope.AllowedEmployeeIds!.Contains(r.EmployeeId));
        if (employeeId.HasValue) query = query.Where(r => r.EmployeeId == employeeId.Value);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(r => r.Status == status);
        if (!string.IsNullOrWhiteSpace(priority)) query = query.Where(r => r.Priority == priority);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(r => r.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(new PagedResult<HRRequest>(items, total, page, pageSize));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var request = await _db.HRRequests
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct);
        if (request is null) return NotFound();

        var comments = await _db.HRRequestComments
            .Where(c => c.TenantId == tenantId && c.HRRequestId == id)
            .OrderBy(c => c.CreatedAtUtc)
            .ToListAsync(ct);

        var attachments = await _db.HRRequestAttachments
            .Where(a => a.TenantId == tenantId && a.HRRequestId == id)
            .ToListAsync(ct);

        return Ok(new { request, comments, attachments });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateHRRequestBody req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var userId = this.GetUserId();

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

        request.Status = req.Status;
        await _db.SaveChangesAsync(ct);
        return Ok(request);
    }

    // ── Comments ────────────────────────────────────────────────────────────

    [HttpPost("{id:guid}/comments")]
    public async Task<IActionResult> AddComment(Guid id, [FromBody] AddCommentRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var userId = this.GetUserId();
        var requestExists = await _db.HRRequests
            .AnyAsync(r => r.Id == id && r.TenantId == tenantId, ct);
        if (!requestExists) return NotFound();

        var comment = new HRRequestComment
        {
            TenantId = tenantId.Value,
            HRRequestId = id,
            EmployeeId = req.EmployeeId,
            UserId = userId,
            Comment = req.Comment,
            AuthorType = "HR",
            AuthorName = User.FindFirstValue(ClaimTypes.Name) ?? User.FindFirstValue("name") ?? "HR",
        };

        _db.HRRequestComments.Add(comment);
        // A reply from HR moves an Open ticket into "In Progress" so the SLA/response
        // indicators reflect that HR has engaged.
        var ticket = await _db.HRRequests.FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct);
        if (ticket is not null && ticket.Status == "Open")
            ticket.Status = "In Progress";
        // Notify the employee in their self-service feed that HR replied.
        if (ticket is not null)
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

    [HttpGet("dashboard")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> Dashboard(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var open = await _db.HRRequests.CountAsync(r => r.TenantId == tenantId && r.Status == "Open", ct);
        var inProgress = await _db.HRRequests.CountAsync(r => r.TenantId == tenantId && r.Status == "InProgress", ct);
        var resolved = await _db.HRRequests.CountAsync(r => r.TenantId == tenantId && r.Status == "Resolved", ct);
        var overdue = await _db.HRRequests.CountAsync(r =>
            r.TenantId == tenantId && r.Status != "Resolved" && r.Status != "Closed"
            && r.DueAtUtc < DateTime.UtcNow, ct);

        var recentRequests = await _db.HRRequests
            .Where(r => r.TenantId == tenantId)
            .OrderByDescending(r => r.CreatedAtUtc)
            .Take(5)
            .ToListAsync(ct);

        return Ok(new { open, inProgress, resolved, overdue, recentRequests });
    }
}

public record CreateHRCategoryRequest(string Name, string Code, int? DefaultSlaHours);
public record CreateHRSlaRequest(Guid? CategoryId, string Priority, int SlaHours);
public record CreateHRRequestBody(int EmployeeId, Guid? CategoryId, string? CategoryName, string Subject, string Description, string? Priority);
public record UpdateHRStatusRequest(string Status);
public record AddCommentRequest(int EmployeeId, string Comment);
