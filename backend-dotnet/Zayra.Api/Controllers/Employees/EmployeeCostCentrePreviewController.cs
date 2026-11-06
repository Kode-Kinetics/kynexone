using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Http;

namespace Zayra.Api.Controllers.Employees;

/// <summary>
/// Cost centre on transfer, Wave A: <c>GET /api/employees/{id}/cost-centre/preview</c> answers "where would this person's cost go" for a
/// hypothetical department, position or own cost centre, with the same resolver payroll posts with, so the edit form shows the server's answer
/// live instead of guessing. READ-ONLY by construction (<see cref="IEmployeeCostCentrePreviewService"/>), and not refused while the person is in a
/// run awaiting finance review: a preview is not a change.
/// <para>Security (threat model section 2): the plain call needs <c>employees.read</c>; any hypothetical id needs <c>employees.write</c> (it would
/// otherwise map departments to cost centres for readers without organization access). The employee must be in the caller's data scope and
/// company scope, else 404 (a missing id and an out-of-scope one read the same). A department or position outside the employee's company is the
/// same 404 as one that does not exist; a foreign cost centre is simply invalid, with no code or name. Rate-limited per user and tenant.</para>
/// </summary>
[ApiController]
[Route("api/employees")]
[Authorize]
public sealed class EmployeeCostCentrePreviewController(IEmployeeCostCentrePreviewService previews, IDataScopeService scopeService) : ControllerBase
{
    private static readonly object DepartmentNotFound = CostAccountingGuards.Error("department_not_found",
        "That department was not found in this employee's company.", "لم يُعثر على هذا القسم في شركة هذا الموظف.");
    private static readonly object PositionNotFound = CostAccountingGuards.Error("position_not_found",
        "That position was not found in this employee's company.", "لم يُعثر على هذا المنصب في شركة هذا الموظف.");

    [HttpGet("{id:int}/cost-centre/preview")]
    [HasPermission("employees.read")]
    [TenantUserRateLimit("cost-centre-preview")]
    public async Task<ActionResult<EmployeeCostCentrePreviewDto>> Get(int id, [FromQuery] Guid? departmentId, [FromQuery] Guid? positionId,
        [FromQuery] Guid? costCenterId, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tenantId) return Unauthorized();
        if ((departmentId ?? positionId ?? costCenterId) is not null && !User.HasPermission("employees.write")) return Forbid();

        var subject = await previews.FindAsync(tenantId, id, ct);
        if (subject is null) return NotFound();
        var scope = await scopeService.ResolveAsync(User, tenantId, ct);
        if (!scope.CanAccessEmployee(id) || !this.GetEntityScope().CanAccessCompany(subject.CompanyId)) return NotFound();

        var (refusal, preview) = await previews.PreviewAsync(tenantId, subject, departmentId, positionId, costCenterId, ct);
        return refusal switch
        {
            CostCentrePreviewRefusal.DepartmentNotFound => NotFound(DepartmentNotFound),
            CostCentrePreviewRefusal.PositionNotFound => NotFound(PositionNotFound),
            _ => Ok(preview),
        };
    }
}
