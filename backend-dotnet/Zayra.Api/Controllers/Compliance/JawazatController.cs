using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Jawazat;

namespace Zayra.Api.Controllers.Compliance;

[ApiController]
[Route("api/compliance/jawazat")]
[Authorize]
public sealed class JawazatController(IJawazatWorkflowService service, IDataScopeService scopes) : ControllerBase
{
    [HttpGet("capabilities")]
    public async Task<IActionResult> Capabilities(CancellationToken ct) => Ok(await service.GetCapabilitiesAsync(ct));

    [HttpGet("policy")]
    public Task<IActionResult> Policy([FromQuery] int? employeeId, CancellationToken ct) =>
        HandleAsync(actor => service.GetPolicyAsync(actor, employeeId, ct), false, ct);

    [HttpPost("evaluate")]
    public Task<IActionResult> Evaluate([FromBody] JawazatCreateRequest request, CancellationToken ct) =>
        HandleAsync(actor => service.EvaluateAsync(actor, request, ct), false, ct);

    [HttpGet("requests")]
    public Task<IActionResult> List(CancellationToken ct) => HandleAsync(actor => service.ListAsync(actor, ct), false, ct);

    [HttpGet("requests/{id:guid}")]
    public Task<IActionResult> Get(Guid id, CancellationToken ct) => HandleAsync(actor => service.GetAsync(actor, id, ct), false, ct);

    [HttpPost("requests")]
    public Task<IActionResult> Create([FromBody] JawazatCreateRequest request, CancellationToken ct) =>
        HandleAsync(actor => service.CreateAsync(actor, request, ct), true, ct, created: true);

    [HttpPost("requests/{id:guid}/submit")]
    public Task<IActionResult> Submit(Guid id, CancellationToken ct) => HandleAsync(actor => service.SubmitAsync(actor, id, ct), true, ct);

    [HttpPost("requests/{id:guid}/reconcile")]
    public Task<IActionResult> Reconcile(Guid id, CancellationToken ct) => HandleAsync(actor => service.ReconcileAsync(actor, id, ct), true, ct);

    private async Task<IActionResult> HandleAsync<T>(Func<JawazatActor, Task<T>> action, bool write, CancellationToken ct, bool created = false)
    {
        try
        {
            var tenantId = this.GetTenantId();
            var userId = this.GetUserId();
            if (tenantId is null || userId is null) return Unauthorized();
            if (User.FindFirst("access_mode")?.Value is "NoLogin" or "KioskOnly") return Forbid();
            var hrRole = User.Claims.Where(c => c.Type == System.Security.Claims.ClaimTypes.Role).Any(c => JawazatConstants.IsHrRole(c.Value));
            var canManage = hrRole && User.HasClaim("permission", write ? "employees.write" : "employees.read");
            var canOwn = User.HasClaim("permission", write ? "ess.write" : "ess.read");
            if (!canManage && !canOwn) return Forbid();
            var actor = new JawazatActor(tenantId.Value, userId.Value, canManage,
                await scopes.ResolveAsync(User, tenantId.Value, ct), this.GetEntityScope());
            var result = await action(actor);
            if (created && result is JawazatRequestDto request) return Created($"/api/compliance/jawazat/requests/{request.Id}", request);
            return Ok(result);
        }
        catch (JawazatException ex) { return StatusCode(ex.StatusCode, new { code = ex.Code, message = ex.Message }); }
        catch (ApprovalRoutingException ex) { return UnprocessableEntity(new { code = ex.Code, message = ex.Message }); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { code = "request_changed", message = "The request changed. Refresh before trying again." }); }
    }
}
