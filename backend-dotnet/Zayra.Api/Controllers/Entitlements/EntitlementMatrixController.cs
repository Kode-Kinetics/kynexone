using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Entitlements;

namespace Zayra.Api.Controllers.Entitlements;

/// <summary>
/// Benefits by grade (Release A requirement 1, slice R1). One matrix, grade by component, for every benefit; per company
/// each benefit is adopted from the group, tailored, or skipped (statutory floors never). Publishing is future-dated,
/// close-only and audited, as the L1 loan grid is. The legacy grade mechanisms (grade pay scales, benefit eligibility
/// rules) are frozen for Release A tenants and imported here with a preview. Every route under /api/entitlements is
/// closed unless the tenant has the release_a flag on (OptInFeatures, enforced by FeatureFlagGuardFilter).
///
/// <para>Services come in through <c>[FromServices]</c> so the controller keeps the parameterless constructor the R0
/// contract test builds it with.</para>
/// </summary>
[Authorize]
[ApiController]
[Route("api/entitlements")]
public sealed class EntitlementMatrixController : ControllerBase
{
    /// <summary>One component of the catalogue, as the matrix and its cell drawer need it.</summary>
    public sealed record EntitlementComponentDto(
        string Code,
        string NameEn,
        string NameAr,
        string Class,
        string Floor,
        IReadOnlyList<string> AllowedValueTypes,
        IReadOnlyList<string> AllowedCoverageTiers,
        IReadOnlyList<string> AllowedDependantScopes,
        IReadOnlyList<string> AllowedLimitPeriods,
        string? DefaultLimitPeriod,
        bool AllowsDependants,
        bool IsFloor,
        bool CanBeSkipped,
        bool IsLoanFacility);

    /// <summary>GET /api/entitlements/components — every catalogue component and the rules its cells follow.</summary>
    [HttpGet("components")]
    [HasPermission("entitlements.read")]
    public ActionResult<IReadOnlyList<EntitlementComponentDto>> Components() =>
        Ok(EntitlementComponentRules.Catalogue
            .Select(r => new EntitlementComponentDto(
                r.Code, r.NameEn, r.NameAr, r.Class, r.Floor, r.AllowedValueTypes, r.AllowedCoverageTiers,
                r.AllowedDependantScopes, r.AllowedLimitPeriods, r.DefaultLimitPeriod, r.AllowsDependants, r.IsFloor,
                EntitlementComponentRules.CanBeSkipped(r), r.IsLoanFacility))
            .ToList());

    /// <summary>
    /// GET /api/entitlements/matrix?companyId=&amp;asOf= — the grid as of a date (default: today in the tenant's calendar).
    /// Without a company: the group grid. With one: that company's view, its own cells over the group defaults, with the
    /// Adopted / Tailored / Skipped mode of each benefit and the grades still without a value.
    /// </summary>
    [HttpGet("matrix")]
    [HasPermission("entitlements.read")]
    public async Task<IActionResult> Matrix([FromServices] EntitlementMatrixService matrix, [FromQuery] Guid? companyId,
        [FromQuery] DateOnly? asOf, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        if (companyId.HasValue && !this.GetEntityScope().CanAccessCompany(companyId)) return Forbid();
        return Ok(await matrix.ReadAsync(tid, companyId, asOf ?? await matrix.TodayAsync(tid, ct), ct));
    }

    /// <summary>
    /// PUT /api/entitlements/matrix — publishes changed cells from <c>effectiveFrom</c> (today or later). Each changed cell
    /// closes the version in force the day before and opens a new one; an audit row per cell. Group cells need access to
    /// every company; company cells need access to that company. <c>?dryRun=true</c> writes nothing and returns the
    /// counts (published, unchanged, employees affected now and at renewal) for the confirmation step.
    /// </summary>
    [HttpPut("matrix")]
    [HasPermission("entitlements.manage")]
    public async Task<IActionResult> Publish([FromServices] EntitlementMatrixService matrix, [FromBody] PublishMatrixRequest req,
        [FromQuery] bool dryRun, CancellationToken ct) =>
        this.GetTenantId() is null ? Unauthorized() : (IActionResult)Result(await matrix.PublishAsync(Actor(), req, dryRun, ct));

    /// <summary>
    /// PUT /api/entitlements/offerings — one company adopts (offers) or skips a benefit from the first day of a month.
    /// Statutory floors (housing, transport, medical) are refused with the legal reason; loans are offered per company
    /// in Loans.
    /// </summary>
    [HttpPut("offerings")]
    [HasPermission("entitlements.manage")]
    public async Task<IActionResult> SetOffering([FromServices] EntitlementMatrixService matrix, [FromBody] SetOfferingRequest req,
        CancellationToken ct) =>
        this.GetTenantId() is null ? Unauthorized() : (IActionResult)Result(await matrix.SetOfferingAsync(Actor(), req, ct));

    /// <summary>
    /// POST /api/entitlements/matrix/import-legacy?commit=false|true&amp;effectiveFrom= — brings the frozen grade pay
    /// scales into the matrix as group cells. Preview and commit list the same rows; rows that can't be mapped without
    /// guessing (and every benefit eligibility rule, which carries no value) are shown with the reason and never written.
    /// </summary>
    [HttpPost("matrix/import-legacy")]
    [HasPermission("entitlements.manage")]
    public async Task<IActionResult> ImportLegacy([FromServices] EntitlementMatrixService matrix, [FromQuery] bool commit,
        [FromQuery] DateOnly? effectiveFrom, CancellationToken ct) =>
        this.GetTenantId() is null ? Unauthorized() : (IActionResult)Result(await matrix.ImportLegacyAsync(Actor(), effectiveFrom, commit, ct));

    private MatrixActor Actor() => new(this.GetTenantId()!.Value, this.GetUserId(), this.GetEntityScope(),
        HttpContext?.Connection.RemoteIpAddress?.ToString());

    private ObjectResult Result(MatrixResult result) => StatusCode(result.Status, result.Body);
}
