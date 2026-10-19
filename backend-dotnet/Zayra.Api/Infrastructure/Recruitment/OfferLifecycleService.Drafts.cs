using System.Net.Mail;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Recruitment;

public sealed partial class OfferLifecycleService
{
    public Task<OfferLetter> SaveDraftAsync(OfferActor actor, Guid offerId, OfferDraftInput input, CancellationToken ct = default) =>
        MutateAsync(actor, offerId, input.ExpectedVersion, true, async (app, offer) =>
        {
            if (offer.Status != "Draft" || offer.FrozenAtUtc is not null || offer.ArtifactId is not null
                || await db.OfferApprovals.AnyAsync(a => a.TenantId == actor.TenantId && a.OfferLetterId == offer.Id, ct))
                throw new OfferLifecycleException("offer_terms_immutable", "These terms have entered approval or been frozen. Create a new revision.");
            OfferApplicationBoundary.RequireUnreserved(app); OfferApplicationBoundary.RequireActive(app);
            if (actor.CompanyId is not { } company)
                throw new OfferLifecycleException("offer_company_required", "Managed offers require a legal company.", 422);
            var candidateId = offer.CandidateId ?? app.CandidateId;
            if (candidateId == Guid.Empty || candidateId != app.CandidateId
                || !await db.Candidates.AnyAsync(c => c.TenantId == actor.TenantId && c.CompanyId == company && c.Id == candidateId, ct))
                throw new OfferLifecycleException("offer_candidate_not_found", "Resolve the application's candidate in this legal company.", 422);
            if (offer.SeriesId is null && !input.AdoptLegacy)
                throw new OfferLifecycleException("offer_adoption_required", "Explicitly adopt and review this legacy draft before saving managed terms.");
            var policy = await OfferComponentOptions.PolicyAsync(db, actor.TenantId, company, ct);
            var policyChanged = offer.OfferPolicyVersion != policy.ExpectedVersion
                || (offer.CompensationSnapshotJson is not null && (offer.CompensationCurrency != policy.Currency || offer.CompensationPrecision != policy.Precision));
            if ((policyChanged && input.ReviewedPolicyVersion != policy.ExpectedVersion)
                || (input.ReviewedPolicyVersion is { } reviewed && reviewed != policy.ExpectedVersion))
                throw new OfferLifecycleException("offer_policy_changed", "The company offer policy changed. Review its current version and explicitly revalidate the draft.");
            if (input.StartDate == DateOnly.MinValue || input.ProbationMonths is < 0 or > 12
                || input.ResponseDeadline is not { Kind: DateTimeKind.Utc } deadline || deadline <= DateTime.UtcNow)
                throw new OfferLifecycleException("offer_terms_invalid", "Choose a start date, a future UTC response deadline, and probation of zero to twelve months.", 422);
            var email = input.RecipientEmail?.Trim() ?? string.Empty;
            if (!MailAddress.TryCreate(email, out var address) || address.Address != email || email.Length > 254)
                throw new OfferLifecycleException("offer_recipient_invalid", "Enter the candidate's complete email address.", 422);
            var oldEmail = offer.RecipientEmail ?? app.CandidateEmail;
            if (oldEmail != email) RequireReason(input.RecipientChangeReason ?? string.Empty);
            var placement = await OfferPlacement.ResolveAsync(db, actor.TenantId, input.DepartmentId,
                input.OfferedDepartment, input.DesignationId, input.OfferedJobTitle, ct, companyId: company);
            if (!placement.IsResolved)
                throw new OfferLifecycleException(placement.Error ?? "offer_placement_unresolved", placement.Message ?? "Resolve the offer placement.", 422);
            OfferCompensationSnapshot compensation;
            try
            {
                compensation = await OfferComponentOptions.CalculateAsync(db, actor.TenantId, company, input.StartDate,
                    input.Components, input.BasicSalary, input.HousingAllowance, input.TransportAllowance, input.OtherAllowances, ct);
            }
            catch (ArgumentException e) { throw new OfferLifecycleException("offer_compensation_invalid", e.Message, 422); }
            // The resolver re-reads policy. A concurrent change must be reviewed, even if its amounts happen to match.
            if (compensation.PolicyVersion != policy.ExpectedVersion)
                throw new OfferLifecycleException("offer_policy_changed", "The company offer policy changed while saving. Review and try again.");
            var before = new { offer.StartDate, offer.ResponseDeadline, offer.OfferedJobTitle, offer.OfferedDepartment,
                offer.ProbationMonths, offer.RecipientEmail, offer.CompensationSnapshotJson };
            if (offer.SeriesId is null)
            {
                var series = new OfferSeries { TenantId = actor.TenantId, CompanyId = company, ApplicationId = app.Id };
                db.OfferSeries.Add(series); offer.SeriesId = series.Id; offer.RevisionNumber = 1;
                if (await OfferRules.AuthorAsync(db, actor.TenantId, offer.Id, ct) is null)
                    db.RecruitmentAuditLogs.Add(OfferRules.AuditRow(actor.TenantId, offer.Id, OfferRules.CreatedAction,
                        actor.UserId, actor.Name, new { source = "ExplicitLegacyAdoption" }));
            }
            offer.CandidateId ??= app.CandidateId;
            offer.RecipientEmail = email; offer.StartDate = input.StartDate; offer.ResponseDeadline = deadline;
            offer.ProbationMonths = input.ProbationMonths; offer.OfferedJobTitle = placement.Designation;
            offer.OfferedDepartment = placement.Department;
            OfferCompensation.ApplySnapshot(offer, compensation);
            try
            {
                if (input.TemplateId is not null && input.DesignJson is not null)
                    throw new ArgumentException("Choose a template or a candidate-specific design, not both.");
                if (input.TemplateId is not null)
                    await OfferLetterDesign.ApplyTemplateAsync(db, offer, input.TemplateId, ct);
                else
                {
                    var existing = offer.DesignSnapshotJson is null ? null
                        : JsonSerializer.Deserialize<OfferDesignSnapshot>(offer.DesignSnapshotJson, OfferLetterDesign.Json);
                    var design = input.DesignJson is null ? existing?.Design ?? OfferLetterDesign.DynamicDefault()
                        : JsonSerializer.Deserialize<OfferDesign>(input.DesignJson, OfferLetterDesign.Json) ?? throw new ArgumentException("Invalid offer design.");
                    var snapshot = await OfferLetterDesign.SnapshotAsync(db, offer, design,
                        input.DesignJson is null ? existing?.TemplateId : null,
                        input.DesignJson is null ? existing?.TemplateVersion : null, ct);
                    offer.DesignSnapshotJson = JsonSerializer.Serialize(snapshot, OfferLetterDesign.Json);
                }
            }
            catch (Exception e) when (e is ArgumentException or JsonException)
            { throw new OfferLifecycleException("offer_design_invalid", e.Message, 422); }
            offer.Version++; app.Version++; app.OfferedSalary = compensation.MonthlyCash;
            AddEvent(actor, offer, "DraftSaved", input.AdoptLegacy ? "Explicitly reviewed and adopted the draft." : "Saved reviewed draft terms.",
                new { before, policyVersion = compensation.PolicyVersion });
            if (oldEmail != email)
                AddEvent(actor, offer, "RecipientChanged", input.RecipientChangeReason!, new { previousEmail = oldEmail, recipientEmail = email });
            return offer;
        }, ct);
}
