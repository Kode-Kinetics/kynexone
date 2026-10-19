using Zayra.Api.Application.Recruitment;
using Zayra.Api.Models;
using Microsoft.EntityFrameworkCore;
using System.Net.Mail;
using System.Text.Encodings.Web;
using Zayra.Api.Infrastructure.Documents.Letters;

namespace Zayra.Api.Infrastructure.Recruitment;

public sealed partial class OfferLifecycleService
{
    public Task<OfferLetter> CreateAsync(OfferActor actor, Guid applicationId, OfferCreateInput input, CancellationToken ct = default) =>
        OfferApplicationBoundary.ExecuteAsync(db, actor, applicationId, async app =>
        {
            if (input.Components is not null) RequireEnabled();
            if (Enabled && input.ExpectedApplicationVersion is null)
                throw new OfferLifecycleException("application_version_required", "Reload the application and submit expectedApplicationVersion.");
            if (input.ExpectedApplicationVersion is { } expected && app.Version != expected)
                throw new OfferLifecycleException("application_version_conflict", "The application changed. Reload before creating another offer.");
            OfferApplicationBoundary.RequireUnreserved(app); OfferApplicationBoundary.RequireActive(app);
            if (app.Stage != "Offer") throw new OfferLifecycleException("application_offer_stage_required", "Move the application to Offer before generating its offer.");
            if (Enabled && actor.CompanyId is null) throw new OfferLifecycleException("offer_company_required", "Managed offers require a legal company.", 422);
            var deadline = input.ResponseDeadline ?? DateTime.UtcNow.AddDays(7);
            if (input.StartDate == DateOnly.MinValue || input.ProbationMonths is < 0 or > 12 || deadline.Kind != DateTimeKind.Utc || deadline <= DateTime.UtcNow)
                throw new OfferLifecycleException("offer_terms_invalid", "Choose a start date, future UTC deadline and probation from zero to twelve months.", 422);
            var opening = await db.JobOpenings.AsNoTracking().SingleOrDefaultAsync(o => o.TenantId == actor.TenantId && o.Id == app.JobOpeningId, ct)
                ?? throw new OfferLifecycleException("offer_opening_not_found", "The application's job opening was not found.", 422);
            var placement = await OfferPlacement.ResolveAsync(db, actor.TenantId,
                input.DepartmentId ?? (string.IsNullOrWhiteSpace(input.OfferedDepartment) ? opening.DepartmentId : null),
                string.IsNullOrWhiteSpace(input.OfferedDepartment) ? opening.DepartmentName : input.OfferedDepartment,
                input.DesignationId ?? (string.IsNullOrWhiteSpace(input.OfferedJobTitle) ? opening.DesignationId : null),
                string.IsNullOrWhiteSpace(input.OfferedJobTitle) ? app.JobTitle : input.OfferedJobTitle, ct, companyId: actor.CompanyId);
            if (!placement.IsResolved) throw new OfferLifecycleException(placement.Error ?? "offer_placement_unresolved", placement.Message ?? "Resolve the offer placement.", 422);
            OfferCompensationSnapshot compensation;
            try { compensation = await OfferComponentOptions.CalculateAsync(db, actor.TenantId, actor.CompanyId, input.StartDate, input.Components,
                input.BasicSalary, input.HousingAllowance, input.TransportAllowance, input.OtherAllowances, ct); }
            catch (ArgumentException e) { throw new OfferLifecycleException("offer_compensation_invalid", e.Message, 422); }
            var candidateName = app.CandidateName; var email = app.CandidateEmail;
            if (Enabled)
            {
                var candidate = await db.Candidates.AsNoTracking().SingleOrDefaultAsync(c => c.TenantId == actor.TenantId && c.CompanyId == actor.CompanyId && c.Id == app.CandidateId, ct)
                    ?? throw new OfferLifecycleException("offer_candidate_not_found", "Resolve the application's candidate in this legal company.", 422);
                candidateName = $"{candidate.FirstName} {candidate.LastName}".Trim(); email = candidate.Email.Trim();
                if (string.IsNullOrWhiteSpace(candidateName) || !MailAddress.TryCreate(email, out var address) || address.Address != email)
                    throw new OfferLifecycleException("offer_recipient_invalid", "Resolve the candidate's name and complete email before reviewing an offer.", 422);
            }
            var offer = new OfferLetter { TenantId = actor.TenantId, CompanyId = actor.CompanyId, ApplicationId = app.Id,
                CandidateId = app.CandidateId, CandidateName = candidateName, RecipientEmail = email, OfferedJobTitle = placement.Designation,
                OfferedDepartment = placement.Department, StartDate = input.StartDate, ProbationMonths = input.ProbationMonths, ResponseDeadline = deadline };
            OfferCompensation.ApplySnapshot(offer, compensation);
            offer.ContentHtml = input.ContentHtml is not null ? HtmlEncoder.Default.Encode(input.ContentHtml)
                : new RecruitmentService(db).GenerateOfferLetterHtml(new(candidateName, placement.Designation, placement.Department,
                    input.StartDate, compensation.BasicSalary, compensation.HousingAllowance, compensation.TransportAllowance,
                    compensation.OtherAllowances, compensation.MonthlyCash, input.ProbationMonths, compensation.Currency));
            try
            {
                await OfferLetterDesign.ApplyTemplateAsync(db, offer, input.TemplateId, ct);
                if (Enabled && input.TemplateId is null)
                    offer.DesignSnapshotJson = OfferLetterDesign.SerializeSnapshot(
                        await OfferLetterDesign.SnapshotAsync(db, offer, OfferLetterDesign.DynamicDefault(), null, null, ct));
            }
            catch (ArgumentException e) { throw new OfferLifecycleException("offer_design_invalid", e.Message, 422); }
            if (Enabled)
            {
                var series = new OfferSeries { TenantId = actor.TenantId, CompanyId = actor.CompanyId, ApplicationId = app.Id };
                db.OfferSeries.Add(series); offer.SeriesId = series.Id; offer.RevisionNumber = 1;
                AddEvent(actor, offer, "DraftCreated", "Created a new managed offer draft.");
            }
            var tracked = db.JobApplications.Local.FirstOrDefault(a => a.Id == app.Id);
            if (tracked is not null) db.Entry(tracked).State = EntityState.Detached;
            db.JobApplications.Attach(app); app.OfferedSalary = compensation.MonthlyCash; app.Version++;
            db.OfferLetters.Add(offer);
            db.RecruitmentAuditLogs.Add(OfferRules.AuditRow(actor.TenantId, offer.Id, OfferRules.CreatedAction, actor.UserId, actor.Name,
                new { offer.GrossSalary, offer.StartDate, currency = compensation.Currency }));
            db.ApplicationEvents.Add(new() { TenantId = actor.TenantId, ApplicationId = app.Id, EventType = "OfferGenerated", Stage = "Offer",
                Notes = $"Created an independent offer draft: {compensation.Currency} {compensation.MonthlyCash:N2} monthly; prior drafts are retained.", PerformedByUserId = actor.UserId, PerformedByName = actor.Name });
            return offer;
        }, ct);
}
