using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Leave;
using Zayra.Api.Data;
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
    private readonly ZayraDbContext _db;
    private readonly ILeaveService _leave;
    private readonly IDataScopeService _scope;

    public LeaveSliceSplitReportController(ZayraDbContext db, ILeaveService leave, IDataScopeService scope)
    {
        _db = db;
        _leave = leave;
        _scope = scope;
    }

    [HttpGet]
    [HasPermission("payroll.read")]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        // Only employees the caller can see: the company filter on Employees, then the caller's data scope.
        var visible = await _db.Employees.AsNoTracking().Where(e => e.TenantId == tenantId).Select(e => e.Id).ToListAsync(ct);
        var scope = await _scope.ResolveAsync(User, tenantId.Value, ct);
        if (!scope.IsUnrestricted) visible = visible.Where(scope.AllowedEmployeeIds!.Contains).ToList();
        var report = await _leave.BuildSliceSplitReportAsync(tenantId.Value, visible, ct);
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
