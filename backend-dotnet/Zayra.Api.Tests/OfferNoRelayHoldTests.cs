using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;
using Zayra.Api.Tests.Platform;

namespace Zayra.Api.Tests;

/// <summary>
/// The managed offer lifecycle and the dispatch worker default ON, so a fresh deployment must still never send a real email without a configured
/// workspace mail relay: no relay means nothing is sent, nothing borrows the platform relay, and the reason is stated in English and Arabic.
/// </summary>
public sealed class OfferNoRelayHoldTests : PlatformTestBase
{
    private static SmtpEmailService Mail(Zayra.Api.Data.ZayraDbContext db) =>
        new(db, new EphemeralDataProtectionProvider(), new ConfigurationBuilder().Build(), NullLogger<SmtpEmailService>.Instance);

    private static void PlatformRelay(Zayra.Api.Data.ZayraDbContext db)
    {
        db.PlatformConfigEntries.Add(new PlatformConfigEntry { Key = PlatformSmtpConfig.KeyHost, Value = "smtp.platform.invalid" });
        db.PlatformConfigEntries.Add(new PlatformConfigEntry { Key = PlatformSmtpConfig.KeyFromAddress, Value = "platform@platform.invalid" });
        db.SaveChanges();
    }

    [Fact]
    public async Task WithNoWorkspaceRelay_DeliveryIsNotConfigured_AndTheExistingPlatformRelayIsNeverUsed()
    {
        using var db = CreateDb();
        PlatformRelay(db);
        var tenantId = Guid.NewGuid();
        var mail = Mail(db);

        var sender = await mail.GetEffectiveSenderAsync(tenantId);
        sender.Configured.Should().BeFalse();
        sender.Source.Should().Be("None");

        // A platform relay exists, yet the offer is not sent: the result is NotConfigured, never an attempt on smtp.platform.invalid.
        var result = await mail.DeliverAsync(tenantId, sender.ConfigurationFingerprint, "candidate@example.invalid", "Candidate", "Offer", "<p>Offer</p>");
        result.Status.Should().Be(EmailDeliveryStatus.NotConfigured);
    }

    [Fact]
    public async Task TheReadinessSaysWhyInEnglishAndArabic_WhenNoWorkspaceRelayIsConfigured()
    {
        using var db = CreateDb();
        PlatformRelay(db);
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "No relay", Slug = $"nr-{tenantId:N}"[..16], IsActive = true });
        db.SaveChanges();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [OfferLifecycleService.EnablementKey] = "true" }).Build();
        var service = new OfferMailReadinessService(db, Mail(db), new OfferLifecycleService(db, config));

        var readiness = await service.GetReadinessAsync(new OfferActor(tenantId, null, Guid.NewGuid(), "HR"));

        readiness.Configured.Should().BeFalse();
        readiness.BlockedReason.Should().Contain("No effective SMTP relay");
        readiness.BlockedReasonAr.Should().MatchRegex("[؀-ۿ]").And.NotBe(readiness.BlockedReason);
    }
}
