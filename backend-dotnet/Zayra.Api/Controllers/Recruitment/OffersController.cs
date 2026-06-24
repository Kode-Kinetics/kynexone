using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Recruitment;

[Authorize]
[ApiController]
[Route("api/recruitment/offers")]
public class OffersController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly ILetterService _letters;
    public OffersController(ZayraDbContext db, ILetterService letters) { _db = db; _letters = letters; }

    private Guid GetTenantId() =>
        Guid.TryParse(User.FindFirst("tenant_id")?.Value, out var id) ? id : Guid.Empty;

    private Guid? GetUserId() =>
        Guid.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    private string GetUserName() => User.FindFirst("name")?.Value ?? User.Identity?.Name ?? "System";

    // GET /api/recruitment/offers?applicationId=...&status=...
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] Guid? applicationId = null,
        [FromQuery] string? status = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
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
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        var offer = await _db.OfferLetters.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        if (offer == null) return NotFound();

        var approvals = await _db.OfferApprovals
            .Where(x => x.TenantId == tid && x.OfferLetterId == id)
            .OrderBy(x => x.StepOrder).ToListAsync(ct);

        return Ok(new { offer, approvals });
    }

    // POST /api/recruitment/offers
    [HttpPost]
    [Authorize(Roles = "Admin,HR Manager,Recruiter")]
    public async Task<IActionResult> Create([FromBody] CreateOfferRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();

        var app = await _db.JobApplications.FirstOrDefaultAsync(x => x.Id == req.ApplicationId && x.TenantId == tid, ct);
        if (app == null) return BadRequest("Application not found.");

        var gross = req.BasicSalary + req.HousingAllowance + req.TransportAllowance + req.OtherAllowances;

        var offer = new OfferLetter
        {
            TenantId = tid,
            ApplicationId = req.ApplicationId,
            CandidateName = app.CandidateName,
            OfferedJobTitle = req.OfferedJobTitle,
            OfferedDepartment = req.OfferedDepartment ?? string.Empty,
            StartDate = req.StartDate,
            BasicSalary = req.BasicSalary,
            HousingAllowance = req.HousingAllowance,
            TransportAllowance = req.TransportAllowance,
            OtherAllowances = req.OtherAllowances,
            GrossSalary = gross,
            ProbationMonths = req.ProbationMonths,
            ContentHtml = req.ContentHtml ?? string.Empty,
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
        if (offer.Status != "Draft" && offer.Status != "Approved") return BadRequest("Offer must be in Draft or Approved state to send.");

        offer.Status = "Sent";
        offer.SentAtUtc = DateTime.UtcNow;

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
        var offer = await _db.OfferLetters.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        if (offer == null) return NotFound();

        offer.Status = "Accepted";
        offer.AcceptedAtUtc = DateTime.UtcNow;

        // Advance application to Hired
        var app = await _db.JobApplications.FirstOrDefaultAsync(x => x.Id == offer.ApplicationId && x.TenantId == tid, ct);
        if (app != null)
        {
            app.Stage = "Hired"; app.StageOrder = 6; app.Status = "Active"; app.HiredAtUtc = DateTime.UtcNow;
            _db.ApplicationEvents.Add(new ApplicationEvent
            {
                TenantId = tid, ApplicationId = app.Id, EventType = "OfferAccepted",
                Stage = "Hired", Notes = "Candidate accepted offer — Hired",
                PerformedByUserId = GetUserId(), PerformedByName = GetUserName(),
            });
        }

        _db.RecruitmentAuditLogs.Add(new RecruitmentAuditLog
        {
            TenantId = tid, EntityType = "Offer", EntityId = id.ToString(),
            Action = "Accepted", PerformedByUserId = GetUserId(), PerformedByName = GetUserName(),
        });

        await _db.SaveChangesAsync(ct);
        return Ok(offer);
    }

    // PATCH /api/recruitment/offers/{id}/decline
    [HttpPatch("{id:guid}/decline")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Decline(Guid id, [FromBody] DeclineOfferRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var offer = await _db.OfferLetters.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        if (offer == null) return NotFound();

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
        var offerCurrency = await _db.ResolveTenantCurrencyAsync(tid, ct);
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

        var nextStep = (await _db.OfferApprovals.Where(a => a.TenantId == tid && a.OfferLetterId == id).CountAsync(ct)) + 1;

        var approval = new OfferApproval
        {
            TenantId = tid, OfferLetterId = id, ApplicationId = offer.ApplicationId,
            StepOrder = nextStep, ApproverName = req.ApproverName,
            ApproverUserId = req.ApproverUserId, ApproverRole = req.ApproverRole ?? string.Empty,
        };

        _db.OfferApprovals.Add(approval);
        offer.Status = "PendingApproval";
        await _db.SaveChangesAsync(ct);
        return Ok(approval);
    }

    // PATCH /api/recruitment/offers/{id}/approvals/{approvalId}/decide
    [HttpPatch("{id:guid}/approvals/{approvalId:guid}/decide")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> DecideApproval(Guid id, Guid approvalId, [FromBody] DecideApprovalRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var approval = await _db.OfferApprovals
            .FirstOrDefaultAsync(x => x.Id == approvalId && x.TenantId == tid && x.OfferLetterId == id, ct);
        if (approval == null) return NotFound();

        approval.Status = req.Decision;
        approval.Comments = req.Comments ?? string.Empty;
        approval.DecidedAtUtc = DateTime.UtcNow;

        // If all approvals are done, mark offer as Approved
        var offer = await _db.OfferLetters.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        if (offer != null)
        {
            var allApprovals = await _db.OfferApprovals.Where(a => a.TenantId == tid && a.OfferLetterId == id).ToListAsync(ct);
            if (allApprovals.All(a => a.Status == "Approved")) offer.Status = "Approved";
            else if (req.Decision == "Rejected") offer.Status = "Draft";
        }

        await _db.SaveChangesAsync(ct);
        return Ok(approval);
    }
}

public record CreateOfferRequest(
    Guid ApplicationId, string OfferedJobTitle, string? OfferedDepartment,
    DateOnly StartDate, decimal BasicSalary, decimal HousingAllowance,
    decimal TransportAllowance, decimal OtherAllowances, int ProbationMonths,
    string? ContentHtml, DateTime? ResponseDeadline);

public record DeclineOfferRequest(string? Reason);
public record AddOfferApprovalRequest(string ApproverName, Guid? ApproverUserId, string? ApproverRole);
public record DecideApprovalRequest(string Decision, string? Comments);
