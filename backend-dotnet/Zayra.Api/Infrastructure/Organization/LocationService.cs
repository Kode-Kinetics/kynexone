using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common.Import;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Organization;

/// <summary>What a caller may set on a location. A null text or number means "not sent".</summary>
public sealed record LocationInput(
    Guid? BranchId, string? Code, string? NameEn, string? NameAr, string? AddressLine1, string? City, string? CountryCode,
    string? PostalCode, decimal? Latitude, decimal? Longitude, decimal? GeofenceRadiusMeters, bool? IsActive = null, string? Reason = null);

/// <summary>The location as the API sends it: nothing internal (tenant, soft-delete flag, user ids).</summary>
public sealed record LocationDto(
    Guid Id, Guid? BranchId, string Code, string NameEn, string NameAr, string AddressLine1, string AddressLine2, string City,
    string CountryCode, string PostalCode, decimal? Latitude, decimal? Longitude, decimal? GeofenceRadiusMeters, bool IsActive,
    DateTime CreatedAtUtc, DateTime? UpdatedAtUtc)
{
    public static LocationDto From(Location l) => new(
        l.Id, l.BranchId, l.Code, l.NameEn, l.NameAr, l.AddressLine1, l.AddressLine2, l.City, l.CountryCode, l.PostalCode,
        l.Latitude, l.Longitude, l.GeofenceRadiusMeters, l.IsActive, l.CreatedAtUtc, l.UpdatedAtUtc);
}

/// <summary>A refusal with the HTTP status it is answered with and a stable code; the body always carries a readable message.</summary>
public sealed class LocationRuleException(int status, string code, string message) : Exception(message)
{
    public const string Invalid = "invalid_location";
    public const string CodeTaken = "code_taken";
    public const string Forbidden = "forbidden";

    public int Status { get; } = status;
    public string Code { get; } = code;
    public object ToPayload() => new { error = Code, message = Message };
}

public sealed record LocationImportOutcome(int Received, int Created, int Updated, int Skipped, IReadOnlyList<ImportRowResult> Rows);

/// <summary>
/// The ONE write rule for locations, used by the Setup screen (/api/admin/locations), the location form and the CSV import
/// (/api/locations): the row's company scope, the code lock, a clean 409 on a taken code, validated input and an audit row
/// with before and after. A location has no company of its own; it belongs to its branch's company, or to the whole group
/// when it has no branch. <c>mayWrite</c> is the caller's grant check for that company (null = group).
/// </summary>
public sealed class LocationService(ZayraDbContext db, IAuditService audit)
{
    public const int MaxCodeLength = 40;
    private const int MaxNameLength = 180;
    private const int MaxAddressLength = 240;
    private const int MaxCityLength = 120;
    private const int MaxCountryLength = 10;
    private const int MaxPostalLength = 20;
    private const decimal MaxGeofenceMeters = 100_000m;

    private static readonly Regex CodeFormat = new(@"^[\p{L}\p{N}][\p{L}\p{N} ._/\-]*$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed record Snapshot(Guid Id, Guid? BranchId, string Code, string NameEn, string NameAr, string AddressLine1, string City,
        string CountryCode, string PostalCode, decimal? Latitude, decimal? Longitude, decimal? GeofenceRadiusMeters, bool IsActive);

    private static Snapshot Snap(Location l) => new(l.Id, l.BranchId, l.Code, l.NameEn, l.NameAr, l.AddressLine1, l.City,
        l.CountryCode, l.PostalCode, l.Latitude, l.Longitude, l.GeofenceRadiusMeters, l.IsActive);

    private static string AuditJson(Snapshot? before, Location after, string? reason, string? via = null) =>
        JsonSerializer.Serialize(new { before, after = Snap(after), reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(), via }, Web);

    // ── validation ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The first problem with the values that were sent, or null. <paramref name="checkCode"/> is false for an unchanged code.</summary>
    private static string? Problem(LocationInput i, bool creating, bool checkCode)
    {
        if (creating && string.IsNullOrWhiteSpace(i.Code)) return "Code is required.";
        if (creating && string.IsNullOrWhiteSpace(i.NameEn)) return "Name (EN) is required.";
        if (i.NameEn is not null && string.IsNullOrWhiteSpace(i.NameEn)) return "Name (EN) cannot be blank.";
        if (checkCode && i.Code is not null)
        {
            var code = OrgCodes.Normalize(i.Code);
            if (code.Length == 0) return "Code is required.";
            if (code.Length > MaxCodeLength) return $"Code can be at most {MaxCodeLength} characters.";
            if (!CodeFormat.IsMatch(code)) return "Code can use letters, digits, spaces and . _ / - only, and must start with a letter or digit.";
        }
        if (Over(i.NameEn, MaxNameLength)) return $"Name (EN) can be at most {MaxNameLength} characters.";
        if (Over(i.NameAr, MaxNameLength)) return $"Name (AR) can be at most {MaxNameLength} characters.";
        if (Over(i.AddressLine1, MaxAddressLength)) return $"Address can be at most {MaxAddressLength} characters.";
        if (Over(i.City, MaxCityLength)) return $"City can be at most {MaxCityLength} characters.";
        if (Over(i.CountryCode, MaxCountryLength)) return $"Country can be at most {MaxCountryLength} characters.";
        if (Over(i.PostalCode, MaxPostalLength)) return $"Postal code can be at most {MaxPostalLength} characters.";
        if (i.Latitude is < -90m or > 90m) return "Latitude must be between -90 and 90.";
        if (i.Longitude is < -180m or > 180m) return "Longitude must be between -180 and 180.";
        if (i.GeofenceRadiusMeters is < 0m or > MaxGeofenceMeters) return $"Geofence radius must be between 0 and {MaxGeofenceMeters:0} metres.";
        return null;
    }

    private static bool Over(string? value, int max) => value is not null && value.Trim().Length > max;

    private static void Validate(LocationInput i, bool creating, bool checkCode)
    {
        if (Problem(i, creating, checkCode) is { } problem) throw new LocationRuleException(400, LocationRuleException.Invalid, problem);
    }

    // ── company of a location ───────────────────────────────────────────────────────────────────────────

    private async Task<Dictionary<Guid, Guid>> BranchCompaniesAsync(Guid tenantId, CancellationToken ct) =>
        await db.Branches.AsNoTracking().Where(b => b.TenantId == tenantId && !b.IsDeleted).ToDictionaryAsync(b => b.Id, b => b.CompanyId, ct);

    /// <summary>The company a location belongs to: its branch's, or null (the whole group) when it has no live branch.</summary>
    private static Guid? CompanyOf(Location l, IReadOnlyDictionary<Guid, Guid> branches) =>
        l.BranchId is { } b && branches.TryGetValue(b, out var company) ? company : null;

    private static Guid? CompanyForNewBranch(Guid? branchId, IReadOnlyDictionary<Guid, Guid> branches)
    {
        if (branchId is null) return null;
        if (!branches.TryGetValue(branchId.Value, out var company))
            throw new LocationRuleException(400, LocationRuleException.Invalid, "That branch does not exist.");
        return company;
    }

    private static LocationRuleException OutOfScope() =>
        new(403, LocationRuleException.Forbidden, "You can't manage locations of that company or of the whole group.");

    // ── code uniqueness ─────────────────────────────────────────────────────────────────────────────────

    private static LocationRuleException Taken(string code) =>
        new(409, LocationRuleException.CodeTaken, $"A location with code '{code}' already exists. Choose a different code.");

    /// <summary>Compared case-insensitively, as every other organisation code is, among the tenant's live locations.</summary>
    private async Task EnsureCodeFreeAsync(Guid tenantId, string code, Guid? excludedId, CancellationToken ct)
    {
        var taken = await db.Locations.AsNoTracking().AnyAsync(
            l => l.TenantId == tenantId && !l.IsDeleted && l.Code.ToUpper() == code && l.Id != excludedId, ct);
        if (taken) throw Taken(code);
    }

    /// <summary>The unique index on a tenant's live location codes. Only a violation of this one means the code was taken; any other unique violation is rethrown as itself.</summary>
    private const string CodeIndexName = "IX_locations_tenant_id_code";

    /// <summary>Saves; a lost race on the code index is the same 409 the pre-check gives.</summary>
    private async Task SaveAsync(string? code, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation, ConstraintName: CodeIndexName })
        {
            throw code is null
                ? new LocationRuleException(409, LocationRuleException.CodeTaken, "A location code in this batch was just taken by someone else. Reload and try again.")
                : Taken(code);
        }
    }

    // ── create ──────────────────────────────────────────────────────────────────────────────────────────

    public async Task<LocationDto> CreateAsync(Guid tenantId, LocationInput input, RequestContext context, Func<Guid?, bool> mayWrite, CancellationToken ct)
    {
        Validate(input, creating: true, checkCode: true);
        var company = CompanyForNewBranch(input.BranchId, await BranchCompaniesAsync(tenantId, ct));
        if (!mayWrite(company)) throw OutOfScope();
        var code = OrgCodes.Normalize(input.Code);
        await EnsureCodeFreeAsync(tenantId, code, null, ct);

        var loc = new Location
        {
            TenantId = tenantId, BranchId = input.BranchId, Code = code, NameEn = input.NameEn!.Trim(),
            NameAr = input.NameAr?.Trim() ?? string.Empty, AddressLine1 = input.AddressLine1?.Trim() ?? string.Empty,
            City = input.City?.Trim() ?? string.Empty, CountryCode = input.CountryCode?.Trim() ?? string.Empty,
            PostalCode = input.PostalCode?.Trim() ?? string.Empty, Latitude = input.Latitude, Longitude = input.Longitude,
            GeofenceRadiusMeters = input.GeofenceRadiusMeters, IsActive = input.IsActive ?? true, CreatedBy = context.UserId,
        };
        db.Locations.Add(loc);
        audit.Stage("organization.location_created", nameof(Location), loc.Id.ToString(), context with { TenantId = tenantId },
            AuditJson(null, loc, input.Reason), company);
        await SaveAsync(code, ct);
        return LocationDto.From(loc);
    }

    // ── update ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Edits a live location. Null when it does not exist or its company is outside the caller's grant (it reads as not found).
    /// <paramref name="replaceOptional"/> is the Setup screen's whole-form save: a text left out becomes empty and a coordinate or
    /// radius left out is cleared; otherwise a value that was not sent is kept. On or off changes through Remove / Restore only.
    /// </summary>
    public async Task<LocationDto?> UpdateAsync(Guid tenantId, Guid id, LocationInput input, bool replaceOptional, RequestContext context,
        Func<Guid?, bool> mayWrite, CancellationToken ct)
    {
        var loc = await db.Locations.FirstOrDefaultAsync(l => l.TenantId == tenantId && l.Id == id && !l.IsDeleted, ct);
        if (loc is null) return null;
        var branches = await BranchCompaniesAsync(tenantId, ct);
        var current = CompanyOf(loc, branches);
        if (!mayWrite(current)) return null;

        var before = Snap(loc);
        await ApplyAsync(loc, tenantId, input, replaceOptional, applyIsActive: false, branches, context, mayWrite, ct);
        if (Snap(loc) != before || !string.IsNullOrWhiteSpace(input.Reason))
        {
            loc.UpdatedAtUtc = DateTime.UtcNow;
            loc.UpdatedBy = context.UserId;
            audit.Stage("organization.location_updated", nameof(Location), loc.Id.ToString(), context with { TenantId = tenantId },
                AuditJson(before, loc, input.Reason), CompanyOf(loc, branches));
        }
        await SaveAsync(loc.Code, ct);
        return LocationDto.From(loc);
    }

    private async Task ApplyAsync(Location loc, Guid tenantId, LocationInput i, bool replaceOptional, bool applyIsActive,
        IReadOnlyDictionary<Guid, Guid> branches, RequestContext context, Func<Guid?, bool> mayWrite, CancellationToken ct)
    {
        var newCode = i.Code is null ? null : OrgCodes.Normalize(i.Code);
        var codeChanges = newCode is not null && !string.Equals(newCode, OrgCodes.Normalize(loc.Code), StringComparison.Ordinal);
        Validate(i, creating: false, checkCode: codeChanges);

        if (i.BranchId is { } branch && branch != loc.BranchId && !mayWrite(CompanyForNewBranch(branch, branches))) throw OutOfScope();

        if (codeChanges)
        {
            var usage = await new OrgRowRemoval(db, audit).UsageAsync(OrgRowKind.Location, tenantId, loc.Id, ct);
            if (usage.InUse)
            {
                await new OrgRowRemoval(db, audit).RecordRefusalAsync(OrgRowKind.Location, tenantId, loc.Id, "code_change", usage.Summary, context, ct);
                throw new LocationRuleException(400, OrganizationRuleException.CodeLocked,
                    $"The code can't be changed because this is in use. {usage.Summary}. Change the name instead.");
            }
            await EnsureCodeFreeAsync(tenantId, newCode!, loc.Id, ct);
            loc.Code = newCode!;
        }

        if (i.BranchId is { } b) loc.BranchId = b;
        if (i.NameEn is not null) loc.NameEn = i.NameEn.Trim();
        loc.NameAr = Text(i.NameAr, loc.NameAr, replaceOptional);
        loc.AddressLine1 = Text(i.AddressLine1, loc.AddressLine1, replaceOptional);
        loc.City = Text(i.City, loc.City, replaceOptional);
        loc.CountryCode = Text(i.CountryCode, loc.CountryCode, replaceOptional);
        loc.PostalCode = Text(i.PostalCode, loc.PostalCode, replaceOptional);
        if (replaceOptional || i.Latitude is not null) loc.Latitude = i.Latitude;
        if (replaceOptional || i.Longitude is not null) loc.Longitude = i.Longitude;
        if (replaceOptional || i.GeofenceRadiusMeters is not null) loc.GeofenceRadiusMeters = i.GeofenceRadiusMeters;
        if (applyIsActive && i.IsActive is { } active) loc.IsActive = active;
    }

    private static string Text(string? sent, string current, bool replace) => sent is not null ? sent.Trim() : replace ? string.Empty : current;

    private static bool TryParseFlag(string cell, out bool value)
    {
        switch (cell.Trim().ToLowerInvariant())
        {
            case "true": case "1": case "yes": case "active": value = true; return true;
            case "false": case "0": case "no": case "inactive": value = false; return true;
            default: value = false; return false;
        }
    }

    // ── CSV import (preview and commit walk the same loop) ──────────────────────────────────────────────

    public async Task<LocationImportOutcome> ImportAsync(Guid tenantId, IReadOnlyList<Dictionary<string, string>> rows, bool commit,
        RequestContext context, Func<Guid?, bool> mayWrite, CancellationToken ct)
    {
        var live = await db.Locations.Where(l => l.TenantId == tenantId && !l.IsDeleted).ToListAsync(ct);
        if (!OrgCodes.TryBuildLookup(live, l => l.Code, out var byCode, out var clash))
            throw new LocationRuleException(409, "code_case_collision",
                $"This tenant holds more than one location whose code differs only in letter case ({string.Join(", ", clash.Select(c => $"'{c}'"))}). Rename or remove all but one of them, then run the import again.");
        var branches = await BranchCompaniesAsync(tenantId, ct);
        var results = new List<ImportRowResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int created = 0, updated = 0, skipped = 0;

        for (var n = 0; n < rows.Count; n++)
        {
            var row = rows[n];
            string Get(string key) => row.TryGetValue(key, out var v) ? v.Trim() : string.Empty;
            string? Sent(string key) => row.TryGetValue(key, out var v) ? v.Trim() : null;
            var rawCode = Get("Code");
            var code = OrgCodes.Normalize(rawCode);
            var nameEn = Get("NameEn");
            var errors = new List<string>();

            if (code.Length == 0) errors.Add("Code is required");
            if (nameEn.Length == 0) errors.Add("NameEn is required");
            if (code.Length > 0 && !seen.Add(code)) errors.Add($"Duplicate Code '{rawCode}' within this batch");
            var existing = code.Length > 0 && byCode.TryGetValue(code, out var found) ? found : null;
            // A blank cell is "unchanged" (a new row starts on); an unknown word is a row error, never a silent "on".
            bool? isActive = null;
            if (Sent("IsActive") is { Length: > 0 } activeCell)
            {
                if (TryParseFlag(activeCell, out var flag)) isActive = flag;
                else errors.Add($"IsActive '{activeCell}' is not recognised. Use true or false (also 1/0, yes/no, active/inactive).");
            }
            var input = new LocationInput(null, existing is null ? rawCode : null, nameEn.Length == 0 ? null : nameEn, Sent("NameAr"), null, Sent("City"),
                Sent("CountryCode"), null, null, null, null, isActive);
            if (errors.Count == 0 && Problem(input, creating: existing is null, checkCode: existing is null) is { } problem) errors.Add(problem);
            // The same words for a code that is outside the grant and one that is not there to add, so the import does not confirm which exist.
            if (errors.Count == 0 && !mayWrite(existing is null ? null : CompanyOf(existing, branches)))
                errors.Add($"Location '{rawCode}' is not one you can manage.");

            if (errors.Count == 0)
            {
                if (existing is null)
                {
                    created++;
                    if (commit)
                    {
                        var loc = new Location
                        {
                            TenantId = tenantId, Code = code, NameEn = nameEn, NameAr = input.NameAr ?? string.Empty, City = input.City ?? string.Empty,
                            CountryCode = input.CountryCode ?? string.Empty, IsActive = input.IsActive ?? true, CreatedBy = context.UserId,
                        };
                        db.Locations.Add(loc);
                        audit.Stage("organization.location_created", nameof(Location), loc.Id.ToString(), context with { TenantId = tenantId },
                            AuditJson(null, loc, null, "import"), null);
                    }
                }
                else
                {
                    updated++;
                    if (commit)
                    {
                        var before = Snap(existing);
                        await ApplyAsync(existing, tenantId, input, replaceOptional: false, applyIsActive: true, branches, context, mayWrite, ct);
                        if (Snap(existing) != before)
                        {
                            existing.UpdatedAtUtc = DateTime.UtcNow;
                            existing.UpdatedBy = context.UserId;
                            audit.Stage("organization.location_updated", nameof(Location), existing.Id.ToString(), context with { TenantId = tenantId },
                                AuditJson(before, existing, null, "import"), CompanyOf(existing, branches));
                        }
                    }
                }
            }
            else skipped++;
            results.Add(new ImportRowResult(n + 2, rawCode, nameEn, errors.Count > 0 ? ImportRowStatus.Error : ImportRowStatus.Ok, errors, Array.Empty<string>()));
        }

        if (commit) await SaveAsync(null, ct);
        return new LocationImportOutcome(rows.Count, created, updated, skipped, results);
    }
}
