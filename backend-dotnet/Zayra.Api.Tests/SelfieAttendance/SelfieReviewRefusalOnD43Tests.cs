using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Amazon.S3;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Models;
using Zayra.Api.Tests.Platform;

namespace Zayra.Api.Tests.SelfieAttendance;

/// <summary>
/// Review 1 of selfie attendance v2: the refusals and guarantees that can be stated with the types the reviewed
/// backend (d43b3943) already had, so this file compiles against d43b3943's production code — where every test here
/// FAILS. Each refusal is paired with a positive control so no rule can pass by refusing everyone.
/// </summary>
public class SelfieReviewRefusalOnD43Tests : PlatformTestBase
{
    // ── Item 1: a delete the store did not confirm never marks a selfie Purged ──────────────────────────────

    [Fact]
    public async Task Item1_Purge_WhenS3Answers403_TheRowStaysActive_NoAuditIsWritten_AndTheNextRunPurgesIt()
    {
        var w = await SelfieWorld.CreateAsync();
        var s3 = new ForbiddenThenAllowedS3();
        var storage = new S3DocumentStorage(s3, new StorageOptions { Bucket = "b" }, NullLogger<S3DocumentStorage>.Instance);
        var key = $"{w.TenantId:N}/attendance-evidence/{Guid.NewGuid():N}.jpg";
        s3.Versions[key] = ["v1", "v2"]; // a versioned bucket: two stored versions of the selfie
        var row = await SeedUnusedAsync(w, key);

        try { await new SelfieEvidencePurger(w.Db, storage).PurgeOneAsync(w.TenantId, row.Id, DateTime.UtcNow, null, default); }
        catch (AmazonS3Exception) { /* the strict delete surfaces the 403 */ }
        await w.Db.SaveChangesAsync();

        Assert.Equal("Active", (await w.Db.AttendanceEvidence.AsNoTracking().SingleAsync()).PurgeState);
        Assert.Empty(w.Db.RetentionPurgeAudits);
        Assert.Equal(2, s3.Versions[key].Count);

        // Storage recovers: the next run deletes EVERY version and only then flips the row.
        s3.Forbidden = false;
        w.Db.ChangeTracker.Clear();
        await new SelfieEvidencePurger(w.Db, storage).PurgeOneAsync(w.TenantId, row.Id, DateTime.UtcNow, null, default);
        await w.Db.SaveChangesAsync();
        Assert.Equal("Purged", (await w.Db.AttendanceEvidence.AsNoTracking().SingleAsync()).PurgeState);
        Assert.Empty(s3.Versions[key]);
    }

    [Fact]
    public async Task Item1_Purge_WithAStoreWhoseDeleteReturnsFalse_NeverMarksTheRowPurged()
    {
        var w = await SelfieWorld.CreateAsync();
        var storage = new FalseReturningStorage();
        var key = $"storage/documents/{w.TenantId:N}/attendance-evidence/x.jpg";
        storage.Objects[key] = [1, 2, 3];
        var row = await SeedUnusedAsync(w, key);

        try { await new SelfieEvidencePurger(w.Db, storage).PurgeOneAsync(w.TenantId, row.Id, DateTime.UtcNow, null, default); }
        catch (Exception ex) when (ex is NotSupportedException or IOException) { }
        await w.Db.SaveChangesAsync();

        Assert.Equal("Active", (await w.Db.AttendanceEvidence.AsNoTracking().SingleAsync()).PurgeState);
        Assert.Empty(w.Db.RetentionPurgeAudits);
        Assert.True(storage.Objects.ContainsKey(key));
    }

    // ── Item 2: residency is checked against the deploy, not typed in ───────────────────────────────────────

    [Fact]
    public async Task Item2_ASignedOffFlag_OnADeployWithNoKsaAllowList_IsTreatedAsOff()
    {
        var w = await SelfieWorld.CreateAsync();
        await w.SetFlagAsync(SelfieWorld.SelfieKey, true, SelfieWorld.SignedOffConfig());

        // No storage configuration at all: the allow-list is empty, so nothing is resident.
        var policy = await new AttendanceVerificationService(w.Db).GetPolicyAsync(w.TenantId, default);

        Assert.False(policy.SelfieEnabled);
    }

    // ── Item 6: a "required" selfie never blocks a kiosk or on-behalf punch ─────────────────────────────────

    [Fact]
    public async Task Item6_RequiredSelfie_BlocksOnlyTheEmployeesOwnAppPunch_NeverKioskOrOnBehalf()
    {
        var w = await SelfieWorld.CreateAsync();
        await w.SetFlagAsync(SelfieWorld.SelfieKey, true, SelfieWorld.SignedOffConfig(requireSelfieForConsented: true));
        w.Db.BiometricConsents.AddRange(
            new BiometricConsent { TenantId = w.TenantId, EmployeeId = w.Caller.Id, PolicyVersion = "1", Channel = BiometricConsentChannels.Mobile },
            new BiometricConsent { TenantId = w.TenantId, EmployeeId = w.Colleague.Id, PolicyVersion = "1", Channel = BiometricConsentChannels.Mobile });
        await w.Db.SaveChangesAsync();

        // Positive control: the consenting employee's own app punch without a selfie IS refused, and the message never
        // suggests withdrawing consent to get round it.
        var own = await w.Attendance(await w.EmployeeAsync(w.Caller, w.CallerUserId)).MobilePunch(new WebPunchRequest(0, "In", null, null, null), default);
        Assert.Equal("selfie_required", SelfieWorld.CodeOf(own.Result));
        Assert.DoesNotContain("withdraw", SelfieWorld.MessageOf(own.Result)!, StringComparison.OrdinalIgnoreCase);

        // HR punching for the consenting colleague, on the app route and at the kiosk: recorded, as None.
        var hr = w.Attendance(await w.RoleAsync("HR Manager", w.Caller, w.CallerUserId));
        var onBehalf = await hr.MobilePunch(new WebPunchRequest(w.Colleague.Id, "In", null, null, null), default);
        var kiosk = await hr.KioskPunch(new WebPunchRequest(w.Colleague.Id, "Out", null, null, null), default);

        foreach (var result in new[] { onBehalf, kiosk })
        {
            var raw = Assert.IsType<AttendanceRawEvent>(Assert.IsType<OkObjectResult>(result.Result).Value);
            Assert.Equal(w.Colleague.Id, raw.EmployeeId);
            Assert.Equal("None", raw.VerificationMethod);
        }
    }

    // ── Item 7: only a platform Owner enables, and the sign-off must be real ────────────────────────────────

    [Fact]
    public async Task Item7_APlatformAdmin_CannotEnableSelfieAttendance_ButCanSwitchItOff()
    {
        await using var db = CreateDb();
        var tenantId = await TenantAsync(db);
        var admin = CreateController(db, platformRole: PlatformRoles.Admin);

        var enable = await admin.SetFeatureFlag(tenantId, SelfieWorld.SelfieKey, new SetFeatureFlagRequest(true, SelfieWorld.SignedOffConfig()), default);

        Assert.IsNotType<OkObjectResult>(enable);
        Assert.False(await db.TenantFeatureFlags.AnyAsync(f => f.FeatureKey == SelfieWorld.SelfieKey && f.IsEnabled));
        // Positive control: the kill switch stays with Admin.
        Assert.IsType<OkObjectResult>(await admin.SetFeatureFlag(tenantId, SelfieWorld.SelfieKey, new SetFeatureFlagRequest(false, null), default));
    }

    [Theory]
    [InlineData("someone", "x", "now")]                    // signer not a platform user id, reference not DPIA-YYYY-NNN
    [InlineData("guid", "DPIA-2026-007", "future")]         // signed off in the future
    [InlineData("guid", "DPIA-2026-007", "2019-06-01T00:00:00Z")] // before 2026-01-01
    [InlineData("unknown-guid", "DPIA-2026-007", "now")]    // a well-formed id that is no platform user
    public async Task Item7_JunkSignOffs_AreRefused_EvenFromANamedOwner(string signer, string reference, string signedAt)
    {
        await using var db = CreateDb();
        var tenantId = await TenantAsync(db);
        var ownerId = Guid.NewGuid();
        db.PlatformUsers.Add(new PlatformUser { Id = ownerId, Email = "owner@p.test", FullName = "Owner", PasswordHash = "x", Role = PlatformRoles.Owner });
        await db.SaveChangesAsync();
        var owner = AsNamedOwner(CreateController(db), ownerId);
        var now = DateTime.UtcNow;
        var config = JsonSerializer.Serialize(new
        {
            dpia = new
            {
                signedOffBy = signer switch { "guid" => ownerId.ToString(), "unknown-guid" => Guid.NewGuid().ToString(), _ => signer },
                signedOffAtUtc = signedAt switch { "now" => now.AddMinutes(-5).ToString("O"), "future" => now.AddHours(12).ToString("O"), _ => signedAt },
                reference,
            },
            dataResidency = new { region = "KSA", confirmedBy = "someone", confirmedAtUtc = now.AddMinutes(-5).ToString("O") },
        });

        var result = await owner.SetFeatureFlag(tenantId, SelfieWorld.SelfieKey, new SetFeatureFlagRequest(true, config), default);

        Assert.IsNotType<OkObjectResult>(result);
        Assert.False(await db.TenantFeatureFlags.AnyAsync(f => f.FeatureKey == SelfieWorld.SelfieKey && f.IsEnabled));
    }

    // ── Item 9: other routes cannot write the server's verification labels ──────────────────────────────────

    [Theory]
    [InlineData("Selfie", null)]
    [InlineData("Geofence", null)]
    [InlineData("Selfie+Geofence", null)]
    [InlineData("Device", "evidence:6f1c1e9e-0000-4000-8000-000000000000")]
    public async Task Item9_EventsPush_RefusesTheReservedLabels(string method, string? photo)
    {
        var w = await SelfieWorld.CreateAsync();
        var hr = w.Attendance(await w.RoleAsync("HR Manager", w.Caller, w.CallerUserId));

        var refused = await hr.PushEvent(RawEvent(w.Colleague.Id, method, photo, DateTime.UtcNow.AddHours(-2)), default);

        Assert.Equal("verification_label_reserved", SelfieWorld.CodeOf(refused.Result));
        Assert.Empty(w.Db.AttendanceRawEvents);
        // Positive control: an ordinary device label and photo reference are recorded.
        Assert.IsType<CreatedResult>((await hr.PushEvent(RawEvent(w.Colleague.Id, "Device", "photo-17.jpg", DateTime.UtcNow.AddHours(-1)), default)).Result);
    }

    // ── Item 10: the geofence stops trusting a silent device ────────────────────────────────────────────────

    [Fact]
    public async Task Item10_AnOldAppSendingNoMockFields_IsRefused_WithAnUpdateMessage()
    {
        var w = await SelfieWorld.CreateAsync();
        await w.AddSiteAsync();
        await w.EnforceGeofenceAsync();
        var c = w.Attendance(await w.EmployeeAsync(w.Caller, w.CallerUserId));

        var result = await c.MobilePunch(Punch(new { latitude = SelfieWorld.SiteLat, longitude = SelfieWorld.SiteLon, accuracyMeters = 10 }), default);

        Assert.Equal("app_update_required", SelfieWorld.CodeOf(result.Result));
        Assert.Equal("Please update the KynexOne app to record attendance at your site.", SelfieWorld.MessageOf(result.Result));
        Assert.Empty(w.Db.AttendanceRawEvents);
        // Positive control: the same punch from an Android app that reports the mock flag goes through.
        Assert.IsType<OkObjectResult>((await c.MobilePunch(Punch(new
        {
            latitude = SelfieWorld.SiteLat, longitude = SelfieWorld.SiteLon, accuracyMeters = 10, locationMocked = false, mockDetection = "Supported",
        }), default)).Result);
    }

    [Fact]
    public async Task Item10_AnIPhone_ThatCannotDetectAMock_IsAccepted_AndAudited()
    {
        var w = await SelfieWorld.CreateAsync();
        await w.AddSiteAsync();
        await w.EnforceGeofenceAsync();
        var c = w.Attendance(await w.EmployeeAsync(w.Caller, w.CallerUserId));

        var result = await c.MobilePunch(Punch(new { latitude = SelfieWorld.SiteLat, longitude = SelfieWorld.SiteLon, accuracyMeters = 10, mockDetection = "Unsupported" }), default);

        var raw = Assert.IsType<AttendanceRawEvent>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Contains(w.Db.AttendanceAuditLogs, a => a.Action == "attendance.geofence.mock_detection_unavailable"
                                                      && a.EntityId == raw.Id.ToString() && a.MetadataJson.Contains($"\"employeeId\":{w.Caller.Id}"));
    }

    [Fact]
    public async Task Item10_ThePunchThatFallsBackToEveryGeofencedSite_IsAudited()
    {
        var w = await SelfieWorld.CreateAsync();
        await w.AddSiteAsync(code: "NORTH"); // the employee works at "HQ": no site matches them
        await w.EnforceGeofenceAsync();
        var c = w.Attendance(await w.EmployeeAsync(w.Caller, w.CallerUserId));

        var result = await c.MobilePunch(Punch(new
        {
            latitude = SelfieWorld.SiteLat, longitude = SelfieWorld.SiteLon, accuracyMeters = 10, locationMocked = false, mockDetection = "Supported",
        }), default);

        var raw = Assert.IsType<AttendanceRawEvent>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Contains(w.Db.AttendanceAuditLogs, a => a.Action == "attendance.geofence.fallback_all_sites" && a.EntityId == raw.Id.ToString());
    }

    // ── helpers (d43b3943's types only) ─────────────────────────────────────────────────────────────────────

    private static WebPunchRequest Punch(object body) => JsonSerializer.Deserialize<WebPunchRequest>(
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["employeeId"] = 0, ["punchDirection"] = "In",
        }.Concat(JsonSerializer.SerializeToElement(body).EnumerateObject().Select(p => new KeyValuePair<string, object?>(p.Name, p.Value)))
            .ToDictionary(p => p.Key, p => p.Value)),
        new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static AttendanceRawEventRequest RawEvent(int employeeId, string method, string? photo, DateTime at) =>
        new(employeeId, null, null, "Integration", at, "In", null, null, null, null, photo, null, null, method, null);

    private static async Task<AttendanceEvidence> SeedUnusedAsync(SelfieWorld w, string key)
    {
        var created = DateTime.UtcNow.AddDays(-2);
        var row = new AttendanceEvidence
        {
            TenantId = w.TenantId, EmployeeId = w.Caller.Id, StorageKey = key, Sha256 = new string('d', 64), ByteSize = 3,
            CreatedAtUtc = created, ExpiresAtUtc = created.AddMinutes(10), PurgeState = "Active",
        };
        w.Db.AttendanceEvidence.Add(row);
        await w.Db.SaveChangesAsync();
        return row;
    }

    private static async Task<Guid> TenantAsync(ZayraDbContext db)
    {
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "T", Slug = $"t-{tenantId:N}" });
        await db.SaveChangesAsync();
        return tenantId;
    }

    /// <summary>A platform Owner as a real platform token carries it: the subject is the platform user's id.</summary>
    private static PlatformController AsNamedOwner(PlatformController controller, Guid ownerId)
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("sub", ownerId.ToString()), new Claim(ClaimTypes.NameIdentifier, ownerId.ToString()),
                    new Claim(ClaimTypes.Role, "PlatformAdmin"), new Claim("is_platform_admin", "true"),
                    new Claim("platform_role", PlatformRoles.Owner),
                ], "Test")),
            },
        };
        return controller;
    }

    /// <summary>
    /// An S3 adapter that behaves like the real one on a versioned bucket where the key lacks delete permission:
    /// every delete answers 403 until <see cref="Forbidden"/> is cleared. S3DocumentStorage.TryDeleteAsync turns that
    /// 403 into a returned false, which is exactly what d43b3943's purge took for "already gone".
    /// </summary>
    internal sealed class ForbiddenThenAllowedS3 : IS3Primitives
    {
        public Dictionary<string, List<string>> Versions { get; } = new(StringComparer.Ordinal);
        public bool Forbidden { get; set; } = true;

        public Task PutAsync(string bucket, string key, Stream content, string contentType, CancellationToken ct) => Task.CompletedTask;
        public Task<byte[]> GetBytesAsync(string bucket, string key, CancellationToken ct) => Task.FromResult(new byte[] { 1 });

        public Task DeleteAsync(string bucket, string key, CancellationToken ct)
        {
            if (Forbidden) throw Denied();
            // An unversioned delete on a versioned bucket only adds a delete marker; the versions stay.
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>?> ListVersionIdsAsync(string bucket, string key, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>?>(Versions.TryGetValue(key, out var v) ? v.ToList() : []);

        public Task DeleteVersionAsync(string bucket, string key, string versionId, CancellationToken ct)
        {
            if (Forbidden) throw Denied();
            Versions[key].Remove(versionId);
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string bucket, string key, CancellationToken ct) =>
            Task.FromResult(Versions.TryGetValue(key, out var v) && v.Count > 0);

        private static AmazonS3Exception Denied() => new("Access Denied") { StatusCode = HttpStatusCode.Forbidden, ErrorCode = "AccessDenied" };
    }

    /// <summary>A store that can only TRY to delete, and answers false (as S3DocumentStorage does on a 403).</summary>
    private sealed class FalseReturningStorage : IDocumentStorage
    {
        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
        public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Objects[storageUrl]);
        public string ResolvePath(string storageUrl) => storageUrl;
        public Task<bool> TryDeleteAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(false);
    }
}
