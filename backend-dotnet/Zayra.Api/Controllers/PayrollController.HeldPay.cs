using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Payroll;

namespace Zayra.Api.Controllers;

/// <summary>Request body for a decision on one held amount: <c>pay</c> (into the next open run) or <c>forfeit</c>.</summary>
public sealed record HeldPayDecisionRequest(string? Action, string? Reason);

/// <summary>
/// Pay held for a suspended employee (see <see cref="HeldPay"/>): the run screen's list, and Finance's decision on each
/// amount. Reading needs <c>payroll.read</c>; a decision needs <c>payroll.approve</c> and is refused to the run's preparer.
/// </summary>
public partial class PayrollController
{
    [HttpGet("runs/{id:guid}/held-pay")]
    [HasPermission("payroll.read")]
    public async Task<IActionResult> GetRunHeldPay(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var run = await _db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, cancellationToken);
        if (run is null) return NotFound();
        var rows = await HeldPay.ListForRunAsync(_db, tenantId, run, cancellationToken);
        return Ok(new
        {
            runId = id,
            period = $"{run.Year}-{run.Month:D2}",
            items = rows,
            heldTotal = rows.Where(r => r.Status == HeldPay.Held).Sum(r => r.Amount),
            awaitingDecision = rows.Count(r => r.Status == HeldPay.Held),
        });
    }

    [HttpPost("held-pay/{holdId:guid}/decision")]
    [HasPermission("payroll.approve")]
    public async Task<IActionResult> DecideHeldPay(Guid holdId, HeldPayDecisionRequest req, CancellationToken cancellationToken)
    {
        var outcome = await HeldPay.DecideAsync(_db, GetTenantId(), holdId, GetUserId(), req?.Action, req?.Reason, cancellationToken);
        return StatusCode(outcome.StatusCode, outcome.Body);
    }
}
