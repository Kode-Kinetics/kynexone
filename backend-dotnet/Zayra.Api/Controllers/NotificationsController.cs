using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/notifications")]
[Authorize]
public class NotificationsController : ControllerBase
{
    private readonly ZayraDbContext _db;

    public NotificationsController(ZayraDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> Recent(CancellationToken cancellationToken)
    {
        var tenantId = Guid.Parse(User.FindFirstValue("tenant_id")!);
        var userId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var parsed) ? parsed : (Guid?)null;
        var items = await _db.Notifications
            .Where(x => x.TenantId == tenantId && (x.UserId == null || x.UserId == userId))
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(50)
            .ToListAsync(cancellationToken);
        return Ok(items);
    }

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = Guid.Parse(User.FindFirstValue("tenant_id")!);
        var notification = await _db.Notifications.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, cancellationToken);
        if (notification is null) return NotFound();
        notification.Status = "Read";
        notification.ReadAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead(CancellationToken cancellationToken)
    {
        var tenantId = Guid.Parse(User.FindFirstValue("tenant_id")!);
        var userId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var parsed) ? parsed : (Guid?)null;
        await _db.Notifications
            .Where(x => x.TenantId == tenantId && (x.UserId == null || x.UserId == userId) && x.Status == "Unread")
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.Status, "Read")
                .SetProperty(n => n.ReadAtUtc, DateTime.UtcNow),
            cancellationToken);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Dismiss(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = Guid.Parse(User.FindFirstValue("tenant_id")!);
        var notification = await _db.Notifications.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, cancellationToken);
        if (notification is null) return NotFound();
        _db.Notifications.Remove(notification);
        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    // ── POD-D5: DELIVERY VISIBILITY ──────────────────────────────────────────
    // Before this, a "payslip ready" notice could fail for every employee in a tenant and leave no
    // trace anywhere — SmtpEmailService logged a warning and returned. These endpoints make what
    // did and did not reach people a first-class, queryable fact.
    //
    // Only the MASKED destination is ever returned; DestinationRaw and Body are never projected.

    /// <summary>Per-delivery outcomes for the tenant. Newest first.</summary>
    [HttpGet("deliveries")]
    [HasPermission("notifications.manage")]
    public async Task<IActionResult> Deliveries(
        [FromQuery] string? channel, [FromQuery] string? outcome, [FromQuery] string? eventCode,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] bool onlyProblems = false,
        [FromQuery] int take = 100, CancellationToken cancellationToken = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        // Explicit tenant predicate — never the ambient filter alone.
        var q = _db.NotificationDeliveries.AsNoTracking().Where(x => x.TenantId == tenantId.Value);
        if (!string.IsNullOrWhiteSpace(channel)) q = q.Where(x => x.Channel == channel);
        if (!string.IsNullOrWhiteSpace(outcome)) q = q.Where(x => x.Outcome == outcome);
        if (!string.IsNullOrWhiteSpace(eventCode)) q = q.Where(x => x.EventCode == eventCode);
        if (from is not null) q = q.Where(x => x.CreatedAtUtc >= from.Value);
        if (to is not null) q = q.Where(x => x.CreatedAtUtc <= to.Value);
        if (onlyProblems)
            q = q.Where(x => x.Outcome == DeliveryOutcomes.Failed
                || x.Outcome == DeliveryOutcomes.NotConfigured
                || x.Outcome == DeliveryOutcomes.NoContact
                || x.Outcome == DeliveryOutcomes.Unknown);

        var rows = await q
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(Math.Clamp(take, 1, 500))
            .Select(x => new
            {
                x.Id, x.EventCode, x.EntityName, x.EntityId, x.Channel, x.Outcome, x.AudienceType,
                x.EmployeeId, x.Subject, x.DestinationMasked, x.ProviderName, x.ProviderReference,
                x.ErrorCode, x.ErrorMessage, x.AttemptCount, x.MaxAttempts,
                x.NextAttemptAtUtc, x.LastAttemptAtUtc, x.CompletedAtUtc, x.CreatedAtUtc,
            })
            .ToListAsync(cancellationToken);

        return Ok(rows);
    }

    /// <summary>
    /// Per-channel × outcome rollup plus a single needsAttention flag — the signal an admin surface
    /// can render as a banner, so 500 not_configured rows are SURFACED rather than merely queryable.
    /// </summary>
    [HttpGet("deliveries/summary")]
    [HasPermission("notifications.manage")]
    public async Task<IActionResult> DeliverySummary([FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] string? eventCode, CancellationToken cancellationToken = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var since = from ?? DateTime.UtcNow.AddDays(-30);
        var q = _db.NotificationDeliveries.AsNoTracking()
            .Where(x => x.TenantId == tenantId.Value && x.CreatedAtUtc >= since);
        if (to is not null) q = q.Where(x => x.CreatedAtUtc <= to.Value);
        if (!string.IsNullOrWhiteSpace(eventCode)) q = q.Where(x => x.EventCode == eventCode);

        var grouped = await q
            .GroupBy(x => new { x.Channel, x.Outcome })
            .Select(g => new { g.Key.Channel, g.Key.Outcome, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var problems = grouped
            .Where(g => DeliveryOutcomes.NeedsAttention(g.Outcome))
            .Sum(g => g.Count);

        return Ok(new
        {
            since,
            byChannel = grouped
                .GroupBy(g => g.Channel)
                .Select(g => new
                {
                    channel = g.Key,
                    total = g.Sum(x => x.Count),
                    outcomes = g.ToDictionary(x => x.Outcome, x => x.Count),
                })
                .OrderBy(g => g.channel)
                .ToList(),
            needsAttention = problems > 0,
            needsAttentionCount = problems,
        });
    }

    // ── POD-D5: EMPLOYEE OPT-IN ──────────────────────────────────────────────
    // EmployeeNotificationPreference was modelled with zero readers AND zero writers repo-wide, so
    // the table is empty in every live tenant. Without a write path there is no way for "what the
    // tenant/employee opted into" to ever be true. A MISSING row means short channels are OFF —
    // the CLR defaults (PushEnabled = true, EmailEnabled = true) are not consent.

    public sealed record NotificationPreferenceDto(bool EmailEnabled, bool PushEnabled, bool SmsEnabled, string QuietHoursJson);

    [HttpGet("preferences")]
    public async Task<IActionResult> GetPreferences(CancellationToken cancellationToken)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var employeeId = await ResolveCallerEmployeeIdAsync(tenantId.Value, cancellationToken);
        if (employeeId is null) return NotFound(new { message = "This account is not linked to an employee record." });

        var row = await _db.EmployeeNotificationPreferences.AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId.Value && p.EmployeeId == employeeId.Value, cancellationToken);

        return Ok(row is null
            // Defaults reported to the client match the defaults the dispatcher enforces.
            ? new NotificationPreferenceDto(true, false, false, "{}")
            : new NotificationPreferenceDto(row.EmailEnabled, row.PushEnabled, row.SmsEnabled, row.QuietHoursJson));
    }

    [HttpPut("preferences")]
    public async Task<IActionResult> UpdatePreferences([FromBody] NotificationPreferenceDto request, CancellationToken cancellationToken)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        // Always the CALLER's own employee id — never a body-supplied one (BOLA).
        var employeeId = await ResolveCallerEmployeeIdAsync(tenantId.Value, cancellationToken);
        if (employeeId is null) return NotFound(new { message = "This account is not linked to an employee record." });

        var quietHours = string.IsNullOrWhiteSpace(request.QuietHoursJson) ? "{}" : request.QuietHoursJson;
        if (!IsJsonObject(quietHours)) return BadRequest(new { message = "quietHoursJson must be a JSON object." });

        var row = await _db.EmployeeNotificationPreferences
            .FirstOrDefaultAsync(p => p.TenantId == tenantId.Value && p.EmployeeId == employeeId.Value, cancellationToken);
        if (row is null)
        {
            row = new EmployeeNotificationPreference { TenantId = tenantId.Value, EmployeeId = employeeId.Value };
            _db.EmployeeNotificationPreferences.Add(row);
        }
        row.EmailEnabled = request.EmailEnabled;
        row.PushEnabled = request.PushEnabled;
        row.SmsEnabled = request.SmsEnabled;
        row.QuietHoursJson = quietHours;
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new NotificationPreferenceDto(row.EmailEnabled, row.PushEnabled, row.SmsEnabled, row.QuietHoursJson));
    }

    private static bool IsJsonObject(string value)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(value);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object;
        }
        catch { return false; }
    }

    /// <summary>Caller's own employee id, scoped to the caller's tenant. Mirrors MobileController.</summary>
    private async Task<int?> ResolveCallerEmployeeIdAsync(Guid tenantId, CancellationToken ct)
    {
        if (int.TryParse(User.FindFirstValue("employee_id"), out var empId)) return empId;

        var email = User.FindFirstValue("email") ?? User.FindFirstValue(ClaimTypes.Email) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(email)) return null;
        var normalized = email.Trim().ToUpperInvariant();
        var employee = await _db.Employees.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && !x.IsDeleted
                && (x.WorkEmail.ToUpper() == normalized || x.PersonalEmail.ToUpper() == normalized), ct);
        return employee?.Id;
    }
}
