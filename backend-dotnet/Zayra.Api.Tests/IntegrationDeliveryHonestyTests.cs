using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Compliance;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Operations;
using Zayra.Api.Infrastructure.Qiwa;
using Zayra.Api.Infrastructure.Reports;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Register item F09: a locally healthy worker is not proof that an email reached anyone or that
/// a Qiwa action reached Qiwa. Each test here pins one place where the product used to claim more
/// than it knew — "sent" with no relay, "Success" for a simulator, a monthly report that gave up
/// for a month over one dropped connection — and every one of them failed on main.
///
/// InMemory EF throughout: no Postgres, no socket, no real mailbox.
/// </summary>
public sealed class IntegrationDeliveryHonestyTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ── SMTP: an unconfigured relay must not look like a sent message ─────────────────────────

    [Fact]
    public async Task An_unconfigured_relay_refuses_the_send_instead_of_returning_as_if_it_was_sent()
    {
        await using var db = InMemory();
        var smtp = new SmtpEmailService(db, DataProtectionProvider.Create("f09"), Config(),
            NullLogger<SmtpEmailService>.Instance);

        // On main this returned normally after a LogWarning, so any caller that did not happen to
        // pre-check IsConfiguredAsync reported the message as sent.
        var send = () => smtp.SendAsync(Guid.NewGuid(), "person@example.test", "Person", "Subject", "<p>x</p>");

        await send.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not configured*");
    }

    // ── Scheduled reports ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_scheduled_report_with_no_relay_ends_NotConfigured_not_Success_or_a_generic_failure()
    {
        await using var db = InMemory();
        await SeedScheduleAsync(db, "Monthly");
        using var services = ReportServices(db, new UnconfiguredEmail());

        await ReportWorker(services).ProcessOnceAsync(CancellationToken.None);

        var execution = await db.ReportExecutionLogs.AsNoTracking().SingleAsync();
        execution.Status.Should().Be("NotConfigured");
        execution.ErrorMessage.Should().Contain("not set up");

        var schedule = await db.ReportSchedules.AsNoTracking().SingleAsync();
        schedule.ConsecutiveFailureCount.Should().Be(1);
        schedule.LastFailureReason.Should().Contain("not set up");
    }

    [Fact]
    public async Task A_scheduled_report_whose_relay_is_down_is_retried_within_the_hour_not_next_period()
    {
        await using var db = InMemory();
        await SeedScheduleAsync(db, "Monthly");
        using var services = ReportServices(db, new RelayDownEmail());
        var before = DateTime.UtcNow;

        await ReportWorker(services).ProcessOnceAsync(CancellationToken.None);

        var schedule = await db.ReportSchedules.AsNoTracking().SingleAsync();
        // On main the claim had already moved NextRunAtUtc a month ahead, and a failure left it
        // there: one dropped connection cost the customer their monthly pack.
        schedule.NextRunAtUtc.Should().BeAfter(before);
        schedule.NextRunAtUtc.Should().BeBefore(before.AddHours(1));
        schedule.ConsecutiveFailureCount.Should().Be(1);
        (await db.ReportExecutionLogs.AsNoTracking().SingleAsync()).Status.Should().Be("Failed");
    }

    [Fact]
    public async Task A_scheduled_report_stops_retrying_after_its_budget_and_says_it_gave_up()
    {
        await using var db = InMemory();
        await SeedScheduleAsync(db, "Monthly");
        var email = new RelayDownEmail();
        using var services = ReportServices(db, email);
        var worker = ReportWorker(services);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var due = await db.ReportSchedules.SingleAsync();
            due.NextRunAtUtc = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            await worker.ProcessOnceAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
        }

        var schedule = await db.ReportSchedules.AsNoTracking().SingleAsync();
        schedule.ConsecutiveFailureCount.Should().Be(3);
        schedule.LastFailureReason.Should().Contain("Gave up after 3 attempts");
        // Back on its regular cadence: the next attempt is the next period, not another retry.
        schedule.NextRunAtUtc.Should().BeAfter(DateTime.UtcNow.AddDays(20));
        email.Attempts.Should().Be(3);
    }

    // ── Notification delivery: retries end in a dead letter, not a generic failure ────────────

    [Fact]
    public async Task An_email_that_keeps_failing_transiently_is_dead_lettered_after_its_attempts()
    {
        using var h = new NotificationHarness(new RelayDownEmail());
        await using var db = h.NewDb();
        var (tenantId, userId) = SeedTenantAndUser(db, "aisha@example.test");

        await h.Notifications.EnqueueAsync(new NotificationRequest
        {
            TenantId = tenantId, UserId = userId, EventCode = "PAYSLIP_READY",
            EntityName = "PayrollRun", EntityId = "run-1", Title = "Payslip ready", Message = "Your payslip is ready.",
        }, default);

        for (var attempt = 0; attempt < 6; attempt++)
        {
            await using (var tick = h.NewDb())
            {
                foreach (var row in await tick.NotificationDeliveries.IgnoreQueryFilters()
                             .Where(d => d.Outcome == DeliveryOutcomes.Queued && d.Channel == "Email").ToListAsync())
                    row.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(-1);
                await tick.SaveChangesAsync();
            }
            await h.Worker.DrainOnceAsync(default);
        }

        await using var read = h.NewDb();
        var email = await read.NotificationDeliveries.IgnoreQueryFilters().SingleAsync(d => d.Channel == "Email");
        email.Outcome.Should().Be("dead_letter");
        email.AttemptCount.Should().Be(email.MaxAttempts);
        email.NextAttemptAtUtc.Should().BeNull();
        email.ErrorMessage.Should().Contain("attempts");
    }

    [Fact]
    public async Task An_exhausted_expired_lease_is_a_dead_letter_for_email_but_stays_unknown_for_sms()
    {
        using var h = new NotificationHarness(new RelayDownEmail());
        var tenantId = Guid.NewGuid();
        await using (var seed = h.NewDb())
        {
            foreach (var channel in new[] { NotificationChannels.Email, NotificationChannels.Sms })
            {
                var row = Delivery(tenantId, DeliveryOutcomes.Sending, attempts: 4);
                row.Channel = channel;
                row.UserId = Guid.NewGuid();
                row.LeaseOwner = Guid.NewGuid();
                row.LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
                seed.NotificationDeliveries.Add(row);
            }
            await seed.SaveChangesAsync();
        }

        await h.Worker.DrainOnceAsync(default);

        await using var read = h.NewDb();
        var rows = await read.NotificationDeliveries.IgnoreQueryFilters().ToListAsync();
        rows.Single(r => r.Channel == NotificationChannels.Email).Outcome.Should().Be(DeliveryOutcomes.DeadLetter);
        // A second SMS is a second billed, delivered message: never a requeueable dead letter.
        rows.Single(r => r.Channel == NotificationChannels.Sms).Outcome.Should().Be(DeliveryOutcomes.Unknown);
    }

    // ── Readiness / telemetry: the counts an operator needs ────────────────────────────────────

    [Fact]
    public async Task Readiness_evidence_counts_dead_letters_not_configured_and_retrying_deliveries()
    {
        await using var db = InMemory();
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Queue Tenant", Slug = $"queue-{Guid.NewGuid():N}" });
        db.NotificationDeliveries.AddRange(
            Delivery(tenantId, "dead_letter", attempts: 4),
            Delivery(tenantId, DeliveryOutcomes.NotConfigured, attempts: 1),
            Delivery(tenantId, DeliveryOutcomes.NotConfigured, attempts: 1),
            Delivery(tenantId, DeliveryOutcomes.Queued, attempts: 2),
            Delivery(tenantId, DeliveryOutcomes.Queued, attempts: 0),
            Delivery(tenantId, DeliveryOutcomes.Failed, attempts: 1));
        await db.SaveChangesAsync();

        var telemetry = await ProductionReadinessEvidence.BuildTelemetryAsync(db, Config(), CancellationToken.None);
        var json = JsonSerializer.Serialize(telemetry.Queues, Web);

        json.Should().Contain("\"notificationsDeadLetter\":1");
        json.Should().Contain("\"notificationsNotConfigured\":2");
        json.Should().Contain("\"notificationsRetrying\":1");
        json.Should().Contain("\"notificationsFailed\":1");
    }

    [Fact]
    public async Task Readiness_treats_a_saved_platform_relay_as_configured_smtp()
    {
        await using var db = InMemory();
        db.PlatformConfigEntries.AddRange(
            new PlatformConfigEntry { Key = PlatformSmtpConfig.KeyHost, Value = "smtp.relay.example.test" },
            new PlatformConfigEntry { Key = PlatformSmtpConfig.KeyFromAddress, Value = "noreply@example.test" });
        await db.SaveChangesAsync();

        var telemetry = await ProductionReadinessEvidence.BuildTelemetryAsync(db, Config(), CancellationToken.None);

        // On main the probe looked only at tenant SystemSettings rows, so the relay every tenant
        // falls back to was reported as "not_configured".
        telemetry.Dependencies.Smtp.Mode.Should().Be("configured");
        telemetry.Dependencies.Smtp.Configured.Should().BeTrue();
    }

    [Fact]
    public async Task Readiness_labels_the_qiwa_sandbox_as_a_simulation()
    {
        await using var db = InMemory();

        var telemetry = await ProductionReadinessEvidence.BuildTelemetryAsync(db, Config(), CancellationToken.None);
        var json = JsonSerializer.Serialize(telemetry.Dependencies.Qiwa, Web);

        json.Should().Contain("\"mode\":\"sandbox_adapter\"");
        json.Should().Contain("\"simulated\":true");
        json.Should().Contain("Simulated (sandbox)");
    }

    // ── Qiwa: a simulator's result is never a Success ──────────────────────────────────────────

    [Fact]
    public async Task A_sandbox_qiwa_sync_is_recorded_as_Simulated_not_Success()
    {
        var dbName = Guid.NewGuid().ToString();
        var protection = DataProtectionProvider.Create($"f09-qiwa-{Guid.NewGuid():N}");
        var (tenantId, syncLogId) = await SeedQiwaAsync(dbName, protection);

        var services = new ServiceCollection();
        services.AddDbContext<ZayraDbContext>(o => o.UseInMemoryDatabase(dbName));
        await using var provider = services.BuildServiceProvider();
        var worker = new QiwaSyncWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            new SandboxQiwaApiAdapter(NullLogger<SandboxQiwaApiAdapter>.Instance), new QiwaOAuthTokenCache(),
            protection, NullLogger<QiwaSyncWorker>.Instance);

        await worker.ProcessOnceAsync(CancellationToken.None);

        await using var read = InMemory(dbName);
        var log = await read.QiwaSyncLogs.IgnoreQueryFilters().SingleAsync(l => l.Id == syncLogId);
        log.Status.Should().Be("Simulated");
        log.CompletedAtUtc.Should().NotBeNull();
        (await read.Employees.IgnoreQueryFilters().SingleAsync(e => e.TenantId == tenantId))
            .QiwaSyncStatus.Should().Be(QiwaSyncStatuses.Simulated);
    }

    [Fact]
    public async Task No_qiwa_summary_reports_a_simulated_run_as_the_last_successful_sync()
    {
        await using var db = InMemory();
        var tenantId = Guid.NewGuid();
        // A row written by main's worker under the sandbox: Status "Success", simulator envelope.
        db.QiwaSyncLogs.Add(new QiwaSyncLog
        {
            TenantId = tenantId, EmployeeId = 1, Direction = "Push", Status = QiwaSyncLogStatuses.Success,
            CompletedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            ResponsePayloadJson = "{\"status\":\"simulated\"," + SandboxQiwaApiAdapter.SimulationMarker + ",\"filed_with_qiwa\":false}",
        });
        await db.SaveChangesAsync();

        var summary = await new QiwaIntegrationService(db, NullLogger<QiwaIntegrationService>.Instance,
            DataProtectionProvider.Create("f09")).GetComplianceSummaryAsync(tenantId);
        summary.LastSuccessfulSync.Should().BeNull("nothing has ever been filed with Qiwa");

        var dashboard = await new SaudiComplianceDashboardService(db, TestReconciliation.For(db))
            .BuildAsync(tenantId, CancellationToken.None);
        dashboard.Qiwa.LastSuccessfulSync.Should().BeNull("a simulator run is not a sync");
    }

    // ── Safe test delivery: capture mode and the permitted-recipient list ─────────────────────

    [Fact]
    public async Task Capture_mode_never_reaches_a_configured_relay_and_says_captured()
    {
        await using var db = InMemory();
        // A relay IS configured — at an address nothing listens on. Reaching it would throw or hang.
        db.PlatformConfigEntries.AddRange(
            new PlatformConfigEntry { Key = PlatformSmtpConfig.KeyHost, Value = "127.0.0.1" },
            new PlatformConfigEntry { Key = PlatformSmtpConfig.KeyPort, Value = "1" },
            new PlatformConfigEntry { Key = PlatformSmtpConfig.KeyFromAddress, Value = "noreply@example.test" });
        await db.SaveChangesAsync();
        var dir = Path.Combine(Path.GetTempPath(), $"f09-capture-{Guid.NewGuid():N}");
        var to = $"employee-{Guid.NewGuid():N}@example.test";
        try
        {
            var smtp = new SmtpEmailService(db, DataProtectionProvider.Create("f09"),
                Config(new Dictionary<string, string?>
                {
                    [EmailTransportPolicy.ModeKey] = "capture",
                    [EmailTransportPolicy.CaptureDirectoryKey] = dir,
                }),
                NullLogger<SmtpEmailService>.Instance);

            (await smtp.IsConfiguredAsync(Guid.NewGuid())).Should().BeTrue("capture mode handles every send");
            var result = await smtp.DeliverAsync(Guid.NewGuid(), to, "Employee", "Your payslip", "<p>payslip</p>",
                [new EmailAttachment("payslip.pdf", [1, 2, 3], "application/pdf")]);

            result.Status.Should().Be(EmailDeliveryStatus.Captured);
            result.ReachedARelay.Should().BeFalse();
            var captured = EmailCaptureSink.To(to).Should().ContainSingle().Subject;
            captured.Subject.Should().Be("Your payslip");
            captured.AttachmentNames.Should().Equal("payslip.pdf");
            Directory.GetFiles(dir, "*.eml").Should().ContainSingle("e2e and CI read captured mail from disk");

            // The legacy call is captured too — and does not throw, because the message was handled.
            await smtp.SendAsync(Guid.NewGuid(), to, "Employee", "Second", "<p>x</p>");
            EmailCaptureSink.To(to).Should().HaveCount(2);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task An_unrecognised_delivery_mode_fails_safe_to_capture()
    {
        await using var db = InMemory();
        var to = $"typo-{Guid.NewGuid():N}@example.test";
        var smtp = new SmtpEmailService(db, DataProtectionProvider.Create("f09"),
            Config(new Dictionary<string, string?> { [EmailTransportPolicy.ModeKey] = "sandbox" }),
            NullLogger<SmtpEmailService>.Instance);

        var result = await smtp.DeliverAsync(Guid.NewGuid(), to, "Person", "Hello", "<p>x</p>");

        result.Status.Should().Be(EmailDeliveryStatus.Captured);
        EmailTransportPolicy.From(Config(new Dictionary<string, string?> { [EmailTransportPolicy.ModeKey] = "sandbox" }))
            .Describe().Should().Contain("capture is assumed");
    }

    [Fact]
    public async Task Permitted_test_delivery_captures_everyone_off_the_list_without_touching_the_relay()
    {
        await using var db = InMemory();
        db.PlatformConfigEntries.AddRange(
            new PlatformConfigEntry { Key = PlatformSmtpConfig.KeyHost, Value = "127.0.0.1" },
            new PlatformConfigEntry { Key = PlatformSmtpConfig.KeyPort, Value = "1" },
            new PlatformConfigEntry { Key = PlatformSmtpConfig.KeyFromAddress, Value = "noreply@example.test" });
        await db.SaveChangesAsync();
        var config = Config(new Dictionary<string, string?>
        {
            [EmailTransportPolicy.AllowedRecipientsKey] = "@qa.example.test, owner@example.test",
        });
        var smtp = new SmtpEmailService(db, DataProtectionProvider.Create("f09"), config, NullLogger<SmtpEmailService>.Instance);
        var realEmployee = $"real-{Guid.NewGuid():N}@customer.example";

        var result = await smtp.DeliverAsync(Guid.NewGuid(), realEmployee, "Real Employee", "Payslip", "<p>x</p>");

        result.Status.Should().Be(EmailDeliveryStatus.Captured);
        EmailCaptureSink.To(realEmployee).Should().ContainSingle();
        var policy = EmailTransportPolicy.From(config);
        policy.Permits("anyone@qa.example.test").Should().BeTrue();
        policy.Permits("OWNER@example.test").Should().BeTrue();
        policy.Permits("someone.else@example.test").Should().BeFalse();
        policy.Permits("anyone@qa.example.test.evil.example").Should().BeFalse();
    }

    [Fact]
    public async Task A_notification_in_capture_mode_is_recorded_as_captured_never_sent()
    {
        var smtpConfig = Config(new Dictionary<string, string?> { [EmailTransportPolicy.ModeKey] = "capture" });
        using var h = new NotificationHarness(null, smtpConfig);
        await using var db = h.NewDb();
        var (tenantId, userId) = SeedTenantAndUser(db, $"aisha-{Guid.NewGuid():N}@example.test");

        await h.Notifications.EnqueueAsync(new NotificationRequest
        {
            TenantId = tenantId, UserId = userId, EventCode = "PAYSLIP_READY",
            EntityName = "PayrollRun", EntityId = "run-1", Title = "Payslip ready", Message = "Your payslip is ready.",
        }, default);
        await using (var tick = h.NewDb())
        {
            foreach (var row in await tick.NotificationDeliveries.IgnoreQueryFilters().Where(d => d.Channel == "Email").ToListAsync())
                row.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(-1);
            await tick.SaveChangesAsync();
        }
        await h.Worker.DrainOnceAsync(default);

        await using var read = h.NewDb();
        var email = await read.NotificationDeliveries.IgnoreQueryFilters().SingleAsync(d => d.Channel == "Email");
        email.Outcome.Should().Be(DeliveryOutcomes.Captured);
        email.NextAttemptAtUtc.Should().BeNull();
        DeliveryOutcomes.Describe(email.Channel, email.Outcome).Should().Be("Captured by test mode, not sent");
        DeliveryOutcomes.NeedsAttention(email.Outcome).Should().BeFalse();
    }

    [Fact]
    public void An_email_the_relay_accepted_is_labelled_accepted_not_delivered()
    {
        DeliveryOutcomes.Describe("Email", DeliveryOutcomes.Sent).Should().Be("Accepted by mail server");
        DeliveryOutcomes.Describe("Email", DeliveryOutcomes.NotConfigured).Should().Be("Not sent: Email is not set up");
        DeliveryOutcomes.Describe("Email", DeliveryOutcomes.DeadLetter, 4, 4).Should().Be("Gave up after 4 attempts");
        DeliveryOutcomes.Describe("Email", DeliveryOutcomes.Queued, 1, 4).Should().Be("Retrying (attempt 2 of 4)");
        DeliveryOutcomes.NeedsAttention(DeliveryOutcomes.DeadLetter).Should().BeTrue();
    }

    [Fact]
    public async Task A_scheduled_report_in_capture_mode_ends_Captured_and_is_not_a_failure()
    {
        await using var db = InMemory();
        await SeedScheduleAsync(db, "Monthly");
        var smtp = new SmtpEmailService(db, DataProtectionProvider.Create("f09"),
            Config(new Dictionary<string, string?> { [EmailTransportPolicy.ModeKey] = "capture" }),
            NullLogger<SmtpEmailService>.Instance);
        using var services = ReportServices(db, smtp);

        await ReportWorker(services).ProcessOnceAsync(CancellationToken.None);

        var execution = await db.ReportExecutionLogs.AsNoTracking().SingleAsync();
        execution.Status.Should().Be("Captured");
        execution.ErrorMessage.Should().Contain("not sent");
        (await db.ReportSchedules.AsNoTracking().SingleAsync()).ConsecutiveFailureCount.Should().Be(0);
        EmailCaptureSink.To("recipient@example.test").Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_report_accepted_for_some_recipients_is_recorded_as_partial_and_not_retried()
    {
        await using var db = InMemory();
        await SeedScheduleAsync(db, "Monthly");
        var schedule = await db.ReportSchedules.SingleAsync();
        schedule.Recipients = "first@example.test,second@example.test";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var email = new FailsForEmail("second@example.test");
        using var services = ReportServices(db, email);
        var before = DateTime.UtcNow;

        await ReportWorker(services).ProcessOnceAsync(CancellationToken.None);

        var execution = await db.ReportExecutionLogs.AsNoTracking().SingleAsync();
        execution.Status.Should().Be("Failed");
        execution.ErrorMessage.Should().Contain("1 of 2").And.Contain("second@example.test").And.Contain("Not retried");
        // A retry would send first@ a second copy, so the next run is the next period.
        (await db.ReportSchedules.AsNoTracking().SingleAsync()).NextRunAtUtc.Should().BeAfter(before.AddDays(20));
        email.Accepted.Should().Equal("first@example.test");
    }

    // ── Readiness in the other SMTP shapes ─────────────────────────────────────────────────────

    [Fact]
    public async Task Readiness_reports_capture_mode_as_simulated_and_tenant_only_relays_as_partial()
    {
        await using var db = InMemory();
        var capture = await ProductionReadinessEvidence.SmtpDependencyAsync(db,
            Config(new Dictionary<string, string?> { [EmailTransportPolicy.ModeKey] = "capture" }), CancellationToken.None);
        capture.Mode.Should().Be("capture");
        capture.Simulated.Should().BeTrue();

        db.SystemSettings.Add(new SystemSetting
        {
            TenantId = Guid.NewGuid(), Category = "Email", SettingKey = "Smtp.Host", SettingValue = "smtp.one-tenant.test",
        });
        await db.SaveChangesAsync();
        var tenantOnly = await ProductionReadinessEvidence.SmtpDependencyAsync(db, Config(), CancellationToken.None);
        tenantOnly.Mode.Should().Be("tenant_relays_only");
        tenantOnly.Detail.Should().Contain("1 workspace(s)").And.Contain("NotConfigured");

        var none = await ProductionReadinessEvidence.SmtpDependencyAsync(InMemory(), Config(), CancellationToken.None);
        none.Mode.Should().Be("not_configured");
        none.Configured.Should().BeFalse();
    }

    // ── Requeue ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_admin_can_requeue_dead_letters_but_never_an_unconfirmed_sms_or_another_tenants_row()
    {
        await using var db = InMemory();
        var tenantId = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var dead = Delivery(tenantId, DeliveryOutcomes.DeadLetter, attempts: 4);
        dead.UserId = Guid.NewGuid();
        var unknownSms = Delivery(tenantId, DeliveryOutcomes.Unknown, attempts: 1);
        unknownSms.Channel = NotificationChannels.Sms;
        unknownSms.EmployeeId = 3;
        var external = Delivery(tenantId, DeliveryOutcomes.DeadLetter, attempts: 4);
        var foreign = Delivery(otherTenant, DeliveryOutcomes.DeadLetter, attempts: 4);
        foreign.UserId = Guid.NewGuid();
        db.NotificationDeliveries.AddRange(dead, unknownSms, external, foreign);
        await db.SaveChangesAsync();

        var controller = new Zayra.Api.Controllers.NotificationsController(db)
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
                {
                    User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                        [new System.Security.Claims.Claim("tenant_id", tenantId.ToString())], "Test")),
                },
            },
        };

        var result = await controller.RequeueDeliveries(
            new Zayra.Api.Controllers.NotificationsController.RequeueDeliveriesRequest(null, DeliveryOutcomes.DeadLetter, null),
            CancellationToken.None);
        var body = JsonSerializer.Serialize(((Microsoft.AspNetCore.Mvc.OkObjectResult)result).Value, Web);
        body.Should().Contain("\"requeued\":1").And.Contain("\"skippedNoAddress\":1");

        var refused = await controller.RequeueDeliveries(
            new Zayra.Api.Controllers.NotificationsController.RequeueDeliveriesRequest(null, DeliveryOutcomes.Unknown, null),
            CancellationToken.None);
        refused.Should().BeOfType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>();

        db.ChangeTracker.Clear();
        var requeued = await db.NotificationDeliveries.SingleAsync(d => d.Id == dead.Id);
        requeued.Outcome.Should().Be(DeliveryOutcomes.Queued);
        requeued.AttemptCount.Should().Be(0);
        requeued.NextAttemptAtUtc.Should().NotBeNull();
        (await db.NotificationDeliveries.SingleAsync(d => d.Id == unknownSms.Id)).Outcome.Should().Be(DeliveryOutcomes.Unknown);
        (await db.NotificationDeliveries.SingleAsync(d => d.Id == foreign.Id)).Outcome.Should().Be(DeliveryOutcomes.DeadLetter);
        (await db.AuditLogs.CountAsync(a => a.Action == "notifications.deliveries_requeued")).Should().Be(1);
    }

    // ── Qiwa: every surface carries the simulation label ──────────────────────────────────────

    [Fact]
    public async Task The_sync_log_api_labels_simulated_runs_including_rows_written_before_the_fix()
    {
        await using var db = InMemory();
        var tenantId = Guid.NewGuid();
        db.QiwaSyncLogs.AddRange(
            new QiwaSyncLog
            {
                TenantId = tenantId, EmployeeId = 1, Status = QiwaSyncLogStatuses.Success,
                ResponsePayloadJson = "{\"status\":\"simulated\"," + SandboxQiwaApiAdapter.SimulationMarker + "}",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-2),
            },
            new QiwaSyncLog { TenantId = tenantId, EmployeeId = 2, Status = QiwaSyncLogStatuses.Simulated });
        await db.SaveChangesAsync();
        var controller = new Zayra.Api.Controllers.QiwaController(
            new QiwaIntegrationService(db, NullLogger<QiwaIntegrationService>.Instance, DataProtectionProvider.Create("f09")),
            new SandboxQiwaApiAdapter(NullLogger<SandboxQiwaApiAdapter>.Instance))
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
                {
                    User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                    [
                        new System.Security.Claims.Claim("tenant_id", tenantId.ToString()),
                        new System.Security.Claims.Claim("permission", "qiwa.read"),
                    ], "Test")),
                },
            },
        };

        var logs = JsonSerializer.Serialize(((Microsoft.AspNetCore.Mvc.OkObjectResult)
            await controller.GetSyncLogs(null, 1, 25, CancellationToken.None)).Value, Web);
        var summary = JsonSerializer.Serialize(((Microsoft.AspNetCore.Mvc.OkObjectResult)
            await controller.GetComplianceSummary(CancellationToken.None)).Value, Web);

        logs.Should().NotContain("\"status\":\"Success\"");
        System.Text.RegularExpressions.Regex.Matches(logs, "\"statusLabel\":\"Simulated \\(sandbox\\)\"").Count.Should().Be(2);
        logs.Should().Contain("\"filedWithQiwa\":false").And.NotContain("\"filedWithQiwa\":true");
        summary.Should().Contain("\"lastSuccessfulSync\":null").And.Contain("\"integrationMode\":\"Simulated (sandbox)\"");
        summary.Should().Contain("\"isLiveIntegration\":false");
    }

    [Fact]
    public async Task The_compliance_dashboard_names_the_simulation_and_counts_only_live_filings()
    {
        await using var db = InMemory();
        var tenantId = Guid.NewGuid();
        var filedAt = DateTime.UtcNow.AddHours(-1);
        db.QiwaSyncLogs.AddRange(
            new QiwaSyncLog { TenantId = tenantId, EmployeeId = 1, Status = QiwaSyncLogStatuses.Success,
                ResponsePayloadJson = "{\"status\":\"synced\"}", CompletedAtUtc = filedAt },
            new QiwaSyncLog { TenantId = tenantId, EmployeeId = 1, Status = QiwaSyncLogStatuses.Simulated,
                CompletedAtUtc = DateTime.UtcNow });
        db.QiwaApiCredentials.Add(new QiwaApiCredential { TenantId = tenantId, ClientId = "c", EncryptedClientSecret = "x" });
        db.QiwaTenantConnections.Add(new QiwaTenantConnection
        {
            TenantId = tenantId, EstablishmentId = "7000123456", Status = QiwaConnectionStatuses.Simulated,
        });
        await db.SaveChangesAsync();

        var simulated = await new SaudiComplianceDashboardService(db, TestReconciliation.For(db),
            new SandboxQiwaApiAdapter(NullLogger<SandboxQiwaApiAdapter>.Instance)).BuildAsync(tenantId, CancellationToken.None);
        simulated.Qiwa.IsLiveIntegration.Should().BeFalse();
        simulated.Qiwa.IntegrationMode.Should().Be("Simulated (sandbox)");
        simulated.Qiwa.LastSuccessfulSync.Should().BeCloseTo(filedAt, TimeSpan.FromSeconds(1));
        simulated.Qiwa.LastSimulatedSync.Should().NotBeNull();
        simulated.ActionItems.Should().Contain(a => a.Id == "qiwa_simulated" && !a.CanAct);

        var live = await new SaudiComplianceDashboardService(db, TestReconciliation.For(db), new LiveStubAdapter())
            .BuildAsync(tenantId, CancellationToken.None);
        live.Qiwa.IsLiveIntegration.Should().BeTrue();
        live.Qiwa.IntegrationMode.Should().Be("Live");
        live.ActionItems.Should().NotContain(a => a.Id == "qiwa_simulated");
    }

    // ── Harness ────────────────────────────────────────────────────────────────────────────────

    private static ZayraDbContext InMemory(string? name = null) => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseInMemoryDatabase(name ?? Guid.NewGuid().ToString()).Options);

    private static IConfiguration Config(IDictionary<string, string?>? values = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(values ?? new Dictionary<string, string?>()).Build();

    private static ReportScheduleWorker ReportWorker(ServiceProvider services) =>
        new(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ReportScheduleWorker>.Instance);

    private static ServiceProvider ReportServices(ZayraDbContext db, IEmailService email)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(email);
        services.AddSingleton<Zayra.Api.Application.Common.IDataScopeService>(new DataScopeService(db));
        services.AddSingleton<INotificationService>(TestNotifications.For(db));
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The owner and every possible recipient are active users in one role holding reports.schedule
    /// and employees.read, because main only mails a scheduled report to somebody who could open it
    /// by hand (ReportAudience). These tests are about what happens AFTER that check.
    /// </summary>
    private static async Task SeedScheduleAsync(ZayraDbContext db, string frequency)
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var permissionId = Guid.NewGuid();
        var employeeReadPermissionId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Reports Tenant", Slug = $"reports-{Guid.NewGuid():N}" });
        db.Users.Add(new User
        {
            Id = userId, TenantId = tenantId, Email = "owner@example.test", NormalizedEmail = "OWNER@EXAMPLE.TEST",
            FullName = "Report Owner", PasswordHash = "hash", IsActive = true, IsGroupScope = true,
        });
        db.Roles.Add(new Role { Id = roleId, TenantId = tenantId, Name = "Analyst", NormalizedName = "ANALYST" });
        db.Permissions.AddRange(
            new Permission { Id = permissionId, Key = "reports.schedule", Module = "Reports" },
            new Permission { Id = employeeReadPermissionId, Key = "employees.read", Module = "Employees" });
        db.UserRoles.Add(new UserRole { UserId = userId, RoleId = roleId });
        db.RolePermissions.AddRange(
            new RolePermission { RoleId = roleId, PermissionId = permissionId },
            new RolePermission { RoleId = roleId, PermissionId = employeeReadPermissionId });
        foreach (var colleague in new[] { "recipient@example.test", "first@example.test", "second@example.test" })
        {
            var colleagueId = Guid.NewGuid();
            db.Users.Add(new User
            {
                Id = colleagueId, TenantId = tenantId, Email = colleague, NormalizedEmail = colleague.ToUpperInvariant(),
                FullName = "Report Recipient", PasswordHash = "hash", IsActive = true, IsGroupScope = true,
            });
            db.UserRoles.Add(new UserRole { UserId = colleagueId, RoleId = roleId });
        }
        db.Employees.Add(new Employee
        {
            Id = 1, TenantId = tenantId, EmployeeCode = "E-1", FullName = "Engineer", Department = "Engineering",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1),
        });
        db.ReportSchedules.Add(new ReportSchedule
        {
            TenantId = tenantId, CreatedBy = userId, ReportKey = "hr.headcount", ReportName = "Headcount",
            Category = "HR", FiltersJson = "{}", Frequency = frequency, DeliveryMethod = "Email",
            Recipients = "recipient@example.test", ExportFormat = "JSON", IsActive = true,
            NextRunAtUtc = DateTime.UtcNow.AddMinutes(-1),
        });
        await db.SaveChangesAsync();
    }

    private static (Guid TenantId, Guid UserId) SeedTenantAndUser(ZayraDbContext db, string email)
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        db.Users.Add(new User { Id = userId, TenantId = tenantId, Email = email, FullName = "Aisha Rahman" });
        db.SaveChanges();
        return (tenantId, userId);
    }

    private static NotificationDelivery Delivery(Guid tenantId, string outcome, int attempts) => new()
    {
        TenantId = tenantId, Channel = NotificationChannels.Email, EventCode = "PAYSLIP_READY", Outcome = outcome,
        AttemptCount = attempts, DedupeKey = Guid.NewGuid().ToString("N"), IdempotencyKey = Guid.NewGuid().ToString("N"),
        NextAttemptAtUtc = outcome == DeliveryOutcomes.Queued ? DateTime.UtcNow.AddMinutes(5) : null,
    };

    private static async Task<(Guid TenantId, Guid SyncLogId)> SeedQiwaAsync(string dbName, IDataProtectionProvider protection)
    {
        await using var db = InMemory(dbName);
        var tenantId = Guid.NewGuid();
        var syncLogId = Guid.NewGuid();
        db.Employees.Add(new Employee
        {
            Id = 7001, TenantId = tenantId, EmployeeCode = "Q-1", FullName = "Qiwa Ready", Status = "Active",
            SaudiOrNonSaudi = "Saudi", IdType = "NationalId", IdNumber = "1012345678", Nationality = "Saudi",
            OccupationCode = "2421", EstablishmentId = "7000123456", WorkLocationId = "WL-1", ContractReference = "C-1",
        });
        db.QiwaTenantConnections.Add(new QiwaTenantConnection
        {
            TenantId = tenantId, EstablishmentId = "7000123456", Environment = "sandbox",
        });
        db.QiwaApiCredentials.Add(new QiwaApiCredential
        {
            TenantId = tenantId, ClientId = "client", Environment = "sandbox",
            EncryptedClientSecret = protection.CreateProtector(QiwaIntegrationService.SecretPurpose).Protect("secret"),
        });
        db.QiwaSyncLogs.Add(new QiwaSyncLog
        {
            Id = syncLogId, TenantId = tenantId, EmployeeId = 7001, Direction = "Push",
            Status = QiwaSyncLogStatuses.Pending,
        });
        await db.SaveChangesAsync();
        return (tenantId, syncLogId);
    }

    /// <summary>The same registrations NotificationDeliveryTests uses, minus what these tests never touch.</summary>
    private sealed class NotificationHarness : IDisposable
    {
        private readonly ServiceProvider _provider;

        /// <param name="email">A fake; null means the REAL SmtpEmailService over <paramref name="config"/>.</param>
        public NotificationHarness(IEmailService? email, IConfiguration? config = null)
        {
            var dbName = Guid.NewGuid().ToString();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMemoryCache();
            services.AddDataProtection();
            services.AddDbContext<ZayraDbContext>(o => o.UseInMemoryDatabase(dbName));
            services.AddSingleton(config ?? Config());
            if (email is null) services.AddScoped<IEmailService, SmtpEmailService>();
            else services.AddSingleton(email);
            services.AddScoped<INotificationRecipientResolver, NotificationRecipientResolver>();
            services.AddScoped<INotificationProviderConfigReader, NotificationProviderConfigReader>();
            services.AddScoped<ISmsProvider, NullSmsProvider>();
            services.AddScoped<IWhatsAppProvider, NullWhatsAppProvider>();
            services.AddScoped<IPushProvider, NullPushProvider>();
            services.AddScoped<INotificationChannelDispatcher, EmailChannelDispatcher>();
            services.AddScoped<INotificationChannelDispatcher, SmsChannelDispatcher>();
            services.AddScoped<INotificationChannelDispatcher, WhatsAppChannelDispatcher>();
            services.AddScoped<INotificationChannelDispatcher, PushChannelDispatcher>();
            services.AddScoped<INotificationService, NotificationService>();
            services.AddScoped<Zayra.Api.Infrastructure.Modules.ITenantModuleService,
                               Zayra.Api.Infrastructure.Modules.TenantModuleService>();
            _provider = services.BuildServiceProvider();
        }

        public ZayraDbContext NewDb() => _provider.CreateScope().ServiceProvider.GetRequiredService<ZayraDbContext>();
        public INotificationService Notifications => _provider.CreateScope().ServiceProvider.GetRequiredService<INotificationService>();
        public NotificationDeliveryWorker Worker =>
            new(_provider.GetRequiredService<IServiceScopeFactory>(),
                _provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<NotificationDeliveryWorker>>());

        public void Dispose() => _provider.Dispose();
    }

    private sealed class UnconfiguredEmail : IEmailService
    {
        public Task SendAsync(string toAddress, string toName, string subject, string htmlBody,
            IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("must not be called when unconfigured");
        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    /// <summary>Accepts every recipient except one, which the relay refuses mid-run.</summary>
    private sealed class FailsForEmail(string refused) : IEmailService
    {
        public List<string> Accepted { get; } = [];
        public Task SendAsync(string toAddress, string toName, string subject, string htmlBody,
            IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
        {
            if (toAddress == refused) throw new IOException("550 mailbox unavailable");
            Accepted.Add(toAddress);
            return Task.CompletedTask;
        }
        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class LiveStubAdapter : IQiwaApiAdapter
    {
        public string AdapterName => "live-stub";
        public bool IsLiveIntegration => true;
        public Task<QiwaApiResult> PushEmployeeAsync(string accessToken, QiwaEmployeePayload payload, Guid idempotencyKey, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<QiwaApiResult> GetEmployeeStatusAsync(string accessToken, string establishmentId, string employeeIdNumber, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<string?> AcquireAccessTokenAsync(string clientId, string clientSecret, string environment, CancellationToken ct)
            => throw new NotSupportedException();
    }

    /// <summary>A configured relay that drops every connection — the transient failure a retry exists for.</summary>
    private sealed class RelayDownEmail : IEmailService
    {
        public int Attempts;
        public Task SendAsync(string toAddress, string toName, string subject, string htmlBody,
            IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Attempts);
            throw new IOException("The relay closed the connection.");
        }
        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
