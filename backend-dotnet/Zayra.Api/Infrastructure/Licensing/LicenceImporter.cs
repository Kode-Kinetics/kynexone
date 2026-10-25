using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Licensing;

public enum LicenceImportOutcome
{
    /// <summary>The licence was installed and is now the active one.</summary>
    Imported,
    /// <summary>This exact licence is already the active one; nothing changed.</summary>
    AlreadyActive,
    Rejected,
}

public sealed record LicenceImportResult(LicenceImportOutcome Outcome, IReadOnlyList<string> Problems, LicencePayload? Payload)
{
    public bool Succeeded => Outcome is LicenceImportOutcome.Imported or LicenceImportOutcome.AlreadyActive;
}

/// <summary>
/// Installs a vendor-signed licence: verifies it, supersedes the previous one in the same transaction, applies the
/// account type, audits the attempt (accepted or rejected) with the real actor, and refreshes
/// <see cref="ILicenceService"/>. Idempotent by licence id + fingerprint. Used by the API, the CLI and the boot import.
/// </summary>
public sealed class LicenceImporter
{
    private readonly ZayraDbContext _db;
    private readonly LicenceVerifier _verifier;
    private readonly ILicenceService _licence;
    private readonly Microsoft.Extensions.Caching.Memory.IMemoryCache? _cache;

    public LicenceImporter(ZayraDbContext db, LicenceVerifier verifier, ILicenceService licence,
        Microsoft.Extensions.Caching.Memory.IMemoryCache? cache = null)
    {
        _cache = cache;
        _db = db;
        _verifier = verifier;
        _licence = licence;
    }

    public async Task<LicenceImportResult> ImportAsync(string fileJson, Guid? actorUserId, string actorName, CancellationToken ct)
    {
        var result = await ImportCoreAsync(fileJson, actorUserId, ct);
        await AuditAsync(result, actorName, ct);
        if (result.Outcome == LicenceImportOutcome.Imported)
        {
            await _licence.RefreshAsync(ct);
            // The module state is cached per organisation; a new licence changes what may be on, now.
            if (_cache is not null && await _db.Tenants.AsNoTracking().Select(t => (Guid?)t.Id).FirstOrDefaultAsync(ct) is { } org)
                Zayra.Api.Infrastructure.Modules.TenantModuleService.Invalidate(_cache, org);
        }
        return result;
    }

    private async Task<LicenceImportResult> ImportCoreAsync(string fileJson, Guid? actorUserId, CancellationToken ct)
    {
        var file = LicenceFile.Parse(fileJson);
        if (file is null) return Rejected("The file is not a KynexOne licence (expected payload, signature and kid).");

        var installationId = await InstallationIdentity.EnsureAsync(_db, ct);
        var verification = _verifier.Verify(file, installationId, LicenceService.KnownModules, LicenceService.KnownOptIns, DateTime.UtcNow);
        if (!verification.IsValid) return new LicenceImportResult(LicenceImportOutcome.Rejected, verification.Problems, null);
        var licence = verification.Licence!;

        var existing = await _db.InstallationLicences.FirstOrDefaultAsync(l => l.LicenceId == licence.Payload.LicenceId, ct);
        if (existing is not null)
        {
            if (existing.Fingerprint != licence.Fingerprint)
                return Rejected($"Licence id '{licence.Payload.LicenceId}' was already imported with different content.");
            if (existing.Status == InstallationLicenceStatuses.Active)
                return new LicenceImportResult(LicenceImportOutcome.AlreadyActive, [], licence.Payload);
            return Rejected($"Licence '{licence.Payload.LicenceId}' was superseded by a later licence and cannot be reinstated.");
        }

        // Same rule as the platform's account-type change used to be: a group with several active companies cannot be
        // turned into a single-company organisation underneath them.
        var organisation = await _db.Tenants.FirstOrDefaultAsync(ct);
        if (organisation is not null && licence.Payload.AccountType == TenantAccountTypes.SingleCompany)
        {
            var activeCompanies = await Zayra.Api.Infrastructure.Data.ScopedBypass.TenantWide(_db.Companies, organisation.Id,
                    "The account-type rule counts every active company of the organisation, not the caller's view.")
                .CountAsync(c => c.IsActive && !c.IsDeleted, ct);
            if (activeCompanies > 1)
                return Rejected($"The licence is for a single company, but this organisation has {activeCompanies} active companies. "
                                + "Deactivate the extra companies or obtain a Group licence.");
        }

        if (_db.Database.IsRelational())
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                _db.ChangeTracker.Clear();
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                await InstallAsync(licence, actorUserId, ct);
                await tx.CommitAsync(ct);
            });
        }
        else
        {
            await InstallAsync(licence, actorUserId, ct);
        }

        return new LicenceImportResult(LicenceImportOutcome.Imported, [], licence.Payload);
    }

    private async Task InstallAsync(VerifiedLicence licence, Guid? actorUserId, CancellationToken ct)
    {
        var active = await _db.InstallationLicences.Where(l => l.Status == InstallationLicenceStatuses.Active).ToListAsync(ct);
        foreach (var row in active) row.Status = InstallationLicenceStatuses.Superseded;
        // Free the one-Active slot before the new row claims it (the partial unique index is checked per statement).
        if (active.Count > 0) await _db.SaveChangesAsync(ct);

        _db.InstallationLicences.Add(new InstallationLicence
        {
            LicenceId = licence.Payload.LicenceId,
            Kid = licence.Kid,
            Payload = licence.PayloadBase64Url,
            Signature = licence.SignatureBase64Url,
            Fingerprint = licence.Fingerprint,
            Licensee = licence.Payload.Licensee,
            ExpiresAtUtc = licence.Payload.ExpiresAtUtc,
            Status = InstallationLicenceStatuses.Active,
            ImportedAtUtc = DateTime.UtcNow,
            ImportedByUserId = actorUserId,
        });

        // The organisation's account type comes from the licence; creation is always self-service within its limits.
        var organisation = await _db.Tenants.FirstOrDefaultAsync(ct);
        if (organisation is not null)
        {
            organisation.AccountType = licence.Payload.AccountType;
            organisation.CompanyCreationMode = CompanyCreationModes.GroupSelfServiceWithinLimit;
        }
        await _db.SaveChangesAsync(ct);
    }

    private async Task AuditAsync(LicenceImportResult result, string actorName, CancellationToken ct)
    {
        // Audit rows belong to the organisation; before organisation setup there is nothing to attach them to.
        var organisationId = await _db.Tenants.AsNoTracking().Select(t => (Guid?)t.Id).FirstOrDefaultAsync(ct);
        if (organisationId is null) return;
        _db.ChangeTracker.Clear();
        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId = organisationId.Value,
            EntityType = "Licence",
            EntityId = result.Payload?.LicenceId ?? "unknown",
            Action = result.Outcome switch
            {
                LicenceImportOutcome.Imported => "LicenceImported",
                LicenceImportOutcome.AlreadyActive => "LicenceImportUnchanged",
                _ => "LicenceImportRejected",
            },
            NewValuesJson = JsonSerializer.Serialize(new
            {
                outcome = result.Outcome.ToString(),
                problems = result.Problems,
                licensee = result.Payload?.Licensee,
                edition = result.Payload?.Edition,
                expiresAtUtc = result.Payload?.ExpiresAtUtc,
            }),
            PerformedByName = actorName,
            IpAddress = "",
        });
        await _db.SaveChangesAsync(ct);
    }

    private static LicenceImportResult Rejected(string problem) => new(LicenceImportOutcome.Rejected, [problem], null);
}
