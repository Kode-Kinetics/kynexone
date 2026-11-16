using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Leave;
using Zayra.Api.Infrastructure.Authorization;

namespace Zayra.Api.Controllers;

/// <summary>
/// Spec M1-M3: the read-only report of multi-month leave deductions written before month slicing, and how each would split.
/// GET only; nothing here changes a row. The owner signs off before any data change is made.
/// </summary>
[ApiController]
[Route("api/payroll/leave-slice-split-report")]
[Authorize]
public class LeaveSliceSplitReportController : ControllerBase
{
    private readonly ILeaveService _leave;
    private readonly IDataScopeService _scope;

    public LeaveSliceSplitReportController(ILeaveService leave, IDataScopeService scope)
    {
        _leave = leave;
        _scope = scope;
    }

    [HttpGet]
    [HasPermission("payroll.read")]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        // The service reads only the employees the caller's company filter shows, narrowed here to their data scope.
        var scope = await _scope.ResolveAsync(User, tenantId.Value, ct);
        var report = await _leave.BuildSliceSplitReportAsync(tenantId.Value, scope.IsUnrestricted ? null : scope.AllowedEmployeeIds, ct);
        return Ok(new
        {
            readOnly = true,
            wouldSplit = report.WouldSplit,
            doNotReconcile = report.DoNotReconcile,
            alreadyDeducted = report.AlreadyDeducted,
            sickScaleRows = report.SickScaleRows,
        });
    }
}
