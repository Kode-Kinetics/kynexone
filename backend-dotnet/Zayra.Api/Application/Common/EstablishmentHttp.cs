using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Organization;

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
}
