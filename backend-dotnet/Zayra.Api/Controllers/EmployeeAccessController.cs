using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Authorization;

namespace Zayra.Api.Controllers;

/// <summary>
/// HR's employee-access surface (contract §4): where an employee stands on the way to signing in, the welcome codes HR
/// hands out (single, bulk, and "Reset sign-in" on an active login), and the work-email backfill from IT.
/// Reads need <c>employees.read</c>; writes need <c>employees.access.issue</c>; resetting an ACTIVE login also needs
/// <c>employees.access.reset</c>. Tenant- and company-scoped like the employee endpoints.
/// </summary>
[ApiController]
[Route("api/employee-access")]
[Authorize]
public sealed class EmployeeAccessController : ControllerBase
{
    public const string IssuePermission = "employees.access.issue";
    public const string ResetPermission = "employees.access.reset";

    private readonly EmployeeAccessService _access;
    private readonly IDataScopeService _dataScope;

    public EmployeeAccessController(EmployeeAccessService access, IDataScopeService dataScope)
    {
        _access = access;
        _dataScope = dataScope;
    }

    [HttpGet("{employeeId:int}")]
    [HasPermission("employees.read")]
    public async Task<ActionResult<EmployeeAccessDto>> Get(int employeeId, CancellationToken ct)
    {
        if (TenantId() is not Guid tenantId) return Unauthorized();
        if (!await InDataScopeAsync(tenantId, employeeId, ct)) return NotFound(new { message = "Employee not found." });
        var dto = await _access.GetAsync(tenantId, employeeId, this.GetEntityScope(), Context(tenantId),
            User.HasPermission(IssuePermission), User.HasPermission(ResetPermission), ct);
        return dto is null ? NotFound(new { message = "Employee not found." }) : Ok(dto);
    }

    [HttpPost("codes")]
    [HasPermission(IssuePermission)]
    [Zayra.Api.Infrastructure.Http.NoStore]
    public async Task<ActionResult<IssueCodesResponse>> IssueCodes(IssueCodesRequest request, CancellationToken ct)
    {
        if (TenantId() is not Guid tenantId || UserId() is null) return Unauthorized();
        try
        {
            var ids = request.EmployeeIds ?? Array.Empty<int>();
            var scope = await _dataScope.ResolveAsync(User, tenantId, ct);
            var allowed = scope.IsUnrestricted ? ids : ids.Where(id => scope.AllowedEmployeeIds!.Contains(id)).ToList();
            var response = await _access.IssueCodesAsync(tenantId, new IssueCodesRequest(allowed, request.Delivery), this.GetEntityScope(), Context(tenantId),
                canIssue: true, canReset: User.HasPermission(ResetPermission), ct);
            var outOfScope = ids.Except(allowed).Select(id => new SkippedCodeDto(id, EmployeeAccessService.Skip.NotFound,
                EmployeeAccessService.SkipReason(EmployeeAccessService.Skip.NotFound)));
            return Ok(response with { Skipped = response.Skipped.Concat(outOfScope).ToList() });
        }
        catch (EmployeeAccessRequestException ex) { return BadRequest(new { code = ex.Code, message = ex.Message }); }
    }

    [HttpPost("work-emails")]
    [HasPermission(IssuePermission)]
    public async Task<ActionResult<WorkEmailBackfillResponse>> BackfillWorkEmails(WorkEmailBackfillRequest request, CancellationToken ct)
    {
        if (TenantId() is not Guid tenantId || UserId() is null) return Unauthorized();
        try
        {
            var scope = await _dataScope.ResolveAsync(User, tenantId, ct);
            if (!scope.IsUnrestricted) return Forbid();
            return Ok(await _access.BackfillWorkEmailsAsync(tenantId, request, this.GetEntityScope(), Context(tenantId), ct));
        }
        catch (EmployeeAccessRequestException ex) { return BadRequest(new { code = ex.Code, message = ex.Message }); }
    }

    private async Task<bool> InDataScopeAsync(Guid tenantId, int employeeId, CancellationToken ct)
    {
        var scope = await _dataScope.ResolveAsync(User, tenantId, ct);
        return scope.IsUnrestricted || scope.AllowedEmployeeIds!.Contains(employeeId);
    }

    private RequestContext Context(Guid tenantId) => new(
        HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), UserId(), tenantId);

    private Guid? UserId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id) ? id : null;

    private Guid? TenantId() => Guid.TryParse(User.FindFirstValue("tenant_id"), out var id) ? id : null;
}
