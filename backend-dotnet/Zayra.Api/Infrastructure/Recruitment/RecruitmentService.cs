using System.Text.Encodings.Web;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Recruitment;

public class RecruitmentService : IRecruitmentService
{
    private readonly ZayraDbContext _db;

    public RecruitmentService(ZayraDbContext db) => _db = db;

    // ── Number generation ──────────────────────────────────────────────────────

    public async Task<string> GenerateRequisitionNumberAsync(Guid tenantId, CancellationToken ct = default)
    {
        var year = DateTime.UtcNow.Year;
        var prefix = $"MRQ-{year}-";
        var last = await _db.ManpowerRequisitions
            .Where(r => r.TenantId == tenantId && r.RequisitionNumber.StartsWith(prefix))
            .OrderByDescending(r => r.RequisitionNumber)
            .Select(r => r.RequisitionNumber)
            .FirstOrDefaultAsync(ct);

        var seq = 1;
        if (last is not null)
        {
            var parts = last.Split('-');
            if (parts.Length == 3 && int.TryParse(parts[2], out var n)) seq = n + 1;
        }
        return $"{prefix}{seq:D4}";
    }

    public async Task<string> GenerateJobCodeAsync(Guid tenantId, CancellationToken ct = default)
    {
        var year = DateTime.UtcNow.Year;
        var prefix = $"JOB-{year}-";
        var last = await _db.JobOpenings
            .Where(j => j.TenantId == tenantId && j.JobCode.StartsWith(prefix))
            .OrderByDescending(j => j.JobCode)
            .Select(j => j.JobCode)
            .FirstOrDefaultAsync(ct);

        var seq = 1;
        if (last is not null)
        {
            var parts = last.Split('-');
            if (parts.Length == 3 && int.TryParse(parts[2], out var n)) seq = n + 1;
        }
        return $"{prefix}{seq:D4}";
    }

    // ── Approval integration ───────────────────────────────────────────────────

    public async Task<Guid?> CreateApprovalRequestAsync(
        Guid tenantId, string entityName, Guid entityId, string title,
        Guid? requestedByUserId, CancellationToken ct = default)
    {
        var workflow = await _db.ApprovalWorkflows
            .FirstOrDefaultAsync(w => w.TenantId == tenantId && w.EntityName == entityName && w.IsActive, ct);
        if (workflow is null) return null;

        var req = new ApprovalRequest
        {
            TenantId = tenantId,
            WorkflowId = workflow.Id,
            EntityName = entityName,
            EntityId = entityId.ToString(),
            Title = title,
            Status = "Pending",
            CurrentStepOrder = 1,
            RequestedByUserId = requestedByUserId,
        };
        _db.ApprovalRequests.Add(req);
        await _db.SaveChangesAsync(ct);
        return req.Id;
    }

    // ── Offer letter HTML ──────────────────────────────────────────────────────

    public string GenerateOfferLetterHtml(OfferLetterTemplateData d)
    {
        var today = DateTime.UtcNow.ToString("dd MMMM yyyy");
        var startFormatted = d.StartDate.ToString("dd MMMM yyyy");
        var probationEnd = d.StartDate.AddMonths(d.ProbationMonths).ToString("dd MMMM yyyy");
        // Stored-XSS defense: every free-text field interpolated into the offer HTML is HTML-encoded.
        // Numerics (:N2) and formatted dates are non-injectable. This document is stored as ContentHtml
        // and can later be served text/html, so encoding here closes the injection sink at the source.
        var candidateName = HtmlEncoder.Default.Encode(d.CandidateName ?? string.Empty);
        var jobTitle = HtmlEncoder.Default.Encode(d.JobTitle ?? string.Empty);
        var department = HtmlEncoder.Default.Encode(d.Department ?? string.Empty);
        var otherRow = d.OtherAllowances > 0
            ? $"<tr><td>Other Allowances</td><td>{d.OtherAllowances:N2}</td></tr>"
            : string.Empty;

        const string css = @"
  body { font-family: 'Segoe UI', Arial, sans-serif; font-size: 13px; color: #1e293b; margin: 0; padding: 0; background: #fff; }
  .page { max-width: 750px; margin: 0 auto; padding: 48px; }
  .header { display: flex; align-items: center; justify-content: space-between; border-bottom: 2px solid #2F6BFF; padding-bottom: 16px; margin-bottom: 32px; }
  .brand { font-size: 22px; font-weight: 700; color: #2F6BFF; letter-spacing: -0.5px; }
  .brand span { color: #00C896; }
  .date-block { text-align: right; font-size: 12px; color: #64748b; }
  h2 { font-size: 14px; font-weight: 600; color: #334155; margin: 24px 0 8px; }
  p { margin: 0 0 10px; line-height: 1.6; color: #334155; }
  .highlight { font-weight: 600; color: #1e293b; }
  table { width: 100%; border-collapse: collapse; margin: 12px 0 20px; }
  th { background: #f1f5f9; text-align: left; padding: 8px 12px; font-size: 11px; font-weight: 600; text-transform: uppercase; letter-spacing: .5px; color: #64748b; border: 1px solid #e2e8f0; }
  td { padding: 8px 12px; border: 1px solid #e2e8f0; color: #334155; }
  .total-row td { background: #eff6ff; font-weight: 700; color: #1e293b; }
  .signature-block { margin-top: 48px; display: flex; gap: 80px; }
  .sig { flex: 1; }
  .sig-line { border-top: 1px solid #94a3b8; margin-top: 40px; padding-top: 6px; font-size: 11px; color: #64748b; }
  .footer { margin-top: 40px; padding-top: 16px; border-top: 1px solid #e2e8f0; font-size: 10px; color: #94a3b8; text-align: center; }
  .badge { display: inline-block; background: #eff6ff; color: #2F6BFF; border-radius: 4px; padding: 2px 8px; font-size: 11px; font-weight: 600; margin-bottom: 16px; }";

        return $@"<!DOCTYPE html>
<html lang=""en"">
<head><meta charset=""UTF-8"" /><style>{css}</style></head>
<body>
<div class=""page"">
  <div class=""header"">
    <div class=""brand"">Zayra<span>HR</span></div>
    <div class=""date-block""><div>{today}</div><div>Confidential</div></div>
  </div>
  <div class=""badge"">OFFER OF EMPLOYMENT</div>
  <p>Dear <span class=""highlight"">{candidateName}</span>,</p>
  <p>We are pleased to extend this offer of employment to you at <strong>Zayra AI Workforce</strong>. After careful consideration of your application and interviews, we are confident you will be a valuable addition to our team.</p>
  <h2>Position Details</h2>
  <table>
    <tr><td width=""40%"">Job Title</td><td><strong>{jobTitle}</strong></td></tr>
    <tr><td>Department</td><td>{department}</td></tr>
    <tr><td>Start Date</td><td>{startFormatted}</td></tr>
    <tr><td>Employment Type</td><td>Full-Time, Permanent</td></tr>
    <tr><td>Probation Period</td><td>{d.ProbationMonths} months (ending {probationEnd})</td></tr>
  </table>
  <h2>Compensation Package</h2>
  <table>
    <tr><th>Component</th><th>Monthly (AED)</th></tr>
    <tr><td>Basic Salary</td><td>{d.BasicSalary:N2}</td></tr>
    <tr><td>Housing Allowance</td><td>{d.HousingAllowance:N2}</td></tr>
    <tr><td>Transport Allowance</td><td>{d.TransportAllowance:N2}</td></tr>
    {otherRow}
    <tr class=""total-row""><td>Total Monthly Package</td><td>{d.GrossSalary:N2}</td></tr>
  </table>
  <h2>Terms &amp; Conditions</h2>
  <p>1. Satisfactory reference checks and background verification.</p>
  <p>2. Submission of all required documentation prior to joining.</p>
  <p>3. Compliance with company policies, code of conduct, and applicable labor laws.</p>
  <p>4. This offer is valid for <strong>7 days</strong> from the date of issue.</p>
  <p>Please indicate your acceptance by signing and returning this letter to our HR department.</p>
  <div class=""signature-block"">
    <div class=""sig""><div class=""sig-line"">Authorized Signatory<br/>Human Resources</div></div>
    <div class=""sig""><div class=""sig-line"">Candidate Acceptance<br/>{candidateName}</div></div>
  </div>
  <div class=""footer"">System-generated offer letter &mdash; Zayra AI Workforce &mdash; Confidential</div>
</div>
</body>
</html>";
    }

    // ── Onboarding conversion ──────────────────────────────────────────────────

    public async Task<OfferAcceptanceResult> AcceptOfferAsync(
        Guid tenantId,
        Guid offerId,
        Guid requestedByUserId,
        string performedByName,
        CancellationToken ct = default)
    {
        var offer = await _db.OfferLetters.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == offerId && o.TenantId == tenantId, ct);
        if (offer is null)
            return Result(OfferAcceptanceOutcome.NotFound, offerId, null, null, "Offer not found.");

        var app = await _db.JobApplications.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == offer.ApplicationId && a.TenantId == tenantId, ct);
        if (app is null)
            return Result(OfferAcceptanceOutcome.IncompleteRecruitmentData, offerId, offer.ApplicationId, null,
                "The offer is not linked to an application in this tenant.");

        if (offer.Status == "Accepted" && app.OnboardingDraftId.HasValue)
            return Result(OfferAcceptanceOutcome.AlreadyAccepted, offerId, app.Id, app.OnboardingDraftId,
                "Offer was already accepted; the existing onboarding draft was returned.");

        if (offer.Status is not ("Sent" or "Accepted"))
            return Result(OfferAcceptanceOutcome.InvalidOfferState, offerId, app.Id, app.OnboardingDraftId,
                $"Offer must be in Sent status (current: {offer.Status}).");

        // An Accepted offer with no linked draft is a recoverable legacy partial write. A Sent
        // offer, however, may only hire an active application. This also prevents a second offer
        // for the same application from consuming another head-count seat.
        var recoveringLegacyAcceptance = offer.Status == "Accepted";
        if (!recoveringLegacyAcceptance && app.Status != "Active")
            return Result(OfferAcceptanceOutcome.InvalidApplicationState, offerId, app.Id, app.OnboardingDraftId,
                $"Only an active application can accept an offer (current: {app.Status}).");
        if (recoveringLegacyAcceptance && app.Status != "Hired")
            return Result(OfferAcceptanceOutcome.InvalidApplicationState, offerId, app.Id, app.OnboardingDraftId,
                "The accepted offer has an inconsistent application state and requires review.");

        var candidate = await _db.Candidates.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == app.CandidateId && c.TenantId == tenantId, ct);
        if (candidate is null)
            return Result(OfferAcceptanceOutcome.IncompleteRecruitmentData, offerId, app.Id, null,
                "The application candidate was not found in this tenant.");

        var opening = await _db.JobOpenings.AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == app.JobOpeningId && j.TenantId == tenantId, ct);
        if (opening is null)
            return Result(OfferAcceptanceOutcome.IncompleteRecruitmentData, offerId, app.Id, null,
                "The application job opening was not found in this tenant.");

        var fullName = $"{candidate.FirstName} {candidate.LastName}".Trim();

        // Auto-hierarchy: default the new joiner's reporting manager to the head of the
        // department they're joining, so the org tree applies without manual entry. HR can override.
        var deptHeadId = await _db.Departments.AsNoTracking()
            .Where(d => d.TenantId == tenantId && !d.IsDeleted && d.NameEn == offer.OfferedDepartment)
            .Select(d => d.ManagerEmployeeId)
            .FirstOrDefaultAsync(ct);

        var draft = new EmployeeDraft
        {
            TenantId = tenantId,
            CreatedByUserId = requestedByUserId,
            Status = "Submitted",
            CurrentStep = "EmploymentInformation",
            EnglishName = fullName,
            PersonalEmail = candidate.Email,
            Phone = candidate.Phone,
            Nationality = candidate.Nationality,
            Department = offer.OfferedDepartment,
            Designation = offer.OfferedJobTitle,
            ManagerEmployeeId = deptHeadId,
            JoiningDate = offer.StartDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            Salary = offer.BasicSalary,
            ContractType = "Permanent",
            ProbationEndDate = offer.StartDate.AddMonths(offer.ProbationMonths),
        };

        // Production providers use one transaction plus compare-and-swap writes. Exactly one
        // request can consume Sent -> Accepted, and exactly one request can claim the application's
        // null OnboardingDraftId. A losing concurrent request rolls back every side effect.
        if (_db.Database.IsRelational())
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                if (!recoveringLegacyAcceptance)
                {
                    var offerWon = await _db.OfferLetters
                        .Where(o => o.Id == offerId && o.TenantId == tenantId && o.Status == "Sent")
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(o => o.Status, "Accepted")
                            .SetProperty(o => o.AcceptedAtUtc, DateTime.UtcNow), ct);

                    if (offerWon != 1)
                    {
                        await tx.RollbackAsync(ct);
                        return await ResolveReplayAsync(tenantId, offerId, ct);
                    }
                }

                var now = DateTime.UtcNow;
                var appWon = await _db.JobApplications
                    .Where(a => a.Id == app.Id && a.TenantId == tenantId
                        && a.OnboardingDraftId == null
                        && a.Status == (recoveringLegacyAcceptance ? "Hired" : "Active"))
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(a => a.OnboardingDraftId, draft.Id)
                        .SetProperty(a => a.Stage, "Hired")
                        .SetProperty(a => a.StageOrder, 6)
                        .SetProperty(a => a.Status, "Hired")
                        .SetProperty(a => a.HiredAtUtc, a => a.HiredAtUtc ?? now)
                        .SetProperty(a => a.StageChangedAtUtc, a => a.StageChangedAtUtc ?? now), ct);

                if (appWon != 1)
                {
                    await tx.RollbackAsync(ct);
                    return await ResolveReplayAsync(tenantId, offerId, ct);
                }

                if (!recoveringLegacyAcceptance)
                {
                    var openingWon = await _db.JobOpenings
                        .Where(j => j.Id == opening.Id && j.TenantId == tenantId)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(j => j.Status,
                                j => j.FilledCount + 1 >= j.HeadCount ? "Closed" : j.Status)
                            .SetProperty(j => j.FilledCount, j => j.FilledCount + 1), ct);
                    if (openingWon != 1)
                        throw new InvalidOperationException("The job opening disappeared during offer acceptance.");
                }

                AddAcceptanceRecords(tenantId, offerId, app.Id, requestedByUserId, performedByName,
                    draft, recoveringLegacyAcceptance);
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                return Result(
                    recoveringLegacyAcceptance ? OfferAcceptanceOutcome.AlreadyAccepted : OfferAcceptanceOutcome.Accepted,
                    offerId, app.Id, draft.Id,
                    recoveringLegacyAcceptance
                        ? "Recovered the existing accepted offer's missing onboarding draft."
                        : "Offer accepted and onboarding started.");
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
        }

        // EF's in-memory provider is used by fast unit tests and cannot execute set-based CAS.
        // Preserve the same sequential state machine there; concurrency is verified against the
        // real relational provider in integration coverage.
        var trackedOffer = await _db.OfferLetters
            .FirstAsync(o => o.Id == offerId && o.TenantId == tenantId, ct);
        var trackedApp = await _db.JobApplications
            .FirstAsync(a => a.Id == app.Id && a.TenantId == tenantId, ct);
        if (trackedOffer.Status == "Accepted" && trackedApp.OnboardingDraftId.HasValue)
            return Result(OfferAcceptanceOutcome.AlreadyAccepted, offerId, app.Id, trackedApp.OnboardingDraftId,
                "Offer was already accepted; the existing onboarding draft was returned.");
        if (!recoveringLegacyAcceptance && (trackedOffer.Status != "Sent" || trackedApp.Status != "Active"))
            return Result(OfferAcceptanceOutcome.InvalidOfferState, offerId, app.Id, trackedApp.OnboardingDraftId,
                "Offer or application state changed before acceptance.");

        var trackedOpening = await _db.JobOpenings
            .FirstAsync(j => j.Id == opening.Id && j.TenantId == tenantId, ct);
        if (!recoveringLegacyAcceptance)
        {
            trackedOffer.Status = "Accepted";
            trackedOffer.AcceptedAtUtc = DateTime.UtcNow;
            trackedOpening.FilledCount++;
            if (trackedOpening.FilledCount >= trackedOpening.HeadCount) trackedOpening.Status = "Closed";
        }
        trackedApp.Stage = "Hired";
        trackedApp.StageOrder = 6;
        trackedApp.Status = "Hired";
        trackedApp.HiredAtUtc ??= DateTime.UtcNow;
        trackedApp.StageChangedAtUtc ??= DateTime.UtcNow;
        trackedApp.OnboardingDraftId = draft.Id;
        AddAcceptanceRecords(tenantId, offerId, app.Id, requestedByUserId, performedByName,
            draft, recoveringLegacyAcceptance);
        await _db.SaveChangesAsync(ct);
        return Result(
            recoveringLegacyAcceptance ? OfferAcceptanceOutcome.AlreadyAccepted : OfferAcceptanceOutcome.Accepted,
            offerId, app.Id, draft.Id,
            recoveringLegacyAcceptance
                ? "Recovered the existing accepted offer's missing onboarding draft."
                : "Offer accepted and onboarding started.");
    }

    private void AddAcceptanceRecords(
        Guid tenantId,
        Guid offerId,
        Guid applicationId,
        Guid requestedByUserId,
        string performedByName,
        EmployeeDraft draft,
        bool recoveringLegacyAcceptance)
    {
        _db.EmployeeDrafts.Add(draft);
        _db.ApplicationEvents.Add(new ApplicationEvent
        {
            TenantId = tenantId,
            ApplicationId = applicationId,
            EventType = recoveringLegacyAcceptance ? "OnboardingRecovered" : "OfferAccepted",
            Stage = "Hired",
            Notes = recoveringLegacyAcceptance
                ? "Recovered missing onboarding draft for an accepted offer."
                : "Offer accepted by candidate. Initiating onboarding.",
            PerformedByUserId = requestedByUserId,
            PerformedByName = performedByName,
        });
        _db.RecruitmentAuditLogs.Add(new RecruitmentAuditLog
        {
            TenantId = tenantId,
            EntityType = "Offer",
            EntityId = offerId.ToString(),
            Action = recoveringLegacyAcceptance ? "OnboardingRecovered" : "Accepted",
            PerformedByUserId = requestedByUserId,
            PerformedByName = performedByName,
            NewValuesJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                status = "Accepted",
                onboardingDraftId = draft.Id,
            }),
        });
    }

    private async Task<OfferAcceptanceResult> ResolveReplayAsync(
        Guid tenantId, Guid offerId, CancellationToken ct)
    {
        _db.ChangeTracker.Clear();
        var current = await _db.OfferLetters.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == offerId && o.TenantId == tenantId, ct);
        if (current is null)
            return Result(OfferAcceptanceOutcome.NotFound, offerId, null, null, "Offer not found.");

        var currentApp = await _db.JobApplications.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == current.ApplicationId && a.TenantId == tenantId, ct);
        if (current.Status == "Accepted" && currentApp?.OnboardingDraftId is { } draftId)
            return Result(OfferAcceptanceOutcome.AlreadyAccepted, offerId, current.ApplicationId, draftId,
                "Offer was already accepted; the existing onboarding draft was returned.");

        return Result(
            current.Status == "Sent"
                ? OfferAcceptanceOutcome.InvalidApplicationState
                : OfferAcceptanceOutcome.InvalidOfferState,
            offerId, current.ApplicationId, currentApp?.OnboardingDraftId,
            current.Status == "Sent"
                ? "The application was already hired or converted by another offer."
                : $"Offer cannot be accepted from its current state ({current.Status}).");
    }

    private static OfferAcceptanceResult Result(
        OfferAcceptanceOutcome outcome,
        Guid offerId,
        Guid? applicationId,
        Guid? onboardingDraftId,
        string message) => new(outcome, offerId, applicationId, onboardingDraftId, message);
}
