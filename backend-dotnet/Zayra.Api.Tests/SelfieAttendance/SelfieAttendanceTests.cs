using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Filters;
using Zayra.Api.Infrastructure.Modules;
using Zayra.Api.Infrastructure.Retention;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;
using Zayra.Api.Tests.Platform;

namespace Zayra.Api.Tests.SelfieAttendance;

/// <summary>
/// Selfie attendance + server-side geofence, v2 — the contract's required tests (CONTRACT.md "Tests"), with realistic
/// callers (<see cref="SelfieWorld"/>). The refusals that compile against main live in
/// <see cref="SelfieAttendanceRefusalOnMainTests"/>; these need v2's own types.
/// </summary>
public class SelfieAttendanceTests : PlatformTestBase
{
    // ── Flag off ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FlagOff_Upload_IsRefused_AndNothingIsStored()
    {
        var w = await SelfieWorld.CreateAsync();
        var result = await w.UploadAsync(await w.EmployeeAsync(w.Caller, w.CallerUserId), SelfieJpeg());

        Assert.Equal(403, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Equal("selfie_not_enabled", SelfieWorld.CodeOf(result));
        Assert.Empty(w.Db.AttendanceEvidence);
        Assert.Empty(w.Storage.Objects);
    }

    [Fact]
    public async Task FlagOff_AnEnabledRowWithoutSignOffs_StillCountsAsOff()
    {
        // A row written by any path other than the platform endpoint, without the two sign-offs, is treated as OFF.
        var w = await SelfieWorld.CreateAsync();
        await w.SetFlagAsync(SelfieWorld.SelfieKey, true, null);
        Assert.False((await w.Verification.GetPolicyAsync(w.TenantId, default)).SelfieEnabled);
    }

    [Fact]
    public async Task FlagOff_PunchWithAnEvidenceId_IsRefused()
    {
        var w = await SelfieWorld.CreateAsync();
        await EnableSelfieAsync(w);
        var evidenceId = await UploadAsync(w);
        await w.SetFlagAsync(SelfieWorld.SelfieKey, false, SelfieWorld.SignedOffConfig());
        var c = w.Attendance(await w.EmployeeAsync(w.Caller, w.CallerUserId));

        var result = await c.MobilePunch(new WebPunchRequest(0, "In", null, null, null, EvidenceId: evidenceId), default);

        Assert.Equal("selfie_not_enabled", SelfieWorld.CodeOf(result.Result));
        Assert.Empty(w.Db.AttendanceRawEvents);
        Assert.Null((await w.Db.AttendanceEvidence.SingleAsync()).UsedAtUtc);
    }

    [Fact]
    public async Task FlagOff_TheOptInGuard_ClosesTheUploadRoute_ButNotWithdrawal()
    {
        var w = await SelfieWorld.CreateAsync();
        var user = await w.EmployeeAsync(w.Caller, w.CallerUserId);
        var modules = new TenantModuleService(w.Db, new MemoryCache(new MemoryCacheOptions()));

        Assert.Equal(FeatureKeys.SelfieAttendance, OptInFeatures.ResolveApiPath("/api/attendance/evidence/selfie"));
        Assert.Null(OptInFeatures.ResolveApiPath("/api/ess/biometric-consent/withdraw"));
        var blocked = await RunGuardAsync(modules, user, "/api/attendance/evidence/selfie");
        Assert.Equal(403, Assert.IsType<ObjectResult>(blocked).StatusCode);
        Assert.Null(await RunGuardAsync(modules, user, "/api/ess/biometric-consent/withdraw"));
    }

    // ── Platform flag: sign-offs, audit, bulk ─────────────────────────────────────────────────

    [Fact]
    public async Task Platform_WithBothSignOffs_EnablesSelfieAttendance_AndAuditsTheSignOffs()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "T", Slug = $"t-{tenantId:N}" });
        await db.SaveChangesAsync();
        var ownerId = Guid.NewGuid();
        var controller = await SelfieWorld.AsPlatformOwnerAsync(CreateController(db), db, ownerId);

        var result = await controller.SetFeatureFlag(tenantId, FeatureKeys.SelfieAttendance, new SetFeatureFlagRequest(true, SelfieWorld.EnableRequestConfig(ownerId)), default);

        Assert.IsType<OkObjectResult>(result);
        var enabled = await db.TenantFeatureFlags.SingleAsync(f => f.FeatureKey == FeatureKeys.SelfieAttendance);
        Assert.True(enabled.IsEnabled);
        // The server stamped the actor, the time and where storage really is.
        Assert.Contains($"\"confirmedBy\":\"{ownerId}\"", enabled.ConfigJson);
        Assert.Contains($"\"storageLocation\":\"{SelfieWorld.ResidentKsaLocation}\"", enabled.ConfigJson);
        Assert.Empty(SelfieAttendanceConfig.MissingSignOffs(enabled.ConfigJson));
        var audit = await db.AdminAuditLogs.SingleAsync(a => a.EntityType == "FeatureFlag");
        Assert.Equal("FeatureEnabled", audit.Action);
        Assert.Contains("DPIA-2026-007", audit.NewValuesJson);
        Assert.Contains("\"region\":\"KSA\"", audit.NewValuesJson);

        // Disabling is always allowed, keeps the recorded sign-offs, and is audited with them.
        Assert.IsType<OkObjectResult>(await controller.SetFeatureFlag(tenantId, FeatureKeys.SelfieAttendance, new SetFeatureFlagRequest(false, null), default));
        var flag = await db.TenantFeatureFlags.SingleAsync(f => f.FeatureKey == FeatureKeys.SelfieAttendance);
        Assert.False(flag.IsEnabled);
        Assert.Contains("DPIA-2026-007", flag.ConfigJson);
        Assert.Contains("DPIA-2026-007", (await db.AdminAuditLogs.SingleAsync(a => a.Action == "FeatureDisabled")).NewValuesJson);
    }

    [Fact]
    public async Task Platform_BulkEnable_OfSelfieAttendance_IsRefused()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "T", Slug = $"t-{tenantId:N}", IsActive = true });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var result = await controller.BulkSetFeatureFlag(new BulkFeatureFlagRequest([tenantId], false, FeatureKeys.SelfieAttendance, true, SelfieWorld.SignedOffConfig()), default);

        Assert.Equal("selfie_bulk_enable_refused", SelfieWorld.CodeOf(Assert.IsType<UnprocessableEntityObjectResult>(result)));
        Assert.Empty(db.TenantFeatureFlags);
    }

    [Fact]
    public async Task Platform_GeofenceConfig_IsValidated()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "T", Slug = $"t-{tenantId:N}" });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        Assert.IsType<BadRequestObjectResult>(await controller.SetFeatureFlag(tenantId, FeatureKeys.PunchGeofence,
            new SetFeatureFlagRequest(true, "{\"maxAccuracyMeters\":0}"), default));
        // The geofence is not biometric: no sign-offs needed.
        Assert.IsType<OkObjectResult>(await controller.SetFeatureFlag(tenantId, FeatureKeys.PunchGeofence,
            new SetFeatureFlagRequest(true, "{\"maxAccuracyMeters\":50}"), default));
    }

    [Fact]
    public void BothKeys_AreOptIn_AndRefusedByTheTenantModuleApi()
    {
        Assert.True(OptInFeatures.IsOptIn(FeatureKeys.SelfieAttendance));
        Assert.True(OptInFeatures.IsOptIn(FeatureKeys.PunchGeofence));
        Assert.Null(ModuleCatalog.TryGet(FeatureKeys.SelfieAttendance));
        Assert.True(ModuleCatalog.NonModuleKeys.ContainsKey(FeatureKeys.SelfieAttendance));
        Assert.True(ModuleCatalog.NonModuleKeys.ContainsKey(FeatureKeys.PunchGeofence));
        var state = TenantModuleService.Build([], "SA");
        Assert.False(state.IsEnabled(FeatureKeys.SelfieAttendance));
        Assert.False(state.IsEnabled(FeatureKeys.PunchGeofence));
    }

    // ── Geofence on ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Supported")]
    [InlineData("Unsupported")]
    [InlineData(null)]
    public async Task Geofence_MockedLocation_IsRefused(string? mockDetection)
    {
        var w = await SelfieWorld.CreateAsync();
        await w.AddSiteAsync();
        await w.EnforceGeofenceAsync();
        var c = w.Attendance(await w.EmployeeAsync(w.Caller, w.CallerUserId));

        var result = await c.MobilePunch(new WebPunchRequest(0, "In", null, SelfieWorld.SiteLat, SelfieWorld.SiteLon, AccuracyMeters: 10, LocationMocked: true, MockDetection: mockDetection), default);

        Assert.Equal("location_mocked", SelfieWorld.CodeOf(result.Result));
        Assert.Empty(w.Db.AttendanceRawEvents);
    }

    [Theory]
    [InlineData("Supported", 150)]
    [InlineData("Unsupported", 150)]
    [InlineData("Supported", null)]
    [InlineData("Unsupported", null)]
    public async Task Geofence_PoorOrMissingAccuracy_IsRefused(string mockDetection, int? accuracy)
    {
        var w = await SelfieWorld.CreateAsync();
        await w.AddSiteAsync();
        await w.EnforceGeofenceAsync(maxAccuracy: 100);
        var c = w.Attendance(await w.EmployeeAsync(w.Caller, w.CallerUserId));

        var result = await c.MobilePunch(new WebPunchRequest(0, "In", null, SelfieWorld.SiteLat, SelfieWorld.SiteLon, AccuracyMeters: accuracy,
            LocationMocked: mockDetection == "Supported" ? false : null, MockDetection: mockDetection), default);

        Assert.Equal("location_inaccurate", SelfieWorld.CodeOf(result.Result));
        Assert.Contains("100 m", SelfieWorld.MessageOf(result.Result));
        Assert.Empty(w.Db.AttendanceRawEvents);
    }

    [Fact]
    public async Task Geofence_InsideTheRadius_Passes_AndIsStoredAsGeofenceVerified()
    {
        var w = await SelfieWorld.CreateAsync();
        await w.AddSiteAsync();
        await w.EnforceGeofenceAsync();
        var c = w.Attendance(await w.EmployeeAsync(w.Caller, w.CallerUserId));

        // ~55 m north of the site, well inside 150 m.
        var result = await c.MobilePunch(new WebPunchRequest(0, "In", null, SelfieWorld.SiteLat + 0.0005m, SelfieWorld.SiteLon, AccuracyMeters: 12, LocationMocked: false, MockDetection: "Supported"), default);

        var raw = Assert.IsType<AttendanceRawEvent>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(w.Caller.Id, raw.EmployeeId);
        Assert.Equal(AttendanceVerificationMethods.Geofence, raw.VerificationMethod);
    }

    [Fact]
    public async Task Geofence_MockedLocation_IsAccepted_OnlyWhenTheTenantAllowsIt()
    {
        var w = await SelfieWorld.CreateAsync();
        await w.AddSiteAsync();
        await w.EnforceGeofenceAsync(allowMocked: true);
        var c = w.Attendance(await w.EmployeeAsync(w.Caller, w.CallerUserId));

        var result = await c.MobilePunch(new WebPunchRequest(0, "In", null, SelfieWorld.SiteLat, SelfieWorld.SiteLon, AccuracyMeters: 10, LocationMocked: true, MockDetection: "Supported"), default);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task Geofence_EnforcedWithNoSiteSetUp_IsRefusedWithAPlainReason()
    {
        var w = await SelfieWorld.CreateAsync();
        await w.AddSiteAsync(radius: null); // a Location without a radius is not a geofence
        await w.EnforceGeofenceAsync();
        var c = w.Attendance(await w.EmployeeAsync(w.Caller, w.CallerUserId));

        var result = await c.MobilePunch(new WebPunchRequest(0, "In", null, SelfieWorld.SiteLat, SelfieWorld.SiteLon, AccuracyMeters: 10, LocationMocked: false, MockDetection: "Supported"), default);

        Assert.Equal("geofence_site_missing", SelfieWorld.CodeOf(result.Result));
    }

    [Fact]
    public async Task Geofence_Off_StoresTheLocationAsGiven_AndVerifiesNothing()
    {
        var w = await SelfieWorld.CreateAsync();
        await w.AddSiteAsync();
        var c = w.Attendance(await w.EmployeeAsync(w.Caller, w.CallerUserId));

        var result = await c.WebPunch(new WebPunchRequest(0, "In", "Home", 21.5m, 39.17m, AccuracyMeters: 900, LocationMocked: true), default);

        var raw = Assert.IsType<AttendanceRawEvent>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(21.5m, raw.Latitude);
        Assert.Equal(39.17m, raw.Longitude);
        Assert.Equal(AttendanceVerificationMethods.None, raw.VerificationMethod);
    }

    [Fact]
    public void Haversine_MatchesAKnownDistance()
    {
        // Riyadh (24.7136, 46.6753) to Jeddah (21.5433, 39.1728): ~845 km great-circle.
        var d = AttendanceVerificationService.HaversineMeters(24.7136, 46.6753, 21.5433, 39.1728);
        Assert.InRange(d, 840_000, 850_000);
        Assert.InRange(AttendanceVerificationService.HaversineMeters(24.7136, 46.6753, 24.7146, 46.6753), 110, 112);
    }

    // ── Evidence ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Evidence_ValidSelfie_IsUsedExactlyOnce_AndBoundToThePunch()
    {
        var w = await SelfieWorld.CreateAsync();
        await EnableSelfieAsync(w);
        var evidenceId = await UploadAsync(w);
        var c = w.Attendance(await w.EmployeeAsync(w.Caller, w.CallerUserId));

        var first = await c.MobilePunch(new WebPunchRequest(0, "In", null, null, null, EvidenceId: evidenceId), default);
        var raw = Assert.IsType<AttendanceRawEvent>(Assert.IsType<OkObjectResult>(first.Result).Value);
        Assert.Equal(AttendanceVerificationMethods.Selfie, raw.VerificationMethod);
        var evidence = await w.Db.AttendanceEvidence.SingleAsync();
        Assert.NotNull(evidence.UsedAtUtc);
        Assert.Equal(raw.Id, evidence.UsedByRawEventId);
        Assert.DoesNotContain(evidence.StorageKey, raw.PhotoReference);
        Assert.Contains(w.Db.AttendanceAuditLogs, a => a.Action == "attendance.selfie.punch_with_evidence" && a.MetadataJson.Contains($"\"employeeId\":{w.Caller.Id}"));

        // Reused: refused, and the second punch is not recorded.
        var second = await c.MobilePunch(new WebPunchRequest(0, "Out", null, null, null, EvidenceId: evidenceId), default);
        Assert.Equal("evidence_used", SelfieWorld.CodeOf(second.Result));
        Assert.Single(w.Db.AttendanceRawEvents);
    }

    [Fact]
    public async Task Evidence_SelfieAndGeofence_IsStoredAsBoth()
    {
        var w = await SelfieWorld.CreateAsync();
        await w.AddSiteAsync();
        await w.EnforceGeofenceAsync();
        await EnableSelfieAsync(w);
        var evidenceId = await UploadAsync(w);
        var c = w.Attendance(await w.EmployeeAsync(w.Caller, w.CallerUserId));

        var result = await c.MobilePunch(new WebPunchRequest(0, "In", null, SelfieWorld.SiteLat, SelfieWorld.SiteLon, AccuracyMeters: 8, LocationMocked: false, EvidenceId: evidenceId, MockDetection: "Supported"), default);

        Assert.Equal(AttendanceVerificationMethods.SelfieAndGeofence, Assert.IsType<AttendanceRawEvent>(Assert.IsType<OkObjectResult>(result.Result).Value).VerificationMethod);
    }

    [Fact]
    public async Task Evidence_AnotherEmployeesSelfie_IsRefused_ForThemAndForAManagerPunchingOnTheirBehalf()
    {
        var w = await SelfieWorld.CreateAsync();
        await EnableSelfieAsync(w);
        await ConsentAsync(w, w.Colleague, w.ColleagueUserId);
        var colleaguesSelfie = await UploadAsync(w, w.Colleague, w.ColleagueUserId);

        // The caller punching for themselves with the colleague's id.
        var self = w.Attendance(await w.EmployeeAsync(w.Caller, w.CallerUserId));
        Assert.Equal("evidence_not_found", SelfieWorld.CodeOf((await self.MobilePunch(new WebPunchRequest(0, "In", null, null, null, EvidenceId: colleaguesSelfie), default)).Result));

        // An HR Manager allowed to punch for the colleague (attendance.write) cannot attach any selfie to the colleague's
        // punch — not their own, not the colleague's: kiosk and on-behalf punches never carry one (no buddy-punching).
        await ConsentAsync(w, w.Caller, w.CallerUserId);
        var hrsOwnSelfie = await UploadAsync(w, w.Caller, w.CallerUserId);
        var hr = w.Attendance(await w.RoleAsync("HR Manager", w.Caller, w.CallerUserId));
        Assert.Equal("evidence_not_accepted", SelfieWorld.CodeOf((await hr.KioskPunch(new WebPunchRequest(w.Colleague.Id, "In", null, null, null, EvidenceId: hrsOwnSelfie), default)).Result));
        Assert.Equal("evidence_not_accepted", SelfieWorld.CodeOf((await hr.MobilePunch(new WebPunchRequest(w.Colleague.Id, "In", null, null, null, EvidenceId: colleaguesSelfie), default)).Result));

        Assert.Empty(w.Db.AttendanceRawEvents);
        Assert.All(w.Db.AttendanceEvidence, e => Assert.Null(e.UsedAtUtc));
    }

    [Fact]
    public async Task Evidence_Expired_IsRefused()
    {
        var w = await SelfieWorld.CreateAsync();
        await EnableSelfieAsync(w);
        var evidenceId = await UploadAsync(w);
        var row = await w.Db.AttendanceEvidence.SingleAsync();
        row.CreatedAtUtc = DateTime.UtcNow.AddMinutes(-11);
        row.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        await w.Db.SaveChangesAsync();
        var c = w.Attendance(await w.EmployeeAsync(w.Caller, w.CallerUserId));

        var result = await c.MobilePunch(new WebPunchRequest(0, "In", null, null, null, EvidenceId: evidenceId), default);

        Assert.Equal("evidence_expired", SelfieWorld.CodeOf(result.Result));
        Assert.Empty(w.Db.AttendanceRawEvents);
    }

    [Fact]
    public async Task Evidence_WithoutConsent_IsRefused_BothAtUploadAndAtPunch()
    {
        var w = await SelfieWorld.CreateAsync();
        await w.SetFlagAsync(SelfieWorld.SelfieKey, true, SelfieWorld.SignedOffConfig());
        var user = await w.EmployeeAsync(w.Caller, w.CallerUserId);

        // Upload without consent: refused.
        var upload = await w.UploadAsync(user, SelfieJpeg());
        Assert.Equal("consent_required", SelfieWorld.CodeOf(upload));
        Assert.Empty(w.Db.AttendanceEvidence);

        // Consent, upload, withdraw: the selfie taken under consent can no longer be used. (Storage is down here, so the
        // unused selfie survives the withdrawal's immediate purge and the punch-time consent check is what refuses it.)
        await ConsentAsync(w, w.Caller, w.CallerUserId);
        var evidenceId = await UploadAsync(w);
        w.Storage.FailDeletes = true;
        Assert.IsType<OkObjectResult>(await w.Ess(user).WithdrawConsent(new WithdrawBiometricConsentRequest("Mobile"), default));
        var punch = await w.Attendance(user).MobilePunch(new WebPunchRequest(0, "In", null, null, null, EvidenceId: evidenceId), default);
        Assert.Equal("consent_required", SelfieWorld.CodeOf(punch.Result));
        Assert.Empty(w.Db.AttendanceRawEvents);
    }

    [Fact]
    public async Task Evidence_ClientBiometricClaims_DoNotChangeTheStoredMethod()
    {
        var w = await SelfieWorld.CreateAsync();
        var c = w.Attendance(await w.EmployeeAsync(w.Caller, w.CallerUserId));

        var result = await c.MobilePunch(new WebPunchRequest(0, "In", null, null, null,
            VerificationMethod: "Face", ConfidenceScore: 99.9m, ClientBiometricVerified: true), default);

        var raw = Assert.IsType<AttendanceRawEvent>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(AttendanceVerificationMethods.None, raw.VerificationMethod);
        Assert.Null(raw.ConfidenceScore);
    }

    [Fact]
    public async Task Upload_ReEncodesAndStripsExif_ReturnsAnOpaqueId_AndIsAudited()
    {
        var w = await SelfieWorld.CreateAsync();
        await EnableSelfieAsync(w);
        var source = SelfieJpeg(withGpsExif: true);
        Assert.True(Contains(source, Encoding.ASCII.GetBytes("Exif")));

        var result = await w.UploadAsync(await w.EmployeeAsync(w.Caller, w.CallerUserId), source);

        var created = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(201, created.StatusCode);
        var body = JsonSerializer.SerializeToElement(created.Value);
        var evidenceId = body.GetProperty("evidenceId").GetGuid();
        Assert.False(body.TryGetProperty("storageKey", out _));
        var row = await w.Db.AttendanceEvidence.SingleAsync();
        Assert.Equal(evidenceId, row.Id);
        Assert.Equal(w.Caller.Id, row.EmployeeId);
        // Ten minutes from when the file was stored (the row was reserved a moment earlier, before the body was read).
        Assert.InRange(row.ExpiresAtUtc, row.CreatedAtUtc.AddMinutes(10), row.CreatedAtUtc.AddMinutes(10).AddSeconds(30));
        Assert.Equal(AttendanceEvidencePurgeStates.Active, row.PurgeState);
        var stored = w.Storage.Objects[row.StorageKey];
        Assert.False(Contains(stored, Encoding.ASCII.GetBytes("Exif")), "the stored selfie must carry no EXIF block");
        Assert.False(Contains(stored, Encoding.ASCII.GetBytes("GPSKYNEX")), "the GPS payload must not survive");
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stored)).ToLowerInvariant(), row.Sha256);
        Assert.Contains(w.Db.AttendanceAuditLogs, a => a.Action == "attendance.selfie.uploaded" && a.MetadataJson.Contains($"\"employeeId\":{w.Caller.Id}"));
    }

    [Fact]
    public async Task Upload_IsRateLimitedToTenPerEmployeePerHour()
    {
        var w = await SelfieWorld.CreateAsync();
        await EnableSelfieAsync(w);
        for (var i = 0; i < 10; i++) await UploadAsync(w);
        // A colleague's uploads are not counted against the caller.
        await ConsentAsync(w, w.Colleague, w.ColleagueUserId);
        await UploadAsync(w, w.Colleague, w.ColleagueUserId);

        var eleventh = await w.UploadAsync(await w.EmployeeAsync(w.Caller, w.CallerUserId), SelfieJpeg());

        Assert.Equal(429, Assert.IsAssignableFrom<ObjectResult>(eleventh).StatusCode);
        Assert.Equal("selfie_rate_limited", SelfieWorld.CodeOf(eleventh));
        Assert.Equal(10, await w.Db.AttendanceEvidence.CountAsync(e => e.EmployeeId == w.Caller.Id));
        Assert.Equal(11, w.Storage.Objects.Count);
    }

    [Fact]
    public async Task Upload_FromAKioskOnlyLogin_IsRefused()
    {
        var w = await SelfieWorld.CreateAsync();
        await EnableSelfieAsync(w);
        var kiosk = w.Principal("Kiosk Operator", w.CallerUserId, w.Caller.Id, ["attendance.kiosk"], "KioskOnly");

        Assert.Equal(403, Assert.IsAssignableFrom<ObjectResult>(await w.UploadAsync(kiosk, SelfieJpeg())).StatusCode);
    }

    // ── Consent ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConsentWithdrawn_TheEmployeeStillPunches_WithoutASelfie()
    {
        var w = await SelfieWorld.CreateAsync();
        // Even a tenant that requires the selfie for consenting employees cannot require it of a non-consenting one.
        await w.SetFlagAsync(SelfieWorld.SelfieKey, true, SelfieWorld.SignedOffConfig(requireSelfieForConsented: true));
        var user = await w.EmployeeAsync(w.Caller, w.CallerUserId);
        await ConsentAsync(w, w.Caller, w.CallerUserId);

        // Consenting + required: a plain punch is refused with a plain reason...
        var required = await w.Attendance(user).MobilePunch(new WebPunchRequest(0, "In", null, null, null), default);
        Assert.Equal("selfie_required", SelfieWorld.CodeOf(required.Result));

        // ...and after withdrawal the same punch goes through, with nothing claimed as verified.
        var withdrawn = await w.Ess(user).WithdrawConsent(null, default);
        Assert.IsType<OkObjectResult>(withdrawn);
        Assert.NotNull((await w.Db.BiometricConsents.SingleAsync()).WithdrawnAtUtc);
        var punch = await w.Attendance(user).MobilePunch(new WebPunchRequest(0, "In", null, null, null), default);
        Assert.Equal(AttendanceVerificationMethods.None, Assert.IsType<AttendanceRawEvent>(Assert.IsType<OkObjectResult>(punch.Result).Value).VerificationMethod);
        Assert.Contains(w.Db.AttendanceAuditLogs, a => a.Action == "attendance.biometric_consent.withdrawn" && a.MetadataJson.Contains($"\"employeeId\":{w.Caller.Id}"));
    }

    [Fact]
    public async Task Consent_IsRecordedPerVersion_AndWithdrawalWorksWithTheFeatureOff()
    {
        var w = await SelfieWorld.CreateAsync();
        await EnableSelfieAsync(w);
        var user = await w.EmployeeAsync(w.Caller, w.CallerUserId);
        var ess = w.Ess(user);

        Assert.IsType<ConflictObjectResult>(await ess.GiveConsent(new GiveBiometricConsentRequest("0", "Mobile"), default));
        Assert.Equal(1, await w.Db.BiometricConsents.CountAsync());
        var given = await w.Db.BiometricConsents.SingleAsync();
        Assert.Equal(("1", BiometricConsentChannels.Mobile), (given.PolicyVersion, given.Channel));
        Assert.Contains(w.Db.AttendanceAuditLogs, a => a.Action == "attendance.biometric_consent.given");

        // The tenant bumps its consent text: the old consent no longer counts.
        await w.SetFlagAsync(SelfieWorld.SelfieKey, true, SelfieWorld.SignedOffConfig().Replace("\"consentPolicyVersion\":\"1\"", "\"consentPolicyVersion\":\"2\""));
        var view = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await ess.GetAttendanceVerification(default)).Value);
        Assert.Equal("consent_needed", view.GetProperty("selfie").GetProperty("step").GetString());

        // Switched off by the platform: withdrawal still works.
        await w.SetFlagAsync(SelfieWorld.SelfieKey, false, SelfieWorld.SignedOffConfig());
        Assert.IsType<OkObjectResult>(await ess.WithdrawConsent(null, default));
        Assert.All(w.Db.BiometricConsents, c => Assert.NotNull(c.WithdrawnAtUtc));
        Assert.IsType<ConflictObjectResult>(await ess.GiveConsent(new GiveBiometricConsentRequest("1", "Mobile"), default));
    }

    [Fact]
    public async Task VerificationView_TellsTheAppWhatApplies()
    {
        var w = await SelfieWorld.CreateAsync();
        var ess = w.Ess(await w.EmployeeAsync(w.Caller, w.CallerUserId));

        var off = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await ess.GetAttendanceVerification(default)).Value);
        Assert.False(off.GetProperty("selfie").GetProperty("enabled").GetBoolean());
        Assert.Equal("off", off.GetProperty("selfie").GetProperty("step").GetString());
        Assert.False(off.GetProperty("geofence").GetProperty("enforced").GetBoolean());

        await w.AddSiteAsync();
        await w.EnforceGeofenceAsync(maxAccuracy: 60);
        await EnableSelfieAsync(w);
        var on = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await ess.GetAttendanceVerification(default)).Value);
        Assert.Equal("optional", on.GetProperty("selfie").GetProperty("step").GetString());
        Assert.Equal("1", on.GetProperty("selfie").GetProperty("currentPolicyVersion").GetString());
        Assert.Equal(60, on.GetProperty("geofence").GetProperty("maxAccuracyMeters").GetInt32());
        Assert.Equal("HQ office", on.GetProperty("geofence").GetProperty("sites")[0].GetProperty("Name").GetString());
    }

    // ── Purge ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Purge_UnusedUpload_IsDeletedAfter24Hours_RowAndShaKept_AuditWritten_SecondRunNoOp()
    {
        var w = await SelfieWorld.CreateAsync();
        await EnableSelfieAsync(w);
        await UploadAsync(w);
        var row = await w.Db.AttendanceEvidence.SingleAsync();
        var sha = row.Sha256;
        var key = row.StorageKey;
        var purger = new SelfieEvidencePurger(w.Db, w.Storage);

        // Not yet: 23 hours after upload.
        Assert.Empty(await purger.FindDueAsync(w.TenantId, row.CreatedAtUtc.AddHours(23), 100, default));
        Assert.Equal(SelfieEvidencePurgeOutcome.NotDue, await purger.PurgeOneAsync(w.TenantId, row.Id, row.CreatedAtUtc.AddHours(23), null, default));
        await w.Db.SaveChangesAsync();
        Assert.True(w.Storage.Objects.ContainsKey(key));

        var now = row.CreatedAtUtc.AddHours(25);
        var due = await purger.FindDueAsync(w.TenantId, now, 100, default);
        Assert.Equal([row.Id], due);
        Assert.Equal(SelfieEvidencePurgeOutcome.Purged, await purger.PurgeOneAsync(w.TenantId, row.Id, now, null, default));
        await w.Db.SaveChangesAsync();

        Assert.False(w.Storage.Objects.ContainsKey(key));
        var kept = await w.Db.AttendanceEvidence.SingleAsync();
        Assert.Equal((AttendanceEvidencePurgeStates.Purged, sha), (kept.PurgeState, kept.Sha256));
        Assert.Equal(now, kept.PurgedAtUtc);
        var audit = await w.Db.RetentionPurgeAudits.SingleAsync();
        Assert.Equal((SelfieEvidenceRetention.RuleKey, RetentionOutcomes.Applied, row.Id.ToString()), (audit.RuleKey, audit.Outcome, audit.EntityId));
        Assert.Contains($"\"employeeId\":{w.Caller.Id}", audit.DetailsJson);
        Assert.Contains(w.Db.AttendanceAuditLogs, a => a.Action == "attendance.selfie.purged");

        // A second run is a no-op: nothing due, nothing deleted, no second audit row.
        Assert.Empty(await purger.FindDueAsync(w.TenantId, now.AddDays(1), 100, default));
        Assert.Equal(SelfieEvidencePurgeOutcome.AlreadyPurged, await purger.PurgeOneAsync(w.TenantId, row.Id, now.AddDays(1), null, default));
        await w.Db.SaveChangesAsync();
        Assert.Single(w.Db.RetentionPurgeAudits);
        Assert.Single(w.Storage.Deleted);
    }

    [Fact]
    public async Task Purge_UsedSelfie_Waits90DaysAfterThePayrollMonthLocks()
    {
        var w = await SelfieWorld.CreateAsync();
        var usedAt = new DateTime(2026, 3, 10, 6, 0, 0, DateTimeKind.Utc);
        var row = await SeedUsedEvidenceAsync(w, usedAt);
        var lockedAt = new DateTime(2026, 4, 5, 12, 0, 0, DateTimeKind.Utc);
        w.Db.PayrollRuns.Add(new PayrollRun { TenantId = w.TenantId, CompanyId = w.CompanyId, Year = 2026, Month = 3, Status = "Locked", LockedAtUtc = lockedAt });
        await w.Db.SaveChangesAsync();
        var purger = new SelfieEvidencePurger(w.Db, w.Storage);

        Assert.Empty(await purger.FindDueAsync(w.TenantId, lockedAt.AddDays(89), 100, default));
        var due = await purger.FindDueAsync(w.TenantId, lockedAt.AddDays(90).AddMinutes(1), 100, default);
        Assert.Equal([row.Id], due);
        Assert.Equal(SelfieEvidencePurgeOutcome.Purged, await purger.PurgeOneAsync(w.TenantId, row.Id, lockedAt.AddDays(90).AddMinutes(1), null, default));
        await w.Db.SaveChangesAsync();
        Assert.Contains("locked on 2026-04-05", (await w.Db.RetentionPurgeAudits.SingleAsync()).Reason);
    }

    [Fact]
    public async Task Purge_UsedSelfie_WithNoLockedRun_IsDeletedAtWorkDatePlus120Days()
    {
        var w = await SelfieWorld.CreateAsync();
        var usedAt = new DateTime(2026, 3, 10, 6, 0, 0, DateTimeKind.Utc);
        var row = await SeedUsedEvidenceAsync(w, usedAt);
        // A voided run and another company's lock do not count.
        w.Db.PayrollRuns.Add(new PayrollRun { TenantId = w.TenantId, CompanyId = w.CompanyId, Year = 2026, Month = 3, Status = "Voided", LockedAtUtc = usedAt.AddDays(5) });
        w.Db.PayrollRuns.Add(new PayrollRun { TenantId = w.TenantId, CompanyId = Guid.NewGuid(), Year = 2026, Month = 3, Status = "Locked", LockedAtUtc = usedAt.AddDays(5) });
        await w.Db.SaveChangesAsync();
        var purger = new SelfieEvidencePurger(w.Db, w.Storage);
        var fallback = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc).AddDays(120);

        Assert.Empty(await purger.FindDueAsync(w.TenantId, fallback.AddHours(-1), 100, default));
        Assert.Equal([row.Id], await purger.FindDueAsync(w.TenantId, fallback.AddHours(1), 100, default));
    }

    [Fact]
    public async Task Purge_AStorageFailure_LeavesTheRowActive_ForTheNextRun()
    {
        var w = await SelfieWorld.CreateAsync();
        await EnableSelfieAsync(w);
        await UploadAsync(w);
        var row = await w.Db.AttendanceEvidence.SingleAsync();
        w.Storage.FailDeletes = true;

        await Assert.ThrowsAsync<IOException>(() => new SelfieEvidencePurger(w.Db, w.Storage).PurgeOneAsync(w.TenantId, row.Id, row.CreatedAtUtc.AddDays(2), null, default));

        Assert.Equal(AttendanceEvidencePurgeStates.Active, (await w.Db.AttendanceEvidence.AsNoTracking().SingleAsync()).PurgeState);
        Assert.Empty(w.Db.RetentionPurgeAudits);
    }

    [Fact]
    public void Purge_DueDateRule()
    {
        var created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var work = new DateOnly(2026, 1, 1);
        Assert.Equal(created.AddHours(24), SelfieEvidenceRetention.DueAtUtc(created, null, null, null));
        Assert.Equal(created.AddDays(120), SelfieEvidenceRetention.DueAtUtc(created, created, work, null));
        Assert.Equal(created.AddDays(30 + 90), SelfieEvidenceRetention.DueAtUtc(created, created, work, created.AddDays(30)));
        Assert.Equal(created.AddDays(110 + 90), SelfieEvidenceRetention.DueAtUtc(created, created, work, created.AddDays(110)));
        // A lock that only arrives after the 120-day fallback does not extend it.
        Assert.Equal(created.AddDays(120), SelfieEvidenceRetention.DueAtUtc(created, created, work, created.AddDays(130)));
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    internal static async Task EnableSelfieAsync(SelfieWorld w)
    {
        await w.SetFlagAsync(SelfieWorld.SelfieKey, true, SelfieWorld.SignedOffConfig());
        await ConsentAsync(w, w.Caller, w.CallerUserId);
    }

    internal static async Task ConsentAsync(SelfieWorld w, Employee employee, Guid userId)
    {
        var result = await w.Ess(await w.EmployeeAsync(employee, userId)).GiveConsent(new GiveBiometricConsentRequest("1", "Mobile"), default);
        Assert.True(result is ObjectResult { StatusCode: 200 or 201 } or OkObjectResult, $"consent was not recorded: {SelfieWorld.MessageOf(result)}");
    }

    internal static Task<Guid> UploadAsync(SelfieWorld w) => UploadAsync(w, w.Caller, w.CallerUserId);

    internal static async Task<Guid> UploadAsync(SelfieWorld w, Employee employee, Guid userId)
    {
        var result = await w.UploadAsync(await w.EmployeeAsync(employee, userId), SelfieJpeg());
        var created = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.True(created.StatusCode == 201, $"upload failed: {SelfieWorld.MessageOf(result)}");
        return JsonSerializer.SerializeToElement(created.Value).GetProperty("evidenceId").GetGuid();
    }

    internal static async Task<AttendanceEvidence> SeedUsedEvidenceAsync(SelfieWorld w, DateTime usedAt)
    {
        var raw = new AttendanceRawEvent { TenantId = w.TenantId, EmployeeId = w.Caller.Id, PunchTimestampUtc = usedAt, PunchDirection = "In" };
        w.Db.AttendanceRawEvents.Add(raw);
        var key = (await w.Storage.SaveAsync(w.TenantId, FormFile([1, 2, 3]), default)).StorageUrl;
        var row = new AttendanceEvidence
        {
            TenantId = w.TenantId, EmployeeId = w.Caller.Id, StorageKey = key, Sha256 = new string('a', 64), ByteSize = 3,
            CreatedAtUtc = usedAt.AddMinutes(-1), ExpiresAtUtc = usedAt.AddMinutes(9), UsedAtUtc = usedAt, UsedByRawEventId = raw.Id,
            PurgeState = AttendanceEvidencePurgeStates.Active,
        };
        w.Db.AttendanceEvidence.Add(row);
        await w.Db.SaveChangesAsync();
        return row;
    }

    internal static IFormFile FormFile(byte[] bytes) =>
        new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "selfie.jpg") { Headers = new HeaderDictionary(), ContentType = "image/jpeg" };

    /// <summary>A real JPEG; optionally with an APP1 EXIF segment carrying a GPS-like payload after SOI.</summary>
    internal static byte[] SelfieJpeg(bool withGpsExif = false)
    {
        using var bitmap = new SKBitmap(64, 48);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(new SKColor(180, 140, 120));
        using var image = SKImage.FromBitmap(bitmap);
        var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 90).ToArray();
        if (!withGpsExif) return jpeg;
        var payload = Encoding.ASCII.GetBytes("Exif\0\0GPSKYNEX lat=24.7136 lon=46.6753");
        var length = payload.Length + 2;
        var app1 = new byte[] { 0xFF, 0xE1, (byte)(length >> 8), (byte)(length & 0xFF) }.Concat(payload).ToArray();
        return jpeg.Take(2).Concat(app1).Concat(jpeg.Skip(2)).ToArray();
    }

    internal static bool Contains(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return true;
        return false;
    }

    /// <summary>Runs the global FeatureFlagGuardFilter for a path; returns the short-circuit result, or null when it let the request through.</summary>
    private static async Task<IActionResult?> RunGuardAsync(ITenantModuleService modules, System.Security.Claims.ClaimsPrincipal user, string path)
    {
        var http = new DefaultHttpContext { User = user };
        http.Request.Path = path;
        var ctx = new ActionExecutingContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), [], new Dictionary<string, object?>(), new object());
        var guard = new FeatureFlagGuardFilter(modules, NullLogger<FeatureFlagGuardFilter>.Instance);
        await guard.OnActionExecutionAsync(ctx, () => Task.FromResult(new ActionExecutedContext(ctx, [], new object())));
        return ctx.Result;
    }
}
