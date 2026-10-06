using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Entitlements;
using Zayra.Api.Controllers.Payroll;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Filters;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Ess;

/// <summary>
/// "My deductions / خصوماتي" (Release A slice R3): the caller's own deductions, each with why it is made and what is left
/// to repay. The employee is ALWAYS the caller — resolved from the token's <c>employee_id</c> claim, never from an id the
/// client sends. A colleague's payslip, a slip that is not final yet, and a voided run all answer 404. Closed unless the
/// tenant has release_a on (the /api/ess/deductions prefix by the guard filter, the payslip route by the attribute).
/// </summary>
[ApiController]
[Authorize]
[Route("api/ess")]
[RequireOptInFeature(FeatureKeys.ReleaseA)]
public sealed class EssDeductionsController(ZayraDbContext db) : ControllerBase
{
    private DeductionStatementService Statements => new(db);

    /// <summary>The caller's statements for the last <paramref name="months"/> pay periods and their open loans and advances.</summary>
    [HttpGet("deductions")]
    [HasPermission("ess.read")]
    public async Task<IActionResult> MyDeductions([FromServices] ITenantClock clock, [FromQuery] int months = 6, CancellationToken ct = default)
    {
        if (Caller() is not (Guid tenantId, int employeeId)) return SelfServiceRefusal();
        var today = await clock.TodayAsync(tenantId, ct);
        var details = await Statements.ForEmployeeAsync(tenantId, employeeId, months, DeductionAudience.Employee, today, ct);
        var balances = await Statements.BalancesAsync(tenantId, employeeId, ct);
        return Ok(EmployeeDeductionsDto.From(details, balances));
    }

    /// <summary>The deductions on one of the caller's own final payslips (<paramref name="id"/> is the payslip id ESS lists).</summary>
    [HttpGet("payslips/{id:guid}/deductions")]
    [HasPermission("ess.read")]
    public async Task<IActionResult> PayslipDeductions(Guid id, CancellationToken ct)
    {
        if (Caller() is not (Guid tenantId, int employeeId)) return SelfServiceRefusal();
        // Ownership first: a colleague's slip is indistinguishable from a missing one.
        var own = await db.PayrollSlips.AsNoTracking()
            .AnyAsync(s => s.TenantId == tenantId && s.Id == id && s.EmployeeId == employeeId, ct);
        if (!own) return NotFound();
        var detail = await Statements.DetailForSlipAsync(tenantId, id, DeductionAudience.Employee, ct);
        if (detail is null) return NotFound();
        db.EmployeePayslipAccessLogs.Add(new EmployeePayslipAccessLog
        {
            TenantId = tenantId, EmployeeId = employeeId, PayslipId = id, Action = "ViewDeductions", UserId = this.GetUserId(),
        });
        await db.SaveChangesAsync(ct);
        return Ok(DeductionStatementDto.From(detail));
    }

    /// <summary>The caller's tenant and own employee id, from the token only. Null for an account that cannot use self-service.</summary>
    private (Guid TenantId, int EmployeeId)? Caller()
    {
        var accessMode = User.FindFirstValue("access_mode") ?? string.Empty;
        if (accessMode is "NoLogin" or "KioskOnly") return null;
        if (!Guid.TryParse(User.FindFirstValue("tenant_id"), out var tenantId)) return null;
        if (!int.TryParse(User.FindFirstValue("employee_id"), out var employeeId) || employeeId <= 0) return null;
        return (tenantId, employeeId);
    }

    private ObjectResult SelfServiceRefusal() => StatusCode(StatusCodes.Status403Forbidden, new
    {
        error = "ess_employee_not_linked",
        message = "Your account is not linked to an employee record, so there are no deductions to show. Ask HR to link your account.",
    });
}
