using System.Net;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Mobile enablement wave. Two independent properties:
///
///  (1) MOBILE BFF SCOPING. POST /api/mobile/notifications/{id}/read was the only endpoint on
///      MobileController that did not resolve the caller's own employee id, so any authenticated
///      user in a tenant could mark a COLLEAGUE'S notification read (CWE-639).
///
///  (2) EXPO PUSH PROVIDER. The first real IPushProvider. The tests that matter are the ones that
///      pin its contract with the EXISTING delivery machinery — especially the "unregistered"
///      error-code spelling that PushChannelDispatcher uses to prune dead device rows — because a
///      provider that silently stops satisfying that contract looks healthy and reaches nobody.
///
/// InMemory EF + a stub HttpMessageHandler. No Postgres, no Docker, no network.
/// </summary>
public class MobilePushEnablementTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // ═════════════════════════════════════════════════════════════════════════
    // (1) Mobile BFF scoping
    // ═════════════════════════════════════════════════════════════════════════

    private static MobileController CreateMobileController(ZayraDbContext db, Guid tenantId, int callerEmployeeId)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim("employee_id", callerEmployeeId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        }, "test"));
        var httpCtx = new DefaultHttpContext { User = principal };
        return new MobileController(db) { ControllerContext = new ControllerContext { HttpContext = httpCtx } };
    }

    /// <summary>
    /// The caller must be a REAL, active employee of the tenant, not merely the bearer of an
    /// employee_id claim. PR #59 hardened ResolveCallerEmployeeIdAsync to verify exactly that
    /// ("a JWT claim is an identifier, not proof that the employee still exists in this tenant"),
    /// so these fixtures now have to seed the caller they claim to be. Without this the controller
    /// refuses every caller with 403 and the scoping assertions below would pass vacuously —
    /// a cross-employee write would look "refused" because NOBODY can write, which is not the
    /// property under test.
    /// </summary>
    private static Employee SeedEmployee(ZayraDbContext db, Guid tenantId, int employeeId)
    {
        var e = new Employee
        {
            Id = employeeId, TenantId = tenantId,
            EmployeeCode = $"E-{employeeId}", FullName = $"Employee {employeeId}",
            Status = EmployeeStatuses.Active, IsDeleted = false,
        };
        db.Employees.Add(e);
        db.SaveChanges();
        return e;
    }

    private static EmployeeNotification SeedNotification(ZayraDbContext db, Guid tenantId, int employeeId)
    {
        var n = new EmployeeNotification
        {
            TenantId = tenantId, EmployeeId = employeeId,
            Title = "Payslip ready", Body = "Your payslip is available.", IsRead = false,
        };
        db.EmployeeNotifications.Add(n);
        db.SaveChanges();
        return n;
    }

    /// <summary>
    /// THE DEFECT. Employee 2001 asks to mark employee 2002's notification read, inside the SAME
    /// tenant, so the old (id + tenant) filter matched it. It must be refused AND the row must be
    /// unchanged — an integrity write is the actual harm, the status code is only the symptom.
    /// </summary>
    [Fact]
    public async Task MarkRead_refuses_a_notification_belonging_to_another_employee()
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        SeedEmployee(db, tenantId, employeeId: 2001);   // the caller, a genuine active employee
        SeedEmployee(db, tenantId, employeeId: 2002);   // the colleague whose row must not be touched
        var victims = SeedNotification(db, tenantId, employeeId: 2002);

        var controller = CreateMobileController(db, tenantId, callerEmployeeId: 2001);
        var result = await controller.MarkRead(victims.Id, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>(
            "a colleague's notification must not be addressable, and 404 must not confirm it exists");

        db.ChangeTracker.Clear();
        var after = await db.EmployeeNotifications.IgnoreQueryFilters().SingleAsync(x => x.Id == victims.Id);
        after.IsRead.Should().BeFalse("the cross-employee write must not have landed");
        after.ReadAtUtc.Should().BeNull();
    }

    /// <summary>Control: the owner's own call still works, so the fix is a scope narrowing and not a break.</summary>
    [Fact]
    public async Task MarkRead_still_marks_the_callers_own_notification()
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        SeedEmployee(db, tenantId, employeeId: 2001);
        var mine = SeedNotification(db, tenantId, employeeId: 2001);

        var controller = CreateMobileController(db, tenantId, callerEmployeeId: 2001);
        var result = await controller.MarkRead(mine.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        db.ChangeTracker.Clear();
        var after = await db.EmployeeNotifications.IgnoreQueryFilters().SingleAsync(x => x.Id == mine.Id);
        after.IsRead.Should().BeTrue();
        after.ReadAtUtc.Should().NotBeNull();
    }

    /// <summary>A notification in ANOTHER tenant stays unreachable — the tenant predicate is still there.</summary>
    [Fact]
    public async Task MarkRead_refuses_a_notification_in_another_tenant()
    {
        using var db = CreateDb();
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        // The caller is a genuine active employee OF THEIR OWN TENANT, so resolution succeeds and
        // the refusal below is attributable to the tenant predicate on the notification rather than
        // to an unresolvable caller. (Employee.Id is the primary key, so the foreign tenant's
        // notification simply carries the same employee number; it needs no row of its own.)
        SeedEmployee(db, mine, employeeId: 2001);
        var foreign = SeedNotification(db, theirs, employeeId: 2001);

        var controller = CreateMobileController(db, mine, callerEmployeeId: 2001);
        var result = await controller.MarkRead(foreign.Id, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        db.ChangeTracker.Clear();
        (await db.EmployeeNotifications.IgnoreQueryFilters().SingleAsync(x => x.Id == foreign.Id))
            .IsRead.Should().BeFalse();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // (2) Expo push provider
    // ═════════════════════════════════════════════════════════════════════════

    private const string GoodToken = "ExponentPushToken[xxxxxxxxxxxxxxxxxxxxxx]";

    private sealed class StubConfigReader(Dictionary<string, string>? values) : INotificationProviderConfigReader
    {
        public Task<NotificationProviderConfig> GetAsync(Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new NotificationProviderConfig(
                values ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (ExpoPushProvider Provider, StubHandler Handler) CreateProvider(
        Dictionary<string, string>? config, HttpStatusCode status = HttpStatusCode.OK, string body = "{}")
    {
        var handler = new StubHandler(status, body);
        var provider = new ExpoPushProvider(
            new StubConfigReader(config),
            new StubHttpClientFactory(handler),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ExpoPushProvider>.Instance);
        return (provider, handler);
    }

    private static Dictionary<string, string> ExpoEnabled() =>
        new(StringComparer.OrdinalIgnoreCase) { ["Push.Provider"] = "expo" };

    private static ProviderMessage Msg(string token = GoodToken, string platform = "ios") =>
        new(Guid.NewGuid(), token, "Fatima Al-Zahra", "Payslip ready",
            "Your payslip is available.", "abc123def456", platform);

    /// <summary>
    /// The degradation contract this pod exists to protect: an unconfigured tenant gets a visible
    /// not_configured answer, no exception, and — critically — NO outbound HTTP call.
    /// </summary>
    [Fact]
    public async Task Unconfigured_tenant_degrades_to_not_configured_without_calling_expo()
    {
        var (provider, handler) = CreateProvider(config: null);

        (await provider.IsConfiguredAsync(Guid.NewGuid(), CancellationToken.None)).Should().BeFalse();

        var result = await provider.SendAsync(Msg(), CancellationToken.None);
        result.Status.Should().Be(ProviderSendStatus.NotConfigured);
        result.ErrorCode.Should().Be("provider_not_configured");
        handler.Calls.Should().Be(0);
    }

    /// <summary>A tenant that declared some OTHER vendor must not be claimed as configured.</summary>
    [Fact]
    public async Task Tenant_declaring_a_different_vendor_reads_as_not_configured()
    {
        var cfg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Push.Provider"] = "fcm" };
        var (provider, handler) = CreateProvider(cfg);

        (await provider.IsConfiguredAsync(Guid.NewGuid(), CancellationToken.None)).Should().BeFalse();
        var result = await provider.SendAsync(Msg(), CancellationToken.None);
        result.Status.Should().Be(ProviderSendStatus.NotConfigured);
        result.ErrorMessage.Should().Contain("fcm");
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Ok_ticket_is_sent_and_carries_the_receipt_id()
    {
        var (provider, handler) = CreateProvider(ExpoEnabled(),
            body: """{"data":[{"status":"ok","id":"receipt-1"}]}""");

        var result = await provider.SendAsync(Msg(platform: "android"), CancellationToken.None);

        result.Status.Should().Be(ProviderSendStatus.Sent);
        result.Reference.Should().Be("receipt-1");
        handler.Calls.Should().Be(1);
        handler.LastRequestBody.Should().Contain(GoodToken);
        // Android channel must match a channel pushNotifications.ts actually creates.
        handler.LastRequestBody.Should().Contain("\"channelId\":\"default\"");
    }

    /// <summary>
    /// THE LOAD-BEARING SPELLING. PushChannelDispatcher prunes a device only when a TERMINAL
    /// result's ErrorCode contains "unregistered". Expo says "DeviceNotRegistered". If this
    /// mapping ever drifts, dead tokens accumulate and re-fail on every payroll run forever.
    /// </summary>
    [Fact]
    public async Task DeviceNotRegistered_maps_to_a_terminal_unregistered_code()
    {
        var (provider, _) = CreateProvider(ExpoEnabled(),
            body: """{"data":[{"status":"error","message":"not registered","details":{"error":"DeviceNotRegistered"}}]}""");

        var result = await provider.SendAsync(Msg(), CancellationToken.None);

        result.Status.Should().Be(ProviderSendStatus.TerminalFailure);
        result.ErrorCode.Should().Contain("unregistered",
            "PushChannelDispatcher keys dead-device pruning off this substring");
    }

    /// <summary>
    /// End-to-end through the REAL dispatcher: an unregistered token must come back as a terminal
    /// failure carrying the device id, which is what NotificationDeliveryWorker deletes. This is the
    /// wiring test — the one above only pins the code string.
    /// </summary>
    [Fact]
    public async Task Dispatcher_marks_the_device_dead_when_expo_says_unregistered()
    {
        var (provider, _) = CreateProvider(ExpoEnabled(),
            body: """{"data":[{"status":"error","message":"not registered","details":{"error":"DeviceNotRegistered"}}]}""");
        var dispatcher = new PushChannelDispatcher(provider,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PushChannelDispatcher>.Instance);
        var deviceId = Guid.NewGuid();

        var request = new NotificationDispatchRequest(Guid.NewGuid(), NotificationChannels.Push,
            "PayslipPublished", "push", "Fatima Al-Zahra", "Payslip ready", "Available now.", "abc123",
            PushTargets: [new PushTarget(deviceId, GoodToken, "ios")]);

        var result = await dispatcher.SendAsync(request, CancellationToken.None);

        result.Outcome.Should().Be(DeliveryOutcomes.Failed);
        result.IsTransient.Should().BeFalse();
        result.DeadDeviceIds.Should().ContainSingle().Which.Should().Be(deviceId);
    }

    /// <summary>
    /// A good send through the real dispatcher — and proof the dispatcher does NOT prune a healthy
    /// device, which is the mirror failure of the test above.
    /// </summary>
    [Fact]
    public async Task Dispatcher_reports_sent_and_prunes_nothing_on_a_good_send()
    {
        var (provider, _) = CreateProvider(ExpoEnabled(),
            body: """{"data":[{"status":"ok","id":"receipt-9"}]}""");
        var dispatcher = new PushChannelDispatcher(provider,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PushChannelDispatcher>.Instance);

        var request = new NotificationDispatchRequest(Guid.NewGuid(), NotificationChannels.Push,
            "PayslipPublished", "push", "Fatima Al-Zahra", "Payslip ready", "Available now.", "abc123",
            PushTargets: [new PushTarget(Guid.NewGuid(), GoodToken, "android")]);

        var result = await dispatcher.SendAsync(request, CancellationToken.None);

        result.Outcome.Should().Be(DeliveryOutcomes.Sent);
        result.ProviderReference.Should().Be("receipt-9");
        result.DeadDeviceIds.Should().BeNull();
    }

    /// <summary>
    /// An unconfigured tenant must stay unconfigured THROUGH the dispatcher too — a not_configured
    /// row, not a "failed" one, so the admin sees "nobody turned this on" rather than "it broke".
    /// </summary>
    [Fact]
    public async Task Dispatcher_surfaces_not_configured_rather_than_failure_when_the_tenant_is_unset()
    {
        var (provider, handler) = CreateProvider(config: null);
        var dispatcher = new PushChannelDispatcher(provider,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PushChannelDispatcher>.Instance);

        var request = new NotificationDispatchRequest(Guid.NewGuid(), NotificationChannels.Push,
            "PayslipPublished", "push", "Fatima Al-Zahra", "Payslip ready", "Available now.", "abc123",
            PushTargets: [new PushTarget(Guid.NewGuid(), GoodToken, "ios")]);

        var result = await dispatcher.SendAsync(request, CancellationToken.None);

        result.Outcome.Should().Be(DeliveryOutcomes.NotConfigured);
        handler.Calls.Should().Be(0);
    }

    /// <summary>
    /// A raw FCM/APNs token from an older build is terminal — but deliberately NOT "unregistered",
    /// so the device row survives for an admin to see instead of being silently deleted.
    /// </summary>
    [Fact]
    public async Task Non_expo_token_is_terminal_but_does_not_trigger_device_pruning()
    {
        var (provider, handler) = CreateProvider(ExpoEnabled());

        var result = await provider.SendAsync(Msg(token: "fZ8Kq3raw-fcm-token"), CancellationToken.None);

        result.Status.Should().Be(ProviderSendStatus.TerminalFailure);
        result.ErrorCode.Should().Be("invalid_push_token");
        result.ErrorCode.Should().NotContain("unregistered");
        handler.Calls.Should().Be(0, "a token Expo cannot address is rejected before the network call");
    }

    [Theory]
    [InlineData("ExponentPushToken[abc]", true)]
    [InlineData("ExpoPushToken[abc]", true)]
    [InlineData("ExponentPushToken[abc", false)]
    [InlineData("", false)]
    [InlineData("raw-fcm-token", false)]
    public void Token_shape_recognises_both_expo_spellings(string token, bool expected) =>
        ExpoPushProvider.IsExpoPushToken(token).Should().Be(expected);

    [Fact]
    public async Task Server_error_is_transient_and_rate_limit_is_transient()
    {
        var (p5xx, _) = CreateProvider(ExpoEnabled(), HttpStatusCode.ServiceUnavailable, "{}");
        (await p5xx.SendAsync(Msg(), CancellationToken.None)).Status
            .Should().Be(ProviderSendStatus.TransientFailure);

        var (pRate, _) = CreateProvider(ExpoEnabled(),
            body: """{"data":[{"status":"error","message":"slow down","details":{"error":"MessageRateExceeded"}}]}""");
        (await pRate.SendAsync(Msg(), CancellationToken.None)).Status
            .Should().Be(ProviderSendStatus.TransientFailure);
    }

    /// <summary>
    /// A 200 with no ticket is genuinely indeterminate. PushChannelDispatcher.RetryOnAmbiguous is
    /// true, so Ambiguous is the honest, safe classification — never a false "sent".
    /// </summary>
    [Fact]
    public async Task Ok_response_with_no_ticket_is_ambiguous_not_sent()
    {
        var (provider, _) = CreateProvider(ExpoEnabled(), body: """{"data":[]}""");

        var result = await provider.SendAsync(Msg(), CancellationToken.None);

        result.Status.Should().Be(ProviderSendStatus.Ambiguous);
        result.ErrorCode.Should().Be("no_ticket");
    }

    /// <summary>
    /// SHAPE REGRESSION — found by calling the real endpoint, not by reading the docs.
    /// exp.host echoes the shape it was given: a bare-object post returns {"data":{...}} while an
    /// array post returns {"data":[...]}. The first version of this adapter posted a bare object and
    /// parsed an array, which would have thrown on EVERY send and retried forever as "ambiguous".
    /// We now post the array form; these assertions pin that BOTH shapes still parse.
    /// </summary>
    [Fact]
    public void Ticket_parsing_accepts_both_the_array_and_the_bare_object_response_shapes()
    {
        var fromArray = ExpoPushProvider.ParseTicket("""{"data":[{"status":"ok","id":"a1"}]}""");
        fromArray!.Status.Should().Be("ok");
        fromArray.Id.Should().Be("a1");

        var fromObject = ExpoPushProvider.ParseTicket("""{"data":{"status":"ok","id":"o1"}}""");
        fromObject!.Status.Should().Be("ok");
        fromObject.Id.Should().Be("o1");

        // The verbatim body exp.host returned for an unregistered token on 2026-09-18.
        var real = ExpoPushProvider.ParseTicket(
            """{"data":[{"status":"error","message":"\"ExponentPushToken[x]\" is not a registered push notification recipient or it is associated with a project that does not exist.","details":{"error":"DeviceNotRegistered","expoPushToken":"ExponentPushToken[x]"}}]}""");
        real!.Details!.Error.Should().Be("DeviceNotRegistered");

        ExpoPushProvider.ParseTicket("""{"data":[]}""").Should().BeNull();
        ExpoPushProvider.ParseTicket("{}").Should().BeNull();
    }

    /// <summary>The wire request must be the batch ARRAY form — that is what makes the reply shape stable.</summary>
    [Fact]
    public async Task Send_posts_the_expo_batch_array_form()
    {
        var (provider, handler) = CreateProvider(ExpoEnabled(),
            body: """{"data":[{"status":"ok","id":"receipt-1"}]}""");

        await provider.SendAsync(Msg(), CancellationToken.None);

        handler.LastRequestBody!.TrimStart().Should().StartWith("[",
            "exp.host mirrors the request shape; only the array form guarantees a {\"data\":[...]} reply");
    }

    /// <summary>
    /// The optional Expo Enhanced-Security token must be on the secret allow-list. It is a bearer
    /// credential for the whole Expo project and the substring matcher in
    /// SetupSettingsController.IsSecretSetting does not catch it.
    /// </summary>
    [Fact]
    public void Expo_access_token_is_registered_as_a_secret_settings_key() =>
        NotificationProviderSecrets.IsSecretKey("Push.AccessToken").Should().BeTrue();
}
