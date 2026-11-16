using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Finance;

namespace Zayra.Api.Controllers.Finance;

/// <summary>
/// Backfills the opening journals of loans and salary advances carried in before the import posted them
/// (<see cref="OpeningBalanceBackfillService"/> holds the rule and explains what is posted). Finance roles or Admin only,
/// company-scoped (import records naming no company are shown to group-level callers only), and gated on
/// finance.opening_balances.import too, so a per-user Deny is honoured. Never run on deploy: preview a company, then apply,
/// confirming the preview's digest.
/// </summary>
[ApiController]
[Route("api/finance/opening-balance-journals")]
[Authorize]
[HasPermission("finance.opening_balances.import")]
public class OpeningBalanceJournalsController(OpeningBalanceBackfillService backfill) : ControllerBase
{
    private const string OpeningBalanceImportPermission = "finance.opening_balances.import";
    private static readonly string[] FinanceRoles = { "Admin", "Finance", "Finance Controller", "Finance Approver" };

    [HttpGet("preview")]
    public async Task<IActionResult> Preview([FromQuery] Guid companyId, CancellationToken ct)
    {
        if (Refusal(companyId) is { } refusal) return refusal;
        return Ok(await backfill.PreviewAsync(this.GetTenantId()!.Value, companyId, this.GetEntityScope().IsGroupLevel, ct));
    }

    [HttpPost("apply")]
    public async Task<IActionResult> Apply([FromBody] OpeningBalanceBackfillRequest req, CancellationToken ct)
    {
        if (Refusal(req.CompanyId) is { } refusal) return refusal;
        var result = await backfill.ApplyAsync(this.GetTenantId()!.Value, req.CompanyId, this.GetEntityScope().IsGroupLevel, req.PreviewDigest,
            this.GetUserId(), User.Identity?.Name ?? string.Empty, HttpContext?.Connection.RemoteIpAddress?.ToString(), ct);
        return result.Stale ? Conflict(result.Body) : Ok(result.Body);
    }

    private IActionResult? Refusal(Guid companyId)
    {
        // The role AND the permission: the permission is what a per-user Deny takes away.
        if (!FinanceRoles.Any(User.IsInRole)
            || !User.Claims.Any(c => c.Type == "permission" && string.Equals(c.Value, OpeningBalanceImportPermission, StringComparison.OrdinalIgnoreCase)))
            return Forbid();
        if (this.GetTenantId() is null) return Unauthorized();
        if (!this.GetEntityScope().CanAccessCompany(companyId)) return Forbid();
        return null;
    }
}

/// <summary>The company whose carried-in loans and advances get their opening journals, and the digest of the preview the caller
/// confirmed (apply refuses with 409 if the plan has changed since).</summary>
public record OpeningBalanceBackfillRequest(Guid CompanyId, string? PreviewDigest);
