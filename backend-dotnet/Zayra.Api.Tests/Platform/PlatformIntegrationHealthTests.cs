using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Controllers;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Platform;

/// <summary>
/// F09 — the platform System Health card is the admin-facing view of outbound integrations. It
/// must show the same modes and counts as /health/ready, and a test-mode send must never read as
/// a passed test or a sent invoice.
/// </summary>
public class PlatformIntegrationHealthTests : PlatformTestBase
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static IConfiguration CaptureConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { [EmailTransportPolicy.ModeKey] = "capture" })
        .Build();

    [Fact]
    public async Task Health_shows_email_mode_qiwa_simulation_and_delivery_counts()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Health", Slug = $"health-{Guid.NewGuid():N}" });
        db.NotificationDeliveries.AddRange(
            Row(tenantId, DeliveryOutcomes.DeadLetter), Row(tenantId, DeliveryOutcomes.NotConfigured),
            Row(tenantId, DeliveryOutcomes.Captured));
        await db.SaveChangesAsync();
        var config = CaptureConfig();
        var controller = CreateController(db, configuration: config,
            emailService: new SmtpEmailService(db, new EphemeralDataProtectionProvider(), config, NullLogger<SmtpEmailService>.Instance));

        var ok = (await controller.Health(CancellationToken.None)).Should().BeOfType<OkObjectResult>().Subject;
        var components = JsonSerializer.SerializeToElement(ok.Value, Web).GetProperty("components");

        // A server in test delivery mode is not "an SMTP server that is configured".
        components.GetProperty("smtp").GetProperty("status").GetString().Should().Be("capture");
        components.GetProperty("email").GetProperty("status").GetString().Should().Be("capture");
        components.GetProperty("email").GetProperty("simulated").GetBoolean().Should().BeTrue();
        components.GetProperty("qiwa").GetProperty("simulated").GetBoolean().Should().BeTrue();
        components.GetProperty("qiwa").GetProperty("detail").GetString().Should().Contain("Qiwa data check only (nothing sent to Qiwa)");
        var deliveries = components.GetProperty("deliveries");
        deliveries.GetProperty("status").GetString().Should().Be("attention");
        deliveries.GetProperty("deadLetter").GetInt32().Should().Be(1);
        deliveries.GetProperty("notConfigured").GetInt32().Should().Be(1);
        deliveries.GetProperty("captured").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task A_platform_smtp_test_that_was_captured_is_not_reported_as_sent()
    {
        await using var db = CreateDb();
        var config = CaptureConfig();
        var protection = new EphemeralDataProtectionProvider();
        var controller = CreateController(db, configuration: config,
            emailService: new SmtpEmailService(db, protection, config, NullLogger<SmtpEmailService>.Instance));
        await controller.UpdateSmtp(new UpdateSmtpRequest(Host: "127.0.0.1", Port: 1, Username: "u", Password: "p",
            UseSsl: false, FromEmail: "noreply@example.test", FromName: "KynexOne", Provider: null), protection, default);

        var ok = (await controller.TestSmtp(new TestSmtpRequest("qa@example.test"), protection, default))
            .Should().BeOfType<OkObjectResult>().Subject;
        var body = JsonSerializer.SerializeToElement(ok.Value, Web);

        body.GetProperty("sent").GetBoolean().Should().BeFalse();
        body.GetProperty("captured").GetBoolean().Should().BeTrue();
        body.GetProperty("message").GetString().Should().Contain("proves nothing");
    }

    private static NotificationDelivery Row(Guid tenantId, string outcome) => new()
    {
        TenantId = tenantId, Channel = NotificationChannels.Email, EventCode = "PAYSLIP_READY", Outcome = outcome,
        AttemptCount = 1, DedupeKey = Guid.NewGuid().ToString("N"), IdempotencyKey = Guid.NewGuid().ToString("N"),
    };
}
