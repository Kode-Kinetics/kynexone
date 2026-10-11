using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Compensation;

namespace Zayra.Api.Controllers.Compensation;

/// <summary>
/// CMP — merit planning, pay benchmarking and pay equity. Every route is gated by a compensation.* permission
/// ([HasPermission], any-of) and, through OptInFeatures, by the tenant's <c>compensation</c> opt-in flag (off unless
/// the platform team enables it). Row scope: the DbContext company filter, plus the reporting line for planners
/// (a manager reaches only their own worksheets). Business refusals are 422 with a plain message and, where useful,
/// a list of blockers.
/// </summary>
[ApiController]
[Route("api/compensation/merit")]
[Authorize]
[HasPermission(CompPermissions.Read, CompPermissions.Plan, CompPermissions.Manage, CompPermissions.Approve, CompPermissions.BenchmarksManage, CompPermissions.EquityRead)]
public sealed class MeritPlanningController(ZayraDbContext db, MeritPlanningService merit, PayBenchmarkService benchmarks, PayEquityService equity) : ControllerBase
{
    // ── cycles ──
    [HttpGet("cycles")]
    public Task<IActionResult> Cycles(CancellationToken ct) => Run(c => merit.ListCyclesAsync(c, ct), ct);

    [HttpPost("cycles")]
    [HasPermission(CompPermissions.Manage)]
    public Task<IActionResult> CreateCycle([FromBody] CreateCompCycleRequest request, CancellationToken ct) => Run(c => merit.CreateCycleAsync(c, request, ct), ct, created: true);

    [HttpGet("cycles/{id:guid}")]
    public Task<IActionResult> Cycle(Guid id, CancellationToken ct) => Run(c => merit.GetCycleAsync(c, id, ct), ct);

    [HttpPut("cycles/{id:guid}")]
    [HasPermission(CompPermissions.Manage)]
    public Task<IActionResult> UpdateCycle(Guid id, [FromBody] UpdateCompCycleRequest request, CancellationToken ct) => Run(c => merit.UpdateCycleAsync(c, id, request, ct), ct);

    [HttpPut("cycles/{id:guid}/budgets")]
    [HasPermission(CompPermissions.Manage)]
    public Task<IActionResult> Budgets(Guid id, [FromBody] SaveCompBudgetsRequest request, CancellationToken ct) => Run(c => merit.SaveBudgetsAsync(c, id, request, ct), ct);

    [HttpPut("cycles/{id:guid}/guidelines")]
    [HasPermission(CompPermissions.Manage)]
    public Task<IActionResult> Guidelines(Guid id, [FromBody] SaveCompGuidelinesRequest request, CancellationToken ct) => Run(c => merit.SaveGuidelinesAsync(c, id, request, ct), ct);

    [HttpPut("cycles/{id:guid}/fx")]
    [HasPermission(CompPermissions.Manage)]
    public Task<IActionResult> Fx(Guid id, [FromBody] SaveCompFxRequest request, CancellationToken ct) => Run(c => merit.SaveFxAsync(c, id, request, ct), ct);

    [HttpPost("cycles/{id:guid}/open-planning")]
    [HasPermission(CompPermissions.Manage)]
    public Task<IActionResult> OpenPlanning(Guid id, [FromBody] CompCycleTransitionRequest request, CancellationToken ct) => Run(c => merit.OpenPlanningAsync(c, id, request, ct), ct);

    [HttpPost("cycles/{id:guid}/start-review")]
    [HasPermission(CompPermissions.Manage)]
    public Task<IActionResult> StartReview(Guid id, [FromBody] CompCycleTransitionRequest request, CancellationToken ct) => Run(c => merit.StartReviewAsync(c, id, request, ct), ct);

    [HttpPost("cycles/{id:guid}/reopen")]
    [HasPermission(CompPermissions.Manage)]
    public Task<IActionResult> Reopen(Guid id, [FromBody] CompCycleTransitionRequest request, CancellationToken ct) => Run(c => merit.ReopenPlanningAsync(c, id, request, ct), ct);

    [HttpPost("cycles/{id:guid}/approve")]
    [HasPermission(CompPermissions.Approve)]
    public Task<IActionResult> Approve(Guid id, [FromBody] CompCycleTransitionRequest request, CancellationToken ct) => Run(c => merit.ApproveCycleAsync(c, id, request, ct), ct);

    [HttpPost("cycles/{id:guid}/apply")]
    [HasPermission(CompPermissions.Manage)]
    public Task<IActionResult> Apply(Guid id, [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] ApplyCompCycleRequest? request, CancellationToken ct) =>
        Run(c => merit.ApplyAsync(c, id, ct, request?.AcknowledgeRetroCost ?? false), ct);

    [HttpPost("cycles/{id:guid}/cancel")]
    [HasPermission(CompPermissions.Manage)]
    public Task<IActionResult> Cancel(Guid id, [FromBody] CompCycleTransitionRequest request, CancellationToken ct) => Run(c => merit.CancelAsync(c, id, request, ct), ct);

    [HttpGet("cycles/{id:guid}/equity")]
    [HasPermission(CompPermissions.EquityRead)]
    public Task<IActionResult> Equity(Guid id, CancellationToken ct) => Run(c => equity.GetAsync(c, id, ct), ct);

    [HttpPost("cycles/{id:guid}/scenarios")]
    [HasPermission(CompPermissions.Plan, CompPermissions.Manage)]
    public Task<IActionResult> CreateScenario(Guid id, [FromBody] CreateCompScenarioRequest request, CancellationToken ct) => Run(c => merit.CreateScenarioAsync(c, id, request, ct), ct, created: true);

    [HttpPost("cycles/{id:guid}/worksheets")]
    [HasPermission(CompPermissions.Manage)]
    public Task<IActionResult> OpenWorksheet(Guid id, [FromBody] OpenCompWorksheetRequest request, CancellationToken ct) => Run(c => merit.CreateWorksheetAsync(c, id, request.ManagerEmployeeId, ct), ct, created: true);

    [HttpPost("worksheets/{id:guid}/assign")]
    [HasPermission(CompPermissions.Manage)]
    public Task<IActionResult> Assign(Guid id, [FromBody] AssignCompEmployeeRequest request, CancellationToken ct) => Run(c => merit.AssignAsync(c, id, request.EmployeeId, ct), ct);

    [HttpPost("worksheets/{id:guid}/close")]
    [HasPermission(CompPermissions.Manage)]
    public Task<IActionResult> CloseWorksheet(Guid id, [FromBody] CloseCompWorksheetRequest request, CancellationToken ct) => Run(c => merit.CloseWorksheetAsync(c, id, request.Reason, ct), ct);

    // ── worksheets ──
    [HttpGet("my-worksheets")]
    public Task<IActionResult> MyWorksheets(CancellationToken ct) => Run(c => merit.MyWorksheetsAsync(c, ct), ct);

    [HttpGet("worksheets/{id:guid}")]
    public Task<IActionResult> Worksheet(Guid id, CancellationToken ct) => Run(c => merit.GetWorksheetAsync(c, id, ct), ct);

    [HttpPut("worksheets/{id:guid}")]
    [HasPermission(CompPermissions.Plan, CompPermissions.Manage)]
    public Task<IActionResult> SaveWorksheet(Guid id, [FromBody] SaveCompWorksheetRequest request, CancellationToken ct) => Run(c => merit.SaveWorksheetAsync(c, id, request, ct), ct);

    [HttpPost("worksheets/{id:guid}/submit")]
    [HasPermission(CompPermissions.Plan, CompPermissions.Manage)]
    public Task<IActionResult> Submit(Guid id, [FromBody] SubmitCompWorksheetRequest request, CancellationToken ct) => Run(c => merit.SubmitWorksheetAsync(c, id, request, ct), ct);

    [HttpPost("worksheets/{id:guid}/make-primary")]
    [HasPermission(CompPermissions.Plan, CompPermissions.Manage)]
    public Task<IActionResult> MakePrimary(Guid id, [FromBody] SubmitCompWorksheetRequest request, CancellationToken ct) => Run(c => merit.SetPrimaryAsync(c, id, request, ct), ct);

    [HttpPost("awards/{lineId:guid}/withdraw")]
    [HasPermission(CompPermissions.Manage)]
    public Task<IActionResult> Withdraw(Guid lineId, [FromBody] WithdrawCompAwardRequest request, CancellationToken ct) => Run(c => merit.WithdrawAwardAsync(c, lineId, request, ct), ct);

    [HttpPost("awards/{lineId:guid}/reverse")]
    [HasPermission(CompPermissions.Manage)]
    public Task<IActionResult> Reverse(Guid lineId, [FromBody] ReverseCompAwardRequest request, CancellationToken ct) => Run(c => merit.ReverseAwardAsync(c, lineId, request, ct), ct);

    // ── benchmarks ──
    [HttpGet("benchmarks")]
    public Task<IActionResult> BenchmarkSets([FromQuery] Guid? companyId, CancellationToken ct) => Run(c => benchmarks.ListAsync(c, companyId, ct), ct);

    [HttpGet("benchmarks/template.csv")]
    [HasPermission(CompPermissions.BenchmarksManage)]
    public IActionResult BenchmarkTemplate() =>
        File(System.Text.Encoding.UTF8.GetBytes(PayBenchmarkService.TemplateCsv()), "text/csv", "pay-benchmark-template.csv");

    [HttpGet("benchmarks/{id:guid}")]
    public Task<IActionResult> BenchmarkSet(Guid id, CancellationToken ct) =>
        Run(async c => { var (set, rows) = await benchmarks.GetAsync(c, id, ct); return new { set, rows }; }, ct);

    [HttpPost("benchmarks/import")]
    [HasPermission(CompPermissions.BenchmarksManage)]
    [RequestSizeLimit(6 * 1024 * 1024)]
    public async Task<IActionResult> Import([FromForm] IFormFile file, [FromForm] Guid companyId, [FromForm] string name, [FromForm] string provider,
        [FromForm] string licenceStatement, [FromForm] DateOnly? surveyDate, [FromForm] string geography, [FromForm] string currency,
        [FromForm] string payBasis, [FromForm] bool isSynthetic, CancellationToken ct)
    {
        if (file is null) return UnprocessableEntity(new { error = "file_required", message = "Choose a CSV or XLSX file." });
        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return await Run(async c =>
        {
            var result = await benchmarks.ImportAsync(c, new PayBenchmarkImportMeta(companyId, name, provider, licenceStatement, surveyDate, geography, currency, payBasis, isSynthetic),
                file.FileName, buffer.ToArray(), ct);
            return result;
        }, ct, unprocessableWhen: r => r is PayBenchmarkImportResultDto { Imported: false });
    }

    [HttpPut("benchmarks/rows/{rowId:guid}/mapping")]
    [HasPermission(CompPermissions.BenchmarksManage)]
    public Task<IActionResult> MapRow(Guid rowId, [FromBody] MapBenchmarkRowRequest request, CancellationToken ct) => Run(c => benchmarks.MapRowAsync(c, rowId, request, ct), ct);

    [HttpPost("benchmarks/{id:guid}/retire")]
    [HasPermission(CompPermissions.BenchmarksManage)]
    public Task<IActionResult> Retire(Guid id, CancellationToken ct) => Run(c => benchmarks.RetireAsync(c, id, ct), ct);

    // ── plumbing ──

    private async Task<IActionResult> Run<T>(Func<CompCaller, Task<T>> action, CancellationToken ct, bool created = false, Func<object?, bool>? unprocessableWhen = null)
    {
        var caller = await CallerAsync(ct);
        if (caller is null) return Unauthorized();
        try
        {
            var result = await action(caller);
            if (unprocessableWhen?.Invoke(result) == true) return UnprocessableEntity(result);
            return created ? StatusCode(StatusCodes.Status201Created, result) : Ok(result);
        }
        catch (CompRuleException ex) { return UnprocessableEntity(new { error = ex.Code, message = ex.Message, blockers = ex.Blockers }); }
        catch (CompBadRequestException ex) { return BadRequest(new { error = ex.Code, message = ex.Message }); }
        catch (CompConflictException ex) { return Conflict(new { error = "conflict", message = ex.Message }); }
        catch (CompNotFoundException ex) { return NotFound(new { error = "not_found", message = ex.Message }); }
        catch (CompForbiddenException ex) { return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Code, message = ex.Message }); }
        // The DbContext's company write guard (a record stamped with a company outside the caller's scope).
        catch (UnauthorizedAccessException) { return StatusCode(StatusCodes.Status403Forbidden, new { error = "company_scope_denied", message = "That company is outside your access." }); }
    }

    private async Task<CompCaller?> CallerAsync(CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tenantId) return null;
        var userId = this.GetUserId();
        var employeeId = await CallerEmployeeResolver.ResolveAsync(db, User, tenantId, ct)
                         ?? (userId is Guid u
                             ? await db.Employees.AsNoTracking().Where(e => e.TenantId == tenantId && e.UserAccountId == u && !e.IsDeleted).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct)
                             : null);
        var permissions = User.Claims.Where(c => c.Type == "permission").Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var context = new RequestContext(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), userId, tenantId,
            User.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value).ToList(), permissions.ToList());
        var name = User.FindFirstValue("name") ?? User.FindFirstValue(ClaimTypes.Name) ?? userId?.ToString() ?? "unknown";
        return new CompCaller(tenantId, userId, name, permissions, employeeId, context);
    }
}
