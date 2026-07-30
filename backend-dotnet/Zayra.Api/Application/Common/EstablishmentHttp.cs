using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Organization;
using Zayra.Api.Infrastructure.Employees;

namespace Zayra.Api.Application.Common;

/// <summary>
/// The ONE place the ESTABLISHMENT_BUDGET_EXCEEDED HTTP contract is rendered — every catch site
/// uses this so the popup payload is identical across create / update / status / transfer /
/// approval / draft paths. canEditEstablishment is claim-derived here (services never see the
/// principal) and is DISPLAY-ONLY: it gates the popup's deep-link button, never enforcement.
/// </summary>
public static class EstablishmentHttp
{
    public const string ErrorCode = "ESTABLISHMENT_BUDGET_EXCEEDED";
    public const string WritePermission = "organization.establishment.write";

    // Returns ObjectResult (not IActionResult) so the one helper serves both IActionResult and
    // ActionResult<T> action signatures via the implicit ActionResult conversion.
    public static ObjectResult EstablishmentConflict(this ControllerBase controller, EstablishmentBudgetExceededException ex)
        => controller.Conflict(new
        {
            error = ErrorCode,
            departmentId = ex.Block.DepartmentId,
            departmentName = ex.Block.DepartmentName,
            staffingLevelId = ex.Block.StaffingLevelId,
            levelCode = ex.Block.LevelCode,
            levelNameEn = ex.Block.LevelNameEn,
            levelNameAr = ex.Block.LevelNameAr,
            budgeted = ex.Block.Budgeted,
            current = ex.Block.Current,
            attempted = ex.Block.Attempted,
            exitingIncumbents = ex.Block.ExitingIncumbents,
            canEditEstablishment = controller.User.Claims.Any(c =>
                c.Type == "permission" && string.Equals(c.Value, WritePermission, StringComparison.OrdinalIgnoreCase))
        });

    public const string NotActivatableError = "employee_not_activatable";

    /// <summary>
    /// The ONE place the employee_not_activatable HTTP contract is rendered (§5.5). 422 Unprocessable
    /// Entity — a precondition/validation failure, distinct from the establishment 409. Rendered
    /// identically across every Active path (ChangeStatus / Activate / ApproveDraft / bulk-activate).
    /// </summary>
    public static ObjectResult NotActivatable(this ControllerBase controller, EmployeeActivationBlockedException ex)
    {
        var r = ex.Readiness;
        var p = ex.Policy;
        var payload = new
        {
            error = NotActivatableError,
            employeeId = ex.EmployeeId,
            message = ex.Message,
            policy = new { countryCode = p.CountryCode, tier = p.Tier, sources = p.Sources },
            progress = new { present = r.Present.Count, requiredTotal = r.RequiredTotal },
            blocking = r.Blocking.Select(ToItem),
            payBlocking = r.PayBlocking.Select(ToItem),
            recommended = r.Recommended.Select(ToItem),
            disclaimer = p.Disclaimer,
        };
        return controller.UnprocessableEntity(payload);
    }

    private static object ToItem(ReadinessItem i) => new
    {
        key = i.Key,
        label = i.Label,
        category = i.Category,
        reason = i.Reason,
        jurisdiction = i.Jurisdiction,
        gate = i.Gate,
        fix = i.FixKind == "document"
            ? (object)new { kind = i.FixKind, documentType = i.DocumentType }
            : new { kind = i.FixKind, target = i.FixTarget },
    };
}
