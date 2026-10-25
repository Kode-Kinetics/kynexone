using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Licensing;

namespace Zayra.Api.Controllers;

/// <summary>
/// The installation's licence, for the organisation's administrators: what it grants, what is used against it, and
/// importing a renewed one. Reachable when no valid licence is installed (<see cref="Zayra.Api.Infrastructure.Filters.LicenceGuardFilter"/>
/// skips this route), so an administrator can always sign in and import one.
/// </summary>
[ApiController]
[Route("api/organisation-admin/licence")]
[HasPermission("security.manage")]
public sealed class OrganisationLicenceController : ControllerBase
{
    /// <summary>A licence file is a few kilobytes; this refuses anything that cannot be one before reading it.</summary>
    private const long MaxLicenceBytes = 64 * 1024;

    private readonly ZayraDbContext _db;
    private readonly ILicenceService _licence;
    private readonly LicenceImporter _importer;

    public OrganisationLicenceController(ZayraDbContext db, ILicenceService licence, LicenceImporter importer)
    {
        _db = db;
        _licence = licence;
        _importer = importer;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var snapshot = _licence.Current;
        var p = snapshot.Payload;
        var usage = await LicenceUsage.MeasureAsync(_db, ct);
        return Ok(new
        {
            state = _licence.State.ToString(),
            installationId = await InstallationIdentity.EnsureAsync(_db, ct),
            licenceId = p?.LicenceId,
            licensee = p?.Licensee,
            edition = p?.Edition,
            accountType = snapshot.AccountType,
            issuedAtUtc = p?.IssuedAtUtc,
            expiresAtUtc = p?.ExpiresAtUtc,
            graceDays = p?.GraceDays,
            supportExpiresAtUtc = p?.SupportExpiresAtUtc,
            limits = new
            {
                maxEmployees = snapshot.MaxEmployees,
                maxUsers = snapshot.MaxUsers,
                maxCompanies = snapshot.MaxCompanies,
                maxAdminUsers = snapshot.MaxAdminUsers,
                aiMonthlyTokenLimit = snapshot.AiMonthlyTokenLimit,
            },
            usage = new
            {
                activeEmployees = usage.ActiveEmployees,
                activeUsers = usage.ActiveUsers,
                companies = usage.Companies,
            },
            modules = p?.Modules ?? [],
            optInFeatures = p?.OptInFeatures ?? [],
            countries = p?.Countries,
            version = Zayra.Api.Infrastructure.Operations.BuildInfo.Version,
        });
    }

    /// <summary>Imports a licence file (multipart field <c>file</c>). Every attempt is audited with the importing user.</summary>
    [HttpPost]
    [RequestSizeLimit(MaxLicenceBytes * 2)]
    public async Task<IActionResult> Import(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "licence_file_required", message = "Choose the licence file your supplier sent (kynexone.licence)." });
        if (file.Length > MaxLicenceBytes)
            return BadRequest(new { error = "licence_file_too_large", message = "That file is too large to be a KynexOne licence." });

        string json;
        using (var reader = new StreamReader(file.OpenReadStream()))
            json = await reader.ReadToEndAsync(ct);

        var actor = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
                    ?? User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Email)?.Value
                    ?? this.GetUserId()?.ToString()
                    ?? "organisation_admin";
        var result = await _importer.ImportAsync(json, this.GetUserId(), actor, ct);
        if (!result.Succeeded)
            return UnprocessableEntity(new
            {
                error = "licence_rejected",
                message = "The licence was not accepted: " + string.Join(" ", result.Problems),
                problems = result.Problems,
            });

        return Ok(new
        {
            outcome = result.Outcome.ToString(),
            state = _licence.State.ToString(),
            licenceId = result.Payload!.LicenceId,
            licensee = result.Payload.Licensee,
        });
    }
}
