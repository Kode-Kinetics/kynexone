using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Recruitment;

[Authorize]
[ApiController]
[Route("api/recruitment/offers")]
public class OffersController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly ILetterService _letters;
    private readonly IRecruitmentService _svc;
    private readonly OfferLifecycleService _lifecycle;
    private readonly OfferArtifactService? _artifacts;
    public OffersController(ZayraDbContext db, ILetterService letters, IRecruitmentService svc,
        OfferLifecycleService? lifecycle = null, OfferArtifactService? artifacts = null)
    { _db = db; _letters = letters; _svc = svc; _lifecycle = lifecycle ?? new(db, new ConfigurationBuilder().Build()); _artifacts = artifacts; }

    private OfferActor Actor(OfferLetter offer) => new(offer.TenantId, offer.CompanyId, GetUserId() ?? Guid.Empty, GetUserName());
    private IActionResult LifecycleFailure(OfferLifecycleException e)
    {
        var body = new { error = e.Code, message = e.Message };
        return e.Status switch { 409 => Conflict(body), 404 => NotFound(body), 422 => UnprocessableEntity(body), 400 => BadRequest(body), _ => StatusCode(e.Status, body) };
    }

    private Guid GetTenantId() =>
        Guid.TryParse(User.FindFirst("tenant_id")?.Value, out var id) ? id : Guid.Empty;

    private Guid? GetUserId() =>
        Guid.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    private string GetUserName() => User.FindFirst("name")?.Value ?? User.Identity?.Name ?? "System";

    /// <summary>Maps a shared-guard refusal to the exact response this endpoint has always
    /// returned. Note "This approval is already in ..." — Loans says "This approval step is
    /// already in ...". The wording differs per module and is preserved rather than unified.</summary>
    private IActionResult OfferDecisionRefusal(ApprovalGuardVerdict verdict, string? stepStatus, string? offerStatus) => verdict.Outcome switch
    {
        ApprovalGuardOutcome.DecisionOutsideVocabulary =>
            BadRequest(new { error = "invalid_decision", message = "Decision must be Approved or Rejected." }),
        ApprovalGuardOutcome.StepNotFound or ApprovalGuardOutcome.ParentNotFound => NotFound(),
        ApprovalGuardOutcome.StepAlreadyDecided =>
            Conflict(new
            {
                error = "approval_already_decided",
                message = $"This approval is already in '{stepStatus}' status."
            }),
        ApprovalGuardOutcome.ParentStateForbidsDecision =>
            Conflict(new
            {
                error = "invalid_offer_state",
                message = $"Offer approval decisions require PendingApproval status (current: {offerStatus})."
            }),
        ApprovalGuardOutcome.MakerIsChecker =>
            StatusCode(StatusCodes.Status403Forbidden, new { error = "offer_maker_checker", message = verdict.Message }),
        ApprovalGuardOutcome.DeciderApprovedEarlierStep =>
            StatusCode(StatusCodes.Status403Forbidden, new { error = "offer_earlier_step_approver", message = verdict.Message }),
        _ => throw new InvalidOperationException($"Unhandled approval guard outcome '{verdict.Outcome}'."),
    };

    // GET /api/recruitment/offers?applicationId=...&status=...
    [HttpGet]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Recruiter")]
    public async Task<IActionResult> List(
        [FromQuery] Guid? applicationId = null,
        [FromQuery] string? status = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var tid = GetTenantId();
        var q = _db.OfferLetters.Where(x => x.TenantId == tid);

        if (applicationId.HasValue) q = q.Where(x => x.ApplicationId == applicationId.Value);
        if (!string.IsNullOrEmpty(status)) q = q.Where(x => x.Status == status);

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(x => x.GeneratedAtUtc)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        return Ok(new { total, page, pageSize, items });
    }

    // GET /api/recruitment/offers/{id}
    [HttpGet("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Recruiter")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        var offer = await _db.OfferLetters.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        if (offer == null) return NotFound();

        var approvals = await _db.OfferApprovals
            .Where(x => x.TenantId == tid && x.OfferLetterId == id)
            .OrderBy(x => x.StepOrder).ToListAsync(ct);

        // What the caller can do next, from the same rules the actions enforce, so the screen
        // shows one clear action instead of a button that will be refused.
        var me = GetUserId();
        var writers = await OfferRules.TermsWritersAsync(_db, tid, id, ct);
        var approval = new
        {
            required = await OfferRules.IsApprovalRequiredAsync(_db, offer, ct),
            isAuthor = me.HasValue && writers.Contains(me.Value),
            canSend = false,
            sendBlockedReason = "Open the offer workspace and choose explicit email delivery or record confirmed external sending.",
            myPendingStepId = approvals
                .Where(a => a.Status == "Pending" && me.HasValue && a.ApproverUserId == me && offer.Status == "PendingApproval")
                .Select(a => (Guid?)a.Id).FirstOrDefault(),
        };

        return Ok(new { offer, approvals, approval });
    }

    // GET /api/recruitment/offers/{id}/approver-options
    /// <summary>The people who can approve this offer: active HR Managers and Admins, other than the
    /// offer's author.</summary>
    [HttpGet("{id:guid}/approver-options")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> ApproverOptions(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        if (!await _db.OfferLetters.AnyAsync(x => x.Id == id && x.TenantId == tid, ct)) return NotFound();
        var writers = await OfferRules.TermsWritersAsync(_db, tid, id, ct);
        var options = await OfferRules.EligibleApprovers(_db, tid)
            .Where(u => !writers.Contains(u.Id))
            .OrderBy(u => u.FullName)
            .Select(u => new OfferApproverOption(u.Id, u.FullName, u.Email))
            .ToListAsync(ct);
        return Ok(options);
    }

    // GET /api/recruitment/offers/placement-options
    /// <summary>The active departments and designations an offer can place a hire in: the records
    /// activation resolves against. Open to everyone who can create an offer, including recruiters,
    /// who cannot read the organisation setup screens.</summary>
    [HttpGet("placement-options")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Recruiter")]
    public async Task<IActionResult> PlacementOptions(CancellationToken ct)
    {
        var tid = GetTenantId();
        var departments = await _db.Departments.AsNoTracking()
            .Where(d => d.TenantId == tid && d.IsActive && !d.IsDeleted)
            .OrderBy(d => d.NameEn)
            .Select(d => new OfferPlacementOption(d.Id, d.NameEn, d.Code))
            .ToListAsync(ct);
        var designations = await _db.Designations.AsNoTracking()
            .Where(d => d.TenantId == tid && d.IsActive && !d.IsDeleted)
            .OrderBy(d => d.TitleEn)
            .Select(d => new OfferPlacementOption(d.Id, d.TitleEn, d.Code))
            .ToListAsync(ct);
        return Ok(new OfferPlacementOptions(departments, designations));
    }

    // POST /api/recruitment/offers
    [HttpPost]
    [Authorize(Roles = "Admin,HR Manager,Recruiter")]
    [OfferAudience(OfferAudienceRequirement.Author)]
    public async Task<IActionResult> Create([FromBody] CreateOfferRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();

        var app = await _db.JobApplications.FirstOrDefaultAsync(x => x.Id == req.ApplicationId && x.TenantId == tid, ct);
        if (app == null) return BadRequest("Application not found.");
        if (req.Components is not null && !_lifecycle.Enabled) return Conflict(new { error = "managed_offer_lifecycle_disabled", message = "Managed component offers are not enabled for this deployment." });

        try
        {
            var offer = await _lifecycle.CreateAsync(new(tid, app.CompanyId, GetUserId() ?? Guid.Empty, GetUserName()), app.Id,
                new(req.StartDate, req.ProbationMonths, req.OfferedJobTitle, req.OfferedDepartment, req.DepartmentId, req.DesignationId,
                    req.ResponseDeadline, req.TemplateId, req.Components, req.BasicSalary, req.HousingAllowance, req.TransportAllowance,
                    req.OtherAllowances, req.ContentHtml, req.ExpectedApplicationVersion), ct);
            return CreatedAtAction(nameof(Get), new { id = offer.Id }, offer);
        }
        catch (OfferLifecycleException e) { return LifecycleFailure(e); }
    }

    // PATCH /api/recruitment/offers/{id}/send
    [HttpPatch("{id:guid}/send")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Send(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        var offer = await _db.OfferLetters.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        if (offer == null) return NotFound();
        return Conflict(new { error = "explicit_delivery_required", message = "This endpoint no longer records delivery. Review a frozen managed revision and use /email or /external-sends. Existing sent offers remain readable." });
    }

    // PATCH /api/recruitment/offers/{id}/accept
    [HttpPatch("{id:guid}/accept")]
    [Authorize(Roles = "Admin,HR Manager")]
    [OfferAudience(OfferAudienceRequirement.Manager)]
    public async Task<IActionResult> Accept(Guid id, CancellationToken ct,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] OfferResponseRequest? request = null)
    {
        var tid = GetTenantId();
        var offer = await _db.OfferLetters.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        if (offer is null) return NotFound();
        if (request is null || request.Decision != "Accepted") return Conflict(new { error = "explicit_response_required", message = "Submit expectedVersion, decision Accepted, source StaffRecorded, occurredAtUtc and reason, or use /responses." });
        OfferResponseDto acceptance;
        try { acceptance = await _lifecycle.RespondAsync(Actor(offer), id, request, ct); }
        catch (OfferLifecycleException e) { return LifecycleFailure(e); }
        offer = await _db.OfferLetters.AsNoTracking().SingleAsync(x => x.Id == id && x.TenantId == tid, ct);
        return Ok(new
        {
            offer,
            onboardingDraftId = acceptance.OnboardingDraftId,
            alreadyAccepted = acceptance.AlreadyRecorded,
        });
    }

    // PATCH /api/recruitment/offers/{id}/decline
    [HttpPatch("{id:guid}/decline")]
    [Authorize(Roles = "Admin,HR Manager")]
    [OfferAudience(OfferAudienceRequirement.Manager)]
    public async Task<IActionResult> Decline(Guid id, [FromBody] DeclineOfferRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var offer = await _db.OfferLetters.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        if (offer == null) return NotFound();
        if (req.Response() is not { } response) return Conflict(new { error = "explicit_response_required", message = "Submit expectedVersion, decision Declined, source StaffRecorded, occurredAtUtc and reason, or use /responses." });
        try { await _lifecycle.RespondAsync(Actor(offer), id, response, ct); }
        catch (OfferLifecycleException e) { return LifecycleFailure(e); }
        return Ok(await _db.OfferLetters.AsNoTracking().SingleAsync(o => o.Id == id && o.TenantId == tid, ct));
    }

    // GET /api/recruitment/offers/{id}/download — Download offer letter as PDF
    [HttpGet("{id:guid}/download")]
    [Authorize(Roles = "Admin,HR Manager,Recruiter")]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        var offer = await _db.OfferLetters.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        if (offer == null) return NotFound();
        if (offer.SeriesId is not null || offer.ArtifactId is not null)
        {
            try
            {
                if (_artifacts is null) throw new OfferLifecycleException("offer_storage_unavailable", "Original document verification is unavailable.");
                var bytes = await _artifacts.OriginalAsync(Actor(offer), id, ct);
                Response.Headers["X-Offer-Provenance"] = "FrozenOriginal";
                return File(bytes, "application/pdf", $"offer-original-{id}.pdf");
            }
            catch (OfferLifecycleException e) { return LifecycleFailure(e); }
        }
        Response.Headers["X-Offer-Provenance"] = "LegacyReconstructed";
        if (offer.DesignSnapshotJson is { Length: > 0 } snapshotJson)
        {
            var snapshot = System.Text.Json.JsonSerializer.Deserialize<OfferDesignSnapshot>(snapshotJson, OfferLetterDesign.Json)!;
            return File(OfferLetterDesign.Render(snapshot), "application/pdf", $"offer-letter-{offer.Id}.pdf");
        }
        var tenant = await _db.Tenants.AsNoTracking().Select(t => new { t.Id, t.Name }).FirstOrDefaultAsync(t => t.Id == tid, ct);
        var offerCurrency = await OfferRules.ResolveCurrencyAsync(_db, tid, offer.CompanyId, ct);
        var data = new OfferLetterData(
            CandidateName: offer.CandidateName,
            Position: offer.OfferedJobTitle,
            Department: offer.OfferedDepartment,
            Salary: offer.GrossSalary,
            Currency: offerCurrency,
            StartDate: offer.StartDate.ToDateTime(TimeOnly.MinValue),
            CompanyName: tenant?.Name ?? "KynexOne Technologies",
            IssuedBy: GetUserName(),
            IssuedDate: DateTime.UtcNow
        );
        var pdf = await _letters.GenerateOfferLetterAsync(data, ct);
        return File(pdf, "application/pdf", $"offer-letter-{offer.CandidateName.Replace(" ", "-")}.pdf");
    }

    // POST /api/recruitment/offers/{id}/approvals — Add approval step
    [HttpPost("{id:guid}/approvals")]
    [Authorize(Roles = "Admin,HR Manager")]
    [OfferAudience(OfferAudienceRequirement.Manager)]
    public async Task<IActionResult> AddApproval(Guid id, [FromBody] AddOfferApprovalRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var offer = await _db.OfferLetters.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        if (offer == null) return NotFound();
        if (req.ExpectedVersion is not { } version) return Conflict(new { error = "offer_version_required", message = "Reload the offer and submit its expectedVersion." });
        try
        {
            // Storage preparation/finalization occurs before submission; a failed upload cannot
            // leave PendingApproval pointing at an unfinished or reconstructed document.
            if (offer.SeriesId is not null && offer.FrozenAtUtc is null)
            {
                if (_artifacts is null) throw new OfferLifecycleException("offer_storage_unavailable", "Original document verification is unavailable.");
                version = (await _artifacts.FreezeAsync(Actor(offer), id, version, ct)).OfferVersion;
            }
            return Ok(await _lifecycle.AddApprovalAsync(Actor(offer), id, version, req.ApproverUserId, req.ApproverName, req.ApproverRole, ct));
        }
        catch (OfferLifecycleException e) { return LifecycleFailure(e); }
    }

    // PATCH /api/recruitment/offers/{id}/approvals/{approvalId}/decide
    [HttpPatch("{id:guid}/approvals/{approvalId:guid}/decide")]
    [Authorize(Roles = "Admin,HR Manager")]
    [OfferAudience(OfferAudienceRequirement.Approver)]
    public async Task<IActionResult> DecideApproval(Guid id, Guid approvalId, [FromBody] DecideApprovalRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();

        var offer = await _db.OfferLetters.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        if (offer is null) return NotFound();
        if (req.Decision is not ("Approved" or "Rejected"))
            return BadRequest(new { error = "invalid_decision", message = "Decision must be Approved or Rejected." });
        try { return Ok(await _lifecycle.DecideApprovalAsync(Actor(offer), id, approvalId, req.ExpectedVersion, req.Decision, req.Comments, ct)); }
        catch (OfferLifecycleException e) { return LifecycleFailure(e); }
    }
}

/// <summary>The job title and department are resolved against the organisation's designation and
/// department records (<see cref="OfferPlacement"/>); an id, when given, wins over the text.</summary>
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public record CreateOfferRequest(
    Guid ApplicationId, string OfferedJobTitle, string? OfferedDepartment,
    DateOnly StartDate, decimal? BasicSalary, decimal? HousingAllowance,
    decimal? TransportAllowance, decimal? OtherAllowances, int ProbationMonths,
    string? ContentHtml, DateTime? ResponseDeadline,
    Guid? DepartmentId = null, Guid? DesignationId = null, Guid? TemplateId = null,
    IReadOnlyList<OfferComponentInput>? Components = null, long? ExpectedApplicationVersion = null);

public record DeclineOfferRequest(string? Reason, long? ExpectedVersion = null, string? Decision = null,
    string? Source = null, DateTime? OccurredAtUtc = null, string? EvidenceReference = null)
{
    public OfferResponseRequest? Response() => ExpectedVersion is { } version && Decision == "Declined" && Source is not null && OccurredAtUtc is { } at
        ? new(version, Decision, Source, at, Reason ?? string.Empty, EvidenceReference) : null;
}
public record OfferPlacementOption(Guid Id, string Name, string Code);
public record OfferPlacementOptions(IReadOnlyList<OfferPlacementOption> Departments, IReadOnlyList<OfferPlacementOption> Designations);
public record AddOfferApprovalRequest(string ApproverName, Guid? ApproverUserId, string? ApproverRole, long? ExpectedVersion = null);
public record OfferApproverOption(Guid UserId, string Name, string Email);
public record DecideApprovalRequest(string Decision, string? Comments, long? ExpectedVersion = null);
