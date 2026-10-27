using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Employees.Onboarding;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Application.Onboarding;

/// <summary>
/// The two calls the employee create/update path makes: check the chosen hire template (same refusal text as every other
/// unavailable reference, so a foreign or out-of-scope id confirms nothing) and raise the hire's checklist.
/// </summary>
public static class HireTemplateHook
{
    public static async Task<HireTemplate?> RequireAsync(ZayraDbContext db, Guid tenantId, Guid? companyId, Guid? hireTemplateId, CancellationToken ct)
    {
        if (hireTemplateId is not Guid id) return null;
        // The company scope filter on HireTemplate hides an out-of-scope company's template; tenant is pinned explicitly.
        var template = await db.HireTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TenantId == tenantId && t.Id == id && t.IsActive && !t.IsDeleted && t.CompanyId == companyId, ct);
        if (template is null)
            throw new EmployeeValidationException(422, EmployeeProblemCodes.InvalidReference,
                new Dictionary<string, string[]> { ["hireTemplateId"] = [EmployeeProblemCodes.InvalidReference] },
                "The selected hire template is not available.", "hireTemplateId");
        return template;
    }

    /// <summary>Raises the checklist (the template's, else the company default). Idempotent; never fails the hire itself.</summary>
    public static async Task GenerateAsync(ZayraDbContext db, Guid tenantId, Employee employee, HireTemplate? template, bool startDateKnown, CancellationToken ct)
    {
        if (employee.CompanyId is not Guid companyId) return;
        try
        {
            var generator = new ChecklistGenerator(db);
            var checklistId = await generator.ResolveChecklistAsync(tenantId, companyId, template?.ChecklistId, ct);
            if (checklistId is Guid id) await generator.EnsureAsync(tenantId, employee, id, startDateKnown, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Tasks are follow-up work: a failure here must not undo or hide a hire that already committed. Saving the
            // draft again re-runs this (idempotent), which repairs it.
        }
    }
}
