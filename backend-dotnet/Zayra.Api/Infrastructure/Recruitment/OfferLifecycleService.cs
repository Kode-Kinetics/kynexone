using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Recruitment;

public sealed partial class OfferLifecycleService(ZayraDbContext db, IConfiguration configuration, OfferArtifactService? artifacts = null)
{
    public const string EnablementKey = "Recruitment:ManagedOfferLifecycleEnabled";
    public bool Enabled => configuration.GetValue<bool>(EnablementKey);

    public void RequireEnabled()
    {
        if (!Enabled) throw new OfferLifecycleException("managed_offer_lifecycle_disabled",
            "Managed offer creation and issuance are not enabled for this deployment.");
    }

    internal IQueryable<OfferLetter> ScopedOffers(OfferActor actor) => db.OfferLetters
        .Where(o => o.TenantId == actor.TenantId && o.CompanyId == actor.CompanyId);

    internal async Task<OfferLetter> ReadAsync(OfferActor actor, Guid id, CancellationToken ct) =>
        await ScopedOffers(actor).AsNoTracking().SingleOrDefaultAsync(o => o.Id == id, ct)
        ?? throw new OfferLifecycleException("offer_not_found", "Offer not found.", 404);

    internal async Task<T> MutateAsync<T>(OfferActor actor, Guid offerId, long? expectedVersion,
        bool managed, Func<JobApplication, OfferLetter, Task<T>> mutation, CancellationToken ct)
    {
        var initial = await ReadAsync(actor, offerId, ct);
        if (managed || initial.SeriesId is not null) RequireEnabled();
        return await OfferApplicationBoundary.ExecuteAsync(db, actor, initial.ApplicationId, async locked =>
        {
            var app = db.JobApplications.Local.FirstOrDefault(a => a.Id == locked.Id);
            if (app is not null) db.Entry(app).State = EntityState.Detached;
            db.JobApplications.Attach(locked);
            var current = await ReadAsync(actor, offerId, ct);
            if (expectedVersion is { } expected) OfferApplicationBoundary.RequireVersion(current, expected);
            var tracked = db.OfferLetters.Local.FirstOrDefault(o => o.Id == current.Id);
            if (tracked is not null) db.Entry(tracked).State = EntityState.Detached;
            db.OfferLetters.Attach(current);
            return await mutation(locked, current);
        }, ct);
    }

    internal void AddEvent(OfferActor actor, OfferLetter offer, string type, string reason,
        object? data = null, string? idempotencyKey = null, DateTime? occurredAt = null)
    {
        if (offer.CompanyId is null)
            throw new OfferLifecycleException("offer_company_required", "Select the offer's legal company before using the managed lifecycle.");
        db.OfferLifecycleEvents.Add(new()
        {
            TenantId = actor.TenantId, CompanyId = offer.CompanyId, OfferId = offer.Id,
            ApplicationId = offer.ApplicationId, EventType = type, ActorUserId = actor.UserId, ActorName = actor.Name,
            Reason = reason, DataJson = JsonSerializer.Serialize(data ?? new { }, OfferCompensation.Json),
            IdempotencyKey = idempotencyKey, OccurredAtUtc = occurredAt ?? DateTime.UtcNow
        });
    }

    internal static void RequireReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 2000)
            throw new OfferLifecycleException("offer_reason_required", "Enter a reason of no more than 2,000 characters.", 422);
    }

    public Task<OfferLetter> ReviseAsync(OfferActor actor, Guid offerId, OfferReasonRequest request, CancellationToken ct = default) =>
        MutateAsync(actor, offerId, request.ExpectedVersion, true, async (app, source) =>
        {
            OfferApplicationBoundary.RequireUnreserved(app); OfferApplicationBoundary.RequireActive(app);
            RequireReason(request.Reason);
            if (actor.CompanyId is null) throw new OfferLifecycleException("offer_company_required", "Managed offers require a legal company.", 422);
            var series = source.SeriesId is { } sid
                ? await db.OfferSeries.SingleAsync(s => s.Id == sid && s.TenantId == actor.TenantId && s.CompanyId == actor.CompanyId, ct)
                : await db.OfferSeries.SingleOrDefaultAsync(s => s.LegacyRootOfferId == source.Id && s.TenantId == actor.TenantId && s.CompanyId == actor.CompanyId, ct);
            int number;
            if (series is null)
            {
                series = new() { TenantId = actor.TenantId, CompanyId = actor.CompanyId,
                    ApplicationId = app.Id, LegacyRootOfferId = source.Id };
                db.OfferSeries.Add(series); number = 1;
            }
            else { number = series.NextRevisionNumber++; series.Version++; }
            var revision = new OfferLetter
            {
                TenantId = actor.TenantId, CompanyId = actor.CompanyId, ApplicationId = source.ApplicationId,
                SeriesId = series.Id, RevisionNumber = number, PreviousRevisionId = source.Id,
                CandidateName = source.CandidateName, CandidateId = source.CandidateId ?? app.CandidateId,
                RecipientEmail = source.RecipientEmail ?? app.CandidateEmail,
                OfferedJobTitle = source.OfferedJobTitle, OfferedDepartment = source.OfferedDepartment,
                StartDate = source.StartDate, ProbationMonths = source.ProbationMonths,
                ResponseDeadline = source.ResponseDeadline, BasicSalary = source.BasicSalary,
                HousingAllowance = source.HousingAllowance, TransportAllowance = source.TransportAllowance,
                OtherAllowances = source.OtherAllowances, GrossSalary = source.GrossSalary,
                AnnualRecurringCash = source.AnnualRecurringCash, OneTimeCash = source.OneTimeCash,
                CompensationSnapshotJson = source.CompensationSnapshotJson,
                CompensationCurrency = source.CompensationCurrency, CompensationPrecision = source.CompensationPrecision,
                OfferPolicyVersion = source.OfferPolicyVersion, ContentHtml = source.ContentHtml,
                DesignSnapshotJson = source.DesignSnapshotJson
            };
            source.Version++; app.Version++;
            db.OfferLetters.Add(revision);
            db.RecruitmentAuditLogs.Add(OfferRules.AuditRow(actor.TenantId, revision.Id, OfferRules.CreatedAction,
                actor.UserId, actor.Name, new { previousRevisionId = source.Id, request.Reason }));
            AddEvent(actor, revision, "RevisionCreated", request.Reason, new { previousRevisionId = source.Id });
            return revision;
        }, ct);

    public Task<OfferLetter> WithdrawAsync(OfferActor actor, Guid offerId, OfferReasonRequest request, CancellationToken ct = default) =>
        MutateAsync(actor, offerId, request.ExpectedVersion, true, (app, offer) =>
        {
            OfferApplicationBoundary.RequireUnreserved(app); OfferApplicationBoundary.RequireActive(app);
            RequireReason(request.Reason);
            if (offer.Status is "Accepted" or "Withdrawn" or "Declined" || offer.SupersededAtUtc is not null)
                throw new OfferLifecycleException("invalid_offer_state", "This offer cannot be withdrawn in its current state.");
            offer.Status = "Withdrawn"; offer.Version++; app.Version++;
            if (app.CurrentIssuedOfferId == offer.Id) app.CurrentIssuedOfferId = null;
            AddEvent(actor, offer, "Withdrawn", request.Reason);
            return Task.FromResult(offer);
        }, ct);
}
