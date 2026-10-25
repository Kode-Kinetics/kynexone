using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Licensing;

namespace Zayra.Api.Controllers.Public;

/// <summary>
/// What a client needs to know about this installation before anyone signs in (implementation prompt WP7.6): whether it
/// has been set up, its organisation's name, the public addresses to use, whether push notifications are on, and the
/// licence state. The web app reads it on start; the mobile app reads it to validate a server address.
/// </summary>
/// <remarks>
/// Anonymous, so it says nothing more: never the limits, the installation id, user counts or an internal host. The
/// shape is pinned by <c>PublicInstallationTests</c>.
/// </remarks>
[ApiController]
[Route("api/public/installation")]
[AllowAnonymous]
[EnableRateLimiting("public_info")]
public sealed class PublicInstallationController : ControllerBase
{
    public const string PushEnabledConfigKey = "Notifications:Push:Enabled";

    private readonly ZayraDbContext _db;
    private readonly ILicenceService _licence;
    private readonly IConfiguration _config;

    public PublicInstallationController(ZayraDbContext db, ILicenceService licence, IConfiguration config)
    {
        _db = db;
        _licence = licence;
        _config = config;
    }

    [HttpGet]
    public async Task<ActionResult<PublicInstallationInfo>> Get(CancellationToken ct)
    {
        var organisation = await _db.Tenants.AsNoTracking()
            .Where(t => t.IsActive)
            .OrderBy(t => t.CreatedAtUtc)
            .Select(t => t.Name)
            .FirstOrDefaultAsync(ct);
        var appUrl = (_config["APP_URL"] ?? string.Empty).Trim().TrimEnd('/');
        return Ok(new PublicInstallationInfo(
            SetupComplete: organisation is not null,
            OrganisationName: organisation,
            PublicBaseUrl: appUrl,
            DeviceIngestUrl: string.IsNullOrEmpty(appUrl) ? "/api/attendance/ingest" : appUrl + "/api/attendance/ingest",
            PushEnabled: IsPushEnabled(_config),
            Licence: new PublicLicenceInfo(_licence.State.ToString())));
    }

    /// <summary>Whether push notifications may be sent (they need outbound HTTPS to exp.host). Default on; the client kit turns it off.</summary>
    public static bool IsPushEnabled(IConfiguration config) =>
        !bool.TryParse(config[PushEnabledConfigKey], out var enabled) || enabled;
}

public sealed record PublicInstallationInfo(
    bool SetupComplete,
    string? OrganisationName,
    string PublicBaseUrl,
    string DeviceIngestUrl,
    bool PushEnabled,
    PublicLicenceInfo Licence);

public sealed record PublicLicenceInfo(string State);
