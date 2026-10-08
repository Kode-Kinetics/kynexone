using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Attendance;

/// <summary>A plain-language refusal for a punch, an upload or a consent call. <see cref="Code"/> is stable for clients.</summary>
public sealed record AttendanceRefusal(string Code, string Message, string MessageAr)
{
    public object Body => new { code = Code, message = Message, messageAr = MessageAr };

    /// <summary>The body plus <c>punchWithoutSelfie</c>: true when the failure was the server's, so the app should punch without a selfie.</summary>
    public object BodyWith(bool punchWithoutSelfie) => new { code = Code, message = Message, messageAr = MessageAr, punchWithoutSelfie };
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

    /// <summary>A kiosk or on-behalf punch carried an evidence id. Those punches never take a selfie.</summary>
    public static readonly AttendanceRefusal EvidenceNotAccepted = new("evidence_not_accepted",
        "A selfie can only be attached to your own punch from the app. Record this punch without a selfie.",
        "لا يمكن إرفاق صورة ذاتية إلا بتسجيلك أنت من التطبيق. سجّل هذا الحضور بدون صورة.");

    // Never tells anyone to withdraw consent to get past it: that would make consent a lever, not a choice.
    public static readonly AttendanceRefusal SelfieRequired = new("selfie_required",
        "Your company asks for a selfie with each punch you make from the app. Take a selfie and try again.",
        "تطلب شركتك صورة ذاتية مع كل تسجيل حضور من التطبيق. التقط صورة وحاول مرة أخرى.");

    /// <summary>
    /// Review 3: the daily cap of server-failure waivers is used up. Still <c>selfie_required</c> (a client handles it
    /// as such), with the plain message the decision fixed.
    /// </summary>
    public static readonly AttendanceRefusal SelfieRequiredTryAgain = new("selfie_required",
        "Please try the selfie again in a moment.",
        "يُرجى محاولة التقاط الصورة الذاتية مرة أخرى بعد قليل.");

    /// <summary>
    /// Review 3: another upload of this employee is still in flight (a Pending row younger than 60 s). Never waives a
    /// required selfie: parallel uploads must not manufacture server failures.
    /// </summary>
    public static readonly AttendanceRefusal SelfieUploadInProgress = new("selfie_upload_in_progress",
        "A selfie is already being sent. Wait a moment, then try again.",
        "يجري إرسال صورة ذاتية بالفعل. انتظر قليلًا ثم حاول مرة أخرى.");

    public static readonly AttendanceRefusal RateLimited = new("selfie_rate_limited",
        "You have tried to upload 10 selfies in the last hour. Wait a little and try again.",
        "حاولت رفع 10 صور خلال الساعة الماضية. انتظر قليلًا وحاول مرة أخرى.");

    public static readonly AttendanceRefusal SelfieBusy = new("selfie_busy",
        "The server is busy processing other selfies. Try again in a few seconds, or record this punch without a selfie.",
        "الخادم مشغول بمعالجة صور أخرى. حاول مرة أخرى بعد بضع ثوانٍ، أو سجّل هذا الحضور بدون صورة.");

    /// <summary>Storage refused or failed the write. The server's failure: it may waive a required selfie for one punch (review 3).</summary>
    public static readonly AttendanceRefusal SelfieStorageUnavailable = new("selfie_storage_unavailable",
        "Your selfie could not be saved because of a problem on our side. Record this punch without a selfie.",
        "تعذّر حفظ صورتك بسبب مشكلة لدينا. سجّل هذا الحضور بدون صورة.");

    /// <summary>The stored upload was purged before it could be activated (e.g. a withdrawal and a new consent in between).</summary>
    public static readonly AttendanceRefusal UploadInterrupted = new("selfie_upload_interrupted",
        "Your selfie could not be saved. Take it again.",
        "تعذّر حفظ صورتك. التقطها مرة أخرى.");

    /// <summary>
    /// The upload did not finish within the server's deadline (body read, decode and storage write together;
    /// <c>AttendanceEvidenceController.UploadDeadline</c>). The attempt is released, so it does not count, and it is NOT a
    /// server failure: it never waives a required selfie (a slow or drip-fed body must not manufacture a waiver).
    /// </summary>
    public static readonly AttendanceRefusal SelfieUploadTimeout = new("selfie_upload_timeout",
        "Sending your selfie took too long, so it was not saved. Check your connection and take it again.",
        "استغرق إرسال صورتك وقتًا طويلًا فلم تُحفظ. تحقّق من اتصالك والتقطها مرة أخرى.");

    /// <summary>
    /// The connection dropped while the selfie was being sent (the body ended early). The attempt is released, so it does
    /// not count, and it never waives a required selfie: the network failing is not the server failing.
    /// </summary>
    public static readonly AttendanceRefusal SelfieUploadIncomplete = new("selfie_upload_incomplete",
        "Your selfie did not arrive completely, so it was not saved. Check your connection and take it again.",
        "لم تصل صورتك كاملة فلم تُحفظ. تحقّق من اتصالك والتقطها مرة أخرى.");

    /// <summary>
    /// The punch said <c>mockDetection: "Unsupported"</c> from an Android phone (its X-Client-Platform header or its
    /// User-Agent), which CAN report a simulated location (review 2, item 11).
    /// </summary>
    public static readonly AttendanceRefusal MockDetectionRequired = new("mock_detection_required",
        "Android phones can report a simulated location, but this app did not check. Update the KynexOne app and try again.",
        "تستطيع هواتف أندرويد الإبلاغ عن الموقع الوهمي، لكن التطبيق لم يتحقق من ذلك. حدّث تطبيق KynexOne وحاول مرة أخرى.");

    public static readonly AttendanceRefusal LocationRequired = new("location_required",
        "Your location is needed to punch. Turn on location for the app and try again.",
        "موقعك مطلوب لتسجيل الحضور. فعّل خدمة الموقع للتطبيق وحاول مرة أخرى.");

    public static readonly AttendanceRefusal LocationInvalid = new("location_invalid",
        "Your device sent a location that is not valid. Turn location off and on again, then retry.",
        "أرسل جهازك موقعًا غير صالح. أوقف خدمة الموقع وشغّلها مجددًا ثم أعد المحاولة.");

    public static readonly AttendanceRefusal LocationMocked = new("location_mocked",
        "Your phone reported a simulated (mock) location. Turn off any location-changing app and try again.",
        "أبلغ هاتفك عن موقع وهمي. أوقف أي تطبيق يغيّر الموقع وحاول مرة أخرى.");

    /// <summary>The geofence is enforced and the app sent neither mockDetection nor locationMocked: an old app version.</summary>
    public static readonly AttendanceRefusal AppUpdateRequired = new("app_update_required",
        "Please update the KynexOne app to record attendance at your site.",
        "يرجى تحديث تطبيق KynexOne لتسجيل الحضور في موقع عملك.");

    /// <summary>The geofence is enforced: a browser cannot attest to a mocked location, so self punches come from the app.</summary>
    public static readonly AttendanceRefusal MobileAppRequired = new("mobile_app_required",
        "Your company requires attendance from the mobile app at your site.",
        "تتطلب شركتك تسجيل الحضور من تطبيق الجوال في موقع عملك.");

    public static readonly AttendanceRefusal NoGeofenceSite = new("geofence_site_missing",
        "No work site with a location and radius is set up for you, so your punch cannot be checked. Ask HR to set one up in Setup → Locations.",
        "لا يوجد موقع عمل بإحداثيات ونطاق مسموح لك، لذلك لا يمكن التحقق من تسجيلك. اطلب من الموارد البشرية إعداده من الإعدادات ← المواقع.");

    /// <summary>events/push or device ingest tried to write a label only the server's own selfie/geofence check may write.</summary>
    public static readonly AttendanceRefusal VerificationLabelReserved = new("verification_label_reserved",
        "Selfie and geofence verification can only be recorded by a punch from the KynexOne app. Send another verification method (for example Device or Manual) and no evidence: photo reference.",
        "لا يُسجَّل التحقق بالصورة الذاتية أو النطاق الجغرافي إلا من تسجيل حضور عبر تطبيق KynexOne. أرسل طريقة تحقق أخرى (مثل Device أو Manual) دون مرجع صورة evidence:.");

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
/// The labels and photo references (<c>evidence:</c>, and review 3's <c>waiver:</c>) only the server's own punch decision
/// may write (selfie attendance v2, review item 9).
/// events/push, CSV import and device ingest REFUSE them (decision: refuse, not rewrite — an integrator learns at once,
/// and nothing is silently changed).
/// </summary>
public static class ReservedVerificationLabels
{
    public const string EvidencePhotoPrefix = "evidence:";

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        AttendanceVerificationMethods.Selfie,
        AttendanceVerificationMethods.Geofence,
        AttendanceVerificationMethods.SelfieAndGeofence,
    };

    public static bool Violates(string? verificationMethod, string? photoReference) =>
        (verificationMethod is not null && Reserved.Contains(verificationMethod.Trim()))
        || (photoReference is not null && (photoReference.TrimStart().StartsWith(EvidencePhotoPrefix, StringComparison.OrdinalIgnoreCase)
                                           // Review 3: a waived punch's reference is the server's too.
                                           || photoReference.TrimStart().StartsWith(SelfieWaivers.PhotoReferencePrefix, StringComparison.OrdinalIgnoreCase)));
}

/// <summary>
/// The selfie_attendance flag's ConfigJson. The two owner decisions (legal and spend) must be recorded before the
/// platform endpoint lets the flag on, and are re-checked whenever the policy is read, so a row written by any other
/// path without them is treated as OFF.
/// <code>
/// {
///   "dpia":          { "signedOffBy": "&lt;platform user id&gt;", "signedOffAtUtc": "2026-10-08T00:00:00Z", "reference": "DPIA-2026-007" },
///   "dataResidency": { "region": "KSA", "confirmedBy": "&lt;server: actor id&gt;", "confirmedAtUtc": "&lt;server: now&gt;", "storageLocation": "&lt;server&gt;" },
///   "enabledBy": "&lt;server: actor id&gt;", "enabledByEmail": "&lt;server&gt;", "enabledAtUtc": "&lt;server: now&gt;",
///   "requireSelfieForConsented": false,
///   "consentPolicyVersion": "1"
/// }
/// </code>
/// The caller supplies the DPIA block and the region; the SERVER stamps who enabled it, when, who confirmed residency
/// and where storage really is (<see cref="StampServerFields"/>). Whatever a request puts in those fields is overwritten.
/// </summary>
public static class SelfieAttendanceConfig
{
    public const string RequiredRegion = StorageResidency.Ksa;
    public const string DefaultConsentPolicyVersion = "1";

    /// <summary>The earliest acceptable sign-off date: nothing about this feature existed before it.</summary>
    public static readonly DateTime EarliestSignOffUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A DPIA reference: <c>DPIA-YYYY-NNN</c> (year, then a register number of three or more digits).</summary>
    public static readonly Regex ReferenceFormat = new(@"^DPIA-20\d{2}-\d{3,6}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The fields a REQUEST must supply that are missing or invalid, by JSON path (the DPIA block and the region); the
    /// residency confirmation is stamped by the server, so it is not asked of the caller.
    /// </summary>
    public static IReadOnlyList<string> RequestProblems(string? configJson, DateTime nowUtc)
    {
        var missing = new List<string>();
        var root = TryParse(configJson);
        var dpia = root?["dpia"] as JsonObject;
        if (Text(dpia, "signedOffBy") is not { } by || !Guid.TryParse(by, out var byId) || byId == Guid.Empty) missing.Add("dpia.signedOffBy");
        if (!InWindow(Instant(dpia, "signedOffAtUtc"), nowUtc)) missing.Add("dpia.signedOffAtUtc");
        if (Text(dpia, "reference") is not { } reference || !ReferenceFormat.IsMatch(reference)) missing.Add("dpia.reference");
        var residency = root?["dataResidency"] as JsonObject;
        if (!string.Equals(Text(residency, "region"), RequiredRegion, StringComparison.OrdinalIgnoreCase)) missing.Add("dataResidency.region");
        return missing;
    }

    /// <summary>
    /// Everything that must hold for a STORED config to count as signed off (read on every policy read): the request
    /// fields above plus the server-stamped residency confirmation. Empty when signed off.
    /// </summary>
    public static IReadOnlyList<string> MissingSignOffs(string? configJson, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var missing = RequestProblems(configJson, now).ToList();
        var residency = TryParse(configJson)?["dataResidency"] as JsonObject;
        if (Text(residency, "confirmedBy") is not { } by || !Guid.TryParse(by, out var byId) || byId == Guid.Empty) missing.Add("dataResidency.confirmedBy");
        if (!InWindow(Instant(residency, "confirmedAtUtc"), now)) missing.Add("dataResidency.confirmedAtUtc");
        return missing;
    }

    /// <summary>
    /// The config to store when an Owner enables the flag: the request's own fields, with the actor, the time and the
    /// verified storage location stamped by the server over anything the request said.
    /// </summary>
    public static string StampServerFields(string? configJson, Guid actorId, string actorEmail, DateTime nowUtc, string storageLocation)
    {
        var root = TryParse(configJson) ?? new JsonObject();
        var residency = root["dataResidency"] as JsonObject ?? new JsonObject();
        residency["region"] = RequiredRegion;
        residency["confirmedBy"] = actorId.ToString();
        residency["confirmedAtUtc"] = nowUtc.ToString("O", CultureInfo.InvariantCulture);
        residency["storageLocation"] = storageLocation;
        root["dataResidency"] = residency;
        root["enabledBy"] = actorId.ToString();
        root["enabledByEmail"] = actorEmail;
        root["enabledAtUtc"] = nowUtc.ToString("O", CultureInfo.InvariantCulture);
        return root.ToJsonString();
    }

    /// <summary>The storage location the server stamped when the flag was enabled (<see cref="StorageResidency.Canonical"/>).</summary>
    public static string? StampedStorageLocation(string? configJson) =>
        Text(TryParse(configJson)?["dataResidency"] as JsonObject, "storageLocation");

    /// <summary>The DPIA's signedOffBy, when it parses.</summary>
    public static Guid? SignedOffBy(string? configJson) =>
        Text(TryParse(configJson)?["dpia"] as JsonObject, "signedOffBy") is { } s && Guid.TryParse(s, out var id) ? id : null;

    /// <summary>The sign-off block for an audit row (verbatim from the stored config, or null).</summary>
    public static object SignOffsForAudit(string? configJson)
    {
        var root = TryParse(configJson);
        return new
        {
            dpia = root?["dpia"]?.DeepClone(),
            dataResidency = root?["dataResidency"]?.DeepClone(),
            enabledBy = root?["enabledBy"]?.DeepClone(),
            enabledAtUtc = root?["enabledAtUtc"]?.DeepClone(),
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

    private static bool InWindow(DateTime? instant, DateTime nowUtc) =>
        instant is { } d && d >= EarliestSignOffUtc && d <= nowUtc;

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

/// <summary>A tenant's effective verification policy, read from the two opt-in flags and the deploy's storage residency.</summary>
public sealed record AttendanceVerificationPolicy(
    bool SelfieEnabled,
    bool RequireSelfieForConsented,
    string ConsentPolicyVersion,
    bool GeofenceEnforced,
    int MaxAccuracyMeters,
    bool AllowMockedLocation,
    string? SelfieOffReason = null);

/// <summary>A work site the geofence measures against (a Setup → Locations row with coordinates and a radius).</summary>
public sealed record GeofenceSite(Guid Id, string Name, decimal Latitude, decimal Longitude, decimal RadiusMeters);

/// <summary>The sites an employee may punch at, and whether they came from the tenant-wide fallback (no match at all).</summary>
public sealed record GeofenceSiteResolution(IReadOnlyList<GeofenceSite> Sites, bool FellBackToAllSites);

/// <summary>An employee the geofence cannot place: neither their work location nor their branch matches a geofenced site.</summary>
public sealed record UnmatchedGeofenceEmployee(int EmployeeId, string EmployeeCode, string Name, string? WorkLocation, Guid? BranchId);

/// <summary>
/// The location facts a self-punch carries. Accuracy, the mock flag and whether the platform can detect a mock at all
/// (<see cref="MockDetection"/>: <c>Supported</c> on Android, <c>Unsupported</c> on iOS) are the device's report.
/// </summary>
public sealed record PunchLocation(decimal? Latitude, decimal? Longitude, decimal? AccuracyMeters, bool? Mocked, string? MockDetection = null,
    ClientPlatform? Platform = null);

/// <summary>
/// The phone platform a punch came from, as far as the server can tell (review 2, item 11): the app's
/// <c>X-Client-Platform</c> header (<c>android</c> | <c>ios</c>) and the User-Agent. <see cref="IsAndroid"/> when
/// EITHER says Android (an Android phone can report a simulated location, so "Unsupported" is refused from it).
/// <see cref="Header"/> is null for an app built before the header existed.
/// </summary>
public sealed record ClientPlatform(string? Header, bool UserAgentSaysAndroid, bool UserAgentSaysIos)
{
    public const string HeaderName = "X-Client-Platform";
    public const string Android = "android";
    public const string Ios = "ios";

    public bool IsAndroid => Header == Android || UserAgentSaysAndroid;

    /// <summary>What the audit records: android, ios, or unknown (no header, and a User-Agent that says neither).</summary>
    public string Describe => IsAndroid ? Android : Header == Ios || UserAgentSaysIos ? Ios : "unknown";

    public static ClientPlatform From(string? header, string? userAgent)
    {
        var h = header?.Trim().ToLowerInvariant();
        var ua = userAgent ?? string.Empty;
        return new ClientPlatform(
            h is Android or Ios ? h : null,
            // React Native on Android sends okhttp's agent; a browser or WebView on Android says "Android".
            ua.Contains("Android", StringComparison.OrdinalIgnoreCase) || ua.StartsWith("okhttp", StringComparison.OrdinalIgnoreCase),
            ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase) || ua.Contains("iPad", StringComparison.OrdinalIgnoreCase)
                || ua.Contains("CFNetwork", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Values of the punch request's <c>mockDetection</c>.</summary>
public static class MockDetectionModes
{
    public const string Supported = "Supported";
    public const string Unsupported = "Unsupported";

    public static string? Normalize(string? value) =>
        string.Equals(value?.Trim(), Supported, StringComparison.OrdinalIgnoreCase) ? Supported
        : string.Equals(value?.Trim(), Unsupported, StringComparison.OrdinalIgnoreCase) ? Unsupported
        : null;
}

/// <summary>
/// Who is punching, and through which route. The selfie requirement applies only to the employee's OWN punch from the
/// app or the web; the geofence applies to self punches and the kiosk (contract rule 3), never to an authorized
/// on-behalf punch, whose location is the operator's, not the employee's.
/// </summary>
public enum PunchChannel
{
    /// <summary>POST punch/mobile by the employee themselves.</summary>
    SelfMobile,
    /// <summary>POST punch/web by the employee themselves.</summary>
    SelfWeb,
    /// <summary>POST /api/mobile/attendance/punch: the old app's self punch, which carries no location and no selfie.</summary>
    SelfMobileLegacy,
    /// <summary>POST punch/kiosk (any caller): a shared device; recorded as VerificationMethod None.</summary>
    Kiosk,
    /// <summary>punch/web or punch/mobile for another employee (attendance.write + scope): recorded as None.</summary>
    OnBehalf,
}

/// <summary>The decision for one punch: refused with a reason, or allowed with what was verified.</summary>
public sealed record PunchVerificationDecision(AttendanceRefusal? Refusal, PunchVerification Verification)
{
    public static PunchVerificationDecision Refuse(AttendanceRefusal refusal) => new(refusal, PunchVerification.Unverified);
}

/// <summary>Outcome of the geofence check for one punch.</summary>
public sealed record GeofenceCheck(AttendanceRefusal? Refusal, bool Verified, string? Site, double? Distance,
    bool FellBackToAllSites = false, bool MockDetectionUnavailable = false, string? ClientPlatform = null)
{
    public static readonly GeofenceCheck NotEnforced = new(null, false, null, null);
    public static GeofenceCheck Refuse(AttendanceRefusal refusal) => new(refusal, false, null, null);
}

/// <summary>
/// Selfie attendance and the server-side geofence (v2). One place decides, for every punch route (punch/web,
/// punch/mobile, punch/kiosk and the legacy /api/mobile/attendance/punch), whether the punch may be recorded and what
/// the server verified. The client's VerificationMethod, ConfidenceScore and ClientBiometricVerified are never read.
/// No face matching is performed.
/// </summary>
public sealed class AttendanceVerificationService
{
    public static readonly TimeSpan EvidenceLifetime = TimeSpan.FromMinutes(10);
    public const int MaxUploadsPerHour = 10;

    /// <summary>Audit action written when the upload failed on the server's side (busy, or storage). Keyed by employee.
    /// A record for people, never read back to decide anything: the waiver lives on the attempt's own row (review 3).</summary>
    public const string SelfieServerFailureAction = "attendance.selfie.server_failure";
    /// <summary>Audit action written when a required selfie was waived for a punch because of such a failure.</summary>
    public const string SelfieRequirementWaivedAction = "attendance.selfie.requirement_waived";
    public const string EmployeeEntity = "Employee";

    /// <summary>
    /// Review 3, item 8 (no behaviour change by default): once the app build that sends <c>X-Client-Platform</c> is the
    /// minimum version, the platform team sets this to true and <c>mockDetection: "Unsupported"</c> WITHOUT the header is
    /// refused under the geofence (<c>app_update_required</c>). Read on every punch, so a configuration reload applies it.
    /// </summary>
    public const string RequirePlatformHeaderForUnsupportedKey = "Attendance:RequirePlatformHeaderForUnsupported";

    private readonly ZayraDbContext _db;
    private readonly StorageResidency _residency;
    private readonly Microsoft.Extensions.Configuration.IConfiguration? _configuration;

    /// <param name="residency">Where this deploy stores documents. Without one nothing is resident, so selfie attendance is OFF.</param>
    /// <param name="configuration">Read for <see cref="RequirePlatformHeaderForUnsupportedKey"/> (absent = false).</param>
    public AttendanceVerificationService(ZayraDbContext db, StorageResidency? residency = null,
        Microsoft.Extensions.Configuration.IConfiguration? configuration = null)
    {
        _db = db;
        _residency = residency ?? StorageResidency.Unconfigured;
        _configuration = configuration;
    }

    /// <summary>Whether "Unsupported" without an X-Client-Platform header is refused under the geofence (default false).</summary>
    public bool RequirePlatformHeaderForUnsupported =>
        _configuration is not null
        && bool.TryParse(_configuration[RequirePlatformHeaderForUnsupportedKey], out var on) && on;

    public StorageResidency Residency => _residency;

    public async Task<AttendanceVerificationPolicy> GetPolicyAsync(Guid tenantId, CancellationToken ct)
    {
        var flags = await _db.TenantFeatureFlags.AsNoTracking()
            .Where(f => f.TenantId == tenantId && f.IsEnabled
                        && (f.FeatureKey == FeatureKeys.SelfieAttendance || f.FeatureKey == FeatureKeys.PunchGeofence))
            .Select(f => new { f.FeatureKey, f.ConfigJson })
            .ToListAsync(ct);
        var selfie = flags.FirstOrDefault(f => f.FeatureKey == FeatureKeys.SelfieAttendance);
        var geofence = flags.FirstOrDefault(f => f.FeatureKey == FeatureKeys.PunchGeofence);

        // Defence in depth, on EVERY read: a selfie row without both sign-offs is OFF however it was written; so is one
        // on a deploy whose storage (configured endpoint/region AND the bucket's own reported region) is not on the KSA
        // allow-list; and so is one whose storage has MOVED since the flag was enabled (the location stamped then).
        string? offReason = null;
        if (selfie is not null)
        {
            if (SelfieAttendanceConfig.MissingSignOffs(selfie.ConfigJson) is { Count: > 0 })
                offReason = "Selfie attendance is switched on for this company, but its DPIA sign-off or data-residency confirmation is incomplete, so it is treated as off.";
            else if (await _residency.CheckAsync(SelfieAttendanceConfig.RequiredRegion, ct) is var verdict && !verdict.Resident)
                offReason = "Selfie attendance is treated as off: " + verdict.Reason;
            else if (!string.Equals(SelfieAttendanceConfig.StampedStorageLocation(selfie.ConfigJson), verdict.Location, StringComparison.OrdinalIgnoreCase))
                offReason = $"Selfie attendance is treated as off: document storage has moved since it was switched on (now {verdict.Location}, "
                            + $"then {SelfieAttendanceConfig.StampedStorageLocation(selfie.ConfigJson) ?? "not recorded"}). A platform Owner must switch it on again.";
        }
        var selfieOn = selfie is not null && offReason is null;
        var (enforced, maxAccuracy, allowMocked) = PunchGeofenceConfig.Parse(geofence?.ConfigJson);
        return new AttendanceVerificationPolicy(
            selfieOn,
            selfieOn && SelfieAttendanceConfig.RequireSelfieForConsented(selfie!.ConfigJson),
            SelfieAttendanceConfig.ConsentPolicyVersion(selfie?.ConfigJson),
            geofence is not null && enforced,
            maxAccuracy,
            allowMocked,
            offReason);
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
    public async Task<IReadOnlyList<GeofenceSite>> SitesForAsync(Guid tenantId, int employeeId, CancellationToken ct) =>
        (await ResolveSitesAsync(tenantId, employeeId, ct)).Sites;

    /// <summary>As <see cref="SitesForAsync"/>, also saying whether the tenant-wide fallback was used (audited per punch).</summary>
    public async Task<GeofenceSiteResolution> ResolveSitesAsync(Guid tenantId, int employeeId, CancellationToken ct)
    {
        var employee = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.Id == employeeId && !e.IsDeleted)
            .Select(e => new { e.BranchId, e.WorkLocation })
            .FirstOrDefaultAsync(ct);
        var sites = await GeofencedSitesAsync(tenantId, ct);
        if (employee is null || sites.Count == 0) return new([], false);
        var matched = Match(sites, employee.WorkLocation, employee.BranchId);
        var fellBack = matched.Count == 0;
        return new((fellBack ? sites : matched).Select(ToSite).ToList(), fellBack);
    }

    /// <summary>
    /// Active employees in <paramref name="employeeIds"/> (null = all) whose sites come only from the tenant-wide
    /// fallback: neither their work location nor their branch matches a geofenced site. Empty when no site exists.
    /// </summary>
    public async Task<IReadOnlyList<UnmatchedGeofenceEmployee>> UnmatchedEmployeesAsync(
        Guid tenantId, IReadOnlyCollection<int>? employeeIds, CancellationToken ct)
    {
        var sites = await GeofencedSitesAsync(tenantId, ct);
        if (sites.Count == 0) return [];
        var query = _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && e.Status == EmployeeStatuses.Active);
        if (employeeIds is not null) query = query.Where(e => employeeIds.Contains(e.Id));
        var employees = await query
            .OrderBy(e => e.EmployeeCode)
            .Select(e => new { e.Id, e.EmployeeCode, e.FullName, e.WorkLocation, e.BranchId })
            .ToListAsync(ct);
        return employees
            .Where(e => Match(sites, e.WorkLocation, e.BranchId).Count == 0)
            .Select(e => new UnmatchedGeofenceEmployee(e.Id, e.EmployeeCode, e.FullName, e.WorkLocation, e.BranchId))
            .ToList();
    }

    private sealed record SiteRow(Guid Id, string Code, string NameEn, string NameAr, Guid? BranchId, decimal Latitude, decimal Longitude, decimal Radius);

    private async Task<List<SiteRow>> GeofencedSitesAsync(Guid tenantId, CancellationToken ct) =>
        await _db.Locations.AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.IsActive && !l.IsDeleted
                        && l.Latitude != null && l.Longitude != null && l.GeofenceRadiusMeters != null && l.GeofenceRadiusMeters > 0)
            .Select(l => new SiteRow(l.Id, l.Code, l.NameEn, l.NameAr, l.BranchId, l.Latitude!.Value, l.Longitude!.Value, l.GeofenceRadiusMeters!.Value))
            .ToListAsync(ct);

    private static List<SiteRow> Match(List<SiteRow> sites, string? workLocation, Guid? branchId)
    {
        var work = workLocation?.Trim() ?? string.Empty;
        var matched = work.Length == 0 ? [] : sites.Where(s =>
            string.Equals(s.Code.Trim(), work, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.NameEn.Trim(), work, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.NameAr.Trim(), work, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matched.Count == 0 && branchId is Guid branch)
            matched = sites.Where(s => s.BranchId == branch).ToList();
        return matched;
    }

    private static GeofenceSite ToSite(SiteRow s) =>
        new(s.Id, string.IsNullOrWhiteSpace(s.NameEn) ? s.Code : s.NameEn, s.Latitude, s.Longitude, s.Radius);

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
    /// <para>On the app (<see cref="PunchChannel.SelfMobile"/>) the mock rule depends on <see cref="PunchLocation.MockDetection"/>:
    /// <c>Supported</c> (Android) refuses <c>locationMocked: true</c> and needs the flag; <c>Unsupported</c> (iOS, which
    /// cannot detect a mock) is accepted and audited; neither field at all is an old app and is refused with
    /// "update the app". Elsewhere (the kiosk) a reported mock is refused and a missing flag is not.</para>
    /// </summary>
    public async Task<GeofenceCheck> CheckGeofenceAsync(
        Guid tenantId, int employeeId, PunchLocation location, AttendanceVerificationPolicy policy, CancellationToken ct,
        PunchChannel channel = PunchChannel.SelfMobile)
    {
        if (!policy.GeofenceEnforced) return GeofenceCheck.NotEnforced;
        if (location.Latitude is not { } lat || location.Longitude is not { } lon) return GeofenceCheck.Refuse(AttendanceRefusals.LocationRequired);
        if (lat is < -90 or > 90 || lon is < -180 or > 180) return GeofenceCheck.Refuse(AttendanceRefusals.LocationInvalid);

        var mockUnavailable = false;
        if (!policy.AllowMockedLocation)
        {
            if (channel == PunchChannel.SelfMobile)
            {
                var detection = MockDetectionModes.Normalize(location.MockDetection)
                                // An app built to the first contract sends locationMocked without mockDetection: Android.
                                ?? (location.Mocked is not null ? MockDetectionModes.Supported : null);
                if (detection is null) return GeofenceCheck.Refuse(AttendanceRefusals.AppUpdateRequired);
                if (location.Mocked == true) return GeofenceCheck.Refuse(AttendanceRefusals.LocationMocked);
                if (detection == MockDetectionModes.Supported && location.Mocked is null)
                    return GeofenceCheck.Refuse(AttendanceRefusals.AppUpdateRequired);
                mockUnavailable = detection == MockDetectionModes.Unsupported;
                // "Unsupported" is only the device's word: an Android phone (by its platform header or User-Agent) CAN
                // detect a mock, so it is refused. With no header and an inconclusive User-Agent (an old app) it is
                // accepted as before, and the audit records the platform as unknown.
                if (mockUnavailable && location.Platform is { IsAndroid: true })
                    return GeofenceCheck.Refuse(AttendanceRefusals.MockDetectionRequired);
                // Review 3, item 8: off by default. Once the header-sending app build is the minimum version, the
                // platform team switches this on and an app that sends no platform header must update.
                if (mockUnavailable && location.Platform?.Header is null && RequirePlatformHeaderForUnsupported)
                    return GeofenceCheck.Refuse(AttendanceRefusals.AppUpdateRequired);
            }
            else if (location.Mocked == true)
            {
                return GeofenceCheck.Refuse(AttendanceRefusals.LocationMocked);
            }
        }
        if (location.AccuracyMeters is not { } accuracy || accuracy < 0 || accuracy > policy.MaxAccuracyMeters)
            return GeofenceCheck.Refuse(AttendanceRefusals.LocationInaccurate(location.AccuracyMeters, policy.MaxAccuracyMeters));

        var resolution = await ResolveSitesAsync(tenantId, employeeId, ct);
        if (resolution.Sites.Count == 0) return GeofenceCheck.Refuse(AttendanceRefusals.NoGeofenceSite);
        var nearest = resolution.Sites
            .Select(s => (Site: s, Distance: HaversineMeters((double)lat, (double)lon, (double)s.Latitude, (double)s.Longitude)))
            .OrderBy(x => x.Distance - (double)x.Site.RadiusMeters)
            .First();
        var platform = location.Platform?.Describe ?? "unknown";
        return nearest.Distance <= (double)nearest.Site.RadiusMeters
            ? new GeofenceCheck(null, true, nearest.Site.Name, nearest.Distance, resolution.FellBackToAllSites, mockUnavailable, platform)
            : new GeofenceCheck(AttendanceRefusals.OutsideGeofence(nearest.Site.Name, nearest.Distance, nearest.Site.RadiusMeters),
                false, nearest.Site.Name, nearest.Distance, resolution.FellBackToAllSites, mockUnavailable, platform);
    }

    /// <summary>
    /// Decides one punch for <paramref name="employeeId"/> (the employee the punch is recorded against).
    /// <list type="bullet">
    ///   <item><see cref="PunchChannel.OnBehalf"/>: no selfie, no geofence (the location is the operator's) — recorded None.</item>
    ///   <item><see cref="PunchChannel.Kiosk"/>: the geofence applies; no selfie is asked for or accepted — recorded None.</item>
    ///   <item><see cref="PunchChannel.SelfWeb"/> under an enforced geofence: refused, the app is required.</item>
    ///   <item>Self punches: evidence must exist in this tenant, belong to that employee, be Active, unused and
    ///     unexpired, under active consent with the flag on. Without evidence a consenting employee is refused only when
    ///     the tenant requires the selfie for consenting employees; a non-consenting employee is never asked.</item>
    /// </list>
    /// </summary>
    public async Task<PunchVerificationDecision> EvaluatePunchAsync(
        Guid tenantId, int employeeId, Guid? evidenceId, PunchLocation location, PunchChannel channel, CancellationToken ct)
    {
        var hasEvidence = evidenceId is Guid given && given != Guid.Empty;
        if (channel == PunchChannel.OnBehalf)
            return hasEvidence ? PunchVerificationDecision.Refuse(AttendanceRefusals.EvidenceNotAccepted)
                : new PunchVerificationDecision(null, PunchVerification.Unverified);

        var policy = await GetPolicyAsync(tenantId, ct);
        if (channel == PunchChannel.Kiosk)
        {
            if (hasEvidence) return PunchVerificationDecision.Refuse(AttendanceRefusals.EvidenceNotAccepted);
            var kioskGeo = await CheckGeofenceAsync(tenantId, employeeId, location, policy, ct, PunchChannel.Kiosk);
            if (kioskGeo.Refusal is not null) return PunchVerificationDecision.Refuse(kioskGeo.Refusal);
            // Recorded None: the kiosk's position is checked, but nothing verifies that THIS employee was present.
            return new PunchVerificationDecision(null, new PunchVerification(AttendanceVerificationMethods.None, null,
                kioskGeo.Site, kioskGeo.Distance, kioskGeo.FellBackToAllSites, false));
        }

        if (channel == PunchChannel.SelfWeb && policy.GeofenceEnforced)
            return PunchVerificationDecision.Refuse(AttendanceRefusals.MobileAppRequired);

        var selfie = false;
        string? waivedReason = null;
        if (hasEvidence)
        {
            var id = evidenceId!.Value;
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
            // The server failing must never block attendance (review 2, item 7), and a waiver is one specific failed
            // attempt (review 3): an unused, uncancelled server failure of THIS employee from the last 10 minutes, at most
            // two a day. This is the pre-check; the punch's own transaction re-checks and consumes it under the
            // per-employee advisory lock (AttendanceService), so two concurrent punches cannot both use it.
            // The legacy route writes its punch without the waiver step, so it never uses one.
            if (channel == PunchChannel.SelfMobileLegacy) return PunchVerificationDecision.Refuse(AttendanceRefusals.SelfieRequired);
            var nowUtc = DateTime.UtcNow;
            var waiver = await SelfieWaivers.FindOpenAsync(_db, tenantId, employeeId, nowUtc, ct);
            if (waiver is null) return PunchVerificationDecision.Refuse(AttendanceRefusals.SelfieRequired);
            if (await SelfieWaivers.CapReachedAsync(_db, tenantId, employeeId, nowUtc, ct))
                return PunchVerificationDecision.Refuse(AttendanceRefusals.SelfieRequiredTryAgain);
            waivedReason = SelfieWaivers.Describe(waiver.FailedReason);
        }

        var geo = await CheckGeofenceAsync(tenantId, employeeId, location, policy, ct, channel);
        if (geo.Refusal is not null) return PunchVerificationDecision.Refuse(geo.Refusal);

        return new PunchVerificationDecision(null, new PunchVerification(
            // A waived punch is recorded None: nothing about the employee was verified, whatever the geofence said.
            waivedReason is not null ? AttendanceVerificationMethods.None : AttendanceVerificationMethods.From(selfie, geo.Verified),
            selfie ? evidenceId : null,
            geo.Site,
            geo.Distance,
            geo.FellBackToAllSites,
            geo.MockDetectionUnavailable,
            geo.ClientPlatform,
            waivedReason));
    }

    /// <summary>The evidence rule, shared by the pre-check and the re-check inside the punch transaction.</summary>
    public static AttendanceRefusal? EvidenceRefusal(
        int? ownerEmployeeId, DateTime? usedAtUtc, string? purgeState, DateTime? expiresAtUtc, int punchEmployeeId, DateTime nowUtc)
    {
        // Another employee's id answers exactly like a missing one: the caller learns nothing about it.
        if (ownerEmployeeId is null || ownerEmployeeId != punchEmployeeId) return AttendanceRefusals.EvidenceNotFound;
        if (usedAtUtc is not null) return AttendanceRefusals.EvidenceUsed;
        // Pending (upload not confirmed) and Purged are both unusable: take a new selfie.
        if (purgeState != AttendanceEvidencePurgeStates.Active || expiresAtUtc is null || expiresAtUtc <= nowUtc)
            return AttendanceRefusals.EvidenceExpired;
        return null;
    }
}

/// <summary>
/// The server-failure waiver (review 3). A waiver is ONE specific failed upload attempt: an <c>attendance_evidence</c>
/// row with <see cref="AttendanceEvidence.FailedReason"/> set, which the upload sets only when the failure was the
/// server's (busy, storage) and the employee had no other attempt in flight. It is open while it is unused, not
/// cancelled by a later successful upload, and younger than <see cref="Window"/>. At most <see cref="DailyCap"/> are
/// used per employee per tenant-local calendar day. <c>attendance_audit_logs</c> are never read to decide any of it.
/// </summary>
public static class SelfieWaivers
{
    /// <summary>A waiver can be used within this long of the failed attempt (the evidence lifetime).</summary>
    public static readonly TimeSpan Window = AttendanceVerificationService.EvidenceLifetime;
    /// <summary>Waivers one employee may use per tenant-local calendar day.</summary>
    public const int DailyCap = 2;
    /// <summary>
    /// Another Pending attempt younger than this is "in flight": a new upload is refused while it lasts. Kept above
    /// <c>AttendanceEvidenceController.UploadDeadline</c> (45 s), so an upload is cut off before its reservation stops
    /// counting as in flight (asserted by a test).
    /// </summary>
    public static readonly TimeSpan InFlight = TimeSpan.FromSeconds(60);

    /// <summary>
    /// When a busy or storage failure is judged for a waiver, ANY not-failed Pending attempt of the employee younger than
    /// this counts as "another attempt in flight", so the failure waives nothing. Wider than <see cref="InFlight"/> on
    /// purpose: an attempt still Pending after 60 s has not finished (it may be stalled, or its process died), and the
    /// 1-hour sweeper (<see cref="SelfieEvidenceRetention.AbandonedPending"/>) is what closes it.
    /// </summary>
    public static readonly TimeSpan FailureInFlightLookback = SelfieEvidenceRetention.AbandonedPending;
    /// <summary>The raw event's <c>PhotoReference</c> prefix of a waived punch (reserved: integrations may not write it).</summary>
    public const string PhotoReferencePrefix = "waiver:";

    /// <summary>The employee's open waiver (newest first), or null. Pass <paramref name="tracked"/> inside the punch transaction.</summary>
    public static Task<AttendanceEvidence?> FindOpenAsync(ZayraDbContext db, Guid tenantId, int employeeId, DateTime nowUtc,
        CancellationToken ct, bool tracked = false)
    {
        var since = nowUtc - Window;
        var rows = tracked ? db.AttendanceEvidence : db.AttendanceEvidence.AsNoTracking();
        return rows
            .Where(e => e.TenantId == tenantId && e.EmployeeId == employeeId && e.CreatedAtUtc > since
                        && (e.FailedReason == SelfieUploadFailureReasons.Busy || e.FailedReason == SelfieUploadFailureReasons.Storage)
                        && e.WaiverConsumedAtUtc == null && e.WaiverCancelledAtUtc == null)
            .OrderByDescending(e => e.CreatedAtUtc).ThenBy(e => e.Id)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Whether the employee has used <see cref="DailyCap"/> waivers on the tenant-local day of <paramref name="nowUtc"/>.</summary>
    public static async Task<bool> CapReachedAsync(ZayraDbContext db, Guid tenantId, int employeeId, DateTime nowUtc, CancellationToken ct) =>
        await UsedTodayAsync(db, tenantId, employeeId, nowUtc, ct) >= DailyCap;

    /// <summary>How many waivers the employee used on the tenant-local day of <paramref name="nowUtc"/>.</summary>
    public static async Task<int> UsedTodayAsync(ZayraDbContext db, Guid tenantId, int employeeId, DateTime nowUtc, CancellationToken ct)
    {
        var (start, end) = await TodayAsync(db, tenantId, nowUtc, ct);
        // A waiver is used within Window of its attempt, so created_at_utc bounds the scan (ix_attendance_evidence__employee_created).
        var createdFrom = start - Window;
        return await db.AttendanceEvidence.AsNoTracking()
            .CountAsync(e => e.TenantId == tenantId && e.EmployeeId == employeeId && e.CreatedAtUtc >= createdFrom && e.CreatedAtUtc < end
                             && e.WaiverConsumedAtUtc >= start && e.WaiverConsumedAtUtc < end, ct);
    }

    /// <summary>The tenant-local calendar day containing <paramref name="nowUtc"/>, as a UTC [start, end) window.</summary>
    public static async Task<(DateTime Start, DateTime End)> TodayAsync(ZayraDbContext db, Guid tenantId, DateTime nowUtc, CancellationToken ct)
    {
        var tz = TenantTimeZone.FromId(await db.TenantLocalizationSettings.AsNoTracking()
            .Where(l => l.TenantId == tenantId).Select(l => l.DefaultTimezone).FirstOrDefaultAsync(ct));
        var today = TenantTimeZone.LocalDate(tz, nowUtc);
        return (TenantTimeZone.LocalDayStartUtc(tz, today), TenantTimeZone.LocalDayStartUtc(tz, today.AddDays(1)));
    }

    /// <summary>The plain reason recorded with a waived punch.</summary>
    public static string Describe(string? failedReason) => failedReason == SelfieUploadFailureReasons.Busy
        ? "The selfie service was busy, so the required selfie was waived for this punch."
        : "Selfie storage failed, so the required selfie was waived for this punch.";

    /// <summary>
    /// Inside the punch transaction (the caller's), under the per-employee advisory lock: re-checks the waiver rules and
    /// marks the open waiver used by <paramref name="raw"/> (staged for the caller's single save, with the raw event).
    /// Throws <see cref="AttendanceRefusalException"/> when no waiver is open any more, or the daily cap is reached —
    /// so of two concurrent punches only one can use a waiver.
    /// </summary>
    public static async Task<AttendanceEvidence> ConsumeAsync(ZayraDbContext db, Guid tenantId, int employeeId, AttendanceRawEvent raw,
        DateTime nowUtc, CancellationToken ct)
    {
        await Zayra.Api.Controllers.SelfieConsentLock.AcquireInCurrentTransactionAsync(db, tenantId, employeeId, ct);
        var waiver = await FindOpenAsync(db, tenantId, employeeId, nowUtc, ct, tracked: true)
                     ?? throw new AttendanceRefusalException(AttendanceRefusals.SelfieRequired);
        if (await CapReachedAsync(db, tenantId, employeeId, nowUtc, ct))
            throw new AttendanceRefusalException(AttendanceRefusals.SelfieRequiredTryAgain);
        waiver.WaiverConsumedAtUtc = nowUtc;
        waiver.WaiverRawEventId = raw.Id;
        raw.VerificationMethod = AttendanceVerificationMethods.None;
        raw.PhotoReference = PhotoReferencePrefix + waiver.Id;
        return waiver;
    }
}
