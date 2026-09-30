using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Infrastructure.Pricing;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Platform;

/// <summary>
/// The defect: POST /api/pricing/quotes saved a PricingQuote and wrote one log line, and nothing else.
/// Meanwhile the public wizard promised the buyer a proposal "within 1 business day".
///
/// The fix announces each quote to sales, and it has to be safe on an ANONYMOUS endpoint:
///   • the request never waits on SMTP or sees a mail error (the controller only queues an id);
///   • the recipient is fixed by platform config, one address, never from the request;
///   • a global budget bounds total sends however many IPs submit;
///   • requester text cannot reach mail headers, and is encoded and capped in the body;
///   • only the platform relay is used, because a public quote belongs to no tenant.
/// </summary>
public class PricingQuoteNotificationTests
{
    private static DbContextOptions<ZayraDbContext> Options(string? name = null) =>
        new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(name ?? Guid.NewGuid().ToString()).Options;

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static SubmitQuoteRequest Request(string company = "Evostel Contracting", string? notes = null) => new(
        CompanyName: company,
        ContactName: "Finance Director",
        ContactEmail: "Finance@Evostel.example ",
        Phone: "+966 50 000 0000",
        OrgType: "group",
        NumCompanies: 3, NumBranches: 5, NumEmployees: 1248, NumAdminUsers: 6, NumCountries: 3,
        NeedsArabic: true,
        SelectedModules: new List<string> { "core_hr", "payroll" },
        EstimatedMonthlyAmount: 4200m,
        EstimatedAnnualAmount: 45360m,
        Notes: notes ?? "Migrating off spreadsheets before the next GOSI filing.");

    private static async Task<Guid> SaveQuoteAsync(DbContextOptions<ZayraDbContext> options, SubmitQuoteRequest? request = null)
    {
        await using var db = new ZayraDbContext(options);
        var queue = new QuoteNotificationQueue();
        var result = await new PricingController(db, NullLogger<PricingController>.Instance, queue)
            .SubmitQuote(request ?? Request(), default);
        result.Should().BeOfType<OkObjectResult>();
        queue.Reader.TryRead(out var id).Should().BeTrue();
        return id;
    }

    private static async Task SetSalesAddressAsync(DbContextOptions<ZayraDbContext> options, string value)
    {
        await using var db = new ZayraDbContext(options);
        db.PlatformConfigEntries.Add(new PlatformConfigEntry { Key = PlatformConfigKeys.SalesNotificationAddress, Value = value });
        await db.SaveChangesAsync();
    }

    private static QuoteNotificationSender Sender(DbContextOptions<ZayraDbContext> options, RecordingEmail email,
        IConfiguration? config = null) =>
        new(new ZayraDbContext(options), email, new EphemeralDataProtectionProvider(), config ?? Config(),
            NullLogger<QuoteNotificationSender>.Instance);

    private static QuoteNotificationWorker Worker(DbContextOptions<ZayraDbContext> options, RecordingEmail email,
        IConfiguration config, TimeProvider? clock = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => new ZayraDbContext(options));
        services.AddScoped<IEmailService>(_ => email);
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton(config);
        services.AddScoped<QuoteNotificationSender>();
        var provider = services.BuildServiceProvider();
        return new QuoteNotificationWorker(new QuoteNotificationQueue(), new QuoteNotificationBudget(config, clock),
            provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<QuoteNotificationWorker>.Instance);
    }

    // ── The request path ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SubmittingAQuote_SavesIt_AndQueuesExactlyOneNotification_WithoutSendingMailInline()
    {
        // The controller has no mail dependency at all, so it cannot wait on SMTP or surface a
        // relay error to an anonymous caller. It saves the quote and queues the id.
        var options = Options();
        await using var db = new ZayraDbContext(options);
        var queue = new QuoteNotificationQueue();

        var result = await new PricingController(db, NullLogger<PricingController>.Instance, queue)
            .SubmitQuote(Request(), default);

        result.Should().BeOfType<OkObjectResult>();
        var saved = await db.PricingQuotes.SingleAsync();
        saved.ContactEmail.Should().Be("finance@evostel.example");
        queue.Reader.TryRead(out var queued).Should().BeTrue();
        queued.Should().Be(saved.Id);
        queue.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task SubmittingAQuote_StillSucceeds_WhenTheNotificationQueueIsFull()
    {
        var options = Options();
        await using var db = new ZayraDbContext(options);
        var queue = new QuoteNotificationQueue();
        for (var i = 0; i < QuoteNotificationQueue.Capacity; i++) queue.TryEnqueue(Guid.NewGuid()).Should().BeTrue();

        var result = await new PricingController(db, NullLogger<PricingController>.Instance, queue)
            .SubmitQuote(Request(), default);

        result.Should().BeOfType<OkObjectResult>();
        (await db.PricingQuotes.CountAsync()).Should().Be(1);
    }

    // ── Who is told, through which relay ─────────────────────────────────────────

    [Fact]
    public async Task NotifiesSales_AtTheConfiguredAddress_ThroughThePlatformRelayOnly()
    {
        var options = Options();
        await SetSalesAddressAsync(options, "sales@kynexone.com");
        var id = await SaveQuoteAsync(options);
        var email = new RecordingEmail { PlatformConfigured = true };

        var outcome = await Sender(options, email).SendAsync(id, heldBack: 0, default);

        outcome.Should().Be(QuoteNotificationOutcome.Sent);
        var sent = email.PlatformSends.Should().ContainSingle().Subject;
        sent.To.Should().Be("sales@kynexone.com");
        sent.Subject.Should().Contain("Evostel Contracting").And.Contain("1248");
        sent.Body.Should().Contain("finance@evostel.example").And.Contain("Migrating off spreadsheets");
        // A public quote has no tenant: an ambient or tenant send would resolve SMTP through tenant rows.
        email.AmbientSends.Should().BeEmpty();
        email.TenantSends.Should().BeEmpty();
    }

    [Fact]
    public async Task FallsBackToConfiguredSalesAddress_WhenNoPlatformKeyIsSet()
    {
        var options = Options();
        var id = await SaveQuoteAsync(options);
        var email = new RecordingEmail { PlatformConfigured = true };

        await Sender(options, email, Config(("Sales:NotificationEmail", "fallback@kynexone.com"))).SendAsync(id, 0, default);

        email.PlatformSends.Single().To.Should().Be("fallback@kynexone.com");
    }

    [Theory]
    [InlineData("sales@kynexone.com, attacker@evil.example")]
    [InlineData("sales@kynexone.com;attacker@evil.example")]
    [InlineData("Sales <sales@kynexone.com>")]
    [InlineData("not-an-address")]
    public async Task NeverSendsToAListOrAMalformedAddress(string configured)
    {
        // Bounded recipients: exactly one plain address, or nothing. A configured-but-invalid value
        // is reported, never "fixed" by guessing or silently falling through to another address.
        var options = Options();
        await SetSalesAddressAsync(options, configured);
        var id = await SaveQuoteAsync(options);
        var email = new RecordingEmail { PlatformConfigured = true };

        var outcome = await Sender(options, email, Config(("Sales:NotificationEmail", "fallback@kynexone.com")))
            .SendAsync(id, 0, default);

        outcome.Should().Be(QuoteNotificationOutcome.NoRecipient);
        email.PlatformSends.Should().BeEmpty();
    }

    [Fact]
    public async Task DoesNotSend_WhenThePlatformRelayIsNotConfigured()
    {
        var options = Options();
        await SetSalesAddressAsync(options, "sales@kynexone.com");
        var id = await SaveQuoteAsync(options);
        var email = new RecordingEmail { PlatformConfigured = false };

        var outcome = await Sender(options, email).SendAsync(id, 0, default);

        outcome.Should().Be(QuoteNotificationOutcome.RelayNotConfigured);
        email.PlatformSends.Should().BeEmpty();
    }

    // ── What the requester controls ──────────────────────────────────────────────

    [Fact]
    public async Task RequesterText_CannotReachMailHeaders_AndIsEncodedAndCappedInTheBody()
    {
        var options = Options();
        await SetSalesAddressAsync(options, "sales@kynexone.com");
        var id = await SaveQuoteAsync(options, Request(
            company: "Acme\r\nBcc: attacker@evil.example\r\n<script>alert(1)</script>",
            notes: new string('x', 50_000)));
        var email = new RecordingEmail { PlatformConfigured = true };

        await Sender(options, email).SendAsync(id, 0, default);

        var sent = email.PlatformSends.Single();
        sent.Subject.Should().NotContain("\r").And.NotContain("\n");
        sent.Subject.Length.Should().BeLessThan(140);
        sent.Body.Should().NotContain("<script>").And.Contain("&lt;script&gt;");
        sent.Body.Length.Should().BeLessThan(6_000, "a 50,000-character note is capped, not mailed whole");
    }

    // ── The worker: containment and the global budget ────────────────────────────

    [Fact]
    public async Task ARelayFailure_IsContainedInTheWorker_AndTheQuoteIsKept()
    {
        var options = Options();
        await SetSalesAddressAsync(options, "sales@kynexone.com");
        var id = await SaveQuoteAsync(options);
        var email = new RecordingEmail { PlatformConfigured = true, ThrowOnSend = true };

        var act = () => Worker(options, email, Config()).ProcessAsync(id, default);

        (await act.Should().NotThrowAsync()).Subject.Should().Be(QuoteNotificationOutcome.Failed);
        await using var db = new ZayraDbContext(options);
        (await db.PricingQuotes.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task TheBudgetCapsSendsGlobally_AndTheNextMailSaysHowManyWereHeldBack()
    {
        var options = Options();
        await SetSalesAddressAsync(options, "sales@kynexone.com");
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++) ids.Add(await SaveQuoteAsync(options));
        var email = new RecordingEmail { PlatformConfigured = true };
        var clock = new ManualClock(new DateTimeOffset(2026, 9, 28, 9, 0, 0, TimeSpan.Zero));
        var worker = Worker(options, email,
            Config(("Sales:QuoteNotificationLimit", "2"), ("Sales:QuoteNotificationWindowMinutes", "60")), clock);

        (await worker.ProcessAsync(ids[0], default)).Should().Be(QuoteNotificationOutcome.Sent);
        (await worker.ProcessAsync(ids[1], default)).Should().Be(QuoteNotificationOutcome.Sent);
        (await worker.ProcessAsync(ids[2], default)).Should().Be(QuoteNotificationOutcome.Suppressed);
        email.PlatformSends.Should().HaveCount(2);

        clock.Advance(TimeSpan.FromMinutes(61));
        (await worker.ProcessAsync(ids[3], default)).Should().Be(QuoteNotificationOutcome.Sent);

        email.PlatformSends.Should().HaveCount(3);
        email.PlatformSends[2].Body.Should().Contain("1 other quote request was saved but not emailed");
    }

    [Theory]
    [InlineData("sales@kynexone.com", true)]
    [InlineData(" sales@kynexone.com ", true)]
    [InlineData("", false)]
    [InlineData("a@b.com b@c.com", false)]
    [InlineData("a@b.com\r\nBcc: c@d.com", false)]
    public void SingleAddress_AcceptsExactlyOnePlainAddress(string value, bool accepted) =>
        (QuoteNotificationSender.SingleAddress(value) is not null).Should().Be(accepted);

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}

/// <summary>Records which of the three IEmailService relays a caller reached.</summary>
internal sealed class RecordingEmail : IEmailService
{
    internal record Send(string To, string Subject, string Body);

    public List<Send> AmbientSends  { get; } = [];
    public List<Send> TenantSends   { get; } = [];
    public List<Send> PlatformSends { get; } = [];

    public bool PlatformConfigured { get; init; }
    public bool ThrowOnSend { get; init; }

    public Task SendAsync(string toAddress, string toName, string subject, string htmlBody,
        IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
    {
        AmbientSends.Add(new Send(toAddress, subject, htmlBody));
        return Task.CompletedTask;
    }

    public Task SendAsync(Guid tenantId, string toAddress, string toName, string subject, string htmlBody,
        IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
    {
        TenantSends.Add(new Send(toAddress, subject, htmlBody));
        return Task.CompletedTask;
    }

    public Task SendPlatformAsync(string toAddress, string toName, string subject, string htmlBody,
        IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
    {
        if (ThrowOnSend) throw new InvalidOperationException("SMTP host unreachable");
        PlatformSends.Add(new Send(toAddress, subject, htmlBody));
        return Task.CompletedTask;
    }

    public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> IsPlatformConfiguredAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(PlatformConfigured);
}
