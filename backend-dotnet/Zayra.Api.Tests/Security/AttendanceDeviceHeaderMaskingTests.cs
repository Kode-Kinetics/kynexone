using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Security.SeededRoleBundles;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// A device's custom headers can carry its credentials (an API-key header). The device read endpoints are open
/// to every attendance.read holder, so header VALUES are shown only to callers who may configure devices
/// (attendance.bulk_import, the key the create/update endpoints require). Everyone else sees the header names
/// with a mask, and a masked value sent back on save never overwrites the stored credential.
/// </summary>
public sealed class AttendanceDeviceHeaderMaskingTests
{
    private const string Secret = "live-device-secret-123";
    private const string ParamSecret = "param-token-456";
    private const string UrlPassword = "url-pass-789";
    private const string QueryKey = "query-key-012";
    private static readonly string StoredUrl = $"https://ops:{UrlPassword}@device.example.com:8443/api/att/logs?api_key={QueryKey}";
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Theory]
    [InlineData("Auditor")]
    [InlineData("Manager")]
    [InlineData("Supervisor")]
    [InlineData("Payroll Officer")]
    [InlineData("HR Assistant")]
    public async Task AnAttendanceReadOnlyHolder_SeesHeaderNamesButNeverValues(string role)
    {
        var (db, tenantId) = await NewTenantAsync("device-mask");
        var permissions = await PermissionsOfAsync(db, tenantId, role);
        permissions.Should().Contain("attendance.read").And.NotContain(AttendanceDeviceDto.ConfigurePermission);
        var device = await SeedDeviceAsync(db, tenantId);
        var controller = Controller(db, await CallerAsync(db, tenantId, role));

        var one = (AttendanceDeviceDto)((OkObjectResult)(await controller.Device(device.Id, Ct)).Result!).Value!;
        var list = await controller.Devices(ct: Ct);

        foreach (var dto in new[] { one, list.Items.Single() })
        {
            var serialized = JsonSerializer.Serialize(dto);
            foreach (var secret in new[] { Secret, ParamSecret, UrlPassword, QueryKey, "ops:" })
                serialized.Should().NotContain(secret);
            dto.EndpointUrl.Should().Be($"https://{AttendanceDeviceDto.MaskedHeaderValue}@device.example.com:8443/api/att/logs?{AttendanceDeviceDto.MaskedHeaderValue}");
            JsonSerializer.Deserialize<Dictionary<string, string>>(dto.DeviceParametersJson).Should().Equal(new Dictionary<string, string>
            {
                ["poll_path"] = "/iclock/cdata",                          // operational, still readable
                ["auth_token"] = AttendanceDeviceDto.MaskedHeaderValue,
                ["comm_key"] = AttendanceDeviceDto.MaskedHeaderValue,
            });
            JsonSerializer.Deserialize<Dictionary<string, string>>(dto.CustomHeadersJson).Should().Equal(new Dictionary<string, string>
            {
                ["X-Api-Key"] = AttendanceDeviceDto.MaskedHeaderValue,
                ["X-Site"] = AttendanceDeviceDto.MaskedHeaderValue,
            });
        }
    }

    [Theory]
    [InlineData("HR Manager")]
    [InlineData("Admin")]
    public async Task ADeviceConfigurer_StillSeesHeaderValues_SoTheEditFormWorks(string role)
    {
        var (db, tenantId) = await NewTenantAsync("device-reveal");
        var device = await SeedDeviceAsync(db, tenantId);
        var controller = Controller(db, await CallerAsync(db, tenantId, role));

        var one = (AttendanceDeviceDto)((OkObjectResult)(await controller.Device(device.Id, Ct)).Result!).Value!;

        one.CustomHeadersJson.Should().Contain(Secret);
        one.DeviceParametersJson.Should().Contain(ParamSecret);
        one.EndpointUrl.Should().Be(StoredUrl);
        one.ErrorLog.Should().Contain(UrlPassword);
    }

    [Fact]
    public async Task SavingMaskedParametersAndUrl_KeepsTheStoredValues()
    {
        var (db, tenantId) = await NewTenantAsync("device-merge-params");
        var device = await SeedDeviceAsync(db, tenantId);
        var controller = Controller(db, await CallerAsync(db, tenantId, "HR Manager"));
        var maskedParams = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["poll_path"] = "/iclock/v2",                             // a real edit
            ["auth_token"] = AttendanceDeviceDto.MaskedHeaderValue,   // round-tripped mask
            ["comm_key"] = AttendanceDeviceDto.MaskedHeaderValue,
        });
        var maskedUrl = AttendanceDeviceDto.RedactEndpointUrl(StoredUrl);

        var result = await controller.UpdateDevice(device.Id, Request(device.CustomHeadersJson, maskedParams, maskedUrl), Ct);

        result.Result.Should().BeOfType<OkObjectResult>();
        var stored = await db.AttendanceDevices.AsNoTracking().SingleAsync(d => d.Id == device.Id);
        stored.EndpointUrl.Should().Be(StoredUrl);
        JsonSerializer.Deserialize<Dictionary<string, string>>(stored.DeviceParametersJson).Should().Equal(new Dictionary<string, string>
        {
            ["poll_path"] = "/iclock/v2", ["auth_token"] = ParamSecret, ["comm_key"] = "4242",
        });
    }

    [Fact]
    public void AMaskedUrlWithANewHost_KeepsTheStoredCredentialsAndQuery() =>
        AttendanceDeviceDto.MergeMaskedEndpointUrl(StoredUrl,
                $"https://{AttendanceDeviceDto.MaskedHeaderValue}@device2.example.com/api?{AttendanceDeviceDto.MaskedHeaderValue}")
            .Should().Be($"https://ops:{UrlPassword}@device2.example.com/api?api_key={QueryKey}");

    [Theory]
    [InlineData("https://device.example.com/api", "https://device.example.com/api")]
    [InlineData("http://10.0.0.5:8080/logs", "http://10.0.0.5:8080/logs")]
    [InlineData("user:pw@10.0.0.5/logs", "••••")]
    [InlineData("", "")]
    public void UrlsWithoutSecretsAreUntouched_AndUnparseableOnesWithSecretsAreMaskedWhole(string url, string expected) =>
        AttendanceDeviceDto.RedactEndpointUrl(url).Should().Be(expected);

    [Fact]
    public async Task SavingAMaskedHeaderValue_KeepsTheStoredCredential()
    {
        var (db, tenantId) = await NewTenantAsync("device-merge");
        var device = await SeedDeviceAsync(db, tenantId);
        var controller = Controller(db, await CallerAsync(db, tenantId, "HR Manager"));
        var masked = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["X-Api-Key"] = AttendanceDeviceDto.MaskedHeaderValue,   // round-tripped from a masked read
            ["X-Site"] = "riyadh-2",                                   // a real edit
            ["X-Unknown"] = AttendanceDeviceDto.MaskedHeaderValue,   // a mask with nothing stored behind it
        });

        var result = await controller.UpdateDevice(device.Id, Request(masked), Ct);

        result.Result.Should().BeOfType<OkObjectResult>();
        var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(
            (await db.AttendanceDevices.AsNoTracking().SingleAsync(d => d.Id == device.Id)).CustomHeadersJson)!;
        stored.Should().Equal(new Dictionary<string, string> { ["X-Api-Key"] = Secret, ["X-Site"] = "riyadh-2" });
    }

    [Theory]
    [InlineData(null, "{}")]
    [InlineData("", "{}")]
    [InlineData("not json", "{}")]
    [InlineData("[\"a\"]", "{}")]
    public void MaskingRevealsNothingFromMalformedHeaders(string? stored, string expected) =>
        AttendanceDeviceDto.MaskHeaderValues(stored).Should().Be(expected);

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────

    private static AttendanceDeviceRequest Request(string customHeadersJson, string? parametersJson = null, string? endpointUrl = null) => new(
        "Gate 1", "Biometric", "ZK", "SN-1", null, "Riyadh", "10.0.0.1", endpointUrl ?? "https://device.example.com", 443,
        null, "Pull API", "Hourly", "None", null, customHeadersJson, parametersJson, null, null);

    private static async Task<AttendanceDevice> SeedDeviceAsync(ZayraDbContext db, Guid tenantId)
    {
        var device = new AttendanceDevice
        {
            TenantId = tenantId, DeviceName = "Gate 1", DeviceType = "Biometric", Vendor = "ZK", SerialNumber = "SN-1",
            CustomHeadersJson = JsonSerializer.Serialize(new Dictionary<string, string> { ["X-Api-Key"] = Secret, ["X-Site"] = "riyadh-1" }),
            DeviceParametersJson = JsonSerializer.Serialize(new Dictionary<string, string>
                { ["poll_path"] = "/iclock/cdata", ["auth_token"] = ParamSecret, ["comm_key"] = "4242" }),
            EndpointUrl = StoredUrl,
            ErrorLog = $"Sync timed out. Check endpoint: {StoredUrl}",
        };
        db.AttendanceDevices.Add(device);
        await db.SaveChangesAsync();
        return device;
    }

    private static AttendanceController Controller(ZayraDbContext db, ClaimsPrincipal caller) =>
        Bind(new AttendanceController(
            new AttendanceService(db, new NullNotifications(), new NullHttpClientFactory()),
            new UnrestrictedScope(), new HrmHierarchyService(db, new NullAudit()), db), caller);

    private sealed class UnrestrictedScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }

    private sealed class NullNotifications : INotificationService
    {
        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NullHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class NullAudit : IAuditService
    {
        public Task WriteAsync(string action, string entityName, string? entityId, RequestContext context, string? metadata, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
