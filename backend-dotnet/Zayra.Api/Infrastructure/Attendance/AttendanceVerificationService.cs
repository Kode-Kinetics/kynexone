using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Attendance;

/// <summary>A plain-language refusal for a punch, an upload or a consent call. <see cref="Code"/> is stable for clients.</summary>
public sealed record AttendanceRefusal(string Code, string Message, string MessageAr)
{
    public object Body => new { code = Code, message = Message, messageAr = MessageAr };
}

/// <summary>Raised inside the punch write when evidence fails its re-check under the transaction (a race, or expiry).</summary>
public sealed class AttendanceRefusalException(AttendanceRefusal refusal) : InvalidOperationException(refusal.Message)
{
    public AttendanceRefusal Refusal { get; } = refusal;
}

/// <summary>The refusal catalogue. Every message says what happened and what to do, in English and Arabic.</summary>
public static class AttendanceRefusals
{
    public static readonly AttendanceRefusal SelfieNotEnabled = new("selfie_not_enabled",
        "Selfie attendance is not switched on for your company. Punch without a selfie.",
        "الحضور بالصورة الذاتية غير مفعّل لشركتك. سجّل الحضور بدون صورة.");

    public static readonly AttendanceRefusal ConsentRequired = new("consent_required",
        "You have not agreed to selfie attendance, so a selfie cannot be used. You can punch without one.",
        "لم توافق على الحضور بالصورة الذاتية، لذلك لا يمكن استخدام صورة. يمكنك تسجيل الحضور بدونها.");

    public static readonly AttendanceRefusal EvidenceNotFound = new("evidence_not_found",
        "This selfie could not be found for you. Take a new selfie and try again.",
        "تعذّر العثور على هذه الصورة لك. التقط صورة جديدة وحاول مرة أخرى.");

    public static readonly AttendanceRefusal EvidenceExpired = new("evidence_expired",
        "This selfie is more than 10 minutes old. Take a new selfie and try again.",
        "مضى على هذه الصورة أكثر من 10 دقائق. التقط صورة جديدة وحاول مرة أخرى.");

    public static readonly AttendanceRefusal EvidenceUsed = new("evidence_used",
        "This selfie was already used for a punch. Take a new selfie for this punch.",
        "استُخدمت هذه الصورة لتسجيل سابق. التقط صورة جديدة لهذا التسجيل.");

    public static readonly AttendanceRefusal SelfieRequired = new("selfie_required",
        "Your company asks for a selfie with each punch because you agreed to selfie attendance. Take a selfie and try again, or withdraw your consent in Self-Service to punch without one.",
        "تطلب شركتك صورة ذاتية مع كل تسجيل لأنك وافقت على ذلك. التقط صورة وحاول مرة أخرى، أو اسحب موافقتك من الخدمة الذاتية للتسجيل بدونها.");

    public static readonly AttendanceRefusal RateLimited = new("selfie_rate_limited",
        "You have uploaded 10 selfies in the last hour. Wait a little and try again.",
        "رفعت 10 صور خلال الساعة الماضية. انتظر قليلًا وحاول مرة أخرى.");

    public static readonly AttendanceRefusal LocationRequired = new("location_required",
        "Your location is needed to punch. Turn on location for the app and try again.",
        "موقعك مطلوب لتسجيل الحضور. فعّل خدمة الموقع للتطبيق وحاول مرة أخرى.");

    public static readonly AttendanceRefusal LocationInvalid = new("location_invalid",
        "Your device sent a location that is not valid. Turn location off and on again, then retry.",
        "أرسل جهازك موقعًا غير صالح. أوقف خدمة الموقع وشغّلها مجددًا ثم أعد المحاولة.");

    public static readonly AttendanceRefusal LocationMocked = new("location_mocked",
        "Your phone reported a simulated (mock) location. Turn off any location-changing app and try again.",
        "أبلغ هاتفك عن موقع وهمي. أوقف أي تطبيق يغيّر الموقع وحاول مرة أخرى.");

    public static readonly AttendanceRefusal NoGeofenceSite = new("geofence_site_missing",
        "No work site with a location and radius is set up for you, so your punch cannot be checked. Ask HR to set one up in Setup → Locations.",
        "لا يوجد موقع عمل بإحداثيات ونطاق مسموح لك، لذلك لا يمكن التحقق من تسجيلك. اطلب من الموارد البشرية إعداده من الإعدادات ← المواقع.");

    public static AttendanceRefusal LocationInaccurate(decimal? accuracy, int max) => accuracy is null
        ? new("location_inaccurate",
            $"Your phone did not say how accurate your location is, and up to {max} m is allowed. Wait for a better GPS signal and try again.",
            $"لم يحدد هاتفك دقة موقعك، والحد المسموح {max} م. انتظر إشارة GPS أفضل وحاول مرة أخرى.")
        : new("location_inaccurate",
            $"Your location is only accurate to about {Math.Round(accuracy.Value)} m, and up to {max} m is allowed. Move to an open area and try again.",
            $"دقة موقعك حوالي {Math.Round(accuracy.Value)} م، والحد المسموح {max} م. انتقل إلى مكان مفتوح وحاول مرة أخرى.");

    public static AttendanceRefusal OutsideGeofence(string site, double distance, decimal radius) => new("outside_geofence",
        $"You are about {Math.Round(distance)} m from {site}; punches are allowed within {Math.Round(radius)} m. Move closer and try again.",
        $"أنت على بعد حوالي {Math.Round(distance)} م من {site}؛ يُسمح بالتسجيل ضمن {Math.Round(radius)} م. اقترب وحاول مرة أخرى.");

    public static readonly AttendanceRefusal LegacyPunchNeedsLocation = new("location_required",
        "Your company checks your location when you punch. Update the app and punch from the Attendance screen.",
        "تتحقق شركتك من موقعك عند التسجيل. حدّث التطبيق وسجّل من شاشة الحضور.");
}

/// <summary>
/// The selfie_attendance flag's ConfigJson. The two owner decisions (legal and spend) must be recorded before the
/// platform endpoint lets the flag on, and are re-checked whenever the policy is read, so a row written by any other
/// path without them is treated as OFF.
/// <code>
/// {
///   "dpia":          { "signedOffBy": "...", "signedOffAtUtc": "2026-10-08T00:00:00Z", "reference": "DPIA-..." },
///   "dataResidency": { "region": "KSA", "confirmedBy": "...", "confirmedAtUtc": "2026-10-08T00:00:00Z" },
///   "requireSelfieForConsented": false,
///   "consentPolicyVersion": "1"
/// }
/// </code>
/// </summary>
public static class SelfieAttendanceConfig
{
    public const string RequiredRegion = "KSA";
    public const string DefaultConsentPolicyVersion = "1";

    /// <summary>The fields that are missing or invalid, by JSON path; empty when both sign-offs are recorded.</summary>
    public static IReadOnlyList<string> MissingSignOffs(string? configJson, DateTime? nowUtc = null)
    {
        var missing = new List<string>();
        var root = TryParse(configJson);
        var limit = (nowUtc ?? DateTime.UtcNow).AddDays(1);

        var dpia = root?["dpia"] as JsonObject;
        if (Text(dpia, "signedOffBy") is null) missing.Add("dpia.signedOffBy");
        if (Instant(dpia, "signedOffAtUtc") is not { } signed || signed > limit) missing.Add("dpia.signedOffAtUtc");
        if (Text(dpia, "reference") is null) missing.Add("dpia.reference");

        var residency = root?["dataResidency"] as JsonObject;
        if (!string.Equals(Text(residency, "region"), RequiredRegion, StringComparison.OrdinalIgnoreCase)) missing.Add("dataResidency.region");
        if (Text(residency, "confirmedBy") is null) missing.Add("dataResidency.confirmedBy");
        if (Instant(residency, "confirmedAtUtc") is not { } confirmed || confirmed > limit) missing.Add("dataResidency.confirmedAtUtc");
        return missing;
    }

    /// <summary>The sign-off block for an audit row (verbatim from the stored config, or null).</summary>
    public static object SignOffsForAudit(string? configJson)
    {
        var root = TryParse(configJson);
        return new
        {
            dpia = root?["dpia"]?.DeepClone(),
            dataResidency = root?["dataResidency"]?.DeepClone(),
        };
    }

    public static bool RequireSelfieForConsented(string? configJson) =>
        TryParse(configJson)?["requireSelfieForConsented"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    public static string ConsentPolicyVersion(string? configJson) =>
        Text(TryParse(configJson), "consentPolicyVersion") is { Length: > 0 and <= 32 } version ? version : DefaultConsentPolicyVersion;

    internal static JsonObject? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static string? Text(JsonObject? obj, string name) =>
        obj?[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static DateTime? Instant(JsonObject? obj, string name) =>
        Text(obj, name) is { } s && DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}

/// <summary>
/// The attendance_geofence flag's ConfigJson: <c>{ "geofenceEnforced": true, "maxAccuracyMeters": 100,
/// "allowMockedLocation": false }</c>. With the flag on, enforcement defaults to true; <c>geofenceEnforced: false</c>
/// pauses it while keeping the settings.
/// </summary>
public static class PunchGeofenceConfig
{
    public const int DefaultMaxAccuracyMeters = 100;
    public const int MinAccuracyMeters = 5;
    public const int MaxAccuracyMetersCeiling = 1000;

    public static (bool Enforced, int MaxAccuracyMeters, bool AllowMockedLocation) Parse(string? configJson)
    {
        var root = SelfieAttendanceConfig.TryParse(configJson);
        var enforced = root?["geofenceEnforced"] is not JsonValue e || !e.TryGetValue<bool>(out var en) || en;
        var max = root?["maxAccuracyMeters"] is JsonValue m && m.TryGetValue<int>(out var mv) && mv is >= MinAccuracyMeters and <= MaxAccuracyMetersCeiling
            ? mv : DefaultMaxAccuracyMeters;
        var allowMocked = root?["allowMockedLocation"] is JsonValue a && a.TryGetValue<bool>(out var av) && av;
        return (enforced, max, allowMocked);
    }

    /// <summary>Why the config cannot be stored, or empty. Absent keys take their defaults.</summary>
    public static IReadOnlyList<string> Problems(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return [];
        var root = SelfieAttendanceConfig.TryParse(configJson);
        if (root is null) return ["ConfigJson must be a JSON object."];
        var problems = new List<string>();
        if (root["geofenceEnforced"] is { } e && !(e is JsonValue ev && ev.TryGetValue<bool>(out _)))
            problems.Add("geofenceEnforced must be true or false.");
        if (root["allowMockedLocation"] is { } a && !(a is JsonValue av && av.TryGetValue<bool>(out _)))
            problems.Add("allowMockedLocation must be true or false.");
        if (root["maxAccuracyMeters"] is { } m
            && !(m is JsonValue mv && mv.TryGetValue<int>(out var max) && max is >= MinAccuracyMeters and <= MaxAccuracyMetersCeiling))
            problems.Add($"maxAccuracyMeters must be a whole number from {MinAccuracyMeters} to {MaxAccuracyMetersCeiling}.");
        return problems;
    }
}

/// <summary>A tenant's effective verification policy, read from the two opt-in flags.</summary>
public sealed record AttendanceVerificationPolicy(
    bool SelfieEnabled,
    bool RequireSelfieForConsented,
    string ConsentPolicyVersion,
    bool GeofenceEnforced,
    int MaxAccuracyMeters,
    bool AllowMockedLocation);

/// <summary>A work site the geofence measures against (a Setup → Locations row with coordinates and a radius).</summary>
public sealed record GeofenceSite(Guid Id, string Name, decimal Latitude, decimal Longitude, decimal RadiusMeters);

/// <summary>The location facts a self-punch carries. Accuracy and the mock flag are the device's report.</summary>
public sealed record PunchLocation(decimal? Latitude, decimal? Longitude, decimal? AccuracyMeters, bool? Mocked);

/// <summary>The decision for one punch: refused with a reason, or allowed with what was verified.</summary>
public sealed record PunchVerificationDecision(AttendanceRefusal? Refusal, PunchVerification Verification)
{
    public static PunchVerificationDecision Refuse(AttendanceRefusal refusal) => new(refusal, PunchVerification.Unverified);
}

/// <summary>
/// Selfie attendance and the server-side geofence (v2). One place decides, for every employee self-punch route
/// (punch/web, punch/mobile, punch/kiosk and the legacy /api/mobile/attendance/punch), whether the punch may be
/// recorded and what the server verified. The client's VerificationMethod, ConfidenceScore and ClientBiometricVerified
/// are never read. No face matching is performed.
/// </summary>
public sealed class AttendanceVerificationService
{
    public static readonly TimeSpan EvidenceLifetime = TimeSpan.FromMinutes(10);
    public const int MaxUploadsPerHour = 10;

    private readonly ZayraDbContext _db;

    public AttendanceVerificationService(ZayraDbContext db) => _db = db;

    public async Task<AttendanceVerificationPolicy> GetPolicyAsync(Guid tenantId, CancellationToken ct)
    {
        var flags = await _db.TenantFeatureFlags.AsNoTracking()
            .Where(f => f.TenantId == tenantId && f.IsEnabled
                        && (f.FeatureKey == FeatureKeys.SelfieAttendance || f.FeatureKey == FeatureKeys.PunchGeofence))
            .Select(f => new { f.FeatureKey, f.ConfigJson })
            .ToListAsync(ct);
        var selfie = flags.FirstOrDefault(f => f.FeatureKey == FeatureKeys.SelfieAttendance);
        var geofence = flags.FirstOrDefault(f => f.FeatureKey == FeatureKeys.PunchGeofence);

        // Defence in depth: a selfie row without both sign-offs is OFF however it was written.
        var selfieOn = selfie is not null && SelfieAttendanceConfig.MissingSignOffs(selfie.ConfigJson).Count == 0;
        var (enforced, maxAccuracy, allowMocked) = PunchGeofenceConfig.Parse(geofence?.ConfigJson);
        return new AttendanceVerificationPolicy(
            selfieOn,
            selfieOn && SelfieAttendanceConfig.RequireSelfieForConsented(selfie!.ConfigJson),
            SelfieAttendanceConfig.ConsentPolicyVersion(selfie?.ConfigJson),
            geofence is not null && enforced,
            maxAccuracy,
            allowMocked);
    }

    /// <summary>The employee's open consent for the CURRENT policy version, or null.</summary>
    public Task<BiometricConsent?> ActiveConsentAsync(Guid tenantId, int employeeId, string policyVersion, CancellationToken ct) =>
        _db.BiometricConsents.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.EmployeeId == employeeId && c.WithdrawnAtUtc == null && c.PolicyVersion == policyVersion)
            .OrderByDescending(c => c.GivenAtUtc)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// The sites an employee may punch at: active Locations with coordinates and a radius, matched by the employee's
    /// work location (code or name), else by branch, else every geofenced site of the tenant.
    /// </summary>
    public async Task<IReadOnlyList<GeofenceSite>> SitesForAsync(Guid tenantId, int employeeId, CancellationToken ct)
    {
        var employee = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.Id == employeeId && !e.IsDeleted)
            .Select(e => new { e.BranchId, e.WorkLocation })
            .FirstOrDefaultAsync(ct);
        var sites = await _db.Locations.AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.IsActive && !l.IsDeleted
                        && l.Latitude != null && l.Longitude != null && l.GeofenceRadiusMeters != null && l.GeofenceRadiusMeters > 0)
            .Select(l => new { l.Id, l.Code, l.NameEn, l.NameAr, l.BranchId, Latitude = l.Latitude!.Value, Longitude = l.Longitude!.Value, Radius = l.GeofenceRadiusMeters!.Value })
            .ToListAsync(ct);
        if (employee is null || sites.Count == 0) return [];

        var work = employee.WorkLocation?.Trim() ?? string.Empty;
        var matched = work.Length == 0 ? [] : sites.Where(s =>
            string.Equals(s.Code.Trim(), work, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.NameEn.Trim(), work, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.NameAr.Trim(), work, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matched.Count == 0 && employee.BranchId is Guid branch)
            matched = sites.Where(s => s.BranchId == branch).ToList();
        if (matched.Count == 0) matched = sites;
        return matched
            .Select(s => new GeofenceSite(s.Id, string.IsNullOrWhiteSpace(s.NameEn) ? s.Code : s.NameEn, s.Latitude, s.Longitude, s.Radius))
            .ToList();
    }

    /// <summary>Great-circle distance in metres (Haversine, mean Earth radius 6,371 km).</summary>
    public static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusMeters = 6_371_000d;
        static double Rad(double degrees) => degrees * Math.PI / 180d;
        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return earthRadiusMeters * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    /// <summary>
    /// The geofence check for one punch. Not enforced: passes, unverified (the location is stored as given). Enforced:
    /// a missing or invalid location, a mocked location (unless allowed), an accuracy worse than the limit (or none
    /// reported), no geofenced site, or a position outside every site's radius is refused with a plain reason.
    /// </summary>
    public async Task<(AttendanceRefusal? Refusal, bool Verified, string? Site, double? Distance)> CheckGeofenceAsync(
        Guid tenantId, int employeeId, PunchLocation location, AttendanceVerificationPolicy policy, CancellationToken ct)
    {
        if (!policy.GeofenceEnforced) return (null, false, null, null);
        if (location.Latitude is not { } lat || location.Longitude is not { } lon) return (AttendanceRefusals.LocationRequired, false, null, null);
        if (lat is < -90 or > 90 || lon is < -180 or > 180) return (AttendanceRefusals.LocationInvalid, false, null, null);
        if (location.Mocked == true && !policy.AllowMockedLocation) return (AttendanceRefusals.LocationMocked, false, null, null);
        if (location.AccuracyMeters is not { } accuracy || accuracy < 0 || accuracy > policy.MaxAccuracyMeters)
            return (AttendanceRefusals.LocationInaccurate(location.AccuracyMeters, policy.MaxAccuracyMeters), false, null, null);

        var sites = await SitesForAsync(tenantId, employeeId, ct);
        if (sites.Count == 0) return (AttendanceRefusals.NoGeofenceSite, false, null, null);
        var nearest = sites
            .Select(s => (Site: s, Distance: HaversineMeters((double)lat, (double)lon, (double)s.Latitude, (double)s.Longitude)))
            .OrderBy(x => x.Distance - (double)x.Site.RadiusMeters)
            .First();
        return nearest.Distance <= (double)nearest.Site.RadiusMeters
            ? (null, true, nearest.Site.Name, nearest.Distance)
            : (AttendanceRefusals.OutsideGeofence(nearest.Site.Name, nearest.Distance, nearest.Site.RadiusMeters), false, nearest.Site.Name, nearest.Distance);
    }

    /// <summary>
    /// Decides one self-punch for <paramref name="employeeId"/> (the employee the punch is recorded against). Evidence
    /// must exist in this tenant, belong to that employee, be unused, unpurged and unexpired, and the employee must hold
    /// active consent under a tenant with selfie attendance on. Without evidence, a consenting employee is refused only
    /// when the tenant requires the selfie for consenting employees; a non-consenting employee is never asked for one.
    /// </summary>
    public async Task<PunchVerificationDecision> EvaluatePunchAsync(
        Guid tenantId, int employeeId, Guid? evidenceId, PunchLocation location, CancellationToken ct)
    {
        var policy = await GetPolicyAsync(tenantId, ct);
        var selfie = false;

        if (evidenceId is Guid id && id != Guid.Empty)
        {
            if (!policy.SelfieEnabled) return PunchVerificationDecision.Refuse(AttendanceRefusals.SelfieNotEnabled);
            var evidence = await _db.AttendanceEvidence.AsNoTracking()
                .Where(e => e.TenantId == tenantId && e.Id == id)
                .Select(e => new { e.EmployeeId, e.UsedAtUtc, e.PurgeState, e.ExpiresAtUtc })
                .FirstOrDefaultAsync(ct);
            var refusal = EvidenceRefusal(evidence?.EmployeeId, evidence?.UsedAtUtc, evidence?.PurgeState, evidence?.ExpiresAtUtc, employeeId, DateTime.UtcNow);
            if (refusal is not null) return PunchVerificationDecision.Refuse(refusal);
            if (await ActiveConsentAsync(tenantId, employeeId, policy.ConsentPolicyVersion, ct) is null)
                return PunchVerificationDecision.Refuse(AttendanceRefusals.ConsentRequired);
            selfie = true;
        }
        else if (policy.RequireSelfieForConsented
                 && await ActiveConsentAsync(tenantId, employeeId, policy.ConsentPolicyVersion, ct) is not null)
        {
            return PunchVerificationDecision.Refuse(AttendanceRefusals.SelfieRequired);
        }

        var geo = await CheckGeofenceAsync(tenantId, employeeId, location, policy, ct);
        if (geo.Refusal is not null) return PunchVerificationDecision.Refuse(geo.Refusal);

        return new PunchVerificationDecision(null, new PunchVerification(
            AttendanceVerificationMethods.From(selfie, geo.Verified),
            selfie ? evidenceId : null,
            geo.Site,
            geo.Distance));
    }

    /// <summary>The evidence rule, shared by the pre-check and the re-check inside the punch transaction.</summary>
    public static AttendanceRefusal? EvidenceRefusal(
        int? ownerEmployeeId, DateTime? usedAtUtc, string? purgeState, DateTime? expiresAtUtc, int punchEmployeeId, DateTime nowUtc)
    {
        // Another employee's id answers exactly like a missing one: the caller learns nothing about it.
        if (ownerEmployeeId is null || ownerEmployeeId != punchEmployeeId) return AttendanceRefusals.EvidenceNotFound;
        if (usedAtUtc is not null) return AttendanceRefusals.EvidenceUsed;
        if (purgeState != AttendanceEvidencePurgeStates.Active || expiresAtUtc is null || expiresAtUtc <= nowUtc)
            return AttendanceRefusals.EvidenceExpired;
        return null;
    }
}
