using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

/// <summary>
/// Setup -> Payroll rules: the per-company payroll choices (see <see cref="CompanyPayrollRules"/>). Every rule is the
/// company's own. HR makes a change (<c>payroll.rules.manage</c>); a change that lowers pay, or goes below a statutory
/// minimum, waits as a <see cref="PayrollRuleChangeRequest"/> until a different person holding <c>payroll.approve</c>
/// approves it. A change that cannot lower pay applies directly. Each change takes effect from a chosen date (never in
/// the past), so a period that already began keeps the rule it began with. Every step is audited in the same
/// transaction as the write.
/// </summary>
[ApiController]
[Route("api/payroll/company-rules")]
[Authorize]
public class CompanyPayrollRulesController : ControllerBase
{
    private readonly ICompanyPayrollRulesService _service;

    public CompanyPayrollRulesController(ICompanyPayrollRulesService service) => _service = service;

    public sealed record SaveRulesRequest(
        Guid CompanyId, List<PayrollRuleChange> Changes, bool Acknowledge = false, string? Note = null,
        DateOnly? EffectiveFrom = null, List<string>? AcknowledgedWarnings = null);

    public sealed record DecisionRequest(string? Note = null);

    [HttpGet]
    [HasPermission("payroll.rules.manage", "payroll.approve", "payroll.read")]
    public async Task<IActionResult> Get([FromQuery] Guid companyId, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        if (!await CanSeeCompanyAsync(tenantId, companyId, ct)) return NotFound();
        return Ok(await _service.ViewAsync(tenantId, companyId, ct));
    }

    [HttpPut]
    [HasPermission("payroll.rules.manage")]
    public async Task<IActionResult> Save([FromBody] SaveRulesRequest request, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        if (request.Changes is null || request.Changes.Count == 0) return BadRequest(new { code = "ERR_PAYROLL_RULES_EMPTY", message = "Nothing to save." });
        if (!await CanSeeCompanyAsync(tenantId, request.CompanyId, ct)) return NotFound();
        return Map(await _service.SaveAsync(tenantId, UserId(), request.CompanyId, request.Changes, request.Acknowledge, request.Note,
            request.EffectiveFrom, request.AcknowledgedWarnings, ct));
    }

    [HttpPost("{id:guid}/approve")]
    [HasPermission("payroll.approve")]
    public Task<IActionResult> Approve(Guid id, [FromBody] DecisionRequest? body, CancellationToken ct) => DecideAsync(id, true, body?.Note, ct);

    [HttpPost("{id:guid}/reject")]
    [HasPermission("payroll.approve")]
    public Task<IActionResult> Reject(Guid id, [FromBody] DecisionRequest? body, CancellationToken ct) => DecideAsync(id, false, body?.Note, ct);

    private async Task<IActionResult> DecideAsync(Guid id, bool approve, string? note, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var companyId = await _service.CompanyOfRequestAsync(tenantId, id, ct);
        if (companyId is null || !await CanSeeCompanyAsync(tenantId, companyId.Value, ct)) return NotFound();
        if (!approve && (note?.Trim().Length ?? 0) < 5)
            return BadRequest(new { code = "ERR_PAYROLL_RULE_REASON_REQUIRED", message = "Say why you are rejecting this change (at least 5 characters)." });
        return Map(await _service.DecideAsync(tenantId, UserId(), id, approve, note, ct));
    }

    private IActionResult Map(RuleOutcome o) => o.Status switch
    {
        200 => Ok(o.Body),
        202 => Accepted(o.Body),
        400 => BadRequest(o.Body),
        404 => NotFound(),
        409 => Conflict(o.Body),
        _ => StatusCode(o.Status, o.Body),
    };

    /// <summary>The company exists in this tenant AND the caller may act for it: group-level callers for any company,
    /// everyone else only for the companies their scope names. Another company is reported as not found.</summary>
    private async Task<bool> CanSeeCompanyAsync(Guid tenantId, Guid companyId, CancellationToken ct)
    {
        var scope = this.GetEntityScope();
        if (!scope.IsGroupLevel && !scope.CanAccessCompany(companyId)) return false;
        return await _service.CompanyExistsAsync(tenantId, companyId, ct);
    }

    private Guid RequireTenant() => Guid.Parse(User.FindFirstValue("tenant_id")!);
    private Guid? UserId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id) ? id : null;
}
