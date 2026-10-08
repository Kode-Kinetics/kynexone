using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Controllers;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;
using Zayra.Api.Tests.Platform;

namespace Zayra.Api.Tests.SelfieAttendance;

/// <summary>
/// The selfie-attendance v2 refusals that can be stated with main's own types (so they compile against main's production
/// code, where every one of them FAILS: main has no geofence and no sign-off gate). Each refusal is paired with a positive
/// control in the same test or the next, so no rule can pass by refusing everyone.
/// </summary>
public class SelfieAttendanceRefusalOnMainTests : PlatformTestBase
{
    // ~280 m north of the site: outside its 150 m radius.
    private const decimal OutsideLat = SelfieWorld.SiteLat + 0.0025m;

    /// <summary>
    /// A punch ~280 m from the site with a good (10 m), unmocked fix, bound from JSON exactly as the HTTP body is — so
    /// it compiles against main, whose request has no accuracy or mock fields (there they are simply ignored).
    /// </summary>
    private static WebPunchRequest Outside(int employeeId) => JsonSerializer.Deserialize<WebPunchRequest>(
        JsonSerializer.Serialize(new
        {
            employeeId, punchDirection = "In", latitude = OutsideLat, longitude = SelfieWorld.SiteLon,
            accuracyMeters = 10, locationMocked = false,
        }), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    /// <summary>
    /// Review item 10: a browser cannot attest to a mocked location, so under an enforced geofence the employee's own
    /// web punch is refused outright — even from inside the radius — with a plain pointer to the app.
    /// </summary>
    [Fact]
    public async Task Geofence_OnWebPunch_IsRefused_TheMobileAppIsRequired_EvenInsideTheRadius()
    {
        var w = await SelfieWorld.CreateAsync();
        await w.AddSiteAsync();
        await w.EnforceGeofenceAsync();
        var c = w.Attendance(await w.EmployeeAsync(w.Caller, w.CallerUserId));

        foreach (var request in new[] { Outside(0), Outside(0) with { Latitude = SelfieWorld.SiteLat + 0.0005m } })
        {
            var bad = Assert.IsType<BadRequestObjectResult>((await c.WebPunch(request, default)).Result);
            Assert.Equal("mobile_app_required", SelfieWorld.CodeOf(bad));
            Assert.Equal("Your company requires attendance from the mobile app at your site.", SelfieWorld.MessageOf(bad));
        }
        Assert.Empty(w.Db.AttendanceRawEvents);
    }

    [Fact]
    public async Task Geofence_PositiveControl_WebPunchWithTheGeofenceOff_IsRecorded()
    {
        var w = await SelfieWorld.CreateAsync();
        await w.AddSiteAsync();
        var c = w.Attendance(await w.EmployeeAsync(w.Caller, w.CallerUserId));

        Assert.IsType<OkObjectResult>((await c.WebPunch(Outside(0), default)).Result);
    }

    [Fact]
    public async Task Geofence_PositiveControl_InsideTheRadius_IsRecorded()
    {
        var w = await SelfieWorld.CreateAsync();
        await w.AddSiteAsync();
        await w.EnforceGeofenceAsync();
        var c = w.Attendance(await w.EmployeeAsync(w.Caller, w.CallerUserId));
        var inside = Outside(0) with { Latitude = SelfieWorld.SiteLat + 0.0005m };

        Assert.IsType<OkObjectResult>((await c.MobilePunch(inside, default)).Result);
        Assert.IsType<OkObjectResult>((await c.MobilePunch(inside with { PunchDirection = "Out" }, default)).Result);
        Assert.Equal(2, await w.Db.AttendanceRawEvents.CountAsync());
    }

    [Fact]
    public async Task Geofence_OnMobilePunch_OutsideTheRadius_IsRefused_AndNothingIsRecorded()
    {
        var w = await SelfieWorld.CreateAsync();
        await w.AddSiteAsync();
        await w.EnforceGeofenceAsync();
        var c = w.Attendance(await w.EmployeeAsync(w.Caller, w.CallerUserId));

        var result = await c.MobilePunch(Outside(0), default);

        Assert.Equal("outside_geofence", SelfieWorld.CodeOf(Assert.IsType<BadRequestObjectResult>(result.Result)));
        Assert.Empty(w.Db.AttendanceRawEvents);
    }

    [Fact]
    public async Task Geofence_OnKioskPunch_OnBehalf_OutsideTheRadius_IsRefused()
    {
        var w = await SelfieWorld.CreateAsync();
        await w.AddSiteAsync();
        await w.EnforceGeofenceAsync();
        // HR Manager holds attendance.write, so punching for a colleague through the kiosk route is authorized (#209).
        var c = w.Attendance(await w.RoleAsync("HR Manager", w.Caller, w.CallerUserId));

        var result = await c.KioskPunch(Outside(w.Colleague.Id), default);

        Assert.Equal("outside_geofence", SelfieWorld.CodeOf(Assert.IsType<BadRequestObjectResult>(result.Result)));
        Assert.Empty(w.Db.AttendanceRawEvents);
    }

    [Fact]
    public async Task Geofence_WithoutALocation_IsRefused_OnMobile_AndTheWebIsClosed()
    {
        var w = await SelfieWorld.CreateAsync();
        await w.AddSiteAsync();
        await w.EnforceGeofenceAsync();
        var c = w.Attendance(await w.EmployeeAsync(w.Caller, w.CallerUserId));

        Assert.Equal("mobile_app_required", SelfieWorld.CodeOf((await c.WebPunch(new WebPunchRequest(0, "In", null, null, null), default)).Result));
        Assert.Equal("location_required", SelfieWorld.CodeOf((await c.MobilePunch(new WebPunchRequest(0, "In", null, null, null), default)).Result));
        Assert.Empty(w.Db.AttendanceRawEvents);
    }

    [Fact]
    public async Task Geofence_TheLegacyMobilePunchRoute_IsNotAWayRoundIt()
    {
        var w = await SelfieWorld.CreateAsync();
        await w.AddSiteAsync();
        var c = w.Mobile(await w.EmployeeAsync(w.Caller, w.CallerUserId));

        // Positive control: with the geofence off the legacy route records the punch as before.
        Assert.IsType<OkObjectResult>(await c.Punch(new MobilePunchRequest(0, "In", null), default));

        await w.EnforceGeofenceAsync();
        var refused = await c.Punch(new MobilePunchRequest(0, "Out", null), default);

        Assert.Equal("location_required", SelfieWorld.CodeOf(Assert.IsType<BadRequestObjectResult>(refused)));
        Assert.Null((await w.Db.AttendanceDailyRecords.SingleAsync()).LastOutUtc);
    }

    [Fact]
    public async Task Platform_EnablingSelfieAttendance_WithoutTheSignOffs_IsRefused()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "T", Slug = $"t-{tenantId:N}" });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        await SelfieWorld.AsPlatformOwnerAsync(controller, db, SelfieWorld.SigningOwnerId);
        var none = await controller.SetFeatureFlag(tenantId, SelfieWorld.SelfieKey, new SetFeatureFlagRequest(true, null), default);
        var dpiaOnly = await controller.SetFeatureFlag(tenantId, SelfieWorld.SelfieKey, new SetFeatureFlagRequest(true, JsonSerializer.Serialize(new
        {
            dpia = new { signedOffBy = "owner", signedOffAtUtc = "2026-10-08T09:00:00Z", reference = "DPIA-1" },
        })), default);
        var wrongRegion = await controller.SetFeatureFlag(tenantId, SelfieWorld.SelfieKey, new SetFeatureFlagRequest(true, JsonSerializer.Serialize(new
        {
            dpia = new { signedOffBy = "owner", signedOffAtUtc = "2026-10-08T09:00:00Z", reference = "DPIA-1" },
            dataResidency = new { region = "EU", confirmedBy = "owner", confirmedAtUtc = "2026-10-08T09:00:00Z" },
        })), default);

        foreach (var refused in new[] { none, dpiaOnly, wrongRegion })
            Assert.Equal("selfie_signoff_missing", SelfieWorld.CodeOf(Assert.IsType<UnprocessableEntityObjectResult>(refused)));
        Assert.False(await db.TenantFeatureFlags.AnyAsync(f => f.FeatureKey == SelfieWorld.SelfieKey && f.IsEnabled));
    }
}
