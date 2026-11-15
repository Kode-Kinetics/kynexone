using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Finance;

namespace Zayra.Api.Controllers.Finance;

/// <summary>
/// Backfills the opening journals of loans and salary advances carried in before the import posted them
/// (<see cref="OpeningBalanceBackfillService"/> holds the rule and explains what is posted). Finance roles or Admin only,
/// company-scoped, never run on deploy: preview a company, then apply.
/// </summary>
[ApiController]
[Route("api/finance/opening-balance-journals")]
[Authorize]
public class OpeningBalanceJournalsController(OpeningBalanceBackfillService backfill) : ControllerBase
{
    private static readonly string[] FinanceRoles = { "Admin", "Finance", "Finance Controller", "Finance Approver" };

    [HttpGet("preview")]
    public async Task<IActionResult> Preview([FromQuery] Guid companyId, CancellationToken ct)
    {
        if (Refusal(companyId) is { } refusal) return refusal;
        return Ok(await backfill.PreviewAsync(this.GetTenantId()!.Value, companyId, ct));
    }

    [HttpPost("apply")]
    public async Task<IActionResult> Apply([FromBody] OpeningBalanceBackfillRequest req, CancellationToken ct)
    {
        if (Refusal(req.CompanyId) is { } refusal) return refusal;
        return Ok(await backfill.ApplyAsync(this.GetTenantId()!.Value, req.CompanyId, this.GetUserId(), User.Identity?.Name ?? string.Empty,
            HttpContext?.Connection.RemoteIpAddress?.ToString(), ct));
    }

    private IActionResult? Refusal(Guid companyId)
    {
        if (!FinanceRoles.Any(User.IsInRole)) return Forbid();
        if (this.GetTenantId() is null) return Unauthorized();
        if (!this.GetEntityScope().CanAccessCompany(companyId)) return Forbid();
        return null;
    }
}

/// <summary>The company whose carried-in loans and advances get their opening journals.</summary>
public record OpeningBalanceBackfillRequest(Guid CompanyId);
