using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Geo;

namespace Zayra.Api.Controllers.Geo;

/// <summary>
/// Location pinning helpers for Setup. A pasted Google Maps short link is resolved here because the browser cannot
/// follow it cross-origin; everything else (full links, plain or DMS coordinates) is parsed in the browser.
/// </summary>
[ApiController]
[Route("api/geo")]
[Authorize]
public class GeoController : ControllerBase
{
    public const string RateLimitPolicy = "geo_resolve";
    public const string UnreadableCode = "ERR_MAP_LINK_UNREADABLE";
    private readonly MapLinkResolver _resolver;
    public GeoController(MapLinkResolver resolver) => _resolver = resolver;

    /// <summary>Only <c>{ latitude, longitude }</c>, or 422 <c>ERR_MAP_LINK_UNREADABLE</c>. Nothing else from Google is returned.</summary>
    [HttpGet("resolve-map-link")]
    [EnableRateLimiting(RateLimitPolicy)]
    public async Task<IActionResult> ResolveMapLink([FromQuery] string? url, CancellationToken ct)
    {
        if (this.GetTenantId() is null) return Unauthorized();
        Response.Headers["Cache-Control"] = "no-store";
        var found = await _resolver.ResolveAsync(url, ct);
        if (found is null)
            return UnprocessableEntity(new
            {
                error = UnreadableCode,
                message = "This map link could not be read. Open it, then copy the full address from the browser bar, or paste the coordinates.",
            });
        return Ok(new { latitude = found.Latitude, longitude = found.Longitude });
    }
}
