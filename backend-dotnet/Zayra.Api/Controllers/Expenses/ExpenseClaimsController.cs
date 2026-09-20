using Zayra.Api.Application.Auth;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Expenses;
using Zayra.Api.Infrastructure.Authorization;

namespace Zayra.Api.Controllers.Expenses;

/// <summary>
/// W2-B — expense claims for approvers, HR and finance. Tenant and company isolation come from the
/// global query filters on <c>ExpenseClaim</c> (ITenantOwned + ICompanyScopedOperational); team
/// scoping (a manager sees their reports) comes from <see cref="IDataScopeService"/>.
/// Employees use <see cref="EssExpensesController"/> instead.
/// </summary>
[ApiController]
[Route("api/expenses")]
[Authorize]
public class ExpenseClaimsController : ControllerBase
{
    private const string ReaderRoles = "Admin,HR Manager,HR Officer,Payroll Manager,Payroll Officer,Finance,Manager,Auditor";
    private const string PayrollRoles = "Admin,Payroll Manager,Payroll Officer";
    private const string PolicyRoles = "Admin,HR Manager,Payroll Manager,Finance";

    private readonly IExpenseClaimService _expenses;
    private readonly IDataScopeService _scope;

    public ExpenseClaimsController(IExpenseClaimService expenses, IDataScopeService scope)
    {
        _expenses = expenses;
        _scope = scope;
    }

    [HttpGet]
    [Authorize(Roles = ReaderRoles)]
    public Task<IActionResult> List([FromQuery] ExpenseClaimQuery query, CancellationToken ct) => ExpenseErrorMapping.Run(this, async () =>
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var scope = await _scope.ResolveAsync(User, tenantId.Value, ct);
        if (query.EmployeeId.HasValue && !scope.CanAccessEmployee(query.EmployeeId.Value)) return Forbid();
        return Ok(await _expenses.ListAsync(tenantId.Value, query, scope.IsUnrestricted ? null : scope.AllowedEmployeeIds, ct));
    });

    [HttpGet("{id:guid}")]
    [Authorize(Roles = ReaderRoles)]
    public Task<IActionResult> Get(Guid id, CancellationToken ct) => ExpenseErrorMapping.Run(this, async () =>
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var claim = await _expenses.GetAsync(tenantId.Value, id, Context(), ct);
        if (claim is null) return NotFound();
        var scope = await _scope.ResolveAsync(User, tenantId.Value, ct);
        // A current approver may always open what they are asked to decide.
        if (!scope.CanAccessEmployee(claim.EmployeeId) && claim.Approval?.CanDecide != true) return NotFound();
        return Ok(claim);
    });

    /// <summary>Approver view: pending expense approvals the caller can see, with CanDecide per claim.</summary>
    [HttpGet("approvals")]
    public Task<IActionResult> ApprovalInbox(CancellationToken ct) => ExpenseErrorMapping.Run(this, async () =>
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await _expenses.GetApprovalInboxAsync(tenantId.Value, Context(), ct));
    });

    [HttpPost("{id:guid}/decision")]
    [HasPermission("approvals.decide")]
    public Task<IActionResult> Decide(Guid id, [FromBody] ExpenseDecisionRequest request, CancellationToken ct) => ExpenseErrorMapping.Run(this, async () =>
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await _expenses.DecideAsync(tenantId.Value, id, request, Context(), ct));
    });

    /// <summary>Binds approved claims to an open payroll run: one PayrollAdjustment per claim.</summary>
    [HttpPost("payroll/schedule")]
    [Authorize(Roles = PayrollRoles)]
    public Task<IActionResult> Schedule([FromBody] ScheduleExpensesRequest request, CancellationToken ct) => ExpenseErrorMapping.Run(this, async () =>
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await _expenses.ScheduleForPayrollAsync(tenantId.Value, request, this.GetUserId(), ct));
    });

    [HttpPost("{id:guid}/unschedule")]
    [Authorize(Roles = PayrollRoles)]
    public Task<IActionResult> Unschedule(Guid id, CancellationToken ct) => ExpenseErrorMapping.Run(this, async () =>
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await _expenses.UnscheduleAsync(tenantId.Value, id, this.GetUserId(), ct));
    });

    [HttpGet("{id:guid}/lines/{lineId:guid}/receipt")]
    [Authorize(Roles = ReaderRoles)]
    public Task<IActionResult> Receipt(Guid id, Guid lineId, CancellationToken ct) => ExpenseErrorMapping.Run(this, async () =>
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var claim = await _expenses.GetAsync(tenantId.Value, id, Context(), ct);
        if (claim is null) return NotFound();
        var scope = await _scope.ResolveAsync(User, tenantId.Value, ct);
        if (!scope.CanAccessEmployee(claim.EmployeeId) && claim.Approval?.CanDecide != true) return NotFound();
        var (content, contentType, fileName) = await _expenses.GetReceiptAsync(tenantId.Value, id, lineId, ct);
        return File(content, contentType, fileName);
    });

    [HttpGet("categories")]
    public Task<IActionResult> Categories([FromQuery] bool includeInactive = false, CancellationToken ct = default) => ExpenseErrorMapping.Run(this, async () =>
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await _expenses.GetCategoriesAsync(tenantId.Value, includeInactive, ct));
    });

    [HttpPut("categories/{code}/policy")]
    [Authorize(Roles = PolicyRoles)]
    public Task<IActionResult> UpdatePolicy(string code, [FromBody] ExpenseCategoryPolicyRequest request, CancellationToken ct) => ExpenseErrorMapping.Run(this, async () =>
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(await _expenses.UpdateCategoryPolicyAsync(tenantId.Value, code, request, ct));
    });

    private RequestContext Context() => new(
        HttpContext.Connection.RemoteIpAddress?.ToString(),
        Request.Headers.UserAgent.ToString(),
        this.GetUserId(),
        this.GetTenantId(),
        User.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value).ToList(),
        User.Claims.Where(c => c.Type == "permission").Select(c => c.Value).ToList());
}
