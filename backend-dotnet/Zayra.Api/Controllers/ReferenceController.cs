using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Common;

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
}
