using Microsoft.AspNetCore.Mvc;

namespace Zayra.Api.Controllers;

public partial class FinanceGlController
{
    /// <summary>STUB (TDD red).</summary>
    [HttpPut("cost-accounting")]
    public Task<IActionResult> SetCostAccounting([FromQuery] Guid companyId, [FromBody] CostAccountingSettingsRequest req, CancellationToken ct)
        => Task.FromResult<IActionResult>(StatusCode(501));

    /// <summary>STUB (TDD red).</summary>
    [HttpGet("cost-accounting/readiness")]
    public Task<IActionResult> CostAccountingReadiness([FromQuery] Guid companyId, CancellationToken ct)
        => Task.FromResult<IActionResult>(StatusCode(501));
}
