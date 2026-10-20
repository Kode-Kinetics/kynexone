using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

// Synthetic regression journeys explicitly adopt/review legacy four-amount drafts and use the
// real frozen-original/external-issue workflow. No transport, local document files or user DBs.
internal static class OfferWorkflowTestSupport
{
    private static readonly OfferMemoryStorage Storage = new();
    internal static OfferArtifactService Artifacts(ZayraDbContext db) => new(db, Storage, OfferLifecycleServiceTests.Config());
    internal static OfferLifecycleService Lifecycle(ZayraDbContext db) => new(db, OfferLifecycleServiceTests.Config(), Artifacts(db));
    internal static Task<long> VersionAsync(ZayraDbContext db, Guid id) => db.OfferLetters.AsNoTracking().Where(o => o.Id == id).Select(o => o.Version).SingleAsync();
    internal static async Task<OfferResponseRequest> ResponseAsync(ZayraDbContext db, Guid id) =>
        new(await VersionAsync(db, id), "Accepted", "StaffRecorded", DateTime.UtcNow, "Synthetic candidate confirmed", "test/case");

    /// <summary>
    /// Seeds <paramref name="offer"/> as a managed revision whose original is already frozen and finalized,
    /// for tests whose subject is a later rule (approval separation of duties), not freezing itself. The
    /// stored bytes match the artifact's hash and size, so the real integrity check still runs.
    /// Call before SaveChanges; the offer and application must not be saved yet.
    /// </summary>
    internal static void SeedFrozenManaged(ZayraDbContext db, OfferLetter offer, JobApplication application)
    {
        var company = new Company { TenantId = offer.TenantId, CountryCode = "SA", Jurisdiction = "KSA-mainland", DefaultCurrency = "SAR" };
        db.Companies.Add(company);
        offer.CompanyId = company.Id; application.CompanyId = company.Id;
        var series = new OfferSeries { TenantId = offer.TenantId, CompanyId = company.Id, ApplicationId = application.Id };
        db.OfferSeries.Add(series);
        offer.SeriesId = series.Id; offer.RevisionNumber = 1;
        var bytes = System.Text.Encoding.UTF8.GetBytes($"%PDF-1.4 synthetic frozen original {offer.Id:N}");
        var key = $"{offer.TenantId:N}/offers/{offer.Id:N}.pdf";
        Storage.Bytes[key] = bytes;
        var artifact = new OfferArtifact
        {
            TenantId = offer.TenantId, CompanyId = company.Id, OfferId = offer.Id, ApplicationId = application.Id,
            StorageKey = key, SizeBytes = bytes.LongLength, State = "Finalized", FinalizedAtUtc = DateTime.UtcNow,
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
        };
        db.OfferArtifacts.Add(artifact);
        offer.ArtifactId = artifact.Id; offer.FrozenAtUtc = DateTime.UtcNow;
    }

    internal static async Task PrepareAsync(ZayraDbContext db, Guid offerId, Guid author)
    {
        var offer = await db.OfferLetters.SingleAsync(o => o.Id == offerId);
        if (offer.FrozenAtUtc is not null) return;
        var app = await db.JobApplications.SingleAsync(a => a.Id == offer.ApplicationId);
        if (offer.CompanyId is null)
        {
            // Fixture prerequisite: historical tests predate legal-company attribution. Add a
            // synthetic company before explicit adoption; never pretend a null company is managed.
            var company = new Company { TenantId = offer.TenantId, CountryCode = "SA", Jurisdiction = "KSA-mainland", DefaultCurrency = "SAR" };
            db.Companies.Add(company); offer.CompanyId = company.Id; app.CompanyId = company.Id;
        }
        var candidate = await db.Candidates.SingleAsync(c => c.Id == app.CandidateId);
        candidate.CompanyId = offer.CompanyId;
        await db.SaveChangesAsync();
        var actor = new OfferActor(offer.TenantId, offer.CompanyId, author, "Test HR");
        var policy = await OfferComponentOptions.PolicyAsync(db, offer.TenantId, offer.CompanyId!.Value, default);
        var saved = await Lifecycle(db).SaveDraftAsync(actor, offerId, new(offer.Version, policy.ExpectedVersion, offer.StartDate,
            offer.ResponseDeadline ?? DateTime.UtcNow.AddDays(7), offer.ProbationMonths, candidate.Email,
            offer.OfferedJobTitle, offer.OfferedDepartment, BasicSalary: offer.BasicSalary, HousingAllowance: offer.HousingAllowance,
            TransportAllowance: offer.TransportAllowance, OtherAllowances: offer.OtherAllowances, AdoptLegacy: true));
        await Artifacts(db).FreezeAsync(actor, offerId, saved.Version);
    }

    internal static async Task<IActionResult> IssueAsync(ZayraDbContext db, Guid offerId, Guid sender)
    {
        await PrepareAsync(db, offerId, sender);
        var offer = await db.OfferLetters.AsNoTracking().SingleAsync(o => o.Id == offerId);
        try
        {
            return new OkObjectResult(await Lifecycle(db).RecordExternalSendAsync(new(offer.TenantId, offer.CompanyId, sender, "Test sender"), offerId,
                new(offer.Version, Guid.NewGuid().ToString(), "Email", DateTime.UtcNow, "test/outbox", "Confirmed synthetic external sending")));
        }
        catch (OfferLifecycleException e) { return new ConflictObjectResult(new { error = e.Code, message = e.Message }); }
    }

    internal static async Task ConfirmCompensationAsync(ZayraDbContext db, Guid draftId, Guid maker)
    {
        var draft = await db.EmployeeDrafts.AsNoTracking().SingleAsync(d => d.Id == draftId);
        if (!OfferCompensationHandoff.HasSnapshot(draft)) return;
        var snapshot = OfferCompensationHandoff.Snapshot(draft)!;
        // These regression fixtures use the four explicitly adopted legacy monthly earnings.
        // Unknown mappings must be reviewed in the fixture, never assigned a catch-all field.
        var rows = snapshot.Components.Select(row => !row.Included
            ? new OfferCompensationDisposition(row.Code, "Excluded")
            : row.StructureField is "BasicSalary" or "HousingAllowance" or "TransportAllowance" or "OtherAllowance"
                ? new OfferCompensationDisposition(row.Code, "Payroll", row.StructureField)
                : throw new InvalidOperationException($"Fixture requires explicit disposition for {row.Code}.")).ToArray();
        var result = await EmployeeDraftLifecyclePostgresTests.Employees(db, draft.TenantId, maker)
            .PutDraftOfferCompensation(draftId, new(draft.OfferCompensationVersion, rows, true), default);
        if (result.Result is not OkObjectResult)
            throw new InvalidOperationException("Fixture compensation confirmation failed: " + System.Text.Json.JsonSerializer.Serialize((result.Result as ObjectResult)?.Value));
    }
}
