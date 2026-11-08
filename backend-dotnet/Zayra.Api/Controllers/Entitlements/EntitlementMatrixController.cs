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
    /// POST /api/entitlements/components — adds a benefit of the customer's own to the grid (a non-paying contract benefit, set per
    /// grade like the rest). Needs access to every company: a new benefit is a row for the whole group.
    /// </summary>
    [HttpPost("components")]
    [HasPermission("entitlements.manage")]
    public async Task<IActionResult> AddBenefit([FromServices] EntitlementMatrixService matrix, [FromBody] CreateBenefitRequest req,
        CancellationToken ct) =>
        this.GetTenantId() is null ? Unauthorized() : (IActionResult)Result(await matrix.CreateBenefitAsync(Actor(), req, ct));

    /// <summary>
    /// PUT /api/entitlements/components/{code} — edits the definition of a benefit the customer added (names, category, policy class, dates, active),
    /// with <c>expectedVersion</c> from the matrix read (409 <c>stale_definition</c> when someone else changed it first). Same permission and group
    /// scope as adding it; serialised with every other matrix write; audited with the old and new values. Behind the <c>benefit_definition_v1</c> feature.
    /// A cash allowance's money terms are not editable here once Finance has confirmed it (they go through Finance's correction), and changing the
    /// dates or policy class of one without the Finance permission puts it back to "pending Finance". The response carries an <c>ETag</c>.
    /// </summary>
    [HttpPut("components/{code}")]
    [HasPermission("entitlements.manage")]
    public async Task<IActionResult> UpdateBenefit([FromServices] EntitlementMatrixService matrix, string code, [FromBody] UpdateBenefitRequest req,
        CancellationToken ct)
    {
        if (this.GetTenantId() is null) return Unauthorized();
        var result = await matrix.UpdateBenefitAsync(Actor(), code, req, ct);
        if (result.IsSuccess && result.Body is MatrixComponentDto { Definition: { } definition })
            Response.Headers.ETag = $"\"{definition.Version}\"";
        return Result(result);
    }

    /// <summary>
    /// GET /api/entitlements/components/cash-gl-drivers — the earning GL drivers a monthly cash allowance may name, each marked <c>usable</c> (or not, with
    /// a plain reason), judged by exactly the rule <c>POST /components</c> applies to a cash benefit. It exists so the person adding the benefit
    /// (HR, who holds <c>entitlements.manage</c> but not the finance GL permissions) can choose a driver without being handed the chart of accounts:
    /// key, label and the verdict only; no account numbers, no balances. Shape: <c>[{ key, label, labelAr, usable, reasonCode, reason }]</c>.
    /// </summary>
    [HttpGet("components/cash-gl-drivers")]
    [HasPermission("entitlements.manage")]
    public async Task<IActionResult> CashGlDrivers([FromServices] EntitlementMatrixService matrix, CancellationToken ct) =>
        this.GetTenantId() is not Guid tid ? Unauthorized() : Ok(await matrix.CashGlDriversAsync(tid, ct));

    /// <summary>
    /// POST /api/entitlements/components/{code}/confirm — Finance confirms a monthly cash allowance that someone without the Finance permission added
    /// ("pending Finance"). Body (all optional): <c>{ glDriverKey?, eosbIncluded?, eosbExclusionCategory?, eosbExclusionReason? }</c>; left out, it is confirmed
    /// as entered, and Finance may change the GL driver or the end-of-service setting in the same action. The person who added it can never confirm it
    /// (403 <c>maker_cannot_confirm</c>). Audited. 200 returns the component; 409 <c>already_confirmed</c> / <c>not_a_cash_allowance</c>.
    /// </summary>
    [HttpPost("components/{code}/confirm")]
    [HasPermission("payroll.approve")]
    public async Task<IActionResult> ConfirmBenefit([FromServices] EntitlementMatrixService matrix, string code, [FromBody] ConfirmAllowanceRequest? req,
        CancellationToken ct) =>
        this.GetTenantId() is null ? Unauthorized() : (IActionResult)Result(await matrix.ConfirmBenefitAsync(Actor(), code, req ?? new ConfirmAllowanceRequest(), ct));

    /// <summary>
    /// POST /api/entitlements/components/{code}/correct — Finance changes a confirmed cash allowance's GL driver and/or end-of-service setting from the first
    /// day of a month (<c>effectiveFrom</c>), never earlier than the first payroll month no company has approved or locked. A new effective-dated version is
    /// written; the old one is closed the day before. Body: <c>{ effectiveFrom, reason, glDriverKey?, eosbIncluded?, eosbExclusionCategory?, eosbExclusionReason? }</c>.
    /// Audited with the old and new values and the reason.
    /// </summary>
    [HttpPost("components/{code}/correct")]
    [HasPermission("payroll.approve")]
    public async Task<IActionResult> CorrectBenefit([FromServices] EntitlementMatrixService matrix, string code, [FromBody] CorrectAllowanceRequest req,
        CancellationToken ct) =>
        this.GetTenantId() is null ? Unauthorized() : (IActionResult)Result(await matrix.CorrectBenefitAsync(Actor(), code, req, ct));

    /// <summary>POST /api/entitlements/components/{code}/remove — removes a benefit the customer added and nobody has used yet.</summary>
    [HttpPost("components/{code}/remove")]
    [HasPermission("entitlements.manage")]
    public async Task<IActionResult> RemoveBenefit([FromServices] EntitlementMatrixService matrix, string code, CancellationToken ct) =>
        this.GetTenantId() is null ? Unauthorized() : (IActionResult)Result(await matrix.RemoveBenefitAsync(Actor(), code, ct));

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
        HttpContext?.Connection.RemoteIpAddress?.ToString(), User.HasPermission(FinancePermission));

    /// <summary>The permission that makes someone "Finance" for cash allowances: the one that approves a salary change and a payroll run.</summary>
    public const string FinancePermission = "payroll.approve";

    private ObjectResult Result(MatrixResult result) => StatusCode(result.Status, result.Body);
}
