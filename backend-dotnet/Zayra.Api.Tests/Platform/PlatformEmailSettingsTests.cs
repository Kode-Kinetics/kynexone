using FluentAssertions;
using MailKit.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Platform;

/// <summary>
/// Covers the platform email relay end to end: the provider catalog, the save/load round trip,
/// password protection, and the test-send.
///
/// The defect these lock down: PlatformController wrote smtp_* keys into PlatformConfigEntries and
/// NOTHING read them. SmtpEmailService only ever looked at tenant-scoped SystemSettings rows, so a
/// fully filled-in Platform Settings form produced a permanently amber "SMTP not configured"
/// banner and a test-send that refused before it opened a socket.
/// </summary>
public class PlatformEmailSettingsTests : PlatformTestBase
{
    private static IDataProtectionProvider Protection() => new EphemeralDataProtectionProvider();

    private static async Task<Dictionary<string, string>> ConfigMapAsync(Zayra.Api.Data.ZayraDbContext db) =>
        await db.PlatformConfigEntries.AsNoTracking().ToDictionaryAsync(e => e.Key, e => e.Value);

    private static UpdateSmtpRequest GoDaddyRequest(string? provider = null, string? password = "s3cret!") =>
        new(Host: "smtpout.secureserver.net", Port: 587, Username: "info@kodekinetics.com",
            Password: password, UseSsl: true, FromEmail: "info@kodekinetics.com",
            FromName: "KynexOne Admin", Provider: provider);

    // ── Status reporting ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetSettings_ReportsNotConfigured_WhenNothingIsSaved()
    {
        using var db = CreateDb();
        var controller = CreateController(db);

        var result = await controller.GetSettings(Protection(), default) as OkObjectResult;

        result.Should().NotBeNull();
        var smtp = Smtp(result!);
        ((bool)smtp["isConfigured"]!).Should().BeFalse();
        ((string)smtp["source"]!).Should().Be("none");
    }

    [Fact]
    public async Task GetSettings_ReportsConfigured_AfterSave()
    {
        using var db = CreateDb();
        var controller = CreateController(db);
        var protection = Protection();

        (await controller.UpdateSmtp(GoDaddyRequest(), protection, default)).Should().BeOfType<OkObjectResult>();

        var smtp = Smtp((OkObjectResult)(await controller.GetSettings(protection, default))!);

        // The banner's input. It was undefined before this change, which is why the warning never cleared.
        ((bool)smtp["isConfigured"]!).Should().BeTrue();
        ((string)smtp["host"]!).Should().Be("smtpout.secureserver.net");
        ((int)smtp["port"]!).Should().Be(587);
        ((string)smtp["source"]!).Should().Be("database");
    }

    [Fact]
    public async Task GetSettings_NeverReturnsThePassword()
    {
        using var db = CreateDb();
        var controller = CreateController(db);
        var protection = Protection();

        await controller.UpdateSmtp(GoDaddyRequest(password: "super-secret-value"), protection, default);

        var smtp = Smtp((OkObjectResult)(await controller.GetSettings(protection, default))!);

        ((string)smtp["password"]!).Should().Be("***");
        ((bool)smtp["hasPassword"]!).Should().BeTrue();
        System.Text.Json.JsonSerializer.Serialize(smtp).Should().NotContain("super-secret-value");
    }

    [Fact]
    public async Task GetSettings_FallsBackToEnvironment_WhenNothingIsSaved()
    {
        using var db = CreateDb();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Smtp:Host"]      = "smtp.office365.com",
            ["Smtp:FromEmail"] = "noreply@example.com",
        }).Build();
        var controller = CreateController(db, configuration: config);

        var smtp = Smtp((OkObjectResult)(await controller.GetSettings(Protection(), default))!);

        ((bool)smtp["isConfigured"]!).Should().BeTrue();
        ((string)smtp["source"]!).Should().Be("environment");
    }

    // ── Validation ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData("missing@tld")]
    public async Task UpdateSmtp_RejectsAnUnusableFromAddress(string fromEmail)
    {
        using var db = CreateDb();
        var controller = CreateController(db);

        // MimeKit throws on an empty From, so accepting this would mean a config that saves fine
        // and then fails on every single send.
        var result = await controller.UpdateSmtp(
            GoDaddyRequest() with { FromEmail = fromEmail }, Protection(), default);

        result.Should().BeOfType<BadRequestObjectResult>();
        (await db.PlatformConfigEntries.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public async Task UpdateSmtp_RejectsAnOutOfRangePort(int port)
    {
        using var db = CreateDb();
        var controller = CreateController(db);

        (await controller.UpdateSmtp(GoDaddyRequest() with { Port = port }, Protection(), default))
            .Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task UpdateSmtp_RejectsABlankHost()
    {
        using var db = CreateDb();
        var controller = CreateController(db);

        (await controller.UpdateSmtp(GoDaddyRequest() with { Host = "  " }, Protection(), default))
            .Should().BeOfType<BadRequestObjectResult>();
    }

    // ── Password handling ─────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateSmtp_EncryptsThePassword_RatherThanBase64EncodingIt()
    {
        using var db = CreateDb();
        var controller = CreateController(db);
        var protection = Protection();
        const string plaintext = "GoDaddyMailbox#2026";

        await controller.UpdateSmtp(GoDaddyRequest(password: plaintext), protection, default);

        var stored = (await ConfigMapAsync(db))[PlatformSmtpConfig.KeyPassword];

        // The old implementation stored Convert.ToBase64String(...) and said so in its own comment.
        stored.Should().NotBe(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext)));
        stored.Should().NotContain(plaintext);
        PlatformSmtpConfig.UnprotectPassword(protection, stored).Should().Be(plaintext);
    }

    [Fact]
    public async Task UpdateSmtp_KeepsTheExistingPassword_WhenTheFieldIsLeftBlank()
    {
        using var db = CreateDb();
        var controller = CreateController(db);
        var protection = Protection();

        await controller.UpdateSmtp(GoDaddyRequest(password: "original"), protection, default);
        // The form sends an empty password to mean "unchanged" — its placeholder promises exactly that.
        await controller.UpdateSmtp(GoDaddyRequest(password: "") with { FromName = "Renamed" }, protection, default);

        var settings = await PlatformSmtpConfig.LoadAsync(db, protection, new ConfigurationBuilder().Build(), null, default);
        settings.Password.Should().Be("original");
        settings.FromName.Should().Be("Renamed");
    }

    [Fact]
    public async Task UpdateSmtp_TreatsTheMaskSentinelAsUnchanged()
    {
        using var db = CreateDb();
        var controller = CreateController(db);
        var protection = Protection();

        await controller.UpdateSmtp(GoDaddyRequest(password: "original"), protection, default);
        // GET returns "***"; a form that round-trips it must not overwrite the real secret with the mask.
        await controller.UpdateSmtp(GoDaddyRequest(password: "***"), protection, default);

        var settings = await PlatformSmtpConfig.LoadAsync(db, protection, new ConfigurationBuilder().Build(), null, default);
        settings.Password.Should().Be("original");
    }

    [Fact]
    public async Task UpdateSmtp_AuditsTheChange_WithoutRecordingThePassword()
    {
        using var db = CreateDb();
        var controller = CreateController(db);
        var protection = Protection();
        const string plaintext = "GoDaddyMailbox#2026";

        await controller.UpdateSmtp(GoDaddyRequest(password: plaintext), protection, default);

        var entry = await db.AdminAuditLogs.SingleAsync(x => x.Action == "SmtpConfigUpdated");

        // Redirecting every outbound platform email is at least as consequential as maintenance
        // mode, which was already audited.
        entry.NewValuesJson.Should().Contain("smtpout.secureserver.net").And.Contain("godaddy");
        entry.NewValuesJson.Should().Contain("passwordChanged");

        // The relay password must never land in the audit trail.
        entry.NewValuesJson.Should().NotContain(plaintext);
        entry.OldValuesJson.Should().NotContain(plaintext);
    }

    [Fact]
    public async Task UpdateSmtp_RecordsWhatTheRelayWasChangedFrom()
    {
        using var db = CreateDb();
        var controller = CreateController(db);
        var protection = Protection();

        await controller.UpdateSmtp(GoDaddyRequest(), protection, default);
        await controller.UpdateSmtp(
            GoDaddyRequest(password: "") with { Host = "smtp.sendgrid.net", Provider = "sendgrid" },
            protection, default);

        var entries = await db.AdminAuditLogs.Where(x => x.Action == "SmtpConfigUpdated").ToListAsync();
        entries.Should().HaveCount(2);

        // Selected by content, not by timestamp — two writes in the same millisecond would tie.
        var move = entries.Single(x => x.NewValuesJson.Contains("smtp.sendgrid.net"));

        // "Changed to X" is not enough to investigate a misdirected relay — the previous value is
        // what says whether mail was silently rerouted.
        move.OldValuesJson.Should().Contain("smtpout.secureserver.net");
    }

    [Fact]
    public async Task LoadAsync_StillReadsLegacyBase64Passwords()
    {
        using var db = CreateDb();
        const string plaintext = "written-by-the-old-endpoint";
        db.PlatformConfigEntries.AddRange(
            new PlatformConfigEntry { Key = PlatformSmtpConfig.KeyHost, Value = "smtpout.secureserver.net" },
            new PlatformConfigEntry { Key = PlatformSmtpConfig.KeyFromAddress, Value = "info@kodekinetics.com" },
            new PlatformConfigEntry
            {
                Key = PlatformSmtpConfig.KeyPassword,
                Value = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext)),
            });
        await db.SaveChangesAsync();

        var settings = await PlatformSmtpConfig.LoadAsync(db, Protection(), new ConfigurationBuilder().Build(), null, default);

        settings.Password.Should().Be(plaintext);
        settings.IsUsable.Should().BeTrue();
    }

    // ── Provider auto-configuration ───────────────────────────────────────────

    [Fact]
    public void EmailProviderCatalog_CoversGoDaddyAndIsInternallyConsistent()
    {
        var providers = EmailProviderPresets.All;

        providers.Should().Contain(p => p.Key == "godaddy",
            "GoDaddy is the relay this platform currently sends through");
        providers.Should().Contain(p => p.Key == EmailProviderPresets.CustomKey);

        providers.Select(p => p.Key).Should().OnlyHaveUniqueItems();

        foreach (var p in providers.Where(p => p.Key != EmailProviderPresets.CustomKey))
        {
            p.Host.Should().NotBeNullOrWhiteSpace($"{p.Key} must carry a real host to auto-fill");
            p.Port.Should().BeInRange(1, 65535);
            p.UseTls.Should().BeTrue($"{p.Key} must not default an admin onto an unencrypted relay");
            p.Guidance.Should().NotBeNullOrWhiteSpace($"{p.Key} must explain its common failure mode");
        }
    }

    [Fact]
    public void DetectByHost_RecognisesEveryPresetHost()
    {
        // cpanel's host is a placeholder ("mail.yourdomain.com"), so it is not detectable.
        var detectable = EmailProviderPresets.All
            .Where(x => !string.IsNullOrEmpty(x.Host) && x.Key != "cpanel");

        foreach (var p in detectable)
        {
            var detected = EmailProviderPresets.DetectByHost(p.Host);
            detected.Should().NotBeNull($"{p.Host} should map to a provider");

            if (p.IsHostAlias)
                // An alias shares its host with a general preset and cannot be told apart on the
                // wire — resolving to the general entry with the same host is the correct answer.
                detected!.Host.Should().BeEquivalentTo(p.Host);
            else
                detected!.Key.Should().Be(p.Key);
        }
    }

    [Fact]
    public void DetectByHost_PrefersTheGeneralProviderOverAnAlias()
        // Both the direct Microsoft 365 preset and GoDaddy's resale of it use this host.
        => EmailProviderPresets.DetectByHost("smtp.office365.com")!.Key.Should().Be("microsoft365");

    [Theory]
    [InlineData("smtpout.secureserver.net", "godaddy")]
    [InlineData("SMTPOUT.SECURESERVER.NET", "godaddy")]
    [InlineData("relay.secureserver.net", "godaddy")]
    [InlineData("email-smtp.eu-west-1.amazonaws.com", "amazon-ses")]
    [InlineData("smtp.zoho.eu", "zoho")]
    public void DetectByHost_HandlesCaseAndRegionalVariants(string host, string expectedKey)
        => EmailProviderPresets.DetectByHost(host)!.Key.Should().Be(expectedKey);

    [Fact]
    public void DetectByHost_ReturnsNullForAnUnknownHost()
        => EmailProviderPresets.DetectByHost("mail.some-private-server.example").Should().BeNull();

    [Fact]
    public async Task UpdateSmtp_InfersTheProvider_WhenTheFormDoesNotSendOne()
    {
        using var db = CreateDb();
        var controller = CreateController(db);
        var protection = Protection();

        // SMTP saved before the catalog existed has no provider key — the host identifies it.
        await controller.UpdateSmtp(GoDaddyRequest(provider: null), protection, default);

        (await ConfigMapAsync(db))[PlatformSmtpConfig.KeyProvider].Should().Be("godaddy");

        var smtp = Smtp((OkObjectResult)(await controller.GetSettings(protection, default))!);
        ((string)smtp["provider"]!).Should().Be("godaddy");
        ((string?)smtp["providerLabel"]).Should().Contain("GoDaddy");
    }

    [Fact]
    public void GetEmailProviders_ReturnsTheCatalog()
    {
        using var db = CreateDb();
        var result = CreateController(db).GetEmailProviders() as OkObjectResult;

        result.Should().NotBeNull();
        var items = ((System.Collections.IEnumerable)result!.Value!).Cast<object>().ToList();
        items.Should().HaveCount(EmailProviderPresets.All.Count);
    }

    // ── Test send ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task TestSmtp_RefusesBeforeTheRelayIsConfigured()
    {
        using var db = CreateDb();
        var controller = CreateController(db);

        (await controller.TestSmtp(new TestSmtpRequest("someone@example.com"), Protection(), default))
            .Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task TestSmtp_DeliversToTheAddressTheAdminChose()
    {
        using var db = CreateDb();
        var recorder = new RecordingPlatformEmailService();
        var controller = CreateController(db, emailService: recorder);
        var protection = Protection();
        await controller.UpdateSmtp(GoDaddyRequest(), protection, default);

        var result = await controller.TestSmtp(new TestSmtpRequest("zack@kodekinetics.com"), protection, default) as OkObjectResult;

        result.Should().NotBeNull();
        var body = Anon(result!.Value!);
        ((bool)body["sent"]!).Should().BeTrue();
        ((string)body["to"]!).Should().Be("zack@kodekinetics.com");

        // The point of the feature: the test lands in an inbox the admin can actually open, not
        // only in the signed-in platform-admin mailbox.
        recorder.PlatformSends.Should().ContainSingle()
            .Which.To.Should().Be("zack@kodekinetics.com");
    }

    [Fact]
    public async Task TestSmtp_FallsBackToTheSignedInAdmin_WhenNoRecipientIsGiven()
    {
        using var db = CreateDb();
        var recorder = new RecordingPlatformEmailService();
        var controller = CreateController(db, emailService: recorder);
        var protection = Protection();
        await controller.UpdateSmtp(GoDaddyRequest(), protection, default);

        await controller.TestSmtp(new TestSmtpRequest(null), protection, default);

        recorder.PlatformSends.Should().ContainSingle().Which.To.Should().Be(AdminEmail);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("two@addresses.com, other@x.com")]
    [InlineData("spaced out@example.com")]
    public async Task TestSmtp_RejectsAMalformedRecipient(string to)
    {
        using var db = CreateDb();
        var recorder = new RecordingPlatformEmailService();
        var controller = CreateController(db, emailService: recorder);
        var protection = Protection();
        await controller.UpdateSmtp(GoDaddyRequest(), protection, default);

        (await controller.TestSmtp(new TestSmtpRequest(to), protection, default))
            .Should().BeOfType<BadRequestObjectResult>();
        recorder.PlatformSends.Should().BeEmpty();
    }

    [Fact]
    public async Task TestSmtp_SurfacesTheRelaysOwnErrorInsteadOfSwallowingIt()
    {
        using var db = CreateDb();
        var recorder = new RecordingPlatformEmailService
        {
            ThrowOnSend = new InvalidOperationException("535 5.7.8 Authentication credentials invalid"),
        };
        var controller = CreateController(db, emailService: recorder);
        var protection = Protection();
        await controller.UpdateSmtp(GoDaddyRequest(), protection, default);

        var result = await controller.TestSmtp(new TestSmtpRequest("zack@kodekinetics.com"), protection, default) as OkObjectResult;

        var body = Anon(result!.Value!);
        ((bool)body["sent"]!).Should().BeFalse();
        ((string)body["error"]!).Should().Contain("535");
        ((string)body["message"]!).Should().Contain("smtpout.secureserver.net");
    }

    [Fact]
    public async Task TestSmtp_UsesThePlatformRelay_NotATenantRelay()
    {
        using var db = CreateDb();
        var recorder = new RecordingPlatformEmailService();
        var controller = CreateController(db, emailService: recorder);
        var protection = Protection();
        await controller.UpdateSmtp(GoDaddyRequest(), protection, default);

        await controller.TestSmtp(new TestSmtpRequest("zack@kodekinetics.com"), protection, default);

        // A platform-admin request carries no tenant_id, so an ambient send would have matched every
        // tenant's Email settings at once. The platform-explicit overload cannot.
        recorder.AmbientSends.Should().BeEmpty();
        recorder.PlatformSends.Should().ContainSingle();
    }

    // ── Transport selection ───────────────────────────────────────────────────

    [Theory]
    [InlineData(465, true,  SecureSocketOptions.SslOnConnect)]  // implicit TLS — StartTls fails here
    [InlineData(465, false, SecureSocketOptions.SslOnConnect)]
    [InlineData(587, true,  SecureSocketOptions.StartTls)]
    [InlineData(2525, true, SecureSocketOptions.StartTls)]
    [InlineData(25, false,  SecureSocketOptions.Auto)]
    public void ResolveSecureOption_MatchesTheSubmissionPortsSemantics(int port, bool useTls, SecureSocketOptions expected)
        // main landed the same 465 fix independently, as ResolveSecureOption. These cases now
        // cover that one rather than adding a second implementation of it.
        => SmtpEmailService.ResolveSecureOption(useTls, port).Should().Be(expected);

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Reflects over the anonymous response object so tests read its properties by name.</summary>
    private static Dictionary<string, object?> Anon(object value) =>
        value.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(value));

    private static Dictionary<string, object?> Smtp(OkObjectResult result) =>
        Anon(Anon(result.Value!)["smtp"]!);

    private sealed class RecordingPlatformEmailService : IEmailService
    {
        public record Sent(string To, string Subject);

        public List<Sent> PlatformSends { get; } = [];
        public List<Sent> AmbientSends { get; } = [];
        public Exception? ThrowOnSend { get; init; }

        public Task SendAsync(string toAddress, string toName, string subject, string htmlBody,
            IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
        {
            AmbientSends.Add(new Sent(toAddress, subject));
            return ThrowOnSend is null ? Task.CompletedTask : Task.FromException(ThrowOnSend);
        }

        public Task SendPlatformAsync(string toAddress, string toName, string subject, string htmlBody,
            IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
        {
            if (ThrowOnSend is not null) return Task.FromException(ThrowOnSend);
            PlatformSends.Add(new Sent(toAddress, subject));
            return Task.CompletedTask;
        }

        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> IsPlatformConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
