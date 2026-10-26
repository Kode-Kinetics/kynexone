using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Authorization;

namespace Zayra.Api.Controllers;

/// <summary>Read-only ISO standards reference data (countries / currencies) for dropdowns + validation.</summary>
[ApiController]
[Route("api/reference")]
[Authorize]
public class ReferenceController : ControllerBase
{
    [HttpGet("countries")]
    public IActionResult Countries() =>
        Ok(IsoReference.Countries.Select(c => new { c.Code, c.Name, c.Currency }));

    [HttpGet("currencies")]
    public IActionResult Currencies() =>
        Ok(IsoReference.Currencies.Select(c => new { c.Code, c.Name, c.Symbol }));

    /// <summary>CD29: Saudi banks by IBAN bank code (positions 5-6). Static reference data, cacheable.</summary>
    [HttpGet("sa-banks")]
    [HasPermission("employees.read")]
    [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Any)]
    public IActionResult SaBanks() =>
        Ok(Zayra.Api.Infrastructure.Payroll.SaudiBanks.All.Select(b => new { code = b.Code, nameEn = b.NameEn, nameAr = b.NameAr }));
}
