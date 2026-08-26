using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zayra.Api.Controllers.Admin;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// POD-D5 — notification DELIVERY. Covers the five properties the pod exists to guarantee:
/// (1) in-app is never lost and reaches the feed the audience actually reads,
/// (2) "not configured" is recorded rather than silent,
/// (3) a retry can never deliver twice,
/// (4) short-channel bodies never carry payroll figures,
/// (5) nothing ever crosses a tenant boundary.
/// InMemory EF — no Postgres/Docker required.
/// </summary>
public class NotificationDeliveryTests
{
    // ── Harness ───────────────────────────────────────────────────────────────

    private sealed class Harness : IDisposable
    {
        public ServiceProvider Provider { get; }
        public string DbName { get; }

        public Harness(IEmailService? email = null, ISmsProvider? sms = null, IPushProvider? push = null)
        {
            DbName = Guid.NewGuid().ToString();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMemoryCache();
            services.AddDataProtection();
            services.AddDbContext<ZayraDbContext>(o => o.UseInMemoryDatabase(DbName));
            services.AddSingleton(email ?? new UnconfiguredEmailService());
            services.AddScoped<INotificationRecipientResolver, NotificationRecipientResolver>();
            services.AddScoped<INotificationProviderConfigReader, NotificationProviderConfigReader>();
            if (sms is null) services.AddScoped<ISmsProvider, NullSmsProvider>();
            else services.AddSingleton(sms);
            services.AddScoped<IWhatsAppProvider, NullWhatsAppProvider>();
            if (push is null) services.AddScoped<IPushProvider, NullPushProvider>();
            else services.AddSingleton(push);
            services.AddScoped<INotificationChannelDispatcher, EmailChannelDispatcher>();
            services.AddScoped<INotificationChannelDispatcher, SmsChannelDispatcher>();
            services.AddScoped<INotificationChannelDispatcher, WhatsAppChannelDispatcher>();
            services.AddScoped<INotificationChannelDispatcher, PushChannelDispatcher>();
            services.AddScoped<INotificationService, NotificationService>();
            Provider = services.BuildServiceProvider();
        }

        public ZayraDbContext NewDb() => Provider.CreateScope().ServiceProvider.GetRequiredService<ZayraDbContext>();
        public INotificationService Notifications => Provider.CreateScope().ServiceProvider.GetRequiredService<INotificationService>();
        public NotificationDeliveryWorker Worker =>
            new(Provider.GetRequiredService<IServiceScopeFactory>(),
                Provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<NotificationDeliveryWorker>>());
        public ComplianceReminderWorker ReminderWorker =>
            new(Provider.GetRequiredService<IServiceScopeFactory>(),
                Provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ComplianceReminderWorker>>());

        public void Dispose() => Provider.Dispose();
    }

    [Fact]
    public async Task DueComplianceReminder_IsEnqueuedOnceAndMarkedSentOnlyWithOutboxEvidence()
    {
        using var h = new Harness();
        await using var db = h.NewDb();
        var tenantId = SeedTenantUserAndEmployee(db, "compliance@example.com", "+971500000001");
        var employee = await db.Employees.SingleAsync(e => e.TenantId == tenantId);
        var reminder = new ComplianceReminder
        {
            TenantId = tenantId, EmployeeId = employee.PublicId, EmployeeName = "untrusted snapshot",
            ReminderType = "PassportExpiry", DocumentType = "Passport",
            ExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            ScheduledAtUtc = DateTime.UtcNow.AddMinutes(-1), Status = "Pending",
        };
        db.ComplianceReminders.Add(reminder);
        await db.SaveChangesAsync();

        (await h.ReminderWorker.DrainOnceAsync(CancellationToken.None)).Should().Be(1);
        db.ChangeTracker.Clear();
        var sent = await db.ComplianceReminders.SingleAsync(r => r.Id == reminder.Id);
        sent.Status.Should().Be("Sent");
        sent.SentAtUtc.Should().NotBeNull();
        (await db.NotificationDeliveries.CountAsync(d => d.TenantId == tenantId
            && d.EntityName == "ComplianceReminder" && d.EntityId == reminder.Id.ToString())).Should().BeGreaterThan(0);
        (await db.EmployeeNotifications.CountAsync(n => n.TenantId == tenantId && n.EmployeeId == employee.Id)).Should().Be(1);

        var deliveryCount = await db.NotificationDeliveries.CountAsync(d => d.TenantId == tenantId);
        (await h.ReminderWorker.DrainOnceAsync(CancellationToken.None)).Should().Be(0);
        (await db.NotificationDeliveries.CountAsync(d => d.TenantId == tenantId)).Should().Be(deliveryCount);
    }

    private sealed class UnconfiguredEmailService : IEmailService
    {
        public Task SendAsync(string toAddress, string toName, string subject, string htmlBody,
            IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("must not be called when unconfigured");
        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class RecordingEmailService : IEmailService
    {
        public List<(Guid TenantId, string To, string Subject, string Body)> Sent { get; } = [];
        public Task SendAsync(string toAddress, string toName, string subject, string htmlBody,
            IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
        {
            Sent.Add((Guid.Empty, toAddress, subject, htmlBody));
            return Task.CompletedTask;
        }
        public Task SendAsync(Guid tenantId, string toAddress, string toName, string subject, string htmlBody,
            IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
        {
            Sent.Add((tenantId, toAddress, subject, htmlBody));
            return Task.CompletedTask;
        }
        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> IsConfiguredAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    /// <summary>Provider that always answers "we don't know if it arrived" — the duplicate-send trap.</summary>
    private sealed class AmbiguousSmsProvider : ISmsProvider
    {
        public int Calls;
        public string Name => "fake-sms";
        public Task<bool> IsConfiguredAsync(Guid tenantId, CancellationToken ct) => Task.FromResult(true);
        public Task<ProviderSendResult> SendAsync(ProviderMessage message, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new ProviderSendResult(ProviderSendStatus.Ambiguous, ErrorCode: "timeout",
                ErrorMessage: $"no response for {message.Destination}"));
        }
    }

    private sealed class CapturingSmsProvider : ISmsProvider
    {
        public List<ProviderMessage> Sent { get; } = [];
        public string Name => "fake-sms";
        public Task<bool> IsConfiguredAsync(Guid tenantId, CancellationToken ct) => Task.FromResult(true);
        public Task<ProviderSendResult> SendAsync(ProviderMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.FromResult(new ProviderSendResult(ProviderSendStatus.Sent, "ref-" + Sent.Count));
        }
    }

    private static Guid SeedTenantUserAndEmployee(ZayraDbContext db, string email, string phone,
        int employeeId = 1, string status = "Active")
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        db.Users.Add(new User { Id = userId, TenantId = tenantId, Email = email, FullName = "Aisha Rahman" });
        db.Employees.Add(new Employee
        {
            Id = employeeId, TenantId = tenantId, UserAccountId = userId, FullName = "Aisha Rahman",
            WorkEmail = email, Phone = phone, Status = status,
        });
        db.SaveChanges();
        return tenantId;
    }

    private static Guid UserIdOf(ZayraDbContext db, Guid tenantId) =>
        db.Users.IgnoreQueryFilters().First(u => u.TenantId == tenantId).Id;

    private static void EnableShortChannel(ZayraDbContext db, Guid tenantId, string code, string channel, string body)
    {
        db.NotificationTemplates.Add(new NotificationTemplate
        {
            TenantId = tenantId, Code = code, Channel = channel, EventType = code,
            SubjectEn = "Payslip ready", BodyEn = body, IsActive = true,
        });
        db.SaveChanges();
    }

    // ── 1. Interpolation: the bug that would have SMS'd a literal "{Period}" ──

    [Fact]
    public void Interpolate_resolves_single_brace_tokens_the_seeded_templates_actually_use()
    {
        var vars = new Dictionary<string, string> { ["Period"] = "2026-07" };

        // TenantProvisioningBundle seeds "Your payslip for {Period} is now available in the portal."
        var rendered = NotificationBodyPolicy.Interpolate(
            "Your payslip for {Period} is now available in the portal.", vars, htmlEncodeValues: false);

        rendered.Should().Be("Your payslip for 2026-07 is now available in the portal.");
        NotificationBodyPolicy.HasUnresolvedPlaceholder(rendered).Should().BeFalse();
    }

    [Fact]
    public void Interpolate_html_encodes_the_value_not_the_template()
    {
        var vars = new Dictionary<string, string> { ["EmployeeName"] = "<script>x</script>" };
        var rendered = NotificationBodyPolicy.Interpolate("Hello {EmployeeName}", vars, htmlEncodeValues: true);
        rendered.Should().NotContain("<script>");
        rendered.Should().Contain("&lt;script&gt;");
    }

    [Fact]
    public async Task Missing_variable_fails_closed_on_email_but_still_reaches_the_in_app_feed()
    {
        using var h = new Harness();
        var db = h.NewDb();
        var tenantId = SeedTenantUserAndEmployee(db, "aisha@acme.test", "+971501234567");
        db.NotificationTemplates.Add(new NotificationTemplate
        {
            TenantId = tenantId, Code = "PAYSLIP_READY", Channel = "InApp", EventType = "PayslipReady",
            SubjectEn = "Your payslip is ready", BodyEn = "Your payslip for {Period} is available.", IsActive = true,
        });
        await db.SaveChangesAsync();

        await h.Notifications.EnqueueAsync(new NotificationRequest
        {
            TenantId = tenantId,
            UserId = UserIdOf(db, tenantId),
            EventCode = "PAYSLIP_READY",
            EntityName = "PayrollRun",
            EntityId = "run-1",
            Variables = new Dictionary<string, string>(),   // {Period} deliberately not supplied
        }, default);

        var read = h.NewDb();
        var email = read.NotificationDeliveries.IgnoreQueryFilters().Single(d => d.Channel == "Email");
        email.Outcome.Should().Be(DeliveryOutcomes.Failed);
        email.ErrorCode.Should().Be("unresolved_placeholder");

        var inApp = read.NotificationDeliveries.IgnoreQueryFilters().Single(d => d.Channel == "InApp");
        inApp.Outcome.Should().Be(DeliveryOutcomes.Sent);
        read.Notifications.IgnoreQueryFilters().Single().Message.Should().NotContain("{Period}");
    }

    // ── 2. Not configured is visible, and in-app still lands ─────────────────

    [Fact]
    public async Task Unconfigured_smtp_produces_a_not_configured_row_and_never_throws()
    {
        using var h = new Harness();   // UnconfiguredEmailService
        var db = h.NewDb();
        var tenantId = SeedTenantUserAndEmployee(db, "aisha@acme.test", "+971501234567");

        await h.Notifications.EnqueueAsync(new NotificationRequest
        {
            TenantId = tenantId, UserId = UserIdOf(db, tenantId), EventCode = "PAYSLIP_READY",
            EntityName = "PayrollRun", EntityId = "run-1", Title = "Payslip ready",
            Message = "Your payslip is ready.",
        }, default);

        await h.Worker.DrainOnceAsync(default);

        var read = h.NewDb();
        var email = read.NotificationDeliveries.IgnoreQueryFilters().Single(d => d.Channel == "Email");
        email.Outcome.Should().Be(DeliveryOutcomes.NotConfigured);
        email.ErrorMessage.Should().NotBeNullOrWhiteSpace();      // the REASON is durable, not a log line
        email.NextAttemptAtUtc.Should().BeNull();                 // terminal, not retried forever
    }

    [Fact]
    public async Task Employee_audience_lands_in_the_feed_ESS_and_mobile_actually_read()
    {
        using var h = new Harness();
        var db = h.NewDb();
        var tenantId = SeedTenantUserAndEmployee(db, "aisha@acme.test", "+971501234567");

        await h.Notifications.EnqueueAsync(new NotificationRequest
        {
            TenantId = tenantId, EmployeeId = 1, EventCode = "PAYSLIP_READY",
            EntityName = "PayrollRun", EntityId = "run-1", Title = "Payslip ready",
            Message = "Your payslip is ready.",
        }, default);

        var read = h.NewDb();
        // EmployeeSelfServiceController and MobileController read EmployeeNotifications, NOT Notifications.
        read.EmployeeNotifications.IgnoreQueryFilters().Should().ContainSingle()
            .Which.EmployeeId.Should().Be(1);
        read.Notifications.IgnoreQueryFilters().Should().ContainSingle();
    }

    // ── 3. Idempotency ───────────────────────────────────────────────────────

    [Fact]
    public void Dedupe_key_is_derived_from_business_identity_only_and_is_stable()
    {
        var tenantId = Guid.NewGuid();
        var a = NotificationService.ComputeDedupeKey(tenantId, "PAYSLIP_READY", "PayrollRun", "run-1",
            "emp:1", "SMS", "Payslip ready", "Your payslip is ready.");
        var b = NotificationService.ComputeDedupeKey(tenantId, "PAYSLIP_READY", "PayrollRun", "run-1",
            "emp:1", "SMS", "Payslip ready", "Your payslip is ready.");
        a.Should().Be(b);

        var other = NotificationService.ComputeDedupeKey(tenantId, "PAYSLIP_READY", "PayrollRun", "run-2",
            "emp:1", "SMS", "Payslip ready", "Your payslip is ready.");
        other.Should().NotBe(a);
    }

    [Fact]
    public async Task Re_entry_for_the_same_run_does_not_enqueue_a_second_delivery()
    {
        using var h = new Harness();
        var db = h.NewDb();
        var tenantId = SeedTenantUserAndEmployee(db, "aisha@acme.test", "+971501234567");
        var userId = UserIdOf(db, tenantId);

        NotificationRequest Request() => new()
        {
            TenantId = tenantId, UserId = userId, EventCode = "PAYSLIP_READY",
            EntityName = "PayrollRun", EntityId = "run-1", Title = "Payslip ready",
            Message = "Your payslip is ready.",
        };

        var first = await h.Notifications.EnqueueAsync(Request(), default);
        var second = await h.Notifications.EnqueueAsync(Request(), default);   // re-Lock / double-click

        first.Should().NotBeEmpty();
        second.Should().BeEmpty();

        var read = h.NewDb();
        read.NotificationDeliveries.IgnoreQueryFilters().Count(d => d.Channel == "Email").Should().Be(1);
        read.Notifications.IgnoreQueryFilters().Should().ContainSingle();   // no duplicate bell entry either
    }

    [Fact]
    public async Task Ambiguous_sms_is_terminal_and_is_never_retried()
    {
        var sms = new AmbiguousSmsProvider();
        using var h = new Harness(sms: sms);
        var db = h.NewDb();
        var tenantId = SeedTenantUserAndEmployee(db, "aisha@acme.test", "+971501234567");
        EnableShortChannel(db, tenantId, "PAYSLIP_READY", "SMS", "Your payslip for {Period} is ready.");
        db.EmployeeNotificationPreferences.Add(new EmployeeNotificationPreference
        { TenantId = tenantId, EmployeeId = 1, SmsEnabled = true });
        await db.SaveChangesAsync();

        await h.Notifications.EnqueueAsync(new NotificationRequest
        {
            TenantId = tenantId, EmployeeId = 1, EventCode = "PAYSLIP_READY",
            EntityName = "PayrollRun", EntityId = "run-1",
            Variables = new Dictionary<string, string> { ["Period"] = "2026-07" },
        }, default);

        await h.Worker.DrainOnceAsync(default);
        await h.Worker.DrainOnceAsync(default);   // a second drain must NOT re-send

        sms.Calls.Should().Be(1);
        var row = h.NewDb().NotificationDeliveries.IgnoreQueryFilters().Single(d => d.Channel == "SMS");
        row.Outcome.Should().Be(DeliveryOutcomes.Unknown);
        row.NextAttemptAtUtc.Should().BeNull();
    }

    [Fact]
    public void Sms_and_whatsapp_dispatchers_declare_ambiguous_outcomes_non_retryable()
    {
        using var h = new Harness();
        var dispatchers = h.Provider.CreateScope().ServiceProvider
            .GetServices<INotificationChannelDispatcher>().ToDictionary(d => d.Channel);
        dispatchers["SMS"].RetryOnAmbiguous.Should().BeFalse();
        dispatchers["WhatsApp"].RetryOnAmbiguous.Should().BeFalse();
    }

    // ── 4. Privacy ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Short_channel_body_containing_a_money_figure_is_suppressed_not_sent()
    {
        var sms = new CapturingSmsProvider();
        using var h = new Harness(sms: sms);
        var db = h.NewDb();
        var tenantId = SeedTenantUserAndEmployee(db, "aisha@acme.test", "+971501234567");
        // A hand-edited tenant template that leaks net pay.
        EnableShortChannel(db, tenantId, "PAYSLIP_READY", "SMS", "Your payslip is ready. Net pay 12,450.00.");
        db.EmployeeNotificationPreferences.Add(new EmployeeNotificationPreference
        { TenantId = tenantId, EmployeeId = 1, SmsEnabled = true });
        await db.SaveChangesAsync();

        await h.Notifications.EnqueueAsync(new NotificationRequest
        {
            TenantId = tenantId, EmployeeId = 1, EventCode = "PAYSLIP_READY",
            EntityName = "PayrollRun", EntityId = "run-1",
        }, default);
        await h.Worker.DrainOnceAsync(default);

        sms.Sent.Should().BeEmpty();
        var row = h.NewDb().NotificationDeliveries.IgnoreQueryFilters().Single(d => d.Channel == "SMS");
        row.Outcome.Should().Be(DeliveryOutcomes.Suppressed);
        row.ErrorCode.Should().Be("pii_guard_blocked");
    }

    [Fact]
    public async Task Short_channel_drops_variables_outside_the_allow_list()
    {
        var sms = new CapturingSmsProvider();
        using var h = new Harness(sms: sms);
        var db = h.NewDb();
        var tenantId = SeedTenantUserAndEmployee(db, "aisha@acme.test", "+971501234567");
        EnableShortChannel(db, tenantId, "PAYSLIP_READY", "SMS", "Payslip ready for {Period}. {NetPay}");
        db.EmployeeNotificationPreferences.Add(new EmployeeNotificationPreference
        { TenantId = tenantId, EmployeeId = 1, SmsEnabled = true });
        await db.SaveChangesAsync();

        await h.Notifications.EnqueueAsync(new NotificationRequest
        {
            TenantId = tenantId, EmployeeId = 1, EventCode = "PAYSLIP_READY",
            EntityName = "PayrollRun", EntityId = "run-1",
            Variables = new Dictionary<string, string> { ["Period"] = "2026-07", ["NetPay"] = "12450.00" },
        }, default);
        await h.Worker.DrainOnceAsync(default);

        sms.Sent.Should().ContainSingle();
        sms.Sent[0].Body.Should().Contain("2026-07");
        sms.Sent[0].Body.Should().NotContain("12450");
    }

    [Fact]
    public void Delivery_log_masks_the_destination_and_scrubs_provider_error_text()
    {
        NotificationBodyPolicy.MaskPhone("+971501234567").Should().NotContain("501234");
        NotificationBodyPolicy.MaskEmail("aisha.rahman@acme.test").Should().NotContain("rahman");
        NotificationBodyPolicy.ScrubProviderError("invalid number +971501234567 for aisha@acme.test")
            .Should().NotContain("971501234567").And.NotContain("aisha@acme.test");
    }

    [Fact]
    public void Every_notification_provider_credential_key_is_masked_on_read()
    {
        // The substring matcher misses Push.ServiceAccountJson / Push.P8Key — the explicit
        // allow-list is what closes that, and this test fails if a new key is added without it.
        foreach (var key in NotificationProviderSecrets.Keys)
            SetupSettingsController.IsSecretSetting(key).Should().BeTrue($"{key} is a credential");
    }

    // ── 5. Scope + opt-in ────────────────────────────────────────────────────

    [Fact]
    public async Task Broadcast_notifications_are_in_app_only()
    {
        using var h = new Harness();
        var db = h.NewDb();
        var tenantId = SeedTenantUserAndEmployee(db, "aisha@acme.test", "+971501234567");

        // LeaveRequestsController raises these with userId == null — visible to EVERY user in the
        // tenant. Fanning that to short channels would SMS the whole company.
        await h.Notifications.NotifyAsync(tenantId, null, "New Leave Request",
            "Aisha submitted a leave request.", "LeaveRequest", "lr-1", default);

        var rows = h.NewDb().NotificationDeliveries.IgnoreQueryFilters().ToList();
        rows.Should().ContainSingle();
        rows[0].Channel.Should().Be(NotificationChannels.InApp);
    }

    [Fact]
    public async Task Short_channels_are_off_when_the_employee_has_no_preference_row()
    {
        using var h = new Harness(sms: new CapturingSmsProvider());
        var db = h.NewDb();
        var tenantId = SeedTenantUserAndEmployee(db, "aisha@acme.test", "+971501234567");
        EnableShortChannel(db, tenantId, "PAYSLIP_READY", "SMS", "Your payslip is ready.");
        // No EmployeeNotificationPreference row — the table is empty in every live tenant, and the
        // CLR defaults (PushEnabled = true) must not be mistaken for consent.

        await h.Notifications.EnqueueAsync(new NotificationRequest
        {
            TenantId = tenantId, EmployeeId = 1, EventCode = "PAYSLIP_READY",
            EntityName = "PayrollRun", EntityId = "run-1",
        }, default);

        h.NewDb().NotificationDeliveries.IgnoreQueryFilters()
            .Any(d => d.Channel == "SMS" || d.Channel == "Push").Should().BeFalse();
    }

    [Fact]
    public async Task Terminated_employees_stop_receiving_short_channel_messages()
    {
        using var h = new Harness(sms: new CapturingSmsProvider());
        var db = h.NewDb();
        var tenantId = SeedTenantUserAndEmployee(db, "aisha@acme.test", "+971501234567", status: "Terminated");
        EnableShortChannel(db, tenantId, "PAYSLIP_READY", "SMS", "Your payslip is ready.");
        db.EmployeeNotificationPreferences.Add(new EmployeeNotificationPreference
        { TenantId = tenantId, EmployeeId = 1, SmsEnabled = true });
        await db.SaveChangesAsync();

        await h.Notifications.EnqueueAsync(new NotificationRequest
        {
            TenantId = tenantId, EmployeeId = 1, EventCode = "PAYSLIP_READY",
            EntityName = "PayrollRun", EntityId = "run-1",
        }, default);

        h.NewDb().NotificationDeliveries.IgnoreQueryFilters().Any(d => d.Channel == "SMS").Should().BeFalse();
    }

    [Fact]
    public void Quiet_hours_defer_a_short_channel_message_instead_of_dropping_it()
    {
        var preference = new EmployeeNotificationPreference
        {
            SmsEnabled = true,
            QuietHoursJson = """{"start":"22:00","end":"07:00","utcOffsetMinutes":180}""",
        };
        // 23:00 local (+03:00) = 20:00 UTC — inside the window.
        var inQuiet = new DateTime(2026, 8, 5, 20, 0, 0, DateTimeKind.Utc);
        NotificationService.ApplyQuietHours(inQuiet, preference, NotificationChannels.Sms)
            .Should().BeAfter(inQuiet);

        // 12:00 local = 09:00 UTC — outside the window, sent immediately.
        var awake = new DateTime(2026, 8, 5, 9, 0, 0, DateTimeKind.Utc);
        NotificationService.ApplyQuietHours(awake, preference, NotificationChannels.Sms).Should().Be(awake);
    }

    [Fact]
    public async Task Worker_never_resolves_a_contact_from_another_tenant()
    {
        var email = new RecordingEmailService();
        using var h = new Harness(email);
        var db = h.NewDb();
        var tenantA = SeedTenantUserAndEmployee(db, "a@acme.test", "+971500000001", employeeId: 1);
        var tenantB = SeedTenantUserAndEmployee(db, "b@other.test", "+971500000002", employeeId: 2);

        await h.Notifications.EnqueueAsync(new NotificationRequest
        {
            TenantId = tenantA, UserId = UserIdOf(db, tenantA), EventCode = "PAYSLIP_READY",
            EntityName = "PayrollRun", EntityId = "run-A", Title = "Payslip ready", Message = "Ready.",
        }, default);
        await h.Notifications.EnqueueAsync(new NotificationRequest
        {
            TenantId = tenantB, UserId = UserIdOf(db, tenantB), EventCode = "PAYSLIP_READY",
            EntityName = "PayrollRun", EntityId = "run-B", Title = "Payslip ready", Message = "Ready.",
        }, default);

        await h.Worker.DrainOnceAsync(default);

        email.Sent.Should().HaveCount(2);
        email.Sent.Single(x => x.TenantId == tenantA).To.Should().Be("a@acme.test");
        email.Sent.Single(x => x.TenantId == tenantB).To.Should().Be("b@other.test");

        // And every row stayed inside its own tenant.
        var rows = h.NewDb().NotificationDeliveries.IgnoreQueryFilters().ToList();
        rows.Where(r => r.TenantId == tenantA).Should().OnlyContain(r => r.EntityId == "run-A");
        rows.Where(r => r.TenantId == tenantB).Should().OnlyContain(r => r.EntityId == "run-B");
    }

    [Fact]
    public async Task A_failing_notification_never_propagates_to_the_business_caller()
    {
        using var h = new Harness();
        // Guid.Empty tenant, no recipient, garbage event — must not throw.
        var act = async () => await h.Notifications.EnqueueAsync(new NotificationRequest
        {
            TenantId = Guid.Empty, EventCode = "NOPE",
        }, default);
        await act.Should().NotThrowAsync();
    }

    // ── AI must not be in this path ──────────────────────────────────────────

    [Fact]
    public void Notification_pipeline_takes_no_dependency_on_the_llm_client()
    {
        var pipeline = new[]
        {
            typeof(NotificationService), typeof(NotificationDeliveryWorker),
            typeof(EmailChannelDispatcher), typeof(SmsChannelDispatcher),
            typeof(WhatsAppChannelDispatcher), typeof(PushChannelDispatcher),
            typeof(NotificationRecipientResolver), typeof(NotificationProviderConfigReader),
        };
        foreach (var type in pipeline)
        foreach (var ctor in type.GetConstructors())
        foreach (var parameter in ctor.GetParameters())
            parameter.ParameterType.Namespace.Should().NotContain("AI",
                $"{type.Name} must stay out of the AI path (AI is opt-in advisory only)");
    }
}
