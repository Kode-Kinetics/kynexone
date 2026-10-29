using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Audit;

/// <summary>
/// One place for settings-style controllers to record who changed what. Entries are added to the
/// caller's unit of work (no save here) so the change and its audit row commit together.
/// </summary>
public static class AdminChangeAudit
{
    public static void RecordChange(this ControllerBase c, ZayraDbContext db, Guid tenantId, Guid? companyId,
        string entityType, string entityId, string action, object? before, object? after)
    {
        var user = c.User;
        Guid? uid = Guid.TryParse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")?.Value, out var id) ? id : null;
        db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId = tenantId,
            CompanyId = companyId,
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            OldValuesJson = before is null ? string.Empty : JsonSerializer.Serialize(before),
            NewValuesJson = after is null ? string.Empty : JsonSerializer.Serialize(after),
            PerformedBy = uid,
            PerformedByName = user.FindFirst(ClaimTypes.Name)?.Value ?? user.FindFirst("name")?.Value ?? string.Empty,
            IpAddress = c.HttpContext?.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
        });
    }
}
