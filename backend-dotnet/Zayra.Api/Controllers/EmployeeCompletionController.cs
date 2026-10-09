using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

/// <summary>Uses the existing ESS action and HR approval records; never grants access or activates a hire.</summary>
[ApiController]
[Authorize]
[Route("api/employee-completion")]
public sealed class EmployeeCompletionController(ZayraDbContext db, IDataScopeService scopeService) : ControllerBase
{
    public const string Category = "EmployeeProfileCompletion";
    public const string RequestReason = "Employee profile completion";
    private static readonly Dictionary<string, int> Fields = new(StringComparer.Ordinal)
    {
        ["preferredName"] = 120, ["personalEmail"] = 180, ["phone"] = 60,
        ["maritalStatus"] = 60, ["emergencyContactName"] = 180, ["emergencyContactPhone"] = 60,
    };

    internal static Guid ActionId(Guid tenantId, int employeeId) => new(
        SHA256.HashData(Encoding.UTF8.GetBytes($"{Category}:{tenantId:D}:{employeeId}"))[..16]);

    [HttpGet("{employeeId:int}")]
    [HasPermission("employees.write")]
    public async Task<ActionResult<EmployeeCompletionDto>> Get(int employeeId, CancellationToken ct)
    {
        var employee = await ManagedEmployee(employeeId, ct);
        if (employee is null) return NotFound();
        return Ok(await Status(employee, includeProfile: false, ct));
    }

    [HttpPost("{employeeId:int}")]
    [HasPermission("employees.write")]
    public async Task<ActionResult<EmployeeCompletionDto>> Request(int employeeId, CancellationToken ct)
    {
        var employee = await ManagedEmployee(employeeId, ct);
        if (employee is null) return NotFound();
        var id = ActionId(employee.TenantId!.Value, employee.Id);
        if (!await db.EmployeeActionItems.AnyAsync(x => x.TenantId == employee.TenantId!.Value && x.Id == id, ct))
        {
            db.EmployeeActionItems.Add(new EmployeeActionItem
            {
                Id = id, TenantId = employee.TenantId!.Value, EmployeeId = employee.Id,
                Category = Category, Title = "Complete your employee details", Status = "Open",
            });
            db.EmployeeSelfServiceAuditLogs.Add(new EmployeeSelfServiceAuditLog
            {
                TenantId = employee.TenantId!.Value, EmployeeId = employee.Id, UserId = UserId(),
                Action = "employee.completion.requested", EntityName = "EmployeeActionItem", EntityId = id.ToString(),
            });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                // Concurrent requests share the same primary key: one task, one successful request audit.
                db.ChangeTracker.Clear();
                if (!await db.EmployeeActionItems.AnyAsync(x => x.TenantId == employee.TenantId!.Value && x.Id == id, ct)) throw;
            }
        }
        return Ok(await Status(employee, includeProfile: false, ct));
    }

    [HttpGet("/api/ess/employee-completion")]
    [HasPermission("ess.read")]
    public async Task<ActionResult<EmployeeCompletionDto>> My(CancellationToken ct)
    {
        var employee = await OwnEmployee(ct);
        if (employee is null) return NotFound();
        return Ok(await Status(employee, includeProfile: true, ct));
    }

    [HttpPost("/api/ess/employee-completion/profile")]
    [HasPermission("ess.write")]
    public async Task<ActionResult<EmployeeCompletionDto>> Submit(EmployeeCompletionRequest request, CancellationToken ct)
    {
        var employee = await OwnEmployee(ct);
        if (employee is null) return NotFound();
        var taskId = ActionId(employee.TenantId!.Value, employee.Id);
        var task = await db.EmployeeActionItems.FirstOrDefaultAsync(x => x.TenantId == employee.TenantId!.Value && x.Id == taskId, ct);
        if (task is null) return Conflict(new { message = "HR has not requested your employee details yet." });
        if (request.Changes is null || request.Changes.Count == 0 || request.Changes.Keys.Any(k => !Fields.ContainsKey(k)))
            return BadRequest(new { message = "Only contact and emergency-contact details can be submitted here." });
        var changes = request.Changes.ToDictionary(x => x.Key, x => (x.Value ?? "").Trim());
        if (changes.Any(x => x.Value.Length > Fields[x.Key] || x.Value.Any(char.IsControl)))
            return BadRequest(new { message = "A field is too long or contains unsupported characters." });
        if (changes.TryGetValue("personalEmail", out var email) && email.Length > 0 && !new EmailAddressAttribute().IsValid(email))
            return BadRequest(new { message = "Enter a valid personal email address." });
        if (changes.TryGetValue("maritalStatus", out var marital) && marital.Length > 0 && !new[] { "Single", "Married", "Divorced", "Widowed" }.Contains(marital))
            return BadRequest(new { message = "Choose a listed marital status." });
        var previous = await db.EmployeeProfileChangeRequests.AsNoTracking()
            .Where(x => x.TenantId == employee.TenantId!.Value && x.EmployeeId == employee.Id && x.Reason == RequestReason)
            .OrderByDescending(x => x.CreatedAtUtc).FirstOrDefaultAsync(ct);
        if (previous?.Status == "PendingHR")
            return Conflict(new { message = "Your details are already awaiting HR review." });
        if (previous?.Status == "Approved")
            return Conflict(new { message = "Your onboarding details have already been approved. Contact HR for further changes." });

        var requestId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{taskId:D}:{previous?.Id.ToString() ?? "first"}"))[..16]);
        db.EmployeeProfileChangeRequests.Add(new EmployeeProfileChangeRequest
        {
            Id = requestId,
            TenantId = employee.TenantId!.Value, EmployeeId = employee.Id, CreatedBy = UserId(),
            Reason = RequestReason, RequestedChangesJson = JsonSerializer.Serialize(changes), ContainsSensitiveFields = false,
        });
        db.EmployeeSelfServiceAuditLogs.Add(new EmployeeSelfServiceAuditLog
        {
            TenantId = employee.TenantId!.Value, EmployeeId = employee.Id, UserId = UserId(),
            Action = "employee.completion.submitted", EntityName = "EmployeeActionItem", EntityId = task.Id.ToString(),
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (!await db.EmployeeProfileChangeRequests.AnyAsync(x => x.TenantId == employee.TenantId!.Value && x.Id == requestId, ct)) throw;
            return Conflict(new { message = "Your details are already awaiting HR review." });
        }
        return Ok(await Status(employee, includeProfile: true, ct));
    }

    private async Task<Employee?> ManagedEmployee(int employeeId, CancellationToken ct)
    {
        if (!User.HasClaim("permission", "employees.write") || !Guid.TryParse(User.FindFirstValue("tenant_id"), out var tenantId)) return null;
        var scope = await scopeService.ResolveAsync(User, tenantId, ct);
        if (!scope.CanAccessEmployee(employeeId)) return null;
        // Normal query filters retain the company boundary as well as tenant scope.
        return await db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == employeeId && !x.IsDeleted, ct);
    }

    private async Task<Employee?> OwnEmployee(CancellationToken ct)
    {
        if (User.FindFirstValue("access_mode") is "NoLogin" or "KioskOnly" ||
            !Guid.TryParse(User.FindFirstValue("tenant_id"), out var tenantId)) return null;
        var employeeId = await CallerEmployeeResolver.ResolveAsync(db, User, tenantId, ct);
        return employeeId is null ? null : await db.Employees.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == employeeId && !x.IsDeleted
                && (x.Status == "Active" || x.Status == "Invited"), ct);
    }

    private Guid? UserId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private async Task<EmployeeCompletionDto> Status(Employee employee, bool includeProfile, CancellationToken ct)
    {
        var taskId = ActionId(employee.TenantId!.Value, employee.Id);
        var requested = await db.EmployeeActionItems.AnyAsync(x => x.TenantId == employee.TenantId!.Value && x.Id == taskId, ct);
        var latest = requested ? await db.EmployeeProfileChangeRequests.AsNoTracking()
            .Where(x => x.TenantId == employee.TenantId!.Value && x.EmployeeId == employee.Id && x.Reason == RequestReason)
            .OrderByDescending(x => x.CreatedAtUtc).FirstOrDefaultAsync(ct) : null;
        var changes = latest is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(latest.RequestedChangesJson)?
            .Where(x => Fields.ContainsKey(x.Key)).ToDictionary(x => x.Key, x => x.Value);
        var profile = includeProfile ? new Dictionary<string, string>
        {
            ["preferredName"] = employee.PreferredName, ["personalEmail"] = employee.PersonalEmail,
            ["phone"] = employee.Phone, ["maritalStatus"] = employee.MaritalStatus,
            ["emergencyContactName"] = employee.EmergencyContactName, ["emergencyContactPhone"] = employee.EmergencyContactPhone,
        } : null;
        // Availability is informative only; authentication and invitation eligibility remain authoritative.
        var available = (employee.Status is "Active" or "Invited") && employee.UserAccountId is not null;
        var reviewNote = latest?.Status == "Rejected" ? await db.EmployeeNotifications.AsNoTracking()
            .Where(x => x.TenantId == employee.TenantId!.Value && x.EmployeeId == employee.Id && x.Id == latest.Id && x.NotificationType == Category)
            .Select(x => x.Body).FirstOrDefaultAsync(ct) : null;
        return new(requested, latest?.Status ?? (requested ? "Open" : "NotRequested"), available, latest?.Id, changes, profile, reviewNote);
    }
}

public record EmployeeCompletionRequest(Dictionary<string, string?> Changes);
public record EmployeeCompletionDto(bool Requested, string Status, bool SelfServiceAvailable, Guid? RequestId,
    Dictionary<string, string>? Changes, Dictionary<string, string>? Profile, string? ReviewNote);
