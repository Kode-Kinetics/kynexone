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
    public OffersController(ZayraDbContext db, ILetterService letters, IRecruitmentService svc) { _db = db; _letters = letters; _svc = svc; }

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
        var author = await OfferRules.AuthorAsync(_db, tid, id, ct);
        var sendVerdict = await OfferRules.EvaluateSendAsync(_db, offer, me, ct);
        var approval = new
        {
            required = await OfferRules.IsApprovalRequiredAsync(_db, offer, ct),
            isAuthor = me.HasValue && author == me,
            canSend = sendVerdict == OfferSendVerdict.Sendable,
            sendBlockedReason = sendVerdict is OfferSendVerdict.Sendable or OfferSendVerdict.InvalidState
                ? null
                : OfferRules.SendRefusal(sendVerdict).Message,
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
        var author = await OfferRules.AuthorAsync(_db, tid, id, ct);
        var options = await OfferRules.EligibleApprovers(_db, tid)
            .Where(u => author == null || u.Id != author)
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
    public async Task<IActionResult> Create([FromBody] CreateOfferRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();

        var app = await _db.JobApplications.FirstOrDefaultAsync(x => x.Id == req.ApplicationId && x.TenantId == tid, ct);
        if (app == null) return BadRequest("Application not found.");

        // The offer's department and designation must be records activation can match, or the
        // accepted offer's draft is refused at approval. Resolved (and re-spelled) here instead.
        var placement = await OfferPlacement.ResolveAsync(_db, tid,
            req.DepartmentId, req.OfferedDepartment, req.DesignationId, req.OfferedJobTitle, ct, companyId: app.CompanyId);
        if (!placement.IsResolved)
            return UnprocessableEntity(new { error = placement.Error, field = placement.Field, message = placement.Message });

        var gross = req.BasicSalary + req.HousingAllowance + req.TransportAllowance + req.OtherAllowances;

        var offer = new OfferLetter
        {
            TenantId = tid,
            CompanyId = app.CompanyId, // inherit legal entity from parent application
            ApplicationId = req.ApplicationId,
            CandidateName = app.CandidateName,
            OfferedJobTitle = placement.Designation,
            OfferedDepartment = placement.Department,
            StartDate = req.StartDate,
            BasicSalary = req.BasicSalary,
            HousingAllowance = req.HousingAllowance,
            TransportAllowance = req.TransportAllowance,
            OtherAllowances = req.OtherAllowances,
            GrossSalary = gross,
            ProbationMonths = req.ProbationMonths,
            // Encode-on-write: the caller-supplied HTML is an arbitrary-HTML sink stored verbatim
            // and later served as text/html. HTML-encoding here guarantees stored content can never
            // execute (stored-XSS defense over localStorage JWTs). Zero-dependency safe default;
            // swap for an allowlist sanitizer only if rich offer formatting is later required.
            ContentHtml = HtmlEncoder.Default.Encode(req.ContentHtml ?? string.Empty),
            ResponseDeadline = req.ResponseDeadline,
        };

        _db.OfferLetters.Add(offer);

        _db.ApplicationEvents.Add(new ApplicationEvent
        {
            TenantId = tid, ApplicationId = req.ApplicationId, EventType = "OfferGenerated",
            Stage = "Offer", Notes = $"Offer generated — Gross {gross:N0}",
            PerformedByUserId = GetUserId(), PerformedByName = GetUserName(),
        });

        _db.RecruitmentAuditLogs.Add(new RecruitmentAuditLog
        {
            TenantId = tid, EntityType = "Offer", EntityId = offer.Id.ToString(),
            Action = "Created", PerformedByUserId = GetUserId(), PerformedByName = GetUserName(),
            NewValuesJson = System.Text.Json.JsonSerializer.Serialize(new { offer.GrossSalary, offer.StartDate }),
        });

        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = offer.Id }, offer);
    }

    // PATCH /api/recruitment/offers/{id}/send
    [HttpPatch("{id:guid}/send")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Send(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        var offer = await _db.OfferLetters.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        if (offer == null) return NotFound();
        var verdict = await OfferRules.EvaluateSendAsync(_db, offer, GetUserId(), ct);
        if (verdict == OfferSendVerdict.InvalidState)
            return BadRequest("Offer must be in Draft or Approved state to send.");
        if (verdict != OfferSendVerdict.Sendable)
        {
            var (error, message) = OfferRules.SendRefusal(verdict);
            return Conflict(new { error, message });
        }

        offer.Status = "Sent";
        offer.SentAtUtc = DateTime.UtcNow;
        _db.RecruitmentAuditLogs.Add(OfferRules.AuditRow(tid, offer.Id, OfferRules.SentAction, GetUserId(), GetUserName()));

        _db.ApplicationEvents.Add(new ApplicationEvent
        {
            TenantId = tid, ApplicationId = offer.ApplicationId, EventType = "OfferSent",
            Stage = "Offer", Notes = "Offer letter sent to candidate",
            PerformedByUserId = GetUserId(), PerformedByName = GetUserName(),
        });

        await _db.SaveChangesAsync(ct);
        return Ok(offer);
    }

    // PATCH /api/recruitment/offers/{id}/accept
    [HttpPatch("{id:guid}/accept")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Accept(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        var acceptance = await _svc.AcceptOfferAsync(tid, id, GetUserId() ?? Guid.Empty, GetUserName(), ct);
        if (acceptance.Outcome == OfferAcceptanceOutcome.NotFound) return NotFound();
        if (!acceptance.IsSuccess)
            return Conflict(new { message = acceptance.Message, outcome = acceptance.Outcome.ToString() });

        var offer = await _db.OfferLetters.AsNoTracking()
            .FirstAsync(x => x.Id == id && x.TenantId == tid, ct);
        return Ok(new
        {
            offer,
            onboardingDraftId = acceptance.OnboardingDraftId,
            alreadyAccepted = !acceptance.WasAcceptedNow,
        });
    }

    // PATCH /api/recruitment/offers/{id}/decline
    [HttpPatch("{id:guid}/decline")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Decline(Guid id, [FromBody] DeclineOfferRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var offer = await _db.OfferLetters.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        if (offer == null) return NotFound();
        if (!OfferRules.CanDecline(offer))
            return Conflict(new
            {
                error = "invalid_offer_state",
                message = $"{OfferRules.DeclineStateMessage} (current: {offer.Status})"
            });

        offer.Status = "Declined";
        offer.DeclinedAtUtc = DateTime.UtcNow;
        offer.DeclineReason = req.Reason ?? string.Empty;

        _db.ApplicationEvents.Add(new ApplicationEvent
        {
            TenantId = tid, ApplicationId = offer.ApplicationId, EventType = "OfferDeclined",
            Stage = "Offer", Notes = $"Offer declined — {req.Reason}",
            PerformedByUserId = GetUserId(), PerformedByName = GetUserName(),
        });

        await _db.SaveChangesAsync(ct);
        return Ok(offer);
    }

    // GET /api/recruitment/offers/{id}/download — Download offer letter as PDF
    [HttpGet("{id:guid}/download")]
    [Authorize(Roles = "Admin,HR Manager,Recruiter")]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        var offer = await _db.OfferLetters.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        if (offer == null) return NotFound();
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
    public async Task<IActionResult> AddApproval(Guid id, [FromBody] AddOfferApprovalRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var offer = await _db.OfferLetters.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        if (offer == null) return NotFound();
        if (offer.Status is not ("Draft" or "PendingApproval"))
            return Conflict(new
            {
                error = "invalid_offer_state",
                message = $"Approval steps can only be configured while an offer is Draft or PendingApproval (current: {offer.Status})."
            });
        if (await OfferRules.HasRejectedApprovalAsync(_db, tid, id, ct))
            return Conflict(new { error = "offer_approval_rejected", message = OfferRules.ApprovalRejectedMessage });

        // Maker-checker: the step names one eligible person, who is not the offer's author.
        var approverVerdict = await OfferRules.EvaluateApproverAsync(_db, tid, id, req.ApproverUserId, ct);
        if (approverVerdict != OfferApproverVerdict.Eligible)
        {
            var (error, message) = OfferRules.ApproverRefusal(approverVerdict);
            return approverVerdict switch
            {
                OfferApproverVerdict.NoApproverNamed => BadRequest(new { error, message }),
                OfferApproverVerdict.ApproverIsAuthor => StatusCode(StatusCodes.Status403Forbidden, new { error, message }),
                OfferApproverVerdict.AuthorUnknown => Conflict(new { error, message }),
                _ => UnprocessableEntity(new { error, message }),
            };
        }
        if (await _db.OfferApprovals.AnyAsync(a => a.TenantId == tid && a.OfferLetterId == id
                && a.ApproverUserId == req.ApproverUserId && a.Status == "Pending", ct))
            return Conflict(new { error = "offer_approver_already_named", message = "This person is already an approver on this offer." });

        var approverName = string.IsNullOrWhiteSpace(req.ApproverName)
            ? await _db.Users.AsNoTracking().Where(u => u.Id == req.ApproverUserId).Select(u => u.FullName).FirstOrDefaultAsync(ct) ?? string.Empty
            : req.ApproverName;
        var nextStep = (await _db.OfferApprovals.Where(a => a.TenantId == tid && a.OfferLetterId == id).CountAsync(ct)) + 1;

        var approval = new OfferApproval
        {
            TenantId = tid, OfferLetterId = id, ApplicationId = offer.ApplicationId,
            StepOrder = nextStep, ApproverName = approverName,
            ApproverUserId = req.ApproverUserId, ApproverRole = req.ApproverRole ?? string.Empty,
        };

        _db.OfferApprovals.Add(approval);
        offer.Status = "PendingApproval";
        _db.RecruitmentAuditLogs.Add(OfferRules.AuditRow(tid, id, "ApprovalRequested", GetUserId(), GetUserName(),
            new { approval.StepOrder, approval.ApproverUserId, approval.ApproverName }));
        await _db.SaveChangesAsync(ct);
        return Ok(approval);
    }

    // PATCH /api/recruitment/offers/{id}/approvals/{approvalId}/decide
    [HttpPatch("{id:guid}/approvals/{approvalId:guid}/decide")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> DecideApproval(Guid id, Guid approvalId, [FromBody] DecideApprovalRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();

        // Shared checklist — see ApprovalDecisionGuard. Both records are resolved first so the
        // checklist runs in one place; the order of refusals and every response body are unchanged.
        var approval = await _db.OfferApprovals
            .FirstOrDefaultAsync(x => x.Id == approvalId && x.TenantId == tid && x.OfferLetterId == id, ct);
        var offer = await _db.OfferLetters.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        var decider = GetUserId();
        var author = offer is null ? null : await OfferRules.AuthorAsync(_db, tid, id, ct);

        var verdict = ApprovalDecisionGuard.Evaluate(new ApprovalDecisionSpec
        {
            Decision = req.Decision,
            AllowedDecisions = ApprovalDecisionGuard.ApprovedOrRejected,
            Step = new ApprovalStepState(approval is not null, approval?.Status ?? string.Empty),
            ParentLabel = "offer",
            ParentExists = offer is not null,
            ParentStatus = offer?.Status ?? string.Empty,
            ParentStatusesAllowingDecision = new[] { "PendingApproval" },
            Lock = ApprovalLock.None,                 // DECLARED ABSENCE: an offer has no payroll lock.
            // The maker is the offer's author, read from its Created audit row (OfferLetter has no
            // creator column). Approval only: an author may still reject, i.e. withdraw, their offer.
            MakerChecker = new MakerCheckerRule(
                author is { } maker && decider.HasValue && maker == decider,
                new[] { "Approved" },
                "The person who wrote the offer cannot approve it."),
        });
        if (!verdict.Passed) return OfferDecisionRefusal(verdict, approval?.Status, offer?.Status);

        // Guard postcondition: a passing verdict means both records were found.
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(offer);

        // A step is decided by the person it names, and nobody else.
        if (approval.ApproverUserId is null || decider is null || approval.ApproverUserId != decider)
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "not_the_named_approver",
                message = approval.ApproverUserId is null
                    ? "This approval step names no approver, so no one can decide it. Generate a new offer and request approval from a named person."
                    : $"Only {(string.IsNullOrWhiteSpace(approval.ApproverName) ? "the named approver" : approval.ApproverName)} can decide this approval step."
            });

        approval.Status = req.Decision;
        approval.Comments = req.Comments ?? string.Empty;
        approval.DecidedAtUtc = DateTime.UtcNow;

        // If all approvals are done, mark offer as Approved
        var allApprovals = await _db.OfferApprovals.Where(a => a.TenantId == tid && a.OfferLetterId == id).ToListAsync(ct);
        if (allApprovals.All(a => a.Status == "Approved")) offer.Status = "Approved";
        else if (req.Decision == "Rejected") offer.Status = "Draft";
        _db.RecruitmentAuditLogs.Add(OfferRules.AuditRow(tid, id, OfferRules.ApprovalDecidedAction, decider, GetUserName(),
            new { approvalId, approval.StepOrder, decision = req.Decision, offerStatus = offer.Status }));

        await _db.SaveChangesAsync(ct);
        return Ok(approval);
    }
}

/// <summary>The job title and department are resolved against the organisation's designation and
/// department records (<see cref="OfferPlacement"/>); an id, when given, wins over the text.</summary>
public record CreateOfferRequest(
    Guid ApplicationId, string OfferedJobTitle, string? OfferedDepartment,
    DateOnly StartDate, decimal BasicSalary, decimal HousingAllowance,
    decimal TransportAllowance, decimal OtherAllowances, int ProbationMonths,
    string? ContentHtml, DateTime? ResponseDeadline,
    Guid? DepartmentId = null, Guid? DesignationId = null);

public record DeclineOfferRequest(string? Reason);
public record OfferPlacementOption(Guid Id, string Name, string Code);
public record OfferPlacementOptions(IReadOnlyList<OfferPlacementOption> Departments, IReadOnlyList<OfferPlacementOption> Designations);
public record AddOfferApprovalRequest(string ApproverName, Guid? ApproverUserId, string? ApproverRole);
public record OfferApproverOption(Guid UserId, string Name, string Email);
public record DecideApprovalRequest(string Decision, string? Comments);
