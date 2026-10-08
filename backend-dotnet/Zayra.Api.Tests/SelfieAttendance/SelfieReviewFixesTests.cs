using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Controllers;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Models;
using Zayra.Api.Tests.Platform;

namespace Zayra.Api.Tests.SelfieAttendance;

/// <summary>
/// Review 1 of selfie attendance v2 (REVIEW-1-DECISIONS.md): the fixes that need the new types. The refusals that can
/// be stated with the reviewed backend's own types are in <see cref="SelfieReviewRefusalOnD43Tests"/>; the ones that need
/// PostgreSQL in <see cref="SelfieReviewFixesPostgresTests"/>; the HTTP pipeline in <see cref="SelfieHttpPipelineTests"/>.
/// </summary>
public class SelfieReviewFixesTests : PlatformTestBase
{
    // ── Item 2: residency ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Item2_Unconfigured_SelfieIsOff_AndTheViewSaysWhyInPlainWords()
    {
        var w = await SelfieWorld.CreateAsync();
        var off = new SelfieWorld
        {
            Db = w.Db, TenantId = w.TenantId, CompanyId = w.CompanyId, CallerUserId = w.CallerUserId, ColleagueUserId = w.ColleagueUserId,
            Caller = w.Caller, Colleague = w.Colleague, Storage = w.Storage, Residency = StorageResidency.Unconfigured,
        };
        await w.SetFlagAsync(SelfieWorld.SelfieKey, true, SelfieWorld.SignedOffConfig());

        var policy = await off.Verification.GetPolicyAsync(w.TenantId, default);
        Assert.False(policy.SelfieEnabled);
        Assert.Contains("Storage:ResidencyAllowList:KSA", policy.SelfieOffReason);
        var view = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(
            await off.Ess(await off.EmployeeAsync(w.Caller, w.CallerUserId)).GetAttendanceVerification(default)).Value);
        Assert.Equal("off", view.GetProperty("selfie").GetProperty("step").GetString());
        Assert.Contains("No storage location is approved for KSA data", view.GetProperty("selfie").GetProperty("offReason").GetString());
        // Positive control: the same flag on a resident deploy is on.
        Assert.True((await w.Verification.GetPolicyAsync(w.TenantId, default)).SelfieEnabled);
    }

    [Fact]
    public void Item2_TheActualLocation_IsWhatStorageConnectsTo_AndOnlyAListedOneIsResident()
    {
        static StorageResidency Of(string provider, string endpoint, string region, params string[] allowed) => new(new StorageOptions
        {
            Provider = provider, Endpoint = endpoint, Region = region,
            ResidencyAllowList = new(StringComparer.OrdinalIgnoreCase) { ["KSA"] = allowed },
        });

        Assert.True(Of("s3", "", "me-central-1", "me-central-1").Check("KSA").Resident);
        // The default region is us-east-1 (what the S3 client really uses for "auto" without an endpoint).
        Assert.Equal("us-east-1", Of("s3", "", "auto", "me-central-1").ActualLocation);
        Assert.False(Of("s3", "", "auto", "me-central-1").Check("KSA").Resident);
        // With an endpoint, the endpoint decides; a KSA-looking region name only signs requests.
        var b2 = Of("s3", "https://s3.us-east-005.backblazeb2.com", "me-central-1", "me-central-1");
        Assert.Equal("s3.us-east-005.backblazeb2.com", b2.ActualLocation);
        Assert.False(b2.Check("KSA").Resident);
        Assert.True(Of("s3", "https://S3.KSA.example.com/", "auto", "s3.ksa.example.com").Check("KSA").Resident);
        // Empty by default.
        Assert.False(StorageResidency.Unconfigured.Check("KSA").Resident);
        Assert.False(Of("local", "", "auto").Check("KSA").Resident);
    }

    [Fact]
    public async Task Item2_EnablingOnADeployWhoseStorageIsNotListed_IsRefused_NamingWhereStorageIs()
    {
        await using var db = CreateDb();
        var tenantId = await TenantAsync(db);
        var ownerId = Guid.NewGuid();
        var owner = await SelfieWorld.AsPlatformOwnerAsync(CreateController(db), db, ownerId, StorageResidency.Unconfigured);

        var result = await owner.SetFeatureFlag(tenantId, FeatureKeys.SelfieAttendance, new SetFeatureFlagRequest(true, SelfieWorld.EnableRequestConfig(ownerId)), default);

        var refused = Assert.IsType<UnprocessableEntityObjectResult>(result);
        Assert.Equal("selfie_residency_unverified", SelfieWorld.CodeOf(refused));
        Assert.Contains("local", SelfieWorld.MessageOf(refused));
        Assert.False(await db.TenantFeatureFlags.AnyAsync(f => f.IsEnabled));
    }

    // ── Item 7: Owner only, server-stamped ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Item7_TheServerStampsActorTimeAndStorage_OverWhateverTheRequestSent()
    {
        await using var db = CreateDb();
        var tenantId = await TenantAsync(db);
        var ownerId = Guid.NewGuid();
        var owner = await SelfieWorld.AsPlatformOwnerAsync(CreateController(db), db, ownerId);
        var forged = JsonSerializer.Serialize(new
        {
            dpia = new { signedOffBy = ownerId.ToString(), signedOffAtUtc = "2026-09-30T08:00:00Z", reference = "DPIA-2026-019" },
            dataResidency = new { region = "KSA", confirmedBy = Guid.NewGuid().ToString(), confirmedAtUtc = "2026-02-01T00:00:00Z", storageLocation = "me-central-1" },
            enabledBy = Guid.NewGuid().ToString(), enabledAtUtc = "2026-02-01T00:00:00Z",
        });
        var before = DateTime.UtcNow.AddSeconds(-1);

        Assert.IsType<OkObjectResult>(await owner.SetFeatureFlag(tenantId, FeatureKeys.SelfieAttendance, new SetFeatureFlagRequest(true, forged), default));

        var stored = JsonDocument.Parse((await db.TenantFeatureFlags.SingleAsync()).ConfigJson!).RootElement;
        Assert.Equal(ownerId.ToString(), stored.GetProperty("dataResidency").GetProperty("confirmedBy").GetString());
        Assert.Equal(ownerId.ToString(), stored.GetProperty("enabledBy").GetString());
        Assert.True(stored.GetProperty("enabledAtUtc").GetDateTime().ToUniversalTime() >= before);
        Assert.Equal("s3.ksa-region.example.test", stored.GetProperty("dataResidency").GetProperty("storageLocation").GetString());
        Assert.Equal("DPIA-2026-019", stored.GetProperty("dpia").GetProperty("reference").GetString());
    }

    [Fact]
    public async Task Item7_AnAdmin_IsRefusedWithAPlainCode_AndTheRefusalsNameTheBadFields()
    {
        await using var db = CreateDb();
        var tenantId = await TenantAsync(db);
        var adminId = Guid.NewGuid();
        var admin = await SelfieWorld.AsPlatformOwnerAsync(CreateController(db), db, adminId, role: PlatformRoles.Admin);
        var refused = await admin.SetFeatureFlag(tenantId, FeatureKeys.SelfieAttendance, new SetFeatureFlagRequest(true, SelfieWorld.EnableRequestConfig(adminId)), default);
        Assert.Equal(403, Assert.IsAssignableFrom<ObjectResult>(refused).StatusCode);
        Assert.Equal("selfie_owner_only", SelfieWorld.CodeOf(refused));

        var ownerId = Guid.NewGuid();
        var owner = await SelfieWorld.AsPlatformOwnerAsync(CreateController(db), db, ownerId);
        var junk = await owner.SetFeatureFlag(tenantId, FeatureKeys.SelfieAttendance,
            new SetFeatureFlagRequest(true, SelfieWorld.EnableRequestConfig(Guid.NewGuid(), reference: "DPIA-7", signedOffAtUtc: "2025-12-31T23:59:00Z")), default);
        var body = JsonSerializer.SerializeToElement(Assert.IsType<UnprocessableEntityObjectResult>(junk).Value);
        var missing = body.GetProperty("missing").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Contains("dpia.reference", missing);
        Assert.Contains("dpia.signedOffAtUtc", missing);
        Assert.Contains("dpia.signedOffBy", missing); // a well-formed id of nobody
        Assert.False(await db.TenantFeatureFlags.AnyAsync(f => f.IsEnabled));
    }

    // ── Item 1: the strict delete in the S3 adapter ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Item1_StrictDelete_RemovesEveryVersion_WhereThePlainDeleteLeftThemAll()
    {
        var tenant = Guid.NewGuid();
        var key = $"{tenant:N}/attendance-evidence/a.jpg";
        var s3 = new VersionedS3();
        s3.Versions[key] = ["v1", "v2", "v3"];
        var storage = S3(s3);

        Assert.True(await storage.TryDeleteAsync(tenant, key));
        Assert.Equal(3, s3.Versions[key].Count); // a delete marker hid it; every version is still stored

        await storage.DeleteStrictAsync(tenant, key);
        Assert.Empty(s3.Versions[key]);
        await storage.DeleteStrictAsync(tenant, key); // already gone: success, re-runnable
    }

    [Fact]
    public async Task Item1_StrictDelete_WithoutVersionListing_DeletesThenVerifies_AndFailsIfAnythingRemains()
    {
        var tenant = Guid.NewGuid();
        var key = $"{tenant:N}/attendance-evidence/b.jpg";
        var s3 = new VersionedS3 { CanListVersions = false, DeleteDoesNothing = true };
        s3.Versions[key] = ["v1"];

        await Assert.ThrowsAsync<DocumentDeletionNotConfirmedException>(() => S3(s3).DeleteStrictAsync(tenant, key));

        s3.DeleteDoesNothing = false;
        await S3(s3).DeleteStrictAsync(tenant, key);
        Assert.Empty(s3.Versions[key]);
    }

    [Fact]
    public async Task Item1_StrictDelete_PropagatesA403_TreatsA404AsGone_AndRefusesAnotherTenantsKey()
    {
        var tenant = Guid.NewGuid();
        var key = $"{tenant:N}/attendance-evidence/c.jpg";
        var s3 = new VersionedS3 { DeleteError = HttpStatusCode.Forbidden };
        s3.Versions[key] = ["v1"];
        Assert.Equal(HttpStatusCode.Forbidden, (await Assert.ThrowsAsync<AmazonS3Exception>(() => S3(s3).DeleteStrictAsync(tenant, key))).StatusCode);
        Assert.Single(s3.Versions[key]);

        s3.DeleteError = HttpStatusCode.NotFound;
        s3.Versions[key].Clear();
        s3.ListAfterDelete = [];
        s3.Versions[key].Add("ghost");
        await S3(s3).DeleteStrictAsync(tenant, key); // the version vanished between list and delete: gone is gone

        await Assert.ThrowsAsync<InvalidOperationException>(() => S3(s3).DeleteStrictAsync(Guid.NewGuid(), key));
    }

    // ── Item 3 / 5: the upload ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Item3_ARateLimitedEmployee_IsRefusedBeforeTheBodyIsRead()
    {
        var w = await SelfieWorld.CreateAsync();
        await SelfieAttendanceTests.EnableSelfieAsync(w);
        for (var i = 0; i < 10; i++) await SeedAttemptAsync(w, DateTime.UtcNow.AddMinutes(-30));

        var result = await w.UploadAsync(await w.EmployeeAsync(w.Caller, w.CallerUserId), "not an image at all"u8.ToArray());

        Assert.Equal(429, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Equal("selfie_rate_limited", SelfieWorld.CodeOf(result));
        Assert.Equal(10, await w.Db.AttendanceEvidence.CountAsync());
    }

    [Fact]
    public async Task Item3_EveryAttemptCounts_EvenOnesThatFailed()
    {
        var w = await SelfieWorld.CreateAsync();
        await SelfieAttendanceTests.EnableSelfieAsync(w);
        var user = await w.EmployeeAsync(w.Caller, w.CallerUserId);
        for (var i = 0; i < 10; i++)
            Assert.Equal("selfie_invalid", SelfieWorld.CodeOf(await w.UploadAsync(user, "junk"u8.ToArray())));

        var eleventh = await w.UploadAsync(user, SelfieAttendanceTests.SelfieJpeg());

        Assert.Equal("selfie_rate_limited", SelfieWorld.CodeOf(eleventh));
        Assert.All(w.Db.AttendanceEvidence, e => Assert.Equal(AttendanceEvidencePurgeStates.Pending, e.PurgeState));
        Assert.Empty(w.Storage.Objects);
    }

    [Fact]
    public async Task Item3_WhenEveryImageSlotIsBusy_TheUploadIsAnswered429AtOnce()
    {
        var w = await SelfieWorld.CreateAsync();
        var busy = new SelfieWorld
        {
            Db = w.Db, TenantId = w.TenantId, CompanyId = w.CompanyId, CallerUserId = w.CallerUserId, ColleagueUserId = w.ColleagueUserId,
            Caller = w.Caller, Colleague = w.Colleague, Storage = w.Storage, Gate = new SelfieImageGate(concurrency: 1),
        };
        await SelfieAttendanceTests.EnableSelfieAsync(busy);
        var user = await busy.EmployeeAsync(w.Caller, w.CallerUserId);
        Assert.True(busy.Gate.TryEnter()); // another upload is decoding

        var controller = busy.Evidence(user);
        controller.Request.Form = new FormCollection(new(), new FormFileCollection { SelfieAttendanceTests.FormFile(SelfieAttendanceTests.SelfieJpeg()) });
        var refused = await controller.UploadSelfie();
        Assert.Equal(429, Assert.IsAssignableFrom<ObjectResult>(refused).StatusCode);
        Assert.Equal("selfie_busy", SelfieWorld.CodeOf(refused));
        Assert.Equal("5", controller.Response.Headers.RetryAfter.ToString());

        busy.Gate.Exit();
        Assert.Equal(201, Assert.IsAssignableFrom<ObjectResult>(await busy.UploadAsync(user, SelfieAttendanceTests.SelfieJpeg())).StatusCode);
    }

    [Fact]
    public async Task Item3_AHugeCanvasPng_IsRefusedFromItsHeader_AndSoIsADecodable20MegapixelOne()
    {
        var w = await SelfieWorld.CreateAsync();
        await SelfieAttendanceTests.EnableSelfieAsync(w);
        var user = await w.EmployeeAsync(w.Caller, w.CallerUserId);

        // 30,000 × 30,000 declared in a 100-byte file: never decoded (3.6 GB of pixels if it were).
        var bomb = await w.UploadAsync(user, HugeCanvasPng(30_000, 30_000), "image/png", "s.png");
        Assert.Equal("selfie_too_large", SelfieWorld.CodeOf(bomb));
        Assert.Contains("16 megapixels", SelfieWorld.MessageOf(bomb));

        var twentyMp = await w.UploadAsync(user, SolidPng(5000, 4000), "image/png", "s.png");
        Assert.Equal("selfie_too_large", SelfieWorld.CodeOf(twentyMp));
        Assert.Empty(w.Storage.Objects);
    }

    [Fact]
    public void Item3_AJpegIsDecodedDownsampled_TowardA1080PxLongEdge()
    {
        using var bitmap = new SKBitmap(4000, 3000);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(SKColors.Teal);
        using var data = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Jpeg, 80);
        using var codec = SKCodec.Create(data);

        var size = ProfilePhotoProcessor.DecodeSize(codec, AttendanceEvidenceController.DecodeLongEdge);

        Assert.InRange(Math.Max(size.Width, size.Height), 1080, 2000); // 1/2 scale: 2000×1500, never the 12 MP original
        var jpeg = ProfilePhotoProcessor.ToSanitisedJpeg(data.ToArray(), AttendanceEvidenceController.MaxSourcePixels, AttendanceEvidenceController.DecodeLongEdge);
        using var output = SKBitmap.Decode(jpeg);
        Assert.Equal(ProfilePhotoProcessor.MaxEdge, Math.Max(output.Width, output.Height));
    }

    [Fact]
    public async Task Item5_TheRowIsReservedPendingAtAKeyFromItsId_AndAFailedStoreLeavesItUnusable()
    {
        var w = await SelfieWorld.CreateAsync();
        await SelfieAttendanceTests.EnableSelfieAsync(w);
        var user = await w.EmployeeAsync(w.Caller, w.CallerUserId);
        w.Storage.FailPuts = true;

        await Assert.ThrowsAsync<IOException>(() => w.UploadAsync(user, SelfieAttendanceTests.SelfieJpeg()));

        var pending = await w.Db.AttendanceEvidence.AsNoTracking().SingleAsync();
        Assert.Equal(AttendanceEvidencePurgeStates.Pending, pending.PurgeState);
        Assert.Equal(w.Storage.TenantKey(w.TenantId, $"attendance-evidence/{pending.Id:N}.jpg"), pending.StorageKey);
        Assert.Null(pending.Sha256);
        // Even if someone learned its id, a Pending row can never back a punch.
        var punch = await w.Attendance(user).MobilePunch(new WebPunchRequest(0, "In", null, null, null, EvidenceId: pending.Id), default);
        Assert.Equal("evidence_expired", SelfieWorld.CodeOf(punch.Result));
        Assert.Empty(w.Db.AttendanceRawEvents);
        // The purge sweeps it an hour after the attempt.
        var purger = new SelfieEvidencePurger(w.Db, w.Storage);
        Assert.Empty(await purger.FindDueAsync(w.TenantId, pending.CreatedAtUtc.AddMinutes(59), 100, default));
        Assert.Equal([pending.Id], await purger.FindDueAsync(w.TenantId, pending.CreatedAtUtc.AddMinutes(61), 100, default));
    }

    // ── Item 8: withdrawal deletes unused selfies ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Item8_Withdrawal_DeletesUnusedSelfiesAtOnce_AndKeepsTheOneAPunchUsed()
    {
        var w = await SelfieWorld.CreateAsync();
        await SelfieAttendanceTests.EnableSelfieAsync(w);
        var user = await w.EmployeeAsync(w.Caller, w.CallerUserId);
        var usedId = await SelfieAttendanceTests.UploadAsync(w);
        Assert.IsType<OkObjectResult>((await w.Attendance(user).MobilePunch(new WebPunchRequest(0, "In", null, null, null, EvidenceId: usedId), default)).Result);
        var unusedId = await SelfieAttendanceTests.UploadAsync(w);
        var pending = await SeedAttemptAsync(w, DateTime.UtcNow.AddMinutes(-2));
        var keys = await w.Db.AttendanceEvidence.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.StorageKey);

        var result = await w.Ess(user).WithdrawConsent(null, default);

        var view = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(2, view.GetProperty("withdrawal").GetProperty("unusedSelfiesDeleted").GetInt32());
        Assert.Equal(0, view.GetProperty("withdrawal").GetProperty("unusedSelfiesAwaitingDeletion").GetInt32());
        var rows = await w.Db.AttendanceEvidence.AsNoTracking().ToDictionaryAsync(e => e.Id);
        Assert.Equal(AttendanceEvidencePurgeStates.Purged, rows[unusedId].PurgeState);
        Assert.Equal(AttendanceEvidencePurgeStates.Purged, rows[pending.Id].PurgeState);
        Assert.False(w.Storage.Objects.ContainsKey(keys[unusedId]));
        // The used selfie backs a pay record: it keeps its retention window, and its file.
        Assert.Equal(AttendanceEvidencePurgeStates.Active, rows[usedId].PurgeState);
        Assert.True(w.Storage.Objects.ContainsKey(keys[usedId]));
        Assert.Equal(2, await w.Db.RetentionPurgeAudits.CountAsync(a => a.Reason.Contains("Consent withdrawn")));
    }

    [Fact]
    public async Task Item8_WhenStorageCannotConfirmTheDelete_TheWithdrawalStillSucceeds_AndThePurgeKeepsTheRow()
    {
        var w = await SelfieWorld.CreateAsync();
        await SelfieAttendanceTests.EnableSelfieAsync(w);
        var user = await w.EmployeeAsync(w.Caller, w.CallerUserId);
        await SelfieAttendanceTests.UploadAsync(w);
        w.Storage.FailDeletes = true;

        var view = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await w.Ess(user).WithdrawConsent(null, default)).Value);

        Assert.Equal(1, view.GetProperty("withdrawal").GetProperty("unusedSelfiesAwaitingDeletion").GetInt32());
        Assert.NotNull((await w.Db.BiometricConsents.AsNoTracking().SingleAsync()).WithdrawnAtUtc);
        Assert.Equal(AttendanceEvidencePurgeStates.Active, (await w.Db.AttendanceEvidence.AsNoTracking().SingleAsync()).PurgeState);
        Assert.Empty(w.Db.RetentionPurgeAudits);
    }

    // ── Item 9: device ingest and CSV import ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Item9_DeviceIngest_RefusesABatchCarryingAReservedLabel_AndStoresNothing()
    {
        var w = await SelfieWorld.CreateAsync();
        var service = new AttendanceService(w.Db, new NullNotifications(), new NullHttpClients());
        var device = new AttendanceDevice { TenantId = w.TenantId, DeviceName = "Gate 1", IsActive = true };
        w.Db.AttendanceDevices.Add(device);
        await w.Db.SaveChangesAsync();
        var key = (await service.GenerateDeviceKeyAsync(w.TenantId, device.Id, new RequestContext("127.0.0.1", "t", null, w.TenantId), default))!.ApiKey;
        var controller = w.With(new AttendanceController(service, new Zayra.Api.Infrastructure.Common.DataScopeService(w.Db),
            new Zayra.Api.Infrastructure.Organization.HrmHierarchyService(w.Db, new NullAudit()), w.Db, w.Verification), new System.Security.Claims.ClaimsPrincipal());
        controller.Request.Headers["X-Device-Key"] = key;
        var at = DateTime.UtcNow.AddHours(-1);

        var refused = await controller.Ingest(new DeviceIngestRequest(
        [
            new DeviceIngestPunch(w.Caller.EmployeeCode, at, "In", "Fingerprint", null, null, null, null, null),
            new DeviceIngestPunch(w.Colleague.EmployeeCode, at, "In", "Selfie+Geofence", null, null, null, null, null),
        ], false), default);

        Assert.Equal("verification_label_reserved", SelfieWorld.CodeOf(Assert.IsType<BadRequestObjectResult>(refused)));
        Assert.Empty(w.Db.AttendanceRawEvents);
        // Positive control: the same batch with an honest label is ingested.
        var accepted = await controller.Ingest(new DeviceIngestRequest(
            [new DeviceIngestPunch(w.Caller.EmployeeCode, at, "In", "Fingerprint", null, null, null, "photo-1.jpg", null)], false), default);
        Assert.IsType<OkObjectResult>(accepted);
        Assert.Single(w.Db.AttendanceRawEvents);
    }

    [Fact]
    public async Task Item9_CsvImport_ARowWithAReservedLabel_IsAFailedRow()
    {
        var w = await SelfieWorld.CreateAsync();
        var service = new AttendanceService(w.Db, new NullNotifications(), new NullHttpClients());
        var at = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var csv = $"employeeCode,punchTimestamp,punchDirection,location,method\n{w.Colleague.EmployeeCode},{at},In,HQ,Geofence\n";

        var batch = await service.ImportCsvAsync(w.TenantId, new ImportAttendanceRequest("p.csv", csv), new RequestContext("127.0.0.1", "t", null, w.TenantId), default);

        Assert.Equal((1, 0), (batch.FailedRows, batch.ImportedRows));
        Assert.Contains(w.Db.AttendanceImportErrors, e => e.ErrorMessage.Contains("can only be recorded by a punch from the KynexOne app"));
    }

    // ── Item 10: geofence ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Item10_SupportedWithoutTheFlag_IsAnOldApp_AndTheViewTellsTheAppWebIsClosed()
    {
        var w = await SelfieWorld.CreateAsync();
        await w.AddSiteAsync();
        await w.EnforceGeofenceAsync();
        var user = await w.EmployeeAsync(w.Caller, w.CallerUserId);

        var result = await w.Attendance(user).MobilePunch(new WebPunchRequest(0, "In", null, SelfieWorld.SiteLat, SelfieWorld.SiteLon, AccuracyMeters: 10, MockDetection: "Supported"), default);

        Assert.Equal("app_update_required", SelfieWorld.CodeOf(result.Result));
        var view = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await w.Ess(user).GetAttendanceVerification(default)).Value);
        Assert.False(view.GetProperty("geofence").GetProperty("webPunchAllowed").GetBoolean());
    }

    [Fact]
    public async Task Item10_HrCanListTheEmployeesNoSiteMatches_WithinTheirScope()
    {
        var w = await SelfieWorld.CreateAsync();
        await w.AddSiteAsync(code: "NORTH");
        var hr = w.Attendance(await w.RoleAsync("HR Manager", w.Caller, w.CallerUserId));

        var body = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await hr.GeofenceUnmatchedEmployees(default)).Value);

        Assert.Equal(2, body.GetProperty("count").GetInt32());
        Assert.Equal([w.Caller.Id, w.Colleague.Id], body.GetProperty("employees").EnumerateArray().Select(e => e.GetProperty("EmployeeId").GetInt32()).Order());
        Assert.Contains("every geofenced site", body.GetProperty("definition").GetString());

        // A site for HQ matches both (WorkLocation "HQ"): nobody is left on the fallback.
        await w.AddSiteAsync(code: "HQ");
        var after = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await hr.GeofenceUnmatchedEmployees(default)).Value);
        Assert.Equal(0, after.GetProperty("count").GetInt32());
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<AttendanceEvidence> SeedAttemptAsync(SelfieWorld w, DateTime createdAt)
    {
        var id = Guid.NewGuid();
        var row = new AttendanceEvidence
        {
            Id = id, TenantId = w.TenantId, EmployeeId = w.Caller.Id, StorageKey = w.Storage.TenantKey(w.TenantId, $"attendance-evidence/{id:N}.jpg"),
            CreatedAtUtc = createdAt, ExpiresAtUtc = createdAt.AddMinutes(10), PurgeState = AttendanceEvidencePurgeStates.Pending,
        };
        w.Db.AttendanceEvidence.Add(row);
        await w.Db.SaveChangesAsync();
        return row;
    }

    private static async Task<Guid> TenantAsync(Zayra.Api.Data.ZayraDbContext db)
    {
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "T", Slug = $"t-{tenantId:N}" });
        await db.SaveChangesAsync();
        return tenantId;
    }

    private static S3DocumentStorage S3(IS3Primitives s3) =>
        new(s3, new StorageOptions { Bucket = "b" }, NullLogger<S3DocumentStorage>.Instance);

    /// <summary>A PNG that DECLARES a <paramref name="width"/>×<paramref name="height"/> canvas in ~100 bytes (header, tiny IDAT, IEND).</summary>
    internal static byte[] HugeCanvasPng(int width, int height)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; ihdr[9] = 2; // 8-bit RGB
        Chunk(ms, "IHDR", ihdr);
        using (var z = new MemoryStream())
        {
            using (var deflate = new ZLibStream(z, CompressionLevel.SmallestSize, leaveOpen: true)) deflate.Write(new byte[64]);
            Chunk(ms, "IDAT", z.ToArray());
        }
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var typeAndData = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        s.Write(typeAndData);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typeAndData));
        s.Write(crc);
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    /// <summary>A real, decodable solid-colour PNG (compresses to a few hundred KB even at 20 MP).</summary>
    internal static byte[] SolidPng(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(new SKColor(200, 160, 140));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>An S3 adapter over a versioned bucket, with switches for the failure modes the strict delete must handle.</summary>
    private sealed class VersionedS3 : IS3Primitives
    {
        public Dictionary<string, List<string>> Versions { get; } = new(StringComparer.Ordinal);
        public bool CanListVersions { get; set; } = true;
        public bool DeleteDoesNothing { get; set; }
        public HttpStatusCode? DeleteError { get; set; }
        public List<string>? ListAfterDelete { get; set; }

        public Task PutAsync(string bucket, string key, Stream content, string contentType, CancellationToken ct) => Task.CompletedTask;
        public Task<byte[]> GetBytesAsync(string bucket, string key, CancellationToken ct) => Task.FromResult(new byte[] { 1 });

        public Task DeleteAsync(string bucket, string key, CancellationToken ct)
        {
            if (DeleteError is { } status) throw new AmazonS3Exception("error") { StatusCode = status };
            // Without a version id: unversioned stores remove it; a versioned bucket only adds a delete marker.
            if (!CanListVersions && !DeleteDoesNothing && Versions.TryGetValue(key, out var v)) v.Clear();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>?> ListVersionIdsAsync(string bucket, string key, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>?>(!CanListVersions ? null : Versions.TryGetValue(key, out var v) ? v.ToList() : []);

        public Task DeleteVersionAsync(string bucket, string key, string versionId, CancellationToken ct)
        {
            if (DeleteError is { } status)
            {
                if (status == HttpStatusCode.NotFound && ListAfterDelete is not null) Versions[key] = ListAfterDelete;
                throw new AmazonS3Exception("error") { StatusCode = status };
            }
            Versions[key].Remove(versionId);
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string bucket, string key, CancellationToken ct) =>
            Task.FromResult(Versions.TryGetValue(key, out var v) && v.Count > 0);
    }
}

/// <summary>Review 1, items 2 and 3, in the production composition: the allow-list binds from configuration, and the image gate is one per process.</summary>
public sealed class SelfieCompositionTests : IClassFixture<SelfieHttpPipelineFixture>
{
    private readonly SelfieHttpPipelineFixture _fx;
    public SelfieCompositionTests(SelfieHttpPipelineFixture fx) => _fx = fx;

    [Fact]
    public void Program_BindsTheResidencyAllowList_AndRegistersOneImageGatePerProcess()
    {
        var services = _fx.Host.Services;
        var residency = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<StorageResidency>(services);
        Assert.True(residency.Check("KSA").Resident); // Storage:ResidencyAllowList:KSA:0 = local, from configuration
        Assert.False(residency.Check("UAE").Resident);

        var gate = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<SelfieImageGate>(services);
        Assert.Same(gate, Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<SelfieImageGate>(services));
        Assert.Equal(SelfieImageGate.DefaultConcurrency, gate.Concurrency);
    }
}
