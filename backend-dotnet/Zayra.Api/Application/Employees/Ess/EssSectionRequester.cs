using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Application.Employees.Ess;

/// <summary>
/// Contract C-1. Asks an employee to complete named profile sections through the one action item the original completion
/// request already uses (deterministic id per employee), so there is still one task, one audit row and no second queue.
/// </summary>
public sealed class EssSectionRequester(ZayraDbContext db) : IEssSectionRequester
{
    public async Task RequestAsync(Guid tenantId, int employeeId, IReadOnlyList<string> sections, CancellationToken ct)
    {
        var unknown = sections.Where(s => !EssSectionKeys.IsKnown(s)).ToList();
        if (unknown.Count > 0) throw new ArgumentException($"Unknown profile section(s): {string.Join(", ", unknown)}.", nameof(sections));
        var wanted = EssSectionKeys.All.Where(sections.Contains).ToList();
        var id = EmployeeCompletionController.ActionId(tenantId, employeeId);
        var item = await db.EmployeeActionItems.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
        if (item is null)
        {
            item = new EmployeeActionItem
            {
                Id = id, TenantId = tenantId, EmployeeId = employeeId, Category = EmployeeCompletionController.Category,
                Title = "Complete your employee details", Status = "Open",
            };
            db.EmployeeActionItems.Add(item);
        }
        var existing = (item.RequestedSections ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Additive: asking again for another section never drops one already asked for. An item that asked for "every missing
        // section" (null) is narrowed to what is named.
        var merged = EssSectionKeys.All.Where(k => wanted.Contains(k) || existing.Contains(k)).ToList();
        item.RequestedSections = wanted.Count == 0 ? item.RequestedSections : string.Join(',', item.RequestedSections is null ? wanted : merged);
        item.Status = "Open";
        db.EmployeeSelfServiceAuditLogs.Add(new EmployeeSelfServiceAuditLog
        {
            TenantId = tenantId, EmployeeId = employeeId, Action = "employee.completion.sections_requested",
            EntityName = nameof(EmployeeActionItem), EntityId = id.ToString(),
        });
        await db.SaveChangesAsync(ct);
    }
}
