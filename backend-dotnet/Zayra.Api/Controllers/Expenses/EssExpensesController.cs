using Zayra.Api.Application.Auth;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Expenses;
using Zayra.Api.Data;

namespace Zayra.Api.Controllers.Expenses;

/// <summary>
/// W2-B — "My expenses" for employees, on web and mobile. Every action is pinned to the caller's
/// OWN employee record, resolved from the token (never from the request), so an employee can only
/// see, edit and submit their own claims. Kept out of <c>EmployeeSelfServiceController</c>, which
/// another stream owns.
///
/// <para>Mobile flow: <c>POST /api/ess/expenses</c> (draft with lines) → <c>POST
/// /api/ess/expenses/{id}/lines/{lineId}/receipt</c> (multipart, field <c>file</c>) per receipt →
/// <c>POST /api/ess/expenses/{id}/submit</c>. Errors are <c>{ code, message, violations[] }</c>.</para>
/// </summary>
[ApiController]
[Route("api/ess/expenses")]
[Authorize]
public class EssExpensesController : ControllerBase
{
    private readonly IExpenseClaimService _expenses;
    private readonly ZayraDbContext _db;

    public EssExpensesController(IExpenseClaimService expenses, ZayraDbContext db)
    {
        _expenses = expenses;
        _db = db;
    }

    /// <summary>Categories with their policy, plus the currency every amount is in.</summary>
    [HttpGet("config")]
    public Task<IActionResult> Config(CancellationToken ct) => ExpenseErrorMapping.Run(this, async () =>
    {
        var (ok, tenantId, employeeId, error) = await ResolveEmployeeAsync(requireWrite: false, ct);
        if (!ok) return Denied(error);
        var currency = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.Id == employeeId)
            .Join(_db.Companies.AsNoTracking(), e => e.CompanyId, c => (Guid?)c.Id, (e, c) => c.DefaultCurrency)
            .FirstOrDefaultAsync(ct);
        return Ok(new
        {
            currency = currency?.Trim().ToUpperInvariant(),
            multiCurrencySupported = false,
            maxReceiptBytes = 10 * 1024 * 1024,
            receiptContentTypes = new[] { "application/pdf", "image/jpeg", "image/png", "image/webp", "image/heic", "image/heif" },
            categories = await _expenses.GetCategoriesAsync(tenantId, includeInactive: false, ct),
        });
    });

    [HttpGet]
    public Task<IActionResult> List([FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) => ExpenseErrorMapping.Run(this, async () =>
    {
        var (ok, tenantId, employeeId, error) = await ResolveEmployeeAsync(requireWrite: false, ct);
        if (!ok) return Denied(error);
        return Ok(await _expenses.ListOwnAsync(tenantId, employeeId, status, page, pageSize, ct));
    });

    [HttpGet("{id:guid}")]
    public Task<IActionResult> Get(Guid id, CancellationToken ct) => ExpenseErrorMapping.Run(this, async () =>
    {
        var (ok, tenantId, employeeId, error) = await ResolveEmployeeAsync(requireWrite: false, ct);
        if (!ok) return Denied(error);
        var claim = await _expenses.GetOwnAsync(tenantId, employeeId, id, ct);
        return claim is null ? NotFound() : Ok(claim);
    });

    [HttpPost]
    public Task<IActionResult> Create([FromBody] SaveExpenseClaimRequest request, CancellationToken ct) => ExpenseErrorMapping.Run(this, async () =>
    {
        var (ok, tenantId, employeeId, error) = await ResolveEmployeeAsync(requireWrite: true, ct);
        if (!ok) return Denied(error);
        var claim = await _expenses.CreateDraftAsync(tenantId, employeeId, request, ct);
        return Created($"/api/ess/expenses/{claim.Id}", claim);
    });

    [HttpPut("{id:guid}")]
    public Task<IActionResult> Update(Guid id, [FromBody] SaveExpenseClaimRequest request, CancellationToken ct) => ExpenseErrorMapping.Run(this, async () =>
    {
        var (ok, tenantId, employeeId, error) = await ResolveEmployeeAsync(requireWrite: true, ct);
        if (!ok) return Denied(error);
        return Ok(await _expenses.UpdateDraftAsync(tenantId, employeeId, id, request, ct));
    });

    [HttpPost("{id:guid}/lines/{lineId:guid}/receipt")]
    [RequestSizeLimit(10_485_760 + 64_000)]
    public Task<IActionResult> UploadReceipt(Guid id, Guid lineId, IFormFile file, CancellationToken ct) => ExpenseErrorMapping.Run(this, async () =>
    {
        var (ok, tenantId, employeeId, error) = await ResolveEmployeeAsync(requireWrite: true, ct);
        if (!ok) return Denied(error);
        return Ok(await _expenses.AttachReceiptAsync(tenantId, employeeId, id, lineId, file, ct));
    });

    [HttpGet("{id:guid}/lines/{lineId:guid}/receipt")]
    public Task<IActionResult> DownloadReceipt(Guid id, Guid lineId, CancellationToken ct) => ExpenseErrorMapping.Run(this, async () =>
    {
        var (ok, tenantId, employeeId, error) = await ResolveEmployeeAsync(requireWrite: false, ct);
        if (!ok) return Denied(error);
        if (await _expenses.GetOwnAsync(tenantId, employeeId, id, ct) is null) return NotFound();
        var (content, contentType, fileName) = await _expenses.GetReceiptAsync(tenantId, id, lineId, ct);
        return File(content, contentType, fileName);
    });

    [HttpPost("{id:guid}/submit")]
    public Task<IActionResult> Submit(Guid id, CancellationToken ct) => ExpenseErrorMapping.Run(this, async () =>
    {
        var (ok, tenantId, employeeId, error) = await ResolveEmployeeAsync(requireWrite: true, ct);
        if (!ok) return Denied(error);
        return Ok(await _expenses.SubmitAsync(tenantId, employeeId, id, Context(tenantId), ct));
    });

    [HttpPost("{id:guid}/cancel")]
    public Task<IActionResult> Cancel(Guid id, CancellationToken ct) => ExpenseErrorMapping.Run(this, async () =>
    {
        var (ok, tenantId, employeeId, error) = await ResolveEmployeeAsync(requireWrite: true, ct);
        if (!ok) return Denied(error);
        return Ok(await _expenses.CancelDraftAsync(tenantId, employeeId, id, ct));
    });

    // Same resolution rules as the ESS controller: access mode, ess.read/ess.write, then the
    // employee_id claim, the login↔employee link, and finally a unique email match.
    private async Task<(bool Ok, Guid TenantId, int EmployeeId, string? Error)> ResolveEmployeeAsync(bool requireWrite, CancellationToken ct)
    {
        var accessMode = User.FindFirstValue("access_mode") ?? string.Empty;
        if (accessMode is "NoLogin" or "KioskOnly") return (false, default, default, "This access mode cannot use self-service.");
        if (!HasPermission("ess.read") && !HasPermission("ess.write")) return (false, default, default, "Self-service read permission is required.");
        if (requireWrite && !HasPermission("ess.write")) return (false, default, default, "Self-service write permission is required.");
        if (!Guid.TryParse(User.FindFirstValue("tenant_id"), out var tenantId)) return (false, default, default, "Tenant claim is missing. Please log in again.");

        if (int.TryParse(User.FindFirstValue("employee_id"), out var claimed)
            && await _db.Employees.AsNoTracking().AnyAsync(e => e.TenantId == tenantId && e.Id == claimed && !e.IsDeleted, ct))
            return (true, tenantId, claimed, null);

        var userId = this.GetUserId();
        if (userId is not null)
        {
            var linked = await _db.Employees.AsNoTracking()
                .Where(e => e.TenantId == tenantId && e.UserAccountId == userId && !e.IsDeleted)
                .Select(e => e.Id).Take(2).ToListAsync(ct);
            if (linked.Count == 1) return (true, tenantId, linked[0], null);
        }

        var email = (User.FindFirstValue("email") ?? User.FindFirstValue(ClaimTypes.Email) ?? string.Empty).Trim().ToUpperInvariant();
        if (email.Length > 0)
        {
            var matches = await _db.Employees.AsNoTracking()
                .Where(e => e.TenantId == tenantId && !e.IsDeleted && (e.WorkEmail.ToUpper() == email || e.PersonalEmail.ToUpper() == email))
                .Select(e => e.Id).Take(2).ToListAsync(ct);
            if (matches.Count == 1) return (true, tenantId, matches[0], null);
        }
        return (false, default, default, "Your login is not linked to an employee record. Ask HR to link your account.");
    }

    private IActionResult Denied(string? error) => StatusCode(StatusCodes.Status403Forbidden, new { code = "ess_forbidden", message = error });

    private bool HasPermission(string permission) => User.Claims.Any(c => c.Type == "permission" && c.Value == permission);

    private RequestContext Context(Guid tenantId) => new(
        HttpContext.Connection.RemoteIpAddress?.ToString(),
        Request.Headers.UserAgent.ToString(),
        this.GetUserId(),
        tenantId,
        User.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value).ToList(),
        User.Claims.Where(c => c.Type == "permission").Select(c => c.Value).ToList());
}
