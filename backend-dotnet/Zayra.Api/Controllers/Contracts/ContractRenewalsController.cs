using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;

namespace Zayra.Api.Controllers.Contracts;

/// <summary>
/// The contract renewal API (Release A requirement 2), split into one partial file per slice so no two slices edit
/// the same file:
/// <list type="bullet">
/// <item><c>ContractRenewalsController.Radar.cs</c> — R4: dashboard, case DTO, chain, open-now, hold/release/cancel.</item>
/// <item><c>ContractRenewalsController.Offer.cs</c> — R5: review, recommendation, offer draft and submit, batch.</item>
/// <item><c>ContractRenewalsController.Response.cs</c>, <c>.Qiwa.cs</c>, <c>.Apply.cs</c> — R6.</item>
/// </list>
/// This file (R0, integration owner) holds the route, the shared constructor and the helpers. A slice that needs a
/// service takes it as an action parameter with <c>[FromServices]</c> rather than editing this constructor.
/// Routes outside <c>api/contracts/renewals</c> (e.g. the chain at <c>api/contracts/{contractId}/chain</c>) use an
/// absolute template (<c>~/api/contracts/...</c>). Every action carries an explicit <c>[HasPermission]</c>
/// (<c>contracts.renewal.read</c> / <c>contracts.renewal.manage</c>). The whole /api/contracts prefix is closed unless the
/// tenant has the release_a flag on (OptInFeatures).
/// </summary>
[Authorize]
[ApiController]
[Route("api/contracts/renewals")]
public sealed partial class ContractRenewalsController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly ITenantClock _clock;

    public ContractRenewalsController(ZayraDbContext db, ITenantClock clock)
    {
        _db = db;
        _clock = clock;
    }

    private Guid RequireTenant() => this.GetTenantId() ?? throw new UnauthorizedAccessException("Tenant claim missing.");

    private Guid? CurrentUserId() => this.GetUserId();

    /// <summary>Tenant-local today: every renewal date rule reads this, never UTC.</summary>
    private Task<DateOnly> TodayAsync(CancellationToken ct) => _clock.TodayAsync(RequireTenant(), ct);
}
