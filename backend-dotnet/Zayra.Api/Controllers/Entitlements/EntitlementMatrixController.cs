using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Entitlements;

namespace Zayra.Api.Controllers.Entitlements;

/// <summary>
/// Benefits by grade (Release A requirement 1). R0 ships the read-only component catalogue — the shared contract of
/// plan §1.1 — and slice R1 adds the matrix, publish, offerings and pay-scale import to this controller (R1 owns the
/// file from here). Every route under /api/entitlements is closed unless the tenant has the release_a flag on
/// (OptInFeatures, enforced by FeatureFlagGuardFilter).
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
}
