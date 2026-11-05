using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Payroll.ManpowerRecon;

namespace Zayra.Api.Controllers;

/// <summary>
/// The manpower and value reconciliation of a payroll run against the prior month, its drill-down, export and sign-off.
/// Thin: the bridge is built by <see cref="ManpowerReconService"/>, the sign-off by <see cref="ReconSignoffLifecycleService"/>.
/// Reading is <c>payroll.read</c>; signing and withdrawing are <c>payroll.approve</c> (no separate permission, so no role backfill).
/// </summary>
[ApiController]
[Route("api/payroll/runs/{id:guid}/recon")]
[Authorize]
public class PayrollReconController : ControllerBase
{
    private readonly IDataScopeService _scopeService;
    private readonly ManpowerReconService _recon;
    private readonly ReconSignoffLifecycleService _signoff;

    public PayrollReconController(ManpowerReconService recon, ReconSignoffLifecycleService signoff, IDataScopeService scopeService)
    {
        _recon = recon;
        _signoff = signoff;
        _scopeService = scopeService;
    }

    // ── Reads ────────────────────────────────────────────────────────────────────────────────────

    [HttpGet("")]
    [HasPermission("payroll.read")]
    public async Task<IActionResult> Get(Guid id, [FromQuery] Guid? priorRunId, CancellationToken cancellationToken)
    {
        if (!Holds("payroll.read")) return Forbid();
        var built = await BuildAsync(id, priorRunId, cancellationToken);
        return built.Result ?? Ok(built.Build!.Dto);
    }

    [HttpGet("employees")]
    [HasPermission("payroll.read")]
    public async Task<IActionResult> Employees(
        Guid id, [FromQuery] string section, [FromQuery] string? category, [FromQuery] string? kind, [FromQuery] string? itemCode,
        [FromQuery] string? bucket, [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default, [FromQuery] Guid? priorRunId = null)
    {
        if (!Holds("payroll.read")) return Forbid();
        var built = await BuildAsync(id, priorRunId, cancellationToken);
        if (built.Result is not null) return built.Result;
        var query = new ReconDrillQuery(section, category, kind, itemCode, bucket, page, pageSize);
        var rows = ManpowerReconService.DrillDown(built.Build!, query);
        if (rows is null)
            return BadRequest(new { error = "invalid_drilldown", message = "Choose a section (volume, value or finalSettlement) with a category, or an item code and bucket for value." });
        // Looking up the people behind a figure reads personal pay data: the first page of each lookup is recorded.
        if (page <= 1)
            await _signoff.RecordViewAsync(TenantId, id, UserId, Ip, query, rows.Total, built.Build!.Partial, built.Build.ProcessVersion, cancellationToken);
        return Ok(rows);
    }

    [HttpGet("export")]
    [HasPermission("payroll.read")]
    public async Task<IActionResult> Export(Guid id, [FromQuery] string format, [FromQuery] Guid? priorRunId, CancellationToken cancellationToken)
    {
        if (!Holds("payroll.read")) return Forbid();
        if (!string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase) && !string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "invalid_format", message = "Format must be xlsx or csv." });
        var built = await BuildAsync(id, priorRunId, cancellationToken);
        if (built.Result is not null) return built.Result;
        var build = built.Build!;
        if (build.Dto.Mode == ReconModes.NotApplicable)
            return Conflict(new { error = "recon_not_applicable", message = "An off-cycle run has no manpower and value reconciliation." });

        var file = ReconExporter.Create(build, format);
        await _signoff.RecordExportAsync(TenantId, id, UserId, Ip, format.ToLowerInvariant(), file.Label, build.Partial, build.ProcessVersion, cancellationToken);
        return File(file.Bytes, file.ContentType, file.FileName);
    }

    // ── Sign-off ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The Active sign-off, else the latest superseded one, else JSON null.</summary>
    [HttpGet("signoff")]
    [HasPermission("payroll.read")]
    public async Task<IActionResult> GetSignoff(Guid id, [FromQuery] Guid? priorRunId, CancellationToken cancellationToken)
    {
        if (!Holds("payroll.read")) return Forbid();
        var built = await BuildAsync(id, priorRunId, cancellationToken);
        if (built.Result is not null) return built.Result;
        return built.Build!.Dto.Signoff is { } dto ? Ok(dto) : Content("null", "application/json");
    }

    [HttpPost("signoff")]
    [HasPermission("payroll.approve")]
    public async Task<IActionResult> Signoff(Guid id, [FromBody] ReconSignoffRequest req, CancellationToken cancellationToken)
    {
        if (!Holds("payroll.approve")) return Forbid();
        var caller = await CallerAsync(cancellationToken);
        return Map(await _signoff.SignAsync(TenantId, id, caller, req, Ip, cancellationToken));
    }

    /// <summary>
    /// Withdraws the Active sign-off with a reason: nothing is deleted, the row is superseded (<c>Withdrawn</c>) by
    /// <see cref="ReconSignoffLifecycleService.WithdrawAsync"/> and stays as the record of what was reviewed.
    /// </summary>
    [HttpDelete("signoff")]
    [HasPermission("payroll.approve")]
    public async Task<IActionResult> WithdrawSignoff(Guid id, [FromBody] ReconWithdrawRequest? req, CancellationToken cancellationToken)
    {
        if (!Holds("payroll.approve")) return Forbid();
        var caller = await CallerAsync(cancellationToken);
        return Map(await _signoff.WithdrawAsync(TenantId, id, caller, req ?? new ReconWithdrawRequest(null), Ip, cancellationToken));
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────────────

    private Guid TenantId => Guid.Parse(User.FindFirstValue("tenant_id")!);
    private Guid? UserId => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id) ? id : null;
    private string? Ip => HttpContext?.Connection.RemoteIpAddress?.ToString();

    private bool Holds(string permission) =>
        User.Claims.Any(c => c.Type == "permission" && string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));

    private async Task<ReconCaller> CallerAsync(CancellationToken ct) => new(
        UserId, User.FindFirstValue(ClaimTypes.Name) ?? User.FindFirstValue("name") ?? "system", Holds("payroll.approve"),
        this.GetEntityScope(), await _scopeService.ResolveAsync(User, TenantId, ct));

    private async Task<(ReconBuild? Build, IActionResult? Result)> BuildAsync(Guid id, Guid? priorRunId, CancellationToken ct)
    {
        var result = await _recon.BuildAsync(TenantId, id, await CallerAsync(ct), priorRunId, ct);
        return result.Status switch
        {
            ReconBuildStatus.Ok => (result.Build, null),
            ReconBuildStatus.PriorRunInvalid => (null, BadRequest(new
            {
                error = PriorRunSelector.PriorRunInvalid,
                message = "The chosen comparison run must be an earlier, non-voided Regular or Replacement run of the same company.",
            })),
            _ => (null, NotFound()),
        };
    }

    private IActionResult Map(ReconActionResult r) =>
        r.Body is not null ? StatusCode(r.Status, r.Body) : r.Status == 404 ? NotFound() : StatusCode(r.Status);
}
